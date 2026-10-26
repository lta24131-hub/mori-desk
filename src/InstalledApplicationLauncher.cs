using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MoriDesk {
 // Stable launcher protocol v1: this bootstrap only resolves committed state
 // and forwards arguments/standard handles. Normal updates replace payloads,
 // not this binary. Even a compatible launcher fix requires an explicit,
 // verified migration; it must never overwrite an unknown/running launcher.
 public static class InstalledApplicationLauncher {
  public const int ProtocolVersion=1;
  internal static string Quote(string value){
   if(value==null||value.IndexOf('\0')>=0)throw new ArgumentException("启动参数无效。");var text=new StringBuilder("\"");int slashes=0;
   foreach(char c in value){if(c=='\\'){slashes++;continue;}if(c=='"'){text.Append('\\',slashes*2+1);text.Append('"');slashes=0;continue;}text.Append('\\',slashes);slashes=0;text.Append(c);}text.Append('\\',slashes*2);return text.Append('"').ToString();
  }
  internal static string Arguments(string[] args){if(args==null)throw new ArgumentNullException("args");return string.Join(" ",args.Select(Quote));}
  internal static bool Native(string[] args){return args!=null&&args.Length>0&&args[0].StartsWith("chrome-extension://",StringComparison.Ordinal);}
  internal static void WriteNativeBusy(Stream output){
   byte[] body=Encoding.UTF8.GetBytes("{\"ok\":false,\"code\":\"APP_UPDATE_IN_PROGRESS\",\"error\":\"Nook 正在更新，本次没有执行收藏；更新完成后请重试。\"}");
   output.Write(BitConverter.GetBytes(body.Length),0,4);output.Write(body,0,body.Length);output.Flush();
  }
  internal static void Pump(Stream source,Stream destination){
   var buffer=new byte[8192];int count;
   // Process.StandardInput.BaseStream is buffered even though its parent
   // StreamWriter has AutoFlush=true. Writing BaseStream bypasses that writer.
   // Chrome keeps stdin open while awaiting a reply, so CopyTo + EOF-only
   // flushing can deadlock on a small native request. Flush every received
   // chunk in both directions; preserve arbitrary bytes and native framing.
   while((count=source.Read(buffer,0,buffer.Length))>0){destination.Write(buffer,0,count);destination.Flush();}
  }
  internal static int Run(string root,string[] args,Action requireContext){
   if(requireContext==null)throw new ArgumentNullException("requireContext");requireContext();bool native=Native(args);Process process;
   InstalledApplicationState.LaunchTarget selected;
   try{selected=InstalledApplicationState.OpenActive(root,native?0:30000);}
   catch(BusyInstallationException){
    if(!native)throw;
    // No input has been read or child created. This is a definite refusal,
    // never an ambiguous timeout and never permission to replay a collection.
    using(var output=Console.OpenStandardOutput())WriteNativeBusy(output);return 1;
   }
   using(var target=selected){
    var start=new ProcessStartInfo(target.Executable,Arguments(args)){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(target.Executable),CreateNoWindow=native,RedirectStandardInput=native,RedirectStandardOutput=native,RedirectStandardError=native};
    // Inheriting .NET standard handles can keep an extra pipe writer alive and
    // lose browser EOF. Explicit byte pumps keep framing unchanged and close
    // the child input when Chrome closes its side; never use text readers.
    process=Process.Start(start);if(process==null)throw new IOException("Nook 未能启动。");
   }
   using(process){
    if(!native)return 0;
    var input=Task.Run(()=>{try{Pump(Console.OpenStandardInput(),process.StandardInput.BaseStream);}catch(IOException){}catch(ObjectDisposedException){}finally{try{process.StandardInput.Close();}catch(IOException){}catch(ObjectDisposedException){}}});
    var output=Task.Run(()=>{using(var stream=Console.OpenStandardOutput()){Pump(process.StandardOutput.BaseStream,stream);stream.Flush();}});
    var error=Task.Run(()=>{using(var stream=Console.OpenStandardError()){Pump(process.StandardError.BaseStream,stream);stream.Flush();}});
    // A child can terminate before the browser closes its input. Do not wait
    // for a blocked input reader after the child exit; that task is background.
    input.ContinueWith(failed=>{var observed=failed.Exception;},TaskContinuationOptions.OnlyOnFaulted);
    process.WaitForExit();Task.WaitAll(output,error);return process.ExitCode;
   }
  }
  [STAThread] public static int Main(string[] args){
   try{return Run(InstalledApplicationState.ProgramRoot,args,()=>{
    PhysicalUserContext.RequireDefault();if(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MORI_DESK_DATA")))throw new InvalidOperationException("固定启动入口不能使用测试数据目录。");
    if(!string.Equals(Path.GetFullPath(Assembly.GetExecutingAssembly().Location),InstalledApplicationState.LauncherPath(InstalledApplicationState.ProgramRoot),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("启动入口位置不正确，请运行 Nook 安装器修复。");
   });}
   catch(BusyInstallationException){if(!Native(args))MessageBox.Show("Nook 正在更新，请等待更新完成后再打开。\n本次没有打开旧版，也没有改动素材和设置。","Nook · 正在更新",MessageBoxButtons.OK,MessageBoxIcon.Information);return 1;}
   catch(Exception){if(!Native(args))MessageBox.Show("Nook 的安装状态无法确认，未打开旧版或其他图库。请运行安装器修复；现有素材和设置不会被删除。","Nook · 无法启动",MessageBoxButtons.OK,MessageBoxIcon.Warning);return 1;}
  }
 }
}
