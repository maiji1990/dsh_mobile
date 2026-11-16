// Test double of DSH's automation profiles for dsh-e2e.test.mjs (vNext PR-07). Speaks the same wire formats as
// @deepseek-ai/dsh 0.2.1-rc.5 (headless --json events, SDK JSON-RPC + session.event, ACP v1 incl.
// session/request_permission and session/resume) but "runs" the e2e task itself instead of calling a model.
//
// FAKE_DSH_MODE: ok | no-credential (model call refused, with a key-shaped string in the error) |
//                lie (claims the tool ran and answers the nonce, writes nothing) | forget (resume cannot recall)
import { randomUUID } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { createInterface } from "node:readline";

const mode = process.env.FAKE_DSH_MODE ?? "ok";
const store = join(process.env.DSH_HOME, "fake-sessions");
mkdirSync(store, { recursive: true });
const load = (id) => (existsSync(join(store, id + ".json")) ? JSON.parse(readFileSync(join(store, id + ".json"), "utf8")) : undefined);
const save = (id, rec) => writeFileSync(join(store, id + ".json"), JSON.stringify(rec));
const AUTH_ERROR = "401 Unauthorized: invalid API key sk-fakeabcdef0123456789 (configure the provider credential)";

/** Runs one task; returns {tool, text} and records the nonce. */
function doTask(id, task, allowWrite = true) {
  const rec = load(id) ?? { cwd: process.cwd() };
  const w = /printf %s (\S+) > (\S+)\. Then reply/.exec(task);
  if (w) {
    if (mode !== "lie" && allowWrite) writeFileSync(join(process.cwd(), w[2]), w[1]);
    save(id, { ...rec, nonce: w[1], file: w[2] });
    return { tool: true, ok: allowWrite, text: allowWrite ? w[1] : "the command was rejected" };
  }
  return { tool: false, ok: true, text: mode === "forget" || !rec.nonce ? "I do not know." : rec.nonce };
}

const args = process.argv.slice(2);
if (args[0] === "--version") { console.log("dsh 0.0.0-fake"); process.exit(0); }
const profile = args[args.indexOf("--profile") + 1];

if (profile === "headless") {
  const rest = args.slice(args.indexOf("--profile") + 2);
  const json = rest.includes("--json");
  const si = rest.indexOf("--session-id");
  const given = si >= 0 ? rest[si + 1] : undefined;
  const task = rest.filter((a, i) => a !== "--json" && i !== si && i !== si + 1).join(" ");
  if (given !== undefined && load(given) === undefined) { process.stderr.write(`session "${given}" does not exist\n`); process.exit(1); }
  const id = given ?? `session-${randomUUID()}`;
  if (!json) process.exit(2);
  const out = (e) => process.stdout.write(JSON.stringify(e) + "\n");
  out({ type: "session", sessionId: id, cwd: process.cwd() });
  if (mode === "no-credential") { process.stderr.write(`dsh: provider_auth: ${AUTH_ERROR}\n`); process.exit(1); }
  if (!given) save(id, { cwd: process.cwd() });
  const r = doTask(id, task);
  if (r.tool) {
    out({ type: "tool_call", callId: "c1", tool: "bash", input: { command: "printf" } });
    out({ type: "tool_result", callId: "c1", status: "completed", result: "" });
  }
  out({ type: "final", text: r.text });
  process.exit(0);
}

const send = (m) => process.stdout.write(JSON.stringify({ jsonrpc: "2.0", ...m }) + "\n");
const pending = new Map();
let nextId = 1000;
const ask = (method, params) => new Promise((resolve) => { const id = nextId++; pending.set(id, resolve); send({ id, method, params }); });
const rl = createInterface({ input: process.stdin });
rl.on("close", () => process.exit(0));

if (profile === "sdk") {
  rl.on("line", (line) => {
    const m = JSON.parse(line);
    const reply = (result) => send({ id: m.id, result });
    const fail = (message) => send({ id: m.id, error: { code: -32603, message } });
    const p = m.params ?? {};
    switch (m.method) {
      case "initialize": return reply({ serverInfo: { name: "deepseek-harness-sdk-runtime", version: "0.0.1" } });
      case "session/prompt": {
        const text = (p.contentBlocks ?? []).map((b) => b.text).join("");
        if (mode === "no-credential") {
          // Like DSH 0.2.1-rc.5: the failed turn ends durably with an error reason and session/wait then answers {}.
          send({ method: "session.event", params: { sessionId: p.sessionId, event: { type: "turn/end", data: { turn: 1, reason: { kind: "error", error: { code: "MISSING_CREDENTIAL", message: AUTH_ERROR } } } } } });
          return reply({ messageId: "m1" });
        }
        if (load(p.sessionId) === undefined) save(p.sessionId, { cwd: process.cwd() });
        const r = doTask(p.sessionId, text);
        const ev = (event) => send({ method: "session.event", params: { sessionId: p.sessionId, event } });
        if (r.tool) { ev({ type: "tool/call", data: { name: "bash" } }); ev({ type: "tool/result", data: { message: { isError: false } } }); }
        ev({ type: "assistant/message", data: { message: { content: [{ type: "text", text: r.text }] } } });
        ev({ type: "turn/end", data: { turn: 1, reason: { kind: "completed" } } });
        return reply({ messageId: "m1" });
      }
      case "session/wait": return reply({});
      case "session/resume": return load(p.sessionId) ? reply({}) : fail(`no persisted session ${p.sessionId}`);
      case "shutdown": reply({}); return setTimeout(() => process.exit(0), 10);
      default: return send({ id: m.id, error: { code: -32601, message: `method not found: ${m.method}` } });
    }
  });
} else if (profile === "acp") {
  rl.on("line", async (line) => {
    const m = JSON.parse(line);
    if (m.method === undefined) { pending.get(m.id)?.(m.result); pending.delete(m.id); return; }
    const reply = (result) => send({ id: m.id, result });
    const p = m.params ?? {};
    const update = (u) => send({ method: "session/update", params: { sessionId: p.sessionId, update: u } });
    switch (m.method) {
      case "initialize": return reply({ protocolVersion: 1, agentInfo: { name: "deepseek-harness-acp" }, agentCapabilities: { sessionCapabilities: { close: {}, list: {}, resume: {} } }, authMethods: [] });
      case "session/new": { const id = randomUUID(); save(id, { cwd: p.cwd }); return reply({ sessionId: id }); }
      case "session/list": return reply({ sessions: readdirSync(store).map((f) => ({ sessionId: f.replace(/\.json$/, ""), cwd: load(f.replace(/\.json$/, "")).cwd })).filter((x) => x.cwd === p.cwd) });
      case "session/resume": return load(p.sessionId) ? reply({}) : send({ id: m.id, error: { code: -32602, message: "session is not resumable" } });
      case "session/prompt": {
        if (mode === "no-credential") return send({ id: m.id, error: { code: -32603, message: AUTH_ERROR } });
        const text = p.prompt.map((b) => b.text).join("");
        let allow = true;
        if (/printf %s/.test(text)) {
          update({ sessionUpdate: "tool_call", toolCallId: "t1", title: "bash", status: "pending" });
          const res = await ask("session/request_permission", { sessionId: p.sessionId, toolCall: { toolCallId: "t1" }, options: [{ optionId: "allow-once", name: "Allow once", kind: "allow_once" }, { optionId: "reject-once", name: "Reject", kind: "reject_once" }] });
          allow = res?.outcome?.outcome === "selected" && res.outcome.optionId === "allow-once";
        }
        const r = doTask(p.sessionId, text, allow);
        if (r.tool) update({ sessionUpdate: "tool_call_update", toolCallId: "t1", status: allow ? "completed" : "failed" });
        update({ sessionUpdate: "agent_message_chunk", content: { type: "text", text: r.text } });
        return reply({ stopReason: "end_turn" });
      }
      default: return send({ id: m.id, error: { code: -32601, message: `method not found: ${m.method}` } });
    }
  });
} else {
  process.stderr.write(`fake-dsh: unknown invocation ${args.join(" ")}\n`);
  process.exit(2);
}
