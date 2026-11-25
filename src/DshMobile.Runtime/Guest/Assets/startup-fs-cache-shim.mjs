/**
 * 启动期只读运行层文件系统元数据缓存垫片，以 node --import 预加载。
 *
 * 仅针对不可变只读运行层前缀（默认 /opt/dshapp/runtime 与 /usr/local）缓存同步
 * realpathSync / realpathSync.native / existsSync / statSync / lstatSync / readdirSync 及 package.json
 * 读取结果，并安全包装 process.binding('fs').internalModuleStat，减少 PRoot ptrace 路径拦截放大。
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
const MAX_CACHE_ENTRIES = 16_384;
const CACHEABLE_ERR_CODES = new Set(["ENOENT", "ENOTDIR"]);
const STATS_SYMBOL = Symbol.for("dshmobile.startupFsCache.state");

const origRealpathSync = fs.realpathSync;
const origRealpathNative = fs.realpathSync.native;
const origExistsSync = fs.existsSync;
const origStatSync = fs.statSync;
const origLstatSync = fs.lstatSync;
const origReadFileSync = fs.readFileSync;
const origReaddirSync = fs.readdirSync;

/** 内部模块 stat 绑定（process.binding('fs')）的原始函数；不可用或不可写时为 null。 */
let moduleStatBinding = null;
let origInternalModuleStat = null;
let moduleStatInstalled = false;

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

let active = true;
const stats = {
  hits: 0,
  misses: 0,
  bypasses: 0,
  moduleStatHits: 0,
  moduleStatMisses: 0,
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

function toCacheablePath(input) {
  if (!active) return null;
  let pathText;
  if (typeof input === "string") {
    pathText = input;
  } else if (Buffer.isBuffer(input)) {
    pathText = input.toString("utf8");
  } else if (input instanceof URL) {
    if (input.protocol !== "file:") return null;
    try {
      pathText = fileURLToPath(input);
    } catch {
      return null;
    }
  } else {
    return null;
  }
  if (pathText.length === 0 || pathText.includes("\0")) return null;
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
  if (existsCache.has(resolved)) {
    stats.hits += 1;
    return existsCache.get(resolved);
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
    if (cacheMap.has(cacheKey)) {
      stats.hits += 1;
      const cached = cacheMap.get(cacheKey);
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

function cachedReadFileSync(input, options) {
  const resolved = toCacheablePath(input);
  if (resolved === null || !resolved.endsWith(PACKAGE_JSON_SUFFIX)) {
    stats.bypasses += 1;
    return origReadFileSync(input, options);
  }
  const parsed = parseReadFileOptions(options);
  if (!parsed.cacheable) {
    stats.bypasses += 1;
    return origReadFileSync(input, options);
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

/**
 * 安全包装 process.binding('fs').internalModuleStat（Node 26 不再从 internal/modules/helpers 导出）：
 * 描述符必须可写或可配置；只缓存 0=文件 / 1=目录的正结果，负结果透传，避免模块解析假阴性。
 * 无 --expose-internals、绑定缺失或描述符不可写时静默跳过，不影响启动。
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
      if (moduleStatCache.has(resolved)) {
        stats.moduleStatHits += 1;
        return moduleStatCache.get(resolved);
      }
      stats.moduleStatMisses += 1;
      const result = original.call(this, input, ...rest);
      if (result === 0 || result === 1) {
        setBounded(moduleStatCache, resolved, result);
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
  sharedRealpathMap.clear();
  realpathResultCache.clear();
  existsCache.clear();
  statCache.clear();
  lstatCache.clear();
  packageJsonBufferCache.clear();
  packageJsonUtf8Cache.clear();
  readdirCache.clear();
  moduleStatCache.clear();
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
syncBuiltinESMExports();
installInternalModuleStatCache();

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
