using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;

namespace MoriDesk {
 internal sealed class AppUpdateRegistration {
  internal Func<bool> Preview;
  internal Func<string,Func<bool>,CancellationToken,Task<bool>> Exit;
 }
 // The only executable handoff entry point. Discovery/download services cannot
 // launch installers; the settings UI must explicitly call BeginAsync.
 internal static class AppUpdateCoordinator {
  static readonly Dictionary<Window,AppUpdateRegistration> windows=new Dictionary<Window,AppUpdateRegistration>();
  internal const int PreparationTimeoutMilliseconds=15*60*1000;
  internal const int PreparationPollMilliseconds=200;
  static int active;
  internal static void Register(Window main,Func<bool> preview,Func<string,Func<bool>,CancellationToken,Task<bool>> exit){
   windows[main]=new AppUpdateRegistration{Preview=preview,Exit=exit};main.Closed+=(s,e)=>windows.Remove(main);
  }
  internal static bool IsActive {get{return Interlocked.CompareExchange(ref active,0,0)!=0;}}
  internal static string VersionText(Version version){
   if(version==null||version.Major<0||version.Minor<0||version.Build<0)throw new ArgumentException("更新版本号无效。");
   return version.Revision>0?version.ToString(4):version.ToString(3);
  }
  internal static string RequestJson(int processId,string executable,long startedTicks,Version version){
   if(processId<=0||startedTicks<=DateTime.MinValue.Ticks||startedTicks>DateTime.MaxValue.Ticks)throw new ArgumentException("更新父进程标识无效。");
   RequireLocalFileName(executable,"Mori.exe");
   return Json().Serialize(new{schemaVersion=1,parentProcessId=processId,parentExecutable=Path.GetFullPath(executable),parentStartedUtcTicks=startedTicks,expectedVersion=VersionText(version),relaunch=true});
  }
  internal static string LaunchArguments(string requestPath){
   RequireLocalFileName(requestPath,"request.json");return "--update-request \""+Path.GetFullPath(requestPath)+"\"";
  }
  internal static void RequireLocalFileName(string path,string name){
   if(string.IsNullOrWhiteSpace(path)||path.Length<4||!((path[0]>='A'&&path[0]<='Z')||(path[0]>='a'&&path[0]<='z'))||path[1]!=':'||(path[2]!='\\'&&path[2]!='/')||path.IndexOf('"')>=0||path.IndexOf('\r')>=0||path.IndexOf('\n')>=0||path.IndexOf('\0')>=0||path.IndexOf(':',2)>=0||!string.Equals(Path.GetFileName(path),name,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("更新文件路径无效。");
  }
  internal static string StartupSession(string requestPath,string updatesRoot){
   RequireLocalFileName(requestPath,"request.json");string path=Path.GetFullPath(requestPath),root=Path.GetFullPath(updatesRoot).TrimEnd('\\','/'),session=Path.GetDirectoryName(path);Guid id;
   if(!string.Equals(Path.GetDirectoryName(session),root,StringComparison.OrdinalIgnoreCase)||!Guid.TryParseExact(Path.GetFileName(session),"N",out id))throw new InvalidDataException("新版启动确认目录不属于本次更新。");return session;
  }
  internal static bool ManagedStartupExecutable(string executable,string programRoot){
   RequireLocalFileName(executable,"Mori.exe");return Path.GetFullPath(executable).StartsWith(Path.GetFullPath(programRoot).TrimEnd('\\','/')+"\\",StringComparison.OrdinalIgnoreCase);
  }
  internal static void ValidateStartupRequest(string text,string expectedVersion,string programRoot){
   if(string.IsNullOrWhiteSpace(text)||text.Length>16384)throw new InvalidDataException("新版启动请求无效。");
   Dictionary<string,object> data;try{data=Json().Deserialize<Dictionary<string,object>>(text);}catch(Exception error){throw new InvalidDataException("新版启动请求无法读取。",error);}
   string[] keys={"schemaVersion","parentProcessId","parentExecutable","parentStartedUtcTicks","expectedVersion","relaunch"};
   if(data==null||data.Count!=keys.Length)throw new InvalidDataException("新版启动请求字段不完整。");foreach(string key in keys)if(!data.ContainsKey(key))throw new InvalidDataException("新版启动请求字段无效。");
   object ticks=data["parentStartedUtcTicks"];long started;
   if(!(data["schemaVersion"] is int)||(int)data["schemaVersion"]!=1||!(data["parentProcessId"] is int)||(int)data["parentProcessId"]<=0||!(ticks is int||ticks is long)||!long.TryParse(Convert.ToString(ticks,CultureInfo.InvariantCulture),NumberStyles.None,CultureInfo.InvariantCulture,out started)||started<=0||started>DateTime.MaxValue.Ticks||!(data["expectedVersion"] is string)||(string)data["expectedVersion"]!=expectedVersion||!(data["relaunch"] is bool)||!(bool)data["relaunch"]||!ManagedStartupExecutable(data["parentExecutable"] as string,programRoot))throw new InvalidDataException("新版启动请求身份或版本不符。");
  }
  internal static string StartupReadyJson(int processId,string executable,long startedTicks,Version version,string programRoot){
   if(processId<=0||startedTicks<=0||startedTicks>DateTime.MaxValue.Ticks||!ManagedStartupExecutable(executable,programRoot))throw new InvalidDataException("新版启动进程身份无效。");
   return Json().Serialize(new{schemaVersion=1,expectedVersion=VersionText(version),processId=processId,startedUtcTicks=startedTicks,executable=Path.GetFullPath(executable)});
  }
  // Called only by the real library main window's successful ContentRendered.
  // A surviving startup error dialog must never count as a healthy upgrade.
  internal static bool ReportStartupReady(Window main){
   string requestPath=Environment.GetEnvironmentVariable("NOOK_UPDATE_REQUEST"),expectedVersion=Environment.GetEnvironmentVariable("NOOK_UPDATE_EXPECTED_VERSION");
   if(string.IsNullOrEmpty(requestPath)&&string.IsNullOrEmpty(expectedVersion))return false;
   Environment.SetEnvironmentVariable("NOOK_UPDATE_REQUEST",null);Environment.SetEnvironmentVariable("NOOK_UPDATE_EXPECTED_VERSION",null);
   try{
    PhysicalUserContext.RequireInstalledContext();
    AppUpdateRegistration registration;
    if(main==null||!main.Dispatcher.CheckAccess()||!main.IsLoaded||!main.IsVisible||!windows.TryGetValue(main,out registration)||registration.Preview()||!Entry.InteractiveShell)return false;
    Version current=typeof(Entry).Assembly.GetName().Version;if(expectedVersion!=VersionText(current))return false;
    string local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),programRoot=Path.Combine(local,"Programs","MoriDesk"),session=StartupSession(requestPath,Path.Combine(local,"MoriDesk","updates"));
    PlainPath(requestPath);string request=ReadSessionRecord(session,"request.json");ValidateStartupRequest(request,expectedVersion,programRoot);
    string ready=ReadSessionRecord(session,"ready.json");ReadRecord(ready,current,"ready");
    if(File.Exists(Path.Combine(session,"cancel.request"))||File.Exists(Path.Combine(session,"result.json"))||File.Exists(Path.Combine(session,"startup-ready.json")))return false;
    using(var process=Process.GetCurrentProcess()){
     var module=process.MainModule;if(module==null||!ManagedStartupExecutable(module.FileName,programRoot)||!string.Equals(module.FileVersionInfo.ProductName,"Nook",StringComparison.Ordinal)||!string.Equals(module.FileVersionInfo.OriginalFilename,"Mori.exe",StringComparison.OrdinalIgnoreCase))return false;
     string receipt=StartupReadyJson(process.Id,module.FileName,process.StartTime.ToUniversalTime().Ticks,current,programRoot),temporary=Path.Combine(session,"startup-ready."+Guid.NewGuid().ToString("N")+".tmp");
     try{WriteNew(temporary,receipt);PlainPath(session);File.Move(temporary,Path.Combine(session,"startup-ready.json"));}
     finally{if(File.Exists(temporary)){PlainPath(temporary);File.Delete(temporary);}}
    }
    return true;
   }catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}catch(ArgumentException){return false;}catch(InvalidOperationException){return false;}catch(System.ComponentModel.Win32Exception){return false;}catch(System.Security.SecurityException){return false;}catch(NotSupportedException){return false;}
  }
  internal static Dictionary<string,object> ReadRecord(string text,Version expectedVersion,string expectedStatus){
   if(string.IsNullOrWhiteSpace(text)||text.Length>16384)throw new InvalidDataException("更新器返回的状态无效。");
   Dictionary<string,object> record;try{record=Json().Deserialize<Dictionary<string,object>>(text);}catch(Exception error){throw new InvalidDataException("更新器状态格式无效。",error);}
   object schema,status,version;
   if(record==null||!record.TryGetValue("schemaVersion",out schema)||!(schema is int)||(int)schema!=1||!record.TryGetValue("status",out status)||!(status is string)||!record.TryGetValue("expectedVersion",out version)||!(version is string)||!string.Equals((string)version,VersionText(expectedVersion),StringComparison.Ordinal))throw new InvalidDataException("更新器状态与本次版本不符。");
   string state=(string)status;
   if(expectedStatus!=null?state!=expectedStatus:state!="installed"&&state!="cancelled"&&state!="error")throw new InvalidDataException("更新器返回了未知状态。");
   return record;
  }
  static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=4*1024*1024,RecursionLimit=24};}
  internal static void PlainPath(string path){
   for(string part=Path.GetFullPath(path);!string.IsNullOrEmpty(part);part=Path.GetDirectoryName(part)){
    FileAttributes attributes;try{attributes=File.GetAttributes(part);}catch(FileNotFoundException){continue;}catch(DirectoryNotFoundException){continue;}
    if((attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("更新目录不能包含链接或重定向路径。");
    if(Path.GetPathRoot(part)==part)break;
   }
  }
  static string ReadSessionRecord(string session,string name){
   PlainPath(session);string file=Path.Combine(session,name);if(!File.Exists(file))return null;PlainPath(file);
   if(new FileInfo(file).Length>65536)throw new InvalidDataException("更新器状态文件过大。");return File.ReadAllText(file,Encoding.UTF8);
  }
  static void WriteNew(string path,string text){
   PlainPath(path);byte[] bytes=new UTF8Encoding(false).GetBytes(text);
   using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read)){file.Write(bytes,0,bytes.Length);file.Flush(true);}
  }
  static void Cancel(string session){try{if(Directory.Exists(session)&&!File.Exists(Path.Combine(session,"cancel.request")))WriteNew(Path.Combine(session,"cancel.request"),"cancelled");}catch(IOException){}catch(UnauthorizedAccessException){}catch(InvalidDataException){}}
  internal static async Task<string> BeginAsync(Window owner,string verifiedInstallerPath,Version expectedVersion,string expectedSha256,CancellationToken cancellationToken){
   PhysicalUserContext.RequireInstalledContext();
   if(owner==null||!owner.Dispatcher.CheckAccess())throw new InvalidOperationException("请从 Nook 设置中点击安装更新。");
   AppUpdateRegistration main=null;
   for(Window current=owner;current!=null;current=current.Owner)if(windows.TryGetValue(current,out main))break;
   if(main==null||main.Preview()||!Entry.InteractiveShell)throw new InvalidOperationException("测试预览窗口不能安装更新，请在正式版设置中操作。");
   if(Interlocked.CompareExchange(ref active,1,0)!=0)return "已有更新正在准备，请等待完成或先取消。";
   string session=null;Process installer=null;bool handedOff=false;
   try{
    cancellationToken.ThrowIfCancellationRequested();VersionText(expectedVersion);RequireLocalFileName(verifiedInstallerPath,AppUpdateService.InstallerName);PlainPath(verifiedInstallerPath);
    if(AppUpdateService.NumericVersion(expectedVersion)<=AppUpdateService.NumericVersion(typeof(Entry).Assembly.GetName().Version))throw new InvalidDataException("更新包版本没有高于当前版本，未执行重装或降级。");
    if(expectedSha256==null||expectedSha256.Length!=64||expectedSha256.Any(c=>!Uri.IsHexDigit(c)))throw new InvalidDataException("缺少可靠的安装包校验值，请重新检查更新。");
    var info=new FileInfo(verifiedInstallerPath);if(!info.Exists||info.Length<1||info.Length>AppUpdateService.MaxInstallerBytes)throw new InvalidDataException("安装包缺失或大小无效，请重新下载。");
    // Holding a read-only share prevents a stale cache executable from being
    // replaced between the last digest check and process creation.
    using(var locked=new FileStream(verifiedInstallerPath,FileMode.Open,FileAccess.Read,FileShare.Read)){
     string actual=await Task.Run(()=>{using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(locked)).Replace("-","");},cancellationToken);
     cancellationToken.ThrowIfCancellationRequested();
     if(!string.Equals(actual,expectedSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("安装包在下载后发生变化，未启动更新。请重新下载。");
     var identity=FileVersionInfo.GetVersionInfo(verifiedInstallerPath);
     if(!InstallerIdentity(identity.ProductName,identity.OriginalFilename,identity.FileVersion,expectedVersion))throw new InvalidDataException("安装包版本或产品身份不符，未启动更新。");
     string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MoriDesk","updates");PlainPath(root);Directory.CreateDirectory(root);PlainPath(root);
     session=Path.Combine(root,Guid.NewGuid().ToString("N"));if(Directory.Exists(session)||File.Exists(session))throw new IOException("更新会话已存在，请重试。");Directory.CreateDirectory(session);PlainPath(session);
     using(var parent=Process.GetCurrentProcess()){
      string request=RequestJson(parent.Id,parent.MainModule.FileName,parent.StartTime.ToUniversalTime().Ticks,expectedVersion);string requestPath=Path.Combine(session,"request.json");WriteNew(requestPath,request);
      var start=new ProcessStartInfo{FileName=verifiedInstallerPath,Arguments=LaunchArguments(requestPath),WorkingDirectory=Path.GetDirectoryName(verifiedInstallerPath),UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
      cancellationToken.ThrowIfCancellationRequested();installer=new Process{StartInfo=start};if(!installer.Start())throw new IOException("更新器未能启动，旧版保持运行。");
     }
    }
    var clock=Stopwatch.StartNew();
    // Package extraction happens before ready. It must not prematurely close
    // the app or be mistaken for a failed 30-second graceful shutdown.
    handedOff=await RunHandoffAsync(token=>WaitForPreparationAsync(
     ()=>ReadSessionRecord(session,"result.json"),()=>ReadSessionRecord(session,"ready.json"),
     ()=>installer.HasExited,()=>clock.ElapsedMilliseconds,(milliseconds,pollToken)=>Task.Delay(milliseconds,pollToken),expectedVersion,token),async token=>{
     // The normal exit pipeline invokes this guard only after inspector,
     // canvases and pin metadata have saved. A veto never forces shutdown.
     using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(token)){
      deadline.CancelAfter(TimeSpan.FromSeconds(25));
      return await main.Exit(Path.Combine(session,"metadata-backup"),()=>!deadline.IsCancellationRequested&&!installer.HasExited&&ReadSessionRecord(session,"result.json")==null,deadline.Token);
     }
    },()=>Cancel(session),cancellationToken);
    if(!handedOff){Cancel(session);return "更新已取消或退出前保存未完成；旧版保持运行，原文件未替换。";}
    return "已正常退出，更新器将完成安装并重新打开 Nook；新版启动确认后自动清理已核对的旧程序，素材和设置保留。";
   }catch(OperationCanceledException){if(session!=null)Cancel(session);return "已取消更新，旧版保持运行。";}
   catch{if(session!=null&&!handedOff)Cancel(session);throw;}
   finally{if(installer!=null)installer.Dispose();Interlocked.Exchange(ref active,0);}
  }
  // Pure polling seam: slow disks may need several minutes to extract and
  // verify the package. This bounded preparation phase never exits the app;
  // normal save/exit still has its separate 25-second deadline above.
  internal static async Task WaitForPreparationAsync(Func<string> readTerminal,Func<string> readReady,Func<bool> installerExited,Func<long> elapsedMilliseconds,Func<int,CancellationToken,Task> delay,Version expectedVersion,CancellationToken token){
   if(readTerminal==null||readReady==null||installerExited==null||elapsedMilliseconds==null||delay==null)throw new ArgumentNullException();
   VersionText(expectedVersion);
   while(true){
    long elapsed=RequirePreparationActive(readTerminal,installerExited,elapsedMilliseconds,expectedVersion,token);
    string ready=readReady();
    if(ready!=null){
     ReadRecord(ready,expectedVersion,"ready");
     // A terminal result/exit/cancel arriving with ready must not authorize
     // shutdown. The normal exit pipeline repeats its own liveness guard.
     RequirePreparationActive(readTerminal,installerExited,elapsedMilliseconds,expectedVersion,token);return;
    }
    await delay((int)Math.Min(PreparationPollMilliseconds,PreparationTimeoutMilliseconds-elapsed),token);
   }
  }
  static long RequirePreparationActive(Func<string> readTerminal,Func<bool> installerExited,Func<long> elapsedMilliseconds,Version expectedVersion,CancellationToken token){
   token.ThrowIfCancellationRequested();
   string terminal=readTerminal();if(terminal!=null){var record=ReadRecord(terminal,expectedVersion,null);throw new IOException("更新未开始："+RecordMessage(record));}
   if(installerExited())throw new IOException("更新器提前退出，旧版保持运行。");
   long elapsed=elapsedMilliseconds();if(elapsed<0)throw new InvalidOperationException("无法确认更新准备时间，旧版保持运行。");
   if(elapsed>=PreparationTimeoutMilliseconds)throw new TimeoutException("准备更新超过 15 分钟，旧版保持运行。请稍后重试。");
   return elapsed;
  }
  // A process/filesystem-free transport seam used by background tests. There is
  // no exit request until readiness is verified; every veto/failure cancels.
  internal static async Task<bool> RunHandoffAsync(Func<CancellationToken,Task> waitReady,Func<CancellationToken,Task<bool>> normalExit,Action cancel,CancellationToken token){
   bool completed=false;
   try{token.ThrowIfCancellationRequested();await waitReady(token);token.ThrowIfCancellationRequested();completed=await normalExit(token);return completed;}
   finally{if(!completed)cancel();}
  }
  static string RecordMessage(Dictionary<string,object> record){object value;return record.TryGetValue("message",out value)&&value is string?((string)value).Substring(0,Math.Min(2048,((string)value).Length)):"旧版保持运行。";}
  internal static bool InstallerIdentity(string product,string filename,string fileVersion,Version expected){
   Version parsed;return string.Equals(product,"Nook Installer",StringComparison.Ordinal)&&string.Equals(filename,"Nook-Setup.exe",StringComparison.OrdinalIgnoreCase)&&Version.TryParse(fileVersion,out parsed)&&AppUpdateService.NumericVersion(parsed)==AppUpdateService.NumericVersion(expected);
  }
 }

}
