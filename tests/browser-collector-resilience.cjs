// Actual collector DOM/event flows with synthetic messages in a disposable
// headless browser. No user profile, native host, real URL or media library.
const {test,before,after}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {chromium}=require('playwright');
const root=path.resolve(__dirname,'..');
const scripts=['preferences.js','collector-text.js','drag-geometry.js','media-source.js','runtime-client.js','content.js'];
// Keep the same ordered production scripts, with one injection round trip per
// disposable page instead of six separate file/DevTools handshakes in the VM.
const scriptBundle=scripts.map(file=>fs.readFileSync(path.join(root,'browser-extension',file),'utf8')).join('\n;\n');
let browser;
before(async()=>{browser=await chromium.launch({channel:'msedge',headless:true});});
after(async()=>{await browser?.close();});
async function fixture() {
  const page=await browser.newPage({viewport:{width:1100,height:850},locale:'zh-CN'}),errors=[];
  page.on('pageerror',error=>errors.push(error.message));
  await page.addInitScript(()=>{
    const nativeTimeout=window.setTimeout;window.fixtureStatusExpiries=[];
    window.setTimeout=(fn,ms,...args)=>{if(ms===30000)fixtureStatusExpiries.push(fn);return nativeTimeout(fn,ms,...args);};
    const attach=Element.prototype.attachShadow;Element.prototype.attachShadow=function(options){return attach.call(this,{...options,mode:'open'});};
    window.fixtureCalls=[];window.fixtureListeners=[];window.fixtureSaveReply={ok:true};window.fixtureSavePending=false;window.fixtureReplies=[];
    window.fixtureFolderReply={ok:true,folders:[{id:'one',name:'测试分类'}]};
    window.chrome={runtime:{id:'a'.repeat(32),getManifest:()=>({version:'0.2.17'}),
      onMessage:{addListener:fn=>fixtureListeners.push(fn),removeListener:fn=>{fixtureListeners=fixtureListeners.filter(value=>value!==fn);}},
      connect:()=>{const port={onMessage:{addListener(){}},onDisconnect:{addListener:fn=>port.disconnectListener=fn},postMessage(){},disconnect(){port.disconnectListener?.();}};return port;},
      sendMessage(message,callback){fixtureCalls.push(message);if(message.action==='save'){if(fixtureSavePending)fixtureReplies.push(callback);else callback(fixtureSaveReply);return;}callback(message.action==='preferences'?{ok:true,preferences:{theme:'light',language:'zh-CN',collector:'popup',editBeforeSave:false}}:message.action==='folders'?fixtureFolderReply:{ok:true});}},
      storage:{onChanged:{addListener(){},removeListener(){}}}};
    window.fixtureMessage=message=>{let result;fixtureListeners.forEach(fn=>fn(message,{},reply=>result=reply));return result;};
  });
  await page.route('https://mori-resilience-fixture.invalid/**',route=>route.fulfill({contentType:'text/html',body:'<!doctype html><title>Synthetic resilience</title><img id="one" style="position:absolute;left:24px;top:24px;width:120px;height:100px" alt="Synthetic image" src="data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9YL7ZioAAAAASUVORK5CYII=">'}));
  await page.goto('https://mori-resilience-fixture.invalid/page');
  await page.addScriptTag({content:scriptBundle});
  const panel=page.locator('[data-mori-collector] .panel');
  const start=()=>page.dispatchEvent('#one','dragstart',{clientX:60,clientY:65,bubbles:true});
  const categories=async()=>{
    await page.locator('[data-mori-collector] .category-toggle').dispatchEvent('dragover',{clientX:240,clientY:280,bubbles:true,composed:true});
    await page.locator('[data-mori-collector] .category-menu').waitFor({state:'visible'});
  };
  const drop=async()=>{await page.locator('[data-mori-collector] .quick').dispatchEvent('drop',{clientX:240,clientY:200,bubbles:true,composed:true});};
  const calls=action=>page.evaluate(action=>fixtureCalls.filter(value=>value.action===action).length,action);
  return {page,panel,start,categories,drop,calls,errors,message:message=>page.evaluate(message=>fixtureMessage(message),message)};
}

test('cancelled drag and accidental double-drop create no extra collection or category query',async()=>{
  const f=await fixture();try{
    await f.start();await f.page.keyboard.press('Escape');assert.equal(await f.panel.count(),0);assert.equal(await f.calls('save'),0);assert.equal(await f.calls('folders'),0);
    await f.page.evaluate(()=>{fixtureSavePending=true;});await f.start();
    await f.page.locator('[data-mori-collector] .quick').evaluate(node=>{for(let at=0;at<2;at++)node.dispatchEvent(new DragEvent('drop',{bubbles:true,composed:true,clientX:240,clientY:200}));});
    assert.equal(await f.calls('save'),1);assert.equal(await f.panel.count(),0);
    await f.page.evaluate(()=>fixtureReplies.splice(0).forEach(reply=>reply({ok:true})));assert.equal(await f.page.locator('[data-mori-notice] .toast').count(),1);assert.deepEqual(f.errors,[]);
  }finally{await f.page.close();}
});

test('a late duplicate does not replace a newer drag and skip releases it without another save',async()=>{
  const f=await fixture();try{
    await f.page.evaluate(()=>fixtureSavePending=true);await f.start();await f.drop();await f.start();
    await f.page.evaluate(()=>fixtureReplies.splice(0).forEach(reply=>reply({ok:true,choiceRequired:true,choiceToken:'fixture-choice'})));
    assert.equal(await f.panel.getAttribute('data-phase'),'dragging');assert.equal(await f.page.locator('[data-mori-notice] .toast-action').count(),1);
    await f.page.keyboard.press('Escape');await f.page.locator('[data-mori-notice] .toast-action').click();assert.equal(await f.panel.getAttribute('data-phase'),'duplicate');
    await f.page.locator('[data-mori-collector] .actions button').first().click();assert.equal(await f.panel.count(),0);assert.equal(await f.calls('save'),1);assert.equal(await f.calls('discard'),1);assert.deepEqual(f.errors,[]);
  }finally{await f.page.close();}
});

test('update preparation waits for an active save and does not block a completed result notice',async()=>{
  const f=await fixture();try{
    await f.page.evaluate(()=>fixtureSavePending=true);await f.start();await f.drop();
    assert.equal((await f.message({action:'mori-update-prepare',lock:'fixture-lock'})).ready,false);
    await f.page.evaluate(()=>fixtureReplies.splice(0).forEach(reply=>reply({ok:true})));
    assert.equal((await f.message({action:'mori-update-prepare',lock:'fixture-lock'})).ready,true);await f.start();assert.equal(await f.panel.count(),0);
    await f.message({action:'mori-update-resume',lock:'fixture-lock'});await f.start();assert.equal(await f.panel.count(),1);assert.deepEqual(f.errors,[]);
  }finally{await f.page.close();}
});

test('lost video save results never claim the video definitely was not saved',async()=>{
  const f=await fixture();try{
    await f.message({action:'context-saving',requestId:'unknown-video',kind:'video',name:'Synthetic video'});
    await f.message({action:'result',requestId:'unknown-video',result:{ok:false,code:'SAVE_RESULT_UNKNOWN',error:'未收到收藏结果，请先检查 Mori 图库，确认后再重试。'}});
    assert.match(await f.page.locator('[data-mori-collector] .title').innerText(),/未确认/);
    assert.doesNotMatch(await f.page.locator('[data-mori-collector] .source').innerText(),/未保存|截图/);
    assert.match(await f.page.locator('[data-mori-collector] .source').innerText(),/检查.*图库/);assert.deepEqual(f.errors,[]);
  }finally{await f.page.close();}
});

test('lost background save results are labeled unconfirmed rather than failed',async()=>{
  const f=await fixture();try{
    await f.page.evaluate(()=>fixtureSaveReply={ok:false,code:'NATIVE_TIMEOUT',error:'等待收藏结果超时，请先检查图库。'});await f.start();await f.drop();
    assert.match(await f.page.locator('[data-mori-notice] .toast-text').innerText(),/结果未确认/);
    assert.doesNotMatch(await f.page.locator('[data-mori-notice] .toast-text').innerText(),/收藏失败/);assert.deepEqual(f.errors,[]);
  }finally{await f.page.close();}
});

test('a pending slow collection keeps its small status until a result arrives or the user dismisses it',async()=>{
  const f=await fixture();try{
    await f.page.evaluate(()=>fixtureSavePending=true);await f.start();await f.drop();
    await f.page.locator('[data-mori-notice] .toast[data-kind="saving"]').waitFor({state:'visible'});
    assert.equal(await f.page.evaluate(()=>fixtureStatusExpiries.length),0,'status must not silently expire at 30s while the bounded save can still be in progress');
    await f.page.locator('[data-mori-notice] .toast-close').click();assert.equal(await f.page.locator('[data-mori-notice]').count(),0);assert.equal(await f.calls('save'),1);
    await f.page.evaluate(()=>fixtureReplies.splice(0).forEach(reply=>reply({ok:true})));
    assert.equal(await f.page.locator('[data-mori-notice] .toast[data-kind="success"]').count(),1);assert.deepEqual(f.errors,[]);
  }finally{await f.page.close();}
});

test('category load recovery keeps the parked image and saves only the selected category',async()=>{
  const f=await fixture();try{
    await f.page.evaluate(()=>fixtureFolderReply={ok:false,error:'测试连接暂时中断'});
    await f.start();await f.categories();
    await f.drop();assert.equal(await f.panel.getAttribute('data-phase'),'choosing');
    const status=f.page.locator('[data-mori-collector] .folder-status');
    assert.equal(await status.isVisible(),true);assert.match(await status.innerText(),/分类暂不可用/);
    assert.equal(await f.panel.getAttribute('data-connection'),'error');
    // The parked category menu intentionally covers the quick-drop headline.
    // Assert the actual visible recovery path, not text behind that menu.
    assert.equal(await f.page.locator('[data-mori-collector] .title').isVisible(),false);
    await f.page.locator('[data-mori-collector] .folder-retry').click();
    assert.equal(await status.isVisible(),true);assert.match(await status.innerText(),/分类暂不可用/);
    assert.equal(await f.calls('save'),0);
    await f.page.evaluate(()=>fixtureFolderReply={ok:true,folders:[{id:'one',name:'测试分类'}]});
    await f.page.locator('[data-mori-collector] .folder-retry').click();
    const folder=f.page.locator('[data-mori-collector] .folder');
    assert.equal(await folder.isVisible(),true);assert.equal(await folder.innerText(),'测试分类');
    assert.equal(await status.count(),0);assert.equal(await f.page.locator('[data-mori-collector] .folder-retry').count(),0);
    assert.equal(await f.panel.getAttribute('data-connection'),null);
    assert.equal(await f.page.locator('[data-mori-collector] .tag').innerText(),'选择文件夹');
    assert.equal(await f.panel.getAttribute('data-phase'),'choosing');assert.equal(await f.calls('save'),0);
    await folder.click();
    assert.equal(await f.calls('save'),1);
    assert.equal(await f.page.evaluate(()=>fixtureCalls.find(value=>value.action==='save').folder),'one');
    assert.deepEqual(f.errors,[]);
  }finally{await f.page.close();}
});

test('category recovery in the editor keeps the users title and source edits until explicit save',async()=>{
  const f=await fixture();try{
    await f.page.evaluate(()=>fixtureFolderReply={ok:false,error:'测试连接暂时中断'});
    await f.message({action:'context-edit',requestId:'category-recovery',item:{url:'https://example.invalid/image.png',name:'原名称',source:'https://example.invalid/original',kind:'image'}});
    const inputs=f.page.locator('[data-mori-collector] .editor input');
    await inputs.nth(0).fill('新名称');await inputs.nth(1).fill('https://example.invalid/edited');
    await f.page.evaluate(()=>fixtureFolderReply={ok:true,folders:[{id:'one',name:'测试分类'}]});
    await f.page.locator('[data-mori-collector] .folder-retry').click();
    assert.equal(await inputs.nth(0).inputValue(),'新名称');assert.equal(await inputs.nth(1).inputValue(),'https://example.invalid/edited');
    assert.equal(await f.page.locator('[data-mori-collector] .editor select').isEnabled(),true);assert.equal(await f.calls('save'),0);
    await f.page.locator('[data-mori-collector] .editor select').selectOption('one');
    await f.page.locator('[data-mori-collector] .editor button[type="submit"]').click();
    const saved=await f.page.evaluate(()=>fixtureCalls.find(value=>value.action==='save'));
    assert.equal(saved.folder,'one');assert.equal(saved.name,'新名称');assert.equal(saved.source,'https://example.invalid/edited');assert.deepEqual(f.errors,[]);
  }finally{await f.page.close();}
});

test('reusing a duplicate reports the existing original instead of falsely placing it in Unfiled',async()=>{
  for(const categorized of [false,true]){
    const f=await fixture();try{
      await f.page.evaluate(()=>fixtureSaveReply={ok:true,choiceRequired:true,choiceToken:'duplicate-location'});
      await f.start();
      if(categorized){await f.categories();await f.drop();await f.page.locator('[data-mori-collector] .folder').click();}
      else await f.drop();
      assert.equal(await f.panel.getAttribute('data-phase'),'duplicate');
      await f.page.evaluate(()=>fixtureSaveReply={ok:true,duplicate:true,keptCopy:false});
      await f.page.locator('[data-mori-collector] .actions button').nth(1).click();
      const notice=f.page.locator('[data-mori-notice] .toast[data-kind="success"]');
      assert.match(await notice.innerText(),/已使用已有图片/);
      assert.match(await notice.getAttribute('title'),categorized?/已保存到「测试分类」/:/已使用图库中的原图，未重复保存文件/,'DUPLICATE_REUSE_LOCATION: success detail must match the original or the explicitly selected category');
      assert.doesNotMatch(await notice.getAttribute('title'),/未分类/);
      const saves=await f.page.evaluate(()=>fixtureCalls.filter(value=>value.action==='save'));
      assert.equal(saves.length,2);assert.equal(saves[1].duplicateAction,'reuse');assert.deepEqual(f.errors,[]);
    }finally{await f.page.close();}
  }
});
