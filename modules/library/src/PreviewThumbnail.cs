using System;
using System.IO;
using System.Threading;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using Drawing=System.Drawing;
namespace Mori {
 // Shared with local shortcut tiles: no file-format lists or decoders diverge.
 internal static class PreviewThumbnail {
  internal static bool Supports(string path){string ext=Path.GetExtension(path);return MediaTypes.Pictures.Contains(ext)||MediaTypes.Documents.Contains(ext);}
  internal static BitmapSource Read(string path,int size,out int width,out int height){
   if(size<1||size>2048)throw new ArgumentOutOfRangeException("size");string ext=Path.GetExtension(path).ToLowerInvariant();width=height=0;
   if(ext==".psd"){
    // Photoshop's saved preview is independent of full-image depth and ZIP
    // compression. Prefer it for fast tiles; full preview keeps the composite.
    if(size<=512)using(var embedded=PsdEmbeddedThumbnail.Open(path,size,out width,out height))if(embedded!=null)return FromDrawing(embedded);
    try{using(var bitmap=PsdComposite.Open(path,size,size*size,CancellationToken.None,out width,out height))return FromDrawing(bitmap);}
    catch(Exception ex){if(!ImageCodec.IsDecodeFailure(ex)||!ImageCodec.Available)throw;return ImageCodec.Render(path,size,out width,out height);}
   }
   if(ext==".psb")return ImageCodec.Render(path,size,out width,out height);
   // PDF points are not raster pixels. Do not put page units into the library's
   // resolution fields; the page image itself still determines its thumbnail.
   if(ext==".pdf"||ext==".ai"){int pageWidth,pageHeight;return PdfPreview.Render(path,size,out pageWidth,out pageHeight);}
   if(ext==".svg")return SvgRaster.Render(path,size,out width,out height);
   if(DocumentContent.Supports(ext))return Document(path,size);
   if(!MediaTypes.Pictures.Contains(ext))throw new NotSupportedException("此文件暂不支持缩略图。");
   try{using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){
    if(stream.Length>256L*1024*1024)throw new NotSupportedException("图片超过 256 MB，暂不生成内置缩略图，请用默认程序打开。");
    var decoder=DecodeWic(stream,path,BitmapCreateOptions.DelayCreation);var frame=decoder.Frames[0];width=frame.PixelWidth;height=frame.PixelHeight;
    if(width<1||height<1||width>100000||height>100000)throw new NotSupportedException("图片尺寸超出安全范围。");
    if(FormatPreviewInfo.NeedsSystemImageCodec(path)&&frame.Thumbnail!=null)return ScaleEmbedded(frame.Thumbnail,size);
    if((long)width*height>32L*1024*1024)throw new NotSupportedException("此图像像素较大且没有可用的轻量缩略图，请用默认程序打开。");stream.Position=0;
    // Stream sources are already independent of the URI cache. IgnoreImageCache
    // asks .NET Framework WPF to evict a null URI and breaks ordinary PNG/JPEG.
    var raster=new BitmapImage();raster.BeginInit();raster.CacheOption=BitmapCacheOption.OnLoad;raster.StreamSource=stream;
    if(width>=height)raster.DecodePixelWidth=Math.Min(size,width);else raster.DecodePixelHeight=Math.Min(size,height);raster.EndInit();raster.Freeze();return raster;
   }}catch(Exception ex){if(!ImageCodec.IsDecodeFailure(ex)||!ImageCodec.Supports(path)||!ImageCodec.Available)throw;return ImageCodec.Render(path,size,out width,out height);}
  }
  internal static void ValidateEmbeddedSize(int width,int height){if(width<1||height<1||width>100000||height>100000||(long)width*height>4L*1024*1024)throw new NotSupportedException("内嵌预览尺寸过大，暂不生成轻量缩略图，请用默认程序打开。");}
  internal static BitmapSource ScaleEmbedded(BitmapSource embedded,int size){
   if(size<1||size>2048)throw new ArgumentOutOfRangeException("size");ValidateEmbeddedSize(embedded.PixelWidth,embedded.PixelHeight);double scale=Math.Min(1,size/(double)Math.Max(embedded.PixelWidth,embedded.PixelHeight));BitmapSource preview=scale<1?new TransformedBitmap(embedded,new ScaleTransform(scale,scale)):embedded;
   // CacheOption.None can retain the decoder stream even after Freeze. Copy only
   // the bounded raster while its source is open, never the original RAW pixels.
   var pixels=preview.Format==PixelFormats.Bgra32?preview:new FormatConvertedBitmap(preview,PixelFormats.Bgra32,null,0);int stride=checked(pixels.PixelWidth*4);var data=new byte[checked(stride*pixels.PixelHeight)];pixels.CopyPixels(data,stride,0);var result=BitmapSource.Create(pixels.PixelWidth,pixels.PixelHeight,96,96,PixelFormats.Bgra32,null,data,stride);result.Freeze();return result;
  }
  internal static BitmapDecoder DecodeWic(Stream stream,string path,BitmapCreateOptions options){
   try{return BitmapDecoder.Create(stream,options,BitmapCacheOption.None);}
   catch(NotSupportedException ex){if(FormatPreviewInfo.NeedsSystemImageCodec(path))throw CodecFailure(ex);throw;}
   catch(FileFormatException ex){if(FormatPreviewInfo.NeedsSystemImageCodec(path))throw CodecFailure(ex);throw;}
   catch(System.Runtime.InteropServices.COMException ex){if(FormatPreviewInfo.NeedsSystemImageCodec(path))throw CodecFailure(ex);throw;}
  }
  static NotSupportedException CodecFailure(Exception ex){return new NotSupportedException("Windows 尚未安装或无法使用此格式的图片解码组件，文件也可能损坏。可安装对应扩展或用默认程序打开。",ex);}
  internal static BitmapSource FromDrawing(Drawing.Bitmap bitmap){using(var stream=new MemoryStream()){bitmap.Save(stream,Drawing.Imaging.ImageFormat.Png);stream.Position=0;var result=new BitmapImage();result.BeginInit();result.CacheOption=BitmapCacheOption.OnLoad;result.StreamSource=stream;result.EndInit();result.Freeze();return result;}}
  static BitmapSource Document(string path,int size){
   var content=DocumentContent.Read(path);int w=Math.Max(1,(int)(size*.77)),h=size;
   using(var bitmap=new Drawing.Bitmap(w,h))using(var graphics=Drawing.Graphics.FromImage(bitmap)){
    graphics.Clear(Drawing.Color.FromArgb(250,250,248));graphics.TextRenderingHint=Drawing.Text.TextRenderingHint.AntiAliasGridFit;float scale=size/512f;
    using(var heading=new Drawing.Font("Segoe UI",Math.Max(4,15*scale),Drawing.FontStyle.Bold))using(var body=new Drawing.Font("Segoe UI",Math.Max(4,12*scale)))using(var ink=new Drawing.SolidBrush(Drawing.Color.FromArgb(54,59,65))){
     graphics.DrawString(content.Kind+" · 内容预览",heading,ink,new Drawing.PointF(26*scale,24*scale));
     graphics.DrawLine(Drawing.Pens.LightGray,26*scale,60*scale,w-26*scale,60*scale);
     string excerpt=content.Text.Length>2400?content.Text.Substring(0,2400):content.Text;
     graphics.DrawString(excerpt,body,ink,new Drawing.RectangleF(26*scale,78*scale,Math.Max(1,w-52*scale),Math.Max(1,h-104*scale)));
    }return FromDrawing(bitmap);
   }
  }
 }
}
