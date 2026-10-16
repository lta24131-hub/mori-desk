using System;
using QingJie;

namespace MoriDesk {
 // Pure state regressions: no Application, dispatcher pump, screen access,
 // clipboard, native hotkey registration, or window construction is involved.
 internal static class CaptureStartGateTests {
  internal static void Run(Action<bool,string> check){
   var gate=new CaptureStartGate();long first,duplicate,next,stale,reentrant;
   check(!gate.IsPending,"capture start gate is initially idle");
   check(gate.TryQueue(out first)&&first!=0&&gate.IsCurrent(first),"capture reserves its start before the delay runs");
   check(!gate.TryQueue(out duplicate)&&duplicate==0&&gate.IsCurrent(first),"repeated hotkeys cannot queue a second delayed capture");
   int executions=0;
   gate.Run(first,()=>{
    executions++;check(gate.IsPending&&!gate.TryQueue(out duplicate),"capture stays guarded throughout overlay creation");
    gate.Run(first,()=>executions++);
    check(executions==1,"an already running timer ticket cannot execute reentrantly");
   });
   check(executions==1&&!gate.IsPending,"successful startup releases the delayed-start guard");
   gate.Run(first,()=>executions++);
   check(executions==1,"a second tick cannot run an already completed capture");
   gate.TryQueue(out stale);gate.Cancel();
   check(!gate.IsPending&&!gate.IsCurrent(stale),"cancelling before the delay invalidates the pending capture");
   gate.Run(stale,()=>executions++);
   check(executions==1,"a queued timer tick does nothing after cancellation");
   check(gate.TryQueue(out next)&&next!=stale,"a new capture can start immediately after cancellation");
   gate.Run(stale,()=>executions++);gate.Complete(stale);
   check(gate.IsCurrent(next)&&executions==1,"a stale callback cannot execute or clear a newer capture request");
   bool failed=false;
   try{gate.Run(next,()=>{throw new InvalidOperationException("synthetic capture startup failure");});}catch(InvalidOperationException){failed=true;}
   check(failed&&!gate.IsPending,"startup failure propagates while always releasing its guard");
   check(gate.TryQueue(out next),"a failed capture never blocks the next capture");
   reentrant=0;
   gate.Run(next,()=>{gate.Cancel();check(gate.TryQueue(out reentrant),"a cancelled in-progress launch permits a fresh request");});
   check(gate.IsCurrent(reentrant),"an older finally block cannot clear a replacement request");
   gate.Run(reentrant,()=>executions++);
   check(executions==2&&!gate.IsPending,"a replacement request executes once and returns to idle");
   gate.Cancel();gate.Cancel();
   gate.Complete(0);gate.Run(0,()=>executions++);
   check(!gate.IsPending&&executions==2,"repeated cancel and zero tickets are harmless");
   for(int i=0;i<1000;i++){
    if(!gate.TryQueue(out next))throw new InvalidOperationException("capture launch remained pending");
    if(i%2==0)gate.Cancel();else gate.Run(next,()=>{});
   }
   check(!gate.IsPending,"repeated capture and cancellation cycles do not accumulate pending launches");
  }
 }
}
