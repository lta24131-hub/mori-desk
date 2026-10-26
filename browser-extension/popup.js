// Health checks never collect or acknowledge a save. One request is in flight
// at a time, including after a UI deadline; closing this popup retires replies.
const MoriPopupHealth = (() => {
  function create(options) {
    const delay=options.setTimeout || setTimeout, cancel=options.clearTimeout || clearTimeout;
    const visible=options.visible || (()=>true);
    let stopped=false, started=false, pending=null, pollTimer, state={phase:'checking'};
    const publish=value=>{state=value;if(!stopped && visible())options.changed(state);};
    const clearPoll=()=>{cancel(pollTimer);pollTimer=null;};
    function schedule() {clearPoll();if(!stopped && visible())pollTimer=delay(()=>{pollTimer=null;void check();},15000);}
    function check() {
      if(stopped || !visible())return Promise.resolve(state);
      if(pending)return pending.promise;
      clearPoll();
      let resolve;const item={promise:new Promise(done=>{resolve=done;}),resolve:null,timer:null};
      item.resolve=resolve;pending=item;publish({phase:'checking'});
      item.timer=delay(()=>{
        if(pending!==item || stopped)return;
        publish({phase:'timeout'});item.resolve(state);
        // Keep pending until the real callback. A UI timeout must not launch
        // another native process while the original check is still running.
      },6000);
      Promise.resolve().then(options.request).then(reply=>complete(item,reply),()=>complete(item,{ok:false,code:'HEALTH_UNAVAILABLE'}));
      return item.promise;
    }
    function complete(item,reply) {
      if(pending!==item || stopped)return;
      cancel(item.timer);pending=null;
      publish({phase:reply?.ok?'connected':'failed',reply:reply || {ok:false,code:'HEALTH_EMPTY_REPLY'}});
      item.resolve(state);schedule();
    }
    function visibilityChanged() {clearPoll();if(!stopped && visible()){options.changed(state);void check();}}
    function stop() {if(stopped)return;stopped=true;clearPoll();if(pending){cancel(pending.timer);pending.resolve(state);pending=null;}}
    return {check,stop,visibilityChanged,start(){if(!started && !stopped){started=true;void check();}},status:()=>state};
  }
  return {create};
})();

(() => {
  const P = MoriPreferences, $ = selector => document.querySelector(selector);
  $('.version').textContent = chrome.runtime.getManifest?.().version || '';
  const media = matchMedia('(prefers-color-scheme: dark)');
  let prefs = P.normalize(), url = '', settingsOpen = false, ready = false, healthState={phase:'checking'};
  const strings = {
    drag:['拖拽收藏','Drag collection'], enabled:['启用','Enabled'], page:['在此页面禁用','Disable on this page'], host:['在此网站禁用','Disable on this site'], disabledAll:['已全局禁用','Disabled everywhere'], preferences:['偏好设置','Preferences'], help:['右键图片或拖到收藏区，保存到本机图库。','Right-click an image or drag it into the collector to save locally.'], interface:['界面设置','Appearance'], theme:['主题','Theme'], light:['浅色','Light'], dark:['深色','Dark'], system:['跟随系统','System'], language:['语言','Language'], browser:['跟随浏览器','Browser default'], collection:['收藏设置','Collection'], layout:['收藏区布局','Collector layout'], popup:['弹窗式','Floating'], bottom:['吸底式','Bottom dock'], disabled:['禁用','Disabled'], edit:['保存前显示编辑窗口','Edit before saving'], editHint:['确认名称、分类和来源；取消不会保存图片。','Review name, category and source. Cancel saves nothing.'], scopeHint:['禁用只影响拖拽收藏，右键收藏仍可使用。','Disabling drag collection does not disable right-click collection.'], exclusions:['已禁用的页面与网站','Disabled pages & sites'], none:['暂无禁用规则','No exclusions'], remove:['移除','Remove'], back:['返回','Back'], saved:['已保存','Saved'], failed:['设置未能保存，请重试。','Could not save preferences. Try again.'], loading:['正在检查本机连接…','Checking local connection…'], connected:['已连接 Nook','Connected to Nook'], disconnected:['尚未连接 Nook','Nook is not connected'], current:['仅针对当前网页；忽略网址参数和锚点。','Current page only; query strings and fragments are ignored.'], unavailable:['此页面不支持拖拽收藏，仍可调整全局偏好。','Drag collection is unavailable here. Global preferences are still available.']
  };
  const t = key => strings[key]?.[P.language(prefs) === 'en' ? 1 : 0] || key;
  Object.assign(strings, {
    checkUpdate:['检查更新状态','Check update status'], acknowledgeUpdate:['已检查图库，继续更新','Library checked; allow update'],
    updateCurrent:['暂无待安装更新；可在 Nook 设置中更新插件','No queued update; update the extension in Nook settings'], updateAvailable:['更新已就绪，关闭此窗口后自动完成','Update is ready; close this popup to finish'],
    updateBusy:['更新已就绪，当前收藏完成后自动更新','Update is ready; it will finish after collection'],
    updateUncertain:['上次收藏结果未确认，请先到图库检查是否保存。不会自动重复收藏。','A previous save is unconfirmed. Check the library first; it will not be replayed.'],
    updateUnavailable:['暂时无法检查更新，请确认 Nook 已打开','Update check unavailable; make sure Nook is open'],
    updateUnpaired:['连接本机 Nook 后可自动更新插件','Connect to local Nook to enable automatic updates'],
    updateChecking:['正在检查插件更新…','Checking extension updates…'],
    updateDeferred:['更新已暂缓；请结束收藏或刷新网页后重试','Update deferred. Finish any collection or refresh the pages, then retry.'],
    updateMultiple:['多个浏览器配置正在连接，请关闭其他连接后重试','Multiple browser profiles are connected; close the others and retry'],
    nativeLabel:['收藏连接','Collection connection'], healthChecking:['正在检查…','Checking…'], healthConnected:['已连接','Connected'],
    healthTimeout:['连接检查较慢，仍在等待；不会重复发送收藏','Connection check is taking longer; waiting without replaying any save'],
    healthRepair:['连接未就绪。请打开 Nook → 设置 → 浏览器收藏，点“安装 / 修复”','Connection is not ready. Open Nook → Settings → Browser collection → Install / repair'],
    diagnostics:['连接详情','Connection details'], updateChannel:['更新通道','Update channel'],
    channelConnected:['已连接（仅本机）','Connected (local only)'], channelUnavailable:['暂不可用','Unavailable'], channelUnknown:['尚未确认','Not checked yet']
  });
  let bookmarkReview=null, importing=false, updateState, checkingUpdate=false, popupPort;
  function renderHealth() {
    const phase=healthState.phase, reply=healthState.reply;
    $('#status').textContent=t('nativeLabel')+' · '+t(phase==='connected'?'healthConnected':phase==='checking'?'healthChecking':phase==='timeout'?'healthTimeout':'healthRepair');
    const detail=[reply?.code,reply?.diagnostic || reply?.nativeDiagnostic].filter(Boolean).join('\n').slice(0,1000);
    $('#connection-detail').textContent=detail;
    $('#connection-diagnostics').classList.toggle('hidden',!detail);
  }
  function renderUpdate() {
    let key='';
    if(checkingUpdate)key='updateChecking';
    else if(updateState?.uncertain)key='updateUncertain';
    else if(updateState?.reason==='multipleClients')key='updateMultiple';
    else if(updateState?.reason==='unavailable')key='updateUnavailable';
    else if(updateState?.reason==='deferred')key='updateDeferred';
    else if(updateState?.pendingVersion)key=updateState.busy?'updateBusy':'updateAvailable';
    else if(updateState?.reason==='current')key='updateCurrent';
    else if(updateState?.reason==='unpaired')key='updateUnpaired';
    $('#update-status').textContent=key?t(key):'';
    $('#update-channel').textContent=t('updateChannel')+' · '+t(updateState?.connection==='connected'?'channelConnected':updateState?.connection==='unavailable'?'channelUnavailable':'channelUnknown');
    $('#check-update').disabled=checkingUpdate;
    $('#acknowledge-update').classList.toggle('hidden',!updateState?.uncertain);
    $('#acknowledge-update').disabled=checkingUpdate || !!updateState?.busy;
  }
  // A live port makes opening the popup an update barrier, including bookmark
  // review and preferences edits. It is released naturally when this UI closes.
  try {
    popupPort=chrome.runtime.connect?.({name:'mori-popup'});
    popupPort?.onMessage.addListener(message=>{if(message?.updateStatus){updateState=message.updateStatus;renderUpdate();}});
  } catch {}
  const send = message => new Promise((resolve,reject)=>chrome.runtime.sendMessage(message,response=>{const error=chrome.runtime.lastError;if(error||!response?.ok)reject(Error(error?.message || response?.error || '本机连接未返回结果'));else resolve(response);}));
  const health=MoriPopupHealth.create({
    visible:()=>document.visibilityState!=='hidden',
    changed:state=>{healthState=state;renderHealth();},
    request:()=>new Promise(resolve=>{
      try {chrome.runtime.sendMessage({action:'bridgeStatus'},response=>{
        const error=chrome.runtime.lastError;
        resolve(error?{ok:false,code:'HEALTH_UNAVAILABLE',diagnostic:String(error.message || '').slice(0,500)}:response || {ok:false,code:'HEALTH_EMPTY_REPLY'});
      });}catch {resolve({ok:false,code:'HEALTH_UNAVAILABLE'});}
    })
  });
  async function checkUpdate(action) {
    if(checkingUpdate)return;
    void health.check();
    checkingUpdate=true;renderUpdate();
    try {updateState=await send({action});} catch {updateState={reason:'unavailable'};}
    finally {checkingUpdate=false;renderUpdate();}
  }
  $('#check-update').onclick=()=>checkUpdate('extensionUpdateCheck');
  $('#acknowledge-update').onclick=()=>checkUpdate('extensionUpdateAcknowledge');
  $('#import-bookmarks').onclick=async()=>{
    if(importing)return;
    try{
      // Optional permission is requested only in this direct user click handler.
      if(!await chrome.permissions.request({permissions:['bookmarks']})){ $('#bookmark-progress').textContent='未授权读取书签，没有导入任何内容。';return; }
      bookmarkReview=MoriBookmarks.flatten(await chrome.bookmarks.getTree());
      $('#bookmark-count').textContent=`可导入 ${bookmarkReview.items.length} 个网址，跳过 ${bookmarkReview.skipped} 个非网页链接。`;
      $('#bookmark-review').classList.remove('hidden');$('#bookmark-confirm').disabled=!bookmarkReview.items.length;
    }catch(error){$('#bookmark-progress').textContent=error.message;}
  };
  $('#bookmark-cancel').onclick=()=>{bookmarkReview=null;$('#bookmark-review').classList.add('hidden');};
  $('#bookmark-confirm').onclick=async()=>{
    if(importing||!bookmarkReview?.items.length)return;
    importing=true;$('#import-bookmarks').disabled=true;$('#bookmark-confirm').disabled=true;$('#bookmark-cancel').disabled=true;
    const items=bookmarkReview.items;let root='',added=0,reused=0,skipped=0,processed=0;
    try{
      for(let at=0;at<items.length;at+=40){
        const result=await send({action:'importBookmarks',root,items:items.slice(at,at+40)});root=result.root;added+=result.added;reused+=result.reused;skipped+=result.skipped;processed+=Math.min(40,items.length-at);
        $('#bookmark-progress').textContent=`已处理 ${processed} / ${items.length} · 新增 ${added} · 已有 ${reused} · 跳过 ${skipped}`;
      }
      $('#bookmark-progress').textContent+= '。请到 Nook 左侧“网址收藏”查看。';bookmarkReview=null;$('#bookmark-review').classList.add('hidden');
    }catch(error){$('#bookmark-progress').textContent=`导入中断，已完成的 ${processed} 个仍保留，可重新导入：${error.message}`;}
    finally{importing=false;$('#import-bookmarks').disabled=false;$('#bookmark-confirm').disabled=false;$('#bookmark-cancel').disabled=false;}
  };
  function render() {
    document.documentElement.dataset.theme = P.theme(prefs, media.matches);
    document.documentElement.lang = P.language(prefs);
    document.querySelectorAll('[data-i18n]').forEach(node => node.textContent = t(node.dataset.i18n));
    $('#back').setAttribute('aria-label', t('back'));
    $('#heading').textContent = settingsOpen ? t('preferences') : 'Nook';
    // The collection/native connection and authenticated HTTP update channel
    // are independent. Neither health check proves a particular save succeeded.
    renderHealth();renderUpdate();
    $('#drag-mode').value = P.mode(prefs, url);
    $('#drag-mode').disabled = !ready || !P.pageKey(url);
    $('#page-note').textContent = t(P.pageKey(url) ? 'current' : 'unavailable');
    document.querySelectorAll('input[name]').forEach(input => input.checked = prefs[input.name] === input.value);
    $('#language').value = prefs.language; $('#edit').checked = prefs.editBeforeSave;
    renderUpdate();
    $('#rules').replaceChildren();
    for (const field of ['disabledHosts','disabledPages']) for (const value of prefs[field]) {
      const row = document.createElement('div'); row.className = 'rule';
      const label = document.createElement('span'); label.textContent = value;
      const button = document.createElement('button'); button.textContent = t('remove'); button.setAttribute('aria-label', t('remove') + ' ' + value);
      button.onclick = () => update(current => ({...current, [field]: current[field].filter(v => v !== value)}));
      row.append(label,button); $('#rules').append(row);
    }
    if (!$('#rules').children.length) $('#rules').textContent = t('none');
  }
  let writes = Promise.resolve();
  function update(change) {
    writes = writes.then(async () => {
      try {
        prefs = await P.write(change(await P.read()));
        $('#message').className = ''; $('#message').textContent = t('saved');
      } catch { $('#message').className = 'error'; $('#message').textContent = t('failed'); }
      render();
    });
    return writes;
  }
  function navigate(open) {
    settingsOpen = open; $('#home').classList.toggle('hidden', open); $('#settings').classList.toggle('hidden', !open); $('#back').classList.toggle('hidden', !open); $('#message').textContent = ''; render();
    (open ? $('#back') : $('#open-settings')).focus();
  }
  $('#open-settings').onclick = () => navigate(true); $('#back').onclick = () => navigate(false);
  $('#drag-mode').onchange = event => {const value = event.target.value; update(current => P.withMode(current,url,value));};
  document.querySelectorAll('input[name]').forEach(input => input.onchange = () => update(current => ({...current,[input.name]:input.value})));
  $('#language').onchange = event => {const value=event.target.value; update(current => ({...current,language:value}));};
  $('#edit').onchange = event => {const value=event.target.checked; update(current => ({...current,editBeforeSave:value}));};
  media.addEventListener('change', render);
  chrome.storage.onChanged.addListener((changes, area) => {if(area === 'local' && changes[P.key]) {prefs=P.normalize(changes[P.key].newValue);render();}});
  render();
  Promise.all([P.read(),chrome.tabs.query({active:true,currentWindow:true})]).then(([value,tabs]) => {prefs=value;url=tabs[0]?.url || '';ready=true;render();}).catch(() => {$('#message').className='error';$('#message').textContent=t('failed');});
  document.addEventListener('visibilitychange',()=>health.visibilityChanged());
  window.addEventListener('pagehide',()=>health.stop(),{once:true});
  window.addEventListener('unload',()=>health.stop(),{once:true});
  health.start();
  chrome.runtime.sendMessage({action:'extensionUpdateStatus'}, response => {if(!chrome.runtime.lastError && response?.ok){updateState=response;renderUpdate();}});
})();
