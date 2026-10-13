using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MoriDesk;

namespace Mori {
 public partial class MainWindow {
  void InitializeLocalAddress(){
   InitializeLocalCollections();
   B("RevealLocal").Click+=delegate{var a=Selected().SingleOrDefault();if(a==null)return;try{FileRouter.Reveal(library.FullPath(a));}catch(Exception ex){Status("无法打开位置："+ex.Message);}};
   B("CopyLocal").Click+=delegate{if(T("LocalAddress").Text.Length>0)try{Clipboard.SetText(T("LocalAddress").Text);Status("已复制本地地址");}catch(Exception ex){Status(ex.Message);}};
  }
  void UpdateLocalAddress(List<Asset> items){
   var panel=Find<StackPanel>("LocalAddressPanel");if(panel==null)return;
   bool single=items.Count==1;panel.Visibility=single&&!items[0].IsBookmark?Visibility.Visible:Visibility.Collapsed;
   T("LocalAddress").Text=single&&!items[0].IsBookmark?library.FullPath(items[0]):"";
   L("LocalStorageHint").Text=single&&items[0].IsReference?"快捷方式 · 移除收藏不删除原文件":"图库保存 · 独立副本";
   if(single&&items[0].HiddenFromAll)L("LocalStorageHint").Text+="\n已在全部素材中隐藏 · 右键可恢复显示";
   if(single&&items[0].AddedRecovered)L("AddedTime").Text="历史收藏";
   if(single&&LocalCollectionsActive)L("CategoryInfo").Text="分类文件夹："+(library.LocalCollections().Where(f=>f.Id==items[0].LocalCollection).Select(f=>f.Name).FirstOrDefault()??"本地收藏外层");
   if(view!="trash"&&items.Any(a=>a.IsReference))B("Delete").Content=items.All(a=>a.IsReference)?"移除快捷收藏":"移除所选收藏";
  }
  bool SaveClassicPending(){if(!HasUnsavedMetadata())return true;if(string.IsNullOrWhiteSpace(T("Name").Text)&&Selected().Count==1){Status("请先填写素材名称，再拖入收藏。");return false;}SaveMetadata();return true;}
  internal void CollectClassicPaths(string[] paths,string folder){CollectClassicPaths(paths,folder,LocalCollectionsActive?localCollection:null);}
  internal void CollectClassicPaths(string[] paths,string folder,string collection){
   HideDragHint();if(busy){Status("请等待当前操作完成后再收藏。");return;}
   // A drop is an immediate shortcut operation. Never recurse, copy, or open a confirmation dialog.
   try{
    // Keep unsaved inspector edits when navigation changes after the drop.
    if(!SaveClassicPending())return;
    string[] ids=null;Edit("收藏本地快捷方式",()=>{ids=library.AddReferenceAssets(paths,folder);if(collection!=null)library.MoveToLocalCollection(ids,collection);});
    bool foldersOnly=ids.Length>0&&library.All().Where(a=>ids.Contains(a.Id)).All(a=>a.IsDirectory);
    bool keepLocal=mediaSection=="shortcuts"||collection!=null||foldersOnly;
    HideBoard();HideReferences();RevealImported(ids,foldersOnly?"":folder);
    if(keepLocal&&(foldersOnly||string.IsNullOrEmpty(folder))){mediaSection="shortcuts";localCollection=collection??"";}
    Refresh(true);Status("已收藏 "+ids.Length+" 项 · 只保存位置，文件夹内容不导入 · Ctrl+Z 撤销");
   }catch(Exception ex){Status("收藏未完成："+ex.Message);}
  }
  void OpenReference(Asset a){try{FileRouter.Open(Window,library.FullPath(a),a.Name);}catch(Exception ex){Status("无法打开："+ex.Message);}}
 }
}
