const {test,before,after} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const {pathToFileURL} = require('node:url');
const {chromium} = require('playwright');
const root = path.resolve(__dirname,'..');
const context=vm.createContext({URL});vm.runInContext(fs.readFileSync(path.join(root,'browser-extension/preferences.js'),'utf8'),context);const P=context.MoriPreferences;
test('preferences default light, normalize malformed data and never retain page query/fragment',()=>{
  assert.equal(P.normalize().theme,'light');assert.equal(P.normalize(null).collector,'popup');
  const p=P.normalize({theme:'hack',language:'fr',collector:'other',editBeforeSave:'yes',disabledPages:['https://example.com/a?token=secret#x','file:///c:/a','javascript:x'],disabledHosts:['example.com','evil/com','https://bad.com']});
  assert.equal(p.editBeforeSave,false);assert.deepEqual(Array.from(p.disabledPages),['https://example.com/a']);assert.deepEqual(Array.from(p.disabledHosts),['example.com']);
  assert.equal(P.language(P.normalize(),'en-US'),'en');assert.equal(P.language(P.normalize(),'zh-TW'),'zh-CN');
});
test('page and domain exclusion rules remain scoped, with explicit enable removing only applicable rules',()=>{
  let p=P.withMode({},'https://a.example.com/path?x=1','page');
  assert.equal(P.mode(p,'https://a.example.com/path?x=2'),'page');assert.equal(P.mode(p,'https://a.example.com/other'),'enabled');
  p=P.withMode(p,'https://example.com','host');p=P.withMode(p,'https://unrelated.com','host');
  assert.equal(P.mode(p,'https://a.example.com/other'),'host');assert.equal(P.mode(p,'https://notexample.com'),'enabled');
  p=P.withMode(p,'https://a.example.com/path','enabled');assert.equal(P.mode(p,'https://a.example.com/path'),'enabled');assert.equal(P.mode(p,'https://unrelated.com'),'host');
  assert.equal(P.withMode({collector:'disabled'},'https://example.com','enabled').collector,'popup');
});
let browser;before(async()=>{browser=await chromium.launch({headless:true,channel:'msedge'});});after(async()=>{await browser?.close();});
async function mockPage(prefs={},width=420) {
  const page=await browser.newPage({viewport:{width,height:680},locale:'zh-CN'});const errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.addInitScript(prefs=>{
    const changed=[];window.testStore={moriPreferences:prefs};window.storageFailure=false;
    window.chrome={storage:{onChanged:{addListener:f=>changed.push(f)},local:{get:async key=>({[key]:testStore[key]}),set:async values=>{if(storageFailure)throw Error('test');for(const key in values){const oldValue=testStore[key];testStore[key]=values[key];for(const f of changed)f({[key]:{oldValue,newValue:values[key]}},'local');}}}},tabs:{query:async()=>[{url:'https://sub.example.com/a?secret=1#x'}]},runtime:{sendMessage:(m,cb)=>cb({ok:true,folders:[]})}};
  },prefs);
  await page.goto(pathToFileURL(path.join(root,'browser-extension/popup.html')).href);await page.waitForFunction(()=>!document.querySelector('#drag-mode').disabled);
  return {page,errors};
}
test('popup initial 80px measurement cannot squeeze its content; themes and languages fit the intended width',async()=>{
  for(const language of ['zh-CN','en'])for(const theme of ['light','dark']) {
    const {page,errors}=await mockPage({language,theme},80);try{
      assert.equal((await page.locator('body').boundingBox()).width,376);
      assert.equal((await page.locator('html').boundingBox()).width,376);
      await page.setViewportSize({width:376,height:600});
      await page.click('#open-settings');
      assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth),376);
      assert.equal(await page.locator('.choices').first().evaluate(n=>n.scrollWidth<=n.clientWidth),true);
      await page.screenshot({path:path.join(__dirname,'browser-artifacts/popup-fixed-'+language+'-'+theme+'.png')});
      assert.deepEqual(errors,[]);
    }finally{await page.close();}
  }
});

test('Chrome bookmarks are read only after permission and saved only after import confirmation',async()=>{
  const {page,errors}=await mockPage();try{
    await page.evaluate(()=>{window.permissionReads=0;window.bookmarkReads=0;window.bookmarkWrites=[];chrome.permissions={request:async()=>{permissionReads++;return true;}};chrome.bookmarks={getTree:async()=>{bookmarkReads++;return[{title:'',children:[{title:'书签栏',children:[{title:'Design',url:'https://example.com/design'},{title:'Unsafe',url:'javascript:bad'}]}]}];}};chrome.runtime.sendMessage=(message,reply)=>{bookmarkWrites.push(message);reply({ok:true,root:'root',added:1,reused:0,skipped:0});};});
    assert.equal(await page.evaluate(()=>bookmarkReads),0);await page.click('#import-bookmarks');await page.waitForFunction(()=>document.querySelector('#bookmark-count').textContent.includes('1 个网址'));
    assert.equal(await page.evaluate(()=>permissionReads),1);assert.equal(await page.evaluate(()=>bookmarkWrites.length),0);assert.match(await page.locator('#bookmark-count').innerText(),/跳过 1/);
    await page.screenshot({path:path.join(__dirname,'browser-artifacts/bookmark-import-review.png')});await page.click('#bookmark-confirm');await page.waitForFunction(()=>document.querySelector('#bookmark-progress').textContent.includes('网址收藏'));
    const saved=await page.evaluate(()=>bookmarkWrites[0]);assert.equal(saved.action,'importBookmarks');assert.deepEqual(saved.items[0].path,['书签栏']);assert.equal(saved.items[0].url,'https://example.com/design');assert.deepEqual(errors,[]);
  }finally{await page.close();}
});
test('denied optional bookmark permission neither reads the tree nor imports',async()=>{
  const {page}=await mockPage();try{await page.evaluate(()=>{chrome.permissions={request:async()=>false};chrome.bookmarks={getTree:()=>{throw Error('must not read');}};});await page.click('#import-bookmarks');await page.waitForFunction(()=>document.querySelector('#bookmark-progress').textContent.includes('未授权'));assert.equal(await page.locator('#bookmark-review').isVisible(),false);}finally{await page.close();}
});
test('popup saves scoped mode, theme/language/layout/edit options and restores existing preferences',async()=>{
  const {page,errors}=await mockPage();try{
    await page.selectOption('#drag-mode','page');await page.waitForFunction(()=>testStore.moriPreferences.disabledPages?.length===1);
    assert.equal(await page.evaluate(()=>testStore.moriPreferences.disabledPages[0]),'https://sub.example.com/a');
    await page.click('#open-settings');await page.locator('input[name=theme][value=dark]').check();await page.waitForFunction(()=>document.documentElement.dataset.theme==='dark');
    await page.locator('input[name=collector][value=bottom]').check();await page.check('#edit');await page.selectOption('#language','en');await page.waitForFunction(()=>document.documentElement.lang==='en');
    assert.equal(await page.locator('#heading').innerText(),'Preferences');assert.equal(await page.locator('input[name=collector][value=bottom]').isChecked(),true);
    await page.screenshot({path:path.join(__dirname,'browser-artifacts/preferences-dark-en.png')});
    await page.locator('input[name=theme][value=light]').check();await page.selectOption('#language','zh-CN');await page.waitForFunction(()=>document.documentElement.lang==='zh-CN');
    await page.screenshot({path:path.join(__dirname,'browser-artifacts/preferences-light.png')});
    await page.click('#exclusions summary');await page.click('#rules button');await page.waitForFunction(()=>testStore.moriPreferences.disabledPages.length===0);
    const saved=await page.evaluate(()=>testStore.moriPreferences);assert.equal(saved.editBeforeSave,true);assert.equal(saved.collector,'bottom');
    const reopened=await mockPage(saved);try{await reopened.page.click('#open-settings');assert.equal(await reopened.page.isChecked('#edit'),true);assert.equal(await reopened.page.isChecked('input[name=collector][value=bottom]'),true);}finally{await reopened.page.close();}
    assert.equal(await page.locator('input[name=download]').count(),0);assert.deepEqual(errors,[]);
  }finally{await page.close();}
});
test('popup follows system only when selected; storage failures are not shown as saved',async()=>{
  const {page,errors}=await mockPage({theme:'system'});try{
    await page.emulateMedia({colorScheme:'dark'});await page.waitForFunction(()=>document.documentElement.dataset.theme==='dark');
    await page.click('#open-settings');await page.evaluate(()=>window.storageFailure=true);await page.click('#edit');await page.waitForFunction(()=>document.querySelector('#message').classList.contains('error'));
    assert.equal(await page.isChecked('#edit'),false);assert.deepEqual(errors,[]);
  }finally{await page.close();}
});
