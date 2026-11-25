// The terminal page: xterm.js wired to the app through WebView2 messages.
//
// App -> page:  {t:'o', d:<base64 bytes>}   output from the session
//               {t:'theme', theme, fontSize, fontFamily}
//               {t:'focus'}  {t:'paste', d:<text>}
//               {t:'find'}  {t:'findNext'}  {t:'findPrevious'}   the find bar
//               {t:'findDemo', d:<text>}   testing aid: search for that and report
// Page -> app:  {t:'ready', cols, rows}  {t:'size', cols, rows}
//               {t:'i', d:<text>}  {t:'b', d:<binary string>}   keyboard and mouse input
//               {t:'title', d}  {t:'bell'}  {t:'focus'}  {t:'link', d:<url>}
//               {t:'copy', d:<text>}  {t:'paste'}   the clipboard is handled by the app
//               {t:'found', index, count, open}   what a search found (for the log)
'use strict';
(() => {
  const bridge = window.chrome && window.chrome.webview;
  const post = (message) => { if (bridge) bridge.postMessage(message); };

  const term = new Terminal({
    cursorBlink: true,
    scrollback: 10000,
    fontFamily: '"Cascadia Mono", Consolas, "Courier New", monospace',
    fontSize: 14,
    // Lets this page's own code use xterm.js interfaces still marked as
    // proposed: the search addon highlights its matches with one of them.
    // It gives programs in the terminal nothing new.
    allowProposedApi: true,
    // Right click copies or pastes (below), like Windows Terminal.
    rightClickSelectsWord: false,
  });

  const fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  // Links open with Ctrl+click, in the default browser, and only if the app agrees.
  term.loadAddon(new WebLinksAddon.WebLinksAddon((event, uri) => {
    if (event.ctrlKey) post({ t: 'link', d: uri });
  }));

  const element = document.getElementById('terminal');
  term.open(element);
  try {
    const webgl = new WebglAddon.WebglAddon();
    webgl.onContextLoss(() => webgl.dispose());
    term.loadAddon(webgl);
  } catch (error) {
    // No WebGL: the DOM renderer carries on.
  }

  // ----- Size -----

  const fitNow = () => {
    // A pane in a hidden workspace has no size; keep the last good one.
    if (element.clientWidth < 20 || element.clientHeight < 20) return;
    try { fit.fit(); } catch (error) { /* not laid out yet */ }
  };
  let fitQueued = false;
  const fitSoon = () => {
    if (fitQueued) return;
    fitQueued = true;
    requestAnimationFrame(() => {
      fitQueued = false;
      fitNow();
    });
  };
  new ResizeObserver(fitSoon).observe(element);

  // ----- Session I/O -----

  term.onData((d) => post({ t: 'i', d }));
  term.onBinary((d) => post({ t: 'b', d }));
  term.onResize(({ cols, rows }) => post({ t: 'size', cols, rows }));
  term.onTitleChange((d) => post({ t: 'title', d }));
  term.onBell(() => post({ t: 'bell' }));

  const decode = (base64) => {
    const raw = atob(base64);
    const bytes = new Uint8Array(raw.length);
    for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
    return bytes;
  };

  // ----- Focus -----

  window.addEventListener('mousedown', () => post({ t: 'focus' }), true);
  if (term.textarea) term.textarea.addEventListener('focus', () => post({ t: 'focus' }));

  // ----- Clipboard -----

  const copySelection = () => {
    if (!term.hasSelection()) return false;
    post({ t: 'copy', d: term.getSelection() });
    return true;
  };

  term.attachCustomKeyEventHandler((event) => {
    if (event.type !== 'keydown') return true;
    const key = event.key.toLowerCase();
    const copy = (event.ctrlKey && event.shiftKey && !event.altKey && key === 'c')
              || (event.ctrlKey && !event.shiftKey && !event.altKey && key === 'insert');
    const paste = (event.ctrlKey && event.shiftKey && !event.altKey && key === 'v')
               || (event.shiftKey && !event.ctrlKey && !event.altKey && key === 'insert');
    if (copy || paste) {
      // Keep the browser's own paste from running as well.
      event.preventDefault();
      if (copy) copySelection(); else post({ t: 'paste' });
      return false;
    }
    return true;
  });

  element.addEventListener('contextmenu', (event) => {
    event.preventDefault();
    // A program that tracks the mouse gets the click itself.
    if (term.modes.mouseTrackingMode !== 'none') return;
    if (copySelection()) term.clearSelection(); else post({ t: 'paste' });
  });

  // ----- Find -----
  //
  // Searches what is on screen and everything that has scrolled out of view.
  // Enter and Shift+Enter (or F3 and Shift+F3 while the bar is open) step
  // through the matches, Esc closes the bar.

  const search = new SearchAddon.SearchAddon();
  term.loadAddon(search);
  const findBar = document.getElementById('find');
  const findText = document.getElementById('find-text');
  const findCount = document.getElementById('find-count');
  const findOptions = {
    decorations: {
      matchBackground: '#4a3a12',
      matchOverviewRuler: '#4a3a12',
      activeMatchBackground: '#8a5a00',
      activeMatchBorder: '#ffd24d',
      activeMatchColorOverviewRuler: '#ffd24d',
    },
  };
  let lastFound = { index: -1, count: 0 };

  search.onDidChangeResults(({ resultIndex, resultCount }) => {
    lastFound = { index: resultIndex, count: resultCount };
    findBar.classList.toggle('none', resultCount === 0 && findText.value !== '');
    if (findText.value === '') findCount.textContent = '';
    else if (resultCount === 0) findCount.textContent = 'No results';
    // Past the number of matches the addon keeps track of, it stops counting.
    else if (resultIndex < 0) findCount.textContent = `${resultCount}+`;
    else findCount.textContent = `${resultIndex + 1} of ${resultCount}`;
  });

  const findStep = (backwards, incremental) => {
    if (findText.value === '') {
      search.clearDecorations();
      findCount.textContent = '';
      findBar.classList.remove('none');
      lastFound = { index: -1, count: 0 };
      return;
    }
    if (backwards) search.findPrevious(findText.value, findOptions);
    else search.findNext(findText.value, { ...findOptions, incremental });
  };

  const showFind = () => {
    findBar.hidden = false;
    // A word or phrase that is selected is what one most likely wants to find.
    const selected = term.getSelection();
    if (selected && !selected.includes('\n') && selected.length <= 200) findText.value = selected;
    findText.focus();
    findText.select();
    findStep(false, true);
  };

  const hideFind = () => {
    if (findBar.hidden) return;
    findBar.hidden = true;
    search.clearDecorations();
    term.focus();
  };

  findText.addEventListener('input', () => findStep(false, true));
  findText.addEventListener('keydown', (event) => {
    if (event.key === 'Enter') {
      event.preventDefault();
      findStep(event.shiftKey, false);
    } else if (event.key === 'Escape') {
      event.preventDefault();
      hideFind();
    }
  });
  document.getElementById('find-next').addEventListener('click', () => findStep(false, false));
  document.getElementById('find-previous').addEventListener('click', () => findStep(true, false));
  document.getElementById('find-close').addEventListener('click', hideFind);
  // F3 belongs to the program in the terminal, except while the bar is open.
  window.addEventListener('keydown', (event) => {
    if (findBar.hidden) return;
    if (event.key === 'F3') {
      event.preventDefault();
      event.stopPropagation();
      findStep(event.shiftKey, false);
    } else if (event.key === 'Escape' && document.activeElement !== findText) {
      event.preventDefault();
      event.stopPropagation();
      hideFind();
    }
  }, true);

  // ----- Messages from the app -----

  const applyTheme = (message) => {
    term.options.theme = message.theme;
    term.options.fontSize = message.fontSize;
    term.options.fontFamily = message.fontFamily;
    document.body.style.backgroundColor = message.theme.background;
    fitSoon();
  };

  if (bridge) {
    bridge.addEventListener('message', (event) => {
      const message = event.data;
      switch (message.t) {
        case 'o': term.write(decode(message.d)); break;
        case 'theme': applyTheme(message); break;
        case 'focus': term.focus(); break;
        case 'paste': term.paste(message.d); break;
        case 'find': showFind(); break;
        case 'findNext':
        case 'findPrevious':
          if (findBar.hidden) showFind(); else findStep(message.t === 'findPrevious', false);
          break;
        case 'findDemo':
          // Testing aid: what the bar would find, told to the app for its log.
          findBar.hidden = false;
          findText.value = message.d;
          findStep(false, true);
          setTimeout(() => post({ t: 'found', index: lastFound.index, count: lastFound.count, open: !findBar.hidden }), 300);
          break;
      }
    });
  }

  const ready = () => {
    fitNow();
    post({ t: 'ready', cols: term.cols, rows: term.rows });
  };
  if (document.fonts && document.fonts.ready) document.fonts.ready.then(ready, ready); else ready();
})();
