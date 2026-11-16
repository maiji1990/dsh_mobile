// Tests for the guest Agent E2E driver (vNext PR-07) against fake-dsh.mjs. The real-DSH run is
// tooling/runtime-smoke.sh (no credential in CI -> "needs-credential") and the owner's device run (docs/TEST_PLAN.md).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

import { redact, isCredentialFailure, parseArgs, overall, textsOf } from '../../src/DshMobile.Runtime/Guest/Assets/dsh-e2e.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const E2E = join(here, '../../src/DshMobile.Runtime/Guest/Assets/dsh-e2e.mjs');
const FAKE = join(here, 'fake-dsh.mjs');

function runE2e(mode, extra = []) {
  const dir = mkdtempSync(join(tmpdir(), 'dsh-e2e-'));
  const ws = join(dir, 'ws');
  const r = spawnSync(process.execPath, [E2E, '--workspace', ws, '--turn-timeout-ms', '20000', ...extra], {
    encoding: 'utf8',
    env: { ...process.env, DSHE2E_DSH_BIN: FAKE, DSH_HOME: join(dir, 'home'), FAKE_DSH_MODE: mode },
    timeout: 60_000,
  });
  const line = r.stdout.split('\n').find((l) => l.startsWith('DSHE2E '));
  assert.ok(line, `no DSHE2E line; stdout=${r.stdout} stderr=${r.stderr}`);
  return { code: r.status, raw: line, report: JSON.parse(line.slice('DSHE2E '.length)), ws };
}
const failedChecks = (s) => s.checks.filter((c) => !c.ok).map((c) => c.name);

// ---------------------------------------------------------------- pure helpers

test('redacts key, bearer and token shapes but keeps ordinary text', () => {
  assert.equal(redact('key sk-abcdef0123456789xyz end'), 'key sk-ab…<redacted> end');
  assert.equal(redact('Authorization: Bearer abc.def-123456'), 'Authorization: Bearer <redacted>');
  assert.equal(redact('{"api_key":"hunter2hunter2"}'), '{"api_key":"<redacted>"}');
  assert.equal(redact('DEEPSEEK_API_KEY=xyz12345'), 'DEEPSEEK_API_KEY=<redacted>');
  assert.equal(redact('wrote e2e-acp.txt = "E2E0A1B2C3D4E5F6"'), 'wrote e2e-acp.txt = "E2E0A1B2C3D4E5F6"');
});

test('classifies credential failures, not other failures', () => {
  for (const t of ['401 Unauthorized', 'invalid API key', 'missing api_key for provider', 'Authentication failed', 'no model configured']) {
    assert.ok(isCredentialFailure(t), t);
  }
  for (const t of ['ECONNREFUSED', 'timeout; stderr: ', 'exit=1 SyntaxError', undefined]) assert.ok(!isCredentialFailure(t), String(t));
});

test('parses arguments and refuses bad ones', () => {
  assert.deepEqual(parseArgs([]).suites, ['headless', 'sdk', 'acp']);
  const o = parseArgs(['--suites', 'acp,sdk', '--workspace', '/w', '--model', 'm', '--turn-timeout-ms', '5']);
  assert.deepEqual([o.suites, o.workspace, o.model, o.turnTimeoutMs], [['acp', 'sdk'], '/w', 'm', 5]);
  assert.throws(() => parseArgs(['--suites', 'web']), /unknown suite/);
  assert.throws(() => parseArgs(['--workspace', 'rel']), /absolute/);
  assert.throws(() => parseArgs(['--turn-timeout-ms', '0']), /positive/);
  assert.throws(() => parseArgs(['--bogus']), /unknown argument/);
});

test('overall status never turns missing work into a pass', () => {
  assert.equal(overall([]), 'fail');
  assert.equal(overall([{ status: 'pass' }, { status: 'needs-credential' }]), 'needs-credential');
  assert.equal(overall([{ status: 'fail' }, { status: 'needs-credential' }]), 'fail');
  assert.equal(overall([{ status: 'pass' }]), 'pass');
  assert.deepEqual(textsOf({ a: [{ type: 'text', text: 'x' }, { content: { type: 'text', text: 'y' } }] }), ['x', 'y']);
});

// ---------------------------------------------------------------- end to end against the fake

test('all three suites pass when the agent really does the work and resumes', () => {
  const { code, report, ws } = runE2e('ok');
  assert.equal(report.status, 'pass', JSON.stringify(report.suites.map((s) => [s.suite, failedChecks(s)])));
  assert.equal(code, 0);
  assert.deepEqual(report.suites.map((s) => s.suite), ['headless', 'sdk', 'acp']);
  for (const s of report.suites) {
    assert.ok(s.checks.some((c) => c.name === `${s.suite}:resume:recall` && c.ok), s.suite);
    assert.ok(existsSync(join(ws, `e2e-${s.suite}.txt`)));
  }
  assert.match(report.suites[2].checks.find((c) => c.name === 'acp:tool').detail, /permission requests=1 \(answered allow-once\)/);
  assert.equal(report.versions.dsh, 'dsh 0.0.0-fake');
  assert.equal(report.workspace, ws);
});

test('no credential is reported as needs-credential (exit 3), never pass, and the key is redacted', () => {
  const { code, raw, report } = runE2e('no-credential');
  assert.equal(report.status, 'needs-credential');
  assert.equal(code, 3);
  for (const s of report.suites) assert.equal(s.status, 'needs-credential', `${s.suite}: ${failedChecks(s)}`);
  assert.ok(!raw.includes('sk-fakeabcdef0123456789'), 'raw key leaked');
  assert.ok(raw.includes('sk-fa…<redacted>'));
});

test('an agent that claims success without writing the file fails the file check in every suite', () => {
  const { code, report } = runE2e('lie');
  assert.equal(report.status, 'fail');
  assert.equal(code, 1);
  for (const s of report.suites) assert.ok(failedChecks(s).includes(`${s.suite}:file`), s.suite);
});

test('a session that cannot recall after a process restart fails resume:recall', () => {
  const { report } = runE2e('forget', ['--suites', 'acp,headless']);
  assert.equal(report.status, 'fail');
  for (const s of report.suites) assert.deepEqual(failedChecks(s), [`${s.suite}:resume:recall`]);
});

test('usage errors exit 2 with a DSHE2E line', () => {
  const r = spawnSync(process.execPath, [E2E, '--suites', 'nope'], { encoding: 'utf8' });
  assert.equal(r.status, 2);
  assert.match(r.stdout, /^DSHE2E .*"usage: unknown suite/m);
});
