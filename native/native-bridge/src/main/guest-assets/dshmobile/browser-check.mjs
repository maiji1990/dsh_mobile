#!/usr/bin/env node
// Guest self-check for browser automation (TASK-011, ADR-0005). Uses the browser-use adapter's OWN client
// (the code behind the 24 browser_* tools) from its staged copy, so this exercises exactly
//   plugin client -> /opt/browser/bin/browser -> BrowserCliBroker -> BrowserCommandService -> WebView
// without DSH or a model. Read-only: `status`, then `observe` of the page the user has open. It never requests
// or uses AI control, never navigates, clicks or types.
//
// Prints one line per step and a final `BROWSERCHECK {...}` JSON line.
// Exit: 0 status+observe OK; 2 Browser not open (no session) or automation off; 3 broker/CLI unavailable;
// 4 observe failed for another reason; 6 the check itself crashed.
import { createRequire } from 'node:module';

const stage = process.argv[2] ?? '/root/projects/.dsh/dshbox/dshbox-plugins/browser-use';
const summary = { status: null, observe: null };
let exit = 6;
try {
  const require = createRequire(import.meta.url);
  const { browserRequest } = require(`${stage}/plugin/lib/client.js`);

  const status = await browserRequest('status', {}, {});
  summary.status = {
    ok: status.ok, code: status.code, state: status.state,
    sessionGeneration: status.sessionGenerationAvailable ? status.sessionGeneration : null,
    controlGeneration: status.controlGenerationAvailable ? status.controlGeneration : null,
    data: safeJson(status.dataJson),
  };
  console.log(`status: ${status.code} state=${status.state || '-'} session=${summary.status.sessionGeneration ?? '-'}`);
  console.log(`  ${status.dataJson.slice(0, 600)}`);

  if (!status.ok) {
    exit = ['BROKER_UNAVAILABLE', 'CLI_UNAVAILABLE'].includes(status.code) ? 3 : 4;
  } else if (!status.sessionGenerationAvailable || status.state === 'CLOSED') {
    // A closed Browser still reports its last sessionGeneration (seen on device after idle_timeout); it is not a
    // live session, so don't ask it to observe.
    console.log('observe: skipped (no open Browser session; open the browser and load a page first)');
    exit = 2;
  } else {
    const observe = await browserRequest('observe', { maxTextChars: 400 }, { sessionGeneration: status.sessionGeneration });
    const data = safeJson(observe.dataJson) ?? {};
    summary.observe = {
      ok: observe.ok, code: observe.code, executed: observe.executed,
      url: data.url ?? null, title: data.title ?? null,
      textChars: typeof data.text === 'string' ? data.text.length : null,
      elements: Array.isArray(data.elements) ? data.elements.length : null,
    };
    console.log(`observe: ${observe.code} ${JSON.stringify(summary.observe)}`);
    if (typeof data.text === 'string') console.log(`  text: ${JSON.stringify(data.text.slice(0, 160))}`);
    if (!observe.ok) console.log(`  ${observe.text}`);
    exit = observe.ok ? 0 : (['AUTOMATION_DISABLED', 'BROWSER_CLOSED', 'SESSION_CLOSED', 'NO_ACTIVE_SESSION'].includes(observe.code) ? 2 : 4);
  }
} catch (err) {
  console.log(`check failed: ${err?.stack ?? err}`);
  exit = 6;
} finally {
  console.log(`BROWSERCHECK ${JSON.stringify({ exit, ...summary })}`);
}
process.exit(exit);

function safeJson(text) {
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}
