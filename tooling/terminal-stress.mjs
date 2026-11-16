#!/usr/bin/env node
// vNext PR-08: output stress for the terminal page logic. Pushes N MiB through terminal-core.js into the REAL vendored
// xterm.js 6.0.0 (headless, Unicode 11 on) exactly as the app does: 64 KiB base64 batches, at most a 1 MiB window of
// unacknowledged bytes (TerminalOutputPump's defaults), the next batch only after an ack frees room.
//
//   node tooling/terminal-stress.mjs [MiB=100] [--mix ascii|utf8|ansi]
//
// Prints MiB/s and checks that every byte was acknowledged and the last line rendered intact. This measures the
// emulator + glue on this machine's Node, NOT the Android WebView: device numbers still need a device run.
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const args = process.argv.slice(2);
const mib = Number(args.find((a) => /^\d+$/.test(a)) ?? 100);
const mix = args.includes('--mix') ? args[args.indexOf('--mix') + 1] : 'utf8';
const BATCH = 64 * 1024;
const WINDOW = 1024 * 1024;

const assets = join(dirname(fileURLToPath(import.meta.url)), '../src/DshMobile.Android/Assets/terminal');
globalThis.self ??= globalThis;
const require = createRequire(import.meta.url);
const core = require(join(assets, 'terminal-core.js'));
const { Terminal } = require(join(assets, 'vendor/xterm.js'));
const { Unicode11Addon } = require(join(assets, 'vendor/addon-unicode11.js'));

const term = new Terminal({ cols: 120, rows: 40, scrollback: 5000, allowProposedApi: true });
term.loadAddon(new Unicode11Addon());
term.unicode.activeVersion = '11';

const lines = {
  ascii: 'the quick brown fox jumps over the lazy dog 0123456789 abcdefghijklmnopqrstuvwxyz\r\n',
  utf8: 'build 中文输出 ✓ émoji 😀 done 0123456789 — 日本語 한국어 ascii tail\r\n',
  ansi: '\x1b[1;32mOK\x1b[0m \x1b[33mwarn\x1b[0m \x1b[38;5;208mcolour\x1b[0m 中文 😀 \x1b[K\r\n',
}[mix];
if (!lines) { console.error(`unknown --mix ${mix}`); process.exit(2); }
const unit = Buffer.from(lines, 'utf8');
// Whole lines only: batches still cut lines, characters and escape sequences anywhere, but the stream must not END
// inside an escape sequence, or the marker's first byte would legitimately complete it (CSI … S).
const total = Math.floor((mib * 1024 * 1024) / unit.length) * unit.length;

let acked = 0;
let inFlight = 0;
let wake = null;
const page = core.bind(term, null, (m) => {
  if (m.t === 'ack') { acked += m.n; inFlight -= m.n; if (wake) { const w = wake; wake = null; w(); } }
}, () => {});

// A batch cuts lines (and UTF-8 characters) at arbitrary points, like PTY reads do.
const source = Buffer.alloc(BATCH + unit.length);
let sent = 0;
let phase = 0;
const t0 = process.hrtime.bigint();
while (sent < total) {
  while (inFlight + BATCH > WINDOW) { await new Promise((r) => { wake = r; }); }
  const n = Math.min(BATCH, total - sent);
  for (let i = 0; i < n; i++) { source[i] = unit[(phase + i) % unit.length]; }
  phase = (phase + n) % unit.length;
  inFlight += n;
  sent += n;
  page.handle({ t: 'out', b: source.subarray(0, n).toString('base64') });
}
while (acked < total) { await new Promise((r) => { wake = r; }); }
const secs = Number(process.hrtime.bigint() - t0) / 1e9;

// Finish on a clean line and check it rendered exactly.
const marker = 'STRESS-END 中文😀';
page.handle({ t: 'out', b: Buffer.from('\r\n' + marker + '\r\n').toString('base64') });
while (acked < total + Buffer.byteLength('\r\n' + marker + '\r\n')) { await new Promise((r) => { wake = r; }); }
const buf = term.buffer.active;
const last = buf.getLine(buf.baseY + buf.cursorY - 1)?.translateToString(true);
const ok = last === marker && acked === total + Buffer.byteLength('\r\n' + marker + '\r\n');
console.log(JSON.stringify({ mib, bytes: total, mix, seconds: +secs.toFixed(2), mibPerSecond: +(total / 1048576 / secs).toFixed(1), batches: Math.ceil(total / BATCH), window: WINDOW, lastLine: last, ok }));
process.exit(ok ? 0 : 1);
