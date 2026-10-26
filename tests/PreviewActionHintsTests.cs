using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Threading;
using Mori;
using Drawing=System.Drawing;
using Forms=System.Windows.Forms;

namespace MoriDesk {
 // Guest ScenarioRunner only. The real async preview opens offscreen with
 // generated PNG/WebP fixtures; no library, clipboard, desktop pin or service.
 internal static class PreviewActionHintsTests {
  const string Single="右键：复制 / 钉在桌面 · Ctrl+C 复制 · Esc 关闭";
  const string Comparison="右键：复制 · Ctrl+C 复制当前图片 · Esc 关闭";
  static void Pump(int milliseconds){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(milliseconds)};timer.Tick+=delegate{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
  static bool Until(Func<bool> ready,int milliseconds){var clock=System.Diagnostics.Stopwatch.StartNew();while(!ready()&&clock.ElapsedMilliseconds<milliseconds)Pump(20);return ready();}
  static DockPanel[] Panes(Window window){var grid=window.Content as Grid;return grid==null?new DockPanel[0]:grid.Children.OfType<DockPanel>().ToArray();}
  static TextBlock Hint(DockPanel panel){return panel.Children.OfType<TextBlock>().SingleOrDefault(text=>text.Name=="ImageActionsHint");}
  static int Hosts(Window window){return Panes(window).Sum(panel=>panel.Children.OfType<WindowsFormsHost>().Count());}
  static Window Open(string[] files){Window result=null;PreviewWindow.Images(null,files,files.Select(Path.GetFileName).ToArray(),delegate(Window window){result=window;window.WindowStartupLocation=WindowStartupLocation.Manual;window.Left=-20000;window.Top=-20000;window.Width=540;window.Height=380;window.ShowActivated=false;window.ShowInTaskbar=false;});return result;}
  static void Close(Window window){if(window!=null)window.Close();Pump(40);}
  static bool Unlocked(string file){using(var stream=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None))return stream.Length>0;}
  public static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);
   string oldData=Environment.GetEnvironmentVariable("MORI_DESK_DATA"),oldTheme=Theme.Current;
   var app=Application.Current??new Application();var shutdown=app.ShutdownMode;var previousMain=app.MainWindow;var context=SynchronizationContext.Current;
   Window active=null;Exception unhandled=null;
   Environment.SetEnvironmentVariable("MORI_DESK_DATA",Path.Combine(root,"isolated-profile"));
   app.ShutdownMode=ShutdownMode.OnExplicitShutdown;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));Theme.Set("dark");
   DispatcherUnhandledExceptionEventHandler onError=delegate(object sender,DispatcherUnhandledExceptionEventArgs error){unhandled=error.Exception;error.Handled=true;};app.DispatcherUnhandledException+=onError;
   string png=Path.Combine(root,"generated.png"),webp=Path.Combine(root,"generated.WEBP");
   try{
    using(var bitmap=new Drawing.Bitmap(8,4)){using(var draw=Drawing.Graphics.FromImage(bitmap))draw.Clear(Drawing.Color.CadetBlue);bitmap.Save(png,Drawing.Imaging.ImageFormat.Png);}
    File.WriteAllBytes(webp,ImageCodecFixtures.WebP());byte[] pngBytes=File.ReadAllBytes(png),webpBytes=File.ReadAllBytes(webp);
    foreach(string file in new[]{png,webp}){
     string label=Path.GetExtension(file);active=Open(new[]{file});
     check(Until(()=>Hosts(active)==1||unhandled!=null,15000)&&unhandled==null&&Hosts(active)==1,"actual successful "+label+" preview finishes decoding before image-action guidance appears");
     var panel=Panes(active).Single();var hint=Hint(panel);var host=panel.Children.OfType<WindowsFormsHost>().SingleOrDefault();
     check(hint!=null&&hint.Text==Single,"successful "+label+" single preview advertises only its supported copy, desktop pin and close actions");
     check(hint!=null&&host!=null&&DockPanel.GetDock(hint)==Dock.Bottom&&panel.Children.IndexOf(hint)<panel.Children.IndexOf(host),"successful "+label+" guidance is bottom-docked before the final fill image host");
     check(hint!=null&&Convert.ToString(hint.ToolTip)==FormatPreviewInfo.Notice(file)&&!hint.Text.Contains("解码器"),"successful "+label+" keeps decoder detail in its tooltip instead of the persistent instruction text");
     var picture=host==null?null:((Forms.Control)host.Child).Controls.OfType<PreviewPicture>().SingleOrDefault();
     check(picture!=null&&picture.ContextMenuStrip.Items.ContainsKey("CopyImage")&&picture.ContextMenuStrip.Items.ContainsKey("PinToDesktop"),"successful "+label+" guidance corresponds to attached production image actions");
     Close(active);check(!active.IsVisible&&!app.Windows.Cast<Window>().Contains(active)&&Unlocked(file),"closing successful "+label+" preview removes its UI and releases the original");active=null;
    }
    active=Open(new[]{png,webp});
    check(Until(()=>Hosts(active)==2||unhandled!=null,20000)&&unhandled==null&&Hosts(active)==2,"actual mixed PNG/WebP comparison finishes both async decodes");
    foreach(var panel in Panes(active)){
     var hint=Hint(panel);var host=panel.Children.OfType<WindowsFormsHost>().SingleOrDefault();
     check(hint!=null&&hint.Text==Comparison&&!hint.Text.Contains("钉"),"comparison guidance says copy the focused image and never advertises unavailable desktop pinning");
     var picture=host==null?null:((Forms.Control)host.Child).Controls.OfType<PreviewPicture>().SingleOrDefault();
     check(picture!=null&&picture.ContextMenuStrip.Items.ContainsKey("CopyImage")&&!picture.ContextMenuStrip.Items.ContainsKey("PinToDesktop"),"each comparison pane's actual menu matches its copy-only guidance");
    }
    Close(active);active=null;

    string missing=Path.Combine(root,"missing.webp"),failure=PreviewWindow.FailureText(new FileNotFoundException());active=Open(new[]{missing});
    check(Until(()=>Panes(active).Any(panel=>panel.Children.OfType<TextBlock>().Any(text=>text.Text==failure))||unhandled!=null,5000)&&unhandled==null,"missing WebP retains its original local-address recovery explanation");
    check(Hosts(active)==0&&Panes(active).All(panel=>Hint(panel)==null),"failed image load never retains misleading successful-image guidance");Close(active);active=null;

    var slots=(SemaphoreSlim)typeof(PreviewImage).GetField("DecodeSlots",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);int held=0;
    try{
     for(int i=0;i<2;i++){if(!slots.Wait(5000))throw new TimeoutException("Fixture could not reserve both decode slots.");held++;}
     active=Open(new[]{webp});
     check(Hosts(active)==0&&Panes(active).All(panel=>Hint(panel)==null),"a queued WebP preview has no success guidance before its decode completes");
     Close(active);
    }finally{if(held>0)slots.Release(held);}
    Pump(160);
    check(!active.IsVisible&&!app.Windows.Cast<Window>().Contains(active)&&Hosts(active)==0&&Panes(active).All(panel=>Hint(panel)==null),"cancelled closed preview cannot attach a late image host or successful-image guidance");active=null;

    // Pure presentation checks preserve visible limitations without opening
    // another document/media viewer or manufacturing a design-format decoder.
    var makeHint=typeof(PreviewWindow).GetMethod("ImageActionsHint",BindingFlags.NonPublic|BindingFlags.Static);
    foreach(string file in new[]{"example.PSD","example.PSB","example.AI"}){
     var hint=makeHint==null?null:(TextBlock)makeHint.Invoke(null,new object[]{file,false});string notice=FormatPreviewInfo.Notice(file);
     check(hint!=null&&notice.Length>0&&hint.Text.StartsWith(notice+" · ",StringComparison.Ordinal)&&hint.Text.EndsWith(Comparison,StringComparison.Ordinal)&&Convert.ToString(hint.ToolTip)==notice,"design-format limitation remains visible alongside truthful comparison guidance: "+Path.GetExtension(file));
    }
    check(FormatPreviewInfo.UsesPagedPreview("example.PDF")&&FormatPreviewInfo.UsesPagedPreview("example.AI"),"existing PDF/AI all-page dispatch remains independent of raster action guidance");
    check(unhandled==null,"all preview guidance lifetimes finish without a late dispatcher exception"+(unhandled==null?"":": "+unhandled));
    check(File.ReadAllBytes(png).SequenceEqual(pngBytes)&&File.ReadAllBytes(webp).SequenceEqual(webpBytes),"preview guidance never changes original generated image bytes");
   }finally{
    if(active!=null)Close(active);Pump(40);app.DispatcherUnhandledException-=onError;app.MainWindow=previousMain;app.ShutdownMode=shutdown;
    Environment.SetEnvironmentVariable("MORI_DESK_DATA",oldData);Theme.Set(oldTheme);SynchronizationContext.SetSynchronizationContext(context);
   }
  }
 }
}
