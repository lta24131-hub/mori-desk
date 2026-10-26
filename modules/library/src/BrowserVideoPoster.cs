using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Mori {
 internal static class BrowserVideoPoster {
  internal static int Worker(string file){
   var clock=Stopwatch.StartNew();Action<string> diagnostic=message=>{try{Console.Error.WriteLine(message+" elapsedMs="+clock.ElapsedMilliseconds);}catch(IOException){}catch(ObjectDisposedException){}};
   diagnostic("video-worker: entry");
   DispatcherFrame frame=null;DispatcherTimer timeout=null;BrowserVideo video=null;Window hostWindow=null;byte[] png=null;int width=0,height=0;
   try{
    diagnostic("video-worker: context-before");SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());diagnostic("video-worker: context-after");
    diagnostic("video-worker: dispatcher-create-before");frame=new DispatcherFrame();timeout=new DispatcherTimer{Interval=TimeSpan.FromSeconds(12)};diagnostic("video-worker: dispatcher-create-after");
    diagnostic("video-worker: browser-create-before");video=new BrowserVideo(file);diagnostic("video-worker: browser-create-after");
    video.Diagnostic+=delegate(string stage){diagnostic("video-stage: "+stage);};video.View.Loaded+=delegate{diagnostic("video-host: loaded-event visible="+video.View.IsVisible);};var host=new Grid{Width=512,Height=512};host.Children.Add(video.View);
    // Give WebView2 the normal WPF Window lifecycle, but keep the worker host
    // off-screen, out of the taskbar and non-activating. A bare HwndSource may
    // reach DOMContentLoaded without completing media initialization.
    diagnostic("video-worker: window-create-before");hostWindow=new Window{Title="Mori thumbnail worker",Width=512,Height=512,Left=-32000,Top=-32000,WindowStartupLocation=WindowStartupLocation.Manual,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,ShowActivated=false,Content=host};diagnostic("video-worker: window-create-after");
    diagnostic("video-worker: show-before");hostWindow.Show();diagnostic("video-worker: show-after");
    diagnostic("video-worker: layout-before");hostWindow.UpdateLayout();diagnostic("video-worker: layout-after");
    diagnostic("video-host: mode=wpf-window loaded="+video.View.IsLoaded+" visible="+video.View.IsVisible+" source="+(PresentationSource.FromVisual(video.View)!=null));
    video.Opened+=delegate{video.Command("thumbnail");};video.Thumbnail+=delegate(byte[] bytes,int w,int h){png=bytes;width=w;height=h;frame.Continue=false;};video.Failed+=delegate{frame.Continue=false;};timeout.Tick+=delegate{diagnostic("video-worker: deadline");frame.Continue=false;};
    diagnostic("video-worker: deadline-start seconds=12");timeout.Start();var loading=video.Start();diagnostic("video-worker: frame-enter");Dispatcher.PushFrame(frame);diagnostic("video-worker: frame-leave");
    if(png==null||png.Length>3*1024*1024||width<1||height<1){diagnostic((video.LastError??"No decoded thumbnail before timeout")+"; last-stage="+video.LastStage);return 2;}
    using(var output=Console.OpenStandardOutput())using(var writer=new BinaryWriter(output)){writer.Write(width);writer.Write(height);writer.Write(png);}return 0;
   }catch(Exception ex){diagnostic("video-worker: error "+ex.GetType().Name+": "+ex.Message);return 2;}finally{
    // A cleanup error must not replace the original decoder/initialization failure.
    try{if(timeout!=null)timeout.Stop();}catch(Exception ex){diagnostic("video-worker: timer-cleanup-error "+ex.GetType().Name+": "+ex.Message);}
    try{if(video!=null)video.Dispose();}catch(Exception ex){diagnostic("video-worker: browser-cleanup-error "+ex.GetType().Name+": "+ex.Message);}
    try{if(hostWindow!=null)hostWindow.Close();}catch(Exception ex){diagnostic("video-worker: window-cleanup-error "+ex.GetType().Name+": "+ex.Message);}
   }
  }
 }
}
