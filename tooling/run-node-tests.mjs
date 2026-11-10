#!/usr/bin/env node
// Runs `node --test` on an explicit list of *.test.mjs files (PR-00).
//
// Why not `node --test <dir>`: on Windows, Node 22 resolves a bare directory argument as a module
// (`MODULE_NOT_FOUND`), so CI failed; newer Node accepts it. Passing explicit files behaves the same on every
// version and platform. The run also fails when zero test files or zero passing tests are found, so an empty
// glob can never turn into a green check.
//
// Usage: node tooling/run-node-tests.mjs <dir> [<dir> ...]
import { spawnSync } from 'node:child_process';
import { readdirSync, statSync } from 'node:fs';
import { join, relative, resolve } from 'node:path';

const dirs = process.argv.slice(2);
if (dirs.length === 0) {
  console.error('usage: run-node-tests.mjs <dir> [<dir> ...]');
  process.exit(2);
}

/** Recursively collects *.test.mjs, sorted for a stable order. */
function collect(dir) {
  const out = [];
  for (const name of readdirSync(dir).sort()) {
    const full = join(dir, name);
    if (statSync(full).isDirectory()) out.push(...collect(full));
    else if (name.endsWith('.test.mjs')) out.push(full);
  }
  return out;
}

const files = dirs.flatMap((d) => collect(resolve(d)));
if (files.length === 0) {
  console.error(`no *.test.mjs files under: ${dirs.join(', ')}`);
  process.exit(3);
}
console.log(`running ${files.length} test file(s):`);
for (const f of files) console.log(`  ${relative(process.cwd(), f)}`);

const result = spawnSync(process.execPath, ['--test', '--test-reporter=tap', ...files], { encoding: 'utf8' });
process.stdout.write(result.stdout ?? '');
process.stderr.write(result.stderr ?? '');
if (result.error) {
  console.error(result.error);
  process.exit(1);
}

const pass = Number(/^# pass (\d+)/m.exec(result.stdout ?? '')?.[1] ?? 0);
const fail = Number(/^# fail (\d+)/m.exec(result.stdout ?? '')?.[1] ?? 0);
if (result.status === 0 && pass === 0) {
  console.error('node --test exited 0 but reported 0 passing tests');
  process.exit(4);
}
console.log(`summary: ${pass} passed, ${fail} failed, exit ${result.status}`);
process.exit(result.status ?? 1);
