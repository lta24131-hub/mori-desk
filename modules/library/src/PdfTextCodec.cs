using System;
using System.IO;
using System.Text;

namespace Mori {
 // Private, versioned transport between the app and its same-binary worker.
 // Avoid the JSON dictionaries, boxed numbers and full UTF-16 payload copy
 // for every word on dense pages. Limits still apply before allocations.
 internal static class PdfTextCodec {
  const int Magic=0x31545850,HeaderBytes=28,MinimumWordBytes=41;
  static readonly Encoding Utf8=new UTF8Encoding(false,true);
  internal static byte[] Encode(PdfTextPage page){
   if(page==null||page.Index<0||page.Index>=PdfPreview.MaximumPages)throw Invalid();page.Validate(page.Index);
   using(var data=new MemoryStream())using(var writer=new BinaryWriter(data)){
    writer.Write(Magic);writer.Write(page.Index);writer.Write(page.Width);writer.Write(page.Height);writer.Write(page.Words.Length);
    byte[] buffer=new byte[1024];
    foreach(var word in page.Words){
     int count=Utf8.GetByteCount(word.Text);if(count<1||data.Length+40L+count>PdfTextWorker.MaximumOutput)throw Invalid();
     if(buffer.Length<count)buffer=new byte[count];Utf8.GetBytes(word.Text,0,word.Text.Length,buffer,0);
     writer.Write(count);writer.Write(buffer,0,count);writer.Write(word.X);writer.Write(word.Y);writer.Write(word.Width);writer.Write(word.Height);writer.Write(word.Line);
    }
    writer.Flush();return data.ToArray();
   }
  }
  internal static PdfTextPage Decode(byte[] bytes,int expected){
   if(bytes==null||bytes.Length<HeaderBytes||bytes.Length>PdfTextWorker.MaximumOutput||expected<0||expected>=PdfPreview.MaximumPages)throw Invalid();
   try{using(var data=new MemoryStream(bytes,false))using(var reader=new BinaryReader(data)){
    if(reader.ReadInt32()!=Magic)throw Invalid();int index=reader.ReadInt32();double width=reader.ReadDouble(),height=reader.ReadDouble();int count=reader.ReadInt32();
    if(index!=expected||count<0||count>PdfTextWorker.MaximumWords||count>(bytes.Length-HeaderBytes)/MinimumWordBytes||!PdfTextPage.Finite(width)||!PdfTextPage.Finite(height)||width<=0||height<=0)throw Invalid();
    var words=new PdfTextWord[count];long characters=0;
    for(int i=0;i<count;i++){
     int length=reader.ReadInt32();if(length<1||length>4L*PdfTextWorker.MaximumCharacters||length>data.Length-data.Position-36)throw Invalid();
     string value=Utf8.GetString(bytes,checked((int)data.Position),length);data.Position+=length;characters+=value.Length;if(characters>PdfTextWorker.MaximumCharacters)throw Invalid();
     words[i]=new PdfTextWord{Text=value,X=reader.ReadDouble(),Y=reader.ReadDouble(),Width=reader.ReadDouble(),Height=reader.ReadDouble(),Line=reader.ReadInt32()};
    }
    if(data.Position!=data.Length)throw Invalid();var page=new PdfTextPage{Index=index,Width=width,Height=height,Words=words};page.Validate(expected);return page;
   }}catch(EndOfStreamException){throw Invalid();}catch(DecoderFallbackException){throw Invalid();}
  }
  static InvalidDataException Invalid(){return new InvalidDataException("PDF 文字层数据无效。");}
 }
}
