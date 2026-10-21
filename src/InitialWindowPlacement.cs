using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MoriDesk {
 // Default-sized windows must remain operable on small screens and scaled
 // monitors. This does not persist a fitted size over the user's preferences.
 internal static class InitialWindowPlacement {
  internal static Rect Fit(Rect work,double width,double height) {
   if(work.IsEmpty||work.Width<=0||work.Height<=0)throw new ArgumentException("Invalid work area");
   double margin=Math.Min(8,Math.Min(work.Width,work.Height)/20);
   double w=Math.Min(width,work.Width-margin*2),h=Math.Min(height,work.Height-margin*2);
   return new Rect(work.Left+(work.Width-w)/2,work.Top+(work.Height-h)/2,w,h);
  }
  internal static void Attach(Window window) {
   EventHandler handler=null;handler=delegate {
    window.SourceInitialized-=handler;
    if(window.WindowState!=WindowState.Normal)return;
    var handle=new WindowInteropHelper(window).Handle;
    var screen=System.Windows.Forms.Screen.FromHandle(handle);
    var source=HwndSource.FromHwnd(handle);
    Matrix transform=source!=null&&source.CompositionTarget!=null?source.CompositionTarget.TransformFromDevice:Matrix.Identity;
    var area=screen.WorkingArea;
    var origin=transform.Transform(new Point(area.Left,area.Top));
    var size=transform.Transform(new Vector(area.Width,area.Height));
    var bounds=Fit(new Rect(origin.X,origin.Y,size.X,size.Y),window.Width,window.Height);
    window.MinWidth=Math.Min(window.MinWidth,bounds.Width);window.MinHeight=Math.Min(window.MinHeight,bounds.Height);
    window.Width=bounds.Width;window.Height=bounds.Height;
    window.WindowStartupLocation=WindowStartupLocation.Manual;window.Left=bounds.Left;window.Top=bounds.Top;
   };
   window.SourceInitialized+=handler;
  }
 }
}
