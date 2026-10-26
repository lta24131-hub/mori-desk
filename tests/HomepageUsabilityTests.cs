using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mori;

namespace MoriDesk {
 internal static class HomepageUsabilityTests {
  const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
  static T Field<T>(MainWindow main,string name){return (T)typeof(MainWindow).GetField(name,Flags).GetValue(main);}
  static void Set(MainWindow main,string name,object value){typeof(MainWindow).GetField(name,Flags).SetValue(main,value);}
  static object Call(MainWindow main,string name,params object[] args){return typeof(MainWindow).GetMethod(name,Flags,null,args.Select(a=>a.GetType()).ToArray(),null).Invoke(main,args);}
  static T Find<T>(MainWindow main,string name)where T:class{return main.Window.FindName(name)as T;}
  static void Click(MainWindow main,string name){Find<Button>(main,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
  static List<Asset> All(MainWindow main){return Field<List<Asset>>(main,"all");}
  static List<Asset> Visible(MainWindow main){return Field<List<Asset>>(main,"visible");}
  static void Select(MainWindow main,string id){var selected=Field<HashSet<string>>(main,"selection");selected.Clear();selected.Add(id);Call(main,"UpdateInspector");}
  static void StopTimers(MainWindow main){foreach(string name in new[]{"externalTimer","searchTimer","layoutTimer","sizeSaveTimer","sidebarSaveTimer"})Field<DispatcherTimer>(main,name).Stop();}
  static void WaitIdle(MainWindow main){var timeout=DateTime.UtcNow.AddSeconds(15);while(Field<bool>(main,"busy")&&DateTime.UtcNow<timeout){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);System.Threading.Thread.Sleep(5);}if(Field<bool>(main,"busy"))throw new Exception("isolated image index did not finish");}
  static Rect Bounds(FrameworkElement element,FrameworkElement root){return new Rect(element.TranslatePoint(new Point(),root),new Size(element.ActualWidth,element.ActualHeight));}
  static bool Hit(FrameworkElement root,Button button){
   // InputHitTest rejects the entire tree while its real Window is hidden.
   // Apply the same local visibility/hit policy to geometric hit testing,
   // without creating an HWND or displaying a desktop test window.
   var point=button.TranslatePoint(new Point(button.ActualWidth/2,button.ActualHeight/2),root);DependencyObject target=null;
   VisualTreeHelper.HitTest(root,delegate(DependencyObject node){var ui=node as UIElement;return ui!=null&&(ui.Visibility!=Visibility.Visible||!ui.IsHitTestVisible)?HitTestFilterBehavior.ContinueSkipSelfAndChildren:HitTestFilterBehavior.Continue;},delegate(HitTestResult result){target=result.VisualHit;return HitTestResultBehavior.Stop;},new PointHitTestParameters(point));
   bool found=false;for(var current=target;current!=null;current=MainWindow.SelectionParent(current)){var ui=current as UIElement;if(ui!=null&&(ui.Visibility!=Visibility.Visible||!ui.IsHitTestVisible||!ui.IsEnabled))return false;if(object.ReferenceEquals(current,button))found=true;}return found;
  }
  static void Measure(MainWindow main,FrameworkElement content,int width){content.Width=width;content.Height=680;content.Measure(new Size(width,680));content.Arrange(new Rect(0,0,width,680));content.UpdateLayout();StopTimers(main);}
  static void Render(FrameworkElement content,string path,int width){var bitmap=new RenderTargetBitmap(width,680,96,96,PixelFormats.Pbgra32);bitmap.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(path))encoder.Save(stream);}
  static void Layout(string root,string picture,Action<bool,string> check){
   string output=Path.Combine(root,"layout");Directory.CreateDirectory(output);
   using(var library=new Library(Path.Combine(output,"library"))){
    string image=library.Import(picture,"","Layout image","https://example.org/layout-image","","").Item.Id;
    string website=library.ImportBookmark("https://example.org/layout-website","Layout website","").Item.Id;
    string folder=library.CreateFolder("一个很长的分类名称：图片与视频参考","");library.AddToFolder(new[]{image},folder);
    var main=new MainWindow(library){PreviewMode=true};main.PendingInformationChoice=()=>{throw new Exception("layout measurement must not prompt");};StopTimers(main);
    var content=(FrameworkElement)main.Window.Content;main.Window.Content=null;content.Resources.MergedDictionaries.Add(main.Window.Resources);
    content.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty,main.Window.FontFamily);content.SetValue(System.Windows.Documents.TextElement.FontSizeProperty,main.Window.FontSize);content.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty,"Text");
    var sidebars=Field<SidebarLayout>(main,"sidebars");
    try{
     foreach(string theme in new[]{"dark","light"})foreach(int width in new[]{1040,1366})foreach(string mode in new[]{"materials","category-search-type","websites","website-search"})foreach(bool selected in new[]{false,true}){
      Theme.Set(theme);foreach(object key in Application.Current.Resources.Keys)content.Resources[key]=Application.Current.Resources[key];content.SetResourceReference(Panel.BackgroundProperty,"Canvas");
      sidebars.SetPreferred(true,width==1040?296:208);sidebars.SetPreferred(false,252);
      bool web=mode=="websites"||mode=="website-search",filtered=mode=="category-search-type"||mode=="website-search";
      main.SetSection(web?"bookmarks":"assets");if(mode=="category-search-type")main.RequestNavigate(folder);
      Find<TextBox>(main,"Search").Text=filtered?"Layout":"";Find<ComboBox>(main,"MediaFilter").SelectedIndex=mode=="category-search-type"?1:0;main.RefreshView();
      if(selected)Select(main,web?website:image);else{Field<HashSet<string>>(main,"selection").Clear();Call(main,"UpdateInspector");}
      Measure(main,content,width);
      string label=theme+" / "+width+" / "+mode+" / selected "+selected+": ";
      var header=Find<Grid>(main,"GalleryHeader");var heading=Find<Grid>(main,"GalleryHeading");var toolbar=Find<StackPanel>(main,"GalleryToolbar");var title=Find<TextBlock>(main,"ViewTitle");var count=Find<TextBlock>(main,"ViewCount");var multi=Find<Button>(main,"MultiSelect");var toggle=Find<Button>(main,"ToggleInspector");var center=Find<Grid>(main,"Center");
      double expectedCenter=width-(width==1040?296:208)-6-252-6;
      check(VisualTreeHelper.GetParent(content)==null&&Math.Abs(content.ActualWidth-width)<.5&&Math.Abs(center.ActualWidth-expectedCenter)<.5&&Math.Abs(header.ActualWidth-(expectedCenter-header.Margin.Left-header.Margin.Right))<.5,label+"actual full-page layout honors the requested window and center widths (root="+content.ActualWidth+", center="+center.ActualWidth+", header="+header.ActualWidth+")");
      bool headingFits=Bounds(heading,content).Right<=Bounds(toggle,content).Left+.1&&Bounds(heading,content).Bottom<=Bounds(toolbar,content).Top+.1&&Bounds(title,content).Right<=Bounds(count,content).Left+.1&&Bounds(count,content).Right<=Bounds(heading,content).Right+.1&&title.ActualWidth>20&&title.ActualWidth<=200.1;
      check(headingFits,label+"title and count stay beside details toggle, above the separate action row even with a long filtered category"+(headingFits?"":" (heading="+Bounds(heading,content)+", title="+Bounds(title,content)+", count="+Bounds(count,content)+", toggle="+Bounds(toggle,content)+", toolbar="+Bounds(toolbar,content)+")"));
      check(title.TextTrimming==TextTrimming.CharacterEllipsis&&Convert.ToString(title.ToolTip)==title.Text,label+"long names remain ellipsized with the full name accessible in a tooltip");
      var controls=new FrameworkElement[]{multi,Find<Button>(main,"ClearFilter"),Find<ComboBox>(main,"MediaFilter"),Find<ComboBox>(main,"Sort")}.Where(e=>e.Visibility==Visibility.Visible).ToArray();
      bool separated=true;for(int i=1;i<controls.Length;i++)separated&=Bounds(controls[i-1],content).Right<=Bounds(controls[i],content).Left+.1;
      bool contained=controls.All(e=>Bounds(e,content).Left>=Bounds(toolbar,content).Left-.1&&Bounds(e,content).Right<=Bounds(header,content).Right+.1&&Bounds(e,content).Top>=Bounds(toolbar,content).Top-.1&&Bounds(e,content).Bottom<=Bounds(toolbar,content).Bottom+.1);
      check(separated&&contained&&Bounds(toggle,content).Right<Bounds(center,content).Right&&toggle.ActualWidth==30&&toggle.ActualHeight==30&&toggle.IsEnabled&&Hit(content,toggle),label+"second-row controls are separated and contained; complete 30px details toggle is hit-testable (separated="+separated+", contained="+contained+", toggle="+Bounds(toggle,content)+", center="+Bounds(center,content)+", enabled="+toggle.IsEnabled+", hit="+Hit(content,toggle)+")");
      check(Find<TextBox>(main,"Search").ActualWidth>=140&&Bounds(Find<Button>(main,"More"),content).Right<=width&&Find<Button>(main,"Export").Visibility==(selected?Visibility.Visible:Visibility.Collapsed)&&Find<Button>(main,"AddBookmark").Visibility==(web?Visibility.Visible:Visibility.Collapsed),label+"real selection and website toolbar states fit the minimum window without clipping");
      check(Find<Button>(main,"ClearFilter").Visibility==(filtered?Visibility.Visible:Visibility.Collapsed)&&Find<ComboBox>(main,"MediaFilter").Visibility==(mode=="category-search-type"?Visibility.Visible:Visibility.Collapsed),label+"category and website filter visibility follows the real navigation state");
      Click(main,"ToggleInspector");Measure(main,content,width);
      check(!sidebars.RightVisible&&Hit(content,toggle)&&AutomationProperties.GetName(toggle)=="显示详情栏",label+"details can collapse from the visible header button and remain accessible to restore");
      Click(main,"ToggleInspector");Measure(main,content,width);
      check(sidebars.RightVisible&&sidebars.Right==252&&Field<HashSet<string>>(main,"selection").Count==(selected?1:0)&&Hit(content,toggle),label+"restoring details retains selection and the preferred sidebar width");
      if(width==1040&&((mode=="category-search-type"&&!selected)||(mode=="websites"&&selected)))Render(content,Path.Combine(output,theme+"-"+mode+"-1040.png"),width);
     }
     main.PublishQuickConversionResults(library,new[]{image});Measure(main,content,1040);
     var resultAction=Find<Button>(main,"QuickConversionResults");var thumbnail=Find<Slider>(main,"ThumbnailSize");
     check(resultAction.Visibility==Visibility.Visible&&resultAction.ActualHeight==26&&Hit(content,resultAction)&&Bounds(resultAction,content).Right<Bounds(thumbnail,content).Left&&Find<TextBlock>(main,"Status").ActualWidth>=140,"minimum-width result receipt is a compact hit-testable footer action, separate from thumbnail controls and status");
     main.ClearQuickConversionResults();
     main.SetSection("assets");main.RequestNavigate(folder);Find<TextBox>(main,"Search").Text="unmatched-layout-query";Find<ComboBox>(main,"MediaFilter").SelectedIndex=1;main.RefreshView();Measure(main,content,1040);
     var recovery=Find<Button>(main,"EmptyClearFilter");check(recovery.Visibility==Visibility.Visible&&recovery.ActualWidth>=80&&recovery.ActualHeight>=30&&Hit(content,recovery),"minimum-width empty-filter recovery is fully visible and actually hit-testable");
     Click(main,"EmptyClearFilter");Measure(main,content,1040);
     check(Visible(main).Single().Id==image&&Field<string>(main,"view")==folder&&recovery.Visibility==Visibility.Collapsed,"visible empty-filter action restores the matching category without moving or deleting an asset");
     check(!main.Window.IsVisible,"actual MainWindow layout and hit testing never display or control a desktop window");
    }finally{StopTimers(main);main.Window.Close();}
   }
  }
  static void ConversionResults(string root,string picture,Action<bool,string> check){
   using(var library=new Library(Path.Combine(root,"conversion-results-library")))using(var elsewhere=new Library(Path.Combine(root,"foreign-results-library"))){
    string folder=library.CreateFolder("Original project","");
    var source=library.Import(picture,folder,"Original picture","","original tags","").Item;
    var result=library.ImportConvertedFile(picture,"Converted result","","","",null,"keep").Item;
    var deleted=library.ImportConvertedFile(picture,"Deleted result","","","",null,"keep").Item;
    library.SetHiddenFromAll(new[]{result.Id},true);library.Trash(new[]{deleted.Id},true);
    byte[] original=File.ReadAllBytes(library.FullPath(source));
    var main=new MainWindow(library){PreviewMode=true};StopTimers(main);
    try{
     var button=Find<Button>(main,"QuickConversionResults");check(button.Visibility==Visibility.Collapsed,"conversion result action is absent before any completed task");
     main.SetSection("pictures");main.RequestNavigate(folder);Find<TextBox>(main,"Search").Text="Original";main.RefreshView();Select(main,source.Id);
     Find<TextBox>(main,"Name").Text="Edited original draft";main.PendingInformationChoice=()=>MessageBoxResult.Cancel;
     var ids=new[]{result.Id,result.Id};main.PublishQuickConversionResults(library,ids);ids[0]=source.Id;ids[1]=deleted.Id;
     Call(main,"Status","An unrelated status update");main.RefreshAfterQuickConversion();
     check(button.Visibility==Visibility.Visible&&Convert.ToString(button.Content)=="查看结果（1）"&&Field<string>(main,"view")==folder&&Find<TextBox>(main,"Search").Text=="Original"&&Field<HashSet<string>>(main,"selection").SetEquals(new[]{source.Id})&&Find<TextBox>(main,"Name").Text=="Edited original draft"&&Field<bool>(main,"refreshDeferred"),"task completion snapshots and deduplicates result IDs while preserving current search, category, selection and draft through unrelated status refreshes");
     check(!main.ViewQuickConversionResults()&&button.Visibility==Visibility.Visible&&Field<string>(main,"view")==folder&&Find<TextBox>(main,"Name").Text=="Edited original draft","canceling draft protection leaves the result receipt and current work intact");
     main.PendingInformationChoice=()=>MessageBoxResult.Yes;
     check(main.ViewQuickConversionResults()&&Field<HashSet<string>>(main,"selection").SetEquals(new[]{result.Id})&&Find<TextBox>(main,"Search").Text==""&&Field<string>(main,"mediaSection")=="pictures"&&library.All().Single(a=>a.Id==source.Id).Name=="Edited original draft","explicit view-results saves the original draft before navigating to the frozen result IDs, not later source selection");
     long navigation=Field<long>(main,"galleryNavigationGeneration");
     check(main.CompleteQuickConversionResultNavigation(navigation,new[]{result.Id})&&library.All().Single(a=>a.Id==result.Id).HiddenFromAll,"explicit result navigation can locate hidden-from-All items through their type page without changing the visibility preference");
     Find<TextBox>(main,"Search").Text="Changed before debounce";
     check(!main.CompleteQuickConversionResultNavigation(navigation,new[]{result.Id}),"a changed search invalidates deferred result scrolling before its debounce refresh can run");
     Find<TextBox>(main,"Search").Text="";main.RefreshView();main.ViewQuickConversionResults();navigation=Field<long>(main,"galleryNavigationGeneration");main.RequestNavigate(folder);
     check(!main.CompleteQuickConversionResultNavigation(navigation,new[]{result.Id})&&Field<string>(main,"view")==folder,"later user navigation cancels delayed conversion-result scrolling instead of pulling the gallery back");
     main.PublishQuickConversionResults(library,new[]{result.Id,deleted.Id});
     check(main.ViewQuickConversionResults()&&Field<HashSet<string>>(main,"selection").SetEquals(new[]{result.Id})&&Convert.ToString(button.Content)=="查看结果（1）"&&library.All().Single(a=>a.Id==deleted.Id).Deleted,"partly removed task results reveal only surviving copies and never resurrect recycled records");
     Set(main,"busy",true);check(!main.ViewQuickConversionResults()&&button.Visibility==Visibility.Visible,"busy library retains the receipt for retry instead of discarding successful conversion results");Set(main,"busy",false);
     main.ClearQuickConversionResults();main.PublishQuickConversionResults(elsewhere,new[]{result.Id});check(button.Visibility==Visibility.Collapsed,"a foreign-library result receipt cannot be published into the current gallery");
     main.PublishQuickConversionResults(library,new[]{deleted.Id});check(!main.ViewQuickConversionResults()&&button.Visibility==Visibility.Collapsed&&library.All().Single(a=>a.Id==deleted.Id).Deleted,"all-removed result action explains the missing copies and closes without changing the recycle bin");
     main.PublishQuickConversionResults(library,new[]{result.Id});main.PublishQuickConversionResults(library,new string[0]);check(button.Visibility==Visibility.Collapsed,"a task with no collected results exposes no dead view-results button");
     check(library.All().Count==3&&File.ReadAllBytes(library.FullPath(source)).SequenceEqual(original)&&library.AssetFolders(source.Id).Single().Id==folder&&!main.Window.IsVisible,"result receipts preserve original file bytes, source categories and every library record without showing a desktop window");
    }finally{StopTimers(main);main.PendingInformationChoice=()=>MessageBoxResult.No;main.Window.Close();}
   }
  }
  public static void Run(string root,Action<bool,string> check){
   root=Path.Combine(root,"homepage-usability");Directory.CreateDirectory(root);
   string data=Environment.GetEnvironmentVariable("MORI_DESK_DATA"),theme=Theme.Current;bool classic=Entry.ClassicEnabled,board=Entry.BoardEnabled,interactive=Entry.InteractiveShell;var previous=ThumbnailImage.CurrentLibrary;
   var app=Application.Current??new Application();var shutdown=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
   var synchronization=System.Threading.SynchronizationContext.Current;System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
   Environment.SetEnvironmentVariable("MORI_DESK_DATA",Path.Combine(root,"config"));Entry.ClassicEnabled=true;Entry.BoardEnabled=false;Entry.InteractiveShell=false;
   try{
    string picture=Path.Combine(root,"picture.png");using(var image=new System.Drawing.Bitmap(48,32)){using(var paint=System.Drawing.Graphics.FromImage(image))paint.Clear(System.Drawing.Color.MediumPurple);image.Save(picture,System.Drawing.Imaging.ImageFormat.Png);}
    Layout(root,picture,check);
    ConversionResults(root,picture,check);
    using(var library=new Library(Path.Combine(root,"library"))){
     string id=library.Import(picture,"","Original name","https://example.org/old","old tags","old notes").Item.Id;
     string web=library.ImportBookmark("https://example.org/saved","Example website","").Item.Id;
     string folder=library.CreateFolder("Project folder","");library.AddToFolder(new[]{id},folder);
     var main=new MainWindow(library){PreviewMode=true};main.PendingInformationChoice=()=>{throw new Exception("passive homepage refresh must not prompt");};StopTimers(main);
     try{
      Select(main,id);var updated=library.All().Single(a=>a.Id==id);updated.Name="Externally renamed";updated.Tags="current tags";updated.Notes="current notes";library.Update(updated);Call(main,"Refresh",false);
      check(Find<TextBox>(main,"Name").Text==updated.Name&&Find<TextBox>(main,"Tags").Text==updated.Tags&&Find<TextBox>(main,"Notes").Text==updated.Notes,"passive metadata reload updates selected inspector instead of leaving stale editable values");
      check(!(bool)Call(main,"HasUnsavedMetadata")&&!Field<bool>(main,"refreshDeferred")&&Field<HashSet<string>>(main,"selection").Contains(id),"external selected-item update does not create false drafts or drop selection");
      Find<TextBox>(main,"Tags").Text="my unsaved draft";updated=library.All().Single(a=>a.Id==id);updated.Notes="new external note";library.Update(updated);Call(main,"Refresh",false);
      check(Find<TextBox>(main,"Tags").Text=="my unsaved draft"&&Find<TextBox>(main,"Notes").Text=="current notes"&&Field<bool>(main,"refreshDeferred"),"real inspector drafts still postpone external refresh without prompting or overwriting");
      Find<TextBox>(main,"Tags").Text="current tags";Call(main,"Refresh",false);
      check(Find<TextBox>(main,"Notes").Text=="new external note"&&!(bool)Call(main,"HasUnsavedMetadata"),"reverting a real draft safely consumes the pending metadata reload");

      main.RequestNavigate(folder);Find<TextBox>(main,"Search").Text="no such picture";main.RefreshView();
      check(Visible(main).Count==0&&Find<TextBlock>(main,"EmptyTitle").Text=="没有匹配的素材"&&Find<Button>(main,"EmptyClearFilter").Visibility==Visibility.Visible&&Find<Button>(main,"EmptyImport").Visibility==Visibility.Collapsed,"no search results explain filtering and offer recovery instead of implying an empty library");
      Click(main,"EmptyClearFilter");
      check(Find<TextBox>(main,"Search").Text==""&&Visible(main).Single().Id==id&&Field<string>(main,"view")==folder&&Find<Button>(main,"EmptyClearFilter").Visibility==Visibility.Collapsed,"one-click recovery clears search while retaining project category and existing assets");
      Find<ComboBox>(main,"MediaFilter").SelectedIndex=2;
      check(Visible(main).Count==0&&Find<Button>(main,"EmptyClearFilter").Visibility==Visibility.Visible,"type-only empty results also offer filter recovery");
      Set(main,"similarId",id);Set(main,"colorId",id);Click(main,"EmptyClearFilter");
      check(Find<ComboBox>(main,"MediaFilter").SelectedIndex==0&&Field<string>(main,"similarId")==""&&Field<string>(main,"colorId")==""&&Visible(main).Count==1,"recovery clears every transient gallery filter without changing collection membership");
      main.SetSection("bookmarks");Find<TextBox>(main,"Search").Text="no such website";main.RefreshView();
      check(Find<TextBlock>(main,"EmptyTitle").Text=="没有匹配的网址"&&Find<Button>(main,"EmptyClearFilter").Visibility==Visibility.Visible,"bookmark empty search uses the same unambiguous recovery");Click(main,"EmptyClearFilter");
      check(Visible(main).Single().Id==web&&Field<bool>(main,"bookmarkMode")&&Find<Button>(main,"EmptyImport").Visibility==Visibility.Visible,"bookmark recovery retains website mode and restores normal import affordance");
      main.SetSection("shortcuts");Find<TextBox>(main,"Search").Text="no such local file";main.RefreshView();
      check(Find<Button>(main,"EmptyClearFilter").Visibility==Visibility.Visible&&Find<TextBlock>(main,"EmptyHint").Text.Contains("现有收藏"),"local shortcut empty search explains collections are untouched");Click(main,"EmptyClearFilter");
      check(Field<string>(main,"mediaSection")=="shortcuts"&&Find<Button>(main,"EmptyClearFilter").Visibility==Visibility.Collapsed&&Find<TextBlock>(main,"EmptyTitle").Text=="把文件拖到这里收藏","local recovery restores the original local empty state without importing files");
      main.SetSection("assets");main.RequestNavigate("trash");Find<TextBox>(main,"Search").Text="no such deleted file";main.RefreshView();Click(main,"EmptyClearFilter");
      check(Field<string>(main,"view")=="trash"&&Find<Button>(main,"EmptyImport").Visibility==Visibility.Collapsed&&Find<Button>(main,"EmptyClearFilter").Visibility==Visibility.Collapsed,"trash recovery does not expose import or navigate outside the recycle bin");

      main.SetSection("assets");var sidebars=Field<SidebarLayout>(main,"sidebars");sidebars.SetPreferred(false,370);int size=Field<int>(main,"thumbnailSize");string config=Path.Combine(Settings.ConfigDir,"settings.json");byte[] before=File.Exists(config)?File.ReadAllBytes(config):null;bool shown=sidebars.RightVisible;
      Click(main,"ToggleInspector");check(sidebars.RightVisible!=shown&&AutomationProperties.GetName(Find<Button>(main,"ToggleInspector"))==(shown?"显示详情栏":"收起详情栏"),"direct detail toggle exposes synchronized accessible action");
      main.ToggleSidebar(false);check(sidebars.RightVisible==shown&&sidebars.Right==370&&Field<int>(main,"thumbnailSize")==size,"keyboard/menu detail toggle restores preferred width without changing thumbnail density");
      var preferences=Settings.Load();preferences.RightSidebarVisible=false;preferences.RightSidebarWidth=390;preferences.ThumbnailSize=size;main.ApplyBrowsePreferences(preferences);
      check(!sidebars.RightVisible&&sidebars.Right==390&&AutomationProperties.GetName(Find<Button>(main,"ToggleInspector"))=="显示详情栏","browse settings update the direct details toggle together with sidebar visibility");Click(main,"ToggleInspector");
      check(before==null?!File.Exists(config):File.ReadAllBytes(config).SequenceEqual(before),"preview-only detail toggles never persist personal settings");
      check(!Find<TextBlock>(main,"InspectorEmptyHint").Text.Contains("双击")&&library.All().Count==2&&library.Members(folder,false).Contains(id),"empty inspector avoids duplicate shortcut copy and homepage recovery never modifies library data");

      Select(main,id);StopTimers(main);Set(main,"cancel",true);Call(main,"SearchByImage",false);WaitIdle(main);
      check(!Field<bool>(main,"cancel")&&Field<string>(main,"similarId")==id&&Visible(main).Any(a=>a.Id==id),"similar-image indexing works after a previous cancelled import");
      Click(main,"ClearFilter");Select(main,id);StopTimers(main);Set(main,"cancel",true);Call(main,"SearchByImage",true);WaitIdle(main);
      check(!Field<bool>(main,"cancel")&&Field<string>(main,"colorId")==id&&Visible(main).Any(a=>a.Id==id),"color indexing independently resets stale cancellation state");
      Click(main,"ClearFilter");Select(main,id);StopTimers(main);var asset=All(main).Single(a=>a.Id==id);asset.Perceptual="";library.Db.Exec("ALTER TABLE assets RENAME TO offline_assets");Call(main,"SearchByImage",false);WaitIdle(main);
      check(!Field<bool>(main,"busy")&&Find<TextBlock>(main,"Status").Text.StartsWith("图像索引未完成：")&&Find<TextBlock>(main,"Status").Text.Contains("可稍后重试"),"index database failure is reported locally and releases busy state instead of crashing the dispatcher");
      check(!main.Window.IsVisible,"homepage usability regression runs without desktop input or visible windows");
     }finally{StopTimers(main);main.Window.Close();}
    }
   }finally{Environment.SetEnvironmentVariable("MORI_DESK_DATA",data);Entry.ClassicEnabled=classic;Entry.BoardEnabled=board;Entry.InteractiveShell=interactive;Theme.Set(theme);ThumbnailImage.CurrentLibrary=previous;app.ShutdownMode=shutdown;System.Threading.SynchronizationContext.SetSynchronizationContext(synchronization);}
  }
 }
}
