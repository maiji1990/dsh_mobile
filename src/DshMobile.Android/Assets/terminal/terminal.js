// PR-06 C# terminal page glue (vNext PR-08: search, links, Unicode 11 widths, flow control; the message logic is in
// terminal-core.js). xterm.js (vendored, tooling/terminal-web.lock.json) is only the emulator: the C# side owns the
// sessions, the PTY bytes and every key mapping. Bytes go both ways over the WebMessagePort the host hands over once
// (never the JSON bridge, never the network).
'use strict';
(function () {
  var core = DshmTerminalCore;
  var term = new Terminal({
    fontFamily: 'monospace',
    fontSize: 14,
    cursorBlink: true,
    scrollback: 5000,
    allowProposedApi: true, // unicode + search decorations are still "proposed" in xterm 6.0.0
    theme: { background: '#000000' }
  });
  var fit = new FitAddon.FitAddon();
  var search = new SearchAddon.SearchAddon({ highlightLimit: 1000 });
  term.loadAddon(fit);
  term.loadAddon(search);
  term.loadAddon(new Unicode11Addon.Unicode11Addon());
  term.unicode.activeVersion = '11'; // CJK / emoji take two columns, as in the guest's wcwidth
  term.open(document.getElementById('terminal'));

  var port = null;
  function send(m) {
    if (port) { port.postMessage(JSON.stringify(m)); }
  }
  term.loadAddon(new WebLinksAddon.WebLinksAddon(core.linkHandler(send)));

  function refit() {
    try { fit.fit(); } catch (e) { /* zero-size while hidden */ }
  }
  var page = core.bind(term, search, send, refit);
  new ResizeObserver(refit).observe(document.getElementById('terminal'));

  window.addEventListener('message', function (e) {
    if (port || e.data !== 'dshm-port' || !e.ports || e.ports.length !== 1) { return; }
    port = e.ports[0];
    port.onmessage = function (ev) {
      var m;
      try { m = JSON.parse(ev.data); } catch (x) { return; }
      page.handle(m);
    };
    page.ready();
  });
})();
