using System;
using System.Drawing;
using System.IO;
using System.Reflection;

namespace Mori {
 public static class PreviewRenderingTests {
  static bool Near(Color actual,Color expected){return Math.Abs(actual.R-expected.R)<5&&Math.Abs(actual.G-expected.G)<5&&Math.Abs(actual.B-expected.B)<5;}
  static void Mouse(System.Windows.Forms.Control control,string method,System.Windows.Forms.MouseButtons button,int x,int y,int clicks=1){typeof(System.Windows.Forms.Control).GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(control,new object[]{new System.Windows.Forms.MouseEventArgs(button,clicks,x,y,0)});}
  public static void Run(Action<bool,string> check){
   var single=PreviewWindow.ImageLayout(1);var singlePane=(System.Windows.Controls.DockPanel)single.Children[0];
   check(single.Children.Count==1&&singlePane.Children.Count==0,"single-image preview has no instructions or duplicate filename");
   check(singlePane.Margin==new System.Windows.Thickness(0),"single-image preview uses the complete client area without a text band");
   var comparison=PreviewWindow.ImageLayout(3);
   check(comparison.ColumnDefinitions.Count==3&&comparison.Children.Count==3,"minimal comparison preview retains independent image columns");
   foreach(System.Windows.Controls.DockPanel pane in comparison.Children)check(pane.Children.Count==0&&pane.Margin.Top==0,"comparison preview has no repeated title or top text band");
   // Render into a synthetic bitmap, never capture or interact with the desktop.
   using(var source=new Bitmap(200,100)){
    using(var graphics=Graphics.FromImage(source)){graphics.Clear(Color.Red);graphics.FillRectangle(Brushes.Blue,100,0,100,100);}
    var view=new PreviewViewport(200,100);view.Resize(200,100);
    using(var control=new PreviewPicture(source,view){Size=new Size(200,100),BackColor=Color.White})using(var output=new Bitmap(200,100)){
     control.DrawToBitmap(output,new Rectangle(0,0,200,100));
     check(Near(output.GetPixel(50,50),Color.Red)&&Near(output.GetPixel(150,50),Color.Blue),"preview fitted renderer displays image halves");
     view.ZoomAt(4,100,50);view.PanTo(0,0);control.DrawToBitmap(output,new Rectangle(0,0,200,100));
     check(Near(output.GetPixel(100,50),Color.Red),"pan renderer reveals left enlarged region");
     view.PanTo(-10000,-10000);control.DrawToBitmap(output,new Rectangle(0,0,200,100));
     check(Near(output.GetPixel(100,50),Color.Blue),"pan renderer reveals right enlarged region");
     view.ZoomAt(10,100,50);control.DrawToBitmap(output,new Rectangle(0,0,200,100));
     check(control.Width==200&&control.Height==100,"zoom retains viewport-sized render surface");
    }
   }
   using(var surface=new PreviewCanvas{Size=new Size(400,300)})using(var picture=new System.Windows.Forms.Control{Size=new Size(400,300)}){
    surface.Controls.Add(picture);var view=new PreviewViewport(1000,800);view.Resize(400,300);view.ZoomAt(4,200,150);double x=view.X,y=view.Y;int repaints=0;
    // Exercise the exact event subscriptions without moving/capturing the user's mouse.
    using(var drag=new PreviewDragController(surface,picture,view,delegate{repaints++;},false)){
     Mouse(picture,"OnMouseDown",System.Windows.Forms.MouseButtons.Left,100,100);
     Mouse(picture,"OnMouseMove",System.Windows.Forms.MouseButtons.None,175,140);
     Mouse(picture,"OnMouseUp",System.Windows.Forms.MouseButtons.Left,175,140);
     check(Math.Abs(view.X-x-75)<0.001&&Math.Abs(view.Y-y-40)<0.001&&repaints==1,"rapid down/move/up pans from event positions without global mouse state");
     check(!drag.IsDragging&&drag.EndDoubleClick(),"mouse release ends pan and suppresses accidental doubleclick close");
     x=view.X;y=view.Y;Mouse(picture,"OnMouseMove",System.Windows.Forms.MouseButtons.None,220,200);
     check(view.X==x&&view.Y==y&&repaints==1,"queued move after mouseup cannot keep panning");
     Mouse(picture,"OnMouseDown",System.Windows.Forms.MouseButtons.Left,100,100);drag.Cancel();Mouse(picture,"OnMouseMove",System.Windows.Forms.MouseButtons.Left,250,200);
     check(view.X==x&&view.Y==y&&!drag.IsDragging,"deactivation/capture cancellation stops queued movement");
     Mouse(picture,"OnMouseDown",System.Windows.Forms.MouseButtons.Left,100,100,2);Mouse(picture,"OnMouseMove",System.Windows.Forms.MouseButtons.Left,250,200);
     check(view.X==x&&view.Y==y&&!drag.IsDragging,"second doubleclick down never starts a pan");
     Mouse(picture,"OnMouseDown",System.Windows.Forms.MouseButtons.Right,100,100);Mouse(picture,"OnMouseMove",System.Windows.Forms.MouseButtons.Right,250,200);
     check(view.X==x&&view.Y==y&&!drag.IsDragging,"right mouse button does not pan");
    }
   }
   using(var source=new Bitmap(200,100))using(var surface=new PreviewCanvas{Size=new Size(400,300)}){
    var view=new PreviewViewport(200,100);view.Resize(400,300);
    using(var picture=new PreviewPicture(source,view){Size=new Size(400,300)}){
     surface.Controls.Add(picture);int closes=0;
     using(var drag=new PreviewDragController(surface,picture,view,delegate{},false)){
      PreviewWindow.AttachDoubleClickClose(picture,drag,delegate{closes++;});
      Mouse(picture,"OnMouseDoubleClick",System.Windows.Forms.MouseButtons.Left,200,150,2);
      check(closes==1,"doubleclick on fitted preview closes it");
      Mouse(picture,"OnMouseDoubleClick",System.Windows.Forms.MouseButtons.Left,5,5,2);
      check(closes==2,"doubleclick on preview letterbox closes it");
      view.ZoomAt(4,200,150);double zoom=view.DisplayWidth;
      Mouse(picture,"OnMouseDoubleClick",System.Windows.Forms.MouseButtons.Left,200,150,2);
      check(closes==3&&view.DisplayWidth==zoom,"doubleclick on enlarged preview closes rather than resetting zoom");
      Mouse(picture,"OnMouseDoubleClick",System.Windows.Forms.MouseButtons.Right,200,150,2);
      check(closes==3,"right doubleclick does not close preview");
      Mouse(picture,"OnMouseDown",System.Windows.Forms.MouseButtons.Left,100,100);
      Mouse(picture,"OnMouseMove",System.Windows.Forms.MouseButtons.Left,140,140);
      Mouse(picture,"OnMouseUp",System.Windows.Forms.MouseButtons.Left,140,140);
      Mouse(picture,"OnMouseDoubleClick",System.Windows.Forms.MouseButtons.Left,140,140,2);
      check(closes==3,"pan followed by a click cannot accidentally close preview");
     }
    }
   }
   // Two 1x1 GIF frames, red then blue. The stream stays alive just like preview.
   byte[] gif={71,73,70,56,57,97,1,0,1,0,128,0,0,255,0,0,0,0,255,
    33,255,11,78,69,84,83,67,65,80,69,50,46,48,3,1,0,0,0,
    33,249,4,4,10,0,0,0,44,0,0,0,0,1,0,1,0,0,2,2,68,1,0,
    33,249,4,4,10,0,0,0,44,0,0,0,0,1,0,1,0,0,2,2,76,1,0,59};
   using(var stream=new MemoryStream(gif))using(var image=Image.FromStream(stream)){
    check(ImageAnimator.CanAnimate(image)&&image.GetFrameCount(System.Drawing.Imaging.FrameDimension.Time)==2,"animated GIF fixture remains multi-frame");
    var view=new PreviewViewport(1,1);view.Resize(40,40);var control=new PreviewPicture(image,view){Size=new Size(40,40)};
    var handle=control.Handle;var animating=typeof(PreviewPicture).GetField("animating",BindingFlags.Instance|BindingFlags.NonPublic);
    check((bool)animating.GetValue(control),"preview starts GIF animation on native handle creation");
    bool sawBlue=false;using(var output=new Bitmap(40,40)){var timer=System.Diagnostics.Stopwatch.StartNew();while(timer.ElapsedMilliseconds<1500&&!sawBlue){System.Threading.Thread.Sleep(30);System.Windows.Forms.Application.DoEvents();control.DrawToBitmap(output,new Rectangle(0,0,40,40));var pixel=output.GetPixel(20,20);sawBlue=pixel.B>pixel.R+40;}}
    check(sawBlue,"preview renderer advances animated GIF frame");
    control.Dispose();check(!(bool)animating.GetValue(control),"preview disposal stops GIF animation callbacks");
   }
  }
 }
}
