using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MoriDeskInstaller;

namespace MoriDesk {
 // Synthetic evidence only. Never calls Queue, opens a real installation,
 // acquires a system mutex, changes the desktop, or deletes any file.
 internal static class InstalledVersionMaintenanceTests {
  static InstalledMaintenanceAttempt Deferred(string reason){return new InstalledMaintenanceAttempt("deferred",reason);}
  static InstalledMaintenanceAttempt Complete(){return new InstalledMaintenanceAttempt("complete","no-old-versions",new InstalledCleanupResult());}
  static InstalledCleanupResult Occupied(){var result=new InstalledCleanupResult();result.Pending.Add(new InstalledCleanupItem("synthetic-old-version","diagnostic text is not a policy"){Retry=InstalledCleanupRetry.FileInUse});return result;}
  internal static void Run(Action<bool,string> check){
   ErrorClassification(check);BoundedRetry(check);InitialIdentityRead(check);Revalidation(check);Reports(check);
  }
  static void ErrorClassification(Action<bool,string> check){
   foreach(int code in new[]{32,33}){
    check(InstalledVersionCleanup.RetryFor(new Win32Exception(code))==InstalledCleanupRetry.FileInUse,"native sharing/lock error has a structured retry reason: "+code);
    check(InstalledVersionCleanup.RetryFor(new IOException("arbitrary language",new Win32Exception(code)))==InstalledCleanupRetry.FileInUse,"native error wrapped by the cleanup handle helper remains recoverable: "+code);
    check(InstalledVersionCleanup.RetryFor(new IOException("arbitrary language",unchecked((int)0x80070000)|code))==InstalledCleanupRetry.FileInUse,"managed I/O sharing/lock HResult is recognized: "+code);
   }
   foreach(Exception error in new Exception[]{null,new IOException("文件正在使用或无法读取，旧版本稍后重试。"),new IOException("ERROR_SHARING_VIOLATION"),new Win32Exception(5),new IOException("access",new Win32Exception(5)),new UnauthorizedAccessException(),new InvalidDataException("corrupt ownership",new Win32Exception(32)),new ArgumentException("unknown path",new IOException("locked",new Win32Exception(32)))})
    check(InstalledVersionCleanup.RetryFor(error)==InstalledCleanupRetry.None,"unknown, access, path and ownership failures do not qualify via wording or nested exceptions");
  }
  static void BoundedRetry(Action<bool,string> check){
   foreach(string reason in new[]{"integration-not-ready","maintenance-busy","file-occupation"}){
    int attempts=0,delays=0;bool held=false;
    var result=InstalledVersionMaintenance.RunWithOneRetryAsync(()=>{held=true;try{attempts++;return attempts==1?Deferred(reason):Complete();}finally{held=false;}},()=>{check(!held,"retry delay starts only after synchronous attempt releases its resources: "+reason);delays++;return Task.FromResult(0);}).GetAwaiter().GetResult();
    check(attempts==2&&delays==1&&result.State=="complete","recoverable reason gets one fresh attempt: "+reason);
    attempts=delays=0;
    result=InstalledVersionMaintenance.RunWithOneRetryAsync(()=>{attempts++;return Deferred(reason);},()=>{delays++;return Task.FromResult(0);}).GetAwaiter().GetResult();
    check(attempts==2&&delays==1&&result.Reason==reason,"a still-busy second attempt is terminal for this launch: "+reason);
   }
   foreach(string reason in new[]{"installation-changed","update-not-committed","update-cancelled","maintenance-error","current-without-manifest","unknown-reason"}){
    int attempts=0,delays=0;
    var result=InstalledVersionMaintenance.RunWithOneRetryAsync(()=>{attempts++;return Deferred(reason);},()=>{delays++;return Task.FromResult(0);}).GetAwaiter().GetResult();
    check(attempts==1&&delays==0&&result.Reason==reason,"nonrecoverable evidence is preserved without automatic repetition: "+reason);
   }
   var pending=Occupied();
   check(InstalledVersionMaintenance.CanRetry(new InstalledMaintenanceAttempt("deferred","entries-pending",pending)),"explicit file occupation qualifies a pending cleanup pass");
   check(!InstalledVersionMaintenance.CanRetry(new InstalledMaintenanceAttempt("failed","entries-pending",pending)),"a failed result cannot be promoted to transient solely by its item marker");
   pending.Pending.Add(new InstalledCleanupItem("unknown-version","corrupt record"));
   check(!InstalledVersionMaintenance.CanRetry(new InstalledMaintenanceAttempt("deferred","entries-pending",pending)),"mixed recoverable and unverified items stop automatic repetition");
   pending=Occupied();pending.Skipped.Add(new InstalledCleanupItem("legacy-version","legacy-version-without-manifest"));
   check(!InstalledVersionMaintenance.CanRetry(new InstalledMaintenanceAttempt("deferred","entries-pending",pending)),"skipped unowned entries never qualify through another occupied file");
   int delayedAttempts=0;var release=new TaskCompletionSource<int>();
   var waiting=InstalledVersionMaintenance.RunWithOneRetryAsync(()=>{delayedAttempts++;return delayedAttempts==1?Deferred("maintenance-busy"):Complete();},()=>release.Task);
   check(delayedAttempts==1&&!waiting.IsCompleted,"a pending asynchronous delay returns control without starting another cleanup pass");
   release.SetResult(0);var finished=waiting.GetAwaiter().GetResult();
   check(delayedAttempts==2&&finished.State=="complete","only releasing the delayed continuation starts the one follow-up attempt");
  }
  static void InitialIdentityRead(Action<bool,string> check){
   foreach(int code in new[]{32,33})foreach(bool stillOccupied in new[]{false,true}){
    string baseline=null;int reads=0,deletions=0,delays=0;
    var result=InstalledVersionMaintenance.RunWithOneRetryAsync(()=>{
     try{
      string failure=InstalledVersionMaintenance.RevalidationFailure(ref baseline,()=>{reads++;if(reads==1||stillOccupied)throw new IOException("synthetic occupation",new Win32Exception(code));return "verified-generation";},()=>true,()=>false,()=>true);
      if(failure!=null)return Deferred(failure);deletions++;return Complete();
     }catch(Exception error){if(InstalledVersionCleanup.RetryFor(error)==InstalledCleanupRetry.FileInUse)return Deferred("file-occupation");throw;}
    },()=>{delays++;check(baseline==null&&deletions==0&&reads==1,"first occupied identity read does not establish authority or delete before retry: "+code);return Task.FromResult(0);}).GetAwaiter().GetResult();
    check(reads==2&&delays==1,"identity discovery shares the same maximum of two attempts: "+code);
    check(stillOccupied?baseline==null&&deletions==0&&result.Reason=="file-occupation":baseline=="verified-generation"&&deletions==1&&result.State=="complete","only a fully verified first identity can authorize cleanup; a repeated lock stops: "+code);
   }
   foreach(string unknown in new string[]{null,""," "}){
    string baseline=null;int reads=0,deletions=0,delays=0;bool rejected=false;
    try{InstalledVersionMaintenance.RunWithOneRetryAsync(()=>{
     string failure=InstalledVersionMaintenance.RevalidationFailure(ref baseline,()=>{reads++;return unknown;},()=>true,()=>false,()=>true);
     if(failure!=null)return Deferred(failure);deletions++;return Complete();
    },()=>{delays++;return Task.FromResult(0);}).GetAwaiter().GetResult();}catch(InvalidDataException){rejected=true;}
    check(rejected&&baseline==null&&reads==1&&deletions==0&&delays==0,"unknown initial identity neither authorizes deletion nor becomes a transient retry");
   }
  }
  static void Revalidation(Action<bool,string> check){
   foreach(string changed in new[]{"identity","commit","cancel","browser"}){
    string identity="generation-one",baseline=null;bool committed=true,cancelled=false,ready=false;int attempts=0,reads=0,deletions=0;
    var result=InstalledVersionMaintenance.RunWithOneRetryAsync(()=>{
     attempts++;string failure=InstalledVersionMaintenance.RevalidationFailure(ref baseline,()=>{reads++;return identity;},()=>committed,()=>cancelled,()=>ready);
     if(failure!=null)return Deferred(failure);deletions++;return Complete();
    },()=>{ready=true;if(changed=="identity")identity="generation-two";if(changed=="commit")committed=false;if(changed=="cancel")cancelled=true;if(changed=="browser")ready=false;return Task.FromResult(0);}).GetAwaiter().GetResult();
    string expected=changed=="identity"?"installation-changed":changed=="commit"?"update-not-committed":changed=="cancel"?"update-cancelled":"integration-not-ready";
    check(attempts==2&&reads==2&&deletions==0&&baseline=="generation-one"&&result.Reason==expected,"follow-up preserves the first successful identity baseline and vetoes stale identity/commit/cancellation/browser authority: "+changed);
   }
   int calls=0;bool invalid=false;string known="known";
   try{InstalledVersionMaintenance.RunWithOneRetryAsync(()=>{calls++;return calls==1?Deferred("integration-not-ready"):Deferred(InstalledVersionMaintenance.RevalidationFailure(ref known,()=>{throw new InvalidDataException("unknown owner");},()=>true,()=>false,()=>true));},()=>Task.FromResult(0)).GetAwaiter().GetResult();}catch(InvalidDataException){invalid=true;}
   check(invalid&&calls==2,"corrupt installation evidence on follow-up is not swallowed or converted into another retry");
   int passes=0;
   // Build valid JSON via serialization so backslashes remain literal paths.
   Func<string,string> settings=output=>new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{LibraryPath=@"D:\Synthetic\Library",FileConversionOutputPath=output});
   string saved=settings(@"D:\Synthetic\Output");string[] lastPaths=null;
   InstalledVersionMaintenance.RunWithOneRetryAsync(()=>{passes++;lastPaths=InstalledVersionMaintenance.ProtectedRoutes(@"C:\Synthetic\Settings",@"D:\Synthetic\Library",saved,@"D:\Synthetic\Default",new[]{@"D:\Synthetic\Components"},path=>{});return passes==1?new InstalledMaintenanceAttempt("deferred","entries-pending",Occupied()):Complete();},()=>{saved=settings(@"D:\Synthetic\MovedOutput");return Task.FromResult(0);}).GetAwaiter().GetResult();
   check(passes==2&&lastPaths.Contains(@"D:\Synthetic\MovedOutput")&&!lastPaths.Contains(@"D:\Synthetic\Output"),"follow-up route evidence reflects a saved output change rather than the first attempt's paths");
  }
  static void Reports(Action<bool,string> check){
   var first=Occupied();first.Deleted.Add(new InstalledCleanupItem("already-removed","deleted"));int attempts=0;
   var result=InstalledVersionMaintenance.RunWithOneRetryAsync(()=>{if(++attempts==1)return new InstalledMaintenanceAttempt("partial","entries-pending",first);var last=new InstalledCleanupResult();last.Deleted.Add(new InstalledCleanupItem("synthetic-old-version","deleted"));return new InstalledMaintenanceAttempt("complete","verified-old-versions-removed",last);},()=>Task.FromResult(0)).GetAwaiter().GetResult();
   check(result.State=="complete"&&result.Result.Deleted.Count==2&&result.Result.Pending.Count==0,"final cleanup report retains both passes' deletions and clears resolved pending entries");
   attempts=0;result=InstalledVersionMaintenance.RunWithOneRetryAsync(()=>++attempts==1?new InstalledMaintenanceAttempt("partial","entries-pending",first):Deferred("installation-changed"),()=>Task.FromResult(0)).GetAwaiter().GetResult();
   check(result.State=="partial"&&result.Result.Deleted.Count==1&&result.Result.Pending.Count==1&&result.Reason=="installation-changed","a vetoed follow-up preserves partial progress without claiming all entries were removed");
   string report=InstalledVersionMaintenance.ReportJson(result.State,result.Reason,result.Result);
   check(InstalledVersionMaintenance.SummaryFromReport(report).Contains("已清理 1 个旧版本"),"partial summary remains readable for a changed-identity follow-up");
   report=InstalledVersionMaintenance.ReportJson("deferred","integration-not-ready",null);
   check(InstalledVersionMaintenance.SummaryFromReport(report).Contains("下次启动"),"terminal browser wait explains the bounded follow-up rather than promising unlimited background cleanup");
  }
 }
}
