importScripts('preferences.js');
importScripts('local-bridge.js');
importScripts('updater.js');
const HOST = 'app.mori.desk';
const MAX = 20 * 1024 * 1024;
// Pending duplicate choices live only in this worker's memory. Never persist
// downloaded image bytes, URLs, or choice tokens to extension storage.
const pendingSaves = new Map();
const CHOICE_TTL = 5 * 60 * 1000;
const ownerOf = sender => `${sender?.tab?.id ?? 'none'}:${sender?.frameId ?? 0}`;
const updater = MoriUpdater.create({
  chrome, uuid:()=>crypto.randomUUID(),
  configured:()=>MoriLocalBridge.configured(),
  identity:()=>({installationId:self.MoriLocalConnection?.installationId,extensionId:chrome.runtime.id,version:chrome.runtime.getManifest().version}),
  request:message=>MoriLocalBridge.request(message),
  extraBusy:()=>{
    for(const [key,value] of pendingSaves)if(Date.now()-value.created>CHOICE_TTL)pendingSaves.delete(key);
    return pendingSaves.size>0 || contextRequests.size>0;
  }
});
function activity(task) {
  const finish=updater.begin();
  return finish ? Promise.resolve().then(task).finally(finish) : Promise.resolve(updater.blocked());
}
const fromPopup = sender => !sender?.tab && sender?.url === `chrome-extension://${chrome.runtime.id}/popup.html`;

// Existing pages do not automatically receive new declarative content scripts
// when an unpacked extension is reloaded. Restore only the missing collectors;
// content.js disposes its old listeners before a replacement becomes active.
const collectorRepairs = new Map();
async function ensureCollector(tabId, frameId = null) {
  if(updater.isFrozen())return false;
  if (!Number.isInteger(tabId) || !chrome.scripting?.executeScript) return false;
  const key = `${tabId}:${frameId ?? 'all'}`;
  if (collectorRepairs.has(key)) return collectorRepairs.get(key);
  const repair = (async () => {
    try {
      const pong = await chrome.tabs.sendMessage(tabId, {action:'mori-collector-ping'}, {frameId:frameId ?? 0});
      if (pong?.ready && pong.version === chrome.runtime.getManifest().version) return true;
    } catch {}
    try {
      const files = chrome.runtime.getManifest().content_scripts[0].js;
      const results = await chrome.scripting.executeScript({target:frameId === null ? {tabId,allFrames:true} : {tabId,frameIds:[frameId]}, files});
      return results?.length > 0;
    } catch {return false;} // Restricted pages and closed tabs are not errors.
  })().finally(() => collectorRepairs.delete(key));
  collectorRepairs.set(key, repair);
  return repair;
}
async function repairOpenCollectors() {
  try {
    const tabs = await chrome.tabs.query({url:['https://*/*','http://*/*']});
    // Bound simultaneous injections when a browser restores many open tabs.
    for (let at = 0; at < tabs.length; at += 4) {
      await Promise.all(tabs.slice(at, at + 4).map(tab => ensureCollector(tab.id)));
    }
  } catch {}
}

function discardChoice(token, owner) {
  if (pendingSaves.get(token)?.owner === owner) pendingSaves.delete(token);
  updater.schedule();
  return {ok: true, cancelled: true};
}

async function submitImage(payload, owner) {
  const reply = await native(payload);
  if (reply.ok && reply.choiceRequired) {
    for (const [key, value] of pendingSaves) if (Date.now() - value.created > CHOICE_TTL) pendingSaves.delete(key);
    // At most three 20 MB originals may wait for a choice in this worker.
    if (pendingSaves.size >= 3) pendingSaves.delete(pendingSaves.keys().next().value);
    const choiceToken = crypto.randomUUID();
    pendingSaves.set(choiceToken, {payload, owner, created: Date.now()});
    return {...reply, choiceToken};
  }
  return reply;
}
function nativeFailure(error) {
  const diagnostic = String(error?.message || error || 'Unknown native messaging error').slice(0, 500);
  const lower = diagnostic.toLowerCase();
  let code = 'NATIVE_CONNECTION_ERROR';
  let message = 'Mori 本机连接失败，请检查连接注册后重试。';
  if (lower.includes('forbidden') || lower.includes('not allowed') || lower.includes('permission')) {
    code = 'NATIVE_EXTENSION_FORBIDDEN';
    message = `本机连接未授权当前扩展 ID（${chrome.runtime.id}）。请用此 ID 重新运行 Register-Extension.ps1，再刷新网页。`;
  } else if (lower.includes('not found') || lower.includes('not registered')) {
    code = 'NATIVE_HOST_NOT_FOUND';
    message = '找不到 Mori 本机连接。请打开 Mori Desk，在设置 → 浏览器收藏中连接当前插件 ID，再重新加载插件。';
  } else if (lower.includes('failed to start') || lower.includes('cannot start')) {
    code = 'NATIVE_HOST_START_FAILED';
    message = 'Mori 本机程序未能启动。请确认 Mori.exe 仍在注册时的位置且可打开，再重新注册连接。';
  } else if (lower.includes('exited') || lower.includes('communicating') || lower.includes('disconnected')) {
    code = 'NATIVE_HOST_DISCONNECTED';
    message = 'Mori 本机连接意外中断。请确认桌面程序可以打开，再重试；若移动过程序，请重新注册连接。';
  } else if (lower.includes('invalid') || lower.includes('manifest')) {
    code = 'NATIVE_HOST_INVALID';
    message = 'Mori 本机连接配置无效。请在桌面程序所在目录重新运行 Register-Extension.ps1。';
  }
  return {ok: false, code, error: message, diagnostic};
}
async function native(message) {
  const uncertain = ['save','bookmark','importBookmarks'].includes(message.action);
  let finishWrite, result;
  if(uncertain) {
    try {finishWrite=await updater.beforeWrite();}
    catch {return{ok:false,code:'SAVE_JOURNAL_UNAVAILABLE',error:'无法安全记录此次收藏，请重启浏览器后重试。本次没有发送保存请求。'};}
  }
  try {result=await new Promise(resolve => {
    let finished = false;
    const finish = reply => {if (!finished) {finished = true; clearTimeout(timer); resolve(reply);}};
    const timer = setTimeout(() => finish({ok:false,code:'NATIVE_TIMEOUT',error:uncertain
      ? '等待 Mori 收藏结果超时，请先检查图库是否已保存，再重试。'
      : 'Mori 本机连接响应超时，请打开桌面程序后重试。'}),30000);
    try {
      chrome.runtime.sendNativeMessage(HOST, message, reply => {
        const error = chrome.runtime.lastError;
        const result = error ? nativeFailure(error) : reply && typeof reply.ok === 'boolean' ? reply : {ok:false,code:'NATIVE_EMPTY_REPLY',error:uncertain
          ? '未收到 Mori 的收藏结果，请先检查图库后再重试。'
          : 'Mori 本机程序未返回结果，请重试。'};
        finish(result);
      });
    } catch (error) {finish(nativeFailure(error));}
  }).then(async reply => {
    // Only retry definite failures before a request reached the host. Never
    // bypass a policy/permission refusal or repeat an ambiguous write/disconnect.
    if (['NATIVE_HOST_NOT_FOUND','NATIVE_HOST_START_FAILED','NATIVE_HOST_INVALID'].includes(reply.code)) {
      const fallback = await MoriLocalBridge.request(message);
      if (fallback) reply={...fallback, nativeDiagnostic:reply.diagnostic};
    }
    return reply;
  });}
  catch(error) {result=nativeFailure(error);}
  finally {if(finishWrite)await finishWrite(result);}
  if (uncertain && ['NATIVE_HOST_DISCONNECTED','NATIVE_CONNECTION_ERROR'].includes(result.code)) result.error = 'Mori 连接中断，尚不能确认是否已保存。请先检查图库，再重试。';
  return result;
}
function bookmarkIcon(message, sender) {
  try {
    if(!message.bookmark || new URL(message.source).origin !== new URL(sender.tab?.url).origin)return '';
    const icon=new URL(sender.tab?.favIconUrl);
    return ['http:','https:'].includes(icon.protocol)&&!icon.username&&!icon.password&&icon.href.length<=2048?icon.href:'';
  }catch{return '';}
}
async function saveImage(message, owner, iconUrl = '') {
  if (message.choiceToken) {
    const pending = pendingSaves.get(message.choiceToken);
    if (!pending || pending.owner !== owner || Date.now() - pending.created > CHOICE_TTL) {
      if (pending?.owner === owner) pendingSaves.delete(message.choiceToken);
      return {ok: false, error: '这次重复图片选择已过期，请重新收藏图片再选择。'};
    }
    if (!['reuse', 'keep'].includes(message.duplicateAction)) return {ok: false, error: '请选择使用已有图片或仍然导入。'};
    pendingSaves.delete(message.choiceToken);
    return await submitImage({...pending.payload, duplicateAction: message.duplicateAction}, owner);
  }
  if (message.bookmark) {
    let address;
    try {address = new URL(message.source);} catch {return {ok:false,error:'网址无效'};}
    if (!['https:','http:'].includes(address.protocol) || address.username || address.password || /[\r\n]/.test(message.source) || message.source.length > 8000) return {ok:false,error:'只支持不含账号密码的 HTTP / HTTPS 网址'};
    return submitImage({action:'bookmark',source:address.href,name:message.name || address.hostname,folder:message.folder || '',iconUrl,duplicateAction:'ask'}, owner);
  }
  let url;
  try { url = new URL(message.url); } catch { return {ok: false, error: '图片地址无效'}; }
  if (!['https:', 'http:', 'data:'].includes(url.protocol) || url.username || url.password || (url.protocol === 'data:' && !/^data:(?:image|video)\//i.test(url.href))) return {ok: false, error: message.kind === 'video' ? '尚未获得可直接保存的完整视频地址，本次未收藏。' : '此素材没有可直接下载的图片或视频地址；流媒体和 blob 暂不支持。截图收藏只会保留静态画面。'};
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 20000);
  try {
    const response = await fetch(url.href, {credentials: 'include', signal: controller.signal});
    if (!response.ok) throw new Error(`网站拒绝下载（HTTP ${response.status}）`);
    if (Number(response.headers.get('content-length')) > MAX) throw new Error('素材超过 20 MB，请保存原文件后拖入 Mori');
    const reader = response.body.getReader();
    const chunks = []; let size = 0;
    while (true) { const {value, done} = await reader.read(); if (done) break; size += value.length; if (size > MAX) { await reader.cancel(); throw new Error('素材超过 20 MB，请保存原文件后拖入 Mori'); } chunks.push(value); }
    const bytes = new Uint8Array(size); let at = 0; for (const chunk of chunks) { bytes.set(chunk, at); at += chunk.length; }
    let binary = ''; for (let i = 0; i < bytes.length; i += 16384) binary += String.fromCharCode(...bytes.subarray(i, i + 16384));
    return await submitImage({action: 'save', data: btoa(binary), folder: message.folder || '', name: message.name || '网页收藏', source: message.source || '', duplicateAction: 'ask'}, owner);
  } catch (error) { return {ok: false, error: (error.name === 'AbortError' ? '下载超时' : error.message) + (message.kind === 'video' ? '。视频未收藏，也没有改存为网址或封面。' : '。可保存原文件后拖入 Mori；截图仅保留静态画面。')}; }
  finally { clearTimeout(timeout); }
}
chrome.runtime.onMessage.addListener((message, sender, reply) => {
  if(['extensionUpdateStatus','extensionUpdateCheck','extensionUpdateAcknowledge'].includes(message?.action)) {
    if(!fromPopup(sender)){reply({ok:false});return;}
    if(message.action==='extensionUpdateStatus')reply(updater.status());
    else (message.action==='extensionUpdateAcknowledge'?updater.acknowledge():updater.poll()).then(reply);
    return true;
  }
  if(message?.action==='bridgeStatus') {if(!fromPopup(sender)){reply({ok:false});return;}native({action:'bridgeStatus'}).then(reply).catch(()=>reply({ok:false}));return true;}
  if(message?.action==='collector-idle') {updater.schedule();reply({ok:true});return;}
  if(message?.action==='importBookmarks') {
    // Only the extension popup, never website content scripts, may bulk import.
    if(sender.tab || !String(sender.url || '').startsWith(`chrome-extension://${chrome.runtime.id}/popup.html`)){reply({ok:false,error:'只能从 Mori 插件窗口导入书签'});return true;}
    activity(()=>native(message)).then(reply).catch(()=>reply({ok:false,error:'书签导入连接中断'}));return true;
  }
  if (!message || !['folders', 'save', 'discard', 'preferences'].includes(message.action)) return;
  if (message.action === 'preferences') {
    MoriPreferences.read().then(preferences => reply({ok: true, preferences, pageUrl: sender.tab?.url || sender.url || '',updateLocked:updater.isFrozen()})).catch(() => reply({ok: false}));
    return true;
  }
  const owner = ownerOf(sender);
  if (message.action === 'discard') {reply(discardChoice(message.choiceToken, owner)); return;}
    (message.action === 'folders' ? native({action: 'folders',scope:message.scope==='bookmark'?'bookmark':'media'}) : activity(()=>saveImage(message, owner, bookmarkIcon(message,sender)))).then(reply).catch(() => reply({ok: false, error: '收藏失败，请重试'}));
  return true;
});
// MV3 workers can sleep between messages. While a choice is visible, a content
// script sends a small heartbeat so its in-memory original survives deliberation.
chrome.runtime.onConnect.addListener(port => {
  if(port.name==='mori-popup' && fromPopup(port.sender)){updater.popup(port);return;}
  if (port.name !== 'mori-duplicate-choice') return;
  const owner = ownerOf(port.sender);
  port.onMessage.addListener(message => {
    const pending = pendingSaves.get(message?.choiceToken);
    if (!pending || pending.owner !== owner || Date.now() - pending.created > CHOICE_TTL) {
      if (pending?.owner === owner) pendingSaves.delete(message.choiceToken);
      port.postMessage({expired: true});
    }
  });
  // The content script disconnects its heartbeat just before submitting a
  // duplicate choice. Deleting here races that submission. Explicit discard
  // and the bounded TTL own cleanup; disconnect alone is not cancellation.
  port.onDisconnect?.addListener(()=>updater.schedule());
});
// Context menus persist when MV3 sleeps. Update before creating so install,
// browser startup and unpacked-extension reload never leave duplicate entries.
let menuRegistration;
function ensureContextMenu() {
  if (menuRegistration) return menuRegistration;
  menuRegistration = new Promise(resolve => {
    // Many sites put a link layer over an image or render it as a CSS background.
    // Keep the entry available there; resolve only the image at the clicked point.
    const properties = {title: '收藏到 Mori', contexts: ['image', 'video', 'link', 'page']};
    chrome.contextMenus.update('mori-save', properties, () => {
      if (!chrome.runtime.lastError) {resolve(); return;}
      chrome.contextMenus.create({id: 'mori-save', ...properties}, () => {
        const error = chrome.runtime.lastError;
        if (error) console.warn('Mori context menu:', error.message);
        resolve();
      });
    });
  }).then(() => new Promise(resolve => {
    const properties={title:'收藏网址到 Mori',contexts:['page','link']};
    chrome.contextMenus.update('mori-bookmark',properties,()=>{
      if (!chrome.runtime.lastError) {resolve();return;}
      chrome.contextMenus.create({id:'mori-bookmark',...properties},()=>{void chrome.runtime.lastError;resolve();});
    });
  })).finally(() => {menuRegistration = null;});
  return menuRegistration;
}
chrome.runtime.onInstalled.addListener(() => Promise.all([ensureContextMenu(), repairOpenCollectors()]));
chrome.runtime.onStartup.addListener(() => Promise.all([ensureContextMenu(), repairOpenCollectors()]));
chrome.tabs.onActivated?.addListener(({tabId}) => {void ensureCollector(tabId);});
ensureContextMenu();
async function updateMenuLanguage() {
  try {
    const prefs = await MoriPreferences.read();
    await ensureContextMenu();
    chrome.contextMenus.update('mori-save', {title: MoriPreferences.language(prefs) === 'en' ? 'Save to Mori' : '收藏到 Mori'}, () => {void chrome.runtime.lastError;});
    chrome.contextMenus.update('mori-bookmark', {title: MoriPreferences.language(prefs) === 'en' ? 'Bookmark in Mori' : '收藏网址到 Mori'}, () => {void chrome.runtime.lastError;});
  } catch {}
}
chrome.storage.onChanged.addListener((changes, area) => {if (area === 'local' && changes[MoriPreferences.key]) updateMenuLanguage();});
updateMenuLanguage();

async function contextFeedback(tabId, message) {
  await ensureCollector(tabId, 0);
  try {return await chrome.tabs.sendMessage(tabId, message, {frameId: 0});}
  catch {return null;}
}
async function contextBadge(tabId, text, title) {
  // Also gives feedback on pages where content scripts cannot run. No extra
  // notification/storage permissions and no saved image data in browser storage.
  await Promise.all([
    chrome.action.setBadgeText({tabId, text}),
    chrome.action.setBadgeBackgroundColor({tabId, color: text === '!' ? '#9b463a' : '#44724d'}),
    chrome.action.setTitle({tabId, title: `Mori · ${title}`})
  ]).catch(() => {}); // The tab may have closed while downloading.
}
const contextRequests = new Map();
chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  if (!['mori-save','mori-bookmark'].includes(info.menuItemId) || tab?.id == null) return;
  const finishActivity=updater.begin();
  if(!finishActivity){await contextBadge(tab.id,'!',updater.blocked().error);return;}
  const requestId = crypto.randomUUID();
  contextRequests.set(tab.id, requestId);
  const owner = ownerOf({tab, frameId: 0});
  try {
    if (info.menuItemId === 'mori-bookmark') {
      const address=info.linkUrl || info.pageUrl || tab.url || '';
      let parsed;try{parsed=new URL(address);}catch{}
      if(!parsed || !['https:','http:'].includes(parsed.protocol) || parsed.username || parsed.password){await contextBadge(tab.id,'!','只支持 HTTP / HTTPS 网址');return;}
      // Always confirm link/title/category so a right-click link is never confused
      // with the current page. No network fetch or native save before confirmation.
      const item={bookmark:true,url:address,source:address,name:info.linkUrl?parsed.hostname:tab.title || parsed.hostname};
      const feedback=await contextFeedback(tab.id,{action:'context-edit',requestId,item});
      await contextBadge(tab.id,feedback?.displayed?'':'!',feedback?.displayed?'请确认网址收藏':'无法显示确认窗口。请复制网址到 Mori 中粘贴收藏。');return;
    }
    let kind = info.mediaType === 'video' ? 'video' : 'image';
    let item = info.srcUrl ? {url: info.srcUrl,kind} : null;
    let lookupError;
    if (!item || info.srcUrl?.startsWith('blob:')) {
      try {
        await ensureCollector(tab.id, info.frameId ?? 0);
        const resolved = await chrome.tabs.sendMessage(tab.id, {action:'resolve-context-image', ...(info.srcUrl ? {expectedUrl:info.srcUrl} : {})}, {frameId: info.frameId ?? 0});
        if (resolved?.kind === 'video' || resolved?.item?.kind === 'video') kind = 'video';
        if (resolved?.ok && resolved.item?.url) item = resolved.item;
        else {item = null;lookupError = resolved?.error || '请重新加载 Mori 插件并刷新网页，再右键收藏。';}
      } catch {item = null;lookupError = kind === 'video' ? '请重新加载 Mori 插件并刷新视频网页，再右键收藏。本次未保存。' : '请重新加载 Mori 插件并刷新网页，再右键收藏；受限页面可截图后粘贴到 Mori。';}
    }
    if (contextRequests.get(tab.id) !== requestId) return;
    // Editing must happen before any download/import, including right-click saves.
    // If a restricted page cannot show the editor, never silently save instead.
    const preferences = await MoriPreferences.read();
    if (contextRequests.get(tab.id) !== requestId) return;
    if (item && preferences.editBeforeSave) {
      const feedback = await contextFeedback(tab.id, {action: 'context-edit', requestId, item: {...item, source: item.mediaPage || info.pageUrl || tab.url || info.frameUrl || '', name: item.name || tab.title || '网页收藏'}});
      await contextBadge(tab.id, feedback?.displayed ? '' : '!', feedback?.displayed ? '请在网页中确认收藏信息' : '无法显示编辑窗口，未保存。请刷新普通网页后重试。');
      return;
    }
    await contextBadge(tab.id, '…', kind === 'video' ? '正在收藏视频' : '正在收藏图片');
    await contextFeedback(tab.id, {action: 'context-saving', requestId, kind, name: item?.name || tab.title || '网页收藏'});
    const result = item ? await saveImage({url: item.url, kind, source: item.mediaPage || info.pageUrl || tab.url || info.frameUrl, name: item.name || tab.title || '网页收藏'}, owner)
      : {ok: false, error: lookupError || '这里没有找到可收藏的图片，请右键图片本身，或截图后粘贴到 Mori。'};
    if (contextRequests.get(tab.id) !== requestId) {
      if (result.choiceToken) discardChoice(result.choiceToken, owner);
      return;
    }
    const feedback = await contextFeedback(tab.id, {action: 'result', requestId, result});
    if (result.choiceToken && !feedback?.displayed) discardChoice(result.choiceToken, owner);
    const title = result.choiceRequired ? (feedback?.displayed ? '图片重复，请在网页中选择处理方式' : '图片已存在，未重复导入。请刷新网页后重新收藏以选择处理方式') : result.ok ? '已收藏到本机图库' : (result.error || '收藏失败，请重试');
    await contextBadge(tab.id, result.choiceRequired || !result.ok ? '!' : '✓', title);
  } catch {
    if(contextRequests.get(tab.id)===requestId)await contextBadge(tab.id,'!','收藏未完成，请刷新页面重试。');
  } finally {
    if (contextRequests.get(tab.id) === requestId) contextRequests.delete(tab.id);
    finishActivity();
  }
});
updater.start();
