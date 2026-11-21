/**
 * 启动期只读运行层文件系统元数据缓存垫片，以 node --import 预加载。
 *
 * 仅针对不可变只读运行层前缀（默认 /opt/dshapp/runtime 与 /usr/local）缓存同步
 * realpathSync / realpathSync.native / existsSync / statSync / lstatSync 及 package.json
 * 读取结果，减少 PRoot ptrace 路径拦截放大；绝不缓存用户工作区（/root/projects）或临时目录。
 *
 * 启动窗口结束后（默认 45 秒）自动恢复全部原生 fs 方法并清空缓存，避免长期驻留内存或
 * 影响运行期更新。本文件为本仓库自有资产，不修改上游 link-shim.mjs。
 */

import fs from "node:fs";
import path from "node:path";
import { createRequire, syncBuiltinESMExports } from "node:module";
import { fileURLToPath } from "node:url";

const DEFAULT_PREFIXES = ["/opt/dshapp/runtime", "/usr/local"];
const DEFAULT_TTL_MS = 45_000;
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

function parsePrefixes() {
  const raw = process.env.DSHM_FS_CACHE_PREFIXES;
  const list = raw
    ? raw.split(path.delimiter).map((item) => item.trim()).filter(Boolean)
    : DEFAULT_PREFIXES;
  return list.map((item) => {
    const resolved = path.resolve(item);
    return resolved.length > 1 && resolved.endsWith(path.sep)
      ? resolved.slice(0, -1)
      : resolved;
  });
}

function parseTtlMs() {
  const raw = process.env.DSHM_FS_CACHE_TTL_MS;
  if (raw === undefined || raw === "") return DEFAULT_TTL_MS;
  const parsed = Number(raw);
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : DEFAULT_TTL_MS;
}

const allowedPrefixes = parsePrefixes();
const ttlMs = parseTtlMs();

const sharedRealpathMap = new Map();
const realpathResultCache = new Map();
const existsCache = new Map();
const statCache = new Map();
const lstatCache = new Map();
const packageJsonBufferCache = new Map();
const packageJsonUtf8Cache = new Map();

let active = true;
const stats = {
  hits: 0,
  misses: 0,
  bypasses: 0,
  installed: true,
};

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
    if (err && CACHEABLE_ERR_CODES.has(err.code)) {
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
  setBounded(existsCache, resolved, exists);
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
      if (err && CACHEABLE_ERR_CODES.has(err.code)) {
        setBounded(cacheMap, cacheKey, makeErrorSnapshot(err));
        if (err.code === "ENOENT") {
          setBounded(existsCache, resolved, false);
          if (!parsed.throwIfNoEntry) return undefined;
        }
      }
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
    if (err && CACHEABLE_ERR_CODES.has(err.code)) {
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

function uninstall() {
  if (!active) return;
  active = false;
  stats.installed = false;
  fs.realpathSync = origRealpathSync;
  fs.existsSync = origExistsSync;
  fs.statSync = origStatSync;
  fs.lstatSync = origLstatSync;
  fs.readFileSync = origReadFileSync;
  syncBuiltinESMExports();
  sharedRealpathMap.clear();
  realpathResultCache.clear();
  existsCache.clear();
  statCache.clear();
  lstatCache.clear();
  packageJsonBufferCache.clear();
  packageJsonUtf8Cache.clear();
}

fs.realpathSync = cachedRealpathSync;
fs.existsSync = cachedExistsSync;
fs.statSync = cachedStatSync;
fs.lstatSync = cachedLstatSync;
fs.readFileSync = cachedReadFileSync;
syncBuiltinESMExports();

if (ttlMs > 0) {
  const timer = setTimeout(uninstall, ttlMs);
  if (typeof timer.unref === "function") {
    timer.unref();
  }
}

globalThis[STATS_SYMBOL] = {
  stats,
  uninstall,
  get active() {
    return active;
  },
  get prefixes() {
    return allowedPrefixes.slice();
  },
  get usesRealpathCacheKey() {
    return realpathCacheKey !== null;
  },
};
