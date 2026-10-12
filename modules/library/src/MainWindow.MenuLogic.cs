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
   Navigate(target);return true;
  }
  internal void UpdateNavigationScope(){
   string scope=bookmarkMode?"网址":"素材";
   B("Unfiled").Content="未分类"+scope;B("Favorites").Content="星标"+scope;B("Trash").Content=scope+"回收站";
   foreach(string name in new[]{"Unfiled","Favorites","Trash"})B(name).ToolTip=bookmarkMode?"仅查看网址收藏":"查看本机图片、动图、视频、音频和 PDF，可用上方类型筛选";
   foreach(string nav in new[]{"All","Pictures","Videos","Audio","Bookmarks","Documents"}){
    string section=nav=="All"?"assets":nav.ToLowerInvariant();
    bool active=nav=="Bookmarks"?bookmarkMode:!bookmarkMode&&view=="all"&&mediaSection==section;
    B(nav).SetResourceReference(Button.BackgroundProperty,active?"AccentSoft":"Panel");
   }
   Find<ComboBox>("MediaFilter").Visibility=!bookmarkMode&&mediaSection=="media"?Visibility.Visible:Visibility.Collapsed;
   B("Import").Content=bookmarkMode?"导入书签":"导入素材";B("Import").ToolTip=bookmarkMode?"从 Chrome / Edge 或书签文件导入 · Ctrl+O":"导入图片、视频、音频和 PDF · Ctrl+O";
   T("Search").ToolTip="搜索当前页面的名称、标签、备注和网址 · Ctrl+F";
   B("ImportBookmarks").Visibility=Visibility.Collapsed;
   B("NewBookmarkFolder").Visibility=bookmarkMode&&view!="trash"?Visibility.Visible:Visibility.Collapsed;
   if(view=="trash"){
    B("EmptyImport").Visibility=Visibility.Collapsed;
    if(visible.Count==0){L("EmptyTitle").Text="没有匹配的回收站内容";L("EmptyHint").Text="这里只显示"+scope+"。删除后可在这里恢复；永久删除会再次确认。";}
   }else B("EmptyImport").Visibility=Visibility.Visible;
   if(!bookmarkMode&&mediaSection=="documents"&&visible.Count==0)L("EmptyHint").Text="当前支持 PDF 文档。可导入文件，或清除搜索条件。";
   if(!bookmarkMode&&mediaSection=="audio"&&visible.Count==0)L("EmptyHint").Text="可导入 MP3、WAV、M4A、FLAC 等音频。\n双击使用电脑默认播放器打开。";
  }
  static MenuItem ActionItem(string title,Action action,string shortcut="",bool enabled=true){
   var item=new MenuItem{Header=title,InputGestureText=shortcut,IsEnabled=enabled};item.Click+=delegate{action();};return item;
  }
  internal ContextMenu CreateMoreMenu(){
   var menu=Theme.Menu(Window);AddUndoMenu(menu);
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
   menu.Items.Add(ActionItem("操作说明",delegate{MessageBox.Show(Window,"全部素材汇总图片（含动图 / SVG）、视频和音频；也可分别浏览。\n网址、文档（PDF）单独显示，不混入全部素材。\n现有素材分类、星标和回收站仍包含所有本地类型，可筛选；网址文件夹独立管理。\n音频双击使用电脑默认播放器打开。\n\nCtrl+F：搜索当前页面    Ctrl+O：导入\nCtrl+C：复制素材 / 网址    Ctrl+V：粘贴收藏\nSpace：预览 / 打开    Esc：取消选择\nCtrl+Z：撤销可恢复操作    F5：刷新\n\n删除分类保留内容；移到回收站可以恢复；永久删除会确认。\n搜索支持名称、标签、备注；相似图片不是 AI 语义搜索。","Mori · 操作说明");}));
   menu.Items.Add(new Separator());menu.Items.Add(ActionItem("退出 Mori Desk",ExitApplication,"",!busy));return menu;
  }
  void ShowBrowserExtension(){
   MoriDesk.BrowserConnection.Show(Window,PreviewMode);
  }
  void More(){var menu=CreateMoreMenu();menu.PlacementTarget=B("More");menu.Placement=PlacementMode.Bottom;menu.IsOpen=true;}
  internal ContextMenu CreateAssetMenu(Asset a){
   var menu=Theme.Menu(Window);var selected=Selected();bool multi=selected.Count>1,web=a.IsBookmark;
   if(!multi){
    menu.Items.Add(ActionItem(web?"打开网址":a.Ext==".pdf"?"打开文档":a.IsAudio?"播放音频（默认播放器）":"预览",()=>Preview(a),"Space",!busy));
    if(!web){menu.Items.Add(ActionItem("在资源管理器中显示",()=>Process.Start("explorer.exe","/select,\""+library.FullPath(a)+"\""),"",!busy));
     if(a.Ext!=".pdf"&&!a.IsAudio)menu.Items.Add(ActionItem("用默认程序打开",()=>OpenFile(a),"",!busy));
     Uri uri;if(SourceLink.TryParse(a.Source,out uri))menu.Items.Add(ActionItem("打开来源网页",()=>OpenSource(a.Source),"",!busy));}
   }
   menu.Items.Add(ActionItem(web?(multi?"复制选中网址":"复制网址"):"复制选中文件",CopySelection,"Ctrl+C",!busy));
   menu.Items.Add(ActionItem(web?"导出网址快捷方式…":"导出选中素材…",Export,"",!busy));
   menu.Items.Add(new Separator());
   if(view=="trash")menu.Items.Add(ActionItem("恢复素材",RestoreTrash,"",!busy));
   else{
    if(!web)menu.Items.Add(ActionItem("移动到分类…",()=>ChooseCategory(true),"",!busy));
    menu.Items.Add(ActionItem(web?"移动到文件夹…":"添加到分类…",Categorize,"",!busy));
    if(CurrentFolder()!="")menu.Items.Add(ActionItem(web?"移到未分类":"从当前分类移除",delegate{if(!PrepareLibraryChange())return;Edit("从分类移除",()=>library.RemoveFromFolder(selection,view));Refresh(true);},"",!busy));
    menu.Items.Add(ActionItem(selected.Count>0&&selected.All(x=>x.Favorite)?"取消星标":"添加星标",Star,"",!busy));
   }
   menu.Items.Add(new Separator());menu.Items.Add(ActionItem(view=="trash"?"永久删除…":"移到回收站",view=="trash"?(Action)DeleteForever:Trash,"Del",!busy));return menu;
  }
  internal static string BookmarkCopyText(IEnumerable<Asset> items){return string.Join(Environment.NewLine,items.Where(a=>a.IsBookmark).Select(a=>a.Source));}
  void CopySelection(){
   var items=Selected();if(items.Count==0||busy)return;
   try{if(items.All(a=>a.IsBookmark))Clipboard.SetText(BookmarkCopyText(items));else{var files=new System.Collections.Specialized.StringCollection();files.AddRange(items.Select(library.FullPath).ToArray());Clipboard.SetFileDropList(files);}Status("已复制 "+items.Count+(items.All(a=>a.IsBookmark)?" 个网址":" 个文件"));}catch(Exception error){Status("复制失败："+error.Message);}
  }
  internal string RevealImported(IEnumerable<string> ids,string folder){
   var imported=new HashSet<string>(ids);var items=library.All().Where(a=>imported.Contains(a.Id)&&!a.Deleted).ToList();if(items.Count==0)return "";
   bool web=items.All(a=>a.IsBookmark);bookmarkMode=web;
   var target=library.Folders(web).FirstOrDefault(f=>f.Id==folder);
   view=target==null?(web?"home":"all"):target.Id;
   var local=items.Where(a=>!a.IsBookmark).ToList();
   mediaSection=web?"bookmarks":target!=null?"media":local.All(a=>SectionContains(a,"pictures"))?"pictures":local.All(IsVideo)?"videos":local.All(a=>a.IsAudio)?"audio":local.All(a=>SectionContains(a,"documents"))?"documents":"assets";
   similarId=colorId="";T("Search").Clear();Find<ComboBox>("MediaFilter").SelectedIndex=0;
   selection.Clear();foreach(var a in items.Where(a=>a.IsBookmark==web))selection.Add(a.Id);ReloadFolders();
   return (items.Any(a=>a.IsBookmark)&&!web?" · 网址已单独保存到“网址”页面":"")+(mediaSection=="assets"&&local.Any(a=>SectionContains(a,"documents"))?" · PDF 已单独保存到“文档”页面":"");
  }
  void ChooseMediaFiles(){if(busy||!PrepareLibraryChange())return;var dialog=new Microsoft.Win32.OpenFileDialog{Multiselect=true,Title="导入素材 · 原文件保持不动",Filter="素材文件|"+string.Join(";",Library.Extensions.Select(x=>"*"+x))+"|所有文件|*.*"};if(dialog.ShowDialog(Window)==true)ImportPaths(dialog.FileNames,null);}
 }
}
