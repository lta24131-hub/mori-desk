// Actual collector DOM/event flows with synthetic messages in a disposable
// headless browser. No user profile, native host, real URL or media library.
const {test,before,after}=require('node:test');
const assert=require('node:assert/strict');
const path=require('node:path');
const {chromium}=require('playwright');
const root=path.resolve(__dirname,'..');
const scripts=['preferences.js','collector-text.js','drag-geometry.js','media-source.js','runtime-client.js','content.js'];
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
    window.chrome={runtime:{id:'a'.repeat(32),getManifest:()=>({version:'0.2.17'}),
      onMessage:{addListener:fn=>fixtureListeners.push(fn),removeListener:fn=>{fixtureListeners=fixtureListeners.filter(value=>value!==fn);}},
      connect:()=>{const port={onMessage:{addListener(){}},onDisconnect:{addListener:fn=>port.disconnectListener=fn},postMessage(){},disconnect(){port.disconnectListener?.();}};return port;},
      sendMessage(message,callback){fixtureCalls.push(message);if(message.action==='save'){if(fixtureSavePending)fixtureReplies.push(callback);else callback(fixtureSaveReply);return;}callback(message.action==='preferences'?{ok:true,preferences:{theme:'light',language:'zh-CN',collector:'popup',editBeforeSave:false}}:message.action==='folders'?{ok:true,folders:[{id:'one',name:'测试分类'}]}:{ok:true});}},
      storage:{onChanged:{addListener(){},removeListener(){}}}};
    window.fixtureMessage=message=>{let result;fixtureListeners.forEach(fn=>fn(message,{},reply=>result=reply));return result;};
  });
  await page.route('https://mori-resilience-fixture.invalid/**',route=>route.fulfill({contentType:'text/html',body:'<!doctype html><title>Synthetic resilience</title><img id="one" style="position:absolute;left:24px;top:24px;width:120px;height:100px" alt="Synthetic image" src="data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9YL7ZioAAAAASUVORK5CYII=">'}));
  await page.goto('https://mori-resilience-fixture.invalid/page');
  for(const script of scripts)await page.addScriptTag({path:path.join(root,'browser-extension',script)});
  const panel=page.locator('[data-mori-collector] .panel');
  const start=()=>page.dispatchEvent('#one','dragstart',{clientX:60,clientY:65,bubbles:true});
  const drop=async()=>{await page.locator('[data-mori-collector] .quick').dispatchEvent('drop',{clientX:240,clientY:200,bubbles:true,composed:true});};
  const calls=action=>page.evaluate(action=>fixtureCalls.filter(value=>value.action===action).length,action);
  return {page,panel,start,drop,calls,errors,message:message=>page.evaluate(message=>fixtureMessage(message),message)};
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
