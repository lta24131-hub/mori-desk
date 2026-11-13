// Real MV3/DOM reload coverage in a fresh, disposable profile. It never loads
// the user's browser profile, native host, pairing token, or media library.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const {chromium} = require('playwright');
(async()=>{
  const root=path.resolve(__dirname,'..');
  const temp=fs.mkdtempSync(path.join(os.tmpdir(),'Mori-collector-lifecycle-'));
  const extension=path.join(temp,'extension');
  fs.cpSync(path.join(root,'browser-extension'),extension,{recursive:true,filter:source=>path.basename(source)!=='local-connection.js'});
  const generation='b'.repeat(32);
  for(const file of ['preferences.js','updater.js'])fs.appendFileSync(path.join(extension,file),`\nglobalThis.MoriInstalledGeneration = ${JSON.stringify(generation)};\n`);
  fs.appendFileSync(path.join(extension,'background.js'),`\n// Synthetic test endpoint only.\nlet fixtureNativeQueue=Promise.resolve();\nnative = async payload => {
    if (payload.action === 'folders') return {ok:true,folders:[]};
    // Progress and actual save run concurrently. Record all calls in order,
    // without mistaking notification requests for saves or losing an append.
    const write=fixtureNativeQueue.then(async()=>{
      const calls=(await chrome.storage.local.get('fixtureSaves')).fixtureSaves || [];
      calls.push({action:payload.action,name:payload.name,state:payload.state||'',id:payload.id||''});
      await chrome.storage.local.set({fixtureSaves:calls});
    });
    fixtureNativeQueue=write.catch(()=>{});await write;
    if (!['save','collectionFeedback'].includes(payload.action)) throw Error('Unexpected synthetic native action: '+payload.action);
    return {ok:true};
  };\n`);
  const browser=await chromium.launchPersistentContext(path.join(temp,'profile'),{channel:'msedge',headless:true,ignoreDefaultArgs:['--disable-extensions'],args:['--disable-extensions-except='+extension,'--load-extension='+extension]});
  const errors=[];
  try {
    let worker=browser.serviceWorkers()[0] || await browser.waitForEvent('serviceworker');
    worker.on('console',message=>{if(message.type()==='error')errors.push(message.text());});
    const page=await browser.newPage();page.on('pageerror',error=>errors.push(error.message));
    await page.route('https://mori-fixture.invalid/**',route=>route.fulfill({contentType:'text/html',body:'<!doctype html><title>Collector fixture</title><img id="one" alt="Synthetic image" style="width:120px;height:100px;margin:32px" src="data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9YL7ZioAAAAASUVORK5CYII=">'}));
    await page.goto('https://mori-fixture.invalid/page');
    const currentTab=()=>worker.evaluate(async()=>{const tabs=await chrome.tabs.query({url:'https://mori-fixture.invalid/*'});return tabs[0].id;});
    const ready=async()=>{const id=await currentTab();return worker.evaluate(id=>chrome.tabs.sendMessage(id,{action:'mori-collector-ping'}),id);};
    const waitReady=async()=>{
      for(let attempt=0;attempt<30;attempt++){
        const response=await ready().catch(()=>null);if(response?.ready)return;
        await new Promise(resolve=>setTimeout(resolve,100));
      }
      assert.equal((await ready()).ready,true);
    };
    await waitReady();
    assert.equal((await ready()).generation,generation);
    // Reproduce a worker restarted from repaired files with an old, same-version
    // collector in an already-open tab. No page navigation or runtime reload.
    let staleId=await currentTab();
    await worker.evaluate(async id=>{
      await chrome.scripting.executeScript({target:{tabId:id},func:()=>{globalThis.MoriInstalledGeneration='a'.repeat(32);}});
      await chrome.scripting.executeScript({target:{tabId:id},files:['content.js']});
    },staleId);
    await waitReady();assert.equal((await ready()).generation,'a'.repeat(32));
    await worker.evaluate(id=>ensureCollector(id),staleId);await waitReady();
    assert.equal((await ready()).generation,generation,'same-version stale collector is repaired from the exact installed generation');
    const state=async()=>{
      const id=await currentTab();
      return worker.evaluate(async id=>(await chrome.scripting.executeScript({target:{tabId:id},func:()=>({ready:globalThis.MoriCollectorInstance?.ready,panels:document.querySelectorAll('[data-mori-collector]').length})}))[0].result,id);
    };
    const start=async()=>{
      await page.mouse.move(70,70);await page.mouse.down();await page.mouse.move(100,105,{steps:7});
      await page.waitForSelector('[data-mori-collector]');
    };
    await start();assert.equal((await state()).panels,1);
    const id=await currentTab();
    await worker.evaluate(async id=>{
      await chrome.scripting.executeScript({target:{tabId:id},func:()=>{
        globalThis.MoriLifecycleFixture={instance:globalThis.MoriCollectorInstance,panel:document.querySelector('[data-mori-collector]')};
      }});
      const files=chrome.runtime.getManifest().content_scripts[0].js;
      await chrome.scripting.executeScript({target:{tabId:id},files});
      await chrome.scripting.executeScript({target:{tabId:id},files});
    },id);
    const busyState=await worker.evaluate(async id=>(await chrome.scripting.executeScript({target:{tabId:id},func:()=>({
      sameInstance:globalThis.MoriCollectorInstance===globalThis.MoriLifecycleFixture.instance,
      samePanel:document.querySelector('[data-mori-collector]')===globalThis.MoriLifecycleFixture.panel,
      panelAttached:globalThis.MoriLifecycleFixture.panel.isConnected,
      busy:globalThis.MoriCollectorInstance.busy,
      panels:document.querySelectorAll('[data-mori-collector]').length
    })}))[0].result,id);
    assert.deepEqual(busyState,{sameInstance:true,samePanel:true,panelAttached:true,busy:true,panels:1},
      'repeated injection preserves the same in-progress drag instead of discarding or duplicating it');
    await page.keyboard.press('Escape');
    await page.mouse.up();
    await page.waitForSelector('[data-mori-collector]',{state:'detached'});
    assert.equal((await state()).panels,0,'Escape dismisses the preserved busy collector');
    assert.equal((await worker.evaluate(async()=>(await chrome.storage.local.get('fixtureSaves')).fixtureSaves || [])).length,0,
      'cancelling the preserved drag never submits a save');
    await worker.evaluate(async id=>{
      const files=chrome.runtime.getManifest().content_scripts[0].js;
      await chrome.scripting.executeScript({target:{tabId:id},files});
      await chrome.scripting.executeScript({target:{tabId:id},files});
    },id);
    const idleState=await worker.evaluate(async id=>(await chrome.scripting.executeScript({target:{tabId:id},func:()=>({
      replaced:globalThis.MoriCollectorInstance!==globalThis.MoriLifecycleFixture.instance,
      oldState:globalThis.MoriLifecycleFixture.instance.state,
      oldPanelAttached:globalThis.MoriLifecycleFixture.panel.isConnected,
      panels:document.querySelectorAll('[data-mori-collector]').length
    })}))[0].result,id);
    assert.deepEqual(idleState,{replaced:true,oldState:'disposed',oldPanelAttached:false,panels:0},
      'idle reinjection retires the previous instance and its panel');
    await waitReady();
    await start();assert.equal((await state()).panels,1,'repeated injection leaves one drag handler');
    await page.keyboard.press('Escape');await page.mouse.up();
    assert.equal((await state()).panels,0,'Escape still dismisses the single collector');
    // The page stays open while the extension context is destroyed and replaced.
    const next=browser.waitForEvent('serviceworker');
    await worker.evaluate(()=>chrome.runtime.reload()).catch(()=>{});
    worker=await next;
    await page.waitForFunction(()=>document.querySelectorAll('[data-mori-collector]').length===0);
    await waitReady();
    assert.equal((await ready()).ready,true,'open page repaired without navigation/reload');
    await start();assert.equal((await state()).panels,1);
    // Locate the native drag target from its stable initial geometry.
    const bounds=await page.evaluate(()=>{
      const host=document.querySelector('[data-mori-collector]');
      return host ? true : false;
    });assert.equal(bounds,true);
    await page.mouse.move(260,220,{steps:8});await page.mouse.up();
    await page.waitForSelector('[data-mori-collector]',{state:'detached'});
    for(let attempt=0;attempt<30;attempt++){
      const result=await worker.evaluate(()=>chrome.storage.local.get('fixtureSaves'));
      if(result.fixtureSaves?.some(call=>call.action==='collectionFeedback'&&call.state==='success'))break;
      await new Promise(resolve=>setTimeout(resolve,100));
    }
    const calls=await worker.evaluate(async()=>(await chrome.storage.local.get('fixtureSaves')).fixtureSaves || []);
    assert.deepEqual(calls.map(call=>[call.action,call.state]),[
      ['collectionFeedback','saving'],['save',''],['collectionFeedback','success']
    ],'one drag emits progress, exactly one save, and success in order: '+JSON.stringify(calls));
    assert.match(calls[0].id,/^[0-9a-f-]{36}$/i);
    assert.equal(calls[2].id,calls[0].id,'progress and success refer to the same collection');
    const records=calls.filter(call=>call.action==='save');
    assert.equal(records.length,1,'one drag creates exactly one save after reload and reinjection: '+JSON.stringify(calls));
    assert.equal(records[0].name,'Synthetic image');assert.deepEqual(errors,[]);
    console.log('PASS: real MV3 same-version generation repair and active-page repair after extension reload; busy injection preserves the same drag, idle injection retires prior collector; drag saves exactly once; Escape closes.');
  }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
