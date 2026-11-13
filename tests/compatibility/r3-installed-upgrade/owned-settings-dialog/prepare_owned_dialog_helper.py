"""Compile-only scoped supplement for public42's owned modal Settings window."""
import hashlib,json,pathlib,subprocess,difflib,zipfile
BASE=pathlib.Path(__file__).resolve().parent
REPO=pathlib.Path(r'D:\Nook-DevTestVM\audit\20261026-file-previews-source')
ORIGIN=BASE/'r3-upgrade-healthy-v3'/'package'
OUT=BASE/'r3-prepare-owned-dialog'
assert not OUT.exists();OUT.mkdir();PKG=OUT/'package';PKG.mkdir()
def sha(p):return hashlib.sha256(pathlib.Path(p).read_bytes()).hexdigest().upper()
def read(p):return json.loads(pathlib.Path(p).read_text(encoding='utf-8-sig'))
def write(p,v):pathlib.Path(p).write_text(json.dumps(v,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
def replace(text,old,new):assert text.count(old)==1,(old,text.count(old));return text.replace(old,new)
origin=ORIGIN/'InstalledUpgradeDriverGuest.cs'
assert sha(origin)=='4C6F18CB11C91C3279065E8D5B37374FEEA7BE904AB54984559ADF074AA89354'
before=origin.read_text(encoding='utf-8-sig');after=before
native=r''' // Only PrepareBrowser42 uses native top-level enumeration. No UIA desktop
 // traversal, title read or action is performed for another process.
 delegate bool PreparationEnumProc(IntPtr window,IntPtr context);
 [DllImport("user32.dll",SetLastError=true)]static extern bool EnumWindows(PreparationEnumProc callback,IntPtr context);
 [DllImport("user32.dll",SetLastError=true)]static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowText(IntPtr window,StringBuilder text,int capacity);
 [DllImport("user32.dll")]static extern IntPtr GetWindow(IntPtr window,uint command);
 [DllImport("user32.dll")]static extern bool IsWindow(IntPtr window);
 static readonly Dictionary<long,object> PreparationWindowsObserved=new Dictionary<long,object>();
 static AutomationElement PreparationSettings(int pid,IntPtr main,bool requireVisible){
  var handles=new List<IntPtr>();bool enumerated=EnumWindows(delegate(IntPtr window,IntPtr context){uint ownerPid;GetWindowThreadProcessId(window,out ownerPid);if(ownerPid==(uint)pid)handles.Add(window);return handles.Count<=128;},IntPtr.Zero);
  if(!enumerated||handles.Count>128)throw new IOException("Cannot enumerate bounded exact-public42 native windows.");
  AutomationElement found=null;
  foreach(IntPtr window in handles){
   uint windowPid;GetWindowThreadProcessId(window,out windowPid);if(windowPid!=(uint)pid)continue;
   var title=new StringBuilder(512);GetWindowText(window,title,title.Capacity);bool visible=IsWindowVisible(window),enabled=IsWindowEnabled(window);IntPtr owner=GetWindow(window,4);
   PreparationWindowsObserved[window.ToInt64()]=new{pid,handle=window.ToInt64(),title=title.ToString(),visible,enabled,ownerHandle=owner.ToInt64(),expectedMainHandle=main.ToInt64(),observedUtc=DateTime.UtcNow.ToString("O")};
   if(PreparationWindowsObserved.Count>128)throw new IOException("Too many exact-public42 native windows observed.");
   if(title.ToString()!="Nook · 设置"||owner!=main||(requireVisible&&!visible))continue;
   try{var element=AutomationElement.FromHandle(window);var current=element.Current;
    if(current.ProcessId!=pid||current.NativeWindowHandle!=window.ToInt32()||current.Name!="Nook · 设置"||current.ControlType!=ControlType.Window)throw new InvalidDataException("Exact native Settings window does not match its public42 UIA identity.");
    if(found!=null)throw new InvalidDataException("More than one owned public42 Settings window.");found=element;
   }catch(ElementNotAvailableException){uint livePid;GetWindowThreadProcessId(window,out livePid);if(IsWindow(window)&&livePid==(uint)pid)throw;}
  }
  return found;
 }
'''
after=replace(after,' static object PrepareBrowser42(){',native+' static object PrepareBrowser42(){')
after=replace(after,'   var main=AutomationElement.FromHandle(child.MainWindowHandle);var settingsButton=', '   IntPtr mainHandle=child.MainWindowHandle;var main=AutomationElement.FromHandle(mainHandle);var settingsButton=')
after=replace(after,'settings=Windows(child.Id).FirstOrDefault(w=>w.Current.Name=="Nook · 设置");','settings=PreparationSettings(child.Id,mainHandle,true);')
after=replace(after,'Wait(()=>!Windows(child.Id).Any(w=>w.Current.Name=="Nook · 设置"),6000,"Settings did not close normally.");','Wait(()=>PreparationSettings(child.Id,mainHandle,false)==null,6000,"Settings did not close normally.");')
after=replace(after,'Operation=args.Length>0?args[0]:"";Need(', 'Operation=args.Length>0?args[0]:"";Need(args.Length==1&&Operation=="--prepare-browser42","This supplement only retries public42 browser preparation; never install, launch-only, reseed or preserve.");Need(')
after=replace(after,'uiActions=Actions,forcedCleanupAtProcessExit=', 'uiActions=Actions,preparationNativeWindows=PreparationWindowsObserved.Values.ToArray(),forcedCleanupAtProcessExit=')
# These complete blocks are byte-for-byte the original reviewed source.
boundaries=[(' static object Install(', ' // The fixed launcher'),(' static object Launch(', ' static object PrepareBrowser42(){'),(' static void CloseApp(', ' static ProcessStartInfo Start('),(' static AutomationElement[] Windows(', ' static AutomationElement Find(')]
unchanged=[]
for start,end in boundaries:
    old=before[before.index(start):before.index(end)]
    new=after[after.index(start):after.index(native if start==' static object Launch(' else end)]
    assert old==new,start
    unchanged.append(dict(section=start.strip(),sha256=hashlib.sha256(old.encode('utf-8')).hexdigest().upper()))
src=PKG/'InstalledUpgradeDriverGuest.cs';src.write_text(after,encoding='utf-8-sig')
(PKG/'owned-dialog.diff').write_text(''.join(difflib.unified_diff(before.splitlines(True),after.splitlines(True),fromfile='reviewed95618/InstalledUpgradeDriverGuest.cs',tofile='owned-dialog/InstalledUpgradeDriverGuest.cs')),encoding='utf-8')
source_evidence=[]
for path,first,last in [('modules/library/src/MainWindow.cs',298,298),('src/MainWindow.Desk.cs',45,53),('src/DeskSettingsWindow.cs',17,18)]:
    data=subprocess.check_output(['git','show','v0.1.42-preview:'+path],cwd=REPO)
    text=data.decode('utf-8');source_evidence.append(dict(path=path,sha256=hashlib.sha256(data).hexdigest().upper(),firstLine=first,lastLine=last,excerpt='\n'.join(text.splitlines()[first-1:last])))
commit=subprocess.check_output(['git','rev-parse','v0.1.42-preview^{commit}'],cwd=REPO,text=True).strip()
write(PKG/'public42-owned-dialog-evidence.json',dict(tag='v0.1.42-preview',commit=commit,files=source_evidence,conclusion='Settings click calls ShowSettings -> ShowDeskSettings -> OpenDeskSettings, creates DeskSettingsWindow with Owner=main window and exact title, then ShowDialog. Original UIA RootElement.Children lookup was not exhaustive for owned modal windows; failure alone does not prove that lookup was the only cause.'))
prov=read(ORIGIN/'InstalledUpgradeDriverGuest.provenance.json');refs=[pathlib.Path(item['path']) for item in prov['inputs'][1:]]
for p,item in zip(refs,prov['inputs'][1:]):assert sha(p)==item['sha256']
exe=PKG/'InstalledUpgradeDriverGuest.exe';compiler=pathlib.Path(r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe')
cmd=[str(compiler),'/nologo','/target:exe','/optimize+','/platform:x64','/codepage:65001','/out:'+str(exe)]+['/r:'+str(p) for p in refs]+[str(src)]
run=subprocess.run(cmd,capture_output=True,text=True);(OUT/'compile.log').write_text(run.stdout+run.stderr,encoding='utf-8');assert run.returncode==0,run.stdout+run.stderr
write(PKG/'provenance.json',dict(originalSource=str(origin),originalSourceSha256=sha(origin),sourceSha256=sha(src),outputSha256=sha(exe),command=cmd,inputs=[dict(path=str(p),sha256=sha(p)) for p in [src]+refs],unchangedSections=unchanged,hostExecution='Compiler only',original1253RetainedFailed=True,otherStagesMustUseOriginal95618=True))
zip_path=OUT/'prepare-owned-dialog.zip'
with zipfile.ZipFile(zip_path,'x',zipfile.ZIP_DEFLATED,compresslevel=1) as z:
    for p in sorted(PKG.iterdir()):z.write(p,p.name)
with zipfile.ZipFile(zip_path) as z:assert z.testzip() is None
handoff=dict(path=str(zip_path),sha256=sha(zip_path),guest='C:\\NookLab\\Audit\\apps\\'+sha(zip_path)[:12],executable=exe.name,args=['--prepare-browser42'],report='prepare-browser42-result.json',sourceSha256=sha(src),exeSha256=sha(exe),original1253RetainedFailed=True,existing42InstallationReused=True,otherStagesMustUseOriginal95618=True,hostExecution='Compiler/package only',runAuthorized=False)
write(OUT/'handoff.json',handoff);print(json.dumps(handoff,indent=2))
