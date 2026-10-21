// Pure update-state tests: no real browser, profile, native host or library.
const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');
const source=fs.readFileSync(path.join(__dirname,'../browser-extension/updater.js'),'utf8');
const deferred=()=>{let resolve;const promise=new Promise(r=>resolve=r);return{promise,resolve};};
function fixture(settings={}) {
  const calls=[],messages=[],timers=new Map(),stored={...(settings.stored||{})};
  let sequence=0,reloads=0,time=0,alarm;
  const browser={
    storage:{local:{get:async()=>settings.initial?settings.initial.promise:{...stored},set:async values=>{if(settings.storageFails)throw Error('storage unavailable');if(settings.storageSet)await settings.storageSet(values,api);Object.assign(stored,values);}}},
    runtime:{reload(){if(settings.reloadThrows)throw Error('reload failed');reloads++;}},
    tabs:{query:async()=>{calls.push({kind:'tabs'});return[{id:1}];},sendMessage:async(tabId,message,{frameId})=>{
      messages.push({tabId,frameId,...message});
      if(settings.message)return settings.message(message,frameId,api);
      return{ready:true,lock:message.lock};
    }},
    scripting:{executeScript:async()=>{
      calls.push({kind:'frames'});
      if(settings.frames)return settings.frames(api);
      return[0,4].map(frameId=>({frameId,result:{collector:true,protocol:1}}));
    }},
    alarms:{onAlarm:{addListener(fn){alarm=fn;}},create(name,value){calls.push({kind:'alarm',name,value});if(settings.alarmFails)return Promise.reject(Error('alarm unavailable'));}}
  };
  const context=vm.createContext({setTimeout,clearTimeout,MoriInstalledGeneration:settings.generation});vm.runInContext(source,context);
  const updater=vm.runInContext('MoriUpdater',context).create({chrome:browser,
    uuid:()=>`00000000-0000-4000-8000-${String(++sequence).padStart(12,'0')}`,
    now:()=>time,setTimeout:(callback,ms)=>{const id=++sequence;timers.set(id,{callback,ms});return id;},clearTimeout:id=>timers.delete(id),
    configured:()=>settings.unpaired!==true,
    identity:()=>({installationId:'fixture-install',extensionId:'a'.repeat(32),version:'0.3.1'}),
    extraBusy:()=>!!settings.extraBusy,
    request:async message=>{calls.push(message);if(settings.request)return settings.request(message,api);return message.op==='poll'?{ok:true,update:{version:'0.3.2',token:'private-apply-token'}}:{ok:true,reload:true};}
  });
  const api={updater,calls,messages,timers,stored,browser,settings,context,get reloads(){return reloads;},advance(ms){time+=ms;},alarm:()=>alarm({name:'mori-extension-update'})};
  return api;
}
function popup(f) {
  let disconnect;const received=[];
  f.updater.popup({postMessage:message=>received.push(message),onDisconnect:{addListener:fn=>disconnect=fn}});
  return{received,close:()=>disconnect()};
}

test('idle update votes every frame, verifies locks and reloads only after apply acknowledgement',async()=>{
  const f=fixture();await f.updater.poll();
  assert.equal(f.reloads,1);assert.equal(f.calls.filter(x=>x.op==='apply').length,1);
  assert.deepEqual(f.messages.filter(x=>x.action==='mori-update-prepare').map(x=>x.frameId),[0,4]);
  assert.equal(f.messages.filter(x=>x.action==='mori-update-verify').length,4);
  const apply=f.calls.find(x=>x.op==='apply');assert.match(apply.clientId,/^[0-9a-f]{32}$/);assert.equal(apply.token,'private-apply-token');
  assert.equal(JSON.stringify(f.updater.status()).includes('private-apply-token'),false);
  assert.equal(f.updater.begin(),null);
});

test('unpaired updater never polls a native process or discovers browser frames',async()=>{
  const f=fixture({unpaired:true});await f.updater.poll();assert.equal(f.calls.length,0);assert.equal(f.updater.status().reason,'unpaired');
});

test('an in-flight save or pending duplicate blocks update without replaying collection',async()=>{
  for(const duplicate of [false,true]){
    const f=fixture({extraBusy:duplicate});const finish=duplicate?null:f.updater.begin();
    await f.updater.poll();assert.equal(f.reloads,0);assert.equal(f.calls.some(x=>x.op==='apply'),false);assert.equal(f.updater.status().reason,'busy');
    finish?.();finish?.();f.settings.extraBusy=false;await f.updater.poll();assert.equal(f.reloads,1);
    assert.equal(f.calls.some(x=>['save','bookmark','importBookmarks'].includes(x.action)),false);
  }
});

test('open popup defers update and receives safe status; closing lets idle update finish',async()=>{
  const f=fixture(),p=popup(f);await f.updater.poll();assert.equal(f.reloads,0);assert.equal(f.updater.status().reason,'popup');
  assert.equal(JSON.stringify(p.received).includes('private-apply-token'),false);assert.equal(f.updater.status().popupOpen,true);
  p.close();await f.updater.poll();assert.equal(f.reloads,1);
});

test('lost write is sticky across worker restarts until user explicitly checks the library',async()=>{
  const f=fixture();f.updater.observeWrite({ok:false,code:'NATIVE_HOST_DISCONNECTED'});await f.updater.poll();
  assert.equal(f.updater.status().uncertain,true);assert.equal(f.stored.moriUpdateUncertainWrite,true);assert.equal(f.reloads,0);
  const restarted=fixture({stored:f.stored});await restarted.updater.poll();assert.equal(restarted.reloads,0);
  const p=popup(restarted);await restarted.updater.acknowledge();assert.equal(restarted.stored.moriUpdateUncertainWrite,false);assert.equal(restarted.updater.status().uncertain,false);
  await restarted.updater.poll();assert.equal(restarted.reloads,0);p.close();await restarted.updater.poll();assert.equal(restarted.reloads,1);
  assert.equal(restarted.calls.some(x=>x.action==='save'),false);
});

test('an old asynchronous storage read cannot clear a newly uncertain write',async()=>{
  const initial=deferred(),f=fixture({initial});f.updater.observeWrite({code:'SAVE_RESULT_UNKNOWN'});initial.resolve({moriUpdateUncertainWrite:false});
  await f.updater.poll();assert.equal(f.updater.status().uncertain,true);assert.equal(f.reloads,0);
});

test('pre-write journal is durable before transmission and a crashed worker leaves an uncertain outcome',async()=>{
  const f=fixture(),finish=await f.updater.beforeWrite();
  assert.equal(f.stored.moriUpdatePendingWrite,true);assert.equal(f.updater.status().busy,true);
  await f.updater.poll();assert.equal(f.reloads,0);
  const restarted=fixture({stored:f.stored});await restarted.updater.poll();
  assert.equal(restarted.updater.status().uncertain,true);assert.equal(restarted.reloads,0);
  await restarted.updater.acknowledge();assert.equal(restarted.stored.moriUpdatePendingWrite,false);await restarted.updater.poll();assert.equal(restarted.reloads,1);
  await finish({ok:true});assert.equal(f.stored.moriUpdatePendingWrite,false);
  assert.deepEqual(Object.keys(f.stored).sort(),['moriUpdateClientId','moriUpdatePendingWrite','moriUpdateUncertainWrite']);
});

test('concurrent native saves cannot clear one another\'s journal; unknown final outcome stays sticky',async()=>{
  const f=fixture(),first=await f.updater.beforeWrite(),second=await f.updater.beforeWrite();
  await first({ok:true});await first({ok:true});assert.equal(f.stored.moriUpdatePendingWrite,true);assert.equal(f.updater.status().busy,true);
  await second({ok:false,code:'NATIVE_TIMEOUT'});assert.equal(f.stored.moriUpdatePendingWrite,false);assert.equal(f.stored.moriUpdateUncertainWrite,true);assert.equal(f.updater.status().busy,false);
  await f.updater.poll();assert.equal(f.reloads,0);
});

test('journal storage failure refuses transmission instead of allowing an untracked save',async()=>{
  const f=fixture();const p=popup(f);await f.updater.poll();f.settings.storageFails=true;
  await assert.rejects(f.updater.beforeWrite(),/journal is unavailable/);assert.equal(f.updater.status().busy,false);assert.equal(f.updater.status().uncertain,true);p.close();await f.updater.poll();assert.equal(f.reloads,0);
});

test('a new unknown response during user acknowledgement is not accidentally cleared',async()=>{
  const gate=deferred(),started=deferred();
  const f=fixture({stored:{moriUpdateUncertainWrite:true},storageSet:async values=>{if(values.moriUpdateUncertainWrite===false){started.resolve();await gate.promise;}}});
  const acknowledgement=f.updater.acknowledge();await started.promise;f.updater.observeWrite({code:'SAVE_RESULT_UNKNOWN'});gate.resolve();await acknowledgement;
  assert.equal(f.updater.status().uncertain,true);assert.equal(f.stored.moriUpdateUncertainWrite,true);await f.updater.poll();assert.equal(f.reloads,0);
});

test('acknowledgement is ignored while saves are active and storage failures fail closed',async()=>{
  const f=fixture();await f.updater.poll(); // initializes stable profile identity
  // A fresh worker avoids the successful-update frozen state in this fixture.
  const other=fixture({stored:f.stored});other.updater.observeWrite({code:'NATIVE_TIMEOUT'});const finish=other.updater.begin();
  await other.updater.acknowledge();assert.equal(other.updater.status().uncertain,true);finish();
  const failed=fixture({storageFails:true});await failed.updater.poll();assert.equal(failed.reloads,0);assert.equal(failed.calls.length,0);
});

test('busy frame, changed frame set and older collector all defer and release prepared locks',async()=>{
  const variants=[
    {message:(message,frameId)=>({ready:frameId!==4,lock:message.lock})},
    {frames:f=>f.calls.filter(x=>x.kind==='frames').length>1?[{frameId:0,result:{collector:true,protocol:1}}]:[0,4].map(frameId=>({frameId,result:{collector:true,protocol:1}}))},
    {frames:()=>[{frameId:0,result:{collector:true,protocol:0}}]}
  ];
  for(const config of variants){const f=fixture(config);await f.updater.poll();assert.equal(f.reloads,0);assert.equal(f.calls.some(x=>x.op==='apply'),false);assert.equal(f.updater.status().reason,'deferred');assert.notEqual(f.updater.begin(),null);}
});

test('invalid or expired lock can never pass verification',async()=>{
  for(const badLock of [null,'another-lock']){
    const f=fixture({message:message=>({ready:true,lock:message.action==='mori-update-verify'?badLock:message.lock})});
    await f.updater.poll();assert.equal(f.reloads,0);assert.equal(f.calls.some(x=>x.op==='apply'),false);
    assert.equal(f.messages.some(x=>x.action==='mori-update-resume'),true);
  }
});

test('failed, absent or uncertain apply acknowledgement never reloads',async()=>{
  for(const reply of [{ok:false},{ok:true},null]){
    const f=fixture({request:async message=>message.op==='poll'?{ok:true,update:{version:'0.3.2',token:'token'}}:reply});
    await f.updater.poll();assert.equal(f.reloads,0);assert.equal(f.updater.isFrozen(),false);assert.equal(f.messages.some(x=>x.action==='mori-update-resume'),true);
  }
});

test('popup opening or expired overall deadline after apply defers reload without retrying writes',async()=>{
  for(const kind of ['popup','deadline']){
    const f=fixture({request:async(message,api)=>{
      if(message.op==='poll')return{ok:true,update:{version:'0.3.2',token:'token'}};
      assert.equal(api.updater.begin(),null);
      if(kind==='popup')popup(api);else api.advance(14000);
      return{ok:true,reload:true};
    }});await f.updater.poll();assert.equal(f.reloads,0);assert.equal(f.updater.isFrozen(),false);assert.equal(f.calls.filter(x=>x.op==='apply').length,1);
  }
});

test('late final frame verification also respects the overall lock deadline',async()=>{
  const f=fixture({message:(message,frameId,api)=>{if(message.action==='mori-update-verify'&&api.calls.some(x=>x.op==='apply'))api.advance(7000);return{ready:true,lock:message.lock};}});
  await f.updater.poll();assert.equal(f.reloads,0);assert.equal(f.updater.isFrozen(),false);
});

test('multi-profile desktop refusal happens before any frame preparation',async()=>{
  const f=fixture({request:async()=>({ok:true,multipleClients:true,update:{version:'0.3.2',token:'token'}})});
  await f.updater.poll();assert.equal(f.updater.status().reason,'multipleClients');assert.equal(f.messages.length,0);assert.equal(f.reloads,0);
});

test('stable profile client ID is reused and legacy UUID IDs are safely replaced',async()=>{
  for(const saved of ['f'.repeat(32),'legacy-profile-identifier']){
    const f=fixture({stored:{moriUpdateClientId:saved}});await f.updater.poll();
    const id=f.calls.find(x=>x.op==='poll').clientId;assert.match(id,/^[a-f0-9]{32}$/);
    assert.equal(id===saved,saved.length===32);assert.equal(id,f.stored.moriUpdateClientId);
  }
});

test('update control reports only the generation captured by this loaded worker',async()=>{
  const generation='a'.repeat(32),f=fixture({generation});
  // Loading new disk files is not simulated by a mutable global after startup.
  f.context.MoriInstalledGeneration='b'.repeat(32);
  await f.updater.poll();
  assert.equal(f.calls.find(x=>x.op==='poll').generation,generation);
  assert.equal(f.calls.find(x=>x.op==='apply').generation,generation);
  const legacy=fixture();await legacy.updater.poll();
  assert.equal(legacy.calls.find(x=>x.op==='poll').generation,'');
  assert.equal(legacy.reloads,1,'legacy source without a generation can still apply and reload');
});

test('alarm polling uses local request only and alarm API rejection is contained',async()=>{
  const f=fixture({unpaired:true,alarmFails:true});f.updater.start();await Promise.resolve();f.alarm();await f.updater.poll();
  assert.equal(f.calls.filter(x=>x.kind==='alarm').length,1);assert.equal(f.calls.some(x=>x.action==='extensionControl'),false);
});

test('ordinary collector idle signals schedule nothing until an update is offered',async()=>{
  const f=fixture();f.updater.schedule();assert.equal(f.timers.size,0);
  const p=popup(f);await f.updater.poll();f.updater.schedule();assert.equal(f.timers.size,1);p.close();
});

test('a thrown reload resumes content instead of leaving a permanent update lock',async()=>{
  const f=fixture({reloadThrows:true});await f.updater.poll();assert.equal(f.reloads,0);assert.equal(f.updater.isFrozen(),false);assert.equal(f.messages.some(x=>x.action==='mori-update-resume'),true);
});
