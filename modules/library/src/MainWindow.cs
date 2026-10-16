using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms=System.Windows.Forms;

namespace Mori {
 public partial class MainWindow {
  public readonly Window Window;
  readonly Library library;
  public bool PreviewMode {get;set;}
  List<Asset> all=new List<Asset>(),visible=new List<Asset>();
  readonly HashSet<string> selection=new HashSet<string>();
  readonly DispatcherTimer searchTimer=new DispatcherTimer(),externalTimer=new DispatcherTimer();
  string view="all",anchor="",similarId="",colorId="",dataVersion="",mediaSection="assets";
  bool busy,cancel,dragged,bookmarkMode,refreshDeferred;Point dragOrigin;Asset pressed;
  int thumbnailSize=GalleryLayout.DefaultSize;bool sizeDirty;
  List<AssetRow> galleryRows=new List<AssetRow>();
  readonly DispatcherTimer layoutTimer=new DispatcherTimer(),sizeSaveTimer=new DispatcherTimer();
  readonly DispatcherTimer sidebarSaveTimer=new DispatcherTimer();SidebarLayout sidebars;bool sidebarDirty;
  T Find<T>(string name)where T:class{return Window.FindName(name)as T;}
  Button B(string name){return Find<Button>(name);}TextBox T(string name){return Find<TextBox>(name);}TextBlock L(string name){return Find<TextBlock>(name);}
  void Status(string text){L("Status").Text=text;}
  public MainWindow(Library lib) {
   library=lib;if(MoriDesk.Entry.ClassicEnabled)lib.EnableReferenceAssets();history=lib.IncludeReferences?new UndoHistory(lib.Db,"board"):new UndoHistory(lib.Db);ThumbnailImage.CurrentLibrary=lib;var settings=Settings.Load();thumbnailSize=settings.ThumbnailSize;Theme.Apply(settings);using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Mori.Main.xaml"))Window=(Window)XamlReader.Load(stream);Theme.Attach(Window,null);
   B("Settings").Click+=delegate{ShowSettings();};B("OpenSource").Click+=delegate{OpenSource(T("Source").Text);};T("Source").TextChanged+=delegate{Uri uri;B("OpenSource").IsEnabled=Selected().Count==1&&SourceLink.TryParse(T("Source").Text,out uri);};
   B("Import").Click+=delegate{ChooseFiles();};B("CreateFirstFolder").Click+=delegate{NewFolder("");};B("ImportBookmarks").Click+=delegate{ShowBookmarkImport();};B("NewBookmarkFolder").Click+=delegate{NewBookmarkFolder(CurrentFolder());};B("Videos").Click+=delegate{SetSection("videos");};B("Documents").Click+=delegate{SetSection("documents");};B("EmptyImport").Click+=delegate{if(bookmarkMode)AddBookmark("");else ChooseFiles();};B("Paste").Click+=delegate{Paste();};B("AddBookmark").Click+=delegate{AddBookmark("");};B("Export").Click+=delegate{Export();};B("More").Click+=delegate{More();};
   B("All").Click+=delegate{SetBookmarkMode(false);};B("Bookmarks").Click+=delegate{SetBookmarkMode(true);};B("Unfiled").Click+=delegate{RequestNavigate("unfiled");};B("Favorites").Click+=delegate{RequestNavigate("favorites");};B("Trash").Click+=delegate{RequestNavigate("trash");};
   B("Pictures").Click+=delegate{SetSection("pictures");};B("Audio").Click+=delegate{SetSection("audio");};
   B("NewFolder").Click+=delegate{NewFolder();};B("SaveMeta").Click+=delegate{SaveMetadata();};B("Categorize").Click+=delegate{Categorize();};B("Delete").Click+=delegate{Trash();};B("Star").Click+=delegate{Star();};B("Similar").Click+=delegate{SearchByImage(false);};B("ColorSearch").Click+=delegate{SearchByImage(true);};B("SelectionCanvas").Click+=delegate{AddSelectionToFreeCanvas();};B("Cancel").Click+=delegate{cancel=true;Status("正在完成当前文件，随后停止。已导入的素材会保留。");};B("ClearFilter").Click+=delegate{ClearGalleryFilters();};B("EmptyClearFilter").Click+=delegate{ClearGalleryFilters();};
   Find<TreeView>("Folders").SelectedItemChanged+=delegate(object s,RoutedPropertyChangedEventArgs<object> e){var node=e.NewValue as TreeViewItem;if(!reloadingFolders&&!folderManaging&&node!=null&&node.Tag is string)RequestNavigate((string)node.Tag);};
   Find<TreeView>("Folders").PreviewMouseRightButtonDown+=FolderRight;
   searchTimer.Interval=TimeSpan.FromMilliseconds(200);searchTimer.Tick+=delegate{searchTimer.Stop();RefreshView();};T("Search").TextChanged+=delegate{searchTimer.Stop();searchTimer.Start();};
   // Set the initial order before binding refresh handlers; the inspector and
   // local-collection hosts are not initialized yet during construction.
   Find<ComboBox>("Sort").SelectedIndex=settings.DefaultSort;
   Find<ComboBox>("Sort").SelectionChanged+=delegate{RefreshView();};Find<ComboBox>("MediaFilter").SelectionChanged+=delegate{RefreshView();};
   var gallery=Find<ListBox>("Gallery");gallery.PreviewMouseLeftButtonDown+=GalleryDown;Window.PreviewMouseMove+=GalleryMove;Window.PreviewMouseLeftButtonUp+=GalleryUp;gallery.PreviewMouseRightButtonDown+=GalleryRight;
   var sizeSlider=Find<Slider>("ThumbnailSize");sizeSlider.Value=thumbnailSize;L("ThumbnailSizeLabel").Text=thumbnailSize+" px";
   layoutTimer.Interval=TimeSpan.FromMilliseconds(65);layoutTimer.Tick+=delegate{layoutTimer.Stop();BuildRows(true);};
   sizeSaveTimer.Interval=TimeSpan.FromMilliseconds(600);sizeSaveTimer.Tick+=delegate{SaveThumbnailSize();};
   sizeSlider.ValueChanged+=delegate{int size=GalleryLayout.Clamp((int)Math.Round(sizeSlider.Value));if(size==thumbnailSize)return;thumbnailSize=size;sizeDirty=true;L("ThumbnailSizeLabel").Text=size+" px";QueueLayout();sizeSaveTimer.Stop();sizeSaveTimer.Start();};
   gallery.PreviewMouseWheel+=delegate(object sender,MouseWheelEventArgs e){GalleryLayout.HandleWheel(sizeSlider,e,Keyboard.Modifiers);};
   gallery.AddHandler(ThumbnailImage.DimensionsReadyEvent,new RoutedEventHandler(delegate{QueueLayout();}));
   sidebars=new SidebarLayout(Find<Grid>("Workspace"),Find<GridSplitter>("LeftSidebarHandle"),Find<GridSplitter>("RightSidebarHandle"),settings.LeftSidebarWidth,settings.RightSidebarWidth);
   InitializeGalleryView(settings);InitializeBatch();InitializeFolderBatch();
   InitializeBookmarkBrowser();new MiddleScroll(Window);
   sidebarSaveTimer.Interval=TimeSpan.FromMilliseconds(600);sidebarSaveTimer.Tick+=delegate{SaveSidebarWidths();};
   sidebars.Changed+=delegate{sidebarDirty=true;sidebarSaveTimer.Stop();sidebarSaveTimer.Start();};
   Window.PreviewDragOver+=CanvasProjectsDragOver;Window.PreviewDragEnter+=CanvasProjectsDragOver;Window.PreviewDrop+=CanvasProjectsDrop;
   Window.PreviewDragOver+=delegate(object s,DragEventArgs e){if(!e.Handled&&!canvasProjectsMode&&!MoriDesk.Entry.BoardEnabled&&!MoriDesk.Entry.ClassicEnabled&&IsLocalFileDrop(e.Data)&&!referenceMode){e.Effects=busy?DragDropEffects.None:DragDropEffects.Link;e.Handled=true;}};
   Window.PreviewDragOver+=DesktopImageOver;
   Window.PreviewDragEnter+=DesktopImageOver;
   Window.PreviewDragLeave+=(s,e)=>{desktopImageHover=false;HideDragHint();if(canvasProjectsPage!=null)canvasProjectsPage.ClearDropTarget();};
   Window.PreviewDrop+=DesktopImageDropped;
   Window.PreviewDrop+=DeskDrop;
   Window.PreviewDragOver+=BoardDragOver;
   Window.Drop+=delegate(object s,DragEventArgs e){if(!e.Handled&&!canvasProjectsMode&&IsLocalFileDrop(e.Data)){e.Handled=true;CollectLocalPaths((string[])e.Data.GetData(DataFormats.FileDrop));}};
   Window.PreviewKeyDown+=OnKey;
   gallery.SizeChanged+=delegate(object sender,SizeChangedEventArgs e){if(e.WidthChanged)QueueLayout();};
   Window.Closing+=delegate(object s,System.ComponentModel.CancelEventArgs e){if(busy&&!exitRequested){cancel=true;e.Cancel=true;Status("正在完成当前操作，请完成后再关闭。");}else if(ShouldCloseToTray(tray!=null,exitRequested,Settings.Load,settings.CloseToTray)){e.Cancel=true;HideToTray();}else{SaveThumbnailSize();SaveSidebarWidths();}};
   // Later handlers may cancel exit (capture confirmation / canvas save).
   // Keep synchronization alive until the window has actually closed.
   Window.Closed+=delegate{externalTimer.Stop();searchTimer.Stop();layoutTimer.Stop();sizeSaveTimer.Stop();sidebarSaveTimer.Stop();};
   externalTimer.Interval=TimeSpan.FromSeconds(3);externalTimer.Tick+=delegate{if(busy)return;if(tray!=null&&(!Window.IsVisible||Window.WindowState==WindowState.Minimized)){SuspendBackground();return;}string next=library.Db.Query("PRAGMA data_version")[0]["data_version"];if(dataVersion!=""&&next!=dataVersion){history.Clear();if(canvasProjectsMode)RefreshCanvasProjects();else if(boardMode)boardPage.Refresh();else if(referenceMode)referencePage.Refresh();else{ReloadFolders();Refresh(false);if(!refreshDeferred)Status("已同步浏览器收藏");}}dataVersion=next;};externalTimer.Start();
   B("RestoreTrash").Click+=delegate{RestoreTrash();};
   InitializeInspectorPreview();ReloadFolders();Refresh(true);if(!string.IsNullOrEmpty(library.DeletionRecoveryWarning))Status("上次删除的文件清理未完成："+library.DeletionRecoveryWarning);InitializeDesk();
  }
  List<Asset> Selected(){return all.Where(a=>selection.Contains(a.Id)).ToList();}
  string CurrentFolder(){return view.Length==32?view:"";}
  void Navigate(string target){HideCanvasProjects();HideBrowser();HideBoard();HideReferences();EndFolderManaging();view=target;similarId=colorId="";selection.Clear();anchor="";pressed=null;if(!bookmarkMode&&target!="all")mediaSection="media";T("Search").Clear();Find<ComboBox>("MediaFilter").SelectedIndex=0;Refresh(true);ReloadFolders();}
  void FolderDrop(object sender,DragEventArgs e){var node=sender as TreeViewItem;if(e.Handled||canvasProjectsMode||node==null||busy)return;string folder=(string)node.Tag;if(e.Data.GetDataPresent("Mori.AssetIds")){Edit("添加到分类："+node.Header,()=>library.AddToFolder((string[])e.Data.GetData("Mori.AssetIds"),folder));Refresh(false);RefreshCategoryLabel();e.Handled=true;}else if(IsLocalFileDrop(e.Data)){CollectLocalPaths((string[])e.Data.GetData(DataFormats.FileDrop));e.Handled=true;}}
  void RefreshCore(bool inspector) {
   if(canvasProjectsMode){RefreshCanvasProjects();ApplyCanvasProjectsPresentation();return;}
   if(browserMode){browserRefreshPending=true;browserInspectorRefreshPending|=inspector;ApplyBrowserPresentation();return;}
   if(boardMode){boardPage.Refresh();return;}if(referenceMode){referencePage.Refresh();return;}
   // Search, filters and external imports must not erase an inspector draft.
   // Keep the current selection/results until the user saves or explicitly discards it.
   if(!inspector&&HasUnsavedMetadata()){refreshDeferred=true;Status("右侧信息尚未保存，列表刷新会在保存或放弃编辑后继续。");return;}
   refreshDeferred=false;
   bool metadataRefreshed=!metadataLoaded||metadataReloadRequired;
   LoadMetadataForRefresh();IEnumerable<Asset> query=all.Where(a=>a.Deleted==(view=="trash")&&a.IsBookmark==bookmarkMode);
   if(view=="favorites")query=query.Where(a=>a.Favorite);
   string search=T("Search").Text.Trim();
   if(view=="unfiled"||(bookmarkMode&&view=="home"&&search.Length==0)){var ids=library.Classified();query=query.Where(a=>!ids.Contains(a.Id));}
   if(!bookmarkMode)query=query.Where(a=>SectionContains(a,mediaSection));
   // Visibility is a preference for the aggregate page, never a media type or deletion flag.
   if(!bookmarkMode&&view=="all"&&mediaSection=="assets")query=query.Where(a=>!a.HiddenFromAll);
   query=FilterLocalCollections(query,search);
   if(CurrentFolder()!=""){var ids=library.Members(view,!bookmarkMode||search.Length>0);query=query.Where(a=>ids.Contains(a.Id));}
   if(search.Length>0)query=query.Where(a=>(a.Name+"\n"+a.Tags+"\n"+a.Notes+"\n"+a.Source+(LocalCollectionsActive?"\n"+a.ReferencePath:"")).IndexOf(search,StringComparison.OrdinalIgnoreCase)>=0);
   int kind=Find<ComboBox>("MediaFilter").SelectedIndex;if(kind==1)query=query.Where(a=>SectionContains(a,"pictures"));else if(kind==2)query=query.Where(IsVideo);else if(kind==3)query=query.Where(a=>SectionContains(a,"documents"));else if(kind==4)query=query.Where(a=>a.IsAudio);
   int sort=Find<ComboBox>("Sort").SelectedIndex;if(sort==1)query=query.OrderBy(a=>a.Added);else if(sort==2)query=query.OrderBy(a=>a.Name,StringComparer.CurrentCultureIgnoreCase);else if(sort==3)query=query.OrderByDescending(a=>a.Bytes);
   if(similarId!=""){var target=all.FirstOrDefault(a=>a.Id==similarId);if(target!=null)query=query.Where(a=>!string.IsNullOrEmpty(a.Perceptual)).OrderBy(a=>Library.Similarity(target.Perceptual,a.Perceptual));}
   if(colorId!=""){var target=all.FirstOrDefault(a=>a.Id==colorId);if(target!=null)query=query.Where(a=>!string.IsNullOrEmpty(a.Color)).OrderBy(a=>ColorDistance(target.Color,a.Color));}
   visible=query.ToList();if(mediaSection=="shortcuts")visible=visible.OrderByDescending(a=>a.IsDirectory).ToList();var visibleIds=new HashSet<string>(visible.Select(a=>a.Id));bool selectionChanged=selection.RemoveWhere(id=>!visibleIds.Contains(id))>0;foreach(var a in visible){a.Selected=selection.Contains(a.Id);a.Selecting=batchSelecting;}
   string title=bookmarkMode&&(view=="home"||view=="all")?"网址收藏":view=="all"?(mediaSection=="pictures"?"图片 · 含动图":mediaSection=="videos"?"视频":mediaSection=="audio"?"音频":mediaSection=="documents"?"文档":mediaSection=="shortcuts"?"本地收藏":"全部素材"):view=="unfiled"?"未分类":view=="favorites"?"星标收藏":view=="trash"?"回收站":library.Folders().Where(f=>f.Id==view).Select(f=>f.Name).FirstOrDefault()??"分类";
   L("ViewTitle").Text=similarId!=""?"相似图片":colorId!=""?"相近颜色":title;L("ViewCount").Text=visible.Count+" 个素材 · "+Asset.Size(visible.Sum(a=>a.Bytes));
   Find<StackPanel>("Empty").Visibility=visible.Count==0?Visibility.Visible:Visibility.Collapsed;
   L("EmptyTitle").Text=all.Count==0?"给灵感一个整齐的家":"这里还没有素材";L("EmptyHint").Text=all.Count==0?"拖入电脑文件 → 本地收藏，只保存位置。\n点击“导入素材” → 在图库保存一份副本。":"可以导入素材、添加分类，或清除搜索和筛选。";
   B("EmptyImport").Content=all.Count==0?"导入第一批素材":"导入素材";B("ClearFilter").Visibility=(similarId!=""||colorId!=""||search!=""||kind>0)?Visibility.Visible:Visibility.Collapsed;
   foreach(string name in new[]{"All","Unfiled","Favorites","Trash"})B(name).SetResourceReference(Button.BackgroundProperty,name.ToLowerInvariant()==view?"AccentSoft":"Panel");
   UpdateBookmarkPage();
   if(bookmarkMode){L("ViewCount").Text=visible.Count+" 个网址";if(visible.Count==0){L("EmptyTitle").Text=search!=""?"没有匹配的网址":view=="trash"?"回收站为空":"这里还没有网址";L("EmptyHint").Text=search!=""?"试试其他关键词，或清除筛选。":"点击“收藏网址”，或从本机 Chrome 导入书签。\n网址可拖到上方文件夹中整理。";B("EmptyImport").Content="收藏网址";}}
   UpdateNavigationScope();UpdateLocalCollections();UpdateBookmarkBrowserStatus();UpdateEmptyRecoveryPresentation();BuildRows();if(inspector||selectionChanged||metadataRefreshed)UpdateInspector();else UpdateBatchActions();
  }
  void QueueLayout(){if(canvasProjectsMode||backgroundSuspended)return;layoutTimer.Stop();layoutTimer.Start();}
  void SaveThumbnailSize(){sizeSaveTimer.Stop();if(!sizeDirty)return;sizeDirty=false;if(PreviewMode)return;try{var settings=Settings.Load();settings.ThumbnailSize=thumbnailSize;settings.Save();}catch(Exception ex){Status("缩略图大小暂未保存："+ex.Message);}}
  void SaveSidebarWidths(){sidebarSaveTimer.Stop();if(!sidebarDirty)return;sidebarDirty=false;if(PreviewMode)return;try{var settings=Settings.Load();settings.LeftSidebarWidth=sidebars.Left;settings.RightSidebarWidth=sidebars.Right;settings.Save();}catch(Exception ex){Status("栏宽暂未保存："+ex.Message);}}
  internal static T VisualChild<T>(DependencyObject root)where T:DependencyObject{if(root==null)return null;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);var match=child as T;if(match!=null)return match;match=VisualChild<T>(child);if(match!=null)return match;}return null;}
  void BuildRows(){BuildRows(false);}
  void BuildRows(bool keepPosition){
   if(Window==null||canvasProjectsMode||backgroundSuspended)return;layoutTimer.Stop();var gallery=Find<ListBox>("Gallery");var scroll=VisualChild<ScrollViewer>(gallery);string topId=null;double fraction=0;
   if(keepPosition&&scroll!=null){double y=0;foreach(var row in galleryRows){if(y+row.Height>scroll.VerticalOffset){topId=row.Items[0].Asset.Id;fraction=(scroll.VerticalOffset-y)/row.Height;break;}y+=row.Height;}}
   galleryRows=mediaSection=="shortcuts"?GalleryLayout.BuildShortcuts(visible,Math.Max(40,gallery.ActualWidth-33),thumbnailSize):GalleryLayout.Build(visible,Math.Max(40,gallery.ActualWidth-33),thumbnailSize,waterfallLayout,showAssetNames,showAssetDetails);gallery.ItemsSource=galleryRows;
   if(topId!=null&&scroll!=null){double y=0;foreach(var row in galleryRows){if(row.Items.Any(t=>t.Asset.Id==topId)){gallery.UpdateLayout();scroll.ScrollToVerticalOffset(y+fraction*row.Height);break;}y+=row.Height;}}
  }
  void UpdateInspector() {
   var items=Selected();Find<StackPanel>("Inspector").IsEnabled=items.Count>0;L("SelectionInfo").Text=items.Count==0?"选中素材查看详情":items.Count==1?items[0].TypeLabel+" · "+items[0].Detail:"已选择 "+items.Count+" 个素材";
   bool single=items.Count==1;T("Source").IsReadOnly=single&&items[0].IsBookmark;T("Source").ToolTip=single&&items[0].IsBookmark?"收藏的网址；修改地址请新建网址收藏":"素材的来源网页";T("Name").IsEnabled=single;T("Notes").IsEnabled=single;T("Source").IsEnabled=single;T("Name").Text=single?items[0].Name:"";T("Tags").Text=single?items[0].Tags:"";T("Notes").Text=single?items[0].Notes:"";T("Source").Text=single?items[0].Source:"";
   L("Resolution").Text=single&&items[0].Width>0?items[0].Width+" × "+items[0].Height:"—";L("FileSize").Text=items.Count>0?Asset.Size(items.Sum(a=>a.Bytes)):"—";L("FileType").Text=single?items[0].TypeLabel:"—";L("CreatedTime").Text=single?FormatTime(items[0].Created):"—";L("AddedTime").Text=single?FormatTime(items[0].Added):"—";
   B("SaveMeta").Content=single?"保存信息":"追加标签";B("Similar").IsEnabled=single&&SectionContains(items[0],"pictures")&&view!="trash";B("ColorSearch").IsEnabled=B("Similar").IsEnabled;B("SelectionCanvas").IsEnabled=!busy&&view!="trash"&&items.Count>=2&&items.All(a=>SectionContains(a,"pictures"));B("Delete").Content=view=="trash"?"永久删除…":"移到回收站";B("RestoreTrash").Visibility=view=="trash"?Visibility.Visible:Visibility.Collapsed;B("Star").Content=items.Count>0&&items.All(a=>a.Favorite)?"取消星标":"星标收藏";B("Categorize").IsEnabled=B("Star").IsEnabled=view!="trash";
   L("CategoryInfo").Text=single?"分类："+string.Join(" / ",library.AssetFolders(items[0].Id).Select(f=>f.Name)):items.Count>1?"批量操作不会改变原文件。":"";
   B("Export").IsEnabled=items.Count>0&&!items.Any(a=>a.IsDirectory);UpdateInspectorPreview();UpdateBatchActions();UpdateLocalAddress(items);UpdateInspectorPresentation();
  }
  internal static DependencyObject SelectionParent(DependencyObject element){
   var content=element as ContentElement;if(content!=null){var parent=ContentOperations.GetParent(content);if(parent!=null)return parent;var framework=content as FrameworkContentElement;return framework==null?null:framework.Parent;}
   if(element is Visual||element is System.Windows.Media.Media3D.Visual3D){var parent=VisualTreeHelper.GetParent(element);if(parent!=null)return parent;}
   return LogicalTreeHelper.GetParent(element);
  }
  internal static bool IsGalleryBlank(DependencyObject source,DependencyObject gallery){
   for(var current=source;current!=null;current=SelectionParent(current)){
    if(ReferenceEquals(current,gallery))return true;
    var tile=current as Border;if(tile!=null&&tile.Tag is Asset)return false;
    if(current is System.Windows.Controls.Primitives.ScrollBar||current is System.Windows.Controls.Primitives.Thumb||current is System.Windows.Controls.Primitives.ButtonBase||current is System.Windows.Controls.Primitives.TextBoxBase||current is ComboBox||current is PasswordBox||current is MenuItem||current is System.Windows.Documents.Hyperlink)return false;
   }return false;
  }
  Border Tile(object element){var d=element as DependencyObject;while(d!=null){var border=d as Border;if(border!=null&&border.Tag is Asset)return border;d=SelectionParent(d);}return null;}
  void GalleryDown(object sender,MouseButtonEventArgs e){if(canvasProjectsMode)return;var tile=Tile(e.OriginalSource);if(tile==null){var gallery=Find<ListBox>("Gallery");if(IsGalleryBlank(e.OriginalSource as DependencyObject,gallery)){ClearGallerySelection();gallery.Focus();e.Handled=true;}return;}var a=(Asset)tile.Tag;
   if(e.ClickCount==2&&!batchSelecting){if(PrepareLibraryChange())Preview(a);pressed=null;e.Handled=true;return;}
   if(!SelectTile(a,Keyboard.Modifiers)){pressed=null;e.Handled=true;return;}pressed=a;dragOrigin=e.GetPosition(Window);dragged=false;Find<ListBox>("Gallery").Focus();e.Handled=true;
  }
  void GalleryUp(object sender,MouseButtonEventArgs e){if(canvasProjectsMode){pressed=null;return;}FinishTileSelection(pressed,dragged);pressed=null;}
  void GalleryMove(object sender,MouseEventArgs e){if(canvasProjectsMode||pressed==null||e.LeftButton!=MouseButtonState.Pressed||dragged)return;Point point=e.GetPosition(Window);if(Math.Abs(point.X-dragOrigin.X)<SystemParameters.MinimumHorizontalDragDistance&&Math.Abs(point.Y-dragOrigin.Y)<SystemParameters.MinimumVerticalDragDistance)return;RestoreDragSelection();var items=Selected();if(items.Count==0)return;dragged=true;var data=new DataObject();data.SetData(DataFormats.FileDrop,items.Select(library.FullPath).ToArray());data.SetData("Mori.AssetIds",items.Select(a=>a.Id).ToArray());try{DragDrop.DoDragDrop(Window,data,DragDropEffects.Copy);}finally{HighlightLocalCollection(null);pressed=null;}}
  void SyncSelection(){foreach(var a in visible){a.Selected=selection.Contains(a.Id);a.Selecting=batchSelecting;}UpdateInspector();if(refreshDeferred)Refresh(true);}
  void ClearGallerySelection(){if(canvasProjectsMode||!PrepareLibraryChange())return;selection.Clear();anchor="";pressed=null;dragged=false;Find<ListBox>("Gallery").UnselectAll();SyncSelection();}
  void GalleryRight(object sender,MouseButtonEventArgs e){if(canvasProjectsMode)return;var tile=Tile(e.OriginalSource);if(tile==null)return;var a=(Asset)tile.Tag;if(!selection.Contains(a.Id)){if(!PrepareLibraryChange()){e.Handled=true;return;}selection.Clear();selection.Add(a.Id);SyncSelection();}var menu=CreateAssetMenu(a);menu.IsOpen=true;e.Handled=true;}
  void OnKey(object sender,KeyEventArgs e){
   if(canvasProjectsMode){canvasProjectsPage.HandleKey(e);return;}
   if(HandleBrowserKey(e))return;
   if(boardMode){boardPage.HandleKey(e);return;}
   if(referenceMode){referencePage.HandleKey(e);return;}
   if(HandleGalleryViewKey(e))return;
   if(Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase){if(e.Key==Key.Escape){Find<ListBox>("Gallery").Focus();e.Handled=true;}return;}
   if(LocalCollectionsActive&&localCollection!=""&&(e.Key==Key.Back||(e.Key==Key.Escape&&selection.Count==0))){OpenLocalCollection("");e.Handled=true;return;}
   if(e.Key==Key.Z&&Keyboard.Modifiers==ModifierKeys.Control){UndoLast();e.Handled=true;return;}
   if(HandleFolderKey(e))return;
   if(e.Key==Key.Escape){
    for(var current=e.OriginalSource as DependencyObject;current!=null;current=SelectionParent(current)){var combo=current as ComboBox;if(combo!=null&&combo.IsDropDownOpen)return;}
    if(batchSelecting)SetBatchSelecting(false);else ClearGallerySelection();Find<ListBox>("Gallery").Focus();e.Handled=true;
   }else if(e.Key==Key.Space){var a=Selected().FirstOrDefault();if(a!=null)Preview(a);e.Handled=true;}
   else if(e.Key==Key.Delete){Trash();e.Handled=true;}
   else if(Keyboard.Modifiers==ModifierKeys.Control){
    if(e.Key==Key.V){Paste();e.Handled=true;}
    if(e.Key==Key.A){SelectAllVisible();e.Handled=true;}
    if(e.Key==Key.F){T("Search").Focus();e.Handled=true;}
    if(e.Key==Key.O){ChooseFiles();e.Handled=true;}
    if(e.Key==Key.C){CopySelection();e.Handled=true;}
   }
  }
  void ChooseFiles(){if(canvasProjectsMode)return;if(bookmarkMode)ShowBookmarkImport();else if(LocalCollectionsActive)ChooseReferenceFiles();else ChooseMediaFiles();}
  async void ImportPaths(string[] paths,string target){if(canvasProjectsMode||busy||!PrepareLibraryChange())return;history.Clear();busy=true;cancel=false;B("Cancel").Visibility=Visibility.Visible;B("Import").IsEnabled=false;B("Paste").IsEnabled=false;string folder=target??CurrentFolder();int added=0,duplicates=0,skipped=0,failed=0;string duplicatePolicy="ask";var errors=new List<string>();var importedIds=new HashSet<string>();
   try{await Task.Run(delegate{foreach(var path in Library.ExpandFiles(paths)){if(cancel)break;try{var result=library.ImportLocalFile(path,folder,duplicatePolicy=="skip"?"ask":duplicatePolicy);if(result.ChoiceRequired){string choice="skip";bool allDuplicates=false;if(duplicatePolicy!="skip")Window.Dispatcher.Invoke(new Action(delegate{choice=DuplicateWindow.Ask(Window,library,result.Item,path,true,out allDuplicates);}));if(allDuplicates)duplicatePolicy=choice;result=library.ImportLocalFile(path,folder,choice);}if(!result.Skipped)importedIds.Add(result.Item.Id);if(result.Skipped)skipped++;else if(result.Duplicate&&!result.KeptCopy)duplicates++;else added++;}catch(Exception ex){failed++;if(errors.Count<5)errors.Add(Path.GetFileName(path)+"："+ex.Message);}int total=added+duplicates+skipped+failed;Window.Dispatcher.Invoke(new Action(delegate{Status("正在导入 "+total+" · "+Path.GetFileName(path));if(total==1||total%12==0)Refresh(false);}));}});string location=RevealImported(importedIds,folder);Refresh(true);Status((cancel?"已停止":"导入完成")+"：新增 "+added+" · 使用已有 "+duplicates+" · 跳过 "+skipped+" · 失败 "+failed+location);if(failed>0)MessageBox.Show(Window,string.Join("\n",errors),"部分素材未导入");}
   catch(Exception ex){MessageBox.Show(Window,ex.Message,"导入未完成");}
   finally{busy=false;B("Cancel").Visibility=Visibility.Collapsed;B("Import").IsEnabled=true;B("Paste").IsEnabled=true;}
  }
  async void Paste(){if(canvasProjectsMode||busy||!PrepareLibraryChange())return;if((MoriDesk.Entry.BoardEnabled||MoriDesk.Entry.ClassicEnabled)&&Clipboard.ContainsFileDropList()){CollectLocalPaths(Clipboard.GetFileDropList().Cast<string>().ToArray());return;}history.Clear();try{if(Clipboard.ContainsFileDropList()){ImportPaths(Clipboard.GetFileDropList().Cast<string>().ToArray(),null);return;}if(Clipboard.ContainsImage()){var bitmap=Clipboard.GetImage();string dir=Path.Combine(Path.GetTempPath(),"Mori-paste");Directory.CreateDirectory(dir);string file=Path.Combine(dir,Guid.NewGuid().ToString("N")+".png");try{var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(file))encoder.Save(stream);busy=true;string folder=boardMode?boardPage.Group:CurrentFolder();var item=await Task.Run(()=>library.Import(file,folder,"截图 "+DateTime.Now.ToString("yyyy-MM-dd HHmmss"),"","","","","ask"));if(item.ChoiceRequired){bool apply;string choice=DuplicateWindow.Ask(Window,library,item.Item,file,false,out apply);if(choice=="skip"){Status("已取消收藏");return;}item=await Task.Run(()=>library.Import(file,folder,"截图 "+DateTime.Now.ToString("yyyy-MM-dd HHmmss"),"","","","",choice));}bookmarkMode=false;mediaSection="pictures";view="all";T("Search").Clear();Find<ComboBox>("MediaFilter").SelectedIndex=0;Refresh(false);selection.Clear();selection.Add(item.Item.Id);Refresh(true);Status(item.KeptCopy?"已另存一份截图":item.Duplicate?"已使用图库中已有的截图":"截图已收藏到图库");}finally{if(File.Exists(file))File.Delete(file);busy=false;}}else if(Clipboard.ContainsText()){Uri address;if(SourceLink.TryParse(Clipboard.GetText(),out address))AddBookmark(address.AbsoluteUri);else Status("剪贴板不是有效的 HTTP / HTTPS 网址，也没有图片或文件。");}else Status("剪贴板没有图片、文件或网址。");}catch(Exception ex){MessageBox.Show(Window,ex.Message,"无法粘贴");}}
  void Categorize(){ChooseCategory(false);}
  string FolderLabel(Category f,List<Category> allFolders){string label=f.Name;var seen=new HashSet<string>{f.Id};while(!string.IsNullOrEmpty(f.Parent)){f=allFolders.FirstOrDefault(x=>x.Id==f.Parent);if(f==null||!seen.Add(f.Id))break;label=f.Name+" / "+label;}return label;}
  void SaveMetadata(){if(canvasProjectsMode||busy)return;var items=Selected();if(items.Count==0)return;if(items.Count==1&&string.IsNullOrWhiteSpace(T("Name").Text)){Status("名称不能为空");return;}Edit("保存信息",delegate{foreach(var a in items){if(items.Count==1){if(string.IsNullOrWhiteSpace(T("Name").Text)){Status("名称不能为空");return;}a.Name=T("Name").Text.Trim();a.Notes=T("Notes").Text;a.Source=T("Source").Text;a.Tags=T("Tags").Text;}else a.Tags=string.Join(", ",(a.Tags+","+T("Tags").Text).Split(new[]{',','，',';'},StringSplitOptions.RemoveEmptyEntries).Select(t=>t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));library.Update(a);}});Refresh(true);Find<ListBox>("Gallery").Focus();}
  void Trash(){
   if(canvasProjectsMode)return;
   if(busy||selection.Count==0||!PrepareLibraryChange())return;
   if(view=="trash"){DeleteForever();return;}
   if(Settings.Load().ConfirmTrash&&MessageBox.Show(Window,"将所选 "+selection.Count+" 项移到回收站或移除快捷收藏？\n快捷收藏不会删除原文件，可用 Ctrl+Z 撤销。","确认移除",MessageBoxButton.OKCancel,MessageBoxImage.Question,MessageBoxResult.Cancel)!=MessageBoxResult.OK)return;
   int count=selection.Count;bool refs=Selected().Any(a=>a.IsReference);Edit((refs?"移除收藏 ":"移入回收站 ")+count+" 项",()=>TrashAction.Apply(library,selection,false));
   selection.Clear();Refresh(true);Status(refs?"已移除所选收藏 · 快捷方式的原文件不删除，图库副本进回收站 · Ctrl+Z 撤销":"已将 "+count+" 项移入回收站 · Ctrl+Z 撤销，原文件保留。");
  }
  void RestoreTrash(){
   if(canvasProjectsMode)return;
   if(busy||view!="trash"||selection.Count==0||!PrepareLibraryChange())return;
   Edit("恢复回收站素材",()=>TrashAction.Apply(library,selection,true));
   selection.Clear();Refresh(true);Status("素材已恢复");
  }
  async void DeleteForever(){
   if(canvasProjectsMode)return;
   if(busy||view!="trash"||selection.Count==0||!PrepareLibraryChange())return;
   busy=true;bool started=false;
   try{
    var ids=TrashAction.ConfirmPermanent(library,selection,delegate(int count){
     return MessageBox.Show(Window,TrashAction.PermanentWarning(count),"永久删除 · 无法恢复",MessageBoxButton.OKCancel,MessageBoxImage.Warning,MessageBoxResult.Cancel)==MessageBoxResult.OK;
    });
    if(ids==null)return;
    history.Clear();started=true;Window.IsEnabled=false;searchTimer.Stop();layoutTimer.Stop();Status("正在永久删除 "+ids.Length+" 个素材…");
    PermanentDeleteResult result;
    using(await ThumbnailImage.PauseAndDrainAsync())result=await Task.Run(()=>library.PermanentlyDelete(ids));
    selection.Clear();Refresh(true);
    if(string.IsNullOrEmpty(result.CleanupWarning))Status("已永久删除 "+result.Count+" 个素材，无法从回收站恢复。");
    else{Status("素材已从图库删除，但磁盘文件尚未完全清理。");MessageBox.Show(Window,"已从图库删除 "+result.Count+" 个素材，但部分暂存文件暂时无法清理。下次启动会重试。\n\n"+result.CleanupWarning,"磁盘清理未完成",MessageBoxButton.OK,MessageBoxImage.Warning);}
   }catch(Exception error){if(started)Refresh(true);MessageBox.Show(Window,error.Message,"永久删除未完成",MessageBoxButton.OK,MessageBoxImage.Warning);}
   finally{busy=false;Window.IsEnabled=true;if(started)BuildRows();}
  }
  void Star(){if(canvasProjectsMode||busy||view=="trash"||!PrepareLibraryChange())return;var items=Selected();bool value=!items.All(a=>a.Favorite);Edit(value?"添加星标":"取消星标",delegate{foreach(var a in items){a.Favorite=value;library.Update(a);}});Refresh(true);}
  async void SearchByImage(bool color){var a=Selected().FirstOrDefault();if(canvasProjectsMode||a==null||busy)return;cancel=false;busy=true;Status("正在建立本地缩略图索引，只分析本机图片…");try{await Task.Run(delegate{foreach(var item in all.Where(x=>!x.Deleted)){if(cancel)break;library.BuildThumbnail(item);}});if(string.IsNullOrEmpty(color?a.Color:a.Perceptual)){Status("此格式暂不支持图像相似度索引");return;}if(color){colorId=a.Id;similarId="";}else{similarId=a.Id;colorId="";}view="all";Refresh(false);Status(color?"按平均颜色接近程度排序":"按视觉指纹接近程度排序；这不是 AI 语义搜索");}catch(Exception ex){Status("图像索引未完成："+ex.Message+" · 可稍后重试");}finally{busy=false;}}
  static double ColorDistance(string one,string two){try{var a=(Color)ColorConverter.ConvertFromString(one);var b=(Color)ColorConverter.ConvertFromString(two);return Math.Pow(a.R-b.R,2)+Math.Pow(a.G-b.G,2)+Math.Pow(a.B-b.B,2);}catch{return double.MaxValue;}}
  public static bool IsVideo(Asset a){return MediaTypes.Contains(a,"videos");}
  void Preview(Asset a){a=ResolveReferenceForOpen(a);if(a==null)return;if(a.IsBookmark){if(Settings.Load().BookmarkOpenInApp)BrowseBookmark(a.Source);else OpenSource(a.Source);return;}if(a.IsDirectory||(a.IsReference&&!MediaTypes.Pictures.Contains(a.Ext)&&!MediaTypes.Videos.Contains(a.Ext)&&!MediaTypes.Audio.Contains(a.Ext)&&!MediaTypes.Documents.Contains(a.Ext))){OpenReference(a);return;}try{FilePreview.Show(Window,library.FullPath(a),a.Name);}catch(Exception error){MessageBox.Show(Window,error.Message,"无法打开预览",MessageBoxButton.OK,MessageBoxImage.Warning);}}
  void OpenFile(Asset a){a=ResolveReferenceForOpen(a);if(a==null)return;if(a.IsBookmark){OpenSource(a.Source);return;}try{MoriDesk.FileRouter.OpenDefault(Window,library.FullPath(a));}catch(Exception ex){MessageBox.Show(Window,ex.Message,"无法打开原文件");}}
  async void Export(){var items=Selected();if(canvasProjectsMode||items.Count==0||busy)return;if(items.Any(a=>a.IsDirectory)){Status("文件夹快捷方式不打包复制，请用“打开位置”查看原文件夹。");return;}var mode=MessageBox.Show(Window,"保留分类层级吗？\n\n是：按分类建立文件夹；多分类素材会导出到各个分类。\n否：全部放入同一个文件夹。\n\n原图不压缩，同名文件自动加序号。","导出 "+items.Count+" 个素材",MessageBoxButton.YesNoCancel);if(mode==MessageBoxResult.Cancel)return;string dir=FolderPicker("选择导出目标文件夹");if(dir==null)return;string prefix=Prompt("导出命名","保持原名请留空；填写前缀则批量编号","");if(prefix==null)return;busy=true;Status("正在导出原图…");try{int count=await Task.Run(()=>library.Export(items,dir,mode==MessageBoxResult.Yes,prefix));Status("已导出 "+count+" 个文件 · "+dir);Process.Start("explorer.exe",dir);}catch(Exception ex){Status("导出未全部完成："+ex.Message);MessageBox.Show(Window,ex.Message,"导出未全部完成");}finally{busy=false;}}
  async void Backup(){if(busy||!PrepareLibraryChange())return;var dlg=new Microsoft.Win32.SaveFileDialog{Title="备份整个图库（含回收站）",Filter="Nook 备份|*.mori.zip",FileName="Nook-"+DateTime.Now.ToString("yyyyMMdd-HHmm")+".mori.zip",OverwritePrompt=true};if(dlg.ShowDialog(Window)!=true)return;if(File.Exists(dlg.FileName)){MessageBox.Show(Window,"请使用新的备份名称，已有备份不会覆盖。","备份");return;}busy=true;Status("正在备份原图及分类索引…");try{await Task.Run(()=>library.Backup(dlg.FileName));Status("备份完成："+dlg.FileName);}catch(Exception ex){MessageBox.Show(Window,ex.Message,"备份失败");}finally{busy=false;}}
  async void Restore(){if(busy)return;var dlg=new Microsoft.Win32.OpenFileDialog{Filter="Nook 备份|*.mori.zip"};if(dlg.ShowDialog(Window)!=true)return;var dir=FolderPicker("选择一个空文件夹恢复图库");if(dir==null)return;busy=true;try{await Task.Run(()=>Library.Restore(dlg.FileName,dir));MessageBox.Show(Window,"恢复完成。通过“更多 → 图库管理 → 选择 / 创建另一个图库”打开。\n"+dir,"恢复成功");}catch(Exception ex){MessageBox.Show(Window,ex.Message,"无法恢复");}finally{busy=false;}}
  async void Migrate(){if(busy||!PrepareLibraryChange())return;var dir=FolderPicker("选择 Eagle 的 .library 图库文件夹");if(dir==null)return;if(MessageBox.Show(Window,"将读取并复制所选 Eagle 图库到 Nook。\n\n不会修改或删除 Eagle 的原图库；可能需要较多磁盘空间。\n现在开始吗？","确认复制迁移",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;history.Clear();busy=true;try{string result=await Task.Run(()=>library.MigrateEagle(dir,s=>Window.Dispatcher.BeginInvoke(new Action(()=>Status(s)))));bookmarkMode=false;mediaSection="assets";ReloadFolders();Navigate("all");MessageBox.Show(Window,result+"\n网址和 PDF 文档可在左侧对应页面查看。","迁移结果");}catch(Exception ex){MessageBox.Show(Window,ex.Message,"迁移未完成");}finally{busy=false;}}
  public static string FormatTime(string value){DateTimeOffset time;return DateTimeOffset.TryParse(value,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out time)?time.LocalDateTime.ToString("yyyy/MM/dd HH:mm"):"—";}
  void ShowSettings(){ShowDeskSettings();}
  void ShowLibrarySettings(){if(busy){Status("请等待当前导入或导出完成后再更改设置。");return;}var request=SettingsWindow.Show(Window,PreviewMode,library);if(request!=null)ChangeStorage(request);}
  internal Func<MessageBoxResult> PendingInformationChoice;
  bool HasUnsavedMetadata(){var items=Selected();return items.Count==1?(T("Name").Text!=items[0].Name||T("Tags").Text!=items[0].Tags||T("Source").Text!=items[0].Source||T("Notes").Text!=items[0].Notes):items.Count>1&&!string.IsNullOrWhiteSpace(T("Tags").Text);}
  bool PrepareLibraryChange(){
   var items=Selected();if(!HasUnsavedMetadata())return true;
   var choice=PendingInformationChoice!=null?PendingInformationChoice():MessageBox.Show(Window,"右侧有尚未保存的信息。要先保存再继续吗？","保存信息",MessageBoxButton.YesNoCancel);
   if(choice==MessageBoxResult.Cancel)return false;
   if(choice==MessageBoxResult.Yes){if(items.Count==1&&string.IsNullOrWhiteSpace(T("Name").Text)){Status("请先填写名称再保存。");return false;}SaveMetadata();}
   else UpdateInspector(); // Discard only after an explicit choice, never during passive refresh.
   return true;
  }
  void ChangeStorage(StorageChangeRequest request){
   if(busy||request==null||LibraryTransfer.SameRoot(request.Path,library.Root)||!PrepareLibraryChange())return;
   busy=true;searchTimer.Stop();bool success=false;
   try {
    success=TransferWindow.Run(Window,async (progress,stopped,committing)=>{
     using(await ThumbnailImage.PauseAndDrainAsync()){
      await Task.Run(delegate{
       if(stopped())throw new OperationCanceledException();
       LibraryTransfer.Transfer(library,request.Path,progress,stopped,delegate(string path){if(stopped())throw new OperationCanceledException();committing();LibraryActivation.Open(path,PreviewMode);});
      });
     }
    });
   }finally{busy=false;}
   if(success)CloseForReplacement();else{BuildRows();Status("仍使用原图库："+library.Root);}
  }
  void OpenSource(string value){Uri uri;if(!SourceLink.TryParse(value,out uri)){Status("请填写有效的 http 或 https 来源网址");return;}try{Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});}catch(Exception ex){Status("无法打开链接："+ex.Message);}}
  async void SwitchLibrary(){if(busy||!PrepareLibraryChange())return;var dir=FolderPicker("选择已有图库，或空文件夹创建新图库（不会迁移当前素材）");if(dir==null||LibraryTransfer.SameRoot(dir,library.Root))return;if(!File.Exists(Path.Combine(dir,".mori","library.db"))&&Directory.EnumerateFileSystemEntries(dir).Any()){MessageBox.Show(Window,"创建图库请选择空文件夹，以免和已有文件混在一起。","选择图库");return;}busy=true;Window.IsEnabled=false;bool success=false;try{await Task.Run(()=>LibraryActivation.Open(dir,PreviewMode));success=true;}catch(Exception ex){MessageBox.Show(Window,ex.Message,"无法切换图库");}finally{busy=false;Window.IsEnabled=true;}if(success)CloseForReplacement();}
  static void Menu(ContextMenu menu,string label,Action action){var item=new MenuItem{Header=label};item.Click+=delegate{action();};menu.Items.Add(item);}
  string FolderPicker(string title){using(var dlg=new Forms.FolderBrowserDialog{Description=title,ShowNewFolderButton=true})return dlg.ShowDialog(new WindowHandle(Window))==Forms.DialogResult.OK?dlg.SelectedPath:null;}
  string Prompt(string title,string hint,string value){var dialog=new Window{Owner=Window,Title=title,Width=410,Height=200,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Window.Background,Foreground=Window.Foreground};var box=new TextBox{Text=value,Margin=new Thickness(0,14,0,18),Padding=new Thickness(8)};var ok=new Button{Content="确认",Width=84,Padding=new Thickness(8),IsDefault=true,HorizontalAlignment=HorizontalAlignment.Right};ok.Click+=delegate{dialog.DialogResult=true;};var stack=new StackPanel{Margin=new Thickness(22)};stack.Children.Add(new TextBlock{Text=hint,TextWrapping=TextWrapping.Wrap});stack.Children.Add(box);stack.Children.Add(ok);Theme.Attach(dialog,Window);dialog.Content=stack;dialog.Loaded+=delegate{box.Focus();box.SelectAll();};dialog.PreviewKeyDown+=delegate(object s,KeyEventArgs e){if(e.Key==Key.Escape)dialog.Close();};return dialog.ShowDialog()==true?box.Text:null;}
 }
 public class WindowHandle:Forms.IWin32Window{public IntPtr Handle{get;private set;}public WindowHandle(Window window){Handle=new System.Windows.Interop.WindowInteropHelper(window).Handle;}}
}
