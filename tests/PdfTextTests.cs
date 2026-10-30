using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mori;

namespace MoriDesk {
 // Deterministic guard/metadata checks, also independently selectable by the
 // VM ScenarioRunner. No product process, document decoder, or window is used.
 internal static class WorkerDiagnosticsTests {
  const long MemoryLimit=256L*1024*1024;
  const string Prefix="Nook.Worker.";
  internal static void Run(Action<bool,string> check){
   foreach(var worker in new[]{new{Kind="svg",Timeout=8000L},new{Kind="pdf-text",Timeout=15000L}}){
    var boundary=new WorkerFailureDiagnostic(worker.Kind);
    check(!boundary.LimitReached(worker.Timeout,MemoryLimit,worker.Timeout,MemoryLimit),worker.Kind+" retains inclusive existing time and 256 MiB guard boundaries");
    var timeout=new WorkerFailureDiagnostic(worker.Kind);bool timedOut=timeout.LimitReached(worker.Timeout+1,MemoryLimit,worker.Timeout,MemoryLimit);
    check(timedOut&&timeout.Reason=="timeout"&&timeout.TimeoutExceeded&&!timeout.MemoryExceeded,worker.Kind+" distinguishes elapsed-limit stop from memory stop without extending its timeout");
    var memory=new WorkerFailureDiagnostic(worker.Kind);bool exceeded=memory.LimitReached(worker.Timeout,MemoryLimit+1,worker.Timeout,MemoryLimit);
    check(exceeded&&memory.Reason=="memory"&&!memory.TimeoutExceeded&&memory.MemoryExceeded,worker.Kind+" distinguishes the first private byte above the unchanged memory cap");
    var both=new WorkerFailureDiagnostic(worker.Kind);both.LimitReached(worker.Timeout+1,MemoryLimit+1,worker.Timeout,MemoryLimit);
    check(both.TimeoutExceeded&&both.MemoryExceeded,worker.Kind+" retains both limit flags when one poll observes simultaneous excesses");
    both.Observe(worker.Timeout+2,8);var error=new NotSupportedException("original user message");both.ParentKillRequested=true;both.Attach(error);
    check((long)error.Data[Prefix+"SampledPeakPrivateBytes"]==MemoryLimit+1&&(long)error.Data[Prefix+"ElapsedMs"]==worker.Timeout+2,worker.Kind+" diagnostics preserve the sampled private-byte peak and elapsed observation without treating it as lifetime RAM");
    check(error.Data[Prefix+"ExitCodeSigned"]==null&&error.Data[Prefix+"ExitCodeHex"]==null&&(bool)error.Data[Prefix+"ParentKillRequested"],worker.Kind+" parent termination records no fabricated child exit code");
   }
   var child=new WorkerFailureDiagnostic("svg"){Reason="child-exit",ExitCode=unchecked((int)0xC000000D)};var childError=new NotSupportedException("unchanged message");child.Attach(childError);
   check((int)childError.Data[Prefix+"ExitCodeSigned"]==-1073741811&&(string)childError.Data[Prefix+"ExitCodeHex"]=="0xC000000D"&&!(bool)childError.Data[Prefix+"ParentKillRequested"],"native/nonzero child exit preserves signed and hexadecimal code separately from parent-enforced termination");
   var source=new TaskCompletionSource<byte[]>();var rootCause=new InvalidDataException("synthetic stdout cap failure");source.SetException(rootCause);
   var output=new WorkerFailureDiagnostic("svg"){ExitCode=2};output.LimitReached(8001,MemoryLimit+1,8000,MemoryLimit);Exception caught=null;
   try{output.ThrowIfOutputFailed(source.Task);}catch(Exception error){caught=error;output.Attach(error);}
   check(ReferenceEquals(caught,rootCause)&&output.Reason=="stdout-failure"&&output.StdoutFaulted&&output.TimeoutExceeded&&output.MemoryExceeded,"already-faulted stdout wins over simultaneous guard limits and keeps its exact original exception");
   check((string)caught.Data[Prefix+"ExitCodeHex"]=="0x00000002"&&!(bool)caught.Data[Prefix+"ParentKillRequested"],"stdout failure also retains an already-observed nonzero child exit rather than losing either fact");
   string[] keys={"Kind","Reason","ElapsedMs","SampledPeakPrivateBytes","TimeoutExceeded","MemoryExceeded","ParentKillRequested","StdoutFaulted","ExitCodeSigned","ExitCodeHex"};
   check(caught.Data.Count==keys.Length&&keys.All(key=>caught.Data.Contains(Prefix+key))&&caught.Data.Values.Cast<object>().All(value=>!(value is string)||!((string)value).Contains("\\")&&!((string)value).Contains("/")&&!((string)value).Contains("synthetic")),"worker metadata contains only the ten fixed content-free fields, never source paths or exception contents");
   var pending=new TaskCompletionSource<byte[]>();var healthy=new WorkerFailureDiagnostic("pdf-text");healthy.ThrowIfOutputFailed(pending.Task);pending.SetResult(new byte[0]);healthy.ThrowIfOutputFailed(pending.Task);
   check(!healthy.StdoutFaulted&&healthy.Reason=="unknown","pending or completed stdout is never synchronously blocked or mislabeled as failed by the guard check");
  }
 }
 internal static class PdfTextTests {
  internal static byte[] Fixture(int rotation=0,bool cropped=false,bool text=true,int pages=2,int denseWords=0){
   var objects=new List<string>{"<< /Type /Catalog /Pages 2 0 R >>"};var children=new StringBuilder();for(int i=0;i<pages;i++)children.Append((4+i*2)+" 0 R ");
   objects.Add("<< /Type /Pages /Kids [ "+children+" ] /Count "+pages+" >>");objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
   for(int i=0;i<pages;i++){
    objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 "+(denseWords>0?"1600 200":"320 240")+"] "+(cropped?"/CropBox [50 30 270 220] ":"")+"/Rotate "+rotation+" /Resources << /Font << /F1 3 0 R >> >> /Contents "+(5+i*2)+" 0 R >>");
    string drawing=denseWords>0?DenseDrawing(denseWords):text?"0 0 0 rg BT /F1 18 Tf 70 180 Td (Hello world) Tj 0 -32 Td (Select this text) Tj ET\n":"0.3 0.5 0.7 rg 70 180 90 20 re f\n";
    objects.Add("<< /Length "+Encoding.ASCII.GetByteCount(drawing)+" >>\nstream\n"+drawing+"endstream");
   }
   using(var output=new MemoryStream()){
    Action<string> write=delegate(string value){byte[] bytes=Encoding.ASCII.GetBytes(value);output.Write(bytes,0,bytes.Length);};write("%PDF-1.4\n");var offsets=new List<long>{0};
    for(int i=0;i<objects.Count;i++){offsets.Add(output.Position);write((i+1)+" 0 obj\n"+objects[i]+"\nendobj\n");}
    long xref=output.Position;write("xref\n0 "+(objects.Count+1)+"\n0000000000 65535 f \n");for(int i=1;i<offsets.Count;i++)write(offsets[i].ToString("D10")+" 00000 n \n");write("trailer\n<< /Size "+(objects.Count+1)+" /Root 1 0 R >>\nstartxref\n"+xref+"\n%%EOF\n");return output.ToArray();
   }
  }
  static string DenseDrawing(int words){
   var drawing=new StringBuilder("0 0 0 rg BT /F1 1 Tf\n");
   for(int i=0;i<words;i++)drawing.Append("1 0 0 1 ").Append(10+(i%200)*7).Append(' ').Append((190-(i/200)*1.8).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" Tm (x) Tj\n");
   return drawing.Append("ET\n").ToString();
  }
  static void Until(Func<bool> condition,string description,int timeout=25000){
   if(condition())return;var frame=new DispatcherFrame();var watch=System.Diagnostics.Stopwatch.StartNew();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};timer.Tick+=delegate{if(condition()||watch.ElapsedMilliseconds>timeout)frame.Continue=false;};timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}if(!condition())throw new TimeoutException(description);
  }
  static void Await(Task task){Until(()=>task.IsCompleted,"PDF text operation did not settle");task.GetAwaiter().GetResult();}
  static bool Unlocked(string file){try{using(var stream=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None))return true;}catch(IOException){return false;}}
  static void Arrange(FrameworkElement view,double width=760,double height=500){view.Measure(new Size(width,height));view.Arrange(new Rect(0,0,width,height));view.UpdateLayout();}
  static bool VisibleInk(BitmapSource raster,PdfTextWord word){
   var bitmap=new FormatConvertedBitmap(raster,PixelFormats.Bgra32,null,0);int width=bitmap.PixelWidth,height=bitmap.PixelHeight;byte[] bytes=new byte[width*height*4];bitmap.CopyPixels(bytes,width*4,0);
   int left=Math.Max(0,(int)(word.X*width)-2),right=Math.Min(width,(int)Math.Ceiling((word.X+word.Width)*width)+2),top=Math.Max(0,(int)(word.Y*height)-2),bottom=Math.Min(height,(int)Math.Ceiling((word.Y+word.Height)*height)+2);
   int ink=0;for(int y=top;y<bottom;y++)for(int x=left;x<right;x++){int at=(y*width+x)*4;if(bytes[at]<160&&bytes[at+1]<160&&bytes[at+2]<160)ink++;}return ink>10;
  }
  internal static void Run(string root,Action<bool,string> check){
   WorkerDiagnosticsTests.Run(check);PdfTextCodecTests.Run(check);Directory.CreateDirectory(root);GeometryAndExtraction(root,check);Selection(check);Cache(check);Lifecycle(root,check);View(root,check);DenseView(root,check);
   check(!AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name.StartsWith("UglyToad.PdfPig",StringComparison.Ordinal)),"PDF text extraction keeps PdfPig assemblies outside the gallery process");
  }
  static void GeometryAndExtraction(string root,Action<bool,string> check){
   foreach(int rotation in new[]{0,90,180,270})foreach(bool cropped in new[]{false,true}){
    string file=Path.Combine(root,"text "+rotation+(cropped?" cropped":"")+".pdf");byte[] original=Fixture(rotation,cropped);File.WriteAllBytes(file,original);DateTime modified=File.GetLastWriteTimeUtc(file);
    PdfTextPage page;using(var session=new PdfTextSession(file))page=session.ReadAsync(0,CancellationToken.None).GetAwaiter().GetResult();
    check(page.Words.Length>=5&&page.Words.Any(w=>w.Text=="Hello")&&page.Words.Any(w=>w.Text=="world"),"isolated PDF text worker extracts words from "+rotation+" degree "+(cropped?"cropped":"full")+" page");
    check(page.Range(0,page.Words.Length-1)=="Hello world"+Environment.NewLine+"Select this text","copy order preserves original words and lines after crop and "+rotation+" degree rotation");
    PdfPagePreview raster;PdfPageSize display;using(var rendering=PdfPreview.OpenAsync(file,CancellationToken.None).GetAwaiter().GetResult()){display=rendering.GetPageSize(0);raster=rendering.RenderAsync(0,512,CancellationToken.None).GetAwaiter().GetResult();}
    // Windows uses 96-dpi page units while PdfPig uses 72-dpi PDF points.
    // Compare native doubles, not rounded integer metadata (320pt -> 427 DIPs).
    check(Math.Abs(page.Width/page.Height-display.Width/display.Height)<.00001,"text/display dimensions agree after crop and "+rotation+" degree rotation");
    check(page.Words.All(w=>VisibleInk(raster.Bitmap,w)),"word bounds overlap actual rendered ink after crop and "+rotation+" degree rotation");
    check(original.SequenceEqual(File.ReadAllBytes(file))&&modified==File.GetLastWriteTimeUtc(file)&&Unlocked(file),"text extraction leaves source bytes/date intact and releases its file lock");
   }
   string ai=Path.Combine(root,"text-artboards.ai");File.WriteAllBytes(ai,Fixture());using(var session=new PdfTextSession(ai))check(session.ReadAsync(1,CancellationToken.None).GetAwaiter().GetResult().Words.Any(w=>w.Text=="Hello"),"PDF-compatible AI exported page shares the text worker without converting source");
   string scan=Path.Combine(root,"scan.pdf");File.WriteAllBytes(scan,Fixture(0,false,false));using(var session=new PdfTextSession(scan))check(session.ReadAsync(0,CancellationToken.None).GetAwaiter().GetResult().Words.Length==0,"non-text/scanned page returns no invented OCR or fabricated selection");
  }
  static PdfTextPage Sample(int index=0){return new PdfTextPage{Index=index,Width=600,Height=800,Words=new[]{new PdfTextWord{Text="Hello",X=.1,Y=.1,Width=.12,Height=.03,Line=0},new PdfTextWord{Text="world",X=.24,Y=.1,Width=.12,Height=.03,Line=0},new PdfTextWord{Text="Second",X=.1,Y=.18,Width=.12,Height=.03,Line=1},new PdfTextWord{Text="line",X=.24,Y=.18,Width=.1,Height=.03,Line=1}}};}
  static void Selection(Action<bool,string> check){
   var page=Sample();var overlay=new PdfTextSelection();overlay.SetPage(page,true);Arrange(overlay,600,800);
   check(overlay.WordAt(new Point(85,88),false)==0&&overlay.WordAt(new Point(500,300),false)==-1,"text hit testing finds visible words but does not start selection in page whitespace");
   check(overlay.WordAt(new Point(900,180),true)==3,"drag endpoint outside the page selects the nearest final visible word");
   string selected=null;overlay.Changed+=delegate(PdfTextSelection source,string value){selected=value;};overlay.Select(0,1);check(selected=="Hello world"&&overlay.SelectedText==selected,"drag-select model produces copyable word range rather than click-only selection");
   overlay.Select(3,0);check(selected=="Hello world"+Environment.NewLine+"Second line","reverse drag covers multiple lines in geometric reading order");
   Arrange(overlay,1200,1600);check(overlay.WordAt(new Point(170,176),false)==0&&overlay.SelectedText==selected,"selection coordinates and text survive zoom without a per-word WPF text tree");
   overlay.SetActive(false);check(overlay.SelectedText==""&&!overlay.IsHitTestVisible,"turning off text mode clears selection and restores unimpeded page browsing");
   var ordered=PdfTextPage.ReadingOrder(new[]{new PdfTextWord{Text="b",X=.2,Y=.104,Width=.04,Height=.02},new PdfTextWord{Text="a",X=.1,Y=.10,Width=.04,Height=.03},new PdfTextWord{Text="next",X=.1,Y=.2,Width=.1,Height=.03}});
   check(ordered[0].Text=="a"&&ordered[1].Text=="b"&&ordered[0].Line==ordered[1].Line&&ordered[2].Line>ordered[1].Line,"variable glyph heights share a baseline while subsequent line stays separate");
   bool invalid=false;try{new PdfTextPage{Index=0,Width=1,Height=1,Words=new[]{new PdfTextWord{Text="unsafe",X=double.NaN,Width=1,Height=1}}}.Validate(0);}catch(InvalidDataException){invalid=true;}check(invalid,"worker output with non-finite geometry is rejected before WPF drawing");
  }
  static void Cache(Action<bool,string> check){
   var cache=new PdfTextCache();for(int i=0;i<20;i++)cache.Put(Sample(i));check(cache.Count==PdfTextCache.MaximumPages&&cache.Get(0)==null&&cache.Get(19)!=null&&cache.Bytes<PdfTextCache.Budget,"text cache evicts least-recently-used pages within its count and byte budget");
   cache.Retain(new[]{19});check(cache.Count==1,"scrolling releases offscreen text pages");cache.Clear();check(cache.Count==0&&cache.Bytes==0,"closing clears all cached text content");
   var large=Sample();large.Words=Enumerable.Range(0,5000).Select(i=>new PdfTextWord{Text=new string('x',500),X=.1,Y=.1,Width=.1,Height=.1}).ToArray();cache.Put(large);check(cache.Count==0&&cache.Bytes==0,"one over-budget page cannot make the cache exceed its memory accounting limit");
   var denseWords=Enumerable.Range(0,20000).Select(i=>new PdfTextWord{Text="x",X=.1,Y=.1,Width=.01,Height=.01}).ToArray();
   for(int i=0;i<3;i++)cache.Put(new PdfTextPage{Index=i,Width=600,Height=800,Words=denseWords});
   check(cache.Get(0)==null&&cache.Get(1)==null&&cache.Get(2)!=null&&cache.Count==1&&cache.Bytes<=PdfTextCache.Budget,"multiple maximum-density text pages evict old entries rather than exceeding the byte budget");
   cache.Clear();var current=new PdfTextPage{Index=0,Width=600,Height=800,Words=denseWords};var adjacent=new PdfTextPage{Index=1,Width=600,Height=800,Words=denseWords};
   check(cache.TryPut(current,0)&&!cache.TryPut(adjacent,0)&&cache.Get(0)==current&&cache.Get(1)==null,"a dense adjacent page cannot evict the current page when both exceed the combined budget");
   check(cache.TryPut(adjacent,1)&&cache.Get(1)==adjacent&&cache.Get(0)==null&&cache.Bytes<=PdfTextCache.Budget,"navigating to the adjacent dense page promotes it and evicts the old page within the same budget");
  }
  static void DenseView(string root,Action<bool,string> check){
   string file=Path.Combine(root,"dense-visible-pages.pdf");File.WriteAllBytes(file,Fixture(0,false,true,5,19000));
   using(var view=new PdfContinuousView()){
    Arrange(view,760,220);Await(view.OpenAsync(file,CancellationToken.None));Arrange(view,760,220);view.SetTextSelection(true);
    Until(()=>view.IsTextReady(0)&&view.IsTextMemoryLimited(1),"dense adjacent page did not settle within the text cache budget",45000);
    check(view.CurrentPage==0&&view.IsTextReady(0)&&!view.IsTextMemoryLimited(0)&&view.TextCachedPages==1&&view.TextCachedBytes<=PdfTextCache.Budget,"real dense PDF keeps the current page selectable when its adjacent page cannot also fit");
    view.SelectTextForTest(0,0,1);check(view.SelectedText=="x x","dense current page still supports word selection after adjacent-page extraction");
    view.GoTo(1);view.UpdateLayout();Until(()=>view.CurrentPage==1&&view.IsTextReady(1)&&view.IsTextMemoryLimited(0),"navigation did not retry the formerly memory-limited dense page",45000);
    check(!view.IsTextReady(0)&&!view.IsTextMemoryLimited(1)&&view.TextCachedPages==1&&view.TextCachedBytes<=PdfTextCache.Budget,"real viewer navigation automatically retries and prioritizes the newly current dense page without toggling text mode");
    view.GoTo(2);view.UpdateLayout();Until(()=>view.CurrentPage==2&&view.IsTextReady(2),"second dense-page navigation did not retry text",45000);
    check(view.TextCachedPages==1&&view.TextCachedBytes<=PdfTextCache.Budget,"repeated dense-page navigation keeps one useful text layer inside the unchanged four MiB budget");
   }
   Until(()=>Unlocked(file),"closed dense text viewer retained its source lock");check(PdfTextSession.Workers.CurrentCount==1,"closing the dense text viewer releases its source and isolated-worker slot");
  }
  static void Lifecycle(string root,Action<bool,string> check){
   string file=Path.Combine(root,"cancel-worker.pdf");File.WriteAllBytes(file,Fixture());
   using(var session=new PdfTextSession(file)){
    using(var canceled=new CancellationTokenSource()){canceled.Cancel();bool caught=false;try{session.ReadAsync(0,canceled.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){caught=true;}check(caught&&PdfTextSession.Workers.CurrentCount==1,"pre-cancelled text request launches no process and consumes no worker slot");}
    PdfTextSession.Workers.Wait();try{using(var canceled=new CancellationTokenSource()){var queued=session.ReadAsync(0,canceled.Token);canceled.Cancel();bool caught=false;try{queued.GetAwaiter().GetResult();}catch(OperationCanceledException){caught=true;}check(caught,"queued text request cancels promptly without waiting for the held worker gate");}}finally{PdfTextSession.Workers.Release();}
    using(var canceled=new CancellationTokenSource()){var active=session.ReadAsync(0,canceled.Token);canceled.CancelAfter(40);bool settled=false;try{settled=active.GetAwaiter().GetResult().Words.Length>0;}catch(OperationCanceledException){settled=true;}check(settled&&Unlocked(file)&&PdfTextSession.Workers.CurrentCount==1,"active text cancellation kills/settles the child and releases source lock and worker gate");}
   }
   var closing=new PdfTextSession(file);var request=closing.ReadAsync(0,CancellationToken.None);closing.Dispose();bool closed=false;try{request.GetAwaiter().GetResult();closed=Unlocked(file);}catch(OperationCanceledException){closed=Unlocked(file);}check(closed&&PdfTextSession.Workers.CurrentCount==1,"disposing a text session cancels current extraction without leaving a resident worker");
   bool rejected=false;using(var session=new PdfTextSession(file)){File.SetLastWriteTimeUtc(file,DateTime.UtcNow.AddMinutes(1));try{session.ReadAsync(0,CancellationToken.None).GetAwaiter().GetResult();}catch(IOException){rejected=true;}}check(rejected,"changed source is rejected instead of painting stale text over the old raster");
  }
  static void View(string root,Action<bool,string> check){
   string file=Path.Combine(root,"selectable-pages.pdf");File.WriteAllBytes(file,Fixture(0,false,true,20));
   using(var view=new PdfContinuousView()){
    Arrange(view);Await(view.OpenAsync(file,CancellationToken.None));Arrange(view);Until(()=>view.IsPageRendered(0),"default PDF raster did not render");
    check(!view.TextSelectionEnabled&&view.TextCachedPages==0,"opening a PDF does not parse text or allocate a background text worker until explicitly requested");
    view.SetTextSelection(true);Until(()=>view.IsTextReady(0),"on-demand PDF text layer failed");view.SelectTextForTest(0,0,1);check(view.SelectedText=="Hello world","visible page selection exposes text for copy and existing translation entry point");
    view.SetZoom(2);Arrange(view);check(view.SelectedText=="Hello world","re-rendering at a larger zoom retains the selected text");
    view.GoTo(19);view.UpdateLayout();Until(()=>view.IsTextReady(19),"last-page text layer failed");check(view.TextCachedPages<=PdfTextCache.MaximumPages&&view.TextCachedBytes<=PdfTextCache.Budget&&string.IsNullOrEmpty(view.SelectedText),"jumping far away releases previous-page selection and bounds nearby text cache");
    view.SetTextSelection(false);check(!view.TextSelectionEnabled&&string.IsNullOrEmpty(view.SelectedText),"done-selecting toggle returns to the existing continuous raster viewer");
   }
   Until(()=>Unlocked(file),"closed text viewer retained its source lock");check(true,"close with raster and text workers releases the original document after cleanup");
   using(var closing=new PdfContinuousView()){
    Arrange(closing);Await(closing.OpenAsync(file,CancellationToken.None));Arrange(closing);closing.SetTextSelection(true);
    // Start the real async pump before disposing, without showing a window or
    // giving its late-created metadata session a chance to be adopted on the UI.
    typeof(PdfContinuousView).GetMethod("PumpText",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(closing,null);closing.Dispose();
    Until(()=>!closing.TextWorkActive&&Unlocked(file)&&PdfTextSession.Workers.CurrentCount==1,"immediately closed text viewer did not release its late metadata session");
    check(closing.TextCachedPages==0&&closing.TextCachedBytes==0,"closing immediately after enabling text mode discards a late metadata session and releases worker/source state");
   }
   string scan=Path.Combine(root,"scan-view.pdf");File.WriteAllBytes(scan,Fixture(0,false,false));string status="";
   using(var view=new PdfContinuousView()){view.StatusChanged+=value=>status=value;Arrange(view);Await(view.OpenAsync(scan,CancellationToken.None));Arrange(view);view.SetTextSelection(true);Until(()=>view.IsTextReady(0),"scan text-layer detection failed");check(status.Contains("没有可选文字层")&&status.Contains("截图翻译")&&view.SelectedText=="","scan/outlined page gives explicit screenshot-translation guidance without silently running OCR");}
  }
 }
}
