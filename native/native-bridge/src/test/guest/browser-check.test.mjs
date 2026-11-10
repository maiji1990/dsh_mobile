// Tests for the guest browser self-check (TASK-011). The real adapter client is replaced by a fake staged copy
// whose browserRequest() returns canned results shaped like the adapter's resultFrom().
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const CHECK = join(here, '../../main/guest-assets/dshmobile/browser-check.mjs');

/** Builds a fake stage dir; `script` is the body of browserRequest(method, args, envelope). */
function fakeStage(script) {
  const stage = mkdtempSync(join(tmpdir(), 'browser-stage-'));
  mkdirSync(join(stage, 'plugin/lib'), { recursive: true });
  const log = join(stage, 'calls.log');
  writeFileSync(join(stage, 'plugin/lib/client.js'), `
const fs = require('node:fs');
const r = (o) => Object.assign({ ok: false, code: 'UNKNOWN', text: '', requestId: 'r', executed: 'false',
  sessionGeneration: 0, controlGeneration: 0, sessionGenerationAvailable: false, controlGenerationAvailable: false,
  state: '', dataJson: '{}', artifactPath: '' }, o);
exports.browserRequest = async (method, args, envelope) => {
  fs.appendFileSync(${JSON.stringify(log)}, JSON.stringify({ method, args, envelope }) + '\\n');
  ${script}
};`);
  return { stage, calls: () => (existsSync(log) ? readFileSync(log, 'utf8').split('\n').filter(Boolean).map((l) => JSON.parse(l)) : []) };
}

const run = (stage) => new Promise((resolve) => {
  const child = spawn(process.execPath, [CHECK, stage]);
  let out = '';
  child.stdout.on('data', (b) => { out += b; });
  child.on('close', (code) => resolve({ code, out, summary: JSON.parse(out.split('\n').find((l) => l.startsWith('BROWSERCHECK ')).slice(13)) }));
});

test('status + observe of an open page: exit 0, read-only methods only', async () => {
  const { stage, calls } = fakeStage(`
    if (method === 'status') return r({ ok: true, code: 'OK', state: 'RUNNING', sessionGeneration: 7, sessionGenerationAvailable: true, dataJson: '{"automationEnabled":true}' });
    if (method === 'observe') return r({ ok: true, code: 'OK', executed: 'true', dataJson: JSON.stringify({ url: 'https://example.com/', title: 'Example Domain', text: 'Example Domain body', elements: [{}, {}] }) });
    return r({ code: 'UNEXPECTED' });`);
  const { code, summary } = await run(stage);
  assert.equal(code, 0);
  assert.equal(summary.observe.title, 'Example Domain');
  assert.equal(summary.observe.elements, 2);
  const c = calls();
  assert.deepEqual(c.map((x) => x.method), ['status', 'observe']);
  assert.deepEqual(c[1].envelope, { sessionGeneration: 7 }, 'observe uses the session generation from status and never claims control');
  assert.deepEqual(c[1].args, { maxTextChars: 400 });
});

test('no open session: exit 2 and observe is not attempted', async () => {
  const { stage, calls } = fakeStage(`return r({ ok: true, code: 'OK', state: 'CLOSED' });`);
  const { code } = await run(stage);
  assert.equal(code, 2);
  assert.deepEqual(calls().map((x) => x.method), ['status']);
});

test('closed browser that still reports its last generation: exit 2, observe not attempted', async () => {
  // Device reply after idle_timeout: state CLOSED but sessionGeneration 2.
  const { stage, calls } = fakeStage(`return r({ ok: true, code: 'OK', state: 'CLOSED', sessionGeneration: 2, sessionGenerationAvailable: true, dataJson: '{"lastCloseReason":"idle_timeout"}' });`);
  assert.equal((await run(stage)).code, 2);
  assert.deepEqual(calls().map((x) => x.method), ['status']);
});

test('session gone between status and observe (NO_ACTIVE_SESSION): exit 2', async () => {
  const { stage } = fakeStage(`
    if (method === 'status') return r({ ok: true, code: 'OK', state: 'IDLE', sessionGeneration: 2, sessionGenerationAvailable: true });
    return r({ code: 'NO_ACTIVE_SESSION' });`);
  assert.equal((await run(stage)).code, 2);
});

test('broker unavailable: exit 3', async () => {
  const { stage } = fakeStage(`return r({ code: 'BROKER_UNAVAILABLE' });`);
  assert.equal((await run(stage)).code, 3);
});

test('observe refused (automation off): exit 2, reported, not success', async () => {
  const { stage } = fakeStage(`
    if (method === 'status') return r({ ok: true, code: 'OK', state: 'RUNNING', sessionGeneration: 3, sessionGenerationAvailable: true });
    return r({ code: 'AUTOMATION_DISABLED', text: 'Browser automation is off.' });`);
  const { code, summary } = await run(stage);
  assert.equal(code, 2);
  assert.equal(summary.observe.ok, false);
  assert.equal(summary.observe.code, 'AUTOMATION_DISABLED');
});

test('missing adapter client: exit 6 (the check itself failed)', async () => {
  const empty = mkdtempSync(join(tmpdir(), 'browser-stage-empty-'));
  assert.equal((await run(empty)).code, 6);
});
