// Built-in terminal page: xterm.js talking to TerminalView (C#) over WebView2 messages.
//   C# -> page: out {d}, theme {dark,bg,fg,font,size}, closed {text}, opened, paste {d}, focus, banner {text}
//   page -> C#: ready {cols,rows}, in {d}, size {cols,rows}, paste, copy {d}, key {k}, title {d}, link {url}, reconnect
(() => {
  'use strict';
  const host = window.chrome && window.chrome.webview;
  const send = (msg) => host && host.postMessage(msg);

  const DARK = {
    background: '#1f1f1f', foreground: '#e6e6e6', cursor: '#e6e6e6', cursorAccent: '#1f1f1f',
    selectionBackground: '#3a5b86',
    black: '#0c0c0c', red: '#e5534b', green: '#57ab5a', yellow: '#c69026', blue: '#539bf5', magenta: '#b083f0',
    cyan: '#39c5cf', white: '#cccccc', brightBlack: '#767676', brightRed: '#ff7b72', brightGreen: '#6bc46d',
    brightYellow: '#daaa3f', brightBlue: '#6cb6ff', brightMagenta: '#dcbdfb', brightCyan: '#56d4dd', brightWhite: '#f2f2f2',
  };
  const LIGHT = {
    background: '#fbfbfb', foreground: '#1f2328', cursor: '#1f2328', cursorAccent: '#fbfbfb',
    selectionBackground: '#b6d6ff',
    black: '#24292f', red: '#cf222e', green: '#116329', yellow: '#7d4e00', blue: '#0969da', magenta: '#8250df',
    cyan: '#1b7c83', white: '#6e7781', brightBlack: '#57606a', brightRed: '#a40e26', brightGreen: '#1a7f37',
    brightYellow: '#633c01', brightBlue: '#218bff', brightMagenta: '#a475f9', brightCyan: '#3192aa', brightWhite: '#8c959f',
  };

  const term = new Terminal({
    allowProposedApi: true,
    cursorBlink: true,
    scrollback: 20000,
    fontFamily: '"Cascadia Mono", "Cascadia Code", Consolas, monospace',
    fontSize: 14,
    theme: DARK,
    rightClickSelectsWord: false,
    macOptionIsMeta: false,
  });
  const fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  term.loadAddon(new WebLinksAddon.WebLinksAddon((e, url) => send({ t: 'link', url })));
  try {
    term.loadAddon(new Unicode11Addon.Unicode11Addon());
    term.unicode.activeVersion = '11';
  } catch (_) { /* optional */ }
  term.open(document.getElementById('term'));

  let closed = false;
  const banner = document.getElementById('banner');
  const showBanner = (text) => { banner.textContent = text || ''; banner.style.display = text ? 'block' : 'none'; };

  // ---------- size ----------
  let lastCols = 0, lastRows = 0;
  const refit = () => {
    try { fit.fit(); } catch (_) { return; }
    if (term.cols !== lastCols || term.rows !== lastRows) {
      lastCols = term.cols; lastRows = term.rows;
      send({ t: 'size', cols: term.cols, rows: term.rows });
    }
  };
  new ResizeObserver(() => refit()).observe(document.getElementById('term'));

  // ---------- input ----------
  term.onData((d) => {
    if (closed) {
      if (d === '\r') send({ t: 'reconnect' });
      return;
    }
    send({ t: 'in', d });
  });
  term.onTitleChange((d) => send({ t: 'title', d }));

  const copySelection = () => {
    const s = term.getSelection();
    if (s) send({ t: 'copy', d: s });
    return !!s;
  };

  term.attachCustomKeyEventHandler((e) => {
    if (e.type !== 'keydown') return true;
    const ctrl = e.ctrlKey && !e.altKey && !e.metaKey;
    const k = e.key.toLowerCase();
    // copy: Ctrl+Shift+C, Ctrl+Insert, or Ctrl+C while something is selected
    if ((ctrl && e.shiftKey && k === 'c') || (ctrl && k === 'insert') || (ctrl && !e.shiftKey && k === 'c' && term.hasSelection())) {
      copySelection();
      term.clearSelection();
      e.preventDefault();
      return false;
    }
    // paste: Ctrl+V, Ctrl+Shift+V, Shift+Insert
    if ((ctrl && k === 'v') || (e.shiftKey && !e.ctrlKey && k === 'insert')) {
      send({ t: 'paste' });
      e.preventDefault();
      return false;
    }
    // tabs and zoom are handled by the app
    if (ctrl && k === 'tab') { send({ t: 'key', k: e.shiftKey ? 'prev' : 'next' }); e.preventDefault(); return false; }
    if (ctrl && e.shiftKey && k === 'w') { send({ t: 'key', k: 'close' }); e.preventDefault(); return false; }
    if (ctrl && e.shiftKey && k === 't') { send({ t: 'key', k: 'duplicate' }); e.preventDefault(); return false; }
    if (ctrl && (k === '=' || k === '+')) { zoom(1); e.preventDefault(); return false; }
    if (ctrl && k === '-') { zoom(-1); e.preventDefault(); return false; }
    if (ctrl && k === '0') { setFontSize(14); e.preventDefault(); return false; }
    return true;
  });

  // right click: copy the selection, or paste when nothing is selected (PuTTY / Windows Terminal style)
  document.addEventListener('contextmenu', (e) => {
    e.preventDefault();
    if (!copySelection()) send({ t: 'paste' });
    else term.clearSelection();
  });

  document.addEventListener('wheel', (e) => {
    if (!e.ctrlKey) return;
    e.preventDefault();
    zoom(e.deltaY < 0 ? 1 : -1);
  }, { passive: false });

  const setFontSize = (size) => {
    term.options.fontSize = Math.max(8, Math.min(32, size));
    refit();
    send({ t: 'fontsize', size: term.options.fontSize });
  };
  const zoom = (dir) => setFontSize(term.options.fontSize + dir);

  // ---------- messages from the app ----------
  host && host.addEventListener('message', (ev) => {
    const m = ev.data;
    switch (m.t) {
      case 'out':
        term.write(m.d);
        break;
      case 'paste':
        if (!closed && m.d) term.paste(m.d);
        break;
      case 'theme': {
        const theme = Object.assign({}, m.dark ? DARK : LIGHT);
        if (m.bg) { theme.background = m.bg; theme.cursorAccent = m.bg; }
        term.options.theme = theme;
        document.documentElement.style.setProperty('--bg', theme.background);
        document.documentElement.style.setProperty('--fg', theme.foreground);
        document.documentElement.style.setProperty('--muted', m.dark ? '#9a9a9a' : '#6e7781');
        if (m.font) term.options.fontFamily = m.font;
        if (m.size) term.options.fontSize = m.size;
        refit();
        break;
      }
      case 'opened':
        closed = false;
        showBanner('');
        term.options.cursorBlink = true;
        break;
      case 'closed':
        closed = true;
        term.options.cursorBlink = false;
        term.write('\r\n\x1b[90m' + (m.text || '') + '\x1b[0m\r\n');
        break;
      case 'banner':
        showBanner(m.text);
        break;
      case 'focus':
        term.focus();
        break;
    }
  });

  refit();
  send({ t: 'ready', cols: term.cols, rows: term.rows });
  term.focus();
})();
