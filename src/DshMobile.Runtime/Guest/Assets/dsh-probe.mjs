// DSH Mobile — guest-side profile probe (TASK-005). Runs INSIDE the PRoot guest under Node 24.
//
// Verifies each DSH entry mode with a real protocol exchange, never a "process started" check:
//   web          --dump-config, and (if running) HTTP GET / on the app's DSH (401 = auth enforced)
//   headless     --dump-config, --help; a real job needs a model credential -> reported as "needs-credential"
//   sdk          --dump-config, JSON-RPC initialize -> serverInfo, shutdown -> {}, exit 0
//   sdk-minimal  same as sdk
//   acp          --dump-config, ACP v1 initialize -> protocolVersion, stdin EOF -> exit 0
//   plugin       `dsh plugin --profile web --help` and `... list` (pnpm forwarding)
//
// DSH is launched exactly like DSHBox launches the web profile: node --expose-internals
// [--import /opt/dshbox/link-shim.mjs] bin.js --profile <p>, DSH_HOME=/root/projects/.dsh, cwd /root/projects.
// No model API is ever called. Output: one line "DSHPROBE <json>" on stdout.

import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import http from "node:http";

const DSH_BIN = "/opt/dshapp/runtime/node_modules/@deepseek-ai/dsh/lib/bin.js";
const SHIM = "/opt/dshbox/link-shim.mjs";
const DSH_HOME = "/root/projects/.dsh";
const CWD = "/root/projects";
const PROFILES = new Set(["web", "headless", "sdk", "sdk-minimal", "acp", "plugin"]);

const profile = process.argv[2];
if (!PROFILES.has(profile)) {
  emit({ profile, status: "fail", checks: [{ name: "args", ok: false, detail: `unknown profile ${profile}` }] });
  process.exit(2);
}

function nodeArgs(args) {
  const pre = ["--expose-internals"];
  if (existsSync(SHIM)) pre.push("--import", SHIM);
  return [...pre, DSH_BIN, ...args];
}

function launch(args) {
  return spawn(process.execPath, nodeArgs(args), {
    cwd: CWD,
    // Same PATH head DSHBox gives the DSH role: link-shim dir + CLI wrappers (pnpm for `dsh plugin`).
    env: { ...process.env, DSH_HOME, NO_COLOR: "1", PATH: `/opt/dshbox:/root/projects/.dsh/dshbox/bin:${process.env.PATH ?? ""}` },
    stdio: ["pipe", "pipe", "pipe"],
  });
}

const tail = (s, n = 400) => (s.length > n ? "…" + s.slice(-n) : s).trim();

/** Run to completion; resolve {code, stdout, stderr, ms}. */
function run(args, timeoutMs, stdin = null) {
  const t0 = Date.now();
  return new Promise((resolve) => {
    const child = launch(args);
    let out = "", err = "", done = false;
    child.stdout.on("data", (d) => { if (out.length < 2_000_000) out += d; });
    child.stderr.on("data", (d) => { if (err.length < 200_000) err += d; });
    const timer = setTimeout(() => { if (!done) { child.kill("SIGKILL"); } }, timeoutMs);
    child.on("close", (code, signal) => {
      done = true; clearTimeout(timer);
      resolve({ code, signal, stdout: out, stderr: err, ms: Date.now() - t0, timedOut: signal === "SIGKILL" });
    });
    if (stdin !== null) child.stdin.end(stdin); else child.stdin.end();
  });
}

/** Newline-delimited JSON-RPC session over a long-running child. */
function rpcSession(args) {
  const child = launch(args);
  const waiters = new Map();
  let buf = "", err = "", exitInfo = null;
  const exitWaiters = [];
  child.stdout.on("data", (d) => {
    buf += d;
    let i;
    while ((i = buf.indexOf("\n")) >= 0) {
      const line = buf.slice(0, i).trim(); buf = buf.slice(i + 1);
      if (!line) continue;
      let msg; try { msg = JSON.parse(line); } catch { continue; }
      if (msg.id !== undefined && waiters.has(msg.id)) { waiters.get(msg.id)(msg); waiters.delete(msg.id); }
    }
  });
  child.stderr.on("data", (d) => { if (err.length < 200_000) err += d; });
  child.on("close", (code, signal) => { exitInfo = { code, signal }; exitWaiters.splice(0).forEach((f) => f(exitInfo)); });
  return {
    request(id, method, params, timeoutMs) {
      return new Promise((resolve) => {
        const timer = setTimeout(() => { waiters.delete(id); resolve({ timeout: true }); }, timeoutMs);
        waiters.set(id, (m) => { clearTimeout(timer); resolve(m); });
        child.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, ...(params === undefined ? {} : { params }) }) + "\n");
      });
    },
    endInput() { child.stdin.end(); },
    waitExit(timeoutMs) {
      if (exitInfo) return Promise.resolve(exitInfo);
      return new Promise((resolve) => {
        const timer = setTimeout(() => { child.kill("SIGKILL"); resolve({ timeout: true }); }, timeoutMs);
        exitWaiters.push((e) => { clearTimeout(timer); resolve(e); });
      });
    },
    kill() { if (!exitInfo) child.kill("SIGKILL"); },
    stderr: () => err,
  };
}

const checks = [];
function check(name, ok, detail, ms) { checks.push({ name, ok, ...(ms !== undefined ? { ms } : {}), detail }); return ok; }

async function dumpConfig(p) {
  const r = await run(["--profile", p, "--dump-config"], 180_000);
  return check(`${p}:dump-config`, r.code === 0 && r.stdout.trim().length > 0,
    r.code === 0 ? `${r.stdout.length} bytes` : `exit=${r.code}${r.timedOut ? " (timeout)" : ""} ${tail(r.stderr)}`, r.ms);
}

function httpStatus(port, path) {
  return new Promise((resolve) => {
    const req = http.get({ host: "127.0.0.1", port, path, timeout: 5000 }, (res) => { res.resume(); resolve(res.statusCode); });
    req.on("timeout", () => { req.destroy(); resolve("timeout"); });
    req.on("error", (e) => resolve(e.code || "error"));
  });
}

async function jsonRpcProbe(p, initParams, validate, close) {
  const t0 = Date.now();
  const s = rpcSession(["--profile", p]);
  try {
    const init = await s.request(1, "initialize", initParams, 240_000);
    const initOk = !init.timeout && !init.error && validate(init.result);
    check(`${p}:initialize`, initOk,
      init.timeout ? `timeout; stderr: ${tail(s.stderr())}` : init.error ? `error ${JSON.stringify(init.error)}` : JSON.stringify(init.result).slice(0, 300),
      Date.now() - t0);
    if (!initOk) return;
    const t1 = Date.now();
    const exit = await close(s);
    check(`${p}:shutdown`, !exit.timeout && exit.code === 0,
      exit.timeout ? `no exit; stderr: ${tail(s.stderr())}` : `exit=${exit.code}${exit.signal ? " signal=" + exit.signal : ""}`, Date.now() - t1);
  } finally {
    s.kill();
  }
}

let needsCredential = false;

switch (profile) {
  case "web": {
    await dumpConfig("web");
    const status = await httpStatus(Number(process.env.PORT || 3080), "/");
    // 401 = the running app-managed DSH enforces its launch token; 200 would mean no auth (unexpected).
    check("web:http", status === 401, `GET / -> ${status}`);
    break;
  }
  case "headless": {
    await dumpConfig("headless");
    const r = await run(["--profile", "headless", "--help"], 120_000);
    check("headless:help", r.code === 0, r.code === 0 ? tail(r.stdout, 200) : `exit=${r.code} ${tail(r.stderr)}`, r.ms);
    needsCredential = true; // a real one-shot job calls the model
    break;
  }
  case "sdk":
  case "sdk-minimal": {
    await dumpConfig(profile);
    await jsonRpcProbe(profile,
      { cwd: CWD, provider: "deepseek-official", model: "deepseek-v4-flash" },
      (r) => r && r.serverInfo && r.serverInfo.name === "deepseek-harness-sdk-runtime",
      async (s) => {
        const bye = await s.request(2, "shutdown", undefined, 60_000);
        check(`${profile}:shutdown-reply`, !bye.timeout && !bye.error, bye.timeout ? "timeout" : JSON.stringify(bye.result ?? bye.error));
        return s.waitExit(60_000);
      });
    break;
  }
  case "acp": {
    await dumpConfig("acp");
    await jsonRpcProbe("acp",
      { protocolVersion: 1, clientCapabilities: { fs: { readTextFile: false, writeTextFile: false }, terminal: false }, clientInfo: { name: "dsh-mobile-probe", version: "0.1.0" } },
      (r) => r && r.protocolVersion === 1 && typeof r.agentCapabilities === "object",
      async (s) => { s.endInput(); return s.waitExit(60_000); });
    break;
  }
  case "plugin": {
    const h = await run(["plugin", "--profile", "web", "--help"], 180_000);
    check("plugin:help", h.code === 0, h.code === 0 ? tail(h.stdout, 200) : `exit=${h.code} ${tail(h.stderr)}`, h.ms);
    const l = await run(["plugin", "--profile", "web", "list"], 240_000);
    check("plugin:list", l.code === 0, l.code === 0 ? tail(l.stdout, 300) : `exit=${l.code}${l.timedOut ? " (timeout)" : ""} ${tail(l.stderr)}`, l.ms);
    break;
  }
}

const allOk = checks.every((c) => c.ok);
emit({ profile, status: !allOk ? "fail" : needsCredential ? "limited" : "pass", needsCredential, shim: existsSync(SHIM), checks });

function emit(obj) { process.stdout.write("DSHPROBE " + JSON.stringify(obj) + "\n"); }
