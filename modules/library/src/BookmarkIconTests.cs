using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Mori {
 public static class BookmarkIconTests {
  static byte[] Png(int w,int h){using(var image=new System.Drawing.Bitmap(w,h)){using(var g=System.Drawing.Graphics.FromImage(image))g.Clear(System.Drawing.Color.Teal);using(var s=new MemoryStream()){image.Save(s,System.Drawing.Imaging.ImageFormat.Png);return s.ToArray();}}}
  static bool Reject(Action work){try{work();return false;}catch{return true;}}
  static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
  public static void Run(string output,Action<bool,string> check){
   Directory.CreateDirectory(output);string site="https://example.com/path?private=value",stem=BookmarkIcons.PathFor(output,site);byte[] png=Png(128,96);int requests=0;
   Func<Uri,byte[]> fetch=uri=>{requests++;return png;};
   check(BookmarkIcons.Key(site)==BookmarkIcons.Key("https://EXAMPLE.com/other")&&BookmarkIcons.Key(site)!=BookmarkIcons.Key("http://example.com/"),"favicon cache key uses origin only, never page path/query");
   foreach(string bad in new[]{"file:///C:/x.ico","http://127.0.0.1/favicon.ico","http://[::1]/favicon.ico","http://10.0.0.1/icon","http://192.168.1.5/icon","http://localhost/icon","https://host.local/icon","https://user:pass@example.com/icon","https://example.com:444/icon"})check(!BookmarkIcons.Allowed(new Uri(bad)),"favicon rejects unsafe endpoint "+bad);
   foreach(string bad in new[]{"172.16.0.1","169.254.169.254","100.64.0.1","198.18.0.1","::ffff:127.0.0.1","fc00::1","fe80::1"})check(!BookmarkIcons.Public(System.Net.IPAddress.Parse(bad)),"favicon rejects non-public IP "+bad);
   check(BookmarkIcons.Allowed(new Uri(site))&&BookmarkIcons.Public(System.Net.IPAddress.Parse("8.8.8.8")),"favicon permits public HTTP(S) sites");
   BookmarkIcons.Remember(output,site,"https://cdn.example.com/icons/site.png");Uri requested=null;var first=BookmarkIcons.Load(output,site,uri=>{requested=uri;return fetch(uri);});
   check(requested.Host=="cdn.example.com"&&first.IsFrozen&&first.PixelWidth==64&&first.PixelHeight==48,"browser icon hint preferred and decoded to bounded frozen bitmap");
   check(File.Exists(stem+".png")&&new FileInfo(stem+".png").Length<32768,"favicon stores only small normalized PNG cache");
   BookmarkIcons.Load(output,"https://example.com/another",fetch);check(requests==1,"same-site repeated load uses local cache without request");
   check(BookmarkIcons.Load(output,site,null)!=null,"cached favicon remains available offline");
   string failure="https://fail.example.org/page";int failedRequests=0;Func<Uri,byte[]> fail=uri=>{failedRequests++;throw new IOException("synthetic failure");};
   check(BookmarkIcons.Load(output,failure,fail)==null,"failed icon returns generic fallback without failing bookmark");int attempts=failedRequests;BookmarkIcons.Load(output,failure,fail);check(attempts==3&&failedRequests==attempts,"bounded origin discovery and failed icon are not repeatedly requested for 24 hours");
   string recovery=BookmarkIcons.PathFor(output,failure)+".hint";BookmarkIcons.Remember(output,failure,"https://fail.example.org/new.png");File.SetLastWriteTimeUtc(recovery,DateTime.UtcNow.AddSeconds(1));check(BookmarkIcons.Load(output,failure,fetch)!=null,"new browser icon hint can recover a previously failed lookup");
   string rootFallback="https://fallback.example.org/path?token=secret";BookmarkIcons.Remember(output,rootFallback,"https://cdn.example.org/unsupported.svg");var seen=new List<string>();var recovered=BookmarkIcons.Load(output,rootFallback,uri=>{seen.Add(uri.AbsoluteUri);if(seen.Count==1)return new byte[12];return png;});
   check(recovered!=null&&seen.Last()=="https://fallback.example.org/favicon.ico","invalid preferred icon falls back to origin only without page query");
   check(Reject(()=>BookmarkIcons.Decode(new byte[BookmarkIcons.MaximumBytes+1]))&&Reject(()=>BookmarkIcons.Decode(System.Text.Encoding.UTF8.GetBytes("<svg onload='bad'></svg>")))&&Reject(()=>BookmarkIcons.Decode(Png(1025,1))),"icon decoder rejects oversized data, SVG and excessive dimensions");
   using(var bytes=new MemoryStream()){System.Drawing.SystemIcons.Information.Save(bytes);check(BookmarkIcons.Decode(bytes.ToArray())!=null,"common ICO format decodes without extra dependency");}
   File.SetLastWriteTimeUtc(stem+".png",DateTime.UtcNow.AddDays(-31));check(BookmarkIcons.Load(output,site,fail)!=null,"failed refresh keeps older cached icon");
   using(var lib=new Library(Path.Combine(output,"library"))){
    var request=new Dictionary<string,object>{{"action","bookmark"},{"source",site},{"name","示例网址"},{"iconUrl","https://example.com/one.ico"}};NativeBridge.Handle(lib,request);string hint=BookmarkIcons.PathFor(lib.Root,site)+".hint";check(File.ReadAllText(hint).EndsWith("one.ico"),"native bookmark save remembers browser-provided icon");
    request["iconUrl"]="https://example.com/two.ico";NativeBridge.Handle(lib,request);check(File.ReadAllText(hint).EndsWith("one.ico"),"duplicate confirmation does not alter icon cache before choice");request["duplicateAction"]="reuse";NativeBridge.Handle(lib,request);check(File.ReadAllText(hint).EndsWith("two.ico")&&lib.All().Count==1,"confirmed reuse may update icon hint without duplicating bookmark");
    var item=lib.All().Single();BookmarkIcons.Load(lib.Root,site,fetch);var rows=GalleryLayout.Build(new[]{item,item,item,item},800,178);check(rows.Count==2&&rows.All(r=>r.Height==62)&&rows.SelectMany(r=>r.Items).All(t=>t.ImageHeight==0),"bookmark cards remain compact around small icons, without image-size blank boxes");
    var previous=ThumbnailImage.CurrentLibrary;ThumbnailImage.CurrentLibrary=lib;
    try{
     var app=Application.Current??new Application();Theme.Set("dark");
     var icon=new BookmarkIcon{DataContext=item};var panel=new StackPanel{Width=400,Background=(Brush)app.Resources["Canvas"],Margin=new Thickness(12)};
     foreach(string label in new[]{"有缓存的网站图标","无图标时的统一占位"}){
      var line=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(12)};var display=label.StartsWith("有")?icon:new BookmarkIcon();line.Children.Add(display);var name=new TextBlock{Text=label,Margin=new Thickness(12,0,0,0),VerticalAlignment=VerticalAlignment.Center};name.SetResourceReference(TextBlock.ForegroundProperty,"Text");line.Children.Add(name);panel.Children.Add(line);
     }
     icon.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); // detached visual is deliberately cache-only
     // Inspect actual production icon control in a bounded, off-screen window.
     var window=new Window{Content=panel,Width=430,Height=160,ShowInTaskbar=false,ShowActivated=false,Left=-20000,Top=-20000,WindowStartupLocation=WindowStartupLocation.Manual};var shutdown=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
     try{window.Show();for(int i=0;i<30&&((Image)icon.Children[1]).Source==null;i++){Pump();System.Threading.Thread.Sleep(10);}check(((Image)icon.Children[1]).Source!=null&&((TextBlock)icon.Children[0]).Visibility==Visibility.Collapsed,"production favicon control displays cached icon and hides placeholder");
      panel.Measure(new Size(400,130));panel.Arrange(new Rect(0,0,400,130));panel.UpdateLayout();var render=new RenderTargetBitmap(400,130,96,96,PixelFormats.Pbgra32);render.Render(panel);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(render));using(var file=File.Create(Path.Combine(output,"icons-dark.png")))encoder.Save(file);
     }finally{window.Close();app.ShutdownMode=shutdown;}
    }finally{ThumbnailImage.CurrentLibrary=previous;}
   }
  }
 }
}
