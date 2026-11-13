using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Mori;

namespace MoriDesk {
 internal static class SvgCancellationTests {
  [DllImport("ntdll.dll")] static extern int NtSuspendProcess(IntPtr process);
  // Suspend only handles reported by this invocation's process-start callback.
  // It gives the real worker a deterministic unfinished lifetime; no desktop,
  // installed application or unrelated process is located or manipulated.
  sealed class HeldWorkers : IDisposable {
   readonly object gate=new object();readonly Dictionary<string,Process> held=new Dictionary<string,Process>(StringComparer.OrdinalIgnoreCase);
   readonly HashSet<string> sources;readonly Action<Process> previous;bool enabled=true,disposed;
   internal HeldWorkers(params string[] paths){sources=new HashSet<string>(paths,StringComparer.OrdinalIgnoreCase);previous=SvgRaster.WorkerStartedForTests;SvgRaster.WorkerStartedForTests=Started;}
   void Started(Process child){
    lock(gate){
     if(!enabled)return;
     string path=sources.FirstOrDefault(p=>child.StartInfo.Arguments.IndexOf("\""+p+"\"",StringComparison.OrdinalIgnoreCase)>=0);if(path==null)return;
     var handle=Process.GetProcessById(child.Id);
     try{if(handle.HasExited||NtSuspendProcess(handle.Handle)!=0)throw new InvalidOperationException("Could not hold the owned SVG worker.");held.Add(path,handle);}
     catch{handle.Dispose();throw;}
    }
   }
   internal bool Ready(string path){lock(gate)return held.ContainsKey(path);}
   internal bool Alive(string path){lock(gate){Process child;return held.TryGetValue(path,out child)&&!child.HasExited;}}
   internal int Id(string path){lock(gate)return held[path].Id;}
   internal void Disable(){lock(gate)enabled=false;}
   public void Dispose(){
    lock(gate){if(disposed)return;disposed=true;enabled=false;SvgRaster.WorkerStartedForTests=previous;foreach(var child in held.Values){try{if(!child.HasExited){child.Kill();child.WaitForExit(2000);}}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}finally{child.Dispose();}}held.Clear();}
   }
  }
  static bool Completed(Task task,int milliseconds){return SpinWait.SpinUntil(()=>task.IsCompleted,milliseconds);}
  static bool Cancelled(Task task){try{task.GetAwaiter().GetResult();return false;}catch(OperationCanceledException){return true;}}
  static void Observe(Task task){if(task==null)return;try{task.Wait(5000);}catch(AggregateException){} }
  static void DisposeResult(Task<PreviewImage> task){if(task!=null&&task.Status==TaskStatus.RanToCompletion)task.Result.Dispose();}
  static SemaphoreSlim Slots(Type owner,string name){return (SemaphoreSlim)owner.GetField(name,BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);}
  static void WriteSvg(string path){File.WriteAllText(path,"<svg xmlns='http://www.w3.org/2000/svg' width='48' height='24'><rect width='48' height='24' fill='#6380d9'/></svg>");}
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);string first=Path.Combine(root,"first.svg"),second=Path.Combine(root,"second.svg"),thumbnail=Path.Combine(root,"thumbnail.svg"),png=Path.Combine(root,"next.png");
   foreach(string path in new[]{first,second,thumbnail})WriteSvg(path);
   using(var bitmap=new Bitmap(19,11)){using(var graphics=Graphics.FromImage(bitmap))graphics.Clear(Color.CornflowerBlue);bitmap.Save(png,ImageFormat.Png);}
   var originals=new[]{first,second,thumbnail,png}.ToDictionary(p=>p,File.ReadAllBytes);
   var workers=Slots(typeof(SvgRaster),"Workers");var previews=Slots(typeof(PreviewImage),"DecodeSlots");
   int width,height;bool cancelled=false;try{SvgRaster.Render(first,128,new CancellationToken(true),out width,out height);}catch(OperationCanceledException){cancelled=true;}
   check(cancelled&&workers.CurrentCount==2,"pre-cancelled SVG does not occupy a worker slot or touch a child process");
   workers.Wait();workers.Wait();
   try{using(var cancel=new CancellationTokenSource()){
    var pending=Task.Run(()=>{int w,h;return SvgRaster.Render(first,128,cancel.Token,out w,out h);});
    check(!pending.Wait(80),"SVG cancellation fixture reaches the occupied decoder queue");cancel.Cancel();
    check(Completed(pending,2000)&&Cancelled(pending),"queued SVG cancellation finishes while both other worker slots remain occupied");
    check(workers.CurrentCount==0,"cancelling an unadmitted SVG cannot over-release another request's slot");
   }}finally{workers.Release(2);}
   using(var held=new HeldWorkers(first,second))using(var cancelFirst=new CancellationTokenSource())using(var cancelSecond=new CancellationTokenSource()){
    Task<PreviewImage> a=null,b=null,next=null;
    try{
     a=PreviewImage.OpenAsync(first,cancelFirst.Token);b=PreviewImage.OpenAsync(second,cancelSecond.Token);
     check(SpinWait.SpinUntil(()=>held.Ready(first)&&held.Ready(second),5000),"two full SVG previews have started distinct real worker processes");
     check(held.Id(first)!=held.Id(second)&&held.Alive(first)&&held.Alive(second)&&workers.CurrentCount==0&&previews.CurrentCount==0,"both live SVG jobs occupy the two shared full-preview and worker slots");
     next=PreviewImage.OpenAsync(png,CancellationToken.None);check(!next.Wait(80),"next ordinary PNG waits behind the two in-flight SVG previews");
     var elapsed=Stopwatch.StartNew();cancelFirst.Cancel();long callerMilliseconds=elapsed.ElapsedMilliseconds;
     check(callerMilliseconds<100,"UI cancellation returns without waiting for SVG process termination");
     check(Completed(a,3000)&&Cancelled(a),"cancelling an active full SVG preview propagates through PreviewImage to the worker");
     check(!held.Alive(first)&&held.Alive(second),"SVG cancellation reaps only its owned child and leaves the other preview worker alive");
     check(Completed(next,3000),"cancelling the old SVG frees a decode slot for the next PNG before the other SVG ends");
     using(var result=next.GetAwaiter().GetResult())using(var pixels=new Bitmap(result.Image))check(result.Image.Width==19&&result.Image.Height==11&&pixels.GetPixel(5,5).ToArgb()==Color.CornflowerBlue.ToArgb(),"the queued PNG actually decodes with its correct pixels and dimensions");next=null;
     check(workers.CurrentCount==1&&previews.CurrentCount==1,"only the still-active second SVG retains worker and preview slots");
     cancelSecond.Cancel();check(Completed(b,3000)&&Cancelled(b)&&!held.Alive(second),"the second SVG independently cancels and releases its owned process");
    }finally{cancelFirst.Cancel();cancelSecond.Cancel();held.Dispose();Observe(a);Observe(b);Observe(next);DisposeResult(a);DisposeResult(b);DisposeResult(next);}
   }
   using(var held=new HeldWorkers(thumbnail))using(var cancel=new CancellationTokenSource()){
    Task<System.Windows.Media.Imaging.BitmapSource> task=null;
    try{
     task=Task.Run(()=>{int w,h;return SvgRaster.Render(thumbnail,128,cancel.Token,out w,out h);});
     check(SpinWait.SpinUntil(()=>held.Ready(thumbnail),5000)&&held.Alive(thumbnail),"failure-cache cancellation fixture observes a real running thumbnail worker");cancel.Cancel();
     check(Completed(task,3000)&&Cancelled(task)&&!held.Alive(thumbnail),"active thumbnail cancellation completes by terminating its own worker");
     check(!SvgRaster.FailureCache.Has(ThumbnailBuildGate.SourceStamp(thumbnail),DateTime.UtcNow),"cancelled thumbnail is not remembered as an unsupported SVG for ten minutes");
     held.Disable();var recovered=SvgRaster.Render(thumbnail,128,out width,out height);
     check(recovered.IsFrozen&&recovered.PixelWidth==128&&recovered.PixelHeight==64&&width==48&&height==24,"the same cancelled SVG can immediately retry and produce a valid thumbnail");
    }finally{cancel.Cancel();held.Dispose();Observe(task);}
   }
   check(workers.CurrentCount==2&&previews.CurrentCount==2,"all cancellation and retry paths return exactly the available SVG and preview slots");
   foreach(var original in originals){check(File.ReadAllBytes(original.Key).SequenceEqual(original.Value),"cancellation preserves original bytes / "+Path.GetFileName(original.Key));using(var exclusive=new FileStream(original.Key,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(exclusive.Length>0,"cancellation leaves original source unlocked / "+Path.GetFileName(original.Key));}
  }
 }
}
