using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using MoriDesk;
namespace Mori {
 public partial class MainWindow {
  ReferencePage referencePage;bool referenceMode;Button deskTools,referenceNav;readonly System.Collections.Generic.Dictionary<UIElement,Visibility> referenceCovered=new System.Collections.Generic.Dictionary<UIElement,Visibility>();
  bool deskLoaded,deskDeferred,deskServicesActive;
  public void DeferDeskServices(){deskDeferred=true;}
  public void StartDeskServices(){
   deskDeferred=false;if(!deskLoaded||deskServicesActive||(!Entry.InteractiveShell&&!PreviewMode))return;
   deskServicesActive=true;DeskRuntime.Notify=NotifyFromTools;DeskRuntime.CollectImage=CollectScreenshot;DeskRuntime.OpenSettings=()=>OpenDeskSettings("capture");DeskRuntime.Start(PreviewMode,library.Root);if(!PreviewMode)BrowserConnection.StartLocal();
  }
  void InitializeDesk(){
   Window.Title="Mori Desk · 素材与工具";
   deskTools=new Button{Content="工具",ToolTip="截图 · 输入翻译 · 看图 · 3D 查看",Style=B("More").Style};UiIcons.SetGlyph(deskTools,"\uE713");var actions=(Panel)B("More").Parent;actions.Children.Insert(actions.Children.IndexOf(B("More")),deskTools);deskTools.Click+=delegate{ToolMenu(deskTools);};
   var nav=new Button{Content="本地收藏",ToolTip="电脑文件拖入默认在这里 · 只存位置，不复制文件",Style=B("All").Style};referenceNav=nav;UiIcons.SetGlyph(nav,"\uE8B7");var navigation=(Panel)B("All").Parent;navigation.Children.Insert(navigation.Children.IndexOf(B("Documents"))+1,nav);nav.Click+=delegate{ShowReferences();};
   Window.Loaded+=delegate{deskLoaded=true;if(!deskDeferred)StartDeskServices();Application.Current.ShutdownMode=ShutdownMode.OnMainWindowClose;};
   Window.Closing+=delegate(object s,System.ComponentModel.CancelEventArgs e){if(!e.Cancel&&!DeskRuntime.PrepareExit(Window))e.Cancel=true;};
   // Keep the capture lease until pin windows have finished saving on shutdown.
   Application.Current.Exit+=delegate{BrowserConnection.StopLocal();DeskRuntime.Stop();};
   if(Entry.BoardEnabled)ShowBoard("all");if(Entry.ClassicEnabled){InitializeLocalAddress();UpdateInspector();UpdateNavigationScope();}
  }
  void CoverForReferences(UIElement element){if(element==null||referenceCovered.ContainsKey(element))return;referenceCovered[element]=element.Visibility;element.Visibility=Visibility.Collapsed;}
  void ShowReferences(){if(Entry.ClassicEnabled){SetSection("shortcuts");return;}if(Entry.BoardEnabled){ShowBoard("shortcuts");return;}if(busy||(!referenceMode&&!PrepareLibraryChange()))return;if(referencePage==null){referencePage=new ReferencePage(Window,new ReferenceStore(library.Db),HideReferences,Status,(path,name)=>FileRouter.Open(Window,path,name));Grid.SetColumn(referencePage,2);Grid.SetColumnSpan(referencePage,3);Panel.SetZIndex(referencePage,10);Find<Grid>("Workspace").Children.Add(referencePage);}if(!referenceMode){referenceCovered.Clear();foreach(UIElement child in Find<Grid>("Workspace").Children)if(child!=referencePage&&Grid.GetColumn(child)>=2)CoverForReferences(child);foreach(string name in new[]{"Import","Paste","AddBookmark","Export"})CoverForReferences(B(name));CoverForReferences((UIElement)T("Search").Parent);CoverForReferences((UIElement)Find<Slider>("ThumbnailSize").Parent);CoverForReferences(Find<TreeView>("Folders"));CoverForReferences(Find<Grid>("MediaFolderHeading"));foreach(string name in new[]{"Unfiled","Favorites","Trash"})CoverForReferences(B(name));}referenceMode=true;referencePage.Visibility=Visibility.Visible;referencePage.Refresh();foreach(string name in new[]{"All","Pictures","Videos","Audio","Bookmarks","Documents"})B(name).SetResourceReference(Button.BackgroundProperty,"Panel");referenceNav.SetResourceReference(Button.BackgroundProperty,"AccentSoft");Status("本地收藏只记位置 · 双击打开 · 不复制、不移动、不删除原件");}
  void HideReferences(){if(!referenceMode)return;referenceMode=false;if(referencePage!=null)referencePage.Visibility=Visibility.Collapsed;foreach(var child in referenceCovered)child.Key.Visibility=child.Value;referenceCovered.Clear();referenceNav.SetResourceReference(Button.BackgroundProperty,"Panel");Refresh(false);}
  internal static bool IsLocalFileDrop(IDataObject data){return data.GetDataPresent(DataFormats.FileDrop)&&!DesktopImageDrop.Internal(data)&&!DesktopImageDrop.TemporaryPictures(data);}
  internal void CollectLocalPaths(string[] paths){if(busy){Status("请等待当前操作结束后再收藏本地文件。");return;}if(Entry.ClassicEnabled){CollectClassicPaths(paths,bookmarkMode?"":CurrentFolder());return;}if(Entry.BoardEnabled){OfferBoardPaths(paths,"");return;}bool local=referenceMode;ShowReferences();if(referenceMode){if(local)referencePage.DropFiles(paths);else referencePage.CollectPaths(paths,"",false);}}
  void DeskDrop(object sender,DragEventArgs e){if(!IsLocalFileDrop(e.Data))return;if(Entry.ClassicEnabled){e.Handled=true;var paths=(string[])e.Data.GetData(DataFormats.FileDrop);var node=FolderNode(e.OriginalSource as DependencyObject,Find<TreeView>("Folders"));string folder=node!=null?Convert.ToString(node.Tag):bookmarkMode?"":CurrentFolder();string collection=node==null?ExternalLocalCollectionTarget(e.OriginalSource as DependencyObject):null;HideDragHint();Window.Dispatcher.BeginInvoke(new Action(()=>CollectClassicPaths(paths,folder,collection)));return;}if(Entry.BoardEnabled){e.Handled=true;var paths=(string[])e.Data.GetData(DataFormats.FileDrop);string target=boardMode?(boardPage.GroupAt(e.OriginalSource as DependencyObject)??boardPage.Group):"";HideDragHint();Window.Dispatcher.BeginInvoke(new Action(()=>OfferBoardPaths(paths,target)));return;}if(referenceMode)return;e.Handled=true;CollectLocalPaths((string[])e.Data.GetData(DataFormats.FileDrop));}
  void ToolMenu(Button anchor){var menu=Theme.Menu(Window);Action<string,Action> add=(title,action)=>{var item=new MenuItem{Header=title};item.Click+=delegate{action();};menu.Items.Add(item);};add("截图 / 标注 / 原位翻译",()=>{if(!DeskRuntime.RequireTools())return;HideToTrayForCapture();QingJie.AppState.BeginCapture();});add("输入翻译",()=>{if(DeskRuntime.RequireTools())QingJie.AppState.ShowTranslator();});add("粘贴 / 找回贴图",()=>{if(DeskRuntime.RequireTools())QingJie.AppState.Paste();});menu.Items.Add(new Separator());add("打开图片…（不收藏）",()=>FileRouter.Choose(Window,false));add("打开 3D 模型…（不收藏）",()=>FileRouter.Choose(Window,true));add("本地文件快捷收藏",ShowReferences);menu.PlacementTarget=anchor;menu.IsOpen=true;}
  void HideToTrayForCapture(){if(!PreviewMode)HideToTray();}
  DeskSettingsWindow deskSettings;
  void ShowDeskSettings(){OpenDeskSettings("general");}
  void OpenDeskSettings(string section){
   if(deskSettings!=null){deskSettings.Select(section);deskSettings.Activate();return;}
   if(busy){Status("当前操作完成后再打开设置。");return;}
   var w=new DeskSettingsWindow(Window,PreviewMode,library);deskSettings=w;
   try{w.Select(section);w.ShowDialog();}finally{deskSettings=null;}
   if(w.StorageRequest!=null)ChangeStorage(w.StorageRequest);
  }
  void NotifyFromTools(string message){Status(message);if(tray!=null&&(!Window.IsVisible||QingJie.AppState.Captures.Count>0))tray.NotifyMessage(message);}
  async Task<bool> CollectScreenshot(BitmapSource bitmap){
   if(busy)throw new InvalidOperationException("素材库正在处理文件，请稍后再收藏；当前截图已保留。");
   busy=true;
   try{
    var result=await ScreenshotCollector.ImportAsync(library,bitmap);history.Clear();
    // The file has committed. Refresh/notification errors must not report a
    // failed save; the library will refresh next time it becomes visible.
    try{if(Window.IsVisible&&!referenceMode)Refresh(false);}catch{}
    try{NotifyFromTools(result.Duplicate?"已收藏到素材库 · 相同截图不重复保存":"截图已收藏到素材库 · 未分类");}catch{}
    return true;
   }finally{busy=false;}
  }
 }
}
