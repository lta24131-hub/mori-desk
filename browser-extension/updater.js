// Only the paired desktop service may offer an update. No remote update URL,
// package bytes, library data or apply token is ever exposed to page scripts.
const MoriUpdater = (() => {
  const ALARM = 'mori-extension-update';
  const CLIENT = 'moriUpdateClientId', UNSAFE = 'moriUpdateUncertainWrite', PENDING = 'moriUpdatePendingWrite';
  const BLOCKED = {ok:false,code:'UPDATE_IN_PROGRESS',error:'Nook 插件正在更新，请稍后再收藏。'};
  function create(options) {
    const browser = options.chrome, delay = options.setTimeout || setTimeout, cancel = options.clearTimeout || clearTimeout;
    const now = options.now || Date.now;
    let active = 0, writers = 0, uncertaintyRevision = 0, frozen = false, uncertain = false, initialized = false;
    let clientId = '', flight = null, scheduled, offered = null, reason = '', listeners = new Set();
    const popups = new Set();
    let storageWrites = Promise.resolve();
    // Serialize tiny metadata writes so one completed save cannot clear another
    // in-flight save's journal. No media, URL, title or payload is persisted.
    function writeStorage(values) {
      const task=storageWrites.then(()=>browser.storage.local.set(values));
      storageWrites=task.catch(()=>{});return task;
    }
    const status = () => ({ok:true,pendingVersion:offered?.version || '',reason,uncertain,busy:active > 0 || writers > 0 || options.extraBusy(),popupOpen:popups.size > 0});
    const publish = () => {const state = status();for (const listener of listeners) {try {listener(state);} catch {}}};
    const persistUnsafe = async value => {try {await writeStorage({[UNSAFE]:value});}catch {uncertaintyRevision++;uncertain=true;reason='uncertain';publish();}};
    const ready = (async () => {
      try {
        const saved = await browser.storage.local.get([CLIENT,UNSAFE,PENDING]);
        // A collection may finish before this asynchronous read. Never clear a
        // newly reported uncertain result with an older stored value.
        uncertain = uncertain || saved[UNSAFE] === true || saved[PENDING] === true;
        clientId = typeof saved[CLIENT] === 'string' && /^[0-9a-f]{32}$/.test(saved[CLIENT]) ? saved[CLIENT] : options.uuid().replace(/-/g,'').toLowerCase();
        if(!/^[0-9a-f]{32}$/.test(clientId))throw Error('Invalid browser client identifier');
        if (clientId !== saved[CLIENT]) await writeStorage({[CLIENT]:clientId});
        initialized = true;
      } catch {reason='unavailable';}
    })();
    function schedule(force=false) {
      // Ordinary image clicks can release a prepared drag. Only a known pending
      // update needs an immediate retry; alarms discover new offers separately.
      if(!force && !offered)return;
      cancel(scheduled);
      scheduled = delay(() => {scheduled=null;void poll();}, 100);
    }
    function begin() {
      if (frozen) return null;
      active++;publish();let ended=false;
      return () => {if(ended)return;ended=true;active--;publish();schedule();};
    }
    function observeWrite(result) {
      if (!result || ['NATIVE_TIMEOUT','NATIVE_EMPTY_REPLY','NATIVE_HOST_DISCONNECTED','NATIVE_CONNECTION_ERROR','LOCAL_CONNECTION_UNAVAILABLE','SAVE_RESULT_UNKNOWN'].includes(result.code)) {
        uncertaintyRevision++;uncertain=true;reason='uncertain';void persistUnsafe(true);publish();
      }
    }
    async function beforeWrite() {
      await ready;
      if(!initialized || frozen)throw Error('Safe save journal is unavailable');
      writers++;publish();
      try {await writeStorage({[PENDING]:true});}
      catch {writers--;uncertaintyRevision++;uncertain=true;reason='unavailable';publish();throw Error('Safe save journal is unavailable');}
      let ended=false;
      return async result=>{
        if(ended)return;ended=true;
        observeWrite(result);writers--;
        try {await writeStorage({[PENDING]:writers>0,[UNSAFE]:uncertain});}
        catch {uncertaintyRevision++;uncertain=true;reason='uncertain';}
        publish();schedule();
      };
    }
    async function acknowledge() {
      await ready;
      // The user only confirms they checked the previous outcome. Never retry it.
      if(active || writers || frozen || options.extraBusy())return status();
      const acknowledgedRevision=uncertaintyRevision;
      try {
        await writeStorage({[UNSAFE]:false,[PENDING]:false});
        // A newly lost response during the storage await is not covered by the
        // user's confirmation of an earlier save outcome.
        if(uncertaintyRevision===acknowledgedRevision){uncertain=false;reason='';}
        else {uncertain=true;reason='uncertain';await persistUnsafe(true);}
        publish();schedule();
      } catch {reason='unavailable';}
      return status();
    }
    function popup(port) {
      popups.add(port);const send=value=>{try{port.postMessage({updateStatus:value});}catch{}};
      listeners.add(send);send(status());
      port.onDisconnect.addListener(()=>{popups.delete(port);listeners.delete(send);publish();schedule();});
    }
    const idle = () => initialized && !uncertain && !active && !writers && !popups.size && !options.extraBusy();
    async function bounded(task, milliseconds) {
      let timer;try{return await Promise.race([task,new Promise((_,reject)=>{timer=delay(()=>reject(Error('Update control timed out')),milliseconds);})]);}finally{cancel(timer);}
    }
    async function frames() {
      const tabs=await browser.tabs.query({url:['https://*/*','http://*/*']});
      const voters=[];
      for(const tab of tabs) {
        try {
          const results=await browser.scripting.executeScript({target:{tabId:tab.id,allFrames:true},func:()=>({collector:!!globalThis.MoriCollectorInstance,protocol:globalThis.MoriCollectorInstance?.updateProtocol || 0})});
          for(const result of results || []) {
            if(!result.result?.collector)continue;
            if(result.result.protocol!==1)throw Error('An older collector must be refreshed first');
            voters.push({tabId:tab.id,frameId:result.frameId});
          }
        } catch(error) {
          // A protected Chrome page or a frame which disappeared has no live
          // collector to interrupt. Any other unavailable vote defers the update.
          if(!/cannot access|missing host permission|no tab with id|frame.*removed|tab.*closed/i.test(String(error?.message || error)))throw error;
        }
      }
      return voters;
    }
    const frameKey = list => list.map(x=>`${x.tabId}:${x.frameId}`).sort().join(',');
    async function message(voter,action,lock) {
      return bounded(browser.tabs.sendMessage(voter.tabId,{action,lock},{frameId:voter.frameId}),2500);
    }
    async function poll() {
      if(flight)return flight;
      flight=(async()=>{
        await ready;
        if(!initialized || !options.configured()) {reason='unpaired';publish();return status();}
        const identity=options.identity();
        if(!identity.installationId || !clientId) {reason='unpaired';publish();return status();}
        let reply;
        try {reply=await bounded(options.request({action:'extensionControl',op:'poll',...identity,clientId}),5000);}catch {reason='unavailable';publish();return status();}
        if(!reply?.ok) {reason='unavailable';publish();return status();}
        const update=reply.update;
        if(!update) {offered=null;reason=uncertain?'uncertain':'current';publish();return status();}
        if(typeof update.token!=='string' || !update.token || update.token.length>512 || typeof update.version!=='string' || !/^\d+(?:\.\d+){1,3}$/.test(update.version)) {reason='unavailable';publish();return status();}
        offered={token:update.token,version:update.version};
        if(reply.multipleClients || update.multipleClients) {reason='multipleClients';publish();return status();}
        if(!idle()) {reason=uncertain?'uncertain':popups.size?'popup':'busy';publish();return status();}
        frozen=true;reason='preparing';publish();
        let voters=[], lock=options.uuid(), reload=false;
        const start=now();
        try {
          voters=await bounded(frames(),4000);
          const validVote = vote => vote?.ready && vote.lock===lock;
          if(!(await Promise.all(voters.map(voter=>message(voter,'mori-update-prepare',lock)))).every(validVote))throw Error('Collector is busy');
          if(!idle() || now()-start>9000)throw Error('Busy before update');
          const current=await bounded(frames(),3000);
          if(frameKey(current)!==frameKey(voters))throw Error('Collector frames changed');
          if(!(await Promise.all(voters.map(voter=>message(voter,'mori-update-verify',lock)))).every(validVote))throw Error('Collector lock expired');
          if(!idle() || now()-start>9000)throw Error('Busy before apply');
          const applied=await bounded(options.request({action:'extensionControl',op:'apply',...identity,clientId,token:offered.token}),4000);
          if(!applied?.ok || applied.reload!==true)throw Error('Desktop did not acknowledge apply');
          // A late ACK must not reload after a content lock expires or a popup
          // opens. A later poll may safely retry the update, never a collection.
          if(!idle() || now()-start>13000)throw Error('Busy after apply');
          if(!(await Promise.all(voters.map(voter=>message(voter,'mori-update-verify',lock)))).every(validVote))throw Error('Collector lock expired');
          if(!idle() || now()-start>13000)throw Error('Busy after final vote');
          browser.runtime.reload();reload=true;
        } catch {reason='deferred';publish();}
        finally {
          if(!reload) {
            await Promise.allSettled(voters.map(voter=>message(voter,'mori-update-resume',lock)));
            frozen=false;publish();
          }
        }
        return status();
      })().finally(()=>{flight=null;});
      return flight;
    }
    function start() {
      // Chrome 120+ supports 30-second alarms. No unpaired native-process poll.
      if(!browser.alarms)return;
      browser.alarms.onAlarm.addListener(alarm=>{if(alarm.name===ALARM)void poll();});
      try {Promise.resolve(browser.alarms.create(ALARM,{periodInMinutes:0.5})).catch(()=>{});} catch {}
      schedule(true);
    }
    return {start,poll,schedule,begin,beforeWrite,observeWrite,acknowledge,popup,status,blocked:()=>({...BLOCKED}),isFrozen:()=>frozen};
  }
  return {create};
})();
