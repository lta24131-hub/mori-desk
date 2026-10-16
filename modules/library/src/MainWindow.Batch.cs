using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Mori {
 public partial class MainWindow {
  bool batchSelecting,collapseSelectionOnUp;
  string[] dragSelection;
  void InitializeBatch(){
   B("MultiSelect").Click+=delegate{SetBatchSelecting(!batchSelecting);};
   B("BatchAll").Click+=delegate{SelectAllVisible();};B("BatchClear").Click+=delegate{ClearGallerySelection();};
   B("BatchMove").Click+=delegate{ChooseCategory(true);};B("MoveCategory").Click+=delegate{ChooseCategory(true);};
   B("BatchDelete").Click+=delegate{Trash();};B("BatchRestore").Click+=delegate{RestoreTrash();};
  }
  internal void SetBatchSelecting(bool enabled){
   if(canvasProjectsMode||busy||!PrepareLibraryChange())return;batchSelecting=enabled;pressed=null;dragSelection=null;
   if(!enabled){selection.Clear();anchor="";}SyncSelection();Find<ListBox>("Gallery").Focus();
  }
  internal void SelectAllVisible(){
   if(canvasProjectsMode||busy||!PrepareLibraryChange())return;selection.Clear();foreach(var a in visible)selection.Add(a.Id);SyncSelection();Find<ListBox>("Gallery").Focus();
  }
  // Capture modifiers on mouse-down. Releasing Ctrl before the mouse button
  // must not collapse the multi-selection again on mouse-up.
  internal bool SelectTile(Asset a,ModifierKeys modifiers){
   if(canvasProjectsMode||busy||a==null||!visible.Any(x=>x.Id==a.Id))return false;
   if((batchSelecting||!selection.Contains(a.Id)||modifiers!=ModifierKeys.None)&&!PrepareLibraryChange())return false;
   dragSelection=batchSelecting&&selection.Contains(a.Id)?selection.ToArray():null;
   collapseSelectionOnUp=!batchSelecting&&modifiers==ModifierKeys.None&&selection.Contains(a.Id);
   if(collapseSelectionOnUp)return true;
   if((modifiers&ModifierKeys.Shift)!=0&&anchor!=""&&visible.Any(x=>x.Id==anchor)){
    int first=visible.FindIndex(x=>x.Id==anchor),last=visible.FindIndex(x=>x.Id==a.Id);for(int i=Math.Min(first,last);i<=Math.Max(first,last);i++)selection.Add(visible[i].Id);
   }else if(batchSelecting||(modifiers&ModifierKeys.Control)!=0){if(!selection.Remove(a.Id))selection.Add(a.Id);anchor=a.Id;}
   else{selection.Clear();selection.Add(a.Id);anchor=a.Id;}
   SyncSelection();return true;
  }
  internal void FinishTileSelection(Asset a,bool wasDragged){
   if(canvasProjectsMode){collapseSelectionOnUp=false;dragSelection=null;return;}
   if(a!=null&&!wasDragged&&collapseSelectionOnUp&&selection.Count>1&&PrepareLibraryChange()){selection.Clear();selection.Add(a.Id);anchor=a.Id;SyncSelection();}collapseSelectionOnUp=false;dragSelection=null;
  }
  internal void RestoreDragSelection(){if(dragSelection==null)return;selection.Clear();foreach(string id in dragSelection)selection.Add(id);dragSelection=null;SyncSelection();}
  void UpdateBatchActions(){
   int count=selection.Count;bool trash=view=="trash";
   Find<Border>("BatchBar").Visibility=batchSelecting||count>1?Visibility.Visible:Visibility.Collapsed;
   L("BatchCount").Text="已选 "+count+" 项";B("MultiSelect").Content=batchSelecting?"完成":"多选";
   B("MultiSelect").SetResourceReference(Control.BackgroundProperty,batchSelecting?"AccentSoft":"Control");
   B("BatchAll").IsEnabled=!busy&&visible.Count>0;B("BatchClear").IsEnabled=!busy&&count>0;
   B("BatchMove").Visibility=trash?Visibility.Collapsed:Visibility.Visible;B("BatchRestore").Visibility=trash?Visibility.Visible:Visibility.Collapsed;
   foreach(string name in new[]{"BatchMove","BatchRestore","BatchDelete"})B(name).IsEnabled=!busy&&count>0;
   B("BatchDelete").Content=trash?"永久删除…":Selected().Any(a=>a.IsReference)?"移除收藏":"移到回收站";
   B("BatchMove").ToolTip=LocalCollectionsActive?"把选中的快捷方式移到本页分类文件夹；不移动原文件":"把选中内容移到一个分类，替换原有分类归属；可 Ctrl+Z 撤销";
   B("MoveCategory").Content=LocalCollectionsActive?"移到分类文件夹…":"移动到分类…";
   B("MoveCategory").Visibility=trash||bookmarkMode?Visibility.Collapsed:Visibility.Visible;B("MoveCategory").IsEnabled=!busy&&count>0;
  }
  internal void ApplyCategory(string[] ids,string folder,bool move,bool bookmarks){
   if(canvasProjectsMode||busy||view=="trash"||ids==null||ids.Length==0)return;
   string name=folder==""?"未分类":library.Folders(bookmarks).Where(f=>f.Id==folder).Select(f=>f.Name).FirstOrDefault();
   if(name==null){Status("目标分类已不存在，请重新选择。");return;}
   try{
    Edit((move?"移动 ":"添加 ")+ids.Distinct().Count()+" 项到："+name,delegate{if(move)library.MoveAssets(ids,folder,bookmarks);else library.AddToFolder(ids,folder);});
    Refresh(true);
   }catch(Exception ex){Status("分类未更改："+ex.Message);}
  }
  void ChooseCategory(bool move){
   if(canvasProjectsMode)return;
   if(LocalCollectionsActive){ChooseLocalCollection();return;}
   if(busy||view=="trash"||selection.Count==0||!PrepareLibraryChange())return;
   var ids=selection.ToArray();bool web=bookmarkMode;move=move||web;
   string target=CategoryPicker.Choose(Window,library.Folders(web),move,ids.Length);
   if(target!=null)ApplyCategory(ids,target,move,web);
  }
 }
}
