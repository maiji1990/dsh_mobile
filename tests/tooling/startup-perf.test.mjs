// Startup performance tooling: log parsing, nearest-rank percentiles, fixed CSV columns, derived groups and the
// CLI contract of tooling/startup-perf.mjs. The privacy assertions here are regressions: only the 12 documented
// columns may leave the tool, and details other than cacheHit must never reach CSV or stdout.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import {
  aggregate,
  CSV_COLUMNS,
  formatSummary,
  parseLog,
  percentile,
  toCsv,
} from '../../tooling/startup-perf.mjs';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const tool = join(repo, 'tooling', 'startup-perf.mjs');
const HEADER = 'runId,scenario,device,android,apkCommit,nodeVersion,dshVersion,stage,elapsedMs,sinceStartMs,result,cacheHit';

function record(stage, elapsedMs, extra = {}) {
  return {
    schema: 1,
    runId: 'a1b2c3d4e5f6',
    stage,
    elapsedMs,
    sinceStartMs: elapsedMs,
    result: 'ok',
    details: {},
    ...extra,
  };
}

function threadtimeLine(value) {
  return '11-17 16:31:42.123  1234  1250 I DshMobile: 16:31:42.123 startup-perf ' + JSON.stringify(value);
}

function runRecords(runId, stages, extra = {}) {
  return Object.entries(stages).map(([stage, elapsedMs]) => ({
    runId,
    stage,
    elapsedMs,
    sinceStartMs: elapsedMs,
    result: 'ok',
    ...extra,
  }));
}

function sampleLog() {
  return [
    '11-17 16:31:42.000  1234  1234 I ActivityManager: Start proc org.example.dshmobile',
    threadtimeLine(record('guest_assets.total', 200, { details: { cacheHit: false, written: 3 } })),
    '16:31:42.200 startup-perf ' + JSON.stringify(record('http.first_response', 300, { runId: 'run-one' })),
    'startup-perf ' + JSON.stringify(record('host.ready', 500, { runId: 'run-one' })),
    threadtimeLine(record('bridge.complete', 1000, { runId: 'run-one' })),
    'startup-perf {broken json}',
    '',
  ].join('\n');
}

function cli(args, options = {}) {
  return spawnSync(process.execPath, [tool, ...args], { encoding: 'utf8', ...options });
}

test('parses threadtime, App timestamp and bare startup-perf prefixes, including suffixes', () => {
  const log = [
    threadtimeLine(record('guest_assets.total', 23.456)),
    '16:31:42.123 startup-perf ' + JSON.stringify(record('bridge.complete', 900)) + ' [tail]',
    'startup-perf ' + JSON.stringify(record('host.ready', 800)),
  ].join('\n');
  const parsed = parseLog(log);
  assert.equal(parsed.skipped, 0);
  assert.deepEqual(parsed.records.map((r) => r.stage), ['guest_assets.total', 'bridge.complete', 'host.ready']);
  assert.equal(parsed.records[0].elapsedMs, 23.456);
  assert.equal(parsed.records[1].runId, 'a1b2c3d4e5f6');
});

test('ignores unrelated log lines and keeps records that sit between them', () => {
  const log = [
    '11-17 16:31:41.000  1234  1234 I ActivityManager: unrelated',
    threadtimeLine(record('guest_assets.total', 10)),
    '11-17 16:31:42.000  1234  1250 W DshMobile: an unrelated warning line',
    '11-17 16:31:43.000  1234  1250 I DshMobile: startup-perf ' + JSON.stringify(record('bridge.complete', 20)),
    'plain text without a marker',
  ].join('\n');
  const parsed = parseLog(log);
  assert.equal(parsed.records.length, 2);
  assert.equal(parsed.skipped, 0);
});

test('skips corrupt JSON, non-schema-1 records and a marker without JSON, and counts them', () => {
  const log = [
    'startup-perf {not valid json',
    'startup-perf ' + JSON.stringify({ ...record('bridge.complete', 1), schema: 2 }),
    '11-17 16:31:42.123  1234  1250 I DshMobile: startup-perf ',
    'startup-perf ' + JSON.stringify(record('guest_assets.total', 7)),
  ].join('\n');
  const parsed = parseLog(log);
  assert.equal(parsed.records.length, 1);
  assert.equal(parsed.skipped, 3);
  assert.equal(parsed.records[0].stage, 'guest_assets.total');
});

test('parseLog drops details except the boolean cacheHit (privacy regression)', () => {
  const line = 'startup-perf ' + JSON.stringify(record('guest_assets.total', 5, {
    details: { cacheHit: true, code: 'BUNDLE_IMPORT_FAILED', probes: 3, stream: 'stderr', written: 9 },
  }));
  const { records } = parseLog(line);
  assert.equal(records.length, 1);
  assert.equal(records[0].cacheHit, true);
  assert.equal('details' in records[0], false);
  const serialized = JSON.stringify(records[0]);
  assert.ok(!serialized.includes('BUNDLE_IMPORT_FAILED'));
  assert.ok(!serialized.includes('probes'));
  assert.ok(!serialized.includes('stderr'));
});

test('percentile uses nearest rank for odd, even and single samples', () => {
  assert.equal(percentile([10], 0.5), 10);
  assert.equal(percentile([10], 0.95), 10);
  assert.equal(percentile([1, 2, 3], 0.5), 2);
  assert.equal(percentile([1, 2, 3], 0.95), 3);
  assert.equal(percentile([1, 2, 3, 4], 0.5), 2);
  assert.equal(percentile([1, 2, 3, 4], 0.95), 4);
  assert.equal(percentile([1, 2, 3, 4, 5], 0.5), 3);
  assert.equal(percentile([1, 2, 3, 4, 5], 0.95), 5);
  assert.equal(percentile([], 0.5), null);
});

test('CSV has the fixed column order and metadata columns', () => {
  const meta = {
    scenario: 'A',
    device: 'Pixel 8',
    android: '17',
    apkCommit: 'deadbeef',
    nodeVersion: 'v26.13.1',
    dshVersion: '0.2.0',
  };
  const records = [
    { runId: 'r1', stage: 'host.ready', elapsedMs: 1234.5, sinceStartMs: 1234.5, result: 'ok', cacheHit: true },
    { runId: 'r1', stage: 'guest_assets.total', elapsedMs: 10, sinceStartMs: 20, result: 'failed', cacheHit: false },
    { runId: 'r2', stage: 'sandbox.spawn', elapsedMs: 3, sinceStartMs: 4, result: 'canceled', cacheHit: undefined },
  ];
  const rows = toCsv(records, meta).trimEnd().split('\n');
  assert.deepEqual(CSV_COLUMNS, HEADER.split(','));
  assert.equal(rows[0], HEADER);
  assert.equal(rows.length, 4);
  assert.deepEqual(rows[1].split(','), ['r1', 'A', 'Pixel 8', '17', 'deadbeef', 'v26.13.1', '0.2.0', 'host.ready', '1234.5', '1234.5', 'ok', 'true']);
  assert.equal(rows[2].split(',').at(-1), 'false');
  assert.equal(rows[3].split(',').at(-1), '');
  for (const row of rows) assert.equal(row.split(',').length, 12);
});

test('CSV never contains cacheHit when it is not a boolean, nor details code/probes (privacy regression)', () => {
  const records = [
    { runId: 'r1', stage: 'guest_assets.total', elapsedMs: 1, sinceStartMs: 1, result: 'ok', cacheHit: 'true', details: { code: 'TOKEN_MISSING', probes: 7 } },
    { runId: 'r1', stage: 'sandbox.spawn', elapsedMs: 2, sinceStartMs: 2, result: 'failed', cacheHit: 1, details: { stream: 'stderr' } },
  ];
  const csv = toCsv(records, {});
  assert.ok(!csv.includes('TOKEN_MISSING'));
  assert.ok(!csv.includes('probes'));
  assert.ok(!csv.includes('stderr'));
  assert.ok(!csv.includes('true,'));
  assert.equal(csv.trimEnd().split('\n')[1].split(',').at(-1), '');
});

test('derived groups follow the documented per-runId formulas', () => {
  const stages = {
    'coordinator.lock_wait': 5,
    'coordinator.sync': 10,
    'coordinator.journal': 2,
    'guest_assets.total': 100,
    'sandbox.spawn': 20,
    'sandbox.fixed_delay': 30,
    'host.proc_scan': 4,
    'host.cleanup_processes': 6,
    'host.lock_sweep': 1,
    'http.first_response': 200,
    'host.ready': 300,
    'bridge.complete': 500,
    'web.activity_create': 12,
    'web.page_finish': 400,
  };
  const agg = aggregate(runRecords('run-1', stages));
  const group = (name) => agg.groups.find((g) => g.name === name);
  assert.equal(group('coordinator').p50, 17);
  assert.equal(group('guest_assets').p50, 100);
  assert.equal(group('sandbox').p50, 50);
  assert.equal(group('cleanup').p50, 11);
  assert.equal(group('node_init').p50, 200);
  assert.equal(group('http_wait').p50, 100);
  assert.equal(group('host_total').p50, 500);
  assert.equal(group('host_total').share, 100);
  assert.ok(Math.abs(group('node_init').share - 40) < 1e-9);
  assert.equal(agg.web.length, 2);
  assert.ok(agg.web.some((stage) => stage.name === 'web.page_finish' && stage.p95 === 400));
  assert.ok(!agg.groups.some((g) => g.name.startsWith('web.')));
  assert.deepEqual(agg.consistency[0].warnings, []);
});

test('missing group inputs are skipped instead of producing NaN', () => {
  const agg = aggregate(runRecords('partial', {
    'coordinator.lock_wait': 5,
    'coordinator.sync': 10,
    'http.first_response': 50,
    'bridge.complete': 100,
  }));
  const group = (name) => agg.groups.find((g) => g.name === name);
  assert.equal(group('coordinator').n, 0);
  assert.equal(group('coordinator').p50, null);
  assert.equal(group('coordinator').p95, null);
  assert.equal(group('coordinator').share, null);
  assert.equal(group('http_wait').p50, null);
  assert.equal(group('host_total').p50, 100);
  const text = formatSummary(agg);
  assert.doesNotMatch(text, /NaN/);
  assert.match(text, /—/);
});

test('consistency warns when the group sum exceeds host_total * 1.05', () => {
  const agg = aggregate(runRecords('oversum', {
    'bridge.complete': 100,
    'coordinator.lock_wait': 10,
    'coordinator.sync': 20,
    'coordinator.journal': 30,
    'guest_assets.total': 60,
  }));
  assert.equal(agg.consistency[0].runId, 'oversum');
  assert.equal(agg.consistency[0].warnings.length, 1);
  assert.match(agg.consistency[0].warnings[0], /group sum 120\.0 ms > host_total 100\.0 ms/);
  const text = formatSummary(agg);
  assert.match(text, /oversum\s+warning:/);
  assert.doesNotMatch(text, /\(no inconsistency\)/);
});

test('consistency warns about a run without bridge.complete', () => {
  const agg = aggregate(runRecords('no-host-total', { 'guest_assets.total': 10 }));
  assert.deepEqual(agg.consistency[0].warnings, ['missing bridge.complete']);
  assert.match(formatSummary(agg), /no-host-total\s+warning: missing bridge\.complete/);
});

test('candidate screening flags >=30% share and P95 over 2000 ms, and says it is not an SLA', () => {
  const agg = aggregate([
    ...runRecords('share-run', {
      'bridge.complete': 1000,
      'coordinator.lock_wait': 100,
      'coordinator.sync': 150,
      'coordinator.journal': 100,
    }),
    ...runRecords('slow-run', {
      'bridge.complete': 100000,
      'guest_assets.total': 2500,
    }),
  ]);
  const group = (name) => agg.groups.find((g) => g.name === name);
  assert.equal(group('coordinator').candidate, true);
  assert.equal(group('guest_assets').candidate, true);
  assert.ok(agg.candidates.some((candidate) => candidate.name === 'coordinator'));
  assert.ok(agg.candidates.some((candidate) => candidate.name === 'guest_assets'));
  const text = formatSummary(agg);
  assert.match(text, /candidate/);
  assert.match(text, /not an SLA/);
});

test('CLI --out writes CSV to the file, keeps stdout empty and sends the summary to stderr', () => {
  const dir = mkdtempSync(join(tmpdir(), 'dshm-startup-perf-'));
  try {
    const log = join(dir, 'sample.log');
    const out = join(dir, 'out.csv');
    writeFileSync(log, sampleLog());
    const result = cli([
      '--log', log, '--out', out, '--scenario', 'A', '--device', 'Pixel 8', '--android', '17',
      '--commit', 'deadbeef', '--node', 'v26.13.1', '--dsh', '0.2.0',
    ]);
    assert.equal(result.status, 0, result.stderr);
    assert.equal(result.stdout, '');
    assert.match(result.stderr, /startup-perf summary/);
    assert.match(result.stderr, /\[stages\]/);
    assert.match(result.stderr, /skipped lines: 1/);
    const csv = readFileSync(out, 'utf8');
    const rows = csv.trimEnd().split('\n');
    assert.equal(rows[0], HEADER);
    assert.equal(rows.length, 5);
    assert.match(rows[1], /Pixel 8/);
    assert.ok(csv.includes('guest_assets.total'));
    assert.ok(!csv.includes('broken json'));
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('CLI without --out writes CSV only to stdout and the summary to stderr', () => {
  const dir = mkdtempSync(join(tmpdir(), 'dshm-startup-perf-'));
  try {
    const log = join(dir, 'sample.log');
    writeFileSync(log, sampleLog());
    const result = cli(['--log', log]);
    assert.equal(result.status, 0, result.stderr);
    const lines = result.stdout.trimEnd().split('\n');
    assert.equal(lines[0], HEADER);
    assert.equal(lines.length, 5);
    assert.ok(!result.stdout.includes('startup-perf summary'));
    assert.match(result.stderr, /startup-perf summary/);
    assert.match(result.stderr, /rule: a group is a candidate/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('CLI accepts stdin with --log -', () => {
  const result = cli(['--log', '-'], { input: sampleLog() });
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stdout.trimEnd().split('\n')[0], HEADER);
  assert.match(result.stderr, /records: 4/);
});

test('CLI exits 1 when the log has no startup-perf records', () => {
  const dir = mkdtempSync(join(tmpdir(), 'dshm-startup-perf-'));
  try {
    const log = join(dir, 'empty.log');
    writeFileSync(log, 'no records here\n');
    const result = cli(['--log', log]);
    assert.equal(result.status, 1);
    assert.equal(result.stdout, '');
    assert.match(result.stderr, /no startup-perf records/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('CLI rejects unknown arguments, missing --log and a missing --out directory with exit 2', () => {
  const dir = mkdtempSync(join(tmpdir(), 'dshm-startup-perf-'));
  try {
    const log = join(dir, 'sample.log');
    writeFileSync(log, sampleLog());

    const unknown = cli(['--log', log, '--bogus']);
    assert.equal(unknown.status, 2);
    assert.match(unknown.stderr, /unknown argument: --bogus/);
    assert.match(unknown.stderr, /usage:/);

    const missing = cli([]);
    assert.equal(missing.status, 2);
    assert.match(missing.stderr, /--log is required/);
    assert.match(missing.stderr, /usage:/);

    const missingOut = cli(['--log', log, '--out', join(dir, 'does-not-exist', 'out.csv')]);
    assert.equal(missingOut.status, 2);
    assert.match(missingOut.stderr, /output directory does not exist/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('CLI --help prints usage to stdout and exits 0', () => {
  const result = cli(['--help']);
  assert.equal(result.status, 0);
  assert.match(result.stdout, /usage:/);
  assert.equal(result.stderr, '');
});
