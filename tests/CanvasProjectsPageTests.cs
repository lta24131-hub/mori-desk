using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mori;

namespace MoriDesk {
 // Real routed controls and copies, using only synthetic files and a temporary
 // profile. No window is shown, desktop service started, or web engine created.
 internal static class CanvasProjectsPageTests {
  const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
  static object Field(MainWindow main,string name){return typeof(MainWindow).GetField(name,Hidden).GetValue(main);}
  static object Call(MainWindow main,string name,params object[] args){var methods=typeof(MainWindow).GetMethods(Hidden).Where(m=>m.Name==name&&m.GetParameters().Length==args.Length);var method=methods.Single(m=>m.GetParameters().Select((p,i)=>args[i]==null||p.ParameterType.IsInstanceOfType(args[i])).All(v=>v));return method.Invoke(main,args);}
  static FreeCanvasWindow Canvas(MainWindow main){return (FreeCanvasWindow)Field(main,"freeCanvas");}
  static string Manifest(FreeCanvasStore store,string id){return Path.Combine(store.Root,id,"project.json");}
  static void Click(Button button){button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
  static void Await(Task task){var deadline=DateTime.UtcNow.AddSeconds(30);while(!task.IsCompleted&&DateTime.UtcNow<deadline){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);Thread.Sleep(1);}if(!task.IsCompleted)throw new Exception("Canvas project import timed out");task.GetAwaiter().GetResult();}
  static string Rows(Library library){return string.Join("\n",new[]{"assets","folders","memberships","desk_refs"}.SelectMany(table=>library.Db.Query("SELECT * FROM "+table).Select(row=>table+":"+string.Join("|",row.OrderBy(p=>p.Key).Select(p=>p.Key+"="+p.Value))).OrderBy(row=>row,StringComparer.Ordinal)));}
  static FreeCanvasItem TextItem(string value){return new FreeCanvasItem{Id=Guid.NewGuid().ToString("N"),Kind="text",Text=value,X=20,Y=30,Width=160,Height=60,FontSize=18,Color="auto"};}
  static FreeCanvasItem Drawing(string kind){return new FreeCanvasItem{Id=Guid.NewGuid().ToString("N"),Kind=kind,X=10,Y=20,EndX=70,EndY=90,Stroke=2,Color="auto"};}
  static DataObject Files(params string[] paths){var data=new DataObject();data.SetData(DataFormats.FileDrop,paths);return data;}
  static DragEventArgs Drag(FrameworkElement target,IDataObject data,RoutedEvent route,DragDropEffects allowed=DragDropEffects.Copy){var e=(DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),Hidden,null,new object[]{data,DragDropKeyStates.None,allowed,target,new Point(12,12)},System.Globalization.CultureInfo.InvariantCulture);e.RoutedEvent=route;target.RaiseEvent(e);return e;}
  static void Render(MainWindow main,string path,int width){var body=(FrameworkElement)main.Window.Content;body.Measure(new Size(width,760));body.Arrange(new Rect(0,0,width,760));body.UpdateLayout();var bitmap=new RenderTargetBitmap(width,760,96,96,PixelFormats.Pbgra32);bitmap.Render(body);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var output=File.Create(path))encoder.Save(output);}
  static bool WithinWindow(MainWindow main,FrameworkElement element,int width){if(element==null||element.Visibility!=Visibility.Visible||element.ActualWidth<=0||element.ActualHeight<=0)return false;var body=(FrameworkElement)main.Window.Content;var bounds=element.TransformToAncestor(body).TransformBounds(new Rect(new Point(),element.RenderSize));return bounds.Left>=-.5&&bounds.Top>=-.5&&bounds.Right<=width+.5&&bounds.Bottom<=760.5;}
  static void CheckLayout(MainWindow main,CanvasProjectsPage page,int width,string appearance,Action<bool,string> check){
   var heading=(Grid)page.Children[0];var titles=((StackPanel)heading.Children[0]).Children.OfType<TextBlock>();var refresh=((StackPanel)page.NewCanvasButton.Parent).Children.OfType<Button>().Single(b=>Convert.ToString(b.Content)=="刷新");
   check(WithinWindow(main,page.NewCanvasButton,width)&&WithinWindow(main,refresh,width),"canvas new and refresh buttons fit entirely inside the supported "+width+" window ("+appearance+")");
   check(WithinWindow(main,heading,width)&&titles.All(t=>WithinWindow(main,t,width)),"canvas heading and project-count text fit entirely inside the supported "+width+" window ("+appearance+")");
   check(page.ProjectsForTests.All(p=>{var button=page.ProjectButtonForTests(p.Id);return WithinWindow(main,button,width)&&((Grid)button.Content).Children.OfType<TextBlock>().All(t=>WithinWindow(main,t,width));}),"canvas project rows and update-time text fit entirely inside the supported "+width+" window ("+appearance+")");
  }
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);var app=Application.Current??new Application();var oldShutdown=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;var oldContext=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
   string oldData=Environment.GetEnvironmentVariable("MORI_DESK_DATA"),oldTheme=Theme.Current;bool oldClassic=Entry.ClassicEnabled,oldBoard=Entry.BoardEnabled,oldInteractive=Entry.InteractiveShell;MainWindow main=null;
   Environment.SetEnvironmentVariable("MORI_DESK_DATA",Path.Combine(root,"profile"));Entry.ClassicEnabled=true;Entry.BoardEnabled=false;Entry.InteractiveShell=false;
   try{
    string libraryRoot=Path.Combine(root,"library"),originals=Path.Combine(root,"originals");Directory.CreateDirectory(originals);new Settings{LibraryPath=libraryRoot,Theme="dark",CanvasDefaultTopmost=true}.SaveTo(Path.Combine(Entry.DataRoot,"settings.json"));
    using(var library=new Library(libraryRoot)){
     var sources=new List<string>();var images=new List<Asset>();string folder=library.CreateFolder("图库项目","");
     for(int i=0;i<2;i++){string source=Path.Combine(originals,"合成图片 "+i+".png");using(var bitmap=new System.Drawing.Bitmap(48+i*16,32+i*16)){using(var paint=System.Drawing.Graphics.FromImage(bitmap))paint.Clear(System.Drawing.Color.FromArgb(60+i*50,90+i*30,140));bitmap.Save(source,System.Drawing.Imaging.ImageFormat.Png);}sources.Add(source);images.Add(library.Import(source,folder,"素材 "+i,"https://example.invalid/"+i,"标签","备注").Item);}
     string directory=Path.Combine(originals,"不展开文件夹"),notImage=Path.Combine(originals,"非图片.txt"),moviePath=Path.Combine(originals,"非图片视频.mp4");Directory.CreateDirectory(directory);File.WriteAllText(notImage,"synthetic text");File.WriteAllText(moviePath,"synthetic video metadata only");var movie=library.Import(moviePath,"","视频","","","").Item;library.EnableReferenceAssets();string directoryId=library.AddReferenceAssets(new[]{directory},"").Single();
     main=new MainWindow(library){PreviewMode=true,FreeCanvasPresentationSuppressedForTests=true};main.SetSection("pictures");var selected=(HashSet<string>)Field(main,"selection");selected.Add(images[0].Id);Call(main,"UpdateInspector");
     string libraryBefore=Rows(library);var managed=Directory.GetFiles(Path.Combine(libraryRoot,"原图"),"*",SearchOption.AllDirectories).ToDictionary(p=>p,File.ReadAllBytes);var sourceBytes=sources.ToDictionary(p=>p,File.ReadAllBytes);var sourceTimes=sources.ToDictionary(p=>p,File.GetLastWriteTimeUtc);
     var store=new FreeCanvasStore(Entry.DataRoot);var nav=(Button)main.Window.FindName("CanvasNav");
     check(nav!=null&&ReferenceEquals(nav.Parent,main.Window.FindName("SidebarCollectionLinks"))&&main.Window.FindName("CanvasEntry")==null,"projects use one direct left-navigation entry without a duplicate top-level canvas entry");
     var navigation=(Panel)nav.Parent;var navigationButtons=navigation.Children.OfType<Button>().ToList();
     check(Convert.ToString(nav.Content)=="项目"&&navigationButtons.Count(b=>Convert.ToString(b.Content)=="项目")==1&&!navigationButtons.Any(b=>Convert.ToString(b.Content)=="画布")&&!navigation.Children.OfType<TextBlock>().Any(t=>t.Text=="项目"||t.Text=="收藏"),"sidebar has one actionable Projects label, not redundant Projects/Canvas or collection headings");
     check(navigationButtons.SequenceEqual(new[]{nav,(Button)main.Window.FindName("ReferenceNav"),(Button)main.Window.FindName("Bookmarks")})&&nav.Visibility==Visibility.Visible,"Projects, local shortcuts and websites are distinct direct collection entries, separate from file-type filtering");
     Click(nav);var page=main.CanvasProjects;CanvasPageTestSupport.Wait(page);check(main.CanvasProjectsMode&&page!=null&&page.ProjectsForTests.Count==0&&page.Visibility==Visibility.Visible,"Projects opens its persistent thumbnail gallery directly, without an intermediate Canvas page");
     check(new[]{"Unfiled","Favorites","SidebarScopeDivider"}.All(name=>((UIElement)main.Window.FindName(name)).Visibility==Visibility.Collapsed),"project gallery hides material/website-specific filters instead of inheriting the previous scope");
     var footer=(Border)main.Window.FindName("SidebarFooter");var trash=(Button)main.Window.FindName("Trash");
     check(footer.Visibility==Visibility.Hidden&&footer.InputHitTest(new Point(12,12))==null&&!trash.Focus(),"project recycle-bin space is preserved without accepting pointer input or keyboard focus");
     check(((StackPanel)((Grid)page.Children[0]).Children[0]).Children.OfType<TextBlock>().First().Text=="项目","project gallery title agrees with its direct navigation label");
     Click(page.NewCanvasButton);var canvas=Canvas(main);string first=canvas.DocumentForTests.Id;
     check(store.List().Count==1&&canvas.DocumentForTests.Name=="画布 1"&&canvas.DocumentForTests.Pinned&&canvas.DocumentForTests.Items.Count==0,"one new-project click creates exactly one empty canvas and respects default pinning");
     check(!main.Window.IsVisible&&!canvas.IsVisible&&canvas.Owner==null&&main.CanvasProjectsMode,"new canvas fixtures stay hidden and keep the project page open");
     var second=store.Create("方案 B");second.Pinned=true;second.Items.Add(TextItem("可见标题"));second.Items.Add(Drawing("line"));second.Items.Add(Drawing("ellipse"));store.Save(second);
     var damaged=store.Create("损坏记录");File.WriteAllText(Manifest(store,damaged.Id),"{ synthetic damaged manifest");var manifests=store.List().ToDictionary(p=>p.Id,p=>File.ReadAllBytes(Manifest(store,p.Id)));CanvasPageTestSupport.Wait(page);
     var detail=page.ProjectsForTests.Single(p=>p.Id==second.Id);var broken=page.ProjectsForTests.Single(p=>p.Id==damaged.Id);
     check(detail.Name==second.Name&&detail.Updated==second.Updated&&detail.Images==0&&detail.Text==1&&detail.Drawing==2&&detail.Pinned&&!string.IsNullOrEmpty(broken.Error),"project list exposes names, update times, content counts, pinning and unreadable records");
     CanvasPageTestSupport.Wait(page);check(manifests.All(p=>File.ReadAllBytes(Manifest(store,p.Key)).SequenceEqual(p.Value)),"repeated project listing does not rewrite manifests or update timestamps");
     Click(page.ProjectButtonForTests(second.Id));check(ReferenceEquals(canvas,Canvas(main))&&canvas.DocumentForTests.Id==second.Id&&store.List().Count==3,"one project-row click reuses the canvas window and opens the exact project ID");
     Click(page.ProjectButtonForTests(damaged.Id));check(canvas.DocumentForTests.Id==second.Id&&File.ReadAllBytes(Manifest(store,damaged.Id)).SequenceEqual(manifests[damaged.Id]),"unreadable project click preserves the current canvas and damaged record");
     canvas.Close();check(Canvas(main)==null&&main.CanvasProjectsMode&&page.Visibility==Visibility.Visible&&page.ProjectsForTests.Count==3,"closing a canvas leaves its project list available");
     Click(page.ProjectButtonForTests(first));canvas=Canvas(main);check(canvas.DocumentForTests.Id==first&&store.List().Count==3,"reopening an older row selects that ID without creating a blank extra project");
     check(!main.OpenFreeCanvasToProject("../invalid",null)&&ReferenceEquals(canvas,Canvas(main))&&canvas.DocumentForTests.Id==first,"invalid project ID cannot replace the current window or project");
     var external=Files(sources[0],sources[1],directory,notImage,moviePath);var drop=Drag(page.ProjectButtonForTests(second.Id),external,UIElement.PreviewDropEvent);Await(main.FreeCanvasImportForTests);
     check(drop.Handled&&drop.Effects==DragDropEffects.Copy&&canvas.DocumentForTests.Id==second.Id&&canvas.DocumentForTests.Items.Count(i=>i.Kind=="image")==2,"window preview drop imports only existing images into the explicitly targeted project");
     check(store.Load(first).Items.Count==0&&store.Load(second.Id).Items.Count==5&&Rows(library)==libraryBefore,"card drop never falls through to library collection or the previously opened project");
     var internalDrop=new DataObject();internalDrop.SetData("Mori.AssetIds",new[]{images[0].Id,images[1].Id,images[0].Id,directoryId,movie.Id,"missing"});internalDrop.SetData(DataFormats.FileDrop,new[]{notImage});
     drop=Drag(page.ProjectButtonForTests(first),internalDrop,UIElement.PreviewDropEvent);Await(main.FreeCanvasImportForTests);
     check(drop.Handled&&drop.Effects==DragDropEffects.Copy&&canvas.DocumentForTests.Id==first&&canvas.DocumentForTests.Items.Count==2&&canvas.DocumentForTests.Items.Select(i=>i.Source).OrderBy(p=>p).SequenceEqual(images.Select(library.FullPath).OrderBy(p=>p)),"multi-image gallery IDs resolve real assets, deduplicate, exclude folders and ignore attached FileDrop payloads");
     check(canvas.DocumentForTests.Items.All(i=>File.ReadAllBytes(store.ImagePath(first,i.Image)).SequenceEqual(File.ReadAllBytes(i.Source))),"project image copies remain byte-identical and independently stored");
     var task=main.FreeCanvasImportForTests;int itemCount=canvas.ItemCount;var wrongTyped=new DataObject();wrongTyped.SetData("Mori.AssetIds","not an ID array");wrongTyped.SetData(DataFormats.FileDrop,sources.ToArray());
     check(!page.DropOnProject(first,wrongTyped)&&!page.DropOnProject(first,Files(directory,notImage,"relative.png"))&&!page.DropOnProject(damaged.Id,external)&&!page.DropOnProject("bad",external)&&main.FreeCanvasImportForTests==task&&canvas.ItemCount==itemCount,"invalid IDs, typed internal data, folders, non-images and unreadable targets are rejected without fallback import");
     var stale=new DataObject();stale.SetData("Mori.AssetIds",new[]{images[0].Id});library.Trash(new[]{images[0].Id},true);
     try{check(main.CanvasDropPaths(stale).Length==1&&main.CanvasCommitPaths(stale).Length==0&&!page.DropOnProject(first,stale)&&main.FreeCanvasImportForTests==task&&canvas.ItemCount==itemCount,"hover can use the gallery snapshot but mouse release revalidates current records and rejects a newly trashed image");}finally{library.Trash(new[]{images[0].Id},false);}
     typeof(MainWindow).GetField("busy",Hidden).SetValue(main,true);try{check(!page.DropOnProject(first,external)&&!main.ShowCanvasProjects(),"busy library refuses project navigation and image drops");}finally{typeof(MainWindow).GetField("busy",Hidden).SetValue(main,false);}
     main.HideCanvasProjects();check(!main.CanvasProjectsMode&&page.Visibility==Visibility.Collapsed&&((Grid)main.Window.FindName("Center")).Visibility==Visibility.Visible,"return from projects restores the library presentation");
     var notes=(TextBox)main.Window.FindName("Notes");notes.Text="未保存右侧信息";main.PendingInformationChoice=()=>MessageBoxResult.Cancel;var hover=Drag(nav,internalDrop,UIElement.PreviewDragOverEvent);check(hover.Handled&&!main.CanvasProjectsMode&&hover.Effects==DragDropEffects.None&&notes.Text=="未保存右侧信息"&&((TextBlock)main.Window.FindName("Status")).Text.Contains("尚未保存"),"sidebar drag refuses unsaved inspector navigation without opening a dialog or discarding the draft");notes.Text=images[0].Notes;main.PendingInformationChoice=null;
     hover=Drag(nav,internalDrop,UIElement.PreviewDragOverEvent);check(hover.Handled&&main.CanvasProjectsMode&&Canvas(main)==canvas&&canvas.ItemCount==itemCount,"holding gallery images over the left label opens projects without prematurely importing");
     var target=page.ProjectButtonForTests(second.Id);hover=Drag(target,internalDrop,UIElement.PreviewDragOverEvent);check(hover.Handled&&hover.Effects==DragDropEffects.Copy&&((SolidColorBrush)target.BorderBrush).Color==((SolidColorBrush)Theme.Brush("Accent")).Color,"continuing a held gallery drag to a readable project highlights that exact target");
     drop=Drag(nav,internalDrop,UIElement.PreviewDropEvent);check(drop.Handled&&drop.Effects==DragDropEffects.None&&main.FreeCanvasImportForTests==task&&Rows(library)==libraryBefore,"dropping on the sidebar itself is consumed and requests a specific project");
     drop=Drag(page,external,UIElement.PreviewDropEvent);check(drop.Handled&&drop.Effects==DragDropEffects.None&&canvas.ItemCount==itemCount&&Rows(library)==libraryBefore,"project-page blank space never imports into the library or most recent canvas");
     int edited=0;check(!(bool)Call(main,"Edit","stale menu",(Action)(()=>edited++))&&edited==0,"central gallery mutation guard blocks stale menu callbacks in canvas mode");
     foreach(string action in new[]{"Trash","DeleteForever","RestoreTrash","UndoLast","Paste","Star","Categorize","SaveMetadata","ChooseMediaFiles","ChooseReferenceFiles","ShowBookmarkImport"})Call(main,action);
     Call(main,"ImportPaths",new[]{sources[0]},"");Call(main,"ImportBookmarkFile",notImage,"stale");Call(main,"CollectClassicPaths",new[]{sources[0]},"","");Call(main,"NewFolder","");Call(main,"RenameFolder",folder);Call(main,"DeleteFolder",folder);Call(main,"SearchByImage",false);Call(main,"ApplyCategory",new[]{images[0].Id},"",true,false);Call(main,"MoveLocalCollection",new[]{directoryId},"");main.SetSelectedOverviewHidden(true);
     check(Rows(library)==libraryBefore&&selected.SetEquals(new[]{images[0].Id})&&main.CanvasProjectsMode,"hidden gallery mutation, paste, undo, indexing, categorization and collection entry points are no-ops");
     new System.Windows.Interop.WindowInteropHelper(main.Window).EnsureHandle();var hwnd=System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(main.Window).Handle);
     foreach(Key key in new[]{Key.Delete,Key.Space,Key.V,Key.Z,Key.A,Key.Escape})main.Window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,hwnd,0,key){RoutedEvent=Keyboard.PreviewKeyDownEvent});
     check(Rows(library)==libraryBefore&&selected.SetEquals(new[]{images[0].Id})&&main.CanvasProjectsMode,"project-page keyboard input cannot operate on retained gallery selection");
     var menu=main.CreateMoreMenu().Items.OfType<MenuItem>().Select(i=>Convert.ToString(i.Header)).ToArray();check(menu.SequenceEqual(new[]{"新建画布","刷新项目","设置","退出 Nook"}),"project page has a small mode-specific More menu without hidden-gallery operations");
     string active=canvas.DocumentForTests.Id,manifest=Manifest(store,active);byte[] stable=File.ReadAllBytes(manifest);canvas.DrawItemForTests(TextItem("尚未保存的画布文字"));var concurrent=store.Load(active);concurrent.Name="另一窗口已更新";store.Save(concurrent);byte[] concurrentBytes=File.ReadAllBytes(manifest);int projectsBefore=store.List().Count,failures=0;canvas.SaveFailureForTests=message=>failures++;
     Click(page.NewCanvasButton);check(store.List().Count==projectsBefore&&Canvas(main)==canvas&&canvas.DocumentForTests.Id==active&&canvas.DocumentForTests.Items.Any(i=>i.Text=="尚未保存的画布文字")&&failures==1&&File.ReadAllBytes(manifest).SequenceEqual(concurrentBytes),"new-project action first saves the current canvas and cannot create a ghost project when that save conflicts");
     File.WriteAllBytes(manifest,stable);check(canvas.SaveForTests(),"synthetic save-conflict fixture is repaired without losing the in-memory canvas edit");canvas.SaveFailureForTests=null;
     string moved=canvas.DocumentForTests.Items.First(i=>i.Kind=="image").Id;double oldX=canvas.DocumentForTests.Items.Single(i=>i.Id==moved).X;canvas.BeginMoveForTests(moved,new Vector(19,7));Click(page.NewCanvasButton);
     check(store.List().Count==projectsBefore+1&&canvas.DocumentForTests.Id!=active&&store.Load(active).Items.Single(i=>i.Id==moved).X==oldX+19&&canvas.DocumentForTests.Items.Count==0,"successful new-project action commits an active gesture, preserves the old project and creates only one new empty project");
     check(main.BrowseBookmark("https://example.invalid/canvas")&&!main.CanvasProjectsMode&&page.Visibility==Visibility.Collapsed&&!main.BrowserPane.InitializationStarted,"opening an offscreen browser hides projects without starting a web engine");var browser=main.BrowserPane;main.ShowCanvasProjects();check(main.CanvasProjectsMode&&!main.BrowserMode&&browser.IsDisposed,"returning to projects disposes the browser instead of layering pages");
     Call(main,"ShowBoard","all");check(!main.CanvasProjectsMode&&(bool)Field(main,"boardMode")&&page.Visibility==Visibility.Collapsed,"board navigation hides the project page");main.ShowCanvasProjects();check(!(bool)Field(main,"boardMode")&&main.CanvasProjectsMode,"projects hide the board before becoming visible");
     Entry.ClassicEnabled=false;Call(main,"ShowReferences");check(!main.CanvasProjectsMode&&(bool)Field(main,"referenceMode"),"reference-page navigation hides projects");main.ShowCanvasProjects();check(!(bool)Field(main,"referenceMode")&&main.CanvasProjectsMode,"projects hide the reference page before becoming visible");Entry.ClassicEnabled=true;
     int narrowWidth=(int)Math.Floor(main.Window.MinWidth);
     check(narrowWidth>0&&narrowWidth<=1040&&main.Window.MinWidth<=main.Window.Width,"project-page narrow visual evidence uses the screen-fitted supported main-window minimum width");
     foreach(string appearance in new[]{"light","dark"}){
      Theme.Set(appearance);main.ApplyBrowsePreferences(Settings.Load());Call(main,"UpdateNavigationPresentation");Call(main,"UpdateInspectorPresentation");
      Render(main,Path.Combine(root,"canvas-projects-"+appearance+".png"),1280);CheckLayout(main,page,1280,appearance,check);
      Render(main,Path.Combine(root,"canvas-projects-"+appearance+"-narrow.png"),narrowWidth);CheckLayout(main,page,narrowWidth,appearance,check);
      check(page.Visibility==Visibility.Visible&&((Grid)main.Window.FindName("Center")).Visibility==Visibility.Collapsed&&((GridSplitter)main.Window.FindName("RightSidebarHandle")).Visibility==Visibility.Collapsed&&!main.Window.IsVisible&&!canvas.IsVisible,"theme, preferences and supported-minimum resize preserve project presentation and never show fixture windows ("+appearance+")");
     }
     int savedProjectCount=store.List().Count;
     foreach(var section in new[]{"audio","bookmarks","documents","assets","pictures"}){
      main.SetSection(section);check(!main.CanvasProjectsMode&&page.Visibility==Visibility.Collapsed&&((Grid)main.Window.FindName("Center")).Visibility==Visibility.Visible,"leaving Projects for "+section+" restores its own library presentation");
      check(new[]{"Unfiled","Favorites","SidebarFooter","SidebarScopeDivider"}.All(name=>((UIElement)main.Window.FindName(name)).Visibility==Visibility.Visible),"leaving Projects restores the scoped filters and trash for "+section);
      check(((SolidColorBrush)nav.Background).Color!=((SolidColorBrush)Theme.Brush("AccentSoft")).Color,"Projects is not left highlighted after navigating to "+section);
      Click(nav);check(main.CanvasProjectsMode&&page.Visibility==Visibility.Visible&&store.List().Count==savedProjectCount&&((SolidColorBrush)nav.Background).Color==((SolidColorBrush)Theme.Brush("AccentSoft")).Color,"returning from "+section+" opens the existing project gallery without creating or entering a project");
     }
     main.SetSection("pictures");check(!main.CanvasProjectsMode&&page.Visibility==Visibility.Collapsed&&((Grid)main.Window.FindName("Center")).Visibility==Visibility.Visible,"ordinary sidebar navigation exits projects and restores the library");
     check(Rows(library)==libraryBefore&&sourceBytes.All(p=>File.ReadAllBytes(p.Key).SequenceEqual(p.Value))&&managed.All(p=>File.ReadAllBytes(p.Key).SequenceEqual(p.Value))&&sourceTimes.All(p=>File.GetLastWriteTimeUtc(p.Key)==p.Value)&&File.ReadAllBytes(Manifest(store,damaged.Id)).SequenceEqual(manifests[damaged.Id]),"all project actions preserve original bytes/times, gallery files/metadata and damaged project records");
     Canvas(main).Close();main.Window.Close();main=null;
    }
   }finally{if(main!=null){if(Canvas(main)!=null){Canvas(main).SaveFailureForTests=message=>{};Canvas(main).CancelGestureForTests();typeof(FreeCanvasWindow).GetField("dirty",Hidden).SetValue(Canvas(main),false);Canvas(main).Close();}main.Window.Close();}ThumbnailImage.CurrentLibrary=null;Environment.SetEnvironmentVariable("MORI_DESK_DATA",oldData);Entry.ClassicEnabled=oldClassic;Entry.BoardEnabled=oldBoard;Entry.InteractiveShell=oldInteractive;Theme.Set(oldTheme);SynchronizationContext.SetSynchronizationContext(oldContext);app.ShutdownMode=oldShutdown;}
  }
 }
}
