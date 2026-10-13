// Only synthetic extension/profile/library fixtures. Never the user's profile.
const fs=require('node:fs'),path=require('node:path'),os=require('node:os');
const {spawn}=require('node:child_process'),{once}=require('node:events');
const crypto=require('node:crypto'),assert=require('node:assert/strict');
const {chromium}=require('playwright');
(async()=>{
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
  let output='',errors='';host.stdout.on('data',d=>output+=d);host.stderr.on('data',d=>errors+=d);
  const exit=once(host,'exit');let browser;
  try{
    await new Promise((resolve,reject)=>{const deadline=Date.now()+10000;const timer=setInterval(()=>{if(output.includes('READY')){clearInterval(timer);resolve();}else if(host.exitCode!==null||Date.now()>deadline){clearInterval(timer);reject(Error(errors||'Fixture host not ready'));}},50);});
    browser=await chromium.launchPersistentContext(path.join(temp,'profile'),{channel:'msedge',headless:true,ignoreDefaultArgs:['--disable-extensions'],args:['--disable-extensions-except='+extension,'--load-extension='+extension]});
    const worker=browser.serviceWorkers()[0]||await browser.waitForEvent('serviceworker',{timeout:10000});
    assert(worker.url().includes(id));
    const folders=await worker.evaluate(()=>native({action:'folders'}));
    assert.equal(folders.ok,true,JSON.stringify(folders));assert.equal(folders.transport,'local');
    assert.match(folders.nativeDiagnostic,/not found/);assert.equal(folders.folders[0].name,'Browser test');
    const image=fs.readFileSync(path.join(root,'dependencies','qingjie-icon.png')).toString('base64');
    const result=await worker.evaluate(async ({image,folder})=>{
      const message={action:'save',url:'data:image/png;base64,'+image,name:'Browser fallback fixture',source:'https://example.com/synthetic',folder};
      return {saved:await saveImage(message,'test'),duplicate:await saveImage(message,'test')};
    },{image,folder:folders.folders[0].id});
    assert.equal(result.saved.ok,true,JSON.stringify(result.saved));assert.equal(result.saved.transport,'local');assert.equal(result.duplicate.choiceRequired,true);
    await worker.evaluate(token=>discardChoice(token,'test'),result.duplicate.choiceToken);
    const popup=await browser.newPage();await popup.goto('chrome-extension://'+id+'/popup.html');await popup.waitForFunction(()=>document.querySelector('#status').textContent.includes('已连接本机图库'));
    assert.match(await popup.locator('#status').getAttribute('title'),/备用通道/);
    const rejected=await worker.evaluate(()=>fetch('http://127.0.0.1:47891/mori/v1',{method:'POST',headers:{'Content-Type':'application/json','X-Mori-Token':'0'.repeat(64)},body:'{"action":"folders"}'}).then(r=>r.status));assert.equal(rejected,403);
    host.stdin.end('\n');const [code]=await exit;assert.equal(code,0,errors+output);
    console.log('PASS: real MV3 worker/native host-not-found -> authenticated local transport -> category/image/duplicate/popup; invalid key denied.');
  }finally{await browser?.close();if(host.exitCode===null)host.kill();}
})().catch(e=>{console.error(e);process.exitCode=1;});
