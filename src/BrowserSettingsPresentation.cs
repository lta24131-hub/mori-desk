using System;

namespace MoriDesk {
 // Presentation rules are independent of the live browser and pairing files.
 internal static class BrowserSettingsPresentation {
  internal static bool Current(BrowserExtensionStatus status){return status.upToDate&&status.connected&&!status.pending&&status.version==status.bundledVersion&&status.installedVersion==status.bundledVersion;}
  internal static string Versions(BrowserExtensionStatus status){
   string text="软件随附 "+status.bundledVersion;
   if(!string.IsNullOrEmpty(status.installedVersion))text+="    已安装 "+status.installedVersion;
   if(!string.IsNullOrEmpty(status.version))text+="    浏览器运行 "+status.version;
   return text;
  }
  internal static bool CanUpdate(BrowserExtensionStatus status){return !status.requiresBootstrap&&!string.IsNullOrEmpty(status.installedVersion)&&(!status.pending||status.retryUpdate)&&status.profiles<=1&&!Newer(status)&&!Current(status);}
  static bool Newer(BrowserExtensionStatus status){return !string.IsNullOrEmpty(status.availableVersion)&&!string.IsNullOrEmpty(status.bundledVersion)&&BrowserExtensionStore.CompareVersions(status.availableVersion,status.bundledVersion)>0;}
  internal static string UpdateHint(BrowserExtensionStatus status){
   if(status.requiresBootstrap||string.IsNullOrEmpty(status.installedVersion))return "请先点“安装 / 修复”。首次使用需要浏览器确认一次。";
   if(Newer(status))return "现有插件较新，请先更新 Nook。不会降级插件。";
   if(Current(status))return "已是最新版本，不需要重复更新。";
   if(status.profiles>1)return "请关闭其他浏览器配置后再更新。普通网页标签不需要关闭。";
   if(status.retryUpdate)return "上次更新等待已超时，可以重新请求。当前收藏仍会先完成。";
   if(status.pending)return "已在等待当前收藏结束和浏览器确认，不需要重复点击。";
   return "更新当前软件随附的插件；正在收藏时会自动等待，不会中断保存。";
  }
 }
}
