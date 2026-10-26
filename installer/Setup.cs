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
using MoriDesk;
[assembly: AssemblyTitle("Nook Installer")]
[assembly: AssemblyProduct("Nook Installer")]
[assembly: AssemblyVersion("0.1.30.0")]
[assembly: AssemblyFileVersion("0.1.30.0")]
namespace MoriDeskInstaller {
 internal sealed class UpdateRequest {
  internal string RequestPath,SessionDirectory,ParentExecutable,ExpectedVersion;
  internal int ParentProcessId;internal long ParentStartedUtcTicks;internal bool Relaunch;
 }
 internal sealed class UpdateCancelledException : IOException {
  internal UpdateCancelledException(string message):base(message){}
  internal UpdateCancelledException(string message,Exception inner):base(message,inner){}
 }
 static partial class Program {
  const string Version="0.1.30";
  internal static string InstallerVersion {get{return Version;}}
  static string ProgramRoot {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","MoriDesk");}}
  static string UpdatesRoot {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MoriDesk","updates");}}
  internal static bool ProductIdentity(string productName,string originalFilename){return (string.Equals(productName,"Mori Desk",StringComparison.OrdinalIgnoreCase)||string.Equals(productName,"Nook",StringComparison.OrdinalIgnoreCase))&&string.Equals(originalFilename,"Mori.exe",StringComparison.OrdinalIgnoreCase);}
  internal static bool MatchesRunningProcess(int processId,int sessionId,string productName,string originalFilename,int installerId,int installerSession){return processId!=installerId&&ProductIdentity(productName,originalFilename);}
  internal static bool PossibleInstalledProcessName(string name){return string.Equals(name,"Mori",StringComparison.OrdinalIgnoreCase)||string.Equals(name,"Nook",StringComparison.OrdinalIgnoreCase);}
  internal static bool IsStableLauncher(string executable,string root){return LocalAbsolutePath(executable)&&string.Equals(Path.GetFullPath(executable),InstalledApplicationState.LauncherPath(root),StringComparison.OrdinalIgnoreCase);}
  static bool HasRunningMoriDesk(){
   int installerId,installerSession;using(var current=Process.GetCurrentProcess()){installerId=current.Id;installerSession=current.SessionId;}
   var processes=Process.GetProcesses();bool running=false;
   try{foreach(var process in processes){bool possible=false;try{if(process.Id==installerId)continue;possible=PossibleInstalledProcessName(process.ProcessName);var module=process.MainModule;if(module!=null&&!IsStableLauncher(module.FileName,ProgramRoot)&&ManagedTarget(module.FileName,ProgramRoot,module.FileVersionInfo.ProductName,module.FileVersionInfo.OriginalFilename)&&MatchesRunningProcess(process.Id,process.SessionId,module.FileVersionInfo.ProductName,module.FileVersionInfo.OriginalFilename,installerId,installerSession))running=true;}
    // Only the immutable bootstrap is excluded. It cannot spawn a payload
    // while this installer holds the active-install lease; any already
    // launched native/main payload is a separate process and remains a veto.
    // System processes and a process exiting during inspection may not expose
    // metadata. Never infer ownership from an executable name or kill it.
    // Other Windows sessions share this user's install. An inaccessible Mori
    // process is a reason to postpone, never permission to delete its files.
    catch(System.ComponentModel.Win32Exception){if(possible)running=true;}catch(InvalidOperationException){}catch(ArgumentException){}catch(IOException){if(possible)running=true;}catch(UnauthorizedAccessException){if(possible)running=true;}}}
   finally{foreach(var process in processes)process.Dispose();}return running;
  }
  internal static ProcessStartInfo VerificationStart(string executable,string folder){
   var start=new ProcessStartInfo(executable,"--suite-test"){UseShellExecute=false,CreateNoWindow=true};
   start.EnvironmentVariables["TEMP"]=Path.Combine(folder,"runtime-temp");start.EnvironmentVariables["TMP"]=start.EnvironmentVariables["TEMP"];start.EnvironmentVariables["MORI_DESK_DATA"]=Path.Combine(folder,"test-data","MoriDesk");return start;
  }
  static string Extract(string folder,bool installed=false){
   using(var source=Assembly.GetExecutingAssembly().GetManifestResourceStream("MoriDesk.Payload.zip"))
    return ExtractPayload(folder,source,installed?(Action<string,IEnumerable<string>,IEnumerable<string>>)InstalledVersionCleanup.WriteManifest:null);
  }
  internal static string ExtractPayload(string folder,Stream source,Action<string,IEnumerable<string>,IEnumerable<string>> seal){
   if(!LocalAbsolutePath(folder)||source==null)throw new InvalidDataException("安装目录或安装包无效。");
   folder=Path.GetFullPath(folder).TrimEnd('\\','/');PlainPath(folder);
   // Never bless an existing directory as installer-owned. The seal below
   // receives ZIP entries, not a disk inventory that could adopt user files.
   if(Directory.Exists(folder)||File.Exists(folder))throw new IOException("安装目标已存在，未覆盖或认领其中的文件。");
   Directory.CreateDirectory(folder);PlainPath(folder);string prefix=folder+"\\";
   var files=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var directories=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   using(var zip=new ZipArchive(source,ZipArchiveMode.Read,true))foreach(var entry in zip.Entries){
    string relative=entry.FullName.Replace('/','\\');bool directory=relative.EndsWith("\\",StringComparison.Ordinal);if(directory)relative=relative.TrimEnd('\\');
    if(string.IsNullOrWhiteSpace(relative)||Path.IsPathRooted(relative)||relative.IndexOfAny(new[]{':','\0','\r','\n'})>=0)throw new InvalidDataException("安装包路径异常。");
    foreach(string part in relative.Split('\\'))if(part.Length==0||part=="."||part==".."||part.EndsWith(".",StringComparison.Ordinal)||part.EndsWith(" ",StringComparison.Ordinal))throw new InvalidDataException("安装包路径不规范。");
    if(string.Equals(relative,InstalledVersionCleanup.ManifestName,StringComparison.OrdinalIgnoreCase)||string.Equals(relative,InstalledVersionCleanup.ProgressName,StringComparison.OrdinalIgnoreCase)||string.Equals(relative,InstalledVersionCleanup.JournalDirectoryName,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("安装包不能自带安装归属记录。");
    string file=Path.GetFullPath(Path.Combine(folder,relative));if(!file.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("安装包路径越界。");PlainPath(file);
    string parent=directory?file:Path.GetDirectoryName(file);
    for(string part=parent;!string.Equals(part,folder,StringComparison.OrdinalIgnoreCase);part=Path.GetDirectoryName(part))directories.Add(part.Substring(prefix.Length));
    Directory.CreateDirectory(parent);PlainPath(parent);
    if(directory)continue;
    if(!files.Add(relative))throw new InvalidDataException("安装包包含重复文件。");
    using(var input=entry.Open())using(var output=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None))input.CopyTo(output);
   }
   string executable=Path.Combine(folder,"Mori.exe");if(!files.Contains("Mori.exe")||!File.Exists(executable))throw new InvalidDataException("安装包缺少程序。");
   if(seal!=null){InstallerProgress.Report("正在核对并记录新版文件…");seal(folder,files,directories);}return executable;
  }
  static string ShortcutTarget(string location){object shell=null,link=null;try{shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));link=((dynamic)shell).CreateShortcut(location);return Convert.ToString(((dynamic)link).TargetPath);}finally{if(link!=null)Marshal.FinalReleaseComObject(link);if(shell!=null)Marshal.FinalReleaseComObject(shell);}}
  internal static bool ManagedTarget(string target,string programRoot,string product,string originalFilename){try{return !string.IsNullOrWhiteSpace(target)&&Path.GetFullPath(target).StartsWith(Path.GetFullPath(programRoot).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase)&&ProductIdentity(product,originalFilename)&&string.Equals(Path.GetFileName(target),"Mori.exe",StringComparison.OrdinalIgnoreCase);}catch(ArgumentException){return false;}catch(NotSupportedException){return false;}catch(PathTooLongException){return false;}}
  static bool OwnsShortcut(string location){try{string target=ShortcutTarget(location);if(!File.Exists(target))return false;var info=FileVersionInfo.GetVersionInfo(target);return ManagedTarget(target,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","MoriDesk"),info.ProductName,info.OriginalFilename);}catch{return false;}}
  static void GuardShortcut(string location){if(File.Exists(location)&&!OwnsShortcut(location))throw new IOException("已有同名快捷方式不属于本软件，未覆盖：\n"+location+"\n请先改名保留该快捷方式，再重新安装。");}
  static void RemoveOwnedLegacyShortcut(string location){if(File.Exists(location)&&OwnsShortcut(location)){try{File.Delete(location);}catch(IOException){}catch(UnauthorizedAccessException){}}}
  static void Shortcut(string location,string exe){PublishShortcut(location,exe,WriteShortcutFile,ShortcutTarget,GuardShortcut);}
  static void WriteShortcutFile(string location,string exe){object shell=null,link=null;try{shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));link=((dynamic)shell).CreateShortcut(location);dynamic shortcut=link;shortcut.TargetPath=exe;shortcut.WorkingDirectory=Path.GetDirectoryName(exe);shortcut.Description="Nook · 素材、截图、翻译与看图";shortcut.IconLocation=exe+",0";shortcut.Save();}finally{if(link!=null)Marshal.FinalReleaseComObject(link);if(shell!=null)Marshal.FinalReleaseComObject(shell);}}
  // Never let Shell.Save truncate a live entry point. A crash before the final
  // atomic publication leaves the previous complete shortcut available.
  internal static void PublishShortcut(string location,string exe,Action<string,string> write,Func<string,string> read,Action<string> guard){
   if(write==null||read==null||guard==null||!LocalAbsolutePath(location)||!LocalAbsolutePath(exe)||!string.Equals(Path.GetExtension(location),".lnk",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("快捷方式发布参数无效。");
   location=Path.GetFullPath(location);exe=Path.GetFullPath(exe);PlainPath(location);guard(location);
   string before=File.Exists(location)?Hash(location):null,temporary=Path.Combine(Path.GetDirectoryName(location),".nook-shortcut-"+Guid.NewGuid().ToString("N")+".lnk");PlainPath(temporary);
   if(File.Exists(temporary)||Directory.Exists(temporary))throw new IOException("快捷方式暂存文件已存在，未覆盖。");
   try{
    write(temporary,exe);PlainPath(temporary);
    using(var flush=new FileStream(temporary,FileMode.Open,FileAccess.ReadWrite,FileShare.Read)){if(flush.Length<1||flush.Length>1024*1024)throw new InvalidDataException("新快捷方式内容不完整，未替换原入口。");flush.Flush(true);}
    if(!string.Equals(Path.GetFullPath(read(temporary)),exe,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("新快捷方式目标校验失败，原入口未替换。");
    PlainPath(location);guard(location);string current=File.Exists(location)?Hash(location):null;
    if(!string.Equals(before,current,StringComparison.Ordinal))throw new IOException("快捷方式在更新期间发生变化，未覆盖其他操作。");
    if(before==null)File.Move(temporary,location);else File.Replace(temporary,location,null);
   }finally{if(File.Exists(temporary)){PlainPath(temporary);File.Delete(temporary);}}
  }
  // This update protocol accepts local data, never URLs, shell commands or an
  // installation destination. New bytes are staged privately; only a committed
  // active-install record is reachable through the single stable public entry.
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
  internal static string ManagedStartupExecutable(string command,string programRoot){
   // No argument splitting, environment expansion, shell interpretation or
   // approximate name matching. Only our exact persisted startup form counts.
   const string suffix="\" --startup";
   if(command==null||command.Length<=suffix.Length+1||command[0]!='"'||!command.EndsWith(suffix,StringComparison.Ordinal)||!LocalAbsolutePath(programRoot))return null;
   string executable=command.Substring(1,command.Length-suffix.Length-1);
   if(!LocalAbsolutePath(executable)||!OwnStartup(command,executable)||!ManagedTarget(executable,programRoot,"Nook","Mori.exe"))return null;
   return Path.GetFullPath(executable);
  }
  internal static string InheritedStartup(string command,string candidate,string programRoot,Func<string,bool> verifyOwned){
   if(verifyOwned==null)throw new ArgumentNullException("verifyOwned");
   string previous=ManagedStartupExecutable(command,programRoot);
   // Missing/disabled and unrelated commands remain untouched. Ownership
   // must come from the actual file, not merely an expected product string.
   if(previous==null||!verifyOwned(previous))return null;
   string next=LocalAbsolutePath(candidate)?"\""+Path.GetFullPath(candidate)+"\" --startup":null;
   if(ManagedStartupExecutable(next,programRoot)==null||!verifyOwned(Path.GetFullPath(candidate)))throw new InvalidDataException("新版自启动程序未通过身份检查，原自启动设置未改动。");
   return string.Equals(command,next,StringComparison.OrdinalIgnoreCase)?null:next;
  }
  internal static void ApplyStartupReplacement(string before,string after,Func<string,string,bool> replace,Func<string> read,Action written){
   if(before==null||after==null||replace==null||read==null||written==null)throw new ArgumentNullException();
   if(!replace(before,after))throw new IOException("开机启动设置在安装期间发生变化，未覆盖；请重新安装以完成升级。");
   // Set rollback ownership immediately after the conditional write, even
   // when verification subsequently fails. Never report an unchecked write.
   written();
   if(!string.Equals(read(),after,StringComparison.Ordinal))throw new IOException("未能确认开机启动已指向新版，未将本次安装报告为成功。");
  }
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
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file,StringBuilder path,uint length,uint flags);
  static bool OwnedStartupFile(string executable){
   if(!LocalAbsolutePath(executable)||!ManagedTarget(executable,ProgramRoot,"Nook","Mori.exe"))return false;
   PlainPath(executable);if(!File.Exists(executable))return false;
   // Hold the actual executable against writes/replacement while checking
   // kernel-resolved location and its real version resource. No linked or
   // redirected file may establish ownership of a startup registration.
   using(var input=new FileStream(executable,FileMode.Open,FileAccess.Read,FileShare.Read)){
    var actual=new StringBuilder(32768);uint length=GetFinalPathNameByHandle(input.SafeFileHandle,actual,(uint)actual.Capacity,0);
    if(length==0||length>=actual.Capacity||!MoriDesk.PhysicalUserContext.SamePhysicalFile(executable,actual.ToString()))throw new IOException("无法确认自启动程序的真实安装位置，原设置未改动。");
    PlainPath(executable);var metadata=FileVersionInfo.GetVersionInfo(executable);
    return ManagedTarget(executable,ProgramRoot,metadata.ProductName,metadata.OriginalFilename);
   }
  }
  static string ReadStartup(){using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",false)){object value=key==null?null:key.GetValue("MoriDesk",null,RegistryValueOptions.DoNotExpandEnvironmentNames);return value is string&&key.GetValueKind("MoriDesk")==RegistryValueKind.String?(string)value:null;}}
  static bool ReplaceStartup(string before,string after){using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",true)){if(key==null||!string.Equals(key.GetValue("MoriDesk",null,RegistryValueOptions.DoNotExpandEnvironmentNames) as string,before,StringComparison.Ordinal)||key.GetValueKind("MoriDesk")!=RegistryValueKind.String)return false;key.SetValue("MoriDesk",after,RegistryValueKind.String);return true;}}
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
   UpdateRequest request=null;Process parent=null;bool parentExited=false;
   try{
    InstallerProgress.Report("正在检查更新请求和安装包…");
    MoriDesk.PhysicalUserContext.RequireInstalledContext();
    int currentId,currentSession;using(var current=Process.GetCurrentProcess()){currentId=current.Id;currentSession=current.SessionId;}
    string session=UpdateSession(requestFile,UpdatesRoot);PlainPath(requestFile);var info=new FileInfo(requestFile);if(!info.Exists||info.Length>16384)throw new InvalidDataException("找不到有效的更新请求。");
    request=ParseUpdateRequest(File.ReadAllText(requestFile),requestFile,UpdatesRoot,ProgramRoot,currentId);
    if(File.Exists(Path.Combine(session,"ready.json"))||File.Exists(Path.Combine(session,"result.json"))||File.Exists(Path.Combine(session,"cancel.request"))||File.Exists(Path.Combine(session,"startup-ready.json")))throw new InvalidDataException("本次更新请求已使用或已取消，请重新检查更新。");
    PlainPath(request.ParentExecutable);parent=Process.GetProcessById(request.ParentProcessId);var module=parent.MainModule;
    if(module==null||!MatchesUpdateParent(request,parent.Id,parent.SessionId,currentSession,module.FileName,module.FileVersionInfo.ProductName,module.FileVersionInfo.OriginalFilename,parent.StartTime.ToUniversalTime().Ticks))throw new InvalidDataException("发起更新的 Nook 进程已改变，请重新检查更新。");
    Version prior;Version desired=new Version(Version+".0");if(!System.Version.TryParse(module.FileVersionInfo.FileVersion,out prior)||prior>desired)throw new InvalidDataException("此安装包不能降级当前 Nook。");
    var previous=ExistingApplication(request.ParentExecutable);string digest=PayloadDigest();
    if(!ShouldApply(new Version(previous.version),desired,previous.payloadSha256,digest)){UpdateRecord(request,"result.json","cancelled","当前已是同一版本，无需重复安装。");return 2;}
    TryCleanupAbandonedStages();RequireStableLauncher();string candidate=StageReplacement(request,digest);
    UpdateRecord(request,"ready.json","ready","");
    InstallerProgress.Report("正在等待 Nook 保存并退出…");
    parentExited=WaitForParentExit(milliseconds=>parent.WaitForExit(milliseconds),()=>File.Exists(Path.Combine(session,"cancel.request")),30000);
    if(!parentExited){UpdateRecord(request,"result.json","cancelled","Nook 尚未退出或更新已取消；旧版本及资料未改动。");return 2;}
    InstallerProgress.Report("正在确认资料备份…");
    ValidatePostExitBackup(parentExited,()=>File.Exists(Path.Combine(session,"cancel.request")),()=>RequireMetadataBackup(session));
    if(HasRunningMoriDesk())throw new IOException("另一个 Nook 窗口仍在运行。请完成收藏并完全退出后再更新；安装器不会强制关闭它。");
    ActivateReplacement(request,candidate,digest,previous);return 0;
   }catch(Exception error){
    string message=error.Message;
    bool cancelled=error is UpdateCancelledException;
    if(request!=null)try{UpdateRecord(request,"result.json",cancelled?"cancelled":"error",message);}catch{}
    QueueInstallerMessage("更新未完成，旧安装目录、素材、贴图和设置未删除。\n"+message,cancelled?"Nook · 更新已取消":"Nook · 更新未完成",cancelled?MessageBoxIcon.Information:MessageBoxIcon.Error);return cancelled?2:1;
   }finally{InstallerProgress.Report("正在完成安装收尾检查…");if(parent!=null)parent.Dispose();TryCleanupAbandonedStages();}
  }
  internal static int Dispatch(string[] args,Action requirePhysicalContext,Func<int> verify,Func<string,int> update,Func<int> install){
   if(args==null||requirePhysicalContext==null||verify==null||update==null||install==null)throw new ArgumentNullException();
   // Only this exact read/test command bypasses the production context gate.
   // A successful write/readback in an MSIX private view is not installation.
   if(args.Length==1&&args[0]=="--verify")return verify();
   if(args.Length!=0&&(args.Length!=2||args[0]!="--update-request"))throw new ArgumentException("无法识别安装参数。");
   requirePhysicalContext();
   return args.Length==0?install():update(args[1]);
  }
  static int Verify(){try{string temp=Path.Combine(Path.GetTempPath(),"MoriDesk-install-check-"+Guid.NewGuid().ToString("N"));string exe=Extract(temp);var start=VerificationStart(exe,temp);Directory.CreateDirectory(start.EnvironmentVariables["TEMP"]);Directory.CreateDirectory(start.EnvironmentVariables["MORI_DESK_DATA"]);using(var process=Process.Start(start)){process.WaitForExit();return process.ExitCode;}}catch{return 1;}}
  static string pendingMessage,pendingTitle;static MessageBoxIcon pendingIcon;
  static void QueueInstallerMessage(string text,string title,MessageBoxIcon icon){pendingMessage=text;pendingTitle=title;pendingIcon=icon;}
  static void ShowInstallerMessage(){if(pendingMessage!=null)MessageBox.Show(pendingMessage,pendingTitle,MessageBoxButtons.OK,pendingIcon);}
  static int ConfirmManualInstallation(){
   // Never hold the installation lease while waiting for a human. Otherwise
   // a forgotten confirmation/result dialog blocks browser collections and
   // the public launcher even though no replacement is in progress.
   if(MessageBox.Show("安装或更新 Nook？\n\n只保留一个正式启动入口。新版确认可以启动后替换当前版本，再清理已核验的旧程序。\n保留素材、分类、贴图、画布、保存位置和设置；不会修改 Windows 默认应用选择。\n请先保存工作并完全退出 Nook。","Nook",MessageBoxButtons.OKCancel,MessageBoxIcon.Information)!=DialogResult.OK)return 0;
   return InstallerProgress.Run(()=>WithReplacementLease(Install));
  }
  [STAThread] static int Main(string[] args){Application.EnableVisualStyles();
   try{int result=Dispatch(args,MoriDesk.PhysicalUserContext.RequireInstalledContext,Verify,
    request=>InstallerProgress.Run(()=>WithReplacementLease(()=>Update(request))),ConfirmManualInstallation);ShowInstallerMessage();return result;}
   catch(Exception error){MessageBox.Show("安装未开始。\n"+error.Message,"Nook · 安装未完成",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;}
  }
  static int Install(){
   UpdateRequest request=null;
   try{
    InstallerProgress.Report("正在检查现有版本和安装包…");
    bool repair;var previous=ExistingApplicationForRepair(out repair);string digest=PayloadDigest();
    bool apply=ShouldReplace(previous==null?null:new Version(previous.version),new Version(Version+".0"),previous==null?null:previous.payloadSha256,digest,repair);
    TryCleanupAbandonedStages();
    if(!apply){ValidateManualExistingLibrary();RepairPublicEntrypoints();QueueInstallerMessage("当前已是这一版本，启动入口已核对并修复。\n未新建另一套 Nook，也未关闭正在运行的窗口或改动素材和设置。","Nook · 已是当前版本",MessageBoxIcon.Information);return 0;}
    RequireStableLauncher();RequireDiskSpace();
    if(HasRunningMoriDesk()){QueueInstallerMessage("Mori Desk / Nook 仍在运行，本次尚未安装。\n\n请先完成当前收藏并保存正在编辑的信息，通过“更多 → 退出 Mori Desk”（新版为“退出 Nook”）或托盘菜单完全退出，再重新运行安装包。右上角 × 只会收进托盘。\n\n安装器不会强制结束程序，也不会更改素材、贴图或开机启动设置。","请先退出 Mori Desk / Nook",MessageBoxIcon.Information);return 1;}
    string candidate=PlannedCandidate();request=ManualRequest(candidate,previous==null?null:InstalledApplicationState.ResolveExecutable(ProgramRoot,previous));
    StageReplacement(request,digest,candidate);UpdateRecord(request,"ready.json","ready","");
    // Back up the configured real data after all existing app processes exit,
    // before the candidate can initialize or migrate any persisted records.
    InstallerProgress.Report("正在备份设置和项目记录…");
    if(CreateManualMetadataBackup(request.SessionDirectory,previous!=null))RequireMetadataBackup(request.SessionDirectory);
    ActivateReplacement(request,candidate,digest,previous,repair);return 0;
   }catch(Exception ex){if(request!=null)try{UpdateRecord(request,"result.json","error",ex.Message);}catch{}QueueInstallerMessage("更新未完成，素材和设置未删除。\n"+ex.Message,"Nook",MessageBoxIcon.Error);return 1;}
   finally{InstallerProgress.Report("正在完成安装收尾检查…");TryCleanupAbandonedStages();}
  }
 }
}
