using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mori;

namespace MoriDesk {
 // Invoked only by the VM ScenarioRunner. Disk decoding is real; the injected
 // callbacks in race tests add deterministic barriers, not replacement images.
 internal static class ThumbnailDecodedCacheTests {
  static void Picture(string path,int width,int height,byte red){
   var pixels=new byte[width*height*4];for(int n=0;n<pixels.Length;n+=4){pixels[n]=31;pixels[n+1]=77;pixels[n+2]=red;pixels[n+3]=255;}
   var image=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var file=File.Create(path))encoder.Save(file);
  }
  static bool Red(BitmapSource image,byte red){var pixel=new byte[4];var converted=new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);converted.CopyPixels(new Int32Rect(0,0,1,1),pixel,4,0);return pixel[2]==red;}
  static bool Reject(Action action){try{action();return false;}catch(InvalidDataException){return true;}catch(IOException){return true;}catch(FormatException){return true;}catch(NotSupportedException){return true;}}
  static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
  static void Until(Func<bool> ready,string description){var watch=Stopwatch.StartNew();while(!ready()&&watch.ElapsedMilliseconds<6000){Pump();Thread.Sleep(1);}if(!ready())throw new TimeoutException("Decoded thumbnail test: "+description);Pump();}
  static void Wait(Func<bool> ready,string description){var watch=Stopwatch.StartNew();while(!ready()&&watch.ElapsedMilliseconds<6000)Thread.Sleep(1);if(!ready())throw new TimeoutException("Decoded thumbnail concurrency test: "+description);}
  static Task<BitmapImage> Background(Func<BitmapImage> read){return Task.Factory.StartNew(read,CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);}
  static BitmapImage Result(Task<BitmapImage> task){if(!task.Wait(6000))throw new TimeoutException("Decoded thumbnail worker did not finish");return task.GetAwaiter().GetResult();}
  static void Unlocked(string path,Action<bool,string> check,string description){using(var file=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(file.Length>0,description);}
  sealed class CacheState {
   readonly int count,pending,failures;readonly long bytes,decodes,hits,joins;
   internal CacheState(DecodedThumbnailCache cache){count=cache.Count;pending=cache.PendingCount;failures=cache.FailureCount;bytes=cache.Bytes;decodes=cache.DecodeCount;hits=cache.HitCount;joins=cache.JoinCount;}
   internal bool Matches(DecodedThumbnailCache cache){return cache.Count==count&&cache.PendingCount==pending&&cache.FailureCount==failures&&cache.Bytes==bytes&&cache.DecodeCount==decodes&&cache.HitCount==hits&&cache.JoinCount==joins;}
  }
  internal static void Run(string root,Action<bool,string> check){
   if(!Path.GetFullPath(root).StartsWith(@"C:\NookLab\Audit\",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Decoded thumbnail tests require the isolated VM fixture root.");
   root=Path.Combine(root,"decoded-thumbnails");Directory.CreateDirectory(root);
   var app=Application.Current??new Application();var shutdown=app.ShutdownMode;var context=SynchronizationContext.Current;var previous=ThumbnailImage.CurrentLibrary;
   app.ShutdownMode=ShutdownMode.OnExplicitShutdown;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
   try{var drain=ThumbnailImage.PauseAndDrainAsync();Until(()=>drain.IsCompleted,"prior thumbnail requests drained");using(drain.GetAwaiter().GetResult()){}RepeatedDisk(root,check);VersionsAndFailures(root,check);InvalidatePaths(root,check);WarmReads(root,check);WarmPending(root,check);Capacity(root,check);Concurrent(root,check);ClearAndChangedRead(root,check);InvalidatePending(root,check,false);InvalidatePending(root,check,true);Lifecycle(root,check);}
   finally{ThumbnailImage.CurrentLibrary=previous;ThumbnailImage.DecodedCache.Clear();SynchronizationContext.SetSynchronizationContext(context);app.ShutdownMode=shutdown;}
  }
  static void RepeatedDisk(string root,Action<bool,string> check){
   string path=Path.Combine(root,"repeat.512.png");Picture(path,512,512,171);var cache=ThumbnailImage.DecodedCache;cache.Clear();
   AppDomain.MonitoringIsEnabled=true;var log=new StringBuilder();
   long allocated=AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;var watch=Stopwatch.StartNew();long start=cache.DecodeCount;var image=ThumbnailImage.ReadThumbnail(path);watch.Stop();
   log.AppendLine("cold_ms="+watch.Elapsed.TotalMilliseconds.ToString("F3",System.Globalization.CultureInfo.InvariantCulture)+"; decoded="+(cache.DecodeCount-start)+"; managed_allocated_bytes="+(AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize-allocated));
   check(image.IsFrozen&&image.PixelWidth==512&&image.PixelHeight==512&&cache.DecodeCount==start+1,"cold disk thumbnail is decoded once into a frozen real 512px BitmapImage");
   long warmDecodes=cache.DecodeCount;allocated=AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;watch.Restart();bool identical=true;for(int n=0;n<100;n++)identical&=object.ReferenceEquals(image,ThumbnailImage.ReadThumbnail(n%2==0?path:path.ToUpperInvariant()));watch.Stop();
   log.AppendLine("warm_100_ms="+watch.Elapsed.TotalMilliseconds.ToString("F3",System.Globalization.CultureInfo.InvariantCulture)+"; decoded="+(cache.DecodeCount-warmDecodes)+"; managed_allocated_bytes="+(AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize-allocated));
   check(identical&&cache.DecodeCount==warmDecodes&&cache.Count==1&&cache.Bytes<=DecodedThumbnailCache.DefaultBudget,"100 real disk thumbnail reads reuse the exact same frozen instance without another decode");
   allocated=AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;watch.Restart();for(int n=0;n<100;n++){var uncached=DecodedThumbnailCache.DecodeFile(path);if(uncached.PixelWidth!=512)throw new Exception("Uncached benchmark decoded the wrong fixture");}watch.Stop();
   log.AppendLine("uncached_100_ms="+watch.Elapsed.TotalMilliseconds.ToString("F3",System.Globalization.CultureInfo.InvariantCulture)+"; decoded=100; managed_allocated_bytes="+(AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize-allocated));
   using(var process=Process.GetCurrentProcess()){process.Refresh();log.AppendLine("process_private_bytes="+process.PrivateMemorySize64+"; working_set_bytes="+process.WorkingSet64+"; handles="+process.HandleCount);}
   log.AppendLine("Managed allocations exclude WIC/native pixel allocations; absolute process samples are not a leak verdict. Timing is evidence, not a fixed pass threshold.");
   File.WriteAllText(Path.Combine(root,"decoded-thumbnail-benchmark.txt"),log.ToString(),Encoding.UTF8);
   Unlocked(path,check,"a live cached bitmap retains no thumbnail file handle");File.Delete(path);
   check(Reject(()=>ThumbnailImage.ReadThumbnail(path))&&cache.Count==0,"a removed generated file evicts the positive entry and cannot return a stale successful image");
  }
  static void VersionsAndFailures(string root,Action<bool,string> check){
   var cache=new DecodedThumbnailCache();string path=Path.Combine(root,"version.512.png");var time=new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc);Picture(path,512,512,101);File.SetLastWriteTimeUtc(path,time);
   var first=cache.Read(path);byte[] good=File.ReadAllBytes(path);File.SetLastWriteTimeUtc(path,time.AddSeconds(1));var retimed=cache.Read(path);
   check(!object.ReferenceEquals(first,retimed)&&cache.DecodeCount==2&&cache.Count==1,"same-length thumbnail with a changed modification stamp is decoded again and replaces only its old version");
   Picture(path,113,79,201);File.SetLastWriteTimeUtc(path,time.AddSeconds(1));var resized=cache.Read(path);
   check(resized.PixelWidth==113&&resized.PixelHeight==79&&Red(resized,201)&&!object.ReferenceEquals(retimed,resized),"changed byte length invalidates the entry even when the timestamp is preserved");
   var corrupt=new byte[good.Length];File.WriteAllBytes(path,corrupt);File.SetLastWriteTimeUtc(path,time);long before=cache.DecodeCount;
   check(Reject(()=>cache.Read(path))&&cache.Count==0&&cache.PendingCount==0&&cache.FailureCount==1,"real corrupt PNG raises an error, evicts any success and cleans its in-flight request");
   check(Reject(()=>cache.Read(path))&&cache.DecodeCount==before+1,"same-stamp corrupt disk data backs off rather than repeatedly invoking the decoder");
   File.WriteAllBytes(path,good);File.SetLastWriteTimeUtc(path,time);
   check(Reject(()=>cache.Read(path))&&cache.DecodeCount==before+1,"same-stamp repair does not bypass the bounded failure backoff accidentally");
   cache.Clear();var recovered=cache.Read(path);
   check(Red(recovered,101)&&cache.FailureCount==0&&cache.Count==1,"explicit cache clear permits immediate recovery of an identically stamped repaired PNG");
   File.WriteAllBytes(path,corrupt);File.SetLastWriteTimeUtc(path,time.AddSeconds(2));check(Reject(()=>cache.Read(path)),"a new corrupt file version is attempted and fails visibly");
   Picture(path,97,61,211);File.SetLastWriteTimeUtc(path,time.AddSeconds(3));var changed=cache.Read(path);
   check(changed.PixelWidth==97&&Red(changed,211)&&cache.FailureCount==0,"changed file stamp immediately recovers from failure without waiting thirty seconds");
   cache.Clear();using(var held=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(Reject(()=>cache.Read(path))&&cache.FailureCount==0&&cache.PendingCount==0,"a transient sharing/IO failure is surfaced but never mistaken for a predictable corrupt-image failure");
   check(cache.Read(path).PixelWidth==97,"releasing a transient file lock permits an immediate successful retry");
   for(int n=0;n<=DecodedThumbnailCache.DefaultMaximum;n++){string broken=Path.Combine(root,"failure-"+n+".512.png");File.WriteAllBytes(broken,new byte[]{1,2,3,4,5});Reject(()=>cache.Read(broken));}
   check(cache.FailureCount<=DecodedThumbnailCache.DefaultMaximum&&cache.PendingCount==0,"corrupt-file backoff is bounded to 128 file stamps and retains no completed in-flight work");
   Unlocked(path,check,"source-change and corrupt-file paths release every decoded file handle");
   string retry=Path.Combine(root,"f5.512.png");Picture(retry,101,77,153);byte[] retryGood=File.ReadAllBytes(retry);File.WriteAllBytes(retry,new byte[retryGood.Length]);File.SetLastWriteTimeUtc(retry,time);
   ThumbnailImage.DecodedCache.Clear();check(Reject(()=>ThumbnailImage.ReadThumbnail(retry))&&ThumbnailImage.DecodedCache.FailureCount==1,"production ReadThumbnail records a predictable generated-PNG failure");File.WriteAllBytes(retry,retryGood);File.SetLastWriteTimeUtc(retry,time);ThumbnailImage.RetryFailedThumbnails();
   check(Red(ThumbnailImage.ReadThumbnail(retry),153)&&ThumbnailImage.DecodedCache.FailureCount==0,"production F5 retry immediately recovers a repaired generated PNG even when length and time are unchanged");
   string original=Path.Combine(root,"original.png"),large=Path.Combine(root,"oversize.512.png");Picture(original,512,512,132);Picture(large,513,512,139);cache.Clear();var originalOne=cache.Read(original);var originalTwo=cache.Read(original);var largeOne=cache.Read(large);var largeTwo=cache.Read(large);
   check(cache.Count==0&&cache.Bytes==0&&!object.ReferenceEquals(originalOne,originalTwo)&&!object.ReferenceEquals(largeOne,largeTwo),"original-path images and generated files larger than 512px are never strongly retained by this cache");
  }
  static void InvalidatePaths(string root,Action<bool,string> check){
   string path=Path.Combine(root,"invalidate.512.png"),other=Path.Combine(root,"invalidate-retained.512.png");var time=new DateTime(2026,1,5,0,0,0,DateTimeKind.Utc);
   Picture(path,101,67,147);Picture(other,79,53,193);File.SetLastWriteTimeUtc(path,time);byte[] good=File.ReadAllBytes(path);var cache=new DecodedThumbnailCache();
   var first=cache.Read(path);var retained=cache.Read(other);long retainedBytes=(long)retained.PixelWidth*retained.PixelHeight*Math.Max(4,(retained.Format.BitsPerPixel+7)/8);long before=cache.DecodeCount;
   cache.Invalidate(path.ToUpperInvariant());
   check(cache.Count==1&&cache.Bytes==retainedBytes&&cache.FailureCount==0&&cache.PendingCount==0&&cache.DecodeCount==before,"per-path invalidation removes only the case-insensitive positive entry and its exact pixel accounting without decoding");
   check(object.ReferenceEquals(retained,cache.Read(other))&&cache.DecodeCount==before,"invalidating one positive path preserves another path's exact cached instance");
   var fresh=cache.Read(path);
   check(!object.ReferenceEquals(first,fresh)&&Red(fresh,147)&&cache.DecodeCount==before+1&&cache.Count==2&&new FileInfo(path).Length==good.Length&&File.GetLastWriteTimeUtc(path)==time,"an explicitly invalidated positive thumbnail is really decoded again even though length and modification time are unchanged");
   cache.Invalidate(Path.Combine(root,"never-cached.512.png"));
   check(cache.Count==2&&object.ReferenceEquals(fresh,cache.Read(path))&&object.ReferenceEquals(retained,cache.Read(other)),"invalidating a missing path neither clears nor replaces unrelated positive entries");
   cache.Invalidate(path);File.WriteAllBytes(path,new byte[good.Length]);File.SetLastWriteTimeUtc(path,time);before=cache.DecodeCount;
   check(Reject(()=>cache.Read(path))&&cache.FailureCount==1&&cache.Count==1&&cache.PendingCount==0,"a real corrupt target is negatively cached while the unrelated positive thumbnail remains retained");
   File.WriteAllBytes(path,good);File.SetLastWriteTimeUtc(path,time);
   check(Reject(()=>cache.Read(path))&&cache.DecodeCount==before+1,"repairing bytes alone cannot accidentally bypass the same-stamp negative entry");
   cache.Invalidate(path.ToUpperInvariant());
   check(cache.FailureCount==0&&cache.Count==1&&cache.Bytes==retainedBytes&&object.ReferenceEquals(retained,cache.Read(other))&&cache.DecodeCount==before+1,"per-path invalidation clears the same-stamp failure without evicting or decoding another path");
   var repaired=cache.Read(path);
   check(Red(repaired,147)&&cache.Count==2&&cache.FailureCount==0&&cache.PendingCount==0&&cache.DecodeCount==before+2&&new FileInfo(path).Length==good.Length&&File.GetLastWriteTimeUtc(path)==time,"a repaired identically stamped PNG recovers immediately after its own path is invalidated");
   Unlocked(path,check,"positive and negative per-path invalidation releases all target file handles");
  }
  static void WarmReads(string root,Action<bool,string> check){
   string path=Path.Combine(root,"warm-only.512.png"),cold=Path.Combine(root,"warm-cold.512.png"),broken=Path.Combine(root,"warm-broken.512.png"),failed=Path.Combine(root,"warm-existing-failure.512.png"),missing=Path.Combine(root,"warm-missing.512.png");
   var time=new DateTime(2026,1,7,0,0,0,DateTimeKind.Utc);Picture(path,73,53,157);Picture(cold,47,29,193);File.SetLastWriteTimeUtc(path,time);File.WriteAllBytes(broken,new byte[]{1,2,3,4,5});File.WriteAllBytes(failed,new byte[]{6,7,8,9,10});
   byte[] original=File.ReadAllBytes(path),coldOriginal=File.ReadAllBytes(cold),brokenOriginal=File.ReadAllBytes(broken);var coldTime=File.GetLastWriteTimeUtc(cold);var brokenTime=File.GetLastWriteTimeUtc(broken);var cache=new DecodedThumbnailCache();var image=cache.ReadOwned(path);
   check(Reject(()=>cache.Read(failed))&&cache.FailureCount==1,"warm-only fixture retains a real unrelated decoder failure before probing");
   var before=new CacheState(cache);
   check(cache.HasWarmCandidate(path)&&cache.HasWarmCandidate(path.ToUpperInvariant())&&!cache.HasWarmCandidate(cold)&&!cache.HasWarmCandidate(broken)&&!cache.HasWarmCandidate(missing)&&!cache.HasWarmCandidate(failed)&&before.Matches(cache),"memory-only candidate checks identify only retained positive paths without decoding, joining or changing any cache state");
   bool identical=true;for(int n=0;n<100;n++)identical&=object.ReferenceEquals(image,cache.TryReadWarm(n%2==0?path:path.ToUpperInvariant()));
   check(identical&&image.IsFrozen&&before.Matches(cache)&&File.ReadAllBytes(path).SequenceEqual(original)&&File.GetLastWriteTimeUtc(path)==time,"100 positive-only warm probes reuse the same real owned frozen pixels without decoding or changing cache counters, accounting, failures or disk data");
   check(cache.TryReadWarm(cold)==null&&cache.TryReadWarm(broken)==null&&cache.TryReadWarm(missing)==null&&cache.TryReadWarm(failed)==null&&before.Matches(cache),"cold, corrupt, missing and negatively cached warm probes are pure misses without decoding, joining, clearing or adding failures");
   File.SetLastWriteTimeUtc(path,time.AddSeconds(1));
   check(cache.TryReadWarm(path)==null&&before.Matches(cache)&&File.ReadAllBytes(path).SequenceEqual(original)&&File.GetLastWriteTimeUtc(path)==time.AddSeconds(1),"changed disk timestamp makes a warm probe miss without evicting cache state or rewriting the file");
   var extended=original.Concat(new byte[]{0}).ToArray();File.WriteAllBytes(path,extended);File.SetLastWriteTimeUtc(path,time);
   check(cache.TryReadWarm(path)==null&&before.Matches(cache)&&File.ReadAllBytes(path).SequenceEqual(extended)&&File.GetLastWriteTimeUtc(path)==time,"changed disk length makes a warm probe miss even at the old timestamp and leaves the new bytes intact");
   File.Delete(path);
   check(cache.HasWarmCandidate(path)&&before.Matches(cache),"a deleted disk file remains only an advisory memory candidate without candidate lookup doing IO, eviction or negative caching");
   check(cache.TryReadWarm(path)==null&&before.Matches(cache)&&!File.Exists(path),"deleted derived thumbnail makes a warm probe miss without recreating a file or mutating cache ownership");
   File.WriteAllBytes(path,original);File.SetLastWriteTimeUtc(path,time);
   check(object.ReferenceEquals(image,cache.TryReadWarm(path))&&before.Matches(cache),"restoring the exact disk version exposes the still-retained warm instance without decode or failure-backoff changes");
   cache.Invalidate(path.ToUpperInvariant());bool invalidatedCandidate=cache.HasWarmCandidate(path);before=new CacheState(cache);
   check(cache.TryReadWarm(path)==null&&before.Matches(cache),"per-path invalidation makes positive-only warm reads miss without refilling the cache");
   cache.ReadOwned(path);cache.Clear();before=new CacheState(cache);
   check(!invalidatedCandidate&&!cache.HasWarmCandidate(path)&&before.Matches(cache),"per-path invalidation and cache clear both withdraw memory-only candidates without resurrecting cache generations");
   check(cache.TryReadWarm(path)==null&&before.Matches(cache)&&cache.Count==0&&cache.Bytes==0&&cache.PendingCount==0&&cache.FailureCount==0,"clear makes positive-only warm reads miss without decoding or resurrecting any cache generation");
   check(File.ReadAllBytes(path).SequenceEqual(original)&&File.GetLastWriteTimeUtc(path)==time&&File.ReadAllBytes(cold).SequenceEqual(coldOriginal)&&File.GetLastWriteTimeUtc(cold)==coldTime&&File.ReadAllBytes(broken).SequenceEqual(brokenOriginal)&&File.GetLastWriteTimeUtc(broken)==brokenTime,"positive-only hits and misses preserve every surviving real fixture's bytes and modification time");
   Unlocked(path,check,"positive-only warm probes retain no derived file handle");
  }
  static void WarmPending(string root,Action<bool,string> check){
   string path=Path.Combine(root,"warm-pending.512.png"),hot=Path.Combine(root,"warm-pending-hot.512.png"),failed=Path.Combine(root,"warm-pending-failure.512.png");Picture(path,89,61,179);Picture(hot,67,43,211);File.WriteAllBytes(failed,new byte[]{1,2,3,4,5});byte[] original=File.ReadAllBytes(path);var time=File.GetLastWriteTimeUtc(path);
   using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false)){
    var cache=new DecodedThumbnailCache(DecodedThumbnailCache.DefaultBudget,128,p=>{var decoded=DecodedThumbnailCache.DecodeFile(p);if(string.Equals(p,path,StringComparison.OrdinalIgnoreCase)){entered.Set();if(!release.WaitOne(6000))throw new TimeoutException("Warm pending test barrier");}return decoded;});
    var retained=cache.Read(hot);Reject(()=>cache.Read(failed));var owner=Background(()=>cache.Read(path));Task<BitmapImage> probe=null;
    try{
     check(entered.WaitOne(6000)&&cache.PendingCount==1&&cache.FailureCount==1,"warm-only pending fixture holds an actual decoder result before publication with unrelated positive and negative entries");
     var before=new CacheState(cache);probe=Background(()=>cache.TryReadWarm(path.ToUpperInvariant()));bool completed=probe.Wait(1500);
     check(completed&&probe.Result==null&&!owner.IsCompleted&&!cache.HasWarmCandidate(path)&&before.Matches(cache),"a warm probe and memory candidate lookup of an in-flight real decode miss before its owner is released and neither joins nor mutates pending work");
     check(object.ReferenceEquals(retained,cache.TryReadWarm(hot))&&before.Matches(cache)&&!owner.IsCompleted,"an unrelated warm hit remains available during a blocked decoder without changing pending, failure, hit or allocation accounting");
    }finally{release.Set();owner.Wait(6000);if(probe!=null)probe.Wait(6000);}
    var completedImage=Result(owner);var after=new CacheState(cache);
    check(cache.PendingCount==0&&object.ReferenceEquals(completedImage,cache.TryReadWarm(path))&&after.Matches(cache)&&File.ReadAllBytes(path).SequenceEqual(original)&&File.GetLastWriteTimeUtc(path)==time,"the original pending owner completes normally after warm misses and its frozen result becomes visible without a second decode or file write");
    Unlocked(path,check,"warm probes around a pending decoder release the real input handle");
   }
  }
  static void Capacity(string root,Action<bool,string> check){
   var byteCache=new DecodedThumbnailCache(2L*512*512*4,128);var countCache=new DecodedThumbnailCache(DecodedThumbnailCache.DefaultBudget,2);
   string[] paths=Enumerable.Range(0,4).Select(n=>Path.Combine(root,"capacity-"+n+".512.png")).ToArray();foreach(string path in paths)Picture(path,512,512,155);
   var zero=byteCache.Read(paths[0]);byteCache.Read(paths[1]);byteCache.Read(paths[0]);byteCache.Read(paths[2]);long decodes=byteCache.DecodeCount;
   check(byteCache.Count==2&&byteCache.Bytes<=2L*512*512*4&&object.ReferenceEquals(zero,byteCache.Read(paths[0]))&&byteCache.DecodeCount==decodes,"byte-budget eviction keeps the recently touched entry and never exceeds the pixel budget");
   byteCache.Read(paths[1]);check(byteCache.DecodeCount==decodes+1&&byteCache.Count==2,"least-recently-used entry is genuinely decoded again after byte eviction");
   foreach(string path in paths)countCache.Read(path);
   check(countCache.Count==2&&countCache.Bytes<=DecodedThumbnailCache.DefaultBudget,"entry-count ceiling applies independently of the byte budget");
   var tooSmall=new DecodedThumbnailCache(1024,2);tooSmall.Read(paths[0]);check(tooSmall.Count==0&&tooSmall.Bytes==0,"an individual decoded bitmap larger than the budget is returned but not cached");
   var global=new DecodedThumbnailCache();foreach(string path in paths)global.Read(path);global.Clear();check(global.Count==0&&global.Bytes==0&&global.FailureCount==0&&global.PendingCount==0,"clear drops every strong positive/negative entry and resets its accounted pixel bytes");
  }
  static void Concurrent(string root,Action<bool,string> check){
   string path=Path.Combine(root,"concurrent.512.png"),other=Path.Combine(root,"independent.512.png");Picture(path,512,512,175);Picture(other,81,57,189);
   using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false)){
    var cache=new DecodedThumbnailCache(DecodedThumbnailCache.DefaultBudget,128,p=>{var bitmap=DecodedThumbnailCache.DecodeFile(p);if(string.Equals(p,path,StringComparison.OrdinalIgnoreCase)){entered.Set();if(!release.WaitOne(6000))throw new TimeoutException("Concurrent test barrier");}return bitmap;});
    var owner=Background(()=>cache.Read(path));Task<BitmapImage>[] joined=null;Task<BitmapImage> independent=null;
    try{
     check(entered.WaitOne(6000),"same-path concurrency owner reaches the real file decoder");
     joined=Enumerable.Range(0,7).Select(n=>Background(()=>cache.Read(n%2==0?path:path.ToUpperInvariant()))).ToArray();Wait(()=>cache.JoinCount==7,"all same-path callers joined");
     independent=Background(()=>cache.Read(other));var independentImage=Result(independent);check(independentImage.PixelWidth==81&&!owner.IsCompleted&&cache.DecodeCount==2,"an unrelated thumbnail progresses while one same-path decode is blocked");
    }finally{release.Set();if(joined!=null)Task.WaitAll(joined,6000);owner.Wait(6000);if(independent!=null)independent.Wait(6000);}
    var expected=Result(owner);check(joined!=null&&joined.All(task=>object.ReferenceEquals(expected,Result(task)))&&cache.DecodeCount==2&&cache.PendingCount==0,"eight concurrent same-path/case-insensitive callers share one actual decode and one frozen result, then release the in-flight map");
    Unlocked(path,check,"in-flight coalescing does not retain a native file handle after completion");
   }
   string bad=Path.Combine(root,"concurrent-bad.512.png");File.WriteAllBytes(bad,new byte[]{1,2,3,4,5});
   using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false)){
    var cache=new DecodedThumbnailCache(DecodedThumbnailCache.DefaultBudget,128,p=>{entered.Set();if(!release.WaitOne(6000))throw new TimeoutException("Failure test barrier");return DecodedThumbnailCache.DecodeFile(p);});
    var owner=Task.Factory.StartNew(()=>Reject(()=>cache.Read(bad)),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);Task<bool> joined=null;
    try{check(entered.WaitOne(6000),"concurrent corrupt-file owner starts");joined=Task.Factory.StartNew(()=>Reject(()=>cache.Read(bad)),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);Wait(()=>cache.JoinCount==1,"failed-read joiner");}
    finally{release.Set();owner.Wait(6000);if(joined!=null)joined.Wait(6000);}
    check(owner.Result&&joined!=null&&joined.Result&&cache.DecodeCount==1&&cache.PendingCount==0&&cache.Count==0,"concurrent corrupt readers both receive a real failure without deadlock, a false positive or an in-flight leak");
   }
  }
  static void ClearAndChangedRead(string root,Action<bool,string> check){
   string path=Path.Combine(root,"clear.512.png");Picture(path,512,512,141);int calls=0;
   using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false)){
    var cache=new DecodedThumbnailCache(DecodedThumbnailCache.DefaultBudget,128,p=>{var image=DecodedThumbnailCache.DecodeFile(p);if(Interlocked.Increment(ref calls)==1){entered.Set();if(!release.WaitOne(6000))throw new TimeoutException("Clear test barrier");}return image;});
    var old=Background(()=>cache.Read(path));BitmapImage current=null;
    try{check(entered.WaitOne(6000),"old-generation decode has real detached pixels");cache.Clear();current=cache.Read(path);check(cache.Count==1&&cache.PendingCount==0&&!old.IsCompleted,"clear allows a new generation to decode without joining an old blocked caller");}
    finally{release.Set();old.Wait(6000);}
    check(!object.ReferenceEquals(Result(old),current)&&object.ReferenceEquals(current,cache.Read(path))&&cache.Count==1&&cache.DecodeCount==2&&cache.PendingCount==0,"old task completion cannot refill or evict the new-generation cache entry");
   }
   string changing=Path.Combine(root,"changing.512.png");Picture(changing,512,512,111);
   using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false)){
    var cache=new DecodedThumbnailCache(DecodedThumbnailCache.DefaultBudget,128,p=>{var image=DecodedThumbnailCache.DecodeFile(p);if(!entered.WaitOne(0)){entered.Set();if(!release.WaitOne(6000))throw new TimeoutException("Changed stamp barrier");}return image;});
    var old=Background(()=>cache.Read(changing));
    try{check(entered.WaitOne(6000),"stamp-race decode reaches its post-read barrier");Picture(changing,101,73,221);File.SetLastWriteTimeUtc(changing,new DateTime(2026,1,2,0,0,0,DateTimeKind.Utc));}
    finally{release.Set();old.Wait(6000);}
    check(Result(old).PixelWidth==512&&cache.Count==0&&cache.PendingCount==0,"a file changed during decoding is not admitted using the old pre-read stamp");var fresh=cache.Read(changing);check(fresh.PixelWidth==101&&Red(fresh,221)&&cache.Count==1,"a following read obtains the changed thumbnail rather than stale pixels");
   }
   string replaced=Path.Combine(root,"replaced-pending.512.png");Picture(replaced,512,512,117);calls=0;
   using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false)){
    var cache=new DecodedThumbnailCache(DecodedThumbnailCache.DefaultBudget,128,p=>{var image=DecodedThumbnailCache.DecodeFile(p);if(Interlocked.Increment(ref calls)==1){entered.Set();if(!release.WaitOne(6000))throw new TimeoutException("Replacement test barrier");}return image;});
    var old=Background(()=>cache.Read(replaced));BitmapImage fresh=null;
    try{check(entered.WaitOne(6000),"old-stamp read is still pending");Picture(replaced,109,83,229);File.SetLastWriteTimeUtc(replaced,new DateTime(2026,1,4,0,0,0,DateTimeKind.Utc));fresh=cache.Read(replaced);check(fresh.PixelWidth==109&&!old.IsCompleted,"a new stamp does not join an in-flight request for the old file version");}
    finally{release.Set();old.Wait(6000);}
    check(Result(old).PixelWidth==512&&object.ReferenceEquals(fresh,cache.Read(replaced))&&cache.Count==1&&cache.PendingCount==0,"old-stamp completion cannot evict or overwrite the newer same-path result");
   }
  }
  static void InvalidatePending(string root,Action<bool,string> check,bool oldFails){
   string mode=oldFails?"failure":"success",path=Path.Combine(root,"invalidate-pending-"+mode+".512.png"),other=Path.Combine(root,"invalidate-pending-retained-"+mode+".512.png");
   var time=new DateTime(2026,1,6,0,0,0,DateTimeKind.Utc);Picture(path,89,61,183);Picture(other,73,47,223);byte[] good=File.ReadAllBytes(path);
   if(oldFails)File.WriteAllBytes(path,new byte[good.Length]);File.SetLastWriteTimeUtc(path,time);int calls=0;BitmapImage oldImage=null;
   using(var oldEntered=new ManualResetEvent(false))using(var releaseOld=new ManualResetEvent(false))using(var newEntered=new ManualResetEvent(false))using(var releaseNew=new ManualResetEvent(false)){
    var cache=new DecodedThumbnailCache(DecodedThumbnailCache.DefaultBudget,128,p=>{
     if(!string.Equals(p,path,StringComparison.OrdinalIgnoreCase))return DecodedThumbnailCache.DecodeFile(p);
     int call=Interlocked.Increment(ref calls);
     // Both barriers run after a real disk decode (or its real exception), so
     // the old failed read cannot accidentally decode repaired bytes instead.
     try{return DecodedThumbnailCache.DecodeFile(p);}finally{
      if(call==1){oldEntered.Set();if(!releaseOld.WaitOne(6000))throw new TimeoutException("Invalidate old "+mode+" barrier");}
      else if(call==2){newEntered.Set();if(!releaseNew.WaitOne(6000))throw new TimeoutException("Invalidate replacement "+mode+" barrier");}
     }
    });
    var retained=cache.Read(other);long retainedBytes=cache.Bytes;var old=Task.Factory.StartNew(()=>Reject(()=>oldImage=cache.Read(path)),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);Task<BitmapImage> replacement=null,joined=null;
    try{
     check(oldEntered.WaitOne(6000),"old "+mode+" path reaches a deterministic barrier after real disk decoding");
     cache.Invalidate(path.ToUpperInvariant());if(oldFails){File.WriteAllBytes(path,good);File.SetLastWriteTimeUtc(path,time);}
     replacement=Background(()=>cache.Read(path));
     check(newEntered.WaitOne(6000)&&cache.PendingCount==1&&!old.IsCompleted,"per-path invalidation detaches old "+mode+" work and lets an identically stamped replacement start independently");
     releaseOld.Set();if(!old.Wait(6000))throw new TimeoutException("Invalidated old "+mode+" did not finish");
     check(old.Result==oldFails&&(oldFails?oldImage==null:oldImage!=null&&Red(oldImage,183)),"the detached old "+mode+" caller receives only its own real decode result");
     check(!replacement.IsCompleted&&cache.PendingCount==1&&cache.Count==1&&cache.Bytes==retainedBytes&&cache.FailureCount==0&&object.ReferenceEquals(retained,cache.Read(other)),"old "+mode+" completion cannot refill the invalidated path, delete its replacement pending entry, poison failure backoff or evict another positive path");
     joined=Background(()=>cache.Read(path));Wait(()=>cache.JoinCount==1,"replacement still accepts same-path joiner after old "+mode+" completes");
     check(!joined.IsCompleted&&cache.DecodeCount==3,"a caller after old "+mode+" completion joins the live replacement rather than starting a third target decode");
    }finally{
     releaseOld.Set();releaseNew.Set();old.Wait(6000);if(replacement!=null)replacement.Wait(6000);if(joined!=null)joined.Wait(6000);
    }
    var current=Result(replacement);
    check(joined!=null&&object.ReferenceEquals(current,Result(joined))&&object.ReferenceEquals(current,cache.Read(path))&&!object.ReferenceEquals(oldImage,current)&&Red(current,183)&&cache.DecodeCount==3&&cache.Count==2&&cache.FailureCount==0&&cache.PendingCount==0,"replacement after invalidated old "+mode+" publishes its own frozen image exactly once, serves its joiner and leaves no pending work");
    check(object.ReferenceEquals(retained,cache.Read(other))&&new FileInfo(path).Length==good.Length&&File.GetLastWriteTimeUtc(path)==time,"invalidated pending "+mode+" preserves the other cached image and exercises unchanged target length and modification time");
    Unlocked(path,check,"invalidated pending "+mode+" and its replacement release all target file handles");
   }
  }
  static void Layout(Grid host){host.Measure(new Size(340,240));host.Arrange(new Rect(0,0,340,240));host.UpdateLayout();}
  static void Mount(Grid host,ThumbnailImage image){host.Children.Clear();host.Children.Add(image);Layout(host);Until(()=>image.IsLoaded&&image.PreviewState=="ready"&&image.Source!=null,"real Loaded thumbnail is ready");}
  static void Unmount(Grid host,ThumbnailImage image){host.Children.Clear();Layout(host);Until(()=>!image.IsLoaded,"real Unloaded thumbnail event");}
  static void Lifecycle(string root,Action<bool,string> check){
   using(var library=new Library(Path.Combine(root,"lifecycle-library")))using(var second=new Library(Path.Combine(root,"second-library"))){
    library.EnableReferenceAssets();string original=Path.Combine(root,"lifecycle-original.png");Picture(original,512,512,151);string id=library.AddReferenceAssets(new[]{original},"").Single();var asset=library.All().Single(a=>a.Id==id);
    ThumbnailImage.CurrentLibrary=library;ThumbnailImage.RetryFailedThumbnails();var cache=ThumbnailImage.DecodedCache;long before=cache.DecodeCount;var host=new Grid{Width=340,Height=240};var image=new ThumbnailImage{Width=220,Height=160,DataContext=asset};
    using(var source=new HwndSource(new HwndSourceParameters("Nook VM decoded thumbnail lifecycle"){Width=340,Height=240,WindowStyle=0})){
     source.RootVisual=host;IDisposable pause=null;
     try{
      Mount(host,image);var first=image.Source;bool same=true,cleared=true;var watch=Stopwatch.StartNew();long allocated=AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
      for(int n=0;n<100;n++){Unmount(host,image);cleared&=image.Source==null&&image.PreviewState=="idle";Mount(host,image);same&=object.ReferenceEquals(first,image.Source);}
      watch.Stop();File.AppendAllText(Path.Combine(root,"decoded-thumbnail-benchmark.txt"),"ui_unload_reload_100_ms="+watch.Elapsed.TotalMilliseconds.ToString("F3",System.Globalization.CultureInfo.InvariantCulture)+"; decoded="+(cache.DecodeCount-before)+"; managed_allocated_bytes="+(AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize-allocated)+Environment.NewLine,Encoding.UTF8);
      check(cleared&&same&&cache.DecodeCount==before+1,"100 actual UI unload/reload cycles clear each view Source but share one decoded frozen thumbnail");
      ThumbnailImage.CurrentLibrary=library;check(cache.Count==1,"reassigning the same current Library does not discard useful decoded thumbnails");
      Picture(original,101,79,213);File.SetLastWriteTimeUtc(original,new DateTime(2026,1,3,0,0,0,DateTimeKind.Utc));Unmount(host,image);image.DataContext=library.All().Single(a=>a.Id==id);Mount(host,image);
      check(((BitmapSource)image.Source).PixelWidth==101&&Red((BitmapSource)image.Source,213)&&!object.ReferenceEquals(first,image.Source),"real Library source-version refresh regenerates disk data and cannot reuse old decoded pixels");
      var draining=ThumbnailImage.PauseAndDrainAsync();Until(()=>draining.IsCompleted,"real scheduler drains before cache clear");pause=draining.GetAwaiter().GetResult();
      check(cache.Count==0&&cache.Bytes==0&&cache.PendingCount==0,"a completed destructive-operation pause releases all decoded cache ownership only after active thumbnail jobs drain");
      Unmount(host,image);pause.Dispose();pause=null;Mount(host,image);long decoded=cache.DecodeCount;
      check(cache.Count==1&&image.Source!=null,"after a drained pause the same real gallery tile reloads successfully");
      ThumbnailImage.RetryFailedThumbnails();check(cache.Count==0&&cache.FailureCount==0,"F5 retry clears both successful decoded copies and same-stamp failures");Unmount(host,image);Mount(host,image);
      check(cache.DecodeCount==decoded+1,"a real reload after F5 performs a fresh decode instead of returning the prior instance");
      ThumbnailImage.CurrentLibrary=second;check(cache.Count==0&&cache.Bytes==0,"switching current Library releases decoded thumbnails even if old views still own their Source");
      Unlocked(original,check,"UI lifecycle and source regeneration release the original file handle");Unlocked(library.ThumbnailPath(asset),check,"UI lifecycle and cache eviction release the generated thumbnail file handle");
     }finally{if(pause!=null)pause.Dispose();host.Children.Clear();Pump();var draining=ThumbnailImage.PauseAndDrainAsync();Until(()=>draining.IsCompleted,"final thumbnail scheduler drain");using(draining.GetAwaiter().GetResult()){}source.RootVisual=null;ThumbnailImage.CurrentLibrary=null;}
    }
   }
  }
 }
}
