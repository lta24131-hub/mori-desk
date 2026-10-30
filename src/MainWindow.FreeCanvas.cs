using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MoriDesk;

namespace Mori {
 public partial class MainWindow {
  FreeCanvasWindow freeCanvas;
  MatureCanvasWindow matureCanvas;
  CanvasProjectsPage canvasProjectsPage;bool canvasProjectsMode;Button canvasNav;
  readonly Dictionary<UIElement,Visibility> canvasProjectsCovered=new Dictionary<UIElement,Visibility>();
  internal bool CanvasProjectsMode {get{return canvasProjectsMode;}}
  internal CanvasProjectsPage CanvasProjects {get{return canvasProjectsPage;}}
  internal bool FreeCanvasPresentationSuppressedForTests;
  internal Task FreeCanvasImportForTests=Task.FromResult(false);
  internal Func<int,CanvasDestination> CanvasDestinationChoiceForTests;
  bool canvasSelectionPending;
  // A mature project's project.json is a recovery index, not its editable
  // content. Even opening a different classic project would expose those
  // indices through the classic selector, so do not downgrade this profile.
  internal static bool ClassicCanvasFallbackAllowed(string dataRoot){
   var legacy=new FreeCanvasStore(dataRoot);var mature=new MatureCanvasStore(dataRoot);
   foreach(string folder in Directory.GetDirectories(legacy.Root)){
    string id=Path.GetFileName(folder);if(!FreeCanvasStore.IsId(id))continue;
    if(mature.Exists(id)||File.Exists(Path.Combine(folder,MatureCanvasStore.ManifestName+".bak"))||Directory.Exists(Path.Combine(folder,MatureCanvasStore.ManifestName)))return false;
   }
   return true;
  }
  bool CanvasEngineCanOpen(){
   if(MatureCanvasWindow.IsAvailable&&!FreeCanvasPresentationSuppressedForTests)return true;
   try{if(ClassicCanvasFallbackAllowed(Entry.DataRoot))return true;Status("新版画布组件不可用，请修复完整的 Nook 安装包；项目已保留，不会改用旧版显示空白或旧内容。");}
   catch(Exception error){Status("画布组件 / 项目暂时无法检查，未改用旧版："+error.Message);}
   return false;
  }
  void OpenFreeCanvas(string[] paths){OpenFreeCanvasToProject(null,paths);}
  internal bool OpenFreeCanvasToProject(string id,string[] paths){
   if(busy||!CanvasEngineCanOpen()||!PrepareLibraryChange())return false;
   // The classic editor remains available to its fixture tests and as a
   // compatibility fallback. A complete candidate package uses the pinned
   // mature editor; this never installs or rewrites the running release.
   if(MatureCanvasWindow.IsAvailable&&!FreeCanvasPresentationSuppressedForTests)return OpenMatureCanvas(id,paths);
   try{
    if(id!=null)new FreeCanvasStore(Entry.DataRoot).Load(id);
    if(paths!=null&&paths.Length>0&&freeCanvas!=null&&!freeCanvas.CanImportImages){Status("画布仍在保存上一批图片，请稍后再添加。");return false;}
    if(freeCanvas==null){
     var canvas=new FreeCanvasWindow(Entry.DataRoot);freeCanvas=canvas;canvas.Closed+=delegate{if(freeCanvas==canvas)freeCanvas=null;RefreshCanvasProjects();};
     // No Owner: the reference board remains visible when the library is in its tray.
    }
    if(id!=null&&!freeCanvas.OpenProject(id)){Status("当前画布还未能保存，请完成后再切换项目。");return false;}
    if(paths!=null&&paths.Length>0){
     if(freeCanvas.CanImportImages)FreeCanvasImportForTests=ImportCanvasSelection(freeCanvas,paths);
     else Status("画布仍在保存上一批图片，请稍后再添加。");
    }
    if(!FreeCanvasPresentationSuppressedForTests){if(freeCanvas.WindowState==WindowState.Minimized)freeCanvas.WindowState=WindowState.Normal;freeCanvas.Show();freeCanvas.Activate();}
    return true;
   }catch(Exception error){Status("无法打开自由画布："+error.Message);return false;}
  }
  bool OpenMatureCanvas(string id,string[] paths,bool isNewProject=false){
   try{
    if(matureCanvas==null){
     var canvas=new MatureCanvasWindow(Entry.DataRoot,id,paths,RefreshCanvasProjects,()=>PreviewMode?new Settings():Settings.Load(),()=>OpenDeskSettings("canvas"),isNewProject);matureCanvas=canvas;
     canvas.Closed+=delegate{if(matureCanvas==canvas)matureCanvas=null;RefreshCanvasProjects();};
    }else{
     if(!matureCanvas.CanImportImages){Status("画布正在启动或保存，请稍后添加 / 切换项目。");return false;}
     FreeCanvasImportForTests=UseMatureCanvas(id,paths);
    }
    if(matureCanvas.WindowState==WindowState.Minimized)matureCanvas.WindowState=WindowState.Normal;
    matureCanvas.Show();matureCanvas.Activate();return true;
   }catch(Exception error){Status("成熟画布未打开，原项目已保留："+error.Message);return false;}
  }
  async Task UseMatureCanvas(string id,string[] paths){
   try{if(id!=null){if(!await matureCanvas.OpenProjectAsync(id,paths))Status("当前画布未能保存，项目没有切换。");}else if(paths!=null&&paths.Length>0)await matureCanvas.ImportImagesAsync(paths);}
   catch(Exception error){Status("图片未添加，原素材完整保留："+error.Message);}
   finally{RefreshCanvasProjects();}
  }
  async Task<bool> ImportCanvasSelection(FreeCanvasWindow canvas,string[] paths){
   try{int before=canvas.ItemCount;await canvas.ImportImagesAsync(paths,null);if(canvas.CanImportImages&&canvas.ItemCount==before+paths.Length){canvas.FitAddedImages();return true;}return false;}
   catch(Exception error){Status("图片未能全部加入画布："+error.Message);return false;}
   finally{RefreshCanvasProjects();}
  }
  void CoverForCanvasProjects(UIElement element,Visibility hidden=Visibility.Collapsed){if(element==null)return;if(!canvasProjectsCovered.ContainsKey(element))canvasProjectsCovered[element]=element.Visibility;element.Visibility=hidden;}
  bool PrepareNewCanvasProject(){if(busy||!CanvasEngineCanOpen()||!PrepareLibraryChange())return false;if(matureCanvas!=null&&!matureCanvas.CanImportImages){Status("当前画布正在保存，请稍后再新建。");return false;}if(freeCanvas!=null&&!freeCanvas.PrepareExit()){Status("当前画布还未能保存，新建未开始；请先保存或另存当前画布。");return false;}return true;}
  void ApplyCanvasProjectsPresentation(){
   if(!canvasProjectsMode)return;
   foreach(UIElement child in Find<Grid>("Workspace").Children)if(child!=canvasProjectsPage&&Grid.GetColumn(child)>=2)CoverForCanvasProjects(child);
   foreach(string name in new[]{"Import","Paste","AddBookmark","Export","HomeAdd"})CoverForCanvasProjects(B(name));
   CoverForCanvasProjects((UIElement)T("Search").Parent);CoverForCanvasProjects((UIElement)Find<Slider>("ThumbnailSize").Parent);CoverForCanvasProjects(Find<TreeView>("Folders"));CoverForCanvasProjects(Find<Grid>("MediaFolderHeading"));CoverForCanvasProjects(Find<StackPanel>("SidebarGuide"));
   foreach(string name in new[]{"All","Pictures","Videos","Audio","Bookmarks","Documents","Unfiled","Favorites","Trash"})B(name).SetResourceReference(Button.BackgroundProperty,"Panel");
   // These act on the last material/website scope, never on canvas projects.
   // Hide them here, then restore their exact visibility when leaving Projects.
   foreach(string name in new[]{"Unfiled","Favorites"})CoverForCanvasProjects(B(name));
   // Keep the footer's natural size so the toolbox/settings above it never
   // move when a page has no recycle bin. Hidden also removes hit testing.
   CoverForCanvasProjects(Find<Border>("SidebarFooter"),Visibility.Hidden);CoverForCanvasProjects(Find<Border>("SidebarScopeDivider"));
   if(referenceNav!=null)referenceNav.SetResourceReference(Button.BackgroundProperty,"Panel");if(canvasNav!=null)canvasNav.SetResourceReference(Button.BackgroundProperty,"AccentSoft");
  }
  internal bool ShowCanvasProjects(bool dragging=false){
   if(busy){Status("请等待当前操作完成后再打开画布项目。");return false;}if(!canvasProjectsMode){if(dragging&&HasUnsavedMetadata()){Status("右侧信息尚未保存，保存或放弃编辑后再将图片拖到画布。");return false;}if(!dragging&&!PrepareLibraryChange())return false;}
   try{
    if(canvasProjectsPage==null){canvasProjectsPage=new CanvasProjectsPage(new FreeCanvasStore(Entry.DataRoot),OpenFreeCanvasToProject,CanvasDropPaths,CanvasCommitPaths,()=>!busy&&(freeCanvas==null||freeCanvas.CanImportImages)&&(matureCanvas==null||matureCanvas.CanImportImages),PrepareNewCanvasProject,()=>Settings.Load().CanvasDefaultTopmost,Status,new MatureCanvasStore(Entry.DataRoot));canvasProjectsPage.Visibility=Visibility.Collapsed;Grid.SetColumn(canvasProjectsPage,2);Grid.SetColumnSpan(canvasProjectsPage,3);Panel.SetZIndex(canvasProjectsPage,16);Find<Grid>("Workspace").Children.Add(canvasProjectsPage);}
    HideBrowser();HideBoard();HideReferences();
    if(!canvasProjectsMode)canvasProjectsCovered.Clear();canvasProjectsMode=true;pressed=null;dragged=false;dragSelection=null;collapseSelectionOnUp=false;searchTimer.Stop();layoutTimer.Stop();HideDragHint();ApplyCanvasProjectsPresentation();canvasProjectsPage.Visibility=Visibility.Visible;canvasProjectsPage.Refresh();
    if(!dragging)canvasProjectsPage.Focus();Status("画布按项目保存 · 单击打开 · 可将图片拖到指定项目");return true;
   }catch(Exception error){Status("无法读取画布项目："+error.Message);return false;}
  }
  internal void HideCanvasProjects(){
   if(!canvasProjectsMode)return;canvasProjectsMode=false;canvasProjectsPage.Visibility=Visibility.Collapsed;canvasProjectsPage.ClearDropTarget();foreach(var item in canvasProjectsCovered)item.Key.Visibility=item.Value;canvasProjectsCovered.Clear();if(canvasNav!=null)canvasNav.SetResourceReference(Button.BackgroundProperty,"Panel");sidebars.Apply();Refresh(false);
  }
  void RefreshCanvasProjects(){if(canvasProjectsPage!=null)canvasProjectsPage.RequestRefresh();}
  // Hover uses the already-loaded gallery snapshot; only mouse release queries
  // current records. Do not rebuild the entire library for every drag position.
  internal string[] CanvasDropPaths(IDataObject data){return ResolveCanvasDropPaths(data,false);}
  internal string[] CanvasCommitPaths(IDataObject data){return ResolveCanvasDropPaths(data,true);}
  string[] ResolveCanvasDropPaths(IDataObject data,bool commit){
   if(data==null)return new string[0];
   try{
    if(data.GetDataPresent("Mori.AssetIds",false)){
     var ids=data.GetData("Mori.AssetIds",false)as string[];if(ids==null)return new string[0];var selectedIds=new HashSet<string>(ids,StringComparer.Ordinal);
     return CanvasProjectsPage.ImageFiles((commit?library.All():all).Where(a=>selectedIds.Contains(a.Id)&&!a.Deleted&&!a.IsDirectory&&SectionContains(a,"pictures")).Select(library.FullPath));
    }
    if(DesktopImageDrop.Internal(data)||!data.GetDataPresent(DataFormats.FileDrop,false))return new string[0];return CanvasProjectsPage.ImageFiles(data.GetData(DataFormats.FileDrop,false)as string[]);
   }catch{ return new string[0]; }
  }
  bool CanvasNavigationTarget(DependencyObject source){for(var current=source;current!=null;current=SelectionParent(current))if(current==canvasNav)return true;return false;}
  void CanvasProjectsDragOver(object sender,DragEventArgs e){
   if(!canvasProjectsMode){if(!CanvasNavigationTarget(e.OriginalSource as DependencyObject))return;if(CanvasDropPaths(e.Data).Length==0||!ShowCanvasProjects(true)){e.Effects=DragDropEffects.None;e.Handled=true;return;}}
   canvasProjectsPage.HandleDragOver(e);if(canvasProjectsPage.ProjectAt(e.OriginalSource as DependencyObject)==null)Status("继续拖到要添加图片的画布项目，再松开。");
  }
  void CanvasProjectsDrop(object sender,DragEventArgs e){
   if(!canvasProjectsMode){if(!CanvasNavigationTarget(e.OriginalSource as DependencyObject))return;ShowCanvasProjects(true);e.Effects=DragDropEffects.None;e.Handled=true;Status("将图片拖到具体的画布项目再松开。");return;}
   desktopImageHover=false;HideDragHint();canvasProjectsPage.HandleDrop(e);
  }
  void AddSelectionToFreeCanvas(){
   var items=Selected();if(canvasSelectionPending||canvasProjectsMode||busy||items.Count==0||view=="trash"||!items.All(a=>SectionContains(a,"pictures")))return;
   if(items.Count>200){Status("每次请加入不超过 200 张图片。");return;}
   canvasSelectionPending=true;
   try{
    var ids=items.Select(a=>a.Id).ToArray();CanvasDestination choice;
    if(CanvasDestinationChoiceForTests!=null)choice=CanvasDestinationChoiceForTests(items.Count);
    else{var store=new FreeCanvasStore(Entry.DataRoot);var dialog=new CanvasDestinationWindow(Window,store,new MatureCanvasStore(Entry.DataRoot),items.Count);choice=dialog.ShowDialog()==true?dialog.Result:null;}
    if(choice==null){canvasSelectionPending=false;return;}
    FreeCanvasImportForTests=CompleteCanvasSelection(ids,choice);
   }catch(Exception error){canvasSelectionPending=false;Status("未能选择画布："+error.Message);}
  }
  async Task CompleteCanvasSelection(string[] ids,CanvasDestination choice){try{await AddSelectionToCanvasAsync(ids,choice);}finally{canvasSelectionPending=false;}}
  internal async Task AddSelectionToCanvasAsync(IEnumerable<string> ids,CanvasDestination choice){
   if(choice==null||canvasProjectsMode||busy||view=="trash")return;
   try{
    var selectedIds=new HashSet<string>(ids??new string[0],StringComparer.Ordinal);if(selectedIds.Count==0||selectedIds.Count>200)return;
    var items=library.All().Where(a=>selectedIds.Contains(a.Id)&&!a.Deleted&&!a.IsDirectory&&SectionContains(a,"pictures")).ToList();
    if(items.Count!=selectedIds.Count)throw new IOException("所选素材已变化，请刷新后重新选择；没有添加到其他画布。");
    var resolved=items.Select(ResolveReferenceForOpen).ToList();if(resolved.Any(a=>a==null))return;
    var paths=resolved.Select(library.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();if(CanvasProjectsPage.ImageFiles(paths).Length!=paths.Length||paths.Length==0)throw new IOException("有图片已移动或无法读取，请先修复文件位置。");
    var store=new FreeCanvasStore(Entry.DataRoot);string target=choice.ProjectId;
    if(!choice.IsNew){if(string.IsNullOrEmpty(target))throw new IOException("请选择要加入的画布。");var row=CanvasProjectsPage.QueryProjects(store,new MatureCanvasStore(Entry.DataRoot)).FirstOrDefault(p=>p.Id==target);if(row==null||!string.IsNullOrEmpty(row.Error))throw new IOException("所选画布已不存在或无法读取；没有改为加入其他画布。");}
    else if(string.IsNullOrWhiteSpace(choice.NewName)||choice.NewName.Trim().Length>120)throw new IOException("画布名称需要 1–120 个字。");
    if(!PrepareLibraryChange())return;
    if(choice.IsNew){
     if(!PrepareNewCanvasProject())return;
     if(matureCanvas!=null&&!await matureCanvas.PrepareProjectChangeAsync())return;
     var created=store.Create(choice.NewName.Trim());if((PreviewMode?new Settings():Settings.Load()).CanvasDefaultTopmost){created.Pinned=true;store.Save(created);}target=created.Id;
    }
    Status("正在加入画布…");
    bool mature=MatureCanvasWindow.IsAvailable&&!FreeCanvasPresentationSuppressedForTests;
    if(mature){
     if(matureCanvas==null&&!OpenMatureCanvas(target,null,choice.IsNew))return;
     var active=matureCanvas;await active.WaitUntilReadyAsync();if(!Object.ReferenceEquals(active,matureCanvas))throw new IOException("画布窗口已变化，本次添加已停止；请重新选择。");if(!await active.OpenProjectAsync(target,paths,choice.IsNew))throw new IOException("画布未完成保存或添加，请在画布窗口检查；原素材保留。");if(!Object.ReferenceEquals(active,matureCanvas)||active.ProjectId!=target)throw new IOException("目标画布已变化，本次添加没有转入其他项目。");
     if(active.WindowState==WindowState.Minimized)active.WindowState=WindowState.Normal;active.Show();active.Activate();
    }else{if(!OpenFreeCanvasToProject(target,null))return;var active=freeCanvas;if(active==null||!active.CanImportImages)throw new IOException("请等待画布完成当前操作。");if(!await ImportCanvasSelection(active,paths))throw new IOException("图片未全部保存，请在当前画布检查后重试。");}
    RefreshCanvasProjects();Status("已加入画布 · 原素材保留在素材库");
   }catch(Exception error){Status("加入画布未完成："+error.Message);}
  }
  void CloseFreeCanvasForExit(object sender,System.ComponentModel.CancelEventArgs e){
   if(!e.Cancel&&matureCanvas!=null){if(!matureCanvas.PreparedForExit){e.Cancel=true;return;}matureCanvas.Close();}
   if(e.Cancel||freeCanvas==null)return;
   freeCanvas.Close();if(freeCanvas!=null&&freeCanvas.IsVisible)e.Cancel=true;
  }
 }
}
