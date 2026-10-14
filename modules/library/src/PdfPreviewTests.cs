using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

namespace Mori {
 internal static class PdfPreviewTests {
  static bool Reject(Action action){try{action();return false;}catch{return true;}}
  internal static byte[] Fixture(int pages,int width=320,int height=180){
   return Fixture(Enumerable.Repeat(width,pages).ToArray(),Enumerable.Repeat(height,pages).ToArray());
  }
  internal static byte[] Fixture(int[] widths,int[] heights){
   if(widths.Length!=heights.Length)throw new ArgumentException("Page dimension counts differ.");
   int pages=widths.Length;
   // A self-contained, uncompressed generated PDF. No user documents, fonts,
   // images, actions, annotations or network resources are used by tests.
   int total=2+pages*2;var objects=new List<string>();
   objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
   var children=new StringBuilder();for(int i=0;i<pages;i++)children.Append((3+i*2)+" 0 R ");
   objects.Add("<< /Type /Pages /Kids [ "+children+" ] /Count "+pages+" >>");
   for(int i=0;i<pages;i++){
    int content=4+i*2,width=widths[i],height=heights[i];
    objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 "+width+" "+height+"] /Resources << >> /Contents "+content+" 0 R >>");
    string drawing=(i%2==0?"0.25 0.48 0.80":"0.30 0.72 0.50")+" rg 0 0 "+width+" "+height+" re f\n";
    objects.Add("<< /Length "+Encoding.ASCII.GetByteCount(drawing)+" >>\nstream\n"+drawing+"endstream");
   }
   using(var output=new MemoryStream()){
    Action<string> write=delegate(string text){byte[] bytes=Encoding.ASCII.GetBytes(text);output.Write(bytes,0,bytes.Length);};
    write("%PDF-1.4\n");var offsets=new List<long>{0};
    for(int i=0;i<objects.Count;i++){offsets.Add(output.Position);write((i+1)+" 0 obj\n"+objects[i]+"\nendobj\n");}
    long xref=output.Position;write("xref\n0 "+(total+1)+"\n0000000000 65535 f \n");for(int i=1;i<offsets.Count;i++)write(offsets[i].ToString("D10")+" 00000 n \n");
    write("trailer\n<< /Size "+(total+1)+" /Root 1 0 R >>\nstartxref\n"+xref+"\n%%EOF\n");return output.ToArray();
   }
  }
  public static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);string file=Path.Combine(root,"生成文档 ' 预览.PDF");byte[] original=Fixture(2);File.WriteAllBytes(file,original);DateTime modified=File.GetLastWriteTimeUtc(file);
   check(PdfPreview.HasPdfHeader(file),"bounded PDF header sniff recognizes uppercase-extension generated PDF");
   var first=PdfPreview.RenderAsync(file,0,512,CancellationToken.None).GetAwaiter().GetResult();
   check(first.PageCount==2&&first.PageIndex==0&&first.Bitmap.IsFrozen,"PDF page preview carries page count, index and cross-thread frozen bitmap");
   check(first.Width>0&&first.Height>0&&first.Bitmap.PixelWidth<=512&&first.Bitmap.PixelHeight<=512,"PDF thumbnail remains within requested dimensions and reports native page units");
   var second=PdfPreview.RenderAsync(file,1,2048,CancellationToken.None).GetAwaiter().GetResult();
   check(second.PageCount==2&&second.PageIndex==1&&second.Bitmap.PixelWidth<=2048&&second.Bitmap.PixelHeight<=2048,"PDF navigation renders only the selected page with bounded output");
   byte[] pixelFirst=new byte[4],pixelSecond=new byte[4];
   var colorFirst=new System.Windows.Media.Imaging.FormatConvertedBitmap(first.Bitmap,System.Windows.Media.PixelFormats.Bgra32,null,0);var colorSecond=new System.Windows.Media.Imaging.FormatConvertedBitmap(second.Bitmap,System.Windows.Media.PixelFormats.Bgra32,null,0);
   colorFirst.CopyPixels(new System.Windows.Int32Rect(first.Bitmap.PixelWidth/2,first.Bitmap.PixelHeight/2,1,1),pixelFirst,4,0);colorSecond.CopyPixels(new System.Windows.Int32Rect(second.Bitmap.PixelWidth/2,second.Bitmap.PixelHeight/2,1,1),pixelSecond,4,0);
   check(!pixelFirst.SequenceEqual(pixelSecond),"different generated PDF pages have different rendered content");
   int width,height;var thumbnail=PdfPreview.Render(file,64,out width,out height);check(thumbnail.PixelWidth<=64&&thumbnail.PixelHeight<=64&&width==first.Width&&height==first.Height,"PDF sync thumbnail API applies bounds and retains native page units");
   check(Reject(()=>PdfPreview.RenderAsync(file,2,512,CancellationToken.None).GetAwaiter().GetResult()),"PDF page beyond page count is rejected without rendering");
   check(Reject(()=>PdfPreview.RenderAsync(file,-1,512,CancellationToken.None).GetAwaiter().GetResult())&&Reject(()=>PdfPreview.RenderAsync(file,0,2049,CancellationToken.None).GetAwaiter().GetResult()),"PDF negative page and oversized destination are rejected");
   using(var cancel=new CancellationTokenSource()){cancel.Cancel();bool canceled=false;try{PdfPreview.RenderAsync(file,0,512,cancel.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){canceled=true;}check(canceled,"pre-canceled PDF render allocates no native page");}
   var workers=(SemaphoreSlim)typeof(PdfPreview).GetField("Workers",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);workers.Wait();
   try{using(var queuedCancel=new CancellationTokenSource()){var queued=PdfPreview.RenderAsync(file,0,512,queuedCancel.Token);queuedCancel.Cancel();bool canceled=false;try{queued.GetAwaiter().GetResult();}catch(OperationCanceledException){canceled=true;}check(canceled,"queued PDF preview cancellation does not wait for or consume a native render slot");}}finally{workers.Release();}
   check(original.SequenceEqual(File.ReadAllBytes(file))&&modified==File.GetLastWriteTimeUtc(file),"PDF preview leaves original bytes and modification time untouched");
   using(var exclusive=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(exclusive.Length==original.Length,"completed PDF preview releases source stream/file lock");
   string ai=Path.Combine(root,"兼容导出.ai");File.WriteAllBytes(ai,original);check(PdfPreview.HasPdfHeader(ai)&&PdfPreview.RenderAsync(ai,0,512,CancellationToken.None).GetAwaiter().GetResult().PageCount==2,"PDF-compatible Illustrator export uses safe PDF rendering, not Illustrator or JavaScript");
   string postscript=Path.Combine(root,"传统导出.ai");File.WriteAllText(postscript,"%!PS-Adobe-3.0\n%%Creator: generated fixture\n",Encoding.ASCII);bool explained=false;try{PdfPreview.RenderAsync(postscript,0,512,CancellationToken.None).GetAwaiter().GetResult();}catch(NotSupportedException error){explained=error.Message.Contains("创建 PDF 兼容文件");}check(!PdfPreview.HasPdfHeader(postscript)&&explained,"PostScript AI export has explicit PDF-compatible export guidance");
   string corrupt=Path.Combine(root,"损坏.pdf");File.WriteAllText(corrupt,"%PDF-1.4\nnot actually a PDF",Encoding.ASCII);check(Reject(()=>PdfPreview.RenderAsync(corrupt,0,512,CancellationToken.None).GetAwaiter().GetResult()),"corrupt PDF fails gracefully without changing the original");
   string oversized=Path.Combine(root,"过大.pdf");using(var output=File.Create(oversized)){byte[] header=Encoding.ASCII.GetBytes("%PDF-1.4\n");output.Write(header,0,header.Length);output.SetLength(PdfPreview.MaximumInput+1);}check(Reject(()=>PdfPreview.RenderAsync(oversized,0,512,CancellationToken.None).GetAwaiter().GetResult()),"oversized PDF source rejected before native document allocation");
   // Keep the synthetic artifact small after checking the logical size cap.
   using(var output=new FileStream(oversized,FileMode.Open,FileAccess.Write))output.SetLength(16);
   string delayed=Path.Combine(root,"too-late.pdf");using(var output=File.Create(delayed)){output.Write(new byte[1024],0,1024);byte[] header=Encoding.ASCII.GetBytes("%PDF-1.4\n");output.Write(header,0,header.Length);}check(!PdfPreview.HasPdfHeader(delayed),"PDF header sniff never scans past first kilobyte");
   string huge=Path.Combine(root,"large-page.pdf");File.WriteAllBytes(huge,Fixture(1,10000,5000));var bounded=PdfPreview.RenderAsync(huge,0,128,CancellationToken.None).GetAwaiter().GetResult();check(bounded.Bitmap.PixelWidth<=128&&bounded.Bitmap.PixelHeight<=128&&bounded.Width>128,"huge PDF page rasterizes directly to bounded pixels, never to native page-sized bitmap");
   check(PdfPreview.RenderAsync(file,0,32,CancellationToken.None).GetAwaiter().GetResult().Bitmap.PixelWidth<=32,"failed/canceled renders release single-worker gate for subsequent valid preview");
   string mixed=Path.Combine(root,"mixed-pages.pdf");byte[] mixedBytes=Fixture(new[]{320,180,10000},new[]{180,320,5000});File.WriteAllBytes(mixed,mixedBytes);DateTime mixedModified=File.GetLastWriteTimeUtc(mixed);
   using(var session=PdfPreview.OpenAsync(mixed,CancellationToken.None).GetAwaiter().GetResult()){
    check(session.PageCount==3&&session.GetPageSize(0).Width>session.GetPageSize(0).Height&&session.GetPageSize(1).Height>session.GetPageSize(1).Width,"PDF session exposes all mixed-orientation page dimensions without rasterizing pages");
    var tail=session.RenderAsync(2,128,CancellationToken.None).GetAwaiter().GetResult();var head=session.RenderAsync(0,128,CancellationToken.None).GetAwaiter().GetResult();
    check(tail.PageCount==3&&tail.PageIndex==2&&tail.Width>128&&tail.Bitmap.PixelWidth<=128&&head.PageIndex==0&&head.Bitmap.IsFrozen,"PDF session reuses one native document for nonsequential bounded page renders");
    check(Reject(()=>session.GetPageSize(-1))&&Reject(()=>session.GetPageSize(3))&&Reject(()=>session.RenderAsync(3,128,CancellationToken.None).GetAwaiter().GetResult()),"PDF session validates metadata and render page bounds");
    using(var canceled=new CancellationTokenSource()){canceled.Cancel();bool caught=false;try{session.RenderAsync(0,128,canceled.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){caught=true;}check(caught&&session.RenderAsync(1,128,CancellationToken.None).GetAwaiter().GetResult().PageIndex==1,"canceled session page request leaves native document reusable");}
    check(mixedBytes.SequenceEqual(File.ReadAllBytes(mixed))&&mixedModified==File.GetLastWriteTimeUtc(mixed),"long-lived PDF session leaves source bytes and modified time untouched");
   }
   using(var exclusive=new FileStream(mixed,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(exclusive.Length==mixedBytes.Length,"disposed PDF session releases its reusable source stream");
   var queuedSession=PdfPreview.OpenAsync(mixed,CancellationToken.None).GetAwaiter().GetResult();workers.Wait();
   try{
    var queued=queuedSession.RenderAsync(0,512,CancellationToken.None);queuedSession.Dispose();bool canceled=false;try{queued.GetAwaiter().GetResult();}catch(OperationCanceledException){canceled=true;}
    check(canceled,"disposing PDF session cancels a queued native render without consuming the held global slot");
    check(Reject(()=>queuedSession.GetPageSize(0))&&Reject(()=>queuedSession.RenderAsync(0,128,CancellationToken.None).GetAwaiter().GetResult()),"disposed PDF session rejects further metadata and page access");
    using(var exclusive=new FileStream(mixed,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(exclusive.Length==mixedBytes.Length,"queued cancellation releases source after session operation completes");
    queuedSession.Dispose();
   }finally{workers.Release();}
   var activeSession=PdfPreview.OpenAsync(mixed,CancellationToken.None).GetAwaiter().GetResult();var activeRender=activeSession.RenderAsync(0,2048,CancellationToken.None);activeSession.Dispose();
   bool closedSafely=false;try{var completed=activeRender.GetAwaiter().GetResult();closedSafely=completed.Bitmap.IsFrozen;}catch(OperationCanceledException){closedSafely=true;}
   check(closedSafely,"closing a PDF session during native rendering either completes safely or cancels after native completion");
   using(var exclusive=new FileStream(mixed,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(exclusive.Length==mixedBytes.Length,"closing during rendering releases source after the render task settles");
   using(var session=PdfPreview.OpenAsync(ai,CancellationToken.None).GetAwaiter().GetResult())check(session.PageCount==2&&session.RenderAsync(1,128,CancellationToken.None).GetAwaiter().GetResult().PageIndex==1,"PDF-compatible AI session exposes and renders every exported page, not only its thumbnail");
   string many=Path.Combine(root,"many-pages.pdf");File.WriteAllBytes(many,Fixture(80));
   using(var session=PdfPreview.OpenAsync(many,CancellationToken.None).GetAwaiter().GetResult())check(session.PageCount==80&&session.GetPageSize(79).Width>0&&session.RenderAsync(79,32,CancellationToken.None).GetAwaiter().GetResult().PageIndex==79,"many-page document loads only dimensions and directly renders a distant page");
   string excessive=Path.Combine(root,"excessive-pages.pdf");File.WriteAllBytes(excessive,Fixture(PdfPreview.MaximumPages+1));bool pageLimit=false;
   try{using(var session=PdfPreview.OpenAsync(excessive,CancellationToken.None).GetAwaiter().GetResult()){};}catch(NotSupportedException error){pageLimit=error.Message.Contains("20,000")&&error.Message.Contains("默认程序");}
   check(pageLimit,"native page metadata allocation is bounded with explicit default-program guidance for excessive page counts");
   using(var exclusive=new FileStream(excessive,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(exclusive.Length>0,"page-count rejection closes native input stream");
   check(PdfPreview.RenderAsync(file,1,32,CancellationToken.None).GetAwaiter().GetResult().PageIndex==1,"session closure and page-count rejection leave global render gate usable");
  }
 }
}
