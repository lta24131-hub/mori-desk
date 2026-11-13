import hashlib, json, pathlib, subprocess, zipfile

base = pathlib.Path(__file__).resolve().parent
out = base / 'r3-after1304-readonly-timing'
out.mkdir(exist_ok=False)
source = r'''using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

// Fixed read-only timing diagnostics for the exact preserved 1304 failure.
// No app entry point, Cleanup, Queue, registration repair, or delete API call.
internal static class CleanupTimingGuest {
 const string Current="0.1.45-20261113145904278-57e527ec", Old="0.1.42-20261113142121298-e4e817b1";
 const string CurrentHash="97B122310B1033BE7EBDA63730F929C256D8D0972CBC3497C7A7D6539CCA50D6";
 const string OldHash="0023F5CF9C7B6021ED8071E9936EB20282C1FBBE467716B33146AA52110662EB";
 const string ManifestHash="F5CDFC58C2F0F6434BBC64D8C4727C0C426BAF8FE100456ED69248EFC44244FD";
 const string ActiveHash="E8E28898DA231CA9C2A8E2CC51AAEB17B904AA234E099678AEB1A0D8D1A2667C";
 const int LimitMilliseconds=240000;
 static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
 static string Package,Program,Data,Operation,CurrentStage="initialization";
 static int Finished;
 static readonly List<string> Checks=new List<string>();
 static readonly Stopwatch Total=Stopwatch.StartNew();
 static void Need(bool value,string reason){if(!value)throw new InvalidDataException(reason);Checks.Add(reason);}
 static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=1024*1024,RecursionLimit=20};}
 static void Plain(string path){for(string p=Path.GetFullPath(path);!string.IsNullOrEmpty(p);p=Path.GetDirectoryName(p))if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked path refused.");}
 static void Save(string suffix,object value){Need(Regex.IsMatch(suffix,@"\A[a-z0-9-]+\.json\z"),"Fixed bounded diagnostic filename.");string path=Path.Combine(Package,Operation+"-"+suffix);Plain(path);byte[] bytes=Utf8.GetBytes(Json().Serialize(value));using(var f=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read)){f.Write(bytes,0,bytes.Length);f.Flush(true);}}
 static void Stage(string name,object value){CurrentStage=name;Save(name+".json",new{stage=name,utc=DateTime.UtcNow.ToString("O"),elapsedMs=Total.Elapsed.TotalMilliseconds,details=value});}
 static string Hash(Stream input){input.Position=0;using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(input)).Replace("-","");}
 static FileStream Open(string path,string expected){Plain(path);var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);try{Need(Hash(f)==expected,"Exact preserved file SHA256: "+Path.GetFileName(path));f.Position=0;return f;}catch{f.Dispose();throw;}}
 static object Field(object value,string name){var f=value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);Need(f!=null,"Exact production field: "+name);return f.GetValue(value);}
 static void Guard(){
  bool vm=false;using(var q=new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem"))foreach(ManagementObject row in q.Get())using(row)vm|=Regex.IsMatch(Convert.ToString(row["Manufacturer"])+" "+Convert.ToString(row["Model"]),"QEMU|KVM|Bochs",RegexOptions.IgnoreCase);
  Need(vm&&Environment.UserName.Equals("NookTester",StringComparison.OrdinalIgnoreCase),"Dedicated NookTester QEMU VM only.");
  string candidate=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');Need(Regex.IsMatch(candidate,@"\AC:\\NookLab\\Audit\\apps\\[A-Fa-f0-9]{12}\z"),"Hash-addressed audit output only.");Plain(candidate);Package=candidate;
  Need(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)==@"C:\Users\NookTester\AppData\Local","Exact dedicated-user physical profile.");
  Environment.SetEnvironmentVariable("MORI_DESK_DATA",null); // This helper process only; matches the actual default installed context.
  Data=@"C:\Users\NookTester\AppData\Local\MoriDesk";Program=@"C:\Users\NookTester\AppData\Local\Programs\MoriDesk";Plain(Data);Plain(Program);
  foreach(string name in new[]{"Mori","Nook","Nook-Setup","chrome","msedge"}){var all=Process.GetProcessesByName(name);try{Need(all.Length==0,"No application/browser process during read-only diagnosis: "+name);}finally{foreach(var p in all)p.Dispose();}}
 }
 static object Capture(Assembly product,string app){
  var type=product.GetType("MoriDesk.InstalledCleanupState",true);var method=type.GetMethod("Capture",BindingFlags.NonPublic|BindingFlags.Static,null,new[]{typeof(string),typeof(string),typeof(IEnumerable<string>)},null);Need(method!=null,"Exact production Capture, never CaptureWith.");
  Stage("01-capture-start",new{method="MoriDesk.InstalledCleanupState.Capture"});var clock=Stopwatch.StartNew();var snapshot=method.Invoke(null,new object[]{app,Program,new[]{Data}});clock.Stop();
  bool complete=Convert.ToBoolean(Field(snapshot,"Complete")),switched=Convert.ToBoolean(Field(snapshot,"EntrypointsSwitched"));string reason=Convert.ToString(Field(snapshot,"Reason"));Need(Regex.IsMatch(reason,@"\A(?:ready|entrypoints-not-current|evidence-unavailable:[A-Za-z0-9]+)\z"),"Production reason contains no configuration data.");
  var running=(string[])Field(snapshot,"RunningExecutables");var references=(string[])Field(snapshot,"ReferencedExecutables");var protect=(string[])Field(snapshot,"ProtectedPaths");
  string launcher=Path.Combine(Program,"Launcher","Mori.exe"),old=Path.Combine(Program,Old)+"\\";
  var result=new{milliseconds=clock.Elapsed.TotalMilliseconds,complete,entrypointsSwitched=switched,reason,runningCount=running.Length,referenceCount=references.Length,protectedPathCount=protect.Length,allManagedReferencesStable=references.All(p=>p.Equals(launcher,StringComparison.OrdinalIgnoreCase)),oldPayloadRunning=running.Count(p=>p.StartsWith(old,StringComparison.OrdinalIgnoreCase)),oldPayloadReferences=references.Count(p=>p.StartsWith(old,StringComparison.OrdinalIgnoreCase)),actualProcessCondition="No installed app/browser running; helper image is outside installation",productionGateNotBypassed=true};
  Stage("02-capture-finished",result);return result;
 }
 static object Tree(Assembly product){
  var engine=product.GetType("MoriDeskInstaller.InstalledVersionCleanup",true);var type=engine.GetNestedType("LockedTree",BindingFlags.NonPublic);Need(type!=null,"Exact private production LockedTree type.");var constructor=type.GetConstructor(BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(string),typeof(bool)},null);Need(constructor!=null,"Exact production LockedTree(string,bool) constructor.");
  object tree=null;double construction=0,disposal=0;int files=0,directories=0;
  Stage("01-tree-start",new{method="MoriDeskInstaller.InstalledVersionCleanup.LockedTree",deletion=false,deleteAccessRequested=false,exclusiveDeletionModeNotReproduced=true});
  try{var clock=Stopwatch.StartNew();tree=constructor.Invoke(new object[]{Path.Combine(Program,Old),false});clock.Stop();construction=clock.Elapsed.TotalMilliseconds;files=((ICollection)Field(tree,"Files")).Count;directories=((ICollection)Field(tree,"Directories")).Count;
   Stage("02-tree-constructed",new{milliseconds=construction,files,directories,rootDirectoryIncluded=true,hashesComputedByTree=false,deletion=false});
  }finally{if(tree!=null){Stage("03-tree-dispose-start",new{deletion=false});var clock=Stopwatch.StartNew();((IDisposable)tree).Dispose();clock.Stop();disposal=clock.Elapsed.TotalMilliseconds;Stage("04-tree-disposed",new{milliseconds=disposal,deletion=false});}}
  return new{constructionMilliseconds=construction,disposeMilliseconds=disposal,files,directories,deletion=false,deleteAccessRequested=false,sourceWrites=false,exclusiveDeletionModeNotReproduced=true};
 }
 static object Run(){
  Guard();string app=Path.Combine(Program,Current,"Mori.exe"),old=Path.Combine(Program,Old);
  using(var current=Open(app,CurrentHash))using(var previous=Open(Path.Combine(old,"Mori.exe"),OldHash))using(var manifest=Open(Path.Combine(old,".nook-installed-files.json"),ManifestHash))using(var activeFile=Open(Path.Combine(Program,"active-install.json"),ActiveHash)){
   string text;using(var reader=new StreamReader(activeFile,Utf8,true,4096,true))text=reader.ReadToEnd();var active=Json().Deserialize<Dictionary<string,object>>(text);Need(Convert.ToString(active["executable"])==Current+"\\Mori.exe"&&Convert.ToString(active["version"])=="0.1.45.0"&&Convert.ToString(active["sha256"]).Equals(CurrentHash,StringComparison.OrdinalIgnoreCase),"Preserved committed descriptor matches exact R3.");
   Stage("00-identity-verified",new{currentDirectory=Current,oldDirectory=Old,currentSha256=CurrentHash,oldSha256=OldHash,oldManifestSha256=ManifestHash,activeDescriptorSha256=ActiveHash,sourceWrites=false,processOnlyDataOverrideCleared=true,coldHelperProcess=true,filesystemCacheNotControlled=true,identityHashReadsWarmOnlySelectedFiles=true});
   var product=Assembly.LoadFrom(app);Need(product.Location.Equals(app,StringComparison.OrdinalIgnoreCase)&&product.GetName().Version==new Version(0,1,45,0),"Loaded exact installed R3 assembly without invoking entry point.");
   return Operation=="capture"?Capture(product,app):Tree(product);
  }
 }
 static object Error(Exception error){while(error is TargetInvocationException&&error.InnerException!=null)error=error.InnerException;return new{type=error.GetType().FullName,hresult=error.HResult,innerType=error.InnerException==null?null:error.InnerException.GetType().FullName};}
 static int Main(string[] args){
  if(args.Length!=1||(args[0]!="--capture"&&args[0]!="--tree"))return 2;Operation=args[0].Substring(2);bool completed=false;object result=null,error=null;
  using(var timer=new Timer(_=>{if(Interlocked.CompareExchange(ref Finished,1,0)!=0)return;try{if(Package!=null)Save("timeout.json",new{diagnosticCompleted=false,stage=CurrentStage,timeoutMs=LimitMilliseconds,utc=DateTime.UtcNow.ToString("O"),sourceWrites=false});}finally{Environment.Exit(124);}},null,LimitMilliseconds,Timeout.Infinite)){
   try{result=Run();completed=true;}catch(Exception e){error=Error(e);}finally{if(Interlocked.CompareExchange(ref Finished,1,0)==0&&Package!=null)Save("timing-result.json",new{passed=completed,diagnosticCompleted=completed,purpose="Read-only timing; not installation qualification",operation=Operation,result,error,stage=CurrentStage,checks=Checks.ToArray(),totalMs=Total.Elapsed.TotalMilliseconds,limitMs=LimitMilliseconds,sourceWrites=false,applicationStarted=false,productEntryPointInvoked=false,cleanupInvoked=false,registrationWrites=false,filesystemCacheNotControlled=true,hostExecuted=false,completedUtc=DateTime.UtcNow.ToString("O")});}
  }
  return completed?0:1;
 }
}
'''
cs = out / 'CleanupTimingGuest.cs'
cs.write_bytes(source.replace('\n','\r\n').encode('utf-8'))
exe = out / 'CleanupTimingGuest.exe'
cmd = [r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe','/nologo','/optimize+','/target:exe','/r:System.Management.dll','/r:System.Web.Extensions.dll','/out:'+str(exe),str(cs)]
r = subprocess.run(cmd,capture_output=True)
(out/'compile.log').write_bytes(r.stdout+r.stderr)
assert r.returncode == 0, (r.stdout+r.stderr).decode(errors='replace')
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
archive=out/'readonly-timing.zip'
with zipfile.ZipFile(archive,'x',zipfile.ZIP_DEFLATED,compresslevel=1) as z:
    z.write(cs,cs.name);z.write(exe,exe.name);z.write(exe,'Mori.exe')
h=dict(path=str(archive),sha256=sha(archive),sourceSha256=sha(cs),exeSha256=sha(exe),guest='C:\\NookLab\\Audit\\apps\\'+sha(archive)[:12],executable=exe.name,operations=[dict(args=['--capture'],reports=['capture-timing-result.json','capture-00-identity-verified.json','capture-01-capture-start.json','capture-02-capture-finished.json','capture-timeout.json']),dict(args=['--tree'],reports=['tree-timing-result.json','tree-00-identity-verified.json','tree-01-tree-start.json','tree-02-tree-constructed.json','tree-03-tree-dispose-start.json','tree-04-tree-disposed.json','tree-timeout.json'])],timeoutSeconds=240,recommendedBrokerTimeoutSeconds=270,compileCommand=cmd,hostExecution='Compiler only; neither helper nor product executed',scope='Exact preserved after1304 R3 Capture and old42 LockedTree(false), independent helper processes, read-only, no source writes',productSha256='97B122310B1033BE7EBDA63730F929C256D8D0972CBC3497C7A7D6539CCA50D6',coldWarmLimit='Fresh helper process per operation; OS caches uncontrolled; required identity hashes warm only current/old main executable, old manifest and active descriptor. Tree(false) does not request DELETE access or reproduce deletion exclusivity.')
(out/'handoff.json').write_text(json.dumps(h,indent=2),encoding='utf-8')
print(json.dumps(h,indent=2))
