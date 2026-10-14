using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MoriDesk;

namespace Mori {
 public partial class MainWindow {
  // Tests inject a sink and cannot change the user's Windows desktop.
  internal WallpaperService WallpaperForTests;
  void AddWallpaperMenu(ContextMenu menu,Asset asset){
   if(!WallpaperService.Supports(asset)||Selected().Count!=1||view=="trash")return;
   bool available=WallpaperService.SourceAvailable(library.FullPath(asset));
   var item=ActionItem("设为桌面壁纸",()=>SetDesktopWallpaper(asset),"",!busy&&available);
   item.ToolTip=available?(asset.IsGif?"使用动图第一帧 · 保留原文件":"保存独立的静态壁纸副本 · 特殊格式使用内置预览"):"原图片不可用，请先重新定位原文件";
   ToolTipService.SetShowOnDisabled(item,true);menu.Items.Add(item);
  }
  internal async Task SetDesktopWallpaperAsync(Asset asset){
   var selected=Selected();
   if(busy||selected.Count!=1||asset==null||selected[0].Id!=asset.Id||!WallpaperService.Supports(asset)||view=="trash"){Status("请只选择一张可用的图片设置桌面壁纸。");return;}
   busy=true;UpdateBatchActions();Status("正在设置桌面壁纸…");
   try{
    if(PreviewMode&&WallpaperForTests==null)throw new InvalidOperationException("测试窗口不修改系统桌面壁纸。");
    var service=WallpaperForTests??WallpaperService.CreateDefault();string source=library.FullPath(asset);
    await Task.Run(()=>service.Set(asset,source));
    Status(asset.IsGif?"已设为桌面壁纸 · 使用动图第一帧":"已设为桌面壁纸");
   }catch(Exception ex){Status("壁纸设置失败："+ex.Message);if(!PreviewMode)MessageBox.Show(Window,ex.Message,"无法设置桌面壁纸",MessageBoxButton.OK,MessageBoxImage.Warning);}
   finally{busy=false;UpdateBatchActions();}
  }
  async void SetDesktopWallpaper(Asset asset){await SetDesktopWallpaperAsync(asset);}
 }
}
