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
   LibraryIntegration(root,check);
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
    string saveSource=Path.Combine(root,"save-retry.png");Image(saveSource,41,27,Color.DarkCyan);string saveId=first.AddReferenceAssets(new[]{saveSource},"").Single();var save=first.All().Single(a=>a.Id==saveId);string savePath=first.ThumbnailPath(save);Directory.CreateDirectory(savePath);first.BuildThumbnail(save);
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
