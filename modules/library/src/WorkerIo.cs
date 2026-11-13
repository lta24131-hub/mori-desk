using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Mori {
 // Decoder callers already occupy pool threads while waiting for child exit.
 // Their pipe readers must not queue behind that same work: an undrained pipe
 // can block a healthy child until its guard kills it. The existing decoder
 // gates bound reader concurrency; these threads end when the child closes its
 // pipes. Keep cancellation/termination and output caps in each owning decoder.
 internal static class WorkerIo {
  // Observation-only seam: tests reproduce the exit between a successful wait
  // poll and the process-property read. Production never allocates a callback
  // per poll and still validates the worker's exit code and output afterward.
  internal static Func<Process,long> PrivateMemoryForTests;
  internal static bool TryReadPrivateMemory(Process process,out long bytes){
   try{
    process.Refresh();var sample=PrivateMemoryForTests;
    bytes=sample==null?process.PrivateMemorySize64:sample(process);return true;
   }catch(InvalidOperationException){if(!process.HasExited)throw;}
   catch(Win32Exception){if(!process.HasExited)throw;}
   bytes=0;return false;
  }
  internal static Task<T> Run<T>(Func<T> read){
   return Task.Factory.StartNew(read,CancellationToken.None,TaskCreationOptions.LongRunning|TaskCreationOptions.DenyChildAttach,TaskScheduler.Default);
  }
  internal static Task Run(Action read){
   return Task.Factory.StartNew(read,CancellationToken.None,TaskCreationOptions.LongRunning|TaskCreationOptions.DenyChildAttach,TaskScheduler.Default);
  }
 }
}
