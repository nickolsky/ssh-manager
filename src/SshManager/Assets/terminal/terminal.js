// Built-in terminal page: xterm.js talking to TerminalView (C#) over WebView2 messages.
//   C# -> page: out {d}, theme {dark,bg,fg,font,size}, closed {text}, opened, paste {d}, focus, banner {text},
//               config {auto,hint}, completions {id,items:[{l,k,d,del,ins}],ghost}
//   page -> C#: ready {cols,rows}, in {d}, size {cols,rows}, paste, copy {d}, key {k}, title {d}, link {url}, reconnect,
//               complete {id,line,cursor,explicit}, cmd {d}, cwd {d}, edit {d}
// Shell integration (OSC 7337 from the server, see shell-integration.sh): A prompt, B input starts, P;cwd, E;file.
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
    if (suggestKey(e, k) === false) { e.preventDefault(); return false; }
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
    if (ctrl && e.shiftKey && k === 'f') { send({ t: 'key', k: 'files' }); e.preventDefault(); return false; }
    if (ctrl && (k === '=' || k === '+')) { zoom(1); e.preventDefault(); return false; }
    if (ctrl && k === '-') { zoom(-1); e.preventDefault(); return false; }
    if (ctrl && k === '0') { setFontSize(14); e.preventDefault(); return false; }
    return true;
  });

  // ---------- right-click menu ----------
  const menuEl = document.getElementById('menu');
  let menuText = { copy: 'Copy', paste: 'Paste', selectAll: 'Select all', clear: 'Clear screen', files: 'Files here', dup: 'New terminal here' };
  const hideMenu = () => { menuEl.style.display = 'none'; };
  const MENU = () => [
    { id: 'copy', ic: '\uE8C8', k: 'Ctrl+Shift+C', off: !term.hasSelection(), run: () => { copySelection(); term.clearSelection(); } },
    { id: 'paste', ic: '\uE77F', k: 'Ctrl+V', off: closed, run: () => send({ t: 'paste' }) },
    { id: 'selectAll', ic: '\uE8B3', k: '', run: () => term.selectAll() },
    { sep: true },
    { id: 'clear', ic: '\uE894', k: '', run: () => term.clear() },
    { sep: true },
    { id: 'files', ic: '\uE8B7', k: 'Ctrl+Shift+F', run: () => send({ t: 'key', k: 'files' }) },
    { id: 'dup', ic: '\uE756', k: 'Ctrl+Shift+T', run: () => send({ t: 'key', k: 'duplicate' }) },
  ];
  document.addEventListener('contextmenu', (e) => {
    e.preventDefault();
    hideSuggest();
    const items = MENU();
    menuEl.innerHTML = items.map((it, i) => it.sep ? '<div class="sep"></div>'
      : '<div class="mi' + (it.off ? ' off' : '') + '" data-i="' + i + '"><span class="ic">' + it.ic + '</span><span>' +
        esc(menuText[it.id] || it.id) + '</span><span class="k">' + it.k + '</span></div>').join('');
    menuEl.style.display = 'block';
    const w = menuEl.offsetWidth, h = menuEl.offsetHeight;
    menuEl.style.left = Math.max(2, Math.min(e.clientX, window.innerWidth - w - 4)) + 'px';
    menuEl.style.top = Math.max(2, Math.min(e.clientY, window.innerHeight - h - 4)) + 'px';
    menuEl.onclick = (ev) => {
      const el = ev.target.closest('.mi');
      if (!el) return;
      hideMenu();
      items[+el.dataset.i].run();
      term.focus();
    };
  });
  menuEl.addEventListener('mousedown', (e) => e.preventDefault()); // keep the selection
  document.addEventListener('mousedown', (e) => { if (!menuEl.contains(e.target)) hideMenu(); });
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape') hideMenu(); }, true);
  window.addEventListener('blur', hideMenu);

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

  // ---------- shell integration and suggestions ----------
  // state: none (no integration) | prompt (drawing it) | input (typing a command) | running
  const shell = { state: 'none', marker: null, x: 0 };
  const ghostEl = document.getElementById('ghost');
  const popEl = document.getElementById('suggest');
  const pop = { items: [], sel: 0, touched: false, visible: false, sticky: false };
  let config = { auto: true, hint: '' };
  let ghost = '';
  let reqId = 0, reqExplicit = false, lastKey = '', timer = 0;

  term.parser.registerOscHandler(7337, (data) => {
    const i = data.indexOf(';');
    const k = i < 0 ? data : data.slice(0, i);
    const v = i < 0 ? '' : data.slice(i + 1);
    switch (k) {
      case 'A':
        shell.state = 'prompt';
        hideSuggest();
        break;
      case 'B': {
        if (shell.marker) shell.marker.dispose();
        shell.marker = term.registerMarker(0);
        shell.x = term.buffer.active.cursorX;
        shell.state = 'input';
        lastKey = '';
        break;
      }
      case 'P': send({ t: 'cwd', d: v }); break;
      case 'E': send({ t: 'edit', d: v }); break;
    }
    return true;
  });

  // The command line as the terminal shows it: from the end of the prompt, following wrapped rows.
  const readInput = () => {
    if (shell.state !== 'input' || closed || !shell.marker || shell.marker.isDisposed || shell.marker.line < 0) return null;
    const b = term.buffer.active;
    if (b.type !== 'normal') return null;
    const y0 = shell.marker.line, cy = b.baseY + b.cursorY;
    if (cy < y0) return null;
    let text = '', cursor = -1;
    for (let y = y0; y < b.length; y++) {
      const line = b.getLine(y);
      if (!line || (y > y0 && !line.isWrapped)) break;
      for (let x = y === y0 ? shell.x : 0; x < term.cols; x++) {
        if (y === cy && x === b.cursorX) cursor = text.length;
        const cell = line.getCell(x);
        if (!cell || cell.getWidth() === 0) continue;
        text += cell.getChars() || ' ';
      }
    }
    if (cursor < 0) cursor = text.replace(/\s+$/, '').length;
    // a right prompt (zsh RPROMPT) after the input is not part of it
    const after = text.slice(cursor).replace(/\s{3,}.*$/, '').replace(/\s+$/, '');
    return { line: text.slice(0, cursor) + after, cursor, atEnd: after.length === 0 };
  };

  const scheduleSuggest = () => {
    if (shell.state !== 'input') return;
    clearTimeout(timer);
    timer = setTimeout(() => requestSuggest(false), 40);
  };
  term.onWriteParsed(scheduleSuggest);

  const requestSuggest = (explicit) => {
    const inp = readInput();
    if (!inp) { hideSuggest(); return; }
    const key = inp.line + '\u0001' + inp.cursor;
    if (!explicit && key === lastKey) return;
    lastKey = key;
    if (!explicit && inp.line.trim() === '') { hideSuggest(); return; }
    reqExplicit = !!explicit;
    send({ t: 'complete', id: ++reqId, line: inp.line, cursor: inp.cursor, explicit: !!explicit });
  };

  const onCompletions = (m) => {
    if (m.id !== reqId) return;
    const inp = readInput();
    if (!inp || inp.line + '\u0001' + inp.cursor !== lastKey) return;
    showGhost(inp.atEnd ? (m.ghost || '') : '');
    if (reqExplicit) pop.sticky = true;
    if ((config.auto || pop.sticky) && m.items && m.items.length) showPopup(m.items, inp);
    else hidePopup();
  };

  const cell = () => {
    const screen = term.element.querySelector('.xterm-screen');
    const r = screen.getBoundingClientRect();
    return { left: r.left, top: r.top, w: r.width / term.cols, h: r.height / term.rows };
  };
  const cursorVisible = () => term.buffer.active.viewportY === term.buffer.active.baseY;

  const showGhost = (text) => {
    ghost = text;
    const b = term.buffer.active;
    const room = term.cols - b.cursorX;
    if (!text || !cursorVisible() || room <= 0) { ghostEl.style.display = 'none'; return; }
    const c = cell();
    ghostEl.textContent = text.length > room ? text.slice(0, room) : text;
    ghostEl.style.left = (c.left + b.cursorX * c.w) + 'px';
    ghostEl.style.top = (c.top + b.cursorY * c.h) + 'px';
    ghostEl.style.height = c.h + 'px';
    ghostEl.style.lineHeight = c.h + 'px';
    ghostEl.style.fontFamily = term.options.fontFamily;
    ghostEl.style.fontSize = term.options.fontSize + 'px';
    ghostEl.style.display = 'block';
  };

  const ICONS = {
    history: '\uE81C', command: '\uE756', sub: '\uE76C', flag: '\uE8EC', dir: '\uE8B7', file: '\uE8A5',
    container: '\uE7B8', image: '\uE7B8', service: '\uE713', package: '\uE7B8', branch: '\uE8AB', value: '\uE8FD',
  };
  const esc = (s) => String(s).replace(/[&<>"]/g, (ch) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[ch]));

  // the typed part of the label in the accent colour
  const highlight = (label, typed) => {
    if (!typed) return esc(label);
    const lo = label.toLowerCase(), t = typed.toLowerCase();
    const i = lo.startsWith(t) ? 0 : lo.indexOf(t);
    if (i < 0) return esc(label);
    return esc(label.slice(0, i)) + '<b>' + esc(label.slice(i, i + t.length)) + '</b>' + esc(label.slice(i + t.length));
  };

  const typedWord = (inp) => {
    const before = inp.line.slice(0, inp.cursor);
    return before.slice(Math.max(before.lastIndexOf(' '), before.lastIndexOf('/'), before.lastIndexOf('=')) + 1);
  };

  const showPopup = (items, inp) => {
    const keep = pop.visible && pop.touched && pop.items[pop.sel] ? pop.items[pop.sel].l : null;
    pop.items = items;
    pop.sel = keep ? Math.max(0, items.findIndex((it) => it.l === keep)) : 0;
    if (!keep) pop.touched = false;
    const before = inp.line.slice(0, inp.cursor);
    const word = typedWord(inp);
    popEl.innerHTML = items.map((it, i) =>
      '<div class="it' + (i === pop.sel ? ' sel' : '') + '" data-i="' + i + '">' +
      '<span class="ic">' + (ICONS[it.k] || '') + '</span>' +
      '<span class="lb">' + highlight(it.l, it.k === 'history' ? before : word) + '</span>' +
      (it.d ? '<span class="dt">' + esc(it.d) + '</span>' : '') + '</div>').join('') +
      (config.hint ? '<div class="hint">' + esc(config.hint) + '</div>' : '');
    popEl.style.display = 'block';
    pop.visible = true;
    place(inp);
    scrollToSel();
  };

  // under the word being completed, above the line when there is no room below
  const place = (inp) => {
    const b = term.buffer.active;
    if (!cursorVisible()) { hidePopup(); return; }
    const c = cell();
    const word = Math.min(typedWord(inp).length, b.cursorX);
    const x = c.left + (b.cursorX - word) * c.w - 30;
    const rowTop = c.top + b.cursorY * c.h;
    const h = popEl.offsetHeight, w = popEl.offsetWidth;
    const below = rowTop + c.h + 2;
    const top = below + h <= window.innerHeight - 4 || rowTop - h - 2 < 0 ? below : rowTop - h - 2;
    popEl.style.top = Math.max(0, top) + 'px';
    popEl.style.left = Math.max(2, Math.min(x, window.innerWidth - w - 4)) + 'px';
  };

  const scrollToSel = () => {
    const el = popEl.querySelector('.it.sel');
    if (el) el.scrollIntoView({ block: 'nearest' });
  };

  const select = (i) => {
    if (!pop.items.length) return;
    pop.sel = (i + pop.items.length) % pop.items.length;
    pop.touched = true;
    popEl.querySelectorAll('.it').forEach((el) => el.classList.toggle('sel', +el.dataset.i === pop.sel));
    scrollToSel();
    ghostEl.style.display = 'none';
  };

  const hidePopup = () => {
    pop.visible = false;
    pop.touched = false;
    popEl.style.display = 'none';
  };
  const hideSuggest = () => {
    hidePopup();
    pop.sticky = false;
    ghost = '';
    ghostEl.style.display = 'none';
  };

  const accept = (it) => {
    hideSuggest();
    if (!it) return;
    const d = '\x7f'.repeat(it.del || 0) + (it.ins || '');
    if (d) send({ t: 'in', d });
  };

  popEl.addEventListener('mousedown', (e) => e.preventDefault()); // keep the focus in the terminal
  popEl.addEventListener('click', (e) => {
    const el = e.target.closest('.it');
    if (el && pop.items[+el.dataset.i]) accept(pop.items[+el.dataset.i]);
    term.focus();
  });
  term.onScroll(() => { if (!cursorVisible()) hideSuggest(); });
  if (term.textarea) term.textarea.addEventListener('blur', () => hidePopup());

  // keys for the popup and the grey suggestion; false = handled here
  const suggestKey = (e, k) => {
    const plain = !e.ctrlKey && !e.altKey && !e.metaKey && !e.shiftKey;
    if (e.ctrlKey && !e.altKey && !e.shiftKey && e.code === 'Space') {
      if (shell.state !== 'input') return true;
      requestSuggest(true);
      return false;
    }
    if (pop.visible) {
      if (plain && k === 'arrowdown') { select(pop.sel + 1); return false; }
      if (plain && k === 'arrowup') { select(pop.sel - 1); return false; }
      if (plain && k === 'pagedown') { select(Math.min(pop.items.length - 1, pop.sel + 8)); return false; }
      if (plain && k === 'pageup') { select(Math.max(0, pop.sel - 8)); return false; }
      if (plain && k === 'tab') { accept(pop.items[pop.sel]); return false; }
      if (plain && k === 'enter' && pop.touched) { accept(pop.items[pop.sel]); return false; }
      if (k === 'escape') { hideSuggest(); return false; }
    } else if (ghost && k === 'escape') {
      hideSuggest();
      return false;
    }
    if (ghost && plain && (k === 'arrowright' || k === 'end')) {
      const inp = readInput();
      if (inp && inp.atEnd) { const g = ghost; hideSuggest(); send({ t: 'in', d: g }); return false; }
    }
    if (ghost && e.ctrlKey && !e.shiftKey && !e.altKey && k === 'arrowright') {
      const inp = readInput();
      const part = (ghost.match(/^\s*[^\s\/]+\/?/) || [ghost])[0];
      if (inp && inp.atEnd) { hideSuggest(); send({ t: 'in', d: part }); return false; }
    }
    if (k === 'enter' && shell.state === 'input') {
      const inp = readInput();
      if (inp && inp.line.trim()) send({ t: 'cmd', d: inp.line });
      shell.state = 'running';
      hideSuggest();
    }
    return true;
  };

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
        const pal = m.dark
          ? { '--pop-bg': '#2b2b2b', '--pop-border': '#454545', '--pop-sel': '#04395e', '--pop-match': '#4fb6ff', '--ghost': '#7a7a7a' }
          : { '--pop-bg': '#ffffff', '--pop-border': '#d0d7de', '--pop-sel': '#cfe3ff', '--pop-match': '#0969da', '--ghost': '#8c959f' };
        for (const [k, v] of Object.entries(pal)) document.documentElement.style.setProperty(k, v);
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
        shell.state = 'none';
        hideSuggest();
        term.options.cursorBlink = false;
        term.write('\r\n\x1b[90m' + (m.text || '') + '\x1b[0m\r\n');
        break;
      case 'banner':
        showBanner(m.text);
        break;
      case 'focus':
        term.focus();
        break;
      case 'config':
        config = { auto: m.auto !== false, hint: m.hint || '' };
        if (m.menu) menuText = Object.assign(menuText, m.menu);
        break;
      case 'completions':
        onCompletions(m);
        break;
    }
  });

  refit();
  send({ t: 'ready', cols: term.cols, rows: term.rows });
  term.focus();
})();
