using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using MoriDesk;

// Real, hidden worker process tests. Only generated images and PDF files in an
// explicit fresh output directory are used; no UI, clipboard or library access.
internal static class IntegrationRunner {
 static int checks;
 static void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;Console.WriteLine("PASS "+message);}
 static string Hash(string path){using(var hash=SHA256.Create())using(var file=File.OpenRead(path))return BitConverter.ToString(hash.ComputeHash(file));}
 static void TwoPagePdf(string path){
  string red="1 0 0 rg\n0 0 144 96 re f\n",blue="0 0 1 rg\n0 0 144 96 re f\n";
  string[] objects={
   "<< /Type /Catalog /Pages 2 0 R >>",
   "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
   "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 144 96] /Contents 5 0 R /Resources << >> >>",
   "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 144 96] /Contents 6 0 R /Resources << >> >>",
   "<< /Length "+red.Length+" >>\nstream\n"+red+"endstream",
   "<< /Length "+blue.Length+" >>\nstream\n"+blue+"endstream"
  };
  var text=new StringBuilder("%PDF-1.4\n");var offsets=new int[objects.Length];
  for(int index=0;index<objects.Length;index++){offsets[index]=text.Length;text.Append((index+1)+" 0 obj\n"+objects[index]+"\nendobj\n");}
  int xref=text.Length;text.Append("xref\n0 "+(objects.Length+1)+"\n0000000000 65535 f \n");
  foreach(int offset in offsets)text.Append(offset.ToString("D10")+" 00000 n \n");
  text.Append("trailer\n<< /Size "+(objects.Length+1)+" /Root 1 0 R >>\nstartxref\n"+xref+"\n%%EOF\n");
  File.WriteAllText(path,text.ToString(),Encoding.ASCII);
 }
 public static int Main(string[] args){
  try{
   if(args.Length!=2)throw new ArgumentException("IntegrationRunner.exe <worker.exe> <new-output-directory>");
   string worker=Path.GetFullPath(args[0]),root=Path.GetFullPath(args[1]);
   if(Directory.Exists(root)||File.Exists(root))throw new IOException("Use a fresh output directory");
   Directory.CreateDirectory(root);
   string first=Path.Combine(root,"测试 素材.png"),second=Path.Combine(root,"second","测试 素材.png");
   Directory.CreateDirectory(Path.GetDirectoryName(second));
   using(var bitmap=new Bitmap(320,160,PixelFormat.Format32bppArgb)){
    for(int y=0;y<bitmap.Height;y++)for(int x=0;x<bitmap.Width;x++)bitmap.SetPixel(x,y,x<32?Color.Transparent:Color.FromArgb(255,(x*19+y)%256,(y*11+x)%256,(x*y)%256));
    bitmap.Save(first,ImageFormat.Png);bitmap.Save(second,ImageFormat.Png);
   }
   string original=Hash(first),sessions=Path.Combine(root,"sessions"),destination=Path.Combine(root,"results");
   var client=new FileConversionClient(worker,sessions,null,Path.Combine(root,"components"));
   var components=client.ListComponentsAsync(CancellationToken.None).GetAwaiter().GetResult();
   Check(components.Length==7&&components.All(item=>item.State=="missing"&&!item.CanUninstall),"real optional component list reaches the legacy host with seven missing purpose-based packs");
   Check(components.Single(item=>item.Id=="libreoffice").Dependencies.Contains("jre")&&components.All(item=>item.DownloadBytes>0&&item.InstalledBytes>0),"real component metadata includes download/disk sizes and package-owned dependencies");
   Check(!Directory.EnumerateFileSystemEntries(sessions).Any(),"real component-list host session cleans after the worker exits");
   string syntheticVideo=Path.Combine(root,"optional-video.webm");File.WriteAllText(syntheticVideo,"SYNTHETIC FORMAT PROBE ONLY");
   var optionalCaps=client.GetCapabilitiesAsync(new[]{syntheticVideo},CancellationToken.None).GetAwaiter().GetResult();
   Check(optionalCaps.MissingComponentIds.Contains("ffmpeg")&&optionalCaps.Targets.Length==0,"real missing video engine is actionable by the host without exposing unsupported conversion buttons");
   Check(client.ListComponentsAsync(CancellationToken.None).GetAwaiter().GetResult().All(item=>item.State=="missing"),"listing and probing a missing engine never silently install optional components");
   var caps=client.GetCapabilitiesAsync(new[]{first,second},CancellationToken.None).GetAwaiter().GetResult();
   Check(caps.Targets.Any(t=>t.Id=="jpg")&&caps.Targets.Any(t=>t.Id=="pdf"),"real worker capability routes reach .NET Framework host");
   Check(caps.Files.Length==2&&caps.Files[0].Source==first&&caps.Files[1].Source==second&&caps.Files.All(f=>f.Format=="png"),"real source capabilities retain input identities and formats");
   Check(!Directory.EnumerateFileSystemEntries(sessions).Any(),"capability-only session cleaned after process exit");
   var batch=client.ConvertAsync(new[]{first,second},"jpg",destination,90,null,CancellationToken.None).GetAwaiter().GetResult();
   Check(!batch.Cancelled&&batch.Files.Count==2&&batch.Files.All(f=>string.IsNullOrEmpty(f.Error)&&f.Outputs.Length==1),"real serial batch succeeds through contained hidden subprocesses");
   string[] outputs=batch.Files.SelectMany(f=>f.Outputs).ToArray();
   Check(outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count()==2&&outputs.All(File.Exists),"same-name sources produce separate output copies without overwriting");
   Check(Path.GetFileName(outputs[0])=="测试 素材.jpg"&&Path.GetFileName(outputs[1])=="测试 素材 (1).jpg","real results use readable Unicode source names instead of worker allocator prefixes");
   Check(outputs.All(p=>FileConversionClient.IsUnder(p,destination))&&!Directory.EnumerateFileSystemEntries(sessions).Any(),"only persistent output copies survive, temp sessions are cleaned");
   using(var jpeg=new Bitmap(outputs[0])){
    Check(jpeg.Width==320&&jpeg.Height==160,"JPEG output decodes with original dimensions");
    Color transparent=jpeg.GetPixel(4,80);Check(transparent.R>240&&transparent.G>240&&transparent.B>240,"transparent source becomes white in JPEG, not black");
   }
   Check(Hash(first)==original&&Hash(second)==original,"original bytes preserved across batch conversion");
   var mixedCaps=client.GetCapabilitiesAsync(new[]{first,outputs[0]},CancellationToken.None).GetAwaiter().GetResult();
   Check(mixedCaps.Files.Length==2&&mixedCaps.Files[0].Format=="png"&&mixedCaps.Files[1].Format=="jpg"&&mixedCaps.Targets.Any(target=>target.Id=="jpg"),"real PNG and JPG selection includes JPG when one input converts and the other is already that target");
   var low=client.ConvertAsync(new[]{first},"jpg",destination,20,null,CancellationToken.None).GetAwaiter().GetResult();
   Check(low.Files.Count==1&&low.Files[0].Outputs.Length==1&&new FileInfo(low.Files[0].Outputs[0]).Length<new FileInfo(outputs[0]).Length,"quality setting reaches worker and changes encoded output");
   var pdf=client.ConvertAsync(new[]{first},"pdf",destination,90,null,CancellationToken.None).GetAwaiter().GetResult();
   Check(pdf.Files.Count==1&&pdf.Files[0].Outputs.Length==1,"image to PDF through real worker");
   var pdfCaps=client.GetCapabilitiesAsync(pdf.Files[0].Outputs,CancellationToken.None).GetAwaiter().GetResult();
   Check(pdfCaps.Files[0].Format=="pdf"&&pdfCaps.Files[0].Targets.Contains("png")&&pdfCaps.Targets.First(t=>t.Id=="png").Warning.Contains("每页"),"real PDF per-page consequence remains available on the chosen target before conversion");
   var pages=client.ConvertAsync(pdf.Files[0].Outputs,"png",destination,90,null,CancellationToken.None).GetAwaiter().GetResult();
   Check(pages.Files.Count==1&&pages.Files[0].Outputs.Length==1,"generated PDF renders back to image through PDFium");
   Check(Path.GetFileName(pages.Files[0].Outputs[0])=="测试 素材.png","single-page conversion avoids accumulating nested internal prefixes or suffixes");
   using(var png=new Bitmap(pages.Files[0].Outputs[0]))Check(png.Width>0&&png.Height>0,"PDF page output is a decodable image");
   string multiPdf=Path.Combine(root,"项目说明.pdf");TwoPagePdf(multiPdf);string multiOriginal=Hash(multiPdf);
   var multi=client.ConvertAsync(new[]{multiPdf},"png",destination,90,null,CancellationToken.None).GetAwaiter().GetResult();
   Check(multi.Files.Count==1&&multi.Files[0].Outputs.Length==2&&multi.Files[0].Error==null,"one real PDF source can successfully produce multiple output files");
   Check(Path.GetFileName(multi.Files[0].Outputs[0])=="项目说明-001.png"&&Path.GetFileName(multi.Files[0].Outputs[1])=="项目说明-002.png","real multi-page results use readable source name and ordered page suffixes");
   using(var pageOne=new Bitmap(multi.Files[0].Outputs[0]))using(var pageTwo=new Bitmap(multi.Files[0].Outputs[1])){
    Color red=pageOne.GetPixel(pageOne.Width/2,pageOne.Height/2),blue=pageTwo.GetPixel(pageTwo.Width/2,pageTwo.Height/2);
    Check(red.R>240&&red.B<10&&blue.B>240&&blue.R<10,"page suffix order matches actual rendered content");
   }
   Check(Hash(multiPdf)==multiOriginal&&!Directory.EnumerateFileSystemEntries(sessions).Any(),"multi-page naming preserves PDF and cleans all worker staging");
   string corrupt=Path.Combine(root,"corrupt.png");File.WriteAllText(corrupt,"SYNTHETIC INVALID IMAGE");
   var partial=client.ConvertAsync(new[]{first,corrupt},"webp",destination,90,null,CancellationToken.None).GetAwaiter().GetResult();
   Check(partial.Files.Count==2&&partial.Files[0].Outputs.Length==1&&partial.Files[1].Outputs.Length==0&&!string.IsNullOrWhiteSpace(partial.Files[1].Error),"corrupt input reports per-file failure without discarding a completed conversion");
   Check(!Directory.EnumerateFileSystemEntries(sessions).Any(),"failed conversion is cleaned and not accepted as a result");
   using(var cancelled=new CancellationTokenSource()){
    cancelled.Cancel();var cancel=client.ConvertAsync(new[]{first},"jpg",Path.Combine(root,"not-created"),90,null,cancelled.Token).GetAwaiter().GetResult();
    Check(cancel.Cancelled&&cancel.Files.Count==0&&!Directory.Exists(Path.Combine(root,"not-created")),"already-cancelled batch starts no worker or destination");
   }
   Check(Hash(first)==original,"original stays unchanged after success, failure and cancellation");
   Console.WriteLine("PASS total="+checks+"; synthetic-only, no Nook entry point or desktop surface");return 0;
  }catch(Exception error){Console.Error.WriteLine(error);return 1;}
 }
}
