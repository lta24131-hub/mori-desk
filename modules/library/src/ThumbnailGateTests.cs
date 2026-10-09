using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Mori {
 public static class ThumbnailGateTests {
  public static void Run(Action<bool,string> check){var passed=new List<string>();Task.Run(()=>RunAsync(delegate(bool ok,string name){if(!ok)throw new Exception("FAIL: "+name);passed.Add(name);})).GetAwaiter().GetResult();foreach(var name in passed)check(true,name);}
  static async Task RunAsync(Action<bool,string> check){
   var gate=new ThumbnailLoadGate(3);var first=await gate.EnterAsync();var second=await gate.EnterAsync();var third=await gate.EnterAsync();
   check(first!=null&&second!=null&&third!=null,"thumbnail gate admits bounded three jobs");
   var queued=gate.EnterAsync();check(!queued.IsCompleted,"fourth thumbnail waits for capacity");
   var pausing=gate.PauseAndDrainAsync();check(gate.IsPaused&&!pausing.IsCompleted,"pause stops admission before draining active jobs");
   check(await gate.EnterAsync()==null,"new thumbnail skipped while pause is pending");
   first.Dispose();check(await queued==null,"previously queued thumbnail rechecks pause after slot acquisition");
   check(!pausing.IsCompleted,"pause cannot complete while active writers remain");
   second.Dispose();third.Dispose();var pause=await pausing;
   check(gate.IsPaused&&await gate.EnterAsync()==null,"drained pause prevents all new writers");
   var nested=await gate.PauseAndDrainAsync();pause.Dispose();pause.Dispose();
   check(gate.IsPaused&&await gate.EnterAsync()==null,"nested pause holds barrier and double dispose is harmless");
   nested.Dispose();check(!gate.IsPaused,"last pause disposal resumes thumbnail loading");
   first=await gate.EnterAsync();second=await gate.EnterAsync();third=await gate.EnterAsync();queued=gate.EnterAsync();
   check(first!=null&&second!=null&&third!=null&&!queued.IsCompleted,"resume restores exact original concurrency capacity");
   first.Dispose();var last=await queued;check(last!=null,"resumed pending load receives a slot");second.Dispose();third.Dispose();last.Dispose();
   using(await gate.PauseAndDrainAsync())check(gate.IsPaused,"idle gate can pause again after complete cycle");
   using(var job=await gate.EnterAsync())check(job!=null&&!gate.IsPaused,"idle resume allows later gallery reload");
  }
 }
}
