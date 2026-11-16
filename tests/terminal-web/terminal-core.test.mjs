// vNext PR-08: the terminal page's message logic (src/DshMobile.Android/Assets/terminal/terminal-core.js) driven with
// the REAL vendored xterm.js 6.0.0 + addons (headless: never opened, no DOM needed for the buffer), so the byte path
// PTY -> base64 batch -> Uint8Array -> xterm is checked against the emulator that ships, not a fake.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const assets = join(here, '../../src/DshMobile.Android/Assets/terminal');
globalThis.self ??= globalThis; // the UMD bundles look for a global object
const require = createRequire(import.meta.url);
const core = require(join(assets, 'terminal-core.js'));
const { Terminal } = require(join(assets, 'vendor/xterm.js'));
const { SearchAddon } = require(join(assets, 'vendor/addon-search.js'));
const { Unicode11Addon } = require(join(assets, 'vendor/addon-unicode11.js'));

function page({ cols = 40, rows = 6 } = {}) {
  const term = new Terminal({ cols, rows, allowProposedApi: true, scrollback: 100 });
  const search = new SearchAddon();
  term.loadAddon(search);
  term.loadAddon(new Unicode11Addon());
  term.unicode.activeVersion = '11';
  const sent = [];
  let refits = 0;
  const p = core.bind(term, search, (m) => sent.push(m), () => { refits++; });
  return { term, p, sent, refits: () => refits };
}

/** What TerminalOutputPump posts: base64 of raw bytes. */
const out = (bytes) => ({ t: 'out', b: Buffer.from(bytes).toString('base64') });
const line = (term, y = 0) => term.buffer.active.getLine(y).translateToString(true);
const acks = (sent) => sent.filter((m) => m.t === 'ack').reduce((n, m) => n + m.n, 0);
async function settle(sent, total) {
  for (let i = 0; i < 200 && acks(sent) < total; i++) await new Promise((r) => setTimeout(r, 5));
}

test('decode is byte-exact and yields bytes, never a decoded string', () => {
  const raw = Buffer.from([0x00, 0xe4, 0xb8, 0xad, 0xff, 0x80, 0x7f]);
  const u = core.decode(raw.toString('base64'));
  assert.ok(u instanceof Uint8Array);
  assert.deepEqual([...u], [...raw]);
});

test('UTF-8 split across batches renders once, with Unicode 11 widths, and every batch is acknowledged', async () => {
  const { term, p, sent } = page();
  const bytes = Buffer.from('中文😀é|end', 'utf8');
  const cuts = [1, 2, 3, 5, 7, 8, 9, bytes.length]; // inside 中, 文, 😀 and é
  let from = 0;
  for (const to of cuts) { p.handle(out(bytes.subarray(from, to))); from = to; }
  await settle(sent, bytes.length);
  assert.equal(line(term), '中文😀é|end');
  assert.equal(term.buffer.active.cursorX, 11); // 2+2+2+1+1+3
  assert.equal(acks(sent), bytes.length);
  assert.equal(sent.filter((m) => m.t === 'ack').length, cuts.length);
});

test('DECCKM changes are reported once per change', async () => {
  const { p, sent } = page();
  p.handle(out(Buffer.from('\x1b[?1h')));
  p.handle(out(Buffer.from('x')));
  p.handle(out(Buffer.from('\x1b[?1l')));
  await settle(sent, 7);
  assert.deepEqual(sent.filter((m) => m.t === 'mode').map((m) => m.appCursor), [true, false]);
});

test('reset clears the screen and the cursor-key mode', async () => {
  const { term, p, sent } = page();
  p.handle(out(Buffer.from('hello\x1b[?1h')));
  await settle(sent, 10);
  p.handle({ t: 'reset' });
  assert.equal(line(term), '');
  assert.equal(term.modes.applicationCursorKeysMode, false);
});

test('search reports a miss and ignores an empty query', async () => {
  const { p, sent } = page();
  const text = Buffer.from('alpha\r\nneedle one\r\nbeta needle\r\n');
  p.handle(out(text));
  await settle(sent, text.length);
  p.handle({ t: 'search', q: 'absent-word', dir: 'next' });
  assert.deepEqual(sent.at(-1), { t: 'search', index: -1, count: 0 });
  const n = sent.length;
  p.handle({ t: 'search', q: '' });
  p.handle({ t: 'search', q: 7 });
  assert.equal(sent.length, n);
  p.handle({ t: 'searchClear' }); // no throw on the real addon
});

test('copy / selectAll / scroll / searchClear call the emulator (selection needs a DOM, so a stub here)', () => {
  const calls = [];
  const stub = {
    modes: { applicationCursorKeysMode: false }, options: { fontSize: 14 }, cols: 80, rows: 24,
    onData() {}, onBinary() {}, onResize() {},
    getSelection: () => 'picked text', selectAll: () => calls.push('selectAll'), clearSelection: () => calls.push('clearSelection'),
    scrollToTop: () => calls.push('top'), scrollToBottom: () => calls.push('bottom'),
  };
  const search = { onDidChangeResults() {}, clearDecorations: () => calls.push('clearDecorations') };
  const sent = [];
  const p = core.bind(stub, search, (m) => sent.push(m), () => {});
  p.handle({ t: 'copy' });
  p.handle({ t: 'selectAll' });
  p.handle({ t: 'scroll', to: 'top' });
  p.handle({ t: 'scroll', to: 'bottom' });
  p.handle({ t: 'searchClear' });
  assert.deepEqual(sent, [{ t: 'selection', text: 'picked text' }]);
  assert.deepEqual(calls, ['selectAll', 'top', 'bottom', 'clearDecorations', 'clearSelection']);
});

test('font size is clamped, applied once, and refits', () => {
  const { term, p, refits } = page();
  const before = refits();
  p.handle({ t: 'font', size: 100 });
  assert.equal(term.options.fontSize, core.FONT_MAX);
  p.handle({ t: 'font', size: 2 });
  assert.equal(term.options.fontSize, core.FONT_MIN);
  p.handle({ t: 'font', size: core.FONT_MIN }); // unchanged: no refit
  p.handle({ t: 'font', size: 'big' });
  assert.equal(term.options.fontSize, core.FONT_MIN);
  assert.equal(refits() - before, 2);
});

test('links: only http(s) goes to the host, as a request', () => {
  const sent = [];
  const h = core.linkHandler((m) => sent.push(m));
  h({}, 'https://example.com/a?b=1');
  h({}, 'http://example.com');
  h({}, 'file:///etc/passwd');
  h({}, 'javascript:alert(1)');
  h({}, 'intent://x#Intent;end');
  h({}, 'https://' + 'a'.repeat(3000));
  h({}, 'https://a b');
  assert.deepEqual(sent, [{ t: 'link', uri: 'https://example.com/a?b=1' }, { t: 'link', uri: 'http://example.com' }]);
});

test('malformed host messages are ignored; ready reports the size', () => {
  const { p, sent } = page({ cols: 33, rows: 7 });
  for (const m of [null, {}, { t: 7 }, { t: 'out' }, { t: 'out', b: 5 }, { t: 'nope' }]) p.handle(m);
  assert.deepEqual(sent, []);
  p.ready();
  assert.deepEqual(sent, [{ t: 'ready', cols: 33, rows: 7 }]);
});

test('typed / emulator replies flow back as in / resize', () => {
  const { term, sent } = page();
  term.input('ls\r');
  term.resize(50, 10);
  assert.deepEqual(sent.filter((m) => m.t !== 'ack'), [{ t: 'in', d: 'ls\r' }, { t: 'resize', cols: 50, rows: 10 }]);
});

test('stress tool: 4 MiB of split UTF-8 + ANSI through the real emulator with the 1 MiB ack window', async () => {
  const { spawnSync } = await import('node:child_process');
  const r = spawnSync(process.execPath, [join(here, '../../tooling/terminal-stress.mjs'), '4', '--mix', 'ansi'], { encoding: 'utf8', timeout: 120_000 });
  assert.equal(r.status, 0, r.stdout + r.stderr);
  const report = JSON.parse(r.stdout.trim().split('\n').pop());
  assert.equal(report.ok, true);
  assert.equal(report.lastLine, 'STRESS-END 中文😀');
});
