// Startup experiment report: JSONL parsing, endpoint rules, nearest-rank statistics, pairing, E4 cache provenance,
// the fixed 22-column CSV and the CLI contract of tooling/startup-experiment-report.mjs.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import {
  CSV_COLUMNS,
  comparePair,
  formatSummary,
  parseJsonl,
  percentile,
  summarize,
  toCsv,
  toMatrix,
} from '../../tooling/startup-experiment-report.mjs';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const tool = join(repo, 'tooling', 'startup-experiment-report.mjs');
const RUN = 'a1b2c3d4e5f6';
const HEADER = 'device,android,apk,node,dsh,experiment,variant,kind,iteration,runId,durationMs,spawnMs,guestFirstMarkerMs,httpReadyMs,httpStatus,exitCode,timedOut,result,cacheMode,cacheFileCount,cacheBytes,startedAt';

function kindOf(variant) {
  return variant.startsWith('E3') || variant.startsWith('E4') ? 'web' : 'exit';
}

function rec(variant, durationMs = 100, extra = {}) {
  const kind = kindOf(variant);
  return {
    schema: 1,
    experiment: variant.slice(0, 2),
    variant,
    kind,
    runId: RUN,
    iteration: 1,
    durationMs,
    spawnMs: 5,
    guestFirstMarkerMs: null,
    httpReadyMs: kind === 'web' ? durationMs : null,
    httpStatus: kind === 'web' ? 401 : null,
    userCpuMs: null,
    systemCpuMs: null,
    peakRssKb: null,
    exitCode: kind === 'exit' ? 0 : null,
    timedOut: false,
    result: 'ok',
    cacheMode: 'unknown',
    cacheFileCount: null,
    cacheBytes: null,
    startedAt: '2026-11-18T02:03:04.567Z',
    note: null,
    ...extra,
  };
}

function jsonl(values) {
  return values.map((value) => (typeof value === 'string' ? value : JSON.stringify(value))).join('\n') + '\n';
}

function csvRows(csv) {
  return csv.trimEnd().split('\n').map((row) => row.split(','));
}

function cli(args, options = {}) {
  return spawnSync(process.execPath, [tool, ...args], { encoding: 'utf8', ...options });
}

test('parseJsonl skips bad JSON, schema!=1 and unknown variants, and ignores blank lines', () => {
  const text = jsonl([
    rec('E1-0', 10),
    '{broken',
    rec('E1-1', 20, { schema: 2 }),
    rec('E1-1', 20, { variant: 'E9-0' }),
    '',
    '   ',
  ]);
  const parsed = parseJsonl(text);
  assert.equal(parsed.records.length, 1);
  assert.equal(parsed.records[0].variant, 'E1-0');
  assert.equal(parsed.skipped, 3);
  assert.equal(parsed.skipReasons['bad-json'], 1);
  assert.equal(parsed.skipReasons.schema, 1);
  assert.equal(parsed.skipReasons.variant, 1);
  assert.equal(parsed.blankLines, 3);
});

test('parseJsonl derives missing experiment/kind from the catalogue and keeps absent fields null', () => {
  const parsed = parseJsonl(jsonl([
    { schema: 1, variant: 'E1-0', result: 'refused' },
  ]));
  assert.equal(parsed.records.length, 1);
  const record = parsed.records[0];
  assert.equal(record.experiment, 'E1');
  assert.equal(record.kind, 'exit');
  assert.equal(record.runId, null);
  assert.equal(record.iteration, null);
  assert.equal(record.durationMs, null);
  assert.equal(record.timedOut, null);
  assert.equal(record.cacheMode, null);
  assert.equal(record.cacheFileCount, null);
  assert.equal(record.startedAt, null);
});

test('summarize accepts already-extracted records without schema and still skips schema 2', () => {
  const summary = summarize([
    { experiment: 'E1', variant: 'E1-0', kind: 'exit', runId: RUN, iteration: 1, durationMs: 10, result: 'ok' },
    { schema: 2, variant: 'E1-0', result: 'ok', durationMs: 20 },
  ]);
  assert.equal(summary.recordCount, 1);
  assert.equal(summary.skipped, 1);
  assert.equal(summary.groups[0].p50, 10);
});

test('refused and setup-error rows stay in attempts but never in duration statistics', () => {
  const { records } = parseJsonl(jsonl([
    rec('E1-2', 100, { iteration: 1 }),
    rec('E1-2', 5, { iteration: 2, result: 'refused' }),
    rec('E1-2', 6, { iteration: 3, result: 'setup-error' }),
    rec('E1-2', 7, { iteration: 4, result: 'failed' }),
  ]));
  const summary = summarize(records);
  const group = summary.groups.find((item) => item.variant === 'E1-2');
  assert.equal(group.attempts, 4);
  assert.equal(group.successes, 1);
  assert.equal(group.p50, 100);
  assert.equal(group.p95, 100);
  assert.equal(summary.quality.notOk, 3);
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

test('variant statistics use only result=ok durations and round P50/P95 to one decimal', () => {
  const values = [10.04, 20.06, 30.01, 40.09, 50, 60, 70, 80, 90, 100];
  const records = values.map((value, index) => rec('E1-0', value, { iteration: index + 1 }));
  const summary = summarize(records);
  const group = summary.groups.find((item) => item.variant === 'E1-0');
  assert.equal(group.attempts, 10);
  assert.equal(group.successes, 10);
  assert.equal(group.p50, 50);
  assert.equal(group.p95, 100);
  assert.equal(group.min, 10);
  assert.equal(group.max, 100);
  assert.equal(group.insufficient, false);
  assert.match(formatSummary(summary), /E1-0\s+exit\s+10\/10\s+50\.0\s+100\.0/);
});

test('fewer than 10 successful samples prints insufficient samples', () => {
  const records = Array.from({ length: 9 }, (_, index) => rec('E1-0', index + 1, { iteration: index + 1 }));
  const summary = summarize(records);
  const group = summary.groups.find((item) => item.variant === 'E1-0');
  assert.equal(group.successes, 9);
  assert.equal(group.insufficient, true);
  assert.deepEqual(summary.quality.insufficient, [{ variant: 'E1-0', successes: 9, attempts: 9 }]);
  const text = formatSummary(summary);
  assert.match(text, /insufficient samples/);
  assert.match(text, /E1-0 \(9\/9\)/);
});

test('missing field counts are reported for accepted rows', () => {
  const { records } = parseJsonl(jsonl([
    { schema: 1, variant: 'E1-0', runId: RUN, result: 'refused' },
    rec('E1-1', 100),
  ]));
  const summary = summarize(records);
  assert.equal(summary.quality.missing.durationMs, 1);
  assert.equal(summary.quality.missing.iteration, 1);
  assert.equal(summary.quality.missing.cacheFileCount, 2);
  assert.ok(summary.quality.missingTotal > 0);
  assert.match(formatSummary(summary), /missing fields: .*durationMs=1/);
});

test('paired differences subtract second minus first only for matching iterations', () => {
  const records = [
    rec('E1-0', 10, { iteration: 1 }),
    rec('E1-0', 20, { iteration: 2 }),
    rec('E1-0', 30, { iteration: 3 }),
    rec('E1-1', 15, { iteration: 1 }),
    rec('E1-1', 27, { iteration: 2 }),
    rec('E1-1', 36, { iteration: 3 }),
  ];
  const summary = summarize(records);
  const pair = summary.pairs.find((item) => item.a === 'E1-0' && item.b === 'E1-1');
  assert.equal(pair.comparable, true);
  assert.equal(pair.reason, null);
  assert.equal(pair.n, 3);
  assert.equal(pair.p50Diff, 6);
  assert.equal(pair.p95Diff, 7);
  const text = formatSummary(summary);
  assert.match(text, /E1-0 vs E1-1: paired, same endpoint; n=3; ΔP50=\+6\.0 ms; ΔP95=\+7\.0 ms/);
});

test('unpaired iterations are intersected, the pair count n is reported, and non-overlap is not comparable', () => {
  const records = [
    rec('E1-0', 10, { iteration: 1 }),
    rec('E1-0', 20, { iteration: 2 }),
    rec('E1-0', 30, { iteration: 3 }),
    rec('E1-1', 22, { iteration: 2 }),
    rec('E1-1', 33, { iteration: 3 }),
    rec('E1-1', 99, { iteration: 9 }),
  ];
  const summary = summarize(records);
  const pair = summary.pairs.find((item) => item.a === 'E1-0' && item.b === 'E1-1');
  assert.equal(pair.comparable, true);
  assert.equal(pair.n, 2);
  assert.equal(pair.p50Diff, 2);
  assert.equal(pair.p95Diff, 3);

  const disjoint = summarize([
    rec('E1-0', 10, { iteration: 1 }),
    rec('E1-1', 20, { iteration: 2 }),
  ]);
  const noOverlap = disjoint.pairs.find((item) => item.a === 'E1-0' && item.b === 'E1-1');
  assert.equal(noOverlap.comparable, false);
  assert.equal(noOverlap.reason, 'no paired iterations');
  assert.match(formatSummary(disjoint), /E1-0 vs E1-1: not comparable \(no paired iterations\)/);
});

test('different endpoints are never subtracted and read not comparable', () => {
  const exitRecord = parseJsonl(jsonl([rec('E1-2', 100)])).records[0];
  const webRecord = parseJsonl(jsonl([rec('E3-0', 1500)])).records[0];
  const comparison = comparePair(
    { experiment: 'E1', kind: 'exit', variant: 'E1-2', records: [exitRecord] },
    { experiment: 'E3', kind: 'web', variant: 'E3-0', records: [webRecord] },
  );
  assert.equal(comparison.comparable, false);
  assert.equal(comparison.reason, 'different endpoint');
  assert.equal(comparison.p50Diff, null);

  const summary = summarize([
    rec('E2-3', 200, { iteration: 1 }),
    rec('E3-P', 1600, { iteration: 1 }),
  ]);
  const boundary = summary.pairs.find((item) => item.a === 'E2-3' && item.b === 'E3-P');
  assert.equal(boundary.comparable, false);
  assert.equal(boundary.reason, 'different endpoint');
  assert.match(formatSummary(summary), /E2-3 vs E3-P: not comparable \(different endpoint \(exit vs web\)\)/);
});

test('different experiments with the same endpoint read not comparable', () => {
  const summary = summarize([
    rec('E1-3', 100, { iteration: 1 }),
    rec('E2-0', 120, { iteration: 1 }),
  ]);
  const boundary = summary.pairs.find((item) => item.a === 'E1-3' && item.b === 'E2-0');
  assert.equal(boundary.comparable, false);
  assert.equal(boundary.reason, 'different experiment');
  assert.match(formatSummary(summary), /E1-3 vs E2-0: not comparable \(different experiment\)/);
});

test('E1/E2 screening states the 1000 ms threshold and says it is screening, not causation', () => {
  const records = [
    rec('E1-2', 1200, { iteration: 1 }),
    rec('E2-1', 900, { iteration: 1 }),
    rec('E2-2', 1500, { iteration: 1 }),
  ];
  const summary = summarize(records);
  const text = formatSummary(summary);
  assert.match(text, /E1-2 P50 1200\.0 ms > 1000 ms: yes/);
  assert.match(text, /E2-1 P50 900\.0 ms > 1000 ms: no/);
  assert.match(text, /E2-2 P50 1500\.0 ms > 1000 ms: yes/);
  assert.match(text, /这是筛查，不是因果结论/);
});

for (const [name, override] of [
  ['cacheFileCount=0', { cacheFileCount: 0 }],
  ['cacheFileCount=null', { cacheFileCount: null }],
  ['cacheMode!=warm', { cacheFileCount: 12, cacheMode: 'cold' }],
]) {
  test('E4-C is not evaluable when ' + name, () => {
    const records = [
      rec('E4-A', 1000, { iteration: 1, cacheMode: 'off' }),
      rec('E4-C', 800, { iteration: 1, cacheMode: 'warm', cacheFileCount: 12, cacheBytes: 4096, ...override }),
    ];
    const summary = summarize(records);
    assert.equal(summary.e4.cacheEvaluable, false);
    assert.equal(summary.e4.message, 'E4-C not-evaluable: 缓存未证明落盘');
    assert.equal(summary.e4.pair, null);
    const text = formatSummary(summary);
    assert.match(text, /E4-C not-evaluable: 缓存未证明落盘/);
    assert.doesNotMatch(text, /收益/);
    assert.doesNotMatch(text, /E4-A vs E4-C/);
  });
}

test('E4-A vs E4-C uses webReadyMs only after the warm cache is proven on disk', () => {
  const records = [
    rec('E4-A', 1000, { iteration: 1, httpReadyMs: 1000, cacheMode: 'off' }),
    rec('E4-A', 1200, { iteration: 2, httpReadyMs: 1200, cacheMode: 'off' }),
    rec('E4-C', 800, { iteration: 1, httpReadyMs: 800, cacheMode: 'warm', cacheFileCount: 12, cacheBytes: 4096 }),
    rec('E4-C', 900, { iteration: 2, httpReadyMs: 900, cacheMode: 'warm', cacheFileCount: 12, cacheBytes: 4096 }),
  ];
  const summary = summarize(records);
  assert.equal(summary.e4.cacheEvaluable, true);
  assert.equal(summary.e4.cacheFiles, 12);
  assert.equal(summary.e4.cacheBytes, 4096);
  assert.equal(summary.e4.pair.comparable, true);
  assert.equal(summary.e4.pair.n, 2);
  assert.equal(summary.e4.pair.p50Diff, -300);
  assert.equal(summary.e4.pair.p95Diff, -200);
  const text = formatSummary(summary);
  assert.match(text, /E4-A vs E4-C \(webReadyMs\): paired, same endpoint; n=2; ΔP50=-300\.0 ms; ΔP95=-200\.0 ms/);
  assert.match(text, /E4-C cache: mode=warm, files=12, bytes=4096/);
});

test('E4-A vs E4-C is not comparable when webReadyMs is missing', () => {
  const records = [
    rec('E4-A', 1000, { iteration: 1, httpReadyMs: null, cacheMode: 'off' }),
    rec('E4-C', 800, { iteration: 1, httpReadyMs: 800, cacheMode: 'warm', cacheFileCount: 12 }),
  ];
  const summary = summarize(records);
  assert.equal(summary.e4.cacheEvaluable, true);
  assert.equal(summary.e4.pair.comparable, false);
  assert.equal(summary.e4.pair.reason, 'no paired iterations');
  assert.match(formatSummary(summary), /E4-A vs E4-C \(webReadyMs\): not comparable/);
});

test('CSV has the fixed 22 columns in the documented order', () => {
  assert.equal(CSV_COLUMNS.length, 22);
  assert.equal(CSV_COLUMNS.join(','), HEADER);
  const { records } = parseJsonl(jsonl([
    rec('E1-0', 1234.5, { iteration: 2 }),
    rec('E3-0', 800, { iteration: 1, httpStatus: 200, cacheMode: 'off', cacheFileCount: 0, cacheBytes: 0 }),
  ]));
  const meta = { device: 'Pixel 8', android: '17', apk: 'deadbeef', node: 'v26.13.1', dsh: '0.2.0' };
  const rows = csvRows(toCsv(records, meta));
  assert.equal(rows.length, 3);
  assert.deepEqual(rows[0], HEADER.split(','));
  for (const row of rows) assert.equal(row.length, 22);
  assert.equal(rows[1][0], 'Pixel 8');
  assert.equal(rows[1][1], '17');
  assert.equal(rows[1][2], 'deadbeef');
  assert.equal(rows[1][3], 'v26.13.1');
  assert.equal(rows[1][4], '0.2.0');
  assert.deepEqual(rows[1].slice(5, 11), ['E1', 'E1-0', 'exit', '2', RUN, '1234.5']);
  assert.equal(rows[1][16], 'false');
  assert.equal(rows[1][17], 'ok');
  assert.equal(rows[2][6], 'E3-0');
  assert.equal(rows[2][10], '800');
  assert.equal(rows[2][12], ''); // guestFirstMarkerMs null
  assert.equal(rows[2][13], '800'); // httpReadyMs
  assert.equal(rows[2][14], '200');
  assert.equal(rows[2][15], '');
  assert.equal(rows[2][17], 'ok');
});

test('CSV leaves missing metadata and null measurements empty and never exports note', () => {
  const { records } = parseJsonl(jsonl([
    { schema: 1, variant: 'E1-0', runId: RUN, result: 'refused', note: 'secret prompt' },
  ]));
  const rows = csvRows(toCsv(records, {}));
  assert.equal(rows[1].length, 22);
  assert.deepEqual(rows[1].slice(0, 5), ['', '', '', '', '']);
  assert.equal(rows[1][8], '');
  assert.equal(rows[1][10], '');
  assert.equal(rows[1][16], '');
  assert.equal(rows[1][18], '');
  assert.ok(!toCsv(records, {}).includes('secret prompt'));
});

test('matrix JSON has the documented schema, metadata, variants and notes', () => {
  const records = [
    rec('E1-0', 100, { iteration: 1 }),
    rec('E4-C', 800, { iteration: 1, cacheMode: 'warm', cacheFileCount: 12, cacheBytes: 4096 }),
  ];
  const summary = summarize(records);
  const matrix = toMatrix(summary, { device: 'Pixel 8', android: '17', apk: 'deadbeef', node: 'v26', dsh: '0.2' }, '2026-11-18T00:00:00.000Z');
  assert.deepEqual(Object.keys(matrix), ['schema', 'generatedAt', 'device', 'android', 'apk', 'node', 'dsh', 'variants', 'notes']);
  assert.equal(matrix.schema, 1);
  assert.equal(matrix.generatedAt, '2026-11-18T00:00:00.000Z');
  assert.equal(matrix.device, 'Pixel 8');
  assert.equal(matrix.android, '17');
  assert.equal(matrix.apk, 'deadbeef');
  assert.equal(matrix.node, 'v26');
  assert.equal(matrix.dsh, '0.2');
  assert.deepEqual(Object.keys(matrix.variants[0]), [
    'experiment', 'variant', 'kind', 'attempts', 'successes', 'p50Ms', 'p95Ms', 'minMs', 'maxMs', 'cacheEvaluable',
  ]);
  const e1 = matrix.variants.find((item) => item.variant === 'E1-0');
  assert.equal(e1.p50Ms, 100);
  assert.equal(e1.p95Ms, 100);
  assert.equal(e1.cacheEvaluable, false);
  const c = matrix.variants.find((item) => item.variant === 'E4-C');
  assert.equal(c.cacheEvaluable, true);
  assert.ok(matrix.notes.some((note) => /not an SLA/.test(note)));
});

test('CLI without --out writes only CSV to stdout and the summary to stderr', () => {
  const dir = mkdtempSync(join(tmpdir(), 'dshm-exp-'));
  try {
    const log = join(dir, 'sample.jsonl');
    writeFileSync(log, jsonl([
      rec('E1-0', 10, { iteration: 1 }),
      rec('E1-1', 15, { iteration: 1 }),
      '{broken',
    ]));
    const result = cli(['--log', log, '--device', 'Pixel 8', '--android', '17']);
    assert.equal(result.status, 0, result.stderr);
    const rows = csvRows(result.stdout);
    assert.equal(rows[0].join(','), HEADER);
    assert.equal(rows.length, 3);
    assert.equal(rows[1][0], 'Pixel 8');
    assert.equal(rows[1][1], '17');
    assert.ok(!result.stdout.includes('startup-experiment summary'));
    assert.match(result.stderr, /startup-experiment summary/);
    assert.match(result.stderr, /skipped lines: 1/);
    assert.match(result.stderr, /paired, same endpoint/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('CLI writes CSV, summary and matrix files, keeps stdout empty and metadata in all outputs', () => {
  const dir = mkdtempSync(join(tmpdir(), 'dshm-exp-'));
  try {
    const log = join(dir, 'sample.jsonl');
    const out = join(dir, 'out.csv');
    const summaryPath = join(dir, 'summary.md');
    const matrixPath = join(dir, 'matrix.json');
    writeFileSync(log, jsonl([
      rec('E1-0', 10, { iteration: 1 }),
      rec('E1-1', 15, { iteration: 1 }),
      rec('E4-A', 1000, { iteration: 1, cacheMode: 'off' }),
      rec('E4-C', 800, { iteration: 1, cacheMode: 'warm', cacheFileCount: 12 }),
    ]));
    const result = cli([
      '--log', log, '--out', out, '--summary', summaryPath, '--matrix', matrixPath,
      '--device', 'Pixel 8', '--android', '17', '--apk', 'deadbeef', '--node', 'v26.13.1', '--dsh', '0.2.0',
    ]);
    assert.equal(result.status, 0, result.stderr);
    assert.equal(result.stdout, '');
    assert.match(result.stderr, /startup-experiment summary/);
    const csv = readFileSync(out, 'utf8');
    assert.equal(csvRows(csv)[0].join(','), HEADER);
    assert.match(csvRows(csv)[1].join(','), /^Pixel 8,17,deadbeef,v26\.13\.1,0\.2\.0,E1,E1-0,exit/);
    const fileSummary = readFileSync(summaryPath, 'utf8');
    assert.match(fileSummary, /startup-experiment summary/);
    assert.match(fileSummary, /\[E4 cache\]/);
    assert.equal(fileSummary.trimEnd(), result.stderr.trimEnd());
    const matrix = JSON.parse(readFileSync(matrixPath, 'utf8'));
    assert.equal(matrix.schema, 1);
    assert.equal(matrix.device, 'Pixel 8');
    assert.equal(matrix.variants.find((item) => item.variant === 'E4-C').cacheEvaluable, true);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('CLI reads stdin with --log -', () => {
  const result = cli(['--log', '-'], { input: jsonl([rec('E1-0', 10, { iteration: 1 })]) });
  assert.equal(result.status, 0, result.stderr);
  assert.equal(csvRows(result.stdout)[0].join(','), HEADER);
  assert.match(result.stderr, /records: 1/);
});

test('CLI exits 1 when no valid record is present', () => {
  const dir = mkdtempSync(join(tmpdir(), 'dshm-exp-'));
  try {
    const log = join(dir, 'empty.jsonl');
    writeFileSync(log, jsonl(['{broken', rec('E1-0', 1, { schema: 2 })]));
    const result = cli(['--log', log]);
    assert.equal(result.status, 1);
    assert.equal(result.stdout, '');
    assert.match(result.stderr, /no valid experiment records found/);
    assert.match(result.stderr, /skipped lines: 2/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('CLI exits 2 for unknown arguments, missing --log, unreadable log and a missing output directory', () => {
  const dir = mkdtempSync(join(tmpdir(), 'dshm-exp-'));
  try {
    const log = join(dir, 'sample.jsonl');
    writeFileSync(log, jsonl([rec('E1-0', 10)]));

    const unknown = cli(['--log', log, '--bogus']);
    assert.equal(unknown.status, 2);
    assert.match(unknown.stderr, /unknown argument: --bogus/);
    assert.match(unknown.stderr, /usage:/);

    const missing = cli([]);
    assert.equal(missing.status, 2);
    assert.match(missing.stderr, /--log is required/);

    const unreadable = cli(['--log', join(dir, 'does-not-exist.jsonl')]);
    assert.equal(unreadable.status, 2);
    assert.match(unreadable.stderr, /cannot read --log/);

    const missingOut = cli(['--log', log, '--out', join(dir, 'no-such-dir', 'out.csv')]);
    assert.equal(missingOut.status, 2);
    assert.match(missingOut.stderr, /output directory does not exist/);

    const missingSummary = cli(['--log', log, '--summary', join(dir, 'no-such-dir', 'summary.md')]);
    assert.equal(missingSummary.status, 2);

    const missingMatrix = cli(['--log', log, '--matrix', join(dir, 'no-such-dir', 'matrix.json')]);
    assert.equal(missingMatrix.status, 2);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('CLI --help prints usage to stdout and exits 0', () => {
  const result = cli(['--help']);
  assert.equal(result.status, 0);
  assert.match(result.stdout, /usage:/);
  assert.match(result.stdout, /--matrix/);
  assert.equal(result.stderr, '');
});
