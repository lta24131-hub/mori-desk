using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Diagnostics;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using MoriDesk;

namespace MoriDeskInstaller {
 internal sealed class InstallerComponent {
  internal string Directory,Name,Description;internal long Bytes;internal bool Selected=true;
  internal InstallerComponent Copy(){return (InstallerComponent)MemberwiseClone();}
 }
 internal sealed class InstallerPlan {
  internal string Location,Version;internal bool LocationLocked;internal long CoreBytes;
  internal InstallerComponent[] Components;
  internal InstallerPlan Copy(){return new InstallerPlan{Location=Location,Version=Version,LocationLocked=LocationLocked,CoreBytes=CoreBytes,Components=Components.Select(c=>c.Copy()).ToArray()};}
  internal long SelectedBytes {get{return CoreBytes+Components.Where(c=>c.Selected).Sum(c=>c.Bytes);}}
  internal bool Includes(string relative){string top=relative.Replace('/','\\').Split('\\')[0];var item=Components.FirstOrDefault(c=>string.Equals(c.Directory,top,StringComparison.OrdinalIgnoreCase));return item==null||item.Selected;}
  internal static string Size(long bytes){return bytes>=1024L*1024*1024?(bytes/(1024d*1024*1024)).ToString("0.##")+" GB":(bytes/(1024d*1024)).ToString("0.#")+" MB";}
 }
 static partial class Program {
  static InstallerPlan selectedPlan;
  static string cleanupSession;
  static InstallerPlan ReadPlan(){
   string root=ProgramRoot;var active=InstalledApplicationState.ReadRecordForRepair(root);
   var plan=new InstallerPlan{Location=root,Version=Version,LocationLocked=Directory.Exists(root),Components=new[]{
    new InstallerComponent{Directory="ocr-engine",Name="增强文字识别",Description="本地识别密集文字、小字和多语言；不选时使用基础识别。"},
    new InstallerComponent{Directory="scroll-capture",Name="滚动截屏",Description="拼接网页和长内容；普通截图不受影响。"},
    new InstallerComponent{Directory="file-conversion",Name="文件格式转换",Description="图片、PDF 等转换核心；视频、Office 等引擎仍可在设置中按需安装。"}
   }};
   using(var key=Registry.CurrentUser.OpenSubKey(InstalledApplicationState.LocationKey))if(key!=null&&key.GetValue("ProgramRoot")!=null)plan.LocationLocked=true;
   if(active!=null){string folder=Path.GetDirectoryName(InstalledApplicationState.ResolveExecutable(root,active));foreach(var item in plan.Components)item.Selected=Directory.Exists(Path.Combine(folder,item.Directory));}
   using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("MoriDesk.Payload.zip")){
    if(stream==null)throw new InvalidDataException("安装包缺少程序。");
    using(var zip=new ZipArchive(stream,ZipArchiveMode.Read))foreach(var entry in zip.Entries){var item=plan.Components.FirstOrDefault(c=>entry.FullName.Replace('\\','/').StartsWith(c.Directory+"/",StringComparison.OrdinalIgnoreCase));if(item==null)plan.CoreBytes=checked(plan.CoreBytes+entry.Length);else item.Bytes=checked(item.Bytes+entry.Length);}
   }
   return plan;
  }
  internal static string ValidateInstallLocation(string location,string existing,bool locked,IEnumerable<string> protectedPaths){
   string path=InstalledApplicationState.ValidateLocation(location);PlainPath(path);
   if(locked&&!SamePath(path,existing))throw new InvalidDataException("已安装的 Nook 会在原位置更新，避免产生两套程序。");
   foreach(string protectedPath in protectedPaths){string protect=Path.GetFullPath(protectedPath).TrimEnd('\\','/');if(SamePath(path,protect)||path.StartsWith(protect+"\\",StringComparison.OrdinalIgnoreCase)||protect.StartsWith(path+"\\",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("程序位置不能与素材、设置或转换结果目录重叠。");}
   if(!locked&&(File.Exists(path)||Directory.Exists(path)&&Directory.EnumerateFileSystemEntries(path).Any()))throw new InvalidDataException("请选择空的 Nook 文件夹，现有文件不会被覆盖。");
   var drive=new DriveInfo(Path.GetPathRoot(path));if(!drive.IsReady||drive.DriveType!=DriveType.Fixed)throw new InvalidDataException("请选择可用的本机固定磁盘。");
   return path;
  }
  internal static void ValidatePayloadPathBudget(string location,string version,IEnumerable<string> entries){
   string folder=Path.Combine(Path.GetFullPath(location).TrimEnd('\\','/'),CandidateDirectoryName(version));
   const string tooLong="安装位置过长，请返回选择较短的目录（例如 D:\\Nook）。";
   if(folder.Length>=248)throw new InvalidDataException(tooLong);
   foreach(string name in entries){
    string relative=name.Replace('/','\\');bool directory=relative.EndsWith("\\",StringComparison.Ordinal);if(directory)relative=relative.TrimEnd('\\');
    if(string.IsNullOrWhiteSpace(relative)||Path.IsPathRooted(relative)||relative.IndexOfAny(new[]{':','\0','\r','\n'})>=0)throw new InvalidDataException("安装包路径异常。");
    foreach(string part in relative.Split('\\'))if(part.Length==0||part=="."||part==".."||part.EndsWith(".",StringComparison.Ordinal)||part.EndsWith(" ",StringComparison.Ordinal))throw new InvalidDataException("安装包路径不规范。");
    // Budget before Path.GetFullPath/FileStream can throw a raw MAX_PATH
    // failure. Include unselected components so adding one later is safe.
    int length=checked(folder.Length+1+relative.Length),separator=relative.LastIndexOf('\\');
    int parent=directory?length:separator<0?folder.Length:folder.Length+1+separator;
    if((!directory&&length>=260)||parent>=248)throw new InvalidDataException(tooLong);
   }
  }
  internal static void ValidatePlanPayload(InstallerPlan plan,string location,Stream source){
   if(source==null)throw new InvalidDataException("安装包缺少程序。");
   using(var zip=new ZipArchive(source,ZipArchiveMode.Read,true))ValidatePayloadPathBudget(location,Version,zip.Entries.Select(entry=>entry.FullName));
  }
  static string ValidatePlan(InstallerPlan plan){
   string location=ValidateInstallLocation(plan.Location,ProgramRoot,plan.LocationLocked,StageProtectedPaths(false));
   using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("MoriDesk.Payload.zip"))ValidatePlanPayload(plan,location,stream);
   return location;
  }
  // Separate per-user location gate closes the two-first-installers race. It
  // is released with the attempt, before any Retry/Finish user interaction.
  static int WithSelectedLocation(Func<int> action){
   string sid;using(var user=WindowsIdentity.GetCurrent())sid=user.User.Value;
   using(var mutex=new Mutex(false,"Global\\Nook.InstallLocation."+sid)){
    bool held=false;try{try{held=mutex.WaitOne(0);}catch(AbandonedMutexException){held=true;}if(!held)throw new IOException("另一个安装器正在工作，请稍后重试。");
     string current=ProgramRoot,path=ValidatePlan(selectedPlan);
     if(!SamePath(current,path)){
      if(selectedPlan.LocationLocked||Directory.Exists(current)&&Directory.EnumerateFileSystemEntries(current).Any())throw new IOException("安装位置已变化，请重新打开安装器。");
      using(var key=Registry.CurrentUser.CreateSubKey(InstalledApplicationState.LocationKey))key.SetValue("ProgramRoot",path,RegistryValueKind.String);
      selectedPlan.LocationLocked=true;
     }
     return action();
    }finally{if(held)mutex.ReleaseMutex();}
   }
  }
  static bool ComponentsChanged(InstalledApplicationDescriptor previous){
   if(previous==null||selectedPlan==null)return false;
   string folder=Path.GetDirectoryName(InstalledApplicationState.ResolveExecutable(ProgramRoot,previous));
   return selectedPlan.Components.Any(c=>Directory.Exists(Path.Combine(folder,c.Directory))!=c.Selected);
  }
  internal static InstallCleanupView ParseCleanupProgress(string text,string executable,string version){
   if(string.IsNullOrWhiteSpace(text)||text.Length>512*1024)throw new InvalidDataException("清理记录过大或为空。");
   var value=new JavaScriptSerializer{MaxJsonLength=512*1024,RecursionLimit=8}.Deserialize<InstallCleanupView>(text);
   if(value==null||value.schemaVersion!=1||value.version!=version||!SamePath(value.executable,executable)||value.lines==null||value.lines.Length>500||value.sequence<0)throw new InvalidDataException("清理记录不属于本次安装。");
   long prior=0;foreach(var line in value.lines){if(line==null||line.sequence<=prior||line.sequence>value.sequence||string.IsNullOrWhiteSpace(line.text)||line.text.Length>600)throw new InvalidDataException("清理记录无效。");prior=line.sequence;}
   if(value.finished&&(string.IsNullOrWhiteSpace(value.summary)||value.summary.Length>4000||!new[]{"complete","partial","deferred","review-required","failed","not-applicable"}.Contains(value.state)))throw new InvalidDataException("清理结果无效。");
   return value;
  }
  internal static InstallCleanupView ReadCleanupProgress(Func<string> read,string executable,string version){
   string text;
   try{text=read();}
   catch(FileNotFoundException){return null;}
   catch(DirectoryNotFoundException){return null;}
   catch(IOException error){int code=System.Runtime.InteropServices.Marshal.GetHRForException(error)&0xffff;if(code==32||code==33)return null;throw;}
   return text==null?null:ParseCleanupProgress(text,executable,version);
  }
  static void WaitForCleanup(){
   if(cleanupSession==null)return;
   // Replacement lease is already disposed. The new app can now take its own
   // health-gated maintenance lease; the installer only observes this session.
   InstallerProgress.Report("正在检查并清理旧程序…");InstallerProgress.Percent(92);
   var active=InstalledApplicationState.Read(ProgramRoot);string executable=InstalledApplicationState.ResolveExecutable(ProgramRoot,active);
   var clock=Stopwatch.StartNew();var idle=Stopwatch.StartNew();long last=0;
   // Keep displaying work that is still advancing. A large verified payload
   // can legitimately take longer than 90 seconds to hash and remove.
   while(clock.ElapsedMilliseconds<30*60*1000&&idle.ElapsedMilliseconds<90000){
    string path=Path.Combine(cleanupSession,InstallCleanupProgress.FileName);
    var value=ReadCleanupProgress(()=>InstalledCleanupState.ReadReplaceablePhysicalText(path,512*1024),executable,Version+".0");
    if(value!=null){
     foreach(var line in value.lines)if(line.sequence>last){InstallerProgress.Log(line.text);last=line.sequence;idle.Restart();}
     if(value.finished){InstallerProgress.Result(value.state=="complete"?"安装完成":"安装完成，部分清理暂缓",value.summary,false,value.state!="complete");return;}
    }
    Thread.Sleep(200);
   }
   InstallerProgress.Result("安装完成，清理待确认","新版已就绪，尚未收到完整清理结果；旧文件暂时保留，下次启动会继续检查。",false,true);
  }
 }
}
