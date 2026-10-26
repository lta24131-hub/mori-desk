using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using MoriDesk;

namespace MoriDeskInstaller {
 internal sealed class ReplacementStage {
  internal int SchemaVersion;
  internal string Directory,Version,PayloadSha256,ExecutableSha256,ManifestSha256,Phase;
 }
 static partial class Program {
  const string StageRecordName="staging.json";
  internal const string StageActivationFenceName="activation-attempted.json";
  static readonly HashSet<string> StageActivationAttempts=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  static readonly Regex StageDirectory=new Regex(@"\A([0-9]+\.[0-9]+\.[0-9]+)-([0-9]{17})-([0-9a-f]{8})\z",RegexOptions.CultureInvariant);
  static readonly Regex StageDigest=new Regex(@"\A[0-9a-f]{64}\z",RegexOptions.CultureInvariant);
  internal static ReplacementStage ParseStageRecord(string text){
   if(string.IsNullOrWhiteSpace(text)||text.Length>4096)throw new InvalidDataException("更新暂存记录大小无效。");
   var value=InstalledApplicationState.ParseFlatRecord(text);string[] keys={"schemaVersion","directory","version","payloadSha256","executableSha256","manifestSha256","phase"};
   if(value.Count!=keys.Length||keys.Any(key=>!value.ContainsKey(key))||!(value["schemaVersion"] is int)||((int)value["schemaVersion"]!=1&&(int)value["schemaVersion"]!=2)||keys.Skip(1).Any(key=>!(value[key] is string)))throw new InvalidDataException("更新暂存记录格式无效。");
   var record=new ReplacementStage{SchemaVersion=(int)value["schemaVersion"],Directory=(string)value["directory"],Version=(string)value["version"],PayloadSha256=(string)value["payloadSha256"],ExecutableSha256=(string)value["executableSha256"],ManifestSha256=(string)value["manifestSha256"],Phase=(string)value["phase"]};
   var folder=StageDirectory.Match(record.Directory);DateTime timestamp;Version version;
   if(!folder.Success||!System.Version.TryParse(record.Version,out version)||version.ToString(3)!=record.Version||folder.Groups[1].Value!=record.Version||!DateTime.TryParseExact(folder.Groups[2].Value,"yyyyMMddHHmmssfff",CultureInfo.InvariantCulture,DateTimeStyles.None,out timestamp)||!StageDigest.IsMatch(record.PayloadSha256)||(record.Phase!="extracting"&&record.Phase!="sealed")||(record.Phase=="sealed"?(!StageDigest.IsMatch(record.ExecutableSha256)||!StageDigest.IsMatch(record.ManifestSha256)):(record.ExecutableSha256!=""||record.ManifestSha256!="")))throw new InvalidDataException("更新暂存记录身份无效。");
   return record;
  }
  static string PlannedCandidate(){return Path.Combine(ProgramRoot,Version+"-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff",CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N").Substring(0,8),"Mori.exe");}
  static string ReadStageText(string session){
   UpdateSession(Path.Combine(session,"request.json"),UpdatesRoot);string file=Path.Combine(session,StageRecordName);PlainPath(file);
   using(var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read)){
    if(stream.Length<1||stream.Length>4096)throw new InvalidDataException("更新暂存记录大小无效。");
    using(var reader=new StreamReader(stream,new UTF8Encoding(false,true),false))return reader.ReadToEnd();
   }
  }
  static void RecordStage(UpdateRequest request,string candidate,string payloadDigest,bool sealedPayload){
   UpdateSession(request.RequestPath,UpdatesRoot);string folder=Path.GetDirectoryName(candidate);
   if(!SamePath(Path.GetDirectoryName(folder),ProgramRoot))throw new InvalidDataException("暂存目录越出安装位置。");
   string text=new JavaScriptSerializer().Serialize(new{schemaVersion=2,directory=Path.GetFileName(folder),version=Version,payloadSha256=payloadDigest,executableSha256=sealedPayload?Hash(candidate).ToLowerInvariant():"",manifestSha256=sealedPayload?Hash(Path.Combine(folder,InstalledVersionCleanup.ManifestName)).ToLowerInvariant():"",phase=sealedPayload?"sealed":"extracting"});
   ParseStageRecord(text);string target=Path.Combine(request.SessionDirectory,StageRecordName);PlainPath(target);
   if(sealedPayload){var prior=ParseStageRecord(ReadStageText(request.SessionDirectory));if(prior.SchemaVersion!=2||prior.Phase!="extracting"||prior.Directory!=Path.GetFileName(folder)||prior.PayloadSha256!=payloadDigest)throw new IOException("更新暂存记录已变化，未认领文件。");}
   else if(File.Exists(target)||Directory.Exists(target))throw new IOException("本次暂存记录已使用。");
   string temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";
   try{byte[] bytes=new UTF8Encoding(false).GetBytes(text);using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(bytes,0,bytes.Length);output.Flush(true);}PlainPath(target);if(sealedPayload)File.Replace(temporary,target,null);else File.Move(temporary,target);}
   finally{if(File.Exists(temporary))File.Delete(temporary);}
  }
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern uint GetLongPathName(string path,StringBuilder output,uint size);
  static string ProtectedStagePath(string path){
   path=AppUpdateMetadataBackup.PlainPath(path).TrimEnd('\\','/');
   // Reject DOS short-name aliases too: lexical overlap checks must not miss
   // a protected library/output which resolves into an installation folder.
   for(string parent=path;!string.IsNullOrEmpty(parent);parent=Path.GetDirectoryName(parent)){
    FileAttributes attributes;try{attributes=File.GetAttributes(parent);}catch(FileNotFoundException){continue;}catch(DirectoryNotFoundException){continue;}
    if((attributes&FileAttributes.Directory)==0)throw new IOException("受保护保存位置不是目录。");
    var actual=new StringBuilder(32768);uint size=GetLongPathName(parent,actual,(uint)actual.Capacity);
    if(size==0||size>=actual.Capacity||!SamePath(actual.ToString(),parent))throw new IOException("保存位置的实际路径无法确认，未清理暂存文件。");return path;
   }
   throw new IOException("保存位置无法确认，未清理暂存文件。");
  }
  static string[] StageProtectedPaths(bool hasActive){
   string data=PhysicalUserContext.DefaultRoot,settings=Path.Combine(data,"settings.json");
   var paths=new List<string>{data,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"Nook 转换"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Nook","ConversionComponents")};
   if(File.Exists(settings)){
    string text=InstalledCleanupState.ReadPhysicalText(settings,4*1024*1024);paths.Add(ReadManualLibraryPath(text));
    var values=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(text);object output;
    if(values.TryGetValue("FileConversionOutputPath",out output)&&output!=null){if(!(output is string))throw new InvalidDataException("转换输出位置无效，未清理暂存文件。");if(!string.IsNullOrWhiteSpace((string)output))paths.Add((string)output);}
   }else if(hasActive)throw new IOException("已有安装缺少设置，未清理暂存文件。");
   return paths.Select(ProtectedStagePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
  }
  static bool MissingStagePath(string path){return InstalledVersionCleanup.ConfirmedMissing(path);}
  static void VerifyStageCandidate(string session,ReplacementStage record,string candidate){
   string path=Path.Combine(session,"candidate.json");string text=InstalledCleanupState.ReadPhysicalText(path,16384);
   var value=InstalledApplicationState.ParseFlatRecord(text);string[] fields={"schemaVersion","expectedVersion","executable","sha256"};
   if(value.Count!=fields.Length||fields.Any(field=>!value.ContainsKey(field))||!(value["schemaVersion"] is int)||(int)value["schemaVersion"]!=1||!Equals(value["expectedVersion"],record.Version)||!(value["executable"] is string)||!SamePath((string)value["executable"],candidate)||!(value["sha256"] is string)||!string.Equals((string)value["sha256"],record.ExecutableSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("暂存程序与原会话记录不同，未清理。");
  }
  internal static bool StageNeverActivated(string session,string original){
   var record=ParseStageRecord(original);
   // Schema 1 predates the durable activation fence. Absence of its result
   // cannot prove that it never committed (the process may have died after
   // switching active-install.json). Leave it to health-gated maintenance.
   if(record.SchemaVersion!=2||record.Phase!="sealed")return false;
   lock(StageActivationAttempts)if(StageActivationAttempts.Contains(Path.GetFullPath(session)))return false;
   if(InstalledCleanupState.ReadPhysicalText(Path.Combine(session,StageRecordName),4096)!=original)throw new IOException("暂存会话记录已改变。");
   // Presence of ANY result or fence is conservative, including malformed,
   // partial and failed writes. Never infer permission from missing fields.
   return MissingStagePath(Path.Combine(session,StageActivationFenceName))&&MissingStagePath(Path.Combine(session,"result.json"));
  }
  static void PersistActivationFence(string fence,byte[] bytes){using(var output=new FileStream(fence,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(bytes,0,bytes.Length);output.Flush(true);}}
  internal static void CommitStageWithFence(string session,string original,string candidate,string payloadDigest,Action commit,Action<string,byte[]> persistFence=null){
   if(commit==null)throw new ArgumentNullException("commit");
   var record=ParseStageRecord(original);
   if(!StageNeverActivated(session,original)||record.Directory!=Path.GetFileName(Path.GetDirectoryName(candidate))||record.PayloadSha256!=payloadDigest)throw new IOException("无法确认本次暂存版本尚未尝试启用，未切换当前版本。");
   VerifyStageCandidate(session,record,candidate);
   string fence=Path.Combine(session,StageActivationFenceName);PlainPath(fence);
   string text=new JavaScriptSerializer().Serialize(new{schemaVersion=1,directory=record.Directory,version=record.Version,payloadSha256=record.PayloadSha256,executableSha256=record.ExecutableSha256,manifestSha256=record.ManifestSha256});
   // This is intentionally never removed, including on write/commit failure.
   // Flush BEFORE publishing the active pointer; a crash or missing result
   // can then never reclassify an activated version as abandoned staging.
   byte[] bytes=new UTF8Encoding(false).GetBytes(text);
   // A disk/permission error may prevent even the first fence byte or error
   // result from being written. Also retain this session during this process's
   // finally cleanup. No pointer can commit without the durable fence below.
   lock(StageActivationAttempts)StageActivationAttempts.Add(Path.GetFullPath(session));
   (persistFence??PersistActivationFence)(fence,bytes);
   if(InstalledCleanupState.ReadPhysicalText(fence,4096)!=text||InstalledCleanupState.ReadPhysicalText(Path.Combine(session,StageRecordName),4096)!=original||!MissingStagePath(Path.Combine(session,"result.json")))throw new IOException("启用保护记录未能核实，未切换当前版本。");
   commit();
  }
  internal static bool RemoveNeverActivatedStage(string programRoot,string current,string session,string original,Func<string,InstalledAbandonedStageGate> inspect){
   if(inspect==null)throw new ArgumentNullException("inspect");
   var record=ParseStageRecord(original);string folder=Path.Combine(programRoot,record.Directory),candidate=Path.Combine(folder,"Mori.exe");
   if(!StageNeverActivated(session,original)||(current!=null&&SamePath(current,candidate)))return false;
   InstalledVersionCleanup.RemoveAbandonedStage(programRoot,current,folder,unused=>{
    if(!StageNeverActivated(session,original))throw new IOException("此版本已启用或状态无法确认，留待新版健康检查后清理。");
    VerifyStageCandidate(session,record,candidate);
    var gate=inspect(folder);if(gate==null)return null;
    gate.SessionOwnershipVerified=true;gate.Uncommitted=StageNeverActivated(session,original);
    gate.ExpectedManifestSha256=record.ManifestSha256;gate.ExpectedExecutableSha256=record.ExecutableSha256;
    return gate;
   });return true;
  }
  static void TryCleanupAbandonedStages(){
   // Called only inside the installation-wide lease. This is NOT an age/size
   // based temp-folder purge. Only a sealed, journal-owned exact payload can
   // be removed; incomplete extraction and unknown directories stay intact.
   try{
    PhysicalUserContext.RequireInstalledContext();if(!Directory.Exists(UpdatesRoot)||!Directory.Exists(ProgramRoot))return;
    int deleted=0,pending=0;string[] sessions=Directory.EnumerateDirectories(UpdatesRoot).Take(513).ToArray();
    if(sessions.Length>512)return; // Bounded work; never choose arbitrary victims.
    foreach(string session in sessions){
     Guid id;if(!Guid.TryParseExact(Path.GetFileName(session),"N",out id))continue;
     string marker=Path.Combine(session,StageRecordName);if(!File.Exists(marker))continue;
     try{
      string original=ReadStageText(session);var record=ParseStageRecord(original);string folder=Path.Combine(ProgramRoot,record.Directory),candidate=Path.Combine(folder,"Mori.exe");
      if(MissingStagePath(folder)&&MissingStagePath(InstalledVersionCleanup.JournalPath(ProgramRoot,folder)))continue;
      var active=InstalledApplicationState.Read(ProgramRoot);string current=active==null?null:InstalledApplicationState.ResolveExecutable(ProgramRoot,active);
      if(current!=null&&SamePath(current,candidate))continue;
      if(!StageNeverActivated(session,original)){pending++;continue;}
      bool removed=RemoveNeverActivatedStage(ProgramRoot,current,session,original,unused=>{
       if(ReadStageText(session)!=original)throw new IOException("暂存会话记录已改变。");
       VerifyStageCandidate(session,record,candidate);
       var latest=InstalledApplicationState.Read(ProgramRoot);string selected=latest==null?null:InstalledApplicationState.ResolveExecutable(ProgramRoot,latest);
       if((selected==null)!=(current==null)||(current!=null&&!SamePath(selected,current)))throw new IOException("当前安装记录已改变。");
       string[] protect=StageProtectedPaths(latest!=null);
       var snapshot=current==null?InstalledCleanupState.CaptureBeforeFirstCommit(candidate,ProgramRoot,protect):InstalledCleanupState.Capture(current,ProgramRoot,protect);
       return new InstalledAbandonedStageGate{SnapshotComplete=snapshot.Complete,ActiveExecutable=selected,RunningExecutables=snapshot.RunningExecutables,ReferencedExecutables=snapshot.ReferencedExecutables,ProtectedPaths=protect};
      });if(removed)deleted++;else pending++;
     }catch(Exception error){if(!(error is IOException)&&!(error is InvalidDataException)&&!(error is UnauthorizedAccessException)&&!(error is ArgumentException)&&!(error is System.ComponentModel.Win32Exception))throw;pending++;}
    }
    // This report contains counts only, never credentials or media paths.
    string text=new JavaScriptSerializer().Serialize(new{schemaVersion=1,utc=DateTime.UtcNow.ToString("O"),deletedCount=deleted,pendingCount=pending});
    string report=Path.Combine(UpdatesRoot,"staging-cleanup.json"),temporary=report+"."+Guid.NewGuid().ToString("N")+".tmp";
    try{byte[] bytes=Encoding.UTF8.GetBytes(text);using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(bytes,0,bytes.Length);output.Flush(true);}PlainPath(report);if(File.Exists(report))File.Replace(temporary,report,null);else File.Move(temporary,report);}
    finally{if(File.Exists(temporary))File.Delete(temporary);}
   }catch(Exception error){TraceStageCleanup(error);}
  }
  static void TraceStageCleanup(Exception error){System.Diagnostics.Trace.WriteLine("Nook staging cleanup deferred: "+error.GetType().Name);}
 }
}
