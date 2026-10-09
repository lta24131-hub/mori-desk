using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Mori {
 public partial class MainWindow {
  bool reloadingFolders;
  void ReloadFolders(){ReloadFolders(null);}
  void ReloadFolders(string reveal){
   var tree=Find<TreeView>("Folders");var expanded=new Dictionary<string,bool>();
   Action<ItemCollection> remember=null;remember=delegate(ItemCollection items){foreach(TreeViewItem node in items){expanded[(string)node.Tag]=node.IsExpanded;remember(node.Items);}};remember(tree.Items);
   var scroll=VisualChild<ScrollViewer>(tree);double offset=scroll==null?0:scroll.VerticalOffset;
   var folders=library.Folders(false);var ancestors=new HashSet<string>();string current=reveal;
   while(!string.IsNullOrEmpty(current)&&ancestors.Add(current)){var found=folders.FirstOrDefault(f=>f.Id==current);current=found==null?null:found.Parent;}
   reloadingFolders=true;
   try{
    tree.Items.Clear();var added=new HashSet<string>();TreeViewItem target=null;
    Action<ItemCollection,string> build=null;build=delegate(ItemCollection items,string parent){
     foreach(var f in folders.Where(c=>c.Parent==parent)){
      if(!added.Add(f.Id))continue;bool open;
      var node=new TreeViewItem{Header=f.Name,Tag=f.Id,ToolTip=f.Name,AllowDrop=true,IsExpanded=ancestors.Contains(f.Id)||!expanded.TryGetValue(f.Id,out open)||open};
      node.Drop+=FolderDrop;items.Add(node);build(node.Items,f.Id);
      if(f.Id==view)node.IsSelected=true;if(f.Id==reveal)target=node;
     }
    };build(tree.Items,"");
    tree.UpdateLayout();scroll=VisualChild<ScrollViewer>(tree);if(scroll!=null)scroll.ScrollToVerticalOffset(offset);
    if(target!=null)target.BringIntoView();
   }finally{reloadingFolders=false;}SyncFolderSelection();
  }
  internal static TreeViewItem FolderNode(DependencyObject source,TreeView tree){
   for(var current=source;current!=null&&current!=tree;current=SelectionParent(current)){var node=current as TreeViewItem;if(node!=null)return node;}
   return null;
  }
  void FolderRight(object sender,MouseButtonEventArgs e){
   var tree=Find<TreeView>("Folders");var node=FolderNode(e.OriginalSource as DependencyObject,tree);
   if(folderManaging&&node!=null&&!folderSelection.Contains((string)node.Tag)){folderSelection.Clear();folderSelection.Add((string)node.Tag);folderAnchor=(string)node.Tag;SyncFolderSelection();}
   // A context action uses the row under the pointer, not the currently viewed
   // category. Opening the menu does not discard pending inspector edits.
   ShowFolderMenu(node,false);e.Handled=true;
  }
  internal ContextMenu CreateFolderMenu(string id){
   var menu=Theme.Menu(Window);
   if(string.IsNullOrEmpty(id))Menu(menu,"新建顶层分类…",delegate{NewFolder("");});
   else{
    var ids=FolderActionIds(id);
    if(ids.Length==1){Menu(menu,"新建子分类…",delegate{NewFolder(id);});Menu(menu,"重命名…",delegate{RenameFolder(id);});}
    Menu(menu,ids.Length>1?"移动 "+ids.Length+" 个分类到…":"移动分类到…",delegate{ChooseFolderParent(ids);});
    menu.Items.Add(new Separator());
    Menu(menu,ids.Length>1?"删除 "+ids.Length+" 个分类…":"删除此分类…",delegate{if(ids.Length==1)DeleteFolder(id);else ConfirmDeleteFolders(ids);});
   }
   menu.Items.Add(new Separator());Menu(menu,folderManaging?"结束分类管理":"批量管理分类…",delegate{SetFolderManaging(!folderManaging);});
   foreach(var item in menu.Items.OfType<MenuItem>())item.IsEnabled=!busy;
   return menu;
  }
  void ShowFolderMenu(TreeViewItem node,bool keyboard){
   var menu=CreateFolderMenu(node==null?null:node.Tag as string);
   menu.PlacementTarget=node==null?(UIElement)Find<TreeView>("Folders"):node;
   menu.Placement=keyboard?PlacementMode.Bottom:PlacementMode.MousePoint;menu.IsOpen=true;
  }
  bool HandleFolderKey(KeyEventArgs e){
   var tree=Find<TreeView>("Folders");var source=Keyboard.FocusedElement as DependencyObject;bool inside=false;
   for(var current=source;current!=null;current=SelectionParent(current))if(current==tree||current==Find<Grid>("MediaFolderHeading")){inside=true;break;}
   if(!inside)return false;
   var node=FolderNode(source,tree);
   if(e.Key==Key.F2&&node!=null){e.Handled=true;if(!folderManaging||folderSelection.Count<=1)RenameFolder((string)node.Tag);}
   else if(e.Key==Key.Delete){e.Handled=true;if(folderManaging)ConfirmDeleteFolders(folderSelection.ToArray());else if(node!=null)DeleteFolder((string)node.Tag);}
   else if(e.Key==Key.A&&Keyboard.Modifiers==ModifierKeys.Control){e.Handled=true;SelectAllFolders();}
   else if(e.Key==Key.Space&&folderManaging&&node!=null){e.Handled=true;SelectFolder((string)node.Tag,Keyboard.Modifiers);}
   else if(e.Key==Key.Apps||(e.Key==Key.F10&&Keyboard.Modifiers==ModifierKeys.Shift)){e.Handled=true;ShowFolderMenu(node,true);}
   else if(e.Key==Key.F5){e.Handled=true;ReloadFolders();Refresh(true);}
   else if(e.Key==Key.Escape){e.Handled=true;if(folderManaging)SetFolderManaging(false);else Find<ListBox>("Gallery").Focus();}
   else if(e.Key==Key.F&&Keyboard.Modifiers==ModifierKeys.Control){e.Handled=true;T("Search").Focus();}
   // Other keys retain native tree navigation, never trigger gallery deletion.
   return true;
  }
  void NewFolder(){NewFolder(CurrentFolder());}
  void NewFolder(string parent){
   if(busy||!PrepareLibraryChange())return;
   string name=Prompt(string.IsNullOrEmpty(parent)?"新建分类":"新建子分类","分类名称","");if(name==null)return;
   try{string id=null;Edit("新建分类",()=>id=library.CreateFolder(name,parent));Navigate(id);ReloadFolders(id);Status("分类已创建 · Ctrl+Z 撤销。");}
   catch(Exception ex){MessageBox.Show(Window,ex.Message,"无法新建分类");}
  }
  void RenameFolder(string id){
   if(busy)return;var folder=library.Folders().FirstOrDefault(f=>f.Id==id);if(folder==null){ReloadFolders();return;}
   string name=Prompt("重命名分类","分类名称",folder.Name);if(name==null||name.Trim()==folder.Name)return;
   try{Edit("重命名分类",()=>library.RenameFolder(id,name));ReloadFolders();Refresh(false);RefreshCategoryLabel();}
   catch(Exception ex){MessageBox.Show(Window,ex.Message,"无法重命名分类");}
  }
  void RefreshCategoryLabel(){var items=Selected();if(items.Count==1)L("CategoryInfo").Text="分类："+string.Join(" / ",library.AssetFolders(items[0].Id).Select(f=>f.Name));}
  void DeleteFolder(string id){
   if(busy)return;var folders=library.Folders();var folder=folders.FirstOrDefault(f=>f.Id==id);if(folder==null){ReloadFolders();return;}
   int children=folders.Count(f=>f.Parent==id);
   string text="删除分类“"+folder.Name+"”？\n\n仅删除分类，不删除图片、视频或网址，也不会把它们放入回收站。\n没有其他分类的内容会转到“未分类”；已有的其他分类归属不变。\n可按 Ctrl+Z 恢复分类及原来的归属。";
   if(children>0)text+="\n\n其中 "+children+" 个子分类会保留并移到上一级。";
   if(MessageBox.Show(Window,text,"删除分类",MessageBoxButton.OKCancel,MessageBoxImage.Warning,MessageBoxResult.Cancel)!=MessageBoxResult.OK)return;
   if(!PrepareLibraryChange())return;
   try{Edit("删除分类“"+folder.Name+"”",()=>library.DeleteFolder(id));if(view==id)Navigate("unfiled");ReloadFolders();Refresh(true);Status("分类已移除，内容保留 · Ctrl+Z 恢复分类及归属。");}
   catch(Exception ex){MessageBox.Show(Window,ex.Message,"无法删除分类");}
  }
 }
}
