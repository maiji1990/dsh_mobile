// PR-06 C# terminal page glue. xterm.js (vendored, tooling/terminal-web.lock.json) is only the emulator: the C#
// activity owns the sessions, the PTY bytes and every key mapping. This file just pipes bytes both ways over the
// WebMessagePort the host hands over once (never the JSON bridge, never the network).
//
//   page -> host: {t:'ready',cols,rows} {t:'in',d} {t:'bin',d} {t:'resize',cols,rows} {t:'mode',appCursor}
//   host -> page: {t:'reset'} {t:'out',b:<base64>} {t:'paste',d} {t:'focus'}
'use strict';
(function () {
  var term = new Terminal({
    fontFamily: 'monospace',
    fontSize: 14,
    cursorBlink: true,
    scrollback: 5000,
    theme: { background: '#000000' }
  });
  var fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  term.open(document.getElementById('terminal'));

  var port = null;
  var appCursor = false;

  function send(m) {
    if (port) { port.postMessage(JSON.stringify(m)); }
  }

  function decode(b64) {
    var s = atob(b64);
    var u = new Uint8Array(s.length);
    for (var i = 0; i < s.length; i++) { u[i] = s.charCodeAt(i); }
    return u;
  }

  // DECCKM decides what the host's arrow / HOME / END keys send; report changes after each write.
  function checkMode() {
    var m = term.modes.applicationCursorKeysMode;
    if (m !== appCursor) { appCursor = m; send({ t: 'mode', appCursor: m }); }
  }

  function refit() {
    try { fit.fit(); } catch (e) { /* zero-size while hidden */ }
  }

  term.onData(function (d) { send({ t: 'in', d: d }); });
  term.onBinary(function (d) { send({ t: 'bin', d: d }); });
  term.onResize(function (s) { send({ t: 'resize', cols: s.cols, rows: s.rows }); });
  new ResizeObserver(refit).observe(document.getElementById('terminal'));

  window.addEventListener('message', function (e) {
    if (port || e.data !== 'dshm-port' || !e.ports || e.ports.length !== 1) { return; }
    port = e.ports[0];
    port.onmessage = function (ev) {
      var m;
      try { m = JSON.parse(ev.data); } catch (x) { return; }
      switch (m.t) {
        case 'reset': term.reset(); appCursor = false; break;
        case 'out': term.write(decode(m.b), checkMode); break;
        case 'paste': term.paste(String(m.d)); break;
        case 'focus': term.focus(); break;
      }
    };
    refit();
    send({ t: 'ready', cols: term.cols, rows: term.rows });
  });
})();
