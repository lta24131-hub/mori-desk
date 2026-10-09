using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing=System.Drawing;
using Imaging=System.Drawing.Imaging;

namespace Mori {
 // GDI preserves animated GIF playback. WIC adds system-supported WebP/AVIF
 // without shipping another decoder/runtime or converting the saved original.
 sealed class PreviewImage : IDisposable {
  Stream stream;
  public Drawing.Image Image {get;private set;}
  public bool UsedSystemDecoder {get;private set;}
  PreviewImage(Stream stream,Drawing.Image image,bool fallback){this.stream=stream;Image=image;UsedSystemDecoder=fallback;}
  public static PreviewImage Open(string path){
   if(string.Equals(Path.GetExtension(path),".svg",StringComparison.OrdinalIgnoreCase)){int w,h;return new PreviewImage(null,FromBitmapSource(SvgRaster.Render(path,2048,out w,out h)),true);}
   var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
   try {
    try {return new PreviewImage(stream,Drawing.Image.FromStream(stream),false);}
    catch(ArgumentException){}catch(ExternalException){}catch(OutOfMemoryException){}
    stream.Position=0;
    var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.None);
    var image=FromBitmapSource(decoder.Frames[0]);
    stream.Dispose();return new PreviewImage(null,image,true);
   }catch{stream.Dispose();throw;}
  }
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
