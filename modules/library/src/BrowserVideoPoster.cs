using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Mori {
 internal static class BrowserVideoPoster {
  internal static int Worker(string file){
   SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
   var frame=new DispatcherFrame();var timeout=new DispatcherTimer{Interval=TimeSpan.FromSeconds(12)};BrowserVideo video=null;HwndSource source=null;byte[] png=null;int width=0,height=0;
   try{
    video=new BrowserVideo(file);var host=new Grid{Width=512,Height=512};host.Children.Add(video.View);source=new HwndSource(new HwndSourceParameters("Mori thumbnail worker"){Width=512,Height=512,WindowStyle=0});source.RootVisual=host;host.Measure(new Size(512,512));host.Arrange(new Rect(0,0,512,512));host.UpdateLayout();
    video.Opened+=delegate{video.Command("thumbnail");};video.Thumbnail+=delegate(byte[] bytes,int w,int h){png=bytes;width=w;height=h;frame.Continue=false;};video.Failed+=delegate{frame.Continue=false;};timeout.Tick+=delegate{frame.Continue=false;};
    timeout.Start();var loading=video.Start();Dispatcher.PushFrame(frame);
    if(png==null||png.Length>3*1024*1024||width<1||height<1){Console.Error.WriteLine(video.LastError??"No decoded thumbnail before timeout");return 2;}
    using(var output=Console.OpenStandardOutput())using(var writer=new BinaryWriter(output)){writer.Write(width);writer.Write(height);writer.Write(png);}return 0;
   }catch(Exception ex){Console.Error.WriteLine(ex.Message);return 2;}finally{timeout.Stop();if(video!=null)video.Dispose();if(source!=null)source.Dispose();}
  }
 }
}
