// Only synthetic extension/profile/library fixtures. Never the user's profile.
const fs=require('node:fs'),path=require('node:path'),os=require('node:os');
const {spawn}=require('node:child_process');
const crypto=require('node:crypto'),assert=require('node:assert/strict');
const {chromium}=require('playwright');
const {readyTimeout,trackHost}=require('./browser-fixture-diagnostics.cjs');
(async()=>{
  const startupTimeoutMs=readyTimeout(10000);
  const root=path.resolve(__dirname,'..'),runtime=path.resolve(root,process.argv[2]||'browser-bridge-candidate');
  const temp=fs.mkdtempSync(path.join(os.tmpdir(),'MoriDesk-browser-test-')),extension=path.join(temp,'extension');
  fs.cpSync(path.join(root,'browser-extension'),extension,{recursive:true});
  const {publicKey}=crypto.generateKeyPairSync('rsa',{modulusLength:2048});
  const key=publicKey.export({type:'spki',format:'der'});
  const id=crypto.createHash('sha256').update(key).digest('hex').slice(0,32).replace(/[0-9a-f]/g,x=>String.fromCharCode(97+parseInt(x,16)));
  const manifest=JSON.parse(fs.readFileSync(path.join(extension,'manifest.json'),'utf8'));manifest.key=key.toString('base64');
  fs.writeFileSync(path.join(extension,'manifest.json'),JSON.stringify(manifest));
  // Real browser-native failure, not a mocked reply. This temporary host name is
  // not registered, forcing the same failure reported by the user's Chrome.
  const bg=path.join(extension,'background.js');fs.writeFileSync(bg,fs.readFileSync(bg,'utf8').replace("const HOST = 'app.mori.desk';","const HOST = 'app.mori.unregistered_test';"));
  const host=spawn(path.join(runtime,'Mori.exe'),['--browser-bridge-test',temp,id],{windowsHide:true,stdio:['pipe','pipe','pipe']});
  const diagnostics=trackHost(host,'local-fallback');let browser,popup;
  try{
    await new Promise((resolve,reject)=>{const deadline=Date.now()+startupTimeoutMs;const timer=setInterval(()=>{try{diagnostics.assertRunning();if(/^READY\r?$/m.test(diagnostics.stdout)){clearInterval(timer);resolve();}else if(Date.now()>deadline){clearInterval(timer);reject(diagnostics.failure('Fixture READY deadline exceeded ('+startupTimeoutMs+' ms)'));}}catch(error){clearInterval(timer);reject(error);}},50);});
    diagnostics.setPhase('ready-launching-isolated-edge');
    browser=await chromium.launchPersistentContext(path.join(temp,'profile'),{channel:'msedge',headless:true,ignoreDefaultArgs:['--disable-extensions'],args:['--disable-extensions-except='+extension,'--load-extension='+extension]});
    const worker=browser.serviceWorkers()[0]||await browser.waitForEvent('serviceworker',{timeout:10000});
    assert(worker.url().includes(id));
    diagnostics.setPhase('edge-worker-ready-local-save-and-permission-checks');
    const health=await worker.evaluate(()=>native({action:'bridgeStatus'}));
    assert.equal(health.ok,true,JSON.stringify(health));assert.equal(health.transport,'local');
    assert.match(health.nativeDiagnostic,/not found/);
    const folders=await worker.evaluate(()=>native({action:'folders'}));
    assert.equal(folders.ok,true,JSON.stringify(folders));assert.equal(folders.transport,'local');
    assert.match(folders.nativeDiagnostic,/not found/);assert.equal(folders.folders[0].name,'Browser test');
    const image=fs.readFileSync(path.join(root,'dependencies','qingjie-icon.png')).toString('base64');
    const mismatched=await worker.evaluate(({image,folder})=>saveImage({action:'save',kind:'video',url:'data:image/png;base64,'+image,name:'Cover is not the video',source:'https://example.com/reel',folder},'test'),{image,folder:folders.folders[0].id});
    assert.equal(mismatched.ok,false,JSON.stringify(mismatched));assert.equal(mismatched.transport,'local');assert.match(mismatched.error,/而不是可保存的视频/);
    const result=await worker.evaluate(async ({image,folder})=>{
      const message={action:'save',url:'data:image/png;base64,'+image,name:'Browser fallback fixture',source:'https://example.com/synthetic',folder};
      return {saved:await saveImage(message,'test'),duplicate:await saveImage(message,'test')};
    },{image,folder:folders.folders[0].id});
    assert.equal(result.saved.ok,true,JSON.stringify(result.saved));assert.equal(result.saved.transport,'local');assert.equal(result.duplicate.choiceRequired,true);
    await worker.evaluate(token=>discardChoice(token,'test'),result.duplicate.choiceToken);
    const rejected=await worker.evaluate(()=>fetch('http://127.0.0.1:47891/mori/v1',{method:'POST',headers:{'Content-Type':'application/json','X-Mori-Token':'0'.repeat(64)},body:'{"action":"folders"}'}).then(r=>r.status));assert.equal(rejected,403);
    // A normal extension tab is deliberately NOT a trusted action popup.
    // Keep that production security boundary; exercise the real action instead.
    // Some headless Edge versions cannot expose an action target to Playwright.
    // Record this UI limit separately from the strict real transport assertions.
    diagnostics.setPhase('transport-assertions-complete-checking-action-popup');
    const popupObserved=browser.waitForEvent('page',{timeout:10000}).catch(()=>null);
    let popupApiError=null;
    try {await worker.evaluate(()=>chrome.action.openPopup());}catch(error){popupApiError=error.message;}
    popup=await popupObserved;
    const popupContexts=await worker.evaluate(async()=>chrome.runtime.getContexts?chrome.runtime.getContexts({contextTypes:['POPUP']}).then(items=>items.map(item=>({contextType:item.contextType,documentUrl:item.documentUrl}))):null).catch(()=>null);
    if(popup){
      await popup.waitForURL('chrome-extension://'+id+'/popup.html',{timeout:10000});
      assert.equal(popup.url(),'chrome-extension://'+id+'/popup.html');
      await popup.waitForFunction(()=>/^(已连接 Nook|Connected to Nook)$/.test(document.querySelector('#status').textContent));
      assert.match(await popup.locator('#status').getAttribute('title'),/备用通道/);
      console.log('ACTION_POPUP_UI '+JSON.stringify({status:'passed',playwrightPageObserved:true,actualActionPopupContextPresent:popupContexts?.some(item=>item.documentUrl==='chrome-extension://'+id+'/popup.html')??null,popupContexts}));
      await popup.close();popup=null;
    }else{
      console.log('ACTION_POPUP_UI '+JSON.stringify({status:'not-observed',playwrightPageObserved:false,actualActionPopupContextPresent:popupContexts?.some(item=>item.documentUrl==='chrome-extension://'+id+'/popup.html')??null,popupContexts,reason:popupApiError||'Headless Edge did not expose the action popup as a page. Popup UI is not accepted by this run.'}));
    }
    diagnostics.setPhase('assertions-complete-awaiting-host-validation');
    host.stdin.end('\n');const [code]=await diagnostics.exit;assert.equal(code,0,JSON.stringify(diagnostics.snapshot()));
    console.log('PASS CORE: real MV3 worker/native host-not-found -> authenticated local health/category/image/duplicate; invalid key denied; host validated the saved library. See ACTION_POPUP_UI for the separate UI result.');
  }catch(error){
    if(popup)try{console.error('POPUP_DIAGNOSTIC '+JSON.stringify(await popup.locator('#status').evaluate(node=>({text:node.textContent,title:node.title,language:document.documentElement.lang}))));}catch{}
    console.error('FIXTURE_FAILURE '+JSON.stringify(diagnostics.snapshot()));throw error;
  }finally{await browser?.close();if(host.exitCode===null)host.kill();}
})().catch(e=>{console.error(e);process.exitCode=1;});
