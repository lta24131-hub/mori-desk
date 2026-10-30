using System;
using System.IO;
using System.Linq;
using Mori;
namespace MoriDesk {
 internal static class PdfTextCodecTests {
  static bool Rejected(byte[] bytes,int page=2){try{PdfTextCodec.Decode(bytes,page);return false;}catch(InvalidDataException){return true;}}
  static byte[] Change(byte[] source,int offset,byte[] value){var copy=(byte[])source.Clone();Buffer.BlockCopy(value,0,copy,offset,value.Length);return copy;}
  internal static void Run(Action<bool,string> check){
   var page=new PdfTextPage{Index=2,Width=600,Height=800,Words=new[]{new PdfTextWord{Text="中文 emoji 😀",X=.1,Y=.2,Width=.3,Height=.04,Line=0},new PdfTextWord{Text="next",X=.1,Y=.3,Width=.2,Height=.04,Line=1}}};
   byte[] bytes=PdfTextCodec.Encode(page);var decoded=PdfTextCodec.Decode(bytes,2);
   check(decoded.Range(0,1)==page.Range(0,1)&&decoded.Width==600&&decoded.Words[0].Bounds==page.Words[0].Bounds,"binary text transport retains Unicode, line order and exact page geometry");
   check(Rejected(null)&&Rejected(new byte[27])&&Rejected(new byte[PdfTextWorker.MaximumOutput+1]),"text transport rejects missing, short and oversized payloads");
   check(Rejected(Change(bytes,0,BitConverter.GetBytes(0)))&&Rejected(bytes,1),"text transport rejects wrong protocol and wrong page identity");
   check(Rejected(Change(bytes,24,BitConverter.GetBytes(int.MaxValue)))&&Rejected(Change(bytes,24,BitConverter.GetBytes(19000))),"forged word counts are rejected before allocating an impossible word array");
   check(Rejected(Change(bytes,28,BitConverter.GetBytes(int.MaxValue)))&&Rejected(Change(bytes,28,BitConverter.GetBytes(-1))),"forged string lengths cannot allocate or read beyond the bounded payload");
   check(Rejected(Change(bytes,32,new byte[]{255}))&&Rejected(Change(bytes,8,BitConverter.GetBytes(double.NaN))),"invalid UTF-8 and nonfinite page dimensions are rejected");
   check(Rejected(bytes.Take(bytes.Length-1).ToArray())&&Rejected(bytes.Concat(new byte[]{0}).ToArray()),"truncated and trailing transport data are rejected");
   check(Rejected(Change(bytes,bytes.Length-4,BitConverter.GetBytes(-1))),"transport retains validation of negative line indices");
   page.Words=new PdfTextWord[0];check(PdfTextCodec.Decode(PdfTextCodec.Encode(page),2).Words.Length==0,"scan pages preserve an empty text layer without fabricated words");
   page.Words=Enumerable.Range(0,19000).Select(i=>new PdfTextWord{Text="x",X=.1,Y=.1,Width=.01,Height=.01,Line=i/200}).ToArray();
   bytes=PdfTextCodec.Encode(page);decoded=PdfTextCodec.Decode(bytes,2);
   check(decoded.Words.Length==19000&&decoded.Range(0,18999)==page.Range(0,18999)&&bytes.Length<800000,"19000-word page retains all words with a transport payload below 800 KB");
   page.Words=new[]{new PdfTextWord{Text=new string('x',PdfTextWorker.MaximumCharacters+1),X=.1,Y=.1,Width=.1,Height=.1}};bool limited=false;try{PdfTextCodec.Encode(page);}catch(InvalidDataException){limited=true;}check(limited,"encoding preserves the existing page character limit");
  }
 }
}
