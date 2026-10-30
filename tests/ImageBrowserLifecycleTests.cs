using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Threading;
using Mori;
using Forms=System.Windows.Forms;

namespace MoriDesk {
 // Offscreen synthetic files only. These tests do not open a real library,
 // modify user preferences, capture the mouse or drive another application.
 internal static class ImageBrowserLifecycleTests {
  const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
  static T Field<T>(ImageBrowser viewer,string name){return (T)typeof(ImageBrowser).GetField(name,Hidden).GetValue(viewer);}
  static void Set(ImageBrowser viewer,string name,object value){typeof(ImageBrowser).GetField(name,Hidden).SetValue(viewer,value);}
  static void Call(ImageBrowser viewer,string name,params object[] arguments){typeof(ImageBrowser).GetMethod(name,Hidden).Invoke(viewer,arguments);}
  static void Mouse(Forms.Control control,string method,Forms.MouseEventArgs args){typeof(Forms.Control).GetMethod(method,Hidden).Invoke(control,new object[]{args});}
  static void Pump(int milliseconds){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(milliseconds)};timer.Tick+=delegate{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
  static bool Until(Func<bool> predicate,int milliseconds){var clock=System.Diagnostics.Stopwatch.StartNew();while(!predicate()&&clock.ElapsedMilliseconds<milliseconds)Pump(20);return predicate();}
  static bool Ready(ImageBrowser viewer){return Field<PreviewImage>(viewer,"image")!=null&&!Field<bool>(viewer,"loading")&&!Field<bool>(viewer,"scanning");}
  static ImageBrowser Create(){var viewer=new ImageBrowser(null){WindowStartupLocation=WindowStartupLocation.Manual,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,Width=700,Height=480};viewer.Show();Pump(20);return viewer;}
  static void Write(string path,int width,int height,Color color){using(var bitmap=new Bitmap(width,height)){using(var graphics=Graphics.FromImage(bitmap))graphics.Clear(color);bitmap.Save(path,ImageFormat.Png);}}
  static bool Animating(PreviewPicture picture){return (bool)typeof(PreviewPicture).GetField("animating",Hidden).GetValue(picture);}
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);var app=Application.Current??new Application();if(Theme.Current==null)Theme.Set("dark");var shutdown=app.ShutdownMode;var main=app.MainWindow;var context=SynchronizationContext.Current;Exception unhandled=null;
   app.ShutdownMode=ShutdownMode.OnExplicitShutdown;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
   DispatcherUnhandledExceptionEventHandler onError=delegate(object sender,DispatcherUnhandledExceptionEventArgs e){unhandled=e.Exception;e.Handled=true;};app.DispatcherUnhandledException+=onError;
   try{LatestWins(root,check);Visibility(root,check);OwnedDocument(root,check);Reveal(root,check);check(unhandled==null,"direct image lifecycle never raises an unhandled dispatcher error / "+unhandled);}
   finally{app.DispatcherUnhandledException-=onError;app.MainWindow=main;app.ShutdownMode=shutdown;SynchronizationContext.SetSynchronizationContext(context);}
  }
  static void LatestWins(string root,Action<bool,string> check){
   string first=Path.Combine(root,"a-first.png"),latest=Path.Combine(root,"b-latest.png"),other=Path.Combine(root,"another-folder");Directory.CreateDirectory(other);other=Path.Combine(other,"newest.png");Write(first,32,20,Color.Red);Write(latest,41,29,Color.Blue);Write(other,53,37,Color.Green);
   var slots=(SemaphoreSlim)typeof(PreviewImage).GetField("DecodeSlots",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);ImageBrowser viewer=Create();
   try{
    slots.Wait();slots.Wait();
    try{
     Call(viewer,"Open",first);Call(viewer,"Open",latest);Call(viewer,"Open",Path.Combine(root,"not-an-image.txt"));Pump(60);
     check(Field<string>(viewer,"currentPath")==latest&&Field<bool>(viewer,"loading")&&Field<PreviewImage>(viewer,"image")==null,"newest dropped image replaces a pending request instead of being ignored");
     check(Field<TextBlock>(viewer,"label").Text=="正在打开…"&&!Field<Button>(viewer,"pinButton").IsEnabled,"canceled or invalid older request cannot clear a valid loading status or expose a stale pin action");
    }finally{slots.Release(2);}
    check(Until(()=>Ready(viewer),4000),"newest direct image request finishes decoding and independent folder discovery");
    var image=Field<PreviewImage>(viewer,"image");using(var pixels=new Bitmap(image.Image))check(image.Image.Width==41&&image.Image.Height==29&&pixels.GetPixel(20,14).B>240&&viewer.Title=="b-latest.png · Nook","latest request owns the actual decoded pixels and window title");
    check(Field<string[]>(viewer,"siblings").Length==2&&Field<int>(viewer,"index")==1&&Field<Button>(viewer,"previous").IsEnabled,"latest folder listing retains previous/next navigation after request cancellation");
    var wheel=new Forms.HandledMouseEventArgs(Forms.MouseButtons.None,0,20,20,120);var picture=Field<PreviewPicture>(viewer,"picture");Field<PreviewViewport>(viewer,"viewport").ZoomAt(.25,20,20);double zoom=Field<PreviewViewport>(viewer,"viewport").Zoom;Mouse(picture,"OnMouseWheel",wheel);
    check(wheel.Handled&&Math.Abs(Field<PreviewViewport>(viewer,"viewport").Zoom/zoom-1.15)<.00001,"one native wheel event is handled once and produces exactly one zoom step");
    PendingPin(viewer,latest,other,check);
    slots.Wait();slots.Wait();
    try{
     Call(viewer,"Step",1);Call(viewer,"Open",other);Pump(60);
     check(Field<string>(viewer,"currentPath")==other&&Field<bool>(viewer,"loading"),"explicit new file also supersedes an in-progress next/previous decode");
    }finally{slots.Release(2);}
    check(Until(()=>Ready(viewer),4000)&&Field<PreviewImage>(viewer,"image").Image.Width==53&&Field<string[]>(viewer,"siblings").SequenceEqual(new[]{other}),"stale navigation completion cannot replace newest picture or its different folder list");
    Call(viewer,"Open",Path.Combine(root,"missing.png"));check(Until(()=>!Field<bool>(viewer,"loading")&&!Field<bool>(viewer,"scanning"),4000)&&Field<PreviewImage>(viewer,"image")==null&&Field<TextBlock>(viewer,"label").Text.StartsWith("无法预览：",StringComparison.Ordinal),"missing image finishes with actionable error rather than a stuck loading state");
    Call(viewer,"Open",latest);check(Until(()=>Ready(viewer),4000),"a valid drop recovers immediately after a failed direct-open request");
    slots.Wait();slots.Wait();
    try{Call(viewer,"Open",first);viewer.Close();check(Field<bool>(viewer,"closed"),"direct viewer closes immediately while a replacement image is queued");}
    finally{slots.Release(2);}
    check(Until(()=>Field<CancellationTokenSource>(viewer,"scan")==null,4000)&&Field<WindowsFormsHost>(viewer,"host")==null&&!viewer.IsVisible,"closed viewer never attaches a late image host and disposes its request source");
    foreach(string path in new[]{first,latest,other})using(var unlocked=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(unlocked.Length>0,"canceled/replaced/closed direct preview releases original file / "+Path.GetFileName(path));
   }finally{viewer.Close();Pump(40);}
  }
  static void PendingPin(ImageBrowser viewer,string latest,string other,Action<bool,string> check){
   var before=DesktopImagePins.SinkForTests;var completion=new TaskCompletionSource<bool>();System.Windows.Media.Imaging.BitmapSource submitted=null;
   try{
    DesktopImagePins.SinkForTests=value=>{submitted=value;return completion.Task;};var pending=(Task)typeof(ImageBrowser).GetMethod("PinCurrentAsync",Hidden).Invoke(viewer,null);
    check(!pending.IsCompleted&&submitted!=null&&submitted.IsFrozen,"desktop pin snapshots and freezes its image before asynchronous handoff");Call(viewer,"Open",other);
    check(Until(()=>Ready(viewer),4000)&&!Field<Button>(viewer,"pinButton").IsEnabled&&!pending.IsCompleted,"new image may load during a pin handoff without reenabling duplicate pin submission");
    completion.SetResult(true);check(Until(()=>pending.IsCompleted,4000),"pending pin completes after its original preview has been released");pending.GetAwaiter().GetResult();var pixel=new byte[4];submitted.CopyPixels(new Int32Rect(20,14,1,1),pixel,4,0);
    check(pixel[0]>240&&pixel[2]<10&&Field<PreviewImage>(viewer,"image").Image.Width==53&&Field<Button>(viewer,"pinButton").IsEnabled,"completed old pin retains its blue pixels without changing the newer green image or pin state");
   }finally{completion.TrySetResult(true);DesktopImagePins.SinkForTests=before;}
   Call(viewer,"Open",latest);check(Until(()=>Ready(viewer),4000),"viewer keeps navigation usable after interleaved pin and open requests");
  }
  static void Visibility(string root,Action<bool,string> check){
   string path=Path.Combine(root,"animated.gif");File.WriteAllBytes(path,(byte[])typeof(PreviewPreferencesTests).GetMethod("AnimatedGif",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null));ImageBrowser viewer=Create();PreviewImage opened=null;
   try{
    opened=PreviewImage.Open(path);var viewport=new PreviewViewport(8,8);var canvas=new PreviewCanvas{Dock=Forms.DockStyle.Fill};var picture=new PreviewPicture(opened.Image,viewport,new Settings{AnimateImages=true});canvas.Controls.Add(picture);var host=new WindowsFormsHost{Child=canvas};var drag=new PreviewDragController(canvas,picture,viewport,delegate{},false);
    Set(viewer,"image",opened);Set(viewer,"viewport",viewport);Set(viewer,"picture",picture);Set(viewer,"host",host);Set(viewer,"drag",drag);((DockPanel)viewer.Content).Children.Add(host);viewer.UpdateLayout();viewport.Resize(canvas.ClientSize.Width,canvas.ClientSize.Height);var handle=picture.Handle;Call(viewer,"UpdatePlaybackVisibility");
    check(Animating(picture),"direct viewer starts an explicitly enabled synthetic GIF while visible");
    viewer.Hide();Pump(20);check(!Animating(picture),"hiding the independent image viewer suspends GIF animator callbacks");
    viewer.Show();Pump(20);check(Animating(picture),"showing the independent viewer resumes its existing GIF preference");
    viewer.WindowState=WindowState.Minimized;Pump(20);check(!Animating(picture),"minimizing the independent viewer suspends GIF work even while WPF remains visible");
    viewer.WindowState=WindowState.Normal;Pump(20);check(Animating(picture),"restoring the independent viewer resumes animation without opening another decoder");
    viewport.Resize(40,40);viewport.ZoomAt(4,20,20);Mouse(picture,"OnMouseDown",new Forms.MouseEventArgs(Forms.MouseButtons.Left,1,20,20,0));check(drag.IsDragging,"direct viewer drag fixture starts without capturing the user's physical mouse");
    typeof(Window).GetMethod("OnDeactivated",Hidden).Invoke(viewer,new object[]{EventArgs.Empty});double x=viewport.X,y=viewport.Y;Mouse(picture,"OnMouseMove",new Forms.MouseEventArgs(Forms.MouseButtons.Left,0,80,80,0));check(!drag.IsDragging&&viewport.X==x&&viewport.Y==y,"window deactivation releases image drag and ignores queued mouse movement");
    Mouse(picture,"OnMouseDown",new Forms.MouseEventArgs(Forms.MouseButtons.Left,1,20,20,0));viewer.Hide();check(!drag.IsDragging&&!Animating(picture),"hiding an actively dragged viewer stops both dragging and GIF callbacks");
    viewer.Close();opened=null;check(picture.IsDisposed&&!Animating(picture)&&Field<PreviewImage>(viewer,"image")==null,"closing a hidden animated viewer releases controls, callbacks and decoded source");
    using(var unlocked=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(unlocked.Length>0,"closing animated direct preview releases its original GIF stream");
   }finally{viewer.Close();if(opened!=null)opened.Dispose();Pump(40);}
  }
  static bool Unlocked(string path){try{using(var file=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))return true;}catch(IOException){return false;}}
  static void Reveal(string root,Action<bool,string> check){
   string missing=Path.Combine(root,"moved-or-deleted.png"),received=null;int calls=0;
   check(ImageBrowser.RevealFailure(null,path=>calls++).Contains("请先打开")&&calls==0,"empty direct viewer gives a non-blocking file-position hint without launching Explorer");
   check(ImageBrowser.RevealFailure(missing,path=>{calls++;received=path;})==""&&calls==1&&received==missing,"safe reveal passes the current file path unchanged to its one launch action");
   foreach(Exception failure in new Exception[]{new IOException("fixture source offline"),new UnauthorizedAccessException("fixture access denied"),new System.ComponentModel.Win32Exception("fixture Explorer unavailable")}){
    string message=ImageBrowser.RevealFailure(missing,path=>{throw failure;});check(message.StartsWith("无法打开文件位置：",StringComparison.Ordinal)&&message.Contains(failure.Message),"file position catches launch/read errors and preserves their actionable reason / "+failure.GetType().Name);
   }
   var viewer=new ImageBrowser(null);
   try{
    Set(viewer,"currentPath",missing);var menu=Field<ContextMenu>(viewer,"moreMenu");var button=menu.Items.OfType<MenuItem>().Single(item=>Equals(item.Header,"文件位置"));button.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    check(Field<TextBlock>(viewer,"label").Text.Contains("无法打开文件位置")&&Field<TextBlock>(viewer,"label").Text.Contains("原文件已移动")&&!Field<bool>(viewer,"closed"),"actual file-position button reports moved/deleted file inline without crashing, closing or opening a modal dialog");
   }finally{viewer.Close();}
  }
  static void OwnedDocument(string root,Action<bool,string> check){
   string ai=Path.Combine(root,"synthetic-artboards.ai");byte[] before=PdfPreviewTests.Fixture(4);File.WriteAllBytes(ai,before);ImageBrowser viewer=Create();DocumentPreviewWindow document=null;
   try{
    check(FormatPreviewInfo.UsesPagedPreview(ai),"direct AI route selects continuous document preview rather than a truncated image decode");
    document=new DocumentPreviewWindow(viewer,ai,"Synthetic AI lifecycle",false){WindowStartupLocation=WindowStartupLocation.Manual,Left=-20000,Top=-20000,ShowActivated=false,ShowInTaskbar=false};document.Show();
    check(document.Owner==viewer&&viewer.OwnedWindows.Cast<Window>().Contains(document),"continuous AI preview remains owned by its independent direct-open window");
    viewer.Hide();viewer.Close();
    check(!viewer.IsVisible&&!document.IsVisible&&(bool)typeof(DocumentPreviewWindow).GetField("closed",Hidden).GetValue(document),"closing a hidden image owner also closes its AI preview during native document opening");
    check(Until(()=>typeof(DocumentPreviewWindow).GetField("loading",Hidden).GetValue(document)==null&&Unlocked(ai),8000)&&File.ReadAllBytes(ai).SequenceEqual(before),"canceled owned AI preview releases native file access and never modifies the original");
   }finally{if(document!=null)document.Close();viewer.Close();Pump(40);}
  }
 }
}
