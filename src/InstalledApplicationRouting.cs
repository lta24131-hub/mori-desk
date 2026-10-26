using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;

namespace MoriDesk {
 // A payload is not the installed application identity. Only the committed
 // descriptor may select the process that owns shell and browser registration.
 internal static class InstalledApplicationRouting {
  static string candidateSession,candidateFailure;
  static Stopwatch candidateClock;
  internal static bool IsNative(string[] args){return args!=null&&args.Length>0&&args[0].StartsWith("chrome-extension://",StringComparison.Ordinal);}
  internal static bool IsExempt(string[] args,string alternateData){
   if(!string.IsNullOrEmpty(alternateData))return true;
   if(args==null||args.Length==0)return false;string first=args[0];
   return first.StartsWith("--ocr-worker=",StringComparison.Ordinal)||new[]{"--image-codec","--video-thumbnail","--pdf-text-worker","--svg-render","--suite-test","--self-test","--video-self-test","--benchmark","--native-test","--ui-test","--mature-canvas-test","--mature-canvas-preview","--desktop-drop-fixture","--integration-test","--browser-bridge-test","--browser-update-host-test"}.Contains(first);
  }
  internal static bool SamePath(string first,string second){return string.Equals(Path.GetFullPath(first),Path.GetFullPath(second),StringComparison.OrdinalIgnoreCase);}
  internal static bool ManagedPayload(string executable,string root){
   if(string.IsNullOrWhiteSpace(executable)||string.IsNullOrWhiteSpace(root))return false;
   try{string file=Path.GetFullPath(executable);var folder=Directory.GetParent(file);return Path.GetFileName(file).Equals("Mori.exe",StringComparison.OrdinalIgnoreCase)&&folder!=null&&folder.Parent!=null&&SamePath(folder.Parent.FullName,root)&&!folder.Name.Equals("Launcher",StringComparison.OrdinalIgnoreCase);}
   catch(ArgumentException){return false;}catch(NotSupportedException){return false;}catch(PathTooLongException){return false;}
  }
  internal static string ShellInstanceName(string physicalRoot,string sid){
   if(string.IsNullOrWhiteSpace(sid))throw new ArgumentException("用户身份无效。");
   using(var hash=SHA256.Create())return "Global\\Nook.Shell."+BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(sid+"|"+Path.GetFullPath(physicalRoot).TrimEnd('\\','/').ToUpperInvariant()))).Replace("-","");
  }
  internal static string ProductionShellInstanceName(){
   if(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MORI_DESK_DATA")))return null;
   string root=InstalledApplicationState.ProgramRoot;if(!ManagedPayload(typeof(Entry).Assembly.Location,root))return null;
   using(var user=WindowsIdentity.GetCurrent())return ShellInstanceName(PhysicalUserContext.DefaultRoot,user.User.Value);
  }
  static string ReadSmall(string path){
   AppUpdateCoordinator.PlainPath(path);using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){
    if(input.Length<1||input.Length>16384)throw new InvalidDataException("更新启动记录大小无效。");
    using(var reader=new StreamReader(input,new UTF8Encoding(false,true),false))return reader.ReadToEnd();
   }
  }
  internal static void ValidateCandidateRecord(string text,string executable,string expectedVersion,string hash){
   if(string.IsNullOrWhiteSpace(text)||text.Length>16384)throw new InvalidDataException("更新候选记录无效。");
   Dictionary<string,object> value;try{value=InstalledApplicationState.ParseFlatRecord(text);}catch(Exception ex){throw new InvalidDataException("更新候选记录无法读取。",ex);}
   string[] keys={"schemaVersion","expectedVersion","executable","sha256"};
   if(value==null||value.Count!=keys.Length||keys.Any(k=>!value.ContainsKey(k))||!(value["schemaVersion"] is int)||(int)value["schemaVersion"]!=1||!(value["expectedVersion"] is string)||(string)value["expectedVersion"]!=expectedVersion||!(value["executable"] is string)||!SamePath((string)value["executable"],executable)||!(value["sha256"] is string)||((string)value["sha256"]).Length!=64||!string.Equals((string)value["sha256"],hash,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("更新候选程序与本次安装不匹配。");
  }
  static string ValidateCandidate(string root,string executable,string request,string expected){
   PhysicalUserContext.RequireInstalledContext();
   if(string.IsNullOrEmpty(request)||string.IsNullOrEmpty(expected)||expected!=AppUpdateCoordinator.VersionText(typeof(Entry).Assembly.GetName().Version))throw new InvalidDataException("更新启动参数不完整或版本不匹配。");
   string session=AppUpdateCoordinator.StartupSession(request,Path.Combine(PhysicalUserContext.DefaultRoot,"updates"));
   AppUpdateCoordinator.ValidateStartupRequest(ReadSmall(request),expected,root);
   AppUpdateCoordinator.ReadRecord(ReadSmall(Path.Combine(session,"ready.json")),typeof(Entry).Assembly.GetName().Version,"ready");
   foreach(string name in new[]{"cancel.request","result.json","startup-ready.json"})if(File.Exists(Path.Combine(session,name)))throw new InvalidDataException("更新启动请求已完成或取消。");
   AppUpdateCoordinator.PlainPath(executable);string hash;
   using(var file=new FileStream(executable,FileMode.Open,FileAccess.Read,FileShare.Read))using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");
   ValidateCandidateRecord(ReadSmall(Path.Combine(session,"candidate.json")),executable,expected,hash);return session;
  }
  internal static bool TryRoute(string[] args,out int exitCode){
   exitCode=0;string request=Environment.GetEnvironmentVariable("NOOK_UPDATE_REQUEST"),expected=Environment.GetEnvironmentVariable("NOOK_UPDATE_EXPECTED_VERSION");
   // A candidate request takes precedence over portable/test exemptions. An
   // inherited isolated-data override must never turn a candidate into an
   // ordinary shell which reports success against a different library.
   if(!string.IsNullOrEmpty(request)||!string.IsNullOrEmpty(expected))PhysicalUserContext.RequireInstalledContext();
   if(IsExempt(args,Environment.GetEnvironmentVariable("MORI_DESK_DATA")))return false;
   string root=InstalledApplicationState.ProgramRoot,executable=typeof(Entry).Assembly.Location;
   if(!ManagedPayload(executable,root))return false; // portable and developer builds never claim the installed identity
   if(!string.IsNullOrEmpty(request)||!string.IsNullOrEmpty(expected)){
    if(IsNative(args)||args.Length!=0)throw new InvalidDataException("更新候选程序只能启动主窗口。");
    candidateSession=ValidateCandidate(root,executable,request,expected);candidateClock=Stopwatch.StartNew();return false;
   }
   var active=InstalledApplicationState.Read(root);if(active==null||SamePath(InstalledApplicationState.ResolveExecutable(root,active),executable))return false;
   if(IsNative(args)){WriteNativeFailure(Console.OpenStandardOutput());exitCode=1;return true;}
   string launcher=InstalledApplicationState.LauncherPath(root);AppUpdateCoordinator.PlainPath(launcher);
   if(!File.Exists(launcher))throw new FileNotFoundException("固定启动入口缺失，请运行安装器修复。",launcher);
   var start=new ProcessStartInfo(launcher,InstalledApplicationLauncher.Arguments(args)){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(launcher)};
   start.EnvironmentVariables.Remove("NOOK_UPDATE_REQUEST");start.EnvironmentVariables.Remove("NOOK_UPDATE_EXPECTED_VERSION");
   using(var process=Process.Start(start)){if(process==null)throw new IOException("无法打开当前 Nook。");}return true;
  }
  internal static void WriteNativeFailure(Stream output){
   byte[] body=Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new{ok=false,errorCode="stale-installation",error="此浏览器连接指向已替换的 Nook，未执行收藏。请使用当前 Nook 修复浏览器连接。"}));
   output.Write(BitConverter.GetBytes(body.Length),0,4);output.Write(body,0,body.Length);output.Flush();
  }
  // The installer holds the installation lease until startup succeeds. Never
  // block that candidate on the same lease while the installer waits for it.
  internal static bool IsPendingCandidate {
   get{
    if(candidateSession==null)return false;
    if(candidateFailure!=null)throw new IOException(candidateFailure);
    string root=InstalledApplicationState.ProgramRoot;
    bool leaseAvailable=false;
    try{using(InstalledApplicationState.AcquireLease(root,0)){leaseAvailable=true;var active=InstalledApplicationState.Read(root);if(active!=null&&SamePath(InstalledApplicationState.ResolveExecutable(root,active),typeof(Entry).Assembly.Location)){candidateSession=null;candidateClock=null;return false;}}}
    catch(IOException){}
    try{
     bool cancelled=File.Exists(Path.Combine(candidateSession,"cancel.request"));string status=null,path=Path.Combine(candidateSession,"result.json");
     if(File.Exists(path))status=(string)AppUpdateCoordinator.ReadRecord(ReadSmall(path),typeof(Entry).Assembly.GetName().Version,null)["status"];
     candidateFailure=CandidateFailure(cancelled,status,leaseAvailable,candidateClock==null?0:candidateClock.ElapsedMilliseconds);
    }catch(Exception ex){candidateFailure="无法核对更新结果，已停止启动连接和后台服务。"+ex.Message;}
    if(candidateFailure!=null)throw new IOException(candidateFailure);
    return true;
   }
  }
  internal static string CandidateFailure(bool cancelled,string resultStatus,bool leaseAvailable,long elapsedMilliseconds){
   if(cancelled||resultStatus=="cancelled"||resultStatus=="error")return "本次更新已取消或未完成，尚未接管连接和后台服务。请从原启动入口重新打开 Nook。";
   if(leaseAvailable&&resultStatus=="installed")return "安装结果与当前版本记录不一致，已停止启动连接和后台服务。请运行安装器修复。";
   // A candidate is born only while its installer owns this lease. If the
   // lease is free but the committed pointer still is not this executable,
   // that transaction has ended/crashed: release the shell immediately so
   // the stable launcher can open the previous working application again.
   if(leaseAvailable)return "更新尚未提交便已中断，已停止候选窗口和后台服务。请从原启动入口重新打开 Nook。";
   if(elapsedMilliseconds>=120000)return "更新确认超时，尚未接管连接和后台服务。请从原启动入口重新打开 Nook。";
   return null;
  }
  internal static string RegistrationExecutable(){
   PhysicalUserContext.RequireInstalledContext();if(IsPendingCandidate)throw new InvalidOperationException("Nook 正在完成更新，连接会在更新完成后恢复。");
   return RegistrationExecutableInScope(InstalledApplicationState.ProgramRoot,typeof(Entry).Assembly.Location);
  }
  internal static string RegistrationExecutableInScope(string root,string executable){
   if(!Directory.Exists(root))return executable; // portable first use has no installed tree
   var active=InstalledApplicationState.Read(root);
   if(active==null)return executable; // legacy installs keep their prior registration contract until migration
   if(!SamePath(InstalledApplicationState.ResolveExecutable(root,active),executable))throw new InvalidOperationException("旧版 Nook 不能修改当前安装的连接，请从桌面打开新版。");
   return InstalledApplicationState.LauncherPath(root);
  }
 }
}
