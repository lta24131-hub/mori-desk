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
  fs.appendFileSync(path.join(extension,'background.js'),`\n// Synthetic test endpoint only.\nnative = async payload => {
    if (payload.action === 'folders') return {ok:true,folders:[]};
    const saved=(await chrome.storage.local.get('fixtureSaves')).fixtureSaves || [];
    saved.push({action:payload.action,name:payload.name});
    await chrome.storage.local.set({fixtureSaves:saved});
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
      const files=chrome.runtime.getManifest().content_scripts[0].js;
      await chrome.scripting.executeScript({target:{tabId:id},files});
      await chrome.scripting.executeScript({target:{tabId:id},files});
    },id);
    assert.equal((await state()).panels,0,'reinjection disposes the visible old collector');
    await waitReady();
    await page.mouse.up();
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
      if(result.fixtureSaves?.length)break;
      await new Promise(resolve=>setTimeout(resolve,100));
    }
    const records=await worker.evaluate(async()=>(await chrome.storage.local.get('fixtureSaves')).fixtureSaves || []);
    assert.equal(records.length,1,'one drag creates exactly one save after reload and reinjection');
    assert.equal(records[0].name,'Synthetic image');assert.deepEqual(errors,[]);
    console.log('PASS: real MV3 active-page repair after extension reload; repeated injection retires prior collector; drag saves exactly once; Escape closes.');
  }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
