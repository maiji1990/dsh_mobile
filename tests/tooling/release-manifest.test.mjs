// vNext PR-02: tooling/release-manifest-build.mjs and the sign / verify commands of release-manifest-sign.mjs, as
// release-publish.yml runs them. The C# side (ReleaseManifestTests.BuiltByTheNodeTool_*) parses the same committed
// manifest, so the builder and the app's parser cannot drift apart.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { generateKeyPairSync } from 'node:crypto';
import { cpSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { buildManifest, describeFile, serialize, SpecError } from '../../tooling/release-manifest-build.mjs';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const built = join(repo, 'tests', 'TestVectors', 'release-manifest', 'built');
const spec = () => JSON.parse(readFileSync(join(built, 'spec.json'), 'utf8'));
const options = { channel: 'preview', generatedAt: '2026-11-16T00:00:00Z', expiresDays: 30, describe: describeFile(join(built, 'artifacts')) };

function tool(script, args, env = {}) {
  return spawnSync(process.execPath, [join(repo, 'tooling', script), ...args], { encoding: 'utf8', env: { ...process.env, ...env } });
}

test('the builder reproduces the committed manifest byte for byte', () => {
  assert.equal(serialize(buildManifest(spec(), options)), readFileSync(join(built, 'release-manifest.json'), 'utf8'));
});

test('urls point at this repository\'s versioned release, digests come from the files', () => {
  const m = buildManifest(spec(), options);
  assert.equal(m.artifacts[0].url, 'https://github.com/maiji1990/dsh_mobile/releases/download/runtime-0.2.0/runtime-bundle-arm64-0.2.0.zip');
  assert.match(m.artifacts[0].sha256, /^[0-9a-f]{64}$/);
  assert.equal(m.expiresAt, '2026-12-16T00:00:00Z');
});

test('spec errors are refused before anything is signed', () => {
  const cases = [
    [(s) => { s.artifacts[1].sourceCommit = undefined; }, /must pin sourceRepo, sourceTag and sourceCommit/],
    [(s) => { s.artifacts[1].sourceCommit = 'abc1234'; }, /full commit hash/],
    [(s) => { s.artifacts[0].file = '../runtime.zip'; }, /plain file name/],
    [(s) => { s.artifacts[0].file = 'runtime.sh'; }, /plain file name/],
    [(s) => { s.artifacts[0].kind = 'plugin'; }, /kind must be one of/],
    [(s) => { s.artifacts[0].version = 'latest'; }, /version is malformed/],
    [(s) => { s.artifacts[0].providesNode = 'latest'; }, /providesNode is malformed/],
    [(s) => { s.artifacts[1].providesNode = '26.13.1'; }, /providesNode is only valid for runtime/],
    [(s) => { s.artifacts.push({ ...s.artifacts[0] }); }, /listed twice/],
    [(s) => { s.repository = 'not a repo'; }, /owner\/name/],
    [(s) => { s.artifacts = []; }, /non-empty/],
  ];
  for (const [edit, message] of cases) {
    const s = spec();
    edit(s);
    assert.throws(() => buildManifest(s, options), (e) => e instanceof SpecError && message.test(e.message), message.source);
  }
  assert.throws(() => buildManifest(spec(), { ...options, channel: 'beta' }), SpecError);
  assert.throws(() => buildManifest(spec(), { ...options, expiresDays: 91 }), SpecError);
  assert.throws(() => buildManifest(spec(), { ...options, generatedAt: '2026-11-16T00:00:00.123Z' }), SpecError);
});

test('sign then verify against a trusted-keys file; tampering, wrong channel and untrusted keys fail', () => {
  const dir = mkdtempSync(join(tmpdir(), 'dshm-sign-'));
  try {
    const { privateKey, publicKey } = generateKeyPairSync('ed25519');
    const other = generateKeyPairSync('ed25519').publicKey;
    const raw = (k) => Buffer.from(k.export({ format: 'jwk' }).x, 'base64url').toString('base64');
    const manifest = join(dir, 'release-manifest.json');
    cpSync(join(built, 'release-manifest.json'), manifest);
    const keys = join(dir, 'keys.txt');
    writeFileSync(keys, `# trusted\n${raw(publicKey)}\n`);

    const signed = tool('release-manifest-sign.mjs', ['sign', manifest], { DSHM_RELEASE_SIGNING_KEY: privateKey.export({ format: 'pem', type: 'pkcs8' }) });
    assert.equal(signed.status, 0, signed.stderr);
    const ok = tool('release-manifest-sign.mjs', ['verify', manifest, '--keys', keys, '--channel', 'preview']);
    assert.equal(ok.status, 0, ok.stderr);
    assert.match(ok.stdout, /valid signature by ed25519:[0-9a-f]{16}, channel preview/);

    assert.notEqual(tool('release-manifest-sign.mjs', ['verify', manifest, '--keys', keys, '--channel', 'stable']).status, 0);
    const untrusted = join(dir, 'other.txt');
    writeFileSync(untrusted, `${raw(other)}\n`);
    assert.notEqual(tool('release-manifest-sign.mjs', ['verify', manifest, '--keys', untrusted]).status, 0);
    const empty = join(dir, 'empty.txt');
    writeFileSync(empty, '# nothing\n');
    assert.notEqual(tool('release-manifest-sign.mjs', ['verify', manifest, '--keys', empty]).status, 0);

    writeFileSync(manifest, readFileSync(manifest, 'utf8').replace('"size": 41', '"size": 42'));
    assert.notEqual(tool('release-manifest-sign.mjs', ['verify', manifest, '--keys', keys]).status, 0);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('the app\'s trusted-keys file parses (and is empty until the owner adds the release key)', () => {
  const text = readFileSync(join(repo, 'src', 'DshMobile.Runtime', 'Updates', 'release-trusted-keys.txt'), 'utf8');
  const keys = text.split(/\r?\n/).map((l) => l.trim()).filter((l) => l !== '' && !l.startsWith('#'));
  for (const k of keys) assert.equal(Buffer.from(k, 'base64').length, 32, k);
});
