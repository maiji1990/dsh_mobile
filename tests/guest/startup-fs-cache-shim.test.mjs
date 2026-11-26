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
  symlinkSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { delimiter, dirname, join, resolve, sep } from "node:path";
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
  for (const preload of options.preloads ?? []) {
    args.push("--import", pathToFileURL(preload).href);
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
      assert.equal(state.usesPackageJsonReader, false); // 默认不安装：启动链路全部走 loaderMethods
      assert.equal(state.usesLoaderMethods, true);
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

      // 4b. 严格只读前缀内负结果可缓存；可写前缀绝不缓存负结果。
      const roMissingModule = path.join(roDir, "no-such-module.js");
      const rwMissingModule = path.join(rwDir, "no-such-module.js");
      const negHitsBefore = state.stats.moduleStatHits;
      assert.ok(bindingStat(roMissingModule) < 0);
      assert.ok(bindingStat(roMissingModule) < 0);
      assert.ok(state.stats.moduleStatHits > negHitsBefore, "readonly negative stat must hit cache");
      writeFileSync(roMissingModule, "module.exports = 1;");
      assert.ok(bindingStat(roMissingModule) < 0, "TTL 内严格只读层负结果保持旧结论");
      const rwNegHitsBefore = state.stats.moduleStatHits;
      assert.ok(bindingStat(rwMissingModule) < 0);
      assert.ok(bindingStat(rwMissingModule) < 0);
      assert.equal(state.stats.moduleStatHits, rwNegHitsBefore, "writable prefix must not cache negative stat");
      writeFileSync(rwMissingModule, "module.exports = 1;");
      assert.equal(bindingStat(rwMissingModule), 0);

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
      // POSIX 目标平台上干净绝对路径必须走零分配快路径；Windows 上按设计回退慢路径。
      if (${JSON.stringify(sep)} === "/") {
        assert.ok(state.stats.fastPathHits > 0, "clean absolute paths must use the fast path");
      }
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
      assert.equal(state.usesPackageJsonReader, false);
      assert.equal(state.usesLoaderMethods, false);
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
      const roMissingModule = path.join(roDir, "ttl-missing.js");

      assert.equal(readFileSync(roPkg, "utf8"), '{"name":"ro","v":1}');
      const names = readdirSync(roDir);
      assert.deepEqual(names, ["pkg"]);
      assert.equal(state.usesInternalModuleStat, true);
      assert.equal(state.usesPackageJsonReader, false);
      const bindingStatEarly = process.binding("fs").internalModuleStat;
      assert.ok(bindingStatEarly(roMissingModule) < 0);
      assert.ok(bindingStatEarly(roMissingModule) < 0);

      // TTL 内：底层文件/目录已变，但缓存仍返回旧快照。
      writeFileSync(roPkg, '{"name":"ro","v":2}');
      writeFileSync(path.join(roDir, "late.txt"), "x");
      assert.equal(readFileSync(roPkg, "utf8"), '{"name":"ro","v":1}');
      assert.deepEqual(readdirSync(roDir), ["pkg"]);

      await new Promise((resolve) => setTimeout(resolve, 140));

      assert.equal(state.active, false);
      assert.equal(state.usesInternalModuleStat, false);
      assert.equal(state.usesPackageJsonReader, false);
      assert.equal(state.usesLoaderMethods, false);
      // uninstall 后所有方法恢复原生行为，缓存不再干扰。
      assert.equal(readFileSync(roPkg, "utf8"), '{"name":"ro","v":2}');
      assert.ok(readdirSync(roDir).includes("late.txt"));
      const bindingStat = process.binding("fs").internalModuleStat;
      const hitsBefore = state.stats.moduleStatHits;
      assert.equal(typeof bindingStat, "function");
      assert.equal(bindingStat(roPkg), 0);
      assert.equal(bindingStat(roPkg), 0);
      assert.equal(state.stats.moduleStatHits, hitsBefore);
      // 负缓存也随 uninstall 清空：新建文件后必须立刻可见。
      writeFileSync(roMissingModule, "module.exports = 1;");
      assert.equal(bindingStat(roMissingModule), 0);
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

test("readonly .json/.yml manifests are cached only under the strict readonly prefix", () => {
  const { root, roDir, rwDir } = makeDirs();
  writeFileSync(join(roDir, "cordis.yml"), "plugins: 1\n");
  writeFileSync(join(rwDir, "cordis.yml"), "plugins: 1\n");
  writeFileSync(join(roDir, "cordis.json"), '{"plugins":1}');
  try {
    const script = `
      import assert from "node:assert/strict";
      import { readFileSync, writeFileSync } from "node:fs";
      import path from "node:path";
      const state = globalThis[Symbol.for("dshmobile.startupFsCache.state")];
      const roYml = ${JSON.stringify(join(roDir, "cordis.yml"))};
      const rwYml = ${JSON.stringify(join(rwDir, "cordis.yml"))};
      const roJson = ${JSON.stringify(join(roDir, "cordis.json"))};

      // 严格只读前缀：正结果缓存，磁盘变化在 TTL 内不可见。
      assert.equal(readFileSync(roYml, "utf8"), "plugins: 1\\n");
      writeFileSync(roYml, "plugins: 2\\n");
      assert.equal(readFileSync(roYml, "utf8"), "plugins: 1\\n");
      assert.ok(state.stats.manifestHits >= 1, "readonly yml must hit cache");
      const roBuf = readFileSync(roYml);
      assert.equal(Buffer.isBuffer(roBuf), true);
      roBuf[0] = 0x58;
      assert.equal(readFileSync(roYml, "utf8"), "plugins: 1\\n");

      // 非 package.json 的 .json 同属只读清单。
      assert.equal(readFileSync(roJson, "utf8"), '{"plugins":1}');
      writeFileSync(roJson, '{"plugins":2}');
      assert.equal(readFileSync(roJson, "utf8"), '{"plugins":1}');

      // 可写前缀保持透传：修改后立即读到新内容。
      assert.equal(readFileSync(rwYml, "utf8"), "plugins: 1\\n");
      writeFileSync(rwYml, "plugins: 2\\n");
      assert.equal(readFileSync(rwYml, "utf8"), "plugins: 2\\n");

      state.uninstall();
      assert.equal(readFileSync(roYml, "utf8"), "plugins: 2\\n");
      console.log("MANIFEST_OK");
    `;
    const env = {
      DSHM_FS_CACHE_PREFIXES: [roDir, rwDir].join(delimiter),
      DSHM_FS_CACHE_NONEXIST_PREFIXES: roDir,
      DSHM_FS_CACHE_TTL_MS: "0",
    };
    assert.equal(runWithShim(script, env), "MANIFEST_OK");
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("package_json_reader.read is cached only for the strict readonly prefix and fully restored", () => {
  const { root, roDir, rwDir } = makeDirs();
  try {
    const script = `
      import assert from "node:assert/strict";
      import { createRequire } from "node:module";
      const state = globalThis[Symbol.for("dshmobile.startupFsCache.state")];
      const require = createRequire(import.meta.url);
      const reader = require("internal/modules/package_json_reader");
      const roPkg = ${JSON.stringify(join(roDir, "pkg", "package.json"))};
      const rwPkg = ${JSON.stringify(join(rwDir, "pkg", "package.json"))};

      assert.equal(state.usesPackageJsonReader, true);
      const first = reader.read(roPkg);
      assert.equal(first.exists, true);
      assert.equal(first.name, "ro");
      const hitsBefore = state.stats.packageJsonReaderHits;
      const second = reader.read(roPkg);
      assert.equal(second.name, "ro");
      assert.notEqual(second, first, "each hit must return an independent top-level object");
      assert.ok(state.stats.packageJsonReaderHits > hitsBefore, "readonly package.json must hit cache");

      // 可写前缀不进入本层缓存：计数不增长。
      const rwHitsBefore = state.stats.packageJsonReaderHits;
      reader.read(rwPkg);
      reader.read(rwPkg);
      assert.equal(state.stats.packageJsonReaderHits, rwHitsBefore, "writable prefix must not be cached");

      // 包装可完整还原：uninstall 后 read 不再是包装函数。
      const wrapped = reader.read;
      state.uninstall();
      assert.equal(state.active, false);
      assert.equal(state.usesPackageJsonReader, false);
      assert.notEqual(reader.read, wrapped);
      assert.equal(reader.read(roPkg).name, "ro");
      console.log("READER_OK");
    `;
    const env = {
      DSHM_FS_CACHE_PREFIXES: [roDir, rwDir].join(delimiter),
      DSHM_FS_CACHE_NONEXIST_PREFIXES: roDir,
      DSHM_FS_CACHE_TTL_MS: "0",
      // 该包装默认关闭（启动链路实测 read 调用为 0），仅此用例显式开启以验证包装语义。
      DSHM_FS_CACHE_PACKAGE_READER: "1",
    };
    assert.equal(runWithShim(script, env), "READER_OK");
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("loaderMethods breaks through esm/get_format early destructuring for CJS and ESM", () => {
  const { root, roDir, rwDir } = makeDirs();
  mkdirSync(join(roDir, "esm"), { recursive: true });
  writeFileSync(join(roDir, "esm", "package.json"), '{"type":"module"}');
  writeFileSync(join(roDir, "esm", "mod.js"), "export const value = 1;");
  try {
    const script = `
      import assert from "node:assert/strict";
      import { createRequire } from "node:module";
      import { pathToFileURL } from "node:url";
      const state = globalThis[Symbol.for("dshmobile.startupFsCache.state")];
      const require = createRequire(import.meta.url);
      const reader = require("internal/modules/package_json_reader");
      const lm = require("internal/modules/helpers").loaderMethods;
      const roPkg = ${JSON.stringify(join(roDir, "pkg", "package.json"))};
      const roEsm = ${JSON.stringify(join(roDir, "esm", "mod.js"))};
      const rwPkg = ${JSON.stringify(join(rwDir, "pkg", "package.json"))};

      assert.equal(state.usesLoaderMethods, true, "shim must wrap loaderMethods");

      // CJS 侧：reader.read 与 reader.getPackageScopeConfig 内部都会回落到 loaderMethods。
      const hits0 = state.stats.loaderMethodsHits;
      const first = reader.read(roPkg);
      assert.equal(first.exists, true);
      const scope1 = reader.getPackageScopeConfig(pathToFileURL(roPkg).href);
      assert.equal(scope1.exists, true);
      reader.read(roPkg);
      reader.getPackageScopeConfig(pathToFileURL(roPkg).href);
      assert.ok(state.stats.loaderMethodsHits > hits0, "readonly package lookups must hit the loaderMethods cache");

      // 可写前缀绝不进缓存。
      const rwHits = state.stats.loaderMethodsHits;
      reader.getPackageScopeConfig(pathToFileURL(rwPkg).href);
      reader.getPackageScopeConfig(pathToFileURL(rwPkg).href);
      assert.equal(state.stats.loaderMethodsHits, rwHits, "writable prefix must not be cached");

      // ESM 真实路径：import 一个 type=module 下的 .js 会触发 getPackageScopeConfig。
      const esmMisses = state.stats.loaderMethodsMisses;
      await import(pathToFileURL(roEsm).href);
      assert.ok(state.stats.loaderMethodsMisses > esmMisses, "real ESM import must reach the loaderMethods wrapper");

      // uninstall 完整还原：包装函数被替换回原生函数。
      const wrapped = lm.getPackageScopeConfig;
      state.uninstall();
      assert.equal(state.usesLoaderMethods, false);
      assert.notEqual(lm.getPackageScopeConfig, wrapped);
      assert.equal(lm.readPackageJSON !== undefined, true);
      assert.equal(reader.getPackageScopeConfig(pathToFileURL(roPkg).href).exists, true);
      console.log("LOADER_METHODS_OK");
    `;
    const env = {
      DSHM_FS_CACHE_PREFIXES: [roDir, rwDir].join(delimiter),
      DSHM_FS_CACHE_NONEXIST_PREFIXES: roDir,
      DSHM_FS_CACHE_TTL_MS: "0",
    };
    assert.equal(runWithShim(script, env), "LOADER_METHODS_OK");
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("loaderMethods installation rolls back when an export is non-writable", () => {
  const root = mkdtempSync(join(tmpdir(), "dshm-loader-rollback-"));
  const sabotage = join(root, "sabotage.mjs");
  const pkg = join(root, "package.json");
  writeFileSync(pkg, '{"name":"rollback"}');
  writeFileSync(
    sabotage,
    `import { createRequire } from "node:module";
const require = createRequire(import.meta.url);
const lm = require("internal/modules/helpers").loaderMethods;
Object.defineProperty(lm, "readPackageJSON", { value: lm.readPackageJSON, writable: false, configurable: false });
`,
  );
  try {
    const script = `
      import assert from "node:assert/strict";
      import { createRequire } from "node:module";
      const state = globalThis[Symbol.for("dshmobile.startupFsCache.state")];
      const require = createRequire(import.meta.url);
      const lm = require("internal/modules/helpers").loaderMethods;
      const descriptor = Object.getOwnPropertyDescriptor(lm, "readPackageJSON");
      // 任一项不可写时必须整体放弃安装，且不抛错、不影响 package_json_reader 包装。
      assert.equal(state.usesLoaderMethods, false);
      assert.equal(descriptor.writable, false);
      assert.equal(state.usesPackageJsonReader, false);
      require("internal/modules/package_json_reader").read(${JSON.stringify(pkg)});
      console.log("ROLLBACK_OK");
    `;
    assert.equal(runWithShim(script, {}, { preloads: [sabotage] }), "ROLLBACK_OK");
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("symlinked manifests and symlinked module paths are never cached", () => {
  const { root, roDir } = makeDirs();
  const target = join(roDir, "pkg", "package.json");
  const link = join(roDir, "linked.json");
  let linked = false;
  try {
    symlinkSync(target, link, "file");
    linked = true;
  } catch {
    /* Windows 无符号链接权限时跳过符号链接断言。 */
  }
  try {
    const script = `
      import assert from "node:assert/strict";
      import { readFileSync, writeFileSync, lstatSync } from "node:fs";
      const state = globalThis[Symbol.for("dshmobile.startupFsCache.state")];
      const roDir = ${JSON.stringify(roDir)};
      const link = ${JSON.stringify(link)};
      const target = ${JSON.stringify(target)};
      const linked = ${JSON.stringify(linked)};
      if (!linked) { console.log("SYMLINK_SKIPPED"); } else {
        assert.equal(lstatSync(link).isSymbolicLink(), true);
        assert.equal(readFileSync(link, "utf8"), '{"name":"ro","v":1}');
        writeFileSync(target, '{"name":"ro","v":3}');
        assert.equal(readFileSync(link, "utf8"), '{"name":"ro","v":3}', "symlinked manifest must bypass the cache");
        assert.equal(state.stats.manifestHits, 0);
        console.log("SYMLINK_OK");
      }
    `;
    const out = runWithShim(script, {
      DSHM_FS_CACHE_PREFIXES: roDir,
      DSHM_FS_CACHE_NONEXIST_PREFIXES: roDir,
      DSHM_FS_CACHE_TTL_MS: "0",
    });
    assert.ok(out === "SYMLINK_OK" || out === "SYMLINK_SKIPPED");
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("clean absolute paths outside the allowlist bypass without path.resolve or caching", () => {
  const script = `
    import assert from "node:assert/strict";
    import { existsSync } from "node:fs";
    const state = globalThis[Symbol.for("dshmobile.startupFsCache.state")];
    const before = state.stats.bypasses;
    // 干净绝对路径但不在白名单：两次调用都必须透传且不入缓存。
    assert.equal(existsSync("/root/projects/no-such-probe.js"), false);
    assert.equal(existsSync("/root/projects/no-such-probe.js"), false);
    assert.equal(state.stats.bypasses, before + 2, "non-allowlisted absolute path must bypass twice");
    console.log("NONALLOWLIST_BYPASS_OK");
  `;
  const env = {
    DSHM_FS_CACHE_PREFIXES: "/opt/dshapp/runtime",
    DSHM_FS_CACHE_NONEXIST_PREFIXES: "/opt/dshapp/runtime",
    DSHM_FS_CACHE_TTL_MS: "0",
  };
  assert.equal(runWithShim(script, env), "NONALLOWLIST_BYPASS_OK");
});

test("file:/// URL and plain path share the same readFileSync cache key", () => {
  const { root, roDir } = makeDirs();
  try {
    const script = `
      import assert from "node:assert/strict";
      import { readFileSync } from "node:fs";
      import { pathToFileURL } from "node:url";
      const state = globalThis[Symbol.for("dshmobile.startupFsCache.state")];
      const roPkg = ${JSON.stringify(join(roDir, "pkg", "package.json"))};
      assert.equal(readFileSync(pathToFileURL(roPkg), "utf8"), '{"name":"ro","v":1}');
      const hits = state.stats.hits;
      assert.equal(readFileSync(roPkg, "utf8"), '{"name":"ro","v":1}');
      assert.ok(state.stats.hits > hits, "file:// URL and plain path must share the cache");
      console.log("FILE_URL_OK");
    `;
    const env = {
      DSHM_FS_CACHE_PREFIXES: roDir,
      DSHM_FS_CACHE_NONEXIST_PREFIXES: roDir,
      DSHM_FS_CACHE_TTL_MS: "0",
    };
    assert.equal(runWithShim(script, env), "FILE_URL_OK");
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("accessSync and fs.promises cache readonly lookups and restore on uninstall", () => {
  const { root, roDir, rwDir } = makeDirs();
  try {
    const script = `
      import assert from "node:assert/strict";
      import fs, { accessSync, constants, readFileSync, writeFileSync, rmSync } from "node:fs";
      import path from "node:path";
      const state = globalThis[Symbol.for("dshmobile.startupFsCache.state")];
      const roDir = ${JSON.stringify(roDir)};
      const rwDir = ${JSON.stringify(rwDir)};
      const roPkg = path.join(roDir, "pkg", "package.json");
      const rwFile = path.join(rwDir, "w.txt");

      // accessSync：正结果可缓存；只读前缀内缺失先探测后创建仍按缓存返回 ENOENT。
      accessSync(roPkg);
      accessSync(roPkg, constants.R_OK);
      const missing = path.join(roDir, "late-access.txt");
      assert.throws(() => accessSync(missing), (e) => e.code === "ENOENT");
      writeFileSync(missing, "x");
      assert.throws(() => accessSync(missing), (e) => e.code === "ENOENT");

      // W_OK 必须透传：删除后立即报 ENOENT，不被缓存。
      writeFileSync(rwFile, "x");
      accessSync(rwFile, constants.W_OK);
      rmSync(rwFile);
      assert.throws(() => accessSync(rwFile, constants.W_OK), (e) => e.code === "ENOENT");

      // fs.promises：同类缓存命中并返回独立副本。
      await fs.promises.access(roPkg);
      await fs.promises.stat(roPkg);
      await fs.promises.lstat(roPkg);
      await fs.promises.realpath(roPkg);
      await fs.promises.readdir(roDir);
      assert.equal(await fs.promises.readFile(roPkg, "utf8"), '{"name":"ro","v":1}');
      writeFileSync(roPkg, '{"name":"ro","v":9}');
      assert.equal(await fs.promises.readFile(roPkg, "utf8"), '{"name":"ro","v":1}');
      assert.equal(await fs.promises.access(roPkg).then(() => true), true);

      // uninstall 必须恢复所有 promises 原生引用。
      const wrappedStat = fs.promises.stat;
      const wrappedAccess = fs.promises.access;
      state.uninstall();
      assert.notEqual(fs.promises.stat, wrappedStat);
      assert.notEqual(fs.promises.access, wrappedAccess);
      assert.ok(fs.promises.stat(roPkg) instanceof Promise);
      console.log("PROMISES_OK");
    `;
    const env = {
      DSHM_FS_CACHE_PREFIXES: [roDir, rwDir].join(delimiter),
      DSHM_FS_CACHE_NONEXIST_PREFIXES: roDir,
      DSHM_FS_CACHE_TTL_MS: "0",
    };
    assert.equal(runWithShim(script, env), "PROMISES_OK");
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
