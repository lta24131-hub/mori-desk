const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const crypto = require('node:crypto');
const root = path.resolve(__dirname, '..');
const popup = fs.readFileSync(path.join(root, 'browser-extension/popup.css'), 'utf8');
const content = fs.readFileSync(path.join(root, 'browser-extension/content.js'), 'utf8');
const model = fs.readFileSync(path.join(root, 'web/model/desktop-theme.css'), 'utf8');
const source = fs.readFileSync(path.join(root, 'src/ModelWindow.cs'), 'utf8');
const palettes = {
  light: ['#FAFAFA','#F3F3F3','#FFFFFF','#FAFAFA','#EEEEEE','#D7D7D7','#E4E4E4','#252525','#626262','#E7E7E7','#6D5BC7','#F0EDFA','#B7B7B7','#6D5BC7','#FFFFFF'],
  dark: ['#161616','#1D1D1D','#242424','#292929','#303030','#484848','#363636','#EEEEEE','#AAAAAA','#3B3B3B','#B7A8ED','#322D40','#626262','#B7A8ED','#161616']
};
const browserTokens = ['bg','panel','surface','input','control','line','divider','text','muted','hover','accent','active','scroll-thumb','primary','on-primary'];
const modelTokens = ['canvas','panel','surface','input','control','border','divider','text','muted','hover','accent','accent-soft','scroll-thumb','primary','on-primary'];
const neutralRoles = [0,1,2,3,4,5,6,7,8,9,12,14];
function cssTemplates(text) {
  const templates = [...text.matchAll(/(?:style\.textContent\s*=\s*|const css\s*=\s*)`([\s\S]*?)`;/g)];
  return templates.length ? templates.map(m => m[1]).join('\n') : text;
}
function rule(css, selector) {
  // Match exact members of grouped selectors, retaining later declarations.
  if (css === content) css = cssTemplates(css);
  const clean = css.replace(/\/\*[\s\S]*?\*\//g,'');
  const found = [...clean.matchAll(/([^{}]+)\{([^{}]*)\}/g)]
    .filter(m => m[1].split(',').some(part => part.trim() === selector));
  assert(found.length, `missing rule ${selector}`);
  return found.map(m => m[2]).join(';');
}
function variables(text) {
  return Object.fromEntries([...text.matchAll(/--([\w-]+)\s*:\s*(#[\da-f]+)\s*[;}]/gi)].map(m => [m[1],m[2].toUpperCase()]));
}
function declaration(css, selector, property) {
  const escaped = property.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const found = [...rule(css,selector).matchAll(new RegExp(`(?:^|;)\\s*${escaped}\\s*:\\s*([^;]+)`, 'g'))];
  assert(found.length, `missing ${property} on ${selector}`);
  return found.at(-1)[1].trim();
}
function tokens(css, selector, expected) {
  for (const [property,value] of Object.entries(expected)) assert.equal(declaration(css,selector,property),value,`${selector} ${property}`);
}
function neutral(hex, description) {
  assert.match(hex,/^#[\dA-F]{6}(?:[\dA-F]{2})?$/i,description);
  const [r,g,b] = hex.slice(1,7).match(/../g).map(value => parseInt(value,16));
  assert.equal(r,g,description+' red/green channels');
  assert.equal(g,b,description+' green/blue channels');
}
for (const [theme,palette] of Object.entries(palettes)) {
  test(`${theme} popup, collector and model chrome match every desktop palette role`, () => {
    const p = variables(rule(popup, theme === 'light' ? ':root' : ':root[data-theme="dark"]'));
    const c = variables(rule(content, theme === 'light' ? '.panel' : '.panel[data-theme="dark"]'));
    const m = variables(rule(model, `html[data-nook-theme="${theme}"]`));
    for (let i=0;i<palette.length;i++) {
      assert.equal(p[browserTokens[i]], palette[i], 'popup '+browserTokens[i]);
      assert.equal(c[browserTokens[i]], palette[i], 'collector '+browserTokens[i]);
      assert.equal(m['nook-'+modelTokens[i]], palette[i], 'model '+modelTokens[i]);
    }
    assert.match(rule(popup, theme === 'light' ? ':root' : ':root[data-theme="dark"]'), new RegExp(`color-scheme:${theme}`));
    const toast = variables(rule(content, theme === 'light' ? '.toast' : '.toast[data-theme="dark"]'));
    assert.equal(toast.bg, palette[2]);assert.equal(toast.text, palette[7]);assert.equal(toast.line, palette[5]);assert.equal(toast.accent, palette[10]);
  });
  test(`${theme} ordinary chrome has equal RGB channels while interaction roles stay purple`, () => {
    for (const [label,css,selector,names,prefix] of [
      ['popup',popup,theme === 'light' ? ':root' : ':root[data-theme="dark"]',browserTokens,''],
      ['collector',content,theme === 'light' ? '.panel' : '.panel[data-theme="dark"]',browserTokens,''],
      ['model',model,`html[data-nook-theme="${theme}"]`,modelTokens,'nook-']
    ]) {
      const values = variables(rule(css,selector));
      for (const i of neutralRoles) neutral(values[prefix+names[i]],`${theme} ${label} ${names[i]}`);
      for (const i of [10,11,13]) {
        const [r,g,b] = values[prefix+names[i]].slice(1).match(/../g).map(value => parseInt(value,16));
        assert(b>r&&r>g,`${theme} ${label} ${names[i]} remains purple`);
      }
    }
    const toast = variables(rule(content,theme === 'light' ? '.toast' : '.toast[data-theme="dark"]'));
    for (const name of ['bg','text','line']) neutral(toast[name],`${theme} toast ${name}`);
  });
}
function luminance(hex) {
  const c = hex.slice(1).match(/../g).map(v => parseInt(v,16)/255).map(v => v<=.04045?v/12.92:((v+.055)/1.055)**2.4);
  return c[0]*.2126+c[1]*.7152+c[2]*.0722;
}
function contrast(a,b) {a=luminance(a);b=luminance(b);return (Math.max(a,b)+.05)/(Math.min(a,b)+.05);}
test('normal, secondary and selected labels and primary buttons have readable contrast', () => {
  for (const [theme,p] of Object.entries(palettes)) {
    for (const surface of [0,1,2,3,4,9,11]) for (const text of [7,8]) assert(contrast(p[text],p[surface])>=4.5,`${theme} role ${text}/${surface}`);
    assert(contrast(p[10],p[11])>=4.5);assert(contrast(p[13],p[14])>=4.5);
  }
});
test('ordinary popup and collector controls and hover states use neutral tokens', () => {
  tokens(popup,'body',{background:'var(--bg)',color:'var(--text)'});
  tokens(popup,'header',{background:'var(--panel)','border-bottom':'1px solid var(--divider)'});
  tokens(popup,'.card',{background:'var(--surface)',border:'1px solid var(--line)'});
  tokens(popup,'select',{background:'var(--input)',border:'1px solid var(--line)'});
  tokens(popup,'.icon',{background:'var(--control)'});
  tokens(popup,'.rule button',{background:'var(--control)'});
  tokens(popup,'.nav:hover',{color:'var(--text)',background:'var(--hover)'});
  for (const selector of ['.update-row button','.update-ack','#message']) tokens(popup,selector,{color:'var(--text)'});
  for (const selector of ['.icon:hover','.choices label:hover span','.rule button:hover','#bookmark-review button:not(:disabled):hover']) tokens(popup,selector,{background:'var(--hover)'});
  tokens(popup,'main',{'scrollbar-color':'var(--scroll-thumb) transparent'});
  for (const selector of ['.layout-preview.floating:after','.layout-preview.dock:after']) tokens(popup,selector,{border:'1px solid var(--muted)',background:'var(--control)'});
  tokens(popup,'.theme-preview',{background:'#FAFAFA'});
  tokens(popup,'.theme-preview.dark',{background:'#161616'});
  assert.equal(declaration(popup,'.theme-preview.system','background'),'linear-gradient(90deg,#FAFAFA 50%,#161616 50%)');
  for (const selector of ['.theme-preview:after','.theme-preview.dark:after']) {
    const background = declaration(popup,selector,'background');
    assert.doesNotMatch(background,/var\(--(?:accent|active|primary)\)/);
    const hexes = [...background.matchAll(/#[\da-f]{6}(?:[\da-f]{2})?\b/gi)];
    assert(hexes.length || /var\(--(?:muted|line|text)\)/.test(background),selector+' uses neutral sample lines');
    for (const [hex] of hexes) neutral(hex,selector+' sample line');
  }
  tokens(content,'.panel',{background:'var(--bg)',color:'var(--text)',border:'1px solid var(--line)'});
  for (const selector of ['.editor input','.editor select']) tokens(content,selector,{background:'var(--input)',color:'var(--text)',border:'1px solid var(--line)'});
  tokens(content,'.choice',{background:'var(--control)',color:'var(--text)',border:'1px solid var(--line)'});
  tokens(content,'.category-toggle:hover',{color:'var(--text)',background:'var(--hover)'});
  for (const selector of ['.folder:hover','.folder.hover']) tokens(content,selector,{color:'var(--text)',background:'var(--hover)'});
  tokens(content,'.folder-retry',{color:'var(--text)'});
  tokens(content,'.collect-icon',{color:'var(--muted)'});
  tokens(content,'.panel[data-phase="success"] .quick',{'border-color':'var(--line)',background:'var(--surface)'});
  for (const selector of ['.folder-retry:hover','.choice:hover']) tokens(content,selector,{background:'var(--hover)'});
  for (const selector of ['.editor','.folder-list']) tokens(content,selector,{'scrollbar-color':'var(--scroll-thumb) transparent'});
});
test('ordinary 3D chrome, icons and export controls use neutral tokens', () => {
  const scoped = selector => 'html[data-nook-theme] '+selector;
  for (const selector of ['.brand-mark','.empty-icon','.cloud-dialog-icon','.header-open','.tool-rail button','.appearance-heading button','.color-picker','.material-grid button','.axis-presets button','.quarter-turns button','.reset-orientation','.stop-loading','.model-info button','.cloud-dialog-actions button','.fullscreen-close','.export-light-button']) {
    tokens(model,scoped(selector),{color:'var(--nook-text)',background:'var(--nook-control)','border-color':'var(--nook-border)'});
    assert.doesNotMatch(rule(model,scoped(selector)),/var\(--nook-(?:accent(?:-soft)?|primary)\)/,selector+' is not washed purple');
  }
  tokens(model,scoped('.export-light-button > svg'),{color:'var(--nook-text)'});
  for (const selector of ['.offline-pill','.gesture-hint']) tokens(model,scoped(selector),{color:'var(--nook-text)',background:'var(--nook-panel)','border-color':'var(--nook-border)'});
  tokens(model,scoped('.offline-pill.preparing'),{color:'var(--nook-muted)'});
  tokens(model,scoped('.gesture-hint i'),{background:'var(--nook-divider)'});
  tokens(model,scoped('.loader'),{'border-color':'var(--nook-border)','border-top-color':'var(--nook-muted)'});
  tokens(model,scoped('.loading-progress'),{background:'var(--nook-control)'});
  tokens(model,scoped('.loading-progress > span'),{background:'var(--nook-muted)'});
  for (const selector of ['.header-open:not(:disabled):hover','.tool-rail button:not(:disabled):not(.active):hover','.appearance-heading button:hover','.material-grid button:not(.selected):hover','.axis-presets button:not(.selected):hover','.quarter-turns button:hover','.reset-orientation:hover','.model-info button:hover','.stop-loading:hover','.fullscreen-close:hover','.export-light-button:hover','.cloud-dialog-actions button:not(.primary):hover']) tokens(model,scoped(selector),{background:'var(--nook-hover)'});
  for (const selector of ['.appearance-panel','.orientation-panel','.cloud-dialog']) tokens(model,scoped(selector),{'scrollbar-color':'var(--nook-scroll-thumb) transparent'});
});
test('purple remains scoped to selected, primary and keyboard-focus interactions', () => {
  tokens(popup,'.choices input:checked+span',{'border-color':'var(--accent)',background:'var(--active)',color:'var(--accent)'});
  tokens(popup,'#bookmark-review #bookmark-confirm',{background:'var(--primary)',color:'var(--on-primary)','border-color':'var(--primary)'});
  tokens(popup,'.toggle-row input:checked',{background:'var(--primary)'});
  tokens(popup,'.toggle-row input:checked:after',{background:'var(--on-primary)'});
  for (const selector of ['button:focus-visible','select:focus-visible','input:focus-visible','summary:focus-visible','.choices input:focus-visible+span']) tokens(popup,selector,{outline:'2px solid var(--accent)'});
  tokens(content,'.category-toggle[aria-expanded="true"]',{color:'var(--accent)',background:'var(--active)'});
  tokens(content,'.choice.primary',{background:'var(--primary)',color:'var(--on-primary)','border-color':'var(--primary)'});
  tokens(content,'.is-over',{'border-color':'var(--accent)'});
  for (const selector of ['.category-toggle:focus-visible','.folder:focus-visible','.folder-retry:focus-visible','.choice:focus-visible','.toast-action:focus-visible','.toast-close:focus-visible']) tokens(content,selector,{outline:'2px solid var(--accent)'});
  for (const selector of ['.tool-rail button.active','.material-grid button.selected','.axis-presets button.selected']) tokens(model,'html[data-nook-theme] '+selector,{background:'var(--nook-accent-soft)',color:'var(--nook-accent)','border-color':'var(--nook-accent)'});
  for (const selector of ['.open-button','.cloud-dialog-actions button.primary']) tokens(model,'html[data-nook-theme] '+selector,{background:'var(--nook-primary)',color:'var(--nook-on-primary)','border-color':'var(--nook-primary)'});
  for (const selector of ['button:focus-visible','input:focus-visible','.color-picker:focus-within']) tokens(model,'html[data-nook-theme] '+selector,{outline:'2px solid var(--nook-accent)'});
});
const frozenCollector = path.join(root,'releases/20261014-124727/Nook/browser-extension/content.js');
const frozenPaintRoot = path.join(root,'releases/20261014-131615/Nook');
const frozenViewer = path.join(root,'releases/20261014-124727/Nook/web/model/_next');
function missingBaseline(paths) {
  const missing = paths.filter(file => !fs.existsSync(file));
  return missing.length ? 'NOT RUN: immutable historical baseline unavailable: '+missing.map(file => path.relative(root,file)).join(', ') : false;
}
test('current collector JavaScript remains syntactically valid independently of historical release baselines', () => {
  assert.doesNotThrow(() => new vm.Script(content));
});
test('collector edits are confined to the two style templates', {skip:missingBaseline([frozenCollector])}, () => {
  // Compare against the immutable 0.1.19 package from before this palette task.
  const frozen = fs.readFileSync(frozenCollector,'utf8');
  const strip = text => text.replace(/style\.textContent = `[\s\S]*?`;/, 'style.textContent = `<toast-style>`;').replace(/const css = `[\s\S]*?`;/, 'const css = `<collector-style>`;').replace(/\r\n/g,'\n');
  assert.equal(strip(content),strip(frozen));
  assert.doesNotThrow(() => new vm.Script(content));
});
test('neutral recoloring preserves every non-paint CSS declaration from frozen 0.1.20', {skip:missingBaseline(['browser-extension/popup.css','browser-extension/content.js','web/model/desktop-theme.css'].map(file => path.join(frozenPaintRoot,file)))}, () => {
  function layoutDeclarations(text) {
    const css = cssTemplates(text).replace(/\/\*[\s\S]*?\*\//g,'');
    const result = {};
    for (const match of css.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
      const properties = {};
      for (const entry of match[2].split(';')) {
        const colon = entry.indexOf(':');
        if (colon < 0) continue;
        const name = entry.slice(0,colon).trim();
        let value = entry.slice(colon+1).trim().replace(/\s+/g,' ');
        if (/^--/.test(name) && /^#[\da-f]+$/i.test(value)) continue;
        if (/^(?:background(?:-[\w-]+)?|color|border(?:-(?:top|right|bottom|left))?-color|outline-color|box-shadow|scrollbar-color)$/.test(name)) continue;
        // Border/outline paint may change; their width and line style may not.
        if (/^(?:border(?:-(?:top|right|bottom|left))?|outline)$/.test(name)) value = value
          .replace(/var\(--[\w-]+\)|#[\da-f]{3,8}\b|rgba?\([^)]*\)|hsla?\([^)]*\)|\b(?:transparent|currentColor|inherit)\b/gi,'')
          .trim().replace(/\s+/g,' ');
        properties[name] = value;
      }
      if (!Object.keys(properties).length) continue;
      const declarationList = Object.entries(properties).sort(([a],[b]) => a.localeCompare(b));
      for (const selector of match[1].split(',').map(value => value.trim())) (result[selector] ??= []).push(declarationList);
    }
    return Object.entries(result).sort(([a],[b]) => a.localeCompare(b));
  }
  for (const [relative,current] of [
    ['browser-extension/popup.css',popup],
    ['browser-extension/content.js',content],
    ['web/model/desktop-theme.css',model]
  ]) {
    const frozen = fs.readFileSync(path.join(frozenPaintRoot,relative),'utf8');
    assert.deepEqual(layoutDeclarations(current),layoutDeclarations(frozen),relative+' retains layout, typography and dimensions');
  }
});
test('popup retains fixed width, scoped checked states, keyboard focus and red errors', () => {
  assert.match(popup,/html,html body\{width:376px;min-width:376px;max-width:376px\}/);
  assert.match(popup,/\.choices input:checked\+span\{[^}]*background:var\(--active\)/);
  assert.match(popup,/\.toggle-row input:checked:after\{[^}]*background:var\(--on-primary\)/);
  assert.match(popup,/focus-visible/);assert.match(popup,/--error:#a03f34/);assert.match(popup,/--error:#f2ab9f/);
  assert.match(content,/--error:#9b463a/);assert.match(content,/\.toast\[data-kind="error"\]\{color:#994c40\}/);
  assert.doesNotMatch(popup+content,/#(?:44724d|a4c797|a4c797|242925|f8f9f7|f8f8f5)\b/i);
});
test('3D adaptation does not target scene backgrounds, material swatches or semantic axes', () => {
  const clean = model.replace(/\/\*[\s\S]*?\*\//g,'');
  assert.doesNotMatch(clean,/\.(?:app-shell|viewer-stage|canvas-host|technical-grid|material-ball|axis-badge|axis-letter)(?:[\s.:>{,]|$)/);
  assert.doesNotMatch(rule(model,'html[data-nook-theme] .color-swatch.selected'),/(?:^|;)\s*(?:background(?:-image|-color)?|color|filter)\s*:/);
  assert.match(rule(model,'html[data-nook-theme] .color-swatch.selected'),/box-shadow:\s*0 0 0 2px var\(--nook-accent\),\s*inset 0 1px 1px #FFFFFF59/);
  assert.match(model,/\.color-picker:focus-within/);assert.match(model,/prefers-reduced-motion: reduce/);
});
test('3D theme bridge initializes early, follows theme changes and unsubscribes on close', () => {
  assert.match(source,/Mori\.Theme\.Changed\+=ApplyTheme/);assert.match(source,/closed=true;Mori\.Theme\.Changed-=ApplyTheme;web\.Dispose\(\)/);
  assert.match(source,/NavigationCompleted\+=\(s,e\)=>ApplyTheme\(\)/);
  assert.match(source,/AddScriptToExecuteOnDocumentCreatedAsync\(ThemeBootstrapScript/);
  assert.match(source,/new JavaScriptSerializer\(\)\.Serialize\(css\)/);
  assert.match(source,/observer\.disconnect\(\)/);
  assert.doesNotMatch(source,/(?:renderer|scene|material)\.(?:background|color|setClearColor)/);
});
test('all upstream minified bundles remain byte-identical to the frozen viewer', {skip:missingBaseline([frozenViewer])}, () => {
  const base=frozenViewer;
  function visit(folder,rel='') {for (const file of fs.readdirSync(folder,{withFileTypes:true})) {
    const name=path.join(rel,file.name);if(file.isDirectory())visit(path.join(folder,file.name),name);
    else {const a=fs.readFileSync(path.join(folder,file.name));const b=fs.readFileSync(path.join(root,'web/model/_next',name));
      assert.equal(crypto.createHash('sha256').update(a).digest('hex'),crypto.createHash('sha256').update(b).digest('hex'),name);}
  }}
  visit(base);
});
