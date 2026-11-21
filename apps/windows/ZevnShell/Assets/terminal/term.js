// Terminal Zevn Shell (Windows): xterm.js + jembatan WebView2 ke ConPTY di C#.
// Autocomplete diporting dari Autocomplete.swift (versi macOS).
(() => {
  const host = window.chrome.webview;
  const post = (o) => host.postMessage(o);
  const el = document.getElementById('term');
  const acEl = document.getElementById('ac');

  const term = new Terminal({
    fontFamily: '"Cascadia Mono", "Cascadia Code", Consolas, monospace',
    fontSize: 13.5, lineHeight: 1.12, cursorBlink: true, scrollback: 10000,
    allowProposedApi: true, macOptionIsMeta: false, rightClickSelectsWord: false,
  });
  const fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  term.open(el);

  let lastSize = '';
  const refit = () => {
    try { fit.fit(); } catch (_) {}
    const s = term.cols + 'x' + term.rows;
    if (s !== lastSize) { lastSize = s; post({ t: 'resize', cols: term.cols, rows: term.rows }); }
  };
  new ResizeObserver(refit).observe(el);

  // ---- Autocomplete ----
  const ac = {
    enabled: true, hostId: '', history: [], snippets: [], common: [],
    buffer: '', tracking: true, dismissed: false, items: [], index: 0, timer: 0,
  };

  const isAlt = () => term.buffer.active.type === 'alternate';

  // Ketikan benar-benar tampil di layar? Prompt password tidak meng-echo → tidak ada saran & tidak disimpan.
  function isEchoed(typed) {
    if (!typed) return false;
    const b = term.buffer.active;
    const line = b.getLine(b.baseY + b.cursorY);
    if (!line) return false;
    const before = line.translateToString(false, 0, b.cursorX);
    const n = Math.max(1, Math.min(typed.length, b.cursorX));
    return before.endsWith(typed.slice(-n));
  }

  function consume(data) {
    let text = data;
    // Balasan otomatis terminal (laporan posisi kursor, atribut perangkat, fokus) bukan ketikan
    if (/^(\x1b\[\??[\d;]*[Rcn]|\x1b\[[IO]|\x1b\][^\x07]*\x07)+$/.test(text)) return;
    if (text.includes('\x1b[200~') || text.includes('\x1b[201~')) {
      text = text.replaceAll('\x1b[200~', '').replaceAll('\x1b[201~', '');
    } else if (text.startsWith('\x1b')) {
      // Panah, Home/End, dsb.: isi baris tidak bisa dipastikan lagi sampai Enter
      ac.tracking = false; hide(); return;
    }
    for (const ch of text) {
      const c = ch.codePointAt(0);
      if (c === 0x0d || c === 0x0a) commit();
      else if (c === 0x7f || c === 0x08) ac.buffer = Array.from(ac.buffer).slice(0, -1).join('');
      else if (c === 0x03 || c === 0x15 || c === 0x04) reset();
      else if (c === 0x17) { ac.buffer = ac.buffer.replace(/\s+$/, '').replace(/\S+$/, ''); }
      else if (c === 0x09) ac.tracking = false;
      else if (c < 0x20) ac.tracking = false;
      else ac.buffer += ch;
    }
    ac.dismissed = false;
    clearTimeout(ac.timer);
    ac.timer = setTimeout(refresh, 70);
  }

  function reset() { ac.buffer = ''; ac.tracking = true; ac.dismissed = false; hide(); }

  function commit() {
    if (ac.tracking && isEchoed(ac.buffer) && !isAlt()) {
      const cmd = ac.buffer.trim();
      if (cmd.length >= 2 && !ac.buffer.startsWith(' ')) {
        ac.history = [cmd, ...ac.history.filter((h) => h !== cmd)].slice(0, 500);
        post({ t: 'hist', cmd: ac.buffer });
      }
    }
    reset();
  }

  function suggestions(typed) {
    const pool = [
      ...ac.history.map((t) => ({ text: t, kind: 'history' })),
      ...ac.snippets.map((s) => ({ text: s.command, kind: 'snippet', label: s.label })),
      ...ac.common.map((t) => ({ text: t, kind: 'command' })),
    ];
    const seen = new Set(), out = [];
    const take = (s) => { if (out.length < 6 && s.text !== typed && !seen.has(s.text)) { seen.add(s.text); out.push(s); } };
    const low = typed.toLowerCase();
    pool.filter((s) => s.text.startsWith(typed)).forEach(take);
    pool.filter((s) => s.text.toLowerCase().startsWith(low)).forEach(take);
    if (typed.length >= 3) {
      pool.filter((s) => s.text.toLowerCase().includes(low)).forEach(take);
      pool.filter((s) => s.kind === 'snippet' && (s.label || '').toLowerCase().includes(low)).forEach(take);
    }
    return out;
  }

  function refresh() {
    if (!ac.enabled || !ac.tracking || ac.dismissed || !ac.buffer.trim() || isAlt() || !isEchoed(ac.buffer)) { hide(); return; }
    const next = suggestions(ac.buffer);
    if (next.map((s) => s.kind + s.text).join('\n') !== ac.items.map((s) => s.kind + s.text).join('\n')) ac.index = 0;
    ac.items = next;
    render();
  }

  function hide() {
    clearTimeout(ac.timer);
    if (ac.items.length) { ac.items = []; render(); }
  }

  const esc = (s) => s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const kindIcon = { history: '', snippet: '', command: '' };
  const kindColor = { history: '#5B9CF5', snippet: '#9D7CF2', command: '#3FD0B4' };

  function render() {
    if (!ac.items.length) { acEl.style.display = 'none'; return; }
    const typed = ac.buffer;
    acEl.innerHTML = ac.items.map((s, i) => {
      const match = s.text.startsWith(typed) ? `<b>${esc(typed)}</b>${esc(s.text.slice(typed.length))}` : esc(s.text);
      const label = s.kind === 'snippet' ? (s.label || 'Snippet') : s.kind === 'history' ? 'Riwayat' : '';
      return `<div class="row${i === ac.index ? ' sel' : ''}" data-i="${i}">
        <span class="ico" style="color:${kindColor[s.kind]};background:${kindColor[s.kind]}26">${kindIcon[s.kind]}</span>
        <span class="txt">${match}</span><span class="lbl">${esc(label)}</span></div>`;
    }).join('') + `<div class="foot"><span><kbd>Tab</kbd>terima</span><span><kbd>↑↓</kbd>pilih</span><span><kbd>Esc</kbd>tutup</span></div>`;
    acEl.style.display = 'block';
    // Posisi di bawah kursor; naik ke atas jika mepet bawah
    const screen = el.querySelector('.xterm-screen');
    const cw = screen.clientWidth / term.cols, ch = screen.clientHeight / term.rows;
    const b = term.buffer.active;
    const pane = document.body.getBoundingClientRect();
    const h = acEl.offsetHeight, w = 440;
    const x = Math.min(Math.max(10, 10 + b.cursorX * cw - 14), Math.max(10, pane.width - w - 12));
    const below = 6 + (b.cursorY + 1) * ch + 4;
    const y = below + h > pane.height - 8 ? Math.max(4, 6 + b.cursorY * ch - h - 4) : below;
    acEl.style.left = x + 'px';
    acEl.style.top = y + 'px';
  }

  acEl.addEventListener('mousedown', (e) => {
    const row = e.target.closest('.row');
    if (row) { e.preventDefault(); accept(+row.dataset.i); }
  });
  acEl.addEventListener('mousemove', (e) => {
    const row = e.target.closest('.row');
    if (row && +row.dataset.i !== ac.index) { ac.index = +row.dataset.i; render(); }
  });

  function accept(i) {
    const s = ac.items[i];
    if (!s) return;
    const typed = ac.buffer;
    hide();
    // Ctrl-U kosongkan baris, lalu ketik saran lengkap (jika bukan lanjutan ketikan)
    term.input(s.text.startsWith(typed) ? s.text.slice(typed.length) : '\x15' + s.text, true);
    term.focus();
  }

  // ---- Keyboard ----
  term.attachCustomKeyEventHandler((e) => {
    if (e.type !== 'keydown') return true;
    const mod = e.ctrlKey || e.altKey || e.metaKey;
    if (ac.items.length && !mod && !e.shiftKey) {
      if (e.key === 'Tab' || e.key === 'ArrowRight') { e.preventDefault(); accept(ac.index); return false; }
      if (e.key === 'ArrowDown') { e.preventDefault(); ac.index = (ac.index + 1) % ac.items.length; render(); return false; }
      if (e.key === 'ArrowUp') { e.preventDefault(); ac.index = (ac.index - 1 + ac.items.length) % ac.items.length; render(); return false; }
      if (e.key === 'Escape') { e.preventDefault(); ac.dismissed = true; hide(); return false; }
    }
    // Salin / tempel ala Windows Terminal
    if (e.ctrlKey && !e.altKey && (e.key === 'c' || e.key === 'C') && (e.shiftKey || term.hasSelection())) {
      if (term.hasSelection()) { post({ t: 'copy', s: term.getSelection() }); term.clearSelection(); }
      e.preventDefault(); return false;
    }
    if (e.ctrlKey && !e.altKey && (e.key === 'v' || e.key === 'V')) { e.preventDefault(); post({ t: 'paste' }); return false; }
    if (e.ctrlKey && e.key === 'Insert') { if (term.hasSelection()) post({ t: 'copy', s: term.getSelection() }); return false; }
    if (e.shiftKey && e.key === 'Insert') { post({ t: 'paste' }); return false; }
    // Pintasan app (diteruskan ke C#)
    if (e.ctrlKey && e.key === 'Tab') { e.preventDefault(); post({ t: 'key', k: e.shiftKey ? 'prevTab' : 'nextTab' }); return false; }
    if (e.ctrlKey && e.shiftKey && /^Digit[0-9]$/.test(e.code)) { e.preventDefault(); post({ t: 'key', k: 'tab' + e.code.slice(5) }); return false; }
    if (e.ctrlKey && e.shiftKey && (e.key === 'W' || e.key === 'w')) { e.preventDefault(); post({ t: 'key', k: 'close' }); return false; }
    if (e.ctrlKey && e.shiftKey && (e.key === 'L' || e.key === 'l')) { e.preventDefault(); post({ t: 'key', k: 'lock' }); return false; }
    if (e.ctrlKey && (e.key === '=' || e.key === '+')) { e.preventDefault(); zoom(1); return false; }
    if (e.ctrlKey && e.key === '-') { e.preventDefault(); zoom(-1); return false; }
    if (e.ctrlKey && e.key === '0') { e.preventDefault(); zoom(0); return false; }
    return true;
  });

  function zoom(d) {
    term.options.fontSize = d === 0 ? 13.5 : Math.min(28, Math.max(9, term.options.fontSize + d));
    refit();
  }

  term.onData((d) => { post({ t: 'in', d }); consume(d); });
  term.onTitleChange((s) => post({ t: 'title', s }));
  term.onScroll(() => { if (ac.items.length) render(); });

  el.addEventListener('contextmenu', (e) => {
    e.preventDefault();
    post({ t: 'menu', x: e.clientX, y: e.clientY, sel: term.hasSelection() });
  });

  function applyTheme(m) {
    term.options.theme = m.theme;
    const r = document.documentElement.style;
    r.setProperty('--bg', m.theme.background);
    r.setProperty('--fg', m.theme.foreground);
    r.setProperty('--accent', m.theme.cursor);
    r.setProperty('--panel', m.light ? 'rgba(255,255,255,.96)' : 'rgba(22,26,34,.96)');
    r.setProperty('--stroke', m.light ? 'rgba(0,0,0,.09)' : 'rgba(255,255,255,.09)');
    r.setProperty('--sel', m.light ? 'rgba(0,0,0,.07)' : 'rgba(255,255,255,.08)');
  }

  host.addEventListener('message', (e) => {
    const m = e.data;
    switch (m.t) {
      case 'out': term.write(m.d); if (ac.items.length) setTimeout(render, 0); break;
      case 'theme': applyTheme(m); break;
      case 'cfg':
        ac.enabled = m.enabled; ac.hostId = m.hostId; ac.history = m.history || []; ac.snippets = m.snippets || [];
        if (m.common) ac.common = m.common;
        if (!ac.enabled) hide();
        break;
      case 'input': term.input(m.d, true); break;
      case 'paste': if (m.d) term.paste(m.d); term.focus(); break;
      case 'selectAll': term.selectAll(); break;
      case 'copySel': if (term.hasSelection()) post({ t: 'copy', s: term.getSelection() }); break;
      case 'reset': term.reset(); reset(); break;
      case 'focus': term.focus(); refit(); break;
      case 'fit': refit(); break;
      case 'acceptAc': if (ac.items.length) accept(ac.index); break;
    }
  });

  requestAnimationFrame(() => {
    refit();
    post({ t: 'ready', cols: term.cols, rows: term.rows });
    term.focus();
  });
})();
