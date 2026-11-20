// 启动期只读运行层文件系统元数据缓存垫片单元测试：验证白名单缓存、用户区透传、路径穿越防护、
// 负缓存错误语义、Buffer 隔离、ESM 具名导入同步与 TTL 自动卸载恢复。
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
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

function runWithShim(script, env) {
  const res = spawnSync(
    process.execPath,
    ["--expose-internals", "--import", shimUrl, "--input-type=module", "-e", script],
    {
      encoding: "utf8",
      env: { ...process.env, ...env },
      timeout: 15_000,
    },
  );
  assert.equal(
    res.status,
    0,
    `child exited with ${res.status}\nstdout:\n${res.stdout}\nstderr:\n${res.stderr}`,
  );
  return res.stdout.trim();
}

test("startup-fs-cache-shim caches only whitelisted prefixes and keeps user workspace uncached", () => {
  const root = mkdtempSync(join(tmpdir(), "dshm-fscache-"));
  const roDir = join(root, "readonly-runtime");
  const rwDir = join(root, "user-workspace");
  mkdirSync(join(roDir, "pkg"), { recursive: true });
  mkdirSync(join(rwDir, "pkg"), { recursive: true });
  writeFileSync(join(roDir, "pkg", "package.json"), '{"name":"ro","v":1}');
  writeFileSync(join(roDir, "pkg", "index.js"), "export const v = 1;");
  writeFileSync(join(rwDir, "pkg", "package.json"), '{"name":"rw","v":1}');

  try {
    const script = `
      import assert from "node:assert/strict";
      import fs, { realpathSync, existsSync, statSync, lstatSync, readFileSync } from "node:fs";
      import path from "node:path";

      const roPkg = ${JSON.stringify(join(roDir, "pkg", "package.json"))};
      const roIdx = ${JSON.stringify(join(roDir, "pkg", "index.js"))};
      const roMissing = ${JSON.stringify(join(roDir, "pkg", "missing.js"))};
      const rwPkg = ${JSON.stringify(join(rwDir, "pkg", "package.json"))};
      const escapePath = path.join(${JSON.stringify(roDir)}, "..", "user-workspace", "pkg", "package.json");
      const state = globalThis[Symbol.for("dshmobile.startupFsCache.state")];

      assert.equal(state.active, true);
      assert.equal(state.usesRealpathCacheKey, true);

      // 1. 白名单内 realpathSync / realpathSync.native / existsSync / statSync / lstatSync 命中缓存
      const rp1 = realpathSync(roPkg);
      const rp2 = fs.realpathSync.native(roPkg);
      assert.equal(rp1, rp2);
      assert.equal(Buffer.isBuffer(realpathSync(roPkg, "buffer")), true);
      assert.equal(existsSync(roPkg), true);
      const st1 = statSync(roPkg);
      const st2 = statSync(roPkg);
      assert.equal(st1, st2);
      const lst1 = lstatSync(roPkg);
      const lst2 = lstatSync(roPkg);
      assert.equal(lst1, lst2);

      // 2. 缺失路径负缓存保留 ENOENT 与 throwIfNoEntry:false 语义
      assert.equal(existsSync(roMissing), false);
      assert.equal(statSync(roMissing, { throwIfNoEntry: false }), undefined);
      assert.throws(() => realpathSync(roMissing), (err) => err.code === "ENOENT");
      assert.throws(() => realpathSync(roMissing), (err) => err.code === "ENOENT");

      // 3. 只读层 package.json 缓存且 Buffer 副本隔离，非 package.json 不缓存
      const txt1 = readFileSync(roPkg, "utf8");
      assert.equal(txt1, '{"name":"ro","v":1}');
      const buf1 = readFileSync(roPkg);
      buf1[0] = 0x58; // 污染返回的 Buffer 不得影响缓存内容
      assert.equal(readFileSync(roPkg, "utf8"), '{"name":"ro","v":1}');

      // 模拟底层文件被改写：只读层缓存期间应命中旧值，非 package.json 与用户区立即可见新值
      fs.writeFileSync(roPkg, '{"name":"ro","v":2}');
      fs.writeFileSync(roIdx, "export const v = 2;");
      fs.writeFileSync(rwPkg, '{"name":"rw","v":2}');

      assert.equal(readFileSync(roPkg, "utf8"), '{"name":"ro","v":1}');
      assert.equal(readFileSync(roIdx, "utf8"), "export const v = 2;");
      assert.equal(readFileSync(rwPkg, "utf8"), '{"name":"rw","v":2}');
      // 路径穿越出白名单目录必须透传
      assert.equal(readFileSync(escapePath, "utf8"), '{"name":"rw","v":2}');
      assert.ok(state.stats.hits >= 5, "expected multiple cache hits, got " + state.stats.hits);
      console.log("OK");
    `;
    const out = runWithShim(script, {
      DSHM_FS_CACHE_PREFIXES: roDir,
      DSHM_FS_CACHE_TTL_MS: "0",
    });
    assert.equal(out, "OK");
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("startup-fs-cache-shim automatically uninstalls and restores fs after TTL expires", () => {
  const root = mkdtempSync(join(tmpdir(), "dshm-fscache-ttl-"));
  const roDir = join(root, "readonly-runtime");
  mkdirSync(join(roDir, "pkg"), { recursive: true });
  const pkgPath = join(roDir, "pkg", "package.json");
  writeFileSync(pkgPath, '{"v":1}');

  try {
    const script = `
      import assert from "node:assert/strict";
      import fs, { readFileSync } from "node:fs";

      const pkgPath = ${JSON.stringify(pkgPath)};
      const state = globalThis[Symbol.for("dshmobile.startupFsCache.state")];
      assert.equal(readFileSync(pkgPath, "utf8"), '{"v":1}');
      fs.writeFileSync(pkgPath, '{"v":2}');
      assert.equal(readFileSync(pkgPath, "utf8"), '{"v":1}');

      await new Promise((resolve) => setTimeout(resolve, 120));
      assert.equal(state.active, false);
      assert.equal(readFileSync(pkgPath, "utf8"), '{"v":2}');
      console.log("UNINSTALLED");
    `;
    const out = runWithShim(script, {
      DSHM_FS_CACHE_PREFIXES: roDir,
      DSHM_FS_CACHE_TTL_MS: "40",
    });
    assert.equal(out, "UNINSTALLED");
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
