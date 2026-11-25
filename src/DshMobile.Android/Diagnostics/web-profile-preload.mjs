// Web-profile diagnostic preload (docs/WEB_STARTUP_DIAGNOSIS.md).
// Diagnostics only: packaged solely with -p:DshDiagnostics=true, loaded with a fixed --import before the DSH entry.
// It opens an in-process inspector.Session (no port), samples V8 CPU from before DSH evaluates, and stops when the host
// writes stop.request into the isolated cache directory. Only numbers and fixed error codes are written back.
import { existsSync, mkdirSync, renameSync, statSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { Session } from 'node:inspector';
import * as nodeModule from 'node:module';
import { performance } from 'node:perf_hooks';

const dir = process.env.DSHM_PROFILE_DIR;
const mode = process.env.DSHM_PROFILE_MODE === 'profile' ? 'profile' : 'off';
const shouldFlushCompileCacheOnStop = process.env.DSHM_COMPILE_CACHE_FLUSH_ON_STOP === '1';
if (!dir) {
  // No diagnostic directory: stay completely passive.
} else {
  mkdirSync(dir, { recursive: true });
}

const wallStart = performance.now();
const cpuStart = process.cpuUsage();
const eluStart = performance.eventLoopUtilization();
let session = null;
let sampling = false;
let startupError = null;

function writeAtomic(name, text) {
  const tmp = join(dir, name + '.tmp');
  const final = join(dir, name);
  writeFileSync(tmp, text);
  renameSync(tmp, final);
}

function call(method) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('INSPECTOR_TIMEOUT')), 30000);
    session.post(method, (error, result) => {
      clearTimeout(timer);
      if (error) {
        reject(error);
      } else {
        resolve(result);
      }
    });
  });
}

if (dir && mode === 'profile') {
  try {
    session = new Session();
    session.connect();
    await call('Profiler.enable');
    await call('Profiler.start');
    sampling = true;
    writeAtomic('profile.started', 'ok');
  } catch {
    startupError = 'PROFILE_UNAVAILABLE';
    try {
      session?.disconnect();
    } catch {
      // ignore
    }
    session = null;
    sampling = false;
  }
}

/**
 * 冷启动轮在 HTTP 就绪后由 Host 写 stop.request；这里在 stop 阶段主动把 V8 compile cache 同步落盘。
 * 只写固定布尔与错误码 token，绝不写缓存路径、模块名或用户数据。
 */
function flushCompileCacheOnStop() {
  if (!dir || !shouldFlushCompileCacheOnStop) return null;
  const result = {
    schema: 1,
    supported: typeof nodeModule.flushCompileCache === 'function',
    flushed: false,
    error: null,
  };
  try {
    if (!result.supported) {
      result.error = 'COMPILE_CACHE_FLUSH_UNSUPPORTED';
    } else {
      nodeModule.flushCompileCache();
      result.flushed = true;
    }
  } catch (err) {
    const code = err && typeof err.code === 'string' ? err.code : '';
    result.error = /^[A-Za-z0-9._-]{1,64}$/.test(code) ? code : 'COMPILE_CACHE_FLUSH_FAILED';
  }
  try {
    writeAtomic('compile-cache.flush.json', JSON.stringify(result));
  } catch {
    // 落盘摘要写不出去时 Host 会按缺失处理，不会把本次冷启动算作成功。
  }
  return result;
}

let stopping = false;
async function stop() {
  if (stopping) {
    return;
  }
  stopping = true;
  const wallMs = performance.now() - wallStart;
  const cpu = process.cpuUsage(cpuStart);
  const elu = performance.eventLoopUtilization(eluStart);
  let samples = null;
  let error = startupError;
  let stopLatencyMs = null;
  try {
    const request = join(dir, 'stop.request');
    if (existsSync(request)) {
      stopLatencyMs = Math.max(0, Date.now() - statSync(request).mtimeMs);
    }
  } catch {
    stopLatencyMs = null;
  }
  if (sampling) {
    try {
      const result = await call('Profiler.stop');
      const profile = result?.profile;
      if (!profile || !Array.isArray(profile.samples) || profile.samples.length === 0) {
        error = 'PROFILE_EMPTY';
      } else {
        samples = profile.samples.length;
        writeAtomic('profile.cpuprofile', JSON.stringify(profile));
      }
    } catch {
      error = 'PROFILE_WRITE_FAILED';
    }
  }
  // 冷启动轮：在采样停止后、写 done 之前主动 flush，确保 Host 看到 done 时缓存文件已经落盘。
  flushCompileCacheOnStop();
  const summary = {
    schema: 1,
    mode,
    wallMs: Math.round(wallMs * 10) / 10,
    nodeUserCpuMs: Math.round((cpu.user / 1000) * 10) / 10,
    nodeSystemCpuMs: Math.round((cpu.system / 1000) * 10) / 10,
    eventLoopActiveMs: Math.round(elu.active * 10) / 10,
    eventLoopIdleMs: Math.round(elu.idle * 10) / 10,
    profileSampleCount: samples,
    stopLatencyMs,
    error,
  };
  writeAtomic('summary.json', JSON.stringify(summary));
  writeAtomic('profile.done', 'ok');
}

if (dir) {
  const timer = setInterval(() => {
    if (existsSync(join(dir, 'stop.request'))) {
      clearInterval(timer);
      void stop();
    }
  }, 100);
  timer.unref?.();
}
