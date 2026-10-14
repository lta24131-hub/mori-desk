using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Mori {
 // Photoshop often stores a small merged JPEG independently of its full
 // composite depth/compression. This is a thumbnail only: never a substitute
 // for layers, transparency, colour-managed full preview or the saved file.
 internal static class PsdEmbeddedThumbnail {
  internal const long MaximumResourceBytes=64L*1024*1024;
  internal const int MaximumResources=4096;
  internal const int MaximumBlobBytes=4*1024*1024;
  internal const int MaximumEmbeddedPixels=4*1024*1024;
  const int MaximumEmbeddedDimension=4096;
  internal static Bitmap Open(string path,int size,out int width,out int height){return Open(path,size,CancellationToken.None,out width,out height);}
  internal static Bitmap Open(string path,int size,CancellationToken cancellation,out int width,out int height){
   width=height=0;
   if(size<1||size>512)throw new ArgumentOutOfRangeException("size","内嵌缩略图边长必须在 1 到 512 像素之间。");
   cancellation.ThrowIfCancellationRequested();
   try{using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))return Decode(stream,size,cancellation,out width,out height);}
   catch(IOException){return null;}catch(UnauthorizedAccessException){return null;}
  }
  // The caller owns the stream. Unrelated resources/layers are skipped by
  // offsets, never expanded or copied into memory. Unsupported/malformed
  // thumbnails return null; cancellation and allocation failure propagate.
  internal static Bitmap Decode(Stream stream,int size,CancellationToken cancellation,out int width,out int height){
   width=height=0;
   if(stream==null)throw new ArgumentNullException("stream");
   if(!stream.CanRead||!stream.CanSeek)throw new ArgumentException("PSD 缩略图需要可读取、可定位的流。","stream");
   if(size<1||size>512)throw new ArgumentOutOfRangeException("size");
   cancellation.ThrowIfCancellationRequested();
   try{
    Require(stream,26);
    if(U32(stream)!=0x38425053||U16(stream)!=1)return null;
    for(int i=0;i<6;i++)if(Byte(stream)!=0)return null;
    int channels=U16(stream);uint sourceHeight=U32(stream),sourceWidth=U32(stream);int depth=U16(stream),mode=U16(stream);
    if(channels<1||channels>56||sourceWidth<1||sourceHeight<1||sourceWidth>30000||sourceHeight>30000||(depth!=1&&depth!=8&&depth!=16&&depth!=32)||mode>9||mode==5||mode==6)return null;
    width=(int)sourceWidth;height=(int)sourceHeight;
    Skip(stream,U32(stream)); // Colour-mode data; no decoding or expansion.
    long resourceLength=U32(stream);if(resourceLength>MaximumResourceBytes)return null;Require(stream,resourceLength);
    long resourceEnd=stream.Position+resourceLength;int entries=0;
    while(stream.Position<resourceEnd){
     cancellation.ThrowIfCancellationRequested();if(++entries>MaximumResources)return null;
     NeedResource(stream,resourceEnd,7);if(U32(stream)!=0x3842494d)return null;int id=U16(stream);
     int nameLength=Byte(stream);long nameBytes=nameLength+(((nameLength+1)&1)==0?0:1);NeedResource(stream,resourceEnd,nameBytes+4);stream.Position+=nameBytes;
     long length=U32(stream),padded=length+(length&1);NeedResource(stream,resourceEnd,padded);long end=stream.Position+padded;
     // 1036 uses RGB. The obsolete 1033 resource uses reversed RGB/BGR
     // ordering; intentionally skip it rather than display wrong colours.
     // Raw format 0 is also skipped; only bounded, dimension-checked JPEG 1.
     if(id==1036&&length>=28&&length<=MaximumBlobBytes){
      byte[] data=new byte[(int)length];Read(stream,data,data.Length,cancellation);
      Bitmap bitmap=Jpeg(data,size,cancellation);if(bitmap!=null)return bitmap;
     }
     stream.Position=end;
    }
    return null;
   }catch(InvalidDataException){return null;}catch(IOException){return null;}
  }
  static Bitmap Jpeg(byte[] resource,int size,CancellationToken cancellation){
   uint format=At32(resource,0),width=At32(resource,4),height=At32(resource,8),rowBytes=At32(resource,12),total=At32(resource,16),compressed=At32(resource,20);
   if(format!=1||At16(resource,24)!=24||At16(resource,26)!=1||width<1||height<1||width>MaximumEmbeddedDimension||height>MaximumEmbeddedDimension||(long)width*height>MaximumEmbeddedPixels)return null;
   long expectedRow=(((long)width*24+31)/32)*4;
   if(rowBytes!=expectedRow||total!=expectedRow*height||compressed<4||compressed!=resource.Length-28)return null;
   int jpegWidth,jpegHeight;if(!JpegDimensions(resource,28,(int)compressed,out jpegWidth,out jpegHeight)||jpegWidth!=width||jpegHeight!=height)return null;
   cancellation.ThrowIfCancellationRequested();
   try{
    using(var encoded=new MemoryStream(resource,28,(int)compressed,false))using(var source=Image.FromStream(encoded,false,true)){
     if(source.Width!=jpegWidth||source.Height!=jpegHeight)return null;
     double ratio=Math.Min(1,size/(double)Math.Max(jpegWidth,jpegHeight));int w=Math.Max(1,(int)Math.Floor(jpegWidth*ratio)),h=Math.Max(1,(int)Math.Floor(jpegHeight*ratio));
     var result=new Bitmap(w,h,PixelFormat.Format32bppArgb);
     try{using(var graphics=Graphics.FromImage(result)){graphics.CompositingMode=CompositingMode.SourceCopy;graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;graphics.PixelOffsetMode=PixelOffsetMode.HighQuality;graphics.DrawImage(source,new Rectangle(0,0,w,h),0,0,source.Width,source.Height,GraphicsUnit.Pixel);}cancellation.ThrowIfCancellationRequested();return result;}
     catch{result.Dispose();throw;}
    }
   }catch(ArgumentException){return null;}catch(ExternalException){return null;}
  }
  static bool JpegDimensions(byte[] bytes,int offset,int length,out int width,out int height){
   width=height=0;int end=offset+length;
   if(length<4||bytes[offset++]!=255||bytes[offset++]!=216)return false;
   while(offset<end){
    if(bytes[offset++]!=255)return false;while(offset<end&&bytes[offset]==255)offset++;if(offset>=end)return false;int marker=bytes[offset++];
    if(marker==0||marker==218||marker==217)return false;
    if(marker==216||marker==1||(marker>=208&&marker<=215))continue;
    if(end-offset<2)return false;int segment=At16(bytes,offset);if(segment<2||segment>end-offset)return false;
    if(marker>=192&&marker<=207&&marker!=196&&marker!=200&&marker!=204){
     if(segment<8||bytes[offset+2]!=8)return false;height=At16(bytes,offset+3);width=At16(bytes,offset+5);int components=bytes[offset+7];
     return width>0&&height>0&&width<=MaximumEmbeddedDimension&&height<=MaximumEmbeddedDimension&&(long)width*height<=MaximumEmbeddedPixels&&(components==1||components==3);
    }
    offset+=segment;
   }return false;
  }
  static int At16(byte[] bytes,int offset){return (bytes[offset]<<8)|bytes[offset+1];}
  static uint At32(byte[] bytes,int offset){return ((uint)bytes[offset]<<24)|((uint)bytes[offset+1]<<16)|((uint)bytes[offset+2]<<8)|bytes[offset+3];}
  static void NeedResource(Stream stream,long end,long count){if(count<0||count>end-stream.Position)throw new InvalidDataException("PSD 缩略图资源边界无效。");}
  static void Require(Stream stream,long count){if(count<0||count>stream.Length-stream.Position)throw new InvalidDataException("PSD 缩略图数据不完整。");}
  static void Skip(Stream stream,long count){Require(stream,count);stream.Position+=count;}
  static int Byte(Stream stream){int value=stream.ReadByte();if(value<0)throw new EndOfStreamException();return value;}
  static int U16(Stream stream){return (Byte(stream)<<8)|Byte(stream);}
  static uint U32(Stream stream){return ((uint)Byte(stream)<<24)|((uint)Byte(stream)<<16)|((uint)Byte(stream)<<8)|(uint)Byte(stream);}
  static void Read(Stream stream,byte[] bytes,int count,CancellationToken cancellation){int read=0;while(read<count){cancellation.ThrowIfCancellationRequested();int next=stream.Read(bytes,read,Math.Min(8192,count-read));if(next==0)throw new EndOfStreamException();read+=next;}}
 }
}
