using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;

namespace Mori {
 public partial class MainWindow {
  void AddOverviewVisibilityMenu(ContextMenu menu,List<Asset> selected){
   if(selected.Count==0||!selected.All(Library.CanSetOverviewVisibility))return;
   menu.Items.Add(new Separator());
   if(selected.Any(a=>!a.HiddenFromAll)){
    var hide=ActionItem("不在全部素材中显示",()=>SetSelectedOverviewHidden(true),"",!busy);
    hide.ToolTip="只从全部素材隐藏，不删除文件；在本地收藏、对应类型或原分类中仍可找到。";menu.Items.Add(hide);
   }
   if(selected.Any(a=>a.HiddenFromAll)){
    var show=ActionItem("在全部素材中显示",()=>SetSelectedOverviewHidden(false),"",!busy);
    show.ToolTip="恢复到全部素材；不复制或移动文件。";menu.Items.Add(show);
   }
  }
  internal void SetSelectedOverviewHidden(bool hidden){
   if(busy||view=="trash"||!PrepareLibraryChange())return;
   var selected=Selected();if(selected.Count==0)return;
   try{
    int changed=selected.Count(a=>a.HiddenFromAll!=hidden);
    Edit(hidden?"不在全部素材中显示":"在全部素材中显示",()=>library.SetHiddenFromAll(selected.Select(a=>a.Id),hidden));
    Refresh(true);
    Status(hidden?"已从全部素材隐藏 "+changed+" 项 · 本地收藏、对应类型和原分类仍可找到 · Ctrl+Z 撤销":"已恢复 "+changed+" 项在全部素材中显示 · Ctrl+Z 撤销");
   }catch(Exception ex){Status("显示设置未更改："+ex.Message);}
  }
 }
}
