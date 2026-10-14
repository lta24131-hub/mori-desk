using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;

namespace MoriDesk {
 public static class PsdCompositeTests {
  public static void Run(string root,Action<bool,string> check){
   var rgb=new[]{new byte[]{255,10,30,90,100,110},new byte[]{0,20,40,120,130,140},new byte[]{10,30,50,150,160,170},new byte[]{255,128,0,90,180,240}};
   foreach(bool rle in new[]{false,true}){
    using(var stream=new MemoryStream(File(3,2,3,rgb,rle)))using(var bitmap=Mori.PsdComposite.Decode(stream)){
     check(bitmap.Width==3&&bitmap.Height==2,"PSD "+(rle?"RLE":"raw")+" merged image size");
     check(bitmap.GetPixel(0,0).ToArgb()==Color.FromArgb(255,255,0,10).ToArgb(),"PSD RGB channel order");
     check(bitmap.GetPixel(1,0).ToArgb()==Color.FromArgb(128,10,20,30).ToArgb(),"PSD optional alpha preserved");
     check(bitmap.GetPixel(2,0).A==0,"PSD zero alpha preserved");
     check(stream.CanRead,"PSD decoder leaves caller stream open");
    }
    using(var stream=new MemoryStream(File(3,2,3,rgb,rle)))using(var bitmap=Mori.PsdComposite.Decode(stream,2,4)){
     check(bitmap.Width==2&&bitmap.Height==1,"PSD thumbnail dimensions bounded");
     check(bitmap.GetPixel(0,0).ToArgb()==Color.FromArgb(90,90,120,150).ToArgb()&&bitmap.GetPixel(1,0).ToArgb()==Color.FromArgb(240,110,140,170).ToArgb(),"PSD thumbnail samples correct source rows and columns");
    }
   }
   using(var stream=new MemoryStream(File(2,1,1,new[]{new byte[]{0,220},new byte[]{80,255}},false)))using(var bitmap=Mori.PsdComposite.Decode(stream)){
    check(bitmap.GetPixel(0,0).ToArgb()==Color.FromArgb(80,0,0,0).ToArgb()&&bitmap.GetPixel(1,0).R==220&&bitmap.GetPixel(1,0).B==220,"PSD grayscale and alpha");
   }
   using(var stream=new MemoryStream(LayerMarker(File(3,2,3,rgb,false),1)))using(var bitmap=Mori.PsdComposite.Decode(stream))check(bitmap.GetPixel(1,0).A==255,"PSD layered saved selection mask does not make image transparent");
   using(var stream=new MemoryStream(LayerMarker(File(3,2,3,rgb,true),-1)))using(var bitmap=Mori.PsdComposite.Decode(stream))check(bitmap.GetPixel(1,0).A==128,"PSD negative layer count declares composite transparency");
   foreach(bool rle in new[]{false,true})using(var stream=new MemoryStream(File(2,1,4,new[]{new byte[]{255,255},new byte[]{0,255},new byte[]{128,255},new byte[]{128,255},new byte[]{90,255}},rle)))using(var bitmap=Mori.PsdComposite.Decode(stream)){
    check(bitmap.GetPixel(0,0).ToArgb()==Color.FromArgb(90,128,0,64).ToArgb(),"PSD inverted CMYK approximate composite");
    check(bitmap.GetPixel(1,0).ToArgb()==Color.White.ToArgb(),"PSD inverted CMYK white");
   }
   using(var stream=new MemoryStream(RleRow(5,new byte[]{252,70})))using(var bitmap=Mori.PsdComposite.Decode(stream))check(bitmap.GetPixel(4,0).R==70,"PSD PackBits repeated run");
   using(var stream=new MemoryStream(RleRow(1,new byte[]{128,0,42,128})))using(var bitmap=Mori.PsdComposite.Decode(stream))check(bitmap.GetPixel(0,0).R==42,"PSD PackBits no-op accepted");
   using(var stream=new VirtualRawStream(8192,8192))using(var bitmap=Mori.PsdComposite.Decode(stream,16,256)){
    check(bitmap.Width==16&&bitmap.Height==16&&bitmap.GetPixel(0,0).ToArgb()==Color.FromArgb(255,30,60,90).ToArgb(),"PSD large source row-wise thumbnail");
    check(stream.BytesRead<1024*1024,"PSD thumbnail reads less than 1 MB of a 192 MB virtual source");
   }
   int sourceWidth,sourceHeight;
   using(var stream=new MemoryStream(File(3,2,3,rgb,false)))using(var bitmap=Mori.PsdComposite.Decode(stream,2,4,CancellationToken.None,out sourceWidth,out sourceHeight))check(sourceWidth==3&&sourceHeight==2&&bitmap.Width==2,"PSD reports original dimensions separately from thumbnail");
   using(var stream=new VirtualRawStream(30000,1))using(var bitmap=Mori.PsdComposite.Decode(stream,8192,1))check(bitmap.Width==1&&bitmap.Height==1,"PSD elongated source obeys single-pixel budget");
   var valid=File(3,2,3,rgb,false);
   check(Fails<InvalidDataException>(new byte[4]),"PSD truncated header rejected");
   var bad=(byte[])valid.Clone();bad[0]=0;check(Fails<InvalidDataException>(bad),"PSD invalid signature rejected");
   bad=(byte[])valid.Clone();bad[5]=2;check(Fails<NotSupportedException>(bad),"PSD PSB explicitly unsupported");
   bad=(byte[])valid.Clone();bad[6]=1;check(Fails<InvalidDataException>(bad),"PSD reserved header corruption rejected");
   bad=(byte[])valid.Clone();bad[13]=0;check(Fails<InvalidDataException>(bad),"PSD zero channel count rejected");
   bad=(byte[])valid.Clone();bad[13]=57;check(Fails<InvalidDataException>(bad),"PSD excessive channel count rejected");
   bad=(byte[])valid.Clone();bad[23]=16;check(Fails<NotSupportedException>(bad),"PSD high bit depth explicitly unsupported");
   bad=(byte[])valid.Clone();bad[25]=9;check(Fails<NotSupportedException>(bad),"PSD LAB explicitly unsupported");
   bad=(byte[])valid.Clone();bad[13]=2;check(Fails<InvalidDataException>(bad),"PSD incomplete RGB channels rejected");
   bad=(byte[])valid.Clone();bad[39]=2;check(Fails<NotSupportedException>(bad),"PSD ZIP explicitly unsupported");
   bad=(byte[])valid.Clone();bad[39]=3;check(Fails<NotSupportedException>(bad),"PSD ZIP prediction explicitly unsupported");
   bad=(byte[])valid.Clone();bad[29]=255;check(Fails<InvalidDataException>(bad),"PSD out-of-file resource length rejected without allocation");
   bad=LayerMarker(valid,1);bad[41]=20;check(Fails<InvalidDataException>(bad),"PSD layer info length cannot escape layer section");
   bad=(byte[])valid.Clone();bad[37]=1;check(Fails<InvalidDataException>(bad),"PSD incomplete layer section rejected");
   bad=new byte[valid.Length-1];Buffer.BlockCopy(valid,0,bad,0,bad.Length);check(Fails<InvalidDataException>(bad),"PSD truncated raw plane rejected before decode");
   check(Fails<NotSupportedException>(Header(30001,1,3,3,0)),"PSD source dimension cap enforced");
   check(Fails<NotSupportedException>(Header(30000,30000,3,3,0)),"PSD source pixel cap enforced");
   check(Fails<InvalidDataException>(RleRow(1,new byte[]{0})),"PSD truncated PackBits literal rejected");
   check(Fails<InvalidDataException>(RleRow(1,new byte[]{255})),"PSD truncated PackBits repeat rejected");
   check(Fails<InvalidDataException>(RleRow(1,new byte[]{255,5})),"PSD PackBits output overflow rejected");
   check(Fails<InvalidDataException>(RleRow(2,new byte[]{0,5})),"PSD PackBits incomplete output rejected");
   check(Fails<InvalidDataException>(RleRow(1,new byte[0])),"PSD empty RLE row rejected");
   bad=File(3,2,3,rgb,true);Array.Resize(ref bad,bad.Length-1);check(Fails<InvalidDataException>(bad),"PSD truncated RLE data rejected");
   var cancellation=new CancellationToken(true);
   bool cancelled=false;try{using(var stream=new MemoryStream(valid))using(var image=Mori.PsdComposite.Decode(stream,128,16384,cancellation)){} }catch(OperationCanceledException){cancelled=true;}
   check(cancelled,"PSD already-cancelled decode does no work");
   using(var tokenSource=new CancellationTokenSource())using(var stream=new VirtualRawStream(8192,8192)){
    stream.Cancel=tokenSource;stream.CancelAfter=1000;cancelled=false;
    try{using(var image=Mori.PsdComposite.Decode(stream,16,256,tokenSource.Token)){} }catch(OperationCanceledException){cancelled=true;}
    check(cancelled&&stream.BytesRead<20000,"PSD in-progress cancellation stops before additional source rows");
   }
   bool oversized=false;try{using(var stream=new VirtualRawStream(30000,30000))using(var image=Mori.PsdComposite.Decode(stream)){} }catch(NotSupportedException){oversized=true;}
   check(oversized,"PSD oversized input file rejected without image allocation");
   bool invalid=false;try{using(var stream=new MemoryStream(valid))using(var image=Mori.PsdComposite.Decode(stream,0,1)){} }catch(ArgumentOutOfRangeException){invalid=true;}
   check(invalid,"PSD invalid target dimension rejected");
   invalid=false;try{using(var stream=new MemoryStream(valid))using(var image=Mori.PsdComposite.Decode(stream,2048,Mori.PsdComposite.MaxOutputPixels+1)){} }catch(ArgumentOutOfRangeException){invalid=true;}
   check(invalid,"PSD output memory cap cannot be overridden");
   string path=Path.Combine(root,"synthetic-merged.psd");Directory.CreateDirectory(root);System.IO.File.WriteAllBytes(path,valid);
   using(var bitmap=Mori.PsdComposite.Open(path,128,16384))check(bitmap.Width==3,"PSD path API returns detached bitmap");
   using(var stream=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(stream.Length==valid.Length,"PSD path API releases original file");
  }
  static bool Fails<T>(byte[] file)where T:Exception{
   try{using(var stream=new MemoryStream(file))using(var image=Mori.PsdComposite.Decode(stream)){}return false;}catch(T){return true;}
  }
  static byte[] Header(int width,int height,int channels,int mode,int compression){
   using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream)){
    U32(writer,0x38425053);U16(writer,1);writer.Write(new byte[6]);U16(writer,channels);U32(writer,(uint)height);U32(writer,(uint)width);U16(writer,8);U16(writer,mode);
    U32(writer,0);U32(writer,0);U32(writer,0);U16(writer,compression);return stream.ToArray();
   }
  }
  static byte[] File(int width,int height,int mode,byte[][] planes,bool rle){
   using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream)){
    writer.Write(Header(width,height,planes.Length,mode,rle?1:0));
    if(!rle){foreach(var plane in planes)writer.Write(plane);}
    else{
     var rows=new List<byte[]>();foreach(var plane in planes)for(int y=0;y<height;y++){
      using(var encoded=new MemoryStream()){
       for(int x=0;x<width;){int count=Math.Min(128,width-x);encoded.WriteByte((byte)(count-1));encoded.Write(plane,y*width+x,count);x+=count;}
       var row=encoded.ToArray();rows.Add(row);U16(writer,row.Length);
      }
     }
     foreach(var row in rows)writer.Write(row);
    }
    return stream.ToArray();
   }
  }
  static byte[] RleRow(int width,byte[] row){using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream)){writer.Write(Header(width,1,1,1,1));U16(writer,row.Length);writer.Write(row);return stream.ToArray();}}
  static byte[] LayerMarker(byte[] file,int count){using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream)){writer.Write(file,0,34);U32(writer,10);U32(writer,2);U16(writer,count);U32(writer,0);writer.Write(file,38,file.Length-38);return stream.ToArray();}}
  static void U16(BinaryWriter writer,int value){writer.Write((byte)(value>>8));writer.Write((byte)value);}
  static void U32(BinaryWriter writer,uint value){writer.Write((byte)(value>>24));writer.Write((byte)(value>>16));writer.Write((byte)(value>>8));writer.Write((byte)value);}
  sealed class VirtualRawStream:Stream{
   readonly byte[] header;readonly long planeBytes;long position;
   internal long BytesRead;
   internal CancellationTokenSource Cancel;internal long CancelAfter;
   internal VirtualRawStream(int width,int height){header=Header(width,height,3,3,0);planeBytes=(long)width*height;}
   public override bool CanRead{get{return true;}}public override bool CanSeek{get{return true;}}public override bool CanWrite{get{return false;}}
   public override long Length{get{return header.Length+planeBytes*3;}}
   public override long Position{get{return position;}set{if(value<0||value>Length)throw new ArgumentOutOfRangeException();position=value;}}
   public override int Read(byte[] buffer,int offset,int count){int result=(int)Math.Min(count,Length-position);for(int i=0;i<result;i++)buffer[offset+i]=(byte)ReadByte();return result;}
   public override int ReadByte(){if(position>=Length)return -1;long current=position++;BytesRead++;if(Cancel!=null&&BytesRead>=CancelAfter)Cancel.Cancel();return current<header.Length?header[(int)current]:(int)(30+((current-header.Length)/planeBytes)*30);}
   public override long Seek(long offset,SeekOrigin origin){Position=(origin==SeekOrigin.Begin?0:origin==SeekOrigin.Current?Position:Length)+offset;return Position;}
   public override void Flush(){}public override void SetLength(long value){throw new NotSupportedException();}public override void Write(byte[] buffer,int offset,int count){throw new NotSupportedException();}
  }
 }
}
