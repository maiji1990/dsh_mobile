#!/usr/bin/env node
// E5 Web startup profile report: E5 JSONL + V8 .cpuprofile -> fixed-column CSV, stderr summary,
// optional matrix JSON. Dependency-free. Observation only: it never starts the app, a guest process,
// a runtime or the inspector.
//
//   node tooling/web-startup-profile-report.mjs --log <jsonl> --profiles <dir>
//        [--out <csv>] [--summary <md>] [--matrix <json>] [--help]
//
// stdout carries CSV only (nothing when --out is given). The summary always goes to stderr.
// See docs/WEB_STARTUP_DIAGNOSIS.md for the record format, handshake and privacy list.

import { readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export const EXPERIMENT = 'E5';
export const VARIANTS = Object.freeze(['E5-OFF', 'E5-PROFILE']);
export const RESULTS = Object.freeze(['ok', 'failed', 'timeout', 'refused', 'setup-error']);
// PROFILE_UNAVAILABLE is the plan's code for 'inspector could not be used'; it is a failure, never a success.
export const ERROR_CODES = Object.freeze(['PROFILE_TIMEOUT', 'PROFILE_WRITE_FAILED', 'PROFILE_EMPTY', 'PROFILE_UNAVAILABLE']);
export const PROFILE_STATES = Object.freeze(['present', 'missing', 'empty', 'invalid']);
export const CATEGORY_ORDER = Object.freeze([
  'idle-or-wait',
  'v8-compile',
  'gc',
  'module-resolution',
  'module-eval',
  'native-or-unknown',
  'app-initialization',
]);

export const CSV_COLUMNS = Object.freeze([
  'variant', 'iteration', 'runId', 'httpReadyMs', 'profileCaptureMs', 'profileSampleCount',
  'nodeUserCpuMs', 'nodeSystemCpuMs', 'eventLoopActiveMs', 'eventLoopIdleMs', 'stopLatencyMs',
  'result', 'errorCode', 'profileStoredPrivately', 'profileFile',
]);

const MEASUREMENT_FIELDS = [
  'httpReadyMs', 'profileCaptureMs', 'profileSampleCount', 'nodeUserCpuMs', 'nodeSystemCpuMs',
  'eventLoopActiveMs', 'eventLoopIdleMs', 'stopLatencyMs',
];

const RUN_ID = /^[0-9a-f]{12}$/i;

function isPlainObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function finiteNumber(value) {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function finiteInteger(value) {
  return typeof value === 'number' && Number.isInteger(value) ? value : null;
}

function round(value, digits) {
  if (!Number.isFinite(value)) return null;
  const factor = 10 ** digits;
  return Math.round(value * factor) / factor;
}

function round1(value) {
  return round(value, 1);
}

/** Nearest-rank percentile over an already ascending array. Returns null for an empty sample. */
export function percentile(sorted, p) {
  if (!Array.isArray(sorted) || sorted.length === 0 || !Number.isFinite(p) || p <= 0 || p > 1) return null;
  const index = Math.ceil(p * sorted.length) - 1;
  return sorted[Math.min(Math.max(index, 0), sorted.length - 1)];
}

/** True median (mean of the two middle values for an even sample). Returns null for an empty sample. */
export function median(values) {
  if (!Array.isArray(values) || values.length === 0) return null;
  const sorted = values.slice().sort((a, b) => a - b);
  const middle = Math.floor(sorted.length / 2);
  return sorted.length % 2 === 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
}

/**
 * Validates one E5 JSONL value. Missing measurements become null, never 0. Any string outside the fixed
 * enum (variant, result, errorCode) is illegal and skips the line. Unknown extra fields are ignored and
 * never exported.
 * Returns { record } or { reason, invalidEnum }.
 */
function normalizeRecord(value) {
  if (!isPlainObject(value)) return { reason: 'not-object' };
  if (value.schema !== 1) return { reason: 'schema' };

  if (value.experiment !== undefined && value.experiment !== null && value.experiment !== EXPERIMENT) {
    return { reason: 'experiment', invalidEnum: true };
  }

  if (typeof value.variant !== 'string' || !VARIANTS.includes(value.variant)) {
    return { reason: 'variant', invalidEnum: true };
  }
  if (typeof value.result !== 'string' || !RESULTS.includes(value.result)) {
    return { reason: 'result', invalidEnum: true };
  }

  let errorCode = null;
  if (value.errorCode !== undefined && value.errorCode !== null) {
    if (typeof value.errorCode !== 'string' || !ERROR_CODES.includes(value.errorCode)) {
      return { reason: 'errorCode', invalidEnum: true };
    }
    errorCode = value.errorCode;
  }

  let runId = null;
  if (value.runId !== undefined && value.runId !== null) {
    if (typeof value.runId !== 'string' || !RUN_ID.test(value.runId)) return { reason: 'runId' };
    runId = value.runId;
  }

  const iteration = value.iteration === undefined || value.iteration === null ? null : finiteInteger(value.iteration);
  if (value.iteration !== undefined && value.iteration !== null && iteration === null) return { reason: 'field-type' };

  const measurements = {};
  for (const field of MEASUREMENT_FIELDS) {
    const raw = value[field];
    if (raw === undefined || raw === null) {
      measurements[field] = null;
      continue;
    }
    const number = field === 'profileSampleCount' ? finiteInteger(raw) : finiteNumber(raw);
    if (number === null) return { reason: 'field-type' };
    measurements[field] = number;
  }

  let profileStoredPrivately = null;
  if (value.profileStoredPrivately !== undefined && value.profileStoredPrivately !== null) {
    if (typeof value.profileStoredPrivately !== 'boolean') return { reason: 'field-type' };
    profileStoredPrivately = value.profileStoredPrivately;
  }

  let startedAt = null;
  if (value.startedAt !== undefined && value.startedAt !== null) {
    if (typeof value.startedAt !== 'string') return { reason: 'field-type' };
    startedAt = value.startedAt;
  }

  return {
    record: {
      schema: 1,
      experiment: EXPERIMENT,
      variant: value.variant,
      runId,
      iteration,
      httpReadyMs: measurements.httpReadyMs,
      profileCaptureMs: measurements.profileCaptureMs,
      profileSampleCount: measurements.profileSampleCount,
      nodeUserCpuMs: measurements.nodeUserCpuMs,
      nodeSystemCpuMs: measurements.nodeSystemCpuMs,
      eventLoopActiveMs: measurements.eventLoopActiveMs,
      eventLoopIdleMs: measurements.eventLoopIdleMs,
      stopLatencyMs: measurements.stopLatencyMs,
      result: value.result,
      errorCode,
      profileStoredPrivately,
      startedAt,
      profileFile: 'missing',
    },
  };
}

/**
 * Parses UTF-8 JSONL text. Blank lines are ignored and counted separately. Corrupt JSON, schema!=1,
 * unknown variants, illegal enum values and malformed field types skip the line and are counted.
 * Never throws for bad input.
 * Returns { records, skipped, skipReasons, invalidEnums, blankLines }.
 */
export function parseRecords(text) {
  const records = [];
  const skipReasons = {};
  let skipped = 0;
  let invalidEnums = 0;
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
      if (result.invalidEnum === true) invalidEnums += 1;
      continue;
    }
    records.push(result.record);
  }
  return { records, skipped, skipReasons, invalidEnums, blankLines };
}

/** Values of result=ok rows for one variant with a non-null httpReadyMs, ascending. */
function okHttpReadyValues(records, variant) {
  return records
    .filter((record) => record.variant === variant && record.result === 'ok' && record.httpReadyMs !== null)
    .map((record) => record.httpReadyMs)
    .sort((a, b) => a - b);
}

function emptyProfileFiles() {
  return { present: 0, missing: 0, empty: 0, invalid: 0 };
}

function profileFileCounts(records) {
  const counts = emptyProfileFiles();
  for (const record of records) {
    const state = PROFILE_STATES.includes(record.profileFile) ? record.profileFile : 'missing';
    counts[state] += 1;
  }
  return counts;
}

/** One row per present E5 variant. Statistics use httpReadyMs only (result=ok and non-null). */
function buildVariants(records) {
  const variants = [];
  for (const variant of VARIANTS) {
    const group = records.filter((record) => record.variant === variant);
    if (group.length === 0) continue;
    const values = okHttpReadyValues(records, variant);
    variants.push({
      variant,
      attempts: group.length,
      successes: values.length,
      p50Ms: round1(percentile(values, 0.5)),
      p95Ms: round1(percentile(values, 0.95)),
      minMs: values.length === 0 ? null : round1(values[0]),
      maxMs: values.length === 0 ? null : round1(values[values.length - 1]),
      profileFiles: profileFileCounts(group),
      insufficient: values.length < 10,
    });
  }
  return variants;
}

/**
 * Pairs E5-PROFILE - E5-OFF by iteration. Only iterations where both rows are result=ok with a non-null
 * httpReadyMs are paired. The percentage is the paired median over the variant OFF P50. Acceptance target:
 * median <= 5% of OFF P50 and median < 800 ms. This is a target, not an SLA.
 */
export function pairOffOn(records) {
  const input = Array.isArray(records) ? records : [];
  const offAll = input.filter((record) =>
    record.variant === 'E5-OFF' && record.result === 'ok' && record.httpReadyMs !== null);
  const profileAll = input.filter((record) =>
    record.variant === 'E5-PROFILE' && record.result === 'ok' && record.httpReadyMs !== null);

  const duplicates = new Set();
  const index = (list) => {
    const map = new Map();
    for (const record of list) {
      if (record.iteration === null || record.iteration === undefined) continue;
      if (map.has(record.iteration)) duplicates.add(record.iteration);
      else map.set(record.iteration, record.httpReadyMs);
    }
    return map;
  };
  const off = index(offAll);
  const profile = index(profileAll);
  for (const iteration of duplicates) {
    off.delete(iteration);
    profile.delete(iteration);
  }

  const common = [...off.keys()].filter((iteration) => profile.has(iteration)).sort((a, b) => a - b);
  const diffs = common.map((iteration) => profile.get(iteration) - off.get(iteration));
  const sortedDiffs = diffs.slice().sort((a, b) => a - b);
  const medianRaw = median(diffs);
  const offValues = offAll.map((record) => record.httpReadyMs).sort((a, b) => a - b);
  const offP50Raw = percentile(offValues, 0.5);
  const percentRaw = medianRaw !== null && offP50Raw !== null && offP50Raw > 0
    ? (medianRaw / offP50Raw) * 100
    : null;
  const n = diffs.length;
  const insufficient = n < 10;
  const acceptable = !insufficient && medianRaw !== null && percentRaw !== null
    ? percentRaw <= 5 && medianRaw < 800
    : null;
  return {
    n,
    medianMs: round1(medianRaw),
    p50Ms: round1(percentile(sortedDiffs, 0.5)),
    p95Ms: round1(percentile(sortedDiffs, 0.95)),
    percent: round1(percentRaw),
    offP50Ms: round1(offP50Raw),
    acceptable,
    insufficient,
    status: acceptable === null ? 'insufficient' : acceptable ? 'acceptable' : 'over',
    duplicateIterations: duplicates.size,
  };
}

/** Maps one V8 call frame to the fixed E5 category. Only the frame url/functionName is consulted. */
function categoryForFrame(frame) {
  const source = isPlainObject(frame) ? frame : {};
  const functionName = typeof source.functionName === 'string' ? source.functionName : '';
  const url = typeof source.url === 'string' ? source.url : '';
  if (functionName === '(idle)') return 'idle-or-wait';
  if (functionName === '(program)') return 'v8-compile';
  if (functionName === '(garbage collector)') return 'gc';
  if (url.includes('node:internal/modules')
    || functionName.includes('require')
    || functionName.includes('loadESM')
    || functionName.includes('resolve')) {
    return 'module-resolution';
  }
  if (url.includes('node_modules')
    || url.includes('/opt/dshapp/runtime')
    || /\.(?:mjs|js|cjs)(?:[?#]|$)/.test(url)) {
    return 'module-eval';
  }
  if (functionName === '(native)' || url === '') return 'native-or-unknown';
  return 'app-initialization';
}

/**
 * Categorizes one parsed V8 .cpuprofile. Every sample is weighted by its timeDeltas entry (microseconds)
 * and attributed to the category of its node's call frame. Returns only category names and numbers.
 * Returns { ok:true, samples, totalUs, durationMs, categories } or { ok:false, code:'empty'|'invalid' }.
 */
export function categorizeProfile(profile) {
  if (!isPlainObject(profile)) return { ok: false, code: 'invalid' };
  const { nodes, samples, timeDeltas } = profile;
  if (!Array.isArray(nodes) || !Array.isArray(samples) || !Array.isArray(timeDeltas)) {
    return { ok: false, code: 'invalid' };
  }
  if (samples.length === 0) return { ok: false, code: 'empty' };

  const frames = new Map();
  for (const node of nodes) {
    if (!isPlainObject(node)) continue;
    const id = finiteInteger(node.id);
    if (id === null) continue;
    frames.set(id, isPlainObject(node.callFrame) ? node.callFrame : {});
  }

  const categories = Object.fromEntries(CATEGORY_ORDER.map((category) => [category, { samples: 0, timeUs: 0 }]));
  let totalUs = 0;
  for (let i = 0; i < samples.length; i += 1) {
    const weight = finiteNumber(timeDeltas[i]);
    const timeUs = weight === null || weight < 0 ? 0 : weight;
    totalUs += timeUs;
    const frame = frames.get(samples[i]) ?? {};
    const category = categoryForFrame(frame);
    categories[category].samples += 1;
    categories[category].timeUs += timeUs;
  }
  if (totalUs <= 0) return { ok: false, code: 'invalid' };
  return {
    ok: true,
    samples: samples.length,
    totalUs,
    durationMs: totalUs / 1000,
    categories,
  };
}

/**
 * Aggregates categorized profiles. samples/timeMs are totals; share is the median of the per-profile
 * shares (a profile where the category is absent contributes 0). runs is the number of profiles where
 * the category has at least one sample. Only observed categories are returned, in fixed order.
 */
export function aggregateCategories(profileResults) {
  const input = Array.isArray(profileResults) ? profileResults : [];
  const results = input
    .map((result) => {
      if (result !== null && typeof result === 'object' && result.ok === true) return result;
      // Also accept a raw .cpuprofile object so callers can aggregate without a separate step.
      if (isPlainObject(result) && Array.isArray(result.nodes) && Array.isArray(result.samples)) {
        return categorizeProfile(result);
      }
      return null;
    })
    .filter((result) => result !== null && result !== undefined && result.ok === true
      && Number.isFinite(result.totalUs) && result.totalUs > 0);
  if (results.length === 0) return [];
  const accumulator = new Map(CATEGORY_ORDER.map((category) => [category, {
    category,
    samples: 0,
    timeUs: 0,
    shares: [],
    runs: 0,
  }]));
  for (const result of results) {
    for (const category of CATEGORY_ORDER) {
      const entry = result.categories?.[category] ?? { samples: 0, timeUs: 0 };
      const bucket = accumulator.get(category);
      const timeUs = Number.isFinite(entry.timeUs) ? entry.timeUs : 0;
      const samples = Number.isFinite(entry.samples) ? entry.samples : 0;
      bucket.samples += samples;
      bucket.timeUs += timeUs;
      bucket.shares.push(timeUs / result.totalUs);
      if (samples > 0) bucket.runs += 1;
    }
  }
  return [...accumulator.values()]
    .filter((bucket) => bucket.samples > 0)
    .map((bucket) => ({
      category: bucket.category,
      samples: bucket.samples,
      timeMs: round(bucket.timeUs / 1000, 3),
      share: round(median(bucket.shares) ?? 0, 4),
      runs: bucket.runs,
    }));
}

function buildProfileIntegrity(records) {
  const rows = records.filter((record) => record.variant === 'E5-PROFILE');
  const files = emptyProfileFiles();
  for (const record of rows) {
    const state = PROFILE_STATES.includes(record.profileFile) ? record.profileFile : 'missing';
    files[state] += 1;
  }
  const values = (field) => rows
    .map((record) => record[field])
    .filter((value) => typeof value === 'number' && Number.isFinite(value));
  const sampleCounts = values('profileSampleCount');
  const captureTimes = values('profileCaptureMs');
  const stopLatencies = values('stopLatencyMs');
  const nullMetricRows = rows.filter((record) =>
    record.profileSampleCount === null || record.profileCaptureMs === null || record.stopLatencyMs === null).length;
  const notPrivateRows = rows.filter((record) => record.profileStoredPrivately !== true).length;
  const offPresent = records.filter((record) =>
    record.variant === 'E5-OFF' && record.profileFile === 'present').length;

  const reasons = [];
  if (rows.length === 0) reasons.push('no E5-PROFILE rows');
  if (files.missing > 0) reasons.push('missing=' + files.missing);
  if (files.empty > 0) reasons.push('empty=' + files.empty);
  if (files.invalid > 0) reasons.push('invalid=' + files.invalid);
  if (nullMetricRows > 0) reasons.push('null metrics on ' + nullMetricRows + ' row(s)');
  if (notPrivateRows > 0) reasons.push('profileStoredPrivately != true on ' + notPrivateRows + ' row(s)');

  return {
    profileRows: rows.length,
    files,
    medianSampleCount: round1(median(sampleCounts)),
    medianCaptureMs: round1(median(captureTimes)),
    medianStopLatencyMs: round1(median(stopLatencies)),
    complete: reasons.length === 0,
    reasons,
    nullMetricRows,
    notPrivateRows,
    offPresent,
  };
}

function buildCpuElu(records) {
  return VARIANTS.map((variant) => {
    const group = records.filter((record) => record.variant === variant);
    const cpuRatios = [];
    const eluShares = [];
    for (const record of group) {
      if (Number.isFinite(record.nodeUserCpuMs) && Number.isFinite(record.nodeSystemCpuMs)
        && Number.isFinite(record.httpReadyMs) && record.httpReadyMs > 0) {
        cpuRatios.push((record.nodeUserCpuMs + record.nodeSystemCpuMs) / record.httpReadyMs * 100);
      }
      if (Number.isFinite(record.eventLoopActiveMs) && Number.isFinite(record.eventLoopIdleMs)
        && record.eventLoopActiveMs + record.eventLoopIdleMs > 0) {
        eluShares.push(record.eventLoopActiveMs / (record.eventLoopActiveMs + record.eventLoopIdleMs) * 100);
      }
    }
    return {
      variant,
      attempts: group.length,
      cpuN: cpuRatios.length,
      cpuRatioMedian: round1(median(cpuRatios)),
      eluN: eluShares.length,
      eluActiveMedian: round1(median(eluShares)),
    };
  });
}

function analyze(records, parsed) {
  const variants = buildVariants(records);
  const overhead = pairOffOn(records);
  const profileIntegrity = buildProfileIntegrity(records);
  const categories = aggregateCategories(records.profileResults ?? []);
  const cpuElu = buildCpuElu(records);
  const failed = records.filter((record) => record.result !== 'ok').length;
  const errorCodes = {};
  for (const record of records) {
    if (record.errorCode === null) continue;
    errorCodes[record.errorCode] = (errorCodes[record.errorCode] ?? 0) + 1;
  }
  return {
    recordCount: records.length,
    skipped: parsed.skipped,
    skipReasons: parsed.skipReasons,
    invalidEnums: parsed.invalidEnums,
    blankLines: parsed.blankLines,
    variants,
    overhead,
    categories,
    profileIntegrity,
    cpuElu,
    dataQuality: {
      skipped: parsed.skipped,
      skipReasons: parsed.skipReasons,
      invalidEnums: parsed.invalidEnums,
      failed,
      errorCodes,
      insufficient: variants.filter((variant) => variant.insufficient).map((variant) => ({
        variant: variant.variant,
        successes: variant.successes,
        attempts: variant.attempts,
      })),
    },
  };
}

/** Aggregates parsed records. Tests may call this directly; the CLI passes the parse counters in. */
export function summarize(records, parsed = {}) {
  const input = Array.isArray(records) ? records : [];
  return analyze(input, {
    skipped: parsed.skipped ?? 0,
    skipReasons: parsed.skipReasons ?? {},
    invalidEnums: parsed.invalidEnums ?? 0,
    blankLines: parsed.blankLines ?? 0,
  });
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

function profileState(value) {
  return PROFILE_STATES.includes(value) ? value : 'missing';
}

/** Fixed 15-column CSV. profileFile is one of present/missing/empty/invalid and never a path. */
export function toCsv(records) {
  const lines = [CSV_COLUMNS.join(',')];
  for (const record of Array.isArray(records) ? records : []) {
    if (!isPlainObject(record) || !VARIANTS.includes(record.variant)) continue;
    // Defense in depth: even a hand-built record can only emit the fixed enums and a 12-hex runId.
    const runId = typeof record.runId === 'string' && RUN_ID.test(record.runId) ? record.runId : '';
    const result = RESULTS.includes(record.result) ? record.result : '';
    const errorCode = ERROR_CODES.includes(record.errorCode) ? record.errorCode : '';
    const row = [
      record.variant,
      csvNumber(record.iteration),
      runId,
      csvNumber(record.httpReadyMs),
      csvNumber(record.profileCaptureMs),
      csvNumber(record.profileSampleCount),
      csvNumber(record.nodeUserCpuMs),
      csvNumber(record.nodeSystemCpuMs),
      csvNumber(record.eventLoopActiveMs),
      csvNumber(record.eventLoopIdleMs),
      csvNumber(record.stopLatencyMs),
      result,
      errorCode,
      csvBoolean(record.profileStoredPrivately),
      profileState(record.profileFile),
    ];
    lines.push(row.map(csvCell).join(','));
  }
  return lines.join('\n') + '\n';
}

/** Matrix JSON: fixed schema 1 with records, per-variant stats, overhead, categories and data quality. */
export function toMatrix(summary, generatedAt = new Date().toISOString()) {
  const result = summary ?? {};
  const overhead = result.overhead ?? {};
  const quality = result.dataQuality ?? {};
  return {
    schema: 1,
    generatedAt,
    records: result.recordCount ?? 0,
    variants: (result.variants ?? []).map((variant) => ({
      variant: variant.variant,
      attempts: variant.attempts,
      successes: variant.successes,
      p50Ms: variant.p50Ms,
      p95Ms: variant.p95Ms,
      minMs: variant.minMs,
      maxMs: variant.maxMs,
      profileFiles: {
        present: variant.profileFiles.present,
        missing: variant.profileFiles.missing,
        empty: variant.profileFiles.empty,
        invalid: variant.profileFiles.invalid,
      },
    })),
    overhead: {
      n: overhead.n ?? 0,
      medianMs: overhead.medianMs ?? null,
      p50Ms: overhead.p50Ms ?? null,
      p95Ms: overhead.p95Ms ?? null,
      percent: overhead.percent ?? null,
      acceptable: overhead.acceptable ?? null,
    },
    categories: (result.categories ?? []).map((category) => ({
      category: category.category,
      samples: category.samples,
      timeMs: category.timeMs,
      share: category.share,
    })),
    dataQuality: {
      skipped: quality.skipped ?? 0,
      invalidEnums: quality.invalidEnums ?? 0,
      failed: quality.failed ?? 0,
    },
  };
}

function fixed(value) {
  return value === null || value === undefined ? '—' : Number(value).toFixed(1);
}

function signed(value) {
  if (value === null || value === undefined) return '—';
  return (value >= 0 ? '+' : '') + Number(value).toFixed(1);
}

function percentText(value) {
  return value === null || value === undefined ? '—' : (value >= 0 ? '+' : '') + Number(value).toFixed(1) + '%';
}

function ratioText(value) {
  return value === null || value === undefined ? '—' : Number(value).toFixed(1) + '%';
}

function pad(text, width) {
  const value = String(text);
  return value.length >= width ? value : value + ' '.repeat(width - value.length);
}

/** Human-readable summary for stderr. Only fixed enums, numbers and category names are emitted. */
export function formatSummary(summary) {
  const result = summary ?? {};
  const variants = result.variants ?? [];
  const overhead = result.overhead ?? {};
  const integrity = result.profileIntegrity ?? {};
  const categories = result.categories ?? [];
  const cpuElu = result.cpuElu ?? [];
  const quality = result.dataQuality ?? {};

  const lines = [];
  lines.push('web-startup-profile summary (E5)');
  lines.push('records: ' + (result.recordCount ?? 0)
    + '  skipped lines: ' + (result.skipped ?? 0)
    + '  invalid enums: ' + (result.invalidEnums ?? 0)
    + '  result!=ok: ' + (quality.failed ?? 0));

  lines.push('');
  lines.push('[variants] (E5; httpReadyMs only, result=ok and non-null; nearest-rank P50/P95)');
  lines.push(pad('variant', 12) + ' ' + pad('success/attempt', 15) + ' '
    + pad('P50 ms', 10) + ' ' + pad('P95 ms', 10) + ' ' + pad('min ms', 10) + ' ' + pad('max ms', 10) + ' flag');
  for (const variant of variants) {
    lines.push(pad(variant.variant, 12) + ' '
      + pad(variant.successes + '/' + variant.attempts, 15) + ' '
      + pad(fixed(variant.p50Ms), 10) + ' ' + pad(fixed(variant.p95Ms), 10) + ' '
      + pad(fixed(variant.minMs), 10) + ' ' + pad(fixed(variant.maxMs), 10) + ' '
      + (variant.insufficient ? 'insufficient samples' : ''));
  }
  if (variants.length === 0) lines.push('(none)');

  lines.push('');
  lines.push('[paired overhead] (E5-PROFILE - E5-OFF by iteration; both ok and non-null httpReadyMs)');
  lines.push('n=' + (overhead.n ?? 0)
    + '  median=' + signed(overhead.medianMs) + ' ms'
    + '  P50=' + signed(overhead.p50Ms) + ' ms'
    + '  P95=' + signed(overhead.p95Ms) + ' ms'
    + '  OFF P50=' + fixed(overhead.offP50Ms) + ' ms'
    + '  percent=' + percentText(overhead.percent));
  if ((overhead.n ?? 0) === 0) {
    lines.push('verdict: insufficient (n=0; need >= 10 paired iterations)');
  } else if (overhead.insufficient) {
    lines.push('verdict: insufficient (n=' + overhead.n + ' < 10; values shown, no acceptance verdict)');
  } else if (overhead.acceptable === null || overhead.acceptable === undefined) {
    lines.push('verdict: insufficient (no evaluable OFF P50 percentage)');
  } else if (overhead.acceptable) {
    lines.push('verdict: acceptable (median <= 5% of OFF P50 and median < 800 ms)');
  } else {
    lines.push('verdict: over (median > 5% of OFF P50 or median >= 800 ms)');
  }
  lines.push('note: the 5% / 800 ms rule is an acceptance target, not an SLA.');

  lines.push('');
  lines.push('[CPU/ELU] (per-round ratios; median across rounds)');
  lines.push(pad('variant', 12) + ' ' + pad('CPU n', 7) + ' ' + pad('CPU/httpReady', 15) + ' '
    + pad('ELU n', 7) + ' ' + pad('ELU active', 12));
  for (const row of cpuElu) {
    lines.push(pad(row.variant, 12) + ' ' + pad(row.cpuN, 7) + ' '
      + pad(ratioText(row.cpuRatioMedian), 15) + ' '
      + pad(row.eluN, 7) + ' ' + pad(ratioText(row.eluActiveMedian), 12));
  }
  lines.push('note: ELU idle 不等于磁盘等待（event-loop idle is not disk wait）; CPU ratio = (nodeUserCpuMs+nodeSystemCpuMs)/httpReadyMs.');

  lines.push('');
  lines.push('[profile integrity]');
  lines.push('E5-PROFILE rows: ' + (integrity.profileRows ?? 0));
  const files = integrity.files ?? { present: 0, missing: 0, empty: 0, invalid: 0 };
  lines.push('profile files: present=' + files.present + ' missing=' + files.missing
    + ' empty=' + files.empty + ' invalid=' + files.invalid);
  lines.push('median profileSampleCount: ' + fixed(integrity.medianSampleCount));
  lines.push('median profileCaptureMs: ' + fixed(integrity.medianCaptureMs) + ' ms');
  lines.push('median stopLatencyMs: ' + fixed(integrity.medianStopLatencyMs) + ' ms');
  if (integrity.complete) {
    lines.push('status: complete');
  } else {
    lines.push('status: not-complete (' + (integrity.reasons ?? ['unknown']).join('; ') + ')');
  }
  if ((integrity.offPresent ?? 0) > 0) {
    lines.push('warning: ' + integrity.offPresent + ' E5-OFF row(s) unexpectedly have a profile file.');
  }
  lines.push('note: a missing/empty/invalid profile file is counted, never treated as a success.');

  lines.push('');
  lines.push('[categories] (timeDeltas-weighted; share = median of per-run shares, zeros included)');
  lines.push(pad('category', 22) + ' ' + pad('samples', 10) + ' ' + pad('time ms', 12) + ' '
    + pad('share', 9) + ' runs');
  for (const category of categories) {
    lines.push(pad(category.category, 22) + ' ' + pad(category.samples, 10) + ' '
      + pad(fixed(category.timeMs), 12) + ' ' + pad(ratioText(category.share * 100), 9) + ' ' + category.runs);
  }
  if (categories.length === 0) lines.push('(none: no readable profile files)');
  lines.push('note: categories come from stack frame url/functionName only; raw frames are never printed.');

  lines.push('');
  lines.push('[data quality]');
  const reasons = Object.entries(quality.skipReasons ?? {});
  lines.push('skipped lines: ' + (quality.skipped ?? 0)
    + (reasons.length === 0 ? '' : ' (' + reasons.map(([key, count]) => key + '=' + count).join(', ') + ')'));
  lines.push('invalid enums: ' + (quality.invalidEnums ?? 0));
  lines.push('result != ok: ' + (quality.failed ?? 0));
  const errorCodes = Object.entries(quality.errorCodes ?? {});
  lines.push('errorCode: ' + (errorCodes.length === 0
    ? '(none)'
    : errorCodes.map(([code, count]) => code + '=' + count).join(', ')));
  const insufficient = quality.insufficient ?? [];
  lines.push('success samples < 10: ' + (insufficient.length === 0
    ? '(none)'
    : insufficient.map((item) => item.variant + ' (' + item.successes + '/' + item.attempts + ')').join(', ')));
  lines.push('privacy: only fixed enums, numbers and category names are emitted; frame urls, function names and paths are never printed.');

  return lines.join('\n');
}

function profileResultForFile(filePath) {
  let text;
  try {
    text = readFileSync(filePath, 'utf8');
  } catch (error) {
    return { state: error.code === 'ENOENT' ? 'missing' : 'invalid', result: null };
  }
  if (text.replace(/^\uFEFF/, '').length === 0) return { state: 'empty', result: null };
  let profile;
  try {
    profile = JSON.parse(text);
  } catch {
    return { state: 'invalid', result: null };
  }
  const result = categorizeProfile(profile);
  if (!result.ok) return { state: result.code === 'empty' ? 'empty' : 'invalid', result: null };
  return { state: 'present', result };
}

/**
 * Decorates records with the profile file state and returns the categorized profiles of result=ok
 * E5-PROFILE rows. Missing/empty/invalid files are states, never successes. The 12-hex runId is
 * validated at parse time, so no record can escape the profiles directory.
 */
function attachProfiles(records, profilesDir) {
  const cache = new Map();
  const categorized = new Map();
  for (const record of records) {
    if (record.runId === null) {
      record.profileFile = 'missing';
      continue;
    }
    if (!cache.has(record.runId)) {
      cache.set(record.runId, profileResultForFile(join(profilesDir, record.runId + '.cpuprofile')));
    }
    const entry = cache.get(record.runId);
    record.profileFile = entry.state;
    if (record.variant === 'E5-PROFILE' && record.result === 'ok' && entry.result !== null) {
      categorized.set(record.runId, entry.result);
    }
  }
  records.profileResults = [...categorized.values()];
  return records;
}

const USAGE = [
  'usage: node tooling/web-startup-profile-report.mjs --log <jsonl> --profiles <dir>',
  '       [--out <csv>] [--summary <md>] [--matrix <json>] [--help]',
  '',
  '  --log       required; E5 JSONL file, or - to read stdin',
  '  --profiles  required; directory containing <runId>.cpuprofile files',
  '  --out       write the fixed 15-column CSV there; omitted: CSV to stdout',
  '  --summary   also write the stderr summary to this Markdown file',
  '  --matrix    write the matrix JSON to this file',
  '  --help      show this usage',
  '',
  'The summary (and every warning) always goes to stderr. stdout carries CSV only.',
  'Exit codes: 0 records reported, 1 no valid record, 2 argument or file error.',
].join('\n');

const OPTIONS = {
  '--log': 'log',
  '--profiles': 'profiles',
  '--out': 'out',
  '--summary': 'summary',
  '--matrix': 'matrix',
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
  if (!options.profiles) throw new UsageError('--profiles is required');
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
    console.error('web-startup-profile-report: ' + error.message);
    console.error(USAGE);
    return 2;
  }
  if (parsed.help) {
    console.log(USAGE);
    return 0;
  }
  const options = parsed.options;

  for (const key of ['out', 'summary', 'matrix']) {
    if (options[key] !== undefined && !outputDirectoryExists(options[key])) {
      console.error('web-startup-profile-report: output directory does not exist');
      return 2;
    }
  }

  try {
    if (!statSync(options.profiles).isDirectory()) {
      console.error('web-startup-profile-report: --profiles is not a directory');
      return 2;
    }
  } catch {
    console.error('web-startup-profile-report: --profiles directory does not exist');
    return 2;
  }

  let text;
  try {
    text = options.log === '-' ? readFileSync(0, 'utf8') : readFileSync(options.log, 'utf8');
  } catch {
    console.error('web-startup-profile-report: cannot read --log');
    return 2;
  }

  const parsedLog = parseRecords(text);
  if (parsedLog.records.length === 0) {
    console.error('web-startup-profile-report: no valid E5 records found (nothing to report)');
    console.error('web-startup-profile-report: skipped lines: ' + parsedLog.skipped
      + '  invalid enums: ' + parsedLog.invalidEnums);
    return 1;
  }

  const records = attachProfiles(parsedLog.records, options.profiles);
  const summary = summarize(records, parsedLog);
  const summaryText = formatSummary(summary);

  try {
    if (options.out !== undefined) writeFileSync(options.out, toCsv(records));
    if (options.summary !== undefined) writeFileSync(options.summary, summaryText + '\n');
    if (options.matrix !== undefined) writeFileSync(options.matrix, JSON.stringify(toMatrix(summary), null, 2) + '\n');
  } catch (error) {
    console.error('web-startup-profile-report: cannot write output: ' + error.message);
    return 2;
  }

  if (options.out === undefined) process.stdout.write(toCsv(records));
  console.error(summaryText);
  return 0;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = runCli(process.argv.slice(2));
}
