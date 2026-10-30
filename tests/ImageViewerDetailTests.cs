using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mori;
using Forms=System.Windows.Forms;

namespace MoriDesk {
 internal static class ImageViewerDetailTests {
  const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
  static T Field<T>(ImageBrowser w,string key){return (T)typeof(ImageBrowser).GetField(key,Hidden).GetValue(w);}
  static object Call(ImageBrowser w,string key,params object[] args){return typeof(ImageBrowser).GetMethod(key,Hidden).Invoke(w,args);}
  static void Pump(int ms){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};timer.Tick+=delegate{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
  static bool Until(Func<bool> ready){var watch=System.Diagnostics.Stopwatch.StartNew();while(!ready()&&watch.ElapsedMilliseconds<10000)Pump(20);return ready();}
  static bool Ready(ImageBrowser w){return Field<PreviewImage>(w,"image")!=null&&!Field<bool>(w,"loading")&&!Field<bool>(w,"scanning");}
  static System.Windows.Controls.Image Thumbnail(Button b){return ((Grid)b.Content).Children.OfType<System.Windows.Controls.Image>().Single();}
  static bool Near(double a,double b){return Math.Abs(a-b)<.001;}
  static void Fixture(string path,int width,int height,bool transparent){using(var image=new Bitmap(width,height,PixelFormat.Format32bppArgb))using(var g=Graphics.FromImage(image)){
   g.Clear(transparent?Color.Transparent:Color.FromArgb(25,42,56));using(var brush=new SolidBrush(Color.FromArgb(114,101,188)))g.FillEllipse(brush,width*.2f,height*.2f,width*.6f,height*.6f);
   using(var pen=new Pen(Color.White,Math.Max(2,width/100)))g.DrawRectangle(pen,width*.3f,height*.3f,width*.4f,height*.4f);image.Save(path,ImageFormat.Png);
  }}
  static void Shot(ImageBrowser w,string name){w.UpdateLayout();Pump(500);using(var bmp=new Bitmap((int)w.ActualWidth,(int)w.ActualHeight))using(var g=Graphics.FromImage(bmp))using(var stream=new MemoryStream()){g.CopyFromScreen((int)w.Left,(int)w.Top,0,0,bmp.Size);bmp.Save(stream,ImageFormat.Jpeg);File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,name+".json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{name=name,imageBase64=Convert.ToBase64String(stream.ToArray())}));}}
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);string[] paths=Enumerable.Range(0,4).Select(i=>Path.Combine(root,i+".png")).ToArray();Fixture(paths[0],480,360,true);Fixture(paths[1],600,3000,false);Fixture(paths[2],3000,400,false);Fixture(paths[3],1000,1400,false);
   var preferences=new Settings{PreviewBackdrop="dark",AnimateImages=false};var w=new ImageBrowser(paths[0],preferences){Width=900,Height=480,Left=0,Top=0,WindowStartupLocation=WindowStartupLocation.Manual};
   try{
    w.Show();check(Until(()=>Ready(w)),"transparent picture opens from a viewer configured with a saved dark backdrop");
    var picture=Field<PreviewPicture>(w,"picture");var viewport=Field<PreviewViewport>(w,"viewport");
    check(picture.BackgroundMode==PreviewBackground.Dark,"single-image viewer honors the configured backdrop instead of overwriting it with automatic");
    using(var render=new Bitmap(picture.Width,picture.Height)){picture.DrawToBitmap(render,new Rectangle(0,0,render.Width,render.Height));var color=render.GetPixel((int)(viewport.X+viewport.DisplayWidth*.1),(int)(viewport.Y+viewport.DisplayHeight*.1));check(Math.Abs(color.R-PreviewBackgrounds.SolidDark.R)<4&&Math.Abs(color.G-PreviewBackgrounds.SolidDark.G)<4,"configured backdrop reaches transparent pixels in the real native image renderer");}
    using(var source=new Bitmap(paths[0]))check(source.GetPixel(10,10).A==0,"preview background does not flatten the source PNG");
    Shot(w,"viewer-transparent");
    check(Until(()=>Field<StackPanel>(w,"strip").Children.OfType<Button>().All(b=>Thumbnail(b).Source!=null)),"mixed-aspect thumbnails finish decoding");
    var cache=Field<Dictionary<string,BitmapSource>>(w,"stripCache");cache.Remove(paths[1]);var slots=(SemaphoreSlim)typeof(ImageBrowser).GetField("stripSlots",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);slots.Wait();slots.Wait();
    try{Call(w,"StopFilmstrip");Call(w,"RefreshFilmstrip");var tiles=Field<StackPanel>(w,"strip").Children.OfType<Button>().ToArray();check(Thumbnail(tiles[1]).Source==null&&Thumbnail(tiles[0]).Source!=null&&Thumbnail(tiles[2]).Source!=null&&Thumbnail(tiles[3]).Source!=null,"cached thumbnails appear immediately on both sides of a slow uncached neighbor");}finally{slots.Release(2);}
    check(Until(()=>Field<StackPanel>(w,"strip").Children.OfType<Button>().All(b=>Thumbnail(b).Source!=null)),"blocked thumbnail fills in after decode capacity becomes available");
    preferences.PreviewBackdrop="white";preferences.AnimateImages=true;
    Field<StackPanel>(w,"strip").Children.OfType<Button>().ElementAt(1).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    check(Until(()=>Ready(w))&&w.Title=="1.png · Nook","clicking a thumbnail opens the intended long image");
    picture=Field<PreviewPicture>(w,"picture");viewport=Field<PreviewViewport>(w,"viewport");check(viewport.IsFit&&viewport.DisplayHeight<=viewport.Height&&viewport.DisplayWidth<=viewport.Width,"long image starts completely visible and keeps its aspect ratio");
    check(picture.BackgroundMode==PreviewBackground.Dark&&!Field<Settings>(w,"previewPreferences").AnimateImages,"an open viewer retains its copied backdrop and animation preferences while navigating");
    Call(w,"SetZoom",1.0);viewport.PanTo(0,-700);double beforeZoom=viewport.Zoom,beforeX=(viewport.Width/2-viewport.X)/viewport.Zoom,beforeY=(viewport.Height/2-viewport.Y)/viewport.Zoom;
    w.SetFocusMode(true);check(viewport.IsFit&&viewport.DisplayHeight<=viewport.Height,"entering focus shows the complete long image in the existing window");
    w.SetFocusMode(false);check(Near(viewport.Zoom,beforeZoom)&&Near((viewport.Width/2-viewport.X)/viewport.Zoom,beforeX)&&Near((viewport.Height/2-viewport.Y)/viewport.Zoom,beforeY),"leaving focus restores the previous zoom and reading position");
    w.SetFocusMode(true);w.Width=760;Pump(60);w.SetFocusMode(false);check(Near(viewport.Zoom,beforeZoom)&&Near((viewport.Height/2-viewport.Y)/viewport.Zoom,beforeY),"focus restoration retains the image position after the window is resized");
    w.SetFocusMode(true);Call(w,"HandleNativeKey",Forms.Keys.Right);check(Until(()=>Ready(w))&&w.Title=="2.png · Nook","keyboard navigation works in focus mode");w.SetFocusMode(false);viewport=Field<PreviewViewport>(w,"viewport");check(viewport.IsFit&&viewport.DisplayWidth<=viewport.Width&&viewport.DisplayHeight<=viewport.Height,"a different picture never inherits the previous image's stale zoom or pan");
    Call(w,"HandleNativeKey",Forms.Keys.Right);check(Until(()=>Ready(w))&&w.Title=="3.png · Nook","keyboard can continue from panorama to portrait");viewport=Field<PreviewViewport>(w,"viewport");check(viewport.IsFit&&Near(viewport.DisplayWidth/viewport.DisplayHeight,1000.0/1400.0),"portrait preserves its proportions");Shot(w,"viewer-portrait");
    w.UpdateEdgeVisibility(20);typeof(Window).GetMethod("OnDeactivated",Hidden).Invoke(w,new object[]{EventArgs.Empty});check(!Field<Forms.Button>(w,"edgePrevious").Visible&&!Field<Forms.Button>(w,"edgeNext").Visible,"edge arrows disappear when the viewer loses activation");
    var menu=Field<ContextMenu>(w,"moreMenu");var background=menu.Items.OfType<MenuItem>().Single(item=>Equals(item.Header,"图片背景"));var white=background.Items.OfType<MenuItem>().Single(item=>(PreviewBackground)item.Tag==PreviewBackground.White);white.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));check(Field<PreviewPicture>(w,"picture").BackgroundMode==PreviewBackground.White,"background menu changes the live preview");
    Call(w,"GoToImage",0);check(Until(()=>Ready(w))&&Field<PreviewPicture>(w,"picture").BackgroundMode==PreviewBackground.White,"explicit per-window backdrop choice follows later images");
    w.Close();check(Field<WindowsFormsHost>(w,"host")==null&&Field<Dictionary<string,BitmapSource>>(w,"stripCache").Count==0,"closing the mixed-image viewer releases controls and cached previews");
    foreach(string path in paths)using(var file=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(file.Length>0,"viewer releases source file / "+Path.GetFileName(path));
   }finally{w.Close();Pump(60);}
  }
 }
}
