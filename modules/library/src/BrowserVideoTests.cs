using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Mori {
 internal static class BrowserVideoTests {
  static void Pump(int ms){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};timer.Tick+=delegate{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
  static void Until(Func<bool> ready,int timeout=16000){var clock=Stopwatch.StartNew();while(!ready()&&clock.ElapsedMilliseconds<timeout)Pump(50);}
  internal static void Run(string output,Action<bool,string> check){
   Directory.CreateDirectory(output);
   foreach(string range in new[]{null,"bytes=0-99","bytes=30-","bytes=-25","bytes=80-999"}){var r=VideoRange.Parse(100,range);check(r.Status==(range==null?200:206)&&r.Start>=0&&r.Start+r.Length<=100,"video range bounded: "+range);}
   check(VideoRange.Parse(100,"bytes=30-").Length==70&&VideoRange.Parse(100,"bytes=-25").Start==75,"video ranges support open ends and suffixes");
   foreach(string range in new[]{"bytes=100-","bytes=5-4","bytes=-0","bytes=1-2,4-5","bytes=-x","bytes=999999999999999999999999-","items=0-1"})check(VideoRange.Parse(100,range).Status==416,"invalid video range rejected: "+range);
   check(BrowserVideo.AllowedRequest(BrowserVideo.ClipUrl,"GET")&&BrowserVideo.AllowedRequest(BrowserVideo.PageUrl,"GET"),"local video allows only its two exact resources");
   foreach(string uri in new[]{"https://example.com/","file:///C:/Windows/win.ini",BrowserVideo.ClipUrl+"?file=secret",BrowserVideo.PageUrl+"/../secret"})check(!BrowserVideo.AllowedRequest(uri,"GET"),"video page cannot read other files or network: "+uri);
   check(!BrowserVideo.AllowedRequest(BrowserVideo.ClipUrl,"POST"),"video origin is read-only");
   string sample=Path.Combine(output,"range.bin");File.WriteAllBytes(sample,Enumerable.Range(0,100).Select(x=>(byte)x).ToArray());using(var slice=new VideoSlice(sample,20,10)){byte[] data=new byte[100];check(slice.Read(data,0,100)==10&&data[0]==20&&data[9]==29&&slice.Read(data,0,1)==0,"video range reader never exceeds selected bytes");slice.Seek(2,SeekOrigin.Begin);check(slice.Read(data,0,100)==8&&data[0]==22,"video range reader seeks relative to slice");using(var exclusive=new FileStream(sample,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(exclusive.Length==100,"range response holds no file lock between reads");}
   string file=Environment.GetEnvironmentVariable("MORI_TEST_WEBM");if(string.IsNullOrEmpty(file))return;
   check(File.Exists(file)&&BrowserVideo.Available,"actual WebM fixture and system WebView2 runtime available");
   byte[] original=File.ReadAllBytes(file);int width,height;var clock=Stopwatch.StartNew();var poster=VideoThumbnail.Render(file,out width,out height);
   check(poster!=null&&width>0&&height>0&&poster.PixelWidth<=512&&poster.PixelHeight<=512,"actual WebM decoded to bounded thumbnail / "+VideoThumbnail.LastFailure);
   using(var stream=File.Create(Path.Combine(output,"webm-thumbnail.png"))){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(poster));encoder.Save(stream);}
   using(var lib=new Library(Path.Combine(output,"library"))){var asset=lib.Import(file,"","WebM regression","https://pollen-robotics.com/microduck/","","").Item;lib.BuildThumbnail(asset);check(File.Exists(lib.ThumbnailPath(asset))&&asset.Width==width&&asset.Height==height,"WebM library caches thumbnail and native dimensions / "+VideoThumbnail.LastFailure);DateTime written=File.GetLastWriteTimeUtc(lib.ThumbnailPath(asset));lib.BuildThumbnail(asset);check(File.GetLastWriteTimeUtc(lib.ThumbnailPath(asset))==written,"cached WebM thumbnail does not launch decoder again");check(original.SequenceEqual(File.ReadAllBytes(lib.FullPath(asset))),"WebM preview preserves original bytes");}
   var app=Application.Current??new Application();var previous=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;var previousContext=System.Threading.SynchronizationContext.Current;System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
   var player=new VideoPlayer(file);var host=new Grid{Width=640,Height=480};host.Children.Add(player.Root);var source=new HwndSource(new HwndSourceParameters("Mori local WebM tests"){Width=640,Height=480,WindowStyle=0});source.RootVisual=host;host.Measure(new Size(640,480));host.Arrange(new Rect(0,0,640,480));host.UpdateLayout();int browserPid=0;
   try{
    player.Start();Until(()=>player.State.Ready||player.Failed);check(player.State.Ready&&!player.Failed&&player.UsingBrowser,"WebM plays in Mori without Windows codec installation / "+player.Browser.LastError);browserPid=player.Browser.BrowserProcessId;
    Until(()=>player.State.Position>.1,5000);check(player.State.Position>.1,"WebM clock advances with real decoded playback");
    player.Toggle();double target=player.State.Duration*.6;player.Seek(target);Pump(500);check(!player.State.Playing&&Math.Abs(player.State.Position-target)<.15,"WebM paused seek lands at chosen frame without resuming");
    player.BeginSeek();player.State.Seek(player.State.Duration*.3);player.EndSeek();Pump(400);check(!player.State.Playing&&Math.Abs(player.State.Position-player.State.Duration*.3)<.15,"WebM mouse-seek preserves pause state");
    byte[] frame=null;player.Browser.Thumbnail+=delegate(byte[] bytes,int w,int h){frame=bytes;};player.Browser.Command("thumbnail");Until(()=>frame!=null,5000);check(frame!=null&&frame.Length>3000,"real WebM frame contains decoded picture, not a black placeholder");File.WriteAllBytes(Path.Combine(output,"webm-frame.png"),frame);
    player.Toggle();player.Seek(Math.Max(0,player.State.Duration-.1));Pump(1100);check(player.State.Playing&&player.State.Position<2,"WebM loops from end to beginning");
    player.SetVisible(false);Pump(300);double stopped=player.State.Position;Pump(500);check(Math.Abs(player.State.Position-stopped)<.08,"hidden WebM pauses decoder and progress");player.SetVisible(true);Pump(500);check(player.State.Position>stopped+.15,"visible WebM resumes from same position");
    for(int i=0;i<12;i++)player.Toggle();Pump(600);check(!player.Failed&&player.State.Playing,"rapid WebM pause/resume does not mistake an aborted play request for decode failure");
    // This executes only in the isolated app-owned test player, never a user browser.
    var probe=player.Browser.View.CoreWebView2.ExecuteScriptAsync("window.moriTestBlocked=false;fetch('https://example.com/not-sent').catch(()=>{window.moriTestBlocked=true;});");Until(()=>probe.IsCompleted,3000);Pump(100);var blocked=player.Browser.View.CoreWebView2.ExecuteScriptAsync("window.moriTestBlocked");Until(()=>blocked.IsCompleted,3000);check(blocked.IsCompleted&&blocked.Result=="true","embedded player CSP blocks external fetch without uploading media");
    bool close=false;player.CloseRequested+=delegate{close=true;};player.BrowserKey("Escape");check(close,"WebM focused Escape reaches preview close callback");
   }finally{player.Dispose();source.Dispose();app.ShutdownMode=previous;System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext);}
   check(!player.Browser.Ready&&!player.TimerRunning&&player.Media.Source==null,"closing WebM releases control and timers");
   Until(()=>{try{return Process.GetProcessById(browserPid).HasExited;}catch(ArgumentException){return true;}},12000);
   bool exited;try{using(var process=Process.GetProcessById(browserPid))exited=process.HasExited;}catch(ArgumentException){exited=true;}check(exited,"last WebM preview closes its browser runtime process");
   check(original.SequenceEqual(File.ReadAllBytes(file)),"playback leaves source WebM unchanged");
  }
 }
}
