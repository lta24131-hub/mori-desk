(() => {
  const key = 'moriPreferences';
  const defaults = {theme: 'light', language: 'auto', collector: 'popup', editBeforeSave: false, disabledPages: [], disabledHosts: []};
  function pageKey(value) {
    try { const url = new URL(value); return /^https?:$/.test(url.protocol) ? url.origin + url.pathname : ''; } catch { return ''; }
  }
  function hostKey(value) { try { return pageKey(value) ? new URL(value).hostname.toLowerCase() : ''; } catch { return ''; } }
  const matchesHost = (host, rule) => host === rule || host.endsWith('.' + rule);
  function normalize(raw = {}) {
    if (!raw || typeof raw !== 'object') raw = {};
    const list = (value, convert) => [...new Set((Array.isArray(value) ? value : []).filter(v => typeof v === 'string' && v.length <= 2000).map(convert).filter(Boolean))].slice(-500);
    return {
      theme: ['light', 'dark', 'system'].includes(raw.theme) ? raw.theme : defaults.theme,
      language: ['auto', 'zh-CN', 'en'].includes(raw.language) ? raw.language : defaults.language,
      collector: ['popup', 'bottom', 'disabled'].includes(raw.collector) ? raw.collector : defaults.collector,
      editBeforeSave: raw.editBeforeSave === true,
      disabledPages: list(raw.disabledPages, pageKey),
      disabledHosts: list(raw.disabledHosts, v => /^[a-z0-9.\-\[\]:]+$/i.test(v) ? hostKey('https://' + v) : '')
    };
  }
  function mode(raw, url) {
    const prefs = normalize(raw), host = hostKey(url);
    if (prefs.collector === 'disabled') return 'disabled';
    if (host && prefs.disabledHosts.some(rule => matchesHost(host, rule))) return 'host';
    if (prefs.disabledPages.includes(pageKey(url))) return 'page';
    return 'enabled';
  }
  function withMode(raw, url, next) {
    const prefs = normalize(raw), page = pageKey(url), host = hostKey(url);
    if (!page || !['enabled', 'page', 'host'].includes(next)) return prefs;
    if (prefs.collector === 'disabled') prefs.collector = 'popup';
    if (next === 'enabled') {
      if (prefs.collector === 'disabled') prefs.collector = 'popup';
      prefs.disabledPages = prefs.disabledPages.filter(v => v !== page);
      prefs.disabledHosts = prefs.disabledHosts.filter(v => !matchesHost(host, v));
    } else if (next === 'host') prefs.disabledHosts.push(host);
    else {
      prefs.disabledHosts = prefs.disabledHosts.filter(v => !matchesHost(host, v));
      prefs.disabledPages.push(page);
    }
    return normalize(prefs);
  }
  async function read() { const data = await chrome.storage.local.get(key); return normalize(data[key]); }
  async function write(value) { const prefs = normalize(value); await chrome.storage.local.set({[key]: prefs}); return prefs; }
  function language(prefs, browser = globalThis.navigator?.language || 'zh-CN') { return prefs.language === 'auto' ? (/^zh/i.test(browser) ? 'zh-CN' : 'en') : prefs.language; }
  function theme(prefs, dark) { return prefs.theme === 'system' ? (dark ? 'dark' : 'light') : prefs.theme; }
  globalThis.MoriPreferences = Object.freeze({key, defaults, normalize, pageKey, hostKey, mode, withMode, read, write, language, theme});
})();
