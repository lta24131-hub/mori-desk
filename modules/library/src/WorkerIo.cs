using System;
using System.Threading;
using System.Threading.Tasks;

namespace Mori {
 // Decoder callers already occupy pool threads while waiting for child exit.
 // Their pipe readers must not queue behind that same work: an undrained pipe
 // can block a healthy child until its guard kills it. The existing decoder
 // gates bound reader concurrency; these threads end when the child closes its
 // pipes. Keep cancellation/termination and output caps in each owning decoder.
 internal static class WorkerIo {
  internal static Task<T> Run<T>(Func<T> read){
   return Task.Factory.StartNew(read,CancellationToken.None,TaskCreationOptions.LongRunning|TaskCreationOptions.DenyChildAttach,TaskScheduler.Default);
  }
  internal static Task Run(Action read){
   return Task.Factory.StartNew(read,CancellationToken.None,TaskCreationOptions.LongRunning|TaskCreationOptions.DenyChildAttach,TaskScheduler.Default);
  }
 }
}
