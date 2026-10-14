using System;
using System.IO;
using Microsoft.Win32;

namespace Mori {
 public static class StartupRegistration {
  const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
  internal const string ValueName="MoriDesk";
  public static bool IsEnabled(string exePath){return IsEnabled(exePath,new RegistryRunStore());}
  public static void SetEnabled(bool enabled,string exePath){SetEnabled(enabled,exePath,new RegistryRunStore());}
  internal static string Command(string exePath){
   if(string.IsNullOrWhiteSpace(exePath)||!Path.IsPathRooted(exePath)||exePath.IndexOfAny(new[]{'"','\r','\n','\0'})>=0)throw new ArgumentException("Nook 程序路径无效。","exePath");
   return "\""+Path.GetFullPath(exePath)+"\" --startup";
  }
  internal static bool IsEnabled(string exePath,IStartupStore store){return string.Equals(store.GetValue(ValueName),Command(exePath),StringComparison.OrdinalIgnoreCase);}
  internal static void SetEnabled(bool enabled,string exePath,IStartupStore store){
   if(store==null)throw new ArgumentNullException("store");
   if(enabled)store.SetValue(ValueName,Command(exePath));else store.DeleteValue(ValueName);
  }
  internal interface IStartupStore {
   string GetValue(string name);
   void SetValue(string name,string value);
   void DeleteValue(string name);
  }
  sealed class RegistryRunStore:IStartupStore {
   public string GetValue(string name){using(var key=Registry.CurrentUser.OpenSubKey(RunKey,false))return key==null?null:key.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames)as string;}
   public void SetValue(string name,string value){using(var key=Registry.CurrentUser.CreateSubKey(RunKey)){if(key==null)throw new IOException("无法写入当前用户的 Windows 启动设置。");key.SetValue(name,value,RegistryValueKind.String);}}
   public void DeleteValue(string name){using(var key=Registry.CurrentUser.OpenSubKey(RunKey,true)){if(key!=null)key.DeleteValue(name,false);}}
  }
 }
}
