using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Mori {
 // Reads Photoshop's saved merged image, never layers or the original at full
 // resolution. No native codec, Photoshop installation or conversion needed.
 internal static class PsdComposite {
  internal const int MaxSourceDimension=30000;
  internal const long MaxSourcePixels=256L*1024*1024;
  internal const long MaxInputBytes=2L*1024*1024*1024;
  internal const int MaxOutputPixels=16*1024*1024;
  internal static Bitmap Open(string path,int maxDimension=2048,int maxPixels=4*1024*1024,CancellationToken cancellation=default(CancellationToken)){
   int width,height;return Open(path,maxDimension,maxPixels,cancellation,out width,out height);
  }
  internal static Bitmap Open(string path,int maxDimension,int maxPixels,CancellationToken cancellation,out int width,out int height){
   using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))return Decode(stream,maxDimension,maxPixels,cancellation,out width,out height);
  }
  // The caller owns the stream. A seekable stream lets thumbnails skip source
  // scanlines rather than expand a many-megapixel planar image into memory.
  internal static Bitmap Decode(Stream stream,int maxDimension=2048,int maxPixels=4*1024*1024,CancellationToken cancellation=default(CancellationToken)){
   int width,height;return Decode(stream,maxDimension,maxPixels,cancellation,out width,out height);
  }
  internal static Bitmap Decode(Stream stream,int maxDimension,int maxPixels,CancellationToken cancellation,out int sourceWidth,out int sourceHeight){
   sourceWidth=0;sourceHeight=0;
   if(stream==null)throw new ArgumentNullException("stream");
   if(!stream.CanRead||!stream.CanSeek)throw new ArgumentException("PSD 预览需要可读取、可定位的文件流。","stream");
   if(maxDimension<1||maxDimension>8192)throw new ArgumentOutOfRangeException("maxDimension");
   if(maxPixels<1||maxPixels>MaxOutputPixels)throw new ArgumentOutOfRangeException("maxPixels");
   cancellation.ThrowIfCancellationRequested();
   long remaining=stream.Length-stream.Position;
   if(remaining<26)throw Broken();
   if(remaining>MaxInputBytes)throw new NotSupportedException("PSD 文件超过轻量预览的 2 GB 限制。");
   if(U32(stream)!=0x38425053)throw new InvalidDataException("这不是有效的 PSD 文件。");
   int version=U16(stream);
   if(version==2)throw new NotSupportedException("PSB 大型文档暂不支持内置预览，请用默认程序打开。");
   if(version!=1)throw new InvalidDataException("PSD 文件版本无效。");
   for(int i=0;i<6;i++)if(Byte(stream)!=0)throw new InvalidDataException("PSD 文件头损坏。");
   int channels=U16(stream);long heightValue=U32(stream),widthValue=U32(stream);
   int depth=U16(stream),mode=U16(stream);
   if(channels<1||channels>56||widthValue<1||heightValue<1)throw new InvalidDataException("PSD 图像尺寸或通道数无效。");
   if(widthValue>MaxSourceDimension||heightValue>MaxSourceDimension||widthValue*heightValue>MaxSourcePixels)throw new NotSupportedException("PSD 图像过大，超过轻量预览的尺寸限制。");
   if(depth!=8)throw new NotSupportedException("PSD 内置预览目前支持 8 位图像；16/32 位图像请用默认程序打开。");
   int baseChannels=mode==1?1:mode==3?3:mode==4?4:0;
   if(baseChannels==0)throw new NotSupportedException("PSD 内置预览目前支持 RGB、灰度和 CMYK 图像。");
   if(channels<baseChannels)throw new InvalidDataException("PSD 颜色通道不完整。");
   SkipSection(stream); // Colour-mode data.
   SkipSection(stream); // Image resources (ICC profiles are not interpreted).
   // Only the signed layer count distinguishes merged transparency from a
   // user's saved selection/spot channels; layer images are never expanded.
   bool useAlpha=SkipLayerSection(stream);
   cancellation.ThrowIfCancellationRequested();
   int compression=U16(stream);
   if(compression!=0&&compression!=1)throw new NotSupportedException("此 PSD 使用 ZIP 压缩，暂不支持轻量预览；请另存为兼容 PSD 或用默认程序打开。");
   int width=(int)widthValue,height=(int)heightValue,outputWidth,outputHeight;
   sourceWidth=width;sourceHeight=height;
   Fit(width,height,maxDimension,maxPixels,out outputWidth,out outputHeight);
   ushort[] rowLengths=null;long[] channelOffsets=new long[channels];
   long compositeStart=stream.Position;
   if(compression==0){
    long planeBytes=(long)width*height,totalBytes=planeBytes*channels;
    Require(stream,totalBytes);
    for(int channel=0;channel<channels;channel++)channelOffsets[channel]=compositeStart+channel*planeBytes;
   }else{
    int count=checked(channels*height);
    Require(stream,(long)count*2);
    rowLengths=new ushort[count];long encodedBytes=0;
    for(int i=0;i<count;i++){
     if((i&16383)==0)cancellation.ThrowIfCancellationRequested();
     int length=U16(stream);
     if(length==0)throw new InvalidDataException("PSD 压缩行数据为空。");
     rowLengths[i]=(ushort)length;encodedBytes+=length;
    }
    Require(stream,encodedBytes);
    long offset=stream.Position;
    for(int channel=0;channel<channels;channel++){
     channelOffsets[channel]=offset;
     for(int y=0;y<height;y++)offset+=rowLengths[channel*height+y];
    }
   }
   // At the maximum output cap this is 64 MB; default previews are <=16 MB.
   // Source working memory is one <=30 KB scanline, one <=64 KB RLE row and
   // an RLE length table capped at 56*30000 ushorts (about 3.2 MB).
   byte[] pixels=new byte[checked(outputWidth*outputHeight*4)];
   for(int i=3;i<pixels.Length;i+=4)pixels[i]=255;
   byte[] row=new byte[width];byte[] packed=compression==1?new byte[65535]:null;
   int[] sampleX=new int[outputWidth],sampleY=new int[outputHeight];
   for(int x=0;x<outputWidth;x++)sampleX[x]=(int)(((2L*x+1)*width)/(2L*outputWidth));
   for(int y=0;y<outputHeight;y++)sampleY[y]=(int)(((2L*y+1)*height)/(2L*outputHeight));
   int usefulChannels=Math.Min(channels,baseChannels+(useAlpha?1:0));
   for(int channel=0;channel<usefulChannels;channel++){
    long offset=channelOffsets[channel];int sourceY=0;
    for(int y=0;y<outputHeight;y++){
     cancellation.ThrowIfCancellationRequested();int targetY=sampleY[y];
     if(compression==0){stream.Position=channelOffsets[channel]+(long)targetY*width;Read(stream,row,width);}
     else{
      while(sourceY<targetY){offset+=rowLengths[channel*height+sourceY];sourceY++;}
      int length=rowLengths[channel*height+targetY];stream.Position=offset;Read(stream,packed,length);Unpack(packed,length,row,width);
      offset+=length;sourceY=targetY+1;
     }
     for(int x=0;x<outputWidth;x++){
      int index=(y*outputWidth+x)*4;byte value=row[sampleX[x]];
      if(channel==baseChannels){pixels[index+3]=value;continue;}
      if(mode==1){pixels[index]=value;pixels[index+1]=value;pixels[index+2]=value;}
      else if(channel<3)pixels[index+2-channel]=value;
      else{
       // Photoshop composite CMYK is inverted: 255 means no ink. Without
       // an ICC converter, this is an intentionally approximate preview.
       pixels[index]=(byte)((pixels[index]*value+127)/255);
       pixels[index+1]=(byte)((pixels[index+1]*value+127)/255);
       pixels[index+2]=(byte)((pixels[index+2]*value+127)/255);
      }
     }
    }
   }
   cancellation.ThrowIfCancellationRequested();
   var bitmap=new Bitmap(outputWidth,outputHeight,PixelFormat.Format32bppArgb);
   try{
    var data=bitmap.LockBits(new Rectangle(0,0,outputWidth,outputHeight),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
    try{
     for(int y=0;y<outputHeight;y++){
      cancellation.ThrowIfCancellationRequested();
      Marshal.Copy(pixels,y*outputWidth*4,IntPtr.Add(data.Scan0,y*data.Stride),outputWidth*4);
     }
    }finally{bitmap.UnlockBits(data);}
    return bitmap;
   }catch{bitmap.Dispose();throw;}
  }
  static bool SkipLayerSection(Stream stream){
   long length=U32(stream);Require(stream,length);
   if(length==0)return true; // Layerless RGBA composites from simple writers.
   long end=stream.Position+length;
   if(length<4)throw Broken();
   long layerLength=U32(stream);
   if(layerLength>length-4||layerLength==1)throw Broken();
   bool transparency=layerLength==0;
   if(layerLength>=2)transparency=unchecked((short)U16(stream))<0;
   stream.Position=end;return transparency;
  }
  static void Fit(int width,int height,int maxDimension,int maxPixels,out int outputWidth,out int outputHeight){
   double scale=Math.Min(1,Math.Min((double)maxDimension/width,(double)maxDimension/height));
   scale=Math.Min(scale,Math.Sqrt((double)maxPixels/((long)width*height)));
   outputWidth=Math.Max(1,(int)Math.Floor(width*scale));outputHeight=Math.Max(1,(int)Math.Floor(height*scale));
   // A one-pixel axis cannot shrink further; keep the pixel limit strict for
   // extremely elongated files or a deliberately tiny thumbnail request.
   if((long)outputWidth*outputHeight>maxPixels){
    if(outputWidth>=outputHeight)outputWidth=Math.Max(1,maxPixels/outputHeight);
    else outputHeight=Math.Max(1,maxPixels/outputWidth);
   }
  }
  static void Unpack(byte[] source,int length,byte[] destination,int width){
   int input=0,output=0;
   while(input<length){
    int tag=source[input++];
    if(tag==128)continue;
    if(tag<=127){
     int count=tag+1;
     if(count>length-input||count>width-output)throw new InvalidDataException("PSD 压缩行数据损坏。");
     Buffer.BlockCopy(source,input,destination,output,count);input+=count;output+=count;
    }else{
     int count=257-tag;
     if(input>=length||count>width-output)throw new InvalidDataException("PSD 压缩行数据损坏。");
     byte value=source[input++];for(int i=0;i<count;i++)destination[output++]=value;
    }
   }
   if(output!=width)throw new InvalidDataException("PSD 压缩行数据不完整。");
  }
  static void SkipSection(Stream stream){long length=U32(stream);Require(stream,length);stream.Position+=length;}
  static void Require(Stream stream,long count){if(count<0||count>stream.Length-stream.Position)throw Broken();}
  static void Read(Stream stream,byte[] buffer,int count){
   int position=0;while(position<count){int read=stream.Read(buffer,position,count-position);if(read<1)throw Broken();position+=read;}
  }
  static int Byte(Stream stream){int value=stream.ReadByte();if(value<0)throw Broken();return value;}
  static int U16(Stream stream){return (Byte(stream)<<8)|Byte(stream);}
  static uint U32(Stream stream){return ((uint)Byte(stream)<<24)|((uint)Byte(stream)<<16)|((uint)Byte(stream)<<8)|(uint)Byte(stream);}
  static InvalidDataException Broken(){return new InvalidDataException("PSD 文件损坏或未完整保存。");}
 }
}
