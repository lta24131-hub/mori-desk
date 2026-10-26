using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mori;

namespace MoriDesk {
 // ScenarioRunner checks QEMU/KVM/Bochs before invoking this group. These
 // tests use real files, WIC, the real enhanced worker and its actual gate.
 internal static class ImageCodecPreflightTests {
  sealed class Rendered {internal BitmapSource Image;internal int Width,Height;}
  static readonly DateTime FixtureTime=new DateTime(2026,1,12,0,0,0,DateTimeKind.Utc);
  static string Hash(string path){using(var file=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(file));}
  static void Picture(string path,bool bmp=false){
   var pixels=new byte[23*13*4];for(int n=0;n<pixels.Length;n+=4){pixels[n]=43;pixels[n+1]=107;pixels[n+2]=191;pixels[n+3]=255;}
   var source=BitmapSource.Create(23,13,96,96,PixelFormats.Bgra32,null,pixels,23*4);BitmapEncoder encoder=bmp?(BitmapEncoder)new BmpBitmapEncoder():new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(source));
   using(var output=File.Create(path))encoder.Save(output);File.SetLastWriteTimeUtc(path,FixtureTime);
  }
  static bool RealPixels(BitmapSource image){
   if(image==null||!image.IsFrozen||image.PixelWidth!=23||image.PixelHeight!=13)return false;
   var pixel=new byte[4];new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0).CopyPixels(new Int32Rect(0,0,1,1),pixel,4,0);return pixel[0]==43&&pixel[1]==107&&pixel[2]==191&&pixel[3]==255;
  }
  static Rendered Render(string path,CancellationToken cancellation){int width,height;var image=ImageCodec.Render(path,32,cancellation,out width,out height);return new Rendered{Image=image,Width=width,Height=height};}
  static Exception Error(string path,CancellationToken cancellation){try{Render(path,cancellation);return null;}catch(Exception error){return error;}}
  static bool Wait(Task task,int milliseconds){try{return task.Wait(milliseconds);}catch(AggregateException){return task.IsCompleted;}}
  static void Finish(Task task){if(!Wait(task,18000))throw new TimeoutException("Image codec preflight task did not drain within the existing worker and cleanup budgets");if(task.IsFaulted){var observed=task.Exception;}}
  static void Hold(SemaphoreSlim slots){if(!slots.Wait(6000))throw new TimeoutException("Previous enhanced worker did not release its slot before the preflight test");}
  static void Unlocked(string path,Action<bool,string> check,string label){using(var file=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(file.Length>0,label);}
  internal static void Run(string root,Action<bool,string> check){
   if(!Path.GetFullPath(root).StartsWith(@"C:\NookLab\Audit\",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Image codec preflight tests require the isolated VM fixture root.");
   root=Path.Combine(root,"image-codec-preflight");Directory.CreateDirectory(root);
   check(ImageCodec.Available,"enhanced preflight tests use the deployed real codec and policy rather than an unavailable-component shortcut");
   var slots=(SemaphoreSlim)typeof(ImageCodec).GetField("Workers",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
   BadDoesNotQueue(root,slots,check);ValidStillQueues(root,slots,check);WicStillSniffs(root,slots,check);TransientFailures(root,check);
   check(slots.CurrentCount==1&&ImageCodec.ActiveWorkerId==0,"preflight, enhanced decode and transient retries release the real worker slot and worker process");
   check(!AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name.StartsWith("Magick.NET",StringComparison.Ordinal)),"managed PNG preflight never loads the Magick wrapper into the gallery process");
  }
  static void BadDoesNotQueue(string root,SemaphoreSlim slots,Action<bool,string> check){
   byte[][] invalid={new byte[]{1,2,3,4,5},new byte[]{137,80,78,71,13,10,26},new byte[]{137,80,78,71,13,10,26,0,0,0,0,0}};
   for(int n=0;n<invalid.Length;n++){
    string path=Path.Combine(root,"bad-"+n+".png");File.WriteAllBytes(path,invalid[n]);File.SetLastWriteTimeUtc(path,FixtureTime);string hash=Hash(path);Task<Exception> task=null;
    Hold(slots);try{using(var cancellation=new CancellationTokenSource())using(var entered=new ManualResetEvent(false)){
     try{
      task=Task.Factory.StartNew(()=>{entered.Set();return Error(path,cancellation.Token);},CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
      if(!entered.WaitOne(3000))throw new TimeoutException("Invalid PNG preflight reader did not start");
      bool completed=Wait(task,3000);
      check(completed&&task.GetAwaiter().GetResult() is NotSupportedException&&ImageCodec.ActiveWorkerId==0,"invalid PNG "+n+" fails with the existing decode error while the real worker slot is still held");
     }finally{cancellation.Cancel();if(task!=null)Finish(task);}
    }}finally{slots.Release();}
    check(Hash(path)==hash&&File.GetLastWriteTimeUtc(path)==FixtureTime,"invalid PNG "+n+" preflight leaves source bytes and timestamp untouched");Unlocked(path,check,"invalid PNG "+n+" preflight releases its source handle");
   }
  }
  static void ValidStillQueues(string root,SemaphoreSlim slots,Action<bool,string> check){
   string path=Path.Combine(root,"valid.PNG");Picture(path);string hash=Hash(path);Task<Rendered> task=null;
   using(var cancellation=new CancellationTokenSource())using(var entered=new ManualResetEvent(false)){
    try{
     Hold(slots);try{
      task=Task.Factory.StartNew(()=>{entered.Set();return Render(path,cancellation.Token);},CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
      if(!entered.WaitOne(3000))throw new TimeoutException("Valid PNG enhanced reader did not start");
      check(!Wait(task,150)&&ImageCodec.ActiveWorkerId==0,"valid case-insensitive PNG is not rejected or launched while the real worker slot is held");
     }finally{slots.Release();}
     Finish(task);var rendered=task.GetAwaiter().GetResult();check(RealPixels(rendered.Image)&&rendered.Width==23&&rendered.Height==13,"valid PNG proceeds through the actual enhanced worker after its slot is released");
    }
    finally{cancellation.Cancel();if(task!=null)Finish(task);}
   }
   check(Hash(path)==hash&&File.GetLastWriteTimeUtc(path)==FixtureTime,"successful enhanced PNG preflight and decode preserve source bytes and timestamp");Unlocked(path,check,"successful enhanced PNG decode releases the source handle");
  }
  static void WicStillSniffs(string root,SemaphoreSlim slots,Action<bool,string> check){
   string path=Path.Combine(root,"real-bmp-with-png-extension.png");Picture(path,true);string hash=Hash(path);Task<Rendered> task=null;
   Hold(slots);try{
    task=Task.Factory.StartNew(delegate{int width,height;return new Rendered{Image=PreviewThumbnail.Read(path,32,out width,out height),Width=width,Height=height};},CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
    bool completed=Wait(task,3000);
    check(completed&&RealPixels(task.GetAwaiter().GetResult().Image)&&task.Result.Width==23&&task.Result.Height==13&&ImageCodec.ActiveWorkerId==0,"real BMP with PNG extension still decodes via WIC without entering the held enhanced-worker gate");
   }finally{slots.Release();if(task!=null)Finish(task);}
   check(Hash(path)==hash&&File.GetLastWriteTimeUtc(path)==FixtureTime,"WIC content sniffing keeps the mismatched-extension original unchanged");Unlocked(path,check,"WIC mismatch preview releases its source handle");
   using(var library=new Library(Path.Combine(root,"mismatched-reference-library"))){
    library.EnableReferenceAssets();string id=library.AddReferenceAssets(new[]{path},"").Single();var asset=library.All().Single(a=>a.Id==id);
    try{check(RealPixels(library.ReadThumbnail(asset))&&asset.IsReference&&asset.ReferencePath==path&&asset.Width==23&&asset.Height==13&&Directory.GetFiles(Path.Combine(library.Root,"原图"),"*",SearchOption.AllDirectories).Length==0,"real local shortcut also preserves WIC content sniffing without copying or rejecting its mismatched-extension source");}
    finally{ThumbnailImage.DecodedCache.Invalidate(library.ThumbnailPath(asset));}
   }
   check(Hash(path)==hash&&File.GetLastWriteTimeUtc(path)==FixtureTime,"local shortcut preview also preserves the source bytes and timestamp");
  }
  static void TransientFailures(string root,Action<bool,string> check){
   string missing=Path.Combine(root,"temporarily-missing.png");check(Error(missing,CancellationToken.None) is FileNotFoundException,"missing PNG reports a real missing source rather than queueing an enhanced worker");Picture(missing);string missingHash=Hash(missing);
   var returned=Render(missing,CancellationToken.None);check(RealPixels(returned.Image)&&returned.Width==23&&returned.Height==13,"previously missing PNG immediately decodes after returning without clearing failure caches");
   check(Hash(missing)==missingHash&&File.GetLastWriteTimeUtc(missing)==FixtureTime,"missing-source retry leaves the returned source bytes and timestamp untouched");Unlocked(missing,check,"missing-source retry releases its source handle");
   string locked=Path.Combine(root,"temporarily-locked.png");Picture(locked);string lockedHash=Hash(locked);
   using(var hold=new FileStream(locked,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(Error(locked,CancellationToken.None) is IOException,"exclusive PNG source reports a transient sharing failure during managed preflight");
   var released=Render(locked,CancellationToken.None);check(RealPixels(released.Image)&&released.Width==23&&released.Height==13,"released PNG sharing lock permits immediate enhanced retry without a ten-minute poisoned failure entry");
   check(Hash(locked)==lockedHash&&File.GetLastWriteTimeUtc(locked)==FixtureTime,"sharing failure and retry preserve the original bytes and timestamp");Unlocked(locked,check,"sharing-failure retry releases the source handle");
  }
 }
}
