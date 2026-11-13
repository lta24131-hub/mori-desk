"""Compile-only exact launch supplement; never execute product or VM queue."""
import hashlib,json,pathlib,subprocess,difflib,zipfile
BASE=pathlib.Path(__file__).resolve().parent
ORIGIN=BASE/'r3-upgrade-healthy-v3/package'
OUT=BASE/'r3-native-launch-v2';assert not OUT.exists();OUT.mkdir();PKG=OUT/'package';PKG.mkdir()
def sha(p):return hashlib.sha256(pathlib.Path(p).read_bytes()).hexdigest().upper()
def read(p):return json.loads(pathlib.Path(p).read_text(encoding='utf-8-sig'))
def write(p,v):pathlib.Path(p).write_text(json.dumps(v,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
def replace(text,old,new):assert text.count(old)==1,(old,text.count(old));return text.replace(old,new)
origin=ORIGIN/'InstalledUpgradeDriverGuest.cs';assert sha(origin)=='4C6F18CB11C91C3279065E8D5B37374FEEA7BE904AB54984559ADF074AA89354'
before=origin.read_text(encoding='utf-8-sig');after=before
native=r''' [DllImport("kernel32.dll",EntryPoint="QueryFullProcessImageNameW",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder path,ref uint length);
 [DllImport("kernel32.dll",SetLastError=true)]static extern uint GetProcessId(IntPtr process);
 // Native process-image query is bound to the retained candidate handle/PID.
 // It does not enumerate Process.Modules while the child loader is starting.
 static string NativeProcessImage(Process process){
  IntPtr handle=process.Handle;uint pid=GetProcessId(handle);
  if(pid==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Cannot identify the retained candidate process handle.");
  if(pid!=(uint)process.Id)throw new InvalidDataException("Candidate handle no longer names the exact candidate PID.");
  var path=new StringBuilder(32768);uint count=(uint)path.Capacity;
  if(!QueryFullProcessImageName(handle,0,path,ref count))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Cannot query the exact candidate executable image.");
  if(count==0||count>=path.Capacity||string.IsNullOrEmpty(path.ToString()))throw new InvalidDataException("Live launcher candidate has no executable path; identity cannot be verified.");
  return path.ToString();
 }
'''
after=replace(after,' static string CandidateImage(Process candidate,int bootstrapPid){',native+' static string CandidateImage(Process candidate,int bootstrapPid){')
old='''   var module=candidate.MainModule;
   if(module==null){if(candidate.HasExited)return null;throw new InvalidDataException("Live launcher candidate has no main module; identity cannot be verified.");}
   string path=module.FileName;
   if(string.IsNullOrEmpty(path))throw new InvalidDataException("Live launcher candidate has no executable path; identity cannot be verified.");
   return path;'''
after=replace(after,old,'   return NativeProcessImage(candidate);')
after=replace(after,'if((error is InvalidOperationException||error is System.ComponentModel.Win32Exception||error is NullReferenceException)&&candidate.HasExited)return null;','if((error is InvalidOperationException||error is System.ComponentModel.Win32Exception)&&candidate.HasExited)return null;')
after=replace(after,'Hash(child.MainModule.FileName)==(newer?NewApp:OldApp)','Hash(NativeProcessImage(child))==(newer?NewApp:OldApp)')
close_before=before[before.index(' static void CloseApp('):before.index(' static ProcessStartInfo Start(')]
close_launch=close_before.replace('static void CloseApp(', 'static void CloseLaunchApp(').replace('Hash(app.MainModule.FileName)','Hash(NativeProcessImage(app))')
after=replace(after,' static string CandidateImage(Process candidate,int bootstrapPid){',close_launch+' static string CandidateImage(Process candidate,int bootstrapPid){')
launch_start=after.index(' static object Launch(');launch_end=after.index(' static object PrepareBrowser42(){')
launch=after[launch_start:launch_end];assert launch.count('CloseApp(child,newer?NewApp:OldApp)')==1
after=after[:launch_start]+launch.replace('CloseApp(child,newer?NewApp:OldApp)','CloseLaunchApp(child,newer?NewApp:OldApp)')+after[launch_end:]
after=replace(after,'Operation=args.Length>0?args[0]:"";Need(', 'Operation=args.Length>0?args[0]:"";Need(args.Length==1&&(Operation=="--launch42"||Operation=="--launch45"),"Native image-query supplement only launches exact installed42/45; never install, prepare, seed or preserve.");Need(')
after=replace(after,'installationStage=Operation=="--install42"||Operation=="--install45",error=', 'installationStage=Operation=="--install42"||Operation=="--install45",processImageQuery="QueryFullProcessImageNameW",error=')
unchanged=[]
for start,end in [(' static object Install(', ' // The fixed launcher'),(' static object PrepareBrowser42(){',' [STAThread]static int Main('),(' static void Wait(', ' static void NoProduct('),(' static AutomationElement[] Windows(', ' static bool TrySession('),(' static void CloseApp(', ' static ProcessStartInfo Start(')]:
    oldblock=before[before.index(start):before.index(end)]
    newblock=after[after.index(start):after.index(end)]
    assert oldblock==newblock,start
    unchanged.append(dict(section=start.strip(),sha256=hashlib.sha256(oldblock.encode('utf-8')).hexdigest().upper()))
# Launch changes only its executable-path provider. All other assertions and
# normal-close menu/window/process/exit-code checks remain textually identical.
for start,end in [(' static object Launch(', ' static object PrepareBrowser42(){')]:
    oldblock=before[before.index(start):before.index(end)]
    newblock=after[after.index(start):after.index(end)]
    assert oldblock.replace('Hash(child.MainModule.FileName)','Hash(NativeProcessImage(child))').replace('CloseApp(child,newer?NewApp:OldApp)','CloseLaunchApp(child,newer?NewApp:OldApp)')==newblock
assert close_launch.replace('static void CloseLaunchApp(','static void CloseApp(').replace('Hash(NativeProcessImage(app))','Hash(app.MainModule.FileName)')==close_before
src=PKG/'InstalledUpgradeLaunchGuest.cs';src.write_text(after,encoding='utf-8-sig')
(PKG/'native-process-image.diff').write_text(''.join(difflib.unified_diff(before.splitlines(True),after.splitlines(True),fromfile='reviewed95618/InstalledUpgradeDriverGuest.cs',tofile=src.name)),encoding='utf-8')
prov=read(ORIGIN/'InstalledUpgradeDriverGuest.provenance.json');refs=[pathlib.Path(item['path']) for item in prov['inputs'][1:]]
for p,item in zip(refs,prov['inputs'][1:]):assert sha(p)==item['sha256']
exe=PKG/'InstalledUpgradeLaunchGuest.exe';compiler=pathlib.Path(r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe')
cmd=[str(compiler),'/nologo','/target:exe','/optimize+','/platform:x64','/codepage:65001','/out:'+str(exe)]+['/r:'+str(p) for p in refs]+[str(src)]
run=subprocess.run(cmd,capture_output=True,text=True);(OUT/'compile.log').write_text(run.stdout+run.stderr,encoding='utf-8');assert run.returncode==0,run.stdout+run.stderr
write(PKG/'provenance.json',dict(originalSource=str(origin),originalSourceSha256=sha(origin),sourceSha256=sha(src),outputSha256=sha(exe),command=cmd,inputs=[dict(path=str(p),sha256=sha(p)) for p in [src]+refs],unchangedSections=unchanged,normalCloseAssertionsUnchanged=True,launchPathCheckOnlyChangesProvider=True,hostExecution='Compiler only',original1262RetainedFailed=True,otherStagesMustUseOriginal95618=True))
zip_path=OUT/'native-launch.zip'
with zipfile.ZipFile(zip_path,'x',zipfile.ZIP_DEFLATED,compresslevel=1) as z:
    for p in sorted(PKG.iterdir()):z.write(p,p.name)
with zipfile.ZipFile(zip_path) as z:assert z.testzip() is None
handoff=dict(path=str(zip_path),sha256=sha(zip_path),guest='C:\\NookLab\\Audit\\apps\\'+sha(zip_path)[:12],executable=exe.name,allowedArgs=[['--launch42'],['--launch45']],reports=['launch42-result.json','launch45-result.json'],sourceSha256=sha(src),exeSha256=sha(exe),original1262RetainedFailed=True,existing42InstallationAndSeedReused=True,otherStagesMustUseOriginal95618=True,hostExecution='Compiler/package only',runAuthorized=False)
write(OUT/'handoff.json',handoff);print(json.dumps(handoff,indent=2))
