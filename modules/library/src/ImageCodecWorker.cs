using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using ImageMagick;

namespace Mori {
 // This type is called only in the isolated --image-codec process. It never
 // passes a filename, URL or pseudo-protocol to ImageMagick and never writes
 // an original, cache, layer export or delegate file.
 internal static class ImageCodecWorker {
  internal static int Run(string path,int longest){
   Configure();
   if(path=="--security-self-test")return SecuritySelfTest()?0:3;
   if(!ImageCodec.Supports(path)||path.IndexOf("://",StringComparison.Ordinal)>=0)return 2;
   using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){
    if(stream.Length<4||stream.Length>ImageCodec.MaximumInput)return 2;
    MagickFormat format=FormatFor(path);if(!ValidMagic(stream,format))return 2;
    var settings=new MagickReadSettings{Format=format,FrameIndex=0,FrameCount=1};
    // The enum overload enumerates every registered format (including denied
    // stealth coders such as DIB). Fixed define strings keep configuration
    // restricted to our explicitly selected saved-composite raster decoder.
    settings.SetDefine("psd:skip-layers","true");settings.SetDefine("psb:skip-layers","true");
    using(var image=new MagickImage()){
     // Metadata preflight avoids inflating a huge saved composite. Explicit
     // raster format and the allowlist policy both remain active while pinging.
     Read(image,stream,settings,true);int width=checked((int)image.Width),height=checked((int)image.Height);ImageCodec.ValidateDimensions(width,height);
     stream.Position=0;Read(image,stream,settings,false);ImageCodec.ValidateDimensions(checked((int)image.Width),checked((int)image.Height));
     if(image.HasProfile("icc")||image.HasProfile("icm"))image.TransformColorSpace(ColorProfiles.SRGB);else image.ColorSpace=ColorSpace.sRGB;
     image.AutoOrient();width=checked((int)image.Width);height=checked((int)image.Height);double ratio=Math.Min(1,longest/(double)Math.Max(image.Width,image.Height));
     if(ratio<1)image.Resize((uint)Math.Max(1,(int)Math.Floor(image.Width*ratio)),(uint)Math.Max(1,(int)Math.Floor(image.Height*ratio)));
     image.Strip();
     using(var standard=Console.OpenStandardOutput())using(var output=new BoundedOutput(standard,ImageCodec.MaximumOutput))using(var writer=new BinaryWriter(output)){
      writer.Write(width);writer.Write(height);writer.Flush();image.Write(output,MagickFormat.Png);
     }
    }
   }return 0;
  }
  static void Configure(){
   // Initialize(path) verifies all static configuration files are present and
   // sets the search path before native initialization. No temp files created.
   MagickNET.Initialize(ImageCodec.ConfigurationPath);
   ResourceLimits.Thread=1;ResourceLimits.Memory=192UL*1024*1024;ResourceLimits.Disk=0;ResourceLimits.MaxMemoryRequest=128UL*1024*1024;ResourceLimits.MaxProfileSize=4UL*1024*1024;
   // ImageMagick's ListLength resource is exclusive (length < limit): two
   // permits exactly one saved composite, while one rejects every raster.
   ResourceLimits.Area=(ulong)ImageCodec.MaximumSourcePixels;ResourceLimits.Width=ImageCodec.MaximumSourceDimension;ResourceLimits.Height=ImageCodec.MaximumSourceDimension;ResourceLimits.ListLength=2;ResourceLimits.Time=10;
  }
  static MagickFormat FormatFor(string path){switch(Path.GetExtension(path).ToLowerInvariant()){
   case ".jpg":case ".jpeg":return MagickFormat.Jpeg;case ".png":return MagickFormat.Png;case ".gif":return MagickFormat.Gif;case ".bmp":return MagickFormat.Bmp;case ".tif":case ".tiff":return MagickFormat.Tiff;case ".ico":return MagickFormat.Ico;
   case ".webp":return MagickFormat.WebP;case ".avif":return MagickFormat.Avif;case ".heic":return MagickFormat.Heic;case ".heif":return MagickFormat.Heif;case ".psd":return MagickFormat.Psd;case ".psb":return MagickFormat.Psb;
   case ".dng":return MagickFormat.Dng;case ".cr2":return MagickFormat.Cr2;case ".cr3":return MagickFormat.Cr3;case ".nef":return MagickFormat.Nef;case ".arw":return MagickFormat.Arw;case ".orf":return MagickFormat.Orf;case ".rw2":return MagickFormat.Rw2;case ".raf":return MagickFormat.Raf;
   default:throw new NotSupportedException("不是允许的光栅图片格式。");
  }}
  internal static bool ValidMagic(Stream stream,MagickFormat format){
   byte[] h=new byte[64];int n=stream.Read(h,0,h.Length);stream.Position=0;
   if(n<4)return false;
   switch(format){
    case MagickFormat.Jpeg:return n>=3&&h[0]==255&&h[1]==216&&h[2]==255;
    case MagickFormat.Png:return n>=8&&h[0]==137&&Ascii(h,1,3)=="PNG"&&h[4]==13&&h[5]==10&&h[6]==26&&h[7]==10;
    case MagickFormat.Gif:return n>=6&&(Ascii(h,0,6)=="GIF87a"||Ascii(h,0,6)=="GIF89a");
    case MagickFormat.Bmp:return n>=14&&h[0]=='B'&&h[1]=='M';
    case MagickFormat.Tiff:case MagickFormat.Dng:case MagickFormat.Nef:case MagickFormat.Arw:return TiffMagic(h);
    case MagickFormat.Cr2:return n>=12&&TiffMagic(h)&&h[8]=='C'&&h[9]=='R';
    case MagickFormat.Orf:return (h[0]=='I'&&h[1]=='I'&&((h[2]=='R'&&h[3]=='O')||(h[2]=='O'&&h[3]=='R')))||(h[0]=='M'&&h[1]=='M'&&h[2]=='O'&&h[3]=='R');
    case MagickFormat.Rw2:return h[0]=='I'&&h[1]=='I'&&h[2]==85&&h[3]==0;
    case MagickFormat.Raf:return n>=16&&Ascii(h,0,16)=="FUJIFILMCCD-RAW ";
    case MagickFormat.Ico:return h[0]==0&&h[1]==0&&h[2]==1&&h[3]==0;
    case MagickFormat.WebP:return n>=12&&Ascii(h,0,4)=="RIFF"&&Ascii(h,8,4)=="WEBP";
    case MagickFormat.Psd:case MagickFormat.Psb:return n>=26&&Ascii(h,0,4)=="8BPS"&&h[4]==0&&h[5]==(format==MagickFormat.Psb?2:1);
    case MagickFormat.Avif:case MagickFormat.Heic:case MagickFormat.Heif:case MagickFormat.Cr3:
     if(n<16||Ascii(h,4,4)!="ftyp")return false;
     string brands=Ascii(h,8,n-8);return format==MagickFormat.Cr3?brands.Contains("crx "):format==MagickFormat.Avif?(brands.Contains("avif")||brands.Contains("avis")):(brands.Contains("heic")||brands.Contains("heix")||brands.Contains("hevc")||brands.Contains("hevx")||brands.Contains("mif1")||brands.Contains("msf1"));
    default:return false;
   }
  }
  static bool TiffMagic(byte[] h){return (h[0]=='I'&&h[1]=='I'&&(h[2]==42||h[2]==43)&&h[3]==0)||(h[0]=='M'&&h[1]=='M'&&h[2]==0&&(h[3]==42||h[3]==43));}
  static string Ascii(byte[] h,int offset,int count){return Encoding.ASCII.GetString(h,offset,count);}
  // Framework's C# 5 compiler cannot bind Magick 15's generic unmanaged
  // interface constraint. Resolve only these two fixed public overloads; no
  // user-controlled method names, assembly names or code are ever evaluated.
  static readonly MethodInfo PingMethod=ReadMethod("Ping"),ReadMethodInfo=ReadMethod("Read");
  static MethodInfo ReadMethod(string name){return typeof(MagickImage).GetMethods().Single(method=>{var parameters=method.GetParameters();return method.Name==name&&parameters.Length==2&&parameters[0].ParameterType==typeof(Stream)&&parameters[1].ParameterType.IsGenericType&&parameters[1].ParameterType.GetGenericTypeDefinition().Name=="IMagickReadSettings`1";});}
  static void Read(MagickImage image,Stream stream,MagickReadSettings settings,bool ping){try{(ping?PingMethod:ReadMethodInfo).Invoke(image,new object[]{stream,settings});}catch(TargetInvocationException ex){if(ex.InnerException!=null)ExceptionDispatchInfo.Capture(ex.InnerException).Throw();throw;}}
  static bool SecuritySelfTest(){
   foreach(var format in new[]{MagickFormat.Pdf,MagickFormat.Ps,MagickFormat.Svg,MagickFormat.Msl,MagickFormat.Mvg,MagickFormat.Https}){
    try{using(var stream=new MemoryStream(Encoding.ASCII.GetBytes("not a document or a URL")))using(var image=new MagickImage())Read(image,stream,new MagickReadSettings{Format=format},false);return false;}
    catch(MagickPolicyErrorException){}catch{return false;}
   }
   // Prove that exclusive ListLength=2 still rejects a second decoded frame.
   // A timed two-frame synthetic GIF contains no personal or network data.
   byte[] animation={71,73,70,56,57,97,1,0,1,0,128,0,0,255,0,0,0,0,255,33,255,11,78,69,84,83,67,65,80,69,50,46,48,3,1,0,0,0,33,249,4,4,10,0,0,0,44,0,0,0,0,1,0,1,0,0,2,2,68,1,0,33,249,4,4,10,0,0,0,44,0,0,0,0,1,0,1,0,0,2,2,76,1,0,59};
   try{using(var collection=new MagickImageCollection(animation))return false;}
   catch(MagickResourceLimitErrorException){return true;}catch{return false;}
  }
  sealed class BoundedOutput : Stream {
   readonly Stream inner;readonly long maximum;long count;
   internal BoundedOutput(Stream inner,long maximum){this.inner=inner;this.maximum=maximum;}
   public override bool CanRead{get{return false;}}public override bool CanSeek{get{return false;}}public override bool CanWrite{get{return true;}}public override long Length{get{return count;}}public override long Position{get{return count;}set{throw new NotSupportedException();}}
   public override void Flush(){inner.Flush();}public override void Write(byte[] buffer,int offset,int length){if(length>maximum-count)throw new InvalidDataException("图像增强输出过大。");inner.Write(buffer,offset,length);count+=length;}
   public override int Read(byte[] buffer,int offset,int count){throw new NotSupportedException();}public override long Seek(long offset,SeekOrigin origin){throw new NotSupportedException();}public override void SetLength(long value){throw new NotSupportedException();}
  }
 }
}
