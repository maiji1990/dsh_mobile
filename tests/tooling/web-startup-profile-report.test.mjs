// E5 Web startup profile report: strict JSONL parsing, httpReadyMs statistics, OFF/ON pairing, V8 profile
// categorization, the fixed 15-column CSV, the matrix JSON, privacy and the CLI contract of
// tooling/web-startup-profile-report.mjs.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import {
  CSV_COLUMNS,
  aggregateCategories,
  aggregateUnmappedCallerContexts,
  categorizeProfile,
  formatSummary,
  median,
  pairOffOn,
  parseRecords,
  percentile,
  summarize,
  toCsv,
  toMatrix,
} from '../../tooling/web-startup-profile-report.mjs';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const tool = join(repo, 'tooling', 'web-startup-profile-report.mjs');
const RUN = 'a1b2c3d4e5f6';
const HEADER = 'variant,iteration,runId,httpReadyMs,profileCaptureMs,profileSampleCount,nodeUserCpuMs,nodeSystemCpuMs,eventLoopActiveMs,eventLoopIdleMs,stopLatencyMs,result,errorCode,profileStoredPrivately,profileFile';

function rec(variant, httpReadyMs = 16000, extra = {}) {
  return {
    schema: 1,
    experiment: 'E5',
    variant,
    runId: RUN,
    iteration: 1,
    httpReadyMs,
    profileCaptureMs: variant === 'E5-PROFILE' ? 420 : null,
    profileSampleCount: variant === 'E5-PROFILE' ? 1240 : null,
    nodeUserCpuMs: 5000,
    nodeSystemCpuMs: 500,
    eventLoopActiveMs: 4000,
    eventLoopIdleMs: 6000,
    stopLatencyMs: variant === 'E5-PROFILE' ? 12 : null,
    result: 'ok',
    errorCode: null,
    profileStoredPrivately: true,
    startedAt: '2026-11-19T02:03:04.567Z',
    ...extra,
  };
}

function jsonl(values) {
  return values.map((value) => (typeof value === 'string' ? value : JSON.stringify(value))).join('\n') + '\n';
}

function cpuProfile(entries) {
  return {
    nodes: entries.map((entry, index) => ({
      id: index + 1,
      callFrame: { functionName: entry.functionName ?? '', url: entry.url ?? '' },
    })),
    samples: entries.map((_, index) => index + 1),
    timeDeltas: entries.map((entry) => entry.deltaUs),
    startTime: 0,
    endTime: entries.reduce((total, entry) => total + entry.deltaUs, 0),
  };
}

function csvRows(csv) {
  return csv.trimEnd().split('\n').map((row) => row.split(','));
}

function cli(args, options = {}) {
  return spawnSync(process.execPath, [tool, ...args], { encoding: 'utf8', ...options });
}

function writeProfile(dir, runId, content) {
  writeFileSync(join(dir, runId + '.cpuprofile'), typeof content === 'string' ? content : JSON.stringify(content));
}

test('parseRecords skips bad JSON, schema, unknown variants, illegal enums and malformed fields', () => {
  const parsed = parseRecords(jsonl([
    rec('E5-OFF'),
    '{broken',
    rec('E5-OFF', 16000, { schema: 2 }),
    rec('E5-OFF', 16000, { variant: 'E5-UNKNOWN' }),
    rec('E5-OFF', 16000, { result: 'weird' }),
    rec('E5-OFF', 16000, { errorCode: 'NOPE' }),
    rec('E5-OFF', 16000, { profileCaptureMs: '420' }),
    rec('E5-OFF', 16000, { runId: 'short' }),
    '',
  ]));
  assert.equal(parsed.records.length, 1);
  assert.equal(parsed.records[0].variant, 'E5-OFF');
  assert.equal(parsed.skipped, 7);
  assert.equal(parsed.skipReasons['bad-json'], 1);
  assert.equal(parsed.skipReasons.schema, 1);
  assert.equal(parsed.skipReasons.variant, 1);
  assert.equal(parsed.skipReasons.result, 1);
  assert.equal(parsed.skipReasons.errorCode, 1);
  assert.equal(parsed.skipReasons['field-type'], 1);
  assert.equal(parsed.skipReasons.runId, 1);
  assert.equal(parsed.invalidEnums, 3);
  assert.equal(parsed.blankLines, 2);
});

test('parseRecords keeps absent measurements null and accepts known error codes', () => {
  const parsed = parseRecords(jsonl([
    { schema: 1, experiment: 'E5', variant: 'E5-OFF', result: 'refused' },
    { schema: 1, experiment: 'E5', variant: 'E5-PROFILE', runId: RUN, result: 'timeout', errorCode: 'PROFILE_TIMEOUT' },
  ]));
  assert.equal(parsed.records.length, 2);
  const minimal = parsed.records[0];
  assert.equal(minimal.runId, null);
  assert.equal(minimal.iteration, null);
  assert.equal(minimal.httpReadyMs, null);
  assert.equal(minimal.profileCaptureMs, null);
  assert.equal(minimal.profileSampleCount, null);
  assert.equal(minimal.nodeUserCpuMs, null);
  assert.equal(minimal.nodeSystemCpuMs, null);
  assert.equal(minimal.eventLoopActiveMs, null);
  assert.equal(minimal.eventLoopIdleMs, null);
  assert.equal(minimal.stopLatencyMs, null);
  assert.equal(minimal.errorCode, null);
  assert.equal(minimal.profileStoredPrivately, null);
  assert.equal(minimal.startedAt, null);
  assert.equal(parsed.records[1].errorCode, 'PROFILE_TIMEOUT');
  assert.equal(parsed.records[1].result, 'timeout');
});

test('parseRecords rejects a non-E5 experiment and a malformed runId', () => {
  const parsed = parseRecords(jsonl([
    rec('E5-OFF', 16000, { experiment: 'E4' }),
    rec('E5-OFF', 16000, { runId: 'zzzzzzzzzzzz' }),
    rec('E5-OFF', 16000, { runId: null }),
  ]));
  assert.equal(parsed.records.length, 1);
  assert.deepEqual(Object.keys(parsed.skipReasons).sort(), ['experiment', 'runId']);
  assert.equal(parsed.invalidEnums, 1);
});

test('percentile is nearest-rank and median is the true middle value', () => {
  assert.equal(percentile([10], 0.5), 10);
  assert.equal(percentile([1, 2, 3], 0.5), 2);
  assert.equal(percentile([1, 2, 3, 4], 0.5), 2);
  assert.equal(percentile([1, 2, 3, 4], 0.95), 4);
  assert.equal(percentile([], 0.5), null);
  assert.equal(median([1, 2]), 1.5);
  assert.equal(median([1, 2, 3]), 2);
  assert.equal(median([]), null);
});

test('variant statistics use only result=ok rows with a non-null httpReadyMs', () => {
  const records = [100, 200, 300, 400, 500, 600, 700, 800, 900, 1000].map((value, index) =>
    rec('E5-OFF', value, { iteration: index + 1 }));
  records.push(rec('E5-OFF', null, { iteration: 11 }));
  records.push(rec('E5-OFF', 9999, { iteration: 12, result: 'failed' }));
  const summary = summarize(records);
  const group = summary.variants.find((variant) => variant.variant === 'E5-OFF');
  assert.equal(group.attempts, 12);
  assert.equal(group.successes, 10);
  assert.equal(group.p50Ms, 500);
  assert.equal(group.p95Ms, 1000);
  assert.equal(group.minMs, 100);
  assert.equal(group.maxMs, 1000);
  assert.equal(group.insufficient, false);
  assert.equal(summary.dataQuality.failed, 1);
  assert.match(formatSummary(summary), /E5-OFF\s+10\/12\s+500\.0\s+1000\.0/);
});

test('pairOffOn pairs by iteration and reports median, nearest-rank P50/P95 and percent of OFF P50', () => {
  const records = [];
  for (let iteration = 1; iteration <= 10; iteration += 1) {
    records.push(rec('E5-OFF', 16000, { iteration }));
    records.push(rec('E5-PROFILE', 16400, { iteration }));
  }
  const overhead = pairOffOn(records);
  assert.equal(overhead.n, 10);
  assert.equal(overhead.medianMs, 400);
  assert.equal(overhead.p50Ms, 400);
  assert.equal(overhead.p95Ms, 400);
  assert.equal(overhead.offP50Ms, 16000);
  assert.equal(overhead.percent, 2.5);
  assert.equal(overhead.insufficient, false);
  assert.equal(overhead.acceptable, true);
  assert.equal(overhead.status, 'acceptable');
});

test('pairOffOn excludes iterations where either side is failed or lacks httpReadyMs', () => {
  const records = [];
  for (let iteration = 1; iteration <= 10; iteration += 1) {
    records.push(rec('E5-OFF', 16000, { iteration }));
    records.push(rec('E5-PROFILE', 16400, { iteration }));
  }
  records.find((record) => record.variant === 'E5-OFF' && record.iteration === 1).result = 'failed';
  records.find((record) => record.variant === 'E5-PROFILE' && record.iteration === 2).httpReadyMs = null;
  const overhead = pairOffOn(records);
  assert.equal(overhead.n, 8);
  assert.equal(overhead.insufficient, true);
  assert.equal(overhead.acceptable, null);
  assert.equal(overhead.status, 'insufficient');
});

test('pairOffOn uses the true median for even diffs and nearest-rank for P50', () => {
  const overhead = pairOffOn([
    rec('E5-OFF', 1000, { iteration: 1 }),
    rec('E5-PROFILE', 1100, { iteration: 1 }),
    rec('E5-OFF', 1000, { iteration: 2 }),
    rec('E5-PROFILE', 1300, { iteration: 2 }),
  ]);
  assert.equal(overhead.n, 2);
  assert.equal(overhead.medianMs, 200);
  assert.equal(overhead.p50Ms, 100);
  assert.equal(overhead.p95Ms, 300);
  assert.equal(overhead.insufficient, true);
});

test('pairOffOn returns insufficient with n=0 when nothing can be paired', () => {
  const overhead = pairOffOn([
    rec('E5-OFF', null, { iteration: 1 }),
    rec('E5-PROFILE', 16400, { iteration: 1 }),
  ]);
  assert.equal(overhead.n, 0);
  assert.equal(overhead.medianMs, null);
  assert.equal(overhead.percent, null);
  assert.equal(overhead.acceptable, null);
  assert.equal(overhead.status, 'insufficient');
});

test('acceptance boundary is median <= 5% of OFF P50 and median < 800 ms', () => {
  const pair = (off, profile) => {
    const records = [];
    for (let iteration = 1; iteration <= 10; iteration += 1) {
      records.push(rec('E5-OFF', off, { iteration }));
      records.push(rec('E5-PROFILE', profile, { iteration }));
    }
    return pairOffOn(records);
  };
  const justInside = pair(16000, 16799);
  assert.equal(justInside.medianMs, 799);
  assert.equal(justInside.percent, 5);
  assert.equal(justInside.acceptable, true);

  const exactly800 = pair(16000, 16800);
  assert.equal(exactly800.medianMs, 800);
  assert.equal(exactly800.percent, 5);
  assert.equal(exactly800.acceptable, false);
  assert.equal(exactly800.status, 'over');

  const overPercent = pair(10000, 10600);
  assert.equal(overPercent.percent, 6);
  assert.equal(overPercent.acceptable, false);
  assert.equal(overPercent.status, 'over');
});

test('CPU and ELU ratios are per-round medians and the summary says ELU idle is not disk wait', () => {
  const summary = summarize([rec('E5-OFF', 10000, {
    nodeUserCpuMs: 5000,
    nodeSystemCpuMs: 500,
    eventLoopActiveMs: 4000,
    eventLoopIdleMs: 6000,
  })]);
  const row = summary.cpuElu.find((item) => item.variant === 'E5-OFF');
  assert.equal(row.cpuN, 1);
  assert.equal(row.cpuRatioMedian, 55);
  assert.equal(row.eluN, 1);
  assert.equal(row.eluActiveMedian, 40);
  const text = formatSummary(summary);
  assert.match(text, /55\.0%/);
  assert.match(text, /40\.0%/);
  assert.match(text, /ELU idle 不等于磁盘等待/);
});

test('CPU and ELU medians stay null when the inputs are null', () => {
  const summary = summarize([rec('E5-OFF', 10000, {
    nodeUserCpuMs: null,
    nodeSystemCpuMs: 500,
    eventLoopActiveMs: null,
    eventLoopIdleMs: 6000,
  })]);
  const row = summary.cpuElu.find((item) => item.variant === 'E5-OFF');
  assert.equal(row.cpuN, 0);
  assert.equal(row.cpuRatioMedian, null);
  assert.equal(row.eluN, 0);
  assert.equal(row.eluActiveMedian, null);
  assert.match(formatSummary(summary), /E5-OFF\s+0\s+—\s+0\s+—/);
});

test('categorizeProfile maps every frame by url/functionName and weights samples by timeDeltas', () => {
  const profile = cpuProfile([
    { functionName: '(idle)', url: '', deltaUs: 1000 },
    { functionName: '(program)', url: '', deltaUs: 2000 },
    { functionName: '(garbage collector)', url: '', deltaUs: 3000 },
    { functionName: 'require', url: 'node:internal/modules/cjs/loader', deltaUs: 4000 },
    { functionName: 'Module._load', url: 'file:///data/app/node_modules/foo/index.js', deltaUs: 5000 },
    { functionName: '(native)', url: '', deltaUs: 6000 },
    { functionName: 'render', url: 'app://bundle/main', deltaUs: 7000 },
    { functionName: 'compile', url: 'file:///data/app/scripts/main.js', deltaUs: 8000 },
    { functionName: 'resolve', url: 'app://bundle/resolve', deltaUs: 9000 },
  ]);
  const result = categorizeProfile(profile);
  assert.equal(result.ok, true);
  assert.equal(result.samples, 9);
  assert.equal(result.totalUs, 45000);
  assert.equal(result.durationMs, 45);
  const time = (category) => result.categories[category].timeUs;
  const samples = (category) => result.categories[category].samples;
  assert.equal(time('idle-or-wait'), 1000);
  assert.equal(time('program-unattributed'), 2000);
  assert.equal(time('gc'), 3000);
  assert.equal(time('module-resolution'), 13000);
  assert.equal(samples('module-resolution'), 2);
  assert.equal(time('module-eval'), 13000);
  assert.equal(samples('module-eval'), 2);
  assert.equal(time('explicit-native'), 6000);
  assert.equal(time('unattributed-no-url'), 0);
  assert.equal(time('app-initialization'), 7000);
  assert.equal(JSON.stringify(result).includes('node_modules'), false);
  assert.equal(JSON.stringify(result).includes('app://bundle'), false);
});

test('categorizeProfile distinguishes empty, invalid and weighted profiles', () => {
  assert.equal(categorizeProfile(null).code, 'invalid');
  assert.equal(categorizeProfile({}).code, 'invalid');
  assert.equal(categorizeProfile({ nodes: [], samples: [], timeDeltas: [] }).code, 'empty');
  assert.equal(categorizeProfile({ nodes: [], samples: [1], timeDeltas: [0] }).code, 'invalid');
  const weighted = categorizeProfile({
    nodes: [
      { id: 1, callFrame: { functionName: 'a', url: 'app://a' } },
      { id: 2, callFrame: { functionName: 'b', url: 'app://b' } },
    ],
    samples: [1, 2, 1],
    timeDeltas: [100, 200, 300],
  });
  assert.equal(weighted.ok, true);
  assert.equal(weighted.samples, 3);
  assert.equal(weighted.totalUs, 600);
  assert.equal(weighted.categories['app-initialization'].samples, 3);
  assert.equal(weighted.categories['app-initialization'].timeUs, 600);
});


test('pseudo program and missing-url frames are not misreported as compile or native', () => {
  const result = categorizeProfile(cpuProfile([
    { functionName: '(program)', url: '', deltaUs: 4000 },
    { functionName: 'CplusplusCallback', url: '', deltaUs: 6000 },
    { functionName: '(native)', url: '', deltaUs: 3000 },
  ]));
  assert.equal(result.categories['program-unattributed'].timeUs, 4000);
  assert.equal(result.categories['unattributed-no-url'].timeUs, 6000);
  assert.equal(result.categories['explicit-native'].timeUs, 3000);
  assert.equal(result.unmappedUs, 9000);
  assert.equal(result.unmappedCallerContexts['no-classified-ancestor'].timeUs, 9000);
});

test('unclassified leaf associates with nearest named caller without double-counting sample time', () => {
  const profile = {
    nodes: [
      { id: 1, callFrame: { functionName: 'Module._resolveFilename', url: 'node:internal/modules/cjs/loader' }, children: [2] },
      { id: 2, callFrame: { functionName: 'hostCallback', url: '' }, children: [] },
      { id: 3, callFrame: { functionName: 'userEntry', url: 'file:///opt/dshapp/runtime/app.js' }, children: [4] },
      { id: 4, callFrame: { functionName: '(native)', url: '' }, children: [] },
    ],
    samples: [2, 4],
    timeDeltas: [3000, 7000],
  };
  const result = categorizeProfile(profile);
  assert.equal(result.ok, true);
  assert.equal(result.totalUs, 10000);
  assert.equal(result.unmappedUs, 10000);
  assert.equal(result.unmappedCallerContexts['module-resolution'].timeUs, 3000);
  assert.equal(result.unmappedCallerContexts['module-eval'].timeUs, 7000);
  const context = aggregateUnmappedCallerContexts([result]);
  assert.equal(context.find(item => item.category === 'module-resolution').share, 0.3);
  assert.equal(context.find(item => item.category === 'module-eval').share, 0.7);
  assert.equal(JSON.stringify(result).includes('opt/dshapp'), false);
  assert.equal(JSON.stringify(context).includes('userEntry'), false);
});

test('broken caller cycles cannot hang the attribution pass', () => {
  const profile = {
    nodes: [
      { id: 1, callFrame: { functionName: 'callback', url: '' }, children: [2] },
      { id: 2, callFrame: { functionName: 'callback', url: '' }, children: [1] },
    ],
    samples: [1], timeDeltas: [500],
  };
  const result = categorizeProfile(profile);
  assert.equal(result.unmappedCallerContexts['no-classified-ancestor'].timeUs, 500);
});

test('aggregateCategories totals samples/time and medians per-run shares with zeros included', () => {
  const run1 = categorizeProfile(cpuProfile([
    { functionName: '(idle)', url: '', deltaUs: 1000 },
    { functionName: 'render', url: 'app://main', deltaUs: 3000 },
    { functionName: '(garbage collector)', url: '', deltaUs: 2000 },
  ]));
  const run2 = categorizeProfile(cpuProfile([
    { functionName: '(idle)', url: '', deltaUs: 1000 },
    { functionName: 'render', url: 'app://main', deltaUs: 9000 },
  ]));
  const categories = aggregateCategories([run1, run2]);
  const idle = categories.find((category) => category.category === 'idle-or-wait');
  const app = categories.find((category) => category.category === 'app-initialization');
  const gc = categories.find((category) => category.category === 'gc');
  assert.equal(idle.samples, 2);
  assert.equal(idle.timeMs, 2);
  assert.equal(idle.share, 0.1333);
  assert.equal(idle.runs, 2);
  assert.equal(app.samples, 2);
  assert.equal(app.timeMs, 12);
  assert.equal(app.share, 0.7);
  assert.equal(app.runs, 2);
  assert.equal(gc.samples, 1);
  assert.equal(gc.timeMs, 2);
  assert.equal(gc.share, 0.1667);
  assert.equal(gc.runs, 1);
  assert.deepEqual(aggregateCategories([]), []);
  assert.deepEqual(aggregateCategories([{ ok: false, code: 'invalid' }]), []);
});

test('aggregateCategories also accepts raw .cpuprofile objects', () => {
  const categories = aggregateCategories([cpuProfile([
    { functionName: '(idle)', url: '', deltaUs: 2500 },
    { functionName: 'render', url: 'app://main', deltaUs: 7500 },
  ])]);
  assert.equal(categories.length, 2);
  assert.equal(categories[0].category, 'idle-or-wait');
  assert.equal(categories[0].samples, 1);
  assert.equal(categories[0].timeMs, 2.5);
  assert.equal(categories[0].share, 0.25);
  assert.equal(categories[1].category, 'app-initialization');
  assert.equal(categories[1].share, 0.75);
});

test('toCsv has the fixed 15 columns and emits only enum/number/null values', () => {
  assert.equal(CSV_COLUMNS.length, 15);
  assert.equal(CSV_COLUMNS.join(','), HEADER);
  const { records } = parseRecords(jsonl([
    rec('E5-OFF', 16800, { iteration: 2 }),
    rec('E5-PROFILE', 16400, { iteration: 1, runId: 'bbbbbbbbbbbb', errorCode: 'PROFILE_WRITE_FAILED', profileStoredPrivately: false, profileFile: 'invalid' }),
  ]));
  records[0].profileFile = 'missing';
  records[1].profileFile = 'invalid';
  const rows = csvRows(toCsv(records));
  assert.equal(rows.length, 3);
  assert.deepEqual(rows[0], HEADER.split(','));
  for (const row of rows) assert.equal(row.length, 15);
  assert.equal(rows[1][0], 'E5-OFF');
  assert.equal(rows[1][1], '2');
  assert.equal(rows[1][2], RUN);
  assert.equal(rows[1][3], '16800');
  assert.equal(rows[1][4], '');
  assert.equal(rows[1][13], 'true');
  assert.equal(rows[1][14], 'missing');
  assert.equal(rows[2][0], 'E5-PROFILE');
  assert.equal(rows[2][11], 'ok');
  assert.equal(rows[2][12], 'PROFILE_WRITE_FAILED');
  assert.equal(rows[2][13], 'false');
  assert.equal(rows[2][14], 'invalid');
});

test('toCsv maps an unknown/missing profile state to missing and never writes a path', () => {
  const { records } = parseRecords(jsonl([rec('E5-PROFILE', 16400)]));
  records[0].profileFile = undefined;
  const csv = toCsv(records);
  assert.match(csv, /,missing\n$/);
  assert.doesNotMatch(csv, /\.cpuprofile/);
  assert.doesNotMatch(csv, /[\\/]/);
});

test('matrix JSON has exactly the documented schema, variants, overhead, categories and dataQuality', () => {
  const records = [
    rec('E5-OFF', 16000, { iteration: 1 }),
    rec('E5-PROFILE', 16400, { iteration: 1 }),
  ];
  records[1].profileFile = 'present';
  const summary = summarize(records);
  summary.categories = [{
    category: 'idle-or-wait',
    samples: 10,
    timeMs: 5.5,
    share: 0.25,
    runs: 1,
  }];
  const matrix = toMatrix(summary, '2026-11-19T00:00:00.000Z');
  assert.deepEqual(Object.keys(matrix), [
    'schema', 'generatedAt', 'records', 'variants', 'overhead', 'categories', 'dataQuality',
  ]);
  assert.equal(matrix.schema, 1);
  assert.equal(matrix.generatedAt, '2026-11-19T00:00:00.000Z');
  assert.equal(matrix.records, 2);
  assert.deepEqual(Object.keys(matrix.variants[0]), [
    'variant', 'attempts', 'successes', 'p50Ms', 'p95Ms', 'minMs', 'maxMs', 'profileFiles',
  ]);
  assert.deepEqual(Object.keys(matrix.variants[0].profileFiles), ['present', 'missing', 'empty', 'invalid']);
  assert.deepEqual(Object.keys(matrix.overhead), ['n', 'medianMs', 'p50Ms', 'p95Ms', 'percent', 'acceptable']);
  assert.deepEqual(Object.keys(matrix.categories[0]), ['category', 'samples', 'timeMs', 'share']);
  assert.deepEqual(Object.keys(matrix.dataQuality), ['skipped', 'invalidEnums', 'failed']);
  assert.equal(matrix.variants[0].profileFiles.missing, 1);
  assert.equal(matrix.variants[1].profileFiles.present, 1);
  assert.equal(matrix.overhead.n, 1);
});

test('CLI writes CSV to stdout, summary to stderr and both files on request', () => {
  const dir = mkdtempSync(join(tmpdir(), 'dshm-e5-'));
  try {
    const profiles = join(dir, 'profiles');
    mkdirSync(profiles);
    writeProfile(profiles, RUN, cpuProfile([
      { functionName: '(idle)', url: '', deltaUs: 1000 },
      { functionName: 'render', url: 'app://main', deltaUs: 3000 },
    ]));
    const log = join(dir, 'sample.jsonl');
    writeFileSync(log, jsonl([
      rec('E5-OFF', 16000, { iteration: 1, runId: 'cccccccccccc' }),
      rec('E5-PROFILE', 16400, { iteration: 1 }),
      '{broken',
    ]));
    const out = join(dir, 'out.csv');
    const summaryPath = join(dir, 'summary.md');
    const matrixPath = join(dir, 'matrix.json');
    const result = cli(['--log', log, '--profiles', profiles, '--out', out, '--summary', summaryPath, '--matrix', matrixPath]);
    assert.equal(result.status, 0, result.stderr);
    assert.equal(result.stdout, '');
    assert.match(result.stderr, /web-startup-profile summary \(E5\)/);
    assert.match(result.stderr, /skipped lines: 1/);
    const csv = readFileSync(out, 'utf8');
    assert.equal(csvRows(csv)[0].join(','), HEADER);
    const fileSummary = readFileSync(summaryPath, 'utf8');
    assert.equal(fileSummary.trimEnd(), result.stderr.trimEnd());
    const matrix = JSON.parse(readFileSync(matrixPath, 'utf8'));
    assert.equal(matrix.schema, 1);
    assert.equal(matrix.records, 2);
    assert.equal(matrix.variants.find((variant) => variant.variant === 'E5-PROFILE').profileFiles.present, 1);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('CLI reports all four profile file states and never calls an incomplete set complete', () => {
  const dir = mkdtempSync(join(tmpdir(), 'dshm-e5-'));
  try {
    const profiles = join(dir, 'profiles');
    mkdirSync(profiles);
    const present = '111111111111';
    const empty = '222222222222';
    const invalid = '333333333333';
    const missing = '444444444444';
    writeProfile(profiles, present, cpuProfile([
      { functionName: '(idle)', url: '', deltaUs: 1000 },
      { functionName: 'render', url: 'app://main', deltaUs: 3000 },
    ]));
    writeProfile(profiles, empty, '');
    writeProfile(profiles, invalid, '{not json');
    const log = join(dir, 'states.jsonl');
    writeFileSync(log, jsonl([
      rec('E5-PROFILE', 16400, { runId: present, iteration: 1 }),
      rec('E5-PROFILE', 16400, { runId: empty, iteration: 2 }),
      rec('E5-PROFILE', 16400, { runId: invalid, iteration: 3 }),
      rec('E5-PROFILE', 16400, { runId: missing, iteration: 4 }),
    ]));
    const out = join(dir, 'out.csv');
    const matrixPath = join(dir, 'matrix.json');
    const result = cli(['--log', log, '--profiles', profiles, '--out', out, '--matrix', matrixPath]);
    assert.equal(result.status, 0, result.stderr);
    const rows = csvRows(readFileSync(out, 'utf8'));
    assert.deepEqual(rows.slice(1).map((row) => row[14]), ['present', 'empty', 'invalid', 'missing']);
    assert.match(result.stderr, /profile files: present=1 missing=1 empty=1 invalid=1/);
    assert.match(result.stderr, /status: not-complete \(missing=1; empty=1; invalid=1\)/);
    assert.doesNotMatch(result.stderr, /status: complete/);
    assert.doesNotMatch(result.stderr, /通过/);
    assert.match(result.stderr, /never treated as a success/);
    const matrix = JSON.parse(readFileSync(matrixPath, 'utf8'));
    assert.deepEqual(matrix.variants[0].profileFiles, { present: 1, missing: 1, empty: 1, invalid: 1 });
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('privacy: profile urls, function names, paths, query strings and extra fields never reach any output', () => {
  const dir = mkdtempSync(join(tmpdir(), 'dshm-e5-'));
  try {
    const profiles = join(dir, 'profiles');
    mkdirSync(profiles);
    const runId = 'abcdefabcdef';
    writeProfile(profiles, runId, cpuProfile([
      {
        functionName: 'Authorization Bearer SECRET_TOKEN_9f3a',
        url: 'https://user:pass@example.test/private?token=SECRET_TOKEN_9f3a',
        deltaUs: 1000,
      },
      {
        functionName: 'loadSecretThing',
        url: '/storage/emulated/0/Android/data/io.github.maiji1990.dshmobile/secret.js?q=SECRET_TOKEN_9f3a',
        deltaUs: 2000,
      },
      { functionName: '(idle)', url: '', deltaUs: 3000 },
    ]));
    const log = join(dir, 'privacy.jsonl');
    writeFileSync(log, jsonl([
      rec('E5-PROFILE', 16400, {
        runId,
        iteration: 1,
        note: 'SECRET_TOKEN_9f3a',
        token: 'SECRET_TOKEN_9f3a',
        authorization: 'Bearer SECRET_TOKEN_9f3a',
      }),
    ]));
    const out = join(dir, 'out.csv');
    const summaryPath = join(dir, 'summary.md');
    const matrixPath = join(dir, 'matrix.json');
    const result = cli(['--log', log, '--profiles', profiles, '--out', out, '--summary', summaryPath, '--matrix', matrixPath]);
    assert.equal(result.status, 0, result.stderr);
    const combined = [
      result.stdout,
      result.stderr,
      readFileSync(out, 'utf8'),
      readFileSync(summaryPath, 'utf8'),
      readFileSync(matrixPath, 'utf8'),
    ].join('\n');
    for (const secret of [
      'SECRET_TOKEN_9f3a',
      'Authorization',
      'user:pass',
      'example.test',
      '/storage/emulated/0',
      'secret.js',
      'loadSecretThing',
      '.cpuprofile',
    ]) {
      assert.equal(combined.includes(secret), false, 'leaked: ' + secret);
    }
    assert.match(result.stderr, /categories/);
    assert.match(result.stderr, /idle-or-wait/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('CLI reads JSONL from stdin with --log -', () => {
  const dir = mkdtempSync(join(tmpdir(), 'dshm-e5-'));
  try {
    const profiles = join(dir, 'profiles');
    mkdirSync(profiles);
    const result = cli(['--log', '-', '--profiles', profiles], {
      input: jsonl([rec('E5-OFF', 16000, { iteration: 1 })]),
    });
    assert.equal(result.status, 0, result.stderr);
    assert.equal(csvRows(result.stdout)[0].join(','), HEADER);
    assert.match(result.stderr, /records: 1/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('CLI exits 1 when no valid E5 record is present', () => {
  const dir = mkdtempSync(join(tmpdir(), 'dshm-e5-'));
  try {
    const profiles = join(dir, 'profiles');
    mkdirSync(profiles);
    const log = join(dir, 'empty.jsonl');
    writeFileSync(log, jsonl(['{broken', rec('E5-OFF', 16000, { schema: 2 }), rec('E5-OFF', 16000, { result: 'nope' })]));
    const result = cli(['--log', log, '--profiles', profiles]);
    assert.equal(result.status, 1);
    assert.equal(result.stdout, '');
    assert.match(result.stderr, /no valid E5 records found/);
    assert.match(result.stderr, /skipped lines: 3\s+invalid enums: 1/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('CLI exits 2 for unknown arguments, missing arguments, unreadable log and missing directories', () => {
  const dir = mkdtempSync(join(tmpdir(), 'dshm-e5-'));
  try {
    const profiles = join(dir, 'profiles');
    mkdirSync(profiles);
    const log = join(dir, 'sample.jsonl');
    writeFileSync(log, jsonl([rec('E5-OFF', 16000, { iteration: 1 })]));

    const unknown = cli(['--log', log, '--profiles', profiles, '--bogus']);
    assert.equal(unknown.status, 2);
    assert.match(unknown.stderr, /unknown argument: --bogus/);
    assert.match(unknown.stderr, /usage:/);

    assert.equal(cli([]).status, 2);
    assert.match(cli([]).stderr, /--log is required/);
    assert.equal(cli(['--log', log]).status, 2);
    assert.match(cli(['--log', log]).stderr, /--profiles is required/);

    const unreadable = cli(['--log', join(dir, 'does-not-exist.jsonl'), '--profiles', profiles]);
    assert.equal(unreadable.status, 2);
    assert.match(unreadable.stderr, /cannot read --log/);

    const noProfileDir = cli(['--log', log, '--profiles', join(dir, 'no-such-dir')]);
    assert.equal(noProfileDir.status, 2);
    assert.match(noProfileDir.stderr, /--profiles directory does not exist/);

    const missingOut = cli(['--log', log, '--profiles', profiles, '--out', join(dir, 'no-such-dir', 'out.csv')]);
    assert.equal(missingOut.status, 2);
    assert.match(missingOut.stderr, /output directory does not exist/);

    assert.equal(cli(['--log', log, '--profiles', profiles, '--summary', join(dir, 'no-such-dir', 's.md')]).status, 2);
    assert.equal(cli(['--log', log, '--profiles', profiles, '--matrix', join(dir, 'no-such-dir', 'm.json')]).status, 2);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('CLI --help prints usage to stdout and exits 0', () => {
  const result = cli(['--help']);
  assert.equal(result.status, 0);
  assert.match(result.stdout, /usage:/);
  assert.match(result.stdout, /--profiles/);
  assert.match(result.stdout, /--matrix/);
  assert.equal(result.stderr, '');
});

test('PROFILE_UNAVAILABLE is an accepted failure code, unknown codes are still skipped', () => {
  const parsed = parseRecords(jsonl([
    rec('E5-PROFILE', 16000, { result: 'failed', errorCode: 'PROFILE_UNAVAILABLE', profileSampleCount: null, stopLatencyMs: null }),
    rec('E5-PROFILE', 16000, { result: 'failed', errorCode: 'PROFILE_BOGUS' }),
  ]));
  assert.equal(parsed.records.length, 1);
  assert.equal(parsed.records[0].errorCode, 'PROFILE_UNAVAILABLE');
  assert.deepEqual(parsed.skipReasons, { errorCode: 1 });
  const matrix = toMatrix({ records: parsed.records, variants: [], overhead: null, categories: [], dataQuality: { skipped: 0, invalidEnums: 1, failed: 1 } }, {});
  assert.equal(matrix.dataQuality.failed, 1);
});
