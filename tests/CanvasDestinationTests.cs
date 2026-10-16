using System;
using System.Collections.Generic;
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
 // The production inspector/menu and destination commands, exercised with
 // synthetic originals, an isolated profile and no presented desktop windows.
 internal static class CanvasDestinationTests {
  const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
  static object Field(MainWindow main,string name){return typeof(MainWindow).GetField(name,Hidden).GetValue(main);}
  static FreeCanvasWindow Canvas(MainWindow main){return (FreeCanvasWindow)Field(main,"freeCanvas");}
  static void Select(MainWindow main,params Asset[] assets){var ids=(HashSet<string>)Field(main,"selection");ids.Clear();foreach(var asset in assets)ids.Add(asset.Id);typeof(MainWindow).GetMethod("UpdateInspector",Hidden).Invoke(main,null);}
  static void Click(MainWindow main){((Button)main.Window.FindName("SelectionCanvas")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
  static void Await(Task task){if(task==null)throw new Exception("Canvas destination action did not expose a task");DateTime deadline=DateTime.UtcNow.AddSeconds(30);while(!task.IsCompleted&&DateTime.UtcNow<deadline){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);Thread.Sleep(1);}if(!task.IsCompleted)throw new Exception("Canvas destination fixture timed out");task.GetAwaiter().GetResult();}
  static string Rows(Library library){return string.Join("\n",new[]{"assets","folders","memberships","desk_refs"}.SelectMany(table=>library.Db.Query("SELECT * FROM "+table).Select(row=>table+":"+string.Join("|",row.OrderBy(p=>p.Key).Select(p=>p.Key+"="+p.Value))).OrderBy(row=>row,StringComparer.Ordinal)));}
  static Dictionary<string,byte[]> Files(string root){return Directory.GetFiles(root,"*",SearchOption.AllDirectories).ToDictionary(p=>p,File.ReadAllBytes,StringComparer.OrdinalIgnoreCase);}
  static bool SameFiles(string root,Dictionary<string,byte[]> before){var current=Directory.GetFiles(root,"*",SearchOption.AllDirectories);return current.Length==before.Count&&before.All(p=>File.Exists(p.Key)&&File.ReadAllBytes(p.Key).SequenceEqual(p.Value));}
  static FreeCanvasItem Text(string value){return new FreeCanvasItem{Id=Guid.NewGuid().ToString("N"),Kind="text",Text=value,X=20,Y=30,Width=160,Height=60,FontSize=18,Color="auto"};}
  static void Render(Window picker,string path,int width,int height){var content=(FrameworkElement)picker.Content;content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);var backing=new DrawingVisual();using(var paint=backing.RenderOpen())paint.DrawRectangle(picker.Background??Theme.Brush("Canvas"),null,new Rect(0,0,width,height));bitmap.Render(backing);bitmap.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var output=File.Create(path))encoder.Save(output);}
  static bool Within(Window picker,FrameworkElement element,int width,int height){if(element.Visibility!=Visibility.Visible||element.ActualWidth<=0||element.ActualHeight<=0)return false;var content=(FrameworkElement)picker.Content;var bounds=element.TransformToAncestor(content).TransformBounds(new Rect(new Point(),element.RenderSize));return bounds.Left>=-.5&&bounds.Top>=-.5&&bounds.Right<=width+.5&&bounds.Bottom<=height+.5;}
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);var app=Application.Current??new Application();ShutdownMode oldShutdown=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
   var oldContext=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
   string oldData=Environment.GetEnvironmentVariable("MORI_DESK_DATA"),oldTheme=Theme.Current;bool oldClassic=Entry.ClassicEnabled,oldBoard=Entry.BoardEnabled,oldInteractive=Entry.InteractiveShell;MainWindow main=null;
   Environment.SetEnvironmentVariable("MORI_DESK_DATA",Path.Combine(root,"profile"));Entry.ClassicEnabled=true;Entry.BoardEnabled=false;Entry.InteractiveShell=false;
   try{
    string libraryRoot=Path.Combine(root,"library"),originals=Path.Combine(root,"originals");Directory.CreateDirectory(originals);new Settings{LibraryPath=libraryRoot,Theme="light"}.SaveTo(Path.Combine(Entry.DataRoot,"settings.json"));
    using(var library=new Library(libraryRoot)){
     var images=new List<Asset>();var sources=new List<string>();string category=library.CreateFolder("保留图库分类","");
     for(int i=0;i<4;i++){string path=Path.Combine(originals,"目标选择图片 "+i+".png");using(var bitmap=new System.Drawing.Bitmap(32+i*5,24+i*5)){using(var paint=System.Drawing.Graphics.FromImage(bitmap))paint.Clear(System.Drawing.Color.FromArgb(50+i*20,80+i*10,130));bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png);}sources.Add(path);images.Add(library.Import(path,category,"图片 "+i,"https://example.invalid/"+i,"标签","备注").Item);}
     main=new MainWindow(library){PreviewMode=true,FreeCanvasPresentationSuppressedForTests=true};main.SetSection("pictures");var store=new FreeCanvasStore(Entry.DataRoot);
     string rows=Rows(library);var sourceBytes=Files(originals);var managedBytes=Files(Path.Combine(libraryRoot,"原图"));var times=sources.ToDictionary(p=>p,File.GetLastWriteTimeUtc);byte[] settings=File.ReadAllBytes(Path.Combine(Entry.DataRoot,"settings.json"));
     Select(main,images[0],images[1]);var blankFiles=Files(store.Root);int prompts=0;var initialTask=main.FreeCanvasImportForTests;
     main.CanvasDestinationChoiceForTests=count=>{prompts++;check(count==2,"destination chooser receives the actual two selected image count");return null;};Click(main);
     check(prompts==1&&Canvas(main)==null&&store.List().Count==0&&SameFiles(store.Root,blankFiles)&&main.FreeCanvasImportForTests==initialTask&&Rows(library)==rows,"cancelling the real canvas button creates no project/window/import or library mutation");

     // Selection can refresh while a destination decision is pending. The
     // command must still use the exact IDs selected when it was invoked.
     main.CanvasDestinationChoiceForTests=count=>{prompts++;Select(main,images[2],images[3]);return new CanvasDestination{NewName="同名项目"};};Click(main);Await(main.FreeCanvasImportForTests);
     var canvas=Canvas(main);string first=canvas.DocumentForTests.Id;check(store.List().Count==1&&canvas.DocumentForTests.Name=="同名项目"&&canvas.DocumentForTests.Items.Count==2&&canvas.DocumentForTests.Items.Select(i=>i.Source).OrderBy(p=>p).SequenceEqual(images.Take(2).Select(library.FullPath).OrderBy(p=>p)),"new destination uses the frozen source selection rather than later inspector selection");
     check(!main.Window.IsVisible&&!canvas.IsVisible&&canvas.Owner==null,"destination regression never presents or owns a desktop canvas window");
     canvas.DrawItemForTests(Text("第一个项目的未保存文字"));Select(main,images[2],images[3]);main.CanvasDestinationChoiceForTests=count=>{prompts++;return new CanvasDestination{NewName="同名项目"};};Click(main);Await(main.FreeCanvasImportForTests);
     string second=canvas.DocumentForTests.Id;check(second!=first&&store.List().Count==2&&store.List().All(p=>p.Name=="同名项目")&&store.Load(first).Items.Count==3&&store.Load(first).Items.Any(i=>i.Text=="第一个项目的未保存文字")&&store.Load(second).Items.Count==2,"new destination while a canvas is open creates one independent project and saves the previous edits, even with duplicate names");
     byte[] secondBefore=File.ReadAllBytes(Path.Combine(store.Root,second,"project.json"));Select(main,images[2],images[3]);main.CanvasDestinationChoiceForTests=count=>{prompts++;return new CanvasDestination{ProjectId=first};};Click(main);Await(main.FreeCanvasImportForTests);
     check(canvas.DocumentForTests.Id==first&&store.Load(first).Items.Count==5&&File.ReadAllBytes(Path.Combine(store.Root,second,"project.json")).SequenceEqual(secondBefore)&&store.List().Count==2,"explicit non-current destination appends only to its exact ID, not the current or same-named project");
     var cancelFiles=Files(store.Root);var beforeTask=main.FreeCanvasImportForTests;int beforeCount=canvas.ItemCount;main.CanvasDestinationChoiceForTests=count=>{prompts++;return null;};Click(main);
     check(Canvas(main)==canvas&&canvas.DocumentForTests.Id==first&&canvas.ItemCount==beforeCount&&main.FreeCanvasImportForTests==beforeTask&&SameFiles(store.Root,cancelFiles),"cancelling with an existing canvas preserves its project, files and previous task");

     canvas.Close();check(Canvas(main)==null,"closing a destination canvas clears only the window slot");Select(main,images[0],images[1]);main.CanvasDestinationChoiceForTests=count=>{prompts++;return new CanvasDestination{ProjectId=second};};Click(main);Await(main.FreeCanvasImportForTests);canvas=Canvas(main);
     check(canvas.DocumentForTests.Id==second&&store.Load(second).Items.Count==4&&store.Load(first).Items.Count==5&&store.List().Count==2,"reopening from selected pictures still uses the chosen non-last project instead of last-opened memory");

     var stableFiles=Files(store.Root);string current=canvas.DocumentForTests.Id;int currentCount=canvas.ItemCount;
     Await(main.AddSelectionToCanvasAsync(images.Take(2).Select(a=>a.Id),new CanvasDestination{ProjectId=Guid.NewGuid().ToString("N")}));
     check(canvas.DocumentForTests.Id==current&&canvas.ItemCount==currentCount&&SameFiles(store.Root,stableFiles),"a disappeared destination cannot fall back to the current canvas or create a replacement project");
     string unavailable=library.FullPath(images[0]),staged=unavailable+".synthetic-unavailable";File.Move(unavailable,staged);
     try{Await(main.AddSelectionToCanvasAsync(new[]{images[0].Id},new CanvasDestination{NewName="不应创建"}));check(canvas.DocumentForTests.Id==current&&canvas.ItemCount==currentCount&&SameFiles(store.Root,stableFiles),"a missing selected image is revalidated before new-project creation and cannot produce a ghost canvas");}finally{File.Move(staged,unavailable);}

     string manifest=Path.Combine(store.Root,current,"project.json");byte[] intact=File.ReadAllBytes(manifest);canvas.DrawItemForTests(Text("保存冲突时必须保留"));var concurrent=store.Load(current);concurrent.Name="模拟另一窗口修改";store.Save(concurrent);byte[] concurrentBytes=File.ReadAllBytes(manifest);int projectsBefore=store.List().Count,failures=0;canvas.SaveFailureForTests=message=>failures++;
     Await(main.AddSelectionToCanvasAsync(images.Take(2).Select(a=>a.Id),new CanvasDestination{NewName="保存失败不能新建"}));
     check(store.List().Count==projectsBefore&&Canvas(main)==canvas&&canvas.DocumentForTests.Id==current&&canvas.DocumentForTests.Items.Any(i=>i.Text=="保存冲突时必须保留")&&File.ReadAllBytes(manifest).SequenceEqual(concurrentBytes),"failed current save preserves in-memory edits/concurrent manifest and cannot create a new destination ghost");
     File.WriteAllBytes(manifest,intact);check(canvas.SaveForTests(),"restoring only the synthetic conflict fixture permits explicit save without losing current work");canvas.SaveFailureForTests=null;

     var pickerFiles=Files(store.Root);foreach(string theme in new[]{"light","dark"}){
      Theme.Set(theme);var picker=new CanvasDestinationWindow(main.Window,store,null,2);
      try{
       Render(picker,Path.Combine(root,"canvas-destination-"+theme+".png"),640,520);
       check(!picker.IsVisible&&picker.Result==null&&SameFiles(store.Root,pickerFiles),"constructing/rendering the "+theme+" destination chooser reads project metadata without creating or choosing a canvas");
       check(Within(picker,picker.Projects.NewCanvasButton,640,520)&&picker.Projects.ProjectsForTests.All(p=>Within(picker,picker.Projects.ProjectButtonForTests(p.Id),640,520)),"new/existing destination controls fit the narrow supported "+theme+" picker");
       picker.BeginNew();picker.ProjectName.Text=" ";picker.ChooseNew();check(picker.Result==null&&SameFiles(store.Root,pickerFiles),"blank new-project name cannot commit a choice or create a canvas in the "+theme+" picker");
       picker.ProjectName.Text="明确的新项目";Render(picker,Path.Combine(root,"canvas-destination-new-"+theme+".png"),640,520);
       check(Within(picker,picker.ProjectName,640,520)&&Within(picker,picker.ConfirmNew,640,520),"new-project name and commit action fit the narrow supported "+theme+" picker");
       picker.ChooseNew();check(picker.Result!=null&&picker.Result.IsNew&&picker.Result.NewName=="明确的新项目"&&SameFiles(store.Root,pickerFiles),"confirming New returns only a named destination decision, never creates project files inside the "+theme+" picker");
      }finally{picker.Close();}
      var existingPicker=new CanvasDestinationWindow(main.Window,store,null,2);try{existingPicker.Projects.ProjectButtonForTests(first).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));check(existingPicker.Result!=null&&!existingPicker.Result.IsNew&&existingPicker.Result.ProjectId==first&&SameFiles(store.Root,pickerFiles),"existing picker decision uses the clicked exact project ID without adding or opening images in "+theme);}finally{existingPicker.Close();}
     }
     check(prompts==6,"each real inspector activation requests an explicit destination, including cancel and reopened canvas");
     check(Rows(library)==rows&&SameFiles(originals,sourceBytes)&&SameFiles(Path.Combine(libraryRoot,"原图"),managedBytes)&&times.All(p=>File.GetLastWriteTimeUtc(p.Key)==p.Value)&&File.ReadAllBytes(Path.Combine(Entry.DataRoot,"settings.json")).SequenceEqual(settings),"all destination cases preserve library metadata/categories/preferences, managed files and original source bytes/times");
     canvas.Close();main.Window.Close();main=null;
    }
   }finally{if(main!=null){if(Canvas(main)!=null)Canvas(main).Close();main.Window.Close();}ThumbnailImage.CurrentLibrary=null;Environment.SetEnvironmentVariable("MORI_DESK_DATA",oldData);Entry.ClassicEnabled=oldClassic;Entry.BoardEnabled=oldBoard;Entry.InteractiveShell=oldInteractive;Theme.Set(oldTheme);SynchronizationContext.SetSynchronizationContext(oldContext);app.ShutdownMode=oldShutdown;}
  }
 }
}
