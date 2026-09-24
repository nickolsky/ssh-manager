// Built-in editor page: CodeMirror 6 talking to EditorView (C#) over WebView2 messages.
//   C# -> page: load {text,name,path,readOnly}, theme {dark,bg}, saved, banner {text}, focus, wrap {on}, requestSave {close}
//   page -> C#: ready, dirty {on}, save {text,close}, key {k}, status {line,col,sel,lines,lang}, wrap {on}
(() => {
  'use strict';
  const host = window.chrome && window.chrome.webview;
  const send = (msg) => host && host.postMessage(msg);
  const { EditorState, EditorView, Compartment, basicSetup, keymap, indentWithTab, oneDark, languages } = window.CM;

  const theme = new Compartment();
  const lang = new Compartment();
  const readOnly = new Compartment();
  const wrap = new Compartment();
  const font = new Compartment();
  const banner = document.getElementById('banner');
  let savedDoc = null;
  let dirty = false;
  let langName = '';
  let fontSize = 14;

  // ---------- language by file name (and the #! line) ----------
  const byName = (name, path, text) => {
    const n = name.toLowerCase();
    const ext = n.includes('.') ? n.slice(n.lastIndexOf('.') + 1) : '';
    const first = text.slice(0, 200).split('\n')[0];
    if (first.startsWith('#!')) {
      if (/\b(ba|z|da|k)?sh\b/.test(first)) return 'shell';
      if (/python/.test(first)) return 'python';
      if (/node/.test(first)) return 'javascript';
      if (/perl/.test(first)) return 'perl';
      if (/lua/.test(first)) return 'lua';
    }
    if (n === 'dockerfile' || n.startsWith('dockerfile.') || ext === 'dockerfile') return 'dockerfile';
    if (/(^|\/)nginx\//.test(path) || n === 'nginx.conf' || /\/sites-(available|enabled)\//.test(path)) return 'nginx';
    if (['.bashrc', '.profile', '.bash_profile', '.bash_aliases', '.zshrc', '.zprofile', '.bash_logout', 'crontab'].includes(n)) return 'shell';
    const map = {
      sh: 'shell', bash: 'shell', zsh: 'shell', ksh: 'shell', env: 'shell',
      yml: 'yaml', yaml: 'yaml', json: 'json', jsonc: 'json', js: 'javascript', mjs: 'javascript', cjs: 'javascript',
      ts: 'typescript', tsx: 'typescript', jsx: 'javascript', py: 'python', html: 'html', htm: 'html', xml: 'xml', svg: 'xml',
      xaml: 'xml', csproj: 'xml', css: 'css', scss: 'css', md: 'markdown', markdown: 'markdown', sql: 'sql', toml: 'toml',
      ini: 'properties', cfg: 'properties', conf: 'properties', cnf: 'properties', properties: 'properties', service: 'properties',
      timer: 'properties', socket: 'properties', mount: 'properties', network: 'properties', lua: 'lua', go: 'go',
      pl: 'perl', pm: 'perl', diff: 'diff', patch: 'diff',
    };
    return map[ext] || '';
  };

  // ---------- themes ----------
  const light = EditorView.theme({
    '&': { backgroundColor: 'var(--bg)', color: 'var(--fg)' },
    '.cm-gutters': { backgroundColor: 'var(--bg)', color: '#8c959f', border: 'none' },
    '.cm-activeLine': { backgroundColor: 'rgba(0,0,0,0.035)' },
    '.cm-activeLineGutter': { backgroundColor: 'rgba(0,0,0,0.05)' },
  }, { dark: false });
  const darkBg = EditorView.theme({
    '&': { backgroundColor: 'var(--bg)' },
    '.cm-gutters': { backgroundColor: 'var(--bg)', border: 'none' },
  }, { dark: true });
  const fontTheme = (size) => EditorView.theme({ '.cm-scroller': { fontSize: size + 'px' } });

  // ---------- editor ----------
  const status = (state) => {
    const sel = state.selection.main;
    const line = state.doc.lineAt(sel.head);
    const selected = state.selection.ranges.reduce((n, r) => n + (r.to - r.from), 0);
    send({ t: 'status', line: line.number, col: sel.head - line.from + 1, sel: selected, lines: state.doc.lines, lang: langName });
  };

  const updateListener = EditorView.updateListener.of((u) => {
    if (u.docChanged) {
      const d = savedDoc === null || !u.state.doc.eq(savedDoc);
      if (d !== dirty) { dirty = d; send({ t: 'dirty', on: d }); }
    }
    if (u.docChanged || u.selectionSet) status(u.state);
  });

  const save = () => {
    send({ t: 'save', text: view.state.doc.toString() });
    return true;
  };
  const zoom = (d) => {
    fontSize = d === 0 ? 14 : Math.max(8, Math.min(32, fontSize + d));
    view.dispatch({ effects: font.reconfigure(fontTheme(fontSize)) });
    return true;
  };
  let wrapOn = false;
  const toggleWrap = () => {
    wrapOn = !wrapOn;
    view.dispatch({ effects: wrap.reconfigure(wrapOn ? EditorView.lineWrapping : []) });
    send({ t: 'wrap', on: wrapOn });
    return true;
  };

  const appKeys = keymap.of([
    { key: 'Mod-s', run: save, preventDefault: true },
    { key: 'Mod-Shift-w', run: () => { send({ t: 'key', k: 'close' }); return true; } },
    { key: 'Mod-w', run: () => { send({ t: 'key', k: 'close' }); return true; } },
    { key: 'Mod-Tab', run: () => { send({ t: 'key', k: 'next' }); return true; } },
    { key: 'Mod-Shift-Tab', run: () => { send({ t: 'key', k: 'prev' }); return true; } },
    { key: 'Mod-=', run: () => zoom(1) }, { key: 'Mod-+', run: () => zoom(1) }, { key: 'Mod--', run: () => zoom(-1) },
    { key: 'Mod-0', run: () => zoom(0) },
    { key: 'Alt-z', run: toggleWrap },
  ]);

  const view = new EditorView({
    parent: document.getElementById('editor'),
    state: EditorState.create({ doc: '', extensions: [] }),
  });

  const create = (text, language, ro, dark) => {
    langName = language;
    const state = EditorState.create({
      doc: text,
      extensions: [
        appKeys,
        basicSetup,
        keymap.of([indentWithTab]),
        theme.of(dark ? [oneDark, darkBg] : light),
        lang.of(language && languages[language] ? languages[language]() : []),
        readOnly.of(EditorState.readOnly.of(!!ro)),
        wrap.of([]),
        font.of(fontTheme(fontSize)),
        updateListener,
      ],
    });
    view.setState(state);
    savedDoc = state.doc;
    dirty = false;
    status(state);
  };

  let isDark = true;
  document.addEventListener('wheel', (e) => {
    if (!e.ctrlKey) return;
    e.preventDefault();
    zoom(e.deltaY < 0 ? 1 : -1);
  }, { passive: false });

  host && host.addEventListener('message', (ev) => {
    const m = ev.data;
    switch (m.t) {
      case 'load':
        banner.textContent = '';
        create(m.text || '', m.lang || byName(m.name || '', m.path || '', m.text || ''), m.readOnly, isDark);
        if (m.line) {
          const line = view.state.doc.line(Math.min(m.line, view.state.doc.lines));
          view.dispatch({ selection: { anchor: line.from }, scrollIntoView: true });
        }
        view.focus();
        break;
      case 'theme':
        isDark = !!m.dark;
        document.documentElement.style.setProperty('--bg', m.bg || (isDark ? '#1f1f1f' : '#fbfbfb'));
        document.documentElement.style.setProperty('--fg', isDark ? '#e6e6e6' : '#1f2328');
        document.documentElement.style.setProperty('--muted', isDark ? '#9a9a9a' : '#6e7781');
        view.dispatch({ effects: theme.reconfigure(isDark ? [oneDark, darkBg] : light) });
        break;
      case 'requestSave':
        send({ t: 'save', text: view.state.doc.toString(), close: !!m.close });
        break;
      case 'saved':
        savedDoc = view.state.doc;
        if (dirty) { dirty = false; send({ t: 'dirty', on: false }); }
        break;
      case 'banner':
        banner.textContent = m.text || '';
        break;
      case 'focus':
        view.focus();
        break;
      case 'wrap':
        if (!!m.on !== wrapOn) toggleWrap();
        break;
    }
  });

  send({ t: 'ready' });
})();
