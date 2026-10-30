using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mori;
using Drawing=System.Drawing;
using Imaging=System.Drawing.Imaging;

namespace MoriDesk {
 internal static class ThumbnailThroughputTests {
  static readonly BindingFlags Hidden=BindingFlags.Static|BindingFlags.NonPublic;
  internal static readonly List<object> Metrics=new List<object>();
  static T Field<T>(string name){return (T)typeof(BookmarkIcons).GetField(name,Hidden).GetValue(null);}
  static int Pending(){lock(Field<object>("sync"))return Field<Dictionary<string,Task<BitmapSource>>>("pending").Count;}
  static bool Until(Func<bool> ready,int timeout){var clock=Stopwatch.StartNew();while(!ready()&&clock.ElapsedMilliseconds<timeout)Thread.Sleep(10);return ready();}
  static int Busy(){int workers,io,maximum,maxIo;ThreadPool.GetAvailableThreads(out workers,out io);ThreadPool.GetMaxThreads(out maximum,out maxIo);return maximum-workers;}
  internal static void Scheduling(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);bool enabled=BookmarkIcons.NetworkEnabled;BookmarkIcons.NetworkEnabled=false;
   var gate=Field<SemaphoreSlim>("workers");var tasks=new List<Task<BitmapSource>>();int held=0;
   try{
   try{
    if(!Until(()=>Pending()==0,10000))throw new Exception("Previous favicon work did not finish");
    gate.Wait();held++;gate.Wait();held++;int before=Busy();
    for(int i=0;i<48;i++)tasks.Add(BookmarkIcons.GetAsync(root,"https://site"+i+".example.com/page"));
    Thread.Sleep(1200);int after=Busy();
    int running=tasks.Count(task=>task.Status==TaskStatus.Running);
    Metrics.Add(new{name="48-icons-awaiting-two-permits",poolBusyBefore=before,poolBusyAfter=after,runningTasks=running,queued=Pending()});
    check(tasks.All(task=>!task.IsCompleted)&&Pending()==48,"queued icons wait for the two available decoder/network slots");
    check(after-before<=2&&running==0,"48 waiting icons do not consume pool worker threads needed by previews or conversions");
    check(ReferenceEquals(tasks[0],BookmarkIcons.GetAsync(root,"https://site0.example.com/other")),"same origin shares its outstanding request");
    var overflow=BookmarkIcons.GetAsync(root,"https://overflow.example.com/");check(overflow.IsCompleted&&overflow.Result==null,"pending favicon work remains bounded at 48");
   }finally{while(held-->0)gate.Release();}
    check(Until(()=>tasks.All(task=>task.IsCompleted)&&Pending()==0,30000),"all queued offline requests complete and their deduplication entries are removed");
    check(tasks.All(task=>task.Status==TaskStatus.RanToCompletion&&task.Result==null)&&gate.CurrentCount==2,"offline failures return placeholders and restore both permits");
    string address="https://cached.example.com/",other=Path.Combine(root,"other");string cache=BookmarkIcons.PathFor(root,address)+".png";
    Directory.CreateDirectory(Path.GetDirectoryName(cache));using(var image=Fixture(31,17,Imaging.PixelFormat.Format32bppArgb))image.Save(cache,Imaging.ImageFormat.Png);
    for(int i=0;i<20;i++){
     var task=BookmarkIcons.GetAsync(root,address);check(Until(()=>task.IsCompleted&&Pending()==0,10000)&&task.Result!=null&&task.Result.IsFrozen,"fast cache completion releases its pending entry / "+i);
    }
    var missing=BookmarkIcons.GetAsync(other,address);check(Until(()=>missing.IsCompleted&&Pending()==0,10000)&&missing.Result==null,"same origin in a different library cannot reuse another library cache");
    File.WriteAllText(cache,"invalid image");var corrupt=BookmarkIcons.GetAsync(root,address);
    check(Until(()=>corrupt.IsCompleted&&Pending()==0,10000)&&corrupt.Result==null&&gate.CurrentCount==2,"corrupt cache returns placeholder without retaining a worker permit");
   }finally{BookmarkIcons.NetworkEnabled=enabled;}
  }
  static BitmapSource PngRoundTrip(Drawing.Bitmap image){using(var stream=new MemoryStream()){image.Save(stream,Imaging.ImageFormat.Png);stream.Position=0;var result=new BitmapImage();result.BeginInit();result.CacheOption=BitmapCacheOption.OnLoad;result.StreamSource=stream;result.EndInit();result.Freeze();return result;}}
  static byte[] Pixels(BitmapSource source){var bgra=source.Format==PixelFormats.Bgra32?source:new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);int stride=bgra.PixelWidth*4;var bytes=new byte[stride*bgra.PixelHeight];bgra.CopyPixels(bytes,stride,0);return bytes;}
  static Drawing.Bitmap Fixture(int width,int height,Imaging.PixelFormat format){
   var image=new Drawing.Bitmap(width,height,format);image.SetResolution(120,144);
   if(format==Imaging.PixelFormat.Format8bppIndexed){
    var palette=image.Palette;for(int i=0;i<palette.Entries.Length;i++)palette.Entries[i]=Drawing.Color.FromArgb(255,i,255-i,i/2);image.Palette=palette;
    var bits=image.LockBits(new Drawing.Rectangle(0,0,width,height),Imaging.ImageLockMode.WriteOnly,format);
    try{var row=new byte[Math.Abs(bits.Stride)];for(int y=0;y<height;y++){for(int x=0;x<width;x++)row[x]=(byte)((x*17+y*11)%256);Marshal.Copy(row,0,IntPtr.Add(bits.Scan0,y*bits.Stride),row.Length);}}finally{image.UnlockBits(bits);}
   }else{
    var bits=image.LockBits(new Drawing.Rectangle(0,0,width,height),Imaging.ImageLockMode.WriteOnly,Imaging.PixelFormat.Format32bppArgb);
    try{var row=new byte[width*4];for(int y=0;y<height;y++){for(int x=0;x<width;x++){row[x*4]=(byte)(x*7+y*3);row[x*4+1]=(byte)(x*13+y*19);row[x*4+2]=(byte)(x*31+y*11);row[x*4+3]=(byte)((x+y)%256);}Marshal.Copy(row,0,IntPtr.Add(bits.Scan0,y*bits.Stride),row.Length);}}finally{image.UnlockBits(bits);}
   }
   return image;
  }
  internal static void Raster(Action<bool,string> check){
   foreach(var format in new[]{Imaging.PixelFormat.Format32bppArgb,Imaging.PixelFormat.Format32bppPArgb,Imaging.PixelFormat.Format24bppRgb,Imaging.PixelFormat.Format32bppRgb,Imaging.PixelFormat.Format8bppIndexed}){
    BitmapSource result;byte[] expected;using(var image=Fixture(31,17,format)){
     var previous=PngRoundTrip(image);expected=Pixels(previous);result=PreviewThumbnail.FromDrawing(image);
     check(Pixels(result).SequenceEqual(expected),"pixel colors and alpha match PNG round-trip / "+format);
     check(result.PixelWidth==31&&result.PixelHeight==17&&Math.Abs(result.DpiX-120)<.1&&Math.Abs(result.DpiY-144)<.1,"dimensions and non-square DPI preserved / "+format);
     check(Pixels(PngRoundTrip(image)).SequenceEqual(expected),"reading leaves source pixels unchanged / "+format);
    }
    check(result.IsFrozen&&Pixels(result).SequenceEqual(expected),"thumbnail is independent after source disposal / "+format);
    check(Task.Run(()=>Pixels(result).SequenceEqual(expected)).GetAwaiter().GetResult(),"frozen thumbnail can be read on another thread / "+format);
   }
   int stride=28,height=5;IntPtr memory=Marshal.AllocHGlobal(stride*height);
   try{using(var image=new Drawing.Bitmap(7,height,-stride,Imaging.PixelFormat.Format32bppArgb,IntPtr.Add(memory,stride*(height-1)))){
    image.SetResolution(96,96);for(int y=0;y<height;y++)for(int x=0;x<7;x++)image.SetPixel(x,y,Drawing.Color.FromArgb(255,x*30,y*40,40));
    check(Pixels(PreviewThumbnail.FromDrawing(image)).SequenceEqual(Pixels(PngRoundTrip(image))),"negative-stride source preserves row order without reading beyond the bitmap");
   }}finally{Marshal.FreeHGlobal(memory);}
   using(var tiny=Fixture(1,1,Imaging.PixelFormat.Format32bppArgb))check(Pixels(PreviewThumbnail.FromDrawing(tiny)).SequenceEqual(Pixels(PngRoundTrip(tiny))),"single transparent pixel retains its complete BGRA value");
  }
  internal static void MeasureRaster(){
   AppDomain.MonitoringIsEnabled=true;
   foreach(int size in new[]{256,512,2048})using(var image=Fixture(size,size,Imaging.PixelFormat.Format32bppArgb)){
    var times=new List<double>();var allocations=new List<long>();long checksum=0;
    for(int i=0;i<4;i++){
     GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();long before=AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;var clock=Stopwatch.StartNew();
     var source=PreviewThumbnail.FromDrawing(image);clock.Stop();allocations.Add(AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize-before);times.Add(clock.Elapsed.TotalMilliseconds);
     checksum+=Pixels(source)[Math.Min(100,size*size*4-1)];
    }
    Metrics.Add(new{name="decoded-bitmap-to-thumbnail",size=size,warmMilliseconds=times.Skip(1).ToArray(),warmManagedBytes=allocations.Skip(1).ToArray(),checksum=checksum});
   }
  }
 }
}
