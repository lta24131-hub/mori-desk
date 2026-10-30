using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Mori;

namespace MoriDesk {
 // Actual inspector/menu events and actual canvas copies, kept wholly offscreen
 // with a temporary profile and synthetic originals. No desktop interaction.
 internal static class SelectionCanvasTests {
  const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
  static object Field(MainWindow main,string name){return typeof(MainWindow).GetField(name,Hidden).GetValue(main);}
  static void Call(MainWindow main,string name,params object[] args){typeof(MainWindow).GetMethod(name,Hidden,null,args.Select(a=>a.GetType()).ToArray(),null).Invoke(main,args);}
  static Button Action(MainWindow main){return (Button)main.Window.FindName("SelectionCanvas");}
  static FreeCanvasWindow Canvas(MainWindow main){return (FreeCanvasWindow)Field(main,"freeCanvas");}
  static void Select(MainWindow main,params Asset[] assets){var ids=(HashSet<string>)Field(main,"selection");ids.Clear();foreach(var asset in assets)ids.Add(asset.Id);Call(main,"UpdateInspector");}
  static void Await(Task task){if(task==null)throw new Exception("Selection canvas action did not start a real import");var end=DateTime.UtcNow.AddSeconds(30);while(!task.IsCompleted&&DateTime.UtcNow<end){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);Thread.Sleep(1);}if(!task.IsCompleted)throw new Exception("Selection canvas import timed out");task.GetAwaiter().GetResult();}
  static string Rows(Library library){return string.Join("\n",new[]{"assets","folders","memberships","desk_refs"}.SelectMany(table=>library.Db.Query("SELECT * FROM "+table).Select(row=>table+":"+string.Join("|",row.OrderBy(p=>p.Key).Select(p=>p.Key+"="+p.Value))).OrderBy(row=>row,StringComparer.Ordinal)));}
  static Dictionary<string,byte[]> Files(IEnumerable<string> paths){return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(path=>path,File.ReadAllBytes,StringComparer.OrdinalIgnoreCase);}
  static bool SameFiles(Dictionary<string,byte[]> before){return before.All(p=>File.Exists(p.Key)&&File.ReadAllBytes(p.Key).SequenceEqual(p.Value));}
  static void Blocked(MainWindow main,Library library,Action<bool,string> check,string label){
   var button=Action(main);var task=main.FreeCanvasImportForTests;var canvas=Canvas(main);int count=canvas==null?0:canvas.DocumentForTests.Items.Count;string before=Rows(library);
   check(!button.IsEnabled,label+" disables the canvas action");button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
   check(main.FreeCanvasImportForTests==task&&Canvas(main)==canvas&&(canvas==null||canvas.DocumentForTests.Items.Count==count)&&Rows(library)==before,label+" remains a no-op even when click is raised programmatically");
  }
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);var app=Application.Current??new Application();var oldShutdown=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
   var context=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
   string oldData=Environment.GetEnvironmentVariable("MORI_DESK_DATA"),oldTheme=Theme.Current;bool oldClassic=Entry.ClassicEnabled,oldBoard=Entry.BoardEnabled,oldInteractive=Entry.InteractiveShell;
   Environment.SetEnvironmentVariable("MORI_DESK_DATA",Path.Combine(root,"profile"));Entry.ClassicEnabled=true;Entry.BoardEnabled=false;Entry.InteractiveShell=false;
   MainWindow main=null;
   try{
    string libraryRoot=Path.Combine(root,"library"),sourceRoot=Path.Combine(root,"originals");Directory.CreateDirectory(sourceRoot);new Settings{LibraryPath=libraryRoot,Theme="dark"}.SaveTo(Path.Combine(Entry.DataRoot,"settings.json"));
    using(var library=new Library(libraryRoot)){
     string folder=library.CreateFolder("图片项目","");var images=new List<Asset>();var sources=new List<string>();
     for(int i=0;i<6;i++){
      string extension=i==4?".jpg":i==5?".webp":".png",source=Path.Combine(sourceRoot,"选中图片 "+i+extension);
      if(i==5)File.WriteAllBytes(source,ImageCodecFixtures.WebP());
      else using(var bitmap=new System.Drawing.Bitmap(64+i,48+i)){using(var g=System.Drawing.Graphics.FromImage(bitmap))g.Clear(System.Drawing.Color.FromArgb(20+i*20,80+i*10,140+i*8));bitmap.Save(source,i==4?System.Drawing.Imaging.ImageFormat.Jpeg:System.Drawing.Imaging.ImageFormat.Png);}
      sources.Add(source);images.Add(library.Import(source,folder,"图片 "+i,"https://example.org/image/"+i,"标签 "+i,"备注 "+i).Item);
     }
     string moviePath=Path.Combine(sourceRoot,"video.mp4"),documentPath=Path.Combine(sourceRoot,"document.pdf"),directory=Path.Combine(sourceRoot,"folder");File.WriteAllText(moviePath,"synthetic video metadata only");File.WriteAllText(documentPath,"%PDF-1.4 synthetic document metadata only");Directory.CreateDirectory(directory);
     var movie=library.Import(moviePath,"","视频","","","").Item;var document=library.Import(documentPath,"","文档","","","").Item;var website=library.ImportBookmark("https://example.org/","网址","").Item;
     main=new MainWindow(library){PreviewMode=true,FreeCanvasPresentationSuppressedForTests=true};main.SetSection("pictures");
     string referenceId=library.AddReferenceAssets(new[]{sources[0]},"").Single(),directoryId=library.AddReferenceAssets(new[]{directory},"").Single();Call(main,"Refresh",true);var reference=library.All().Single(a=>a.Id==referenceId);var directoryAsset=library.All().Single(a=>a.Id==directoryId);
     var originals=Files(sources);var times=sources.ToDictionary(path=>path,File.GetLastWriteTimeUtc);var managed=Files(Directory.GetFiles(Path.Combine(libraryRoot,"原图"),"*",SearchOption.AllDirectories));byte[] config=File.ReadAllBytes(Path.Combine(Entry.DataRoot,"settings.json"));string initial=Rows(library);
     Select(main,images[0]);check(Action(main).Visibility==Visibility.Visible&&Action(main).IsEnabled,"single-image selection offers the same explicit canvas destination action as multiple images");
     int choices=0,requestedCount=0;string chosenProject=null;main.CanvasDestinationChoiceForTests=count=>{choices++;requestedCount=count;return chosenProject==null?new CanvasDestination{NewName="已明确新建的画布"}:new CanvasDestination{ProjectId=chosenProject};};
     Select(main,images[0],images[1]);var button=Action(main);check(button.Visibility==Visibility.Visible&&button.IsEnabled&&Convert.ToString(button.Content)=="加入画布"&&((Border)main.Window.FindName("BatchBar")).Visibility==Visibility.Visible&&main.Window.FindName("Compare")==null,"two-picture selection exposes its canvas destination action in the contextual selection bar");
     check(choices==0&&Canvas(main)==null,"selecting two images alone neither chooses a destination nor creates or opens a canvas");
     button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Await(main.FreeCanvasImportForTests);var canvas=Canvas(main);var store=new FreeCanvasStore(Entry.DataRoot);string project=canvas.DocumentForTests.Id;chosenProject=project;
     check(choices==1&&requestedCount==2&&canvas.DocumentForTests.Name=="已明确新建的画布"&&store.List().Count==1,"the toolbar action asks once for both selected images and creates only the explicitly chosen new canvas");
     check(canvas.DocumentForTests.Items.Count==2&&canvas.DocumentForTests.Items.Select(i=>i.Source).OrderBy(p=>p,StringComparer.Ordinal).SequenceEqual(images.Take(2).Select(library.FullPath).OrderBy(p=>p,StringComparer.Ordinal)),"actual inspector click imports every selected library image rather than only opening an empty canvas");
     check(canvas.DocumentForTests.Items.All(i=>File.ReadAllBytes(store.ImagePath(project,i.Image)).SequenceEqual(File.ReadAllBytes(i.Source)))&&!main.Window.IsVisible&&!canvas.IsVisible&&canvas.Owner==null,"batch canvas uses independent byte-identical copies and never presents fixture windows");
     check(canvas.SaveForTests()&&store.Load(project).Items.Count==2&&Rows(library)==initial,"batch canvas saves and reloads without changing library records or memberships");
     Select(main,images.ToArray());check(button.Visibility==Visibility.Visible&&button.IsEnabled,"six-picture selection remains usable beyond the old four-picture limit");double earlierBottom=canvas.DocumentForTests.Items.Max(i=>i.Bounds().Bottom);var firstTask=main.FreeCanvasImportForTests;button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));var pendingBatch=main.FreeCanvasImportForTests;
     check(!pendingBatch.IsCompleted&&!canvas.CanImportImages,"six-picture batch reaches an actual in-progress import before dispatcher continuations run");button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
     check(main.FreeCanvasImportForTests==pendingBatch&&!canvas.CanImportImages,"rapid repeat of the same canvas button neither starts a second batch nor replaces the waiting import task");Await(pendingBatch);
     check(choices==2&&main.FreeCanvasImportForTests!=firstTask&&Canvas(main)==canvas&&canvas.DocumentForTests.Id==project&&canvas.DocumentForTests.Items.Count==8&&store.List().Count==1,"second batch explicitly chooses the project and appends six images without creating a blank project");
     check(canvas.DocumentForTests.Items[2].Y>=earlierBottom+24,"automatic second-batch placement starts below existing image bounds instead of covering the first batch");
     check(canvas.DocumentForTests.Items.Skip(2).Select(i=>i.Source).OrderBy(p=>p,StringComparer.Ordinal).SequenceEqual(images.Select(library.FullPath).OrderBy(p=>p,StringComparer.Ordinal))&&store.Load(project).Items.Count==8,"every image in a batch above four is independently committed and reloadable");
     check(images.Count(i=>i.Ext==".png")==4&&images.Count(i=>i.Ext==".jpg")==1&&images.Count(i=>i.Ext==".webp")==1&&canvas.DocumentForTests.Items.Skip(2).All(i=>File.ReadAllBytes(store.ImagePath(project,i.Image)).SequenceEqual(File.ReadAllBytes(i.Source))),"mixed PNG JPEG and WebP batch commits byte-identical independent canvas images");
     canvas.UndoForTests();check(canvas.DocumentForTests.Items.Count==2&&canvas.SaveForTests()&&store.Load(project).Items.Count==2,"one canvas undo removes only the added batch and preserves the earlier two images");
     Select(main,reference);var menu=main.CreateAssetMenu(reference).Items.OfType<MenuItem>().Single(i=>Convert.ToString(i.Header)=="加入自由画布…");menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Await(main.FreeCanvasImportForTests);
     check(Canvas(main)==canvas&&canvas.DocumentForTests.Items.Count==3&&canvas.DocumentForTests.Items.Last().Source==sources[0]&&store.Load(project).Items.Count==3,"existing single-image menu resolves a local shortcut and adds its original into the same real canvas");
     Select(main,images[0],movie);check(button.Visibility==Visibility.Collapsed,"mixed video selection hides the image canvas entry");Blocked(main,library,check,"mixed image/video selection");
     Select(main,images[0],document);Blocked(main,library,check,"mixed image/document selection");Select(main,images[0],website);Blocked(main,library,check,"mixed image/website selection");Select(main,images[0],directoryAsset);Blocked(main,library,check,"mixed image/folder selection");Select(main);Blocked(main,library,check,"empty selection");
     Select(main,images[0],images[1]);typeof(MainWindow).GetField("busy",Hidden).SetValue(main,true);Call(main,"UpdateInspector");try{Blocked(main,library,check,"busy library");}finally{typeof(MainWindow).GetField("busy",Hidden).SetValue(main,false);Call(main,"UpdateInspector");}
     check(Rows(library)==initial&&SameFiles(originals)&&SameFiles(managed)&&times.All(p=>File.GetLastWriteTimeUtc(p.Key)==p.Value)&&File.ReadAllBytes(Path.Combine(Entry.DataRoot,"settings.json")).SequenceEqual(config),"all valid/invalid canvas actions preserve original bytes/times, gallery files, metadata, categories and preferences");
     canvas.Close();check(Canvas(main)==null&&!canvas.IsVisible,"closing the hidden canvas clears only its window slot");Select(main,images[4],images[5]);button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Await(main.FreeCanvasImportForTests);canvas=Canvas(main);
     check(choices==4&&canvas.DocumentForTests.Id==project&&canvas.DocumentForTests.Items.Count==5&&store.List().Count==1&&store.Load(project).Items.Count==5,"reopening through batch action asks again and reloads the chosen project before adding images");
     library.Trash(new[]{images[0].Id,images[1].Id},true);main.RequestNavigate("trash");Select(main,library.All().Single(a=>a.Id==images[0].Id),library.All().Single(a=>a.Id==images[1].Id));check(button.Visibility==Visibility.Collapsed,"recycle-bin picture selection hides canvas");Blocked(main,library,check,"recycle-bin image selection");
     check(SameFiles(originals)&&SameFiles(managed)&&times.All(p=>File.GetLastWriteTimeUtc(p.Key)==p.Value)&&library.All().Count==11&&library.Db.Query("PRAGMA foreign_key_check").Count==0,"recycle-bin regression and canvas imports preserve every synthetic file and library integrity");
     if(Canvas(main)!=null)Canvas(main).Close();main.Window.Close();main=null;
    }
   }finally{if(main!=null){if(Canvas(main)!=null)Canvas(main).Close();main.Window.Close();}ThumbnailImage.CurrentLibrary=null;Environment.SetEnvironmentVariable("MORI_DESK_DATA",oldData);Entry.ClassicEnabled=oldClassic;Entry.BoardEnabled=oldBoard;Entry.InteractiveShell=oldInteractive;Theme.Set(oldTheme);SynchronizationContext.SetSynchronizationContext(context);app.ShutdownMode=oldShutdown;}
  }
 }
}
