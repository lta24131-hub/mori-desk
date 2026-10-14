using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Mori;

namespace MoriDesk {
 internal static class PsdEmbeddedThumbnailTests {
  static void U16(BinaryWriter writer,int value){writer.Write((byte)(value>>8));writer.Write((byte)value);}
  static void U32(BinaryWriter writer,uint value){writer.Write((byte)(value>>24));writer.Write((byte)(value>>16));writer.Write((byte)(value>>8));writer.Write((byte)value);}
  static byte[] Jpeg(int width,int height){using(var bitmap=new Bitmap(width,height)){using(var graphics=Graphics.FromImage(bitmap))graphics.Clear(Color.FromArgb(218,65,28));using(var output=new MemoryStream()){bitmap.Save(output,ImageFormat.Jpeg);return output.ToArray();}}}
  static byte[] Thumbnail(byte[] jpeg,int width=96,int height=48,int format=1){using(var output=new MemoryStream())using(var writer=new BinaryWriter(output)){int row=((width*24+31)/32)*4;U32(writer,(uint)format);U32(writer,(uint)width);U32(writer,(uint)height);U32(writer,(uint)row);U32(writer,(uint)(row*height));U32(writer,(uint)jpeg.Length);U16(writer,24);U16(writer,1);writer.Write(jpeg);return output.ToArray();}}
  static byte[] Resource(int id,byte[] body,string name=""){using(var output=new MemoryStream())using(var writer=new BinaryWriter(output)){writer.Write(Encoding.ASCII.GetBytes("8BIM"));U16(writer,id);byte[] title=Encoding.ASCII.GetBytes(name);writer.Write((byte)title.Length);writer.Write(title);if((title.Length+1)%2!=0)writer.Write((byte)0);U32(writer,(uint)body.Length);writer.Write(body);if(body.Length%2!=0)writer.Write((byte)0);return output.ToArray();}}
  static byte[] Psd(byte[] resources,int depth=16,int compression=2){using(var output=new MemoryStream())using(var writer=new BinaryWriter(output)){writer.Write(Encoding.ASCII.GetBytes("8BPS"));U16(writer,1);writer.Write(new byte[6]);U16(writer,3);U32(writer,8000);U32(writer,12000);U16(writer,depth);U16(writer,3);U32(writer,0);U32(writer,(uint)resources.Length);writer.Write(resources);U32(writer,0);U16(writer,compression);writer.Write(new byte[]{0,1,2,3});return output.ToArray();}}
  internal static byte[] Fixture(){return Psd(Resource(1036,Thumbnail(Jpeg(96,48))));}
  static Bitmap Decode(byte[] bytes,int size=64){int width,height;using(var stream=new MemoryStream(bytes))return PsdEmbeddedThumbnail.Decode(stream,size,CancellationToken.None,out width,out height);}
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);byte[] jpeg=Jpeg(96,48),thumbnail=Thumbnail(jpeg),resource=Resource(1036,thumbnail,"RGB preview");
   foreach(int depth in new[]{8,16,32})foreach(int compression in new[]{0,1,2,3})using(var stream=new MemoryStream(Psd(resource,depth,compression))){int width,height;using(var bitmap=PsdEmbeddedThumbnail.Decode(stream,64,CancellationToken.None,out width,out height)){check(bitmap!=null&&bitmap.Width==64&&bitmap.Height==32,"PSD "+depth+" bit / compression "+compression+" uses bounded embedded JPEG independently of composite");check(width==12000&&height==8000&&stream.CanRead,"embedded PSD preview reports original dimensions and leaves caller stream open");check(bitmap.GetPixel(32,16).R>bitmap.GetPixel(32,16).B+100,"PSD 1036 RGB thumbnail does not swap red and blue");}}
   using(var bitmap=Decode(Psd(Resource(1033,thumbnail))))check(bitmap==null,"obsolete BGR 1033 resource is deliberately skipped instead of showing reversed colours");
   using(var bitmap=Decode(Psd(Resource(1036,Thumbnail(jpeg,96,48,0)))))check(bitmap==null,"raw thumbnail format 0 is deliberately unsupported");
   using(var bitmap=Decode(Psd(new byte[0])))check(bitmap==null,"PSD without embedded thumbnail returns null for composite fallback");
   byte[] corrupt=(byte[])resource.Clone();corrupt[0]=0;using(var bitmap=Decode(Psd(corrupt)))check(bitmap==null,"invalid resource signature fails without decoding or large allocation");
   corrupt=(byte[])resource.Clone();int bodyStart=resource.Length-thumbnail.Length-(thumbnail.Length%2);corrupt[bodyStart-1]=255;using(var bitmap=Decode(Psd(corrupt)))check(bitmap==null,"resource data length cannot escape its declared resource section");
   byte[] noJpeg=(byte[])thumbnail.Clone();noJpeg[28]=0;using(var bitmap=Decode(Psd(Resource(1036,noJpeg))))check(bitmap==null,"invalid JPEG thumbnail returns null rather than using arbitrary resource bytes as an image");
   byte[] wrongDimensions=(byte[])thumbnail.Clone();wrongDimensions[7]=1;using(var bitmap=Decode(Psd(Resource(1036,wrongDimensions))))check(bitmap==null,"resource/JPEG dimension mismatch is rejected before GDI decoder");
   byte[] enormous=(byte[])thumbnail.Clone();enormous[4]=127;using(var bitmap=Decode(Psd(Resource(1036,enormous))))check(bitmap==null,"oversized embedded pixel dimensions are rejected before GDI allocation");
   byte[] truncated=Psd(resource);Array.Resize(ref truncated,36);using(var bitmap=Decode(truncated))check(bitmap==null,"truncated PSD resource section returns null safely");
   byte[] tooLargeSection=Psd(resource);tooLargeSection[30]=127;using(var bitmap=Decode(tooLargeSection))check(bitmap==null,"resource-section size is bounded independently of individual resources");
   using(var many=new MemoryStream()){byte[] blank=Resource(1000,new byte[0]);for(int i=0;i<PsdEmbeddedThumbnail.MaximumResources;i++)many.Write(blank,0,blank.Length);many.Write(resource,0,resource.Length);using(var bitmap=Decode(Psd(many.ToArray())))check(bitmap==null,"resource count bound stops scanning before an excessive number of entries");}
   string path=Path.Combine(root,"synthetic-16bit-ZIP.psd");byte[] original=Psd(resource);File.WriteAllBytes(path,original);DateTime time=File.GetLastWriteTimeUtc(path);int originalWidth,originalHeight;
   using(var bitmap=PsdEmbeddedThumbnail.Open(path,32,out originalWidth,out originalHeight))check(bitmap!=null&&bitmap.Width<=32&&bitmap.Height<=32&&originalWidth==12000,"path API returns detached bitmap with strict requested bound");
   check(File.ReadAllBytes(path).SequenceEqual(original)&&File.GetLastWriteTimeUtc(path)==time,"embedded PSD preview preserves source bytes and timestamp");
   using(var exclusive=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(exclusive.Length==original.Length,"embedded PSD preview releases source file handle");
   bool canceled=false;try{using(var bitmap=PsdEmbeddedThumbnail.Open(path,32,new CancellationToken(true),out originalWidth,out originalHeight)){} }catch(OperationCanceledException){canceled=true;}check(canceled,"embedded PSD thumbnail never swallows pre-cancellation");
   bool invalid=false;try{using(var bitmap=PsdEmbeddedThumbnail.Open(path,513,out originalWidth,out originalHeight)){} }catch(ArgumentOutOfRangeException){invalid=true;}check(invalid,"embedded PSD target cannot exceed 512-pixel cap");
   using(var stream=new CancelStream(original))using(var token=new CancellationTokenSource()){stream.Token=token;stream.CancelAfter=28;canceled=false;try{using(var bitmap=PsdEmbeddedThumbnail.Decode(stream,32,token.Token,out originalWidth,out originalHeight)){} }catch(OperationCanceledException){canceled=true;}check(canceled,"embedded PSD thumbnail cancellation during resource scanning is propagated");}
   using(var stream=new OomStream(original)){bool propagated=false;try{using(var bitmap=PsdEmbeddedThumbnail.Decode(stream,32,CancellationToken.None,out originalWidth,out originalHeight)){} }catch(OutOfMemoryException){propagated=true;}check(propagated,"embedded PSD decoder never converts allocation failure into a missing-thumbnail result");}
  }
  sealed class CancelStream:MemoryStream {
   internal CancellationTokenSource Token;internal int CancelAfter;int bytes;
   internal CancelStream(byte[] input):base(input){}
   public override int ReadByte(){int value=base.ReadByte();if(value>=0&&++bytes>=CancelAfter&&Token!=null)Token.Cancel();return value;}
  }
  sealed class OomStream:MemoryStream {
   internal OomStream(byte[] input):base(input){}
   public override int ReadByte(){throw new OutOfMemoryException("Synthetic allocation failure");}
  }
 }
}
