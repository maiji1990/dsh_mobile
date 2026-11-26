/**
 * 启动期只读运行层文件系统元数据缓存垫片，以 node --import 预加载。
 *
 * 仅针对不可变只读运行层前缀（默认 /opt/dshapp/runtime 与 /usr/local）缓存同步
 * realpathSync / realpathSync.native / existsSync / statSync / lstatSync / readdirSync 及 package.json
 * 读取结果，并安全包装 process.binding('fs').internalModuleStat（Node 26 中与 internalBinding('fs') 同一对象）。
 * 干净绝对路径走零分配快路径；严格只读前缀（默认 /opt/dshapp/runtime）内的内部模块 stat 负结果、
 * 只读小清单（.json/.yml/.yaml）正结果、package_json_reader.read 正结果，以及
 * internal/modules/helpers 的 loaderMethods（getPackageScopeConfig / readPackageJSON /
 * getNearestParentPackageJSON / getNearestParentPackageJSONType）正结果也会缓存。
 * 符号链接（S_ISLNK）一律透传不缓存；可写层与伪文件系统永不进入负缓存。
 * 绝不缓存用户工作区（/root/projects）或临时目录；ENOENT/ENOTDIR 只对严格只读前缀
 * （DSHM_FS_CACHE_NONEXIST_PREFIXES，默认 /opt/dshapp/runtime）生效。
 *
 * 启动窗口结束后（默认 45 秒）自动恢复全部原生 fs 方法、内部模块 stat 绑定并清空缓存，避免长期驻留内存或
 * 影响运行期更新。本文件为本仓库自有资产，不修改上游 link-shim.mjs。
 *
 * 同一切片还负责 DSH 的 V8 编译缓存兜底落盘：NODE_COMPILE_CACHE 的默认刷盘发生在正常退出路径，
 * Android 上的 OOM / SIGKILL / 强杀会跳过该路径。这里在启动稳定窗口后（DSHM_COMPILE_CACHE_FLUSH_MS，
 * 默认 5 秒，定时器 unref）、uninstall() 以及 SIGTERM 退出路径主动调用 module.flushCompileCache()；
 * 旧 Node 或未启用缓存时特性探测为 no-op。SIGTERM 处理器只做同步落盘，不吞掉 DSH 自己的清理逻辑，
 * 也不改变没有其它监听者时 Node 的默认终止语义。
 */

import fs from "node:fs";
import * as nodeModule from "node:module";
import path from "node:path";
import { fileURLToPath } from "node:url";

const { createRequire, syncBuiltinESMExports } = nodeModule;

const DEFAULT_PREFIXES = ["/opt/dshapp/runtime", "/usr/local"];
const DEFAULT_NONEXIST_PREFIXES = ["/opt/dshapp/runtime"];
/** 这些路径即使被误写进负缓存配置也绝不缓存 ENOENT/ENOTDIR（可写工作区与系统伪文件系统）。 */
const FORBIDDEN_NONEXIST_PREFIXES = ["/usr/local", "/root/projects", "/tmp", "/proc", "/dev"];
const DEFAULT_TTL_MS = 45_000;
const DEFAULT_COMPILE_CACHE_FLUSH_MS = 5_000;
const MAX_PACKAGE_JSON_BYTES = 256 * 1024;
const MAX_MANIFEST_BYTES = 256 * 1024;
const MAX_CACHE_ENTRIES = 16_384;
/** 仅 POSIX 目标平台启用零分配快路径；Windows 上 path.resolve 会改写分隔符，统一退回慢路径。 */
const FAST_PATH_ENABLED = path.sep === "/";
const CACHEABLE_ERR_CODES = new Set(["ENOENT", "ENOTDIR"]);
const STATS_SYMBOL = Symbol.for("dshmobile.startupFsCache.state");

const origRealpathSync = fs.realpathSync;
const origRealpathNative = fs.realpathSync.native;
const origExistsSync = fs.existsSync;
const origStatSync = fs.statSync;
const origLstatSync = fs.lstatSync;
const origReadFileSync = fs.readFileSync;
const origReaddirSync = fs.readdirSync;
const origAccessSync = fs.accessSync;
/** fs.promises 的原始引用：卸载时必须完整恢复。 */
const origPromises = fs.promises;
const origPromiseStat = origPromises.stat;
const origPromiseLstat = origPromises.lstat;
const origPromiseRealpath = origPromises.realpath;
const origPromiseReaddir = origPromises.readdir;
const origPromiseAccess = origPromises.access;
const origPromiseReadFile = origPromises.readFile;

/** 内部模块 stat 绑定（process.binding('fs')）的原始函数；不可用或不可写时为 null。 */
let moduleStatBinding = null;
let origInternalModuleStat = null;
let moduleStatInstalled = false;

/** internal/modules/package_json_reader 的 read / purge 原始函数与安装状态。 */
let packageJsonReader = null;
let origPackageJsonRead = null;
let origPackageJsonPurge = null;
let packageJsonReaderInstalled = false;

/** internal/modules/helpers 的 loaderMethods 包装目标：这 4 个函数是 ESM/CJS 模块解析的真实收敛点。 */
const LOADER_METHOD_NAMES = [
  "getPackageScopeConfig",
  "readPackageJSON",
  "getNearestParentPackageJSON",
  "getNearestParentPackageJSONType",
];
let loaderMethods = null;
let origLoaderMethods = null;
let loaderMethodsInstalled = false;

/** 探测 Node 内部 realpathCacheKey，使同树路径共享祖先目录 lstat 缓存并保持完整符号链接语义。 */
let realpathCacheKey = null;
try {
  const internalRequire = createRequire(import.meta.url);
  const fsUtils = internalRequire("internal/fs/utils");
  if (fsUtils && typeof fsUtils.realpathCacheKey === "symbol") {
    realpathCacheKey = fsUtils.realpathCacheKey;
  }
} catch {
  /* 未启用 --expose-internals 或内部符号变化时安全退化为精确路径缓存 */
}

function normalizePrefix(item) {
  const resolved = path.resolve(item);
  return resolved.length > 1 && resolved.endsWith(path.sep)
    ? resolved.slice(0, -1)
    : resolved;
}

const forbiddenNonexistPrefixes = FORBIDDEN_NONEXIST_PREFIXES.map(normalizePrefix);

function parsePrefixes() {
  const raw = process.env.DSHM_FS_CACHE_PREFIXES;
  const list = raw
    ? raw.split(path.delimiter).map((item) => item.trim()).filter(Boolean)
    : DEFAULT_PREFIXES;
  return list.map(normalizePrefix);
}

/**
 * 负缓存前缀单独配置：只有严格只读层才允许缓存 ENOENT/ENOTDIR。
 * 即使环境变量误写了可写目录或伪文件系统，也会被 FORBIDDEN_NONEXIST_PREFIXES 过滤掉。
 */
function parseNonexistPrefixes() {
  const raw = process.env.DSHM_FS_CACHE_NONEXIST_PREFIXES;
  const list = raw
    ? raw.split(path.delimiter).map((item) => item.trim()).filter(Boolean)
    : DEFAULT_NONEXIST_PREFIXES;
  const allowed = [];
  for (const item of list) {
    const resolved = normalizePrefix(item);
    const forbidden = forbiddenNonexistPrefixes.some(
      (prefix) => resolved === prefix || resolved.startsWith(prefix + path.sep),
    );
    if (!forbidden) {
      allowed.push(resolved);
    }
  }
  return allowed;
}

function parseTtlMs() {
  const raw = process.env.DSHM_FS_CACHE_TTL_MS;
  if (raw === undefined || raw === "") return DEFAULT_TTL_MS;
  const parsed = Number(raw);
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : DEFAULT_TTL_MS;
}

/** 编译缓存主动落盘延迟；0 表示关闭定时触发（uninstall 与 SIGTERM 兜底仍然生效）。 */
function parseCompileCacheFlushMs() {
  const raw = process.env.DSHM_COMPILE_CACHE_FLUSH_MS;
  if (raw === undefined || raw === "") return DEFAULT_COMPILE_CACHE_FLUSH_MS;
  const parsed = Number(raw);
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : DEFAULT_COMPILE_CACHE_FLUSH_MS;
}

const allowedPrefixes = parsePrefixes();
const nonexistPrefixes = parseNonexistPrefixes();
const ttlMs = parseTtlMs();
const compileCacheFlushMs = parseCompileCacheFlushMs();
/** 预拼接前缀+分隔符，避免快路径每次 startsWith 都临时分配字符串。 */
const fastPrefixes = allowedPrefixes.map((prefix) => prefix + path.sep);

/** 编译缓存运行时状态：特性探测结果、成功次数与最近一次错误码（只用于观测与测试，不包含任何敏感数据）。 */
const compileCache = {
  supported: typeof nodeModule.flushCompileCache === "function",
  flushes: 0,
  lastError: null,
};
let compileCacheFlushTimer = null;
let compileCacheSigtermInstalled = false;

/**
 * 主动把 V8 编译缓存同步落盘。module.flushCompileCache 在 Node 22.1+ 可用；
 * 旧 Node 或未启用 NODE_COMPILE_CACHE 时安全 no-op，任何异常都不得影响 DSH 启动/退出。
 */
function flushCompileCacheSafely() {
  if (!compileCache.supported) return false;
  try {
    nodeModule.flushCompileCache();
    compileCache.flushes += 1;
    return true;
  } catch (err) {
    compileCache.lastError = err && (err.code || err.name) ? String(err.code || err.name) : "unknown";
    return false;
  }
}

/**
 * SIGTERM 兜底：先同步落盘，再按原语义继续退出。
 * 若 DSH 自己注册了 SIGTERM 监听器，则直接返回，让其原有停机清理照常执行；
 * 仅当本处理器是唯一监听者时移除自己并重新投递 SIGTERM，保持 Node 默认的信号终止语义。
 */
function onSigterm() {
  flushCompileCacheSafely();
  const others = process.listeners("SIGTERM").filter((listener) => listener !== onSigterm);
  if (others.length > 0) {
    return;
  }
  process.removeListener("SIGTERM", onSigterm);
  compileCacheSigtermInstalled = false;
  try {
    process.kill(process.pid, "SIGTERM");
  } catch {
    process.exit(143);
  }
}

const sharedRealpathMap = new Map();
const realpathResultCache = new Map();
const existsCache = new Map();
const statCache = new Map();
const lstatCache = new Map();
const packageJsonBufferCache = new Map();
const packageJsonUtf8Cache = new Map();
const readdirCache = new Map();
const moduleStatCache = new Map();
const manifestBufferCache = new Map();
const manifestUtf8Cache = new Map();
const packageJsonReaderCache = new Map();
const loaderMethodsCache = new Map();
/** accessSync / promises.access 的只读成功缓存（键带 F:/R: 模式前缀）。 */
const accessCache = new Map();

let active = true;
const stats = {
  hits: 0,
  misses: 0,
  bypasses: 0,
  fastPathHits: 0,
  moduleStatHits: 0,
  moduleStatMisses: 0,
  packageJsonReaderHits: 0,
  packageJsonReaderMisses: 0,
  manifestHits: 0,
  manifestMisses: 0,
  loaderMethodsHits: 0,
  loaderMethodsMisses: 0,
  installed: true,
};

function isUnderPrefix(resolved, prefixes) {
  for (let i = 0; i < prefixes.length; i += 1) {
    const prefix = prefixes[i];
    if (resolved === prefix || resolved.startsWith(prefix + path.sep)) {
      return true;
    }
  }
  return false;
}

/** 只有严格只读前缀才允许缓存 ENOENT/ENOTDIR；可写前缀一律透传。 */
function isNegativeCacheable(resolved) {
  return active && isUnderPrefix(resolved, nonexistPrefixes);
}

/**
 * 符号链接一律不缓存：只读层内也可能存在指向可写层或系统目录的链接，
 * 按链接路径缓存会把目标变化固化成过期结论。判定失败时保守视为不可缓存。
 */
function isSymlinkPath(resolved) {
  // 复用已有的 lstat 正缓存，避免重复触发 PRoot 系统调用；错误快照保守视为不可缓存。
  const key = "N:" + resolved;
  const cached = lstatCache.get(key);
  if (cached) {
    return cached.__err ? true : cached.isSymbolicLink();
  }
  try {
    const st = origLstatSync(resolved, { throwIfNoEntry: false });
    if (st) {
      if (st.isSymbolicLink()) {
        // 符号链接绝不写入任何缓存，保持链接目标变化的可见性。
        return true;
      }
      // 同一路径最多触发一次 lstat：正结果回填 lstat 与 exists 缓存。
      setBounded(lstatCache, key, st);
      setBounded(existsCache, resolved, true);
      return false;
    }
    // 严格只读前缀内的缺失也回填 ENOENT 快照，后续 lstat 不再穿透 PRoot。
    if (isNegativeCacheable(resolved)) {
      setBounded(lstatCache, key, makeErrorSnapshot({
        code: "ENOENT",
        errno: -2,
        syscall: "lstat",
        path: resolved,
        message: "ENOENT: no such file or directory, lstat '" + resolved + "'",
      }));
    }
    return false;
  } catch {
    return true;
  }
}

/**
 * 纯内存判定：只查已有 lstat 记录，绝不触发 origLstatSync。
 * 热路径（module stat / loaderMethods）用它避免每次正结果都多一次 PRoot 系统调用。
 */
function isKnownSymlink(resolved) {
  const cached = lstatCache.get("N:" + resolved);
  return Boolean(cached && !cached.__err && cached.isSymbolicLink());
}

/**
 * 干净绝对路径判定：只接受不会因 path.resolve() 改变形态的字符串。
 * 含 NUL、双分隔符、`/.`（同时覆盖 `..`）或末尾分隔符时一律退回慢路径，
 * 保证缓存键与原生调用看到的路径完全一致。
 */
function isCleanFastPath(text) {
  return (
    FAST_PATH_ENABLED
    && text.length > 0
    && !text.includes("\0")
    && !text.includes("//")
    && !text.includes("/.")
    && !text.endsWith("/")
  );
}

function toCacheablePath(input) {
  if (!active) return null;
  let pathText;
  if (typeof input === "string") {
    // ESM 内部会把文件 URL 转成字符串再传给 loaderMethods，这里先还原为文件系统路径。
    if (input.startsWith("file:")) {
      // POSIX 干净 file:/// 字符串直接切片，跳过 URL 解析与分配；含 %/NUL/?/# 时退回原生解析。
      if (FAST_PATH_ENABLED && input.startsWith("file:///")
          && !input.includes("%") && !input.includes("\0")
          && !input.includes("?") && !input.includes("#")) {
        pathText = input.slice(7);
      } else {
        try {
          pathText = fileURLToPath(input);
        } catch {
          return null;
        }
      }
    } else {
      pathText = input;
    }
  } else if (Buffer.isBuffer(input)) {
    pathText = input.toString("utf8");
  } else if (input instanceof URL) {
    if (input.protocol !== "file:") return null;
    // 干净 file:/// URL 直接取 pathname（已解码场景除外），避免 fileURLToPath 的额外分配。
    if (FAST_PATH_ENABLED && input.href.startsWith("file:///")
        && !input.pathname.includes("%") && !input.pathname.includes("\0")) {
      pathText = input.pathname;
    } else {
      try {
        pathText = fileURLToPath(input);
      } catch {
        return null;
      }
    }
  } else {
    return null;
  }
  if (pathText.length === 0 || pathText.includes("\0")) return null;
  // 零分配快路径：Android 只读运行层上的干净绝对路径直接复用原串，跳过 path.resolve()。
  if (isCleanFastPath(pathText)) {
    for (let i = 0; i < allowedPrefixes.length; i += 1) {
      if (pathText === allowedPrefixes[i] || pathText.startsWith(fastPrefixes[i])) {
        stats.fastPathHits += 1;
        return pathText;
      }
    }
    // 干净绝对 POSIX 路径且不匹配任何白名单：直接判为不可缓存，
    // 绝不调用 path.resolve()（消除 /root/projects、/lib、/etc 等的 normalizeString 开销）。
    if (pathText.charCodeAt(0) === 47) {
      return null;
    }
  }
  const resolved = path.resolve(pathText);
  for (let i = 0; i < allowedPrefixes.length; i += 1) {
    const prefix = allowedPrefixes[i];
    if (resolved === prefix || resolved.startsWith(prefix + path.sep)) {
      return resolved;
    }
  }
  return null;
}

function makeErrorSnapshot(err) {
  return {
    __err: true,
    code: err.code,
    errno: err.errno,
    syscall: err.syscall,
    path: err.path,
    message: err.message,
  };
}

function rethrowSnapshot(snapshot) {
  const err = new Error(snapshot.message);
  if (snapshot.code !== undefined) err.code = snapshot.code;
  if (snapshot.errno !== undefined) err.errno = snapshot.errno;
  if (snapshot.syscall !== undefined) err.syscall = snapshot.syscall;
  if (snapshot.path !== undefined) err.path = snapshot.path;
  throw err;
}

function setBounded(map, key, value) {
  if (map.size < MAX_CACHE_ENTRIES) {
    map.set(key, value);
  }
}

function normalizeRealpathEncoding(options) {
  if (options === undefined || options === null) return "utf8";
  if (typeof options === "string") return options;
  if (typeof options === "object") {
    if (realpathCacheKey && Object.prototype.hasOwnProperty.call(options, realpathCacheKey)) {
      return null;
    }
    const enc = options.encoding;
    return enc === undefined || enc === null ? "utf8" : enc;
  }
  return null;
}

function formatPathResult(str, encoding) {
  if (encoding === "buffer") return Buffer.from(str, "utf8");
  return str;
}

function resolveReadonlyRealpath(resolved, preferNative) {
  if (realpathResultCache.has(resolved)) {
    stats.hits += 1;
    const cached = realpathResultCache.get(resolved);
    if (cached && cached.__err) rethrowSnapshot(cached);
    return cached;
  }
  stats.misses += 1;
  try {
    let result;
    if (realpathCacheKey) {
      result = origRealpathSync(resolved, { encoding: "utf8", [realpathCacheKey]: sharedRealpathMap });
    } else if (preferNative && typeof origRealpathNative === "function") {
      result = origRealpathNative(resolved, "utf8");
    } else {
      result = origRealpathSync(resolved, "utf8");
    }
    setBounded(realpathResultCache, resolved, result);
    setBounded(existsCache, resolved, true);
    return result;
  } catch (err) {
    if (err && CACHEABLE_ERR_CODES.has(err.code) && isNegativeCacheable(resolved)) {
      setBounded(realpathResultCache, resolved, makeErrorSnapshot(err));
      if (err.code === "ENOENT") {
        setBounded(existsCache, resolved, false);
      }
    }
    throw err;
  }
}

function cachedRealpathSync(input, options) {
  const resolved = toCacheablePath(input);
  if (resolved === null) {
    stats.bypasses += 1;
    return origRealpathSync(input, options);
  }
  const encoding = normalizeRealpathEncoding(options);
  if (encoding !== "utf8" && encoding !== "utf-8" && encoding !== "buffer") {
    stats.bypasses += 1;
    return origRealpathSync(input, options);
  }
  const result = resolveReadonlyRealpath(resolved, false);
  return formatPathResult(result, encoding);
}

function cachedRealpathNative(input, options) {
  const resolved = toCacheablePath(input);
  if (resolved === null) {
    stats.bypasses += 1;
    return origRealpathNative(input, options);
  }
  const encoding = normalizeRealpathEncoding(options);
  if (encoding !== "utf8" && encoding !== "utf-8" && encoding !== "buffer") {
    stats.bypasses += 1;
    return origRealpathNative(input, options);
  }
  const result = resolveReadonlyRealpath(resolved, true);
  return formatPathResult(result, encoding);
}

cachedRealpathSync.native = cachedRealpathNative;

function cachedExistsSync(input) {
  const resolved = toCacheablePath(input);
  if (resolved === null) {
    stats.bypasses += 1;
    return origExistsSync(input);
  }
  // 单次 get 判断（boolean 缓存的 false 也是有效命中），避免 has+get 双次 hash。
  const cachedExists = existsCache.get(resolved);
  if (cachedExists !== undefined) {
    stats.hits += 1;
    return cachedExists;
  }
  stats.misses += 1;
  const exists = origExistsSync(resolved);
  // 正结果可缓存；false 只对严格只读前缀缓存，避免可写目录“先探测后创建”被假阴性挡住。
  if (exists || isNegativeCacheable(resolved)) {
    setBounded(existsCache, resolved, exists);
  }
  return exists;
}

function parseStatOptions(options) {
  if (options === undefined || options === null) {
    return { bigint: false, throwIfNoEntry: true };
  }
  if (typeof options !== "object") return null;
  const bigint = Boolean(options.bigint);
  const throwIfNoEntry = options.throwIfNoEntry !== false;
  return { bigint, throwIfNoEntry };
}

function makeCachedStat(origFn, cacheMap) {
  return function cachedStat(input, options) {
    const resolved = toCacheablePath(input);
    const parsed = resolved === null ? null : parseStatOptions(options);
    if (resolved === null || parsed === null) {
      stats.bypasses += 1;
      return origFn(input, options);
    }
    const cacheKey = (parsed.bigint ? "B:" : "N:") + resolved;
    // 单次 get：缓存值只可能是 Stat 或错误快照，永远不是 undefined。
    const cached = cacheMap.get(cacheKey);
    if (cached !== undefined) {
      stats.hits += 1;
      if (cached && cached.__err) {
        if (!parsed.throwIfNoEntry && cached.code === "ENOENT") return undefined;
        rethrowSnapshot(cached);
      }
      return cached;
    }
    stats.misses += 1;
    try {
      const value = origFn(resolved, { bigint: parsed.bigint, throwIfNoEntry: true });
      setBounded(cacheMap, cacheKey, value);
      setBounded(existsCache, resolved, true);
      return value;
    } catch (err) {
      // 负结果只对严格只读前缀入缓存；可写前缀保持透传，保证“探测未命中 → 创建 → 立即可见”。
      const cacheableError = err && CACHEABLE_ERR_CODES.has(err.code) && isNegativeCacheable(resolved);
      if (cacheableError) {
        setBounded(cacheMap, cacheKey, makeErrorSnapshot(err));
        if (err.code === "ENOENT") {
          setBounded(existsCache, resolved, false);
        }
      }
      if (err && err.code === "ENOENT" && !parsed.throwIfNoEntry) return undefined;
      throw err;
    }
  };
}

const cachedStatSync = makeCachedStat(origStatSync, statCache);
const cachedLstatSync = makeCachedStat(origLstatSync, lstatCache);

const PACKAGE_JSON_SUFFIX = path.sep + "package.json";

function parseReadFileOptions(options) {
  if (options === undefined || options === null) {
    return { encoding: null, cacheable: true };
  }
  if (typeof options === "string") {
    const lower = options.toLowerCase();
    return {
      encoding: lower === "utf8" || lower === "utf-8" ? "utf8" : null,
      cacheable: lower === "utf8" || lower === "utf-8",
    };
  }
  if (typeof options === "object") {
    if (options.signal !== undefined) return { encoding: null, cacheable: false };
    if (options.flag !== undefined && options.flag !== "r") return { encoding: null, cacheable: false };
    if (options.encoding === undefined || options.encoding === null) {
      return { encoding: null, cacheable: true };
    }
    if (typeof options.encoding === "string") {
      const lower = options.encoding.toLowerCase();
      if (lower === "utf8" || lower === "utf-8") {
        return { encoding: "utf8", cacheable: true };
      }
    }
  }
  return { encoding: null, cacheable: false };
}

/** 是否为可缓存的只读小清单后缀；避免热路径上创建数组闭包。 */
function hasManifestSuffix(resolved) {
  return resolved.endsWith(".json") || resolved.endsWith(".yml") || resolved.endsWith(".yaml");
}

/** 只读小清单（.json/.yml/.yaml）仅缓存正结果；缺失结果透传，避免掩盖可写层新增文件。 */
function cachedReadonlyManifestSync(resolved, parsed) {
  if (parsed.encoding === "utf8" && manifestUtf8Cache.has(resolved)) {
    stats.manifestHits += 1;
    return manifestUtf8Cache.get(resolved);
  }
  if (manifestBufferCache.has(resolved)) {
    stats.manifestHits += 1;
    const cached = manifestBufferCache.get(resolved);
    if (parsed.encoding === "utf8") {
      const text = cached.toString("utf8");
      setBounded(manifestUtf8Cache, resolved, text);
      return text;
    }
    return Buffer.from(cached);
  }
  stats.manifestMisses += 1;
  // 仅在 miss 后判定符号链接，避免每次命中都触发一次 PRoot lstat。
  if (isSymlinkPath(resolved)) {
    stats.bypasses += 1;
    const buf = origReadFileSync(resolved);
    return parsed.encoding === "utf8" ? buf.toString("utf8") : buf;
  }
  const buf = origReadFileSync(resolved);
  if (buf.length <= MAX_MANIFEST_BYTES) {
    setBounded(manifestBufferCache, resolved, buf);
    setBounded(existsCache, resolved, true);
    if (parsed.encoding === "utf8") {
      const text = buf.toString("utf8");
      setBounded(manifestUtf8Cache, resolved, text);
      return text;
    }
  }
  return parsed.encoding === "utf8" ? buf.toString("utf8") : buf;
}

function cachedReadFileSync(input, options) {
  const resolved = toCacheablePath(input);
  if (resolved === null) {
    stats.bypasses += 1;
    return origReadFileSync(input, options);
  }
  const isPackageJson = resolved.endsWith(PACKAGE_JSON_SUFFIX);
  const isManifest = !isPackageJson && hasManifestSuffix(resolved);
  // 非 package.json 的只读清单只在严格只读前缀下缓存，/usr/local 与工作区保持透传。
  if (!isPackageJson && (!isManifest || !isNegativeCacheable(resolved))) {
    stats.bypasses += 1;
    return origReadFileSync(input, options);
  }
  const parsed = parseReadFileOptions(options);
  if (!parsed.cacheable) {
    stats.bypasses += 1;
    return origReadFileSync(input, options);
  }
  // 符号链接可能指向可写层，透传原生读取，避免把链接目标内容固化进缓存。
  // 注意：这里只在 cache miss 后判定，命中路径必须保持纯内存零系统调用。
  if (isManifest) {
    return cachedReadonlyManifestSync(resolved, parsed);
  }
  if (parsed.encoding === "utf8" && packageJsonUtf8Cache.has(resolved)) {
    stats.hits += 1;
    const cached = packageJsonUtf8Cache.get(resolved);
    if (cached && cached.__err) rethrowSnapshot(cached);
    return cached;
  }
  if (packageJsonBufferCache.has(resolved)) {
    stats.hits += 1;
    const cached = packageJsonBufferCache.get(resolved);
    if (cached && cached.__err) rethrowSnapshot(cached);
    if (parsed.encoding === "utf8") {
      const text = cached.toString("utf8");
      setBounded(packageJsonUtf8Cache, resolved, text);
      return text;
    }
    return Buffer.from(cached);
  }
  stats.misses += 1;
  // 仅在确认为 miss 后做符号链接判定：符号链接不写入缓存，因此永远不会命中缓存。
  if (isSymlinkPath(resolved)) {
    stats.bypasses += 1;
    return origReadFileSync(input, options);
  }
  try {
    const buf = origReadFileSync(resolved);
    if (buf.length <= MAX_PACKAGE_JSON_BYTES) {
      setBounded(packageJsonBufferCache, resolved, buf);
      setBounded(existsCache, resolved, true);
      if (parsed.encoding === "utf8") {
        const text = buf.toString("utf8");
        setBounded(packageJsonUtf8Cache, resolved, text);
        return text;
      }
      return Buffer.from(buf);
    }
    return parsed.encoding === "utf8" ? buf.toString("utf8") : buf;
  } catch (err) {
    if (err && CACHEABLE_ERR_CODES.has(err.code) && isNegativeCacheable(resolved)) {
      const snap = makeErrorSnapshot(err);
      setBounded(packageJsonBufferCache, resolved, snap);
      setBounded(packageJsonUtf8Cache, resolved, snap);
      if (err.code === "ENOENT") {
        setBounded(existsCache, resolved, false);
      }
    }
    throw err;
  }
}

function normalizeReaddirEncoding(value) {
  if (value === undefined || value === null) return "utf8";
  if (typeof value !== "string") return null;
  const lower = value.toLowerCase();
  if (lower === "utf8" || lower === "utf-8") return "utf8";
  if (lower === "buffer") return "buffer";
  return null;
}

function parseReaddirOptions(options) {
  if (options === undefined) {
    return { encoding: "utf8", withFileTypes: false, cacheable: true };
  }
  // null 在原生 readdirSync 中是非法参数；透传以保持原有 TypeError 语义。
  if (options === null) return { cacheable: false };
  if (typeof options === "string") {
    const encoding = normalizeReaddirEncoding(options);
    return encoding === null ? { cacheable: false } : { encoding, withFileTypes: false, cacheable: true };
  }
  if (typeof options !== "object") return { cacheable: false };
  // 递归 readdir 的返回语义（相对路径、缺失目录跳过）不适合快照缓存，直接透传。
  if (options.recursive) return { cacheable: false };
  const withFileTypes = Boolean(options.withFileTypes);
  const encoding = withFileTypes ? "utf8" : normalizeReaddirEncoding(options.encoding);
  if (encoding === null) return { cacheable: false };
  return { encoding, withFileTypes, cacheable: true };
}

/** 复制 Dirent，避免调用方修改 name/类型方法影响缓存快照。 */
function cloneDirent(dirent) {
  const clone = Object.create(Object.getPrototypeOf(dirent));
  Object.defineProperties(clone, Object.getOwnPropertyDescriptors(dirent));
  return clone;
}

/** 缓存内保存不可被调用方触碰的快照；每次命中都重新物化一份副本。 */
function snapshotReaddir(result, parsed) {
  if (parsed.withFileTypes) {
    return { kind: "dirents", values: result.map(cloneDirent) };
  }
  if (parsed.encoding === "buffer") {
    return { kind: "buffers", values: result.map((item) => Buffer.from(item)) };
  }
  return { kind: "names", values: result.slice() };
}

function materializeReaddir(snapshot) {
  if (snapshot && snapshot.__err) {
    rethrowSnapshot(snapshot);
  }
  switch (snapshot.kind) {
    case "dirents":
      return snapshot.values.map(cloneDirent);
    case "buffers":
      return snapshot.values.map((item) => Buffer.from(item));
    default:
      return snapshot.values.slice();
  }
}

function cachedReaddirSync(input, options) {
  const resolved = toCacheablePath(input);
  const parsed = resolved === null ? null : parseReaddirOptions(options);
  if (parsed === null || !parsed.cacheable) {
    stats.bypasses += 1;
    return origReaddirSync(input, options);
  }
  const cacheKey = parsed.encoding + "|" + (parsed.withFileTypes ? "D" : "F") + "|" + resolved;
  if (readdirCache.has(cacheKey)) {
    stats.hits += 1;
    return materializeReaddir(readdirCache.get(cacheKey));
  }
  stats.misses += 1;
  try {
    const result = origReaddirSync(resolved, { encoding: parsed.encoding, withFileTypes: parsed.withFileTypes });
    const snapshot = snapshotReaddir(result, parsed);
    setBounded(readdirCache, cacheKey, snapshot);
    return materializeReaddir(snapshot);
  } catch (err) {
    // 与其它 fs 方法一致：ENOENT/ENOTDIR 只对严格只读前缀缓存。
    if (err && CACHEABLE_ERR_CODES.has(err.code) && isNegativeCacheable(resolved)) {
      setBounded(readdirCache, cacheKey, makeErrorSnapshot(err));
    }
    throw err;
  }
}

/** 仅 F_OK / R_OK 的只读探测可缓存；W_OK / X_OK 及其组合一律透传。 */
function cacheableAccessMode(mode) {
  if (mode === undefined) return fs.constants.F_OK;
  if (typeof mode !== "number") return null;
  return mode === fs.constants.F_OK || mode === fs.constants.R_OK ? mode : null;
}

function accessCacheKey(mode, resolved) {
  return (mode === fs.constants.R_OK ? "R:" : "F:") + resolved;
}

function cachedAccessSync(input, mode) {
  const accessMode = cacheableAccessMode(mode);
  if (accessMode === null) {
    return origAccessSync(input, mode);
  }
  const resolved = toCacheablePath(input);
  if (resolved === null) {
    return origAccessSync(input, mode);
  }
  const key = accessCacheKey(accessMode, resolved);
  const cached = accessCache.get(key);
  if (cached === true) {
    return undefined;
  }
  if (cached && cached.__err) {
    rethrowSnapshot(cached);
  }
  try {
    origAccessSync(input, mode);
  } catch (err) {
    // 缺失只在严格只读前缀下缓存；EACCES 等其它错误透传不缓存。
    if (err && CACHEABLE_ERR_CODES.has(err.code) && isNegativeCacheable(resolved)) {
      setBounded(accessCache, key, makeErrorSnapshot(err));
    }
    throw err;
  }
  // 已知符号链接不写入缓存，保持链接目标变化的可见性。
  if (!isKnownSymlink(resolved)) {
    setBounded(accessCache, key, true);
  }
  return undefined;
}

/** promises 包装：可缓存时直接复用同步缓存，未命中/不可缓存时调用原始 Promise API。 */
function promiseStat(input, options) {
  if (options !== undefined && options !== null && typeof options !== "object") {
    return origPromiseStat.call(this, input, options);
  }
  const resolved = toCacheablePath(input);
  if (resolved === null) {
    return origPromiseStat.call(this, input, options);
  }
  try {
    return Promise.resolve(cachedStatSync(resolved, options));
  } catch (err) {
    return Promise.reject(err);
  }
}

function promiseLstat(input, options) {
  if (options !== undefined && options !== null && typeof options !== "object") {
    return origPromiseLstat.call(this, input, options);
  }
  const resolved = toCacheablePath(input);
  if (resolved === null) {
    return origPromiseLstat.call(this, input, options);
  }
  try {
    return Promise.resolve(cachedLstatSync(resolved, options));
  } catch (err) {
    return Promise.reject(err);
  }
}

function promiseRealpath(input, options) {
  const resolved = toCacheablePath(input);
  if (resolved === null) {
    return origPromiseRealpath.call(this, input, options);
  }
  try {
    return Promise.resolve(cachedRealpathSync(resolved, options));
  } catch (err) {
    return Promise.reject(err);
  }
}

function promiseReaddir(input, options) {
  const resolved = toCacheablePath(input);
  if (resolved === null) {
    return origPromiseReaddir.call(this, input, options);
  }
  const parsed = parseReaddirOptions(options);
  if (!parsed.cacheable) {
    return origPromiseReaddir.call(this, input, options);
  }
  try {
    return Promise.resolve(cachedReaddirSync(resolved, options));
  } catch (err) {
    return Promise.reject(err);
  }
}

function promiseReadFile(input, options) {
  const resolved = toCacheablePath(input);
  if (resolved === null) {
    return origPromiseReadFile.call(this, input, options);
  }
  const isPackageJson = resolved.endsWith(PACKAGE_JSON_SUFFIX);
  const isManifest = !isPackageJson && hasManifestSuffix(resolved);
  if (!isPackageJson && (!isManifest || !isNegativeCacheable(resolved))) {
    return origPromiseReadFile.call(this, input, options);
  }
  if (!parseReadFileOptions(options).cacheable) {
    return origPromiseReadFile.call(this, input, options);
  }
  try {
    return Promise.resolve(cachedReadFileSync(resolved, options));
  } catch (err) {
    return Promise.reject(err);
  }
}

async function promiseAccess(input, mode) {
  const accessMode = cacheableAccessMode(mode);
  if (accessMode === null) {
    return origPromiseAccess.call(this, input, mode);
  }
  const resolved = toCacheablePath(input);
  if (resolved === null) {
    return origPromiseAccess.call(this, input, mode);
  }
  const key = accessCacheKey(accessMode, resolved);
  const cached = accessCache.get(key);
  if (cached === true) {
    return undefined;
  }
  if (cached && cached.__err) {
    rethrowSnapshot(cached);
  }
  try {
    await origPromiseAccess.call(this, input, mode);
  } catch (err) {
    if (err && CACHEABLE_ERR_CODES.has(err.code) && isNegativeCacheable(resolved)) {
      setBounded(accessCache, key, makeErrorSnapshot(err));
    }
    throw err;
  }
  if (!isKnownSymlink(resolved)) {
    setBounded(accessCache, key, true);
  }
  return undefined;
}

/**
 * 安全包装 process.binding('fs').internalModuleStat（Node 26 不再从 internal/modules/helpers 导出）：
 * 描述符必须可写或可配置；0=文件 / 1=目录的正结果始终缓存，负结果只在严格只读前缀下缓存，
 * 避免可写层出现模块解析假阴性。无 --expose-internals、绑定缺失或描述符不可写时静默跳过。
 */
function installInternalModuleStatCache() {
  try {
    const binding = process.binding("fs");
    const descriptor = Object.getOwnPropertyDescriptor(binding, "internalModuleStat");
    if (
      !descriptor
      || typeof descriptor.value !== "function"
      || (!descriptor.writable && !descriptor.configurable)
    ) {
      return false;
    }
    const original = descriptor.value;
    const wrapper = function cachedInternalModuleStat(input, ...rest) {
      const resolved = toCacheablePath(input);
      if (resolved === null) {
        return original.call(this, input, ...rest);
      }
      // 单次 get：缓存值为数字（0/1/负值），用 !== undefined 判断命中。
      const cachedModuleStat = moduleStatCache.get(resolved);
      if (cachedModuleStat !== undefined) {
        stats.moduleStatHits += 1;
        return cachedModuleStat;
      }
      stats.moduleStatMisses += 1;
      const result = original.call(this, input, ...rest);
      if (result === 0 || result === 1) {
        // 正结果只查内存里已知的符号链接，绝不主动触发 PRoot lstat。
        if (!isKnownSymlink(resolved)) {
          setBounded(moduleStatCache, resolved, result);
        }
      } else if (result < 0 && isNegativeCacheable(resolved)) {
        // 负结果不可能是符号链接：只在内存里查已有 lstat 记录。
        if (!isKnownSymlink(resolved)) {
          setBounded(moduleStatCache, resolved, result);
        }
      }
      return result;
    };
    binding.internalModuleStat = wrapper;
    if (binding.internalModuleStat !== wrapper) {
      return false;
    }
    moduleStatBinding = binding;
    origInternalModuleStat = original;
    moduleStatInstalled = true;
    return true;
  } catch {
    moduleStatBinding = null;
    origInternalModuleStat = null;
    moduleStatInstalled = false;
    return false;
  }
}

/**
 * 包装 internal/modules/package_json_reader.read：仅缓存严格只读前缀下“确实存在”的 package.json 结果。
 * Node 26 的 read 接受字符串路径（传 URL 会触发原生断言），且原生层已按路径缓存反序列化结果；
 * 这里省掉重复的原生读取与结果展开，但绝不缓存缺失结果，避免模块解析假阴性。
 * read 与 purgePackageJSONCacheForPrefix 都可写/可配置，uninstall() 会逐一还原并清空本层缓存；
 * 无 --expose-internals 或内部模块结构变化时静默跳过。
 */
function installPackageJsonReaderCache() {
  // 实测 Node 26 启动链路中 read 调用数为 0，全部由 loaderMethods 承接；
  // 默认不安装，避免无谓的内部模块 require 与代理开销，测试可显式开启。
  if (process.env.DSHM_FS_CACHE_PACKAGE_READER !== "1") {
    return false;
  }
  try {
    const internalRequire = createRequire(import.meta.url);
    const reader = internalRequire("internal/modules/package_json_reader");
    const readDescriptor = Object.getOwnPropertyDescriptor(reader, "read");
    const purgeDescriptor = Object.getOwnPropertyDescriptor(reader, "purgePackageJSONCacheForPrefix");
    if (
      !readDescriptor
      || typeof readDescriptor.value !== "function"
      || (!readDescriptor.writable && !readDescriptor.configurable)
    ) {
      return false;
    }
    if (
      !purgeDescriptor
      || typeof purgeDescriptor.value !== "function"
      || (!purgeDescriptor.writable && !purgeDescriptor.configurable)
    ) {
      return false;
    }
    const originalRead = readDescriptor.value;
    const originalPurge = purgeDescriptor.value;
    const wrappedRead = function cachedPackageJsonRead(jsonPath, options) {
      // read 只接受字符串路径；URL 等其它入参保持原生行为（包括其原生报错/断言）。
      if (typeof jsonPath !== "string") {
        return originalRead.call(this, jsonPath, options);
      }
      const resolved = toCacheablePath(jsonPath);
      if (resolved === null || !isNegativeCacheable(resolved)) {
        return originalRead.call(this, jsonPath, options);
      }
      const isESM = Boolean(options && options.isESM);
      const cacheKey = (isESM ? "E:" : "C:") + resolved;
      const cached = packageJsonReaderCache.get(cacheKey);
      if (cached !== undefined) {
        stats.packageJsonReaderHits += 1;
        // 与原生一致：每次返回独立的顶层对象，避免调用方修改污染缓存。
        return { __proto__: null, ...cached };
      }
      stats.packageJsonReaderMisses += 1;
      const result = originalRead.call(this, jsonPath, options);
      // 只缓存存在的结果；缺失/无效 JSON 交给原生路径处理，绝不制造模块解析假阴性。
      if (result && result.exists === true) {
        setBounded(packageJsonReaderCache, cacheKey, { __proto__: null, ...result });
      }
      return result;
    };
    const wrappedPurge = function cachedPurgePackageJson(prefix) {
      // 任何显式 purge 都保守清空本层缓存，保持与原生失效语义一致。
      packageJsonReaderCache.clear();
      return originalPurge.call(this, prefix);
    };
    reader.read = wrappedRead;
    reader.purgePackageJSONCacheForPrefix = wrappedPurge;
    if (reader.read !== wrappedRead || reader.purgePackageJSONCacheForPrefix !== wrappedPurge) {
      try {
        reader.read = originalRead;
        reader.purgePackageJSONCacheForPrefix = originalPurge;
      } catch {
        /* 回滚失败时保持可用原生函数优先，下面统一标记为未安装。 */
      }
      return false;
    }
    packageJsonReader = reader;
    origPackageJsonRead = originalRead;
    origPackageJsonPurge = originalPurge;
    packageJsonReaderInstalled = true;
    return true;
  } catch {
    packageJsonReader = null;
    origPackageJsonRead = null;
    origPackageJsonPurge = null;
    packageJsonReaderInstalled = false;
    return false;
  }
}

/**
 * 包装 internal/modules/helpers 的 loaderMethods：这是 ESM 与 CJS 模块解析的真实收敛点。
 * esm/get_format 在模块加载时会解构 package_json_reader 的导出，但 package_json_reader 内部
 * 每次调用都回落到 loaderMethods，因此包装这里对早期解构的调用方同样生效。
 * 只缓存只读前缀下的正结果与严格只读前缀下的缺失结果；符号链接、可写层与伪文件系统一律透传。
 * 任一属性不可写即整体回滚并保持原生函数，uninstall() 会逐一还原。
 */
function installLoaderMethodsCache() {
  try {
    const internalRequire = createRequire(import.meta.url);
    const helpers = internalRequire("internal/modules/helpers");
    const methods = helpers && helpers.loaderMethods;
    if (!methods) return false;
    const originals = {};
    for (const name of LOADER_METHOD_NAMES) {
      const descriptor = Object.getOwnPropertyDescriptor(methods, name);
      if (
        !descriptor
        || typeof descriptor.value !== "function"
        || (!descriptor.writable && !descriptor.configurable)
      ) {
        return false;
      }
      originals[name] = descriptor.value;
    }
    const wrapped = {
      getPackageScopeConfig(input) {
        const resolved = typeof input === "string" ? toCacheablePath(input) : null;
        if (resolved === null || !isNegativeCacheable(resolved)) {
          return originals.getPackageScopeConfig.call(this, input);
        }
        const key = "S:" + resolved;
        // 单次 get：本层只缓存数组/字符串，值不会是 undefined。
        const cached = loaderMethodsCache.get(key);
        if (cached !== undefined) {
          stats.loaderMethodsHits += 1;
          return Array.isArray(cached) ? cached.slice() : cached;
        }
        stats.loaderMethodsMisses += 1;
        // 仅在 miss 后判定符号链接：只查内存已知记录，绝不触发 PRoot lstat。
        if (isKnownSymlink(resolved)) {
          stats.bypasses += 1;
          return originals.getPackageScopeConfig.call(this, input);
        }
        const result = originals.getPackageScopeConfig.call(this, input);
        // 正结果是原生数组；字符串表示“未找到 package.json”，只允许严格只读前缀负缓存。
        if (Array.isArray(result) || (typeof result === "string" && isNegativeCacheable(resolved))) {
          setBounded(loaderMethodsCache, key, Array.isArray(result) ? result.slice() : result);
        }
        return result;
      },
      readPackageJSON(jsonPath, isESM, base, specifier) {
        const resolved = typeof jsonPath === "string" ? toCacheablePath(jsonPath) : null;
        if (resolved === null || !isNegativeCacheable(resolved)) {
          return originals.readPackageJSON.call(this, jsonPath, isESM, base, specifier);
        }
        const key = (isESM ? "E:" : "C:") + resolved;
        // 单次 get：本层只缓存数组，值不会是 undefined。
        const cached = loaderMethodsCache.get(key);
        if (cached !== undefined) {
          stats.loaderMethodsHits += 1;
          return Array.isArray(cached) ? cached.slice() : cached;
        }
        stats.loaderMethodsMisses += 1;
        if (isKnownSymlink(resolved)) {
          stats.bypasses += 1;
          return originals.readPackageJSON.call(this, jsonPath, isESM, base, specifier);
        }
        const result = originals.readPackageJSON.call(this, jsonPath, isESM, base, specifier);
        // 只缓存成功解析的数组结果，缺失或非法 JSON 交给原生路径处理。
        if (Array.isArray(result)) {
          setBounded(loaderMethodsCache, key, result.slice());
        }
        return result;
      },
      getNearestParentPackageJSON(input) {
        const resolved = typeof input === "string" ? toCacheablePath(input) : null;
        if (resolved === null || !isNegativeCacheable(resolved)) {
          return originals.getNearestParentPackageJSON.call(this, input);
        }
        const key = "N:" + resolved;
        const cachedHit = loaderMethodsCache.has(key);
        if (cachedHit) {
          stats.loaderMethodsHits += 1;
          return loaderMethodsCache.get(key);
        }
        stats.loaderMethodsMisses += 1;
        if (isKnownSymlink(resolved)) {
          stats.bypasses += 1;
          return originals.getNearestParentPackageJSON.call(this, input);
        }
        const result = originals.getNearestParentPackageJSON.call(this, input);
        if (typeof result === "string" || (result === undefined && isNegativeCacheable(resolved))) {
          setBounded(loaderMethodsCache, key, result);
        }
        return result;
      },
      getNearestParentPackageJSONType(input) {
        const resolved = typeof input === "string" ? toCacheablePath(input) : null;
        if (resolved === null || !isNegativeCacheable(resolved)) {
          return originals.getNearestParentPackageJSONType.call(this, input);
        }
        const key = "T:" + resolved;
        const cachedHit = loaderMethodsCache.has(key);
        if (cachedHit) {
          stats.loaderMethodsHits += 1;
          return loaderMethodsCache.get(key);
        }
        stats.loaderMethodsMisses += 1;
        if (isKnownSymlink(resolved)) {
          stats.bypasses += 1;
          return originals.getNearestParentPackageJSONType.call(this, input);
        }
        const result = originals.getNearestParentPackageJSONType.call(this, input);
        if (typeof result === "string" || (result === undefined && isNegativeCacheable(resolved))) {
          setBounded(loaderMethodsCache, key, result);
        }
        return result;
      },
    };
    for (const name of LOADER_METHOD_NAMES) {
      methods[name] = wrapped[name];
    }
    for (const name of LOADER_METHOD_NAMES) {
      if (methods[name] !== wrapped[name]) {
        for (const other of LOADER_METHOD_NAMES) {
          try {
            methods[other] = originals[other];
          } catch {
            /* 回滚失败时保持原生函数优先，下面统一标记为未安装。 */
          }
        }
        return false;
      }
    }
    loaderMethods = methods;
    origLoaderMethods = originals;
    loaderMethodsInstalled = true;
    return true;
  } catch {
    loaderMethods = null;
    origLoaderMethods = null;
    loaderMethodsInstalled = false;
    return false;
  }
}

function uninstall() {
  if (!active) return;
  active = false;
  stats.installed = false;
  fs.realpathSync = origRealpathSync;
  fs.existsSync = origExistsSync;
  fs.statSync = origStatSync;
  fs.lstatSync = origLstatSync;
  fs.readFileSync = origReadFileSync;
  fs.readdirSync = origReaddirSync;
  fs.accessSync = origAccessSync;
  // fs.promises 逐项恢复原生引用，避免长期驻留影响运行期行为。
  fs.promises.stat = origPromiseStat;
  fs.promises.lstat = origPromiseLstat;
  fs.promises.realpath = origPromiseRealpath;
  fs.promises.readdir = origPromiseReaddir;
  fs.promises.access = origPromiseAccess;
  fs.promises.readFile = origPromiseReadFile;
  syncBuiltinESMExports();
  // 彻底还原内部模块 stat 绑定；绑定已被替换或异常时保持原样，绝不抛错。
  if (moduleStatInstalled && moduleStatBinding && origInternalModuleStat) {
    try {
      moduleStatBinding.internalModuleStat = origInternalModuleStat;
    } catch {
      /* 绑定已被外部替换：保持现状，避免在卸载路径抛出异常。 */
    }
  }
  moduleStatInstalled = false;
  moduleStatBinding = null;
  origInternalModuleStat = null;
  // 完整还原 package_json_reader 的 read / purge；内部模块已被替换时不抛错，保持现场。
  if (packageJsonReaderInstalled && packageJsonReader) {
    if (origPackageJsonRead) {
      try {
        packageJsonReader.read = origPackageJsonRead;
      } catch {
        /* read 已被外部替换：保持现状，不影响 purge 的独立恢复。 */
      }
    }
    if (origPackageJsonPurge) {
      try {
        packageJsonReader.purgePackageJSONCacheForPrefix = origPackageJsonPurge;
      } catch {
        /* purge 已被外部替换：保持现状，避免在卸载路径抛出异常。 */
      }
    }
  }
  packageJsonReaderInstalled = false;
  packageJsonReader = null;
  origPackageJsonRead = null;
  origPackageJsonPurge = null;
  // 完整还原 loaderMethods 的 4 个函数；已被外部替换的属性保持现状，卸载路径绝不抛错。
  if (loaderMethodsInstalled && loaderMethods && origLoaderMethods) {
    for (const name of LOADER_METHOD_NAMES) {
      try {
        loaderMethods[name] = origLoaderMethods[name];
      } catch {
        /* 属性被外部改为不可写时保持现状，避免在卸载路径抛出异常。 */
      }
    }
  }
  loaderMethodsInstalled = false;
  loaderMethods = null;
  origLoaderMethods = null;
  sharedRealpathMap.clear();
  realpathResultCache.clear();
  existsCache.clear();
  statCache.clear();
  lstatCache.clear();
  packageJsonBufferCache.clear();
  packageJsonUtf8Cache.clear();
  readdirCache.clear();
  moduleStatCache.clear();
  manifestBufferCache.clear();
  manifestUtf8Cache.clear();
  packageJsonReaderCache.clear();
  loaderMethodsCache.clear();
  accessCache.clear();
  // 启动窗口结束（TTL）或显式 uninstall 时再落盘一次：即使此后进程被 OOM / SIGKILL 强杀，
  // 启动阶段已编译的模块也已在磁盘上。SIGTERM 监听器保留到进程结束，继续兜底后续加载的模块。
  if (compileCacheFlushTimer !== null) {
    clearTimeout(compileCacheFlushTimer);
    compileCacheFlushTimer = null;
  }
  flushCompileCacheSafely();
}

fs.realpathSync = cachedRealpathSync;
fs.existsSync = cachedExistsSync;
fs.statSync = cachedStatSync;
fs.lstatSync = cachedLstatSync;
fs.readFileSync = cachedReadFileSync;
fs.readdirSync = cachedReaddirSync;
fs.accessSync = cachedAccessSync;
fs.promises.stat = promiseStat;
fs.promises.lstat = promiseLstat;
fs.promises.realpath = promiseRealpath;
fs.promises.readdir = promiseReaddir;
fs.promises.access = promiseAccess;
fs.promises.readFile = promiseReadFile;
syncBuiltinESMExports();
installInternalModuleStatCache();
installPackageJsonReaderCache();
installLoaderMethodsCache();

if (ttlMs > 0) {
  const timer = setTimeout(uninstall, ttlMs);
  if (typeof timer.unref === "function") {
    timer.unref();
  }
}

if (compileCache.supported) {
  // prepend 保证在 DSH 自己的 SIGTERM 处理器之前同步落盘；不改变其它监听者的执行与退出语义。
  process.prependListener("SIGTERM", onSigterm);
  compileCacheSigtermInstalled = true;
  if (compileCacheFlushMs > 0) {
    // 启动稳定窗口后主动落盘；unref 保证该定时器永远不会拖住进程正常退出。
    compileCacheFlushTimer = setTimeout(() => {
      compileCacheFlushTimer = null;
      flushCompileCacheSafely();
    }, compileCacheFlushMs);
    if (typeof compileCacheFlushTimer.unref === "function") {
      compileCacheFlushTimer.unref();
    }
  }
}

globalThis[STATS_SYMBOL] = {
  stats,
  uninstall,
  flushCompileCache: flushCompileCacheSafely,
  get active() {
    return active;
  },
  get prefixes() {
    return allowedPrefixes.slice();
  },
  get negativePrefixes() {
    return nonexistPrefixes.slice();
  },
  get usesRealpathCacheKey() {
    return realpathCacheKey !== null;
  },
  get usesInternalModuleStat() {
    return moduleStatInstalled;
  },
  get usesPackageJsonReader() {
    return packageJsonReaderInstalled;
  },
  get usesLoaderMethods() {
    return loaderMethodsInstalled;
  },
  get compileCacheSupported() {
    return compileCache.supported;
  },
  get compileCacheFlushes() {
    return compileCache.flushes;
  },
  get compileCacheLastError() {
    return compileCache.lastError;
  },
  get compileCacheFlushMs() {
    return compileCacheFlushMs;
  },
  get compileCacheSigtermInstalled() {
    return compileCacheSigtermInstalled;
  },
};
