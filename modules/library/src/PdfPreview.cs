using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Mori {
 internal sealed class PdfPagePreview {
  internal BitmapSource Bitmap;
  internal int PageCount,PageIndex,Width,Height;
 }
 internal struct PdfPageSize {
  internal readonly double Width,Height;
  internal PdfPageSize(double width,double height){Width=width;Height=height;}
 }
 // The UI owns only page geometry and bounded, frozen images. Windows' PDF
 // renderer lives in a reusable child without a WPF Application or window.
 internal sealed class PdfPreviewSession:IDisposable {
  readonly object sync=new object();
  readonly CancellationTokenSource lifetime=new CancellationTokenSource();
  readonly string path;readonly long length;readonly DateTime modified;readonly int requestedPage;
  readonly PdfPageSize[] sizes;
  readonly TaskCompletionSource<bool> completion=new TaskCompletionSource<bool>();
  PdfRenderConnection connection;bool disposed;int active;Task cleanup;
  internal PdfPreviewSession(string path,long length,DateTime modified,int requestedPage,PdfRenderConnection connection){this.path=path;this.length=length;this.modified=modified;this.requestedPage=requestedPage;this.connection=connection;sizes=connection.Sizes;}
  internal int PageCount{get{return sizes.Length;}}
  internal int WorkerId{get{lock(sync){return connection==null?0:connection.ProcessId;}}}
  internal PdfPageSize GetPageSize(int index){if(index<0||index>=sizes.Length)throw new ArgumentOutOfRangeException("index");lock(sync){if(disposed)throw new ObjectDisposedException("PdfPreviewSession");return sizes[index];}}
  internal Task<PdfPagePreview> RenderAsync(int page,int dimension,CancellationToken cancellation){
   PdfPreview.ValidateRender(page,dimension);if(page>=sizes.Length)throw new ArgumentOutOfRangeException("page");
   CancellationTokenSource linked;lock(sync){if(disposed)throw new ObjectDisposedException("PdfPreviewSession");linked=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token);active++;}
   return RenderCoreAsync(page,dimension,linked);
  }
  async Task<PdfPagePreview> RenderCoreAsync(int page,int dimension,CancellationTokenSource linked){
   bool entered=false;Task pending=null;PdfPagePreview result=null;ExceptionDispatchInfo failure=null;
   try{
    await PdfPreview.Workers.WaitAsync(linked.Token).ConfigureAwait(false);entered=true;
    result=await Task.Run(delegate{
     linked.Token.ThrowIfCancellationRequested();
     var info=PdfPreview.Source(path);if(info.Length!=length||info.LastWriteTimeUtc!=modified)throw new IOException("原文件已发生变化，请重新打开预览。");
     // Cancellation only discards a worker whose request already reached the
     // pipe. Queued/pre-cancelled requests preserve the reusable document.
     if(connection==null){
      var reopened=PdfRenderConnection.Open(path,requestedPage,linked.Token);
      bool same=reopened.Sizes.Length==sizes.Length;
      if(same)for(int i=0;i<sizes.Length;i++)if(sizes[i].Width!=reopened.Sizes[i].Width||sizes[i].Height!=reopened.Sizes[i].Height){same=false;break;}
      if(!same){reopened.Dispose();throw new IOException("原文件页面已发生变化，请重新打开预览。");}connection=reopened;
     }
     try{return connection.Render(page,dimension,linked.Token);}
     catch{connection.Dispose();connection=null;throw;}
    }).ConfigureAwait(false);
   }catch(Exception error){failure=ExceptionDispatchInfo.Capture(error);}finally{
    if(entered)PdfPreview.Workers.Release();linked.Dispose();
    lock(sync){active--;if(disposed&&active==0)pending=StartCleanup();}
   }
   if(pending!=null)await pending.ConfigureAwait(false);if(failure!=null)failure.Throw();return result;
  }
  Task StartCleanup(){
   if(cleanup==null){var old=connection;connection=null;cleanup=Task.Run(delegate{try{if(old!=null)old.Dispose();completion.TrySetResult(true);}catch(Exception error){completion.TrySetException(error);}finally{lifetime.Dispose();}});}return completion.Task;
  }
  public void Dispose(){lock(sync){if(disposed)return;disposed=true;}try{lifetime.Cancel();}catch(ObjectDisposedException){}lock(sync){if(active==0)StartCleanup();}}
  internal Task CloseAsync(){Dispose();return completion.Task;}
 }
 internal static class PdfPreview {
  internal const long MaximumInput=256L*1024*1024;
  internal const int MaximumDimension=2048,MaximumPages=20000;
  // Bound native work across all windows, thumbnails and canvas imports.
  internal static readonly SemaphoreSlim Workers=new SemaphoreSlim(1);
  internal static bool HasPdfHeader(string path){
   using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){
    byte[] bytes=new byte[1024];int used=0,n;while(used<bytes.Length&&(n=input.Read(bytes,used,bytes.Length-used))>0)used+=n;
    for(int i=0;i<=used-5;i++)if(bytes[i]==37&&bytes[i+1]==80&&bytes[i+2]==68&&bytes[i+3]==70&&bytes[i+4]==45)return true;return false;
   }
  }
  internal static FileInfo Source(string path){
   if(string.IsNullOrWhiteSpace(path))throw new ArgumentException("没有提供要预览的文件。","path");
   var info=new FileInfo(Path.GetFullPath(path));string ext=info.Extension;
   if(!string.Equals(ext,".pdf",StringComparison.OrdinalIgnoreCase)&&!string.Equals(ext,".ai",StringComparison.OrdinalIgnoreCase))throw new NotSupportedException("此预览入口只支持 PDF 和兼容 PDF 的 AI 文件。");
   if(!info.Exists)throw new FileNotFoundException("原文件不存在。",info.FullName);
   if(info.Length>MaximumInput)throw new NotSupportedException("此文档超过 256 MB，暂不生成内置预览。请用默认程序打开。");
   if(!HasPdfHeader(info.FullName))throw new NotSupportedException(string.Equals(ext,".ai",StringComparison.OrdinalIgnoreCase)?"此 AI 文件没有兼容 PDF 的预览。请在 Illustrator 中另存时开启“创建 PDF 兼容文件”，或用默认程序打开。":"此文件不是可识别的 PDF，可能损坏或格式不符。请用默认程序检查。");
   return info;
  }
  internal static void ValidateRender(int page,int dimension){if(page<0)throw new ArgumentOutOfRangeException("page");if(dimension<1||dimension>MaximumDimension)throw new ArgumentOutOfRangeException("dimension");}
  internal static BitmapSource Render(string path,int dimension,out int width,out int height){var page=RenderAsync(path,0,dimension,CancellationToken.None).GetAwaiter().GetResult();width=page.Width;height=page.Height;return page.Bitmap;}
  internal static async Task<PdfPagePreview> RenderAsync(string path,int page,int dimension,CancellationToken cancellation){
   ValidateRender(page,dimension);await Workers.WaitAsync(cancellation).ConfigureAwait(false);
   // A thumbnail/canvas request holds its slot through open, render AND close.
   // Releasing it after metadata would let queued thumbnails each start an
   // idle process while their page requests wait behind the remaining opens.
   try{return await Task.Run(delegate{
    cancellation.ThrowIfCancellationRequested();var info=Source(path);
    using(var worker=PdfRenderConnection.Open(info.FullName,page,cancellation))return worker.Render(page,dimension,cancellation);
   }).ConfigureAwait(false);}finally{Workers.Release();}
  }
  internal static Task<PdfPreviewSession> OpenAsync(string path,CancellationToken cancellation){return OpenCoreAsync(path,-1,cancellation);}
  static async Task<PdfPreviewSession> OpenCoreAsync(string path,int requestedPage,CancellationToken cancellation){
   await Workers.WaitAsync(cancellation).ConfigureAwait(false);
   try{return await Task.Run(delegate{
    cancellation.ThrowIfCancellationRequested();var info=Source(path);var worker=PdfRenderConnection.Open(info.FullName,requestedPage,cancellation);
    try{cancellation.ThrowIfCancellationRequested();return new PdfPreviewSession(info.FullName,info.Length,info.LastWriteTimeUtc,requestedPage,worker);}catch{worker.Dispose();throw;}
   }).ConfigureAwait(false);}finally{Workers.Release();}
  }
 }
}
