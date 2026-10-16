using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Mori {
 public partial class MainWindow {
  // Type shortcuts show the whole type. Project folders and utility views show
  // all local media by default; websites remain a separate namespace.
  internal bool RequestNavigate(string target){
   if(busy||!PrepareLibraryChange()){ReloadFolders();return false;}
   if(MoriDesk.Entry.BoardEnabled&&!bookmarkMode&&target=="unfiled"){ShowBoard("unfiled");return true;}
   Navigate(target);return true;
  }
  internal void UpdateNavigationScope(){
   if(canvasProjectsMode){ApplyCanvasProjectsPresentation();return;}if(canvasNav!=null)canvasNav.SetResourceReference(Button.BackgroundProperty,"Panel");
   string scope=bookmarkMode?"网址":"素材";
   B("Unfiled").Content="未分类"+scope;B("Favorites").Content="星标"+scope;B("Trash").Content=scope+"回收站";
   foreach(string name in new[]{"Unfiled","Favorites","Trash"})B(name).ToolTip=bookmarkMode?"仅查看网址收藏":"查看本机图片、动图、视频、音频和文档，可用上方类型筛选";
   foreach(string nav in new[]{"All","Pictures","Videos","Audio","Bookmarks","Documents"}){
    string section=nav=="All"?"assets":nav.ToLowerInvariant();
    bool active=nav=="Bookmarks"?bookmarkMode:!bookmarkMode&&view=="all"&&mediaSection==section;
    B(nav).SetResourceReference(Button.BackgroundProperty,active?"AccentSoft":"Panel");
   }
   if(referenceNav!=null)referenceNav.SetResourceReference(Button.BackgroundProperty,!bookmarkMode&&view=="all"&&mediaSection=="shortcuts"?"AccentSoft":"Panel");
   Find<ComboBox>("MediaFilter").Visibility=!bookmarkMode&&mediaSection=="media"?Visibility.Visible:Visibility.Collapsed;
   B("Import").Content=bookmarkMode?"导入书签":"导入素材";B("Import").ToolTip=bookmarkMode?"从 Chrome / Edge 或书签文件导入 · Ctrl+O":"导入图片、视频、音频和文档 · Ctrl+O";
   T("Search").ToolTip="搜索当前页面的名称、标签、备注和网址 · Ctrl+F";
   B("ImportBookmarks").Visibility=Visibility.Collapsed;
   B("NewBookmarkFolder").Visibility=bookmarkMode&&view!="trash"?Visibility.Visible:Visibility.Collapsed;
   if(view=="trash"){
    B("EmptyImport").Visibility=Visibility.Collapsed;
    if(visible.Count==0){L("EmptyTitle").Text="没有匹配的回收站内容";L("EmptyHint").Text="这里只显示"+scope+"。删除后可在这里恢复；永久删除会再次确认。";}
   }else B("EmptyImport").Visibility=Visibility.Visible;
   if(!bookmarkMode&&mediaSection=="documents"&&visible.Count==0)L("EmptyHint").Text="可拖入 PDF、Office 等文档作为快捷收藏，原文件保留在原处。";
   if(!bookmarkMode&&mediaSection=="audio"&&visible.Count==0)L("EmptyHint").Text="可导入 MP3、WAV、M4A、FLAC 等音频。\n双击直接预览、播放和拖动进度。";
   UpdateNavigationPresentation();
  }
  static MenuItem ActionItem(string title,Action action,string shortcut="",bool enabled=true){
   var item=new MenuItem{Header=title,InputGestureText=shortcut,IsEnabled=enabled};item.Click+=delegate{action();};return item;
  }
  internal ContextMenu CreateMoreMenu(){
   if(canvasProjectsMode){var projects=Theme.Menu(Window);projects.Items.Add(ActionItem("新建画布",canvasProjectsPage.CreateProject,"Ctrl+N",!busy&&(freeCanvas==null||freeCanvas.CanImportImages)));projects.Items.Add(ActionItem("刷新项目",canvasProjectsPage.Refresh,"F5",!busy));projects.Items.Add(ActionItem("设置",ShowDeskSettings));projects.Items.Add(new Separator());projects.Items.Add(ActionItem("退出 Nook",ExitApplication,"",!busy));return projects;}
   if(browserMode){var browsing=Theme.Menu(Window);browsing.Items.Add(ActionItem("返回收藏",HideBrowser));browsing.Items.Add(ActionItem("设置",ShowDeskSettings));browsing.Items.Add(new Separator());browsing.Items.Add(ActionItem("退出 Nook",ExitApplication,"",!busy));return browsing;}
   if(boardMode){var desktop=Theme.Menu(Window);desktop.Items.Add(ActionItem("设置",ShowDeskSettings));desktop.Items.Add(ActionItem("图库高级管理",()=>{HideBoard();Navigate("all");}));desktop.Items.Add(ActionItem("浏览器收藏插件…",ShowBrowserExtension));desktop.Items.Add(new Separator());desktop.Items.Add(ActionItem("退出 Nook",ExitApplication,"",!busy));return desktop;}
   if(referenceMode){var local=Theme.Menu(Window);local.Items.Add(ActionItem("返回素材库",HideReferences));local.Items.Add(ActionItem("设置",ShowDeskSettings));local.Items.Add(new Separator());local.Items.Add(ActionItem("退出 Nook",ExitApplication,"",!busy));return local;}
   var menu=Theme.Menu(Window);AddUndoMenu(menu);menu.Items.Add(ActionItem("收藏网址…",()=>AddBookmark(""),"",!busy));
   var import=new MenuItem{Header="导入",IsEnabled=!busy};
   import.Items.Add(ActionItem("素材文件…",ChooseMediaFiles));
   import.Items.Add(ActionItem("整个文件夹…",delegate{var dir=FolderPicker("导入文件夹 · 原文件保持不动");if(dir!=null)ImportPaths(new[]{dir},null);}));
   import.Items.Add(ActionItem("浏览器书签…",ShowBookmarkImport));
   import.Items.Add(ActionItem("Eagle 图库…",Migrate));menu.Items.Add(import);
   var manage=new MenuItem{Header="图库管理",IsEnabled=!busy};
   manage.Items.Add(ActionItem("打开图库文件夹",delegate{Process.Start("explorer.exe",library.Root);}));
   manage.Items.Add(ActionItem("查看存储说明",delegate{MessageBox.Show(Window,library.Root+"\n\n原图：普通文件，按月份存放。\n分类、标签和备注：.mori\\library.db\n\n更改位置请用“设置 → 默认保存位置”整体迁移。","图库存储");}));
   manage.Items.Add(new Separator());manage.Items.Add(ActionItem("备份整个图库…",Backup));manage.Items.Add(ActionItem("从备份恢复到新文件夹…",Restore));
   manage.Items.Add(new Separator());manage.Items.Add(ActionItem("选择 / 创建另一个图库…",SwitchLibrary));menu.Items.Add(manage);
   var display=new MenuItem{Header="显示"};var viewMenu=CreateGalleryMenu();
   while(viewMenu.Items.Count>0){var entry=viewMenu.Items[0];viewMenu.Items.RemoveAt(0);display.Items.Add(entry);}menu.Items.Add(display);
   menu.Items.Add(new Separator());menu.Items.Add(ActionItem("浏览器收藏插件…",ShowBrowserExtension));
   menu.Items.Add(ActionItem("操作说明",delegate{MessageBox.Show(Window,"全部素材汇总图片（含动图 / SVG / PSD / PDF 兼容 AI）、视频和音频；也可分别浏览。\n网址、文档单独显示，不混入全部素材。\n现有素材分类、星标和回收站仍包含所有本地类型，可筛选；网址文件夹独立管理。\n音视频双击播放，可调节音量和拖动进度；视频 F11 全屏，Esc 先退出全屏。\nPDF 可翻页，Office 文件仅预览文字内容，不保留原版式。\n\nCtrl+F：搜索当前页面    Ctrl+O：导入\nCtrl+C：复制素材 / 网址    Ctrl+V：粘贴收藏\nSpace：预览 / 打开    Esc：取消选择\nCtrl+Z：撤销可恢复操作    F5：刷新\n\n删除分类保留内容；移到回收站可以恢复；永久删除会确认。\n搜索支持名称、标签、备注；相似图片不是 AI 语义搜索。","Nook · 操作说明");}));
   menu.Items.Add(new Separator());menu.Items.Add(ActionItem("退出 Nook",ExitApplication,"",!busy));return menu;
  }
  void ShowBrowserExtension(){
   OpenDeskSettings("browser");
  }
  void More(){var menu=CreateMoreMenu();menu.PlacementTarget=B("More");menu.Placement=PlacementMode.Custom;menu.CustomPopupPlacementCallback=(popup,target,offset)=>new[]{new CustomPopupPlacement(new Point(target.Width-popup.Width,target.Height),PopupPrimaryAxis.Horizontal)};menu.IsOpen=true;}
  internal ContextMenu CreateAssetMenu(Asset a){
   var menu=Theme.Menu(Window);var selected=Selected();bool multi=selected.Count>1,web=a.IsBookmark;
   if(!multi){
    menu.Items.Add(ActionItem(web?"快速浏览":MediaTypes.Documents.Contains(a.Ext)?"预览文档":a.IsAudio?"播放音频":"预览",()=>Preview(a),"Space",!busy));
    if(web)menu.Items.Add(ActionItem("用外部浏览器打开",()=>OpenSource(a.Source),"",!busy));
    if(!web){menu.Items.Add(ActionItem("在资源管理器中显示",()=>RevealAsset(a),"",!busy));
     menu.Items.Add(ActionItem("用默认程序打开",()=>OpenFile(a),"",!busy));
     AddWallpaperMenu(menu,a);
     if(a.IsReference)menu.Items.Add(ActionItem("重新定位原文件…",()=>RelinkReference(a),"",!busy));
     Uri uri;if(SourceLink.TryParse(a.Source,out uri))menu.Items.Add(ActionItem("打开来源网页",()=>OpenSource(a.Source),"",!busy));}
   }
   menu.Items.Add(ActionItem(web?(multi?"复制选中网址":"复制网址"):"复制选中文件",CopySelection,"Ctrl+C",!busy));
   menu.Items.Add(ActionItem(web?"导出网址快捷方式…":"导出选中素材…",Export,"",!busy&&!selected.Any(x=>x.IsDirectory)));
   if(view!="trash"&&selected.Count>0&&selected.All(x=>SectionContains(x,"pictures")))menu.Items.Add(ActionItem("加入自由画布…",AddSelectionToFreeCanvas,"",!busy));
   menu.Items.Add(new Separator());
   if(view=="trash")menu.Items.Add(ActionItem("恢复素材",RestoreTrash,"",!busy));
   else{
    if(LocalCollectionsActive&&selected.Count>0&&selected.All(x=>x.IsReference)){
     menu.Items.Add(ActionItem("移动到分类文件夹…",ChooseLocalCollection,"",!busy));
     menu.Items.Add(ActionItem("新建分类并移入…",()=>NewLocalCollection(true),"",!busy));
     if(selected.Any(x=>!string.IsNullOrEmpty(x.LocalCollection)))menu.Items.Add(ActionItem("移回本地收藏外层",()=>MoveLocalCollection(selection.ToArray(),""),"",!busy));
     menu.Items.Add(new Separator());
    }else{
     if(!web)menu.Items.Add(ActionItem("移动到分类…",()=>ChooseCategory(true),"",!busy));
     menu.Items.Add(ActionItem(web?"移动到文件夹…":"添加到分类…",Categorize,"",!busy));
     if(CurrentFolder()!="")menu.Items.Add(ActionItem(web?"移到未分类":"从当前分类移除",delegate{if(!PrepareLibraryChange())return;Edit("从分类移除",()=>library.RemoveFromFolder(selection,view));Refresh(true);},"",!busy));
    }
    menu.Items.Add(ActionItem(selected.Count>0&&selected.All(x=>x.Favorite)?"取消星标":"添加星标",Star,"",!busy));
    AddOverviewVisibilityMenu(menu,selected);
   }
   menu.Items.Add(new Separator());menu.Items.Add(ActionItem(view=="trash"?"永久删除…":selected.Count>0&&selected.All(x=>x.IsReference)?"移除快捷收藏":selected.Any(x=>x.IsReference)?"移除所选收藏":"移到回收站",view=="trash"?(Action)DeleteForever:Trash,"Del",!busy));return menu;
  }
  internal static string BookmarkCopyText(IEnumerable<Asset> items){return string.Join(Environment.NewLine,items.Where(a=>a.IsBookmark).Select(a=>a.Source));}
  void CopySelection(){
   var items=Selected();if(canvasProjectsMode||items.Count==0||busy)return;
   try{if(items.All(a=>a.IsBookmark))Clipboard.SetText(BookmarkCopyText(items));else{var files=new System.Collections.Specialized.StringCollection();files.AddRange(items.Select(library.FullPath).ToArray());Clipboard.SetFileDropList(files);}Status("已复制 "+items.Count+(items.All(a=>a.IsBookmark)?" 个网址":" 个文件"));}catch(Exception error){Status("复制失败："+error.Message);}
  }
  internal string RevealImported(IEnumerable<string> ids,string folder){
   HideCanvasProjects();HideBrowser();
   var imported=new HashSet<string>(ids);var items=library.All().Where(a=>imported.Contains(a.Id)&&!a.Deleted).ToList();if(items.Count==0)return "";
   bool web=items.All(a=>a.IsBookmark);bookmarkMode=web;
   var target=library.Folders(web).FirstOrDefault(f=>f.Id==folder);
   view=target==null?(web?"home":"all"):target.Id;
   var local=items.Where(a=>!a.IsBookmark).ToList();
   mediaSection=web?"bookmarks":target!=null?"media":local.All(a=>SectionContains(a,"pictures"))?"pictures":local.All(IsVideo)?"videos":local.All(a=>a.IsAudio)?"audio":local.All(a=>SectionContains(a,"documents"))?"documents":"assets";
   similarId=colorId="";T("Search").Clear();Find<ComboBox>("MediaFilter").SelectedIndex=0;
   selection.Clear();foreach(var a in items.Where(a=>a.IsBookmark==web))selection.Add(a.Id);ReloadFolders();
   return (items.Any(a=>a.IsBookmark)&&!web?" · 网址已单独保存到“网址”页面":"")+(mediaSection=="assets"&&local.Any(a=>SectionContains(a,"documents"))?" · 文档已单独保存到“文档”页面":"");
  }
  void ChooseMediaFiles(){if(canvasProjectsMode||busy||!PrepareLibraryChange())return;var dialog=new Microsoft.Win32.OpenFileDialog{Multiselect=true,Title="导入素材 · 原文件保持不动",Filter="素材文件|"+string.Join(";",Library.Extensions.Select(x=>"*"+x))+"|所有文件|*.*"};if(dialog.ShowDialog(Window)==true)ImportPaths(dialog.FileNames,null);}
 }
}
