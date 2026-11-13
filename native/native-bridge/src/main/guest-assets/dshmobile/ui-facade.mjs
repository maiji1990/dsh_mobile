#!/usr/bin/env node
// DSH Mobile ui_* facade (TASK-010, ADR-0004, ADR-0008): a dependency-free MCP stdio server that exposes the
// `maiji1990/ui_mcp` contract (ui_list_devices / ui_snapshot / ui_find / ui_get_element / ui_click / ui_set_text /
// ui_press_key / ui_scroll / ui_wait_for / ui_screenshot) on top of the Interlock Relay guest CLI
// (`/opt/pilot/bin/relay`). Every phone call goes through Relay's own gate: the user approves it on the phone
// (notification + in-app confirmation page for allows); nothing here approves, and there is no ui_confirm_approval.
//
// Rules this file keeps (AGENTS.md):
//  - single contract: tool names, ref semantics and error codes follow ui_mcp; no second tool set;
//  - never fake success: every Relay failure becomes an MCP error result with a stable contract code and the
//    original Relay code attached; unknown Relay codes map to OBSERVATION_INCOMPLETE, never to a guess;
//  - stale refs never reach Relay: a ref from an older snapshot fails locally with STALE_ELEMENT, and a ref
//    from the latest snapshot is re-read and fingerprint-checked before it is returned or acted on;
//  - a write never repeats an effect: only "the user has not answered yet" (nothing ran) is retried, and after
//    any write the snapshot's refs are retired, so the next action needs a fresh ui_snapshot.
//
// stdout carries JSON-RPC only; diagnostics go to stderr.

import { execFile } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';

export const DEVICE_ID = 'android-local';
export const PROVIDER = 'android-relay';
const SERVER_INFO = { name: 'dshmobile-ui', version: '0.2.0' };
const LEGACY_VERSIONS = ['2025-11-25', '2025-06-18', '2025-03-26', '2024-11-05'];
const FALLBACK_VERSION = '2025-06-18';

const INSTRUCTIONS = [
  'Read and operate the phone screen through the ui_* contract (deviceId "android-local").',
  'Every Relay call needs the user to approve it on the phone ("允许一次" / "本次会话允许", then confirm in DSH Mobile).',
  'Call ui_snapshot first; refs (e.g. "e12") are only valid for the snapshotId they came from.',
  'After ui_click / ui_set_text / ui_press_key / ui_scroll the refs are retired: call ui_snapshot again to verify the result.',
  'STALE_ELEMENT means the screen changed: take a new snapshot instead of retrying the ref.',
  'APPROVAL_REQUIRED means the user has not answered yet; POLICY_DENIED means they declined: do not loop.',
  'OPERATION_TIMEOUT on a write means the effect is unknown: snapshot first, never blindly repeat it.',
  'Writes are refused while DSH Mobile itself is on screen (PROVIDER_UNAVAILABLE, relay code E_TASK_SUSPENDED_BY_HOST).',
].join('\n');

// ------------------------------------------------------------------ contract errors

export class UiError extends Error {
  constructor(code, message, extra = {}) {
    super(message);
    this.code = code;
    this.extra = extra;
  }
}

/** Relay codes that only mean "the user has not answered yet": safe to retry the identical read call. */
const WAITING_CODES = new Set(['E_AWAITING_CONSENT', 'E_GATE_WAITING_TURN']);

/**
 * Relay error code -> ui_mcp code. Conservative: only codes whose meaning is unambiguous are mapped;
 * everything else is OBSERVATION_INCOMPLETE with the Relay code attached.
 */
export function mapRelayCode(relayCode) {
  switch (relayCode) {
    case 'E_AWAITING_CONSENT':
    case 'E_GATE_WAITING_TURN':
      return 'APPROVAL_REQUIRED';
    case 'E_GATE_HOST_DENIED':
    case 'E_GATE_APPROVAL_TIMEOUT':
    case 'E_GATE_CANCELLED':
    case 'E_NODE_SYSTEM_CONSENT_TARGET':
    case 'E_SURFACE_SYSTEM_CONSENT_REQUIRED':
      return 'POLICY_DENIED';
    case 'E_NODE_NOT_ACTIONABLE':
    case 'E_NODE_NO_IME_ACTION':
      return 'ACTION_UNSUPPORTED';
    case 'E_ACTION_REJECTED':
      return 'ACTION_FAILED';
    case 'E_GATE_SYSTEM_MISSING':
    case 'E_GATE_NO_FOREGROUND':
    case 'E_CAPABILITY_UNAVAILABLE_ON_DEVICE':
    case 'E_BACKEND_UNAVAILABLE':
    case 'E_SURFACE_UNAVAILABLE':
    case 'E_TASK_SUSPENDED_BY_HOST':
    case 'E_QUEUE_FULL':
    case 'E_RATE_LIMITED':
      return 'PROVIDER_UNAVAILABLE';
    case 'E_SURFACE_SECURE_WINDOW':
      return 'SECURE_CONTENT';
    case 'E_NODE_NOT_FOUND':
      return 'ELEMENT_NOT_FOUND';
    case 'E_NODE_AMBIGUOUS':
      return 'AMBIGUOUS_ELEMENT';
    case 'E_TRANSPORT_TIMEOUT':
    case 'E_WAIT_TIMEOUT':
      return 'OPERATION_TIMEOUT';
    default:
      return 'OBSERVATION_INCOMPLETE';
  }
}

const HINTS = {
  APPROVAL_REQUIRED: 'Ask the user to approve the DSH notification on the phone, then call again.',
  POLICY_DENIED: 'The user declined or did not answer in time. Do not retry automatically.',
  PROVIDER_UNAVAILABLE: 'Check DSH Mobile > 手机操控: relay started, accessibility service on, notifications allowed.',
  STALE_ELEMENT: 'The screen changed since that snapshot. Call ui_snapshot again and use the new refs.',
  OPERATION_TIMEOUT: 'Relay did not answer in time; it may be stopped. For a write the effect is unknown: call ui_snapshot before anything else.',
  ACTION_UNSUPPORTED: 'This control or key does not accept that action. Pick another element or tool.',
  ACTION_FAILED: 'The system refused the action. Take a new snapshot and check the screen before trying again.',
};

// ------------------------------------------------------------------ relay CLI

/** Runs the relay CLI. A `.mjs`/`.js` path (test fakes) runs under this Node; the real CLI is executed directly. */
function runRelay(relayCli, argv, options, callback) {
  return /\.(mjs|cjs|js)$/.test(relayCli)
    ? execFile(process.execPath, [relayCli, ...argv], options, callback)
    : execFile(relayCli, argv, options, callback);
}

/**
 * Runs `relay call <cap> --json <args> --timeout <ms>` once and returns the parsed reply.
 * Resolves `{ ok: true, data }` or `{ ok: false, relayCode, exitCode, reply }`; throws UiError only when the CLI
 * itself cannot run (not installed, no JSON reply).
 */
export function relayOnce(relayCli, capability, args, { timeoutMs = 30000, signal } = {}) {
  return new Promise((resolve, reject) => {
    const argv = ['call', capability, '--json', JSON.stringify(args ?? {}), '--timeout', String(timeoutMs)];
    runRelay(relayCli, argv, { timeout: timeoutMs + 15000, maxBuffer: 16 * 1024 * 1024, signal }, (err, stdout) => {
      if (err && (err.code === 'ENOENT' || err.code === 'EACCES')) {
        return reject(new UiError('PROVIDER_UNAVAILABLE', `relay CLI not runnable at ${relayCli} (${err.code})`));
      }
      if (err && err.name === 'AbortError') {
        return reject(new UiError('OPERATION_TIMEOUT', 'cancelled by the caller'));
      }
      const reply = lastJsonLine(stdout);
      if (!reply) {
        const why = err?.killed ? 'relay CLI timed out' : `relay CLI gave no JSON reply (exit ${err?.code ?? 0})`;
        return reject(new UiError(err?.killed ? 'OPERATION_TIMEOUT' : 'OBSERVATION_INCOMPLETE', why));
      }
      const exitCode = typeof err?.code === 'number' ? err.code : 0;
      if (exitCode === 0 && reply.ok === true) return resolve({ ok: true, data: reply.data ?? {}, reply });
      const relayCode = reply.error?.code ?? String(reply.reason ?? '').split('/')[0] ?? 'E_UNKNOWN';
      resolve({ ok: false, relayCode: relayCode || 'E_UNKNOWN', exitCode, reply });
    });
  });
}

export function lastJsonLine(text) {
  const lines = String(text ?? '').split(/\r?\n/).map((l) => l.trim()).filter(Boolean);
  for (let i = lines.length - 1; i >= 0; i--) {
    if (!lines[i].startsWith('{')) continue;
    try {
      return JSON.parse(lines[i]);
    } catch {
      // keep looking: a partial line may precede the reply
    }
  }
  return null;
}

const sleep = (ms, signal) =>
  new Promise((resolve) => {
    const t = setTimeout(resolve, ms);
    signal?.addEventListener('abort', () => { clearTimeout(t); resolve(); }, { once: true });
  });

/**
 * One logical read: retries the identical call while Relay only reports "the user has not answered yet"
 * (Relay waits ~8 s inline and parks a later answer for the next identical call), until `waitMs` runs out.
 * Any other outcome is returned at once. Those two codes mean nothing ran (Relay parks the user's later answer for
 * the identical call), so the retry is safe for writes too: a write runs only once Relay says it may.
 */
export async function relayRead(relayCli, capability, args, { waitMs = 90000, attemptTimeoutMs = 30000, pauseMs = 1000, signal, now = Date.now } = {}) {
  const deadline = now() + waitMs;
  let last;
  for (;;) {
    last = await relayOnce(relayCli, capability, args, { timeoutMs: attemptTimeoutMs, signal });
    if (last.ok || !WAITING_CODES.has(last.relayCode)) return last;
    if (signal?.aborted || now() + pauseMs >= deadline) return last;
    await sleep(pauseMs, signal);
  }
}

export function relayFailure(result, context = {}) {
  let code = mapRelayCode(result.relayCode);
  if (context.nodeLookup && result.relayCode === 'E_NODE_NOT_FOUND') code = 'STALE_ELEMENT';
  const effect = result.reply?.effect ?? result.reply?.error?.effect ?? null;
  const message = result.reply?.error?.message ?? result.reply?.reason ?? result.relayCode;
  return new UiError(code, String(message), {
    relay: {
      code: result.relayCode, exitCode: result.exitCode, retryable: result.reply?.error?.retryable ?? null,
      ...(effect ? { effect } : {}),
    },
  });
}

// ------------------------------------------------------------------ model mapping

const ROLE_BY_CLASS = [
  [/(^|\.)(ImageButton|FloatingActionButton)$/, 'button'],
  [/(^|\.)(CheckBox|CheckedTextView)$/, 'checkbox'],
  [/(^|\.)(Switch|SwitchCompat|SwitchMaterial|ToggleButton)$/, 'switch'],
  [/(^|\.)RadioButton$/, 'radio'],
  [/(^|\.)\w*Button$/, 'button'],
  [/(^|\.)\w*(EditText|TextField|SearchView)$/, 'textbox'],
  [/(^|\.)(RecyclerView|ListView|GridView|ScrollView|HorizontalScrollView|NestedScrollView|ViewPager\d?)$/, 'list'],
  [/(^|\.)\w*(ImageView|Image)$/, 'image'],
  [/(^|\.)\w*(SeekBar|Slider|ProgressBar)$/, 'slider'],
  [/(^|\.)(WebView)$/, 'document'],
  [/(^|\.)\w*TextView$/, 'text'],
  [/(^|\.)\w*(Layout|ViewGroup|FrameLayout|View|Holder)$/, 'group'],
];

export function roleOf(node) {
  if (node.editable) return 'textbox';
  const cls = String(node.class ?? '');
  for (const [re, role] of ROLE_BY_CLASS) if (re.test(cls)) return role;
  return 'unknown';
}

function boundsOf(node) {
  if (node.offscreen || node.left == null) return null;
  return { x: node.left, y: node.top, width: node.right - node.left, height: node.bottom - node.top };
}

function actionsOf(node) {
  const a = [];
  if (node.clickable) a.push('click');
  if (node.editable) a.push('set_text');
  if (node.scrollable) a.push('scroll');
  return a;
}

/** Snapshot node -> contract element. `enabled` is not in Relay snapshots, so it is omitted (see ui_get_element). */
export function elementOf(node) {
  const el = {
    ref: `e${node.nodeId}`,
    provider: PROVIDER,
    role: roleOf(node),
    name: node.desc ?? node.text ?? null,
    text: node.text ?? null,
    nativeId: null,
    bounds: boundsOf(node),
    visible: !node.offscreen,
    actions: actionsOf(node),
    extra: { class: node.class ?? '', nodeId: node.nodeId },
  };
  if (node.checked) el.extra.checked = true;
  return el;
}

/** Layout-only nodes carry nothing an agent can read or target; compact mode drops them. */
function isInteresting(node) {
  return Boolean(node.text || node.desc || node.clickable || node.editable || node.scrollable || node.checked);
}

/** Same control? Class and label must match; bounds may drift a little (animations, status bar). */
export function sameControl(snapEl, liveNode, tolerance = 24) {
  if ((liveNode.class ?? '') !== (snapEl.extra.class ?? '')) return false;
  const liveName = liveNode.desc ?? liveNode.text ?? null;
  if ((snapEl.name ?? null) !== liveName) return false;
  const a = snapEl.bounds;
  const b = boundsOf(liveNode);
  if ((a == null) !== (b == null)) return false;
  if (a && b) {
    for (const k of ['x', 'y', 'width', 'height']) if (Math.abs(a[k] - b[k]) > tolerance) return false;
  }
  return true;
}

// ------------------------------------------------------------------ facade

export class UiFacade {
  constructor({
    relayCli = '/opt/pilot/bin/relay', approvalWaitMs = 90000, read = relayRead, clock = () => new Date(),
    readFile = (p) => readFileSync(p), pollMs = 1000, now = Date.now,
  } = {}) {
    this.relayCli = relayCli;
    this.readFile = readFile;
    this.pollMs = pollMs;
    this.now = now;
    this.approvalWaitMs = approvalWaitMs;
    this.read = read;
    this.clock = clock;
    this.sessionId = randomUUID();
    this.revision = 0;
    this.latest = null; // { snapshotId, byRef: Map, snapshot }
    this.queue = Promise.resolve(); // single writer: one Relay call at a time
  }

  serialize(fn) {
    const run = this.queue.then(fn, fn);
    this.queue = run.catch(() => {});
    return run;
  }

  async relay(capability, args, signal, context) {
    return (await this.relayFull(capability, args, signal, context)).data;
  }

  async relayFull(capability, args, signal, context) {
    const r = await this.read(this.relayCli, capability, args, { waitMs: this.approvalWaitMs, signal });
    if (!r.ok) throw relayFailure(r, context);
    return r;
  }

  checkDevice(args) {
    if (args.deviceId != null && args.deviceId !== DEVICE_ID) {
      throw new UiError('TARGET_NOT_FOUND', `unknown deviceId "${args.deviceId}"; the only device is "${DEVICE_ID}"`);
    }
  }

  async listDevices() {
    // `relay doctor` is local (no approval): installed CLI + writable mailbox + allowlisted capabilities.
    const doctor = await new Promise((resolve) => {
      runRelay(this.relayCli, ['doctor'], { timeout: 15000 }, (err, stdout) => resolve({ err, stdout }));
    });
    let status = 'offline';
    let detail;
    if (doctor.err && (doctor.err.code === 'ENOENT' || doctor.err.code === 'EACCES')) {
      detail = `relay CLI not runnable at ${this.relayCli}`;
    } else {
      const body = parseLooseJson(doctor.stdout);
      const usable = body?.manifest?.usable ?? 0;
      status = body?.ok === true && usable > 0 ? 'online' : 'offline';
      detail = body ? `doctor ok=${body.ok} usable=${usable}` : 'doctor gave no JSON';
    }
    return {
      devices: [{
        deviceId: DEVICE_ID,
        platform: 'android',
        transport: 'local-a11y',
        provider: PROVIDER,
        status,
        readOnly: false,
        detail,
      }],
    };
  }

  async snapshot(args, signal) {
    this.checkDevice(args);
    const mode = args.mode ?? 'compact';
    if (mode !== 'compact' && mode !== 'full') throw new UiError('INVALID_ARGUMENT', 'mode must be "compact" or "full"');
    const maxNodes = args.maxNodes ?? 200;
    if (!Number.isInteger(maxNodes) || maxNodes < 1 || maxNodes > 600) {
      throw new UiError('INVALID_ARGUMENT', 'maxNodes must be an integer in 1..600');
    }
    const data = await this.relay('ui.snapshot', {}, signal);
    const nodes = Array.isArray(data.nodes) ? data.nodes : [];
    const kept = mode === 'full' ? nodes : nodes.filter(isInteresting);
    const elements = kept.slice(0, maxNodes).map(elementOf);
    this.revision += 1;
    const snapshotId = `s${this.revision}`;
    const snapshot = {
      snapshotId,
      sessionId: this.sessionId,
      target: { deviceId: DEVICE_ID, applicationId: data.package ?? null, windowId: null },
      provider: PROVIDER,
      revision: this.revision,
      capturedAt: this.clock().toISOString(),
      truncated: Boolean(data.truncated) || kept.length > maxNodes,
      screen: data.screen ?? null,
      totalNodes: nodes.length,
      elements,
    };
    // Only the latest snapshot's refs are live: an older snapshotId fails locally with STALE_ELEMENT.
    // Every node of the tree is addressable (also those compact mode or maxNodes left out of the reply).
    this.latest = { snapshotId, byRef: new Map(nodes.map((n) => [`e${n.nodeId}`, elementOf(n)])) };
    return snapshot;
  }

  /** After a write the screen may have changed under every ref: retire them (contract: re-snapshot to verify). */
  retireRefs(reason) {
    if (this.latest) this.latest = { ...this.latest, retired: reason };
  }

  requireLatest(snapshotId) {
    if (!this.latest) throw new UiError('STALE_ELEMENT', 'no snapshot yet; call ui_snapshot first');
    if (snapshotId != null && snapshotId !== this.latest.snapshotId) {
      throw new UiError('STALE_ELEMENT', `snapshot ${snapshotId} is no longer current (latest is ${this.latest.snapshotId})`);
    }
    if (this.latest.retired) {
      throw new UiError('STALE_ELEMENT', `snapshot ${this.latest.snapshotId} was taken before ${this.latest.retired}; call ui_snapshot again`);
    }
    return this.latest;
  }

  find(args) {
    this.checkDevice(args);
    const latest = this.requireLatest(args.snapshotId);
    const limit = args.limit ?? 20;
    if (!Number.isInteger(limit) || limit < 1 || limit > 200) throw new UiError('INVALID_ARGUMENT', 'limit must be an integer in 1..200');
    const exact = args.exact === true;
    const norm = (s) => String(s ?? '').toLowerCase();
    const hit = (value, wanted) => (exact ? value === wanted : norm(value).includes(norm(wanted)));
    const matches = [];
    for (const el of latest.byRef.values()) {
      if (args.role != null && el.role !== args.role) continue;
      if (args.name != null && !hit(el.name, args.name) && !hit(el.text, args.name)) continue;
      if (args.text != null && !hit(el.text, args.text)) continue;
      if (args.clickable === true && !el.actions.includes('click')) continue;
      matches.push(el);
      if (matches.length >= limit) break;
    }
    return { snapshotId: latest.snapshotId, count: matches.length, matches };
  }

  /** Resolves a ref of the latest snapshot and re-reads it live; any drift is STALE_ELEMENT, before any action. */
  async liveElement(args, signal) {
    this.checkDevice(args);
    if (typeof args.snapshotId !== 'string' || typeof args.refId !== 'string') {
      throw new UiError('INVALID_ARGUMENT', 'snapshotId and refId are required strings');
    }
    const latest = this.requireLatest(args.snapshotId);
    const snapEl = latest.byRef.get(args.refId);
    if (!snapEl) throw new UiError('ELEMENT_NOT_FOUND', `ref ${args.refId} is not in snapshot ${args.snapshotId}`);
    const data = await this.relay('ui.node', { nodeId: snapEl.extra.nodeId }, signal, { nodeLookup: true });
    const live = data.node ?? {};
    if (data.treeWarning || !sameControl(snapEl, live)) {
      throw new UiError('STALE_ELEMENT', `ref ${args.refId} now points at a different control; take a new snapshot`, {
        treeWarning: data.treeWarning ?? null,
      });
    }
    return { latest, snapEl, live };
  }

  async getElement(args, signal) {
    const { latest, snapEl, live } = await this.liveElement(args, signal);
    return {
      snapshotId: latest.snapshotId,
      element: {
        ...snapEl,
        nativeId: live.id ?? null,
        enabled: live.enabled ?? null,
        visible: live.visible ?? snapEl.visible,
        bounds: boundsOf(live),
        extra: {
          ...snapEl.extra,
          package: live.package ?? null,
          checkable: live.checkable ?? null,
          checked: live.checked ?? null,
          selected: live.selected ?? null,
          focused: live.focused ?? null,
          longClickable: live.longClickable ?? null,
          nativeActions: live.actions ?? [],
        },
      },
    };
  }

  /** ui_mcp identity check: optional expectedRole / expectedName must match the snapshot element. */
  static checkExpected(args, el) {
    if (args.expectedRole != null && args.expectedRole !== el.role) {
      throw new UiError('STALE_ELEMENT', `ref ${args.refId} is a ${el.role}, not the expected ${args.expectedRole}`);
    }
    if (args.expectedName != null && args.expectedName !== el.name && args.expectedName !== el.text) {
      throw new UiError('STALE_ELEMENT', `ref ${args.refId} is "${el.name ?? el.text ?? ''}", not the expected "${args.expectedName}"`);
    }
  }

  /** Runs one write; whatever its outcome the refs are retired (a failed or timed-out write may still have moved the screen). */
  async write(capability, relayArgs, signal, what) {
    try {
      return await this.relayFull(capability, relayArgs, signal);
    } finally {
      this.retireRefs(what);
    }
  }

  async click(args, signal) {
    const { latest, snapEl } = await this.liveElement(args, signal);
    UiFacade.checkExpected(args, snapEl);
    const r = await this.write('ui.click', { nodeId: snapEl.extra.nodeId }, signal, `ui_click on ${args.refId}`);
    return { snapshotId: latest.snapshotId, refId: args.refId, action: 'click', dispatched: true, verified: false, relay: r.data };
  }

  async setText(args, signal) {
    if (typeof args.text !== 'string' || args.text.length === 0) throw new UiError('INVALID_ARGUMENT', 'text is a required non-empty string');
    const { latest, snapEl } = await this.liveElement(args, signal);
    UiFacade.checkExpected(args, snapEl);
    const r = await this.write('ui.setValue', { nodeId: snapEl.extra.nodeId, text: args.text }, signal, `ui_set_text on ${args.refId}`);
    // Relay reports what the control holds afterwards; masks and autocorrect may change it, so no "matches" verdict.
    return {
      snapshotId: latest.snapshotId, refId: args.refId, action: 'set_text', dispatched: true,
      readBack: r.data.readBack ?? null, readBackUnavailable: r.data.readBackUnavailable ?? null,
    };
  }

  async pressKey(args, signal) {
    this.checkDevice(args);
    const key = String(args.key ?? '').toLowerCase();
    if (!['back', 'home', 'recents'].includes(key)) {
      throw new UiError('ACTION_UNSUPPORTED', `key "${args.key}" is not a window-level key on Android; supported: back, home, recents`);
    }
    const r = await this.write('ui.key', { key }, signal, `ui_press_key ${key}`);
    return { action: 'press_key', key, dispatched: true, relay: r.data };
  }

  async scroll(args, signal) {
    this.checkDevice(args);
    const direction = args.direction ?? 'down';
    if (!['up', 'down', 'left', 'right'].includes(direction)) throw new UiError('INVALID_ARGUMENT', 'direction must be up, down, left or right');
    const amount = args.amount ?? 3;
    if (!Number.isInteger(amount) || amount < 1 || amount > 20) throw new UiError('INVALID_ARGUMENT', 'amount must be an integer in 1..20');
    let target;
    if (args.refId != null) {
      target = (await this.liveElement(args, signal)).snapEl;
      if (!target.actions.includes('scroll')) throw new UiError('ACTION_UNSUPPORTED', `ref ${args.refId} is not scrollable`);
    } else {
      // Window-level scroll: the largest visible scrollable container of the latest snapshot.
      const latest = this.requireLatest(args.snapshotId);
      const area = (el) => (el.bounds ? el.bounds.width * el.bounds.height : 0);
      target = [...latest.byRef.values()].filter((el) => el.visible && el.actions.includes('scroll')).sort((a, b) => area(b) - area(a))[0];
      if (!target) throw new UiError('ELEMENT_NOT_FOUND', 'no scrollable container in the latest snapshot; pass refId or use another tool');
    }
    // Android scrolls a container forward (content moves up / left) or backward.
    const relayDirection = direction === 'down' || direction === 'right' ? 'forward' : 'backward';
    const r = await this.write('ui.scroll', { nodeId: target.extra.nodeId, direction: relayDirection, times: amount }, signal,
      `ui_scroll ${direction}`);
    return { action: 'scroll', direction, amount, refId: target.ref, relay: r.data };
  }

  async waitFor(args, signal) {
    this.checkDevice(args);
    if (args.role == null && args.name == null) throw new UiError('INVALID_ARGUMENT', 'role or name is required');
    const timeoutMs = args.timeoutMs ?? 10000;
    if (!Number.isInteger(timeoutMs) || timeoutMs < 0 || timeoutMs > 60000) {
      throw new UiError('INVALID_ARGUMENT', 'timeoutMs must be an integer in 0..60000');
    }
    const absent = args.absent === true;
    const deadline = this.now() + timeoutMs;
    for (let polls = 1; ; polls++) {
      const snap = await this.snapshot({ mode: 'full', maxNodes: 600 }, signal);
      const hit = this.find({ snapshotId: snap.snapshotId, role: args.role, name: args.name, limit: 1 }).matches[0] ?? null;
      if (absent ? hit == null : hit != null) {
        return { snapshotId: snap.snapshotId, absent, satisfied: true, polls, element: hit };
      }
      if (signal?.aborted) throw new UiError('OPERATION_TIMEOUT', 'cancelled by the caller');
      // Bounded twice: by the clock and by the poll count (a stuck clock must not poll forever).
      if (this.now() + this.pollMs > deadline || polls > Math.floor(timeoutMs / Math.max(1, this.pollMs))) {
        throw new UiError('OPERATION_TIMEOUT', `condition not met within ${timeoutMs} ms (${polls} snapshots)`, { snapshotId: snap.snapshotId });
      }
      await sleep(this.pollMs, signal);
    }
  }

  async screenshot(args, signal) {
    this.checkDevice(args);
    const r = await this.relayFull('screen.capture', {}, signal);
    const file = Array.isArray(r.reply?.artifacts) ? r.reply.artifacts[0] : null;
    if (!file) {
      throw new UiError('OBSERVATION_INCOMPLETE', 'screen.capture returned no file', { artifactDelivery: r.reply?.artifactDelivery ?? null });
    }
    const bytes = this.readFile(file);
    const meta = { path: file, bytes: bytes.length, mimeType: 'image/png', screen: r.data.screen ?? null };
    return { meta, image: bytes.length <= MAX_INLINE_IMAGE_BYTES ? bytes.toString('base64') : null };
  }
}

/** Larger captures are returned by path only. */
const MAX_INLINE_IMAGE_BYTES = 8 * 1024 * 1024;

function parseLooseJson(text) {
  // `relay doctor` prints pretty JSON over several lines.
  const s = String(text ?? '');
  const start = s.indexOf('{');
  const end = s.lastIndexOf('}');
  if (start < 0 || end <= start) return null;
  try {
    return JSON.parse(s.slice(start, end + 1));
  } catch {
    return null;
  }
}

// ------------------------------------------------------------------ MCP tools

const DEVICE_PROP = { deviceId: { type: 'string', description: `Optional; the only device is "${DEVICE_ID}".` } };
const READ_ONLY = { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false };
const WRITE = { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: false };
const REF_PROPS = {
  snapshotId: { type: 'string', description: 'The latest snapshot (refs retire after every write).' },
  refId: { type: 'string', description: 'A ref such as "e12" from that snapshot.' },
  expectedRole: { type: 'string', description: 'Optional identity check; mismatch → STALE_ELEMENT, nothing is done.' },
  expectedName: { type: 'string', description: 'Optional identity check against name or text.' },
};

export const TOOLS = [
  {
    name: 'ui_list_devices',
    description: 'List UI devices. Returns the local phone ("android-local") and whether the relay is usable. Needs no approval.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    annotations: READ_ONLY,
  },
  {
    name: 'ui_snapshot',
    description: 'Read the accessibility tree of the phone screen in front. Needs the user to approve a notification on the phone. Returns snapshotId and elements with refs ("e12") valid only for this snapshot.',
    inputSchema: {
      type: 'object',
      properties: {
        ...DEVICE_PROP,
        mode: { type: 'string', enum: ['compact', 'full'], description: 'compact (default) drops layout-only nodes.' },
        maxNodes: { type: 'integer', minimum: 1, maximum: 600, description: 'Default 200.' },
      },
      additionalProperties: false,
    },
    annotations: READ_ONLY,
  },
  {
    name: 'ui_find',
    description: 'Filter the latest snapshot locally (no phone call). Matches role and/or name/text substrings (exact:true for equality).',
    inputSchema: {
      type: 'object',
      properties: {
        ...DEVICE_PROP,
        snapshotId: { type: 'string', description: 'Must be the latest snapshot; default latest.' },
        role: { type: 'string', description: 'button, textbox, text, image, checkbox, switch, radio, list, slider, document, group, unknown' },
        name: { type: 'string' },
        text: { type: 'string' },
        clickable: { type: 'boolean' },
        exact: { type: 'boolean' },
        limit: { type: 'integer', minimum: 1, maximum: 200 },
      },
      additionalProperties: false,
    },
    annotations: READ_ONLY,
  },
  {
    name: 'ui_get_element',
    description: 'Re-read one element from the latest snapshot on the live screen (needs approval). Fails with STALE_ELEMENT if the screen changed.',
    inputSchema: {
      type: 'object',
      properties: {
        ...DEVICE_PROP,
        snapshotId: { type: 'string' },
        refId: { type: 'string', description: 'A ref such as "e12" from that snapshot.' },
      },
      required: ['snapshotId', 'refId'],
      additionalProperties: false,
    },
    annotations: READ_ONLY,
  },
  {
    name: 'ui_click',
    description: 'Semantic click on a ref of the latest snapshot (re-read and identity-checked first). Needs approval on the phone. Call ui_snapshot afterwards to verify.',
    inputSchema: { type: 'object', properties: { ...DEVICE_PROP, ...REF_PROPS }, required: ['snapshotId', 'refId'], additionalProperties: false },
    annotations: WRITE,
  },
  {
    name: 'ui_set_text',
    description: 'Replace the text of an input ref (do not pass secrets in clear). Needs approval. Returns what the control reads back.',
    inputSchema: {
      type: 'object',
      properties: { ...DEVICE_PROP, ...REF_PROPS, text: { type: 'string' } },
      required: ['snapshotId', 'refId', 'text'],
      additionalProperties: false,
    },
    annotations: WRITE,
  },
  {
    name: 'ui_press_key',
    description: 'Window-level key: back, home or recents. Needs approval.',
    inputSchema: {
      type: 'object',
      properties: { ...DEVICE_PROP, key: { type: 'string', enum: ['back', 'home', 'recents'] } },
      required: ['key'],
      additionalProperties: false,
    },
    annotations: WRITE,
  },
  {
    name: 'ui_scroll',
    description: 'Scroll the largest scrollable container of the latest snapshot (or refId) up/down/left/right by amount steps. Needs approval.',
    inputSchema: {
      type: 'object',
      properties: {
        ...DEVICE_PROP,
        direction: { type: 'string', enum: ['up', 'down', 'left', 'right'], description: 'Default down (reveal content below).' },
        amount: { type: 'integer', minimum: 1, maximum: 20, description: 'Default 3.' },
        snapshotId: REF_PROPS.snapshotId,
        refId: { type: 'string', description: 'Optional scrollable ref; default the largest scrollable container.' },
      },
      additionalProperties: false,
    },
    annotations: WRITE,
  },
  {
    name: 'ui_wait_for',
    description: 'Poll the screen (one snapshot per second, each needs approval unless allowed for the session) until an element with role/name appears, or disappears with absent:true.',
    inputSchema: {
      type: 'object',
      properties: {
        ...DEVICE_PROP,
        role: { type: 'string' },
        name: { type: 'string' },
        absent: { type: 'boolean' },
        timeoutMs: { type: 'integer', minimum: 0, maximum: 60000, description: 'Default 10000.' },
      },
      additionalProperties: false,
    },
    annotations: READ_ONLY,
  },
  {
    name: 'ui_screenshot',
    description: 'Screenshot fallback, only when the accessibility tree is not enough. Needs approval; secure windows are refused (SECURE_CONTENT).',
    inputSchema: { type: 'object', properties: { ...DEVICE_PROP }, additionalProperties: false },
    annotations: READ_ONLY,
  },
];

function toolResult(value) {
  return { content: [{ type: 'text', text: JSON.stringify(value) }], structuredContent: value };
}

function imageResult({ meta, image }) {
  const content = [{ type: 'text', text: JSON.stringify(meta) }];
  if (image) content.push({ type: 'image', data: image, mimeType: meta.mimeType });
  return { content, structuredContent: meta };
}

function toolError(err) {
  const code = err instanceof UiError ? err.code : 'OBSERVATION_INCOMPLETE';
  const body = { error: { code, message: String(err?.message ?? err), hint: HINTS[code] ?? null, ...(err?.extra ?? {}) } };
  return { content: [{ type: 'text', text: JSON.stringify(body) }], isError: true };
}

export async function callTool(facade, name, args, signal) {
  try {
    const a = args ?? {};
    switch (name) {
      case 'ui_list_devices':
        return toolResult(await facade.listDevices());
      case 'ui_snapshot':
        return toolResult(await facade.serialize(() => facade.snapshot(a, signal)));
      case 'ui_find':
        return toolResult(facade.find(a));
      case 'ui_get_element':
        return toolResult(await facade.serialize(() => facade.getElement(a, signal)));
      case 'ui_click':
        return toolResult(await facade.serialize(() => facade.click(a, signal)));
      case 'ui_set_text':
        return toolResult(await facade.serialize(() => facade.setText(a, signal)));
      case 'ui_press_key':
        return toolResult(await facade.serialize(() => facade.pressKey(a, signal)));
      case 'ui_scroll':
        return toolResult(await facade.serialize(() => facade.scroll(a, signal)));
      case 'ui_wait_for':
        return toolResult(await facade.serialize(() => facade.waitFor(a, signal)));
      case 'ui_screenshot':
        return imageResult(await facade.serialize(() => facade.screenshot(a, signal)));
      default:
        return toolError(new UiError('ACTION_UNSUPPORTED', `unknown tool ${name}`));
    }
  } catch (err) {
    if (!(err instanceof UiError)) process.stderr.write(`ui-facade: ${err?.stack ?? err}\n`);
    return toolError(err);
  }
}

// ------------------------------------------------------------------ JSON-RPC over stdio

export function serve({ input = process.stdin, output = process.stdout, facade = new UiFacade() } = {}) {
  const inflight = new Map();
  const send = (msg) => output.write(JSON.stringify(msg) + '\n');
  const reply = (id, result) => send({ jsonrpc: '2.0', id, result });
  const fail = (id, code, message) => send({ jsonrpc: '2.0', id, error: { code, message } });

  const rl = createInterface({ input, crlfDelay: Infinity });
  rl.on('line', async (line) => {
    if (!line.trim()) return;
    let msg;
    try {
      msg = JSON.parse(line);
    } catch {
      return fail(null, -32700, 'parse error');
    }
    const { id, method, params } = msg;
    const isRequest = id !== undefined && id !== null && typeof method === 'string';
    if (!isRequest) {
      if (method === 'notifications/cancelled') inflight.get(params?.requestId)?.abort();
      return; // other notifications (initialized, ...) and stray responses need nothing
    }
    switch (method) {
      case 'initialize': {
        const asked = params?.protocolVersion;
        reply(id, {
          protocolVersion: LEGACY_VERSIONS.includes(asked) ? asked : FALLBACK_VERSION,
          capabilities: { tools: { listChanged: false } },
          serverInfo: SERVER_INFO,
          instructions: INSTRUCTIONS,
        });
        return;
      }
      case 'ping':
        return reply(id, {});
      case 'tools/list':
        return reply(id, { tools: TOOLS });
      case 'tools/call': {
        const controller = new AbortController();
        inflight.set(id, controller);
        try {
          reply(id, await callTool(facade, params?.name, params?.arguments, controller.signal));
        } finally {
          inflight.delete(id);
        }
        return;
      }
      default:
        // Includes `server/discover`: answering "method not found" makes the client fall back to initialize.
        return fail(id, -32601, `method not found: ${method}`);
    }
  });
  return rl;
}

function parseArgv(argv) {
  const opts = {};
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--relay') opts.relayCli = argv[++i];
    else if (argv[i] === '--approval-wait-ms') opts.approvalWaitMs = Number(argv[++i]);
  }
  return opts;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  serve({ facade: new UiFacade(parseArgv(process.argv.slice(2))) });
}
