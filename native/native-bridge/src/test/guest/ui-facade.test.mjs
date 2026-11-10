// Tests for the guest ui_* facade (TASK-010). Run: node --test native/native-bridge/src/test/guest/
import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtempSync, readFileSync, existsSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createInterface } from 'node:readline';

import {
  UiFacade, callTool, mapRelayCode, lastJsonLine, roleOf, relayRead, TOOLS, DEVICE_ID,
} from '../../main/guest-assets/dshmobile/ui-facade.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const FAKE = join(here, 'fake-relay.mjs');
const FACADE = join(here, '../../main/guest-assets/dshmobile/ui-facade.mjs');

let logFile;
beforeEach(() => {
  const dir = mkdtempSync(join(tmpdir(), 'ui-facade-'));
  logFile = join(dir, 'calls.log');
  process.env.FAKE_RELAY_LOG = logFile;
  process.env.FAKE_RELAY_MODE = 'ok';
});

const calls = () => (existsSync(logFile) ? readFileSync(logFile, 'utf8').split('\n').filter(Boolean).map((l) => JSON.parse(l)) : []);
const facade = (opts = {}) => new UiFacade({ relayCli: FAKE, approvalWaitMs: 3000, ...opts });
const body = (result) => JSON.parse(result.content[0].text);

// ---------------------------------------------------------------- pure functions

test('maps Relay codes conservatively', () => {
  assert.equal(mapRelayCode('E_AWAITING_CONSENT'), 'APPROVAL_REQUIRED');
  assert.equal(mapRelayCode('E_GATE_HOST_DENIED'), 'POLICY_DENIED');
  assert.equal(mapRelayCode('E_GATE_APPROVAL_TIMEOUT'), 'POLICY_DENIED');
  assert.equal(mapRelayCode('E_GATE_SYSTEM_MISSING'), 'PROVIDER_UNAVAILABLE');
  assert.equal(mapRelayCode('E_GATE_NO_FOREGROUND'), 'PROVIDER_UNAVAILABLE');
  assert.equal(mapRelayCode('E_SURFACE_SECURE_WINDOW'), 'SECURE_CONTENT');
  assert.equal(mapRelayCode('E_NODE_AMBIGUOUS'), 'AMBIGUOUS_ELEMENT');
  assert.equal(mapRelayCode('E_TRANSPORT_TIMEOUT'), 'OPERATION_TIMEOUT');
  assert.equal(mapRelayCode('E_SOMETHING_NEW'), 'OBSERVATION_INCOMPLETE');
});

test('reads the last JSON line and ignores noise', () => {
  assert.deepEqual(lastJsonLine('warn: x\n{"a":1}\n{"b":2}\n'), { b: 2 });
  assert.equal(lastJsonLine('no json here'), null);
  assert.deepEqual(lastJsonLine('{broken\n{"ok":true}'), { ok: true });
});

test('derives roles from Android classes', () => {
  assert.equal(roleOf({ class: 'android.widget.Button' }), 'button');
  assert.equal(roleOf({ class: 'android.widget.ImageButton' }), 'button');
  assert.equal(roleOf({ class: 'android.widget.EditText' }), 'textbox');
  assert.equal(roleOf({ class: 'android.view.View', editable: true }), 'textbox');
  assert.equal(roleOf({ class: 'android.widget.CheckBox' }), 'checkbox');
  assert.equal(roleOf({ class: 'androidx.recyclerview.widget.RecyclerView' }), 'list');
  assert.equal(roleOf({ class: 'android.widget.TextView' }), 'text');
  assert.equal(roleOf({ class: 'android.widget.FrameLayout' }), 'group');
  assert.equal(roleOf({ class: 'com.example.Weird' }), 'unknown');
});

test('tool list is read-only and matches the contract names', () => {
  assert.deepEqual(TOOLS.map((t) => t.name).sort(), ['ui_find', 'ui_get_element', 'ui_list_devices', 'ui_snapshot']);
  for (const t of TOOLS) assert.equal(t.annotations.readOnlyHint, true);
});

// ---------------------------------------------------------------- facade over the fake CLI

test('snapshot builds contract elements; compact drops layout-only nodes', async () => {
  const r = await callTool(facade(), 'ui_snapshot', {});
  assert.equal(r.isError, undefined);
  const s = body(r);
  assert.equal(s.snapshotId, 's1');
  assert.equal(s.target.deviceId, DEVICE_ID);
  assert.equal(s.target.applicationId, 'com.android.systemui');
  assert.equal(s.totalNodes, 6);
  assert.deepEqual(s.elements.map((e) => e.ref), ['e5', 'e6', 'e9', 'e20', 'e66']);
  const close = s.elements.find((e) => e.ref === 'e9');
  assert.deepEqual(close, {
    ref: 'e9', provider: 'android-relay', role: 'button', name: '收起', text: null, nativeId: null,
    bounds: { x: 1133, y: 536, width: 56, height: 63 }, visible: true, actions: ['click'],
    extra: { class: 'android.widget.Button', nodeId: 9 },
  });
  const off = s.elements.find((e) => e.ref === 'e5');
  assert.equal(off.bounds, null);
  assert.equal(off.visible, false);
  assert.equal('enabled' in close, false, 'snapshot must not invent enabled');
  assert.deepEqual(calls().map((c) => c.capability), ['ui.snapshot']);
});

test('maxNodes truncates and says so', async () => {
  const s = body(await callTool(facade(), 'ui_snapshot', { mode: 'full', maxNodes: 2 }));
  assert.equal(s.elements.length, 2);
  assert.equal(s.truncated, true);
});

test('find filters the latest snapshot locally', async () => {
  const f = facade();
  await callTool(f, 'ui_snapshot', {});
  const before = calls().length;
  const r = body(await callTool(f, 'ui_find', { name: '通知', role: 'image' }));
  assert.equal(r.count, 1);
  assert.equal(r.matches[0].ref, 'e66');
  assert.equal(calls().length, before, 'find must not call Relay');
});

test('get_element re-reads the live node and adds what snapshots lack', async () => {
  const f = facade();
  await callTool(f, 'ui_snapshot', {});
  const r = body(await callTool(f, 'ui_get_element', { snapshotId: 's1', refId: 'e9' }));
  assert.equal(r.element.ref, 'e9');
  assert.equal(r.element.nativeId, 'com.android.systemui:id/fake');
  assert.equal(r.element.enabled, true);
  assert.deepEqual(r.element.bounds, { x: 1136, y: 536, width: 56, height: 63 });
  assert.deepEqual(calls().at(-1), { command: 'call', capability: 'ui.node', args: { nodeId: 9 } });
});

test('a ref from an older snapshot fails with STALE_ELEMENT and never reaches Relay', async () => {
  const f = facade();
  await callTool(f, 'ui_snapshot', {});
  await callTool(f, 'ui_snapshot', {});
  const before = calls().length;
  const r = await callTool(f, 'ui_get_element', { snapshotId: 's1', refId: 'e9' });
  assert.equal(r.isError, true);
  assert.equal(body(r).error.code, 'STALE_ELEMENT');
  assert.equal(calls().length, before);
});

test('get_element before any snapshot is STALE_ELEMENT', async () => {
  const r = await callTool(facade(), 'ui_get_element', { snapshotId: 's1', refId: 'e9' });
  assert.equal(body(r).error.code, 'STALE_ELEMENT');
  assert.equal(calls().length, 0);
});

test('a different control under the same node id is STALE_ELEMENT', async () => {
  const f = facade();
  await callTool(f, 'ui_snapshot', {});
  process.env.FAKE_RELAY_MODE = 'changed';
  const r = await callTool(f, 'ui_get_element', { snapshotId: 's1', refId: 'e9' });
  assert.equal(body(r).error.code, 'STALE_ELEMENT');
});

test('a node that disappeared is STALE_ELEMENT, not a generic miss', async () => {
  const f = facade();
  await callTool(f, 'ui_snapshot', {});
  process.env.FAKE_RELAY_MODE = 'node-gone';
  const r = body(await callTool(f, 'ui_get_element', { snapshotId: 's1', refId: 'e9' }));
  assert.equal(r.error.code, 'STALE_ELEMENT');
  assert.equal(r.error.relay.code, 'E_NODE_NOT_FOUND');
});

test('unknown ref in the current snapshot is ELEMENT_NOT_FOUND', async () => {
  const f = facade();
  await callTool(f, 'ui_snapshot', {});
  assert.equal(body(await callTool(f, 'ui_get_element', { snapshotId: 's1', refId: 'e999' })).error.code, 'ELEMENT_NOT_FOUND');
});

test('waiting for approval is retried, then succeeds', async () => {
  process.env.FAKE_RELAY_MODE = 'consent-then-ok';
  const r = await relayRead(FAKE, 'ui.snapshot', {}, { waitMs: 5000, pauseMs: 10 });
  assert.equal(r.ok, true);
  assert.equal(calls().length, 2);
});

test('no answer within the wait is APPROVAL_REQUIRED, with the Relay code attached', async () => {
  process.env.FAKE_RELAY_MODE = 'consent';
  const r = body(await callTool(facade({ approvalWaitMs: 0 }), 'ui_snapshot', {}));
  assert.equal(r.error.code, 'APPROVAL_REQUIRED');
  assert.equal(r.error.relay.code, 'E_AWAITING_CONSENT');
  assert.equal(r.error.relay.exitCode, 5);
});

test('a denial is POLICY_DENIED and is not retried', async () => {
  process.env.FAKE_RELAY_MODE = 'denied';
  const r = body(await callTool(facade(), 'ui_snapshot', {}));
  assert.equal(r.error.code, 'POLICY_DENIED');
  assert.equal(calls().length, 1);
});

test('accessibility off is PROVIDER_UNAVAILABLE', async () => {
  process.env.FAKE_RELAY_MODE = 'no-a11y';
  assert.equal(body(await callTool(facade(), 'ui_snapshot', {})).error.code, 'PROVIDER_UNAVAILABLE');
});

test('a reply without JSON is OBSERVATION_INCOMPLETE, never success', async () => {
  process.env.FAKE_RELAY_MODE = 'garbage';
  const r = await callTool(facade(), 'ui_snapshot', {});
  assert.equal(r.isError, true);
  assert.equal(body(r).error.code, 'OBSERVATION_INCOMPLETE');
});

test('missing relay CLI is PROVIDER_UNAVAILABLE', async () => {
  const r = body(await callTool(facade({ relayCli: join(tmpdir(), 'no-such-relay-cli') }), 'ui_snapshot', {}));
  assert.equal(r.error.code, 'PROVIDER_UNAVAILABLE');
});

test('bad arguments are INVALID_ARGUMENT / TARGET_NOT_FOUND without calling Relay', async () => {
  const f = facade();
  assert.equal(body(await callTool(f, 'ui_snapshot', { maxNodes: 0 })).error.code, 'INVALID_ARGUMENT');
  assert.equal(body(await callTool(f, 'ui_snapshot', { mode: 'tree' })).error.code, 'INVALID_ARGUMENT');
  assert.equal(body(await callTool(f, 'ui_snapshot', { deviceId: 'emulator-5554' })).error.code, 'TARGET_NOT_FOUND');
  assert.equal(body(await callTool(f, 'ui_click', {})).error.code, 'ACTION_UNSUPPORTED');
  assert.equal(calls().length, 0);
});

test('list_devices reports online/offline from relay doctor', async () => {
  assert.equal(body(await callTool(facade(), 'ui_list_devices', {})).devices[0].status, 'online');
  process.env.FAKE_RELAY_MODE = 'doctor-bad';
  const d = body(await callTool(facade(), 'ui_list_devices', {})).devices[0];
  assert.equal(d.status, 'offline');
  assert.equal(d.deviceId, DEVICE_ID);
});

// ---------------------------------------------------------------- guest self-check (relay.selftest)

const CHECK = join(here, '../../main/guest-assets/dshmobile/ui-facade-check.mjs');
const runCheck = (mode) => new Promise((resolve) => {
  const child = spawn(process.execPath, [CHECK, '--relay', FAKE, '--approval-wait-ms', '0'], {
    env: { ...process.env, FAKE_RELAY_MODE: mode },
  });
  let out = '';
  child.stdout.on('data', (b) => { out += b; });
  child.on('close', (code) => resolve({ code, out }));
});

test('self-check passes end to end and reports the snapshot', async () => {
  const { code, out } = await runCheck('ok');
  assert.equal(code, 0, out);
  const summary = JSON.parse(out.split('\n').find((l) => l.startsWith('UICHECK ')).slice(8));
  assert.equal(summary.snapshot.app, 'com.android.systemui');
  assert.equal(summary.element.enabled, true);
});

test('self-check exit codes: 5 waiting for approval, 4 denied', async () => {
  assert.equal((await runCheck('consent')).code, 5);
  assert.equal((await runCheck('denied')).code, 4);
});

// ---------------------------------------------------------------- MCP stdio protocol

test('speaks MCP over stdio: discover falls back, initialize, list, call', async () => {
  const child = spawn(process.execPath, [FACADE, '--relay', FAKE, '--approval-wait-ms', '3000'], { stdio: ['pipe', 'pipe', 'inherit'] });
  const lines = createInterface({ input: child.stdout });
  const pending = new Map();
  lines.on('line', (l) => {
    const msg = JSON.parse(l);
    pending.get(msg.id)?.(msg);
  });
  let next = 1;
  const rpc = (method, params) => new Promise((resolve) => {
    const id = next++;
    pending.set(id, resolve);
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  });
  try {
    const discover = await rpc('server/discover', {});
    assert.equal(discover.error.code, -32601);
    const init = await rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 't', version: '1' } });
    assert.equal(init.result.protocolVersion, '2025-06-18');
    assert.equal(init.result.serverInfo.name, 'dshmobile-ui');
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
    const unknownVersion = await rpc('initialize', { protocolVersion: '1999-01-01', capabilities: {} });
    assert.equal(unknownVersion.result.protocolVersion, '2025-06-18');
    const list = await rpc('tools/list', {});
    assert.equal(list.result.tools.length, 4);
    const snap = await rpc('tools/call', { name: 'ui_snapshot', arguments: {} });
    assert.equal(snap.result.isError, undefined);
    assert.equal(snap.result.structuredContent.snapshotId, 's1');
    assert.equal((await rpc('ping', {})).result && true, true);
  } finally {
    child.kill();
    rmSync(dirname(logFile), { recursive: true, force: true });
  }
});
