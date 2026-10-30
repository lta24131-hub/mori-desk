using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mori;

namespace MoriDesk {
 // Exercise the real controls with synthetic data. Rendering is offscreen;
 // interactive popup/focus and desktop services are covered by VM acceptance.
 internal static class HomepageRedesignTests {
  const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
  static T Field<T>(MainWindow main,string name){return (T)typeof(MainWindow).GetField(name,Hidden).GetValue(main);}
  static void Set(MainWindow main,string name,object value){typeof(MainWindow).GetField(name,Hidden).SetValue(main,value);}
  static object Call(MainWindow main,string name,params object[] args){return typeof(MainWindow).GetMethods(Hidden).Single(method=>method.Name==name&&method.GetParameters().Length==args.Length&&method.GetParameters().Select((p,i)=>args[i]==null||p.ParameterType.IsInstanceOfType(args[i])).All(ok=>ok)).Invoke(main,args);}
  static T Find<T>(MainWindow main,string name)where T:class{return main.Window.FindName(name)as T;}
  static void Click(Button button){button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
  static MenuItem MenuAction(ItemsControl menu,string label){return menu.Items.OfType<MenuItem>().Single(item=>Convert.ToString(item.Header)==label);}
  static void Click(MenuItem item){item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));}
  static void Select(MainWindow main,params Asset[] assets){var ids=Field<HashSet<string>>(main,"selection");ids.Clear();foreach(var asset in assets)ids.Add(asset.Id);Call(main,"UpdateInspector");}
  static void StopTimers(MainWindow main){foreach(string name in new[]{"externalTimer","searchTimer","layoutTimer","sizeSaveTimer","sidebarSaveTimer"})Field<DispatcherTimer>(main,name).Stop();}
  static string Rows(Library library){return string.Join("\n",new[]{"assets","folders","memberships","desk_refs"}.SelectMany(table=>library.Db.Query("SELECT * FROM "+table).Select(row=>table+":"+string.Join("|",row.OrderBy(pair=>pair.Key).Select(pair=>pair.Key+"="+pair.Value))).OrderBy(row=>row,StringComparer.Ordinal)));}
  static IEnumerable<T> Descendants<T>(DependencyObject root)where T:DependencyObject{for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T)yield return (T)child;foreach(var item in Descendants<T>(child))yield return item;}}
  static bool Displayed(DependencyObject item){for(var node=item;node!=null;node=MainWindow.SelectionParent(node)){var ui=node as UIElement;if(ui!=null&&ui.Visibility!=Visibility.Visible)return false;}return true;}
  static Rect Bounds(FrameworkElement element,FrameworkElement root){return new Rect(element.TranslatePoint(new Point(),root),new Size(element.ActualWidth,element.ActualHeight));}
  static bool Hit(FrameworkElement root,FrameworkElement element){
   var point=element.TranslatePoint(new Point(element.ActualWidth/2,element.ActualHeight/2),root);DependencyObject hit=null;
   VisualTreeHelper.HitTest(root,node=>{var ui=node as UIElement;return ui!=null&&(ui.Visibility!=Visibility.Visible||!ui.IsHitTestVisible)?HitTestFilterBehavior.ContinueSkipSelfAndChildren:HitTestFilterBehavior.Continue;},result=>{hit=result.VisualHit;return HitTestResultBehavior.Stop;},new PointHitTestParameters(point));
   for(var node=hit;node!=null;node=MainWindow.SelectionParent(node))if(ReferenceEquals(node,element))return true;return false;
  }
  static void Measure(MainWindow main,FrameworkElement content,int width){content.Width=width;content.Height=680;content.Measure(new Size(width,680));content.Arrange(new Rect(0,0,width,680));content.UpdateLayout();StopTimers(main);}
  static void Render(FrameworkElement content,string path){
   int width=(int)Math.Ceiling(content.ActualWidth),height=(int)Math.Ceiling(content.ActualHeight);var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(path))encoder.Save(file);
   string name=Path.GetFileNameWithoutExtension(path);
   if(name.StartsWith("toolbox-",StringComparison.Ordinal)||(width==1366&&(name.Contains("empty-selection")||name.Contains("single-picture")))){
    var jpeg=new JpegBitmapEncoder{QualityLevel=76};jpeg.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=new MemoryStream()){jpeg.Save(stream);string value=Convert.ToBase64String(stream.ToArray());if(value.Length>=256000)throw new Exception("Homepage preview exceeds report transport limit: "+name);File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"homepage-redesign-"+name+".base64.txt"),value);}
   }
  }
  static void Await(Task task){var end=DateTime.UtcNow.AddSeconds(30);while(!task.IsCompleted&&DateTime.UtcNow<end){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);Thread.Sleep(1);}if(!task.IsCompleted)throw new Exception("Homepage canvas action timed out");task.GetAwaiter().GetResult();}
  static void AwaitImport(MainWindow main){var end=DateTime.UtcNow.AddSeconds(30);while(Field<bool>(main,"busy")&&DateTime.UtcNow<end){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);Thread.Sleep(1);}if(Field<bool>(main,"busy"))throw new Exception("Homepage import did not finish");StopTimers(main);}

  static void Toolbox(string root,Action<bool,string> check){
   var expected=new Dictionary<string,string>{{"ToolboxCapture","capture"},{"ToolboxTranslate","translate"},{"ToolboxPastePin","paste-pin"},{"ToolboxRecoverPin","recover-pin"},{"ToolboxTogglePins","toggle-pins"},{"ToolboxConvert","convert"},{"ToolboxViewImage","image"},{"ToolboxView3D","model"}};
   var commands=new List<string>();var panel=new ToolboxPanel(commands.Add);panel.SetCaptureShortcut("Ctrl + Shift + F8");
   foreach(string theme in new[]{"light","dark"}){
    Theme.Set(theme);panel.Measure(new Size(356,double.PositiveInfinity));panel.Arrange(new Rect(new Point(),panel.DesiredSize));panel.UpdateLayout();
    var buttons=Descendants<Button>(panel).ToArray();
    check(buttons.Length==expected.Count&&buttons.Select(button=>button.Name).Distinct().Count()==expected.Count&&buttons.All(button=>expected.ContainsKey(button.Name)),theme+" toolbox exposes the complete tool set once, including separate paste/recover/show-hide pin actions");
    check(buttons.All(button=>button.Focusable&&button.IsTabStop&&!string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))&&!string.IsNullOrWhiteSpace(Convert.ToString(button.ToolTip)))&&KeyboardNavigation.GetTabNavigation(panel)==KeyboardNavigationMode.Cycle,theme+" every toolbox action is named, focusable and in a contained keyboard path");
    check(buttons.All(button=>Displayed(button)&&button.ActualWidth>=24&&button.ActualHeight>=24&&Hit(panel,button)&&new Rect(new Point(),panel.RenderSize).Contains(Bounds(button,panel))),theme+" all toolbox hit targets fit inside the panel without clipping or overlapping blockers");
    check(((SolidColorBrush)panel.Background).Color==((SolidColorBrush)Theme.Brush("Surface")).Color&&buttons.All(button=>((SolidColorBrush)button.Foreground).Color==((SolidColorBrush)Theme.Brush("Text")).Color),theme+" an existing toolbox follows live theme changes for its surface and action labels");
    check(Descendants<TextBlock>(panel).Any(text=>text.Text=="Ctrl + Shift + F8"&&Displayed(text)),theme+" capture displays the configured shortcut instead of a fixed default");
    foreach(var button in buttons){int before=commands.Count;Click(button);check(commands.Count==before+1&&commands.Last()==expected[button.Name],theme+" "+button.Name+" executes exactly its own tool command");}
    Render(panel,Path.Combine(root,"toolbox-"+theme+".png"));
   }
   panel.SetCaptureShortcut(null);check(!Descendants<TextBlock>(panel).Any(text=>text.Text=="Ctrl + Shift + F8"&&Displayed(text)),"disabled capture shortcut does not leave a stale keyboard hint");
   panel.Dispose();
  }

  static void ToolboxPins(string root,Action<bool,string> check){
   var store=new QingJie.PinStore(Path.Combine(root,"empty-pin-history"));int recoveries=0,pastes=0,toggles=0,windows=Application.Current.Windows.Count;bool recovered=false,fail=false;var messages=new List<string>();
   // Exercise the real button-to-command routing, with the production empty
   // history lookup and a counted clipboard boundary. Never read or change the
   // real clipboard, create a pin window, or borrow the active AppState store.
   using(var panel=new ToolboxPanel(command=>ToolboxPinActions.Execute(command,()=>{recoveries++;if(fail)throw new InvalidOperationException("synthetic recovery failure");return recovered||store.Recover();},()=>pastes++,()=>toggles++,messages.Add))){
    panel.Measure(new Size(356,double.PositiveInfinity));panel.Arrange(new Rect(new Point(),panel.DesiredSize));panel.UpdateLayout();
    var buttons=Descendants<Button>(panel).ToDictionary(button=>button.Name);var recover=buttons["ToolboxRecoverPin"];
    Click(recover);Click(recover);
    check(recoveries==2&&pastes==0&&toggles==0&&messages.Count==2&&messages.All(message=>message.Contains("没有可找回")&&message.Contains("粘贴图片")),"empty-history recovery reports the empty state every time without invoking clipboard paste or show/hide");
    check(store.Records.Count==0&&Directory.GetFiles(store.Folder).Length==0&&Application.Current.Windows.Count==windows,"empty-history recovery creates no pin, history file or window");
    recovered=true;Click(recover);
    check(recoveries==3&&pastes==0&&toggles==0&&messages.Last()=="已找回最近关闭的贴图。","successful recovery does not also paste a new image");
    recovered=false;fail=true;bool failed=false;try{Click(recover);}catch(InvalidOperationException){failed=true;}
    check(failed&&recoveries==4&&pastes==0&&toggles==0&&messages.Count==3,"a recovery failure propagates to the toolbox error handler without falling back to clipboard paste or reporting success");
    fail=false;Click(buttons["ToolboxPastePin"]);Click(buttons["ToolboxTogglePins"]);
    check(pastes==1&&toggles==1&&recoveries==4&&messages.Count==3,"explicit paste and show/hide execute only their own operations and never recover old history");
   }
  }

  static void LayoutAndNavigation(string root,Library library,Asset first,Asset second,Asset website,Asset reference,Action<bool,string> check){
   string category=library.CreateFolder("保持搜索的分类","");library.AddToFolder(new[]{first.Id},category);
   var main=new MainWindow(library){PreviewMode=true,FreeCanvasPresentationSuppressedForTests=true};StopTimers(main);main.PendingInformationChoice=()=>{throw new Exception("passive homepage navigation must not prompt");};
   var content=(FrameworkElement)main.Window.Content;main.Window.Content=null;content.Resources.MergedDictionaries.Add(main.Window.Resources);content.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty,main.Window.FontFamily);content.SetValue(System.Windows.Documents.TextElement.FontSizeProperty,main.Window.FontSize);content.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty,"Text");
   string before=Rows(library);var store=new FreeCanvasStore(Entry.DataRoot);int projects=store.List().Count;
   try{
    foreach(string theme in new[]{"light","dark"})foreach(int width in new[]{1040,1366}){
     Theme.Set(theme);foreach(object key in Application.Current.Resources.Keys)content.Resources[key]=Application.Current.Resources[key];content.SetResourceReference(Panel.BackgroundProperty,"Canvas");Rect? searchBounds=null,addBounds=null;Dictionary<string,Rect> utilityBounds=null;
     foreach(string mode in new[]{"empty-selection","single-picture","multiple-pictures","website","local-shortcut","projects","return-materials"}){
      main.SetSection(mode=="website"?"bookmarks":mode=="local-shortcut"?"shortcuts":"assets");Select(main);
      if(mode=="single-picture")Select(main,first);if(mode=="multiple-pictures")Select(main,first,second);if(mode=="website")Select(main,website);if(mode=="local-shortcut")Select(main,reference);if(mode=="projects")main.ShowCanvasProjects();
      Measure(main,content,width);string label=theme+" / "+width+" / "+mode+": ";
      var add=Find<Button>(main,"HomeAdd");var search=Find<TextBox>(main,"Search");var tools=Find<Button>(main,"DeskTools");var settings=Find<Button>(main,"Settings");var more=Find<Button>(main,"More");
      check(add!=null&&tools!=null&&Displayed(tools)&&Displayed(settings)&&Displayed(more)&&new[]{tools,settings,more}.All(button=>button.ActualWidth>=26&&button.ActualHeight>=26&&Hit(content,button)),label+" toolbox, settings and application menu stay visible and reachable at the bottom");
      check(Bounds(tools,content).Top>content.ActualHeight/2&&Bounds(settings,content).Top>content.ActualHeight/2&&Bounds(more,content).Top>content.ActualHeight/2,label+" low-frequency tools and maintenance commands do not occupy the browsing header");
      var utilities=new[]{tools,settings,more};if(utilityBounds==null)utilityBounds=utilities.ToDictionary(button=>button.Name,button=>Bounds(button,content));
      check(utilities.All(button=>{var current=Bounds(button,content);var initial=utilityBounds[button.Name];return Math.Abs(current.Left-initial.Left)<.5&&Math.Abs(current.Top-initial.Top)<.5&&Math.Abs(current.Width-initial.Width)<.5&&Math.Abs(current.Height-initial.Height)<.5;}),label+" toolbox, settings and More retain the exact material-page positions and sizes across every page and return trip");
      var trash=Find<Button>(main,"Trash");bool hasTrash=mode!="projects"&&mode!="local-shortcut";
      check(Displayed(trash)==hasTrash&&Hit(content,trash)==hasTrash,label+" unavailable recycle bins keep only their space and cannot be clicked; material/website recycle bins remain reachable");
      check(new[]{"Import","Paste","Export","AddBookmark"}.All(name=>!Displayed(Find<Button>(main,name))),label+" legacy commands remain wired but do not form a competing top toolbar");
      if(mode!="projects"){
       var currentSearch=Bounds(search,content);var currentAdd=Bounds(add,content);
       if(!searchBounds.HasValue){searchBounds=currentSearch;addBounds=currentAdd;}
       check(Displayed(add)&&Displayed(search)&&search.ActualWidth>=220&&Hit(content,add)&&Math.Abs(currentSearch.Left-searchBounds.Value.Left)<.5&&Math.Abs(currentSearch.Width-searchBounds.Value.Width)<.5&&Math.Abs(currentAdd.Left-addBounds.Value.Left)<.5,label+" search and Add keep their positions while navigation and selection change");
       bool selected=mode!="empty-selection"&&mode!="return-materials";
       check(Displayed(Find<Border>(main,"BatchBar"))==selected,label+" contextual actions appear only for the current selection");
       var canvas=Find<Button>(main,"SelectionCanvas");bool images=mode=="single-picture"||mode=="multiple-pictures"||mode=="local-shortcut";
       check(Displayed(canvas)==images&&(!images||canvas.IsEnabled&&Hit(content,canvas)),label+" the canvas action follows supported single or multiple images without appearing on websites");
      }else check(main.CanvasProjectsMode&&main.CanvasProjects.Visibility==Visibility.Visible&&!Displayed(Find<Border>(main,"BatchBar")),label+" project gallery replaces the material workspace without retaining stale selection actions");
      if(mode=="empty-selection"||mode=="single-picture"||mode=="multiple-pictures"||mode=="projects")Render(content,Path.Combine(root,theme+"-"+mode+"-"+width+".png"));
     }
    }
    foreach(string section in new[]{"shortcuts","bookmarks","assets"}){
     main.ShowCanvasProjects();check(Find<TextBlock>(main,"Status").Text.Contains("画布按项目保存"),"project navigation establishes its own page instructions before leaving for "+section);
     main.SetSection(section);string hint=Find<TextBlock>(main,"Status").Text;
     check(!hint.Contains("画布")&&hint.Contains(section=="shortcuts"?"本地收藏只记位置":section=="bookmarks"?"收藏网址":"双击或空格预览"),"leaving Projects replaces stale canvas instructions with the correct "+section+" page hint");
    }
    Call(main,"Status","Synthetic operation result");Call(main,"Refresh",false);
    check(Find<TextBlock>(main,"Status").Text=="Synthetic operation result","ordinary background refresh preserves operation feedback instead of replacing it with navigation instructions");
    main.SetSection("assets");Find<TextBox>(main,"Search").Text="测试素材";main.RefreshView();Click(MenuAction(main.CreateHomeTypeMenu(),"视频"));
    check(Find<TextBox>(main,"Search").Text=="测试素材"&&Field<string>(main,"mediaSection")=="videos"&&Field<List<Asset>>(main,"visible").Count==0,"changing the whole-library type retains the user's existing search rather than silently clearing it");
    Click(MenuAction(main.CreateHomeTypeMenu(),"图片（含动图）"));check(Find<TextBox>(main,"Search").Text=="测试素材"&&Field<List<Asset>>(main,"visible").Count==2,"returning to pictures applies the same query and restores only its matches");
    main.RequestNavigate(category);Find<TextBox>(main,"Search").Text="测试素材";main.RefreshView();Click(MenuAction(main.CreateHomeTypeMenu(),"图片"));
    check(Field<string>(main,"view")==category&&Find<TextBox>(main,"Search").Text=="测试素材"&&Field<List<Asset>>(main,"visible").Single().Id==first.Id,"type filtering within a category retains category membership and query");
    main.SetSection("assets");Select(main);var sidebars=Field<SidebarLayout>(main,"sidebars");sidebars.SetVisible(false,true);Set(main,"homeInspectorExplicit",false);Call(main,"UpdateInspector");
    var details=MenuAction(main.CreateGalleryMenu(),"显示右栏");check(!sidebars.RightDisplayed&&sidebars.RightVisible&&!details.IsChecked,"View check mark describes the automatically collapsed panel without overwriting the saved preference");
    Click(details);Select(main,first);Select(main);check(sidebars.RightDisplayed&&MenuAction(main.CreateGalleryMenu(),"显示右栏").IsChecked,"one View action opens details and that explicit choice survives selection changes");
    main.ToggleSidebar(false);Select(main,first);main.ShowCanvasProjects();main.SetSection("assets");Select(main,first);check(!sidebars.RightDisplayed&&!sidebars.RightVisible,"explicitly hiding details remains honored after selecting images and visiting projects");
    check(Rows(library)==before&&store.List().Count==projects&&!main.Window.IsVisible,"all homepage navigation/selection/layout checks preserve every data row and canvas and never show a desktop window");
    main.OpenToolbox();check(main.ToolboxPopup!=null&&!main.ToolboxPopup.IsOpen,"offscreen or hidden main windows cannot leave a stray visible toolbox popup");
   }finally{StopTimers(main);main.Window.Close();}
  }

  static void CanvasDestinations(Library library,Asset first,Asset second,Action<bool,string> check){
   var main=new MainWindow(library){PreviewMode=true,FreeCanvasPresentationSuppressedForTests=true};StopTimers(main);string before=Rows(library);var store=new FreeCanvasStore(Entry.DataRoot);int originalProjects=store.List().Count;int choices=0,requestedCount=0;CanvasDestination destination=null;
   main.CanvasDestinationChoiceForTests=count=>{choices++;requestedCount=count;return destination;};
   try{
    main.SetSection("pictures");Select(main,first);var button=Find<Button>(main,"SelectionCanvas");
    check(button.Visibility==Visibility.Visible&&button.IsEnabled,"a single image exposes the same explicit canvas destination action as a multiple-image selection");
    var previous=main.FreeCanvasImportForTests;Click(button);check(choices==1&&requestedCount==1&&main.FreeCanvasImportForTests==previous&&store.List().Count==originalProjects,"canceling the single-image destination choice neither creates a canvas nor changes the library");
    destination=new CanvasDestination{NewName="单图新建画布"};Click(button);Await(main.FreeCanvasImportForTests);var canvas=Field<FreeCanvasWindow>(main,"freeCanvas");string id=canvas.DocumentForTests.Id;
    check(choices==2&&requestedCount==1&&canvas.DocumentForTests.Items.Count==1&&canvas.DocumentForTests.Items[0].Source==library.FullPath(first)&&canvas.SaveForTests()&&store.Load(id).Items.Count==1,"single-image toolbar creates the explicitly named project and commits the selected image");
    destination=new CanvasDestination{ProjectId=id};Select(main,second);var item=main.CreateAssetMenu(second).Items.OfType<MenuItem>().Single(entry=>Convert.ToString(entry.Header).Contains("画布"));item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Await(main.FreeCanvasImportForTests);
    check(choices==3&&requestedCount==1&&Field<FreeCanvasWindow>(main,"freeCanvas")==canvas&&canvas.DocumentForTests.Id==id&&canvas.DocumentForTests.Items.Count==2&&canvas.DocumentForTests.Items.Last().Source==library.FullPath(second),"right-click uses the same destination choice and appends only the selected image into that exact existing canvas");
    destination=new CanvasDestination{NewName="两图新建画布"};Select(main,first,second);Click(button);Await(main.FreeCanvasImportForTests);canvas=Field<FreeCanvasWindow>(main,"freeCanvas");
    check(choices==4&&requestedCount==2&&canvas.DocumentForTests.Id!=id&&canvas.DocumentForTests.Items.Count==2&&store.Load(id).Items.Count==2&&store.List().Count==originalProjects+2,"multiple selection can explicitly create a separate canvas without silently appending to the previous project");
    check(Rows(library)==before&&!main.Window.IsVisible&&!canvas.IsVisible,"single and multi-image canvas operations preserve gallery files/categories and remain offscreen");canvas.Close();
   }finally{StopTimers(main);var canvas=Field<FreeCanvasWindow>(main,"freeCanvas");if(canvas!=null)canvas.Close();main.Window.Close();}
  }

  static void ImportCompletion(string root,Action<bool,string> check){
   using(var library=new Library(Path.Combine(root,"import-completion-library"))){
    var main=new MainWindow(library){PreviewMode=true,FreeCanvasPresentationSuppressedForTests=true};StopTimers(main);main.PendingInformationChoice=()=>{throw new Exception("importing a new file must not ask about an unchanged inspector");};
    try{
     main.SetSection("assets");string folder=library.CreateFolder("Import destination","");
     for(int index=0;index<2;index++){
      string source=Path.Combine(root,"import-completion-"+index+".png");using(var bitmap=new System.Drawing.Bitmap(107+index,71)){using(var graphics=System.Drawing.Graphics.FromImage(bitmap))graphics.Clear(index==0?System.Drawing.Color.SeaGreen:System.Drawing.Color.Coral);bitmap.Save(source,System.Drawing.Imaging.ImageFormat.Png);}byte[] original=File.ReadAllBytes(source);
      Call(main,"ImportPaths",new[]{source},folder);check(Field<bool>(main,"busy"),"file import enters its real asynchronous busy state before completion");AwaitImport(main);
      // Do not navigate, select, or refresh after completion: any of these
      // would conceal controls left disabled by the import's final refresh.
      var imported=library.All().Single(asset=>asset.Name==Path.GetFileNameWithoutExtension(source));
      check(library.All().Count==index+1&&Field<HashSet<string>>(main,"selection").SetEquals(new[]{imported.Id})&&Field<string>(main,"view")==folder,"import completion reveals exactly the newly committed image in its destination category");
      check(Find<Button>(main,"HomeAdd").IsEnabled&&Find<Button>(main,"HomeTypePicker").IsEnabled,"Add and type filtering recover immediately after import without another navigation or selection event");
      check(new[]{"BatchMove","SelectionCanvas","SelectionMore"}.All(name=>Find<Button>(main,name).IsEnabled),"the imported single image is immediately ready for classification, canvas and more actions");
      check(File.ReadAllBytes(source).SequenceEqual(original)&&File.ReadAllBytes(library.FullPath(imported)).SequenceEqual(original)&&!main.Window.IsVisible,"import preserves the original bytes, saves a matching library copy and never opens a desktop window");
     }
    }finally{StopTimers(main);main.Window.Close();}
   }
  }

  internal static void Run(string root,Action<bool,string> check){
   root=Path.Combine(root,"homepage-redesign");Directory.CreateDirectory(root);var app=Application.Current??new Application();var shutdown=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
   string data=Environment.GetEnvironmentVariable("MORI_DESK_DATA"),theme=Theme.Current;bool classic=Entry.ClassicEnabled,board=Entry.BoardEnabled,interactive=Entry.InteractiveShell;var previous=ThumbnailImage.CurrentLibrary;var context=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
   Environment.SetEnvironmentVariable("MORI_DESK_DATA",Path.Combine(root,"config"));Entry.ClassicEnabled=true;Entry.BoardEnabled=false;Entry.InteractiveShell=false;
   try{
    Toolbox(root,check);ToolboxPins(root,check);
    using(var library=new Library(Path.Combine(root,"library"))){
     library.EnableReferenceAssets();
     var images=new List<Asset>();var bytes=new Dictionary<string,byte[]>();
     for(int index=0;index<2;index++){string path=Path.Combine(root,"picture-"+index+".png");using(var bitmap=new System.Drawing.Bitmap(96+index,64)){using(var graphics=System.Drawing.Graphics.FromImage(bitmap))graphics.Clear(index==0?System.Drawing.Color.MediumPurple:System.Drawing.Color.CornflowerBlue);bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png);}bytes[path]=File.ReadAllBytes(path);images.Add(library.Import(path,"","测试素材 "+index,"https://example.org/source/"+index,"测试标签","").Item);}
     var website=library.ImportBookmark("https://example.org/test","测试网址","").Item;string localId=library.AddReferenceAssets(new[]{bytes.Keys.First()},"").Single();var reference=library.All().Single(asset=>asset.Id==localId);
     LayoutAndNavigation(root,library,images[0],images[1],website,reference,check);CanvasDestinations(library,images[0],images[1],check);
     check(bytes.All(pair=>File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),"homepage redesign preserves every synthetic original byte");
    }
    ImportCompletion(root,check);
   }finally{Environment.SetEnvironmentVariable("MORI_DESK_DATA",data);Entry.ClassicEnabled=classic;Entry.BoardEnabled=board;Entry.InteractiveShell=interactive;Theme.Set(theme);ThumbnailImage.CurrentLibrary=previous;SynchronizationContext.SetSynchronizationContext(context);app.ShutdownMode=shutdown;}
  }
 }
}
