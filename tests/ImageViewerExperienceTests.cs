using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Threading;
using Mori;
using Forms=System.Windows.Forms;

namespace MoriDesk {
 internal static class ImageViewerExperienceTests {
  const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
  static T Field<T>(ImageBrowser w,string key){return (T)typeof(ImageBrowser).GetField(key,Hidden).GetValue(w);}
  static object Call(ImageBrowser w,string key,params object[] args){return typeof(ImageBrowser).GetMethod(key,Hidden).Invoke(w,args);}
  static void Pump(int ms){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};timer.Tick+=delegate{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
  static bool Until(Func<bool> predicate){var clock=System.Diagnostics.Stopwatch.StartNew();while(!predicate()&&clock.ElapsedMilliseconds<12000)Pump(20);return predicate();}
  static bool Ready(ImageBrowser w){return Field<PreviewImage>(w,"image")!=null&&!Field<bool>(w,"loading")&&!Field<bool>(w,"scanning");}
  [System.Runtime.InteropServices.DllImport("dwmapi.dll")]static extern int DwmFlush();
  static void Shot(ImageBrowser w,string name,List<object> shots){w.UpdateLayout();Pump(600);DwmFlush();using(var bmp=new Bitmap((int)w.ActualWidth,(int)w.ActualHeight))using(var g=Graphics.FromImage(bmp))using(var stream=new MemoryStream()){g.CopyFromScreen((int)w.Left,(int)w.Top,0,0,bmp.Size);bmp.Save(stream,ImageFormat.Jpeg);var shot=new{name=name,imageBase64=Convert.ToBase64String(stream.ToArray())};shots.Add(shot);File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,name+".json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(shot));}}
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);var shots=new List<object>();string oldTheme=Theme.Current;var main=Application.Current.MainWindow;
   // Synthetic detail target. No personal files or host desktop are accessed.
   for(int i=0;i<14;i++)using(var bmp=new Bitmap(1600,1000))using(var g=Graphics.FromImage(bmp))using(var font=new Font("Arial",36)){
    g.Clear(Color.FromArgb(26+i*5,42+i*5,70+i*4));for(int y=0;y<10;y++)for(int x=0;x<16;x++)using(var brush=new SolidBrush(Color.FromArgb(255,(x*17+i*11)%256,(y*23+60)%256,170)))g.FillRectangle(brush,x*100+8,y*100+8,84,84);g.DrawString("Nook / image "+(i+1),font,Brushes.White,50,40);bmp.Save(Path.Combine(root,"image-"+i.ToString("D2")+".png"),ImageFormat.Png);
   }
   var w=new ImageBrowser(Path.Combine(root,"image-03.png")){Width=900,Height=480,Left=0,Top=0,WindowStartupLocation=WindowStartupLocation.Manual};
   try{
    w.Show();w.Left=0;w.Top=0;check(Until(()=>Ready(w)),"viewer opens decoded picture and folder navigation");
    Pump(800);
    var host=Field<WindowsFormsHost>(w,"host");var viewport=Field<PreviewViewport>(w,"viewport");var picture=Field<PreviewPicture>(w,"picture");
    check(viewport.IsFit&&viewport.DisplayWidth<=viewport.Width&&viewport.DisplayHeight<=viewport.Height,"initial fit keeps the complete image inside the viewer");
    check(Field<Button>(w,"zoomButton").Content.ToString().EndsWith("%"),"actual zoom percentage is visible");
    check(Until(()=>Field<StackPanel>(w,"strip").Children.OfType<Button>().All(b=>((Grid)b.Content).Children.OfType<System.Windows.Controls.Image>().Single().Source!=null)),"visible thumbnail strip decodes its nearby files");
    check(Field<StackPanel>(w,"strip").Children.Count<=11,"thumbnails stay bounded when browsing larger folders");
    Shot(w,"viewer-dark",shots);
    double width=w.ActualWidth,height=w.ActualHeight,left=w.Left,top=w.Top;double area=host.ActualHeight;WindowState state=w.WindowState;
    w.SetFocusMode(true);Pump(80);check(w.ActualWidth==width&&w.ActualHeight==height&&w.Left==left&&w.Top==top&&w.WindowState==state,"focus mode never changes the window's size, placement or maximization state");
    check(Field<FrameworkElement>(w,"toolbar").Visibility==Visibility.Collapsed&&Field<FrameworkElement>(w,"filmstrip").Visibility==Visibility.Collapsed&&Field<FrameworkElement>(w,"footer").Visibility==Visibility.Collapsed,"focus mode hides all toolbars, thumbnails and status");
    check(host.ActualHeight>area+70&&viewport.IsFit&&viewport.DisplayWidth<=viewport.Width&&viewport.DisplayHeight<=viewport.Height,"image uses newly available space without crop or stretch");Shot(w,"viewer-focus",shots);
    Call(w,"HandleNativeKey",Forms.Keys.Escape);check(!Field<bool>(w,"focusMode")&&!Field<bool>(w,"closed")&&Field<FrameworkElement>(w,"toolbar").Visibility==Visibility.Visible,"Escape leaves focus mode without closing the picture");
    w.UpdateEdgeVisibility(20);check(Field<Forms.Button>(w,"edgePrevious").Visible&&!Field<Forms.Button>(w,"edgeNext").Visible,"left edge reveals only the previous-image control");
    w.UpdateEdgeVisibility(host.Child.ClientSize.Width-20);check(!Field<Forms.Button>(w,"edgePrevious").Visible&&Field<Forms.Button>(w,"edgeNext").Visible,"right edge reveals only the next-image control");
    var edge=Field<Forms.Button>(w,"edgeNext");check(edge.Region!=null&&!edge.Region.IsVisible(0,0)&&edge.Region.IsVisible(edge.Width/2,edge.Height/2),"edge buttons have rounded corners while keeping a solid clickable center");
    Forms.Cursor.Position=host.Child.PointToScreen(new System.Drawing.Point(host.Child.ClientSize.Width-28,host.Child.ClientSize.Height/2));Pump(60);
    Shot(w,"viewer-edge",shots);
    w.UpdateEdgeVisibility(host.Child.ClientSize.Width/2);check(!Field<Forms.Button>(w,"edgeNext").Visible&&!Field<Forms.Button>(w,"edgePrevious").Visible,"both edge controls disappear away from the edges");
    Forms.Cursor.Position=host.Child.PointToScreen(new System.Drawing.Point(host.Child.ClientSize.Width/2,host.Child.ClientSize.Height/2));Pump(60);
    w.SetEdgeNavigation(false);w.UpdateEdgeVisibility(20);check(!Field<Forms.Button>(w,"edgePrevious").Visible,"user can completely disable edge navigation icons");w.SetEdgeNavigation(true);
    typeof(Forms.Control).GetMethod("OnMouseDoubleClick",Hidden).Invoke(picture,new object[]{new Forms.MouseEventArgs(Forms.MouseButtons.Left,2,200,200,0)});
    check(!Field<bool>(w,"closed")&&Math.Abs(viewport.Zoom-1)<.0001,"double-click inspects original pixels instead of closing the viewer");
    typeof(Forms.Control).GetMethod("OnMouseDoubleClick",Hidden).Invoke(picture,new object[]{new Forms.MouseEventArgs(Forms.MouseButtons.Left,2,200,200,0)});check(viewport.IsFit,"second double-click restores complete-image fit");
    var slots=(System.Threading.SemaphoreSlim)typeof(PreviewImage).GetField("DecodeSlots",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);slots.Wait();slots.Wait();
    try{Call(w,"Step",1);Call(w,"Step",1);Call(w,"Step",1);check(Field<PreviewPicture>(w,"picture")==picture&&!picture.IsDisposed&&Field<string>(w,"currentPath").EndsWith("image-06.png"),"rapid navigation preserves the last picture while latest requested target waits");}finally{slots.Release(2);}
    check(Until(()=>Ready(w))&&w.Title=="image-06.png · Nook","rapid navigation settles on the last requested image");
    Call(w,"HandleNativeKey",Forms.Keys.Left);check(Until(()=>Ready(w))&&w.Title=="image-05.png · Nook","keyboard navigation still works after rapid cancellation");
    Theme.Set("light");Pump(80);Shot(w,"viewer-light",shots);w.Width=650;Pump(80);Shot(w,"viewer-narrow",shots);
    check(Field<FrameworkElement>(w,"toolbar").ActualHeight<55,"small window retains one compact toolbar row");
    var toolbar=Field<FrameworkElement>(w,"toolbar");var tools=VisualChildren<Button>(toolbar).ToArray();
    check(tools.All(b=>{var p=b.TranslatePoint(new System.Windows.Point(0,0),toolbar);return p.X>=0&&p.X+b.ActualWidth<=toolbar.ActualWidth+.5;}),"every toolbar action fits the narrow window without clipping");
    var pin=Field<Button>(w,"pinButton");var zoom=Field<Button>(w,"zoomButton");check(pin.TranslatePoint(new System.Windows.Point(0,0),toolbar).X>zoom.TranslatePoint(new System.Windows.Point(0,0),toolbar).X+100,"pin and more controls remain separated at the right of the toolbar");
    Call(w,"ToggleFilmstrip");check(Field<FrameworkElement>(w,"filmstrip").Visibility==Visibility.Collapsed,"thumbnail strip can be hidden separately");
    w.SetFocusMode(true);w.SetFocusMode(false);check(Field<FrameworkElement>(w,"filmstrip").Visibility==Visibility.Collapsed,"focus toggle preserves the user's hidden-thumbnail choice");
    w.SetImageSequence(new[]{Path.Combine(root,"image-10.png"),Path.Combine(root,"image-02.png"),Path.Combine(root,"image-03.png")},new[]{"项目第三张","项目第一张","项目第二张"});Call(w,"Open",Path.Combine(root,"image-02.png"));
    check(Until(()=>Ready(w))&&w.Title=="项目第一张 · Nook"&&Field<int>(w,"index")==1&&Field<string[]>(w,"siblings").Length==3,"library viewer preserves friendly names and the supplied filtered sequence");
    Call(w,"Step",1);check(Until(()=>Ready(w))&&w.Title=="项目第二张 · Nook","library navigation follows gallery order rather than unrelated folder files");
    host=Field<WindowsFormsHost>(w,"host");int edgeX=host.Child.ClientSize.Width-28,edgeY=host.Child.ClientSize.Height/2;Forms.Cursor.Position=host.Child.PointToScreen(new System.Drawing.Point(edgeX,edgeY));Pump(100);
    // SetCursorPos alone is not guaranteed to deliver WM_MOUSEMOVE in a nested
    // dispatcher test loop. Deliver that native control event explicitly.
    typeof(Forms.Control).GetMethod("OnMouseMove",Hidden).Invoke(Field<PreviewPicture>(w,"picture"),new object[]{new Forms.MouseEventArgs(Forms.MouseButtons.None,0,edgeX,edgeY,0)});
    check(Field<Forms.Button>(w,"edgeNext").Visible,"native picture mouse move exposes the arrow before clicking");
    string edgeBefore=EdgeState(w);Field<Forms.Button>(w,"edgeNext").PerformClick();bool turned=Until(()=>Ready(w));check(turned&&w.Title=="项目第三张 · Nook"&&Field<Forms.Button>(w,"edgeNext").Visible,"edge navigation remains visible under a stationary pointer after turning a page / before "+edgeBefore+" / after "+EdgeState(w));
    Field<Forms.Button>(w,"edgeNext").PerformClick();check(Until(()=>Ready(w))&&w.Title=="项目第一张 · Nook","stationary pointer can click the new edge button again without leaving and re-entering / "+EdgeState(w));
    w.Close();check(Field<bool>(w,"closed")&&Field<WindowsFormsHost>(w,"host")==null&&Field<StackPanel>(w,"strip").Children.Count==0,"close releases native viewer, images and thumbnail UI");
   }finally{w.Close();Pump(100);Theme.Set(oldTheme);Application.Current.MainWindow=main;File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"viewer-shots.json"),new System.Web.Script.Serialization.JavaScriptSerializer{MaxJsonLength=32*1024*1024}.Serialize(shots));}
  }
  static IEnumerable<T> VisualChildren<T>(DependencyObject parent)where T:DependencyObject{for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);i++){var child=System.Windows.Media.VisualTreeHelper.GetChild(parent,i);if(child is T)yield return (T)child;foreach(var nested in VisualChildren<T>(child))yield return nested;}}
  static string EdgeState(ImageBrowser w){var button=Field<Forms.Button>(w,"edgeNext");var canvas=Field<WindowsFormsHost>(w,"host").Child;var point=canvas.PointToClient(Forms.Cursor.Position);return w.Title+";active="+w.IsActive+";edge="+button.Visible+"/"+button.Enabled+"/"+button.CanSelect+";point="+point+";canvas="+canvas.ClientSize+";loading="+Field<bool>(w,"loading")+";scanning="+Field<bool>(w,"scanning")+";drag="+Field<PreviewDragController>(w,"drag").IsDragging;}
 }
}
