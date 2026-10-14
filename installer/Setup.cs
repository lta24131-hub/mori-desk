using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Diagnostics;
namespace MoriDeskInstaller {
 static class Program {
  const string Version="0.1.22";
  internal static bool ProductIdentity(string productName,string originalFilename){return (string.Equals(productName,"Mori Desk",StringComparison.OrdinalIgnoreCase)||string.Equals(productName,"Nook",StringComparison.OrdinalIgnoreCase))&&string.Equals(originalFilename,"Mori.exe",StringComparison.OrdinalIgnoreCase);}
  internal static bool MatchesRunningProcess(int processId,int sessionId,string productName,string originalFilename,int installerId,int installerSession){return processId!=installerId&&sessionId==installerSession&&ProductIdentity(productName,originalFilename);}
  static bool HasRunningMoriDesk(){
   int installerId,installerSession;using(var current=Process.GetCurrentProcess()){installerId=current.Id;installerSession=current.SessionId;}
   var processes=Process.GetProcesses();bool running=false;
   try{foreach(var process in processes){try{if(process.Id==installerId||process.SessionId!=installerSession)continue;var module=process.MainModule;if(module!=null&&MatchesRunningProcess(process.Id,process.SessionId,module.FileVersionInfo.ProductName,module.FileVersionInfo.OriginalFilename,installerId,installerSession))running=true;}
    // System processes and a process exiting during inspection may not expose
    // metadata. Never infer ownership from an executable name or kill it.
    catch(System.ComponentModel.Win32Exception){}catch(InvalidOperationException){}catch(ArgumentException){}catch(IOException){}catch(UnauthorizedAccessException){}}}
   finally{foreach(var process in processes)process.Dispose();}return running;
  }
  internal static ProcessStartInfo VerificationStart(string executable,string folder){
   var start=new ProcessStartInfo(executable,"--suite-test"){UseShellExecute=false,CreateNoWindow=true};
   start.EnvironmentVariables["TEMP"]=Path.Combine(folder,"runtime-temp");start.EnvironmentVariables["TMP"]=start.EnvironmentVariables["TEMP"];start.EnvironmentVariables["MORI_DESK_DATA"]=Path.Combine(folder,"test-data","MoriDesk");return start;
  }
  static string Extract(string folder){Directory.CreateDirectory(folder);string prefix=Path.GetFullPath(folder).TrimEnd('\\')+"\\";using(var source=Assembly.GetExecutingAssembly().GetManifestResourceStream("MoriDesk.Payload.zip"))using(var zip=new ZipArchive(source,ZipArchiveMode.Read)){foreach(var entry in zip.Entries){string file=Path.GetFullPath(Path.Combine(prefix,entry.FullName));if(!file.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("安装包路径异常");if(string.IsNullOrEmpty(entry.Name)){Directory.CreateDirectory(file);continue;}Directory.CreateDirectory(Path.GetDirectoryName(file));using(var input=entry.Open())using(var output=new FileStream(file,FileMode.CreateNew,FileAccess.Write))input.CopyTo(output);}}string exe=Path.Combine(folder,"Mori.exe");if(!File.Exists(exe))throw new InvalidDataException("安装包缺少程序");return exe;}
  static string ShortcutTarget(string location){object shell=null,link=null;try{shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));link=((dynamic)shell).CreateShortcut(location);return Convert.ToString(((dynamic)link).TargetPath);}finally{if(link!=null)Marshal.FinalReleaseComObject(link);if(shell!=null)Marshal.FinalReleaseComObject(shell);}}
  internal static bool ManagedTarget(string target,string programRoot,string product,string originalFilename){try{return !string.IsNullOrWhiteSpace(target)&&Path.GetFullPath(target).StartsWith(Path.GetFullPath(programRoot).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase)&&ProductIdentity(product,originalFilename)&&string.Equals(Path.GetFileName(target),"Mori.exe",StringComparison.OrdinalIgnoreCase);}catch(ArgumentException){return false;}catch(NotSupportedException){return false;}catch(PathTooLongException){return false;}}
  static bool OwnsShortcut(string location){try{string target=ShortcutTarget(location);if(!File.Exists(target))return false;var info=FileVersionInfo.GetVersionInfo(target);return ManagedTarget(target,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","MoriDesk"),info.ProductName,info.OriginalFilename);}catch{return false;}}
  static void GuardShortcut(string location){if(File.Exists(location)&&!OwnsShortcut(location))throw new IOException("已有同名快捷方式不属于本软件，未覆盖：\n"+location+"\n请先改名保留该快捷方式，再重新安装。");}
  static void RemoveOwnedLegacyShortcut(string location){if(File.Exists(location)&&OwnsShortcut(location)){try{File.Delete(location);}catch(IOException){}catch(UnauthorizedAccessException){}}}
  static void Shortcut(string location,string exe){GuardShortcut(location);object shell=null,link=null;try{shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));link=((dynamic)shell).CreateShortcut(location);dynamic shortcut=link;shortcut.TargetPath=exe;shortcut.WorkingDirectory=Path.GetDirectoryName(exe);shortcut.Description="Nook · 素材、截图、翻译与看图";shortcut.IconLocation=exe+",0";shortcut.Save();}finally{if(link!=null)Marshal.FinalReleaseComObject(link);if(shell!=null)Marshal.FinalReleaseComObject(shell);}}
  [STAThread] static int Main(string[] args){Application.EnableVisualStyles();
   if(args.Length==1&&args[0]=="--verify"){try{string temp=Path.Combine(Path.GetTempPath(),"MoriDesk-install-check-"+Guid.NewGuid().ToString("N"));string exe=Extract(temp);var start=VerificationStart(exe,temp);Directory.CreateDirectory(start.EnvironmentVariables["TEMP"]);Directory.CreateDirectory(start.EnvironmentVariables["MORI_DESK_DATA"]);using(var process=Process.Start(start)){process.WaitForExit();return process.ExitCode;}}catch{return 1;}}
   if(MessageBox.Show("安装 Nook 测试版？\n\n包含素材库、截图翻译、输入翻译、图片和 3D 查看器。\n保留已有 Mori Desk / Nook 数据，不替换独立 Mori / Luma。\n不自动迁移素材，不设置开机启动，不修改默认应用。\n\n3D / 部分视频需要系统已安装 WebView2。\n浏览器插件可在设置中安装、修复和更新。\n首次加载仍需在 Chrome / Edge 中确认。","Nook",MessageBoxButtons.OKCancel,MessageBoxIcon.Information)!=DialogResult.OK)return 0;
   try{
    if(HasRunningMoriDesk()){MessageBox.Show("Mori Desk / Nook 仍在运行，本次尚未安装。\n\n请先完成当前收藏并保存正在编辑的信息，通过“更多 → 退出 Mori Desk”（新版为“退出 Nook”）或托盘菜单完全退出，再重新运行安装包。右上角 × 只会收进托盘。\n\n安装器不会强制结束程序，也不会更改素材、贴图或开机启动设置。","请先退出 Mori Desk / Nook",MessageBoxButtons.OK,MessageBoxIcon.Information);return 1;}
    string desktop=Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    string programs=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),"Programs");
    string desktopLink=Path.Combine(desktop,"Nook.lnk"),start=Path.Combine(programs,"Nook"),startLink=Path.Combine(start,"Nook.lnk");
    // Check both destinations before extracting or replacing any shortcut.
    // A different application named Nook must never be overwritten.
    GuardShortcut(desktopLink);GuardShortcut(startLink);
    string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","MoriDesk",Version+"-"+DateTime.Now.ToString("yyyyMMddHHmmss"));
    string exe=Extract(root);Directory.CreateDirectory(start);Shortcut(desktopLink,exe);Shortcut(startLink,exe);
    // Remove only links whose existing target is demonstrably our old suite.
    // Never remove old program directories, user data or unrelated Mori links.
    RemoveOwnedLegacyShortcut(Path.Combine(desktop,"Mori Desk.lnk"));
    RemoveOwnedLegacyShortcut(Path.Combine(programs,"Mori Desk","Mori Desk.lnk"));
    if(MessageBox.Show("安装完成。桌面已创建 Nook 快捷方式。\n\n升级前后都请确保旧 Mori Desk / Nook 已完全退出，否则可能打开旧窗口。独立 Mori / Luma 仍在运行时，F1 / F3 等快捷键可能被占用，可先用新软件的“工具”菜单体验。\n\n如原来开启了自启动，请在新版设置中重新勾选并保存。\n\n现在打开吗？","Nook",MessageBoxButtons.YesNo,MessageBoxIcon.Information)==DialogResult.Yes){if(HasRunningMoriDesk()){MessageBox.Show("软件已安装，但 Mori Desk / Nook 又被打开了。请完成收藏并完全退出已有窗口后，再通过新版桌面快捷方式打开。","暂未打开新版",MessageBoxButtons.OK,MessageBoxIcon.Information);return 0;}Process.Start(exe);}return 0;}catch(Exception ex){MessageBox.Show("安装未完成，素材、贴图和设置未改动。\n若新版快捷方式已创建，修正下面的问题后可重新安装。\n"+ex.Message,"Nook",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;}
  }
 }
}
