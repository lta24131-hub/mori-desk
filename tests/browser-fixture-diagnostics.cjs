// Test harness only. The extended deadline is accepted only after a real guest
// hardware probe, never by trusting an environment flag on the user's PC.
'use strict';
const {spawnSync}=require('node:child_process');
function readyTimeout(defaultTimeoutMs) {
  const requested=process.env.NOOK_VM_BROWSER_READY_TIMEOUT_MS;
  if(!requested)return defaultTimeoutMs;
  if(requested!=='120000')throw Error('Unsupported VM browser ready timeout: '+requested);
  if(process.platform!=='win32')throw Error('VM browser startup override is Windows guest-only. No fixture was started.');
  const check=spawnSync('powershell.exe',['-NoLogo','-NoProfile','-NonInteractive','-Command',
    'Get-CimInstance Win32_ComputerSystem | ForEach-Object { $_.Manufacturer + " " + $_.Model }'],
    {windowsHide:true,encoding:'utf8',timeout:90000});
  if(check.status!==0||!/QEMU|KVM|Bochs/i.test(check.stdout||''))throw Error('VM startup override refused; no fixture was started. '+JSON.stringify({exitCode:check.status,stdout:check.stdout,stderr:check.stderr,error:check.error?.message}));
  console.log('VM_READY_BUDGET '+JSON.stringify({timeoutMs:120000,hardware:check.stdout.trim()}));
  return 120000;
}
function trackHost(host,label) {
  const started=Date.now();let phase='spawned-awaiting-ready',stdout='',stderr='',spawnError=null,closed=false,exitCode=null,signal=null;
  const retain=(previous,value)=>(previous+value.toString()).slice(-65536);
  host.stdout.on('data',value=>stdout=retain(stdout,value));
  host.stderr.on('data',value=>stderr=retain(stderr,value));
  let complete;
  const exit=new Promise(resolve=>{complete=resolve;});
  host.once('error',error=>{spawnError={name:error.name,code:error.code,message:error.message,stack:error.stack};});
  host.once('close',(code,closedSignal)=>{closed=true;exitCode=code;signal=closedSignal;complete([code,closedSignal]);});
  function snapshot(){return {label,phase,elapsedMs:Date.now()-started,pid:host.pid??null,closed,exitCode:closed?exitCode:host.exitCode,signal:closed?signal:host.signalCode,spawnError,stdout,stderr};}
  function failure(reason){const error=Error(reason+'; fixture diagnostics: '+JSON.stringify(snapshot()));error.fixtureHostFatal=true;return error;}
  function assertRunning(){if(spawnError||closed||host.exitCode!==null||host.signalCode!==null)throw failure('Fixture host exited or could not start');}
  return {exit,snapshot,failure,assertRunning,setPhase(value){phase=value;console.log('FIXTURE_PHASE '+JSON.stringify(snapshot()));},get stdout(){return stdout;}};
}
module.exports={readyTimeout,trackHost};
