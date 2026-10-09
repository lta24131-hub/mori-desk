(() => {
  const P = MoriPreferences, $ = selector => document.querySelector(selector);
  $('.version').textContent = chrome.runtime.getManifest?.().version || '';
  const media = matchMedia('(prefers-color-scheme: dark)');
  let prefs = P.normalize(), url = '', settingsOpen = false, nativeReply, nativeError, ready = false;
  const strings = {
    drag:['拖拽收藏','Drag collection'], enabled:['启用','Enabled'], page:['在此页面禁用','Disable on this page'], host:['在此网站禁用','Disable on this site'], disabledAll:['已全局禁用','Disabled everywhere'], preferences:['偏好设置','Preferences'], help:['右键图片或拖到收藏区，保存到本机图库。','Right-click an image or drag it into the collector to save locally.'], interface:['界面设置','Appearance'], theme:['主题','Theme'], light:['浅色','Light'], dark:['深色','Dark'], system:['跟随系统','System'], language:['语言','Language'], browser:['跟随浏览器','Browser default'], collection:['收藏设置','Collection'], layout:['收藏区布局','Collector layout'], popup:['弹窗式','Floating'], bottom:['吸底式','Bottom dock'], disabled:['禁用','Disabled'], edit:['保存前显示编辑窗口','Edit before saving'], editHint:['确认名称、分类和来源；取消不会保存图片。','Review name, category and source. Cancel saves nothing.'], scopeHint:['禁用只影响拖拽收藏，右键收藏仍可使用。','Disabling drag collection does not disable right-click collection.'], exclusions:['已禁用的页面与网站','Disabled pages & sites'], none:['暂无禁用规则','No exclusions'], remove:['移除','Remove'], back:['返回','Back'], saved:['已保存','Saved'], failed:['设置未能保存，请重试。','Could not save preferences. Try again.'], loading:['正在检查本机连接…','Checking local connection…'], connected:['已连接本机图库','Connected to local library'], disconnected:['尚未连接 Mori','Mori is not connected'], current:['仅针对当前网页；忽略网址参数和锚点。','Current page only; query strings and fragments are ignored.'], unavailable:['此页面不支持拖拽收藏，仍可调整全局偏好。','Drag collection is unavailable here. Global preferences are still available.']
  };
  const t = key => strings[key]?.[P.language(prefs) === 'en' ? 1 : 0] || key;
  let bookmarkReview=null, importing=false;
  const send = message => new Promise((resolve,reject)=>chrome.runtime.sendMessage(message,response=>{const error=chrome.runtime.lastError;if(error||!response?.ok)reject(Error(error?.message || response?.error || '本机连接未返回结果'));else resolve(response);}));
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
      $('#bookmark-progress').textContent+= '。请到 Mori 左侧“网址收藏”查看。';bookmarkReview=null;$('#bookmark-review').classList.add('hidden');
    }catch(error){$('#bookmark-progress').textContent=`导入中断，已完成的 ${processed} 个仍保留，可重新导入：${error.message}`;}
    finally{importing=false;$('#import-bookmarks').disabled=false;$('#bookmark-confirm').disabled=false;$('#bookmark-cancel').disabled=false;}
  };
  function render() {
    document.documentElement.dataset.theme = P.theme(prefs, media.matches);
    document.documentElement.lang = P.language(prefs);
    document.querySelectorAll('[data-i18n]').forEach(node => node.textContent = t(node.dataset.i18n));
    $('#back').setAttribute('aria-label', t('back'));
    $('#heading').textContent = settingsOpen ? t('preferences') : 'Mori';
    $('#status').textContent = nativeError || (nativeReply ? (nativeReply.ok ? t('connected') : nativeReply.error || t('disconnected')) : t('loading'));
    $('#drag-mode').value = P.mode(prefs, url);
    $('#drag-mode').disabled = !ready || !P.pageKey(url);
    $('#page-note').textContent = t(P.pageKey(url) ? 'current' : 'unavailable');
    document.querySelectorAll('input[name]').forEach(input => input.checked = prefs[input.name] === input.value);
    $('#language').value = prefs.language; $('#edit').checked = prefs.editBeforeSave;
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
  chrome.runtime.sendMessage({action:'folders'}, response => {nativeError=chrome.runtime.lastError?.message;nativeReply=response || {ok:false};render();});
})();
