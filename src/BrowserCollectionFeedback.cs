using System;
using System.Diagnostics;
using System.IO;

namespace MoriDesk {
 // A feedback button is an explicit UI activation, never a second save or a
 // page-supplied executable. Resolve the same stable entry as desktop shortcuts.
 internal static class BrowserCollectionFeedback {
  internal static bool CanOpen {get{return string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MORI_DESK_DATA"));}}
  internal static ProcessStartInfo StartInfo(string executable){
   var start=new ProcessStartInfo(executable){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(executable)};
   start.EnvironmentVariables.Remove("NOOK_UPDATE_REQUEST");start.EnvironmentVariables.Remove("NOOK_UPDATE_EXPECTED_VERSION");
   return start;
  }
  internal static object Open(string extensionId){
   BrowserConnection.ValidateId(extensionId);
   PhysicalUserContext.RequireInstalledContext();
   string executable=InstalledApplicationRouting.RegistrationExecutable();
   AppUpdateCoordinator.PlainPath(executable);
   if(!File.Exists(executable))throw new FileNotFoundException("请从任务栏打开 Nook；启动入口缺失时请使用安装器修复。");
   using(var process=Process.Start(StartInfo(executable))){if(process==null)throw new IOException("请从任务栏打开 Nook");}
   return new{ok=true};
  }
 }
}
