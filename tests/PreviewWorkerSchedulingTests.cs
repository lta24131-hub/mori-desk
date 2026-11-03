using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mori;

namespace MoriDesk {
 // Exercise the actual child processes while all pool workers are occupied by
 // the decoder and unrelated gallery work. A private watchdog restores the
 // process-wide pool even against the old blocked-pipe implementation.
 internal static class PreviewWorkerSchedulingTests {
  static T BusyPool<T>(Func<Task<T>> start){
   int oldMin,oldMinIo,oldMax,oldMaxIo;ThreadPool.GetMinThreads(out oldMin,out oldMinIo);ThreadPool.GetMaxThreads(out oldMax,out oldMaxIo);
   int workers=Math.Max(2,Environment.ProcessorCount);var release=new ManualResetEventSlim();var done=new ManualResetEventSlim();var entered=new CountdownEvent(workers-1);var exited=new CountdownEvent(workers-1);
   Thread watchdog=null;int watchdogFired=0;bool queued=false;
   try{
    if(!ThreadPool.SetMinThreads(workers,oldMinIo)||!ThreadPool.SetMaxThreads(workers,oldMaxIo))throw new Exception("Cannot constrain fixture thread pool");
    for(int i=0;i<workers-1;i++)ThreadPool.QueueUserWorkItem(_=>{entered.Signal();try{release.Wait();}finally{exited.Signal();}});queued=true;
    if(!entered.Wait(10000))throw new TimeoutException("Fixture workers did not start");
    watchdog=new Thread(()=>{if(!done.Wait(30000)){Interlocked.Exchange(ref watchdogFired,1);release.Set();}}){IsBackground=true};watchdog.Start();
    T result=start().GetAwaiter().GetResult();
    if(Volatile.Read(ref watchdogFired)!=0)throw new TimeoutException("Preview waited for unrelated gallery work to finish");
    return result;
   }finally{
    release.Set();done.Set();if(watchdog!=null)watchdog.Join(5000);
    ThreadPool.SetMaxThreads(oldMax,oldMaxIo);ThreadPool.SetMinThreads(oldMin,oldMinIo);
    if(queued&&!exited.Wait(10000))throw new TimeoutException("Fixture workers did not exit");
    release.Dispose();done.Dispose();entered.Dispose();exited.Dispose();
   }
  }
  internal static void Svg(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);string file=Path.Combine(root,"busy.svg");
   File.WriteAllText(file,"<svg xmlns='http://www.w3.org/2000/svg' width='512' height='512'><defs><linearGradient id='g' x2='1' y2='1'><stop stop-color='#477bff'/><stop offset='1' stop-color='#32cda6'/></linearGradient></defs><rect width='512' height='512' fill='url(#g)'/><circle cx='220' cy='180' r='75' fill='white'/></svg>");byte[] before=File.ReadAllBytes(file);
   int width,height;var baseline=SvgRaster.Render(file,512,out width,out height);check(baseline.PixelWidth==512&&width==512,"SVG fixture renders before background contention");
   var rendered=BusyPool(()=>Task.Run(()=>{int w,h;return SvgRaster.Render(file,512,out w,out h);}));
   check(rendered.IsFrozen&&rendered.PixelWidth==512&&rendered.PixelHeight==512,"SVG result drains while unrelated pool work is blocked");
   check(before.SequenceEqual(File.ReadAllBytes(file)),"busy SVG rendering preserves source bytes");
  }
  internal static void EnhancedImage(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);string file=Path.Combine(root,"busy.png");
   using(var image=new System.Drawing.Bitmap(256,256)){uint color=19;for(int y=0;y<256;y++)for(int x=0;x<256;x++){color=unchecked(color*1664525+1013904223);image.SetPixel(x,y,System.Drawing.Color.FromArgb(unchecked((int)(color|0xff000000))));}image.Save(file,System.Drawing.Imaging.ImageFormat.Png);}
   byte[] before=File.ReadAllBytes(file);int width,height;check(ImageCodec.Render(file,256,out width,out height).PixelWidth==256,"enhanced image fixture renders before background contention");
   var rendered=BusyPool(()=>Task.Run(()=>{int w,h;return ImageCodec.Render(file,256,out w,out h);}));
   check(rendered.IsFrozen&&rendered.PixelWidth==256,"enhanced image drains both output pipes while gallery work is blocked");
   check(before.SequenceEqual(File.ReadAllBytes(file)),"busy enhanced decoding preserves source bytes");
  }
  internal static void PdfPixels(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);string file=Path.Combine(root,"busy.pdf");File.WriteAllBytes(file,PdfTextTests.Fixture());byte[] before=File.ReadAllBytes(file);
   var rendered=BusyPool(()=>Task.Run(()=>{using(var session=PdfRenderConnection.Open(file,-1,CancellationToken.None)){check(session.Sizes.Length==2,"PDF handshake completes while gallery work is blocked");return session.Render(0,512,CancellationToken.None);}}));
   check(rendered.Bitmap.IsFrozen&&rendered.Bitmap.PixelWidth>0&&rendered.PageCount==2,"PDF pixels arrive while unrelated pool work remains blocked");
   check(before.SequenceEqual(File.ReadAllBytes(file)),"busy PDF rendering preserves source bytes");
  }
  internal static void PdfText(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);string file=Path.Combine(root,"busy-text.pdf");File.WriteAllBytes(file,PdfTextTests.Fixture());byte[] before=File.ReadAllBytes(file);
   using(var session=new PdfTextSession(file)){
    var page=BusyPool(()=>session.ReadAsync(0,CancellationToken.None));
    check(page.Words.Any(w=>w.Text=="Hello")&&page.Words.Any(w=>w.Text=="world"),"PDF text drains while its decode task occupies the last pool worker");
   }
   check(before.SequenceEqual(File.ReadAllBytes(file)),"busy PDF text extraction preserves source bytes");
  }
  internal static void Video(string root,Action<bool,string> check){
   RunVideo(root,".webm",WebMediaFixtures.WebM(),check);
  }
  internal static void VideoMp4(string root,Action<bool,string> check){RunVideo(root,".mp4",WebMediaFixtures.Mp4(),check);}
  static void RunVideo(string root,string extension,byte[] before,Action<bool,string> check){
   Directory.CreateDirectory(root);string file=Path.Combine(root,"busy"+extension);File.WriteAllBytes(file,before);
   int width,height;var baseline=VideoThumbnail.Render(file,out width,out height);
   File.WriteAllText(Path.Combine(root,"video-diagnostic.txt"),"failure="+VideoThumbnail.LastFailure+Environment.NewLine+VideoThumbnail.LastWorkerDiagnostic);
   check(baseline!=null,"real "+extension+" thumbnail renders before background contention / "+VideoThumbnail.LastFailure+" / "+VideoThumbnail.LastWorkerDiagnostic);
   var rendered=BusyPool(()=>Task.Run(()=>{int w,h;return VideoThumbnail.Render(file,out w,out h);}));
   check(rendered!=null&&rendered.IsFrozen&&rendered.PixelWidth>0,"video thumbnail drains both pipes while gallery work is blocked: "+VideoThumbnail.LastFailure);
   check(before.SequenceEqual(File.ReadAllBytes(file)),"busy video thumbnail preserves source bytes");
  }
  internal static void Run(string root,Action<bool,string> check){Svg(Path.Combine(root,"svg"),check);EnhancedImage(Path.Combine(root,"image"),check);PdfPixels(Path.Combine(root,"pdf"),check);PdfText(Path.Combine(root,"text"),check);Video(Path.Combine(root,"video"),check);}
 }
}
