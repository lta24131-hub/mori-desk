// Read-only connection probe in a fresh browser profile, never the user's profile.
const {chromium}=require('playwright');
const fs=require('node:fs');
const path=require('node:path');
const os=require('node:os');
const extension=process.argv[2];
if(!extension || !fs.existsSync(path.join(extension,'manifest.json')))throw Error('Extension directory required');
(async()=>{
  const profile=fs.mkdtempSync(path.join(os.tmpdir(),'mori-bridge-probe-'));
  if(process.env.MORI_PROBE_BROWSER==='chrome'){
    // Seed only this newly created synthetic profile using Chromium's normal
    // unpacked-extension loader. No real browser profile or credentials copied.
    const seed=await chromium.launchPersistentContext(profile,{channel:'msedge',headless:true,
      ignoreDefaultArgs:['--disable-extensions'],args:['--disable-extensions-except='+extension,'--load-extension='+extension]});
    await seed.waitForEvent('serviceworker',{timeout:10000}).catch(()=>{});
    await seed.close();
  }
  const browser=await chromium.launchPersistentContext(profile,{channel:process.env.MORI_PROBE_BROWSER||'msedge',headless:true,
    ignoreDefaultArgs:['--disable-extensions'],
    args:['--disable-extensions-except='+extension,'--load-extension='+extension],timeout:20000});
  try {
    const worker=browser.serviceWorkers()[0]||await browser.waitForEvent('serviceworker',{timeout:10000});
    console.log('Worker: '+worker.url());
    const result=await worker.evaluate(async()=>{
      const response=await new Promise(resolve=>chrome.runtime.sendNativeMessage('app.mori.desk',{action:'folders'},reply=>{
        const error=chrome.runtime.lastError;
        resolve({ok:reply?.ok===true,error:error?.message||reply?.error||'',folderCount:reply?.folders?.length});
      }));
      return {extensionId:chrome.runtime.id,...response};
    });
    console.log(JSON.stringify(result));
    if(!result.ok)process.exitCode=1;
  }finally{await browser.close();}
})().catch(error=>{console.error(error.message);process.exitCode=1;});
