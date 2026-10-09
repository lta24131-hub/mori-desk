using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Mori {
 public static class FolderSelection {
  public static readonly DependencyProperty ManagingProperty=DependencyProperty.RegisterAttached("Managing",typeof(bool),typeof(FolderSelection),new PropertyMetadata(false));
  public static bool GetManaging(DependencyObject o){return (bool)o.GetValue(ManagingProperty);} public static void SetManaging(DependencyObject o,bool v){o.SetValue(ManagingProperty,v);}
  public static readonly DependencyProperty CheckedProperty=DependencyProperty.RegisterAttached("Checked",typeof(bool),typeof(FolderSelection),new PropertyMetadata(false));
  public static bool GetChecked(DependencyObject o){return (bool)o.GetValue(CheckedProperty);} public static void SetChecked(DependencyObject o,bool v){o.SetValue(CheckedProperty,v);}
 }
 public partial class MainWindow {
  bool folderManaging;string folderAnchor="";
  readonly HashSet<string> folderSelection=new HashSet<string>();
  bool PrepareFolderChange(){if(busy||bookmarkMode||!PrepareLibraryChange())return false;UpdateInspector();return true;}
  void InitializeFolderBatch(){
   B("ManageFolders").Click+=delegate{SetFolderManaging(!folderManaging);};
   B("FolderSelectAll").Click+=delegate{SelectAllFolders();};
   B("FolderClear").Click+=delegate{folderSelection.Clear();folderAnchor="";SyncFolderSelection();};
   B("FolderMove").Click+=delegate{ChooseFolderParent(folderSelection.ToArray());};
   B("FolderDelete").Click+=delegate{ConfirmDeleteFolders(folderSelection.ToArray());};
   Find<TreeView>("Folders").PreviewMouseLeftButtonDown+=FolderSelectDown;
  }
  List<TreeViewItem> FolderNodes(bool visibleOnly){
   var result=new List<TreeViewItem>();Action<ItemCollection> walk=null;
   walk=delegate(ItemCollection items){foreach(TreeViewItem node in items){result.Add(node);if(!visibleOnly||node.IsExpanded)walk(node.Items);}};
   walk(Find<TreeView>("Folders").Items);return result;
  }
  internal void SetFolderManaging(bool enabled){
   if(!PrepareFolderChange())return;
   folderManaging=enabled;folderSelection.Clear();folderAnchor="";if(enabled)SyncFolderSelection();else ReloadFolders();Find<TreeView>("Folders").Focus();
  }
  void EndFolderManaging(){folderManaging=false;folderSelection.Clear();folderAnchor="";SyncFolderSelection();}
  void SyncFolderSelection(){
   var nodes=FolderNodes(false);var valid=new HashSet<string>(nodes.Select(n=>(string)n.Tag));folderSelection.RemoveWhere(id=>!valid.Contains(id));
   foreach(var node in nodes){FolderSelection.SetManaging(node,folderManaging);FolderSelection.SetChecked(node,folderSelection.Contains((string)node.Tag));}
   Find<StackPanel>("FolderBatchBar").Visibility=folderManaging&&!bookmarkMode?Visibility.Visible:Visibility.Collapsed;
   B("ManageFolders").Content=folderManaging?"完成":"管理";B("ManageFolders").ToolTip="批量选择、移动或删除分类 · Ctrl / Shift 多选";
   B("ManageFolders").SetResourceReference(Control.BackgroundProperty,folderManaging?"AccentSoft":"Panel");
   L("FolderSelectedCount").Text="已选 "+folderSelection.Count;
   B("FolderMove").IsEnabled=B("FolderDelete").IsEnabled=!busy&&folderSelection.Count>0;
   B("FolderClear").IsEnabled=!busy&&folderSelection.Count>0;B("FolderSelectAll").IsEnabled=!busy&&nodes.Count>0;
  }
  internal void SelectAllFolders(){
   if(!PrepareFolderChange())return;folderManaging=true;folderSelection.Clear();
   foreach(var node in FolderNodes(true))folderSelection.Add((string)node.Tag);SyncFolderSelection();
  }
  internal void SelectFolder(string id,ModifierKeys keys){
   if(!PrepareFolderChange())return;
   var order=FolderNodes(true).Select(n=>(string)n.Tag).ToList();if(!order.Contains(id))return;
   if(!folderManaging){folderManaging=true;folderSelection.Clear();var current=Find<TreeView>("Folders").SelectedItem as TreeViewItem;if(current!=null){folderSelection.Add((string)current.Tag);folderAnchor=(string)current.Tag;}}
   if((keys&ModifierKeys.Shift)!=0&&order.Contains(folderAnchor)){
    int first=order.IndexOf(folderAnchor),last=order.IndexOf(id);for(int i=Math.Min(first,last);i<=Math.Max(first,last);i++)folderSelection.Add(order[i]);
   }else{if(!folderSelection.Remove(id))folderSelection.Add(id);folderAnchor=id;}
   SyncFolderSelection();
  }
  void FolderSelectDown(object sender,MouseButtonEventArgs e){
   var tree=Find<TreeView>("Folders");var node=FolderNode(e.OriginalSource as DependencyObject,tree);if(node==null)return;
   for(var source=e.OriginalSource as DependencyObject;source!=null&&source!=node;source=SelectionParent(source))if(source is ToggleButton)return;
   var keys=Keyboard.Modifiers;if(!folderManaging&&(keys&(ModifierKeys.Control|ModifierKeys.Shift))==0)return;
   SelectFolder((string)node.Tag,keys);if(folderManaging)node.Focus();e.Handled=true;
  }
  internal string[] FolderActionIds(string id){return folderManaging&&folderSelection.Contains(id)?folderSelection.ToArray():string.IsNullOrEmpty(id)?new string[0]:new[]{id};}
  internal void ApplyFolderMove(string[] ids,string parent){
   if(ids==null||ids.Length==0||!PrepareFolderChange())return;
   try{Edit("移动 "+ids.Distinct().Count()+" 个分类",()=>library.MoveFolders(ids,parent,false));ReloadFolders(parent);Refresh(false);Status("分类已移动，子分类与素材保留 · Ctrl+Z 撤销。");}
   catch(Exception ex){MessageBox.Show(Window,ex.Message,"分类未移动");}
  }
  void ChooseFolderParent(string[] ids){
   if(ids.Length==0||!PrepareFolderChange())return;
   try{var folders=library.Folders(false);var excluded=FolderBatch.Branches(folders,ids);
    string parent=CategoryPicker.ChooseParent(Window,folders,excluded,ids.Length);if(parent!=null)ApplyFolderMove(ids,parent);
   }catch(Exception ex){MessageBox.Show(Window,ex.Message,"无法移动分类");}
  }
  internal void ApplyFolderDelete(string[] ids){
   if(ids==null||ids.Length==0||!PrepareFolderChange())return;
   try{Edit("删除 "+ids.Distinct().Count()+" 个分类",()=>library.DeleteFolders(ids,false));
    if(ids.Contains(view))Navigate("unfiled");else{ReloadFolders();Refresh(true);}
    Status("所选分类已删除，素材全部保留 · Ctrl+Z 恢复分类及归属。");
   }catch(Exception ex){MessageBox.Show(Window,ex.Message,"分类未删除");}
  }
  void ConfirmDeleteFolders(string[] ids){
   if(busy||ids.Length==0)return;var folders=library.Folders(false);var selected=folders.Where(f=>ids.Contains(f.Id)).ToList();
   if(selected.Count!=ids.Distinct().Count()){ReloadFolders();Status("分类已变化，请重新选择。");return;}
   string names=string.Join("\n",selected.Take(6).Select(f=>"· "+f.Name))+(selected.Count>6?"\n…":"");
   string text="删除选中的 "+selected.Count+" 个分类？\n\n"+names+"\n\n只删除勾选的分类，不删除任何素材，也不移入回收站。\n没有其他分类的素材转到“未分类”；其他归属不变。\n未勾选的子分类保留，移到最近的未删除上级。\n可按 Ctrl+Z 一次恢复分类和原归属。";
   if(MessageBox.Show(Window,text,"删除分类，保留素材",MessageBoxButton.OKCancel,MessageBoxImage.Warning,MessageBoxResult.Cancel)==MessageBoxResult.OK)ApplyFolderDelete(ids);
  }
 }
}
