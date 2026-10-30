// Deterministic extension logic checks. No browser profile, network, or library writes.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const {test} = require('node:test');
const crypto = require('node:crypto');
const root = path.resolve(__dirname, '..');
const read = file => fs.readFileSync(path.join(root, 'browser-extension', file), 'utf8');
const extensionManifest = JSON.parse(read('manifest.json'));
const geometryContext = vm.createContext({});
vm.runInContext(read('drag-geometry.js'), geometryContext);
const geometry = geometryContext.MoriDragGeometry;

test('a large target starts 44px from the pointer, switches sides, and fits viewport edges', () => {
  const viewport = {width: 1440, height: 900};
  const left = geometry.place({x: 240, y: 300}, viewport);
  assert.equal(left.width, 408);
  assert.equal(left.height, 300);
  assert.equal(left.x, 284);
  assert.equal(geometry.distance({x: 240, y: 300}, left), 44);
  const right = geometry.place({x: 1340, y: 300}, viewport);
  assert.equal(right.x + right.width, 1296);
  for (const [width, height] of [[1440, 900], [1280, 720], [800, 600], [390, 700], [300, 240]]) {
    for (const x of [0, width / 2, width]) for (const y of [0, height / 2, height]) {
      const rect = geometry.place({x, y}, {width, height});
      assert(rect.x >= 12 && rect.y >= 12);
      assert(rect.x + rect.width <= width - 12);
      assert(rect.y + rect.height <= height - 12);
      const resized = geometry.fit(rect, {width: 300, height: 240});
      assert(resized.x + resized.width <= 288 && resized.y + resized.height <= 228);
    }
  }
});

test('geometry only measures distance to the fixed target; no pointer-following API remains', () => {
  const rect = {x: 500, y: 200, width: 408, height: 300};
  assert.equal(geometry.distance({x: 490, y: 230}, rect), 10);
  assert.equal(geometry.distance({x: 530, y: 230}, rect), 0);
  assert.equal(geometry.distance({x: 200, y: 230}, rect), 300);
  assert.equal(geometry.shouldFollow, undefined);
});

function background(localOptions = {}) {
  let listener;
  let clickListener, installedListener, startupListener, connectedListener;
  let receiver = true;
  let contextImage = {ok: false, error: '这里没有找到可收藏的图片'};
  const menus = new Map();
  const tabMessages = [], badges = [], titles = [];
  let nativeError;
  let nativeReply = {ok: true, folders: [], theme: 'dark'};
  const nativeCalls = [];
  const fetchCalls = [];
  let preferences = {language:'zh-CN'};
  const metadata = {};
  const storeWrites=[];
  const chrome = {
    storage:{local:{get:async () => ({...metadata,moriPreferences:preferences}),set:async values=>{if(localOptions.storageFail)throw Error('storage failed');storeWrites.push(values);Object.assign(metadata,values);}},onChanged:{addListener(){}}},
    runtime: {
      id: 'abcdefghijklmnopabcdefghijklmnop',
      onMessage: {addListener(value) {listener = value;}},
      onInstalled: {addListener(value) {installedListener = value;}},
      onStartup: {addListener(value) {startupListener = value;}},
      onConnect: {addListener(value) {connectedListener=value;}},
      sendNativeMessage(host, message, callback) {
        if(['save','bookmark','importBookmarks'].includes(message.action))assert.equal(metadata.moriUpdatePendingWrite,true,'pre-write journal must be durable before native transmission');
        nativeCalls.push({host, message});
        chrome.runtime.lastError = nativeError ? {message: nativeError} : null;
        callback(nativeError ? undefined : nativeReply);
        chrome.runtime.lastError = null;
      }
    },
    contextMenus: {
      update(id, properties, callback) {
        chrome.runtime.lastError = menus.has(id) ? null : {message: 'Cannot find menu item'};
        if (menus.has(id)) menus.set(id, {...menus.get(id), ...properties});
        callback(); chrome.runtime.lastError = null;
      },
      create(properties, callback) {assert(!menus.has(properties.id)); menus.set(properties.id, properties); chrome.runtime.lastError = null; callback();},
      onClicked: {addListener(value) {clickListener = value;}}
    },
    tabs: {sendMessage: async (id, message, options) => {tabMessages.push({id, message, options}); if (!receiver) throw new Error('No receiver'); return message.action === 'resolve-context-image' ? contextImage : {displayed: true};}},
    action: {
      setBadgeText: async value => {badges.push(value);},
      setBadgeBackgroundColor: async () => {},
      setTitle: async value => {titles.push(value);}
    }
  };
  const context = vm.createContext({chrome, crypto, URL, AbortController, Uint8Array, setTimeout, clearTimeout,
    MoriInstalledGeneration:localOptions.generation,
    self: {MoriLocalConnection:localOptions.config},
    btoa: value => Buffer.from(value, 'binary').toString('base64'),
    fetch: async (url, options) => {
      fetchCalls.push({url, options});
      if (url === 'http://127.0.0.1:47891/mori/v1') {
        if(localOptions.fail)throw Error('Connection lost');
        return {ok:!localOptions.reject,json:async()=>localOptions.reply||{ok:true,folders:[]}};
      }
      let delivered = false;
      return {ok: true, headers: {get() {return '3';}}, body: {getReader() {return {read: async () => delivered ? {done: true} : (delivered = true, {value: Uint8Array.of(1, 2, 3), done: false})};}}};
    }
  });
  context.importScripts = file => vm.runInContext(read(file), context);
  const collectorScripts=[];
  if(localOptions.collectors){
    let pong=localOptions.pong;
    chrome.runtime.getManifest=()=>extensionManifest;
    chrome.tabs.sendMessage=async(id,message)=>{
      if(message.action==='mori-collector-ping')return pong;
      if(message.action==='mori-collector-wake')return localOptions.wakePong || pong;
      return {displayed:true};
    };
    chrome.scripting={executeScript:async request=>{
      collectorScripts.push(request);
      pong=localOptions.injectedPong || {ready:true,busy:false,version:extensionManifest.version,generation:localOptions.generation};
      return [{result:true}];
    }};
  }
  vm.runInContext(read('background.js'), context);
  return {
    nativeCalls, fetchCalls, menus, tabMessages, badges, titles,metadata,storeWrites,collectorScripts,
    repairCollector:()=>vm.runInContext('ensureCollector(1,0)',context),
    preferences(value) {preferences = {...preferences,...value};},
    install: () => installedListener(), startup: () => startupListener(),
    click: (info, tab = {id: 1, title: '网页标题', url: 'https://example.com/page'}) => clickListener(info, tab),
    receive(value) {receiver = value;},
    resolveImage(value) {contextImage = value;},
    fail(error) {nativeError = error;},
    respond(value) {nativeReply = value;},
    dispatch(message,sender) {return new Promise(resolve=>listener(message,sender,resolve));},
    connect(port) {connectedListener(port);},
    request(message, tabId = 1, sender = {tab: {id: tabId}, frameId: 0}) {return new Promise(resolve => {const result = listener(message, sender, resolve); if (message.action !== 'discard') assert.equal(result, true);});}
  };
}

test('same-version collector repair compares loaded generation, while legacy bundles keep version compatibility',async()=>{
  const version=extensionManifest.version;
  for(const pong of [{ready:true,busy:false,version,generation:'a'.repeat(32)},{ready:true,busy:false,version}]){
    const bridge=background({collectors:true,generation:'b'.repeat(32),pong});
    assert.equal(await bridge.repairCollector(),true);
    assert.equal(bridge.collectorScripts.length,1,'new background replaces old same-version open-page collector');
  }
  for(const options of [
    {generation:'b'.repeat(32),pong:{ready:true,version,generation:'b'.repeat(32)}},
    {generation:'b'.repeat(32),pong:{ready:true,busy:true,version,generation:'a'.repeat(32)}},
    {generation:'b'.repeat(32),pong:{ready:true,version,generation:'a'.repeat(32)}},
    {pong:{ready:true,version}}
  ]){
    const bridge=background({collectors:true,...options});await bridge.repairCollector();
    assert.equal(bridge.collectorScripts.length,0,'matching generation, an active gesture or a legacy bundle does not churn collectors');
  }
});

test('collector repair verifies readiness after injection and leaves a busy replacement intact',async()=>{
  const generation='b'.repeat(32),version=extensionManifest.version;
  const bridge=background({collectors:true,generation,pong:{ready:true,busy:false,version,generation:'a'.repeat(32)},
    injectedPong:{ready:false,busy:true,version,generation,readinessProtocol:1}});
  assert.equal(await bridge.repairCollector(),false,'executing files is not proof the new collector is ready');
  assert.equal(await bridge.repairCollector(),false,'a repeated repair cannot interrupt the busy replacement');
  assert.equal(bridge.collectorScripts.length,1);
  assert.deepEqual(Array.from(bridge.collectorScripts[0].files),extensionManifest.content_scripts[0].js);
});

test('a matching initializing collector wakes without repeated script injection',async()=>{
  const generation='b'.repeat(32),version=extensionManifest.version;
  const bridge=background({collectors:true,generation,pong:{ready:false,busy:false,version,generation,readinessProtocol:1},
    wakePong:{ready:true,busy:false,version,generation,readinessProtocol:1}});
  assert.equal(await bridge.repairCollector(),true);assert.equal(bridge.collectorScripts.length,0);
});

test('paired backup handles host-not-found without altering the save payload', async () => {
  const bridge=background({config:{endpoint:'http://127.0.0.1:47891/mori/v1',token:'a'.repeat(64)}});
  bridge.fail('Specified native messaging host not found.');
  const reply=await bridge.request({action:'save',url:'https://example.com/original.png',name:'Backup test',source:'https://example.com/page',folder:'folder-1'});
  assert.equal(reply.ok,true);assert.equal(reply.transport,'local');
  assert.equal(bridge.fetchCalls.length,2);
  const call=bridge.fetchCalls.at(-1);
  assert.equal(call.options.credentials,'omit');assert.equal(call.options.redirect,'error');
  assert.equal(call.options.headers['X-Mori-Token'],'a'.repeat(64));
  assert.deepEqual(JSON.parse(call.options.body),JSON.parse(JSON.stringify(bridge.nativeCalls[0].message)));
});

test('backup never bypasses a permission/policy refusal or retries ambiguous saves', async () => {
  for(const error of ['Access to the specified native messaging host is forbidden.','Native messaging is disabled by system policy.','Native host has exited.','Error when communicating with the native messaging host.']){
    const bridge=background({config:{endpoint:'http://127.0.0.1:47891/mori/v1',token:'a'.repeat(64)}});bridge.fail(error);
    assert.equal((await bridge.request({action:'folders'})).ok,false);assert.equal(bridge.fetchCalls.length,0);
  }
});

test('no automatic retry on lost backup response, and pairing cannot redirect to a remote server', async () => {
  const config={endpoint:'http://127.0.0.1:47891/mori/v1',token:'a'.repeat(64)};
  const bridge=background({config,fail:true});bridge.fail('Specified native messaging host not found.');
  const reply=await bridge.request({action:'save',url:'https://example.com/original.png'});
  assert.equal(reply.code,'LOCAL_CONNECTION_UNAVAILABLE');assert.match(reply.error,/检查图库/);assert.equal(bridge.fetchCalls.length,2);
  for(const bad of [{...config,endpoint:'https://evil.example/'},{...config,token:'x'}]){const test=background({config:bad});test.fail('Specified native messaging host not found.');assert.equal((await test.request({action:'folders'})).code,'NATIVE_HOST_NOT_FOUND');assert.equal(test.fetchCalls.length,0);}
});

test('read-only connection checks do not claim a save may have occurred',async()=>{
  const bridge=background({config:{endpoint:'http://127.0.0.1:47891/mori/v1',token:'a'.repeat(64)},fail:true});bridge.fail('Specified native messaging host not found.');
  const result=await bridge.dispatch({action:'bridgeStatus'},{url:`chrome-extension://${'abcdefghijklmnopabcdefghijklmnop'}/popup.html`});
  assert.equal(result.code,'LOCAL_CONNECTION_UNAVAILABLE');assert.match(result.error,/请先打开/);assert.doesNotMatch(result.error,/收藏结果|是否已保存/);
});

test('successful native saves clear their tiny journal; failed journal write sends no native save',async()=>{
  const bridge=background();await bridge.request({action:'save',url:'https://example.com/original.png'});
  assert.equal(bridge.metadata.moriUpdatePendingWrite,false);assert.equal(bridge.metadata.moriUpdateUncertainWrite,false);
  assert.equal(bridge.storeWrites.some(x=>x.moriUpdatePendingWrite===true),true);
  const failed=background({storageFail:true});const result=await failed.request({action:'save',url:'https://example.com/original.png'});
  assert.equal(result.code,'SAVE_JOURNAL_UNAVAILABLE');assert.equal(failed.nativeCalls.length,0);assert.match(result.error,/没有发送/);
});

test('bookmark favicon comes only from same-origin browser tab metadata, not webpage messages', async () => {
  const bridge=background();
  const message={action:'save',bookmark:true,source:'https://example.com/page',iconUrl:'https://evil.example/icon'};
  await bridge.request(message,1,{tab:{id:1,url:'https://example.com/other',favIconUrl:'https://cdn.example.com/icon.png'},frameId:0});
  assert.equal(bridge.nativeCalls.at(-1).message.iconUrl,'https://cdn.example.com/icon.png');
  await bridge.request({...message,source:'https://other.example.com/'},1,{tab:{id:1,url:'https://example.com/',favIconUrl:'https://cdn.example.com/icon.png'},frameId:0});
  assert.equal(bridge.nativeCalls.at(-1).message.iconUrl,'');
  await bridge.request(message,1,{tab:{id:1,url:'https://example.com/',favIconUrl:'file:///C:/icon.ico'},frameId:0});
  assert.equal(bridge.nativeCalls.at(-1).message.iconUrl,'');assert.equal(bridge.fetchCalls.length,0);
});

test('native bridge distinguishes forbidden extension, missing host, launch, disconnect and configuration errors', async () => {
  const bridge = background();
  for (const [message, code] of [
    ['Access to the specified native messaging host is forbidden.', 'NATIVE_EXTENSION_FORBIDDEN'],
    ['Specified native messaging host not found.', 'NATIVE_HOST_NOT_FOUND'],
    ['Failed to start native messaging host.', 'NATIVE_HOST_START_FAILED'],
    ['Native host has exited.', 'NATIVE_HOST_DISCONNECTED'],
    ['Invalid native messaging host manifest.', 'NATIVE_HOST_INVALID'],
    ['Unexpected transport error', 'NATIVE_CONNECTION_ERROR']
  ]) {
    bridge.fail(message);
    const reply = await bridge.request({action: 'folders'});
    assert.equal(reply.ok, false);
    assert.equal(reply.code, code);
    assert.equal(reply.diagnostic, message);
    if (code === 'NATIVE_EXTENSION_FORBIDDEN') {
      assert.match(reply.error, /未允许.*本机程序/);
      assert.match(reply.error, /本次请求未发送/);
    }
  }
  bridge.fail(null);
  assert.equal((await bridge.request({action: 'folders'})).theme, 'dark');
  assert(bridge.nativeCalls.every(call => call.host === 'app.mori.desk'));
});

test('a native disconnect after a write reports an unknown result and never retries', async () => {
  const bridge=background({config:{endpoint:'http://127.0.0.1:47891/mori/v1',token:'a'.repeat(64)}});
  bridge.fail('Native host has exited.');
  const result=await bridge.request({action:'save',url:'https://example.com/image.png'});
  assert.equal(result.code,'NATIVE_HOST_DISCONNECTED');assert.match(result.error,/尚不能确认.*检查图库/);
  assert.equal(bridge.nativeCalls.length,1);assert.equal(bridge.fetchCalls.length,1);
});

test('save retains source URL, category, name and original bytes through the actual bridge path', async () => {
  const bridge = background();
  const reply = await bridge.request({action: 'save', url: 'https://example.com/original.png', source: 'https://example.com/article?q=one', folder: 'folder-42', name: '参考图'});
  assert.equal(reply.ok, true);
  assert.equal(bridge.fetchCalls[0].url, 'https://example.com/original.png');
  assert.equal(bridge.fetchCalls[0].options.credentials, 'include');
  const saved = bridge.nativeCalls[0].message;
  assert.equal(saved.data, 'AQID');
  assert.equal(saved.kind, 'image');
  assert.equal(saved.source, 'https://example.com/article?q=one');
  assert.equal(saved.folder, 'folder-42');
  assert.equal(saved.name, '参考图');
  assert.equal(saved.duplicateAction, 'ask');
  assert.match((await bridge.request({action: 'save', url: 'blob:https://example.com/private'})).error, /截图/);
  assert.equal(bridge.fetchCalls.length, 1, 'unsupported sources never fetch or save');
});

test('media source metadata rejects embedded credentials before fetching without leaking them, but preserves escaped links', async () => {
  const bridge=background();
  for(const source of ['https://user:PRIVATE-PASSWORD@example.com/photo','http://PRIVATE-USER@example.com/photo','file:///C:/private.png','https://example.com/\nprivate']){
    const result=await bridge.request({action:'save',url:'https://example.com/original.png',source});
    assert.equal(result.ok,false);assert.match(result.error,/不含账号密码/);assert.doesNotMatch(result.error,/PRIVATE-/);
  }
  assert.equal(bridge.fetchCalls.length,0);assert.equal(bridge.nativeCalls.length,0,'invalid metadata must not save or report success');
  const source='https://example.com/a%2Fb?q=one%20two#image%203';
  assert.equal((await bridge.request({action:'save',url:'https://example.com/original.png',source})).ok,true);
  assert.equal(bridge.nativeCalls[0].message.source,source);
});

test('duplicate choices keep original bytes in memory, require explicit choice, and do not refetch', async () => {
  const bridge = background();
  bridge.respond({ok: true, duplicate: true, choiceRequired: true, existing: {name: '已有'}, incoming: {name: '本次'}});
  const first = await bridge.request({action: 'save', url: 'https://example.com/original.png', source: 'https://example.com/page', folder: 'chosen', name: 'Original name'});
  assert(first.choiceToken);
  assert.equal(bridge.nativeCalls.length, 1);
  const stolen = await bridge.request({action: 'save', choiceToken: first.choiceToken, duplicateAction: 'keep'}, 2);
  assert.equal(stolen.ok, false, 'another tab cannot commit the pending original');
  assert.equal(bridge.nativeCalls.length, 1);
  bridge.respond({ok: true, duplicate: true, choiceRequired: false, keptCopy: true});
  const saved = await bridge.request({action: 'save', choiceToken: first.choiceToken, duplicateAction: 'keep', source: 'changed', folder: 'changed'});
  assert.equal(saved.keptCopy, true);
  assert.equal(bridge.fetchCalls.length, 1);
  assert.equal(bridge.nativeCalls[1].message.data, 'AQID');
  assert.equal(bridge.nativeCalls[1].message.source, 'https://example.com/page');
  assert.equal(bridge.nativeCalls[1].message.folder, 'chosen');
  assert.equal(bridge.nativeCalls[1].message.name, 'Original name');
  assert.equal(bridge.nativeCalls[1].message.duplicateAction, 'keep');
  assert.equal((await bridge.request({action: 'save', choiceToken: first.choiceToken, duplicateAction: 'keep'})).ok, false, 'a token cannot commit twice');
});

test('skip discards a duplicate choice without another native write', async () => {
  const bridge = background();
  bridge.respond({ok: true, duplicate: true, choiceRequired: true});
  const first = await bridge.request({action: 'save', url: 'https://example.com/original.png'});
  await bridge.request({action: 'discard', choiceToken: first.choiceToken});
  assert.equal(bridge.nativeCalls.length, 1);
  assert.equal((await bridge.request({action: 'save', choiceToken: first.choiceToken, duplicateAction: 'reuse'})).ok, false);
  assert.equal(bridge.nativeCalls.length, 1);
});

test('disconnecting the duplicate heartbeat cannot consume the choice before its save arrives',async()=>{
  const bridge=background();bridge.respond({ok:true,choiceRequired:true});
  const first=await bridge.request({action:'save',url:'https://example.com/original.png'});let disconnect;
  bridge.connect({name:'mori-duplicate-choice',sender:{tab:{id:1},frameId:0},onMessage:{addListener(){}},onDisconnect:{addListener:fn=>disconnect=fn},postMessage(){}});
  disconnect();bridge.respond({ok:true,duplicate:true});
  const result=await bridge.request({action:'save',choiceToken:first.choiceToken,duplicateAction:'reuse'});
  assert.equal(result.ok,true);assert.equal(bridge.nativeCalls.length,2);assert.equal(bridge.fetchCalls.length,1);
});

test('another frame cannot cancel a duplicate and double-confirm consumes its token only once',async()=>{
  const bridge=background();bridge.respond({ok:true,choiceRequired:true});
  const first=await bridge.request({action:'save',url:'https://example.com/original.png'});
  await bridge.request({action:'discard',choiceToken:first.choiceToken},1,{tab:{id:1},frameId:7});
  bridge.respond({ok:true,duplicate:true});
  const results=await Promise.all([bridge.request({action:'save',choiceToken:first.choiceToken,duplicateAction:'keep'}),bridge.request({action:'save',choiceToken:first.choiceToken,duplicateAction:'keep'})]);
  assert.equal(results.filter(result=>result.ok).length,1);assert.equal(bridge.nativeCalls.length,2);assert.equal(bridge.fetchCalls.length,1);
});

test('pending duplicate originals stay bounded and an evicted choice cannot write',async()=>{
  const bridge=background();bridge.respond({ok:true,choiceRequired:true});const choices=[];
  for(let at=0;at<4;at++)choices.push(await bridge.request({action:'save',url:`https://example.com/${at}.png`}));
  bridge.respond({ok:true,duplicate:true});assert.equal((await bridge.request({action:'save',choiceToken:choices[0].choiceToken,duplicateAction:'keep'})).ok,false);
  assert.equal(bridge.nativeCalls.length,4);assert.equal((await bridge.request({action:'save',choiceToken:choices[3].choiceToken,duplicateAction:'reuse'})).ok,true);
  assert.equal(bridge.nativeCalls.length,5);assert.equal(bridge.fetchCalls.length,4);
});

test('unknown generic native errors and malformed native replies remain uncertain and are never replayed',async()=>{
  for(const mode of ['generic','malformed']){
    const bridge=background({config:{endpoint:'http://127.0.0.1:47891/mori/v1',token:'a'.repeat(64)}});
    if(mode==='generic')bridge.fail('Unexpected transport error');else bridge.respond({});
    const result=await bridge.request({action:'save',url:'https://example.com/image.png'});
    assert.equal(result.code,mode==='generic'?'NATIVE_CONNECTION_ERROR':'NATIVE_EMPTY_REPLY');assert.match(result.error,/检查图库/);
    assert.equal(bridge.nativeCalls.length,1);assert.equal(bridge.fetchCalls.length,1);assert.equal(bridge.metadata.moriUpdateUncertainWrite,true);assert.equal(bridge.metadata.moriUpdatePendingWrite,false);
  }
});

test('health and update controls are accepted only from the exact extension popup',async()=>{
  const bridge=background(),popup=`chrome-extension://${'abcdefghijklmnopabcdefghijklmnop'}/popup.html`;
  for(const sender of [{tab:{id:1},url:popup},{url:popup+'?pretend'},{url:'https://example.com/'}]){
    for(const action of ['bridgeStatus','extensionUpdateStatus','extensionUpdateCheck','extensionUpdateAcknowledge'])assert.equal((await bridge.dispatch({action},sender)).ok,false);
  }
  assert.equal(bridge.nativeCalls.length,0);
  assert.equal((await bridge.dispatch({action:'extensionUpdateStatus'},{url:popup})).ok,true);
  assert.equal((await bridge.dispatch({action:'bridgeStatus'},{url:popup})).ok,true);
  assert.equal(bridge.nativeCalls.length,1);assert.equal(bridge.nativeCalls[0].message.action,'bridgeStatus');
});

test('context menu survives worker activation, extension reload/update and browser startup without duplicates', async () => {
  const bridge = background();
  await bridge.install(); await bridge.startup(); await bridge.install();
  assert.equal(bridge.menus.size, 2);
  const menu = bridge.menus.get('mori-save');
  assert.equal(menu.title, '收藏到 Nook');
  assert.deepEqual(Array.from(menu.contexts), ['image', 'video', 'link', 'page']);
  assert.equal(bridge.nativeCalls.length, 0, 'registration does not access the library');
});

test('right-click collection preserves image/source and sends progress/result only to the top frame', async () => {
  const bridge = background(); bridge.respond({ok: true});
  await bridge.click({menuItemId: 'other', srcUrl: 'https://example.com/no.png'});
  assert.equal(bridge.nativeCalls.length, 0);
  await bridge.click({menuItemId: 'mori-save', srcUrl: 'https://example.com/original.png', pageUrl: 'https://example.com/article', frameId: 8, frameUrl: 'https://example.com/embed'});
  const saved = bridge.nativeCalls[0].message;
  assert.equal(saved.data, 'AQID'); assert.equal(saved.source, 'https://example.com/article');
  assert.equal(saved.name, '网页标题'); assert.equal(saved.folder, ''); assert.equal(saved.duplicateAction, 'ask');
  const alias=bridge.tabMessages.find(call=>call.message.action==='resolve-context-native-image');
  assert.equal(alias.options.frameId,8);assert.equal(alias.message.expectedUrl,'https://example.com/original.png');
  const feedback=bridge.tabMessages.filter(call=>['context-saving','result'].includes(call.message.action));
  assert.deepEqual(feedback.map(call=>call.message.action),['context-saving','result']);
  assert.equal(feedback[0].message.requestId,feedback[1].message.requestId);
  assert(feedback.every(call => call.id === 1 && call.options.frameId === 0));
  assert.equal(bridge.badges.at(-1).text, '✓');
  assert.match(bridge.titles.at(-1).title, /已收藏/);
});

test('right-click duplicate is explicit and can reuse original without another download', async () => {
  const bridge = background(); bridge.respond({ok: true, duplicate: true, choiceRequired: true});
  await bridge.click({menuItemId: 'mori-save', srcUrl: 'https://example.com/original.png'});
  const result = bridge.tabMessages.at(-1).message.result;
  assert(result.choiceToken); assert.equal(bridge.nativeCalls.length, 1);
  assert.equal(bridge.nativeCalls[0].message.source, 'https://example.com/page');
  bridge.respond({ok: true, duplicate: true});
  assert.equal((await bridge.request({action: 'save', choiceToken: result.choiceToken, duplicateAction: 'reuse'})).ok, true);
  assert.equal(bridge.fetchCalls.length, 1);
  assert.equal(bridge.nativeCalls.at(-1).message.duplicateAction, 'reuse');
});

test('restricted/missing page receiver retains success/error feedback and releases unseen duplicate choices', async () => {
  const bridge = background(); bridge.receive(false); bridge.respond({ok: true});
  await bridge.click({menuItemId: 'mori-save', srcUrl: 'https://example.com/original.png'});
  assert.equal(bridge.badges.at(-1).text, '✓');
  bridge.respond({ok: true, duplicate: true, choiceRequired: true});
  await bridge.click({menuItemId: 'mori-save', srcUrl: 'https://example.com/original.png'});
  const token = bridge.tabMessages.at(-1).message.result.choiceToken;
  assert.match(bridge.titles.at(-1).title, /未重复导入.*刷新/);
  assert.equal((await bridge.request({action: 'save', choiceToken: token, duplicateAction: 'keep'})).ok, false);
  assert.equal(bridge.nativeCalls.length, 2);
  await bridge.click({menuItemId: 'mori-save', srcUrl: 'blob:https://example.com/private'});
  assert.equal(bridge.badges.at(-1).text, '!'); assert.match(bridge.titles.at(-1).title, /截图/);
  assert.equal(bridge.fetchCalls.length, 2);
  await bridge.click({menuItemId: 'mori-save'}, {});
  assert.equal(bridge.fetchCalls.length, 2);
});

test('link-covered image resolves from the clicked frame, saves original URL and keeps feedback in the top frame', async () => {
  const bridge = background(); bridge.respond({ok: true});
  bridge.resolveImage({ok: true, item: {url: 'https://example.com/robot.webp', name: '机器人原图'}});
  await bridge.click({menuItemId: 'mori-save', linkUrl: 'https://example.com/product.html', pageUrl: 'https://example.com/robots', frameId: 7});
  assert.equal(bridge.fetchCalls.length, 1);
  assert.equal(bridge.fetchCalls[0].url, 'https://example.com/robot.webp');
  assert.equal(bridge.nativeCalls[0].message.source, 'https://example.com/robots');
  assert.equal(bridge.nativeCalls[0].message.name, '机器人原图');
  assert.equal(bridge.tabMessages[0].message.action, 'resolve-context-image');
  assert.equal(bridge.tabMessages[0].options.frameId, 7);
  assert(bridge.tabMessages.slice(1).every(call => call.options.frameId === 0));
  assert.equal(bridge.badges.at(-1).text, '✓');
});

test('plain links, empty backgrounds and unavailable content scripts never download a page or save an unrelated image', async () => {
  const bridge = background();
  for (const info of [{linkUrl: 'https://example.com/product.html'}, {}]) {
    await bridge.click({menuItemId: 'mori-save', ...info});
    assert.equal(bridge.fetchCalls.length, 0); assert.equal(bridge.nativeCalls.length, 0);
    assert.match(bridge.titles.at(-1).title, /没有找到/);
  }
  bridge.receive(false);
  await bridge.click({menuItemId: 'mori-save', linkUrl: 'https://example.com/product.html'});
  assert.equal(bridge.fetchCalls.length, 0); assert.equal(bridge.nativeCalls.length, 0);
  assert.match(bridge.titles.at(-1).title, /重新加载.*刷新/);
});

test('native image URL takes precedence over DOM lookup and an unsafe DOM source never fetches', async () => {
  const bridge = background(); bridge.respond({ok: true});
  bridge.resolveImage({ok: true, item: {url: 'javascript:alert(1)'}});
  await bridge.click({menuItemId: 'mori-save', srcUrl: 'https://example.com/image.png', linkUrl: 'https://example.com/product'});
  assert(!bridge.tabMessages.some(call => call.message.action === 'resolve-context-image'));
  assert.equal(bridge.fetchCalls[0].url, 'https://example.com/image.png');
  await bridge.click({menuItemId: 'mori-save', linkUrl: 'https://example.com/product'});
  assert.equal(bridge.fetchCalls.length, 1); assert.equal(bridge.nativeCalls.length, 1);
  assert.equal(bridge.badges.at(-1).text, '!');
});

test('right-click blob video resolves in the original frame and stores the actual media with the selected Reel source', async () => {
  const bridge=background();bridge.respond({ok:true});
  const mediaPage='https://www.instagram.com/reel/SyntheticClip1/';
  const url='https://video.cdninstagram.com/full.mp4';
  const blob='blob:https://www.instagram.com/fixture';
  bridge.resolveImage({ok:true,item:{kind:'video',url,mediaPage,name:'Chosen video'}});
  await bridge.click({menuItemId:'mori-save',srcUrl:blob,frameId:7,pageUrl:'https://www.instagram.com/'},{id:1,url:'https://www.instagram.com/',title:'Feed'});
  const lookup=bridge.tabMessages.find(call=>call.message.action==='resolve-context-image');
  assert.equal(lookup.options.frameId,7);assert.equal(lookup.message.expectedUrl,blob);
  assert.equal(bridge.fetchCalls.length,1);assert.equal(bridge.fetchCalls[0].url,url);
  assert.equal(bridge.nativeCalls.length,1);assert.equal(bridge.nativeCalls[0].message.source,mediaPage);
  assert.notEqual(bridge.nativeCalls[0].message.action,'bookmark');
  assert.equal(bridge.nativeCalls[0].message.kind,'video');
  assert.equal(bridge.nativeCalls[0].message.data,'AQID');
});

test('video intent survives local fallback and duplicate choices cannot downgrade it to an image', async () => {
  const config={endpoint:'http://127.0.0.1:47891/mori/v1',token:'a'.repeat(64)};
  const bridge=background({config,reply:{ok:true,choiceRequired:true}});
  bridge.fail('Specified native messaging host not found.');
  const first=await bridge.request({action:'save',kind:'video',url:'https://example.com/clip.mp4',source:'https://example.com/reel'});
  assert.equal(first.ok,true);assert(first.choiceToken);
  const original=JSON.parse(bridge.fetchCalls.at(-1).options.body);
  assert.equal(original.kind,'video');assert.equal(bridge.nativeCalls[0].message.kind,'video');
  bridge.fail(null);bridge.respond({ok:true,duplicate:true});
  const second=await bridge.request({action:'save',choiceToken:first.choiceToken,duplicateAction:'reuse',kind:'image'});
  assert.equal(second.ok,true);assert.equal(bridge.nativeCalls.at(-1).message.kind,'video');
  assert.equal(bridge.nativeCalls.at(-1).message.data,original.data);
  assert.equal(bridge.fetchCalls.length,2,'a duplicate choice never fetches or substitutes another media item');
});

test('a receiver media-kind rejection is shown as failure without fallback, retry, or success badge', async () => {
  const bridge=background({config:{endpoint:'http://127.0.0.1:47891/mori/v1',token:'a'.repeat(64)}});
  bridge.respond({ok:false,error:'网页返回了图片而不是可保存的视频，本次没有收藏，也没有改存为封面。'});
  await bridge.click({menuItemId:'mori-save',mediaType:'video',srcUrl:'https://example.com/looks-like-video.mp4'});
  assert.equal(bridge.nativeCalls.length,1);assert.equal(bridge.nativeCalls[0].message.kind,'video');
  assert.equal(bridge.fetchCalls.length,1);assert.equal(bridge.badges.at(-1).text,'!');
  assert.equal(bridge.tabMessages.at(-1).message.result.ok,false);
  assert.match(bridge.titles.at(-1).title,/没有收藏/);
});

test('failed or stale right-click blob resolution never fetches a blob, poster or bookmark', async () => {
  for(const error of ['视频目标已变化','分段视频，未收藏']) {
    const bridge=background();bridge.resolveImage({ok:false,error});
    await bridge.click({menuItemId:'mori-save',srcUrl:'blob:https://www.instagram.com/fixture'});
    assert.equal(bridge.fetchCalls.length,0);assert.equal(bridge.nativeCalls.length,0);
    assert.equal(bridge.badges.at(-1).text,'!');assert.equal(bridge.titles.at(-1).title,'Nook · '+error);
  }
});

test('right-click video failure preserves its media type when the resolver could not return a video item', async () => {
  for(const useMediaType of [true,false]) {
    const bridge=background();bridge.resolveImage({ok:false,kind:'video',error:'未能确定视频（IG13-TARGET）'});
    await bridge.click({menuItemId:'mori-save',srcUrl:'blob:https://www.instagram.com/fixture',...(useMediaType?{mediaType:'video'}:{})});
    assert.equal(bridge.tabMessages.find(call=>call.message.action==='context-saving').message.kind,'video');
    assert.equal(bridge.fetchCalls.length,0);assert.equal(bridge.nativeCalls.length,0);assert(!bridge.titles.at(-1).title.includes('截图'));
  }
});

test('unavailable video receiver and unsupported video URL do not suggest screenshot substitution', async () => {
  const bridge=background();bridge.receive(false);
  await bridge.click({menuItemId:'mori-save',mediaType:'video',srcUrl:'blob:https://www.instagram.com/fixture'});
  assert(!bridge.titles.at(-1).title.includes('截图'));
  const reply=await bridge.request({action:'save',kind:'video',url:'blob:https://www.instagram.com/fixture'});
  assert.equal(reply.ok,false);assert(!reply.error.includes('截图'));assert.equal(bridge.nativeCalls.length,0);
});

test('extension manifest retains repair and idle update permissions and sources loaded before content', () => {
  const manifest = JSON.parse(read('manifest.json'));
  assert.equal(manifest.manifest_version,3);assert.match(manifest.version,/^\d+\.\d+\.\d+(?:\.\d+)?$/);
  assert.deepEqual(manifest.permissions, ['nativeMessaging', 'contextMenus', 'storage', 'scripting', 'alarms']);
  assert.deepEqual(manifest.host_permissions, ['https://*/*', 'http://*/*']);
  assert.deepEqual(manifest.content_scripts[0].js, ['preferences.js', 'collector-text.js', 'drag-geometry.js', 'media-source.js', 'runtime-client.js', 'content.js']);
  assert.equal(manifest.content_scripts[0].all_frames, true);
});

test('preferences handshake uses top-level tab URL even for an embedded frame',async()=>{
  const bridge=background();bridge.preferences({disabledHosts:['example.com']});
  const result=await bridge.request({action:'preferences'},1,{tab:{id:1,url:'https://example.com/page'},frameId:7,url:'https://ads.invalid/embed'});
  assert.equal(result.pageUrl,'https://example.com/page');assert.equal(result.preferences.disabledHosts[0],'example.com');assert.equal(bridge.nativeCalls.length,0);
});
test('bookmark right-click confirms the target and save never downloads a webpage',async()=>{
  const bridge=background();await bridge.click({menuItemId:'mori-bookmark',linkUrl:'https://example.org/link',pageUrl:'https://example.com/page'});
  assert.equal(bridge.fetchCalls.length,0);assert.equal(bridge.nativeCalls.length,0);const item=bridge.tabMessages.at(-1).message.item;assert.equal(item.bookmark,true);assert.equal(item.source,'https://example.org/link');
  bridge.respond({ok:true});await bridge.request({...item,action:'save',folder:'one'});assert.equal(bridge.nativeCalls.at(-1).message.action,'bookmark');assert.equal(bridge.nativeCalls.at(-1).message.folder,'one');assert.equal(bridge.fetchCalls.length,0);
  const before=bridge.nativeCalls.length;const rejected=await bridge.request({action:'save',bookmark:true,source:'javascript:alert(1)'});assert.equal(rejected.ok,false);assert.equal(bridge.nativeCalls.length,before);
});
test('bulk browser import is restricted to extension popup rather than websites',async()=>{
  const bridge=background();const payload={action:'importBookmarks',items:[{url:'https://example.com/',path:[]}]};assert.equal((await bridge.request(payload)).ok,false);assert.equal(bridge.nativeCalls.length,0);
  await bridge.request(payload,1,{url:'chrome-extension://abcdefghijklmnopabcdefghijklmnop/popup.html'});assert.equal(bridge.nativeCalls.at(-1).message.action,'importBookmarks');assert.equal(bridge.fetchCalls.length,0);
});
test('right-click edit waits for confirmation and never silently saves when editor cannot display',async()=>{
  const bridge=background();bridge.preferences({editBeforeSave:true});
  await bridge.click({menuItemId:'mori-save',srcUrl:'https://example.com/image.png'});
  assert.equal(bridge.nativeCalls.length,0);assert.equal(bridge.fetchCalls.length,0);
  const edit=bridge.tabMessages.find(call=>call.message.action==='context-edit');
  assert.equal(edit.message.item.source,'https://example.com/page');assert.equal(edit.options.frameId,0);
  assert(!bridge.tabMessages.some(call=>call.message.action==='context-saving'));
  bridge.receive(false);await bridge.click({menuItemId:'mori-save',srcUrl:'https://example.com/image.png'});
  assert.equal(bridge.fetchCalls.length,0);assert.equal(bridge.badges.at(-1).text,'!');assert.match(bridge.titles.at(-1).title,/未保存/);
});
