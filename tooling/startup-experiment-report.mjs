#!/usr/bin/env node
// Startup experiment report (E1-E4): JSONL -> fixed-column CSV, stderr summary, optional matrix JSON.
// Dependency-free. Observation only: it never starts the app, a guest process or a runtime.
//
//   node tooling/startup-experiment-report.mjs --log <jsonl> [--out <csv>] [--summary <md>]
//        [--matrix <json>] [--device X] [--android Y] [--apk Z] [--node V] [--dsh V] [--help]
//
// stdout carries CSV only (nothing when --out is given). The summary always goes to stderr. See
// docs/STARTUP_EXPERIMENTS.md for the record format, endpoint rules and privacy list.

import { readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export const EXPERIMENTS = ['E1', 'E2', 'E3', 'E4'];
export const KINDS = ['exit', 'web'];
export const RESULTS = ['ok', 'failed', 'timeout', 'refused', 'setup-error'];
export const CACHE_MODES = ['off', 'cold', 'warm', 'unknown'];
export const THRESHOLD_MS = 1000;

// Fixed catalogue: variant ids are stable and the report keys on them. Unknown ids are skipped, never guessed.
const VARIANT_DEFS = [
  ['E1-0', 'E1', 'exit'],
  ['E1-1', 'E1', 'exit'],
  ['E1-2', 'E1', 'exit'],
  ['E1-3', 'E1', 'exit'],
  ['E2-0', 'E2', 'exit'],
  ['E2-1', 'E2', 'exit'],
  ['E2-2', 'E2', 'exit'],
  ['E2-3', 'E2', 'exit'],
  ['E3-P', 'E3', 'web'],
  ['E3-0', 'E3', 'web'],
  ['E3-1', 'E3', 'web'],
  ['E4-A', 'E4', 'web'],
  ['E4-B', 'E4', 'web'],
  ['E4-C', 'E4', 'web'],
];

export const VARIANT_ORDER = VARIANT_DEFS.map(([id]) => id);

export const VARIANTS = Object.freeze(Object.fromEntries(
  VARIANT_DEFS.map(([id, experiment, kind]) => [id, Object.freeze({ experiment, kind })]),
));

export const CSV_COLUMNS = [
  'device', 'android', 'apk', 'node', 'dsh',
  'experiment', 'variant', 'kind', 'iteration', 'runId',
  'durationMs', 'spawnMs', 'guestFirstMarkerMs', 'httpReadyMs', 'httpStatus',
  'exitCode', 'timedOut', 'result', 'cacheMode', 'cacheFileCount', 'cacheBytes', 'startedAt',
];

const MISSING_FIELDS = [
  'runId', 'durationMs', 'spawnMs', 'guestFirstMarkerMs', 'httpReadyMs', 'httpStatus',
  'userCpuMs', 'systemCpuMs', 'peakRssKb', 'exitCode', 'timedOut',
  'cacheMode', 'cacheFileCount', 'cacheBytes', 'startedAt', 'iteration',
];

/** Nearest-rank percentile over an already ascending array. Returns null for an empty sample. */
export function percentile(sorted, p) {
  if (!Array.isArray(sorted) || sorted.length === 0 || !Number.isFinite(p) || p <= 0 || p > 1) return null;
  const index = Math.ceil(p * sorted.length) - 1;
  return sorted[Math.min(Math.max(index, 0), sorted.length - 1)];
}

function isPlainObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function finiteNumber(value) {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function finiteInteger(value) {
  return typeof value === 'number' && Number.isInteger(value) ? value : null;
}

function nonEmptyText(value) {
  return typeof value === 'string' && value.length > 0 ? value : null;
}

function variantIndex(variant) {
  const index = VARIANT_ORDER.indexOf(variant);
  return index < 0 ? VARIANT_ORDER.length : index;
}

/**
 * Validates one JSON value. Missing optional fields become null; experiment/kind are derived from the fixed variant
 * catalogue when absent, but a present value that contradicts the catalogue is corrupt and skips the line.
 * Returns { record } or { reason }.
 */
function normalizeRecord(value, assumeSchema = false) {
  if (!isPlainObject(value)) return { reason: 'not-object' };
  if (value.schema !== 1 && !(assumeSchema && value.schema === undefined)) return { reason: 'schema' };
  const variant = nonEmptyText(value.variant);
  const definition = variant === null ? undefined : VARIANTS[variant];
  if (definition === undefined) return { reason: 'variant' };

  let experiment = definition.experiment;
  if (value.experiment !== undefined && value.experiment !== null) {
    if (value.experiment !== definition.experiment) return { reason: 'experiment' };
    experiment = value.experiment;
  }

  let kind = definition.kind;
  if (value.kind !== undefined && value.kind !== null) {
    if (!KINDS.includes(value.kind)) return { reason: 'kind' };
    if (value.kind !== definition.kind) return { reason: 'kind' };
    kind = value.kind;
  }

  if (typeof value.result !== 'string' || !RESULTS.includes(value.result)) return { reason: 'result' };

  // A refused/setup-error row may be minimal; every other row must identify its run.
  const runId = nonEmptyText(value.runId);
  if (runId === null && value.result !== 'refused' && value.result !== 'setup-error') return { reason: 'runId' };

  let cacheMode = null;
  if (value.cacheMode !== undefined && value.cacheMode !== null) {
    cacheMode = CACHE_MODES.includes(value.cacheMode) ? value.cacheMode : 'unknown';
  }

  return {
    record: {
      schema: 1,
      experiment,
      variant,
      kind,
      runId,
      iteration: finiteInteger(value.iteration),
      durationMs: finiteNumber(value.durationMs),
      spawnMs: finiteNumber(value.spawnMs),
      guestFirstMarkerMs: finiteNumber(value.guestFirstMarkerMs),
      httpReadyMs: finiteNumber(value.httpReadyMs),
      httpStatus: finiteInteger(value.httpStatus),
      userCpuMs: finiteNumber(value.userCpuMs),
      systemCpuMs: finiteNumber(value.systemCpuMs),
      peakRssKb: finiteInteger(value.peakRssKb),
      exitCode: finiteInteger(value.exitCode),
      timedOut: typeof value.timedOut === 'boolean' ? value.timedOut : null,
      result: value.result,
      cacheMode,
      cacheFileCount: finiteInteger(value.cacheFileCount),
      cacheBytes: finiteInteger(value.cacheBytes),
      startedAt: nonEmptyText(value.startedAt),
    },
  };
}

/**
 * Parses UTF-8 JSONL text. Blank lines are ignored (counted separately); corrupt JSON, schema!=1, unknown variants
 * and otherwise invalid records are skipped and counted. Never throws for bad input.
 * Returns { records, skipped, skipReasons, blankLines }.
 */
export function parseJsonl(text) {
  const records = [];
  const skipReasons = {};
  let skipped = 0;
  let blankLines = 0;
  const lines = String(text ?? '').replace(/^\uFEFF/, '').split(/\r?\n/);
  for (const line of lines) {
    if (line.trim() === '') {
      blankLines += 1;
      continue;
    }
    let value;
    try {
      value = JSON.parse(line);
    } catch {
      skipped += 1;
      skipReasons['bad-json'] = (skipReasons['bad-json'] ?? 0) + 1;
      continue;
    }
    const result = normalizeRecord(value);
    if (result.record === undefined) {
      skipped += 1;
      skipReasons[result.reason] = (skipReasons[result.reason] ?? 0) + 1;
      continue;
    }
    records.push(result.record);
  }
  return { records, skipped, skipReasons, blankLines };
}

function round1(value) {
  return value === null || value === undefined ? null : Math.round(value * 10) / 10;
}

function mapFor(records, metric) {
  const map = new Map();
  let duplicate = false;
  for (const record of records) {
    if (record.result !== 'ok' || record.iteration === null) continue;
    const value = finiteNumber(record[metric]);
    if (value === null) continue;
    if (map.has(record.iteration)) duplicate = true;
    else map.set(record.iteration, value);
  }
  return { map, duplicate };
}

/**
 * Compares two variants on one metric. Diffs are second minus first and only exist when experiment and kind match and
 * iterations pair one-to-one. Different endpoints are never subtracted, added or averaged.
 */
export function comparePair(a, b, metric = 'durationMs') {
  if (a.kind !== b.kind) {
    return { comparable: false, reason: 'different endpoint', n: 0, p50Diff: null, p95Diff: null };
  }
  if (a.experiment !== b.experiment) {
    return { comparable: false, reason: 'different experiment', n: 0, p50Diff: null, p95Diff: null };
  }
  const left = mapFor(a.records, metric);
  const right = mapFor(b.records, metric);
  if (left.duplicate || right.duplicate) {
    return { comparable: false, reason: 'iterations do not correspond one-to-one', n: 0, p50Diff: null, p95Diff: null };
  }
  const common = [...left.map.keys()].filter((iteration) => right.map.has(iteration)).sort((x, y) => x - y);
  if (common.length === 0) {
    return { comparable: false, reason: 'no paired iterations', n: 0, p50Diff: null, p95Diff: null };
  }
  const diffs = common.map((iteration) => right.map.get(iteration) - left.map.get(iteration)).sort((x, y) => x - y);
  return {
    comparable: true,
    reason: null,
    n: diffs.length,
    p50Diff: round1(percentile(diffs, 0.5)),
    p95Diff: round1(percentile(diffs, 0.95)),
  };
}

function buildGroups(records) {
  const groups = new Map();
  for (const record of records) {
    if (!groups.has(record.variant)) {
      groups.set(record.variant, {
        experiment: record.experiment,
        variant: record.variant,
        kind: record.kind,
        attempts: 0,
        successes: 0,
        durationMsValues: [],
        records: [],
      });
    }
    const group = groups.get(record.variant);
    group.records.push(record);
    group.attempts += 1;
    if (record.result === 'ok' && record.durationMs !== null) {
      group.successes += 1;
      group.durationMsValues.push(record.durationMs);
    }
  }
  return [...groups.values()]
    .sort((a, b) => variantIndex(a.variant) - variantIndex(b.variant))
    .map((group) => {
      const sorted = group.durationMsValues.slice().sort((a, b) => a - b);
      return {
        ...group,
        p50: round1(percentile(sorted, 0.5)),
        p95: round1(percentile(sorted, 0.95)),
        min: sorted.length === 0 ? null : round1(sorted[0]),
        max: sorted.length === 0 ? null : round1(sorted[sorted.length - 1]),
        insufficient: group.successes < 10,
      };
    });
}

function buildPairs(groups) {
  const pairs = [];
  const add = (a, b) => {
    if (a === undefined || b === undefined) return;
    pairs.push({
      a: a.variant,
      b: b.variant,
      aKind: a.kind,
      bKind: b.kind,
      aExperiment: a.experiment,
      bExperiment: b.experiment,
      ...comparePair(a, b),
    });
  };

  // All within-experiment pairs: same experiment and (for the fixed catalogue) the same endpoint.
  for (const experiment of ['E1', 'E2', 'E3']) {
    const list = groups.filter((group) => group.experiment === experiment);
    for (let i = 0; i < list.length; i += 1) {
      for (let j = i + 1; j < list.length; j += 1) add(list[i], list[j]);
    }
  }

  // Boundary pairs stay in the report as a guard: different experiment or endpoint must read "not comparable".
  const boundaries = [['E1', 'E2'], ['E2', 'E3'], ['E3', 'E4']];
  for (const [leftExperiment, rightExperiment] of boundaries) {
    const left = groups.filter((group) => group.experiment === leftExperiment).at(-1);
    const right = groups.filter((group) => group.experiment === rightExperiment)[0];
    add(left, right);
  }
  return pairs;
}

function evaluateE4(groups) {
  const a = groups.find((group) => group.variant === 'E4-A');
  const c = groups.find((group) => group.variant === 'E4-C');
  if (c === undefined) {
    return { present: false, cacheEvaluable: false, message: 'E4-C not present', cacheFiles: null, cacheBytes: null, pair: null };
  }
  const counts = c.records.map((record) => record.cacheFileCount).filter((value) => Number.isFinite(value));
  const cacheFiles = counts.length === 0 ? null : Math.max(...counts);
  const bytes = c.records.map((record) => record.cacheBytes).filter((value) => Number.isFinite(value));
  const cacheBytes = bytes.length === 0 ? null : Math.max(...bytes);
  const cacheEvaluable = c.records.length > 0 && c.records.every((record) =>
    record.cacheMode === 'warm' && Number.isFinite(record.cacheFileCount) && record.cacheFileCount > 0);
  if (!cacheEvaluable) {
    return {
      present: true,
      cacheEvaluable: false,
      message: 'E4-C not-evaluable: 缓存未证明落盘',
      cacheFiles,
      cacheBytes,
      pair: null,
    };
  }
  if (a === undefined) {
    return { present: true, cacheEvaluable: true, message: 'E4-A not present', cacheFiles, cacheBytes, pair: null };
  }
  return {
    present: true,
    cacheEvaluable: true,
    message: null,
    cacheFiles,
    cacheBytes,
    pair: { a: a.variant, b: c.variant, ...comparePair(a, c, 'httpReadyMs') },
  };
}

/**
 * Aggregates normalized records. Only result=ok rows with a non-null durationMs feed P50/P95/min/max; refused and
 * setup-error rows stay in the CSV and in the attempt counts but never in the statistics.
 */
export function summarize(records, meta = {}) {
  const input = Array.isArray(records) ? records : [];
  const accepted = [];
  let invalid = 0;
  for (const value of input) {
    // summarize() also accepts already-extracted records (parseJsonl output or test fixtures without schema).
    const result = normalizeRecord(value, true);
    if (result.record === undefined) invalid += 1;
    else accepted.push(result.record);
  }

  const groups = buildGroups(accepted);
  const pairs = buildPairs(groups);
  const missing = {};
  for (const field of MISSING_FIELDS) missing[field] = 0;
  for (const record of accepted) {
    for (const field of MISSING_FIELDS) {
      if (record[field] === null) missing[field] += 1;
    }
  }
  const missingTotal = Object.values(missing).reduce((total, count) => total + count, 0);
  const notOk = accepted.filter((record) => record.result !== 'ok').length;
  const insufficient = groups.filter((group) => group.insufficient).map((group) => ({
    variant: group.variant,
    successes: group.successes,
    attempts: group.attempts,
  }));

  const screening = ['E1-2', 'E2-1', 'E2-2'].map((variant) => {
    const group = groups.find((candidate) => candidate.variant === variant);
    const p50 = group?.p50 ?? null;
    return {
      variant,
      p50Ms: p50,
      thresholdMs: THRESHOLD_MS,
      exceeds: p50 === null ? null : p50 > THRESHOLD_MS,
    };
  });

  const e4 = evaluateE4(groups);
  const endpoints = {
    exit: groups.filter((group) => group.kind === 'exit').length,
    web: groups.filter((group) => group.kind === 'web').length,
  };

  return {
    meta: meta ?? {},
    recordCount: accepted.length,
    skipped: invalid,
    skipReasons: invalid === 0 ? {} : { invalid },
    blankLines: 0,
    groups,
    pairs,
    screening,
    e4,
    endpoints,
    quality: {
      skipped: invalid,
      skipReasons: invalid === 0 ? {} : { invalid },
      missing,
      missingTotal,
      notOk,
      insufficient,
    },
  };
}

function csvCell(value) {
  if (value === null || value === undefined) return '';
  const text = String(value);
  return /[",\r\n]/.test(text) ? '"' + text.replace(/"/g, '""') + '"' : text;
}

function csvNumber(value) {
  return Number.isFinite(value) ? String(value) : '';
}

function csvBoolean(value) {
  return value === true ? 'true' : value === false ? 'false' : '';
}

/** Fixed 22-column CSV. Only documented columns are written; note is never exported. */
export function toCsv(records, meta = {}) {
  const m = meta ?? {};
  const lines = [CSV_COLUMNS.join(',')];
  for (const value of Array.isArray(records) ? records : []) {
    const normalized = normalizeRecord(value, true);
    if (normalized.record === undefined) continue;
    const record = normalized.record;
    const row = [
      m.device, m.android, m.apk ?? m.apkCommit, m.node ?? m.nodeVersion, m.dsh ?? m.dshVersion,
      record.experiment, record.variant, record.kind, record.iteration, record.runId,
      csvNumber(record.durationMs), csvNumber(record.spawnMs), csvNumber(record.guestFirstMarkerMs),
      csvNumber(record.httpReadyMs), csvNumber(record.httpStatus), csvNumber(record.exitCode),
      csvBoolean(record.timedOut), record.result, record.cacheMode,
      csvNumber(record.cacheFileCount), csvNumber(record.cacheBytes), record.startedAt,
    ];
    lines.push(row.map(csvCell).join(','));
  }
  return lines.join('\n') + '\n';
}

/** Matrix JSON: one row per variant with the same success sample as the stderr summary. */
export function toMatrix(summary, meta = {}, generatedAt = new Date().toISOString()) {
  const m = meta ?? {};
  const result = summary ?? { groups: [], quality: { insufficient: [] }, e4: null };
  const variants = (result.groups ?? []).map((group) => ({
    experiment: group.experiment,
    variant: group.variant,
    kind: group.kind,
    attempts: group.attempts,
    successes: group.successes,
    p50Ms: round1(group.p50),
    p95Ms: round1(group.p95),
    minMs: round1(group.min),
    maxMs: round1(group.max),
    cacheEvaluable: group.variant === 'E4-C' ? Boolean(result.e4?.cacheEvaluable) : false,
  }));
  const notes = ['P50/P95 are nearest-rank over result=ok rows with a non-null duration; screening only, not an SLA.'];
  notes.push('Different endpoints (exit vs web) and different experiments are never added or subtracted.');
  if (result.e4?.message) notes.push(result.e4.message);
  if ((result.quality?.insufficient ?? []).length > 0) {
    notes.push('insufficient samples: ' + result.quality.insufficient
      .map((item) => item.variant + ' (' + item.successes + '/' + item.attempts + ')').join(', '));
  }
  return {
    schema: 1,
    generatedAt,
    device: m.device ?? null,
    android: m.android ?? null,
    apk: m.apk ?? m.apkCommit ?? null,
    node: m.node ?? m.nodeVersion ?? null,
    dsh: m.dsh ?? m.dshVersion ?? null,
    variants,
    notes,
  };
}

function fixed(value) {
  return value === null || value === undefined ? '—' : Number(value).toFixed(1);
}

function signed(value) {
  if (value === null || value === undefined) return '—';
  return (value >= 0 ? '+' : '') + Number(value).toFixed(1);
}

function pad(text, width) {
  const value = String(text);
  return value.length >= width ? value : value + ' '.repeat(width - value.length);
}

function pairReason(reason) {
  if (reason === 'different endpoint') return 'different endpoint (exit vs web)';
  return reason;
}

/** Human-readable summary for stderr. Chinese labels match docs/STARTUP_EXPERIMENTS.md; no raw JSON is echoed. */
export function formatSummary(summary, meta = {}) {
  const result = summary ?? {
    recordCount: 0,
    skipped: 0,
    groups: [],
    pairs: [],
    screening: [],
    e4: { present: false, cacheEvaluable: false, message: 'E4-C not present', pair: null },
    quality: { skipped: 0, skipReasons: {}, missing: {}, missingTotal: 0, notOk: 0, insufficient: [] },
  };
  const m = meta ?? result.meta ?? {};
  const context = [
    m.device ? 'device=' + m.device : null,
    m.android ? 'android=' + m.android : null,
    m.apk ? 'apk=' + m.apk : null,
    m.node ? 'node=' + m.node : null,
    m.dsh ? 'dsh=' + m.dsh : null,
  ].filter(Boolean).join('  ');

  const lines = [];
  lines.push('startup-experiment summary');
  lines.push('records: ' + result.recordCount + '  skipped lines: ' + (result.skipped ?? 0) + '  result!=ok: ' + (result.quality?.notOk ?? 0));
  if (context) lines.push(context);

  lines.push('');
  lines.push('[variants] (kind, success/attempt, nearest-rank P50/P95; only result=ok with non-null durationMs)');
  lines.push(pad('variant', 8) + ' ' + pad('kind', 5) + ' ' + pad('success', 12) + ' '
    + pad('P50 ms', 10) + ' ' + pad('P95 ms', 10) + ' ' + pad('min ms', 10) + ' ' + pad('max ms', 10) + ' flag');
  for (const group of result.groups ?? []) {
    lines.push(pad(group.variant, 8) + ' ' + pad(group.kind, 5) + ' '
      + pad(group.successes + '/' + group.attempts, 12) + ' '
      + pad(fixed(group.p50), 10) + ' ' + pad(fixed(group.p95), 10) + ' '
      + pad(fixed(group.min), 10) + ' ' + pad(fixed(group.max), 10) + ' '
      + (group.insufficient ? 'insufficient samples' : ''));
  }
  if ((result.groups ?? []).length === 0) lines.push('(none)');

  lines.push('');
  lines.push('[paired differences] (per-iteration second - first; only same experiment + same endpoint)');
  for (const pair of result.pairs ?? []) {
    if (pair.comparable) {
      lines.push(pair.a + ' vs ' + pair.b + ': paired, same endpoint; n=' + pair.n
        + '; ΔP50=' + signed(pair.p50Diff) + ' ms; ΔP95=' + signed(pair.p95Diff) + ' ms');
    } else {
      lines.push(pair.a + ' vs ' + pair.b + ': not comparable (' + pairReason(pair.reason) + ')');
    }
  }
  if ((result.pairs ?? []).length === 0) lines.push('(none)');
  lines.push('rule: exit and web end at different points; those durations are never added, averaged or subtracted.');

  lines.push('');
  lines.push('[E1/E2 screening] (P50 > 1000 ms is a screening flag, not a causal conclusion)');
  for (const item of result.screening ?? []) {
    const verdict = item.exceeds === null ? 'no successful sample' : item.exceeds ? 'yes' : 'no';
    lines.push(item.variant + ' P50 ' + fixed(item.p50Ms) + ' ms > ' + item.thresholdMs + ' ms: ' + verdict);
  }
  lines.push('这是筛查，不是因果结论。');

  lines.push('');
  lines.push('[E4 cache] (A vs C on webReadyMs; cache provenance must be proven before a comparison)');
  if (result.e4?.present === false) {
    lines.push(result.e4.message ?? 'E4-C not present');
  } else if (result.e4?.cacheEvaluable === false) {
    lines.push(result.e4.message ?? 'E4-C not-evaluable: 缓存未证明落盘');
  } else {
    lines.push('E4-C cache: mode=warm, files=' + (result.e4?.cacheFiles ?? '—') + ', bytes=' + (result.e4?.cacheBytes ?? '—'));
    if (result.e4?.message) lines.push(result.e4.message);
    const pair = result.e4?.pair;
    if (pair?.comparable) {
      lines.push('E4-A vs E4-C (webReadyMs): paired, same endpoint; n=' + pair.n
        + '; ΔP50=' + signed(pair.p50Diff) + ' ms; ΔP95=' + signed(pair.p95Diff) + ' ms');
    } else {
      lines.push('E4-A vs E4-C (webReadyMs): not comparable (' + pairReason(pair?.reason ?? 'no paired iterations') + ')');
    }
    lines.push('只陈述配对数据；不给出因果结论。');
  }

  lines.push('');
  lines.push('[data quality]');
  const reasons = Object.entries(result.quality?.skipReasons ?? {});
  lines.push('skipped lines: ' + (result.quality?.skipped ?? result.skipped ?? 0)
    + (reasons.length === 0 ? '' : ' (' + reasons.map(([key, count]) => key + '=' + count).join(', ') + ')'));
  const missing = Object.entries(result.quality?.missing ?? {}).filter(([, count]) => count > 0);
  lines.push('missing fields: ' + (missing.length === 0
    ? '(none)'
    : missing.map(([field, count]) => field + '=' + count).join(', ') + ' (total=' + (result.quality?.missingTotal ?? 0) + ')'));
  lines.push('result != ok: ' + (result.quality?.notOk ?? 0));
  const insufficient = result.quality?.insufficient ?? [];
  lines.push('insufficient samples: ' + (insufficient.length === 0
    ? '(none)'
    : insufficient.map((item) => item.variant + ' (' + item.successes + '/' + item.attempts + ')').join(', ')));

  return lines.join('\n');
}

const USAGE = [
  'usage: node tooling/startup-experiment-report.mjs --log <jsonl> [--out <csv>] [--summary <md>]',
  '       [--matrix <json>] [--device X] [--android Y] [--apk Z] [--node V] [--dsh V] [--help]',
  '',
  '  --log      required; E1-E4 JSONL file, or - to read stdin',
  '  --out      write the fixed 22-column CSV there; omitted: CSV to stdout',
  '  --summary  also write the stderr summary to this Markdown file',
  '  --matrix   write the matrix JSON to this file',
  '  --device / --android / --apk / --node / --dsh  optional metadata columns',
  '  --help     show this usage',
  '',
  'The summary (and every warning) always goes to stderr. stdout carries CSV only.',
  'Exit codes: 0 records reported, 1 no valid record, 2 argument or file error.',
].join('\n');

const OPTIONS = {
  '--log': 'log',
  '--out': 'out',
  '--summary': 'summary',
  '--matrix': 'matrix',
  '--device': 'device',
  '--android': 'android',
  '--apk': 'apk',
  '--node': 'node',
  '--dsh': 'dsh',
};

class UsageError extends Error {}

function parseArgs(argv) {
  const options = {};
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === '--help' || arg === '-h') return { help: true };
    const key = OPTIONS[arg];
    if (key === undefined) throw new UsageError('unknown argument: ' + arg);
    const value = argv[i + 1];
    if (value === undefined || value.startsWith('--')) throw new UsageError('missing value for ' + arg);
    options[key] = value;
    i += 1;
  }
  if (!options.log) throw new UsageError('--log is required');
  return { options };
}

function outputDirectoryExists(path) {
  try {
    return statSync(dirname(resolve(path))).isDirectory();
  } catch {
    return false;
  }
}

function runCli(argv) {
  let parsed;
  try {
    parsed = parseArgs(argv);
  } catch (error) {
    if (!(error instanceof UsageError)) throw error;
    console.error('startup-experiment-report: ' + error.message);
    console.error(USAGE);
    return 2;
  }
  if (parsed.help) {
    console.log(USAGE);
    return 0;
  }
  const options = parsed.options;
  const meta = {
    device: options.device,
    android: options.android,
    apk: options.apk,
    node: options.node,
    dsh: options.dsh,
  };

  for (const key of ['out', 'summary', 'matrix']) {
    if (options[key] !== undefined && !outputDirectoryExists(options[key])) {
      console.error('startup-experiment-report: output directory does not exist: ' + dirname(resolve(options[key])));
      return 2;
    }
  }

  let text;
  try {
    text = options.log === '-' ? readFileSync(0, 'utf8') : readFileSync(options.log, 'utf8');
  } catch (error) {
    console.error('startup-experiment-report: cannot read --log: ' + error.message);
    return 2;
  }

  const parsedLog = parseJsonl(text);
  if (parsedLog.records.length === 0) {
    console.error('startup-experiment-report: no valid experiment records found (nothing to report)');
    console.error('startup-experiment-report: skipped lines: ' + parsedLog.skipped);
    return 1;
  }

  const summary = summarize(parsedLog.records, meta);
  summary.skipped = parsedLog.skipped;
  summary.skipReasons = parsedLog.skipReasons;
  summary.blankLines = parsedLog.blankLines;
  summary.quality.skipped = parsedLog.skipped;
  summary.quality.skipReasons = parsedLog.skipReasons;
  const summaryText = formatSummary(summary, meta);

  try {
    if (options.out !== undefined) writeFileSync(options.out, toCsv(parsedLog.records, meta));
    if (options.summary !== undefined) writeFileSync(options.summary, summaryText + '\n');
    if (options.matrix !== undefined) writeFileSync(options.matrix, JSON.stringify(toMatrix(summary, meta), null, 2) + '\n');
  } catch (error) {
    console.error('startup-experiment-report: cannot write output: ' + error.message);
    return 2;
  }

  if (options.out === undefined) process.stdout.write(toCsv(parsedLog.records, meta));
  console.error(summaryText);
  return 0;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = runCli(process.argv.slice(2));
}
