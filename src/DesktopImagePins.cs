using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing=System.Drawing;
using Imaging=System.Drawing.Imaging;

namespace MoriDesk {
 // Preview pixels, not the letterboxed viewport or its transparency checker.
 // The resident screenshot runtime remains the only owner of the pin index.
 internal static class DesktopImagePins {
  internal static Func<BitmapSource,Task> SinkForTests;
  static int placement;
  internal static System.Windows.Int32Rect InitialBounds(int width,int height,System.Drawing.Rectangle work,int cascade){
   ImagePinBridge.ValidateDimensions(width,height);double scale=Math.Min(1,Math.Min(Math.Min(720,Math.Max(80,work.Width-48))/(double)width,Math.Max(80,work.Height-72)/(double)height));int w=Math.Max(1,(int)Math.Round(width*scale)),h=Math.Max(1,(int)Math.Round(height*scale));int offset=24+(Math.Abs(cascade%8)*24);return new System.Windows.Int32Rect(work.Left+Math.Max(12,Math.Min(offset,work.Width-w-12)),work.Top+Math.Max(12,Math.Min(offset,work.Height-h-12)),w,h);
  }
  internal static void ShowOnDesktop(BitmapSource image){var screen=System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);QingJie.AppState.Pins.NewAt(image,InitialBounds(image.PixelWidth,image.PixelHeight,screen.WorkingArea,placement++));}
  internal static BitmapSource Snapshot(Drawing.Image image){
   if(image==null)throw new InvalidOperationException("请先等待图片加载完成。");
   ImagePinBridge.ValidateDimensions(image.Width,image.Height);
   var bitmap=image as Drawing.Bitmap;
   using(var copy=bitmap!=null?bitmap.Clone(new Drawing.Rectangle(0,0,image.Width,image.Height),Imaging.PixelFormat.Format32bppArgb):new Drawing.Bitmap(image.Width,image.Height,Imaging.PixelFormat.Format32bppArgb)){
    if(bitmap==null)using(var graphics=Drawing.Graphics.FromImage(copy)){graphics.CompositingMode=System.Drawing.Drawing2D.CompositingMode.SourceCopy;graphics.DrawImage(image,0,0,image.Width,image.Height);}
    var data=copy.LockBits(new Drawing.Rectangle(0,0,copy.Width,copy.Height),Imaging.ImageLockMode.ReadOnly,Imaging.PixelFormat.Format32bppArgb);
    try{var snapshot=BitmapSource.Create(copy.Width,copy.Height,96,96,PixelFormats.Bgra32,null,data.Scan0,checked(data.Stride*copy.Height),data.Stride);snapshot.Freeze();return snapshot;}finally{copy.UnlockBits(data);}
   }
  }
  internal static Task PinAsync(BitmapSource image){
   ImagePinBridge.ValidateDimensions(image.PixelWidth,image.PixelHeight);
   if(SinkForTests!=null)return SinkForTests(image);
   if(DeskRuntime.Started&&QingJie.AppState.Pins!=null){if(QingJie.AppState.Quitting)throw new InvalidOperationException("Nook 正在退出，请稍后重试。");ShowOnDesktop(image);return Task.FromResult(true);}
   return ImagePinBridge.SendAsync(Entry.DataRoot,image);
  }
  internal static async Task ShowAsync(Window owner,Drawing.Image image){
   try{await PinAsync(Snapshot(image));}
   catch(Exception error){if(owner==null||!owner.IsVisible)MessageBox.Show(error.Message,"未能钉在桌面");else MessageBox.Show(owner,error.Message,"未能钉在桌面");}
  }
 }
}
