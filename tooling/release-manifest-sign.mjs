#!/usr/bin/env node
// Release manifest signing (ADR-0022). Dependency-free: Node's built-in Ed25519.
//
//   node tooling/release-manifest-sign.mjs keygen <out-dir>
//       New Ed25519 key pair. Writes <out-dir>/release-signing-key.pem (PKCS#8, mode 0600) and prints the raw public
//       key (base64) and its key id. Keep the private key out of the repository: it belongs only in the protected
//       release environment (a GitHub Actions environment secret with required reviewers).
//   node tooling/release-manifest-sign.mjs sign <release-manifest.json> [--key <pem>] [--append]
//       Writes <release-manifest.json>.sig. The key comes from --key or from the DSHM_RELEASE_SIGNING_KEY environment
//       variable (PEM text). --append adds a signature to an existing envelope (key rotation: old + new key).
//   node tooling/release-manifest-sign.mjs pubkey <pem>
//       Prints the raw public key (base64) and key id of a private key.
//
// Signed message = "DSHM-RELEASE-MANIFEST-V1\n" + the manifest file's exact bytes (ReleaseManifestVerifier.Domain).
// The key id is "ed25519:" + the first 16 hex digits of SHA-256(raw public key) (ReleaseTrust.KeyId).

import { createHash, createPrivateKey, createPublicKey, generateKeyPairSync, sign } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const DOMAIN = Buffer.from('DSHM-RELEASE-MANIFEST-V1\n', 'ascii');

function rawPublicKey(publicKey) {
  return Buffer.from(publicKey.export({ format: 'jwk' }).x, 'base64url');
}

function keyId(raw) {
  return 'ed25519:' + createHash('sha256').update(raw).digest('hex').slice(0, 16);
}

function fail(message) {
  console.error(message);
  process.exit(2);
}

function loadPrivateKey(args) {
  const i = args.indexOf('--key');
  const pem = i >= 0 ? readFileSync(args[i + 1], 'utf8') : process.env.DSHM_RELEASE_SIGNING_KEY;
  if (!pem) fail('no key: pass --key <pem> or set DSHM_RELEASE_SIGNING_KEY');
  const key = createPrivateKey(pem);
  if (key.asymmetricKeyType !== 'ed25519') fail('the signing key must be Ed25519');
  return key;
}

const [cmd, ...args] = process.argv.slice(2);
if (cmd === 'keygen') {
  const dir = args[0] ?? fail('usage: keygen <out-dir>');
  mkdirSync(dir, { recursive: true });
  const target = join(dir, 'release-signing-key.pem');
  if (existsSync(target)) fail(`${target} exists; refusing to overwrite a key`);
  const { publicKey, privateKey } = generateKeyPairSync('ed25519');
  writeFileSync(target, privateKey.export({ format: 'pem', type: 'pkcs8' }), { mode: 0o600 });
  const raw = rawPublicKey(publicKey);
  console.log(JSON.stringify({ privateKey: target, publicKey: raw.toString('base64'), keyId: keyId(raw) }, null, 2));
} else if (cmd === 'sign') {
  const manifestPath = args[0] ?? fail('usage: sign <release-manifest.json> [--key <pem>] [--append]');
  const privateKey = loadPrivateKey(args);
  const raw = rawPublicKey(createPublicKey(privateKey));
  const sig = sign(null, Buffer.concat([DOMAIN, readFileSync(manifestPath)]), privateKey);
  const sigPath = manifestPath + '.sig';
  const envelope = args.includes('--append') && existsSync(sigPath)
    ? JSON.parse(readFileSync(sigPath, 'utf8'))
    : { schema: 1, signatures: [] };
  envelope.signatures = envelope.signatures.filter((s) => s.keyId !== keyId(raw));
  envelope.signatures.push({ alg: 'ed25519', keyId: keyId(raw), sig: sig.toString('base64') });
  writeFileSync(sigPath, JSON.stringify(envelope, null, 2) + '\n');
  console.log(`${sigPath}: signed by ${keyId(raw)}`);
} else if (cmd === 'pubkey') {
  const raw = rawPublicKey(createPublicKey(createPrivateKey(readFileSync(args[0] ?? fail('usage: pubkey <pem>'), 'utf8'))));
  console.log(JSON.stringify({ publicKey: raw.toString('base64'), keyId: keyId(raw) }, null, 2));
} else {
  fail('usage: release-manifest-sign.mjs keygen <out-dir> | sign <manifest> [--key <pem>] [--append] | pubkey <pem>');
}
