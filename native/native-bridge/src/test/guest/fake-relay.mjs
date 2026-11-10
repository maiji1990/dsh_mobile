// Fake Interlock Relay guest CLI for ui-facade tests. Reply shapes are copied from the device run recorded in
// docs/COMPATIBILITY.md (TASK-010). Behaviour is chosen by FAKE_RELAY_MODE; every invocation appends one line
// to FAKE_RELAY_LOG so tests can assert how many times (and with what) Relay was called.
import { appendFileSync, readFileSync, existsSync } from 'node:fs';

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

const NODES = [
  { nodeId: 0, class: 'android.widget.FrameLayout', left: 0, top: 0, right: 1280, bottom: 2800 },
  { nodeId: 5, class: 'android.widget.Button', offscreen: true, clickable: true },
  { nodeId: 6, class: 'android.widget.TextView', text: 'DSH Mobile', left: 231, top: 539, right: 460, bottom: 596 },
  { nodeId: 9, class: 'android.widget.Button', desc: '收起', left: 1133, top: 536, right: 1189, bottom: 599, clickable: true },
  { nodeId: 20, class: 'android.widget.EditText', text: '', left: 40, top: 900, right: 1240, bottom: 1000, editable: true },
  { nodeId: 66, class: 'android.widget.ImageView', desc: '清除所有通知', left: 543, top: 2397, right: 736, bottom: 2590, clickable: true },
];

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
