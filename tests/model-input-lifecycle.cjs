// Executes the exact desktop-host injection, not a copied implementation. It
// uses synthetic responses and never opens a browser or reads a user's model.
const assert = require('node:assert/strict');
const {test} = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname,'../src/ModelWindow.cs'),'utf8');
// Scope the injection to the input-loading method: other host helpers (such
// as ThemeAccentScript) also declare a variable named script.
const loadSelected = source.match(/\basync\s+void\s+LoadSelected\s*\(\s*\)\s*\{(.*?)\}\s*\r?\n/s);
assert.ok(loadSelected,'ModelWindow.LoadSelected method must remain available to the input lifecycle tests');
const injection = loadSelected[1].match(/string script=(.*?);await web\.ExecuteScriptAsync\(script\)/s);
assert.ok(injection,'LoadSelected must inject its actual selected-model loading script');
const expression = injection[1];
function script(url,name) {
  return vm.runInNewContext(expression,{modelUrl:url,name:JSON.stringify(name)});
}
function fixture() {
  const requests = [], changes = [], messages = [];
  const input = {files:null,dispatchEvent(event){changes.push({event,files:this.files});}};
  const context = vm.createContext({
    window:{},AbortController,Event,
    File:class {constructor(parts,name,options){this.parts=parts;this.name=name;this.type=options.type;}},
    DataTransfer:class {constructor(){this.files=[];this.items={add:file=>this.files.push(file)};}},
    document:{querySelector:()=>input},
    chrome:{webview:{postMessage:value=>messages.push(value)}},
    fetch:(url,options)=>new Promise((resolve,reject)=>{
      const request={url,options,resolve,reject,aborted:false};requests.push(request);
      options.signal.addEventListener('abort',()=>{request.aborted=true;const error=new Error('aborted');error.name='AbortError';reject(error);},{once:true});
    }),
  });
  return {context,requests,changes,messages};
}
test('a replacement model aborts the earlier fetch and only dispatches its own input',async()=>{
  const f=fixture();const old=vm.runInContext(script('https://mori-input.local/first','first.step'),f.context);
  const latest=vm.runInContext(script('https://mori-input.local/second','参考 \' second.STEP'),f.context);
  assert.equal(f.requests.length,2);assert.equal(f.requests[0].aborted,true);
  f.requests[1].resolve({ok:true,blob:async()=>({synthetic:true})});
  await Promise.all([old,latest]);
  assert.equal(f.changes.length,1);assert.equal(f.changes[0].files[0].name,"参考 ' second.STEP");
  assert.equal(f.messages.length,0);assert.equal(f.context.window.__deskLoadAbort,null);
});
test('superseding a response already reading its body cannot inject the stale model',async()=>{
  const f=fixture();let releaseBody;
  const old=vm.runInContext(script('https://mori-input.local/first','first.step'),f.context);
  f.requests[0].resolve({ok:true,blob:()=>new Promise(resolve=>{releaseBody=resolve;})});
  await new Promise(resolve=>setImmediate(resolve));assert.equal(typeof releaseBody,'function');
  const latest=vm.runInContext(script('https://mori-input.local/second','second.step'),f.context);
  assert.equal(f.requests[0].aborted,true);releaseBody({synthetic:true});
  f.requests[1].resolve({ok:true,blob:async()=>({synthetic:true})});
  await Promise.all([old,latest]);assert.equal(f.changes.length,1);assert.equal(f.changes[0].files[0].name,'second.step');
  assert.equal(f.messages.length,0);
});
test('a failed selected model reports one useful error and clears its cancellation owner',async()=>{
  const f=fixture();const current=vm.runInContext(script('https://mori-input.local/failed','failed.step'),f.context);
  f.requests[0].resolve({ok:false});await current;
  assert.equal(f.changes.length,0);assert.equal(f.messages.length,1);
  assert.match(f.messages[0],/^desk-error:/);assert.equal(f.context.window.__deskLoadAbort,null);
});
