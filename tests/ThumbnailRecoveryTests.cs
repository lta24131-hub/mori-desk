using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;
using Mori;

namespace MoriDesk {
 // VM-only integration tests. All images, references, databases and reparse
 // targets are synthetic. The production decoder/build/cache are not replaced.
 internal static class ThumbnailRecoveryTests {
  static readonly DateTime FixtureTime=new DateTime(2026,1,11,0,0,0,DateTimeKind.Utc);
  static void Picture(string path,int width,int height,byte red){
   var pixels=new byte[width*height*4];for(int n=0;n<pixels.Length;n+=4){pixels[n]=41;pixels[n+1]=83;pixels[n+2]=red;pixels[n+3]=255;}
   var image=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var file=File.Create(path))encoder.Save(file);File.SetLastWriteTimeUtc(path,FixtureTime);
  }
  static void BigEndian(byte[] data,int offset,uint value){for(int n=3;n>=0;n--){data[offset+n]=(byte)value;value>>=8;}}
  static void HugePngHeader(string path){
   // A correct PNG signature and IHDR (including CRC), but deliberately no
   // pixel payload. Its claimed trillion pixels must never reach WIC EndInit.
   var data=new byte[]{137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82,0,0,0,0,0,0,0,0,8,6,0,0,0,0,0,0,0};BigEndian(data,16,1000000);BigEndian(data,20,1000000);
   uint crc=0xffffffff;for(int n=12;n<29;n++){crc^=data[n];for(int bit=0;bit<8;bit++)crc=(crc&1)!=0?(crc>>1)^0xedb88320:crc>>1;}BigEndian(data,29,~crc);
   File.WriteAllBytes(path,data);File.SetLastWriteTimeUtc(path,FixtureTime.AddDays(2));
  }
  static bool HeaderDenied(DecodedThumbnailCache cache,string path){try{cache.ReadOwned(path);return false;}catch(InvalidDataException){return true;}}
  static bool Pixel(BitmapSource image,int width,int height,byte red){
   if(image==null||!image.IsFrozen||image.PixelWidth!=width||image.PixelHeight!=height)return false;
   var pixel=new byte[4];new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0).CopyPixels(new Int32Rect(0,0,1,1),pixel,4,0);return pixel[0]==41&&pixel[1]==83&&pixel[2]==red&&pixel[3]==255;
  }
  static string Hash(string path){using(var file=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(file));}
  static bool Unavailable(Func<BitmapImage> read){
   try{return read()==null;}catch(InvalidDataException){return true;}catch(IOException){return true;}catch(FormatException){return true;}catch(NotSupportedException){return true;}catch(UnauthorizedAccessException){return true;}catch(ArgumentException){return true;}catch(InvalidOperationException){return true;}
  }
  static Asset Reference(Library library,string path){string id=library.AddReferenceAssets(new[]{path},"").Single();return library.All().Single(a=>a.Id==id);}
  static string ThumbnailMetadata(Library library,Asset asset){
   var row=library.Db.Query("SELECT width,height,color,perceptual,bytes"+(asset.IsReference?",thumb_stamp":"")+" FROM "+(asset.IsReference?"desk_refs":"assets")+" WHERE id="+Database.Q(asset.IsReference?asset.Id.Substring(4):asset.Id)).Single();
   return string.Join("|",new[]{row["width"],row["height"],row["color"],row["perceptual"],row["bytes"],asset.IsReference?row["thumb_stamp"]:""});
  }
  static bool WriteDenied(Action action){try{action();return false;}catch(IOException){return true;}}
  static void Broken(string path){File.WriteAllBytes(path,new byte[]{1,2,3,4,5});File.SetLastWriteTimeUtc(path,FixtureTime.AddDays(1));}
  static bool NoTemporaryFiles(Library library){return Directory.GetFiles(Path.Combine(library.Root,".mori","thumbs"),"*.tmp",SearchOption.TopDirectoryOnly).Length==0;}
  static void Unlocked(string path,Action<bool,string> check,string label){using(var file=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(file.Length>0,label);}
  static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
  static void Until(Func<bool> condition,string description){var watch=Stopwatch.StartNew();while(!condition()&&watch.ElapsedMilliseconds<6000){Pump();Thread.Sleep(1);}if(!condition())throw new TimeoutException("Thumbnail recovery: "+description);Pump();}
  static IDisposable Drained(Task<IDisposable> drain){Until(()=>drain.IsCompleted,"scheduler drain");return drain.GetAwaiter().GetResult();}
  static void Layout(Grid host){host.Measure(new Size(340,240));host.Arrange(new Rect(0,0,340,240));host.UpdateLayout();}
  static int AvailableSlots(){
   const BindingFlags fields=BindingFlags.Instance|BindingFlags.NonPublic;
   var scheduler=typeof(ThumbnailImage).GetField("Gate",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);var shared=typeof(ThumbnailLoadScheduler).GetField("shared",fields).GetValue(scheduler);return ((SemaphoreSlim)typeof(ThumbnailLoadGate).GetField("slots",fields).GetValue(shared)).CurrentCount;
  }
  internal static void Run(string root,Action<bool,string> check){
   if(!Path.GetFullPath(root).StartsWith(@"C:\NookLab\Audit\",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Thumbnail recovery tests require the isolated VM fixture root.");
   root=Path.Combine(root,"thumbnail-recovery");Directory.CreateDirectory(root);
   var app=Application.Current??new Application();var shutdown=app.ShutdownMode;var previous=ThumbnailImage.CurrentLibrary;var context=SynchronizationContext.Current;
   app.ShutdownMode=ShutdownMode.OnExplicitShutdown;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
   try{
    using(Drained(ThumbnailImage.PauseAndDrainAsync())){}ThumbnailImage.RetryFailedThumbnails();
     OwnedAndF5(root,check);OwnedSizeBounds(root,check);OwnedModeIsolation(root,check);ReferenceFailuresAndLocks(root,check);Concurrent(root,check);Safety(root,check);LinkedLibraryReads(root,false,check);LinkedLibraryReads(root,true,check);UiRecovery(root,check);
   }finally{
    try{using(Drained(ThumbnailImage.PauseAndDrainAsync())){}}
    finally{ThumbnailImage.CurrentLibrary=previous;ThumbnailImage.RetryFailedThumbnails();SynchronizationContext.SetSynchronizationContext(context);app.ShutdownMode=shutdown;}
   }
  }
  static void OwnedAndF5(string root,Action<bool,string> check){
   string source=Path.Combine(root,"owned-source.png");Picture(source,97,61,173);
   using(var library=new Library(Path.Combine(root,"owned-library"))){
    var asset=library.Import(source,"","Source name","https://example.org/","source tag","source note").Item;string original=library.FullPath(asset),originalHash=Hash(original);DateTime originalTime=File.GetLastWriteTimeUtc(original);
    var first=library.ReadThumbnail(asset);string thumbnail=library.ThumbnailPath(asset);File.SetLastWriteTimeUtc(thumbnail,FixtureTime.AddHours(1));byte[] good=File.ReadAllBytes(thumbnail);DateTime goodTime=File.GetLastWriteTimeUtc(thumbnail);
    check(Pixel(first,97,61,173),"asset-aware read generates and returns the real frozen copied-material thumbnail");
    bool healthy=true;for(int n=0;n<8;n++)healthy&=Pixel(library.ReadThumbnail(asset),97,61,173);ThumbnailImage.RetryFailedThumbnails();healthy&=Pixel(library.ReadThumbnail(asset),97,61,173);
    check(healthy&&File.ReadAllBytes(thumbnail).SequenceEqual(good)&&File.GetLastWriteTimeUtc(thumbnail)==goodTime,"healthy repeated reads and F5 preserve successful derived PNG bytes and timestamp");
    Broken(thumbnail);var repaired=library.ReadThumbnail(asset);
    check(Pixel(repaired,97,61,173)&&Pixel(DecodedThumbnailCache.DecodeFile(thumbnail),97,61,173),"changed-stamp corrupt derived PNG self-repairs through the real source without fixture deletion or repair");
    check(Hash(original)==originalHash&&File.GetLastWriteTimeUtc(original)==originalTime,"automatic copied-material recovery preserves original SHA-256 and timestamp");
    good=File.ReadAllBytes(thumbnail);DateTime sameTime=File.GetLastWriteTimeUtc(thumbnail);var blank=new byte[good.Length];File.WriteAllBytes(thumbnail,blank);File.SetLastWriteTimeUtc(thumbnail,sameTime);
    // A same-length/time replacement is intentionally invisible to the stamp
    // cache until F5. The test does not pretend the fingerprint is a hash.
    ThumbnailImage.RetryFailedThumbnails();var sameStampRepair=library.ReadThumbnail(asset);
    check(Pixel(sameStampRepair,97,61,173)&&!File.ReadAllBytes(thumbnail).SequenceEqual(blank),"explicit F5 rebuilds a same-length/time corrupt derived PNG rather than only clearing its in-memory failure");
    var indexed=library.All().Single(a=>a.Id==asset.Id);
    check(indexed.Name=="Source name"&&indexed.Tags=="source tag"&&indexed.Notes=="source note"&&indexed.Source=="https://example.org/"&&indexed.Width==97&&indexed.Height==61,"recovery updates computed dimensions without replacing user metadata");
    check(Hash(original)==originalHash&&File.GetLastWriteTimeUtc(original)==originalTime&&NoTemporaryFiles(library),"same-stamp recovery leaves the original untouched and no pending PNG files");
    Unlocked(original,check,"recovery retains no original file handle");Unlocked(thumbnail,check,"recovery retains no generated file handle");
   }
  }
  static void OwnedSizeBounds(string root,Action<bool,string> check){
   string generic=Path.Combine(root,"generic-513.512.png"),boundary=Path.Combine(root,"owned-boundary.512.png");Picture(generic,513,37,197);Picture(boundary,512,31,199);var local=new DecodedThumbnailCache();
   string genericHash=Hash(generic);DateTime genericTime=File.GetLastWriteTimeUtc(generic);
   check(Pixel(local.Read(generic),513,37,197)&&local.Count==0,"generic filename-based reader still accepts a real 513px image without retaining it as a bounded thumbnail");
   long decoded=local.DecodeCount;
   check(HeaderDenied(local,generic)&&local.DecodeCount==decoded&&local.PendingCount==0&&local.FailureCount==1,"owned reader rejects a real oversized PNG before WIC pixel decoding and releases its pending request");
   check(Pixel(local.Read(generic),513,37,197)&&local.DecodeCount==decoded+1&&Hash(generic)==genericHash&&File.GetLastWriteTimeUtc(generic)==genericTime,"owned-only size rejection never poisons generic 513px decoding or modifies its input");
   var exact=local.ReadOwned(boundary);decoded=local.DecodeCount;
   check(Pixel(exact,512,31,199)&&object.ReferenceEquals(exact,local.Read(boundary))&&object.ReferenceEquals(exact,local.ReadOwned(boundary))&&local.DecodeCount==decoded,"512px boundary is accepted and both modes share the same bounded frozen positive cache entry");
   Unlocked(generic,check,"owned size rejection and generic 513px reads release every handle");Unlocked(boundary,check,"owned 512px boundary decoding retains no stream handle");
   string source=Path.Combine(root,"owned-size-source.png");Picture(source,95,57,233);
   using(var library=new Library(Path.Combine(root,"owned-size-library"))){
    library.EnableReferenceAssets();var copied=library.Import(source,"","Size guard copied asset","","","").Item;var reference=Reference(library,source);var cache=ThumbnailImage.DecodedCache;
    foreach(var asset in new[]{copied,reference}){
     check(Pixel(library.ReadThumbnail(asset),95,57,233),"size guard fixture has an actual healthy "+(asset.IsReference?"reference":"copied-material")+" thumbnail");
     string original=library.FullPath(asset),originalHash=Hash(original),metadata=ThumbnailMetadata(library,asset),thumbnail=library.ThumbnailPath(asset);DateTime originalTime=File.GetLastWriteTimeUtc(original);int gates=ThumbnailBuildGate.ActivePathCount;
     for(int variant=0;variant<3;variant++){
      if(variant==0)Picture(thumbnail,513,17,239);else if(variant==1)Picture(thumbnail,17,513,241);else HugePngHeader(thumbnail);
      cache.Invalidate(thumbnail);decoded=cache.DecodeCount;
      check(HeaderDenied(cache,thumbnail)&&cache.DecodeCount==decoded&&cache.PendingCount==0,"owned "+(variant==2?"header-only trillion-pixel":"real oversized "+(variant==0?"width":"height"))+" PNG is rejected before pixel allocation");
      var repaired=library.ReadThumbnail(asset);
      check(Pixel(repaired,95,57,233)&&Pixel(DecodedThumbnailCache.DecodeFile(thumbnail),95,57,233)&&cache.DecodeCount==decoded+1,"asset-aware read automatically repairs oversized owned cache from its real source instead of returning unsafe pixels");
      string repairedHash=Hash(thumbnail);DateTime repairedTime=File.GetLastWriteTimeUtc(thumbnail);long warm=cache.DecodeCount;bool shared=true;
      for(int n=0;n<8;n++)shared&=object.ReferenceEquals(repaired,library.ReadThumbnail(asset));
      check(shared&&object.ReferenceEquals(repaired,cache.Read(thumbnail))&&cache.DecodeCount==warm&&Hash(thumbnail)==repairedHash&&File.GetLastWriteTimeUtc(thumbnail)==repairedTime,"repaired healthy bounded pixels are reused without another decode or disk rewrite");
      check(Hash(original)==originalHash&&File.GetLastWriteTimeUtc(original)==originalTime&&ThumbnailMetadata(library,asset)==metadata&&cache.PendingCount==0&&ThumbnailBuildGate.ActivePathCount==gates&&NoTemporaryFiles(library),"oversized-cache recovery preserves original SHA/time and indexed metadata and releases pending files, cache requests and output gates");
      Unlocked(original,check,"oversized-cache recovery releases original handles");Unlocked(thumbnail,check,"oversized-cache recovery releases derived PNG handles");
     }
    }
   }
  }
  static void OwnedModeIsolation(string root,Action<bool,string> check){
   string large=Path.Combine(root,"owned-mode-large.512.png");Picture(large,513,37,207);string hash=Hash(large);DateTime time=File.GetLastWriteTimeUtc(large);
   using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false)){
    var cache=new DecodedThumbnailCache(DecodedThumbnailCache.DefaultBudget,128,p=>{var image=DecodedThumbnailCache.DecodeFile(p);entered.Set();if(!release.WaitOne(6000))throw new TimeoutException("Generic oversized decode barrier");return image;});
    var generic=Task.Factory.StartNew(()=>cache.Read(large),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);Task<bool> owned=null;Task<BitmapImage> joiner=null;
    try{
     if(!entered.WaitOne(6000))throw new TimeoutException("Generic oversized owner did not start");
     owned=Task.Factory.StartNew(()=>HeaderDenied(cache,large),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
     if(!owned.Wait(6000))throw new TimeoutException("Owned reader joined an unsafe generic decode");
     check(owned.Result&&cache.PendingCount==1&&!generic.IsCompleted&&cache.DecodeCount==1,"owned oversized read rejects independently without joining or clearing the still-running generic pending owner");
     joiner=Task.Factory.StartNew(()=>cache.Read(large),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);Until(()=>cache.JoinCount==1,"generic join after owned rejection");
     check(!joiner.IsCompleted&&cache.PendingCount==1&&cache.DecodeCount==1,"owned failure leaves the generic pending entry joinable instead of starting a duplicate decoder");
    }finally{release.Set();if(!generic.Wait(6000))throw new TimeoutException("Generic oversized owner did not drain");if(owned!=null&&!owned.Wait(6000))throw new TimeoutException("Owned oversized request did not drain");if(joiner!=null&&!joiner.Wait(6000))throw new TimeoutException("Generic oversized joiner did not drain");}
    check(Pixel(generic.Result,513,37,207)&&joiner!=null&&object.ReferenceEquals(generic.Result,joiner.Result)&&cache.PendingCount==0&&cache.Count==0&&Pixel(cache.Read(large),513,37,207)&&Hash(large)==hash&&File.GetLastWriteTimeUtc(large)==time,"generic owner and joiner retain original 513px behavior after owned rejection without pending leaks or input changes");
   }
   Unlocked(large,check,"independent owned/generic requests release the oversized input handle");
   foreach(bool failed in new[]{false,true}){
    string path=Path.Combine(root,failed?"owned-mode-failure.512.png":"owned-mode-success.512.png");Picture(path,91,53,219);byte[] good=File.ReadAllBytes(path);string expectedHash=Hash(path);time=File.GetLastWriteTimeUtc(path);if(failed)File.WriteAllBytes(path,new byte[good.Length]);
    File.SetLastWriteTimeUtc(path,time);BitmapImage genericImage=null,ownedImage=null;
    using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false)){
     // Barriers wrap the real decoder. The failed variant has already decoded
     // actual corrupt bytes before the identically stamped fixture is restored.
     var cache=new DecodedThumbnailCache(DecodedThumbnailCache.DefaultBudget,128,p=>{try{return DecodedThumbnailCache.DecodeFile(p);}finally{entered.Set();if(!release.WaitOne(6000))throw new TimeoutException("Generic mode completion barrier");}});
     var generic=Task.Factory.StartNew(()=>Unavailable(()=>{genericImage=cache.Read(path);return genericImage;}),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);long retainedBytes=0;
     try{
      if(!entered.WaitOne(6000))throw new TimeoutException("Generic mode owner did not start");
      if(failed){File.WriteAllBytes(path,good);File.SetLastWriteTimeUtc(path,time);}
      ownedImage=cache.ReadOwned(path);retainedBytes=cache.Bytes;
      check(Pixel(ownedImage,91,53,219)&&cache.PendingCount==1&&!generic.IsCompleted&&object.ReferenceEquals(ownedImage,cache.Read(path)),"owned success shares bounded pixels while preserving the other mode's independent pending completion");
     }finally{release.Set();if(!generic.Wait(6000))throw new TimeoutException("Generic mode completion did not drain");}
     check(generic.Result==failed&&(failed?genericImage==null:object.ReferenceEquals(genericImage,ownedImage))&&cache.Count==1&&cache.Bytes==retainedBytes&&cache.PendingCount==0&&cache.FailureCount==0&&object.ReferenceEquals(ownedImage,cache.ReadOwned(path))&&object.ReferenceEquals(ownedImage,cache.Read(path)),"late generic "+(failed?"failure cannot evict or poison":"success cannot duplicate-charge or replace")+" a healthy owned positive entry");
     check(Hash(path)==expectedHash&&File.GetLastWriteTimeUtc(path)==time,"cross-mode completion preserves the restored synthetic input SHA/time");
    }
    Unlocked(path,check,"cross-mode "+(failed?"failure":"success")+" releases all real decoder handles");
   }
  }
  static void ReferenceFailuresAndLocks(string root,Action<bool,string> check){
   string source=Path.Combine(root,"reference-source.png");Picture(source,83,47,181);string originalHash=Hash(source);DateTime originalTime=File.GetLastWriteTimeUtc(source);
   using(var library=new Library(Path.Combine(root,"reference-library"))){
    library.EnableReferenceAssets();var asset=Reference(library,source);check(Pixel(library.ReadThumbnail(asset),83,47,181),"reference read uses a real external original without copying it into the library");string thumbnail=library.ThumbnailPath(asset);
    Broken(thumbnail);byte[] broken=File.ReadAllBytes(thumbnail);DateTime brokenTime=File.GetLastWriteTimeUtc(thumbnail);string offline=source+".offline";File.Move(source,offline);
    try{check(Unavailable(()=>library.ReadThumbnail(asset))&&File.ReadAllBytes(thumbnail).SequenceEqual(broken)&&File.GetLastWriteTimeUtc(thumbnail)==brokenTime,"missing reference source leaves the old corrupt derived file intact and reports unavailable");}
    finally{if(!File.Exists(source)&&File.Exists(offline))File.Move(offline,source);}
    check(Pixel(library.ReadThumbnail(asset),83,47,181),"returning missing source permits immediate automatic recovery without manual deletion or F5");
    Broken(thumbnail);broken=File.ReadAllBytes(thumbnail);brokenTime=File.GetLastWriteTimeUtc(thumbnail);
    using(var held=new FileStream(source,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(Unavailable(()=>library.ReadThumbnail(asset))&&File.ReadAllBytes(thumbnail).SequenceEqual(broken)&&File.GetLastWriteTimeUtc(thumbnail)==brokenTime,"exclusively locked source cannot destroy or replace the previous corrupt derived output");
    check(!ThumbnailBuildGate.HasDecodeFailure(ThumbnailBuildGate.SourceStamp(source))&&Pixel(library.ReadThumbnail(asset),83,47,181),"releasing a transient source lock immediately recovers without source-failure poisoning or F5");
    Broken(thumbnail);ThumbnailImage.DecodedCache.Invalidate(thumbnail);broken=File.ReadAllBytes(thumbnail);brokenTime=File.GetLastWriteTimeUtc(thumbnail);string sourceStamp=ThumbnailBuildGate.SourceStamp(source);
    using(var held=new FileStream(thumbnail,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(Unavailable(()=>library.ReadThumbnail(asset)),"a transient exclusive derived-output lock is unavailable, not permission to delete or replace it");
    check(File.ReadAllBytes(thumbnail).SequenceEqual(broken)&&File.GetLastWriteTimeUtc(thumbnail)==brokenTime&&!ThumbnailBuildGate.HasDecodeFailure(sourceStamp),"derived-output sharing failure preserves its bytes/time and never poisons source decoding");
    check(Pixel(library.ReadThumbnail(asset),83,47,181),"releasing a derived-output lock immediately permits repair without clearing F5 caches");
    check(Hash(source)==originalHash&&File.GetLastWriteTimeUtc(source)==originalTime&&Directory.GetFiles(Path.Combine(library.Root,"原图"),"*",SearchOption.AllDirectories).Length==0&&NoTemporaryFiles(library),"reference failure and retry paths preserve source SHA/time, copy no original, and clean temporary output");
    Unlocked(source,check,"reference failure/retry releases original handles");Unlocked(thumbnail,check,"reference failure/retry releases output handles");
   }
  }
  static void Concurrent(string root,Action<bool,string> check){
   string source=Path.Combine(root,"concurrent-source.png"),otherSource=Path.Combine(root,"independent-source.png"),folder=Path.Combine(root,"concurrent-library");Picture(source,79,53,193);Picture(otherSource,43,29,201);string sourceHash=Hash(source);DateTime sourceTime=File.GetLastWriteTimeUtc(source);
   using(var first=new Library(folder))using(var second=new Library(folder)){
    first.EnableReferenceAssets();second.EnableReferenceAssets();var asset=Reference(first,source);var other=Reference(first,otherSource);first.ReadThumbnail(asset);first.ReadThumbnail(other);Broken(first.ThumbnailPath(asset));
    var stale=second.All().Single(a=>a.Id==asset.Id);stale.Name="Unsaved label";stale.Notes="Unsaved note";int baseline=ThumbnailBuildGate.ActivePathCount;Task<BitmapImage>[] callers=null;Task<BitmapImage> independent=null;
    using(var start=new ManualResetEvent(false))using(var entered=new CountdownEvent(6)){
     var held=ThumbnailBuildGate.Enter(first.ThumbnailPath(asset));
     try{
      callers=Enumerable.Range(0,6).Select(n=>Task.Factory.StartNew(()=>{start.WaitOne();entered.Signal();return n%2==0?first.ReadThumbnail(asset):second.ReadThumbnail(stale);},CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default)).ToArray();start.Set();
      if(!entered.Wait(6000))throw new TimeoutException("Concurrent recovery readers did not reach their shared gate");
      independent=Task.Factory.StartNew(()=>first.ReadThumbnail(other),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
      if(!independent.Wait(6000))throw new TimeoutException("Independent recovery path did not complete");
      check(Pixel(independent.GetAwaiter().GetResult(),43,29,201)&&callers.All(task=>!task.IsCompleted),"independent output progresses while six cross-Library recovery callers wait on the same real output gate");
     }finally{held.Dispose();if(callers!=null&&!Task.WaitAll(callers,6000))throw new TimeoutException("Concurrent recovery callers did not drain");if(independent!=null&&!independent.Wait(6000))throw new TimeoutException("Independent recovery task did not drain");}
    }
    var images=callers.Select(task=>task.GetAwaiter().GetResult()).ToArray();
    check(images.All(image=>Pixel(image,79,53,193))&&images.All(image=>object.ReferenceEquals(image,images[0])),"same-output cross-Library callers converge on one repaired frozen decoded result");
    check(stale.Name=="Unsaved label"&&stale.Notes=="Unsaved note"&&second.All().Single(a=>a.Id==asset.Id).Width==79,"concurrent recovery refreshes computed metadata without overwriting another caller's unsaved fields");
    byte[] healthy=File.ReadAllBytes(first.ThumbnailPath(asset));DateTime time=File.GetLastWriteTimeUtc(first.ThumbnailPath(asset));using(var heldSource=new FileStream(source,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(Pixel(second.ReadThumbnail(stale),79,53,193),"after the first repair a stale Library caller can reuse the successful output while its source is locked");
    check(File.ReadAllBytes(first.ThumbnailPath(asset)).SequenceEqual(healthy)&&File.GetLastWriteTimeUtc(first.ThumbnailPath(asset))==time&&ThumbnailBuildGate.ActivePathCount==baseline&&NoTemporaryFiles(first),"concurrent repair does not keep rebuilding a healthy file, leak output gates or retain pending files");
    check(Hash(source)==sourceHash&&File.GetLastWriteTimeUtc(source)==sourceTime,"concurrent repair preserves external source SHA-256 and timestamp");
   }
  }
  static void Safety(string root,Action<bool,string> check){
   string source=Path.Combine(root,"safety-source.png");Picture(source,71,39,211);string sourceHash=Hash(source);DateTime sourceTime=File.GetLastWriteTimeUtc(source);
   using(var library=new Library(Path.Combine(root,"safety-library"))){
    library.EnableReferenceAssets();var real=Reference(library,source);library.ReadThumbnail(real);
    string sentinel=Path.Combine(library.Root,"outside.512.png");Picture(sentinel,31,23,223);string sentinelHash=Hash(sentinel);DateTime sentinelTime=File.GetLastWriteTimeUtc(sentinel);
    foreach(string id in new[]{@"..\..\outside",@"..\outside",@"nested\outside","outside:stream",@"C:\outside",""}){
     var invalid=new Asset{Id=id,Ext=".png",ReferencePath=source};check(Unavailable(()=>library.ReadThumbnail(invalid)),"unsafe thumbnail ID is rejected: "+id);
    }
    check(Hash(sentinel)==sentinelHash&&File.GetLastWriteTimeUtc(sentinel)==sentinelTime&&Hash(source)==sourceHash&&File.GetLastWriteTimeUtc(source)==sourceTime,"rejected traversal/ADS IDs leave out-of-cache sentinel and original SHA/time unchanged");
    string same=library.ThumbnailPath(real);string sameHash=Hash(same);DateTime sameTime=File.GetLastWriteTimeUtc(same);
    library.Db.Exec("UPDATE desk_refs SET path="+Database.Q(same)+" WHERE id="+Database.Q(real.Id.Substring(4)));var pointsAtOutput=library.All().Single(a=>a.Id==real.Id);
    check(Unavailable(()=>library.ReadThumbnail(pointsAtOutput))&&Hash(same)==sameHash&&File.GetLastWriteTimeUtc(same)==sameTime,"real persisted reference pointing at its own derived output is rejected without modifying that original");
    check(ThumbnailBuildGate.ActivePathCount==0&&NoTemporaryFiles(library),"invalid path and self-source exits release their output gates and create no pending files");
   }
   JunctionSafety(root,source,check);check(Hash(source)==sourceHash&&File.GetLastWriteTimeUtc(source)==sourceTime,"reparse-directory denial also leaves its external original SHA/time unchanged");
  }
  static void JunctionSafety(string root,string source,Action<bool,string> check){
   using(var library=new Library(Path.Combine(root,"junction-library"))){
    library.EnableReferenceAssets();var asset=Reference(library,source);library.ReadThumbnail(asset);
    string firstSource=Path.Combine(root,"junction-first-build.png"),refreshSource=Path.Combine(root,"junction-refresh.png");Picture(firstSource,59,37,157);Picture(refreshSource,47,31,167);
    var first=Reference(library,firstSource);var copiedFirst=library.Import(firstSource,"","Redirected first copied asset","","","").Item;var refresh=Reference(library,refreshSource);library.ReadThumbnail(refresh);string firstMetadata=ThumbnailMetadata(library,first),copiedFirstMetadata=ThumbnailMetadata(library,copiedFirst),refreshMetadata=ThumbnailMetadata(library,refresh);
    string firstHash=Hash(firstSource);DateTime firstTime=File.GetLastWriteTimeUtc(firstSource);
    string thumbs=Path.Combine(library.Root,".mori","thumbs"),saved=thumbs+"-original",target=Path.Combine(root,"junction-target");Directory.CreateDirectory(target);string sentinel=Path.Combine(target,asset.Id+Library.ThumbnailSuffix);Broken(sentinel);string hash=Hash(sentinel);DateTime time=File.GetLastWriteTimeUtc(sentinel);Directory.Move(thumbs,saved);
    string refreshSentinel=Path.Combine(target,refresh.Id+Library.ThumbnailSuffix);File.Copy(Path.Combine(saved,refresh.Id+Library.ThumbnailSuffix),refreshSentinel);string refreshHash=Hash(refreshSentinel);DateTime refreshTime=File.GetLastWriteTimeUtc(refreshSentinel);
    try{
     CreateJunction(thumbs,target);
     check((File.GetAttributes(thumbs)&FileAttributes.ReparsePoint)!=0,"synthetic cache-directory junction fixture is an actual reparse point");
     library.BuildThumbnail(first);library.BuildThumbnail(copiedFirst);
     check(!File.Exists(library.ThumbnailPath(first))&&!File.Exists(library.ThumbnailPath(copiedFirst))&&ThumbnailMetadata(library,first)==firstMetadata&&ThumbnailMetadata(library,copiedFirst)==copiedFirstMetadata&&Hash(firstSource)==firstHash&&File.GetLastWriteTimeUtc(firstSource)==firstTime&&Directory.GetFiles(target).Length==2,"public reference/copied-material first-build entry points cannot create redirected cache output or change original/index metadata");
     Picture(refreshSource,67,41,179);File.SetLastWriteTimeUtc(refreshSource,FixtureTime.AddHours(3));string changedHash=Hash(refreshSource);DateTime changedTime=File.GetLastWriteTimeUtc(refreshSource);
     library.BuildThumbnail(refresh);var fallback=library.ReadThumbnail(refresh);
     check(Pixel(fallback,47,31,167)&&Hash(refreshSentinel)==refreshHash&&File.GetLastWriteTimeUtc(refreshSentinel)==refreshTime&&ThumbnailMetadata(library,refresh)==refreshMetadata&&Hash(refreshSource)==changedHash&&File.GetLastWriteTimeUtc(refreshSource)==changedTime&&Directory.GetFiles(target).Length==2,"public reference refresh and asset-aware read keep the old healthy redirected cache read-only without advancing its source stamp or metadata");
     check(Unavailable(()=>library.ReadThumbnail(asset))&&Hash(sentinel)==hash&&File.GetLastWriteTimeUtc(sentinel)==time&&Directory.GetFiles(target).Length==2,"redirected corrupt cache is rejected before repair and never modifies the external sentinel target");
    }finally{
     // Delete only this known link/empty placeholder, never recurse through it.
     if(Directory.Exists(thumbs))Directory.Delete(thumbs);if(Directory.Exists(saved)&&!Directory.Exists(thumbs))Directory.Move(saved,thumbs);
    }
    library.BuildThumbnail(first);
    check(Pixel(library.ReadThumbnail(asset),71,39,211)&&Pixel(library.ReadThumbnail(first),59,37,157)&&Pixel(library.ReadThumbnail(copiedFirst),59,37,157)&&Pixel(library.ReadThumbnail(refresh),67,41,179)&&ThumbnailMetadata(library,refresh)!=refreshMetadata&&ThumbnailBuildGate.ActivePathCount==0&&NoTemporaryFiles(library),"removing the fixture junction restores safe first-builds, reference-version refresh and healthy reads without poisoned failures");
   }
  }
  static void LinkedLibraryReads(string root,bool ancestor,Action<bool,string> check){
   string label=ancestor?"linked-ancestor":"linked-root",target=Path.Combine(root,label+"-target"),link=Path.Combine(root,label+"-alias");Directory.CreateDirectory(target);
   string real=ancestor?Path.Combine(target,"library"):target,alias=ancestor?Path.Combine(link,"library"):link;
   string copiedSource=Path.Combine(root,label+"-copied.png"),referenceSource=Path.Combine(root,label+"-reference.png");Picture(copiedSource,73,43,151);Picture(referenceSource,81,49,163);string copiedId,referenceId;
   using(var plain=new Library(real)){
    plain.EnableReferenceAssets();var copied=plain.Import(copiedSource,"","Linked copied asset","","","").Item;var reference=Reference(plain,referenceSource);copiedId=copied.Id;referenceId=reference.Id;plain.ReadThumbnail(copied);plain.ReadThumbnail(reference);
   }
   CreateJunction(link,target);
   try{
    using(var library=new Library(alias)){
     library.EnableReferenceAssets();var copied=library.All().Single(a=>a.Id==copiedId);var reference=library.All().Single(a=>a.Id==referenceId);
     string copiedThumbnail=library.ThumbnailPath(copied),referenceThumbnail=library.ThumbnailPath(reference),copiedHash=Hash(copiedThumbnail),referenceHash=Hash(referenceThumbnail),copiedMetadata=ThumbnailMetadata(library,copied),referenceMetadata=ThumbnailMetadata(library,reference);DateTime copiedTime=File.GetLastWriteTimeUtc(copiedThumbnail),referenceTime=File.GetLastWriteTimeUtc(referenceThumbnail);byte[] healthyReference=File.ReadAllBytes(referenceThumbnail);
     check((File.GetAttributes(link)&FileAttributes.ReparsePoint)!=0&&Pixel(library.ReadThumbnail(copied),73,43,151)&&Pixel(library.ReadThumbnail(reference),81,49,163)&&Hash(copiedThumbnail)==copiedHash&&File.GetLastWriteTimeUtc(copiedThumbnail)==copiedTime&&Hash(referenceThumbnail)==referenceHash&&File.GetLastWriteTimeUtc(referenceThumbnail)==referenceTime&&ThumbnailMetadata(library,copied)==copiedMetadata&&ThumbnailMetadata(library,reference)==referenceMetadata,"healthy copied/reference caches remain readable without writes through a real "+label+" junction");
     Picture(referenceSource,99,63,191);File.SetLastWriteTimeUtc(referenceSource,FixtureTime.AddHours(4));string changedHash=Hash(referenceSource);DateTime changedTime=File.GetLastWriteTimeUtc(referenceSource);
     library.BuildThumbnail(reference);var fallback=library.ReadThumbnail(reference);
     check(Pixel(fallback,81,49,163)&&Hash(referenceThumbnail)==referenceHash&&File.GetLastWriteTimeUtc(referenceThumbnail)==referenceTime&&ThumbnailMetadata(library,reference)==referenceMetadata&&reference.Width==81&&reference.Height==49&&Hash(referenceSource)==changedHash&&File.GetLastWriteTimeUtc(referenceSource)==changedTime&&NoTemporaryFiles(library),"changed reference under "+label+" retains a read-only healthy cache and its old indexed source version instead of falsely marking it refreshed");
     Broken(referenceThumbnail);ThumbnailImage.DecodedCache.Invalidate(referenceThumbnail);string brokenHash=Hash(referenceThumbnail);DateTime brokenTime=File.GetLastWriteTimeUtc(referenceThumbnail);
     check(Unavailable(()=>library.ReadThumbnail(reference))&&Hash(referenceThumbnail)==brokenHash&&File.GetLastWriteTimeUtc(referenceThumbnail)==brokenTime&&ThumbnailMetadata(library,reference)==referenceMetadata&&Hash(referenceSource)==changedHash&&File.GetLastWriteTimeUtc(referenceSource)==changedTime&&NoTemporaryFiles(library),"corrupt cache under "+label+" is never repaired through redirected ancestors or written back into its source/index");
     // Restore only this synthetic derived PNG so self-source denial is checked
     // with a healthy decoder input, not accidentally proved by corrupt bytes.
     File.WriteAllBytes(referenceThumbnail,healthyReference);File.SetLastWriteTimeUtc(referenceThumbnail,referenceTime);ThumbnailImage.DecodedCache.Invalidate(referenceThumbnail);
     library.Db.Exec("UPDATE desk_refs SET path="+Database.Q(referenceThumbnail)+" WHERE id="+Database.Q(referenceId.Substring(4)));var self=library.All().Single(a=>a.Id==referenceId);
     try{check(Unavailable(()=>library.ReadThumbnail(self))&&WriteDenied(()=>library.BuildThumbnail(self))&&Hash(referenceThumbnail)==referenceHash&&File.GetLastWriteTimeUtc(referenceThumbnail)==referenceTime&&NoTemporaryFiles(library),"healthy self-source remains structurally rejected through both entry points under "+label+" without changing that original");}
     finally{library.Db.Exec("UPDATE desk_refs SET path="+Database.Q(referenceSource)+" WHERE id="+Database.Q(referenceId.Substring(4)));}
    }
   }finally{
    // Remove only this known junction, never recurse into the real library.
    if(Directory.Exists(link))Directory.Delete(link);
   }
   using(var plain=new Library(real)){
    plain.EnableReferenceAssets();var reference=plain.All().Single(a=>a.Id==referenceId);string before=reference.ThumbnailSourceStamp;
     check(Pixel(plain.ReadThumbnail(reference),99,63,191)&&reference.ThumbnailSourceStamp!=before&&ThumbnailBuildGate.ActivePathCount==0&&NoTemporaryFiles(plain),"ordinary path after removing "+label+" still evaluates the live reference version and publishes its new pixels");
   }
  }
  static void UiRecovery(string root,Action<bool,string> check){
   string source=Path.Combine(root,"ui-source.png");Picture(source,89,57,229);string hash=Hash(source);DateTime time=File.GetLastWriteTimeUtc(source);
   using(var library=new Library(Path.Combine(root,"ui-library"))){
    library.EnableReferenceAssets();var asset=Reference(library,source);library.BuildThumbnail(asset);Broken(library.ThumbnailPath(asset));ThumbnailImage.CurrentLibrary=library;
    var host=new Grid{Width=340,Height=240};var image=new ThumbnailImage{Width=220,Height=160,DataContext=asset};int baseline=ThumbnailBuildGate.ActivePathCount;IDisposable held=null,pause=null;
    using(var window=new HwndSource(new HwndSourceParameters("Nook VM thumbnail self-recovery"){Width=340,Height=240,WindowStyle=0})){
     window.RootVisual=host;
     try{
      host.Children.Add(image);Layout(host);Until(()=>image.IsLoaded&&image.PreviewState=="ready","actual UI self-recovery");
      check(Pixel(image.Source as BitmapSource,89,57,229)&&Pixel(DecodedThumbnailCache.DecodeFile(library.ThumbnailPath(asset)),89,57,229),"actual Loaded thumbnail becomes ready from a corrupt persisted PNG with no fixture-assisted deletion");
      host.Children.Clear();Layout(host);Until(()=>!image.IsLoaded,"actual UI unload");Broken(library.ThumbnailPath(asset));ThumbnailImage.RetryFailedThumbnails();held=ThumbnailBuildGate.Enter(library.ThumbnailPath(asset));host.Children.Add(image);Layout(host);Until(()=>image.IsLoaded&&image.PreviewState=="loading"&&AvailableSlots()==2,"UI repair owns its scheduler slot before pause");
      var drain=ThumbnailImage.PauseAndDrainAsync();check(!drain.IsCompleted,"destructive-operation pause waits for the actual in-flight recovery request");held.Dispose();held=null;pause=Drained(drain);
      check(image.Source==null&&image.PreviewState=="idle"&&NoTemporaryFiles(library),"recovery completed during pause cannot publish pixels/status or keep a pending write after drain");
      host.Children.Clear();Layout(host);Until(()=>!image.IsLoaded,"paused UI unload");pause.Dispose();pause=null;host.Children.Add(image);Layout(host);Until(()=>image.PreviewState=="ready","UI recovery after resume");
      check(Pixel(image.Source as BitmapSource,89,57,229)&&Hash(source)==hash&&File.GetLastWriteTimeUtc(source)==time&&ThumbnailBuildGate.ActivePathCount==baseline,"resumed real tile reads the repaired preview while preserving source SHA/time and releasing gates");
     }finally{if(held!=null)held.Dispose();if(pause!=null)pause.Dispose();host.Children.Clear();Pump();try{using(Drained(ThumbnailImage.PauseAndDrainAsync())){}}finally{window.RootVisual=null;}}
    }
   }
   ThumbnailImage.CurrentLibrary=null;
  }
  // A mount-point junction needs no shell, external helper or symlink privilege.
  // Failure to construct it is a test failure, never an unreported coverage skip.
  static void CreateJunction(string link,string target){
   Directory.CreateDirectory(link);string substitute=@"\??\"+Path.GetFullPath(target),print=Path.GetFullPath(target);byte[] substituteBytes=Encoding.Unicode.GetBytes(substitute),printBytes=Encoding.Unicode.GetBytes(print);var buffer=new byte[20+substituteBytes.Length+printBytes.Length];
   using(var stream=new MemoryStream(buffer))using(var writer=new BinaryWriter(stream)){
    writer.Write(0xA0000003u);writer.Write((ushort)(buffer.Length-8));writer.Write((ushort)0);writer.Write((ushort)0);writer.Write((ushort)substituteBytes.Length);writer.Write((ushort)(substituteBytes.Length+2));writer.Write((ushort)printBytes.Length);writer.Write(substituteBytes);writer.Write((ushort)0);writer.Write(printBytes);writer.Write((ushort)0);
   }
   using(var handle=CreateFile(link,0x40000000,0,IntPtr.Zero,3,0x02200000,IntPtr.Zero)){
    if(handle.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot open synthetic junction fixture");uint returned;if(!DeviceIoControl(handle,0x000900A4,buffer,buffer.Length,IntPtr.Zero,0,out returned,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot construct synthetic junction fixture");
   }
  }
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
  [DllImport("kernel32.dll",SetLastError=true)]static extern bool DeviceIoControl(SafeFileHandle handle,uint code,byte[] input,int inputLength,IntPtr output,int outputLength,out uint returned,IntPtr overlapped);
 }
}
