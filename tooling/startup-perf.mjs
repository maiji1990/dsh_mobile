#!/usr/bin/env node
// Startup performance log parser: DshMobile startup-perf JSON lines -> fixed-column CSV + P50/P95 summary.
// Dependency-free. Observation only: it reads logcat text and never touches the app or its startup semantics.
//
//   node tooling/startup-perf.mjs --log <path|-> [--out <csvPath>] [--scenario A] [--device <name>]
//        [--android <ver>] [--commit <sha>] [--node <ver>] [--dsh <ver>] [--help]
//
// stdout carries CSV only (or nothing when --out is given); every diagnostic, including the summary table,
// goes to stderr. See docs/STARTUP_PERF.md for the log format, stage list, derived groups and privacy rules.

import { readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const MARKER = 'startup-perf ';

export const STAGES = [
  'ui.click_to_bridge',
  'coordinator.lock_wait',
  'coordinator.sync',
  'coordinator.journal',
  'guest_assets.total',
  'guest_assets.shim',
  'guest_assets.scripts',
  'guest_assets.corepack',
  'guest_assets.wrappers',
  'host.preconditions',
  'host.proc_scan',
  'sandbox.spawn',
  'sandbox.fixed_delay',
  'host.cleanup_processes',
  'host.lock_sweep',
  'dsh.command_env',
  'dsh.spawn',
  'dsh.first_output',
  'dsh.token_available',
  'http.first_port_open',
  'http.first_response',
  'protocol.connected',
  'protocol.initialized',
  'host.ready',
  'bridge.complete',
  'web.activity_create',
  'web.load_url',
  'web.page_start',
  'web.commit_visible',
  'web.page_finish',
];

export const RESULTS = ['ok', 'failed', 'canceled', 'busy', 'incomplete', 'refused'];

export const CSV_COLUMNS = [
  'runId', 'scenario', 'device', 'android', 'apkCommit', 'nodeVersion', 'dshVersion',
  'stage', 'elapsedMs', 'sinceStartMs', 'result', 'cacheHit',
];

const HOST_GROUPS = ['coordinator', 'guest_assets', 'sandbox', 'cleanup', 'node_init', 'http_wait', 'host_total'];
const GROUP_LABELS = {
  coordinator: 'coordinator',
  guest_assets: 'guest_assets',
  sandbox: 'sandbox',
  cleanup: 'cleanup',
  node_init: 'node_init',
  http_wait: 'http_wait',
  host_total: 'host_total',
};

/** Nearest-rank percentile over an already ascending array. Returns null for an empty sample. */
export function percentile(sorted, p) {
  if (!Array.isArray(sorted) || sorted.length === 0 || !Number.isFinite(p) || p <= 0 || p > 1) return null;
  const index = Math.ceil(p * sorted.length) - 1;
  return sorted[Math.min(Math.max(index, 0), sorted.length - 1)];
}

/**
 * Parses logcat text. Finds "startup-perf " in a line, takes the slice from the first "{" after it to the
 * last "}" of that line, and parses it as JSON. Malformed lines and non-schema-1 records are counted, never thrown.
 * Returns { records, skipped }; details are reduced to the cacheHit boolean so no other detail can leak downstream.
 */
export function parseLog(text) {
  const records = [];
  let skipped = 0;
  for (const line of String(text ?? '').split(/\r?\n/)) {
    const marker = line.indexOf(MARKER);
    if (marker < 0) continue;
    const open = line.indexOf('{', marker + MARKER.length);
    const close = line.lastIndexOf('}');
    if (open < 0 || close < open) {
      skipped += 1;
      continue;
    }
    let value;
    try {
      value = JSON.parse(line.slice(open, close + 1));
    } catch {
      skipped += 1;
      continue;
    }
    const record = toRecord(value);
    if (record === null) {
      skipped += 1;
      continue;
    }
    records.push(record);
  }
  return { records, skipped };
}

function toRecord(value) {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) return null;
  if (value.schema !== 1) return null;
  if (typeof value.runId !== 'string' || value.runId === '') return null;
  if (typeof value.stage !== 'string' || value.stage === '') return null;
  if (!Number.isFinite(value.elapsedMs) || !Number.isFinite(value.sinceStartMs)) return null;
  if (typeof value.result !== 'string' || !RESULTS.includes(value.result)) return null;
  const details = value.details;
  const cacheHit = details !== null && typeof details === 'object' && typeof details.cacheHit === 'boolean'
    ? details.cacheHit
    : undefined;
  return {
    runId: value.runId,
    stage: value.stage,
    elapsedMs: value.elapsedMs,
    sinceStartMs: value.sinceStartMs,
    result: value.result,
    cacheHit,
  };
}

function stageOrder(name) {
  const index = STAGES.indexOf(name);
  return index < 0 ? STAGES.length : index;
}

function sampleSummary(name, values) {
  const sorted = values.slice().sort((a, b) => a - b);
  return { name, n: sorted.length, p50: percentile(sorted, 0.5), p95: percentile(sorted, 0.95) };
}

function runValue(stages, name) {
  return stages.has(name) ? stages.get(name) : null;
}

function runSum(stages, names) {
  let total = 0;
  for (const name of names) {
    const value = runValue(stages, name);
    if (value === null) return null;
    total += value;
  }
  return total;
}

function groupValue(group, stages) {
  switch (group) {
    case 'coordinator':
      return runSum(stages, ['coordinator.lock_wait', 'coordinator.sync', 'coordinator.journal']);
    case 'guest_assets':
      return runValue(stages, 'guest_assets.total');
    case 'sandbox':
      return runSum(stages, ['sandbox.spawn', 'sandbox.fixed_delay']);
    case 'cleanup':
      return runSum(stages, ['host.proc_scan', 'host.cleanup_processes', 'host.lock_sweep']);
    case 'node_init':
      return runValue(stages, 'http.first_response');
    case 'http_wait': {
      const ready = runValue(stages, 'host.ready');
      const first = runValue(stages, 'http.first_response');
      return ready === null || first === null ? null : ready - first;
    }
    case 'host_total':
      return runValue(stages, 'bridge.complete');
    default:
      return null;
  }
}

/**
 * Aggregates parsed records into raw stage percentiles, derived per-runId groups, web.* rows (kept separate),
 * per-run consistency warnings, and screening candidates. Missing components skip a group for that run instead
 * of producing NaN.
 */
export function aggregate(records) {
  const input = Array.isArray(records) ? records : [];
  const byStage = new Map();
  const byRun = new Map();
  for (const record of input) {
    if (!byStage.has(record.stage)) byStage.set(record.stage, []);
    byStage.get(record.stage).push(record.elapsedMs);
    if (!byRun.has(record.runId)) byRun.set(record.runId, new Map());
    // A repeated stage within one run (retry) keeps the latest measurement, not the sum.
    byRun.get(record.runId).set(record.stage, record.elapsedMs);
  }

  const stageNames = [...byStage.keys()].sort((a, b) => stageOrder(a) - stageOrder(b) || a.localeCompare(b));
  const stages = stageNames.map((name) => sampleSummary(name, byStage.get(name)));
  const web = stages
    .filter((stage) => stage.name.startsWith('web.'))
    .map((stage) => ({ ...stage, candidate: stage.p95 !== null && stage.p95 > 2000 }));

  const runs = [...byRun.entries()].map(([runId, stageValues]) => ({ runId, stages: stageValues }));
  const groupSamples = new Map(HOST_GROUPS.map((name) => [name, []]));
  for (const run of runs) {
    for (const name of HOST_GROUPS) {
      const value = groupValue(name, run.stages);
      if (value !== null && Number.isFinite(value)) groupSamples.get(name).push(value);
    }
  }

  const hostP50 = sampleSummary('host_total', groupSamples.get('host_total')).p50;
  const groups = HOST_GROUPS.map((name) => {
    const summary = sampleSummary(name, groupSamples.get(name));
    const share = hostP50 !== null && hostP50 > 0 && summary.p50 !== null ? (summary.p50 / hostP50) * 100 : null;
    const candidate = name !== 'host_total'
      && ((share !== null && share >= 30) || (summary.p95 !== null && summary.p95 > 2000));
    return { ...summary, share, candidate };
  });

  const consistency = runs.map((run) => {
    const warnings = [];
    const hostTotal = groupValue('host_total', run.stages);
    const parts = HOST_GROUPS
      .filter((name) => name !== 'host_total')
      .map((name) => groupValue(name, run.stages))
      .filter((value) => value !== null && Number.isFinite(value));
    if (hostTotal === null) {
      warnings.push('missing bridge.complete');
    } else if (parts.length > 0) {
      const sum = parts.reduce((total, value) => total + value, 0);
      if (sum > hostTotal * 1.05) {
        warnings.push(`group sum ${sum.toFixed(1)} ms > host_total ${hostTotal.toFixed(1)} ms * 1.05 (${(hostTotal * 1.05).toFixed(1)} ms)`);
      }
    }
    return { runId: run.runId, warnings };
  });

  const candidates = [
    ...groups.filter((group) => group.candidate).map((group) => ({ name: group.name, share: group.share, p95: group.p95 })),
    ...web.filter((stage) => stage.candidate).map((stage) => ({ name: stage.name, share: null, p95: stage.p95 })),
  ];

  return {
    recordCount: input.length,
    runCount: runs.length,
    skipped: 0,
    stages,
    groups,
    web,
    consistency,
    candidates,
  };
}

function csvCell(value) {
  if (value === null || value === undefined) return '';
  const text = String(value);
  return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
}

function csvNumber(value) {
  return Number.isFinite(value) ? String(value) : '';
}

/** Fixed-column CSV. Only the 12 documented columns are written; details never reach this function. */
export function toCsv(records, meta = {}) {
  const m = meta ?? {};
  const lines = [CSV_COLUMNS.join(',')];
  for (const record of Array.isArray(records) ? records : []) {
    const cacheHit = record.cacheHit === true ? 'true' : record.cacheHit === false ? 'false' : '';
    const row = [
      record.runId,
      m.scenario,
      m.device,
      m.android,
      m.apkCommit,
      m.nodeVersion,
      m.dshVersion,
      record.stage,
      csvNumber(record.elapsedMs),
      csvNumber(record.sinceStartMs),
      record.result,
      cacheHit,
    ];
    lines.push(row.map(csvCell).join(','));
  }
  return lines.join('\n') + '\n';
}

function fixed(value) {
  return value === null || value === undefined ? '—' : value.toFixed(1);
}

function pad(text, width) {
  const value = String(text);
  return value.length >= width ? value : value + ' '.repeat(width - value.length);
}

/** Human-readable summary for stderr. Never includes details or raw log content. */
export function formatSummary(aggregation, meta = {}) {
  const result = aggregation ?? { recordCount: 0, runCount: 0, skipped: 0, stages: [], groups: [], web: [], consistency: [], candidates: [] };
  const context = [
    meta?.scenario ? `scenario=${meta.scenario}` : null,
    meta?.device ? `device=${meta.device}` : null,
    meta?.android ? `android=${meta.android}` : null,
  ].filter(Boolean).join('  ');
  const lines = [];
  lines.push('startup-perf summary');
  lines.push(`records: ${result.recordCount}  runs: ${result.runCount}  skipped lines: ${result.skipped ?? 0}`);
  if (context) lines.push(context);

  lines.push('');
  lines.push('[stages]');
  lines.push(`${pad('stage', 32)} ${pad('n', 4)} ${pad('P50 ms', 10)} ${pad('P95 ms', 10)}`);
  for (const stage of result.stages) {
    lines.push(`${pad(stage.name, 32)} ${pad(stage.n, 4)} ${pad(fixed(stage.p50), 10)} ${pad(fixed(stage.p95), 10)}`);
  }
  if (result.stages.length === 0) lines.push('(none)');

  lines.push('');
  lines.push('[derived groups] (per-runId sums; P50 % is share of host_total P50; screening only)');
  lines.push(`${pad('group', 32)} ${pad('n', 4)} ${pad('P50 ms', 10)} ${pad('P95 ms', 10)} ${pad('P50/host_total', 14)} flag`);
  for (const group of result.groups) {
    const share = group.share === null ? '—' : `${group.share.toFixed(1)}%`;
    lines.push(`${pad(GROUP_LABELS[group.name] ?? group.name, 32)} ${pad(group.n, 4)} ${pad(fixed(group.p50), 10)} ${pad(fixed(group.p95), 10)} ${pad(share, 14)} ${group.candidate ? 'candidate' : ''}`);
  }

  lines.push('');
  lines.push('[web.* stages] (elapsedMs from LoadUrl; web.activity_create is activity creation; excluded from host_total %)');
  lines.push(`${pad('stage', 32)} ${pad('n', 4)} ${pad('P50 ms', 10)} ${pad('P95 ms', 10)} flag`);
  for (const stage of result.web) {
    lines.push(`${pad(stage.name, 32)} ${pad(stage.n, 4)} ${pad(fixed(stage.p50), 10)} ${pad(fixed(stage.p95), 10)} ${stage.candidate ? 'candidate' : ''}`);
  }
  if (result.web.length === 0) lines.push('(none)');

  lines.push('');
  lines.push('[run consistency]');
  const flagged = (result.consistency ?? []).filter((run) => run.warnings.length > 0);
  if (flagged.length === 0) {
    lines.push('  (no inconsistency)');
  } else {
    for (const run of flagged) {
      for (const warning of run.warnings) lines.push(`  ${run.runId}  warning: ${warning}`);
    }
  }

  lines.push('');
  lines.push('[optimization candidates] (screening threshold, not an SLA)');
  if ((result.candidates ?? []).length === 0) {
    lines.push('  (none at this sample size)');
  } else {
    for (const candidate of result.candidates) {
      const share = candidate.share === null ? 'no host_total share' : `${candidate.share.toFixed(1)}% of host_total P50`;
      lines.push(`  ${candidate.name}  P95 ${fixed(candidate.p95)} ms  ${share}`);
    }
  }
  lines.push('rule: a group is a candidate when its P50 is >= 30% of host_total P50, or its P95 > 2000 ms.');
  lines.push('      This is a screening threshold for optimization candidates, not an SLA; web.* has no host_total share.');
  return lines.join('\n');
}

const USAGE = [
  'usage: node tooling/startup-perf.mjs --log <path|-> [--out <csvPath>] [--scenario A] [--device <name>]',
  '       [--android <ver>] [--commit <sha>] [--node <ver>] [--dsh <ver>] [--help]',
  '',
  '  --log   required; logcat text file, or - to read stdin',
  '  --out   write fixed-column CSV there (parent directory must exist); omitted: CSV to stdout',
  '  --help  show this usage',
  '',
  'summary and warnings always go to stderr; stdout is CSV only.',
].join('\n');

const OPTIONS = {
  '--log': 'log',
  '--out': 'out',
  '--scenario': 'scenario',
  '--device': 'device',
  '--android': 'android',
  '--commit': 'apkCommit',
  '--node': 'nodeVersion',
  '--dsh': 'dshVersion',
};

class UsageError extends Error {}

function parseArgs(argv) {
  const options = {};
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === '--help' || arg === '-h') return { help: true };
    const key = OPTIONS[arg];
    if (key === undefined) throw new UsageError(`unknown argument: ${arg}`);
    const value = argv[i + 1];
    if (value === undefined || value.startsWith('--')) throw new UsageError(`missing value for ${arg}`);
    options[key] = value;
    i += 1;
  }
  if (!options.log) throw new UsageError('--log is required');
  return { options };
}

function runCli(argv) {
  let parsed;
  try {
    parsed = parseArgs(argv);
  } catch (error) {
    if (!(error instanceof UsageError)) throw error;
    console.error(`startup-perf: ${error.message}`);
    console.error(USAGE);
    return 2;
  }
  if (parsed.help) {
    console.log(USAGE);
    return 0;
  }
  const options = parsed.options;
  const meta = {
    scenario: options.scenario,
    device: options.device,
    android: options.android,
    apkCommit: options.apkCommit,
    nodeVersion: options.nodeVersion,
    dshVersion: options.dshVersion,
  };

  let outDir;
  if (options.out !== undefined) {
    outDir = dirname(resolve(options.out));
    try {
      if (!statSync(outDir).isDirectory()) throw new Error('not a directory');
    } catch {
      console.error(`startup-perf: output directory does not exist: ${outDir}`);
      return 2;
    }
  }

  let text;
  try {
    text = options.log === '-' ? readFileSync(0, 'utf8') : readFileSync(options.log, 'utf8');
  } catch (error) {
    console.error(`startup-perf: cannot read --log: ${error.message}`);
    return 2;
  }

  const { records, skipped } = parseLog(text);
  if (records.length === 0) {
    console.error('startup-perf: no startup-perf records found (nothing to summarise)');
    return 1;
  }
  const aggregation = aggregate(records);
  aggregation.skipped = skipped;
  const summary = formatSummary(aggregation, meta);

  if (options.out !== undefined) {
    try {
      writeFileSync(options.out, toCsv(records, meta));
    } catch (error) {
      console.error(`startup-perf: cannot write --out: ${error.message}`);
      return 2;
    }
    console.error(summary);
  } else {
    process.stdout.write(toCsv(records, meta));
    console.error(summary);
  }
  return 0;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = runCli(process.argv.slice(2));
}
