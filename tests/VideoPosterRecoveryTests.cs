using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mori {
 // Run only in the dedicated VM. The real fixture leg uses the production
 // VideoThumbnail.Render -> Mori.exe --video-thumbnail -> WebView2 path.
 internal static class VideoPosterRecoveryTests {
  const string CountingSha256="4708A0D84BC28FF1EA0A3F995D6B196099D0A0CC3ADF55F92BBD8792317D7704";
  static string Digest(string path){using(var input=File.OpenRead(path))using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(input)).Replace("-","");}
  static byte[] Payload(){
   byte[] pixels=new byte[16*8*4];for(int n=0;n<pixels.Length;n+=4){pixels[n]=77;pixels[n+1]=144;pixels[n+2]=211;pixels[n+3]=255;}
   var bitmap=BitmapSource.Create(16,8,96,96,PixelFormats.Bgra32,null,pixels,16*4);
   using(var buffer=new MemoryStream())using(var writer=new BinaryWriter(buffer)){writer.Write(320);writer.Write(240);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));encoder.Save(buffer);return buffer.ToArray();}
  }
  static bool Rejected(byte[] payload){try{int w,h;VideoThumbnail.DecodeOutput(payload,out w,out h);return false;}catch(InvalidDataException){return true;}}
  static bool HasPicture(BitmapSource source){
   var converted=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);int stride=converted.PixelWidth*4;byte[] pixels=new byte[stride*converted.PixelHeight];converted.CopyPixels(pixels,stride,0);
   var colors=new HashSet<int>();int low=255,high=0;for(int n=0;n<pixels.Length;n+=4){int color=pixels[n]|pixels[n+1]<<8|pixels[n+2]<<16;if(colors.Count<64)colors.Add(color);low=Math.Min(low,Math.Min(pixels[n],Math.Min(pixels[n+1],pixels[n+2])));high=Math.Max(high,Math.Max(pixels[n],Math.Max(pixels[n+1],pixels[n+2])));}
   return colors.Count>10&&high-low>80;
  }
  static void Save(BitmapSource bitmap,string path){using(var stream=File.Create(path)){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));encoder.Save(stream);}}
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);
   using(var input=new MemoryStream(new byte[17]))check(VideoThumbnail.ReadBounded(input,17).Length==17,"video worker reads exact output budget without truncation");
   bool exceeded=false;try{using(var input=new MemoryStream(new byte[18]))VideoThumbnail.ReadBounded(input,17);}catch(InvalidDataException){exceeded=true;}
   check(exceeded,"video worker rejects excess stdout or diagnostic bytes rather than retaining an unbounded stream");
   check(VideoThumbnail.MaximumOutput==3*1024*1024&&VideoThumbnail.MaximumDiagnostic==64*1024&&VideoThumbnail.MaximumMilliseconds==15000&&VideoThumbnail.MaximumPrivateMemory==256L*1024*1024,"video worker keeps original output, time and private-memory limits, with bounded diagnostics");
   int width,height;byte[] valid=Payload();var image=VideoThumbnail.DecodeOutput(valid,out width,out height);
   check(width==320&&height==240&&image.PixelWidth==16&&image.PixelHeight==8&&image.IsFrozen,"video PNG parser preserves native dimensions and a frozen bounded bitmap");
   check(Rejected(null)&&Rejected(new byte[8])&&Rejected(new byte[VideoThumbnail.MaximumOutput+1]),"video parser rejects absent, truncated and over-budget results");
   var bad=(byte[])valid.Clone();bad[8]=0;check(Rejected(bad),"video parser checks PNG signature before WIC decode");
   bad=(byte[])valid.Clone();bad[19]=12;check(Rejected(bad),"video parser requires a real PNG IHDR");
   bad=(byte[])valid.Clone();bad[24]=0;bad[25]=1;bad[26]=0;bad[27]=0;check(Rejected(bad),"oversized PNG allocation is rejected from IHDR before decoding");
   bad=(byte[])valid.Clone();for(int n=24;n<28;n++)bad[n]=0;check(Rejected(bad),"zero-size video posters are rejected");
   bad=(byte[])valid.Clone();for(int n=0;n<4;n++)bad[n]=255;check(Rejected(bad),"invalid native source dimensions are rejected");

   string fixture=Environment.GetEnvironmentVariable("MORI_TEST_WEBM");if(string.IsNullOrWhiteSpace(fixture))fixture=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"audit-fixtures","journey-media","counting.webm");
   bool exact=File.Exists(fixture)&&Digest(fixture)==CountingSha256;
   check(exact,"original failing WPT counting.webm fixture is present with pinned SHA256 (no synthetic or mock replacement)");if(!exact)return;
   check(BrowserVideo.Available,"system WebView2 runtime is available for genuine WEBM decoding");if(!BrowserVideo.Available)return;
   string original=Digest(fixture);VideoThumbnail.ClearFailures();var elapsed=Stopwatch.StartNew();var poster=VideoThumbnail.Render(fixture,out width,out height);elapsed.Stop();
   File.WriteAllText(Path.Combine(root,"webm-worker-diagnostic.txt"),"elapsedMs="+elapsed.ElapsedMilliseconds+Environment.NewLine+"failure="+VideoThumbnail.LastFailure+Environment.NewLine+VideoThumbnail.LastWorkerDiagnostic,Encoding.UTF8);
   check(poster!=null&&width>0&&height>0,"production worker decodes the previously failing WEBM poster / "+VideoThumbnail.LastFailure);if(poster==null)return;
   check(poster.PixelWidth<=512&&poster.PixelHeight<=512&&HasPicture(poster),"WEBM poster contains a decoded varied frame rather than an empty/solid placeholder");
   check((VideoThumbnail.LastWorkerDiagnostic??"").Contains("thumbnail-captured"),"successful worker records metadata/decode/capture stages rather than only process exit");
   Save(poster,Path.Combine(root,"counting-webm-poster.png"));
   using(var library=new Library(Path.Combine(root,"library"))){
    var asset=library.Import(fixture,"","counting-webm-recovery","","","").Item;library.BuildThumbnail(asset);string thumbnail=library.ThumbnailPath(asset);
    check(File.Exists(thumbnail)&&asset.Width==width&&asset.Height==height,"WEBM poster is actually cached by the library with native dimensions");
    if(File.Exists(thumbnail)){DateTime written=File.GetLastWriteTimeUtc(thumbnail);library.BuildThumbnail(asset);check(File.GetLastWriteTimeUtc(thumbnail)==written,"existing WEBM poster is reused without relaunching decoder");}
    check(Digest(library.FullPath(asset))==original,"library poster generation preserves original video bytes");
   }
   string broken=Path.Combine(root,"truncated.webm");File.WriteAllBytes(broken,File.ReadAllBytes(fixture).Take(40).ToArray());VideoThumbnail.ClearFailures();var rejected=VideoThumbnail.Render(broken,out width,out height);
   check(rejected==null&&!string.IsNullOrEmpty(VideoThumbnail.LastFailure),"corrupt WEBM has an actionable decode failure, never a fake successful poster");
   check(Digest(fixture)==original,"real WEBM remains unchanged after successful and failed worker runs");
  }
 }
}
