// DSH Mobile — guest-side Agent end-to-end check (vNext PR-07). Runs INSIDE the guest under the app's Node.
//
//   node /opt/dshmobile-cache/dshmobile/dsh-e2e.mjs [--suites headless,sdk,acp] [--workspace DIR]
//        [--provider deepseek-official] [--model deepseek-v4-flash] [--turn-timeout-ms 600000]
//
// Where dsh-probe.mjs stops at the handshake, this drives REAL model turns through each automation profile and checks
// their effects on disk, never a "process started" or "the model said so" check:
//   headless  `--json` task: a shell tool call writes a nonce file -> tool_call + completed tool_result + file content
//             + final answer; then a NEW process `--session-id <id>` recalls the nonce (persisted session resume).
//   sdk       initialize -> session/prompt -> session/wait, the same file + answer checks from `session.event`;
//             then a NEW process: initialize -> session/resume -> recall.
//   acp       ACP v1 initialize -> session/new -> session/prompt (any session/request_permission is answered
//             allow-once and counted) -> stopReason end_turn + file + answer; session/list; then a NEW process:
//             session/resume -> recall.
//
// Credentials: this script never reads, asks for, prints or stores a model credential. It uses whatever the owner
// configured in DSH on the device (WebUI). A turn that fails for lack of one is reported "needs-credential", never
// "pass" (AGENTS.md rule 5). Every string it prints passes through redact() (API-key / bearer / token shapes).
// Output: one line "DSHE2E <json>" on stdout. Exit 0 pass, 3 needs-credential, 1 fail, 2 usage.
// Files are written only under the workspace (default /root/projects/deepseek-harness/e2e/<run id>).

import { spawn } from "node:child_process";
import { randomBytes, randomUUID } from "node:crypto";
import { existsSync, mkdirSync, readFileSync } from "node:fs";
import { isAbsolute, join } from "node:path";
import { pathToFileURL } from "node:url";

const DSH_BIN = process.env.DSHE2E_DSH_BIN || "/opt/dshapp/runtime/node_modules/@deepseek-ai/dsh/lib/bin.js";
const SHIM = "/opt/dshmobile/link-shim.mjs";
const DSH_HOME = process.env.DSH_HOME || "/root/projects/.dsh";
const SUITES = ["headless", "sdk", "acp"];

// ---------------------------------------------------------------- pure helpers (exported for tests)

/** Masks credential-shaped substrings. Applied to every string this script prints. */
export function redact(text) {
  return String(text)
    .replace(/\b(sk-[A-Za-z0-9]{2})[A-Za-z0-9_-]{6,}/g, "$1…<redacted>")
    .replace(/(Bearer\s+)[A-Za-z0-9._~+/=-]{6,}/gi, "$1<redacted>")
    .replace(/((?:api[_-]?key|access[_-]?token|auth[_-]?token|secret|password|authorization)["']?\s*[:=]\s*["']?)(?!Bearer\b)[^\s"',}]{4,}/gi, "$1<redacted>");
}

const CREDENTIAL = /api[ _-]?key|credential|unauthori[sz]ed|\b401\b|authenticat|not logged in|no (?:model|provider) (?:is )?configured|missing .*key|invalid .*key|incorrect .*key/i;

/** True when a failure text says the model call had no (valid) credential. */
export function isCredentialFailure(text) {
  return CREDENTIAL.test(String(text ?? ""));
}

export function parseArgs(argv) {
  const opts = { suites: [...SUITES], workspace: undefined, provider: "deepseek-official", model: "deepseek-v4-flash", turnTimeoutMs: 600_000 };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i], v = argv[i + 1];
    const need = () => { if (v === undefined) throw new Error(`${a} needs a value`); i++; return v; };
    if (a === "--suites") {
      opts.suites = need().split(",").map((s) => s.trim()).filter(Boolean);
      const bad = opts.suites.filter((s) => !SUITES.includes(s));
      if (bad.length || !opts.suites.length) throw new Error(`unknown suite(s): ${bad.join(",") || "<none>"}`);
    } else if (a === "--workspace") opts.workspace = need();
    else if (a === "--provider") opts.provider = need();
    else if (a === "--model") opts.model = need();
    else if (a === "--turn-timeout-ms") {
      opts.turnTimeoutMs = Number(need());
      if (!Number.isSafeInteger(opts.turnTimeoutMs) || opts.turnTimeoutMs <= 0) throw new Error("--turn-timeout-ms must be a positive integer");
    } else throw new Error(`unknown argument ${a}`);
  }
  if (opts.workspace !== undefined && !isAbsolute(opts.workspace)) throw new Error("--workspace must be absolute");
  return opts;
}

/** Overall status: any fail -> fail; else any needs-credential -> needs-credential; else pass. */
export function overall(suites) {
  if (suites.some((s) => s.status === "fail")) return "fail";
  if (suites.some((s) => s.status === "needs-credential")) return "needs-credential";
  return suites.length > 0 ? "pass" : "fail";
}

/** All text blocks ({type:"text", text}) inside a value. */
export function textsOf(value, out = []) {
  if (Array.isArray(value)) value.forEach((v) => textsOf(v, out));
  else if (value && typeof value === "object") {
    if (value.type === "text" && typeof value.text === "string") out.push(value.text);
    else Object.values(value).forEach((v) => textsOf(v, out));
  }
  return out;
}

// ---------------------------------------------------------------- process plumbing

function launch(args, cwd) {
  const pre = ["--expose-internals"];
  if (existsSync(SHIM)) pre.push("--import", SHIM);
  return spawn(process.execPath, [...pre, DSH_BIN, ...args], {
    cwd,
    // Same PATH head the app gives the DSH role (link-shim dir + guest wrappers, incl. xdg-user-dir).
    env: { ...process.env, DSH_HOME, NO_COLOR: "1", PATH: `/opt/dshmobile:/root/projects/.dsh/dshmobile/bin:${process.env.PATH ?? ""}` },
    stdio: ["pipe", "pipe", "pipe"],
  });
}

const tail = (s, n = 600) => redact(s.length > n ? "…" + s.slice(-n) : s).trim();

function run(args, cwd, timeoutMs) {
  const t0 = Date.now();
  return new Promise((resolve) => {
    const child = launch(args, cwd);
    let out = "", err = "";
    child.stdout.on("data", (d) => { if (out.length < 4_000_000) out += d; });
    child.stderr.on("data", (d) => { if (err.length < 400_000) err += d; });
    const timer = setTimeout(() => child.kill("SIGKILL"), timeoutMs);
    child.on("close", (code, signal) => {
      clearTimeout(timer);
      resolve({ code, signal, stdout: out, stderr: err, ms: Date.now() - t0, timedOut: signal === "SIGKILL" });
    });
    child.stdin.end();
  });
}

/** Newline-delimited JSON-RPC peer: our requests, its notifications, and its requests to us (onRequest). */
function rpcPeer(args, cwd, onRequest = () => { throw Object.assign(new Error("method not found"), { code: -32601 }); }) {
  const child = launch(args, cwd);
  const waiters = new Map();
  const notifications = [];
  let buf = "", err = "", exitInfo = null, nextId = 1;
  const exitWaiters = [];
  const write = (msg) => { if (!exitInfo) child.stdin.write(JSON.stringify({ jsonrpc: "2.0", ...msg }) + "\n"); };
  child.stdout.on("data", async (d) => {
    buf += d;
    let i;
    while ((i = buf.indexOf("\n")) >= 0) {
      const line = buf.slice(0, i).trim(); buf = buf.slice(i + 1);
      if (!line) continue;
      let msg; try { msg = JSON.parse(line); } catch { continue; }
      if (msg.method !== undefined && msg.id !== undefined) {
        try { write({ id: msg.id, result: await onRequest(msg.method, msg.params ?? {}) }); }
        catch (e) { write({ id: msg.id, error: { code: e.code ?? -32603, message: String(e.message) } }); }
      } else if (msg.method !== undefined) {
        notifications.push({ method: msg.method, params: msg.params });
      } else if (waiters.has(msg.id)) {
        waiters.get(msg.id)(msg); waiters.delete(msg.id);
      }
    }
  });
  child.stderr.on("data", (d) => { if (err.length < 400_000) err += d; });
  child.on("close", (code, signal) => {
    exitInfo = { code, signal };
    for (const w of waiters.values()) w({ exited: true });
    waiters.clear();
    exitWaiters.splice(0).forEach((f) => f(exitInfo));
  });
  return {
    notifications,
    request(method, params, timeoutMs) {
      const id = nextId++;
      return new Promise((resolve) => {
        if (exitInfo) { resolve({ exited: true }); return; }
        const timer = setTimeout(() => { waiters.delete(id); resolve({ timeout: true }); }, timeoutMs);
        waiters.set(id, (m) => { clearTimeout(timer); resolve(m); });
        write({ id, method, ...(params === undefined ? {} : { params }) });
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

/** Describes a failed JSON-RPC reply for a check detail. */
function rpcFailure(reply, peer) {
  if (reply.timeout) return `timeout; stderr: ${tail(peer.stderr())}`;
  if (reply.exited) return `process exited; stderr: ${tail(peer.stderr())}`;
  if (reply.error) return `error ${redact(JSON.stringify(reply.error))}`;
  return null;
}

// ---------------------------------------------------------------- suites

class Suite {
  constructor(name) { this.name = name; this.checks = []; this.needsCredential = false; }
  check(name, ok, detail, ms) {
    this.checks.push({ name: `${this.name}:${name}`, ok: !!ok, ...(ms !== undefined ? { ms } : {}), detail: redact(detail ?? "") });
    return !!ok;
  }
  /** A failure that is really "no credential": recorded once, the rest of the suite is skipped. */
  credentialGate(text) {
    if (!isCredentialFailure(text)) return false;
    this.needsCredential = true;
    this.check("credential", false, `model call refused for lack of a credential: ${tail(String(text), 300)}`);
    return true;
  }
  result() {
    const realFailure = this.checks.some((c) => !c.ok && c.name !== `${this.name}:credential`);
    const status = realFailure || this.checks.length === 0 ? "fail" : this.needsCredential ? "needs-credential" : "pass";
    return { suite: this.name, status, needsCredential: this.needsCredential, checks: this.checks };
  }
}

const nonceOf = () => "E2E" + randomBytes(6).toString("hex").toUpperCase();
const writeTask = (nonce, file) =>
  `This is an automated DSH Mobile end-to-end check. Use your shell tool to run exactly this command in the current directory: printf %s ${nonce} > ${file}. Then reply with only the text ${nonce}.`;
const recallTask = (file) =>
  `What exact string did you write to ${file} earlier in this session? Reply with only that string and do not use any tools.`;

function fileCheck(suite, ws, file, nonce) {
  const path = join(ws, file);
  const content = existsSync(path) ? readFileSync(path, "utf8") : null;
  return suite.check("file", content === nonce, content === null ? `${file} was not written` : `${file} = ${JSON.stringify(content.slice(0, 80))}`);
}

async function headless(opts, ws) {
  const s = new Suite("headless");
  const nonce = nonceOf(), file = "e2e-headless.txt";
  const r = await run(["--profile", "headless", "--json", writeTask(nonce, file)], ws, opts.turnTimeoutMs);
  const events = r.stdout.split("\n").flatMap((l) => { try { return [JSON.parse(l)]; } catch { return []; } });
  const errors = events.filter((e) => e.type === "error").map((e) => e.message).join("; ");
  const sessionId = events.find((e) => e.type === "session")?.sessionId;
  if (r.code !== 0 && s.credentialGate(`${errors} ${r.stderr}`)) return s.result();
  s.check("exit", r.code === 0, `exit=${r.code}${r.timedOut ? " (timeout)" : ""} ${errors} ${tail(r.stderr, 300)}`, r.ms);
  s.check("session", typeof sessionId === "string" && sessionId.length > 0, `sessionId=${sessionId}`);
  const calls = events.filter((e) => e.type === "tool_call");
  const done = events.filter((e) => e.type === "tool_result" && e.status === "completed");
  s.check("tool", calls.length > 0 && done.length > 0, `tool_call=${calls.map((c) => c.tool).join(",")} completed=${done.length}`);
  fileCheck(s, ws, file, nonce);
  const final = events.findLast((e) => e.type === "final")?.text ?? "";
  s.check("answer", final.includes(nonce), `final=${JSON.stringify(final.slice(0, 200))}`);
  if (!sessionId) return s.result();

  const again = await run(["--profile", "headless", "--json", "--session-id", sessionId, recallTask(file)], ws, opts.turnTimeoutMs);
  const ev2 = again.stdout.split("\n").flatMap((l) => { try { return [JSON.parse(l)]; } catch { return []; } });
  s.check("resume:exit", again.code === 0, `exit=${again.code}${again.timedOut ? " (timeout)" : ""} ${tail(again.stderr, 300)}`, again.ms);
  s.check("resume:session", ev2.find((e) => e.type === "session")?.sessionId === sessionId, "same sessionId in a new process");
  const final2 = ev2.findLast((e) => e.type === "final")?.text ?? "";
  s.check("resume:recall", final2.includes(nonce), `final=${JSON.stringify(final2.slice(0, 200))}`);
  return s.result();
}

/** SDK: events arrive as `session.event` notifications carrying Session log events. */
function sdkObserved(peer, sessionId) {
  const ev = peer.notifications.filter((n) => n.method === "session.event" && n.params?.sessionId === sessionId).map((n) => n.params.event ?? {});
  return {
    calls: ev.filter((e) => e.type === "tool/call").map((e) => e.data?.name),
    results: ev.filter((e) => e.type === "tool/result" && e.data?.message?.isError !== true).length,
    text: ev.filter((e) => e.type === "assistant/message").flatMap((e) => textsOf(e.data?.message?.content)).join(""),
  };
}

async function sdkTurn(s, peer, sessionId, text, opts, label) {
  const t0 = Date.now();
  const p = await peer.request("session/prompt", { sessionId, contentBlocks: [{ type: "text", text }] }, 120_000);
  if (rpcFailure(p, peer)) {
    if (s.credentialGate(rpcFailure(p, peer))) return false;
    return s.check(`${label}prompt`, false, rpcFailure(p, peer));
  }
  const w = await peer.request("session/wait", { sessionId }, opts.turnTimeoutMs);
  const failure = rpcFailure(w, peer);
  if (failure && s.credentialGate(failure)) return false;
  return s.check(`${label}turn`, !failure, failure ?? `messageId=${p.result?.messageId}`, Date.now() - t0);
}

async function sdkInit(s, peer, opts, ws, label) {
  const init = await peer.request("initialize", { cwd: ws, provider: opts.provider, model: opts.model }, 240_000);
  const failure = rpcFailure(init, peer);
  if (failure && s.credentialGate(failure)) return false;
  return s.check(`${label}initialize`, !failure && init.result?.serverInfo?.name === "deepseek-harness-sdk-runtime", failure ?? JSON.stringify(init.result).slice(0, 200));
}

async function sdk(opts, ws) {
  const s = new Suite("sdk");
  const nonce = nonceOf(), file = "e2e-sdk.txt", sessionId = `session-${randomUUID()}`;
  let peer = rpcPeer(["--profile", "sdk"], ws);
  try {
    if (!await sdkInit(s, peer, opts, ws, "") || !await sdkTurn(s, peer, sessionId, writeTask(nonce, file), opts, "")) return s.result();
    const o = sdkObserved(peer, sessionId);
    s.check("tool", o.calls.length > 0 && o.results > 0, `tool/call=${o.calls.join(",")} ok results=${o.results}`);
    fileCheck(s, ws, file, nonce);
    s.check("answer", o.text.includes(nonce), `assistant=${JSON.stringify(o.text.slice(0, 200))}`);
    await peer.request("shutdown", undefined, 60_000);
    const exit = await peer.waitExit(60_000);
    s.check("shutdown", !exit.timeout && exit.code === 0, exit.timeout ? "no exit" : `exit=${exit.code}`);
  } finally { peer.kill(); }

  peer = rpcPeer(["--profile", "sdk"], ws);
  try {
    if (!await sdkInit(s, peer, opts, ws, "resume:")) return s.result();
    const r = await peer.request("session/resume", { sessionId }, 120_000);
    if (!s.check("resume:resume", !rpcFailure(r, peer), rpcFailure(r, peer) ?? "persisted session reopened in a new process")) return s.result();
    const before = sdkObserved(peer, sessionId).text.length;
    if (!await sdkTurn(s, peer, sessionId, recallTask(file), opts, "resume:")) return s.result();
    const text = sdkObserved(peer, sessionId).text.slice(before);
    s.check("resume:recall", text.includes(nonce), `assistant=${JSON.stringify(text.slice(0, 200))}`);
    await peer.request("shutdown", undefined, 60_000);
  } finally { peer.kill(); }
  return s.result();
}

const ACP_INIT = { protocolVersion: 1, clientCapabilities: { fs: { readTextFile: false, writeTextFile: false }, terminal: false }, clientInfo: { name: "dsh-mobile-e2e", version: "0.1.0" } };

function acpPeer(ws, permissions) {
  return rpcPeer(["--profile", "acp"], ws, (method, params) => {
    if (method === "session/request_permission") {
      const allow = (params.options ?? []).find((o) => o.kind === "allow_once");
      permissions.push({ toolCallId: params.toolCall?.toolCallId, options: (params.options ?? []).map((o) => o.kind) });
      return allow ? { outcome: { outcome: "selected", optionId: allow.optionId } } : { outcome: { outcome: "cancelled" } };
    }
    throw Object.assign(new Error(`method not found: ${method}`), { code: -32601 }); // no fs / terminal capability offered
  });
}

function acpObserved(peer, sessionId, from = 0) {
  const up = peer.notifications.slice(from).filter((n) => n.method === "session/update" && n.params?.sessionId === sessionId).map((n) => n.params.update ?? {});
  return {
    calls: up.filter((u) => u.sessionUpdate === "tool_call").length,
    completed: up.filter((u) => (u.sessionUpdate === "tool_call_update" || u.sessionUpdate === "tool_call") && u.status === "completed").length,
    text: up.filter((u) => u.sessionUpdate === "agent_message_chunk").flatMap((u) => textsOf(u.content)).join(""),
  };
}

async function acpPrompt(s, peer, sessionId, text, opts, label) {
  const t0 = Date.now();
  const r = await peer.request("session/prompt", { sessionId, prompt: [{ type: "text", text }] }, opts.turnTimeoutMs);
  const failure = rpcFailure(r, peer);
  if (failure && s.credentialGate(failure)) return false;
  return s.check(`${label}prompt`, !failure && r.result?.stopReason === "end_turn", failure ?? `stopReason=${r.result?.stopReason}`, Date.now() - t0);
}

async function acp(opts, ws) {
  const s = new Suite("acp");
  const nonce = nonceOf(), file = "e2e-acp.txt", permissions = [];
  let sessionId, peer = acpPeer(ws, permissions);
  try {
    const init = await peer.request("initialize", ACP_INIT, 240_000);
    if (!s.check("initialize", !rpcFailure(init, peer) && init.result?.protocolVersion === 1, rpcFailure(init, peer) ?? JSON.stringify(init.result?.agentCapabilities ?? {}).slice(0, 200))) return s.result();
    const created = await peer.request("session/new", { cwd: ws, mcpServers: [] }, 240_000);
    sessionId = created.result?.sessionId;
    if (!s.check("new", typeof sessionId === "string", rpcFailure(created, peer) ?? `sessionId=${sessionId}`)) return s.result();
    if (!await acpPrompt(s, peer, sessionId, writeTask(nonce, file), opts, "")) return s.result();
    const o = acpObserved(peer, sessionId);
    s.check("tool", o.calls > 0 && o.completed > 0, `tool_call=${o.calls} completed=${o.completed} permission requests=${permissions.length} (answered allow-once)`);
    fileCheck(s, ws, file, nonce);
    s.check("answer", o.text.includes(nonce), `agent=${JSON.stringify(o.text.slice(0, 200))}`);
    if (init.result?.agentCapabilities?.sessionCapabilities?.list) {
      const list = await peer.request("session/list", { cwd: ws }, 60_000);
      s.check("list", (list.result?.sessions ?? []).some((x) => x.sessionId === sessionId), rpcFailure(list, peer) ?? `${(list.result?.sessions ?? []).length} session(s)`);
    }
    peer.endInput();
    const exit = await peer.waitExit(60_000);
    s.check("shutdown", !exit.timeout && exit.code === 0, exit.timeout ? "no exit on stdin EOF" : `exit=${exit.code}`);
  } finally { peer.kill(); }

  peer = acpPeer(ws, permissions);
  try {
    const init = await peer.request("initialize", ACP_INIT, 240_000);
    if (!init.result?.agentCapabilities?.sessionCapabilities?.resume) {
      s.check("resume:supported", false, "agent does not advertise sessionCapabilities.resume");
      return s.result();
    }
    const r = await peer.request("session/resume", { sessionId, cwd: ws, mcpServers: [] }, 240_000);
    if (!s.check("resume:resume", !rpcFailure(r, peer), rpcFailure(r, peer) ?? "persisted session reopened in a new process")) return s.result();
    const from = peer.notifications.length;
    if (!await acpPrompt(s, peer, sessionId, recallTask(file), opts, "resume:")) return s.result();
    const text = acpObserved(peer, sessionId, from).text;
    s.check("resume:recall", text.includes(nonce), `agent=${JSON.stringify(text.slice(0, 200))}`);
    peer.endInput();
    await peer.waitExit(60_000);
  } finally { peer.kill(); }
  return s.result();
}

// ---------------------------------------------------------------- main

function emit(obj) { process.stdout.write("DSHE2E " + redact(JSON.stringify(obj)) + "\n"); }

async function main() {
  let opts;
  try { opts = parseArgs(process.argv.slice(2)); } catch (e) {
    emit({ schema: 1, status: "fail", error: `usage: ${e.message}` });
    process.exit(2);
  }
  const runId = new Date().toISOString().replace(/[-:]/g, "").replace(/\..*/, "") + "-" + randomBytes(3).toString("hex");
  const ws = opts.workspace ?? `/root/projects/deepseek-harness/e2e/${runId}`;
  mkdirSync(ws, { recursive: true });
  const startedAt = new Date().toISOString();
  const v = await run(["--version"], ws, 120_000);
  const versions = {
    dsh: v.code === 0 ? v.stdout.trim().split("\n").pop() : `unknown (exit ${v.code})`,
    node: process.version,
    debian: existsSync("/etc/debian_version") ? readFileSync("/etc/debian_version", "utf8").trim() : null,
    arch: process.arch,
  };
  const runners = { headless, sdk, acp };
  const suites = [];
  for (const name of opts.suites) {
    try { suites.push(await runners[name](opts, ws)); } catch (e) {
      suites.push({ suite: name, status: "fail", needsCredential: false, checks: [{ name: `${name}:crash`, ok: false, detail: redact(String(e?.stack ?? e)) }] });
    }
  }
  const status = overall(suites);
  emit({ schema: 1, status, runId, workspace: ws, startedAt, finishedAt: new Date().toISOString(), provider: opts.provider, model: opts.model, versions, suites });
  process.exit(status === "pass" ? 0 : status === "needs-credential" ? 3 : 1);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) await main();
