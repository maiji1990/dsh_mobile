// 启动期只读运行层缓存垫片单元测试：白名单缓存、readdir 快照隔离、严格只读负缓存边界、
// 内部模块 stat 包装/还原、TTL 自动卸载、无 --expose-internals 安全加载，以及 NODE_COMPILE_CACHE 主动落盘。
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import {
  mkdirSync,
  mkdtempSync,
  readdirSync,
  rmSync,
  statSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { delimiter, dirname, join, resolve } from "node:path";
import { test } from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
const shimPath = join(
  repoRoot,
  "src",
  "DshMobile.Runtime",
  "Guest",
  "Assets",
  "startup-fs-cache-shim.mjs",
);
const shimUrl = pathToFileURL(shimPath).href;

function spawnShim(script, env, options = {}) {
  const exposeInternals = options.exposeInternals !== false;
  const args = [];
  if (exposeInternals) {
    args.push("--expose-internals");
  }
  args.push("--import", shimUrl, "--input-type=module", "-e", script);
  return spawnSync(process.execPath, args, {
    encoding: "utf8",
    env: { ...process.env, ...env },
    timeout: options.timeout ?? 20_000,
  });
}

function runWithShim(script, env, options = {}) {
  const res = spawnShim(script, env, options);
  assert.equal(
    res.status,
    0,
    `child exited with ${res.status} (signal ${res.signal})\nstdout:\n${res.stdout}\nstderr:\n${res.stderr}`,
  );
  return res.stdout.trim();
}

/** 递归列出目录下的普通文件，用于断言编译缓存确实落盘且非空。 */
function listFilesRecursive(dir) {
  const out = [];
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) {
      out.push(...listFilesRecursive(full));
    } else {
      out.push(full);
    }
  }
  return out;
}

function makeDirs() {
  const root = mkdtempSync(join(tmpdir(), "dshm-fscache-"));
  const roDir = join(root, "readonly-runtime");
  const rwDir = join(root, "writable-layer");
  const wsDir = join(root, "user-workspace");
  mkdirSync(join(roDir, "pkg"), { recursive: true });
  mkdirSync(join(rwDir, "pkg"), { recursive: true });
  mkdirSync(join(wsDir, "pkg"), { recursive: true });
  writeFileSync(join(roDir, "pkg", "package.json"), '{"name":"ro","v":1}');
  writeFileSync(join(roDir, "pkg", "index.js"), "export const v = 1;");
  writeFileSync(join(rwDir, "pkg", "package.json"), '{"name":"rw","v":1}');
  writeFileSync(join(wsDir, "pkg", "package.json"), '{"name":"ws","v":1}');
  return { root, roDir, rwDir, wsDir };
}

test("readonly metadata, readdir snapshots and internalModuleStat are cached; writable prefixes are not negative-cached", () => {
  const { root, roDir, rwDir, wsDir } = makeDirs();
  try {
    const script = `
      import assert from "node:assert/strict";
      import fs, { realpathSync, existsSync, statSync, lstatSync, readFileSync, readdirSync, writeFileSync, mkdirSync } from "node:fs";
      import path from "node:path";

      const state = globalThis[Symbol.for("dshmobile.startupFsCache.state")];
      const roDir = ${JSON.stringify(roDir)};
      const rwDir = ${JSON.stringify(rwDir)};
      const wsDir = ${JSON.stringify(wsDir)};
      const roPkg = path.join(roDir, "pkg", "package.json");
      const rwPkg = path.join(rwDir, "pkg", "package.json");
      const wsPkg = path.join(wsDir, "pkg", "package.json");
      const escapePath = path.join(roDir, "..", "user-workspace", "pkg", "package.json");

      assert.equal(state.active, true);
      assert.equal(state.usesRealpathCacheKey, true);
      assert.equal(state.usesInternalModuleStat, true);
      // 负缓存前缀只保留严格只读目录：误配的 /usr/local 与 /tmp 会被过滤。
      assert.deepEqual(state.negativePrefixes, [path.resolve(roDir)]);

      // 1. realpath / exists / stat / lstat 命中缓存，路径穿越透传。
      const rp1 = realpathSync(roPkg);
      const rp2 = fs.realpathSync.native(roPkg);
      assert.equal(rp1, rp2);
      assert.equal(Buffer.isBuffer(realpathSync(roPkg, "buffer")), true);
      assert.equal(existsSync(roPkg), true);
      const st1 = statSync(roPkg);
      const st2 = statSync(roPkg);
      assert.equal(st1, st2);
      assert.equal(lstatSync(roPkg), lstatSync(roPkg));
      assert.equal(readFileSync(escapePath, "utf8"), '{"name":"ws","v":1}');

      // 2. readdirSync 三种常见形态：默认 / encoding=buffer / withFileTypes，命中均返回独立快照。
      const names1 = readdirSync(roDir);
      assert.deepEqual(names1, ["pkg"]);
      names1.push("injected");
      names1[0] = "mutated";
      assert.deepEqual(readdirSync(roDir), ["pkg"]);

      const bufs = readdirSync(roDir, { encoding: "buffer" });
      assert.equal(Buffer.isBuffer(bufs[0]), true);
      const firstName = bufs[0].toString("utf8");
      bufs[0][0] = 0x58;
      assert.equal(readdirSync(roDir, { encoding: "buffer" })[0].toString("utf8"), firstName);

      const dirents = readdirSync(roDir, { withFileTypes: true });
      assert.equal(dirents.length, 1);
      assert.equal(dirents[0].name, "pkg");
      assert.equal(dirents[0].isDirectory(), true);
      dirents[0].name = "mutated";
      dirents.push(dirents[0]);
      const again = readdirSync(roDir, { withFileTypes: true });
      assert.equal(again.length, 1);
      assert.equal(again[0].name, "pkg");
      assert.equal(again[0].isDirectory(), true);

      // 3. package.json 读取缓存与 Buffer 隔离。
      assert.equal(readFileSync(roPkg, "utf8"), '{"name":"ro","v":1}');
      const buf1 = readFileSync(roPkg);
      buf1[0] = 0x58;
      assert.equal(readFileSync(roPkg, "utf8"), '{"name":"ro","v":1}');

      // 4. 内部模块 stat：0=文件 / 1=目录，第二次命中。
      const bindingStat = process.binding("fs").internalModuleStat;
      const hitsBefore = state.stats.moduleStatHits;
      assert.equal(bindingStat(roPkg), 0);
      assert.equal(bindingStat(roPkg), 0);
      assert.equal(bindingStat(roDir), 1);
      assert.equal(bindingStat(roDir), 1);
      assert.ok(state.stats.moduleStatHits >= 2);
      assert.ok(state.stats.moduleStatHits > hitsBefore);

      // 5. 可写前缀：先缺失后创建必须立即可见（不缓存 ENOENT）。
      const rwMissing = path.join(rwDir, "created-later.txt");
      assert.equal(existsSync(rwMissing), false);
      writeFileSync(rwMissing, "x");
      assert.equal(existsSync(rwMissing), true);
      const rwMissingDir = path.join(rwDir, "created-dir");
      assert.throws(() => readdirSync(rwMissingDir), (err) => err.code === "ENOENT");
      mkdirSync(rwMissingDir);
      assert.deepEqual(readdirSync(rwMissingDir), []);

      // 6. 严格只读前缀：ENOENT/ENOTDIR 允许负缓存，TTL 内保持旧结论。
      const roMissing = path.join(roDir, "later.txt");
      assert.equal(existsSync(roMissing), false);
      writeFileSync(roMissing, "x");
      assert.equal(existsSync(roMissing), false);
      const roMissingDir = path.join(roDir, "later-dir");
      assert.throws(() => readdirSync(roMissingDir), (err) => err.code === "ENOENT");
      mkdirSync(roMissingDir);
      assert.throws(() => readdirSync(roMissingDir), (err) => err.code === "ENOENT");

      // 7. 工作区/非白名单路径永不被缓存：修改后立即读回。
      assert.equal(existsSync(wsPkg), true);
      assert.equal(readFileSync(wsPkg, "utf8"), '{"name":"ws","v":1}');
      writeFileSync(path.join(wsDir, "late.txt"), "x");
      assert.ok(readdirSync(wsDir).includes("late.txt"));
      assert.ok(state.stats.bypasses > 0);

      assert.ok(state.stats.hits >= 8, "expected cache hits, got " + state.stats.hits);
      console.log("OK");
    `;
    const env = {
      // 工作区不在白名单内：所有访问都必须透传（bypass），绝不缓存。
      DSHM_FS_CACHE_PREFIXES: [roDir, rwDir].join(delimiter),
      // 故意把可写前缀与 /tmp 混进负缓存配置：只应保留严格只读的 roDir。
      DSHM_FS_CACHE_NONEXIST_PREFIXES: [roDir, "/usr/local", "/tmp"].join(delimiter),
      DSHM_FS_CACHE_TTL_MS: "0",
    };
    assert.equal(runWithShim(script, env), "OK");
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("default negative-cache prefixes contain only the strict read-only runtime layer", () => {
  const script = `
    import assert from "node:assert/strict";
    import path from "node:path";
    const state = globalThis[Symbol.for("dshmobile.startupFsCache.state")];
    assert.deepEqual(state.negativePrefixes, [path.resolve("/opt/dshapp/runtime")]);
    for (const forbidden of ["/usr/local", "/root/projects", "/tmp", "/proc", "/dev"]) {
      assert.ok(!state.negativePrefixes.includes(path.resolve(forbidden)));
    }
    console.log("DEFAULTS");
  `;
  assert.equal(runWithShim(script, {}), "DEFAULTS");
});

test("works without --expose-internals: readdir/moduleStat stay cached, realpath cache key degrades", () => {
  const { root, roDir } = makeDirs();
  try {
    const script = `
      import assert from "node:assert/strict";
      import { existsSync, readdirSync } from "node:fs";
      import path from "node:path";
      const state = globalThis[Symbol.for("dshmobile.startupFsCache.state")];
      const roDir = ${JSON.stringify(roDir)};
      const roPkg = path.join(roDir, "pkg", "package.json");
      assert.equal(state.active, true);
      assert.equal(state.usesRealpathCacheKey, false);
      assert.equal(state.usesInternalModuleStat, true);
      assert.equal(existsSync(roPkg), true);
      assert.deepEqual(readdirSync(roDir), ["pkg"]);
      assert.deepEqual(readdirSync(roDir), ["pkg"]);
      assert.equal(process.binding("fs").internalModuleStat(roPkg), 0);
      console.log("NO_EXPOSE");
    `;
    assert.equal(
      runWithShim(
        script,
        { DSHM_FS_CACHE_PREFIXES: roDir, DSHM_FS_CACHE_NONEXIST_PREFIXES: roDir, DSHM_FS_CACHE_TTL_MS: "0" },
        { exposeInternals: false },
      ),
      "NO_EXPOSE",
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("TTL uninstall restores every fs method and the internalModuleStat binding, and clears caches", () => {
  const { root, roDir } = makeDirs();
  try {
    const script = `
      import assert from "node:assert/strict";
      import { readFileSync, readdirSync, writeFileSync } from "node:fs";
      import path from "node:path";
      const state = globalThis[Symbol.for("dshmobile.startupFsCache.state")];
      const roDir = ${JSON.stringify(roDir)};
      const roPkg = path.join(roDir, "pkg", "package.json");

      assert.equal(readFileSync(roPkg, "utf8"), '{"name":"ro","v":1}');
      const names = readdirSync(roDir);
      assert.deepEqual(names, ["pkg"]);
      assert.equal(state.usesInternalModuleStat, true);

      // TTL 内：底层文件/目录已变，但缓存仍返回旧快照。
      writeFileSync(roPkg, '{"name":"ro","v":2}');
      writeFileSync(path.join(roDir, "late.txt"), "x");
      assert.equal(readFileSync(roPkg, "utf8"), '{"name":"ro","v":1}');
      assert.deepEqual(readdirSync(roDir), ["pkg"]);

      await new Promise((resolve) => setTimeout(resolve, 140));

      assert.equal(state.active, false);
      assert.equal(state.usesInternalModuleStat, false);
      // uninstall 后所有方法恢复原生行为，缓存不再干扰。
      assert.equal(readFileSync(roPkg, "utf8"), '{"name":"ro","v":2}');
      assert.ok(readdirSync(roDir).includes("late.txt"));
      const bindingStat = process.binding("fs").internalModuleStat;
      const hitsBefore = state.stats.moduleStatHits;
      assert.equal(typeof bindingStat, "function");
      assert.equal(bindingStat(roPkg), 0);
      assert.equal(bindingStat(roPkg), 0);
      assert.equal(state.stats.moduleStatHits, hitsBefore);
      console.log("UNINSTALLED");
    `;
    const env = {
      DSHM_FS_CACHE_PREFIXES: roDir,
      DSHM_FS_CACHE_NONEXIST_PREFIXES: roDir,
      DSHM_FS_CACHE_TTL_MS: "40",
    };
    assert.equal(runWithShim(script, env), "UNINSTALLED");
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("NODE_COMPILE_CACHE flush writes non-empty cache files before the process exits", () => {
  const root = mkdtempSync(join(tmpdir(), "dshm-compile-cache-"));
  const cache = join(root, "cache");
  const dep = join(root, "dep.cjs");
  mkdirSync(cache, { recursive: true });
  writeFileSync(dep, "module.exports = { value: 7 };\n");
  try {
    const script = `
      import assert from "node:assert/strict";
      import { createRequire } from "node:module";
      import * as nodeModule from "node:module";
      import { readdirSync, statSync } from "node:fs";
      import { join } from "node:path";
      const state = globalThis[Symbol.for("dshmobile.startupFsCache.state")];
      const require = createRequire(import.meta.url);
      require(${JSON.stringify(dep)});
      // 等待垫片的主动落盘定时器（50 ms）触发；进程此时仍在运行。
      await new Promise((resolve) => setTimeout(resolve, 400));
      assert.equal(state.compileCacheSupported, true);
      assert.ok(state.compileCacheFlushes >= 1, "shim timer must flush before exit");
      const cacheDir = nodeModule.getCompileCacheDir();
      assert.ok(cacheDir, "NODE_COMPILE_CACHE must produce a versioned directory");
      const files = readdirSync(cacheDir);
      assert.ok(files.length > 0, "cache must contain at least one file before exit");
      const total = files.reduce((sum, name) => sum + statSync(join(cacheDir, name)).size, 0);
      assert.ok(total > 0, "cache files must be non-empty");
      console.log("FLUSHED:" + files.length + ":" + total);
    `;
    const out = runWithShim(script, {
      NODE_COMPILE_CACHE: cache,
      DSHM_COMPILE_CACHE_FLUSH_MS: "50",
      DSHM_FS_CACHE_TTL_MS: "0",
    });
    assert.match(out, /^FLUSHED:\d+:[1-9]\d*$/);
    // 进程退出后再次从磁盘确认：缓存文件仍在且非空。
    const persisted = listFilesRecursive(cache);
    assert.ok(persisted.length > 0, "cache directory must survive process exit");
    assert.ok(persisted.some((file) => statSync(file).size > 0), "persisted cache files must be non-empty");
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("uninstall() flushes the compile cache immediately when the delayed timer is disabled", () => {
  const root = mkdtempSync(join(tmpdir(), "dshm-compile-cache-uninstall-"));
  const cache = join(root, "cache");
  const dep = join(root, "dep.cjs");
  mkdirSync(cache, { recursive: true });
  writeFileSync(dep, "module.exports = { value: 11 };\n");
  try {
    const script = `
      import assert from "node:assert/strict";
      import * as nodeModule from "node:module";
      import { createRequire } from "node:module";
      import { readdirSync, statSync } from "node:fs";
      import { join } from "node:path";
      const state = globalThis[Symbol.for("dshmobile.startupFsCache.state")];
      const require = createRequire(import.meta.url);
      require(${JSON.stringify(dep)});
      assert.equal(state.compileCacheFlushes, 0, "delayed timer is disabled");
      state.uninstall();
      assert.equal(state.active, false);
      assert.ok(state.compileCacheFlushes >= 1, "uninstall must flush immediately");
      const cacheDir = nodeModule.getCompileCacheDir();
      const files = readdirSync(cacheDir);
      assert.ok(files.length > 0, "uninstall flush must write cache files");
      assert.ok(files.some((name) => statSync(join(cacheDir, name)).size > 0), "uninstall flush must write non-empty files");
      console.log("UNINSTALL_FLUSHED");
    `;
    const out = runWithShim(script, {
      NODE_COMPILE_CACHE: cache,
      // 0 关闭延迟定时器：本用例只验证 uninstall() 自身的落盘路径。
      DSHM_COMPILE_CACHE_FLUSH_MS: "0",
      DSHM_FS_CACHE_TTL_MS: "0",
    });
    assert.equal(out, "UNINSTALL_FLUSHED");
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("compile-cache flush timer is unref'ed and does not delay normal exit", () => {
  const root = mkdtempSync(join(tmpdir(), "dshm-compile-cache-unref-"));
  const cache = join(root, "cache");
  const dep = join(root, "dep.cjs");
  mkdirSync(cache, { recursive: true });
  writeFileSync(dep, "module.exports = { value: 8 };\n");
  try {
    const started = Date.now();
    const out = runWithShim(
      `
      import { createRequire } from "node:module";
      const require = createRequire(import.meta.url);
      require(${JSON.stringify(dep)});
      console.log("EXIT_NOW");
      `,
      {
        NODE_COMPILE_CACHE: cache,
        // 60 秒后才落盘；若定时器未 unref，进程会被拖住直到超时。
        DSHM_COMPILE_CACHE_FLUSH_MS: "60000",
        DSHM_FS_CACHE_TTL_MS: "0",
      },
      { timeout: 15_000 },
    );
    const elapsed = Date.now() - started;
    assert.equal(out, "EXIT_NOW");
    assert.ok(elapsed < 10_000, `unref'ed timer must not keep Node alive (elapsed ${elapsed} ms)`);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("SIGTERM flushes the compile cache without swallowing an existing DSH handler", () => {
  const root = mkdtempSync(join(tmpdir(), "dshm-compile-cache-sigterm-"));
  const cache = join(root, "cache");
  const dep = join(root, "dep.cjs");
  const marker = join(root, "handled.txt");
  mkdirSync(cache, { recursive: true });
  writeFileSync(dep, "module.exports = { value: 9 };\n");
  try {
    const script = `
      import assert from "node:assert/strict";
      import fs from "node:fs";
      import { createRequire } from "node:module";
      const state = globalThis[Symbol.for("dshmobile.startupFsCache.state")];
      const require = createRequire(import.meta.url);
      require(${JSON.stringify(dep)});
      const marker = ${JSON.stringify(marker)};
      let dshHandled = false;
      // 模拟 DSH 自己注册的 SIGTERM 停机清理：垫片必须让它照常执行。
      process.on("SIGTERM", () => {
        dshHandled = true;
        fs.writeFileSync(marker, "handled");
      });
      const before = state.compileCacheFlushes;
      process.emit("SIGTERM");
      assert.equal(dshHandled, true, "DSH SIGTERM handler must still run");
      assert.equal(state.compileCacheFlushes, before + 1, "shim must flush before DSH cleanup");
      assert.equal(state.compileCacheSigtermInstalled, true, "shim must not remove itself while another handler exists");
      console.log("STILL_ALIVE");
    `;
    const out = runWithShim(script, {
      NODE_COMPILE_CACHE: cache,
      DSHM_COMPILE_CACHE_FLUSH_MS: "60000",
      DSHM_FS_CACHE_TTL_MS: "0",
    });
    assert.equal(out, "STILL_ALIVE");
    assert.equal(statSync(marker).size > 0, true, "DSH handler must have run");
    assert.ok(listFilesRecursive(cache).some((file) => statSync(file).size > 0));
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("SIGTERM with no other listener keeps Node's default terminate semantics after flushing", () => {
  const root = mkdtempSync(join(tmpdir(), "dshm-compile-cache-sigterm-default-"));
  const cache = join(root, "cache");
  const dep = join(root, "dep.cjs");
  mkdirSync(cache, { recursive: true });
  writeFileSync(dep, "module.exports = { value: 10 };\n");
  try {
    const res = spawnShim(
      `
      import { createRequire } from "node:module";
      const require = createRequire(import.meta.url);
      require(${JSON.stringify(dep)});
      console.log("BEFORE_EMIT");
      process.emit("SIGTERM");
      // 默认终止语义必须保留：这个定时器不应有机会执行。
      setTimeout(() => console.log("AFTER_EMIT"), 200);
      `,
      {
        NODE_COMPILE_CACHE: cache,
        DSHM_COMPILE_CACHE_FLUSH_MS: "60000",
        DSHM_FS_CACHE_TTL_MS: "0",
      },
      { timeout: 15_000 },
    );
    assert.equal(res.error, undefined, "child must terminate on its own, not by timeout");
    assert.notEqual(res.status, 0, "default SIGTERM termination must keep a non-zero exit");
    assert.ok(!res.stdout.includes("AFTER_EMIT"), "process must not continue after default SIGTERM");
    assert.ok(listFilesRecursive(cache).some((file) => statSync(file).size > 0), "cache must be flushed before termination");
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
