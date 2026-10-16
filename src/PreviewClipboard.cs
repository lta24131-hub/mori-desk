using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Drawing=System.Drawing;

namespace MoriDesk {
 internal static class PreviewClipboard {
  // Copy the decoded image, never the viewport, checkerboard or original path.
  // PNG retains alpha for compatible receivers; Bitmap supports other apps.
  internal static void Copy(Drawing.Image image,Action<DataObject,bool> write=null){
   if(image==null)throw new InvalidOperationException("请先等待图片加载完成。");
   if(image.Width<1||image.Height<1||(long)image.Width*image.Height>ImagePinBridge.MaximumPixels)throw new NotSupportedException("图片超过复制大小限制（3200 万像素），请先降低图片文件的像素尺寸后重试；缩放预览不会改变图片大小。");
   var snapshot=DesktopImagePins.Snapshot(image);
   using(var png=new MemoryStream()){
    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(snapshot));encoder.Save(png);png.Position=0;
    var data=new DataObject();data.SetImage(snapshot);data.SetData("PNG",png,false);
    // Flush while the PNG stream is alive so closing the preview afterwards
    // does not invalidate the user's clipboard. Never clear it preemptively.
    if(write==null)Clipboard.SetDataObject(data,true);else write(data,true);
   }
  }
  internal static string TryCopy(Drawing.Image image,Action<DataObject,bool> write=null){
   try{Copy(image,write);return "";}
   catch(ExternalException error){return error.ErrorCode==unchecked((int)0x800401D0)?"剪贴板暂时被其他程序占用，请稍后再次复制。":"未能复制图片："+error.Message;}
   catch(Exception error){return "未能复制图片："+error.Message;}
  }
  internal static void CopyForWindow(Window owner,Drawing.Image image){
   string failure=TryCopy(image);if(failure.Length==0)return;
   if(owner!=null&&owner.IsVisible)MessageBox.Show(owner,failure,"复制图片",MessageBoxButton.OK,MessageBoxImage.Information);
   else MessageBox.Show(failure,"复制图片",MessageBoxButton.OK,MessageBoxImage.Information);
  }
 }
}
