using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Mori {
 // PDF and PDF-compatible Illustrator previews are rasterized by Windows.
 // No HTML, JavaScript, macros, external URLs or linked files are executed.
 internal sealed class PdfPagePreview {
  internal BitmapSource Bitmap;
  internal int PageCount,PageIndex;
  // Native PDF page units, not the original artwork's raster resolution.
  internal int Width,Height;
 }
 internal struct PdfPageSize {
  internal readonly double Width,Height;
  internal PdfPageSize(double width,double height){Width=width;Height=height;}
 }
 // One source and native document per preview window, not per visible page.
 // Only small page dimensions are retained; raster pages belong to the UI's
 // bounded cache, never to this session.
 internal sealed class PdfPreviewSession : IDisposable {
  readonly object sync=new object();
  readonly CancellationTokenSource lifetime=new CancellationTokenSource();
  readonly PdfPageSize[] sizes;
  IRandomAccessStreamWithContentType source;
  PdfDocument document;
  bool disposed,released;
  int active;
  internal PdfPreviewSession(PdfDocument document,IRandomAccessStreamWithContentType source,PdfPageSize[] sizes){this.document=document;this.source=source;this.sizes=sizes;}
  internal int PageCount{get{return sizes.Length;}}
  internal PdfPageSize GetPageSize(int pageIndex){
   if(pageIndex<0||pageIndex>=sizes.Length)throw new ArgumentOutOfRangeException("pageIndex","此页不存在，请重新选择页码。");
   lock(sync){if(disposed)throw new ObjectDisposedException("PdfPreviewSession");return sizes[pageIndex];}
  }
  internal Task<PdfPagePreview> RenderAsync(int pageIndex,int maximumDimension,CancellationToken cancellation){
   PdfPreview.ValidateRender(pageIndex,maximumDimension);
   if(pageIndex>=sizes.Length)throw new ArgumentOutOfRangeException("pageIndex","此页不存在，请重新选择页码。");
   CancellationToken sessionToken;
   lock(sync){if(disposed)throw new ObjectDisposedException("PdfPreviewSession");active++;sessionToken=lifetime.Token;}
   // Native page acquisition and render start are also worker-only, even when
   // the shared gate is already free. Do not give Task.Run a cancellation token:
   // every scheduled call must enter finally and release its session reference.
   return Task.Run(async delegate{return await RenderCoreAsync(pageIndex,maximumDimension,cancellation,sessionToken).ConfigureAwait(false);});
  }
  async Task<PdfPagePreview> RenderCoreAsync(int pageIndex,int maximumDimension,CancellationToken cancellation,CancellationToken sessionToken){
   bool entered=false;
   try{
    using(var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellation,sessionToken)){
     await PdfPreview.Workers.WaitAsync(linked.Token).ConfigureAwait(false);entered=true;
     linked.Token.ThrowIfCancellationRequested();
     using(var page=document.GetPage((uint)pageIndex))return await PdfPreview.RenderPageAsync(page,sizes[pageIndex],sizes.Length,pageIndex,maximumDimension,linked.Token).ConfigureAwait(false);
    }
   }catch(OperationCanceledException){throw;}catch(ArgumentOutOfRangeException){throw;}catch(ObjectDisposedException){throw;}catch(NotSupportedException){throw;}
   catch(Exception error){throw PdfPreview.NativeError(error);}
   finally{
    if(entered)PdfPreview.Workers.Release();
    lock(sync){active--;if(disposed&&active==0)Release();}
   }
  }
  public void Dispose(){
   lock(sync){if(disposed)return;disposed=true;}
   // Cancellation callbacks may complete work synchronously; never hold sync
   // while canceling and never close resources still in use by Windows.
   try{lifetime.Cancel();}catch(ObjectDisposedException){}
   lock(sync){if(active==0)Release();}
  }
  void Release(){
   if(released)return;released=true;document=null;
   if(source!=null){source.Dispose();source=null;}lifetime.Dispose();
  }
 }
 internal static class PdfPreview {
  internal const long MaximumInput=256L*1024*1024;
  internal const int MaximumDimension=2048,MaximumPages=20000;
  const long MaximumRenderedBytes=24L*1024*1024;
  internal static readonly SemaphoreSlim Workers=new SemaphoreSlim(1);
  internal static bool HasPdfHeader(string path){
   using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))return Header(input);
  }
  static bool Header(Stream input){
   // The PDF header may be preceded by a short export preamble. Only inspect
   // the first kilobyte; sniffing never scans/copies a complete design file.
   byte[] bytes=new byte[1024];int used=0,n;while(used<bytes.Length&&(n=input.Read(bytes,used,bytes.Length-used))>0)used+=n;
   for(int i=0;i<=used-5;i++)if(bytes[i]==37&&bytes[i+1]==80&&bytes[i+2]==68&&bytes[i+3]==70&&bytes[i+4]==45)return true;
   return false;
  }
  static string FormatError(string path){return string.Equals(Path.GetExtension(path),".ai",StringComparison.OrdinalIgnoreCase)?"此 AI 文件没有兼容 PDF 的预览。请在 Illustrator 中另存时开启“创建 PDF 兼容文件”，或用默认程序打开。":"此文件不是可识别的 PDF，可能损坏或格式不符。请用默认程序检查。";}
  internal static BitmapSource Render(string path,int maximumDimension,out int width,out int height){
   var page=RenderAsync(path,0,maximumDimension,CancellationToken.None).GetAwaiter().GetResult();width=page.Width;height=page.Height;return page.Bitmap;
  }
  internal static NotSupportedException NativeError(Exception error){return new NotSupportedException("Windows 无法预览此 PDF / AI 文件，可能已加密、损坏或包含不受支持的内容。请用默认程序打开。",error);}
  internal static void ValidateRender(int pageIndex,int maximumDimension){
   if(pageIndex<0)throw new ArgumentOutOfRangeException("pageIndex","页码不能小于零。");
   if(maximumDimension<1||maximumDimension>MaximumDimension)throw new ArgumentOutOfRangeException("maximumDimension","预览边长必须在 1 到 2048 像素之间。");
  }
  internal static async Task<PdfPagePreview> RenderAsync(string path,int pageIndex,int maximumDimension,CancellationToken cancellation){
   ValidateRender(pageIndex,maximumDimension);
   // Gallery thumbnails still inspect only the requested page geometry; they
   // must not walk every page in a document merely to generate the first icon.
   using(var session=await Task.Run(async delegate{return await OpenCoreAsync(path,cancellation,pageIndex).ConfigureAwait(false);},cancellation).ConfigureAwait(false))return await session.RenderAsync(pageIndex,maximumDimension,cancellation).ConfigureAwait(false);
  }
  internal static Task<PdfPreviewSession> OpenAsync(string path,CancellationToken cancellation){
   // A free gate can complete synchronously: keep header checks and the native
   // metadata walk on a worker thread, never on the caller's UI thread.
   return Task.Run(async delegate{return await OpenCoreAsync(path,cancellation,-1).ConfigureAwait(false);},cancellation);
  }
  static async Task<PdfPreviewSession> OpenCoreAsync(string path,CancellationToken cancellation,int requestedPage){
   if(string.IsNullOrWhiteSpace(path))throw new ArgumentException("没有提供要预览的文件。","path");
   string file=Path.GetFullPath(path),ext=Path.GetExtension(file);
   if(!string.Equals(ext,".pdf",StringComparison.OrdinalIgnoreCase)&&!string.Equals(ext,".ai",StringComparison.OrdinalIgnoreCase))throw new NotSupportedException("此预览入口只支持 PDF 和兼容 PDF 的 AI 文件。");
   cancellation.ThrowIfCancellationRequested();await Workers.WaitAsync(cancellation).ConfigureAwait(false);
   IRandomAccessStreamWithContentType source=null;
   try{
    using(var input=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read)){
     if(input.Length>MaximumInput)throw new NotSupportedException("此文档超过 256 MB，暂不生成内置预览。请用默认程序打开。");
     if(!Header(input))throw new NotSupportedException(FormatError(file));
    }
    cancellation.ThrowIfCancellationRequested();
    // Native WinRT streams avoid SDK UnionMetadata framework adapters.
    var storage=await Wait(StorageFile.GetFileFromPathAsync(file),cancellation).ConfigureAwait(false);
    source=await Wait(storage.OpenReadAsync(),cancellation).ConfigureAwait(false);
    // Recheck the opened native stream, not only the earlier filesystem
    // snapshot; replacement between those two read-only opens must not bypass
    // the source-size cap before Windows allocates a native document.
    if(source.Size>(ulong)MaximumInput)throw new NotSupportedException("此文档超过 256 MB，暂不生成内置预览。请用默认程序打开。");
    cancellation.ThrowIfCancellationRequested();
    var document=await Wait(PdfDocument.LoadFromStreamAsync(source),cancellation).ConfigureAwait(false);
    cancellation.ThrowIfCancellationRequested();
    if(document.PageCount==0)throw new NotSupportedException("此文档没有可预览的页面。");
    if(document.PageCount>MaximumPages)throw new NotSupportedException("此文档超过 20,000 页，暂不生成内置预览。请用默认程序打开。");
    if(requestedPage>=document.PageCount)throw new ArgumentOutOfRangeException("pageIndex","此页不存在，请重新选择页码。");
    var sizes=new PdfPageSize[checked((int)document.PageCount)];
    int first=requestedPage<0?0:requestedPage,last=requestedPage<0?sizes.Length:requestedPage+1;
    for(int i=first;i<last;i++){
     cancellation.ThrowIfCancellationRequested();
     using(var page=document.GetPage((uint)i)){
      double width=page.Size.Width,height=page.Size.Height;
      if(double.IsNaN(width)||double.IsInfinity(width)||double.IsNaN(height)||double.IsInfinity(height)||width<=0||height<=0||width>int.MaxValue||height>int.MaxValue)throw new NotSupportedException("此文档页面尺寸无效，无法安全预览。");
      sizes[i]=new PdfPageSize(width,height);
     }
    }
    cancellation.ThrowIfCancellationRequested();
    var session=new PdfPreviewSession(document,source,sizes);source=null;return session;
   }catch(OperationCanceledException){throw;}catch(FileNotFoundException){throw;}catch(DirectoryNotFoundException){throw;}catch(UnauthorizedAccessException){throw;}catch(ArgumentOutOfRangeException){throw;}catch(NotSupportedException){throw;}
   catch(Exception error){throw NativeError(error);}
   finally{if(source!=null)source.Dispose();Workers.Release();}
  }
  internal static async Task<PdfPagePreview> RenderPageAsync(PdfPage page,PdfPageSize size,int count,int pageIndex,int maximumDimension,CancellationToken cancellation){
   // Vector content renders directly to bounded pixels, never to an enormous
   // native-page-size intermediate bitmap.
   double ratio=maximumDimension/Math.Max(size.Width,size.Height);
   uint width=(uint)Math.Max(1,Math.Min(maximumDimension,Math.Round(size.Width*ratio))),height=(uint)Math.Max(1,Math.Min(maximumDimension,Math.Round(size.Height*ratio)));
   var options=new PdfPageRenderOptions{DestinationWidth=width,DestinationHeight=height,BackgroundColor=Windows.UI.Color.FromArgb(255,255,255,255)};
   using(var rendered=new InMemoryRandomAccessStream()){
    await Wait(page.RenderToStreamAsync(rendered,options),cancellation).ConfigureAwait(false);cancellation.ThrowIfCancellationRequested();
    if(rendered.Size==0||rendered.Size>(ulong)MaximumRenderedBytes)throw new NotSupportedException("此页生成的预览过大或无效，请用默认程序打开。");
    using(var read=rendered.GetInputStreamAt(0))using(var reader=new DataReader(read)){
     uint sizeBytes=checked((uint)rendered.Size),received=await Wait(reader.LoadAsync(sizeBytes),cancellation).ConfigureAwait(false);
     if(received!=sizeBytes)throw new NotSupportedException("此页预览数据不完整，请用默认程序打开。");
     byte[] bytes=new byte[sizeBytes];reader.ReadBytes(bytes);
     using(var stream=new MemoryStream(bytes,false)){
      var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();
      if(bitmap.PixelWidth>maximumDimension||bitmap.PixelHeight>maximumDimension)throw new NotSupportedException("此页预览超出安全尺寸，请用默认程序打开。");
      cancellation.ThrowIfCancellationRequested();
      return new PdfPagePreview{Bitmap=bitmap,PageCount=count,PageIndex=pageIndex,Width=(int)Math.Max(1,Math.Round(size.Width)),Height=(int)Math.Max(1,Math.Round(size.Height))};
     }
    }
   }
  }
  // Keep the render slot and its streams alive until Windows actually reports
  // completion, including cancellation, rather than returning early while a
  // native renderer is still using a disposed page or a closed input stream.
  static async Task<T> Wait<T>(IAsyncOperation<T> operation,CancellationToken cancellation){
   var completion=new TaskCompletionSource<T>();
   operation.Completed=delegate(IAsyncOperation<T> op,AsyncStatus status){try{if(status==AsyncStatus.Canceled)completion.TrySetCanceled();else completion.TrySetResult(op.GetResults());}catch(Exception error){completion.TrySetException(error);}};
   using(cancellation.Register(delegate{try{operation.Cancel();}catch{}})){
    try{return await completion.Task.ConfigureAwait(false);}finally{operation.Close();}
   }
  }
  static async Task Wait(IAsyncAction operation,CancellationToken cancellation){
   var completion=new TaskCompletionSource<bool>();
   operation.Completed=delegate(IAsyncAction op,AsyncStatus status){try{if(status==AsyncStatus.Canceled)completion.TrySetCanceled();else{op.GetResults();completion.TrySetResult(true);}}catch(Exception error){completion.TrySetException(error);}};
   using(cancellation.Register(delegate{try{operation.Cancel();}catch{}})){
    try{await completion.Task.ConfigureAwait(false);}finally{operation.Close();}
   }
  }
 }
}
