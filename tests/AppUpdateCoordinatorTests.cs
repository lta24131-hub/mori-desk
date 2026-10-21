using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MoriDesk {
 // No real installer, Window, Process.Start, input, settings or library root.
 // Transport is fake; files are generated beneath a new sandbox GUID only.
 internal static class AppUpdateCoordinatorTests {
#if APP_UPDATE_COORDINATOR_TEST_RUNNER
  static int Main(){int count=0;try{Run(Path.Combine(Path.GetTempPath(),"Nook-update-tests-"+Guid.NewGuid().ToString("N")),(ok,name)=>{if(!ok)throw new Exception(name);count++;Console.WriteLine("PASS "+name);});Console.WriteLine("PASS "+count+" update coordinator checks");return 0;}catch(Exception error){Console.Error.WriteLine(error);return 1;}}
#endif
  static readonly Version Version=new Version(0,1,24,0);
  static bool Reject<T>(Action action)where T:Exception{try{action();return false;}catch(T){return true;}}
  static string Record(string status="ready",string version="0.1.24",int schema=1){return new JavaScriptSerializer().Serialize(new{schemaVersion=schema,status=status,expectedVersion=version,message="synthetic"});}
  internal static void Run(string root,Action<bool,string> check){
   Protocol(check);StartupProtocol(check);Task.Run(()=>Sequence(check)).GetAwaiter().GetResult();Backup(Path.Combine(root,Guid.NewGuid().ToString("N")),check);
  }
  static void StartupProtocol(Action<bool,string> check){
   string updates=@"C:\generated\MoriDesk\updates",session=Path.Combine(updates,"0123456789abcdef0123456789abcdef"),request=Path.Combine(session,"request.json"),programRoot=@"C:\generated\Programs\MoriDesk",previous=Path.Combine(programRoot,"0.1.23","Mori.exe"),current=Path.Combine(programRoot,"0.1.24","Mori.exe");
   check(AppUpdateCoordinator.StartupSession(request,updates)==session,"startup acknowledgement is confined to exact GUID request session beneath fixed updates root");
   foreach(string path in new[]{@"C:\generated\elsewhere\0123456789abcdef0123456789abcdef\request.json",Path.Combine(updates,"not-a-guid","request.json"),Path.Combine(session,"child","request.json"),Path.Combine(session,"startup-ready.json"),request+":stream"})check(Reject<InvalidDataException>(()=>AppUpdateCoordinator.StartupSession(path,updates)),"startup request cannot redirect acknowledgment outside its fixed update session");
   string source=AppUpdateCoordinator.RequestJson(123,previous,638000000000000000L,Version);AppUpdateCoordinator.ValidateStartupRequest(source,"0.1.24",programRoot);check(true,"startup acknowledges only exact original six-field update request and version");
   var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(source);
   foreach(string key in new[]{"schemaVersion","parentProcessId","parentExecutable","parentStartedUtcTicks","expectedVersion","relaunch"}){var changed=new Dictionary<string,object>(data);changed.Remove(key);check(Reject<InvalidDataException>(()=>AppUpdateCoordinator.ValidateStartupRequest(new JavaScriptSerializer().Serialize(changed),"0.1.24",programRoot)),"startup cannot acknowledge incomplete original request: "+key);}
   foreach(string version in new[]{"0.1.23","0.1.24.0","99.0.0"})check(Reject<InvalidDataException>(()=>AppUpdateCoordinator.ValidateStartupRequest(source,version,programRoot)),"startup cannot acknowledge another numeric or textual version");
   check(Reject<InvalidDataException>(()=>AppUpdateCoordinator.ValidateStartupRequest(source,"0.1.24",programRoot+"-other")),"startup request must originate from exact installed program root, not neighboring directory");
   string receipt=AppUpdateCoordinator.StartupReadyJson(456,current,638000000000000001L,Version,programRoot);var record=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(receipt);
   check(record.Count==5&&(int)record["schemaVersion"]==1&&(string)record["expectedVersion"]=="0.1.24"&&(int)record["processId"]==456&&(long)record["startedUtcTicks"]==638000000000000001L&&(string)record["executable"]==current,"receipt contains exactly schema, expected version, actual PID, start time and installed executable identity");
   check(Reject<InvalidDataException>(()=>AppUpdateCoordinator.StartupReadyJson(0,current,638000000000000001L,Version,programRoot)),"invalid launched process cannot emit startup receipt");
   check(Reject<InvalidDataException>(()=>AppUpdateCoordinator.StartupReadyJson(456,current,0,Version,programRoot)),"unknown process start time cannot emit startup receipt");
   check(Reject<InvalidDataException>(()=>AppUpdateCoordinator.StartupReadyJson(456,@"C:\arbitrary\Mori.exe",638000000000000001L,Version,programRoot)),"an executable outside controlled installation cannot emit startup receipt");
   foreach(string path in new[]{@"1:\generated\request.json", "C:\\generated\\request\0.json"})check(Reject<InvalidDataException>(()=>AppUpdateCoordinator.LaunchArguments(path)),"non-drive-letter or embedded-NUL update paths are rejected");
  }
  static void Protocol(Action<bool,string> check){
   string json=AppUpdateCoordinator.RequestJson(123,@"C:\Users\synthetic\AppData\Local\Programs\MoriDesk\0.1.23\Mori.exe",638000000000000000L,Version);var request=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json);
   check(request.Keys.OrderBy(x=>x).SequenceEqual(new[]{"schemaVersion","parentProcessId","parentExecutable","parentStartedUtcTicks","expectedVersion","relaunch"}.OrderBy(x=>x)),"update request has exactly the installer's six versioned protocol fields");
   check(request["schemaVersion"] is int&&(int)request["schemaVersion"]==1&&request["parentProcessId"] is int&&(int)request["parentProcessId"]==123,"request process identity is numeric rather than an executable command");
   check(request["parentStartedUtcTicks"] is long&&(long)request["parentStartedUtcTicks"]==638000000000000000L&&(bool)request["relaunch"],"request contains full numeric UTC start ticks and explicit relaunch");
   check((string)request["expectedVersion"]=="0.1.24"&&AppUpdateCoordinator.VersionText(new Version(0,1,24,1))=="0.1.24.1","zero revision normalizes to installer contract, nonzero revision is retained");
   check(AppUpdateCoordinator.LaunchArguments(@"C:\updates\test folder\request.json")=="--update-request \"C:\\updates\\test folder\\request.json\"","installer request path is quoted data without shell interpretation");
   foreach(string path in new[]{"request.json","C:request.json",@"\updates\request.json",@"\\server\share\request.json",@"C:\updates\wrong.json",@"C:\updates\request.json:stream","C:\\updates\\\"\\request.json","C:\\updates\\\nrequest.json".Replace("\\n","\n")})check(Reject<InvalidDataException>(()=>AppUpdateCoordinator.LaunchArguments(path)),"launch rejects nonlocal, injected or wrong request path: "+path.Replace("\n","newline"));
   foreach(int pid in new[]{0,-1})check(Reject<ArgumentException>(()=>AppUpdateCoordinator.RequestJson(pid,@"C:\synthetic\Mori.exe",638000000000000000L,Version)),"invalid parent process cannot authorize update: "+pid);
   check(Reject<ArgumentException>(()=>AppUpdateCoordinator.RequestJson(1,@"C:\synthetic\Mori.exe",0,Version)),"zero process start time cannot authorize update");
   AppUpdateCoordinator.ReadRecord(Record(),Version,"ready");check(true,"matching ready state is accepted only for expected version");
   foreach(string text in new[]{"","null","{",new string('x',16385),Record("ready","0.1.23"),Record("ready","0.1.24",2),Record("installed"),"{\"schemaVersion\":\"1\",\"status\":\"ready\",\"expectedVersion\":\"0.1.24\"}"})check(Reject<InvalidDataException>(()=>AppUpdateCoordinator.ReadRecord(text,Version,"ready")),"malformed, stale or non-ready installer record cannot trigger exit");
   foreach(string status in new[]{"installed","cancelled","error"}){AppUpdateCoordinator.ReadRecord(Record(status),Version,null);check(true,"terminal status is explicit: "+status);}
   check(Reject<InvalidDataException>(()=>AppUpdateCoordinator.ReadRecord(Record("success"),Version,null)),"unknown terminal status cannot be mistaken for installation");
   check(AppUpdateCoordinator.InstallerIdentity("Nook Installer","Nook-Setup.exe","0.1.24.0",Version),"installer product, filename and numeric version all match");
   foreach(string[] identity in new[]{new[]{"Other","Nook-Setup.exe","0.1.24.0"},new[]{"Nook Installer","Mori.exe","0.1.24.0"},new[]{"Nook Installer","Nook-Setup.exe","0.1.23.0"},new[]{"Nook Installer","Nook-Setup.exe","not a version"}})check(!AppUpdateCoordinator.InstallerIdentity(identity[0],identity[1],identity[2],Version),"mismatched product identity or version cannot execute as an update");
   string sanitized=AppUpdateMetadataBackup.SanitizeSettings("{\"LibraryPath\":\"C:\\\\synthetic\",\"Theme\":\"dark\",\"Token\":\"SYNTHETIC_SECRET\",\"ApiKey\":\"SYNTHETIC_SECRET\",\"CanvasGrid\":true,\"ThemeAccent\":{\"secret\":\"SYNTHETIC_SECRET\"}}",false);
   check(sanitized.Contains("LibraryPath")&&sanitized.Contains("CanvasGrid")&&!sanitized.Contains("SYNTHETIC_SECRET")&&!sanitized.Contains("ThemeAccent"),"config recovery uses typed scalar allowlist, never unknown token or nested credential keys");
   sanitized=AppUpdateMetadataBackup.SanitizeSettings("{\"FileConversionOutputPath\":\"D:\\\\Nook\\\\转换结果\",\"Token\":\"SYNTHETIC_SECRET\"}",false);
   check(sanitized.Contains("FileConversionOutputPath")&&sanitized.Contains("转换结果")&&!sanitized.Contains("SYNTHETIC_SECRET"),"update metadata backup preserves the configurable conversion result path without adding credentials");
   sanitized=AppUpdateMetadataBackup.SanitizeSettings("{\"Color\":\"#123456\",\"ApiKey\":\"SYNTHETIC_SECRET\",\"Shortcuts\":{\"Capture\":{\"Key\":112,\"Modifiers\":0,\"Token\":\"SYNTHETIC_SECRET\"},\"Unknown\":{\"Key\":1,\"Modifiers\":2}}}",true);
   check(sanitized.Contains("#123456")&&sanitized.Contains("Capture")&&!sanitized.Contains("Unknown")&&!sanitized.Contains("SYNTHETIC_SECRET"),"capture shortcut recovery preserves documented fields while dropping nested credentials");
  }
  static async Task Sequence(Action<bool,string> check){
   var pending=new TaskCompletionSource<bool>();int exits=0,cancels=0;
   var handoff=AppUpdateCoordinator.RunHandoffAsync(token=>pending.Task,token=>{exits++;return Task.FromResult(true);},()=>cancels++,CancellationToken.None);
   check(exits==0&&!handoff.IsCompleted,"original application remains alive throughout installer extraction before ready");
   pending.SetResult(true);check(await handoff&&exits==1&&cancels==0,"normal exit is requested exactly once after verified readiness");
   exits=0;cancels=0;bool veto=await AppUpdateCoordinator.RunHandoffAsync(token=>Task.FromResult(true),token=>{exits++;return Task.FromResult(false);},()=>cancels++,CancellationToken.None);
   check(!veto&&exits==1&&cancels==1,"unsaved draft, canvas, pin or backup veto cancels installer and keeps original running");
   exits=0;cancels=0;bool failed=false;try{await AppUpdateCoordinator.RunHandoffAsync(token=>{throw new IOException("synthetic staging failure");},token=>{exits++;return Task.FromResult(true);},()=>cancels++,CancellationToken.None);}catch(IOException){failed=true;}
   check(failed&&exits==0&&cancels==1,"staging failure cannot call exit and always emits cancellation");
   exits=0;cancels=0;using(var cancelled=new CancellationTokenSource()){
    cancelled.Cancel();bool stopped=false;try{await AppUpdateCoordinator.RunHandoffAsync(token=>{throw new Exception("must not start");},token=>{exits++;return Task.FromResult(true);},()=>cancels++,cancelled.Token);}catch(OperationCanceledException){stopped=true;}
    check(stopped&&exits==0&&cancels==1,"cancelled explicit action cannot start readiness or exit workflows");
   }
   exits=0;cancels=0;using(var cancelled=new CancellationTokenSource()){
    bool stopped=false;try{await AppUpdateCoordinator.RunHandoffAsync(token=>{cancelled.Cancel();return Task.FromResult(true);},token=>{exits++;return Task.FromResult(true);},()=>cancels++,cancelled.Token);}catch(OperationCanceledException){stopped=true;}
    check(stopped&&exits==0&&cancels==1,"cancellation received together with ready still prevents application exit");
   }
   cancels=0;failed=false;try{await AppUpdateCoordinator.RunHandoffAsync(token=>Task.FromResult(true),token=>{throw new IOException("synthetic exit failure");},()=>cancels++,CancellationToken.None);}catch(IOException){failed=true;}
   check(failed&&cancels==1,"save/exit exceptions cancel handoff without forcing shutdown");
  }
  static void Backup(string root,Action<bool,string> check){
   if(Directory.Exists(root)||File.Exists(root))throw new IOException("Tests refuse existing sandbox.");Directory.CreateDirectory(root);
   string data=Path.Combine(root,"data"),library=Path.Combine(root,"library"),output=Path.Combine(root,"metadata-backup");Directory.CreateDirectory(Path.Combine(library,".mori"));Directory.CreateDirectory(Path.Combine(library,"原图"));Directory.CreateDirectory(Path.Combine(data,"capture","pins"));
   File.WriteAllText(Path.Combine(library,".mori","library.db"),"generated database original");File.WriteAllText(Path.Combine(library,"原图","never-copy.png"),"GENERATED_SOURCE_MEDIA");
   File.WriteAllText(Path.Combine(data,"settings.json"),"{\"LibraryPath\":\"C:\\\\synthetic\",\"Theme\":\"dark\",\"GitHubToken\":\"SYNTHETIC_SECRET\"}");File.WriteAllText(Path.Combine(data,"capture","settings.json"),"{\"Color\":\"#123456\",\"ApiKey\":\"SYNTHETIC_SECRET\"}");File.WriteAllText(Path.Combine(data,"capture","pins","pins.json"),"[]");File.WriteAllText(Path.Combine(data,"capture","pins","never-copy.png"),"GENERATED_PIN_IMAGE");
   Directory.CreateDirectory(Path.Combine(data,"update"));File.WriteAllText(Path.Combine(data,"update","github-token.dat"),"SYNTHETIC_SECRET");Directory.CreateDirectory(Path.Combine(data,"browser-profile"));File.WriteAllText(Path.Combine(data,"browser-profile","Cookies"),"SYNTHETIC_SECRET");
   string project=Path.Combine(data,"free-canvas",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(project,"images"));File.WriteAllText(Path.Combine(project,"project.json"),"{\"Version\":1}");File.WriteAllText(Path.Combine(project,"mature-canvas.json"),"{\"Version\":1}");File.WriteAllText(Path.Combine(project,"images","never-copy.png"),"GENERATED_CANVAS_IMAGE");
   int snapshots=0;AppUpdateMetadataBackup.Create(output,data,library,target=>{snapshots++;File.WriteAllText(target,"generated consistent snapshot after normal saves");});
   check(snapshots==1&&File.ReadAllText(Path.Combine(output,"library.db")).Contains("consistent snapshot"),"metadata backup uses one caller-provided consistent database snapshot after save gate");
   check(File.Exists(Path.Combine(output,"complete.json"))&&File.ReadAllText(Path.Combine(output,"complete.json")).Contains("\"metadataOnly\":true"),"only fully finished bounded metadata backup gets a completion marker");
   string[] files=Directory.GetFiles(output);check(files.Length==7&&files.All(path=>Path.GetExtension(path)==".json"||Path.GetFileName(path)=="library.db"),"backup includes only database, two settings, pins and two canvas manifests, not media trees");
   check(files.All(path=>!File.ReadAllText(path).Contains("SYNTHETIC_SECRET")),"credentials, browser cookies and unknown API keys never enter generated recovery backup");
   check(File.ReadAllText(Path.Combine(library,"原图","never-copy.png"))=="GENERATED_SOURCE_MEDIA"&&File.ReadAllText(Path.Combine(data,"capture","pins","never-copy.png"))=="GENERATED_PIN_IMAGE"&&File.ReadAllText(Path.Combine(project,"images","never-copy.png"))=="GENERATED_CANVAS_IMAGE","metadata snapshot leaves original media and pin/canvas images untouched");
   check(Reject<IOException>(()=>AppUpdateMetadataBackup.Create(output,data,library,target=>{throw new Exception("must not run");})),"a completed backup directory is never overwritten or reused");
   string failedOutput=Path.Combine(root,"failed","metadata-backup");bool failed=false;try{AppUpdateMetadataBackup.Create(failedOutput,data,library,target=>{throw new IOException("synthetic snapshot failure");});}catch(IOException){failed=true;}
   check(failed&&!File.Exists(Path.Combine(failedOutput,"complete.json")),"failed snapshot has no completion marker and vetoes exit instead of claiming a backup");
   string oversizedOutput=Path.Combine(root,"oversized","metadata-backup");using(var file=new FileStream(Path.Combine(library,".mori","library.db"),FileMode.Open,FileAccess.Write))file.SetLength(AppUpdateMetadataBackup.MaximumFileBytes+1);
   check(Reject<IOException>(()=>AppUpdateMetadataBackup.Create(oversizedOutput,data,library,target=>{throw new Exception("must not run");}))&&!Directory.Exists(oversizedOutput),"oversized metadata is rejected before starting snapshot rather than copying the full library");
   check(File.ReadAllText(Path.Combine(data,"update","github-token.dat"))=="SYNTHETIC_SECRET"&&File.ReadAllText(Path.Combine(data,"browser-profile","Cookies"))=="SYNTHETIC_SECRET","credential and browser profile files are neither modified nor migrated");
  }
 }
}
