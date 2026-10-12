using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Diagnostics;
namespace MoriDeskInstaller {
 static class Program {
  const string Version="0.1.1-preview";
  static string Extract(string folder){Directory.CreateDirectory(folder);string prefix=Path.GetFullPath(folder).TrimEnd('\\')+"\\";using(var source=Assembly.GetExecutingAssembly().GetManifestResourceStream("MoriDesk.Payload.zip"))using(var zip=new ZipArchive(source,ZipArchiveMode.Read)){foreach(var entry in zip.Entries){string file=Path.GetFullPath(Path.Combine(prefix,entry.FullName));if(!file.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("安装包路径异常");if(string.IsNullOrEmpty(entry.Name)){Directory.CreateDirectory(file);continue;}Directory.CreateDirectory(Path.GetDirectoryName(file));using(var input=entry.Open())using(var output=new FileStream(file,FileMode.CreateNew,FileAccess.Write))input.CopyTo(output);}}string exe=Path.Combine(folder,"Mori.exe");if(!File.Exists(exe))throw new InvalidDataException("安装包缺少程序");return exe;}
  static void Shortcut(string location,string exe){object shell=null,link=null;try{shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));link=((dynamic)shell).CreateShortcut(location);dynamic shortcut=link;shortcut.TargetPath=exe;shortcut.WorkingDirectory=Path.GetDirectoryName(exe);shortcut.Description="Mori Desk · 素材、截图、翻译与看图";shortcut.IconLocation=exe+",0";shortcut.Save();}finally{if(link!=null)Marshal.FinalReleaseComObject(link);if(shell!=null)Marshal.FinalReleaseComObject(shell);}}
  [STAThread] static int Main(string[] args){Application.EnableVisualStyles();
   if(args.Length==1&&args[0]=="--verify"){try{string temp=Path.Combine(Path.GetTempPath(),"MoriDesk-install-check-"+Guid.NewGuid().ToString("N"));string exe=Extract(temp);using(var process=Process.Start(new ProcessStartInfo(exe,"--suite-test"){UseShellExecute=false,CreateNoWindow=true})){process.WaitForExit();return process.ExitCode;}}catch{return 1;}}
   if(MessageBox.Show("安装 Mori Desk 测试版？\n\n包含素材库、截图翻译、输入翻译、图片和 3D 查看器。\n使用独立数据目录，不替换旧 Mori / Luma。\n不自动迁移素材，不设置开机启动，不修改默认应用。\n\n3D / 部分视频需要系统已安装 WebView2。\n浏览器插件需在 Chrome / Edge 中手动加载并连接。","Mori Desk",MessageBoxButtons.OKCancel,MessageBoxIcon.Information)!=DialogResult.OK)return 0;
   try{string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","MoriDesk",Version+"-"+DateTime.Now.ToString("yyyyMMddHHmmss"));string exe=Extract(root);Shortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"Mori Desk.lnk"),exe);string start=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),"Programs","Mori Desk");Directory.CreateDirectory(start);Shortcut(Path.Combine(start,"Mori Desk.lnk"),exe);if(MessageBox.Show("安装完成。桌面已创建 Mori Desk 快捷方式。\n\n旧软件仍在运行时，F1 / F3 等快捷键可能被占用。可以先用新软件的“工具”菜单体验。\n\n现在打开吗？","Mori Desk",MessageBoxButtons.YesNo,MessageBoxIcon.Information)==DialogResult.Yes)Process.Start(exe);return 0;}catch(Exception ex){MessageBox.Show("安装未完成，旧软件和数据未改动。\n"+ex.Message,"Mori Desk",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;}
  }
 }
}
