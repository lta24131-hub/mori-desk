const {test}=require('node:test');
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
function client(send){const context=vm.createContext({});vm.runInContext(fs.readFileSync(path.join(__dirname,'../browser-extension/desktop-feedback.js'),'utf8'),context);let id=0;return context.MoriDesktopFeedback.create({send,uuid:()=>String(++id)});}
test('pending and final notification share identity, actual save is not held up by desktop cold start',async()=>{
 let finish,ran=false,started=false;const calls=[];
 const feedback=client(message=>{calls.push(message);return calls.length===1?new Promise(resolve=>finish=resolve):Promise.resolve({ok:true});});
 const work=feedback.collect({name:'图.png',folderName:'设计'},async()=>{ran=true;return {ok:true,id:'saved',name:'原图.png'};},()=>started=true);
 assert(ran);assert.equal(calls[0].state,'saving');finish({ok:true});const result=await work;
 assert(started);assert.equal(result.id,'saved');assert(result.desktopFeedback);assert.equal(calls.length,2);assert.equal(calls[1].id,calls[0].id);assert.equal(calls[1].state,'success');assert.equal(calls[1].name,'原图.png');assert.equal(calls[1].folderName,'设计');
});
test('desktop unavailable leaves save successful, returns page fallback and never retries',async()=>{
 let saves=0,notifications=0;const feedback=client(async()=>{notifications++;throw Error('notification unavailable');});
 const result=await feedback.collect({},async()=>{saves++;return {ok:true,id:'saved'};});
 assert.equal(result.ok,true);assert.equal(result.desktopFeedback,false);assert.equal(saves,1);assert.equal(notifications,1,'failed progress must not cause a second native timeout');
});
test('failed final feedback preserves the exact save result and enables page fallback',async()=>{
 let notifications=0,saves=0;const feedback=client(async()=>({ok:++notifications===1}));
 const result=await feedback.collect({},async()=>{saves++;return {ok:true,id:'saved',name:'saved.png'};});
 assert.equal(result.ok,true);assert.equal(result.id,'saved');assert.equal(result.name,'saved.png');assert.equal(result.desktopFeedback,false);assert.equal(saves,1);assert.equal(notifications,2);
});
test('unavailable notifications preserve duplicate choices and uncertain results without another delivery',async()=>{
 for(const outcome of [{ok:true,choiceRequired:true,choiceToken:'choice'},{ok:false,code:'SAVE_RESULT_UNKNOWN',error:'check library'}]){
  let calls=0;const feedback=client(async()=>{calls++;return {ok:false};});const result=await feedback.collect({},async()=>outcome);
  assert.equal(result.ok,outcome.ok);assert.equal(result.choiceToken,outcome.choiceToken);assert.equal(result.code,outcome.code);assert.equal(result.desktopFeedback,false);assert.equal(calls,1);
 }
});
for(const [result,state] of [[{ok:false,error:'disk'},'error'],[{ok:false,code:'NATIVE_TIMEOUT'},'unknown'],[{ok:true,choiceRequired:true,choiceToken:'choice'},'choice'],[{ok:true,duplicate:true},'success']]){
 test(`desktop reports ${state} without inventing a successful save`,async()=>{const calls=[];const feedback=client(async message=>{calls.push(message);return {ok:true};});const reply=await feedback.collect({},async()=>result);assert.equal(calls.at(-1).state,state);assert.equal(reply.ok,result.ok);assert.equal(reply.choiceToken,result.choiceToken);});
}
test('page disposal cannot lose final feedback, no tab is required',async()=>{const calls=[];const feedback=client(async message=>{calls.push(message);return {ok:true};});const result=await feedback.collect({},async()=>({ok:true}),()=>{throw Error('tab closed');});assert.equal(result.ok,true);assert.equal(calls.at(-1).state,'success');});
test('only bounded inline PNG preview and selected theme are forwarded, no page URL or path',async()=>{const calls=[];const feedback=client(async message=>{calls.push(message);return {ok:true};});await feedback.collect({feedbackPreview:'https://private/image',feedbackTheme:'dark',name:'x'.repeat(999),path:'C:\\anything'},async()=>({ok:true}));assert.equal(calls[0].preview,'');assert.equal(calls[0].theme,'dark');assert.equal(calls[0].name.length,250);assert.equal(calls[0].path,undefined);});
test('concurrent collections cannot replace each other',async()=>{const calls=[];const feedback=client(async message=>{calls.push(message);return {ok:true};});await Promise.all([feedback.collect({name:'one'},async()=>({ok:true})),feedback.collect({name:'two'},async()=>({ok:false}))]);assert.notEqual(calls[0].id,calls[1].id);for(const id of ['1','2'])assert.equal(calls.filter(call=>call.id===id).length,2);});
test('resolving a duplicate updates the same desktop card instead of leaving stale choice feedback',async()=>{
 const calls=[],feedback=client(async message=>{calls.push(message);return {ok:true};}),feedbackId='12345678-1234-1234-1234-123456789abc';
 await feedback.collect({feedbackId},async()=>({ok:true,choiceRequired:true}));
 await feedback.collect({feedbackId},async()=>({ok:true,keptCopy:true}));
 assert.deepEqual(calls.map(call=>call.state),['saving','choice','saving','success']);assert(calls.every(call=>call.id===feedbackId));assert.equal(calls.at(-1).keptCopy,true);
});
