#!/usr/bin/env node
// Guest self-check for the ui_* facade (TASK-010): starts ui-facade.mjs as an MCP stdio server — the same way
// DSH's dsh-mcp-client does — and drives initialize / tools/list / ui_list_devices / ui_snapshot /
// ui_get_element through the real Relay CLI. Prints one summary line per step and a final
// `UICHECK {...}` JSON line. Exit 0 only if the snapshot and the element re-read both succeeded.
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const facade = join(dirname(fileURLToPath(import.meta.url)), 'ui-facade.mjs');
const child = spawn(process.execPath, [facade, ...process.argv.slice(2)], { stdio: ['pipe', 'pipe', 'inherit'] });
const pending = new Map();
createInterface({ input: child.stdout }).on('line', (line) => {
  try {
    const msg = JSON.parse(line);
    pending.get(msg.id)?.(msg);
  } catch {
    console.log(`non-JSON on stdout: ${line.slice(0, 200)}`);
  }
});
let next = 1;
const rpc = (method, params) => new Promise((resolve) => {
  const id = next++;
  pending.set(id, resolve);
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
});
const tool = async (name, args) => {
  const r = (await rpc('tools/call', { name, arguments: args })).result;
  const body = JSON.parse(r.content[0].text);
  return { ok: !r.isError, body };
};

const summary = { tools: [], devices: null, snapshot: null, element: null };
let exit = 1;
try {
  const init = await rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'ui-facade-check', version: '1' } });
  console.log(`initialize: ${init.result?.serverInfo?.name} protocol ${init.result?.protocolVersion}`);
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
  summary.tools = (await rpc('tools/list', {})).result.tools.map((t) => t.name);
  console.log(`tools: ${summary.tools.join(', ')}`);

  const devices = await tool('ui_list_devices', {});
  summary.devices = devices.body.devices?.map((d) => `${d.deviceId}:${d.status}`) ?? devices.body.error;
  console.log(`ui_list_devices: ${JSON.stringify(summary.devices)}`);

  const snap = await tool('ui_snapshot', { maxNodes: 40 });
  if (!snap.ok) {
    summary.snapshot = snap.body.error;
    console.log(`ui_snapshot: ERROR ${JSON.stringify(snap.body.error)}`);
    exit = snap.body.error?.code === 'APPROVAL_REQUIRED' ? 5 : 4;
  } else {
    const s = snap.body;
    summary.snapshot = { snapshotId: s.snapshotId, app: s.target.applicationId, elements: s.elements.length, totalNodes: s.totalNodes, truncated: s.truncated };
    console.log(`ui_snapshot: ${JSON.stringify(summary.snapshot)}`);
    for (const e of s.elements.slice(0, 12)) console.log(`  ${e.ref} ${e.role} ${JSON.stringify(e.name)}${e.bounds ? '' : ' (offscreen)'}`);
    // Re-read the first visible element: proves ui.node + the stale check on the live screen.
    const target = s.elements.find((e) => e.bounds && e.name) ?? s.elements.find((e) => e.bounds);
    if (target) {
      const el = await tool('ui_get_element', { snapshotId: s.snapshotId, refId: target.ref });
      summary.element = el.ok ? { ref: el.body.element.ref, role: el.body.element.role, nativeId: el.body.element.nativeId, enabled: el.body.element.enabled } : el.body.error;
      console.log(`ui_get_element ${target.ref}: ${el.ok ? 'ok' : 'ERROR'} ${JSON.stringify(summary.element)}`);
      exit = el.ok ? 0 : (el.body.error?.code === 'STALE_ELEMENT' ? 3 : 4);
    } else {
      console.log('ui_get_element: skipped (no visible element)');
      exit = 0;
    }
  }
} catch (err) {
  console.log(`check failed: ${err?.stack ?? err}`);
  exit = 6;
} finally {
  console.log(`UICHECK ${JSON.stringify({ exit, ...summary })}`);
  child.kill();
}
process.exit(exit);
