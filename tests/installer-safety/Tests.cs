using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Web.Script.Serialization;

namespace MoriDeskInstaller {
 internal static class InstallerSafetyTests {
  static int checks;
  static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
  static void Rejected(Action action,string message){bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}catch(InvalidDataException){rejected=true;}Check(rejected,message);}
  static Dictionary<string,object> UpdateData(string executable){return new Dictionary<string,object>{{"schemaVersion",1},{"parentProcessId",20},{"parentExecutable",executable},{"parentStartedUtcTicks",638960000000000000L},{"expectedVersion",Program.InstallerVersion},{"relaunch",true}};}
  static string Json(Dictionary<string,object> data){return new JavaScriptSerializer().Serialize(data);}
  static Dictionary<string,object> BackupData(){return new Dictionary<string,object>{{"schemaVersion",1},{"completedUtc",new DateTime(2026,10,16,1,2,3,DateTimeKind.Utc).ToString("O",CultureInfo.InvariantCulture)},{"metadataOnly",true},{"sourceMediaIncluded",false},{"credentialsIncluded",false},{"bytes",1024}};}
  static Dictionary<string,object> StartupData(string executable){return new Dictionary<string,object>{{"schemaVersion",1},{"expectedVersion",Program.InstallerVersion},{"processId",42},{"startedUtcTicks",638960000000000001L},{"executable",executable}};}
  static void VersionChecks(){
   var assembly=typeof(Program).Assembly;var compiled=assembly.GetName().Version;
   var metadata=(AssemblyFileVersionAttribute)Attribute.GetCustomAttribute(assembly,typeof(AssemblyFileVersionAttribute));
   Check(compiled.Revision==0&&metadata!=null&&new Version(metadata.Version)==compiled,"installer assembly and file versions agree with the three-field update protocol");
   Check(Program.InstallerVersion==compiled.ToString(3),"runtime desired version comes from the compiled release, not a stale independent constant");
   var desired=new Version(Program.InstallerVersion+".0");
   Check(Program.ShouldReplace(new Version(0,1,31,0),desired,"previous-payload","new-payload",false),"the compiled release can replace the prior 0.1.31 installation");
   Check(Program.ShouldReplace(new Version(0,1,32,0),desired,"previous-candidate","new-payload",false),"the corrected release can replace the 0.1.32 candidate without bypassing version protection");
   Check(!Program.ShouldReplace(compiled,desired,"same-payload","same-payload",false),"the identical compiled release remains a no-op");
   Rejected(()=>Program.ShouldReplace(compiled,desired,"other-payload","new-payload",false),"equal-version different bytes remain rejected");
   Rejected(()=>Program.ShouldReplace(compiled,desired,"other-payload","new-payload",true),"repair cannot bypass equal-version payload protection");
   Rejected(()=>Program.ShouldReplace(new Version(compiled.Major,compiled.Minor,compiled.Build+1,0),desired,"future-payload","new-payload",false),"a genuinely newer installation still blocks downgrade");
   string root=@"C:\Nook-installer-version-fixture\Programs\MoriDesk",executable=Path.Combine(root,"previous","Mori.exe");
   string updates=@"C:\Nook-installer-version-fixture\updates",request=Path.Combine(updates,"0123456789abcdef0123456789abcdef","request.json");
   var data=UpdateData(executable);data["expectedVersion"]=compiled.ToString(3);
   Check(Program.ParseUpdateRequest(Json(data),request,updates,root,10).ExpectedVersion==compiled.ToString(3),"request version generated from release metadata is accepted by the runtime protocol");
  }
  static void StartupChecks(){
   string executable=@"C:\Generated\Programs\MoriDesk\0.1.24\Mori.exe";
   Action<string> verify=text=>Program.ValidateStartupReceipt(text,Program.InstallerVersion,executable,42,638960000000000001L);
   verify(Json(StartupData(executable)));Check(true,"startup receipt binds schema, version, process ID, start ticks and exact new executable");
   foreach(string key in new[]{"schemaVersion","expectedVersion","processId","startedUtcTicks","executable"}){var data=StartupData(executable);data.Remove(key);Rejected(()=>verify(Json(data)),"all startup receipt identity fields are required: "+key);}
   foreach(object value in new object[]{null,"1",0,2,1.5}){var data=StartupData(executable);data["schemaVersion"]=value;Rejected(()=>verify(Json(data)),"startup schema cannot be missing, coerced or future");}
   foreach(object value in new object[]{null,"",Program.InstallerVersion+".0","0.1.23","99.0.0",1}){var data=StartupData(executable);data["expectedVersion"]=value;Rejected(()=>verify(Json(data)),"another version cannot claim this update's startup");}
   foreach(object value in new object[]{null,"42",0,41,43,1.5}){var data=StartupData(executable);data["processId"]=value;Rejected(()=>verify(Json(data)),"other PID or coerced process ID cannot acknowledge the launched child");}
   foreach(object value in new object[]{null,"638960000000000001",638960000000000000L,638960000000000002L,0,-1,1.5}){var data=StartupData(executable);data["startedUtcTicks"]=value;Rejected(()=>verify(Json(data)),"PID reuse or missing/coerced process start time cannot acknowledge new startup");}
   foreach(object value in new object[]{null,"",@"C:\Other\Mori.exe",@"\\server\share\Mori.exe",executable+":stream",executable+"\" --bad"}){var data=StartupData(executable);data["executable"]=value;Rejected(()=>verify(Json(data)),"only exact launched executable may confirm startup");}
   {var data=StartupData(executable);data["healthy"]=true;Rejected(()=>verify(Json(data)),"unknown receipt fields are not a substitute for strict startup protocol");}
   foreach(string text in new[]{null,"","null","{broken",new string(' ',4097)})Rejected(()=>verify(text),"invalid or oversized startup receipt cannot report success");
   int reads=0,delays=0,elapsed=0,verifications=0;
   bool ready=Program.WaitForStartupReceipt(()=>{reads++;return reads==4?Json(StartupData(executable)):null;},()=>false,()=>false,ms=>{delays++;elapsed+=ms;},text=>{verifications++;verify(text);},30000);
   Check(ready&&reads==4&&delays==3&&elapsed==300&&verifications==1,"startup waits for valid rendered-main receipt rather than treating process survival as success");
   reads=delays=elapsed=verifications=0;
   ready=Program.WaitForStartupReceipt(()=>{reads++;return null;},()=>false,()=>false,ms=>{delays++;elapsed+=ms;},text=>verifications++,30000);
   Check(!ready&&reads==300&&delays==300&&elapsed==30000&&verifications==0,"startup error dialog staying alive for 30 seconds cannot mark update installed");
   reads=delays=0;ready=Program.WaitForStartupReceipt(()=>{reads++;return Json(StartupData(executable));},()=>true,()=>false,ms=>delays++,verify,30000);Check(!ready&&reads==0&&delays==0,"child exit prevents receipt inspection and successful update");
   ready=Program.WaitForStartupReceipt(()=>{reads++;return Json(StartupData(executable));},()=>false,()=>true,ms=>delays++,verify,30000);Check(!ready&&reads==0&&delays==0,"cancelled startup never accepts a stale receipt");
   int polls=0;ready=Program.WaitForStartupReceipt(()=>Json(StartupData(executable)),()=>++polls>1,()=>false,ms=>{},verify,30000);Check(!ready&&polls==2,"child exiting with its ready receipt is not considered installed");
   polls=0;ready=Program.WaitForStartupReceipt(()=>Json(StartupData(executable)),()=>false,()=>++polls>1,ms=>{},verify,30000);Check(!ready&&polls==2,"cancellation received together with startup receipt still rolls back update");
   Rejected(()=>Program.WaitForStartupReceipt(()=>"{bad",()=>false,()=>false,ms=>{},verify,30000),"malformed receipt cannot fall through to successful process-survival check");
   Rejected(()=>Program.WaitForStartupReceipt(()=>null,()=>false,()=>false,ms=>{},verify,30001),"startup wait is bounded, with no indefinite installer wait");
   string request=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MoriDesk","updates","0123456789abcdef0123456789abcdef","request.json");
   var start=Program.RelaunchStart(executable,request,Program.InstallerVersion);
   Check(!start.UseShellExecute&&start.Arguments==""&&start.EnvironmentVariables["NOOK_UPDATE_REQUEST"]==request&&start.EnvironmentVariables["NOOK_UPDATE_EXPECTED_VERSION"]==Program.InstallerVersion,"update launch passes only fixed per-child session/version environment without shell commands or extra launch modes");
   Rejected(()=>Program.RelaunchStart(executable,request,"99.0.0"),"update launch cannot spoof a different expected version");
   Rejected(()=>Program.RelaunchStart(executable,Path.Combine(Path.GetTempPath(),"request.json"),Program.InstallerVersion),"startup receipt request cannot point outside controlled updates root");
  }
  static void BackupChecks(){
   Program.ValidateMetadataBackupMarker(Json(BackupData()));Check(true,"only exact completed metadata-only backup marker is accepted");
   foreach(string key in new[]{"schemaVersion","completedUtc","metadataOnly","sourceMediaIncluded","credentialsIncluded","bytes"}){var data=BackupData();data.Remove(key);Rejected(()=>Program.ValidateMetadataBackupMarker(Json(data)),"missing backup completion field cannot authorize install: "+key);}
   foreach(object value in new object[]{null,"1",0,2,true,1.5}){var data=BackupData();data["schemaVersion"]=value;Rejected(()=>Program.ValidateMetadataBackupMarker(Json(data)),"backup marker schema is exactly numeric 1");}
   foreach(object value in new object[]{null,"","not a timestamp","2026-10-16T01:02:03","2026-10-16T01:02:03.0000000+08:00",42}){var data=BackupData();data["completedUtc"]=value;Rejected(()=>Program.ValidateMetadataBackupMarker(Json(data)),"backup completion must contain an exact roundtrip UTC timestamp");}
   foreach(string key in new[]{"metadataOnly","sourceMediaIncluded","credentialsIncluded"})foreach(object value in new object[]{null,"true",1,!(bool)BackupData()[key]}){var data=BackupData();data[key]=value;Rejected(()=>Program.ValidateMetadataBackupMarker(Json(data)),"metadata-only and excluded media/credentials flags cannot be coerced or reversed");}
   foreach(object value in new object[]{null,"1024",-1,1.5,128L*1024*1024+1,true}){var data=BackupData();data["bytes"]=value;Rejected(()=>Program.ValidateMetadataBackupMarker(Json(data)),"backup byte count must be a bounded nonnegative integer");}
   foreach(long value in new[]{0L,128L*1024*1024}){var data=BackupData();data["bytes"]=value;Program.ValidateMetadataBackupMarker(Json(data));Check(true,"backup byte-count boundary is accepted: "+value);}
   {var data=BackupData();data["directory"]=@"C:\Other";Rejected(()=>Program.ValidateMetadataBackupMarker(Json(data)),"backup marker cannot supply a different backup target");}
   foreach(string text in new[]{null,"","null","{broken",new string(' ',4097)})Rejected(()=>Program.ValidateMetadataBackupMarker(text),"malformed or oversized completion marker is rejected");
   int reads=0;bool rejected=false;
   try{Program.ValidatePostExitBackup(false,()=>false,()=>reads++);}catch(UpdateCancelledException){rejected=true;}Check(rejected&&reads==0,"backup cannot be inspected or installation authorized before parent has exited");
   rejected=false;try{Program.ValidatePostExitBackup(true,()=>true,()=>reads++);}catch(UpdateCancelledException){rejected=true;}Check(rejected&&reads==0,"exit arriving with cancellation never reads marker or installs");
   rejected=false;try{Program.ValidatePostExitBackup(true,()=>false,()=>{reads++;throw new FileNotFoundException("synthetic missing marker");});}catch(UpdateCancelledException){rejected=true;}Check(rejected&&reads==1,"manual exit before a completed backup cancels rather than reporting success");
   reads=0;Program.ValidatePostExitBackup(true,()=>false,()=>{reads++;Program.ValidateMetadataBackupMarker(Json(BackupData()));});Check(reads==1,"post-exit completed backup is verified exactly once");
   int polls=0;rejected=false;try{Program.ValidatePostExitBackup(true,()=>++polls>1,()=>reads++);}catch(UpdateCancelledException){rejected=true;}Check(rejected&&polls==2,"cancellation after marker validation still blocks installation");
   bool linked=false;try{Program.RequirePlainAttributes(FileAttributes.ReparsePoint|FileAttributes.Directory);}catch(IOException){linked=true;}Check(linked,"reparse-point backup directories cannot redirect marker verification");
   linked=false;try{Program.RequirePlainAttributes(FileAttributes.ReparsePoint|FileAttributes.Normal);}catch(IOException){linked=true;}Check(linked,"reparse-point marker files cannot redirect marker verification");
   Program.RequirePlainAttributes(FileAttributes.Normal);Check(true,"plain regular backup file attributes are accepted");
   string sandbox=Path.Combine(Path.GetTempPath(),"Nook-installer-marker-"+Guid.NewGuid().ToString("N")),session=Path.Combine(sandbox,Guid.NewGuid().ToString("N")),backup=Path.Combine(session,"metadata-backup");
   Directory.CreateDirectory(backup);string marker=Path.Combine(backup,"complete.json");File.WriteAllText(marker,Json(BackupData()));Program.RequireMetadataBackup(session);Check(true,"exact generated session-local metadata-backup/complete.json is accepted");
   using(var file=new FileStream(marker,FileMode.Open,FileAccess.Write))file.SetLength(4097);Rejected(()=>Program.RequireMetadataBackup(session),"marker over 4096 bytes is rejected before parsing");
   string absent=Path.Combine(sandbox,Guid.NewGuid().ToString("N"));bool missing=false;try{Program.RequireMetadataBackup(absent);}catch(IOException){missing=true;}Check(missing,"missing session marker cannot be mistaken for completed backup");
   Console.WriteLine("Generated marker fixtures retained at: "+sandbox);
  }
  public static int Main(string[] args){
   try{
    VersionChecks();
    foreach(string product in new[]{"Mori Desk","mori desk","MORI DESK","Nook","nook","NOOK"}){
     Check(Program.MatchesRunningProcess(20,1,product,"Mori.exe",10,1),"same-session legacy or new product blocks installation: "+product);
     Check(Program.MatchesRunningProcess(20,1,product,"mori.exe",10,1),"stable original-filename comparison is case insensitive: "+product);
     Check(!Program.MatchesRunningProcess(10,1,product,"Mori.exe",10,1),"installer never blocks on itself: "+product);
     Check(Program.MatchesRunningProcess(20,2,product,"Mori.exe",10,1),"an owned running process in another Windows session blocks replacement without being closed: "+product);
    }
    foreach(string product in new[]{"Mori Desk","Nook"})foreach(string filename in new[]{null,"","Nook.exe","NookApp.exe","Mori-Desk.exe","MoriDesk.exe","Mori.exe.old","Mori.exe "," Mori.exe","C:\\Other App\\Mori.exe"})
     Check(!Program.MatchesRunningProcess(20,1,product,filename,10,1),"same-named unrelated product or unknown original identity does not block installation");
    foreach(string product in new[]{null,"","Mori","MoriDesk","Luma","QingJie","Mori.exe","Mori Desk Helper","Mori Desk Installer","Not Mori Desk","Nook Helper","Nook Installer","Nook Setup","Nook.exe","Not Nook","Nook Desktop"," Nook","Nook ","Mori Desk "})
     Check(!Program.MatchesRunningProcess(20,1,product,"Mori.exe",10,1),"unrelated, unknown or approximate product does not block installation");
    // Synthetic absolute paths only: ManagedTarget never inspects a process,
    // reads a shortcut, or creates/removes any file or directory.
    string programs=@"C:\Nook-installer-path-fixture\Programs",programRoot=Path.Combine(programs,"MoriDesk");
    string managed=Path.Combine(programRoot,"0.1.19-20261014000000","Mori.exe");
    foreach(string product in new[]{"Mori Desk","Nook"}){
     Check(Program.ManagedTarget(managed,programRoot,product,"Mori.exe"),"controlled installation target accepts old or new product: "+product);
     Check(Program.ManagedTarget(managed,programRoot+"\\",product,"Mori.exe"),"controlled root trailing separator is accepted: "+product);
     Check(Program.ManagedTarget(managed.ToUpperInvariant(),programRoot.ToLowerInvariant(),product.ToLowerInvariant(),"mori.exe"),"managed target path and stable metadata are case insensitive: "+product);
     Check(Program.ManagedTarget(Path.Combine(programRoot,"unused","..","0.1.19-20261014000000","Mori.exe"),programRoot,product,"Mori.exe"),"normalization may remain safely inside the controlled root: "+product);
     foreach(string target in new[]{null,""," ",programRoot,Path.Combine(programRoot,"0.1.19-20261014000000","Nook.exe"),Path.Combine(programRoot,"0.1.19-20261014000000","Mori.exe.old"),Path.Combine(programs,"MoriDeskOther","0.1.19","Mori.exe"),Path.Combine(programs,"MoriDesk-backup","Mori.exe"),Path.Combine(programs,"Other App","Mori.exe"),Path.Combine(programRoot,"..","Other App","Mori.exe"),Path.Combine(programRoot,"version","..","..","Other App","Mori.exe"),@"C:\Elsewhere\Mori.exe"})
      Check(!Program.ManagedTarget(target,programRoot,product,"Mori.exe"),"outside, adjacent, escaping or incorrectly named shortcut target is not owned");
     foreach(string original in new[]{null,"","Nook.exe","MoriDesk.exe","Mori.exe.old"})
      Check(!Program.ManagedTarget(managed,programRoot,product,original),"managed path alone cannot establish the original product identity");
    }
    foreach(string product in new[]{null,"","Mori","Luma","QingJie","Nook Helper","Nook Installer","Other Nook","Nook "})
     Check(!Program.ManagedTarget(managed,programRoot,product,"Mori.exe"),"managed path and filename cannot claim an unrelated product");
    Check(!Program.ManagedTarget(managed,null,"Nook","Mori.exe"),"unknown controlled root is rejected without throwing");
    Check(!Program.ManagedTarget(managed,"","Nook","Mori.exe"),"empty controlled root is rejected without throwing");
    string updates=@"C:\Nook-installer-path-fixture\MoriDesk\updates",session=Path.Combine(updates,"0123456789abcdef0123456789abcdef"),requestFile=Path.Combine(session,"request.json");
    var request=Program.ParseUpdateRequest(Json(UpdateData(managed)),requestFile,updates,programRoot,10);
    Check(request.ParentExecutable==managed&&request.ParentProcessId==20&&request.ParentStartedUtcTicks==638960000000000000L&&request.ExpectedVersion==Program.InstallerVersion&&request.Relaunch,"valid local update request retains only the fixed protocol fields");
    Check(request.SessionDirectory==session&&request.RequestPath==requestFile,"update records are confined to a fresh single session directory");
    foreach(string path in new[]{null,"","request.json",@"\\server\share\request.json",Path.Combine(updates,"request.json"),Path.Combine(updates,"not-a-guid","request.json"),Path.Combine(updates,"0123456789abcdef0123456789abcdef","nested","request.json"),Path.Combine(updates+"-other","0123456789abcdef0123456789abcdef","request.json"),Path.Combine(session,"other.json"),requestFile+":stream"})Rejected(()=>Program.UpdateSession(path,updates),"update request rejects unsafe or non-session path");
    foreach(string path in new[]{"https://github.com/x/setup.exe",@"\\server\share\Mori.exe",@"C:\Other\Mori.exe",Path.Combine(programRoot,"version","Other.exe"),managed+"\" --bad",managed+":stream"}){var values=UpdateData(managed);values["parentExecutable"]=path;Rejected(()=>Program.ParseUpdateRequest(Json(values),requestFile,updates,programRoot,10),"update parent cannot be a URL, network path, unrelated executable, or command");}
    foreach(object value in new object[]{null,"1",0,2,true,1.5}){var values=UpdateData(managed);values["schemaVersion"]=value;Rejected(()=>Program.ParseUpdateRequest(Json(values),requestFile,updates,programRoot,10),"schema version must be exact numeric 1");}
    foreach(object value in new object[]{null,"20",0,-1,10,1.5,int.MaxValue+1L}){var values=UpdateData(managed);values["parentProcessId"]=value;Rejected(()=>Program.ParseUpdateRequest(Json(values),requestFile,updates,programRoot,10),"parent process must be bounded, positive, numeric and not the installer");}
    foreach(object value in new object[]{null,"638960000000000000",0,-1,DateTime.MaxValue.Ticks+1,1.5}){var values=UpdateData(managed);values["parentStartedUtcTicks"]=value;Rejected(()=>Program.ParseUpdateRequest(Json(values),requestFile,updates,programRoot,10),"process start time must be numeric UTC ticks, not missing or coerced");}
    foreach(object value in new object[]{null,"",Program.InstallerVersion+".0","0.0.1","99.0.0",1}){var values=UpdateData(managed);values["expectedVersion"]=value;Rejected(()=>Program.ParseUpdateRequest(Json(values),requestFile,updates,programRoot,10),"requested version must exactly match the embedded installer");}
    foreach(object value in new object[]{null,false,"true",1}){var values=UpdateData(managed);values["relaunch"]=value;Rejected(()=>Program.ParseUpdateRequest(Json(values),requestFile,updates,programRoot,10),"update cannot inject a new launch mode");}
    foreach(string key in new[]{"schemaVersion","parentProcessId","parentExecutable","parentStartedUtcTicks","expectedVersion","relaunch"}){var values=UpdateData(managed);values.Remove(key);Rejected(()=>Program.ParseUpdateRequest(Json(values),requestFile,updates,programRoot,10),"all required fields must be present: "+key);}
    {var values=UpdateData(managed);values["arguments"]="--anything";Rejected(()=>Program.ParseUpdateRequest(Json(values),requestFile,updates,programRoot,10),"unknown update request commands are rejected");}
    Rejected(()=>Program.ParseUpdateRequest(new string(' ',16385),requestFile,updates,programRoot,10),"oversized update JSON rejected before parsing");
    Rejected(()=>Program.ParseUpdateRequest("{broken",requestFile,updates,programRoot,10),"invalid JSON never falls through to interactive installation");
    Check(Program.MatchesUpdateParent(request,20,1,1,managed,"Nook","Mori.exe",request.ParentStartedUtcTicks),"same process path, product, session and start time match");
    Check(!Program.MatchesUpdateParent(request,20,1,1,managed,"Nook","Mori.exe",request.ParentStartedUtcTicks+1),"PID reuse cannot authorize an update");
    Check(!Program.MatchesUpdateParent(request,21,1,1,managed,"Nook","Mori.exe",request.ParentStartedUtcTicks),"another PID cannot stand in for the requesting parent");
    Check(!Program.MatchesUpdateParent(request,20,2,1,managed,"Nook","Mori.exe",request.ParentStartedUtcTicks),"another Windows session cannot authorize update");
    Check(!Program.MatchesUpdateParent(request,20,1,1,Path.Combine(programRoot,"different","Mori.exe"),"Nook","Mori.exe",request.ParentStartedUtcTicks),"another managed version cannot impersonate the parent");
    Check(!Program.MatchesUpdateParent(request,20,1,1,managed,"Other Nook","Mori.exe",request.ParentStartedUtcTicks),"product identity is checked in addition to the managed path");
    Check(Program.OwnStartup("\""+managed+"\" --startup",managed),"only the exact existing own startup command is carried forward");
    foreach(string command in new[]{null,"",managed,"\""+managed+"\"","\""+managed+"\" --startup --bad","\""+managed+"\" --Startup","\"C:\\Other\\Mori.exe\" --startup"})Check(!Program.OwnStartup(command,managed),"disabled, unrelated or expanded startup commands remain untouched");
    var restart=Program.RelaunchStart(managed);Check(restart.FileName==managed&&restart.Arguments==""&&!restart.UseShellExecute&&restart.WorkingDirectory==Path.GetDirectoryName(managed),"relaunch uses only a validated local executable with no shell or arguments");
    Rejected(()=>Program.RelaunchStart("https://github.com/x/setup.exe"),"relaunch rejects arbitrary remote executable targets");
    int waits=0,totalWait=0;Check(Program.WaitForParentExit(delay=>{waits++;totalWait+=delay;return waits==3;},()=>false,30000)&&waits==3&&totalWait==750,"graceful exit is observed without a kill or a fixed long sleep");
    waits=0;totalWait=0;Check(!Program.WaitForParentExit(delay=>{waits++;totalWait+=delay;return false;},()=>false,30000)&&waits==120&&totalWait==30000,"an unclosed parent blocks installation after a bounded 30 seconds");
    waits=0;Check(!Program.WaitForParentExit(delay=>{waits++;return false;},()=>true,30000)&&waits==0,"cancelled handoff never waits, closes or installs anything");
    Rejected(()=>Program.WaitForParentExit(delay=>false,()=>false,30001),"update cannot request an unbounded parent wait");
    string root=Path.Combine(Path.GetTempPath(),"MoriDesk-installer-pure-"+Guid.NewGuid().ToString("N"));
    string previousTemp=Environment.GetEnvironmentVariable("TEMP"),previousTmp=Environment.GetEnvironmentVariable("TMP"),previousData=Environment.GetEnvironmentVariable("MORI_DESK_DATA");
    string exe=Path.Combine(root,"Mori.exe");var start=Program.VerificationStart(exe,root);
    Check(start.FileName==exe&&start.Arguments=="--suite-test","verification starts only the extracted suite test");
    Check(!start.UseShellExecute&&start.CreateNoWindow,"verification is a hidden child with explicit environment");
    Check(start.EnvironmentVariables["TEMP"]==Path.Combine(root,"runtime-temp"),"verification isolates TEMP");
    Check(start.EnvironmentVariables["TMP"]==start.EnvironmentVariables["TEMP"],"verification isolates TMP too");
    Check(start.EnvironmentVariables["MORI_DESK_DATA"]==Path.Combine(root,"test-data","MoriDesk"),"verification isolates personal data separately from program payload");
    Check(Environment.GetEnvironmentVariable("TEMP")==previousTemp&&Environment.GetEnvironmentVariable("TMP")==previousTmp&&Environment.GetEnvironmentVariable("MORI_DESK_DATA")==previousData,"verification child configuration does not alter the parent process environment");
    Check(!Directory.Exists(root),"pure guard/configuration tests do not create directories or mutate personal data");
    BackupChecks();
    StartupChecks();
    if(args.Length==1){var metadata=FileVersionInfo.GetVersionInfo(Path.GetFullPath(args[0]));Check(Program.MatchesRunningProcess(20,1,metadata.ProductName,metadata.OriginalFilename,10,1),"real suite assembly metadata matches the guard");Check(new Version(metadata.FileVersion)==typeof(Program).Assembly.GetName().Version,"actual suite file version matches the compiled installer and update protocol");}
    Console.WriteLine("PASS "+checks+" installer safety checks; no process inspection, registry writes or install performed.");return 0;
   }catch(Exception error){Console.Error.WriteLine("FAIL after "+checks+" checks: "+error.Message);return 1;}
  }
 }
}
