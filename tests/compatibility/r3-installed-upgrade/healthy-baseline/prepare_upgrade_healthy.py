"""Prepare a separate healthy-baseline upgrade; compile/package only on host."""
import hashlib,json,pathlib,shutil,subprocess,zipfile,difflib
BASE=pathlib.Path(__file__).resolve().parent
OLD=BASE/'r3-upgrade-journey'
OUT=BASE/'r3-upgrade-healthy-v3'
assert not OUT.exists()
OUT.mkdir(); PKG=OUT/'package';PKG.mkdir()
def sha(p):
    with pathlib.Path(p).open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()
def read(p):return json.loads(pathlib.Path(p).read_text(encoding='utf-8-sig'))
def write(p,v):pathlib.Path(p).write_text(json.dumps(v,ensure_ascii=False,indent=2),encoding='utf-8')
def replace(text,old,new,count=1):
    assert text.count(old)==count,(old,text.count(old));return text.replace(old,new)
sources={}
for name in ['InstalledUpgradeDriverGuest','InstalledUpgradeFixtureGuest','InstalledUpgradePreserveGuest']:
    origin=(BASE/'r3-upgrade-launcher-race'/'package' if 'Driver' in name else OLD/'helpers')/(name+'.cs')
    original=origin.read_text(encoding='utf-8-sig')
    after=original.replace('20261113-r3','20261113-r3-healthy')
    assert original!=after
    sources[name]=(origin,original,after)

# Fresh preservation retains both failed installed payloads and all old data;
# registry is observed, not changed. Keep the exact reviewed inventory beside it.
name='InstalledUpgradePreserveGuest';origin,before,after=sources[name]
after=replace(after,'audit-installed-inventory-before42\\.json','audit-installed-inventory-before-healthy42\\.json')
needle='NoProcesses();Directory.CreateDirectory(Destination);Save('
after=replace(after,needle,'NoProcesses();Directory.CreateDirectory(Destination);File.Copy(inventory,Path.Combine(Destination,"reviewed-inventory.json"),false);Need(Hash(Path.Combine(Destination,"reviewed-inventory.json"))==expectedHash,"Exact reviewed failure-state inventory preserved unchanged.");Save(')
sources[name]=(origin,before,after)

# Add one explicit public UI preparation stage. Existing Install, Launch,
# candidate identity handling and their assertions remain byte-for-byte except
# for the newly isolated fixture path.
name='InstalledUpgradeDriverGuest';origin,before,after=sources[name]
prepare=r''' static object PrepareBrowser42(){
  NoProduct();var active=Active("0.1.42.0",OldApp);string executable=Path.Combine(Program,Text(active["executable"])),launcher=Path.Combine(Program,"Launcher","Mori.exe"),browser=Path.Combine(Data,"browser");
  Need(!File.Exists(Path.Combine(browser,"app.mori.desk.json"))&&!File.Exists(Path.Combine(browser,"extension-installs.json"))&&!Directory.Exists(Path.Combine(browser,"extension")),"New healthy baseline has no browser manifest/catalog/runtime to replace.");
  foreach(string name in new[]{"chrome","msedge"}){var all=Process.GetProcessesByName(name);try{Need(all.Length==0,"No browser runs while the public42 UI prepares its own extension: "+name);}finally{foreach(var item in all)item.Dispose();}}
  Process child=null;DateTime beforeClick=DateTime.MinValue;using(var bootstrap=Process.Start(Start(launcher))){
   Wait(()=>{var candidates=Process.GetProcessesByName("Mori");try{foreach(var candidate in candidates){string image=CandidateImage(candidate,bootstrap.Id);if(image!=null&&image.Equals(executable,StringComparison.OrdinalIgnoreCase)&&candidate.MainWindowHandle!=IntPtr.Zero&&IsWindowVisible(candidate.MainWindowHandle)&&IsWindowEnabled(candidate.MainWindowHandle)){child=candidate;return true;}}return false;}finally{foreach(var candidate in candidates)if(candidate!=child)candidate.Dispose();}},30000,"Fixed launcher did not open public42 for browser preparation.");
   Need(child!=null&&Hash(child.MainModule.FileName)==OldApp,"Browser preparation belongs to exact genuine public42.");bool contained;Need(IsProcessInJob(child.Handle,Job,out contained)&&contained,"Public42 preparation child is owned by this helper Job.");
   var main=AutomationElement.FromHandle(child.MainWindowHandle);var settingsButton=main.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"Settings"));Invoke(settingsButton,child.Id,"Open public42 Settings");
   AutomationElement settings=null;Wait(()=>{settings=Windows(child.Id).FirstOrDefault(w=>w.Current.Name=="Nook · 设置");return settings!=null;},6000,"Public42 Settings dialog did not appear.");
   Invoke(Find(settings,"浏览器收藏",ControlType.Button),child.Id,"Select public42 browser collection settings");
   AutomationElement install=null;Wait(()=>{install=Find(settings,"安装 / 修复",ControlType.Button);return install!=null&&install.Current.IsEnabled;},6000,"Public42 supported Install / Repair button did not appear.");
   beforeClick=DateTime.UtcNow;Invoke(install,child.Id,"Prepare extension once through public42 Install / Repair");
   string manifest=Path.Combine(browser,"app.mori.desk.json"),catalog=Path.Combine(browser,"extension-installs.json"),runtime=Path.Combine(browser,"extension","manifest.json"),connection=Path.Combine(browser,"connection-status.log");
   Wait(()=>File.Exists(manifest)&&File.Exists(catalog)&&File.Exists(runtime)&&File.Exists(connection)&&File.GetLastWriteTimeUtc(connection)>=beforeClick&&File.ReadAllText(connection,Utf8).Contains(" listening"),30000,"Public42 UI did not prepare its manifest/catalog/runtime and local listening state.");
   var native=Read(manifest);Need(Text(native["name"])=="app.mori.desk"&&Text(native["type"])=="stdio"&&Text(native["path"]).Equals(launcher,StringComparison.OrdinalIgnoreCase)&&File.Exists(launcher),"Public42 generated manifest targets existing stable launcher.");
   object pattern=null;Need(settings.Current.ProcessId==child.Id&&settings.TryGetCurrentPattern(WindowPattern.Pattern,out pattern),"Only owned public42 Settings dialog closes normally.");((WindowPattern)pattern).Close();
   Wait(()=>!Windows(child.Id).Any(w=>w.Current.Name=="Nook · 设置"),6000,"Settings did not close normally.");
   CloseApp(child,OldApp);child.Dispose();Wait(()=>bootstrap.HasExited,5000,"Fixed launcher did not exit after browser preparation.");
   return new{publicUiOnly=true,clickedInstallRepairOnce=true,realChromeLoaded=false,manifestSha256=Hash(manifest),catalogSha256=Hash(catalog),runtimeManifestSha256=Hash(runtime),localListeningObserved=true,normalClose=true};
  }
 }
'''
after=replace(after,' [STAThread]static int Main(string[] args)',prepare+' [STAThread]static int Main(string[] args)')
after=replace(after,'(Operation=="--launch42"||Operation=="--launch45")','(Operation=="--launch42"||Operation=="--launch45"||Operation=="--prepare-browser42")')
after=replace(after,'result=args.Length==2?Install(Operation=="--install45",args[1]):Launch(Operation=="--launch45");','result=Operation=="--prepare-browser42"?PrepareBrowser42():args.Length==2?Install(Operation=="--install45",args[1]):Launch(Operation=="--launch45");')
assert after[after.index(' static object Install('):after.index(' // The fixed launcher')]==before[before.index(' static object Install('):before.index(' // The fixed launcher')].replace('20261113-r3','20261113-r3-healthy')
sources[name]=(origin,before,after)

health=r'''using System;
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
using Microsoft.Win32;
// Read-only validation of exact public42 after its actual supported UI setup.
// Never invokes setup, repair, registry writes, cleanup, or CaptureWith seams.
internal static class InstalledUpgradeHealthGuest {
 const string Hash42="0023F5CF9C7B6021ED8071E9936EB20282C1FBBE467716B33146AA52110662EB";
 static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
 static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=4*1024*1024,RecursionLimit=40};
 static readonly List<string> Checks=new List<string>();static string Package,Data,Program;static object Snapshot;
 static void Need(bool value,string reason){if(!value)throw new InvalidDataException(reason);Checks.Add(reason);}
 static string Text(object value){return Convert.ToString(value);}
 static void Plain(string path){for(string p=Path.GetFullPath(path);!string.IsNullOrEmpty(p);p=Path.GetDirectoryName(p))if((Directory.Exists(p)||File.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked validation path refused.");}
 static string Hash(string path){Plain(path);using(var f=File.OpenRead(path))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(f)).Replace("-","");}
 static Dictionary<string,object> Read(string path){Plain(path);Need(new FileInfo(path).Length<=4*1024*1024,"Bounded validation input.");return Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path,Utf8));}
 static object Field(object value,string name){var field=value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);Need(field!=null,"Exact production snapshot field: "+name);return field.GetValue(value);}
 static void Guard(){bool vm=false;using(var query=new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem"))foreach(ManagementObject row in query.Get())using(row)vm|=Regex.IsMatch(Text(row["Manufacturer"])+" "+Text(row["Model"]),"QEMU|KVM|Bochs",RegexOptions.IgnoreCase);Need(vm&&Environment.UserName.Equals("NookTester",StringComparison.OrdinalIgnoreCase),"Dedicated NookTester VM only.");Package=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');Need(Regex.IsMatch(Package,@"\AC:\\NookLab\\Audit\\apps\\[A-Fa-f0-9]{12}\z"),"Hash-addressed output directory required.");Plain(Package);Environment.SetEnvironmentVariable("MORI_DESK_DATA",null);Data=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MoriDesk");Need(Data==@"C:\Users\NookTester\AppData\Local\MoriDesk","Actual dedicated default profile required.");Program=Path.Combine(Path.GetDirectoryName(Data),"Programs","MoriDesk");Plain(Data);Plain(Program);foreach(string name in new[]{"Mori","Nook","Nook-Setup","chrome","msedge"}){var all=Process.GetProcessesByName(name);try{Need(all.Length==0,"No related product/browser process during baseline validation: "+name);}finally{foreach(var item in all)item.Dispose();}}}
 static object Validate(){
  var active=Read(Path.Combine(Program,"active-install.json"));string relative=Text(active["executable"]);Need(Regex.IsMatch(relative,@"\A0\.1\.42-[A-Za-z0-9.-]+\\Mori\.exe\z"),"Exact public42 committed directory.");string app=Path.Combine(Program,relative),launcher=Path.Combine(Program,"Launcher","Mori.exe");Need(Text(active["version"])=="0.1.42.0"&&Text(active["sha256"]).Equals(Hash42,StringComparison.OrdinalIgnoreCase)&&Hash(app)==Hash42,"Exact installed public42 identity before reflection.");Need(File.Exists(launcher)&&File.ReadAllText(Path.Combine(Program,"Launcher",".nook-launcher-v1"),Utf8)=="Nook stable launcher protocol 1\n"+Hash(launcher)+"\n","Stable launcher ownership matches actual executable bytes.");
  string browser=Path.Combine(Data,"browser"),manifest=Path.Combine(browser,"app.mori.desk.json"),catalog=Path.Combine(browser,"extension-installs.json");var native=Read(manifest);Need(Text(native["name"])=="app.mori.desk"&&Text(native["type"])=="stdio"&&Text(native["path"]).Equals(launcher,StringComparison.OrdinalIgnoreCase),"Actual native manifest exists, parses and targets stable launcher.");var origins=((IEnumerable)native["allowed_origins"]).Cast<object>().Select(Text).ToArray();Need(origins.Length==1&&Regex.IsMatch(origins[0],@"\Achrome-extension://[a-p]{32}/\z"),"Exactly one product-generated extension origin, not invented authorization.");
  var known=Read(catalog);var installs=((IEnumerable)known["installs"]).Cast<Dictionary<string,object>>().ToArray();Need(installs.Length==1&&Text(installs[0]["path"]).Equals(Path.Combine(browser,"extension"),StringComparison.OrdinalIgnoreCase)&&"chrome-extension://"+Text(installs[0]["extensionId"])+"/"==origins[0],"Managed catalog agrees with native origin and actual runtime location.");
  var registrations=new List<object>();foreach(var hive in new[]{RegistryHive.CurrentUser,RegistryHive.LocalMachine})foreach(var view in new[]{RegistryView.Registry32,RegistryView.Registry64})using(var root=RegistryKey.OpenBaseKey(hive,view))foreach(string name in new[]{@"Google\Chrome",@"Microsoft\Edge"})using(var key=root.OpenSubKey(@"Software\"+name+@"\NativeMessagingHosts\app.mori.desk",false)){
   if(key==null){Need(hive!=RegistryHive.CurrentUser,"Both current-user browser registrations must exist in both views.");continue;}object raw=key.GetValue("",null,RegistryValueOptions.DoNotExpandEnvironmentNames);Need(raw is string&&key.GetValueKind("")==RegistryValueKind.String,"Exact native registration string type.");string path=(string)raw;Need(path.Equals(manifest,StringComparison.OrdinalIgnoreCase),"Every existing native host registration names the actual generated manifest.");Need(File.Exists(path)&&Hash(path)==Hash(manifest),"Every existing native host manifest is readable and byte-identical.");var target=Read(path);Need(Text(target["path"]).Equals(launcher,StringComparison.OrdinalIgnoreCase)&&File.Exists(Text(target["path"]))&&Hash(Text(target["path"]))==Hash(launcher),"Every registered native target exists and is the owned stable launcher.");registrations.Add(new{hive=hive.ToString(),view=view.ToString(),browser=name});
  }
  var product=Assembly.LoadFrom(app);Need(product.Location.Equals(app,StringComparison.OrdinalIgnoreCase),"Reflection reads the actual public42 assembly.");var manager=product.GetType("MoriDesk.BrowserExtensionManager",true);var files=(string[])manager.GetField("RuntimeFiles",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);Need(files.Length>10&&files.Length<100,"Actual public42 required runtime inventory is bounded.");foreach(string file in files){Need(Path.GetFileName(file)==file,"Runtime inventory has basename-only entries.");string path=Path.Combine(browser,"extension",file);Plain(path);Need(File.Exists(path)&&new FileInfo(path).Length>0,"Actual generated required runtime file exists: "+file);}
  var type=product.GetType("MoriDesk.InstalledCleanupState",true);var capture=type.GetMethod("Capture",BindingFlags.NonPublic|BindingFlags.Static,null,new[]{typeof(string),typeof(string),typeof(IEnumerable<string>)},null);Need(capture!=null,"Exact production Capture method, never injected CaptureWith.");var snap=capture.Invoke(null,new object[]{app,Program,new[]{Data}});bool complete=Convert.ToBoolean(Field(snap,"Complete")),switched=Convert.ToBoolean(Field(snap,"EntrypointsSwitched"));string reason=Text(Field(snap,"Reason"));string[] running=(string[])Field(snap,"RunningExecutables"),references=(string[])Field(snap,"ReferencedExecutables"),protectedPaths=(string[])Field(snap,"ProtectedPaths");Snapshot=new{complete,entrypointsSwitched=switched,reason,runningCount=running.Length,referenceCount=references.Length,protectedPathCount=protectedPaths.Length};Need(complete&&switched&&reason=="ready","Production public42 cleanup snapshot is complete and all entrypoints switched.");Need(running.Length==0,"Production snapshot has no installed executable still running.");Need(references.Length>0&&references.All(path=>path.Equals(launcher,StringComparison.OrdinalIgnoreCase)),"Every managed reference observed by production points to the stable launcher.");
  return new{appSha256=Hash42,launcherSha256=Hash(launcher),manifestSha256=Hash(manifest),catalogSha256=Hash(catalog),registrations,runtimeFiles=files.Length,productionSnapshot=Snapshot,sourceWrites=false,realChromeLoaded=false};
 }
 static int Main(string[] args){bool passed=false;object result=null;Exception error=null;try{Need(args.Length==1&&args[0]=="--validate-public42-browser","Explicit read-only baseline validation.");Guard();using(var watchdog=new Timer(_=>Environment.Exit(124),null,120000,Timeout.Infinite)){result=Validate();passed=true;}}catch(Exception e){error=e;}finally{if(Package!=null){string file=Path.Combine(Package,"healthy42-preflight-result.json");using(var stream=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.Read))using(var writer=new StreamWriter(stream,Utf8)){writer.Write(Json.Serialize(new{passed,result,productionSnapshot=Snapshot,checks=Checks,error=error==null?null:error.ToString(),hostExecuted=false,sourceWrites=false}));writer.Flush();stream.Flush(true);}}}return passed?0:1;}
}
'''

provenance=[]
for name,(origin,before,after) in sources.items():
    src=PKG/(name+'.cs');src.write_text(after,encoding='utf-8-sig')
    (PKG/(name+'.diff')).write_text(''.join(difflib.unified_diff(before.splitlines(True),after.splitlines(True),fromfile=str(origin),tofile=name+'.cs')),encoding='utf-8')
    original=read(OLD/'helpers'/(name+'.provenance.json'))
    refs=[pathlib.Path(item['path']) for item in original['inputs'][1:]]
    for p,item in zip(refs,original['inputs'][1:]):assert sha(p)==item['sha256']
    provenance.append((name,src,origin,refs))
name='InstalledUpgradeHealthGuest';src=PKG/(name+'.cs');src.write_text(health,encoding='utf-8-sig')
refs=[pathlib.Path(item['path']) for item in read(OLD/'helpers'/'InstalledUpgradePreserveGuest.provenance.json')['inputs'][1:]]
provenance.append((name,src,None,refs))
for name,src,origin,refs in provenance:
    compiler=pathlib.Path(r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe');exe=PKG/(name+'.exe')
    cmd=[str(compiler),'/nologo','/target:exe','/optimize+','/platform:x64','/codepage:65001','/out:'+str(exe)]+['/r:'+str(p) for p in refs]+[str(src)]
    run=subprocess.run(cmd,capture_output=True,text=True);(OUT/(name+'.compile.log')).write_text(run.stdout+run.stderr,encoding='utf-8');assert run.returncode==0,run.stdout+run.stderr
    write(PKG/(name+'.provenance.json'),dict(originalSource=str(origin) if origin else None,originalSourceSha256=sha(origin) if origin else None,sourceSha256=sha(src),command=cmd,inputs=[dict(path=str(p),sha256=sha(p),bytes=p.stat().st_size) for p in [src]+refs],outputSha256=sha(exe),hostExecution='Compiler only'))
shutil.copyfile(PKG/'InstalledUpgradeDriverGuest.exe',PKG/'Mori.exe')
write(PKG/'scope.json',dict(purpose='Independent healthy public42 baseline after observed fixture-native-registration contamination',original1215RetainedFailed=True,productionChanged=False,registryWrittenOnlyByPublic42Ui=True,actualChromeLoaded=False,fixtureRoot=r'C:\NookLab\Upgrade42To45-20261113-r3-healthy',preservedRoot=r'C:\NookLab\Audit\upgrade42to45-preserved-20261113-r3-healthy',noPayloadDeletedByHelper=True,failurePolicy='Stop on failure; retain failed reports and state. No automatic retry/reseed/rollback.',physicalProductExecution=False))
zip_path=OUT/'upgrade-healthy-helpers.zip'
with zipfile.ZipFile(zip_path,'x',zipfile.ZIP_DEFLATED,compresslevel=1) as z:
    for p in sorted(PKG.iterdir()):z.write(p,p.name)
with zipfile.ZipFile(zip_path) as z:assert z.testzip() is None;members=len(z.infolist())
handoff=dict(path=str(zip_path),sha256=sha(zip_path),bytes=zip_path.stat().st_size,members=members,guest='C:\\NookLab\\Audit\\apps\\'+sha(zip_path)[:12],sources=[dict(name=n,sha256=sha(s),exeSha256=sha(PKG/(n+'.exe'))) for n,s,_,_ in provenance],hostExecution='Compiler/package only',runAuthorized=False)
write(OUT/'handoff.json',handoff)
plan=read(OLD/'journey-plan-bound.json');plan['purpose']='Independent healthy public42-to-frozenR3 upgrade; original1215 remains failed';plan['runAuthorized']=False;plan['inventory']=None;plan['inventoryState']='Fresh actual current45 inventory required before preservation; exact path and hash must be independently bound.';plan['packages']=plan['packages'][:2]+[handoff];plan.pop('sourceAdaptations');plan['preservedRoot']+='-healthy';plan['fixtureRoot']+='-healthy';plan['originalFailureRetained']=dict(task=1215,sha256=sha(BASE/'r3-upgrade-launcher-race/results/install45-result.json'));plan['preUpgradeHealthyGate']='After public42 UI preparation, fresh independent read-only inventory and actual production Capture must both pass independent review before any seed or upgrade.'
plan['stages']=[dict(stage,guestPackage=handoff['guest'],args=[arg.replace('20261113-r3','20261113-r3-healthy') for arg in stage['args']]) for stage in plan['stages']]
plan['stages'][0]['args']=['--preserve-reviewed-vm-state',None,None]
plan['stages'][2:2]=[dict(report='prepare-browser42-result.json',exe='InstalledUpgradeDriverGuest.exe',args=['--prepare-browser42'],guestPackage=handoff['guest']),dict(report='healthy42-preflight-result.json',exe='InstalledUpgradeHealthGuest.exe',args=['--validate-public42-browser'],guestPackage=handoff['guest'],independentInventoryAlsoRequired=True,stopForReviewBeforeNextStage=True)]
write(OUT/'journey-plan-unbound.json',plan)
print(json.dumps(handoff,indent=2))
