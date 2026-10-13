// Isolated desktop lab benchmark. Synthetic images/categories and mocked
// extension messages only; never opens a user profile, host or media library.
const assert=require('node:assert/strict');
const path=require('node:path');
const {chromium}=require('playwright');
const baseline=process.argv.includes('--baseline');
const root=path.resolve(__dirname,'..');
const scripts=['preferences.js','collector-text.js','drag-geometry.js','media-source.js','runtime-client.js','content.js'];
const median=values=>[...values].sort((a,b)=>a-b)[Math.floor(values.length/2)];
(async()=>{
  const browser=await chromium.launch({channel:'msedge',headless:true});
  const page=await browser.newPage({viewport:{width:1100,height:850},locale:'zh-CN'});
  const errors=[];page.on('pageerror',error=>errors.push(error.message));
  try {
    await page.addInitScript(()=>{
      // Open shadows in this test harness only, so real production event code
      // can be exercised without adding test helpers to the extension.
      const attach=Element.prototype.attachShadow;
      Element.prototype.attachShadow=function(options){return attach.call(this,{...options,mode:'open'});};
      window.fixtureCalls=[];window.fixtureListeners=[];window.fixtureReplies=[];window.fixtureFolderFailure=false;window.fixtureFolderPending=false;window.fixtureFolderCount=500;window.hoverOps=0;window.labelWrites=0;
      window.fixtureFolderResponse=()=>fixtureFolderFailure?{ok:false,error:'请先连接 Mori 桌面程序'}:{ok:true,folders:Array.from({length:fixtureFolderCount},(_,i)=>({id:'folder-'+i,name:'分类 '+i}))};
      const toggle=DOMTokenList.prototype.toggle,add=DOMTokenList.prototype.add,remove=DOMTokenList.prototype.remove;
      DOMTokenList.prototype.toggle=function(token,...args){if(token==='hover')hoverOps++;return toggle.call(this,token,...args);};
      DOMTokenList.prototype.add=function(...tokens){hoverOps+=tokens.filter(x=>x==='hover').length;return add.apply(this,tokens);};
      DOMTokenList.prototype.remove=function(...tokens){hoverOps+=tokens.filter(x=>x==='hover').length;return remove.apply(this,tokens);};
      const text=Object.getOwnPropertyDescriptor(Node.prototype,'textContent');
      Object.defineProperty(Node.prototype,'textContent',{...text,set(value){if(this.nodeType===1&&(this.className==='title'||this.className==='hint'))labelWrites++;return text.set.call(this,value);}});
      window.chrome={runtime:{id:'a'.repeat(32),getManifest:()=>({version:'0.2.16'}),onMessage:{addListener(listener){fixtureListeners.push(listener);},removeListener(listener){fixtureListeners=fixtureListeners.filter(value=>value!==listener);}},
        sendMessage(message,callback){fixtureCalls.push(message);if(message.action==='folders'){if(fixtureFolderPending)fixtureReplies.push(callback);else callback(fixtureFolderResponse());return;}callback(message.action==='preferences'?{ok:true,preferences:{theme:'light',language:'zh-CN',collector:'popup',editBeforeSave:false}}:{ok:true});}},
        storage:{onChanged:{addListener(){},removeListener(){}}}};
    });
    await page.route('https://mori-performance-fixture.invalid/**',route=>route.fulfill({contentType:'text/html',body:'<!doctype html><title>Drag lab fixture</title><img id="one" alt="Synthetic benchmark" style="position:absolute;left:24px;top:24px;width:120px;height:100px" src="data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9YL7ZioAAAAASUVORK5CYII=">'}));
    await page.goto('https://mori-performance-fixture.invalid/page');
    for(const script of scripts)await page.addScriptTag({path:path.join(root,'browser-extension',script)});
    const start=()=>page.dispatchEvent('#one','dragstart',{clientX:60,clientY:65,bubbles:true});
    const cancel=()=>page.keyboard.press('Escape');
    const categoryButton=page.locator('[data-mori-collector] .category-toggle');
    const folders=()=>page.evaluate(()=>fixtureCalls.filter(x=>x.action==='folders').length);
    await start();await page.waitForSelector('[data-mori-collector]');
    const queriesBeforePicker=await folders();
    await categoryButton.click();await page.waitForFunction(()=>document.querySelector('[data-mori-collector]')?.shadowRoot.querySelectorAll('.folder').length===500);
    assert.equal(await folders(),1,'classification picker loads current categories once');
    await categoryButton.click();await categoryButton.click();assert.equal(await folders(),1,'reopening the same picker reuses its session result');
    const samples=[];let operations,writes;
    for(let repeat=0;repeat<3;repeat++){
      const result=await page.evaluate(()=>{
        const panel=document.querySelector('[data-mori-collector]').shadowRoot;
        const rows=panel.querySelectorAll('.folder');window.hoverOps=0;window.labelWrites=0;
        const start=performance.now();
        for(const index of [0,1])for(let at=0;at<100;at++){
          const rect=rows[index].getBoundingClientRect();
          rows[index].dispatchEvent(new DragEvent('dragover',{bubbles:true,composed:true,clientX:rect.x+10,clientY:rect.y+10}));
        }
        return{elapsed:performance.now()-start,operations:hoverOps,writes:labelWrites};
      });samples.push(result.elapsed);operations=result.operations;writes=result.writes;
    }
    await cancel();await page.waitForSelector('[data-mori-collector]',{state:'detached'});
    // Cancelled drags should not query the native folder list needlessly.
    const before=await folders();for(let at=0;at<5;at++){await start();await cancel();}
    const cancelledQueries=(await folders())-before;
    if(!baseline){
      assert.equal(queriesBeforePicker,0);assert.equal(cancelledQueries,0);assert(operations<=4,'only the old/new hover rows should change');assert(writes<=2,'stable drag labels should not be rewritten for every event');
      // A failed query leaves the parked gesture usable, with an explicit retry.
      const failedBefore=await folders();
      await page.evaluate(()=>{fixtureFolderFailure=true;});await start();await categoryButton.click();
      const retry=page.locator('[data-mori-collector] .folder-retry');await retry.waitFor({state:'visible'});
      await page.locator('[data-mori-collector] .category-menu').dispatchEvent('drop',{bubbles:true,composed:true});
      assert.equal(await page.locator('[data-mori-collector] .panel').getAttribute('data-phase'),'choosing','releasing over the menu parks the gesture instead of choosing an accidental folder');
      assert.equal(await page.evaluate(()=>fixtureCalls.filter(x=>x.action==='save').length),0);
      await page.evaluate(()=>{fixtureFolderFailure=false;});await retry.click();
      await page.waitForFunction(()=>document.querySelector('[data-mori-collector]')?.shadowRoot.querySelectorAll('.folder').length===500);
      assert.equal(await folders()-failedBefore,2,'one failed query plus one user-requested retry');await cancel();

      // Reopening while the native bridge is still responding does not fork it.
      const pendingBefore=await folders();await page.evaluate(()=>{fixtureFolderPending=true;});await start();await categoryButton.click();await categoryButton.click();await categoryButton.click();
      assert.equal(await folders()-pendingBefore,1,'in-flight category queries are deduplicated');
      await page.evaluate(()=>{fixtureFolderPending=false;fixtureReplies.splice(0).forEach(reply=>reply(fixtureFolderResponse()));});
      await page.waitForFunction(()=>document.querySelector('[data-mori-collector]')?.shadowRoot.querySelectorAll('.folder').length===500);await cancel();

      // Empty categories are still a successful session result, not a cache miss.
      const emptyBefore=await folders();await page.evaluate(()=>{fixtureFolderCount=0;});await start();await categoryButton.click();await categoryButton.click();await categoryButton.click();
      assert.equal(await folders()-emptyBefore,1,'empty successful category results are reused');await cancel();

      // Context-menu editors need the same recoverable category loading path.
      const editorBefore=await folders();await page.evaluate(()=>{
        fixtureFolderFailure=true;fixtureFolderCount=500;
        fixtureListeners.forEach(listener=>listener({action:'context-edit',requestId:'performance-editor',item:{name:'Synthetic editor',url:'https://mori-performance-fixture.invalid/image.png',source:location.href}}, {},()=>{}));
      });
      const editorRetry=page.locator('[data-mori-collector] .editor-error .folder-retry');await editorRetry.waitFor({state:'visible'});
      assert.equal(await page.locator('[data-mori-collector] .editor select').isDisabled(),true);
      const editorInputs=page.locator('[data-mori-collector] .editor input');
      await editorInputs.nth(0).fill('保留编辑中的名称');await editorInputs.nth(1).fill('https://mori-performance-fixture.invalid/edited');
      await page.evaluate(()=>{fixtureFolderFailure=false;});await editorRetry.click();
      await page.waitForFunction(()=>document.querySelector('[data-mori-collector]')?.shadowRoot.querySelector('select')?.options.length===501);
      assert.equal(await page.locator('[data-mori-collector] .editor select').isDisabled(),false);
      assert.equal(await editorInputs.nth(0).inputValue(),'保留编辑中的名称');assert.equal(await editorInputs.nth(1).inputValue(),'https://mori-performance-fixture.invalid/edited');
      assert.equal(await folders()-editorBefore,2,'editor error can be retried without losing name or source fields');await cancel();
    }
    assert.deepEqual(errors,[]);
    console.log(JSON.stringify({kind:baseline?'baseline':'optimized',browser:browser.version(),conditions:'isolated headless Edge, desktop 1100x850, warm synthetic page, no CPU/network throttling, 500 folders, 200 dragover events × 3',queriesBeforePicker,cancelledQueries,hoverOperations:operations,labelWrites:writes,medianMilliseconds:Number(median(samples).toFixed(2)),rangeMilliseconds:samples.map(x=>Number(x.toFixed(2)))}));
  }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
