(() => {
  const geometry = globalThis.MoriDragGeometry;
  const P = globalThis.MoriPreferences;
  let preferences = P.normalize(), preferencesReady = false, topPageUrl = location.href;
  const darkMedia = matchMedia('(prefers-color-scheme: dark)');
  const t = text => MoriCollectorText(text, P.language(preferences));
  let active = null;
  let hideTimer;
  let contextPoint;
  let contextImage;
  let contextTime = 0;
  const notices = new Map();
  let noticeHost, noticeRoot, noticeList;

  function removeNotice(session, cancel = false) {
    const notice = notices.get(session);
    if (!notice) return;
    clearTimeout(notice.timer);
    notice.row.remove(); notices.delete(session);
    if (!notices.size) {noticeHost.remove(); noticeHost = null;}
    if (cancel) notice.cancel?.();
  }

  function notify(session, message, kind = 'success', detail = '', action = null, cancel = null) {
    removeNotice(session);
    // Bound DOM/memory even during a long burst of collections.
    if (notices.size >= 3) removeNotice(notices.keys().next().value, true);
    if (!noticeHost) {
      noticeHost = document.createElement('div');
      noticeHost.style.cssText = 'position:fixed;inset:0;z-index:2147483647;pointer-events:none';
      noticeRoot = noticeHost.attachShadow({mode:'closed'});
      const style = document.createElement('style');
      style.textContent = `:host{all:initial;font:12px/1.5 "Segoe UI","Microsoft YaHei",system-ui,sans-serif}.notices{position:absolute;right:20px;bottom:20px;max-width:calc(100% - 40px);display:flex;flex-direction:column;align-items:flex-end;gap:6px;pointer-events:none}.toast{box-sizing:border-box;display:flex;align-items:center;gap:7px;max-width:320px;padding:7px 10px;border-radius:7px;background:#f7f8f5f5;color:#3c5041;box-shadow:0 2px 10px #0001;overflow-wrap:anywhere}.toast[data-theme="dark"]{background:#252b27f5;color:#d8e4da}.toast[data-kind="error"]{color:#994c40}.toast[data-theme="dark"][data-kind="error"]{color:#f0b3a7}.toast-text{min-width:0}.toast-action,.toast-close{font:inherit;pointer-events:auto;cursor:pointer;border:0;background:none;color:inherit;padding:0}.toast-action{text-align:left;text-decoration:underline;text-underline-offset:3px}.toast-close{flex:none;width:18px;height:20px;opacity:.65}.toast-action:focus-visible,.toast-close:focus-visible{outline:2px solid currentColor;outline-offset:2px}`;
      noticeList = element('div', 'notices');
      noticeRoot.append(style, noticeList); document.documentElement.append(noticeHost);
    }
    const row = element('div', 'toast');
    row.dataset.kind = kind; row.dataset.theme = P.theme(preferences, darkMedia.matches);
    row.setAttribute('role', kind === 'error' ? 'alert' : 'status');
    row.setAttribute('aria-live', kind === 'error' ? 'assertive' : 'polite');
    row.title = [session.item?.name, detail].filter(Boolean).join(' · ').slice(0, 1000);
    const label = element(action ? 'button' : 'span', action ? 'toast-action' : 'toast-text', message);
    if (action) {label.type = 'button'; label.onclick = action;}
    const close = element('button', 'toast-close', '×'); close.type = 'button';
    close.setAttribute('aria-label', t('关闭提示')); close.onclick = () => removeNotice(session, true);
    row.append(label, close); noticeList.append(row);
    const notice = {row, cancel}; notices.set(session, notice);
    notice.timer = setTimeout(() => removeNotice(session, true), kind === 'choice' ? 300000 : kind === 'saving' ? 30000 : kind === 'error' ? 10000 : 1800);
  }

  function detachCollector(session) {
    cancelFolderTimer(session);
    session.host.remove();
    if (active === session) {clearTimeout(hideTimer); active = null;}
  }

  function beginBackground(session) {
    session.background = true;
    setPhase(session, 'saving');
    detachCollector(session);
    // Fast saves only show the result; slow transfers get a small honest status.
    session.progressTimer = setTimeout(() => notify(session, '正在收藏…', 'saving'), 500);
  }

  function backgroundResult(session, response) {
    clearTimeout(session.progressTimer); removeNotice(session);
    if (response.ok && response.choiceRequired && response.choiceToken) {
      const open = () => {
        if (active && active !== session) return; // Never replace a newer drag/editor.
        removeNotice(session);
        document.documentElement.append(session.host); active = session;
        session.panel.dataset.theme = P.theme(preferences, darkMedia.matches);
        showDuplicate(session, response);
      };
      if (!active) {open(); return;}
      setPhase(session, 'duplicate'); session.choiceToken = response.choiceToken;
      notify(session, '发现重复图片 · 选择处理', 'choice', session.item?.name, open, () => {
        const token = session.choiceToken; session.choiceToken = null; releaseChoice(session);
        if (token) runtime({action:'discard', choiceToken:token}, () => {});
      });
      holdChoice(session);
      return;
    }
    releaseChoice(session); detachCollector(session);
    session.phase = response.ok ? 'success' : 'error';
    const message = response.ok ? (response.keptCopy ? '已另存一份' : response.duplicate ? '已使用已有图片' : '已收藏到 Mori') : '收藏失败：' + (response.error || t(session.item?.kind === 'video' ? '视频未收藏，请稍后重试。' : '请重试，或截图后在 Mori 按 Ctrl+V 收藏。'));
    notify(session, message, response.ok ? 'success' : 'error', response.ok ? (session.folderName ? `已保存到「${session.folderName}」` : '已保存到本机图库 · 未分类') : response.diagnostic);
  }

  function imageAtContext(event) {
    const p = point(event);
    const seen = new Set();
    const path = event.composedPath().filter(node => node instanceof Element);
    const hits = [...path, ...document.elementsFromPoint(p.x, p.y)].filter(node => !['HTML', 'BODY'].includes(node.tagName));
    const visibleAt = node => {
      const rect = node.getBoundingClientRect();
      if (rect.width <= 0 || rect.height <= 0 || p.x < rect.left || p.x > rect.right || p.y < rect.top || p.y > rect.bottom) return false;
      for (let parent = node; parent instanceof Element; parent = parent.parentElement) {
        const style = getComputedStyle(parent);
        if (style.display === 'none' || ['hidden', 'collapse'].includes(style.visibility) || Number(style.opacity) === 0) return false;
      }
      return true;
    };
    const item = (value, name, kind = 'image') => {
      if (typeof value !== 'string' || !value.trim()) return null;
      try {
        const url = new URL(value, document.baseURI);
        if (!['http:', 'https:', 'blob:'].includes(url.protocol) && !(url.protocol === 'data:' && /^data:(?:image|video)\//i.test(url.href))) return null;
        if (url.username || url.password) return null;
        return {url: url.href, kind, name: (name || document.title || '网页收藏').slice(0, 200)};
      } catch {return null;}
    };
    // Prefer actual image pixels at this point. Bounded descendant checks also
    // cover pointer-events:none images underneath a stretched card link.
    for (const node of hits.slice(0, 16)) {
      const images = node.matches('img,video') ? [node] : Array.from(node.querySelectorAll('img,video')).slice(0, 80);
      for (const image of images) {
        if (seen.has(image)) continue;
        seen.add(image);
        if (visibleAt(image)) {
          const video = image instanceof HTMLVideoElement;
          // A poster is not the animation. preload=none videos often have no
          // currentSrc yet, so use the declared source without forcing playback.
          const src = image.currentSrc || image.getAttribute('src') || (video ? image.querySelector('source[src]')?.getAttribute('src') : '');
          const found = item(src, image.alt || image.getAttribute('aria-label') || image.title, video ? 'video' : 'image');
          if (found) {
            if (video && found.url.startsWith('blob:')) found.mediaPage = MoriMediaSource.identify(image);
            return found;
          }
        }
      }
    }
    // CSS background URLs are already resolved by the browser. Never use the
    // clicked link's href or choose an unrelated nearby image as a fallback.
    for (const node of hits.slice(0, 16)) {
      if (!visibleAt(node)) continue;
      const match = getComputedStyle(node).backgroundImage.match(/url\(\s*(?:"((?:\\.|[^"])*)"|'((?:\\.|[^'])*)'|([^)]*))\s*\)/i);
      if (match) {
        const found = item((match[1] ?? match[2] ?? match[3]).trim().replace(/\\([\\"'])/g, '$1'));
        if (found) return found;
      }
    }
    return null;
  }

  const css = `
    :host{all:initial;font:14px/1.5 "Segoe UI","Microsoft YaHei",system-ui,sans-serif}
    *{box-sizing:border-box}button{font:inherit}
    .panel{--bg:#f8f8f5;--surface:#eef1eb;--text:#28362d;--muted:#69736b;--line:#d4dcd2;--accent:#44724d;--hover:#e4eedf;--error:#9b463a;position:absolute;display:flex;flex-direction:column;gap:12px;padding:18px;background:var(--bg);color:var(--text);border:1px solid var(--line);border-radius:20px;box-shadow:0 14px 52px #0003;pointer-events:auto;overflow:hidden;color-scheme:light}
    .panel[data-theme="dark"]{--bg:#242925;--surface:#2e352f;--text:#e5ebe2;--muted:#a5b2a6;--line:#465047;--accent:#a4c797;--hover:#3d4d3a;--error:#f2ab9f;color-scheme:dark}
    .panel[data-phase="editing"] .quick,.panel[data-phase="editing"] .note,.panel[data-phase="editing"] .foot{display:none}.editor{display:flex;flex-direction:column;gap:10px;min-height:0;flex:1;overflow:auto}.editor label{display:flex;flex-direction:column;gap:4px;font-size:12px;color:var(--muted)}.editor input,.editor select{width:100%;font:inherit;font-size:13px;min-height:34px;padding:6px 8px;border:1px solid var(--line);border-radius:6px;background:var(--surface);color:var(--text)}.editor input:focus-visible,.editor select:focus-visible{outline:2px solid var(--accent)}.editor-error{color:var(--error);font-size:11px;overflow-wrap:anywhere}.editor-actions{display:flex;justify-content:flex-end;gap:8px}.editor-actions button{padding:7px 14px}
    .collect-icon{display:none;place-items:center;color:var(--accent);pointer-events:none}
    [data-phase="saving"] .collect-icon,[data-phase="success"] .collect-icon,[data-connection="error"] .collect-icon{display:grid}
    .glyph{display:none;width:28px;height:28px;fill:none;stroke:currentColor;stroke-width:1.7;stroke-linecap:round;stroke-linejoin:round}
    [data-phase="saving"] .glyph-saving,[data-phase="success"] .glyph-success{display:block}
    [data-connection="error"] .collect-icon{color:var(--error)}[data-connection="error"] .glyph-warning{display:block}
    @keyframes mori-spin{to{transform:rotate(360deg)}}
    @media(prefers-reduced-motion:no-preference){.glyph-saving{animation:mori-spin 1s linear infinite}}
    .head{display:flex;align-items:center;gap:8px;min-height:22px;flex:none}.brand{font-size:15px;font-weight:650;letter-spacing:.8px}.tag{color:var(--muted);font-size:11px;margin-left:auto}.close{border:0;background:transparent;color:var(--muted);font-size:19px;line-height:22px;width:24px;height:24px;padding:0;cursor:pointer;border-radius:6px}.close:hover{background:var(--surface);color:var(--text)}.close:focus-visible{outline:2px solid var(--accent)}
    .quick{flex:1;min-height:84px;display:flex;flex-direction:column;justify-content:center;align-items:center;text-align:center;gap:7px;padding:14px;border:1.5px dashed var(--line);border-radius:12px;background:var(--surface)}.title{font-size:21px;line-height:1.4;font-weight:650;margin:0}.hint{font-size:12px;color:var(--muted);line-height:1.6;max-width:100%;overflow-wrap:anywhere}.source{font-size:11px;color:var(--muted);max-width:100%;white-space:nowrap;text-overflow:ellipsis;overflow:hidden}.is-over{border-color:var(--accent);box-shadow:0 0 0 3px color-mix(in srgb,var(--accent) 22%,transparent),0 14px 52px #0003}.is-over .quick{border-color:var(--accent);border-style:solid;background:var(--hover)}
    .note{font-size:11px;line-height:1.5;color:var(--muted);margin:0;max-height:48px;overflow:auto;overflow-wrap:anywhere}.foot{display:flex;align-items:center;justify-content:space-between;gap:8px;font-size:10px;color:var(--muted);flex:none}.foot-info{flex:1}.hidden{display:none!important}
    .category-toggle{display:grid;place-items:center;flex:none;width:28px;height:28px;padding:4px;border:0;border-radius:7px;background:transparent;color:var(--muted);cursor:pointer}.category-toggle svg{width:20px;height:20px;fill:none;stroke:currentColor;stroke-width:1.6;stroke-linecap:round;stroke-linejoin:round;pointer-events:none}.category-toggle:hover,.category-toggle[aria-expanded="true"]{color:var(--accent);background:var(--surface)}.category-toggle:focus-visible,.folder:focus-visible{outline:2px solid var(--accent);outline-offset:-2px}
    .panel:not([data-phase="dragging"]) .category-toggle{display:none}
    .folder-unfiled{display:none;font:inherit;font-size:12px;padding:4px 7px;border:0;border-radius:5px;background:var(--surface);color:var(--text);cursor:pointer}.folder-unfiled:focus-visible{outline:2px solid var(--accent)}.panel[data-phase="choosing"] .folder-unfiled{display:block}.panel[data-phase="choosing"] .foot-info{display:none}
    .category-label:focus{outline:none}.panel[data-phase="choosing"] .note{display:none}
    .category-menu{position:absolute;left:18px;right:18px;top:54px;bottom:58px;display:flex;flex-direction:column;padding:9px;gap:6px;border:1px solid var(--line);border-radius:10px;background:var(--surface)}.categories-open .quick>*{visibility:hidden}.category-label{font-size:11px;color:var(--muted);padding:0 6px;flex:none}.folder-list{min-height:0;overflow:auto;overscroll-behavior:contain;scrollbar-width:thin;scrollbar-color:var(--line) transparent}.folder{display:block;width:100%;min-height:32px;padding:6px 8px;border:0;border-radius:6px;background:transparent;color:var(--text);font-size:12px;line-height:20px;text-align:left;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;cursor:pointer}.folder:hover,.folder.hover{background:var(--hover);color:var(--accent)}.folder-status{padding:8px;font-size:12px;color:var(--muted)}
    .panel[data-phase="saving"] .quick{border-style:solid}.panel[data-phase="success"] .quick{border-color:var(--accent);background:var(--hover)}.panel[data-phase="error"] .quick{border-color:var(--error)}.panel[data-phase="error"] .title{color:var(--error);font-size:20px}.panel[data-phase="error"] .hint{text-align:left}.diagnostic{font-size:10px;line-height:1.5;white-space:pre-wrap;overflow-wrap:anywhere;color:var(--muted);max-height:64px;overflow:auto}
    .panel[data-phase="duplicate"] .quick{flex:none;min-height:0;padding:0;border:0;background:none;align-items:flex-start;text-align:left;gap:3px}.panel[data-phase="duplicate"] .title{font-size:19px}.comparison{display:grid;grid-template-columns:1fr 1fr;gap:10px;min-height:0;flex:1;overflow:auto}.preview{min-width:0;display:flex;flex-direction:column;gap:5px;background:var(--surface);padding:9px;border-radius:10px}.preview-label{font-size:10px;color:var(--muted)}.preview-image{width:100%;min-height:40px;height:104px;object-fit:contain;background:var(--bg);border-radius:5px}.preview-name{font-size:12px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.preview-meta{font-size:10px;color:var(--muted)}.preview-empty{display:flex;align-items:center;justify-content:center;font-size:11px;color:var(--muted)}.actions{display:grid;grid-template-columns:1fr 1fr 1fr;gap:8px;flex:none}.choice{font-size:12px;border:1px solid var(--line);border-radius:8px;padding:8px 3px;background:var(--surface);color:var(--text);cursor:pointer}.choice.primary{background:var(--accent);color:var(--bg);border-color:var(--accent)}.choice:hover{filter:brightness(.95)}.choice:focus-visible{outline:2px solid var(--accent);outline-offset:2px}
    @media(max-height:480px){.preview-image{height:54px;flex:none}.preview{padding:7px;gap:4px}}
    @media(max-height:360px){.panel{padding:12px;gap:8px}.quick{padding:8px;min-height:66px}.title{font-size:18px}.source{display:none}.category-menu{left:12px;right:12px;top:44px;bottom:52px}}
    @media(prefers-reduced-motion:no-preference){.quick{transition:background .12s,border-color .12s}}
  `;

  const viewport = () => ({width: innerWidth, height: innerHeight});
  const point = event => ({x: event.clientX, y: event.clientY});

  function element(tag, className, text) {
    const node = document.createElement(tag);
    node.className = className;
    if (text !== undefined) node.textContent = t(text);
    return node;
  }

  function dismiss() {
    clearTimeout(hideTimer);
    if (active) {
      cancelFolderTimer(active);
      if (active.choiceToken) runtime({action: 'discard', choiceToken: active.choiceToken}, () => {});
      releaseChoice(active);
      active.host.remove();
    }
    active = null;
  }

  function position(session, rect) {
    session.rect = rect;
    Object.assign(session.panel.style, {left: `${rect.x}px`, top: `${rect.y}px`, width: `${rect.width}px`, height: `${rect.height}px`});
  }

  function panelSize(phase) {
    if (phase === 'duplicate') return {width: 408, height: 440};
    if (phase === 'editing') return {width: 408, height: 388};
    if (preferences.collector === 'bottom') return {width: 560, height: 260};
    return {width: 408, height: 300};
  }

  function fitted(session, phase = session.phase) {
    const size = panelSize(phase), view = viewport();
    const rect = geometry.fit(session.rect, view, size);
    return preferences.collector === 'bottom' ? {...rect, x: Math.max(12, (view.width - rect.width) / 2), y: Math.max(12, view.height - rect.height - 12)} : rect;
  }

  function applyPreferences(value) {
    preferences = P.normalize(value);
    for (const {row} of notices.values()) row.dataset.theme = P.theme(preferences, darkMedia.matches);
    if (!active) return;
    if (['dragging', 'choosing'].includes(active.phase) && P.mode(preferences, window === window.top ? location.href : topPageUrl) !== 'enabled') {dismiss(); return;}
    active.panel.dataset.theme = P.theme(preferences, darkMedia.matches);
    position(active, fitted(active));
  }
  runtime({action: 'preferences'}, response => {
    if (!response.ok) return;
    topPageUrl = response.pageUrl || location.href;
    applyPreferences(response.preferences); preferencesReady = true;
  });
  chrome.storage.onChanged.addListener((changes, area) => {
    if (area === 'local' && changes[P.key]) applyPreferences(changes[P.key].newValue);
  });
  darkMedia.addEventListener('change', () => {if (active) active.panel.dataset.theme = P.theme(preferences, darkMedia.matches);for (const {row} of notices.values()) row.dataset.theme = P.theme(preferences, darkMedia.matches);});

  function setPhase(session, phase) {
    session.phase = phase;
    session.panel.dataset.phase = phase;
    setFoldersOpen(session, false);
    delete session.panel.dataset.connection;
    position(session, fitted(session, phase));
  }

  function collectionIcon() {
    const icon = element('div', 'collect-icon');
    icon.setAttribute('aria-hidden', 'true');
    for (const [name, path] of [
      ['saving', 'M20 12a8 8 0 1 1-8-8'],
      ['success', 'M5 12.5l4.5 4.5L19 7.5'],
      ['warning', 'M12 8v5 M12 16h.01 M10.3 4.5 2.6 18a1.5 1.5 0 0 0 1.3 2.2h16.2a1.5 1.5 0 0 0 1.3-2.2L13.7 4.5a2 2 0 0 0-3.4 0Z']
    ]) {
      const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
      svg.setAttribute('viewBox', '0 0 24 24');
      svg.setAttribute('class', 'glyph glyph-' + name);
      const shape = document.createElementNS('http://www.w3.org/2000/svg', 'path');
      shape.setAttribute('d', path);
      svg.append(shape);
      icon.append(svg);
    }
    return icon;
  }

  function mount(item, start) {
    dismiss();
    const host = document.createElement('div');
    host.style.cssText = 'position:fixed;inset:0;z-index:2147483647;pointer-events:none';
    const root = host.attachShadow({mode: 'closed'});
    const style = document.createElement('style');
    style.textContent = css;
    const panel = element('section', 'panel');
    panel.setAttribute('aria-label', t('Mori 网页收藏'));
    panel.dataset.theme = P.theme(preferences, darkMedia.matches);
    panel.dataset.phase = 'dragging';
    const head = element('div', 'head');
    const tag = element('span', 'tag', '保存到本机');
    const close = element('button', 'close', '×');
    close.type = 'button';
    close.setAttribute('aria-label', t('关闭收藏浮层'));
    close.addEventListener('click', dismiss);
    head.append(element('span', 'brand', 'Mori'), tag, close);
    const quick = element('div', 'quick');
    const title = element('div', 'title', '松手，快速收藏');
    title.setAttribute('role', 'status');
    title.setAttribute('aria-live', 'polite');
    const hint = element('div', 'hint', '拖到这里松手 · 保存到未分类');
    const source = element('div', 'source');source.textContent = item?.name || t('网页图片');
    const diagnostic = element('div', 'diagnostic hidden');
    quick.append(collectionIcon(), title, hint, source, diagnostic);
    const note = element('p', 'note', '正在连接 Mori…');
    const foot = element('div', 'foot');
    const footerAction = element('span', '', 'Esc 取消');
    const categoryToggle = element('button', 'category-toggle');
    categoryToggle.type = 'button';
    categoryToggle.title = t('选择分类 · 拖到这里稍停即可展开');
    categoryToggle.setAttribute('aria-label', t('选择分类'));
    categoryToggle.setAttribute('aria-expanded', 'false');
    categoryToggle.setAttribute('aria-controls', 'mori-category-menu');
    const folderIcon = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    folderIcon.setAttribute('viewBox', '0 0 24 24');
    folderIcon.setAttribute('aria-hidden', 'true');
    const folderPath = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    folderPath.setAttribute('d', 'M3 7V5a1 1 0 0 1 1-1h5l2 3h9a1 1 0 0 1 1 1v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7Z');
    folderIcon.append(folderPath);
    categoryToggle.append(folderIcon);
    const categoryMenu = element('div', 'category-menu hidden');
    categoryMenu.id = 'mori-category-menu';
    categoryMenu.setAttribute('aria-label', t('收藏分类'));
    const folderListElement = element('div', 'folder-list');
    const categoryLabel = element('div', 'category-label', '松手后，点击文件夹收藏');
    categoryLabel.tabIndex = -1;
    categoryLabel.setAttribute('role', 'status');
    const unfiled = element('button', 'folder-unfiled', '保存到未分类');
    unfiled.type = 'button';
    categoryMenu.append(categoryLabel, folderListElement);
    foot.append(categoryToggle, unfiled, element('span', 'foot-info', item?.kind === 'video' ? '视频与网页来源一起保存' : '原图与网页来源一起保存'), footerAction);
    panel.append(head, quick, note, foot, categoryMenu);
    root.append(style, panel);
    document.documentElement.append(host);
    const session = {host, panel, quick, title, hint, source, diagnostic, note, tag, foot, footerAction, categoryToggle, categoryMenu, categoryLabel, folderListElement, folderList: null, foldersOpen: false, item, phase: 'dragging'};
    active = session;
    unfiled.addEventListener('click', () => {if (session.phase === 'choosing') save(session, null);});
    position(session, geometry.place(start, viewport()));
    if (preferences.collector === 'bottom') position(session, fitted(session));
    categoryToggle.addEventListener('click', () => {
      if (active === session && session.phase === 'dragging') setFoldersOpen(session, !session.foldersOpen);
    });
    panel.addEventListener('dragover', event => acceptDrag(session, event));
    panel.addEventListener('dragleave', event => {
      if (session.phase === 'dragging' && geometry.distance(point(event), session.rect) > 0) clearHover(session);
    });
    panel.addEventListener('drop', event => drop(session, event));
    return session;
  }

  function runtime(message, callback) {
    try {
      chrome.runtime.sendMessage(message, response => {
        const error = chrome.runtime.lastError;
        callback(error ? {ok: false, error: '扩展连接已失效，请刷新网页后再试。', diagnostic: error.message} : (response || {ok: false, error: 'Mori 未返回结果，请重新尝试。'}));
      });
    } catch (error) {
      callback({ok: false, error: '扩展连接已失效，请刷新网页后再试。', diagnostic: error.message});
    }
  }

  function clearHover(session) {
    cancelFolderTimer(session);
    session.folderHover = false;
    for (const row of session.folderListElement.children) row.classList.remove('hover');
    session.panel.classList.remove('is-over');
    session.title.textContent = t('松手，快速收藏');
    session.hint.textContent = t('拖到这里松手 · 保存到未分类');
  }

  function cancelFolderTimer(session) {
    clearTimeout(session.folderTimer);
    session.folderTimer = null;
  }

  function renderFolders(session) {
    session.folderListElement.replaceChildren();
    if (!session.folderList?.length) {
      session.folderListElement.append(element('div', 'folder-status', session.folderError || (session.folderList ? '暂无分类，可在 Mori 中新建' : '正在加载分类…')));
      return;
    }
    const fragment = document.createDocumentFragment();
    session.folderList.forEach((folder, index) => {
      const row = element('button', 'folder');row.textContent = folder.name;
      row.type = 'button';
      row.dataset.folderIndex = String(index);
      row.title = folder.name;
      row.addEventListener('click', () => save(session, folder));
      fragment.append(row);
    });
    session.folderListElement.append(fragment);
  }

  function setFoldersOpen(session, open) {
    cancelFolderTimer(session);
    session.foldersOpen = open;
    session.panel.classList.toggle('categories-open', open);
    session.categoryToggle.setAttribute('aria-expanded', String(open));
    session.categoryMenu.classList.toggle('hidden', !open);
    if (open) renderFolders(session);
    else session.folderListElement.replaceChildren();
  }

  function folderTarget(session, event) {
    const path = event.composedPath();
    const row = session.foldersOpen && path.find(node => node.dataset?.folderIndex !== undefined && node.parentElement === session.folderListElement);
    return {folder: row ? session.folderList?.[Number(row.dataset.folderIndex)] : null, toggle: path.includes(session.categoryToggle), menu: path.includes(session.categoryMenu)};
  }

  function acceptDrag(session, event) {
    if (active !== session || session.phase !== 'dragging') return;
    event.preventDefault();
    event.stopPropagation();
    if (event.dataTransfer) event.dataTransfer.dropEffect = 'copy';
    const target = folderTarget(session, event);
    if (target.toggle) {
      if (!session.folderHover) {
        session.folderHover = true;
        session.folderTimer = setTimeout(() => {
          session.folderTimer = null;
          if (active === session && session.phase === 'dragging') setFoldersOpen(session, !session.foldersOpen);
        }, 450);
      }
    } else {
      session.folderHover = false;
      cancelFolderTimer(session);
    }
    const browsing = session.foldersOpen || target.toggle || target.menu;
    session.panel.classList.toggle('is-over', !browsing || Boolean(target.folder));
    for (const row of session.folderListElement.children) row.classList.toggle('hover', Boolean(target.folder) && session.folderList[Number(row.dataset.folderIndex)] === target.folder);
    session.title.textContent = t(browsing ? '选择收藏分类' : '松手即可收藏');
    session.hint.textContent = t(browsing ? '松手后，点击文件夹收藏' : '保存到未分类');
    if (target.menu) {
      const bounds = session.folderListElement.getBoundingClientRect();
      if (event.clientY > bounds.bottom - 20) session.folderListElement.scrollTop += 10;
      else if (event.clientY < bounds.top + 20) session.folderListElement.scrollTop -= 10;
    }
  }

  function drop(session, event) {
    if (active !== session || session.phase !== 'dragging') return;
    event.preventDefault();
    event.stopPropagation();
    const target = folderTarget(session, event);
    cancelFolderTimer(session);
    // Once browsing folders, releasing the drag only parks the image. It must
    // not save to whichever row happens to be underneath the pointer.
    if (session.foldersOpen || target.toggle || target.menu) {chooseFolder(session); return;}
    save(session, target.folder);
  }

  function chooseFolder(session) {
    clearHover(session);
    session.phase = 'choosing';
    session.panel.dataset.phase = 'choosing';
    if (!session.foldersOpen) setFoldersOpen(session, true);
    session.categoryLabel.textContent = t('点击文件夹收藏 · 可滚轮查找');
    session.tag.textContent = t('选择文件夹');
    // Keep both the panel position and the list's current scroll position.
    // Focus the instruction so Tab reaches the folders without scrolling.
    session.categoryLabel.focus({preventScroll:true});
  }

  function save(session, folder) {
    if (active !== session || !['dragging', 'choosing'].includes(session.phase)) return;
    if (preferences.editBeforeSave) {edit(session, folder); return;}
    commit(session, folder);
  }

  function loadFolders(session) {
      runtime({action: 'folders',scope:session.item?.bookmark?'bookmark':'media'}, response => {
      if (active !== session || !['dragging', 'choosing', 'editing'].includes(session.phase)) return;
      if (!response.ok) {
        session.note.textContent = t(response.error || '请先连接 Mori 桌面程序');
        session.tag.textContent = t('连接未就绪');
        session.panel.dataset.connection = 'error';
        session.panel.title = session.note.textContent;
        session.title.textContent = session.note.textContent;
        session.folderError = t('分类暂不可用，请检查 Mori 连接');
      } else {
        session.note.classList.add('hidden');
        session.folderList = Array.isArray(response.folders) ? response.folders.filter(folder => folder && typeof folder.id === 'string' && folder.id && typeof folder.name === 'string') : [];
      }
      if (session.foldersOpen) renderFolders(session);
      session.refreshEditorFolders?.();
    });
  }

  function edit(session, folder) {
    setPhase(session, 'editing');
    session.panel.classList.remove('is-over');
    session.tag.textContent = t('确认后收藏');
    const form = element('form', 'editor');
    const input = (label, type, value) => {
      const row = element('label', '', label), field = element(type, '');
      if (type === 'input') field.value = value || '';
      row.append(field); form.append(row); return field;
    };
    const name = input('名称', 'input', session.item.name); name.maxLength = 250; name.required = true;
    const category = input('分类', 'select');
    const source = input('来源网址', 'input', session.item.source); source.maxLength = 8000;
    const message = element('div', 'editor-error'); message.setAttribute('role', 'status');
    session.refreshEditorFolders = () => {
      const selected = category.value || folder?.id || '';
      category.replaceChildren();
      const unfiled = element('option', '', '未分类'); unfiled.value = ''; category.append(unfiled);
      for (const value of session.folderList || []) {const option = element('option', ''); option.textContent = value.name; option.value = value.id; category.append(option);}
      category.value = [...category.options].some(option => option.value === selected) ? selected : '';
      category.disabled = !session.folderList;
      message.textContent = session.folderError || (!session.folderList ? t('正在加载分类…') : '');
    };
    session.refreshEditorFolders();
    const actions = element('div', 'editor-actions');
    const cancel = element('button', 'choice', '取消'), confirm = element('button', 'choice primary', '收藏');
    cancel.type = 'button'; confirm.type = 'submit'; cancel.onclick = dismiss;
    actions.append(cancel, confirm); form.append(message, actions); session.panel.append(form); session.editor = form;
    form.onsubmit = event => {
      event.preventDefault();
      if (active !== session || session.phase !== 'editing') return;
      const title = name.value.trim(), address = source.value.trim();
      if (!title) {name.focus(); return;}
      if ((session.item.bookmark && !address) || (address && !P.pageKey(address))) {message.textContent = t('来源网址须以 http:// 或 https:// 开头'); source.focus(); return;}
      session.item = {...session.item, name: title, source: address};
      session.source.textContent = title;
      const destination = session.folderList?.find(value => value.id === category.value);
      form.remove(); session.refreshEditorFolders = null;
      commit(session, destination);
    };
    name.focus(); name.select();
  }

  function commit(session, folder) {
    session.folderName = folder?.name;
    beginBackground(session);
    const send = item => runtime({...item, action:'save', folder:folder?.id || ''}, response => showResult(session,response));
    if (session.item.kind === 'video' && session.item.url.startsWith('blob:')) {
      MoriMediaSource.resolve(session.item).then(send, error => showResult(session,{ok:false,error:error.message}));
    } else send(session.item);
  }

  function preview(metadata, label) {
    const data = metadata || {};
    const card = element('div', 'preview');
    card.append(element('div', 'preview-label', label));
    if (typeof data.thumbnail === 'string' && data.thumbnail.length < 2 * 1024 * 1024 && /^data:image\/png;base64,[A-Za-z0-9+/=\r\n]+$/.test(data.thumbnail)) {
      const image = element('img', 'preview-image');
      image.src = data.thumbnail;
      image.alt = label + '预览';
      card.append(image);
    } else card.append(element('div', 'preview-image preview-empty', '暂无预览'));
    const name = element('div', 'preview-name');name.textContent = data.name || t('未命名图片');
    name.title = data.name || '未命名图片';
    const dimensions = Number(data.width) > 0 && Number(data.height) > 0 ? `${data.width} × ${data.height}` : '尺寸未知';
    const bytes = Number(data.bytes);
    const size = Number.isFinite(bytes) && bytes >= 0 ? (bytes >= 1024 * 1024 ? `${(bytes / 1024 / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`) : '';
    card.append(name, element('div', 'preview-meta', [dimensions, size].filter(Boolean).join(' · ')));
    return card;
  }

  function releaseChoice(session) {
    clearInterval(session.choiceHeartbeat);
    if (session.choicePort) {
      const port = session.choicePort;
      session.choicePort = null;
      try {port.disconnect();} catch {}
    }
  }

  function holdChoice(session) {
    if (session.choicePort) return;
    function expired() {
      if (!session.choiceToken || session.phase !== 'duplicate') return;
      const choiceToken = session.choiceToken;
      session.choiceToken = null;
      releaseChoice(session);
      runtime({action: 'discard', choiceToken}, () => {});
      showResult(session, {ok: false, error: '这次重复图片选择已过期，请重新收藏图片再选择。'});
    }
    try {
      const port = chrome.runtime.connect({name: 'mori-duplicate-choice'});
      session.choicePort = port;
      port.onMessage.addListener(message => {if (message?.expired) expired();});
      port.onDisconnect.addListener(() => {
        // Reading lastError also consumes a browser-reported disconnect error.
        const error = chrome.runtime.lastError;
        if (session.choicePort === port) expired();
      });
      port.postMessage({choiceToken: session.choiceToken});
      session.choiceHeartbeat = setInterval(() => {
        try {port.postMessage({choiceToken: session.choiceToken});} catch {expired();}
      }, 20000);
    } catch {expired();}
  }

  function showDuplicate(session, response) {
    setPhase(session, 'duplicate');
    session.panel.removeAttribute('title');
    session.choiceToken = response.choiceToken;
    session.note.classList.add('hidden');
    session.source.classList.add('hidden');
    session.diagnostic.classList.add('hidden');
    session.title.textContent = t('发现相同图片');
    session.hint.textContent = t(response.existing?.deleted ? '这张图片在回收站中。使用已有会恢复它，也可另存一份。' : '这张图片已在图库里，请选择怎么处理。');
    session.tag.textContent = t('等待你的选择');
    session.footerAction.textContent = t('Esc 不导入');
    const comparison = element('div', 'comparison');
    comparison.append(preview(response.existing, response.existing?.deleted ? '回收站中已有' : '图库中已有'), preview(response.incoming, '本次收藏'));
    const actions = element('div', 'actions');
    for (const [label, action] of [['不导入', 'skip'], ['使用已有', 'reuse'], ['仍然导入', 'keep']]) {
      const button = element('button', 'choice' + (action === 'reuse' ? ' primary' : ''), label);
      button.type = 'button';
      button.addEventListener('click', () => chooseDuplicate(session, action));
      actions.append(button);
    }
    session.comparison?.remove();
    session.actions?.remove();
    session.comparison = comparison;
    session.actions = actions;
    session.panel.insertBefore(comparison, session.foot);
    session.panel.insertBefore(actions, session.foot);
    holdChoice(session);
  }

  function chooseDuplicate(session, action) {
    if (active !== session || session.phase !== 'duplicate') return;
    if (action === 'skip') {dismiss(); return;}
    const choiceToken = session.choiceToken;
    session.choiceToken = null;
    if (session.background) {
      releaseChoice(session);
      session.comparison.remove(); session.actions.remove();
      beginBackground(session);
      runtime({action:'save', choiceToken, duplicateAction:action}, response => showResult(session, response));
      return;
    }
    setPhase(session, 'saving');
    session.panel.title = t('正在收藏…');
    releaseChoice(session);
    session.comparison.remove();
    session.actions.remove();
    session.title.textContent = t('正在收藏…');
    session.hint.textContent = t(action === 'keep' ? '正在另存一份原图' : '正在使用图库中的已有图片');
    session.tag.textContent = t('保存中');
    session.footerAction.textContent = t('Esc 关闭');
    runtime({action: 'save', choiceToken, duplicateAction: action}, response => {
      if (active === session) showResult(session, response);
      else if (response.choiceToken) runtime({action: 'discard', choiceToken: response.choiceToken}, () => {});
    });
  }

  function showResult(session, response) {
    if (session.background) {backgroundResult(session, response); return;}
    clearTimeout(hideTimer);
    if (response.ok && response.choiceRequired && response.choiceToken) {showDuplicate(session, response); return;}
    releaseChoice(session);
    session.comparison?.remove();
    session.actions?.remove();
    setPhase(session, response.ok ? 'success' : 'error');
    session.panel.classList.remove('is-over');
    session.note.classList.add('hidden');
    session.source.classList.remove('hidden');
    session.title.textContent = t(response.ok ? (response.keptCopy ? '已另存一份' : response.duplicate ? '已使用已有图片' : '已收藏') : '这次没有收藏成功');
    session.hint.textContent = t(response.ok ? (session.folderName ? `已保存到「${session.folderName}」` : response.duplicate && !response.keptCopy ? '已使用图库中的原图，未重复保存文件' : '已保存到本机图库 · 未分类') : response.error || (session.item?.kind === 'video' ? '视频未收藏，请稍后重试。' : '请重试，或截图后在 Mori 按 Ctrl+V 收藏。'));
    session.panel.title = response.ok ? session.title.textContent + ' · ' + session.hint.textContent : '';
    session.tag.textContent = t(response.ok ? '本机已保存' : '需要处理');
    session.footerAction.textContent = t('Esc 关闭');
    if (!response.ok) {
      session.source.textContent = t(session.item?.kind === 'video' ? '本次未保存视频，也没有改存为网址或封面。' : '也可截图后，在 Mori 按 Ctrl+V 收藏');
      if (response.diagnostic) {
        session.diagnostic.textContent = t(`连接诊断：${response.diagnostic}`);
        session.diagnostic.classList.remove('hidden');
      }
    }
    hideTimer = setTimeout(() => {if (active === session) dismiss();}, response.ok ? 1800 : 15000);
  }

  let preparedDrag = null;
  function restoreDrag() {
    const prepared = preparedDrag; preparedDrag = null;
    if (!prepared) return;
    const {target, draggable, userDrag, priority} = prepared;
    if (target.getAttribute('draggable') === 'true') {
      if (draggable === null) target.removeAttribute('draggable'); else target.setAttribute('draggable', draggable);
    }
    if (target.style.getPropertyValue('-webkit-user-drag') === 'element') {
      if (userDrag) target.style.setProperty('-webkit-user-drag', userDrag, priority);
      else target.style.removeProperty('-webkit-user-drag');
    }
  }
  document.addEventListener('pointerdown', event => {
    restoreDrag();
    if (event.composedPath().includes(active?.host) || event.composedPath().includes(noticeHost)) return;
    if (event.button !== 0 || event.pointerType !== 'mouse' || !preferencesReady || P.mode(preferences, window === window.top ? location.href : topPageUrl) !== 'enabled') return;
    const target = event.composedPath().find(node => node instanceof Element);
    if (!target || target.closest('input,textarea,select,[contenteditable]:not([contenteditable="false"])')) return;
    // Do not turn a player's seek/volume controls into a collection gesture.
    if (target instanceof HTMLVideoElement && target.controls) return;
    const item = imageAtContext(event);
    if (!item) return;
    preparedDrag = {target, item, draggable:target.getAttribute('draggable'), userDrag:target.style.getPropertyValue('-webkit-user-drag'), priority:target.style.getPropertyPriority('-webkit-user-drag')};
    target.setAttribute('draggable', 'true');
    target.style.setProperty('-webkit-user-drag', 'element', 'important');
  }, true);
  document.addEventListener('pointerup', restoreDrag, true);
  addEventListener('blur', () => {restoreDrag(); if (active?.phase === 'dragging') dismiss();});
  document.addEventListener('dragstart', event => {
    if (event.composedPath().includes(active?.host) || event.composedPath().includes(noticeHost)) return;
    if (!preferencesReady || P.mode(preferences, window === window.top ? location.href : topPageUrl) !== 'enabled') return;
    const target = event.target instanceof Element ? event.target : null;
    let found = preparedDrag && event.composedPath().includes(preparedDrag.target) ? preparedDrag.item : imageAtContext(event);
    // Programmatic/native image drags may report a point outside their element.
    if (!found && target instanceof HTMLImageElement && (target.currentSrc || target.getAttribute('src'))) found = {url:target.currentSrc || target.src, kind:'image', name:target.alt || document.title};
    if (!found) return;
    const item = {...found, source:location.href};
    if (event.dataTransfer && !(target instanceof HTMLImageElement)) {
      event.dataTransfer.setData('text/uri-list', item.url);
      event.dataTransfer.setData('text/plain', item.url);
      event.dataTransfer.effectAllowed = 'copyLink';
    }
    const session = mount(item, point(event));
    loadFolders(session);
    setTimeout(() => {if (event.defaultPrevented && active === session && session.phase === 'dragging') {restoreDrag();dismiss();}}, 0);
  }, true);

  document.addEventListener('dragover', event => {
    const session = active;
    if (!session || session.phase !== 'dragging') return;
    // Place once at dragstart. Never chase a pointer that may be heading to
    // another app or drop target; outside events keep their native behavior.
    if (geometry.distance(point(event), session.rect) > 0) clearHover(session);
  }, true);

  document.addEventListener('dragend', () => {restoreDrag();if (active?.phase === 'dragging') dismiss();}, true);
  document.addEventListener('keydown', event => {
    if (event.key === 'Escape' && active) {event.preventDefault();event.stopPropagation();restoreDrag();dismiss();}
  }, true);
  addEventListener('resize', () => {if (active) position(active, fitted(active));});
  document.addEventListener('contextmenu', event => {
    contextPoint = point(event);
    contextImage = imageAtContext(event);
    contextTime = Date.now();
  }, true);
  chrome.runtime.onMessage.addListener((message, sender, reply) => {
    if (message?.action === 'context-edit') {
      const session = mount(message.item, contextPoint || {x: innerWidth - 20, y: innerHeight - 120});
      session.requestId = message.requestId;
      edit(session); loadFolders(session);
      reply({displayed: true}); return;
    }
    if (message?.action === 'resolve-context-image') {
      const item = Date.now() - contextTime < 120000 ? contextImage : null;
      contextImage = null;
      if (message.expectedUrl && item?.url !== message.expectedUrl) {reply({ok:false,kind:item?.kind,error:'视频目标已变化，请重新右键目标视频收藏。'});return;}
      if (item?.kind === 'video' && item.url.startsWith('blob:')) {
        MoriMediaSource.resolve(item).then(value => reply({ok:true,item:value}), error => reply({ok:false,kind:'video',error:error.message}));
        return true;
      }
      reply(item ? {ok: true, item} : {ok: false, error: '这里没有找到可收藏的图片，请右键图片本身，或截图后粘贴到 Mori。'});
      return;
    }
    if (message?.action === 'context-saving') {
      const session = mount({name: message.name,kind:message.kind === 'video' ? 'video' : 'image'}, contextPoint || {x: innerWidth - 20, y: innerHeight - 120});
      session.requestId = message.requestId;
      setPhase(session, 'saving');
      session.panel.title = t('正在收藏…');
      session.note.classList.add('hidden');
      session.title.textContent = t('正在收藏…');
      session.hint.textContent = t(session.item.kind === 'video' ? '正在保存视频与来源到本机图库' : '正在保存原图与网页来源到本机图库');
      session.tag.textContent = t('保存中');
      session.footerAction.textContent = t('Esc 关闭');
      reply({displayed: true});
      return;
    }
    if (message?.action !== 'result') return;
    // Closing the progress panel or beginning another collection must not bring
    // it back when a slow request completes, nor replace a newer duplicate choice.
    if (message.requestId && active?.requestId !== message.requestId) {reply({displayed: false}); return;}
    const session = message.requestId ? active : mount(null, contextPoint || {x: innerWidth - 20, y: innerHeight - 120});
    showResult(session, message.result || {ok: false, error: 'Mori 未返回结果，请重试。'});
    reply({displayed: true});
  });
})();
