using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mori;

namespace MoriDesk {
 internal static class BrowseSettingsTests {
  const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
  static object Field(MainWindow main,string name){return typeof(MainWindow).GetField(name,Flags).GetValue(main);}
  static void Call(MainWindow main,string name,params object[] args){typeof(MainWindow).GetMethod(name,Flags,null,args.Select(a=>a.GetType()).ToArray(),null).Invoke(main,args);}
  static T Find<T>(MainWindow main,string name)where T:class{return main.Window.FindName(name)as T;}
  static void Select(MainWindow main,params Asset[] items){var selected=(HashSet<string>)Field(main,"selection");selected.Clear();foreach(var item in items)selected.Add(item.Id);Call(main,"UpdateInspector");}
  static IEnumerable<DependencyObject> All(DependencyObject root){yield return root;foreach(object child in LogicalTreeHelper.GetChildren(root)){var value=child as DependencyObject;if(value!=null)foreach(var nested in All(value))yield return nested;}}
  public static void Run(string root,Action<bool,string> check){
   root=Path.Combine(root,"browse-settings");Directory.CreateDirectory(root);
   string originalData=Environment.GetEnvironmentVariable("MORI_DESK_DATA"),oldTheme=Theme.Current;
   bool classic=Entry.ClassicEnabled,board=Entry.BoardEnabled,interactive=Entry.InteractiveShell;
   Environment.SetEnvironmentVariable("MORI_DESK_DATA",Path.Combine(root,"config"));Entry.ClassicEnabled=true;Entry.BoardEnabled=false;Entry.InteractiveShell=false;
   try{Preferences(root,check);Draft(root,check);SaveFailures(root,check);Presentation(root,check);}
   finally{Environment.SetEnvironmentVariable("MORI_DESK_DATA",originalData);Entry.ClassicEnabled=classic;Entry.BoardEnabled=board;Entry.InteractiveShell=interactive;Theme.Set(oldTheme);ThumbnailImage.CurrentLibrary=null;}
  }
  static void Preferences(string root,Action<bool,string> check){
   string path=Path.Combine(root,"roundtrip.json");var defaults=new Settings();
   check(defaults.StartupSection=="assets"&&defaults.DefaultSort==0&&defaults.ShowAssetNames&&defaults.ShowAssetDetails&&defaults.LeftSidebarVisible&&defaults.RightSidebarVisible,"browse defaults preserve all-materials landing, newest order and visible detail panels");
   foreach(string section in new[]{"assets","pictures","videos","audio","bookmarks","documents","shortcuts"}){
    var s=new Settings{LibraryPath=root,StartupSection=section,DefaultSort=3,ThumbnailSize=310,LeftSidebarWidth=340,RightSidebarWidth=410,WaterfallLayout=true,ShowAssetNames=false,ShowAssetDetails=false,LeftSidebarVisible=false,RightSidebarVisible=false};s.SaveTo(path);s=Settings.LoadFrom(path);
    check(s.StartupSection==section&&s.DefaultSort==3&&s.ThumbnailSize==310&&s.LeftSidebarWidth==340&&s.RightSidebarWidth==410&&s.WaterfallLayout&&!s.ShowAssetNames&&!s.ShowAssetDetails&&!s.LeftSidebarVisible&&!s.RightSidebarVisible,"browse preference roundtrip for "+section);
   }
   var json=new System.Web.Script.Serialization.JavaScriptSerializer();
   foreach(object bad in new object[]{"missing-section",17,null}){File.WriteAllText(path,json.Serialize(new{LibraryPath=root,StartupSection=bad,DefaultSort="invalid"}));var s=Settings.LoadFrom(path);check(s.StartupSection=="assets"&&s.DefaultSort==0&&s.LibraryPath==root,"invalid optional browse preferences retain valid library with safe defaults");}
   var value=new Settings();value.DefaultSort=-999;value.StartupSection="../bad";check(value.DefaultSort==0&&value.StartupSection=="assets","invalid startup and negative order normalized");value.DefaultSort=999;check(value.DefaultSort==3,"default order bounded to supported choices");
   new Settings{LibraryPath=root}.SaveTo(path);var stale=Settings.LoadFrom(path);var current=Settings.LoadFrom(path);current.LibraryPath=Path.Combine(root,"new-library");current.Theme="light";current.StartupSection="videos";current.DefaultSort=2;current.SaveTo(path);stale.ThumbnailSize=290;stale.SaveTo(path);value=Settings.LoadFrom(path);
   check(value.StartupSection=="videos"&&value.DefaultSort==2&&value.Theme=="light"&&value.LibraryPath==current.LibraryPath&&value.ThumbnailSize==290,"old thumbnail editor cannot overwrite newer landing, sort, theme or library route");
   stale=Settings.LoadFrom(path);current=Settings.LoadFrom(path);current.ShowAssetNames=false;current.RightSidebarVisible=false;current.SaveTo(path);stale.StartupSection="audio";stale.DefaultSort=1;stale.SaveTo(path);value=Settings.LoadFrom(path);
   check(value.StartupSection=="audio"&&value.DefaultSort==1&&!value.ShowAssetNames&&!value.RightSidebarVisible,"old browse editor merges only explicitly changed fields");
  }
  static void Draft(string root,Action<bool,string> check){
   string path=Path.Combine(root,"draft.json");new Settings{LibraryPath=root,ThumbnailSize=120,LeftSidebarWidth=160,RightSidebarWidth=240}.SaveTo(path);byte[] before=File.ReadAllBytes(path);int applies=0;
   var owner=new Window();using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Mori.Main.xaml")){var styleOwner=(Window)System.Windows.Markup.XamlReader.Load(stream);owner.Resources.MergedDictionaries.Add(styleOwner.Resources);styleOwner.Close();}Theme.Attach(owner,null);
   var draft=new BrowseSettingsPanel(owner,false,value=>applies++,Settings.LoadFrom(path),value=>value.SaveTo(path));draft.Resources.MergedDictionaries.Add(owner.Resources);draft.FontFamily=owner.FontFamily;draft.FontSize=owner.FontSize;draft.SetResourceReference(Control.ForegroundProperty,"Text");draft.SetResourceReference(Control.BackgroundProperty,"Canvas");
   try{
    check(draft.Home.Items.Count==7&&draft.Sort.Items.Count==4&&draft.LayoutChoice.Items.Count==2,"browse panel exposes seven landing sections and supported order/layout choices");
    foreach(var slider in new[]{draft.Size,draft.LeftWidth,draft.RightWidth}){var label=((DockPanel)slider.Parent).Children.OfType<TextBlock>().Single();check(label.Text==((int)slider.Minimum)+" px"&&!string.IsNullOrWhiteSpace(AutomationProperties.GetName(slider)),"minimum-value slider still shows exact size and accessible name");}
    draft.Home.SelectedIndex=6;draft.Sort.SelectedIndex=2;draft.LayoutChoice.SelectedIndex=1;draft.Size.Value=300;draft.LeftWidth.Value=330;draft.RightWidth.Value=400;draft.Names.IsChecked=false;draft.Details.IsChecked=false;draft.Left.IsChecked=false;draft.Right.IsChecked=false;
    check(File.ReadAllBytes(path).SequenceEqual(before)&&applies==0,"changing browse controls is a draft until explicit save");
    var newer=Settings.LoadFrom(path);newer.LibraryPath=Path.Combine(root,"concurrent-library");newer.Theme="light";newer.SaveTo(path);
    check(draft.SaveDraft()&&applies==1,"browse save applies preferences once after persistence");var saved=Settings.LoadFrom(path);
    check(saved.LibraryPath==newer.LibraryPath&&saved.Theme=="light"&&saved.StartupSection=="shortcuts"&&saved.DefaultSort==2&&saved.ThumbnailSize==300&&saved.LeftSidebarWidth==330&&saved.RightSidebarWidth==400&&!saved.ShowAssetNames&&!saved.ShowAssetDetails&&!saved.LeftSidebarVisible&&!saved.RightSidebarVisible&&saved.WaterfallLayout,"browse save retains concurrent storage/appearance and persists every browse control");
    newer=Settings.LoadFrom(path);newer.DefaultSort=1;newer.RightSidebarWidth=440;newer.SaveTo(path);draft.Size.Value=310;draft.SaveDraft();saved=Settings.LoadFrom(path);
    check(saved.DefaultSort==1&&saved.RightSidebarWidth==440&&saved.ThumbnailSize==310&&draft.Sort.SelectedIndex==1&&draft.RightWidth.Value==440,"stale browse page preserves newer untouched fields and refreshes controls to merged saved values");
    before=File.ReadAllBytes(path);draft.Reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));var defaults=new Settings();
    check(draft.Home.SelectedIndex==0&&draft.Sort.SelectedIndex==0&&draft.Size.Value==defaults.ThumbnailSize&&draft.LeftWidth.Value==defaults.LeftSidebarWidth&&draft.RightWidth.Value==defaults.RightSidebarWidth&&draft.Names.IsChecked==true&&draft.Details.IsChecked==true&&draft.Left.IsChecked==true&&draft.Right.IsChecked==true&&draft.LayoutChoice.SelectedIndex==0,"reset restores all browse controls, not library or theme");
    check(File.ReadAllBytes(path).SequenceEqual(before)&&applies==2,"reset is draft-only and does not silently persist or apply");
    int writes=0;var preview=new BrowseSettingsPanel(owner,true,value=>applies++,Settings.LoadFrom(path),value=>writes++);preview.Size.Value=230;preview.Save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    check(!preview.Save.IsEnabled&&!preview.SaveDraft()&&writes==0&&applies==2&&File.ReadAllBytes(path).SequenceEqual(before),"preview browse settings reject even programmatic save without personal writes");
    foreach(string appearance in new[]{"dark","light"}){Theme.Set(appearance);owner.Content=draft;Theme.Attach(owner,null);draft.Width=514;draft.Height=400;draft.Measure(new Size(514,400));draft.Arrange(new Rect(0,0,514,400));draft.UpdateLayout();
     check(draft.ScrollableWidth<.1&&draft.ScrollableHeight>0,"minimum settings viewport scrolls vertically without horizontal overflow in "+appearance);
     var bitmap=new RenderTargetBitmap(514,400,96,96,PixelFormats.Pbgra32);bitmap.Render(draft);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var output=File.Create(Path.Combine(root,"browse-min-"+appearance+".png")))encoder.Save(output);
    }
   }finally{owner.Close();}
  }
  static void SaveFailures(string root,Action<bool,string> check){
   string path=Path.Combine(root,"save-failures.json");new Settings{LibraryPath=root,DefaultSort=0,RightSidebarWidth=320}.SaveTo(path);
   var initial=Settings.LoadFrom(path);var newer=Settings.LoadFrom(path);newer.LibraryPath=Path.Combine(root,"newer-library");newer.DefaultSort=2;newer.RightSidebarWidth=440;newer.SaveTo(path);
   var owner=new Window();int writes=0,applies=0;bool controlsMergedDuringApply=false;BrowseSettingsPanel draft=null;
   try{
    draft=new BrowseSettingsPanel(owner,false,value=>{applies++;controlsMergedDuringApply=draft.Sort.SelectedIndex==2&&draft.RightWidth.Value==440;throw new IOException("synthetic page refresh failure");},initial,value=>{writes++;value.SaveTo(path);});draft.Size.Value=300;
    check(draft.SaveDraft(),"successful browse persistence remains successful if current-page application throws");
    var saved=Settings.LoadFrom(path);check(writes==1&&applies==1&&saved.ThumbnailSize==300&&saved.LibraryPath==newer.LibraryPath&&saved.DefaultSort==2&&saved.RightSidebarWidth==440,"failed browse refresh neither repeats persistence nor rolls back committed merged settings");
    check(controlsMergedDuringApply&&draft.Sort.SelectedIndex==2&&draft.RightWidth.Value==440,"committed merged controls reload before applying preferences, even when application fails");
    check(All(draft).OfType<TextBlock>().Any(t=>t.Text.Contains("已保存")&&t.Text.Contains("未能立即更新")&&t.Text.Contains("synthetic page refresh failure")),"failed browse application explains saved state instead of claiming the save failed");
    byte[] before=File.ReadAllBytes(path);int failedWrites=0,unexpectedApplies=0;
    var failed=new BrowseSettingsPanel(owner,false,value=>unexpectedApplies++,Settings.LoadFrom(path),value=>{failedWrites++;throw new IOException("synthetic persistence failure");});failed.Size.Value=310;
    bool caught=false;try{failed.SaveDraft();}catch(IOException error){caught=error.Message=="synthetic persistence failure";}
    check(caught&&failedWrites==1&&unexpectedApplies==0&&File.ReadAllBytes(path).SequenceEqual(before),"real persistence failure stays a save failure without application, repeat writes or rollback");
    check(failed.Size.Value==310&&!All(failed).OfType<TextBlock>().Any(t=>t.Text.Contains("已保存")),"failed persistence retains the editable draft without falsely reporting a successful save");
   }finally{owner.Close();}
  }
  static void Presentation(string root,Action<bool,string> check){
   string config=Path.Combine(Entry.DataRoot,"settings.json");new Settings{LibraryPath=Path.Combine(root,"library"),DefaultSort=2}.SaveTo(config);
   using(var library=new Library(Path.Combine(root,"library"))){
    string one=Path.Combine(root,"one.png"),two=Path.Combine(root,"two.png"),video=Path.Combine(root,"movie.mp4"),directory=Path.Combine(root,"original-folder");
    using(var b=new System.Drawing.Bitmap(31,29)){b.SetPixel(0,0,System.Drawing.Color.Red);b.Save(one,System.Drawing.Imaging.ImageFormat.Png);b.SetPixel(0,0,System.Drawing.Color.Blue);b.Save(two,System.Drawing.Imaging.ImageFormat.Png);}File.WriteAllText(video,"synthetic metadata fixture");Directory.CreateDirectory(directory);
    var z=library.Import(one,"","Z picture","https://example.org/image","","").Item;var a=library.Import(two,"","A picture","","","").Item;var movie=library.Import(video,"","movie","","","").Item;var web=library.ImportBookmark("https://example.org/","website","").Item;
    var main=new MainWindow(library){PreviewMode=true};
    try{
     check(Find<ComboBox>(main,"Sort").SelectedIndex==2&&((List<Asset>)Field(main,"visible")).First().Id==a.Id,"non-default initial sort initializes safely before refresh handlers");
     check(Find<StackPanel>(main,"Inspector").Visibility==Visibility.Collapsed&&Find<StackPanel>(main,"InspectorEmpty").Visibility==Visibility.Visible&&Find<Button>(main,"Export").Visibility==Visibility.Collapsed&&Find<Button>(main,"AddBookmark").Visibility==Visibility.Collapsed,"unselected home hides disabled detail form and irrelevant toolbar actions");
     check(Find<StackPanel>(main,"SidebarGuide").Visibility==Visibility.Visible&&Find<Button>(main,"CreateFirstFolder").Visibility==Visibility.Visible,"empty material category sidebar has a direct create action");
     string folder=library.CreateFolder("Project","");Call(main,"ReloadFolders");check(Find<StackPanel>(main,"SidebarGuide").Visibility==Visibility.Collapsed,"category creation immediately removes stale empty-sidebar guide");
     Select(main,z);check(Find<StackPanel>(main,"Inspector").Visibility==Visibility.Visible&&Find<StackPanel>(main,"InspectorEmpty").Visibility==Visibility.Collapsed&&Find<TextBox>(main,"Name").Visibility==Visibility.Visible&&Find<Grid>(main,"SourceRow").Visibility==Visibility.Visible&&Find<Button>(main,"Export").Visibility==Visibility.Visible,"single image retains editable metadata, source and export");
     Select(main,z,a);check(Find<TextBox>(main,"Name").Visibility==Visibility.Collapsed&&Find<TextBox>(main,"Notes").Visibility==Visibility.Collapsed&&Find<Grid>(main,"SourceRow").Visibility==Visibility.Collapsed&&Find<Grid>(main,"FileInfo").Visibility==Visibility.Collapsed&&Find<Button>(main,"SelectionCanvas").Visibility==Visibility.Visible&&Convert.ToString(Find<Button>(main,"SelectionCanvas").Content)=="画布"&&Find<TextBlock>(main,"TagsLabel").Text.Contains("批量"),"multi-picture inspector exposes batch tags and canvas without misleading single-item metadata");
     Select(main,movie);check(Find<Button>(main,"Similar").Visibility==Visibility.Collapsed&&Find<Button>(main,"ColorSearch").Visibility==Visibility.Collapsed&&Find<Button>(main,"SelectionCanvas").Visibility==Visibility.Collapsed,"video inspector hides image-only commands");
     main.SetSection("bookmarks");Select(main,web);check(Find<Button>(main,"AddBookmark").Visibility==Visibility.Visible&&Find<TreeView>(main,"Folders").Visibility==Visibility.Collapsed&&Find<Grid>(main,"MediaFolderHeading").Visibility==Visibility.Collapsed&&Find<Grid>(main,"FileInfo").Visibility==Visibility.Collapsed,"URL page exposes add link while hiding irrelevant media categories and file metadata");
     main.SetSection("shortcuts");check(Convert.ToString(Find<Button>(main,"Import").Content)=="收藏本地文件"&&Convert.ToString(Find<Button>(main,"EmptyImport").Content)=="收藏本地文件"&&Find<Button>(main,"Import").ToolTip.ToString().Contains("不复制")&&Find<Button>(main,"EmptyImport").ToolTip.ToString().Contains("不复制"),"local toolbar and empty-state action consistently indicate shortcuts rather than copying imports");
     check(Find<TreeView>(main,"Folders").Visibility==Visibility.Collapsed&&new[]{"Unfiled","Favorites","Trash"}.All(n=>Find<Button>(main,n).Visibility==Visibility.Collapsed),"local page uses in-page folders without unrelated duplicate sidebar categories");
     string id=library.AddReferenceAssets(new[]{directory},"").Single();Call(main,"Refresh",true);Select(main,library.All().Single(x=>x.Id==id));check(Find<Button>(main,"Export").Visibility==Visibility.Collapsed&&Find<Button>(main,"Categorize").Visibility==Visibility.Collapsed,"directory shortcut hides file-export and duplicate category buttons");
     check(Find<System.Windows.Controls.Primitives.UniformGrid>(main,"CategoryActions").Columns==1&&Find<Button>(main,"Star").Margin.Left==0&&Find<System.Windows.Controls.Primitives.UniformGrid>(main,"ImageActions").Visibility==Visibility.Collapsed,"local actions use a full-width star row without empty half-cells");
     main.SetSection("assets");main.RequestNavigate(folder);var create=main.CreateNewFolderMenu();check(create.Items.OfType<MenuItem>().Count()==2&&create.Items.OfType<MenuItem>().First().Header.ToString().Contains("顶层")&&create.Items.OfType<MenuItem>().Last().Header.ToString().Contains("Project"),"new category explicitly offers top-level and named child destination");
     main.SetSection("assets");check(main.CreateNewFolderMenu().Items.OfType<MenuItem>().Count()==1,"outside project categories create only a top-level category");
     var more=main.CreateMoreMenu();check(more.Items.OfType<MenuItem>().Any(i=>i.Header.ToString()=="收藏网址…")&&more.Items.OfType<MenuItem>().Single(i=>i.Header.ToString()=="导入").Items.Count==4,"contextual toolbar keeps bookmark and all four original import actions reachable");
     check(AutomationProperties.GetName(Find<Button>(main,"Settings"))=="设置"&&Find<Button>(main,"Settings").Width==34,"compact settings icon preserves accessible label and hit area");
     Select(main,z);Find<TextBox>(main,"Notes").Text="unsaved fixture draft";var prefs=new Settings{ThumbnailSize=260,DefaultSort=3,WaterfallLayout=true,ShowAssetNames=false,ShowAssetDetails=false,LeftSidebarVisible=false,RightSidebarVisible=false,LeftSidebarWidth=340,RightSidebarWidth=410};main.ApplyBrowsePreferences(prefs);var sidebars=(SidebarLayout)Field(main,"sidebars");
     check(Find<TextBox>(main,"Notes").Text=="unsaved fixture draft"&&library.All().Single(x=>x.Id==z.Id).Notes=="","applying browse preferences preserves pending metadata without silently saving");
     check(sidebars.Left==340&&sidebars.Right==410&&!sidebars.LeftVisible&&!sidebars.RightVisible&&(int)Field(main,"thumbnailSize")==260&&(bool)Field(main,"waterfallLayout")&&!(bool)Field(main,"showAssetNames")&&!(bool)Field(main,"showAssetDetails")&&Find<ComboBox>(main,"Sort").SelectedIndex==3,"browse save immediately applies all layout, information, size and panel preferences");
     Call(main,"UpdateInspector");check(!main.Window.IsVisible,"browse regression uses hidden synthetic windows without desktop input");
    }finally{main.Window.Close();}
   }
  }
 }
}
