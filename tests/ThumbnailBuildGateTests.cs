using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mori;

namespace MoriDesk {
 internal static class ThumbnailBuildGateTests {
  static void Image(string path,int width,int height,Color color){using(var bitmap=new Bitmap(width,height)){using(var g=Graphics.FromImage(bitmap))g.Clear(color);bitmap.Save(path,ImageFormat.Png);}}
  internal static void Run(string testRoot,Action<bool,string> check){
   string root=Path.Combine(testRoot,"thumbnail-build-gate");Directory.CreateDirectory(root);
   int baseline=ThumbnailBuildGate.ActivePathCount,builds=0;bool cached=false;
   string same=Path.Combine(root,"same.png"),other=Path.Combine(root,"other.png");
   using(var start=new ManualResetEvent(false))using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))using(var independent=new ManualResetEvent(false)){
    var callers=Enumerable.Range(0,12).Select(index=>Task.Factory.StartNew(delegate{
     start.WaitOne();using(ThumbnailBuildGate.Enter(index%2==0?same:same.ToUpperInvariant())){if(!cached){Interlocked.Increment(ref builds);entered.Set();release.WaitOne();cached=true;}}
    },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default)).ToArray();
    bool first=false,separate=false,held=false;Task unrelated=null;
    try{start.Set();first=entered.WaitOne(5000);held=ThumbnailBuildGate.ActivePathCount==baseline+1;unrelated=Task.Factory.StartNew(delegate{using(ThumbnailBuildGate.Enter(other))independent.Set();},CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);separate=independent.WaitOne(5000);}
    finally{release.Set();Task.WaitAll(callers);if(unrelated!=null)unrelated.Wait();}
    check(first&&held&&builds==1,"same thumbnail path across concurrent callers and casing shares one build");
    check(separate,"unrelated thumbnail paths progress while another source is decoding");
   }
   check(ThumbnailBuildGate.ActivePathCount==baseline,"thumbnail path gates are removed after their last caller");
   bool escaped=false;try{using(ThumbnailBuildGate.Enter(same))throw new IOException("synthetic work failure");}catch(IOException){escaped=true;}
   using(ThumbnailBuildGate.Enter(same))check(escaped&&ThumbnailBuildGate.ActivePathCount==baseline+1,"exception releases path gate and a future request can enter");
   check(ThumbnailBuildGate.ActivePathCount==baseline,"exception path does not leak gate entries");
   TryEnterContention(root,check);
   string versioned=Path.Combine(root,"version.bin");var modified=new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc);File.WriteAllBytes(versioned,new byte[]{1,2,3});File.SetLastWriteTimeUtc(versioned,modified);string oldVersion=ThumbnailBuildGate.SourceStamp(versioned);DateTime now=DateTime.UtcNow;
   ThumbnailBuildGate.RememberDecodeFailure(oldVersion,now);
   check(ThumbnailBuildGate.HasDecodeFailure(oldVersion,now.AddSeconds(1)),"predictable source decode failure is cached briefly");
   check(ThumbnailBuildGate.SourceStamp(versioned.ToUpperInvariant())==oldVersion,"source failure key uses full case-insensitive path");
   File.WriteAllBytes(versioned,new byte[]{1,2,3,4});File.SetLastWriteTimeUtc(versioned,modified);string changedLength=ThumbnailBuildGate.SourceStamp(versioned);
   check(changedLength!=oldVersion&&!ThumbnailBuildGate.HasDecodeFailure(changedLength,now.AddSeconds(1)),"changed source length bypasses negative cache immediately");
   File.SetLastWriteTimeUtc(versioned,modified.AddSeconds(1));string changedTime=ThumbnailBuildGate.SourceStamp(versioned);
   check(changedTime!=changedLength&&!ThumbnailBuildGate.HasDecodeFailure(changedTime,now.AddSeconds(1)),"same-size source edit bypasses negative cache immediately");
   check(!ThumbnailBuildGate.HasDecodeFailure(oldVersion,now+ThumbnailBuildGate.FailureLifetime),"source negative cache expires without waiting in the test");
   check(ThumbnailBuildGate.SourceStamp(Path.Combine(root,"missing.png"))==null,"missing source does not acquire a misleading failure version");
   ThumbnailBuildGate.RememberDecodeFailure(null,now);check(!ThumbnailBuildGate.HasDecodeFailure(null,now),"metadata access failures are never negatively cached");
   string failurePrefix=Path.GetFullPath(Path.Combine(root,"negative-"));for(int i=0;i<=ThumbnailBuildGate.MaximumFailures;i++)ThumbnailBuildGate.RememberDecodeFailure(failurePrefix+i,now.AddMilliseconds(i));
   check(ThumbnailBuildGate.FailureCount<=ThumbnailBuildGate.MaximumFailures&&!ThumbnailBuildGate.HasDecodeFailure(failurePrefix+"0",now.AddSeconds(1))&&ThumbnailBuildGate.HasDecodeFailure(failurePrefix+ThumbnailBuildGate.MaximumFailures,now.AddSeconds(1)),"negative cache is bounded and retains its newest source failures");
   for(int i=0;i<=ThumbnailBuildGate.MaximumFailures;i++)ThumbnailBuildGate.HasDecodeFailure(failurePrefix+i,now.AddMinutes(1));
   long beforeRefresh=ThumbnailBuildGate.FailureVersion;ThumbnailBuildGate.RememberDecodeFailure(oldVersion,now);VideoThumbnail.FailureCache.Remember(oldVersion,now);long videoAttempt=VideoThumbnail.FailureCache.Version;
   SvgRaster.FailureCache.Remember(oldVersion,now);long svgAttempt=SvgRaster.FailureCache.Version;
   ThumbnailImage.RetryFailedThumbnails();
   check(!ThumbnailBuildGate.HasDecodeFailure(oldVersion,now)&&!VideoThumbnail.FailureCache.Has(oldVersion,now)&&ThumbnailBuildGate.FailureCount==0&&VideoThumbnail.FailureCache.Count==0,"explicit retry clears picture and video failure caches without waiting for expiry");
   check(SvgRaster.FailureCache.Count==0&&!SvgRaster.FailureCache.Has(oldVersion,now),"explicit F5 retry also clears independent SVG failure backoff");
   SvgRaster.FailureCache.Remember(oldVersion,now,svgAttempt);
   check(!SvgRaster.FailureCache.Has(oldVersion,now),"SVG decode begun before F5 cannot reinsert stale failure after refresh");
   ThumbnailBuildGate.RememberDecodeFailure(oldVersion,beforeRefresh);VideoThumbnail.FailureCache.Remember(oldVersion,now,videoAttempt);
   check(!ThumbnailBuildGate.HasDecodeFailure(oldVersion,now)&&!VideoThumbnail.FailureCache.Has(oldVersion,now),"decode attempts started before refresh cannot repopulate stale failures after refresh");
   ThumbnailBuildGate.RememberDecodeFailure(oldVersion,ThumbnailBuildGate.FailureVersion);VideoThumbnail.FailureCache.Remember(oldVersion,now,VideoThumbnail.FailureCache.Version);
   check(ThumbnailBuildGate.HasDecodeFailure(oldVersion,now)&&VideoThumbnail.FailureCache.Has(oldVersion,now.AddMinutes(9)),"failed explicit retry is cached again instead of creating an automatic retry loop");
   check(!VideoThumbnail.FailureCache.Has(oldVersion,now.AddMinutes(10)),"video failure expires after its bounded ten-minute backoff");
   ThumbnailImage.RetryFailedThumbnails();
   LibraryIntegration(root,check);
  }
  static void TryEnterContention(string root,Action<bool,string> check){
   string path=Path.Combine(root,"nonblocking.png"),other=Path.Combine(root,"nonblocking-other.png");int baseline=ThumbnailBuildGate.ActivePathCount;Task<bool> probe=null;
   try{
    using(var held=ThumbnailBuildGate.Enter(path)){
     // Monitor is reentrant on its owning thread. Only this separate worker
     // can establish that a contended warm probe does not wait for the writer.
     probe=Task.Factory.StartNew(()=>{bool missed=true;for(int n=0;n<100;n++){using(var lease=ThumbnailBuildGate.TryEnter(n%2==0?path:path.ToUpperInvariant()))missed&=lease==null;}return missed;},CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
     bool finished=probe.Wait(5000);
     check(finished&&probe.Result&&ThumbnailBuildGate.ActivePathCount==baseline+1,"100 contended cross-thread/case-insensitive TryEnter probes miss before the writer releases its lock and add no path entries");
     using(var independent=ThumbnailBuildGate.TryEnter(other))check(independent!=null&&ThumbnailBuildGate.ActivePathCount==baseline+2,"unrelated TryEnter path progresses while a different output remains writer-locked");
     check(ThumbnailBuildGate.ActivePathCount==baseline+1,"disposing an independent TryEnter lease preserves only the existing writer's path gate");
    }
   }finally{if(probe!=null&&!probe.Wait(6000))throw new TimeoutException("Nonblocking thumbnail gate worker did not finish");}
   check(ThumbnailBuildGate.ActivePathCount==baseline,"failed nonblocking gate probes release their user counts after the writer exits");
   using(var lease=ThumbnailBuildGate.TryEnter(path.ToUpperInvariant()))check(lease!=null&&ThumbnailBuildGate.ActivePathCount==baseline+1,"TryEnter obtains the same output immediately after the previous writer releases it");
   check(ThumbnailBuildGate.ActivePathCount==baseline,"successful nonblocking gate lease disposal removes its final path entry");
  }
  static void LibraryIntegration(string root,Action<bool,string> check){
   string library=Path.Combine(root,"library"),source=Path.Combine(root,"sample.png");Image(source,73,51,Color.DarkSlateBlue);byte[] original=File.ReadAllBytes(source);
   using(var first=new Library(library))using(var second=new Library(library)){
    var item=first.Import(source,"","Source label","https://example.org/","source tag","source note").Item;var stale=second.All().Single();stale.Name="Unsaved name";stale.Notes="Unsaved note";first.BuildThumbnail(item);var saved=File.GetLastWriteTimeUtc(first.ThumbnailPath(item));second.BuildThumbnail(stale);
    check(stale.Width==73&&stale.Height==51&&stale.Color==item.Color&&stale.Perceptual==item.Perceptual,"cached material refreshes computed metadata on a stale asset from another Library");
    check(stale.Name=="Unsaved name"&&stale.Notes=="Unsaved note"&&File.GetLastWriteTimeUtc(first.ThumbnailPath(item))==saved,"cache hit preserves unsaved user metadata and never rewrites thumbnail");
    first.EnableReferenceAssets();second.EnableReferenceAssets();string id=first.AddReferenceAssets(new[]{source},"").Single();var live=first.All().Single(a=>a.Id==id);var staleReference=second.All().Single(a=>a.Id==id);first.BuildThumbnail(live);var cachedTime=new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc);File.SetLastWriteTimeUtc(first.ThumbnailPath(live),cachedTime);second.BuildThumbnail(staleReference);
    check(staleReference.Width==73&&staleReference.Height==51&&staleReference.ThumbnailSourceStamp==live.ThumbnailSourceStamp&&File.GetLastWriteTimeUtc(first.ThumbnailPath(live))==cachedTime,"stale shortcut metadata shares existing thumbnail across Library instances without decoding twice");
    Image(source,81,29,Color.CadetBlue);var changed=first.All().Single(a=>a.Id==id);first.BuildThumbnail(changed);second.BuildThumbnail(staleReference);
    check(staleReference.Width==81&&staleReference.Height==29&&staleReference.Bytes==new FileInfo(source).Length&&staleReference.Color==changed.Color,"changed shortcut source refreshes dimensions and version on stale asset");
    string invalid=Path.Combine(root,"broken.png");File.WriteAllBytes(invalid,new byte[]{1,2,3,4,5});string brokenId=first.AddReferenceAssets(new[]{invalid},"").Single();var broken=first.All().Single(a=>a.Id==brokenId);first.BuildThumbnail(broken);string failureStamp=ThumbnailBuildGate.SourceStamp(invalid);
    check(!File.Exists(first.ThumbnailPath(broken))&&ThumbnailBuildGate.HasDecodeFailure(failureStamp),"Library caches decoder failures only after failed thumbnail decode");
    Image(invalid,39,23,Color.DarkOliveGreen);first.BuildThumbnail(broken);
    check(File.Exists(first.ThumbnailPath(broken))&&broken.Width==39&&broken.Height==23,"corrected source retries immediately instead of waiting for negative cache expiry");
    string retrySource=Path.Combine(root,"explicit-retry.png");Image(retrySource,31,19,Color.DarkSeaGreen);byte[] retryOriginal=File.ReadAllBytes(retrySource);File.WriteAllBytes(retrySource,new byte[retryOriginal.Length]);var retryTime=new DateTime(2026,1,2,0,0,0,DateTimeKind.Utc);File.SetLastWriteTimeUtc(retrySource,retryTime);string retryId=first.AddReferenceAssets(new[]{retrySource},"").Single();var retry=first.All().Single(a=>a.Id==retryId);first.BuildThumbnail(retry);string retryStamp=ThumbnailBuildGate.SourceStamp(retrySource);
    File.WriteAllBytes(retrySource,retryOriginal);File.SetLastWriteTimeUtc(retrySource,retryTime);first.BuildThumbnail(retry);
    check(ThumbnailBuildGate.SourceStamp(retrySource)==retryStamp&&!File.Exists(first.ThumbnailPath(retry)),"unchanged source version does not automatically loop failed decoding even if its bytes recover");
    ThumbnailImage.RetryFailedThumbnails();first.BuildThumbnail(retry);
    check(File.Exists(first.ThumbnailPath(retry))&&retry.Width==31&&retry.Height==19,"explicit refresh retries a previously failed identical source version through the real image decoder");
    check(File.ReadAllBytes(retrySource).SequenceEqual(retryOriginal)&&File.GetLastWriteTimeUtc(retrySource)==retryTime,"explicit retry never rewrites the source bytes or source timestamp");
    string saveSource=Path.Combine(root,"save-retry.png");Image(saveSource,41,27,Color.DarkCyan);string saveId=first.AddReferenceAssets(new[]{saveSource},"").Single();var save=first.All().Single(a=>a.Id==saveId);string savePath=first.ThumbnailPath(save);byte[] saveOriginal=File.ReadAllBytes(saveSource);Directory.CreateDirectory(savePath);bool outputDenied=false;
    // Structural output collisions are deliberately rejected before decoding.
    // Catch only that expected IO rejection so the recovery/database legs below
    // still execute; unrelated failures must continue to fail this test group.
    try{first.BuildThumbnail(save);}catch(IOException){outputDenied=true;}
    check(outputDenied&&Directory.Exists(savePath)&&Directory.GetFileSystemEntries(savePath).Length==0&&File.ReadAllBytes(saveSource).SequenceEqual(saveOriginal)&&ThumbnailBuildGate.ActivePathCount==0,"directory collision is explicitly rejected without writing the output, changing the source or leaking its path gate");
    check(!ThumbnailBuildGate.HasDecodeFailure(ThumbnailBuildGate.SourceStamp(saveSource)),"thumbnail output/save failure does not poison source decoding cache");
    Directory.Delete(savePath);first.BuildThumbnail(save);
    check(File.Exists(savePath)&&save.Width==41&&save.Height==27,"save failure can retry immediately after output becomes writable");
    string dbSource=Path.Combine(root,"db-retry.png");Image(dbSource,47,31,Color.DarkGoldenrod);string dbId=first.AddReferenceAssets(new[]{dbSource},"").Single();var dbItem=first.All().Single(a=>a.Id==dbId);first.Db.Exec("CREATE TEMP TRIGGER fail_thumbnail_update BEFORE UPDATE OF width ON desk_refs BEGIN SELECT RAISE(ABORT,'synthetic thumbnail index failure'); END");bool dbFailed=false;
    try{first.BuildThumbnail(dbItem);}catch(Exception){dbFailed=true;}finally{first.Db.Exec("DROP TRIGGER fail_thumbnail_update");}
    check(dbFailed&&!ThumbnailBuildGate.HasDecodeFailure(ThumbnailBuildGate.SourceStamp(dbSource)),"database failure is surfaced and never stored as a decoder failure");
    first.BuildThumbnail(dbItem);check(dbItem.Width==47&&dbItem.Height==31&&first.All().Single(a=>a.Id==dbId).Width==47,"database failure retries successfully while preserving decoded source");
    check(File.ReadAllBytes(first.FullPath(item)).SequenceEqual(original),"thumbnail generation preserves original copied material bytes");
   }
  }
 }
}
