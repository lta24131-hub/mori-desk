using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Mori {
 public partial class MainWindow {
  // Presentation only: these groups never migrate or alter library membership.
  void UpdateNavigationPresentation(){
   bool local=LocalCollectionsActive;
   B("AddBookmark").Visibility=bookmarkMode?Visibility.Visible:Visibility.Collapsed;
   B("Import").Content=local?"收藏本地文件":bookmarkMode?"导入书签":"导入素材";
   B("Import").ToolTip=local?"仅保存文件位置，不复制文件 · Ctrl+O":bookmarkMode?"从 Chrome / Edge 或书签文件导入 · Ctrl+O":"在图库保存独立副本 · Ctrl+O；直接拖入电脑文件则只保存位置";
   if(local){B("EmptyImport").Content="收藏本地文件";B("EmptyImport").ToolTip="仅保存文件位置，不复制文件";if(visible.Count==0&&T("Search").Text.Trim()==""){L("EmptyTitle").Text="这里还没有本地收藏";L("EmptyHint").Text="拖入电脑文件或文件夹，或点击“收藏本地文件”。\n只保存原文件的位置，不复制、不移动原件。";}}
   else B("EmptyImport").ToolTip=bookmarkMode?"保存网页链接":"在图库保存独立副本";
   B("Export").Visibility=Selected().Count>0&&!Selected().Any(a=>a.IsDirectory)?Visibility.Visible:Visibility.Collapsed;
   Find<Grid>("MediaFolderHeading").Visibility=bookmarkMode||local?Visibility.Collapsed:Visibility.Visible;
   Find<TreeView>("Folders").Visibility=bookmarkMode||local?Visibility.Collapsed:Visibility.Visible;
   B("Unfiled").Visibility=B("Favorites").Visibility=B("Trash").Visibility=local?Visibility.Collapsed:Visibility.Visible;
   bool noFolders=Find<TreeView>("Folders").Items.Count==0;
   Find<StackPanel>("SidebarGuide").Visibility=bookmarkMode||local||noFolders?Visibility.Visible:Visibility.Collapsed;
   L("SidebarGuideTitle").Text=local?"本地快捷收藏":bookmarkMode?"网址文件夹":"还没有素材分类";
   L("SidebarGuideHint").Text=local?"分组在本页上方管理。\n文件只保存位置，原件不移动。":bookmarkMode?"文件夹在网址页内管理。\n素材分类与网址文件夹互不混用。":"可按项目、用途建立分类。\n一个素材可以属于多个分类。";
   B("CreateFirstFolder").Visibility=!bookmarkMode&&!local&&noFolders?Visibility.Visible:Visibility.Collapsed;
   L("InspectorEmptyHint").Text=local?"选中文件，查看本地地址和所属分组。\n移除快捷收藏不会删除原文件。":bookmarkMode?"选中网址，查看链接、标签和备注。\n双击快速浏览，右键可在外部打开。":"选中素材，查看来源、标签和本地地址。\n双击或按空格预览。";
   ApplyBrowserPresentation();
  }
  void UpdateInspectorPresentation(){
   var items=Selected();bool single=items.Count==1,any=items.Count>0;
   Find<StackPanel>("Inspector").Visibility=any?Visibility.Visible:Visibility.Collapsed;
   Find<StackPanel>("InspectorEmpty").Visibility=any?Visibility.Collapsed:Visibility.Visible;
   L("SelectionInfo").Visibility=L("InspectorShortcuts").Visibility=any?Visibility.Visible:Visibility.Collapsed;
   foreach(string name in new[]{"Name","Notes"})T(name).Visibility=single?Visibility.Visible:Visibility.Collapsed;
   Find<Grid>("SourceRow").Visibility=L("NotesLabel").Visibility=single?Visibility.Visible:Visibility.Collapsed;
   L("TagsLabel").Text=single?"标签 · 逗号分隔":"批量追加标签 · 保留已有标签";
   Find<Grid>("FileInfo").Visibility=L("FileInfoHeading").Visibility=single&&!items[0].IsBookmark?Visibility.Visible:Visibility.Collapsed;
   bool pictures=any&&items.All(a=>SectionContains(a,"pictures"));
   B("Similar").Visibility=B("ColorSearch").Visibility=single&&pictures?Visibility.Visible:Visibility.Collapsed;
   Find<UniformGrid>("ImageActions").Visibility=single&&pictures?Visibility.Visible:Visibility.Collapsed;
   B("SelectionCanvas").Visibility=items.Count>=2&&pictures&&view!="trash"?Visibility.Visible:Visibility.Collapsed;
   // Both buttons formerly opened the same local-group chooser.
   B("Categorize").Visibility=LocalCollectionsActive?Visibility.Collapsed:Visibility.Visible;
   Find<UniformGrid>("CategoryActions").Columns=LocalCollectionsActive?1:2;
   B("Star").Margin=LocalCollectionsActive?new Thickness(0,0,0,5):new Thickness(3,0,0,5);
   B("Export").Visibility=any&&!items.Any(a=>a.IsDirectory)?Visibility.Visible:Visibility.Collapsed;
   ApplyBrowserPresentation();
  }
  internal ContextMenu CreateNewFolderMenu(){
   var menu=Theme.Menu(Window);menu.Items.Add(ActionItem("新建顶层分类…",()=>NewFolder(""),"",!busy));
   string parent=CurrentFolder();var folder=library.Folders(false).FirstOrDefault(f=>f.Id==parent);
   if(folder!=null)menu.Items.Add(ActionItem("新建子分类："+folder.Name+"…",()=>NewFolder(parent),"",!busy));
   return menu;
  }
  void ChooseReferenceFiles(){
   if(busy||!PrepareLibraryChange())return;
   var dialog=new Microsoft.Win32.OpenFileDialog{Multiselect=true,Title="收藏本地文件 · 仅保存位置",Filter="所有文件|*.*"};
   if(dialog.ShowDialog(Window)==true)CollectClassicPaths(dialog.FileNames,"",localCollection);
  }
 }
}
