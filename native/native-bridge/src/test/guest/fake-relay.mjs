// Fake Interlock Relay guest CLI for ui-facade tests. Reply shapes are copied from the device run recorded in
// docs/COMPATIBILITY.md (TASK-010). Behaviour is chosen by FAKE_RELAY_MODE; every invocation appends one line
// to FAKE_RELAY_LOG so tests can assert how many times (and with what) Relay was called.
import { appendFileSync, readFileSync, existsSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const mode = process.env.FAKE_RELAY_MODE ?? 'ok';
const log = process.env.FAKE_RELAY_LOG;
const [command, capability, , json] = process.argv.slice(2);
const calls = log && existsSync(log) ? readFileSync(log, 'utf8').split('\n').filter(Boolean).length : 0;
if (log) appendFileSync(log, JSON.stringify({ command, capability, args: json ? JSON.parse(json) : null }) + '\n');

const out = (obj, code = 0) => {
  process.stdout.write(JSON.stringify(obj) + '\n');
  process.exit(code);
};
const fail = (code, exitCode, retryable = false) =>
  out({ v: 2, id: 'fake', ok: false, elapsedMs: 5, reason: `${code}/x`, error: { code, retryable, exitCode }, state: 'FAILED' }, exitCode);

const BASE_NODES = [
  { nodeId: 0, class: 'android.widget.FrameLayout', left: 0, top: 0, right: 1280, bottom: 2800 },
  { nodeId: 5, class: 'android.widget.Button', offscreen: true, clickable: true },
  { nodeId: 6, class: 'android.widget.TextView', text: 'DSH Mobile', left: 231, top: 539, right: 460, bottom: 596 },
  { nodeId: 9, class: 'android.widget.Button', desc: '收起', left: 1133, top: 536, right: 1189, bottom: 599, clickable: true },
  { nodeId: 20, class: 'android.widget.EditText', text: '', left: 40, top: 900, right: 1240, bottom: 1000, editable: true },
  { nodeId: 66, class: 'android.widget.ImageView', desc: '清除所有通知', left: 543, top: 2397, right: 736, bottom: 2590, clickable: true },
];
// mode "with-list": a scrollable list joins the tree (window-level ui_scroll picks the largest one).
const NODES = mode === 'with-list'
  ? [...BASE_NODES,
    { nodeId: 30, class: 'androidx.recyclerview.widget.RecyclerView', left: 0, top: 600, right: 1280, bottom: 2400, scrollable: true },
    { nodeId: 31, class: 'android.widget.ScrollView', left: 0, top: 0, right: 200, bottom: 200, scrollable: true }]
  : BASE_NODES;
const WRITES = new Set(['ui.click', 'ui.setValue', 'ui.key', 'ui.scroll']);

if (command === 'doctor') {
  if (mode === 'doctor-bad') {
    process.stdout.write(JSON.stringify({ command: 'doctor', ok: false, manifest: { capabilities: 2, usable: 0 } }, null, 2) + '\n');
    process.exit(4);
  }
  process.stdout.write(JSON.stringify({ command: 'doctor', entryDir: '/opt/pilot', manifest: { capabilities: 2, usable: 2 }, ok: true }, null, 2) + '\n');
  process.exit(0);
}

if (mode === 'denied') fail('E_GATE_HOST_DENIED', 2);
if (mode === 'consent' || (mode === 'consent-then-ok' && calls === 0)) fail('E_AWAITING_CONSENT', 5, true);
if (mode === 'no-a11y') fail('E_GATE_SYSTEM_MISSING', 4);
if (mode === 'garbage') {
  process.stdout.write('relay: something odd happened\n');
  process.exit(6);
}

if (WRITES.has(capability)) {
  if (mode === 'suspended') fail('E_TASK_SUSPENDED_BY_HOST', 4);
  if (mode === 'write-timeout') fail('E_TRANSPORT_TIMEOUT', 3, false);
  if (mode === 'not-actionable') fail('E_NODE_NOT_ACTIONABLE', 1);
  const args = JSON.parse(json ?? '{}');
  const data = capability === 'ui.setValue'
    ? { nodeId: args.nodeId, requested: args.text, readBack: args.text.toUpperCase() }
    : capability === 'ui.scroll'
      ? { nodeId: args.nodeId, direction: args.direction, dispatched: args.times, advanced: args.times }
      : { ...args, performed: true };
  out({ v: 2, id: 'fake', ok: true, elapsedMs: 30, data, retryPolicy: 'query_first', effectState: 'dispatched', state: 'SUCCEEDED' });
}

if (capability === 'screen.capture') {
  if (mode === 'secure') fail('E_SURFACE_SECURE_WINDOW', 1);
  const file = join(tmpdir(), `fake-relay-capture-${process.pid}.png`);
  writeFileSync(file, Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]));
  out({
    v: 2, id: 'fake', ok: true, elapsedMs: 200, data: { screen: { width: 1280, height: 2800 } },
    artifacts: [file], state: 'SUCCEEDED',
  });
}

if (capability === 'ui.snapshot') {
  out({
    v: 2, id: 'fake', ok: true, elapsedMs: 120, surface: 'foreground', degraded: false,
    data: { package: 'com.android.systemui', screen: { width: 1280, height: 2800, densityDpi: 560, display: 0 }, nodes: NODES, truncated: false },
    retryPolicy: 'safe_retry', effectState: 'verified', state: 'SUCCEEDED',
  });
}

if (capability === 'ui.node') {
  const wanted = JSON.parse(json ?? '{}').nodeId;
  if (mode === 'node-gone') fail('E_NODE_NOT_FOUND', 1);
  const base = NODES.find((n) => n.nodeId === wanted);
  if (!base) fail('E_NODE_NOT_FOUND', 1);
  const node = {
    display: 0, class: mode === 'changed' ? 'android.widget.TextView' : base.class,
    ...(base.text != null ? { text: base.text } : {}), ...(base.desc != null ? { desc: base.desc } : {}),
    id: 'com.android.systemui:id/fake', package: 'com.android.systemui',
    enabled: true, checkable: false, checked: false, clickable: Boolean(base.clickable), longClickable: false,
    scrollable: false, selected: false, visible: !base.offscreen, focused: false, editable: Boolean(base.editable),
    actions: base.clickable ? ['click'] : [],
    ...(base.offscreen ? { offscreen: true } : { left: base.left + 3, top: base.top, right: base.right + 3, bottom: base.bottom }),
  };
  out({ v: 2, id: 'fake', ok: true, elapsedMs: 40, data: { matchMode: 'exact', nodeId: wanted, node, treeDisplay: 0 }, state: 'SUCCEEDED' });
}

fail('E_NOT_IMPLEMENTED', 1);
