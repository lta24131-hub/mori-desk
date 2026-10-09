using System;

namespace Mori {
 public static class PreviewViewportTests {
  static bool Near(double left,double right){return Math.Abs(left-right)<0.00001;}
  public static void Run(Action<bool,string> check){
   var view=new PreviewViewport(1000,500);view.Resize(800,600);
   check(view.IsFit&&Near(view.Zoom,0.796)&&Near(view.X,2)&&Near(view.Y,101),"preview fit centers with proportional margins");
   check(!view.CanPan,"fitted image does not show pan interaction");
   double pixelX=(200-view.X)/view.Zoom,pixelY=(250-view.Y)/view.Zoom;
   view.ZoomAt(2,200,250);
   check(!view.IsFit&&view.CanPan,"enlarged image enables pan");
   check(Near((200-view.X)/view.Zoom,pixelX)&&Near((250-view.Y)/view.Zoom,pixelY),"wheel zoom preserves image point below cursor");
   double zoom=view.Zoom;view.PanTo(9999,9999);
   check(Near(view.X,0)&&Near(view.Y,0)&&Near(view.Zoom,zoom),"pan clamps top left without altering zoom");
   view.PanTo(-9999,-9999);
   check(Near(view.X,view.Width-view.DisplayWidth)&&Near(view.Y,view.Height-view.DisplayHeight),"pan clamps bottom right without blank overscroll");
   view.PanTo(-400,-100);pixelX=(view.Width/2-view.X)/view.Zoom;pixelY=(view.Height/2-view.Y)/view.Zoom;view.Resize(700,500);
   check(Near((view.Width/2-view.X)/view.Zoom,pixelX)&&Near((view.Height/2-view.Y)/view.Zoom,pixelY),"resize retains centered image point while zoomed");
   view.ZoomAt(0.0001,250,250);
   check(Near(view.Zoom,0.03)&&!view.CanPan&&Near(view.X,(view.Width-view.DisplayWidth)/2)&&Near(view.Y,(view.Height-view.DisplayHeight)/2),"zoomed out image stays centered at lower limit");
   view.Fit();view.Resize(500,300);
   check(view.IsFit&&Near(view.Zoom,0.496)&&Near(view.X,2)&&Near(view.Y,26),"fit follows subsequent window resize");
   view.ZoomAt(100000,250,150);check(view.Zoom<=12&&view.DisplayWidth<=60000,"maximum zoom keeps rendering dimensions bounded");
   var panorama=new PreviewViewport(4000,300);panorama.Resize(1000,800);panorama.ZoomAt(2,500,400);double centered=panorama.Y;panorama.PanTo(-400,-800);
   check(panorama.CanPan&&Near(panorama.Y,centered)&&panorama.X<0,"wide image pans only overflowing axis");
   var tiny=new PreviewViewport(1,1);tiny.Resize(800,600);double fitted=tiny.Zoom;tiny.ZoomAt(1.15,400,300);
   check(tiny.Zoom>=fitted,"wheel does not unexpectedly shrink tiny fitted image");
   var giant=new PreviewViewport(100000,50000);giant.Resize(800,600);fitted=giant.Zoom;giant.ZoomAt(1.15,400,300);
   check(Near(giant.Zoom,fitted*1.15),"giant scan zoom starts smoothly below normal minimum");
   giant.ZoomAt(100000,400,300);check(giant.DisplayWidth<=60000,"giant scan cannot create unbounded GDI control");
   double x=giant.X,y=giant.Y;zoom=giant.Zoom;giant.ZoomAt(double.NaN,10,10);giant.PanTo(double.PositiveInfinity,0);giant.Resize(double.NaN,100);
   check(giant.X==x&&giant.Y==y&&giant.Zoom==zoom,"invalid viewport input is ignored");
   view.Resize(0,0);view.Fit();check(view.Width==1&&view.Height==1&&view.DisplayWidth>=1&&view.DisplayHeight>=1,"minimized viewport remains finite and renderable");
  }
 }
}
