const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const {test} = require('node:test');
const script = fs.readFileSync(path.join(__dirname,'../browser-extension/runtime-client.js'),'utf8');
function client(replies, clock = {}) {
  const pending = [], calls = [];
  const chrome = {runtime:{sendMessage(message,callback) {
    calls.push(message); const value = replies.shift();
    if (value?.throw) throw Error(value.throw);
    if (value?.pending) {pending.push(callback); return;}
    chrome.runtime.lastError = value?.error ? {message:value.error} : null;
    callback(value?.reply); chrome.runtime.lastError = null;
  }}};
  const context = vm.createContext({chrome,setTimeout:clock.setTimeout || setTimeout,clearTimeout:clock.clearTimeout || clearTimeout});
  vm.runInContext(script,context);
  return {calls,pending,chrome,api:context.MoriRuntime,send:message=>new Promise(resolve=>context.MoriRuntime.send(message,resolve))};
}
test('invalid extension context gets a precise refresh message, not a lost-save assertion',async()=>{
  const runtime=client([{throw:'Extension context invalidated.'}]);
  const result=await runtime.send({action:'save'});
  assert.equal(result.code,'EXTENSION_RELOADED');assert.match(result.error,/插件已更新/);assert.equal(runtime.calls.length,1);
});
test('only a read retries a temporarily starting worker; a write is never replayed',async()=>{
  const read=client([{error:'Could not establish connection. Receiving end does not exist.'},{reply:{ok:true,folders:[]}}]);
  assert.equal((await read.send({action:'folders'})).ok,true);assert.equal(read.calls.length,2);
  const write=client([{error:'Could not establish connection. Receiving end does not exist.'},{reply:{ok:true}}]);
  assert.equal((await write.send({action:'save'})).code,'EXTENSION_NO_RECEIVER');assert.equal(write.calls.length,1);
});
test('lost write reply tells the user to check the library before retrying',async()=>{
  const runtime=client([{error:'The message port closed before a response was received.'}]);
  const result=await runtime.send({action:'save'});
  assert.equal(result.code,'SAVE_RESULT_UNKNOWN');assert.match(result.error,/检查.*图库/);assert.equal(runtime.calls.length,1);
  const noReply=client([{}]);assert.equal((await noReply.send({action:'save'})).code,'SAVE_RESULT_UNKNOWN');
});
test('duplicate transport callbacks notify the UI once',()=>{
  const runtime=client([{pending:true}]);const results=[];
  runtime.api.send({action:'save'},value=>results.push(value));
  runtime.pending[0]({ok:true});runtime.pending[0]({ok:true});
  assert.equal(results.length,1);assert.equal(runtime.calls.length,1);
});

function clock() {
  let time=0,sequence=0;const pending=new Map();
  return {
    setTimeout(fn,ms){const id=++sequence;pending.set(id,{fn,at:time+ms});return id;},
    clearTimeout(id){pending.delete(id);},
    advance(ms){const end=time+ms;while(true){const next=[...pending].filter(([,task])=>task.at<=end).sort((a,b)=>a[1].at-b[1].at)[0];if(!next)break;time=next[1].at;pending.delete(next[0]);next[1].fn();}time=end;}
  };
}

test('a slow download plus native save can complete without an early unknown-result error',()=>{
  const time=clock(),runtime=client([{pending:true}],time),results=[];
  runtime.api.send({action:'save'},value=>results.push(value));
  // Download (18s) plus a valid native save (28s), both within backend budgets.
  time.advance(46000);assert.equal(results.length,0,'the content deadline must cover the combined backend stages');
  runtime.pending[0]({ok:true});assert.equal(results.length,1);assert.equal(results[0].ok,true);assert.equal(runtime.calls.length,1);
  time.advance(80000);assert.equal(results.length,1);
});

test('malformed results are never presented as confirmed failures or confirmed saves',async()=>{
  for(const reply of [{},'unexpected',true,{ok:'true'}]){
    const runtime=client([{reply}]);const result=await runtime.send({action:'save'});
    assert.equal(result.ok,false);assert.equal(result.code,'SAVE_RESULT_UNKNOWN');assert.match(result.error,/检查.*图库/);
  }
  const read=client([{reply:{}}]);assert.equal((await read.send({action:'folders'})).code,'EXTENSION_UNAVAILABLE');
});

test('duplicated read error callbacks schedule only one safe retry',()=>{
  const time=clock(),runtime=client([{pending:true},{reply:{ok:true,folders:[]}}],time),results=[];
  runtime.api.send({action:'folders'},value=>results.push(value));
  runtime.chrome.runtime.lastError={message:'Could not establish connection. Receiving end does not exist.'};
  runtime.pending[0]();runtime.pending[0]();runtime.chrome.runtime.lastError=null;
  time.advance(150);assert.equal(runtime.calls.length,2);assert.equal(results.length,1);assert.equal(results[0].ok,true);
});

test('the extended save deadline is still bounded and never replays a hung write',()=>{
  const time=clock(),runtime=client([{pending:true}],time),results=[];
  runtime.api.send({action:'save'},value=>results.push(value));time.advance(74999);assert.equal(results.length,0);
  time.advance(1);assert.equal(results.length,1);assert.equal(results[0].code,'SAVE_RESULT_UNKNOWN');assert.equal(runtime.calls.length,1);
  runtime.pending[0]({ok:true});assert.equal(results.length,1,'a late callback cannot replace an already-reported unconfirmed outcome');
});
