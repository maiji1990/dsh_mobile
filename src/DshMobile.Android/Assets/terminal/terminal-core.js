// vNext PR-08 terminal page logic, kept apart from the DOM wiring (terminal.js) so tests/terminal-web can drive it with
// a fake terminal. The C# side owns sessions, PTY bytes, key mapping and every decision that leaves the page; this file
// only translates messages.
//
//   page -> host: {t:'ready',cols,rows} {t:'in',d} {t:'bin',d} {t:'resize',cols,rows} {t:'mode',appCursor}
//                 {t:'ack',n}            bytes of one 'out' batch, sent after xterm rendered it (flow control)
//                 {t:'search',index,count}   result of the last search (index -1: no match)
//                 {t:'selection',text}   answer to 'copy'
//                 {t:'link',uri}         a recognised http(s) link was tapped (the host confirms, then opens it)
//   host -> page: {t:'reset'} {t:'out',b:<base64>} {t:'paste',d} {t:'focus'}
//                 {t:'search',q,dir:'next'|'prev',caseSensitive} {t:'searchClear'} {t:'copy'} {t:'selectAll'}
//                 {t:'font',size} {t:'scroll',to:'top'|'bottom'}
'use strict';
(function (root, factory) {
  if (typeof module === 'object' && module.exports) { module.exports = factory(); } else { root.DshmTerminalCore = factory(); }
})(typeof self !== 'undefined' ? self : this, function () {
  var FONT_MIN = 8;
  var FONT_MAX = 32;
  var SEARCH_DECORATIONS = {
    matchBackground: '#5c4b00', matchOverviewRuler: '#ffd54f',
    activeMatchBackground: '#a67c00', activeMatchColorOverviewRuler: '#ffab00'
  };

  /** base64 -> raw bytes. Never a string: xterm decodes UTF-8 itself, across batch boundaries. */
  function decode(b64) {
    var s = atob(b64);
    var u = new Uint8Array(s.length);
    for (var i = 0; i < s.length; i++) { u[i] = s.charCodeAt(i); }
    return u;
  }

  function clampFont(n) {
    n = Math.round(Number(n));
    if (!isFinite(n)) { return null; }
    return Math.min(FONT_MAX, Math.max(FONT_MIN, n));
  }

  /** WebLinksAddon handler: only http(s) leaves the page, and only as a request to the host. */
  function linkHandler(send) {
    return function (event, uri) {
      if (typeof uri === 'string' && /^https?:\/\/[^\s]+$/i.test(uri) && uri.length <= 2048) { send({ t: 'link', uri: uri }); }
    };
  }

  /**
   * Wires a Terminal (and its search addon) to `send`. Returns {handle(message), ready()}.
   * `refit` recomputes rows / columns (FitAddon); a size change comes back through term.onResize.
   */
  function bind(term, search, send, refit) {
    var appCursor = false;

    // DECCKM decides what the host's arrow / HOME / END keys send; report changes after each write.
    function checkMode() {
      var m = term.modes.applicationCursorKeysMode;
      if (m !== appCursor) { appCursor = m; send({ t: 'mode', appCursor: m }); }
    }

    term.onData(function (d) { send({ t: 'in', d: d }); });
    term.onBinary(function (d) { send({ t: 'bin', d: d }); });
    term.onResize(function (s) { send({ t: 'resize', cols: s.cols, rows: s.rows }); });
    if (search) {
      search.onDidChangeResults(function (r) { send({ t: 'search', index: r.resultIndex, count: r.resultCount }); });
    }

    function handle(m) {
      if (!m || typeof m.t !== 'string') { return; }
      switch (m.t) {
        case 'reset': term.reset(); appCursor = false; break;
        case 'out': {
          if (typeof m.b !== 'string') { return; }
          var bytes = decode(m.b);
          term.write(bytes, function () { checkMode(); send({ t: 'ack', n: bytes.length }); });
          break;
        }
        case 'paste': term.paste(String(m.d)); break;
        case 'focus': term.focus(); break;
        case 'search': {
          if (!search || typeof m.q !== 'string' || m.q === '') { return; }
          var opts = { caseSensitive: m.caseSensitive === true, decorations: SEARCH_DECORATIONS };
          var found = m.dir === 'prev' ? search.findPrevious(m.q, opts) : search.findNext(m.q, opts);
          if (!found) { send({ t: 'search', index: -1, count: 0 }); }
          break;
        }
        case 'searchClear': if (search) { search.clearDecorations(); } term.clearSelection(); break;
        case 'copy': send({ t: 'selection', text: term.getSelection() }); break;
        case 'selectAll': term.selectAll(); break;
        case 'font': {
          var size = clampFont(m.size);
          if (size !== null && size !== term.options.fontSize) { term.options.fontSize = size; refit(); }
          break;
        }
        case 'scroll': if (m.to === 'top') { term.scrollToTop(); } else { term.scrollToBottom(); } break;
      }
    }

    function ready() { refit(); send({ t: 'ready', cols: term.cols, rows: term.rows }); }

    return { handle: handle, ready: ready };
  }

  return { decode: decode, clampFont: clampFont, linkHandler: linkHandler, bind: bind, FONT_MIN: FONT_MIN, FONT_MAX: FONT_MAX, SEARCH_DECORATIONS: SEARCH_DECORATIONS };
});
