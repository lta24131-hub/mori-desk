using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mori;

namespace MoriDesk {
 internal static class ImageCodecTests {
  static byte[] Photoshop(bool large,int depth){
   using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream)){
    writer.Write(Encoding.ASCII.GetBytes("8BPS"));U16(writer,large?2:1);writer.Write(new byte[6]);U16(writer,3);U32(writer,1);U32(writer,1);U16(writer,depth);U16(writer,3);
    U32(writer,0);U32(writer,0);U32(writer,0);if(large)U32(writer,0);U16(writer,0);
    if(depth==16){U16(writer,0xeeee);U16(writer,0x6666);U16(writer,0x2222);}else writer.Write(new byte[]{238,102,34});return stream.ToArray();
   }
  }
  static void U16(BinaryWriter w,int value){w.Write((byte)(value>>8));w.Write((byte)value);}
  static void U32(BinaryWriter w,uint value){w.Write((byte)(value>>24));w.Write((byte)(value>>16));w.Write((byte)(value>>8));w.Write((byte)value);}
  static byte[] OrientedJpeg(){
   byte[] jpeg;using(var stream=new MemoryStream())using(var bitmap=new Bitmap(8,4)){bitmap.Save(stream,ImageFormat.Jpeg);jpeg=stream.ToArray();}
   // APP1 Exif, little-endian TIFF, one SHORT Orientation=6 (90 degrees).
   byte[] exif={255,225,0,34,69,120,105,102,0,0,73,73,42,0,8,0,0,0,1,0,18,1,3,0,1,0,0,0,6,0,0,0,0,0,0,0};
   var result=new byte[jpeg.Length+exif.Length];Buffer.BlockCopy(jpeg,0,result,0,2);Buffer.BlockCopy(exif,0,result,2,exif.Length);Buffer.BlockCopy(jpeg,2,result,2+exif.Length,jpeg.Length-2);return result;
  }
  static int WorkerExit(string path,string longest){
   var start=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--image-codec \""+path+"\" "+longest){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
   using(var process=Process.Start(start)){
    var stdout=Task.Run(()=>process.StandardOutput.ReadToEnd());var stderr=Task.Run(()=>process.StandardError.ReadToEnd());
    try{if(!process.WaitForExit(15000)){process.Kill();process.WaitForExit(2000);return -1;}stdout.GetAwaiter().GetResult();stderr.GetAwaiter().GetResult();return process.ExitCode;}
    finally{if(!process.HasExited){process.Kill();process.WaitForExit(2000);}}
   }
  }
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);check(ImageCodec.Available,"image enhancement deploys managed/native dependencies and static policy");
   check(ImageCodec.Supports("x.PSB")&&!ImageCodec.Supports("x.svg")&&!ImageCodec.Supports("x.pdf")&&!ImageCodec.Supports("x.ai")&&!ImageCodec.Supports("x.mvg"),"image enhancement keeps PDF, Illustrator, SVG and scripts on their existing routes");
   check(MediaTypes.Pictures.Contains(".psb")&&FileRouter.PictureFilter.Contains("*.psb")&&FileRouter.Kind("x.PSB")=="image","PSB is classified as a picture by all shared routes");
   string png=Path.Combine(root,"unchanged.png");using(var bitmap=new Bitmap(96,48)){bitmap.SetPixel(0,0,System.Drawing.Color.FromArgb(100,30,60,90));bitmap.Save(png,ImageFormat.Png);}byte[] original=File.ReadAllBytes(png);
   using(var preview=PreviewImage.Open(png))check(!preview.UsedSystemDecoder&&preview.Image.Width==96,"ordinary PNG retains existing GDI fast path");
   // Same proven two-frame timed animation used by viewer-lifecycle-probe.
   string gif=Path.Combine(root,"animated.gif");File.WriteAllBytes(gif,new byte[]{71,73,70,56,57,97,1,0,1,0,128,0,0,255,0,0,0,0,255,33,255,11,78,69,84,83,67,65,80,69,50,46,48,3,1,0,0,0,33,249,4,4,10,0,0,0,44,0,0,0,0,1,0,1,0,0,2,2,68,1,0,33,249,4,4,10,0,0,0,44,0,0,0,0,1,0,1,0,0,2,2,76,1,0,59});
   using(var preview=PreviewImage.Open(gif))check(!preview.UsedSystemDecoder&&preview.Image.GetFrameCount(FrameDimension.Time)==2,"animated GIF retains both frames and original decoder");
   check(!AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name.StartsWith("Magick.NET",StringComparison.Ordinal)),"normal picture use does not load Magick.NET into main process");
   int width,height;var enhanced=ImageCodec.Render(png,32,CancellationToken.None,out width,out height);check(width==96&&height==48&&enhanced.PixelWidth==32&&enhanced.PixelHeight==16&&enhanced.IsFrozen,"isolated image worker returns original dimensions and a bounded detached PNG");
   using(var exclusive=new FileStream(png,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(exclusive.Length==original.Length,"completed enhancement closes source file handles");
   string oriented=Path.Combine(root,"oriented.jpg");byte[] orientedBytes=OrientedJpeg();File.WriteAllBytes(oriented,orientedBytes);var upright=ImageCodec.Render(oriented,64,out width,out height);check(width==4&&height==8&&upright.PixelWidth==4&&upright.PixelHeight==8,"enhanced EXIF orientation reports dimensions of the actually displayed image");check(File.ReadAllBytes(oriented).SequenceEqual(orientedBytes),"orientation correction does not rewrite original EXIF or pixels");
   foreach(string format in new[]{"webp","avif","heic"}){
    byte[] fixture=format=="webp"?ImageCodecFixtures.WebP():format=="avif"?ImageCodecFixtures.Avif():ImageCodecFixtures.Heic();string source=Path.Combine(root,"real-format."+format);File.WriteAllBytes(source,fixture);
    var raster=ImageCodec.Render(source,64,out width,out height);int expectedWidth=format=="heic"?512:8,expectedHeight=format=="heic"?512:4;
    check(width==expectedWidth&&height==expectedHeight&&raster.PixelWidth<=64&&raster.PixelHeight<=64&&raster.IsFrozen,"actual "+format.ToUpperInvariant()+" native decoder works with all external delegates disabled");
    var thumb=PreviewThumbnail.Read(source,64,out width,out height);check(thumb.IsFrozen&&thumb.PixelWidth>0&&width==expectedWidth,"shared gallery path supports actual "+format.ToUpperInvariant()+" fixture");
    using(var preview=PreviewImage.Open(source))check(preview.Image.Width>0&&preview.Image.Height>0,"shared full preview opens actual "+format.ToUpperInvariant()+" fixture");
    check(File.ReadAllBytes(source).SequenceEqual(fixture),"actual "+format.ToUpperInvariant()+" source remains byte-for-byte unchanged");
   }
   string psd=Path.Combine(root,"sixteen-bit.psd"),psb=Path.Combine(root,"large-document.psb");byte[] psdBytes=Photoshop(false,16),psbBytes=Photoshop(true,8);File.WriteAllBytes(psd,psdBytes);File.WriteAllBytes(psb,psbBytes);
   foreach(string path in new[]{psd,psb}){
    using(var preview=PreviewImage.Open(path)){
     check(preview.UsedSystemDecoder&&preview.Image.Width==1&&preview.Image.Height==1,"saved composite variant opens via image enhancement: "+Path.GetExtension(path));
     var pixel=((Bitmap)preview.Image).GetPixel(0,0);check(Math.Abs(pixel.R-238)<3&&Math.Abs(pixel.G-102)<3&&Math.Abs(pixel.B-34)<3,"enhanced saved Photoshop composite preserves its displayed RGB channels: "+Path.GetExtension(path));
    }
    var thumb=PreviewThumbnail.Read(path,64,out width,out height);check(width==1&&height==1&&thumb.PixelWidth==1&&thumb.IsFrozen,"enhanced Photoshop variants share bounded gallery thumbnail: "+Path.GetExtension(path));
    using(var exclusive=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(exclusive.Length>0,"enhanced Photoshop variant does not retain original handle: "+Path.GetExtension(path));
   }
   check(WorkerExit("--security-self-test","1")==0,"actual Magick security policy denies PDF, PS, SVG, MSL, MVG and HTTPS before decoding and rejects a second image frame");
   string disguised=Path.Combine(root,"not-a-picture.png");File.WriteAllText(disguised,"<svg xmlns='http://www.w3.org/2000/svg'><image href='https://example.org/a.png'/></svg>");check(WorkerExit(disguised,"64")==2,"raster worker rejects disguised SVG before reaching native decoder");
   File.WriteAllText(disguised,"%PDF-1.7\nthis is not a raster");check(WorkerExit(disguised,"64")==2,"raster worker rejects disguised PDF without delegates");
   check(WorkerExit(png,"0")==2&&WorkerExit(png,"2049")==2,"worker CLI rejects output dimensions outside bounded contract");
   bool cancelled=false;try{ImageCodec.Render(png,32,new CancellationToken(true),out width,out height);}catch(OperationCanceledException){cancelled=true;}check(cancelled,"pre-cancelled enhancement never launches a worker");
   var slots=(SemaphoreSlim)typeof(ImageCodec).GetField("Workers",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);slots.Wait();
   try{using(var cancel=new CancellationTokenSource()){
    var task=Task.Run(delegate{int w,h;return ImageCodec.Render(png,32,cancel.Token,out w,out h);});check(!task.Wait(80),"native enhancements are serialized instead of multiplying memory");cancel.Cancel();cancelled=false;try{task.GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}check(cancelled,"closing a queued enhanced preview cancels without waiting for another worker");
   }}finally{slots.Release();}
   cancelled=false;using(var cancel=new CancellationTokenSource()){
    var task=Task.Run(delegate{int w,h;return ImageCodec.Render(png,2048,cancel.Token,out w,out h);});SpinWait.SpinUntil(()=>ImageCodec.ActiveWorkerId>0||task.IsCompleted,3000);int workerId=ImageCodec.ActiveWorkerId;check(workerId>0&&!task.IsCompleted,"running cancellation fixture observes a real native worker, not just a queued task");var stopping=Stopwatch.StartNew();cancel.Cancel();check(stopping.ElapsedMilliseconds<100,"UI cancellation never waits for native process termination");try{task.GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}
    check(cancelled,"cancelling a started enhanced preview terminates the worker promptly");
    bool alive=false;if(workerId>0)try{using(var worker=Process.GetProcessById(workerId))alive=!worker.HasExited;}catch(ArgumentException){}
    check(!alive&&ImageCodec.ActiveWorkerId==0&&stopping.ElapsedMilliseconds<4000,"cancelled native worker is reaped before returning its queue slot");
   }
   check(slots.CurrentCount==1,"cancelled and completed enhanced previews return their concurrency slot");
   var recovered=ImageCodec.Render(png,16,out width,out height);check(recovered.PixelWidth==16,"worker queue remains usable after cancellation");
   check(File.ReadAllBytes(png).SequenceEqual(original)&&File.ReadAllBytes(psd).SequenceEqual(psdBytes)&&File.ReadAllBytes(psb).SequenceEqual(psbBytes),"all enhanced source files remain byte-for-byte unchanged");
   check(!AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name.StartsWith("Magick.NET",StringComparison.Ordinal)),"enhanced previews also keep the native wrapper outside the gallery process");
  }
 }
}
