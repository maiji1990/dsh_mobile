#!/usr/bin/env node
// Builds an unsigned release manifest (ADR-0022 schema 1) from a reviewed artifact spec and the artifact files.
// Dependency-free. Deterministic: the same spec, files and --generated-at give the same bytes.
//
//   node tooling/release-manifest-build.mjs --spec <spec.json> --dir <artifact dir> --channel <stable|preview>
//        [--generated-at <RFC 3339 UTC>] [--expires-days <1..90, default 30>] --out <release-manifest.json>
//
// spec.json (committed under release/, reviewed like code):
//   {"repository": "maiji1990/dsh_mobile",
//    "artifacts": [{"kind": "runtime|node|dsh|app", "version": "...", "abi": "arm64", "file": "<name in --dir>",
//                   "releaseTag": "<versioned release holding the file>", "minAppVersion"?, "requiresNode"?,
//                   "sourceRepo"?, "sourceTag"?, "sourceCommit"? (all three required for dsh)}]}
//
// sha256 and size are computed from the files; the URL is always
// https://github.com/<repository>/releases/download/<releaseTag>/<file>. The rules mirror ReleaseManifestParser, so a
// manifest this tool writes is one the app accepts (cross-checked by tests/TestVectors/release-manifest/built.*).

import { createHash } from 'node:crypto';
import { readFileSync, statSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const KINDS = ['runtime', 'node', 'dsh', 'app'];
const VERSION = /^v?[0-9][0-9A-Za-z.+-]{0,63}$/;
const FILE = /^[A-Za-z0-9][A-Za-z0-9._+-]{0,127}$/;
const ABI = /^[a-z0-9][a-z0-9_-]{0,31}$/;
const REPO = /^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/;
const TOKEN = /^[A-Za-z0-9][A-Za-z0-9._/+-]{0,127}$/;
const COMMIT = /^([0-9a-f]{40}|[0-9a-f]{64})$/;
const TAG = /^[A-Za-z0-9][A-Za-z0-9._+-]{0,127}$/;
const SUFFIXES = ['.tar.zst', '.tar.gz', '.tar', '.zip', '.apk'];
const MAX_BYTES = 4 * 1024 ** 3;

export class SpecError extends Error {}

function need(cond, message) {
  if (!cond) throw new SpecError(message);
}

/** Pure: spec + a stat/hash function → manifest object (field order fixed). */
export function buildManifest(spec, { channel, generatedAt, expiresDays, describe }) {
  need(channel === 'stable' || channel === 'preview', `channel must be stable or preview, got ${channel}`);
  need(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$/.test(generatedAt), `generatedAt must be UTC RFC 3339 without fractions, got ${generatedAt}`);
  need(Number.isInteger(expiresDays) && expiresDays >= 1 && expiresDays <= 90, `expires-days must be 1..90, got ${expiresDays}`);
  need(spec && typeof spec === 'object' && REPO.test(spec.repository ?? ''), 'spec.repository must be owner/name');
  need(Array.isArray(spec.artifacts) && spec.artifacts.length > 0, 'spec.artifacts must be a non-empty array');
  const seen = new Set();
  const artifacts = spec.artifacts.map((a, i) => {
    const at = `artifacts[${i}]`;
    need(KINDS.includes(a.kind), `${at}.kind must be one of ${KINDS.join(', ')}`);
    need(VERSION.test(a.version ?? ''), `${at}.version is malformed`);
    need(ABI.test(a.abi ?? ''), `${at}.abi is malformed`);
    need(FILE.test(a.file ?? '') && SUFFIXES.some((s) => a.file.endsWith(s)), `${at}.file must be a plain file name ending in ${SUFFIXES.join(' / ')}`);
    need(TAG.test(a.releaseTag ?? ''), `${at}.releaseTag is malformed`);
    for (const k of ['minAppVersion', 'providesNode']) need(a[k] === undefined || VERSION.test(a[k]), `${at}.${k} is malformed`);
    need(a.providesNode === undefined || a.kind === 'runtime', `${at}.providesNode is only valid for runtime artifacts`);
    for (const k of ['requiresNode', 'sourceTag']) need(a[k] === undefined || TOKEN.test(a[k]), `${at}.${k} is malformed`);
    need(a.sourceRepo === undefined || REPO.test(a.sourceRepo), `${at}.sourceRepo is malformed`);
    need(a.sourceCommit === undefined || COMMIT.test(a.sourceCommit), `${at}.sourceCommit must be a full commit hash`);
    if (a.kind === 'dsh') need(a.sourceRepo && a.sourceTag && a.sourceCommit, `${at}: a dsh layer must pin sourceRepo, sourceTag and sourceCommit`);
    const key = `${a.kind}\0${a.version}\0${a.abi}`;
    need(!seen.has(key), `${at}: ${a.kind} ${a.version} (${a.abi}) is listed twice`);
    seen.add(key);
    const { sha256, size } = describe(a.file);
    need(size > 0 && size <= MAX_BYTES, `${at}: ${a.file} has size ${size}`);
    const out = {
      kind: a.kind,
      version: a.version,
      abi: a.abi,
      archive: a.file,
      url: `https://github.com/${spec.repository}/releases/download/${a.releaseTag}/${a.file}`,
      sha256,
      size,
    };
    for (const k of ['minAppVersion', 'providesNode', 'requiresNode', 'sourceRepo', 'sourceTag', 'sourceCommit']) if (a[k] !== undefined) out[k] = a[k];
    return out;
  });
  const expiresAt = new Date(Date.parse(generatedAt) + expiresDays * 86400_000).toISOString().replace('.000Z', 'Z');
  return { schema: 1, channel, generatedAt, expiresAt, artifacts };
}

export function serialize(manifest) {
  return JSON.stringify(manifest, null, 2) + '\n';
}

export function describeFile(dir) {
  return (file) => {
    const path = join(dir, file);
    return { sha256: createHash('sha256').update(readFileSync(path)).digest('hex'), size: statSync(path).size };
  };
}

function arg(args, name, fallback) {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : fallback;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  try {
    const spec = JSON.parse(readFileSync(arg(args, '--spec') ?? (() => { throw new SpecError('--spec is required'); })(), 'utf8'));
    const out = arg(args, '--out') ?? (() => { throw new SpecError('--out is required'); })();
    const manifest = buildManifest(spec, {
      channel: arg(args, '--channel'),
      generatedAt: arg(args, '--generated-at', new Date().toISOString().replace(/\.\d{3}Z$/, 'Z')),
      expiresDays: Number(arg(args, '--expires-days', '30')),
      describe: describeFile(arg(args, '--dir', '.')),
    });
    writeFileSync(out, serialize(manifest));
    console.log(`${out}: ${manifest.channel}, ${manifest.artifacts.length} artifact(s), expires ${manifest.expiresAt}`);
  } catch (e) {
    console.error(e instanceof SpecError ? `spec error: ${e.message}` : e);
    process.exit(2);
  }
}
