using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mori;

namespace MoriDesk {
 // All setters are injected. These fixtures never invoke the real Win32 API,
 // never display a window, and never change the user's desktop wallpaper.
 internal static class WallpaperTests {
  const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
  static void Reject(Action action,Action<bool,string> check,string message){bool failed=false;try{action();}catch{failed=true;}check(failed,message);}
  static void Select(MainWindow main,params Asset[] assets){var ids=(HashSet<string>)typeof(MainWindow).GetField("selection",Hidden).GetValue(main);ids.Clear();foreach(var asset in assets)ids.Add(asset.Id);typeof(MainWindow).GetMethod("UpdateInspector",Hidden).Invoke(main,null);}
  static MenuItem Menu(MainWindow main,Asset asset){return main.CreateAssetMenu(asset).Items.OfType<MenuItem>().SingleOrDefault(i=>Convert.ToString(i.Header)=="设为桌面壁纸");}
  static string Status(MainWindow main){return ((TextBlock)main.Window.FindName("Status")).Text;}
  static bool Busy(MainWindow main){return (bool)typeof(MainWindow).GetField("busy",Hidden).GetValue(main);}
  static void Wait(Func<bool> ready){var end=DateTime.UtcNow.AddSeconds(15);while(!ready()&&DateTime.UtcNow<end){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);Thread.Sleep(1);}if(!ready())throw new Exception("Wallpaper fixture timed out");}
  static void Await(Task task){Wait(()=>task.IsCompleted);task.GetAwaiter().GetResult();}
  static Asset Picture(string path){return new Asset{Id=Guid.NewGuid().ToString("N"),Ext=Path.GetExtension(path),ReferencePath=path,ReferenceSection="pictures"};}
  static void Save(string path,ImageFormat format){using(var bitmap=new Bitmap(6,4)){using(var g=Graphics.FromImage(bitmap))g.Clear(System.Drawing.Color.FromArgb(40,90,150));bitmap.Save(path,format);}}
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);string sources=Path.Combine(root,"sources"),cache=Path.Combine(root,"cache");Directory.CreateDirectory(sources);
   int calls=0;string requested=null;var service=new WallpaperService(cache,path=>{calls++;requested=path;check(File.Exists(path),"wallpaper setter receives an already-committed local BMP copy");});
   Reject(()=>new WallpaperService("relative",p=>{}),check,"wallpaper rejects relative cache root");
   Reject(()=>new WallpaperService("C:relative",p=>{}),check,"wallpaper rejects drive-relative cache root");
   Reject(()=>new WallpaperService(Path.GetPathRoot(root),p=>{}),check,"wallpaper rejects broad volume-root cache");
   Reject(()=>new WallpaperService("\\\\server\\share\\cache",p=>{}),check,"wallpaper cache is always local");
   Reject(()=>new WallpaperService(cache,null),check,"wallpaper setter must be explicit");
   check(!WallpaperService.Supports(null)&&!WallpaperService.Supports(new Asset{Ext=".mp4"})&&!WallpaperService.Supports(new Asset{Ext=".pdf"})&&!WallpaperService.Supports(new Asset{Ext=".url"})&&!WallpaperService.Supports(new Asset{Ext=".png",IsDirectory=true})&&!WallpaperService.Supports(new Asset{Ext=".png",Deleted=true}),"wallpaper eligibility excludes every non-image and deleted item");
   check(WallpaperService.Supports(new Asset{Ext=".PNG"})&&WallpaperService.Supports(new Asset{Ext=".svg"})&&WallpaperService.Supports(new Asset{Ext=".psd"})&&WallpaperService.Supports(new Asset{Ext=".ai"}),"wallpaper shares case-insensitive picture eligibility with existing image previews");
   string png=Path.Combine(sources,"透明 图片.png");using(var bitmap=new Bitmap(2,1,System.Drawing.Imaging.PixelFormat.Format32bppArgb)){bitmap.SetPixel(0,0,System.Drawing.Color.Transparent);bitmap.SetPixel(1,0,System.Drawing.Color.FromArgb(255,30,80,130));bitmap.Save(png,ImageFormat.Png);}
   byte[] original=File.ReadAllBytes(png);DateTime modified=File.GetLastWriteTimeUtc(png);
   string output=service.Set(Picture(png),png);
   check(calls==1&&requested==output&&Path.GetDirectoryName(output)==cache&&output!=png&&Path.GetExtension(output)==".bmp","wallpaper sets the independent cache path rather than mutable library/shortcut source");
   using(var image=new Bitmap(output)){check(image.Width==2&&image.Height==1&&image.RawFormat.Guid==ImageFormat.Bmp.Guid,"wallpaper uses a Windows-compatible static BMP without thumbnail downscaling");check(image.GetPixel(0,0).ToArgb()==System.Drawing.Color.White.ToArgb()&&image.GetPixel(1,0).R==30,"transparent background is flattened to white without changing opaque picture colors");}
   check(File.ReadAllBytes(png).SequenceEqual(original)&&File.GetLastWriteTimeUtc(png)==modified,"setting wallpaper leaves original bytes and timestamp unchanged");
   using(var exclusive=new FileStream(png,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(exclusive.Length==original.Length,"wallpaper conversion releases the original handle");
   File.Move(png,png+".moved");check(File.Exists(output),"cached wallpaper survives moving a local original");File.Move(png+".moved",png);
   string userFile=Path.Combine(cache,"personal.bmp"),unknown=Path.Combine(cache,"nook-wallpaper-keep.bmp");File.WriteAllText(userFile,"not owned");File.WriteAllText(unknown,"not owned");
   foreach(var pair in new[]{Tuple.Create(".jpg",ImageFormat.Jpeg),Tuple.Create(".bmp",ImageFormat.Bmp),Tuple.Create(".tiff",ImageFormat.Tiff)}){
    string path=Path.Combine(sources,"format"+pair.Item1);Save(path,pair.Item2);byte[] bytes=File.ReadAllBytes(path);string bitmap=service.Set(Picture(path),path);
    using(var image=new Bitmap(bitmap))check(image.Width==6&&image.Height==4,"actual "+pair.Item1+" fixture becomes full-sized wallpaper");check(File.ReadAllBytes(path).SequenceEqual(bytes),"wallpaper preserves "+pair.Item1+" source");
   }
   check(Directory.GetFiles(cache,"nook-wallpaper-*.bmp").Length==3&&File.ReadAllText(userFile)=="not owned"&&File.ReadAllText(unknown)=="not owned","cache cleanup keeps only active and previous owned copies and preserves unfamiliar files");
   string gif=Path.Combine(sources,"animation.gif");File.WriteAllBytes(gif,new byte[]{71,73,70,56,57,97,1,0,1,0,128,0,0,255,0,0,0,0,255,33,255,11,78,69,84,83,67,65,80,69,50,46,48,3,1,0,0,0,33,249,4,4,10,0,0,0,44,0,0,0,0,1,0,1,0,0,2,2,68,1,0,33,249,4,4,10,0,0,0,44,0,0,0,0,1,0,1,0,0,2,2,76,1,0,59});
   using(var image=System.Drawing.Image.FromFile(gif))check(image.GetFrameCount(FrameDimension.Time)==2,"GIF fixture contains two actual animation frames");byte[] animation=File.ReadAllBytes(gif);output=service.Set(Picture(gif),gif);
   using(var image=new Bitmap(output))check(image.GetPixel(0,0).R>240&&image.GetPixel(0,0).B<10,"animated GIF becomes its actual first frame, never a random playback frame");check(File.ReadAllBytes(gif).SequenceEqual(animation),"GIF source is unchanged and remains animated");
   string svg=Path.Combine(sources,"vector.svg");File.WriteAllText(svg,"<svg xmlns='http://www.w3.org/2000/svg' width='12' height='8'><rect width='12' height='8' fill='#285a96'/></svg>");output=service.Set(Picture(svg),svg);
   using(var image=new Bitmap(output))check(image.Width>1&&image.Height>1&&image.GetPixel(image.Width/2,image.Height/2).B>100,"SVG wallpaper uses existing safe vector renderer rather than unsupported Win32 source");
   string wide=Path.Combine(sources,"wide.png");using(var bitmap=new Bitmap(4100,1)){using(var g=Graphics.FromImage(bitmap))g.Clear(System.Drawing.Color.Teal);bitmap.Save(wide,ImageFormat.Png);}output=service.Set(Picture(wide),wide);using(var image=new Bitmap(output))check(image.Width==4096&&image.Height==1,"wallpaper output is bounded at 4096 pixels while preserving aspect ratio");
   int before=calls;int cacheBefore=Directory.GetFiles(cache).Length;
   Reject(()=>service.Set(Picture(Path.Combine(sources,"missing.png")),Path.Combine(sources,"missing.png")),check,"missing source does not report wallpaper success");
   Reject(()=>service.Set(new Asset{Ext=".mp4"},png),check,"non-image request rejected before conversion");
   Reject(()=>service.Set(Picture(png),"relative.png"),check,"relative source path rejected");
   Reject(()=>service.Set(Picture(png),sources),check,"directory source rejected");
   string invalid=Path.Combine(sources,"invalid.png");File.WriteAllText(invalid,"not a PNG");Reject(()=>service.Set(Picture(invalid),invalid),check,"corrupt PNG fails without setting Windows wallpaper");
   string empty=Path.Combine(sources,"empty.png");File.WriteAllBytes(empty,new byte[0]);Reject(()=>service.Set(Picture(empty),empty),check,"empty picture rejected");
   string invalidWebp=Path.Combine(sources,"invalid.webp");File.WriteAllText(invalidWebp,"not WebP");Reject(()=>service.Set(Picture(invalidWebp),invalidWebp),check,"unsupported special-format variant fails explicitly rather than pretending success");
   string text=Path.Combine(sources,"not-image.txt");File.WriteAllText(text,"fixture");Reject(()=>service.Set(Picture(png),text),check,"declared image cannot disguise non-image source extension");
   check(calls==before&&Directory.GetFiles(cache).Length==cacheBefore,"invalid requests never call setter or leave partial wallpaper output");
   var denied=new WallpaperService(cache,p=>{throw new Win32Exception(5,"fixture denied");});Reject(()=>denied.Set(Picture(png),png),check,"Windows rejection propagates instead of claiming success");
   check(Directory.GetFiles(cache).Length==cacheBefore&&File.Exists(output),"failed OS application removes only its new copy and retains active wallpaper");
   string blocked=Path.Combine(root,"blocked-cache");File.WriteAllText(blocked,"fixture");Reject(()=>new WallpaperService(blocked,p=>{}).Set(Picture(png),png),check,"cache write failure is explicit without overwriting existing file");check(File.ReadAllText(blocked)=="fixture","cache location collision preserves existing file");
   uint action=0,parameter=99,flags=0;string winPath=null;WallpaperService.ApplyWindows(output,(a,p,s,f)=>{action=a;parameter=p;winPath=s;flags=f;return true;},()=>0);
   check(action==0x0014&&parameter==0&&flags==3&&winPath==output,"Windows adapter persists wallpaper and broadcasts change only for the stable image path");
   bool error=false;try{WallpaperService.ApplyWindows(output,(a,p,s,f)=>false,()=>5);}catch(Win32Exception ex){error=ex.NativeErrorCode==5;}check(error,"Windows adapter preserves actual system failure code");
   Ui(root,png,check);
   check(File.ReadAllBytes(png).SequenceEqual(original)&&File.GetLastWriteTimeUtc(png)==modified,"all wallpaper fixture checks preserve original image content and time");
  }
  static void Ui(string root,string png,Action<bool,string> check){
   var app=Application.Current??new Application();var oldContext=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));bool oldClassic=Entry.ClassicEnabled;Entry.ClassicEnabled=true;
   using(var lib=new Library(Path.Combine(root,"ui-library"))){
    var image=lib.Import(png,"","Wallpaper picture","","","").Item;lib.EnableReferenceAssets();string[] refs=lib.AddReferenceAssets(new[]{png,Path.GetDirectoryName(png)},"");var shortcut=lib.All().Single(a=>a.Id==refs[0]);var directory=lib.All().Single(a=>a.Id==refs[1]);
    string video=Path.Combine(root,"video.mp4");File.WriteAllText(video,"metadata fixture");var movie=lib.Import(video,"","video","","","").Item;
    var main=new MainWindow(lib){PreviewMode=true};try{
     main.SetSection("pictures");Select(main,image);var item=Menu(main,image);check(item!=null&&item.IsEnabled,"single managed image menu exposes enabled desktop wallpaper command");
     int requested=0;main.WallpaperForTests=new WallpaperService(Path.Combine(root,"ui-wallpaper"),p=>{requested++;});
     item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Wait(()=>!Busy(main));check(requested==1&&Status(main)=="已设为桌面壁纸","actual context-menu click routes to injected setter and shows success only after it completes");
     main.SetSection("shortcuts");Select(main,shortcut);check(Menu(main,shortcut)!=null&&Menu(main,shortcut).IsEnabled,"local shortcut image supports the same wallpaper action");Await(main.SetDesktopWallpaperAsync(shortcut));check(requested==2,"shortcut wallpaper resolves its original address through the library");
     string moved=png+".unavailable";File.Move(png,moved);try{check(Menu(main,shortcut)!=null&&!Menu(main,shortcut).IsEnabled&&Convert.ToString(Menu(main,shortcut).ToolTip).Contains("重新定位"),"missing original has disabled wallpaper action with recovery explanation");Await(main.SetDesktopWallpaperAsync(shortcut));check(requested==2&&Status(main).StartsWith("壁纸设置失败："),"source disappearing after menu opening cannot produce false success");}finally{File.Move(moved,png);}
     Select(main,directory);check(Menu(main,directory)==null,"folder shortcut has no wallpaper action");
     main.SetSection("assets");Select(main,image,shortcut);check(Menu(main,image)==null,"multi-selected images have no ambiguous wallpaper action");Await(main.SetDesktopWallpaperAsync(image));check(requested==2,"multi-selection cannot set only one image silently");
     main.SetSection("videos");Select(main,movie);check(Menu(main,movie)==null,"video selection has no wallpaper action");
     main.SetSection("pictures");Select(main,image);typeof(MainWindow).GetField("busy",Hidden).SetValue(main,true);check(!Menu(main,image).IsEnabled,"wallpaper is disabled during conflicting library work");typeof(MainWindow).GetField("busy",Hidden).SetValue(main,false);
     main.WallpaperForTests=new WallpaperService(Path.Combine(root,"ui-denied"),p=>{throw new Win32Exception(5,"fixture denied");});Await(main.SetDesktopWallpaperAsync(image));check(Status(main).StartsWith("壁纸设置失败：")&&!Busy(main),"OS failure is visible and always releases UI busy state");
     main.WallpaperForTests=null;Await(main.SetDesktopWallpaperAsync(image));check(Status(main).Contains("测试窗口不修改")&&!Busy(main),"preview window without a fake sink is explicitly prohibited from changing real wallpaper");
     lib.Trash(new[]{image.Id},true);main.RequestNavigate("trash");image=lib.All().Single(a=>a.Id==image.Id);Select(main,image);check(Menu(main,image)==null,"recycle-bin images do not offer a live wallpaper action");
     check(!main.Window.IsVisible&&lib.All().Count==4,"wallpaper UI checks never show a test window or remove fixture assets");
    }finally{main.Window.Close();ThumbnailImage.CurrentLibrary=null;Entry.ClassicEnabled=oldClassic;SynchronizationContext.SetSynchronizationContext(oldContext);}
   }
  }
 }
}
