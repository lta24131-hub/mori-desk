using System;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using MoriDesk;

namespace MoriDeskInstaller {
 // Manual installation and in-app update share this activation transaction.
 // Staged folders are implementation details, never independent public apps.
 static partial class Program {
  internal static int WithReplacementLease(Func<int> action){
   if(action==null)throw new ArgumentNullException("action");
   using(InstalledApplicationState.AcquireLease(ProgramRoot,0))return action();
  }
  internal static bool ShouldApply(Version installed,Version desired,string installedPayload,string desiredPayload){
   if(desired==null||string.IsNullOrEmpty(desiredPayload))throw new InvalidDataException("更新包身份不完整。");
   if(installed==null)return true;
   int order=installed.CompareTo(desired);
   if(order>0)throw new InvalidDataException("电脑上已有更新版本，旧安装包不能覆盖它。");
   if(order==0){
    if(string.Equals(installedPayload,desiredPayload,StringComparison.OrdinalIgnoreCase))return false;
    throw new InvalidDataException("此安装包与当前程序版本号相同，但内容不同。为防止旧安装包覆盖修复版，已停止更新；请使用版本号更高的正式更新包。");
   }
   return true;
  }
  internal static bool ShouldReplace(Version installed,Version desired,string installedPayload,string desiredPayload,bool repair){
   // Repair is not an escape hatch for downgrade or same-version new bytes.
   bool apply=ShouldApply(installed,desired,installedPayload,desiredPayload);
   if(repair&&installed==null)throw new InvalidDataException("找不到可核对的原安装记录，未按修复处理。");
   return apply||repair;
  }
  static string PayloadDigest(){
   using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("MoriDesk.Payload.zip")){
    if(stream==null)throw new InvalidDataException("安装包缺少程序。");
    using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();
   }
  }
  internal static long RequiredSpace(Stream payload){
   if(payload==null)throw new ArgumentNullException("payload");long total=0;
   using(var zip=new ZipArchive(payload,ZipArchiveMode.Read,true))foreach(var entry in zip.Entries){
    if(entry.Length<0||entry.Length>8L*1024*1024*1024||total>8L*1024*1024*1024-entry.Length)throw new InvalidDataException("安装包大小超出支持范围。");
    total+=entry.Length;
   }
   // Reserve temporary metadata/filesystem headroom; existing usable files are
   // never deleted just to make enough room for an incoming payload.
   return checked(total+64L*1024*1024);
  }
  static void RequireDiskSpace(){
   long required;using(var payload=Assembly.GetExecutingAssembly().GetManifestResourceStream("MoriDesk.Payload.zip"))required=RequiredSpace(payload);
   var drive=new DriveInfo(Path.GetPathRoot(ProgramRoot));
   if(!drive.IsReady||drive.AvailableFreeSpace<required)throw new IOException("程序所在磁盘空间不足，尚未退出或替换 Nook。请先释放空间后重试；素材目录不会改动。");
  }
  static bool SamePath(string a,string b){return !string.IsNullOrEmpty(a)&&!string.IsNullOrEmpty(b)&&string.Equals(Path.GetFullPath(a),Path.GetFullPath(b),StringComparison.OrdinalIgnoreCase);}
  static string LegacyInstalledExecutable(){
   string desktop=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"Nook.lnk");
   string menu=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),"Programs","Nook","Nook.lnk");
   string selected=null;
   foreach(string link in new[]{desktop,menu}){
    if(!File.Exists(link))continue;
    string target=ShortcutTarget(link);
    if(SamePath(target,InstalledApplicationState.LauncherPath(ProgramRoot)))continue;
    if(!OwnedStartupFile(target))throw new IOException("同名启动入口不属于本次安装，未覆盖它。");
    if(selected!=null&&!SamePath(selected,target))throw new IOException("旧启动入口指向不同程序，已停止替换，避免选择错误的旧版。");
    selected=target;
   }
   using(var key=Registry.CurrentUser.OpenSubKey(OpenWithRegistration.ApplicationKey+"\\shell\\open\\command")){
    string target=key==null?null:OpenWithRegistration.CommandTarget(key.GetValue("") as string);
    if(target!=null&&!SamePath(target,InstalledApplicationState.LauncherPath(ProgramRoot))&&OwnedStartupFile(target)){
     if(selected!=null&&!SamePath(selected,target))throw new IOException("打开方式与桌面入口不一致，已停止替换，原程序保留。");
     selected=target;
    }
   }
   return selected;
  }
  static InstalledApplicationDescriptor ExistingApplication(string expectedParent=null){
   var existing=InstalledApplicationState.Read(ProgramRoot);
   if(existing!=null){
    if(expectedParent!=null&&!SamePath(InstalledApplicationState.ResolveExecutable(ProgramRoot,existing),expectedParent))throw new IOException("这次更新来自已被替换的旧程序，未修改当前 Nook。");
    return existing;
   }
   string legacy=expectedParent??LegacyInstalledExecutable();if(legacy==null)return null;
   if(!OwnedStartupFile(legacy))throw new IOException("无法确认现有程序身份，未替换。");
   // Legacy packages have no archive identity. The executable digest creates
   // a migration identity, never a same-package repair match. Future releases
   // must use a higher version, not a guessed timestamp or newest directory.
   return InstalledApplicationState.Commit(ProgramRoot,null,InstalledApplicationState.CreateDescriptor(ProgramRoot,legacy,Hash(legacy).ToLowerInvariant()));
  }
  static InstalledApplicationDescriptor ExistingApplicationForRepair(out bool repair){
   repair=false;
   try{
    var existing=ExistingApplication();
    if(existing!=null)InstalledVersionCleanup.VerifyReadOnly(Path.GetDirectoryName(InstalledApplicationState.ResolveExecutable(ProgramRoot,existing)));
    return existing;
   }
   catch(Exception error){
    if(!(error is IOException)&&!(error is InvalidDataException))throw;
    // An unreadable/corrupt ledger must still fail. Only a strict intact
    // descriptor may authorize repair of its damaged/missing target. Never
    // use this metadata-only evidence to launch the old executable.
    var previous=InstalledApplicationState.ReadRecordForRepair(ProgramRoot);
    if(previous==null)throw;
    repair=true;return previous;
   }
  }
  static string StageReplacement(UpdateRequest request,string payloadDigest,string candidate=null){
   RequireDiskSpace();PlainPath(ProgramRoot);
   candidate=candidate??PlannedCandidate();string folder=Path.GetDirectoryName(candidate);
   RecordStage(request,candidate,payloadDigest,false);
   InstallerProgress.Report("正在解压新版程序…");
   if(!SamePath(Extract(folder,true),candidate))throw new InvalidDataException("暂存程序路径不匹配。");PlainPath(candidate);
   var descriptor=InstalledApplicationState.CreateDescriptor(ProgramRoot,candidate,payloadDigest);
   if(new Version(descriptor.version)!=new Version(Version+".0"))throw new InvalidDataException("安装包与程序版本不匹配，原程序未替换。");
   InstallerProgress.Report("正在校验新版程序…");
   InstalledVersionCleanup.VerifyReadOnly(folder);WriteCandidate(request,candidate);RecordStage(request,candidate,payloadDigest,true);
   return candidate;
  }
  static byte[] StableLauncherBytes(){
   byte[] bytes;using(var resource=Assembly.GetExecutingAssembly().GetManifestResourceStream("MoriDesk.Launcher.exe")){
    if(resource==null||resource.Length<1024||resource.Length>2*1024*1024)throw new InvalidDataException("安装包缺少固定启动入口。");
    using(var output=new MemoryStream()){resource.CopyTo(output);bytes=output.ToArray();}
   }
   return bytes;
  }
  const string LauncherOwnershipName=".nook-launcher-v1";
  // One reviewed protocol-compatible correction to the original frozen v1
  // bootstrap. Never accept an arbitrary older binary merely by its marker.
  internal const string LegacyLauncherDigest="72F8B36F91CAEF61269871E19459C9BC462F1751AD0EA690596BBA428E9CC583";
  internal static string LauncherOwnership(string digest){
   if(digest==null||digest.Length!=64)throw new InvalidDataException("固定启动入口的校验值无效。");
   foreach(char c in digest)if(!((c>='0'&&c<='9')||(c>='A'&&c<='F')))throw new InvalidDataException("固定启动入口的校验值无效。");
   return "Nook stable launcher protocol 1\n"+digest+"\n";
  }
  static bool HasLauncherOwnership(string target,string digest){
   string record=Path.Combine(Path.GetDirectoryName(target),LauncherOwnershipName);PlainPath(record);
   try{using(var input=new FileStream(record,FileMode.Open,FileAccess.Read,FileShare.Read)){
    if(input.Length<1||input.Length>256)return false;
    using(var reader=new StreamReader(input,new UTF8Encoding(false,true),false))return reader.ReadToEnd()==LauncherOwnership(digest);
   }}catch(FileNotFoundException){return false;}catch(DirectoryNotFoundException){return false;}catch(DecoderFallbackException){return false;}
  }
  internal static bool LauncherReplacementAuthorized(string actual,string desired,bool desiredOwned,bool legacyOwned){
   // Exact new bytes also recover a crash between binary and marker publish.
   // The established new marker retains its original same-bytes repair right.
   return actual==null||actual==desired||desiredOwned||(actual==LegacyLauncherDigest&&desired!=LegacyLauncherDigest&&legacyOwned);
  }
  static bool HasRunningStableLauncher(){
   var processes=Process.GetProcesses();bool running=false;
   try{foreach(var process in processes){bool possible=false;try{
    possible=PossibleInstalledProcessName(process.ProcessName);if(!possible)continue;
    var module=process.MainModule;if(module!=null&&SamePath(module.FileName,InstalledApplicationState.LauncherPath(ProgramRoot)))running=true;
   }catch(System.ComponentModel.Win32Exception){if(possible)running=true;}catch(InvalidOperationException){}catch(ArgumentException){}catch(IOException){if(possible)running=true;}catch(UnauthorizedAccessException){if(possible)running=true;}}}
   finally{foreach(var process in processes)process.Dispose();}return running;
  }
  static void RequireLauncherReplacement(string target,string digest){
   string actual=File.Exists(target)?Hash(target):null;
   if(!LauncherReplacementAuthorized(actual,digest,HasLauncherOwnership(target,digest),HasLauncherOwnership(target,LegacyLauncherDigest)))throw new IOException("固定启动入口与安装记录不一致，无法确认归属。请使用经过验证的启动器修复包。");
  }
  static void RequireStableLauncher(){
   string target=InstalledApplicationState.LauncherPath(ProgramRoot);PlainPath(target);
   byte[] bytes=StableLauncherBytes();string digest;using(var sha=SHA256.Create())digest=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");
   RequireLauncherReplacement(target,digest);
  }
  internal static string PublishStableLauncher(string target,byte[] bytes,Func<bool> payloadBusy,Func<bool> launcherBusy){
   if(bytes==null||bytes.Length==0||payloadBusy==null||launcherBusy==null)throw new ArgumentException("固定入口发布参数无效。");
   string folder=Path.GetDirectoryName(target);PlainPath(target);
   PlainPath(folder);Directory.CreateDirectory(folder);PlainPath(folder);
   string digest;using(var sha=SHA256.Create())digest=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");
   RequireLauncherReplacement(target,digest);
   bool present=File.Exists(target);string original=present?Hash(target):null;bool intact=original==digest;
   // Normally no executable is replaced. The single pinned v1 correction
   // requires both old bytes and old ownership, and no active launcher/payload.
   if(!intact){
   if(payloadBusy()||launcherBusy())throw new IOException("Nook 仍在使用固定启动入口，请完成收藏并正常退出 Nook 和浏览器后再修复；未强制关闭任何窗口。");
   string temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";
   try{using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(bytes,0,bytes.Length);output.Flush(true);}PlainPath(target);if(payloadBusy()||launcherBusy())throw new IOException("固定启动入口仍在运行，尚未替换；请完成收藏并正常退出后重试。");if(File.Exists(target)!=present||(present&&Hash(target)!=original))throw new IOException("固定启动入口在修复期间发生变化，未覆盖。");RequireLauncherReplacement(target,digest);if(present){File.Replace(temporary,target,null);}else File.Move(temporary,target);}
   finally{if(File.Exists(temporary))File.Delete(temporary);}
   }
   string owner=Path.Combine(folder,LauncherOwnershipName);PlainPath(owner);
   if(!HasLauncherOwnership(target,digest)){
    // Exact official binary bytes establish ownership even if publication of
    // this small generated record was interrupted on first installation.
    if(Hash(target)!=digest)throw new IOException("无法确认固定入口，未写入归属记录。");
    string temporary=owner+"."+Guid.NewGuid().ToString("N")+".tmp";byte[] record=Encoding.UTF8.GetBytes(LauncherOwnership(digest));
    try{using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(record,0,record.Length);output.Flush(true);}PlainPath(owner);if(File.Exists(owner))File.Replace(temporary,owner,null);else File.Move(temporary,owner);}
    finally{if(File.Exists(temporary))File.Delete(temporary);}
   }
   if(Hash(target)!=digest||!HasLauncherOwnership(target,digest))throw new IOException("固定启动入口发布后校验失败，未继续更新程序。");
   return target;
  }
  static string InstallStableLauncher(){
   RequireStableLauncher();return PublishStableLauncher(InstalledApplicationState.LauncherPath(ProgramRoot),StableLauncherBytes(),HasRunningMoriDesk,HasRunningStableLauncher);
  }
  static void WriteCandidate(UpdateRequest request,string candidate){
   string data=new JavaScriptSerializer().Serialize(new{schemaVersion=1,expectedVersion=Version,executable=Path.GetFullPath(candidate),sha256=Hash(candidate)});
   PlainPath(request.SessionDirectory);string path=Path.Combine(request.SessionDirectory,"candidate.json");
   using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){byte[] bytes=new UTF8Encoding(false).GetBytes(data);file.Write(bytes,0,bytes.Length);file.Flush(true);}
  }
  static UpdateRequest ManualRequest(string candidate,string previous){
   string session=Path.Combine(UpdatesRoot,Guid.NewGuid().ToString("N"));PlainPath(session);Directory.CreateDirectory(session);PlainPath(session);
   using(var process=Process.GetCurrentProcess()){
    var request=new UpdateRequest{RequestPath=Path.Combine(session,"request.json"),SessionDirectory=session,ParentExecutable=previous??candidate,ExpectedVersion=Version,ParentProcessId=process.Id,ParentStartedUtcTicks=process.StartTime.ToUniversalTime().Ticks,Relaunch=true};
    string text=new JavaScriptSerializer().Serialize(new{schemaVersion=1,parentProcessId=request.ParentProcessId,parentExecutable=request.ParentExecutable,parentStartedUtcTicks=request.ParentStartedUtcTicks,expectedVersion=Version,relaunch=true});
    using(var file=new FileStream(request.RequestPath,FileMode.CreateNew,FileAccess.Write,FileShare.None)){byte[] bytes=new UTF8Encoding(false).GetBytes(text);file.Write(bytes,0,bytes.Length);file.Flush(true);}
    return request;
   }
  }
  sealed class NativeRoute {
   readonly string path;readonly byte[] previous;byte[] published;
   internal NativeRoute(){path=Path.Combine(PhysicalUserContext.DefaultRoot,"browser","app.mori.desk.json");PlainPath(path);if(File.Exists(path)){var info=new FileInfo(path);if(info.Length>65536)throw new InvalidDataException("浏览器连接记录过大，未覆盖。");previous=File.ReadAllBytes(path);}}
   internal void Publish(string launcher){
    if(previous==null)return; // Do not install, authorize, or unpair an extension.
    var json=new JavaScriptSerializer{MaxJsonLength=65536,RecursionLimit=8};var data=json.Deserialize<System.Collections.Generic.Dictionary<string,object>>(Encoding.UTF8.GetString(previous));object name,type,origins;
    if(data==null||!data.TryGetValue("name",out name)||!(name is string)||(string)name!="app.mori.desk"||!data.TryGetValue("type",out type)||!(type is string)||(string)type!="stdio"||!data.TryGetValue("allowed_origins",out origins))throw new InvalidDataException("原浏览器连接记录无效，未覆盖。");
    data["path"]=launcher;published=new UTF8Encoding(false).GetBytes(json.Serialize(data));Replace(previous,published);
   }
   void Replace(byte[] expected,byte[] replacement){
    PlainPath(path);byte[] current=File.ReadAllBytes(path);if(!EqualBytes(expected,current))throw new IOException("浏览器连接在更新期间发生变化，未覆盖。");
    string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
    try{using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(replacement,0,replacement.Length);output.Flush(true);}File.Replace(temp,path,null);}finally{if(File.Exists(temp))File.Delete(temp);}
   }
   internal void Restore(){if(previous!=null&&published!=null&&File.Exists(path)&&EqualBytes(File.ReadAllBytes(path),published))Replace(published,previous);}
   static bool EqualBytes(byte[] a,byte[] b){if(a==null||b==null||a.Length!=b.Length)return false;for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;return true;}
  }
  static void RepairPublicEntrypoints(){
   // Idempotent reinstall repairs missing public routes without staging a
   // duplicate payload or shutting down a healthy running main window.
   string launcher=InstallStableLauncher();
   string menuRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),"Programs","Nook");
   var desktop=new UpdateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"Nook.lnk"));var menu=new UpdateShortcut(Path.Combine(menuRoot,"Nook.lnk"));
   var native=new NativeRoute();string oldStartup=ReadStartup(),newStartup=null;bool startupChanged=false;
   try{
    Directory.CreateDirectory(menuRoot);desktop.Install(launcher);menu.Install(launcher);
    newStartup=InheritedStartup(oldStartup,launcher,ProgramRoot,OwnedStartupFile);
    if(newStartup!=null)ApplyStartupReplacement(oldStartup,newStartup,ReplaceStartup,ReadStartup,()=>startupChanged=true);
    OpenWithRegistration.RepairStableInstalled(launcher);RequireStableOpenWith(launcher);native.Publish(launcher);
   }catch{
    desktop.Restore(launcher);menu.Restore(launcher);if(startupChanged)ReplaceStartup(newStartup,oldStartup);native.Restore();throw;
   }
  }
  // Only this method publishes public routes or advances the committed pointer.
  // Before commit every stable route still resolves the old descriptor; after
  // commit every route resolves the candidate, including after process death.
  static void ActivateReplacement(UpdateRequest request,string candidate,string payloadDigest,InstalledApplicationDescriptor previous,bool repair=false){
   InstallerProgress.Report("正在更新统一启动入口…");
   if(HasRunningMoriDesk())throw new IOException("Nook 仍有窗口或收藏任务在运行，未强行结束，也未替换当前程序。");
   string launcher=InstallStableLauncher();
   var desired=InstalledApplicationState.CreateDescriptor(ProgramRoot,candidate,payloadDigest);
   InstalledApplicationState.ValidateTransition(previous,desired);
   string menuRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),"Programs","Nook");
   var desktop=new UpdateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"Nook.lnk"));var menu=new UpdateShortcut(Path.Combine(menuRoot,"Nook.lnk"));
   var native=new NativeRoute();string oldStartup=null,newStartup=null;bool startupChanged=false,committed=false;Process launched=null;long started=0;
   try{
    if(HasRunningMoriDesk())throw new IOException("Nook 仍有窗口或收藏任务在运行，未强行结束，也未替换当前程序。");
    Directory.CreateDirectory(menuRoot);desktop.Install(launcher);menu.Install(launcher);
    oldStartup=ReadStartup();newStartup=InheritedStartup(oldStartup,launcher,ProgramRoot,OwnedStartupFile);
    if(newStartup!=null)ApplyStartupReplacement(oldStartup,newStartup,ReplaceStartup,ReadStartup,()=>startupChanged=true);
    OpenWithRegistration.RepairStableInstalled(launcher);RequireStableOpenWith(launcher);native.Publish(launcher);
    InstallerProgress.Report("正在启动并检查新版 Nook…");
    launched=Process.Start(RelaunchStart(candidate,request.RequestPath,Version));if(launched==null)throw new IOException("新版未启动，当前版本未替换。");started=launched.StartTime.ToUniversalTime().Ticks;
    bool ready=WaitForStartupReceipt(()=>ReadStartupReceipt(request.SessionDirectory),()=>launched.HasExited,()=>File.Exists(Path.Combine(request.SessionDirectory,"cancel.request")),milliseconds=>Thread.Sleep(milliseconds),receipt=>ValidateStartupReceipt(receipt,Version,candidate,launched.Id,started),30000);
    if(!ready||!MatchesLaunchedChild(launched,candidate,started))throw new IOException("新版没有确认素材库和主窗口已就绪，仍保留当前版本。");
    RequireStableOpenWith(launcher);
    if(!SamePath(ShortcutTarget(desktop.Location),launcher)||!SamePath(ShortcutTarget(menu.Location),launcher))throw new IOException("启动入口在更新期间发生变化，尚未替换当前版本。");
    InstallerProgress.Report("新版已就绪，正在完成版本切换…");
    CommitStageWithFence(request.SessionDirectory,ReadStageText(request.SessionDirectory),candidate,payloadDigest,()=>{
     if(repair)InstalledApplicationState.Repair(ProgramRoot,previous.generation,desired);
     else InstalledApplicationState.Commit(ProgramRoot,previous==null?null:previous.generation,desired);
     committed=true;
    });
    UpdateRecord(request,"result.json","installed","Nook 已更新。原启动入口、资料位置和设置保留，正在清理已确认的旧程序。");
   }catch(Exception error){
    if(committed){ // Durable pointer is authority even if the receipt write failed.
     try{UpdateRecord(request,"result.json","installed","Nook 已替换成功，完成记录稍后恢复；旧程序暂不清理。");}catch{}
     QueueInstallerMessage("Nook 已切换到新版，但完成记录未写入。旧程序暂时保留；资料未改动。\n"+error.Message,"Nook · 更新收尾待完成",MessageBoxIcon.Information);return;
    }
    InstallerProgress.Report("更新尚未完成，正在恢复原启动入口…");
    bool closed=CloseOwnedNewChild(launched,candidate,started);
    // Stable routes with a committed previous descriptor remain valid across
    // crashes. On handled failure also restore exact old shortcuts/preferences.
    try{desktop.Restore(launcher);menu.Restore(launcher);if(startupChanged)ReplaceStartup(newStartup,oldStartup);native.Restore();if(previous!=null&&!repair)RestoreOpenWith(launcher,InstalledApplicationState.ResolveExecutable(ProgramRoot,previous));}catch(Exception restore){throw new IOException(error.Message+"\n恢复入口未完成；当前版本指针和资料仍保留。"+restore.Message,error);}
    if(closed&&previous!=null&&!repair&&!HasRunningMoriDesk())using(var recovery=Process.Start(RelaunchStart(InstalledApplicationState.ResolveExecutable(ProgramRoot,previous)))){}
    if(!closed)throw new IOException(error.Message+"\n新版窗口尚未正常退出，未强制关闭；请先保存并退出该窗口。",error);
    throw;
   }finally{if(launched!=null)launched.Dispose();}
  }
  static void RequireStableOpenWith(string launcher){using(var command=Registry.CurrentUser.OpenSubKey(OpenWithRegistration.ApplicationKey+"\\shell\\open\\command")){if(command==null||!SamePath(OpenWithRegistration.CommandTarget(command.GetValue("") as string),launcher))throw new IOException("无法确认打开方式已指向固定入口，未提交更新。");}}
 }
}
