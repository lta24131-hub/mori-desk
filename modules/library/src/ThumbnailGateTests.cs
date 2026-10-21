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
   await Scheduler(check);
  }
  static async Task<T> Complete<T>(Task<T> task){if(await Task.WhenAny(task,Task.Delay(5000))!=task)throw new Exception("Thumbnail scheduler did not release its queued work");return await task;}
  static async Task Scheduler(Action<bool,string> check){
   var gate=new ThumbnailLoadScheduler(3);var video=await gate.EnterAsync(true);var queuedVideo=gate.EnterAsync(true);var anotherVideo=gate.EnterAsync(true);
   check(video!=null&&!queuedVideo.IsCompleted&&!anotherVideo.IsCompleted,"video scheduler admits one decoder and queues later videos before shared capacity");
   var image=await Complete(gate.EnterAsync(false));var otherImage=await Complete(gate.EnterAsync(false));var queuedImage=gate.EnterAsync(false);
   check(image!=null&&otherImage!=null&&!queuedImage.IsCompleted,"waiting videos leave two shared slots available for picture thumbnails");
   image.Dispose();image.Dispose();image=await Complete(queuedImage);
   check(image!=null&&!queuedVideo.IsCompleted&&!anotherVideo.IsCompleted,"picture work progresses while an earlier video is still decoding");
   var pausing=gate.PauseAndDrainAsync();check(gate.IsPaused&&!pausing.IsCompleted,"scheduler pause closes both admissions before draining active thumbnails");
   check(await gate.EnterAsync(true)==null&&await gate.EnterAsync(false)==null,"paused scheduler skips new video and picture requests");
   video.Dispose();image.Dispose();otherImage.Dispose();
   check(await Complete(queuedVideo)==null&&await Complete(anotherVideo)==null,"video waiters recheck pause and release every acquired slot without starting a decoder");
   var pause=await Complete(pausing);var nested=await Complete(gate.PauseAndDrainAsync());pause.Dispose();pause.Dispose();
   check(gate.IsPaused&&await gate.EnterAsync(false)==null,"nested scheduler pauses remain closed until their final lease is released");
   nested.Dispose();check(!gate.IsPaused,"final scheduler pause release restores admission");
   bool failed=false;try{using(await gate.EnterAsync(true))throw new InvalidOperationException("synthetic decoder failure");}catch(InvalidOperationException){failed=true;}
   video=await Complete(gate.EnterAsync(true));image=await Complete(gate.EnterAsync(false));otherImage=await Complete(gate.EnterAsync(false));queuedImage=gate.EnterAsync(false);
   check(failed&&video!=null&&image!=null&&otherImage!=null&&!queuedImage.IsCompleted,"exception disposal restores the serial decoder and exact shared capacity");
   video.Dispose();image.Dispose();otherImage.Dispose();(await Complete(queuedImage)).Dispose();
   // Exercise the opposite wait ordering: a video owns its serial permit but
   // is waiting for a picture's shared slot when replacement/close pauses work.
   var one=new ThumbnailLoadScheduler(1);image=await one.EnterAsync(false);queuedVideo=one.EnterAsync(true);pausing=one.PauseAndDrainAsync();
   check(!queuedVideo.IsCompleted&&!pausing.IsCompleted&&one.IsPaused,"pause can begin while a video is waiting for a picture's shared slot");image.Dispose();
   check(await Complete(queuedVideo)==null,"shared-gate pause releases a waiting video's serial permit instead of deadlocking");
   pause=await Complete(pausing);pause.Dispose();using(var resumed=await Complete(one.EnterAsync(true)))check(resumed!=null&&!one.IsPaused,"deadlock-prevention path preserves capacity for later videos");
  }
 }
}
