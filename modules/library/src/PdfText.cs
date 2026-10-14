using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;

namespace Mori {
 // All coordinates are fractions of the displayed (cropped and rotated)
 // page, with a top-left origin. No PDF objects survive in the UI process.
 internal sealed class PdfTextWord {
  public string Text {get;set;}
  public double X {get;set;} public double Y {get;set;}
  public double Width {get;set;} public double Height {get;set;}
  public int Line {get;set;}
  internal Rect Bounds {get{return new Rect(X,Y,Width,Height);}}
 }
 internal sealed class PdfTextPage {
  public int Index {get;set;} public double Width {get;set;} public double Height {get;set;}
  public PdfTextWord[] Words {get;set;}
  // Include object/string/array overhead conservatively, not only characters.
  internal long Bytes {get{return 96+(Words==null?0:Words.Sum(w=>112L+2*w.Text.Length));}}
  internal string Range(int first,int last){
   if(Words==null||Words.Length==0||first<0||last<0)return "";
   int start=Math.Max(0,Math.Min(first,last)),end=Math.Min(Words.Length-1,Math.Max(first,last));
   var result=new StringBuilder();
   for(int i=start;i<=end;i++){if(i>start)result.Append(Words[i].Line==Words[i-1].Line?" ":Environment.NewLine);result.Append(Words[i].Text);}
   return result.ToString();
  }
  internal static bool Finite(double value){return !double.IsNaN(value)&&!double.IsInfinity(value);}
  internal void Validate(int expected){
   if(Index!=expected||!Finite(Width)||!Finite(Height)||Width<=0||Height<=0||Words==null||Words.Length>PdfTextWorker.MaximumWords)throw new InvalidDataException("PDF 文字层数据无效。");
   long characters=0;
   foreach(var word in Words){
    if(word==null||string.IsNullOrWhiteSpace(word.Text)||!Finite(word.X)||!Finite(word.Y)||!Finite(word.Width)||!Finite(word.Height)||word.X<0||word.Y<0||word.Width<=0||word.Height<=0||word.X+word.Width>1.000001||word.Y+word.Height>1.000001||word.Line<0)throw new InvalidDataException("PDF 文字坐标无效。");
    characters+=word.Text.Length;if(characters>PdfTextWorker.MaximumCharacters)throw new InvalidDataException("PDF 单页文字过多。");
   }
  }
  static Rect OrderBounds(PdfTextWord word,int rotation){
   // Inverse page rotation only for line ordering. The stored/hit-test bounds
   // remain in display coordinates, which PdfPig has already transformed.
   if(rotation==90)return new Rect(word.Y,1-word.X-word.Width,word.Height,word.Width);
   if(rotation==180)return new Rect(1-word.X-word.Width,1-word.Y-word.Height,word.Width,word.Height);
   if(rotation==270)return new Rect(1-word.Y-word.Height,word.X,word.Height,word.Width);
   return word.Bounds;
  }
  internal static PdfTextWord[] ReadingOrder(IEnumerable<PdfTextWord> source,int rotation=0){
   // Group overlapping baselines, not identical glyph tops (capital letters,
   // punctuation and descenders have different heights on the same line).
   var sorted=source.OrderBy(w=>{var b=OrderBounds(w,rotation);return b.Y+b.Height/2;}).ThenBy(w=>OrderBounds(w,rotation).X).ToArray();
   var lines=new List<List<PdfTextWord>>();double center=0,height=0;
   foreach(var word in sorted){var bounds=OrderBounds(word,rotation);double mid=bounds.Y+bounds.Height/2;
    if(lines.Count==0||Math.Abs(mid-center)>Math.Max(height,bounds.Height)*.65){lines.Add(new List<PdfTextWord>());center=mid;height=bounds.Height;}
    var line=lines[lines.Count-1];line.Add(word);center=(center*(line.Count-1)+mid)/line.Count;height=Math.Max(height,bounds.Height);
   }
   var result=new List<PdfTextWord>();for(int i=0;i<lines.Count;i++)foreach(var word in lines[i].OrderBy(w=>OrderBounds(w,rotation).X)){word.Line=i;result.Add(word);}return result.ToArray();
  }
 }
 internal sealed class PdfTextCache {
  internal const long Budget=4L*1024*1024;internal const int MaximumPages=8;
  sealed class Entry {internal PdfTextPage Page;internal long Touch;}
  readonly Dictionary<int,Entry> entries=new Dictionary<int,Entry>();long clock;
  internal long Bytes {get;private set;}internal int Count {get{return entries.Count;}}
  internal PdfTextPage Get(int index){Entry item;if(!entries.TryGetValue(index,out item))return null;item.Touch=++clock;return item.Page;}
  internal void Put(PdfTextPage page){TryPut(page,-1);}
  internal bool TryPut(PdfTextPage page,int priorityPage){
   long incoming=page.Bytes;if(incoming>Budget)return false;
   // A nearby page must not displace the page the user is actually reading.
   // If both cannot fit, keep the current page and retry the other on navigation.
   Entry priority;if(page.Index!=priorityPage&&entries.TryGetValue(priorityPage,out priority)&&priority.Page.Bytes+incoming>Budget)return false;
   Remove(page.Index);entries[page.Index]=new Entry{Page=page,Touch=++clock};Bytes+=incoming;
   while(Bytes>Budget||entries.Count>MaximumPages){int evict=entries.Where(e=>e.Key!=priorityPage).OrderBy(e=>e.Value.Touch).First().Key;Remove(evict);}
   return entries.ContainsKey(page.Index);
  }
  internal void Retain(IEnumerable<int> indices){var keep=new HashSet<int>(indices);foreach(int key in entries.Keys.ToArray())if(!keep.Contains(key))Remove(key);}
  void Remove(int index){Entry entry;if(entries.TryGetValue(index,out entry)){Bytes-=entry.Page.Bytes;entries.Remove(index);}}
  internal void Clear(){entries.Clear();Bytes=0;}
 }
 internal sealed class PdfTextSession:IDisposable {
  internal const int TimeoutMilliseconds=15000;
  internal const long MaximumWorkerMemory=256L*1024*1024;
  // One process globally, not a process for every page or every open window.
  internal static readonly SemaphoreSlim Workers=new SemaphoreSlim(1);
  readonly string path;readonly long length;readonly DateTime modified;
  readonly CancellationTokenSource lifetime=new CancellationTokenSource();readonly object sync=new object();bool closed;int active;
  internal PdfTextSession(string file){var info=PdfTextWorker.Source(file);path=info.FullName;length=info.Length;modified=info.LastWriteTimeUtc;}
  internal async Task<PdfTextPage> ReadAsync(int page,CancellationToken cancellation){
   if(page<0||page>=PdfPreview.MaximumPages)throw new ArgumentOutOfRangeException("page");
   CancellationTokenSource pending;lock(sync){if(closed)throw new OperationCanceledException(new CancellationToken(true));pending=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,cancellation);active++;}
   try{await Workers.WaitAsync(pending.Token).ConfigureAwait(false);try{pending.Token.ThrowIfCancellationRequested();return await Task.Run(()=>Run(page,pending.Token),pending.Token).ConfigureAwait(false);}finally{Workers.Release();}}
   finally{pending.Dispose();lock(sync){active--;if(closed&&active==0)lifetime.Dispose();}}
  }
  PdfTextPage Run(int page,CancellationToken cancellation){
   var info=PdfTextWorker.Source(path);if(info.Length!=length||info.LastWriteTimeUtc!=modified)throw new IOException("原文件已发生变化，请重新打开预览后选字。");
   var start=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--pdf-text-worker \""+path+"\" "+page){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true};
   using(var process=Process.Start(start)){
    var reading=Task.Run(delegate{using(var output=new MemoryStream()){var buffer=new byte[16384];int read;while((read=process.StandardOutput.BaseStream.Read(buffer,0,buffer.Length))>0){if(output.Length+read>PdfTextWorker.MaximumOutput)throw new InvalidDataException("PDF 文字输出过大。");output.Write(buffer,0,read);}return output.ToArray();}});
    var watch=Stopwatch.StartNew();
    try{
     while(!process.WaitForExit(40)){
      cancellation.ThrowIfCancellationRequested();
      if(reading.IsFaulted)reading.GetAwaiter().GetResult();
      long memory;try{process.Refresh();memory=process.PrivateMemorySize64;}catch(InvalidOperationException){if(process.HasExited)break;throw;}
      if(watch.ElapsedMilliseconds>TimeoutMilliseconds||memory>MaximumWorkerMemory)throw new NotSupportedException("此页文字较复杂，已停止提取；仍可浏览或使用截图翻译。");
     }
     cancellation.ThrowIfCancellationRequested();
     if(process.ExitCode!=0)throw new NotSupportedException("此页文字层无法提取；仍可浏览或使用截图翻译。");
     if(!reading.Wait(2000))throw new IOException("PDF 文字读取未完成。");
     byte[] bytes=reading.GetAwaiter().GetResult();if(bytes.Length==0)throw new InvalidDataException("PDF 文字输出为空。");
     var result=PdfTextWorker.Serializer().Deserialize<PdfTextPage>(Encoding.UTF8.GetString(bytes));if(result==null)throw new InvalidDataException("PDF 文字层数据无效。");result.Validate(page);return result;
    }finally{
     try{if(!process.HasExited){process.Kill();process.WaitForExit(2000);}}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
     try{reading.Wait(2000);}catch(AggregateException){}
    }
   }
  }
  public void Dispose(){lock(sync){if(closed)return;closed=true;}try{lifetime.Cancel();}catch(ObjectDisposedException){}lock(sync){if(active==0)lifetime.Dispose();}}
 }
 internal static class PdfTextWorker {
  internal const int MaximumLetters=40000,MaximumWords=20000,MaximumCharacters=150000,MaximumOutput=4*1024*1024;
  internal static JavaScriptSerializer Serializer(){return new JavaScriptSerializer{MaxJsonLength=MaximumOutput,RecursionLimit=16};}
  internal static FileInfo Source(string path){
   var info=new FileInfo(Path.GetFullPath(path));string extension=info.Extension;
   if(!string.Equals(extension,".pdf",StringComparison.OrdinalIgnoreCase)&&!string.Equals(extension,".ai",StringComparison.OrdinalIgnoreCase))throw new NotSupportedException("仅支持 PDF 或 PDF 兼容 AI 文字层。");
   if(!info.Exists)throw new FileNotFoundException("原文件不存在。",info.FullName);
   if(info.Length>PdfPreview.MaximumInput)throw new NotSupportedException("文件过大，文字层暂不能提取；仍可使用默认程序打开。");
   if(!PdfPreview.HasPdfHeader(info.FullName))throw new NotSupportedException("文件没有 PDF 文字层。");return info;
  }
  internal static PdfTextPage Extract(string path,int index){
   var info=Source(path);if(index<0||index>=PdfPreview.MaximumPages)throw new ArgumentOutOfRangeException("index");
   using(var document=UglyToad.PdfPig.PdfDocument.Open(info.FullName)){
    if(document.NumberOfPages>PdfPreview.MaximumPages||index>=document.NumberOfPages)throw new NotSupportedException("文档页数超出文字预览范围。");
    var page=document.GetPage(index+1);if(page.Letters.Count>MaximumLetters)throw new NotSupportedException("单页文字过多。");
    if(!PdfTextPage.Finite(page.Width)||!PdfTextPage.Finite(page.Height)||page.Width<=0||page.Height<=0)throw new InvalidDataException("PDF 页面尺寸无效。");
    var words=new List<PdfTextWord>();long characters=0;
    // PdfPig 0.1.16 applies CropBox translation and rotation to letters itself.
    // Its page dimensions are already display dimensions; rotating again or
    // subtracting the original CropBox would misalign rotated/cropped PDFs.
    // The default extractor assumes horizontal text and splits a rotated
    // "Hello" into five selectable glyphs. Use PdfPig's orientation-aware
    // grouping only when needed; ordinary/dense horizontal pages stay cheap.
    var extracted=page.Letters.Any(l=>l.TextOrientation!=UglyToad.PdfPig.Content.TextOrientation.Horizontal)?page.GetWords(new UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor.NearestNeighbourWordExtractor(new UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor.NearestNeighbourWordExtractor.NearestNeighbourWordExtractorOptions{MaxDegreeOfParallelism=1})):page.GetWords();
    foreach(var word in extracted){
     string value=new string(word.Text.Where(c=>!char.IsControl(c)).ToArray()).Trim();if(value.Length==0)continue;
     characters+=value.Length;if(characters>MaximumCharacters||words.Count>=MaximumWords)throw new NotSupportedException("单页文字过多。");
     var box=word.BoundingBox;var corners=new[]{box.BottomLeft,box.BottomRight,box.TopLeft,box.TopRight};
     double left=corners.Min(p=>p.X)/page.Width,right=corners.Max(p=>p.X)/page.Width,top=1-corners.Max(p=>p.Y)/page.Height,bottom=1-corners.Min(p=>p.Y)/page.Height;
     if(!PdfTextPage.Finite(left)||!PdfTextPage.Finite(right)||!PdfTextPage.Finite(top)||!PdfTextPage.Finite(bottom))continue;
     left=Math.Max(0,left);top=Math.Max(0,top);right=Math.Min(1,right);bottom=Math.Min(1,bottom);if(right<=left||bottom<=top)continue;
     words.Add(new PdfTextWord{Text=value,X=left,Y=top,Width=right-left,Height=bottom-top});
    }
    var result=new PdfTextPage{Index=index,Width=page.Width,Height=page.Height,Words=PdfTextPage.ReadingOrder(words,page.Rotation.Value)};result.Validate(index);return result;
   }
  }
  internal static int Run(string[] args){
   try{int index;if(args.Length!=3||args[0]!="--pdf-text-worker"||!int.TryParse(args[2],out index))return 2;var page=Extract(args[1],index);byte[] bytes=Encoding.UTF8.GetBytes(Serializer().Serialize(page));if(bytes.Length>MaximumOutput)return 2;using(var output=Console.OpenStandardOutput()){output.Write(bytes,0,bytes.Length);output.Flush();}return 0;}catch{return 2;}
  }
 }
}
