using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Mori {
 public partial class MainWindow {
  readonly UndoHistory history;
  bool Edit(string label,Action action){if(busy)return false;bool recorded=history.Record(label,action);Status(label+(recorded?" · Ctrl+Z 撤销":""));return recorded;}
  internal void UndoLast(){
   if(busy){Status("请等待当前操作完成后再撤销。");return;}
   if(history.Count==0){Status("没有可撤销的操作。输入框内 Ctrl+Z 撤销文字编辑。");return;}
   if(!PrepareLibraryChange())return;
   try{string label=history.Undo();if(CurrentFolder()!=""&&!library.Folders().Any(f=>f.Id==view))view="unfiled";ReloadFolders();Refresh(true);Status("已撤销："+label);}
   catch(Exception error){Status(error.Message);}
  }
  void AddUndoMenu(ContextMenu menu){var item=new MenuItem{Header=history.Count>0?"撤销："+history.Label:"撤销",InputGestureText="Ctrl+Z",IsEnabled=!busy&&history.Count>0};item.Click+=delegate{UndoLast();};menu.Items.Add(item);menu.Items.Add(new Separator());}
 }
}
