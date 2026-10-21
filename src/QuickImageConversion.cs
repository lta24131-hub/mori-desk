using System;
using System.IO;

namespace MoriDesk {
 // The quick menu is deliberately a still-image operation. File extensions
 // decide whether to show it, but bounded container inspection decides whether
 // it may run. No pixel decoder or complete image buffer enters the UI process.
 internal static class QuickImageConversion {
  const int MaximumChunks=65536;
  static string Format(string value){
   if(string.IsNullOrWhiteSpace(value))return "";
   string extension=value.Trim().ToLowerInvariant();
   if(extension.IndexOfAny(new[]{'\\','/'})>=0||extension.LastIndexOf('.')>0)extension=Path.GetExtension(extension);
   extension=extension.TrimStart('.');
   return extension=="jpeg"?"jpg":extension;
  }
  internal static bool SupportsExtension(string pathOrExtension){
   string source=Format(pathOrExtension);
   return source=="png"||source=="jpg"||source=="webp"||source=="bmp";
  }
  internal static string[] TargetsFor(string sourceExtension){
   string source=Format(sourceExtension);
   if(!SupportsExtension(source))return new string[0];
   if(source=="png")return new[]{"jpg","webp"};
   if(source=="jpg")return new[]{"png","webp"};
   if(source=="webp")return new[]{"png","jpg"};
   return new[]{"png","jpg","webp"};
  }
  internal static string ValidateStaticFile(string path){
   string source=Format(path);
   if(!SupportsExtension(source))throw new IOException("快速转换暂支持 PNG、JPG、WebP 和 BMP 静态图片，原文件未改动。");
   string input=FileConversionClient.ValidateInputs(new[]{path})[0];
   using(var stream=new FileStream(input,FileMode.Open,FileAccess.Read,FileShare.Read)){
    if(stream.Length==0||stream.Length>FileConversionClient.MaximumInputBytes)throw Invalid();
    try{
     if(source=="png")ReadPng(stream);
     else if(source=="webp")ReadWebP(stream);
     else if(source=="jpg")ReadJpeg(stream);
     else ReadBmp(stream);
    }catch(EndOfStreamException){throw Invalid();}
   }
   return source;
  }
  static IOException Invalid(){return new IOException("图片内容与格式不符或文件不完整，未进行快速转换；原文件未改动。");}
  static IOException Animated(){return new IOException("这是一张动图或多图文件，快速转换会丢失动画或其他画面，因此没有转换；原文件未改动。");}
  static byte[] Read(Stream stream,int length){
   var bytes=new byte[length];int offset=0;
   while(offset<length){int read=stream.Read(bytes,offset,length-offset);if(read==0)throw new EndOfStreamException();offset+=read;}
   return bytes;
  }
  static uint Big(byte[] bytes,int offset){return (uint)bytes[offset]<<24|(uint)bytes[offset+1]<<16|(uint)bytes[offset+2]<<8|bytes[offset+3];}
  static uint Little(byte[] bytes,int offset){return (uint)bytes[offset]|(uint)bytes[offset+1]<<8|(uint)bytes[offset+2]<<16|(uint)bytes[offset+3]<<24;}
  static int Short(byte[] bytes,int offset){return bytes[offset]|bytes[offset+1]<<8;}
  static bool Tag(byte[] bytes,int offset,string text){
   if(bytes.Length-offset<text.Length)return false;
   for(int index=0;index<text.Length;index++)if(bytes[offset+index]!=(byte)text[index])return false;
   return true;
  }
  static void ReadPng(Stream stream){
   byte[] signature=Read(stream,8);
   if(signature[0]!=137||!Tag(signature,1,"PNG\r\n")||signature[6]!=26||signature[7]!=10)throw Invalid();
   bool header=false,data=false,nonemptyData=false,palette=false,dataEnded=false;int color=0;
   for(int chunks=0;chunks<MaximumChunks;chunks++){
    byte[] chunk=Read(stream,8);uint length=Big(chunk,0);long start=stream.Position;
    if(length>0x7fffffff||length>stream.Length-start-4)throw Invalid();
    for(int index=4;index<8;index++)if(!((chunk[index]>=65&&chunk[index]<=90)||(chunk[index]>=97&&chunk[index]<=122)))throw Invalid();
    if(Tag(chunk,4,"acTL")||Tag(chunk,4,"fcTL")||Tag(chunk,4,"fdAT"))throw Animated();
    if(!header){
     if(!Tag(chunk,4,"IHDR")||length!=13)throw Invalid();
     byte[] info=Read(stream,13);int depth=info[8];color=info[9];
     if(Big(info,0)==0||Big(info,4)==0||Big(info,0)>0x7fffffff||Big(info,4)>0x7fffffff||
      info[10]!=0||info[11]!=0||info[12]>1)throw Invalid();
     if(color==0){if(depth!=1&&depth!=2&&depth!=4&&depth!=8&&depth!=16)throw Invalid();}
     else if(color==3){if(depth!=1&&depth!=2&&depth!=4&&depth!=8)throw Invalid();}
     else if(color==2||color==4||color==6){if(depth!=8&&depth!=16)throw Invalid();}
     else throw Invalid();
     header=true;
    }else if(Tag(chunk,4,"IHDR"))throw Invalid();
    else if(Tag(chunk,4,"PLTE")){
     if(palette||data||length==0||length>768||length%3!=0||color==0||color==4)throw Invalid();
     palette=true;
    }else if(Tag(chunk,4,"IDAT")){
     if(dataEnded||(color==3&&!palette))throw Invalid();
     data=true;if(length>0)nonemptyData=true;
    }else if(Tag(chunk,4,"IEND")){
     if(length!=0||!data||!nonemptyData||start+4!=stream.Length)throw Invalid();
     return;
    }else{
     // Unknown critical chunks may introduce a format we cannot prove static.
     if((chunk[4]&32)==0)throw Invalid();
     if(data)dataEnded=true;
    }
    stream.Position=start+length+4; // Skip the payload and CRC without decoding.
   }
   throw Invalid();
  }
  static void ReadWebP(Stream stream){
   byte[] header=Read(stream,12);
   if(!Tag(header,0,"RIFF")||!Tag(header,8,"WEBP")||(long)Little(header,4)+8!=stream.Length)throw Invalid();
   bool raster=false,extended=false;int chunks=0;
   while(stream.Position<stream.Length&&chunks<MaximumChunks){
    byte[] chunk=Read(stream,8);uint length=Little(chunk,4);long start=stream.Position;
    long padded=(long)length+(length&1);
    if(padded>stream.Length-start)throw Invalid();
    if(Tag(chunk,0,"ANIM")||Tag(chunk,0,"ANMF"))throw Animated();
    if(Tag(chunk,0,"VP8X")){
     if(chunks!=0||extended||length!=10)throw Invalid();
     byte[] info=Read(stream,10);if((info[0]&2)!=0)throw Animated();
     extended=true;
    }else if(Tag(chunk,0,"VP8 ")){
     if(raster||length<10)throw Invalid();
     byte[] info=Read(stream,10);
     if((info[0]&1)!=0||info[3]!=157||info[4]!=1||info[5]!=42||
      (Short(info,6)&16383)==0||(Short(info,8)&16383)==0)throw Invalid();
     raster=true;
    }else if(Tag(chunk,0,"VP8L")){
     if(raster||length<5)throw Invalid();
     byte[] info=Read(stream,5);if(info[0]!=47||(info[4]&224)!=0)throw Invalid();
     raster=true;
    }else if(!extended)throw Invalid();
    stream.Position=start+padded;chunks++;
   }
   if(!raster||stream.Position!=stream.Length)throw Invalid();
  }
  static void ReadJpeg(Stream stream){
   byte[] signature=Read(stream,2);if(signature[0]!=255||signature[1]!=216)throw Invalid();
   bool frame=false,scan=false;
   for(int chunks=0;chunks<MaximumChunks;chunks++){
    if(stream.ReadByte()!=255)throw Invalid();
    int marker=stream.ReadByte();int padding=0;
    while(marker==255&&padding++<MaximumChunks)marker=stream.ReadByte();
    if(marker<0||marker==0||marker==216||marker==217||marker==1||(marker>=208&&marker<=215))throw Invalid();
    byte[] size=Read(stream,2);int length=(size[0]<<8|size[1])-2;long start=stream.Position;
    if(length<0||length>stream.Length-start)throw Invalid();
    if(marker==226&&length>=4){
     byte[] info=Read(stream,4);if(Tag(info,0,"MPF\0"))throw Animated();
    }
    // SOF markers exclude the Huffman table, JPEG extensions and arithmetic table.
    if(marker>=192&&marker<=207&&marker!=196&&marker!=200&&marker!=204){
     if(frame||length<6)throw Invalid();
     byte[] info=Read(stream,6);
     if(info[0]==0||(info[1]==0&&info[2]==0)||(info[3]==0&&info[4]==0)||info[5]==0||length!=6+3*info[5])throw Invalid();
     frame=true;
    }
    if(marker==218){
     if(!frame||length<4)throw Invalid();
     byte[] info=Read(stream,1);if(info[0]==0||length!=4+2*info[0])throw Invalid();
     scan=true;break;
    }
    stream.Position=start+length;
   }
   if(!scan||stream.Length<4)throw Invalid();
   // Compressed scan data remains the isolated worker's responsibility.
   stream.Position=stream.Length-2;
   if(stream.ReadByte()!=255||stream.ReadByte()!=217)throw Invalid();
  }
  static void ReadBmp(Stream stream){
   byte[] header=Read(stream,18);
   if(!Tag(header,0,"BM"))throw Invalid();
   uint size=Little(header,2),offset=Little(header,10),dib=Little(header,14);
   if(size>stream.Length||size<offset||offset>=stream.Length||dib>stream.Length-14||offset<(long)dib+14)throw Invalid();
   if(dib==12){
    byte[] info=Read(stream,8);int bits=Short(info,6);
    if(Short(info,0)==0||Short(info,2)==0||Short(info,4)!=1||(bits!=1&&bits!=4&&bits!=8&&bits!=24))throw Invalid();
   }else{
    if(dib<40)throw Invalid();
    byte[] info=Read(stream,20);int width=unchecked((int)Little(info,0)),height=unchecked((int)Little(info,4));
    int bits=Short(info,10);uint compression=Little(info,12);
    if(width<=0||height==0||height==int.MinValue||Short(info,8)!=1||
     (bits!=1&&bits!=4&&bits!=8&&bits!=16&&bits!=24&&bits!=32)||
     (compression!=0&&compression!=1&&compression!=2&&compression!=3&&compression!=6))throw Invalid();
   }
  }
 }
}
