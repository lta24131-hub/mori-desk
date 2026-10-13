using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Mori {
 public static class PreviewImageTests {
  static void Pump(int milliseconds){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(milliseconds)};timer.Tick+=delegate{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
  static bool Until(Func<bool> predicate,int milliseconds){var clock=System.Diagnostics.Stopwatch.StartNew();while(!predicate()&&clock.ElapsedMilliseconds<milliseconds)Pump(20);return predicate();}
  static bool HasImageHost(Window window){var grid=window.Content as Grid;return grid!=null&&grid.Children.OfType<DockPanel>().Any(panel=>panel.Children.OfType<WindowsFormsHost>().Any());}
  static void WindowLifetime(string png,Action<bool,string> check){
   var app=Application.Current??new Application();if(Theme.Current==null)Theme.Set("dark");var previousShutdown=app.ShutdownMode;var previousMain=app.MainWindow;var previousContext=SynchronizationContext.Current;Window loaded=null,queued=null;Exception unhandled=null;
   app.ShutdownMode=ShutdownMode.OnExplicitShutdown;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
   DispatcherUnhandledExceptionEventHandler onError=delegate(object sender,DispatcherUnhandledExceptionEventArgs e){unhandled=e.Exception;e.Handled=true;};app.DispatcherUnhandledException+=onError;
   Action<Window> offscreen=delegate(Window window){window.WindowStartupLocation=WindowStartupLocation.Manual;window.Left=-20000;window.Top=-20000;window.ShowInTaskbar=false;window.ShowActivated=false;window.Width=540;window.Height=380;};
   try{
    PreviewWindow.Images(null,new[]{png},new[]{"Synthetic lifecycle"},delegate(Window window){offscreen(window);loaded=window;});
    check(Until(()=>HasImageHost(loaded)||unhandled!=null,4000)&&unhandled==null&&HasImageHost(loaded),"async preview fixture finishes loading in its own offscreen window");
    loaded.Close();Pump(40);check(unhandled==null&&!loaded.IsVisible&&!app.Windows.Cast<Window>().Contains(loaded),"closing a fully loaded async preview does not cancel a disposed CTS");
    using(var exclusive=new FileStream(png,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(exclusive.Length>0,"closing the actual preview window releases the decoded original stream");
    var slots=(SemaphoreSlim)typeof(PreviewImage).GetField("DecodeSlots",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);slots.Wait();slots.Wait();
    try{
     PreviewWindow.Images(null,new[]{png},new[]{"Synthetic queued lifecycle"},delegate(Window window){offscreen(window);queued=window;});
     check(!HasImageHost(queued),"preview fixture remains queued while both decode slots are occupied");queued.Close();
    }finally{slots.Release(2);}
    Pump(120);check(unhandled==null&&!queued.IsVisible&&!HasImageHost(queued)&&!app.Windows.Cast<Window>().Contains(queued),"closing a queued preview never attaches a late image host after slots resume");
    using(var exclusive=new FileStream(png,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(exclusive.Length>0,"queued preview cancellation leaves the source unlocked");
   }finally{
    if(loaded!=null&&loaded.IsVisible)loaded.Close();if(queued!=null&&queued.IsVisible)queued.Close();Pump(20);app.DispatcherUnhandledException-=onError;app.MainWindow=previousMain;app.ShutdownMode=previousShutdown;SynchronizationContext.SetSynchronizationContext(previousContext);
   }
  }
  public static void Run(string output,Action<bool,string> check){
   Directory.CreateDirectory(output);
   byte[] bgra={30,70,180,255, 120,40,20,128};
   var source=BitmapSource.Create(2,1,96,96,PixelFormats.Bgra32,null,bgra,8);
   using(var result=PreviewImage.FromBitmapSource(source)){
    var first=result.GetPixel(0,0);var second=result.GetPixel(1,0);
    check(result.Width==2&&result.Height==1,"WIC fallback preserves dimensions");
    check(first.R==180&&first.G==70&&first.B==30&&first.A==255,"WIC fallback preserves RGB channel order");
    check(second.R==20&&second.G==40&&second.B==120&&second.A==128,"WIC fallback preserves alpha without premultiplication halos");
   }
   var gray=BitmapSource.Create(2,1,96,96,PixelFormats.Gray8,null,new byte[]{45,190},2);
   using(var result=PreviewImage.FromBitmapSource(gray))check(result.GetPixel(0,0).R==45&&result.GetPixel(1,0).B==190,"WIC fallback converts non-BGRA formats");
   string png=Path.Combine(output,"sample.png");
   using(var bitmap=new Bitmap(2,2)){bitmap.SetPixel(0,0,System.Drawing.Color.Red);bitmap.Save(png,ImageFormat.Png);}
   byte[] before=File.ReadAllBytes(png);
   using(var loaded=PreviewImage.Open(png))check(!loaded.UsedSystemDecoder&&loaded.Image.Width==2,"PNG retains existing lightweight GDI path");
   using(var unlocked=new FileStream(png,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(unlocked.Length==before.Length,"preview disposal releases original file handle");
   string invalid=Path.Combine(output,"bad.webp");File.WriteAllText(invalid,"not a valid image");bool rejected=false;try{using(var loaded=PreviewImage.Open(invalid)) {}}catch{rejected=true;}
   check(rejected,"invalid image rejected safely");
   using(var unlocked=new FileStream(invalid,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(unlocked.Length>0,"failed decoder releases original file handle");
   using(var cancel=new CancellationTokenSource()){
    cancel.Cancel();bool canceled=false;try{using(var loaded=PreviewImage.OpenAsync(png,cancel.Token).GetAwaiter().GetResult()) {}}catch(OperationCanceledException){canceled=true;}
    check(canceled,"closed preview cancels queued decode before opening the source");
   }
   for(int i=0;i<20;i++){
    using(var loaded=PreviewImage.OpenAsync(png,CancellationToken.None).GetAwaiter().GetResult())check(loaded.Image.Width==2,"background image decode retains original dimensions "+i);
   }
   using(var unlocked=new FileStream(png,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(unlocked.Length==before.Length,"repeated background previews release original file handles");
   bool missing=false;try{using(var loaded=PreviewImage.OpenAsync(Path.Combine(output,"missing.png"),CancellationToken.None).GetAwaiter().GetResult()) {}}catch(FileNotFoundException){missing=true;}
   check(missing,"background decoder propagates missing original instead of hanging");
   check(PreviewWindow.FailureText(new FileNotFoundException()).Contains("本地地址"),"missing shortcut preview points to the local address rather than unsupported format");
   check(PreviewWindow.FailureText(new UnauthorizedAccessException()).Contains("访问权限"),"preview denied-file error describes permission recovery");
   var slots=(SemaphoreSlim)typeof(PreviewImage).GetField("DecodeSlots",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);slots.Wait();slots.Wait();
   try{using(var cancel=new CancellationTokenSource()){
    var pending=PreviewImage.OpenAsync(png,cancel.Token);check(!pending.IsCompleted,"simultaneous previews cap background image decodes at two");cancel.Cancel();bool canceled=false;try{using(var loaded=pending.GetAwaiter().GetResult()) {}}catch(OperationCanceledException){canceled=true;}
    check(canceled,"closing a queued preview exits without waiting for other decodes");
   }}finally{slots.Release(2);}
   WindowLifetime(png,check);
  }
 }
}
