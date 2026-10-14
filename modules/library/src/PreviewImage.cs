using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing=System.Drawing;
using Imaging=System.Drawing.Imaging;

namespace Mori {
 // GDI preserves animated GIF playback. WIC adds system-supported WebP/AVIF
 // without shipping another decoder/runtime or converting the saved original.
 sealed class PreviewImage : IDisposable {
  // A second window may be opened while the first is still decoding. Keep that
  // work bounded, and cancel queued jobs without touching the original file.
  static readonly SemaphoreSlim DecodeSlots=new SemaphoreSlim(2,2);
  Stream stream;
  public Drawing.Image Image {get;private set;}
  public bool UsedSystemDecoder {get;private set;}
  PreviewImage(Stream stream,Drawing.Image image,bool fallback){this.stream=stream;Image=image;UsedSystemDecoder=fallback;}
  internal static async Task<PreviewImage> OpenAsync(string path,CancellationToken cancellation){
   await DecodeSlots.WaitAsync(cancellation).ConfigureAwait(false);
   try{
    cancellation.ThrowIfCancellationRequested();
    var loaded=await Task.Run(()=>Open(path,cancellation)).ConfigureAwait(false);
    if(cancellation.IsCancellationRequested){loaded.Dispose();cancellation.ThrowIfCancellationRequested();}
    return loaded;
   }finally{DecodeSlots.Release();}
  }
  public static PreviewImage Open(string path){return Open(path,CancellationToken.None);}
  internal static PreviewImage Open(string path,CancellationToken cancellation){
   cancellation.ThrowIfCancellationRequested();
   string ext=Path.GetExtension(path);
   if(string.Equals(ext,".psd",StringComparison.OrdinalIgnoreCase)){
    try{return new PreviewImage(null,PsdComposite.Open(path,2048,4*1024*1024,cancellation),true);}
    catch(Exception ex){if(!ImageCodec.IsDecodeFailure(ex)||!ImageCodec.Available)throw;return Enhanced(path,cancellation);}
   }
   if(string.Equals(ext,".psb",StringComparison.OrdinalIgnoreCase))return Enhanced(path,cancellation);
   if(string.Equals(Path.GetExtension(path),".ai",StringComparison.OrdinalIgnoreCase)){var page=PdfPreview.RenderAsync(path,0,2048,cancellation).GetAwaiter().GetResult();return new PreviewImage(null,FromBitmapSource(page.Bitmap),true);}
   if(string.Equals(Path.GetExtension(path),".svg",StringComparison.OrdinalIgnoreCase)){int w,h;return new PreviewImage(null,FromBitmapSource(SvgRaster.Render(path,2048,out w,out h)),true);}
   var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
   try {
    if(FormatPreviewInfo.NeedsSystemImageCodec(path)){if(stream.Length>256L*1024*1024)throw new NotSupportedException("图片超过 256 MB，请用默认程序打开。");var system=PreviewThumbnail.DecodeWic(stream,path,BitmapCreateOptions.DelayCreation);var source=system.Frames[0];if((long)source.PixelWidth*source.PixelHeight>32L*1024*1024){if(source.Thumbnail==null)throw new NotSupportedException("图像像素过大且没有内嵌缩略图，请用默认程序打开。");var small=FromBitmapSource(source.Thumbnail);stream.Dispose();return new PreviewImage(null,small,true);}stream.Position=0;}
    try {return new PreviewImage(stream,Drawing.Image.FromStream(stream),false);}
    catch(ArgumentException){}catch(ExternalException){}catch(OutOfMemoryException){}
    stream.Position=0;
    var decoder=PreviewThumbnail.DecodeWic(stream,path,BitmapCreateOptions.PreservePixelFormat);
    var image=FromBitmapSource(decoder.Frames[0]);
    stream.Dispose();return new PreviewImage(null,image,true);
   }catch(Exception ex){stream.Dispose();if(!ImageCodec.IsDecodeFailure(ex)||!ImageCodec.Supports(path)||!ImageCodec.Available)throw;return Enhanced(path,cancellation);}
  }
  static PreviewImage Enhanced(string path,CancellationToken cancellation){int width,height;return new PreviewImage(null,FromBitmapSource(ImageCodec.Render(path,2048,cancellation,out width,out height)),true);}
  internal static Drawing.Bitmap FromBitmapSource(BitmapSource source){
   int width=source.PixelWidth,height=source.PixelHeight;
   if(width<1||height<1||(long)width*height>32L*1024*1024)throw new NotSupportedException("图片像素过大，请用默认程序打开。");
   var pixels=source.Format==PixelFormats.Bgra32?source:new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
   var bitmap=new Drawing.Bitmap(width,height,Imaging.PixelFormat.Format32bppArgb);
   try {
    var data=bitmap.LockBits(new Drawing.Rectangle(0,0,width,height),Imaging.ImageLockMode.WriteOnly,Imaging.PixelFormat.Format32bppArgb);
    try{pixels.CopyPixels(Int32Rect.Empty,data.Scan0,checked(data.Stride*height),data.Stride);}finally{bitmap.UnlockBits(data);}
    return bitmap;
   }catch{bitmap.Dispose();throw;}
  }
  public void Dispose(){if(Image!=null){Image.Dispose();Image=null;}if(stream!=null){stream.Dispose();stream=null;}}
 }
}
