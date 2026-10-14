using System;
using System.IO;
using System.Linq;
using System.Windows;
using MoriDesk;

namespace Mori {
 public partial class MainWindow {
  // Tests exercise the production commands without real file pickers/prompts.
  internal Func<Asset,MissingLocalFileAction> MissingReferenceChoice;
  internal Func<Asset,string> ReferencePathChoice;

  Asset ResolveReferenceForOpen(Asset item){
   if(item==null||!item.IsReference)return item;
   if(MissingLocalFile.Exists(item.ReferencePath,item.IsDirectory))return item;
   Status("原文件暂不可用 · 收藏仍保留，可重新定位");
   var choice=MissingReferenceChoice==null?MissingLocalFile.Show(Window,item.ReferencePath,item.IsDirectory):MissingReferenceChoice(item);
   if(choice==MissingLocalFileAction.Relocate)return RelinkReference(item);
   if(choice==MissingLocalFileAction.RemoveShortcut){
    if(busy||!PrepareLibraryChange())return null;
    Edit("仅移除失效快捷收藏",()=>library.Trash(new[]{item.Id},true));selection.Remove(item.Id);Refresh(true);Status("已仅移除收藏 · 原文件未删除 · Ctrl+Z 可恢复");
   }
   return null;
  }
  Asset RelinkReference(Asset item){
   if(item==null||!item.IsReference||busy)return null;
   string path=null;
   if(ReferencePathChoice!=null)path=ReferencePathChoice(item);
   else if(item.IsDirectory){using(var picker=new System.Windows.Forms.FolderBrowserDialog{Description="重新定位原文件夹（不导入里面的文件）"})if(picker.ShowDialog()==System.Windows.Forms.DialogResult.OK)path=picker.SelectedPath;}
   else{var picker=new Microsoft.Win32.OpenFileDialog{Title="重新定位原文件（仅更改收藏地址）",Filter="全部文件|*.*",CheckFileExists=true};if(picker.ShowDialog(Window)==true)path=picker.FileName;}
   if(string.IsNullOrEmpty(path)||!PrepareLibraryChange())return null;
   try{Edit("重新定位快捷收藏",()=>library.RelinkReferenceAsset(item.Id,path));Refresh(true);Status("已重新定位 · 原文件未复制或移动 · Ctrl+Z 可撤销");return all.FirstOrDefault(a=>a.Id==item.Id);}
   catch(Exception error){MessageBox.Show(Window,error.Message,"重新定位未完成",MessageBoxButton.OK,MessageBoxImage.Warning);return null;}
  }
  void RevealAsset(Asset item){
   try{item=ResolveReferenceForOpen(item);if(item==null)return;FileRouter.Reveal(library.FullPath(item));}
   catch(Exception error){MessageBox.Show(Window,error.Message,"无法打开原文件位置",MessageBoxButton.OK,MessageBoxImage.Warning);}
  }
 }
}
