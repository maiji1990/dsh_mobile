import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseVersions, updateKey, nextBundleVersion, selectDshTag, sha } from '../../tooling/update-upstream.mjs';
import { buildManifest } from '../../tooling/release-manifest-build.mjs';
import { readFileSync } from 'node:fs';

test('version precedence is numeric and stable > prerelease', () => {
  assert.equal(parseVersions('0.2.2-rc.3','0.2.2-rc.1'),1);
  assert.equal(parseVersions('0.2.10','0.2.9'),1);
  assert.equal(parseVersions('0.2.2','0.2.2-rc.4'),1);
  assert.equal(parseVersions('0.2.2-rc.10','0.2.2-rc.9'),1);
  assert.equal(parseVersions('0.2.2-rc.1','0.2.2-rc.1'),0);
  assert.equal(parseVersions('0.2.1-rc.5','0.2.2-rc.1'),-1);
});
test('source tags are filtered and ordered before npm validation', () => {
  assert.deepEqual(selectDshTag(
    ['unrelated','dsh-v0.2.2-rc.3','dsh-v0.2.2-rc.1','dsh-v0.2.1-rc.5','dsh-v0.2.10'],
    'dsh-v','0.2.1-rc.5'),['0.2.10','0.2.2-rc.3','0.2.2-rc.1']);
});
test('lock mutations preserve all unrelated pinned values', () => {
  const original='NODE_VERSION=24.19.0\nDSH_VERSION=0.2.1-rc.5\n';
  assert.equal(updateKey(original,'NODE_VERSION','26.0.1'),
    'NODE_VERSION=26.0.1\nDSH_VERSION=0.2.1-rc.5\n');
  assert.throws(()=>updateKey(original,'UNKNOWN','x'),/missing lock key/);
  assert.equal(nextBundleVersion('0.2.0'),'0.2.1');
  assert.match(sha(Buffer.from('abc')),/^[a-f0-9]{64}$/);
});
test('reviewed preview spec matches the App parser input schema', () => {
  const p=JSON.parse(readFileSync(new URL('../../release/preview.json',import.meta.url),'utf8'));
  const manifest=buildManifest(p,{channel:'preview',generatedAt:'2026-11-16T00:00:00Z',
    expiresDays:30,describe:()=>({sha256:'a'.repeat(64),size:100})});
  assert.equal(manifest.artifacts.length,2);
  assert.equal(manifest.artifacts[1].sourceTag,'dsh-v0.2.1-rc.5');
  assert.equal(manifest.artifacts[1].sourceCommit.length,40);
});
