using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;
[assembly: AssemblyTitle("Nook Installer")]
[assembly: AssemblyProduct("Nook Installer")]
[assembly: AssemblyVersion("0.1.24.0")]
[assembly: AssemblyFileVersion("0.1.24.0")]
namespace MoriDeskInstaller {
 internal sealed class UpdateRequest {
  internal string RequestPath,SessionDirectory,ParentExecutable,ExpectedVersion;
  internal int ParentProcessId;internal long ParentStartedUtcTicks;internal bool Relaunch;
 }
 internal sealed class UpdateCancelledException : IOException {
  internal UpdateCancelledException(string message):base(message){}
  internal UpdateCancelledException(string message,Exception inner):base(message,inner){}
 }
 static class Program {
  const string Version="0.1.24";
  internal static string InstallerVersion {get{return Version;}}
  static string ProgramRoot {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","MoriDesk");}}
  static string UpdatesRoot {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MoriDesk","updates");}}
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
  // This update protocol accepts local data, never URLs, shell commands or an
  // installation destination. All new programs are installed side by side.
  internal static bool LocalAbsolutePath(string value){return !string.IsNullOrWhiteSpace(value)&&value.Length>=4&&((value[0]>='A'&&value[0]<='Z')||(value[0]>='a'&&value[0]<='z'))&&value[1]==':'&&(value[2]=='\\'||value[2]=='/')&&value.Substring(2).IndexOf(':')<0&&value.IndexOfAny(new[]{'"','\r','\n','\0'})<0;}
  internal static string UpdateSession(string requestPath,string updatesRoot){
   if(!LocalAbsolutePath(requestPath)||!LocalAbsolutePath(updatesRoot))throw new InvalidDataException("更新请求必须位于本机更新目录。");
   string path=Path.GetFullPath(requestPath),root=Path.GetFullPath(updatesRoot).TrimEnd('\\','/'),folder=Path.GetDirectoryName(path);Guid session;
   if(!string.Equals(Path.GetFileName(path),"request.json",StringComparison.OrdinalIgnoreCase)||!string.Equals(Path.GetDirectoryName(folder),root,StringComparison.OrdinalIgnoreCase)||!Guid.TryParseExact(Path.GetFileName(folder),"N",out session))throw new InvalidDataException("更新请求目录无效。");
   return folder;
  }
  internal static UpdateRequest ParseUpdateRequest(string text,string requestPath,string updatesRoot,string programRoot,int installerId){
   string folder=UpdateSession(requestPath,updatesRoot);
   if(text==null||text.Length>16384)throw new InvalidDataException("更新请求过大或为空。");
   Dictionary<string,object> data;try{data=new JavaScriptSerializer{MaxJsonLength=16384,RecursionLimit=8}.Deserialize<Dictionary<string,object>>(text);}catch(Exception error){throw new InvalidDataException("更新请求无法读取。",error);}
   string[] keys={"schemaVersion","parentProcessId","parentExecutable","parentStartedUtcTicks","expectedVersion","relaunch"};
   if(data==null||data.Count!=keys.Length)throw new InvalidDataException("更新请求字段不完整。");foreach(string key in keys)if(!data.ContainsKey(key))throw new InvalidDataException("更新请求字段无效。");
   if(!(data["schemaVersion"] is int)||(int)data["schemaVersion"]!=1||!(data["parentProcessId"] is int)||(int)data["parentProcessId"]<=0||(int)data["parentProcessId"]==installerId)throw new InvalidDataException("更新请求进程无效。");
   object ticks=data["parentStartedUtcTicks"];long started;if(!(ticks is long||ticks is int)||!long.TryParse(Convert.ToString(ticks,CultureInfo.InvariantCulture),NumberStyles.None,CultureInfo.InvariantCulture,out started)||started<=DateTime.MinValue.Ticks||started>DateTime.MaxValue.Ticks)throw new InvalidDataException("更新请求进程时间无效。");
   string parent=data["parentExecutable"] as string;
   if(!LocalAbsolutePath(parent)||!ManagedTarget(parent,programRoot,"Nook","Mori.exe"))throw new InvalidDataException("更新只能由已安装的 Nook 发起。");
   if(!(data["expectedVersion"] is string)||(string)data["expectedVersion"]!=Version||!(data["relaunch"] is bool)||!(bool)data["relaunch"])throw new InvalidDataException("更新版本或启动选项无效。");
   return new UpdateRequest{RequestPath=Path.GetFullPath(requestPath),SessionDirectory=folder,ParentProcessId=(int)data["parentProcessId"],ParentExecutable=Path.GetFullPath(parent),ParentStartedUtcTicks=started,ExpectedVersion=Version,Relaunch=true};
  }
  internal static bool MatchesUpdateParent(UpdateRequest request,int processId,int sessionId,int installerSession,string executable,string product,string original,long startedUtcTicks){
   return request!=null&&processId==request.ParentProcessId&&sessionId==installerSession&&startedUtcTicks==request.ParentStartedUtcTicks&&ProductIdentity(product,original)&&LocalAbsolutePath(executable)&&string.Equals(Path.GetFullPath(executable),request.ParentExecutable,StringComparison.OrdinalIgnoreCase);
  }
  internal static bool OwnStartup(string command,string oldExecutable){return LocalAbsolutePath(oldExecutable)&&command!=null&&command.EndsWith(" --startup",StringComparison.Ordinal)&&string.Equals(command,"\""+Path.GetFullPath(oldExecutable)+"\" --startup",StringComparison.OrdinalIgnoreCase);}
  internal static ProcessStartInfo RelaunchStart(string executable){if(!LocalAbsolutePath(executable))throw new InvalidDataException("程序启动地址无效。");var start=new ProcessStartInfo(Path.GetFullPath(executable)){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(Path.GetFullPath(executable))};start.EnvironmentVariables.Remove("NOOK_UPDATE_REQUEST");start.EnvironmentVariables.Remove("NOOK_UPDATE_EXPECTED_VERSION");return start;}
  internal static ProcessStartInfo RelaunchStart(string executable,string requestFile,string expectedVersion){
   if(expectedVersion!=Version)throw new InvalidDataException("更新启动版本不符。");UpdateSession(requestFile,UpdatesRoot);
   var start=RelaunchStart(executable);start.EnvironmentVariables["NOOK_UPDATE_REQUEST"]=Path.GetFullPath(requestFile);start.EnvironmentVariables["NOOK_UPDATE_EXPECTED_VERSION"]=expectedVersion;return start;
  }
  internal static bool WaitForParentExit(Func<int,bool> wait,Func<bool> cancelled,int timeoutMilliseconds){if(wait==null||cancelled==null||timeoutMilliseconds<1||timeoutMilliseconds>30000)throw new ArgumentException("更新等待参数无效。");for(int elapsed=0;elapsed<timeoutMilliseconds;){if(cancelled())return false;int delay=Math.Min(250,timeoutMilliseconds-elapsed);if(wait(delay))return true;elapsed+=delay;}return false;}
  internal static void ValidateMetadataBackupMarker(string text){
   if(string.IsNullOrWhiteSpace(text)||text.Length>4096)throw new InvalidDataException("更新前备份完成标记为空或过大。");
   Dictionary<string,object> data;try{data=new JavaScriptSerializer{MaxJsonLength=4096,RecursionLimit=4}.Deserialize<Dictionary<string,object>>(text);}catch(Exception error){throw new InvalidDataException("更新前备份完成标记无法读取。",error);}
   string[] keys={"schemaVersion","completedUtc","metadataOnly","sourceMediaIncluded","credentialsIncluded","bytes"};
   if(data==null||data.Count!=keys.Length)throw new InvalidDataException("更新前备份完成标记字段不完整。");foreach(string key in keys)if(!data.ContainsKey(key))throw new InvalidDataException("更新前备份完成标记字段无效。");
   DateTime completed;object count=data["bytes"];long bytes;
   if(!(data["schemaVersion"] is int)||(int)data["schemaVersion"]!=1||!(data["completedUtc"] is string)||!DateTime.TryParseExact((string)data["completedUtc"],"O",CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out completed)||completed.Kind!=DateTimeKind.Utc)throw new InvalidDataException("更新前备份完成标记版本或时间无效。");
   if(!(data["metadataOnly"] is bool)||!(bool)data["metadataOnly"]||!(data["sourceMediaIncluded"] is bool)||(bool)data["sourceMediaIncluded"]||!(data["credentialsIncluded"] is bool)||(bool)data["credentialsIncluded"])throw new InvalidDataException("更新前备份类型与安全约定不符。");
   if(!(count is int||count is long)||!long.TryParse(Convert.ToString(count,CultureInfo.InvariantCulture),NumberStyles.None,CultureInfo.InvariantCulture,out bytes)||bytes<0||bytes>128L*1024*1024)throw new InvalidDataException("更新前备份大小无效。");
  }
  internal static void RequireMetadataBackup(string session){
   // Fixed session-local marker only. Never accept a caller-supplied backup
   // filename, enumerate a data tree, or inspect a backup before parent exit.
   if(!LocalAbsolutePath(session))throw new InvalidDataException("更新备份目录无效。");
   string folder=Path.GetFullPath(session);UpdateSession(Path.Combine(folder,"request.json"),Path.GetDirectoryName(folder));
   string marker=Path.Combine(folder,"metadata-backup","complete.json");PlainPath(marker);
   using(var input=new FileStream(marker,FileMode.Open,FileAccess.Read,FileShare.Read)){
    if(input.Length<1||input.Length>4096)throw new InvalidDataException("更新前备份完成标记为空或过大。");
    using(var reader=new StreamReader(input,new UTF8Encoding(false,true),true,1024))ValidateMetadataBackupMarker(reader.ReadToEnd());
    PlainPath(marker);
   }
  }
  internal static void ValidatePostExitBackup(bool parentExited,Func<bool> cancelled,Action verifyBackup){
   if(cancelled==null||verifyBackup==null)throw new ArgumentNullException();
   if(!parentExited||cancelled())throw new UpdateCancelledException("更新已取消，旧版本及资料未改动。");
   try{verifyBackup();}catch(Exception error){throw new UpdateCancelledException("没有确认更新前备份已完成，已取消安装；旧版本及资料未改动。\n"+error.Message,error);}
   // Cancellation and process exit may arrive in the same 250 ms wait slice.
   // A successful wait alone must never turn an aborted handoff into install.
   if(cancelled())throw new UpdateCancelledException("更新已取消，旧版本及资料未改动。");
  }
  internal static void ValidateStartupReceipt(string text,string expectedVersion,string executable,int processId,long startedUtcTicks){
   if(string.IsNullOrWhiteSpace(text)||text.Length>4096||expectedVersion!=Version||!LocalAbsolutePath(executable)||processId<=0||startedUtcTicks<=0)throw new InvalidDataException("新版启动确认无效。");
   Dictionary<string,object> data;try{data=new JavaScriptSerializer{MaxJsonLength=4096,RecursionLimit=4}.Deserialize<Dictionary<string,object>>(text);}catch(Exception error){throw new InvalidDataException("新版启动确认无法读取。",error);}
   string[] keys={"schemaVersion","expectedVersion","processId","startedUtcTicks","executable"};
   if(data==null||data.Count!=keys.Length)throw new InvalidDataException("新版启动确认字段不完整。");foreach(string key in keys)if(!data.ContainsKey(key))throw new InvalidDataException("新版启动确认字段无效。");
   object ticks=data["startedUtcTicks"];long started;string actual=data["executable"] as string;
   if(!(data["schemaVersion"] is int)||(int)data["schemaVersion"]!=1||!(data["expectedVersion"] is string)||(string)data["expectedVersion"]!=expectedVersion||!(data["processId"] is int)||(int)data["processId"]!=processId||!(ticks is int||ticks is long)||!long.TryParse(Convert.ToString(ticks,CultureInfo.InvariantCulture),NumberStyles.None,CultureInfo.InvariantCulture,out started)||started!=startedUtcTicks||!LocalAbsolutePath(actual)||!string.Equals(Path.GetFullPath(actual),Path.GetFullPath(executable),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("新版启动确认与本次进程身份不符。");
  }
  internal static bool WaitForStartupReceipt(Func<string> read,Func<bool> childExited,Func<bool> cancelled,Action<int> delay,Action<string> verify,int timeoutMilliseconds){
   if(read==null||childExited==null||cancelled==null||delay==null||verify==null||timeoutMilliseconds<1||timeoutMilliseconds>30000)throw new ArgumentException("新版启动等待参数无效。");
   for(int elapsed=0;elapsed<timeoutMilliseconds;){
    if(childExited()||cancelled())return false;string receipt=read();
    if(receipt!=null){verify(receipt);return !childExited()&&!cancelled();}
    int interval=Math.Min(100,timeoutMilliseconds-elapsed);delay(interval);elapsed+=interval;
   }
   return false;
  }
  static string ReadStartupReceipt(string session){
   string file=Path.Combine(session,"startup-ready.json");PlainPath(file);if(!File.Exists(file))return null;
   using(var input=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read)){
    if(input.Length<1||input.Length>4096)throw new InvalidDataException("新版启动确认大小无效。");
    using(var reader=new StreamReader(input,new UTF8Encoding(false,true),true,1024))return reader.ReadToEnd();
   }
  }
  static bool MatchesLaunchedChild(Process child,string executable,long startedTicks){
   if(child==null||child.HasExited||child.StartTime.ToUniversalTime().Ticks!=startedTicks)return false;var module=child.MainModule;
   return module!=null&&ProductIdentity(module.FileVersionInfo.ProductName,module.FileVersionInfo.OriginalFilename)&&string.Equals(Path.GetFullPath(module.FileName),executable,StringComparison.OrdinalIgnoreCase);
  }
  static bool CloseOwnedNewChild(Process child,string executable,long startedTicks){
   if(child==null||child.HasExited)return true;
   // Never kill, enumerate-and-close, or send commands to an unverified PID.
   if(!MatchesLaunchedChild(child,executable,startedTicks))return false;
   child.CloseMainWindow();return child.WaitForExit(5000);
  }
  internal static void RequirePlainAttributes(FileAttributes attributes){if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("更新路径包含链接，已停止更新。");}
  static void PlainPath(string path){for(string part=Path.GetFullPath(path);!string.IsNullOrEmpty(part);part=Path.GetDirectoryName(part)){FileAttributes attributes;try{attributes=File.GetAttributes(part);}catch(FileNotFoundException){continue;}catch(DirectoryNotFoundException){continue;}RequirePlainAttributes(attributes);}}
  static string Hash(string path){using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(input)).Replace("-","");}
  static void UpdateRecord(UpdateRequest request,string name,string status,string message){
   PlainPath(request.SessionDirectory);string target=Path.Combine(request.SessionDirectory,name);string temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";
   try{byte[] bytes=new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(new{schemaVersion=1,status=status,expectedVersion=Version,message=message??""}));using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(bytes,0,bytes.Length);output.Flush(true);}File.Move(temporary,target);}finally{if(File.Exists(temporary))File.Delete(temporary);}
  }
  static string ReadStartup(){using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",false))return key==null?null:key.GetValue("MoriDesk",null,RegistryValueOptions.DoNotExpandEnvironmentNames) as string;}
  static bool ReplaceStartup(string before,string after){using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",true)){if(key==null||!string.Equals(key.GetValue("MoriDesk",null,RegistryValueOptions.DoNotExpandEnvironmentNames) as string,before,StringComparison.OrdinalIgnoreCase))return false;key.SetValue("MoriDesk",after,RegistryValueKind.String);return true;}}
  sealed class UpdateShortcut {
   internal readonly string Location;readonly byte[] before;bool written;
   internal UpdateShortcut(string path){Location=path;GuardShortcut(path);before=File.Exists(path)?File.ReadAllBytes(path):null;}
   internal void Install(string executable){written=true;Shortcut(Location,executable);}
   internal void Restore(string candidate){if(!written||!File.Exists(Location))return;string target=ShortcutTarget(Location);if(!string.Equals(target,candidate,StringComparison.OrdinalIgnoreCase))return;if(before==null){File.Delete(Location);return;}string temporary=Location+"."+Guid.NewGuid().ToString("N")+".tmp";try{File.WriteAllBytes(temporary,before);File.Replace(temporary,Location,null);}finally{if(File.Exists(temporary))File.Delete(temporary);}}
  }
  static void RestoreOpenWith(string candidate,string previous){
   using(var app=Registry.CurrentUser.OpenSubKey(MoriDesk.OpenWithRegistration.ApplicationKey,true)){
    if(app==null||!string.Equals(app.GetValue("FriendlyAppName") as string,"Nook",StringComparison.Ordinal))return;
    using(var command=app.OpenSubKey("shell\\open\\command",true)){
     if(command==null||!string.Equals(command.GetValue("") as string,"\""+candidate+"\" \"%1\"",StringComparison.OrdinalIgnoreCase))return;
     command.SetValue("","\""+previous+"\" \"%1\"",RegistryValueKind.String);
    }
    using(var icon=app.OpenSubKey("DefaultIcon",true))if(icon!=null)icon.SetValue("","\""+previous+"\",0",RegistryValueKind.String);
    using(var application=app.OpenSubKey("Application",true))if(application!=null)application.SetValue("ApplicationIcon","\""+previous+"\",0",RegistryValueKind.String);
   }
  }
  static int Update(string requestFile){
   UpdateRequest request=null;Process parent=null,launched=null;long launchedStartedTicks=0;bool parentExited=false,startupChanged=false,committed=false;string oldHash=null,candidate=null,oldStartup=null,newStartup=null;UpdateShortcut desktop=null,menu=null;
   try{
    int currentId,currentSession;using(var current=Process.GetCurrentProcess()){currentId=current.Id;currentSession=current.SessionId;}
    string session=UpdateSession(requestFile,UpdatesRoot);PlainPath(requestFile);var info=new FileInfo(requestFile);if(!info.Exists||info.Length>16384)throw new InvalidDataException("找不到有效的更新请求。");
    request=ParseUpdateRequest(File.ReadAllText(requestFile),requestFile,UpdatesRoot,ProgramRoot,currentId);
    if(File.Exists(Path.Combine(session,"ready.json"))||File.Exists(Path.Combine(session,"result.json"))||File.Exists(Path.Combine(session,"cancel.request"))||File.Exists(Path.Combine(session,"startup-ready.json")))throw new InvalidDataException("本次更新请求已使用或已取消，请重新检查更新。");
    PlainPath(request.ParentExecutable);parent=Process.GetProcessById(request.ParentProcessId);var module=parent.MainModule;
    if(module==null||!MatchesUpdateParent(request,parent.Id,parent.SessionId,currentSession,module.FileName,module.FileVersionInfo.ProductName,module.FileVersionInfo.OriginalFilename,parent.StartTime.ToUniversalTime().Ticks))throw new InvalidDataException("发起更新的 Nook 进程已改变，请重新检查更新。");
    Version prior;Version desired=new Version(Version+".0");if(!System.Version.TryParse(module.FileVersionInfo.FileVersion,out prior)||prior>desired)throw new InvalidDataException("此安装包不能降级当前 Nook。");
    oldHash=Hash(request.ParentExecutable);
    string startMenu=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),"Programs","Nook");
    desktop=new UpdateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"Nook.lnk"));menu=new UpdateShortcut(Path.Combine(startMenu,"Nook.lnk"));
    PlainPath(ProgramRoot);string folder=Path.Combine(ProgramRoot,Version+"-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff",CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N").Substring(0,8));
    candidate=Extract(folder);PlainPath(candidate);var next=FileVersionInfo.GetVersionInfo(candidate);Version actual;
    if(!ProductIdentity(next.ProductName,next.OriginalFilename)||!System.Version.TryParse(next.FileVersion,out actual)||actual!=desired)throw new InvalidDataException("安装包中的程序版本无效，旧版本未改动。");
    UpdateRecord(request,"ready.json","ready","");
    parentExited=WaitForParentExit(milliseconds=>parent.WaitForExit(milliseconds),()=>File.Exists(Path.Combine(session,"cancel.request")),30000);
    if(!parentExited){UpdateRecord(request,"result.json","cancelled","Nook 尚未退出或更新已取消；旧版本及资料未改动。");return 2;}
    ValidatePostExitBackup(parentExited,()=>File.Exists(Path.Combine(session,"cancel.request")),()=>RequireMetadataBackup(session));
    if(HasRunningMoriDesk())throw new IOException("另一个 Nook 窗口仍在运行。请完成收藏并完全退出后再更新；安装器不会强制关闭它。");
    // Read again after graceful shutdown: a preference saved while staging
    // must win over the earlier preflight snapshot.
    oldStartup=ReadStartup();desktop=new UpdateShortcut(desktop.Location);menu=new UpdateShortcut(menu.Location);
    Directory.CreateDirectory(startMenu);desktop.Install(candidate);menu.Install(candidate);
    if(OwnStartup(oldStartup,request.ParentExecutable)){newStartup="\""+candidate+"\" --startup";startupChanged=ReplaceStartup(oldStartup,newStartup);}
    launched=Process.Start(RelaunchStart(candidate,request.RequestPath,Version));if(launched==null)throw new IOException("新版进程未能启动，正在尝试重新打开旧版。");
    launchedStartedTicks=launched.StartTime.ToUniversalTime().Ticks;
    bool startupReady=WaitForStartupReceipt(()=>ReadStartupReceipt(session),()=>launched.HasExited,()=>File.Exists(Path.Combine(session,"cancel.request")),milliseconds=>Thread.Sleep(milliseconds),receipt=>ValidateStartupReceipt(receipt,Version,candidate,launched.Id,launchedStartedTicks),30000);
    if(!startupReady||!MatchesLaunchedChild(launched,candidate,launchedStartedTicks))throw new IOException("新版没有确认素材库和主窗口已就绪，未将本次更新标记为成功。");
    committed=true;MoriDesk.OpenWithRegistration.RepairInstalled(candidate);UpdateRecord(request,"result.json","installed","新版已安装并启动；旧版、素材、贴图和设置均保留。");return 0;
   }catch(Exception error){
    string message=error.Message;
    if(committed){MessageBox.Show("新版已安装并启动，旧安装目录和资料均保留。\n部分完成信息未能保存："+message,"Nook · 更新已完成",MessageBoxButtons.OK,MessageBoxIcon.Information);return 0;}
    if(parentExited&&request!=null){
     try{if(!CloseOwnedNewChild(launched,candidate,launchedStartedTicks))message+="\n新版窗口尚未退出；为保护正在编辑的信息，未强制关闭。请关闭该窗口后再打开保留的旧版。";}catch(Exception recovery){message+="\n未能正常关闭本次新版窗口，未强制结束进程："+recovery.Message;}
     try{if(desktop!=null&&candidate!=null)desktop.Restore(candidate);if(menu!=null&&candidate!=null)menu.Restore(candidate);if(startupChanged)ReplaceStartup(newStartup,oldStartup);if(candidate!=null)RestoreOpenWith(candidate,request.ParentExecutable);}catch(Exception recovery){message+="\n旧快捷方式恢复未完成："+recovery.Message;}
     try{PlainPath(request.ParentExecutable);var previous=FileVersionInfo.GetVersionInfo(request.ParentExecutable);if(!HasRunningMoriDesk()&&ProductIdentity(previous.ProductName,previous.OriginalFilename)&&Hash(request.ParentExecutable)==oldHash){using(var recovery=Process.Start(RelaunchStart(request.ParentExecutable))){}message+="\n已尝试重新打开保留的旧版本。";}}catch(Exception recovery){message+="\n旧版自动重开失败，请从原安装目录打开："+recovery.Message;}
    }
    bool cancelled=error is UpdateCancelledException;
    if(request!=null)try{UpdateRecord(request,"result.json",cancelled?"cancelled":"error",message);}catch{}
    MessageBox.Show("更新未完成，旧安装目录、素材、贴图和设置未删除。\n"+message,cancelled?"Nook · 更新已取消":"Nook · 更新未完成",MessageBoxButtons.OK,cancelled?MessageBoxIcon.Information:MessageBoxIcon.Error);return cancelled?2:1;
   }finally{if(parent!=null)parent.Dispose();if(launched!=null)launched.Dispose();}
  }
  [STAThread] static int Main(string[] args){Application.EnableVisualStyles();
   if(args.Length>0&&args[0]=="--update-request"){if(args.Length==2)return Update(args[1]);MessageBox.Show("更新参数无效。","Nook",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;}
   if(args.Length==1&&args[0]=="--verify"){try{string temp=Path.Combine(Path.GetTempPath(),"MoriDesk-install-check-"+Guid.NewGuid().ToString("N"));string exe=Extract(temp);var start=VerificationStart(exe,temp);Directory.CreateDirectory(start.EnvironmentVariables["TEMP"]);Directory.CreateDirectory(start.EnvironmentVariables["MORI_DESK_DATA"]);using(var process=Process.Start(start)){process.WaitForExit();return process.ExitCode;}}catch{return 1;}}
   if(args.Length!=0){MessageBox.Show("无法识别安装参数。","Nook",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;}
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
    // Refresh only this suite's Open-with entry. Windows user defaults and
    // other applications' registrations are intentionally left untouched.
    MoriDesk.OpenWithRegistration.RepairInstalled(exe);
    // Remove only links whose existing target is demonstrably our old suite.
    // Never remove old program directories, user data or unrelated Mori links.
    RemoveOwnedLegacyShortcut(Path.Combine(desktop,"Mori Desk.lnk"));
    RemoveOwnedLegacyShortcut(Path.Combine(programs,"Mori Desk","Mori Desk.lnk"));
    if(MessageBox.Show("安装完成。桌面已创建 Nook 快捷方式。\n\n升级前后都请确保旧 Mori Desk / Nook 已完全退出，否则可能打开旧窗口。独立 Mori / Luma 仍在运行时，F1 / F3 等快捷键可能被占用，可先用新软件的“工具”菜单体验。\n\n如原来开启了自启动，请在新版设置中重新勾选并保存。\n\n现在打开吗？","Nook",MessageBoxButtons.YesNo,MessageBoxIcon.Information)==DialogResult.Yes){if(HasRunningMoriDesk()){MessageBox.Show("软件已安装，但 Mori Desk / Nook 又被打开了。请完成收藏并完全退出已有窗口后，再通过新版桌面快捷方式打开。","暂未打开新版",MessageBoxButtons.OK,MessageBoxIcon.Information);return 0;}Process.Start(exe);}return 0;}catch(Exception ex){MessageBox.Show("安装未完成，素材、贴图和设置未改动。\n若新版快捷方式已创建，修正下面的问题后可重新安装。\n"+ex.Message,"Nook",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;}
  }
 }
}
