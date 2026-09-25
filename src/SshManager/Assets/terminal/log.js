// Read-only terminal for the AI agent log (LogConsoleView in C#): the app writes ANSI text, nothing is typed.
//   C# -> page: reset {d}, out {d}, theme {dark,bg}, labels {copy,selectAll}
//   page -> C#: ready, copy {d}, link {url}
(() => {
  'use strict';
  const host = window.chrome && window.chrome.webview;
  const send = (msg) => host && host.postMessage(msg);

  const DARK = {
    background: '#1f1f1f', foreground: '#e6e6e6', cursor: '#1f1f1f', cursorAccent: '#1f1f1f',
    selectionBackground: '#3a5b86',
    black: '#0c0c0c', red: '#e5534b', green: '#57ab5a', yellow: '#c69026', blue: '#539bf5', magenta: '#b083f0',
    cyan: '#39c5cf', white: '#cccccc', brightBlack: '#8b8b8b', brightRed: '#ff7b72', brightGreen: '#6bc46d',
    brightYellow: '#daaa3f', brightBlue: '#6cb6ff', brightMagenta: '#dcbdfb', brightCyan: '#56d4dd', brightWhite: '#f2f2f2',
  };
  const LIGHT = {
    background: '#fbfbfb', foreground: '#1f2328', cursor: '#fbfbfb', cursorAccent: '#fbfbfb',
    selectionBackground: '#b6d6ff',
    black: '#24292f', red: '#cf222e', green: '#116329', yellow: '#7d4e00', blue: '#0969da', magenta: '#8250df',
    cyan: '#1b7c83', white: '#6e7781', brightBlack: '#6e7781', brightRed: '#a40e26', brightGreen: '#1a7f37',
    brightYellow: '#633c01', brightBlue: '#218bff', brightMagenta: '#a475f9', brightCyan: '#3192aa', brightWhite: '#8c959f',
  };

  const term = new Terminal({
    allowProposedApi: true,
    disableStdin: true,
    cursorBlink: false,
    cursorInactiveStyle: 'none',
    scrollback: 50000,
    convertEol: true,
    fontFamily: '"Cascadia Mono", "Cascadia Code", Consolas, monospace',
    fontSize: 13,
    theme: DARK,
  });
  const fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  term.loadAddon(new WebLinksAddon.WebLinksAddon((e, url) => send({ t: 'link', url })));
  try {
    term.loadAddon(new Unicode11Addon.Unicode11Addon());
    term.unicode.activeVersion = '11';
  } catch (_) { /* optional */ }
  term.open(document.getElementById('term'));
  const HIDE_CURSOR = '\x1b[?25l';
  term.write(HIDE_CURSOR);

  const refit = () => { try { fit.fit(); } catch (_) { /* not laid out yet */ } };
  new ResizeObserver(() => refit()).observe(document.getElementById('term'));

  const copySelection = () => {
    const text = term.getSelection();
    if (text) send({ t: 'copy', d: text });
  };

  term.attachCustomKeyEventHandler((e) => {
    if (e.type !== 'keydown') return true;
    const ctrl = e.ctrlKey && !e.altKey && !e.metaKey;
    const k = e.key.toLowerCase();
    if (ctrl && (k === 'c' || k === 'insert')) { copySelection(); e.preventDefault(); return false; }
    if (ctrl && k === 'a') { term.selectAll(); e.preventDefault(); return false; }
    if (ctrl && k === 'end') { term.scrollToBottom(); e.preventDefault(); return false; }
    if (ctrl && k === 'home') { term.scrollToTop(); e.preventDefault(); return false; }
    if (k === 'pageup') { term.scrollPages(-1); e.preventDefault(); return false; }
    if (k === 'pagedown') { term.scrollPages(1); e.preventDefault(); return false; }
    if (ctrl && (k === '=' || k === '+')) { zoom(1); e.preventDefault(); return false; }
    if (ctrl && k === '-') { zoom(-1); e.preventDefault(); return false; }
    if (ctrl && k === '0') { term.options.fontSize = 13; refit(); e.preventDefault(); return false; }
    return false;
  });

  const zoom = (dir) => {
    term.options.fontSize = Math.max(8, Math.min(32, term.options.fontSize + dir));
    refit();
  };
  document.addEventListener('wheel', (e) => {
    if (!e.ctrlKey) return;
    e.preventDefault();
    zoom(e.deltaY < 0 ? 1 : -1);
  }, { passive: false });

  // ---------- right-click menu ----------
  const menuEl = document.getElementById('menu');
  let labels = { copy: 'Copy', selectAll: 'Select all' };
  const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
  const hideMenu = () => { menuEl.style.display = 'none'; };
  document.addEventListener('contextmenu', (e) => {
    e.preventDefault();
    const items = [
      { id: 'copy', ic: '', k: 'Ctrl+C', off: !term.hasSelection(), run: () => { copySelection(); term.clearSelection(); } },
      { id: 'selectAll', ic: '', k: 'Ctrl+A', run: () => term.selectAll() },
    ];
    menuEl.innerHTML = items.map((it, i) =>
      '<div class="mi' + (it.off ? ' off' : '') + '" data-i="' + i + '"><span class="ic">' + it.ic + '</span><span>' +
      esc(labels[it.id] || it.id) + '</span><span class="k">' + it.k + '</span></div>').join('');
    menuEl.style.display = 'block';
    const w = menuEl.offsetWidth, h = menuEl.offsetHeight;
    menuEl.style.left = Math.max(2, Math.min(e.clientX, window.innerWidth - w - 4)) + 'px';
    menuEl.style.top = Math.max(2, Math.min(e.clientY, window.innerHeight - h - 4)) + 'px';
    menuEl.onclick = (ev) => {
      const el = ev.target.closest('.mi');
      if (!el) return;
      hideMenu();
      items[+el.dataset.i].run();
    };
  });
  menuEl.addEventListener('mousedown', (e) => e.preventDefault()); // keep the selection
  document.addEventListener('mousedown', (e) => { if (!menuEl.contains(e.target)) hideMenu(); });
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape') hideMenu(); }, true);
  window.addEventListener('blur', hideMenu);

  // ---------- messages from the app ----------
  host && host.addEventListener('message', (ev) => {
    const m = ev.data;
    switch (m.t) {
      case 'reset':
        term.reset();
        term.write(HIDE_CURSOR + (m.d || ''), () => term.scrollToBottom());
        break;
      case 'out':
        term.write(m.d || '');
        break;
      case 'theme': {
        const theme = Object.assign({}, m.dark ? DARK : LIGHT);
        if (m.bg) { theme.background = m.bg; theme.cursor = m.bg; theme.cursorAccent = m.bg; }
        term.options.theme = theme;
        const root = document.documentElement.style;
        root.setProperty('--bg', theme.background);
        root.setProperty('--fg', theme.foreground);
        root.setProperty('--muted', m.dark ? '#9a9a9a' : '#6e7781');
        const pal = m.dark
          ? { '--pop-bg': '#2b2b2b', '--pop-border': '#454545', '--pop-sel': '#04395e' }
          : { '--pop-bg': '#ffffff', '--pop-border': '#d0d7de', '--pop-sel': '#cfe3ff' };
        for (const [k, v] of Object.entries(pal)) root.setProperty(k, v);
        refit();
        break;
      }
      case 'labels':
        labels = Object.assign(labels, m);
        break;
    }
  });

  refit();
  send({ t: 'ready' });
})();
