using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using MoriDeskInstaller;

namespace MoriDesk {
 // Successful real-main rendering is the only caller. Portable/test/native
 // processes never invoke this service. Cleanup cannot authorize an upgrade.
 internal static class InstalledVersionMaintenance {
  static int scheduled;
  const int MaximumReportBytes=256*1024;
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern uint GetLongPathName(string path,StringBuilder output,uint size);
  internal static bool ExpectedVersionMatches(string expectedVersion,Version runningVersion){
   Version expected;
   return runningVersion!=null&&Version.TryParse(expectedVersion,out expected)&&expected.Build>=0&&
    new Version(expected.Major,expected.Minor,expected.Build,Math.Max(0,expected.Revision))==
    new Version(runningVersion.Major,runningVersion.Minor,Math.Max(0,runningVersion.Build),Math.Max(0,runningVersion.Revision));
  }
  internal static bool CommittedUpdate(string text,string expectedVersion){
   // A stale session/environment cannot authorize another running build.
   if(!ExpectedVersionMatches(expectedVersion,typeof(Entry).Assembly.GetName().Version)||string.IsNullOrWhiteSpace(text)||text.Length>16384)return false;
   try{
    var record=new JavaScriptSerializer{MaxJsonLength=16384,RecursionLimit=8}.Deserialize<Dictionary<string,object>>(text);object schema,status,expected;
    return record!=null&&record.TryGetValue("schemaVersion",out schema)&&schema is int&&(int)schema==1&&record.TryGetValue("status",out status)&&status is string&&(string)status=="installed"&&record.TryGetValue("expectedVersion",out expected)&&expected is string&&(string)expected==expectedVersion;
   }catch(ArgumentException){return false;}catch(InvalidOperationException){return false;}
  }
  internal static async Task<bool> WaitForCommitAsync(bool updateLaunch,Func<string> readResult,string expectedVersion,Func<bool> cancelled,Func<Task> delay,int attempts){
   if(readResult==null||cancelled==null||delay==null||attempts<1||attempts>120)throw new ArgumentException("Invalid cleanup wait.");
   if(cancelled())return false;
   if(!updateLaunch)return true;
   if(!ExpectedVersionMatches(expectedVersion,typeof(Entry).Assembly.GetName().Version))return false;
   for(int n=0;n<attempts;n++){
    if(cancelled())return false;string result=readResult();
    if(result!=null)return CommittedUpdate(result,expectedVersion)&&!cancelled();
    await delay().ConfigureAwait(false);
   }
   return false;
  }
  internal static string[] ProtectedRoutes(string dataRoot,string liveLibrary,string settingsText,string defaultOutput,IEnumerable<string> componentPaths,Action<string> validate){
   if(validate==null||string.IsNullOrWhiteSpace(settingsText)||settingsText.Length>1024*1024||componentPaths==null)throw new InvalidDataException("protected-routes-unavailable");
   var settings=new JavaScriptSerializer{MaxJsonLength=1024*1024,RecursionLimit=16}.Deserialize<Dictionary<string,object>>(settingsText);object library,output;
   if(settings==null||!settings.TryGetValue("LibraryPath",out library)||!(library is string)||string.IsNullOrWhiteSpace((string)library))throw new InvalidDataException("saved-library-unavailable");
   // Missing legacy output preference has a defined default. Malformed
   // non-string preferences cannot silently become that default for cleanup.
   string outputPath=defaultOutput;
   if(settings.TryGetValue("FileConversionOutputPath",out output)){
    if(output!=null&&!(output is string))throw new InvalidDataException("saved-output-unavailable");
    if(!string.IsNullOrWhiteSpace(output as string))outputPath=(string)output;
   }
   var components=componentPaths.ToArray();if(components.Length==0)throw new InvalidDataException("component-paths-unavailable");
   var paths=new[]{dataRoot,liveLibrary,(string)library,outputPath}.Concat(components).Select(ProtectionPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
   foreach(string path in paths)validate(path);
   return paths;
  }
  static string ProtectionPath(string path){
   string normalized=InstalledCleanupState.LocalPath(path);
   // Inspect the input before GetFullPath strips Windows trailing dots/spaces.
   if(path.Replace('/','\\').Substring(3).Split('\\').Any(part=>part.EndsWith(".",StringComparison.Ordinal)||part.EndsWith(" ",StringComparison.Ordinal)))throw new InvalidDataException("ambiguous-protected-path");
   return normalized;
  }
  internal static void ValidateProtectedPath(string path){
   path=ProtectionPath(path);
   AppUpdateCoordinator.PlainPath(path);
   // A DOS short-name alias can otherwise evade lexical overlap checks. A
   // missing leaf is valid only when its nearest existing ancestor is known.
   for(string ancestor=path;!string.IsNullOrEmpty(ancestor);ancestor=Path.GetDirectoryName(ancestor)){
    FileAttributes attributes;try{attributes=File.GetAttributes(ancestor);}catch(FileNotFoundException){continue;}catch(DirectoryNotFoundException){continue;}
    if((attributes&FileAttributes.Directory)==0)throw new InvalidDataException("protected-route-is-not-directory");
    var actual=new StringBuilder(32768);uint count=GetLongPathName(ancestor,actual,(uint)actual.Capacity);
    if(count==0||count>=actual.Capacity||!string.Equals(InstalledCleanupState.LocalPath(actual.ToString()),InstalledCleanupState.LocalPath(ancestor),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("unverified-protected-route");
    return;
   }
   throw new InvalidDataException("protected-route-unavailable");
  }
  internal static InstalledCleanupGate CleanupGate(string executable,InstalledCleanupSnapshot state,bool ready,string[] protectedPaths){
   if(state==null||protectedPaths==null||protectedPaths.Length==0)throw new InvalidDataException("cleanup-evidence-unavailable");
   // Preserve ancestors and outside roots as well: the engine owns the
   // bidirectional overlap check, not the executable-reference snapshot.
   return new InstalledCleanupGate{StartupReady=ready,ReadyExecutable=executable,EntrypointsSwitched=state.EntrypointsSwitched,SnapshotComplete=state.Complete,RunningExecutables=state.RunningExecutables,ReferencedExecutables=state.ReferencedExecutables,ProtectedPaths=(string[])protectedPaths.Clone()};
  }
  internal static void Queue(string updateRequest,string expectedVersion,string libraryPath,Action<string> notify,Func<bool> integrationReady=null){
   if(!Entry.InteractiveShell||!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MORI_DESK_DATA"))||Interlocked.CompareExchange(ref scheduled,1,0)!=0)return;
   // Low-priority maintenance must never hide/terminate the user's main window.
   Task.Run(async ()=>{
    string local=null;
    try{
     PhysicalUserContext.RequireDefault();local=Path.GetDirectoryName(PhysicalUserContext.DefaultRoot);
     string executable=typeof(Entry).Assembly.Location,root=Path.Combine(local,"Programs","MoriDesk");
     if(!OpenWithRegistration.ManagedPath(executable,root)){Publish(local,"not-applicable","unmanaged-install",null,notify);return;}
     if(!File.Exists(Path.Combine(Path.GetDirectoryName(executable),InstalledVersionCleanup.ManifestName))){Publish(local,"review-required","current-without-manifest",null,notify);return;}
     bool updateLaunch=!string.IsNullOrEmpty(updateRequest)||!string.IsNullOrEmpty(expectedVersion);string session=null;
     if(updateLaunch){
      if(string.IsNullOrEmpty(updateRequest)||string.IsNullOrEmpty(expectedVersion)){Publish(local,"deferred","incomplete-update-session",null,notify);return;}
      if(!ExpectedVersionMatches(expectedVersion,typeof(Entry).Assembly.GetName().Version)){Publish(local,"deferred","running-version-mismatch",null,notify);return;}
      session=AppUpdateCoordinator.StartupSession(updateRequest,Path.Combine(local,"MoriDesk","updates"));
     }
     Func<bool> cancelled=()=>session!=null&&File.Exists(Path.Combine(session,"cancel.request"));
     Func<string> read=()=>session==null?null:ReadCommit(Path.Combine(session,"result.json"));
     if(!await WaitForCommitAsync(updateLaunch,read,expectedVersion,cancelled,()=>Task.Delay(500),80).ConfigureAwait(false)){
      Publish(local,"deferred",cancelled()?"update-cancelled":"update-not-committed",null,notify);return;
     }
     // A rendered library is not proof that a configured browser bridge works.
     // Keep the old payload if postcommit connection recovery has not completed.
     if(integrationReady!=null){
      for(int attempt=0;attempt<40&&!integrationReady();attempt++)await Task.Delay(500).ConfigureAwait(false);
      if(!integrationReady()){Publish(local,"deferred","integration-not-ready",null,notify);return;}
     }
     // Explorer components are installed only after the real main window and
     // the committed installation are ready. Failure must not block the app
     // or authorize deleting any additional payload.
     try{ShellVisualRegistration.RepairCommittedInstallation(executable);}
     catch(Exception visualError){Trace.WriteLine("Nook file previews deferred: "+visualError.GetType().Name);}
     string sid;using(var identity=WindowsIdentity.GetCurrent())sid=identity.User.Value;
     using(var mutex=new Mutex(false,"Local\\Nook.VersionCleanup."+sid)){
      bool held=false;try{
       try{held=mutex.WaitOne(0);}catch(AbandonedMutexException){held=true;}
       if(!held){Publish(local,"deferred","maintenance-busy",null,notify);return;}
       var result=InstalledVersionCleanup.Cleanup(root,executable,old=>{
        // Re-read saved routes before EVERY deletion pass. Missing evidence
        // must fail closed, never become an empty protection list.
        string dataRoot=Entry.DataRoot;
        string saved=InstalledCleanupState.ReadPhysicalText(Path.Combine(dataRoot,"settings.json"),1024*1024);
        var paths=ProtectedRoutes(dataRoot,libraryPath,saved,Mori.Settings.DefaultConversionOutputPath,
         new[]{Path.Combine(local,"Nook","ConversionComponents")},ValidateProtectedPath);
        var state=InstalledCleanupState.Capture(executable,root,paths);
        return CleanupGate(executable,state,!cancelled()&&(integrationReady==null||integrationReady()),paths);
       },InstalledLegacyPayloadCatalog.TryCertify);
       Publish(local,StateFor(result),ReasonFor(result),result,notify);
      }finally{if(held)mutex.ReleaseMutex();}
     }
    }catch(Exception error){
     // No raw exception message, credential, document or full path in reports.
     Trace.WriteLine("Nook version cleanup deferred: "+error.GetType().Name);
     if(local!=null)Publish(local,"failed","maintenance-error",null,notify);
     else Notify(notify,"无法确认清理环境，未清理旧版本；当前版本和资料保留。");
    }
   });
  }
  static string ReadCommit(string file){
   AppUpdateCoordinator.PlainPath(file);if(!File.Exists(file))return null;
   return InstalledCleanupState.ReadPhysicalText(file,16384);
  }
  internal static string StateFor(InstalledCleanupResult result){
   if(result==null)throw new ArgumentNullException("result");
   if(result.Complete)return "complete";
   if(result.Deleted.Count>0)return "partial";
   return result.Pending.Count>0?"deferred":"review-required";
  }
  internal static string ReasonFor(InstalledCleanupResult result){
   if(result==null)throw new ArgumentNullException("result");
   if(result.Skipped.Any(item=>item.Reason=="legacy-version-without-manifest"))return "legacy-version-without-manifest";
   if(result.Pending.Count>0)return "entries-pending";
   if(result.Skipped.Count>0)return "entries-skipped";
   return result.Deleted.Count>0?"verified-old-versions-removed":"no-old-versions";
  }
  internal static string ReportJson(string state,string reason,InstalledCleanupResult result){
   return new JavaScriptSerializer().Serialize(new{schemaVersion=1,utc=DateTime.UtcNow.ToString("O"),version=typeof(Entry).Assembly.GetName().Version.ToString(),state=state,reason=reason,complete=state=="complete",deletedCount=result==null?0:result.Deleted.Count,pendingCount=result==null?0:result.Pending.Count,skippedCount=result==null?0:result.Skipped.Count});
  }
  internal static string SummaryFromReport(string text){
   const string invalid="上次清理状态无法确认，历史版本暂保留，请勿手动删除不明目录。";
   if(text==null)return "尚无旧版本清理记录。";
   if(string.IsNullOrWhiteSpace(text)||text.Length>MaximumReportBytes)return invalid;
   try{
    var report=new JavaScriptSerializer{MaxJsonLength=MaximumReportBytes,RecursionLimit=8}.Deserialize<Dictionary<string,object>>(text);object schema,state,reason,deleted,pending,skipped,complete;
    if(report==null||!report.TryGetValue("schemaVersion",out schema)||!(schema is int)||(int)schema!=1||!report.TryGetValue("state",out state)||!(state is string)||!report.TryGetValue("reason",out reason)||!(reason is string)||!report.TryGetValue("deletedCount",out deleted)||!Count(deleted)||!report.TryGetValue("pendingCount",out pending)||!Count(pending)||!report.TryGetValue("skippedCount",out skipped)||!Count(skipped)||!report.TryGetValue("complete",out complete)||!(complete is bool))return invalid;
    int removed=(int)deleted,waiting=(int)pending,review=(int)skipped;
    if((bool)complete!=((string)state=="complete"))return invalid;
    if(!new[]{"unmanaged-install","current-without-manifest","incomplete-update-session","running-version-mismatch","update-cancelled","update-not-committed","integration-not-ready","maintenance-busy","maintenance-error","legacy-version-without-manifest","entries-pending","entries-skipped","verified-old-versions-removed","no-old-versions"}.Contains((string)reason,StringComparer.Ordinal))return invalid;
    if((string)state!="complete"&&(string)state!="partial"&&removed!=0)return invalid;
    if((string)state=="complete")return waiting!=0||review!=0?invalid:removed>0?"上次已清理 "+removed+" 个已核验的旧版本；资料未改动。":"已检查，无需清理旧版本。";
    if((string)state=="partial"&&removed>0&&(waiting>0||review>0))return "已清理 "+removed+" 个旧版本；其余版本仍在使用或待核对，已保留。";
    if((string)state=="review-required")return (string)reason=="current-without-manifest"?"当前安装缺少可核验清单，旧版本已保留，需核对后清理。":"部分历史版本缺少归属证明或需核对，已保留，不会直接删除。";
    if((string)state=="deferred"){
     if((string)reason=="integration-not-ready")return "浏览器连接尚未恢复，旧版本暂时保留；连接确认后再清理。";
     if((string)reason=="update-not-committed"||(string)reason=="incomplete-update-session"||(string)reason=="update-cancelled"||(string)reason=="running-version-mismatch")return "本次更新尚未确认完成，旧版本已保留，未执行清理。";
     return "旧版本清理暂缓，可能仍被使用或无法核验；已保留待核对。";
    }
    if((string)state=="failed")return "上次旧版本清理未完成，需核对后处理；当前版本仍可使用。";
    if((string)state=="not-applicable")return "当前不是标准安装位置，未自动清理历史版本。";
    return invalid;
   }catch(ArgumentException){return invalid;}catch(InvalidOperationException){return invalid;}
  }
  static bool Count(object value){return value is int&&(int)value>=0&&(int)value<=20000;}
  internal static string ReadSummary(){
   try{
    if(!Entry.InteractiveShell)return "后台与测试进程不读取正式版清理状态。";
    if(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MORI_DESK_DATA")))return "独立数据环境不读取正式版清理状态。";
    return SummaryFromReport(InstalledCleanupState.ReadPhysicalText(Path.Combine(PhysicalUserContext.DefaultRoot,"maintenance","version-cleanup.json"),MaximumReportBytes));
   }catch(FileNotFoundException){return SummaryFromReport(null);}catch(DirectoryNotFoundException){return SummaryFromReport(null);}
   catch{return "无法读取上次清理状态，未对旧版本执行任何操作。";}
  }
  static void Notify(Action<string> notify,string text){try{if(notify!=null)notify(text);}catch{}}
  static void Publish(string local,string state,string reason,InstalledCleanupResult result,Action<string> notify){
   PublishWith(state,reason,result,text=>SaveReport(local,text),notify);
  }
  // Test seam receives a report sink, not access to Queue or real cleanup.
  internal static void PublishWith(string state,string reason,InstalledCleanupResult result,Action<string> save,Action<string> notify){
   string text=ReportJson(state,reason,result);
   try{if(save==null)throw new ArgumentNullException("save");save(text);}
   catch(Exception error){Trace.WriteLine("Nook version cleanup report unavailable: "+error.GetType().Name);Notify(notify,"旧版本维护记录保存失败，请暂时保留历史目录；当前版本仍可使用。");return;}
   if(state!="complete"||result!=null&&result.Deleted.Count>0)Notify(notify,SummaryFromReport(text));
  }
  internal static void SaveReport(string local,string text){
   string folder=Path.Combine(local,"MoriDesk","maintenance");AppUpdateCoordinator.PlainPath(folder);Directory.CreateDirectory(folder);AppUpdateCoordinator.PlainPath(folder);
   string target=Path.Combine(folder,"version-cleanup.json"),temp=Path.Combine(folder,"version-cleanup."+Guid.NewGuid().ToString("N")+".tmp");AppUpdateCoordinator.PlainPath(target);
   byte[] bytes=new UTF8Encoding(false).GetBytes(text);
   try{
    using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(bytes,0,bytes.Length);output.Flush(true);}
    AppUpdateCoordinator.PlainPath(target);if(File.Exists(target))File.Replace(temp,target,null);else File.Move(temp,target);
   }finally{if(File.Exists(temp)){AppUpdateCoordinator.PlainPath(temp);File.Delete(temp);}}
  }
 }
}
