"""Compile-only minimal public42 owned-dialog close fix; never run on the host."""
import difflib, hashlib, json, pathlib, subprocess, zipfile
base = pathlib.Path(__file__).resolve().parent
origin = base/'r3-prepare-owned-dialog/package/InstalledUpgradeDriverGuest.cs'
native = base/'r3-native-launch-v3/package/InstalledUpgradeLaunchGuest.cs'
sha = lambda path: hashlib.sha256(path.read_bytes()).hexdigest().upper()
assert sha(origin) == '634DD34E836ED08DD1C55AC80776B2D404BE43BD5EEBFD08195CBCFA85E5036D'
assert sha(native) == '371B26653C5506338A99903D7AC76BDD50514F9DDB34AB14D960EA4E1975F705'
root = base/'r4-owned-prepare-v3'
package = root/'package'
package.mkdir(parents=True, exist_ok=False)
before = origin.read_text(encoding='utf-8-sig')
text = before
native_text = native.read_text(encoding='utf-8-sig')
native_start = native_text.index(' [DllImport("kernel32.dll",EntryPoint="QueryFullProcessImageNameW"')
native_end = native_text.index(' static object Launch(bool newer)', native_start)
candidate_start = text.index(' static string CandidateImage(Process candidate,int bootstrapPid)')
candidate_end = text.index(' static object Launch(bool newer)', candidate_start)
text = text[:candidate_start] + native_text[native_start:native_end] + text[candidate_end:]
insertion = r'''
 // Verify the live owned dialog once before Close. During destruction, UIA
 // identity is unavailable; completion uses only this retained native handle.
 static long ClosePreparationSettings(AutomationElement settings,Process child,IntPtr main){
  Need(!child.HasExited&&Hash(NativeProcessImage(child))==OldApp,"Only exact live public42 may close its owned Settings dialog.");
  int pid=child.Id;var current=settings.Current;IntPtr window=new IntPtr(current.NativeWindowHandle);uint ownerPid,mainPid;
  Need(window!=IntPtr.Zero&&IsWindow(window)&&GetWindowThreadProcessId(window,out ownerPid)!=0&&ownerPid==(uint)pid&&GetWindow(window,4)==main,"Retained Settings handle belongs to exact public42 PID and main-window owner.");
  Need(IsWindow(main)&&GetWindowThreadProcessId(main,out mainPid)!=0&&mainPid==(uint)pid,"Retained Settings owner is the exact public42 main window.");
  var title=new StringBuilder(512);GetWindowText(window,title,title.Capacity);
  Need(current.ProcessId==pid&&current.Name=="Nook · 设置"&&current.ControlType==ControlType.Window&&title.ToString()=="Nook · 设置","Live Settings native and UIA identities agree before normal close.");
  object pattern=null;Need(settings.TryGetCurrentPattern(WindowPattern.Pattern,out pattern),"Only owned public42 Settings dialog closes normally.");
  ((WindowPattern)pattern).Close();
  Wait(()=>{
   bool closed=!IsWindow(window);
   if(!closed){uint livePid;uint thread=GetWindowThreadProcessId(window,out livePid);
    if(thread==0){if(!IsWindow(window))closed=true;else throw new InvalidDataException("Live retained Settings handle cannot be identified during normal close.");}
    else closed=livePid!=(uint)pid;
   }
   uint liveMainPid;
   return closed&&IsWindow(main)&&GetWindowThreadProcessId(main,out liveMainPid)!=0&&liveMainPid==(uint)pid&&IsWindowVisible(main)&&IsWindowEnabled(main);
  },6000,"Retained Settings window did not close and restore its verified owner within the original budget.");
  Need(!child.HasExited&&Hash(NativeProcessImage(child))==OldApp,"Public42 remains the same live process after Settings closes.");
  Need(IsWindow(main)&&GetWindowThreadProcessId(main,out mainPid)!=0&&mainPid==(uint)pid&&IsWindowVisible(main)&&IsWindowEnabled(main),"Verified main window is visible and enabled after its Settings dialog closes.");
  return window.ToInt64();
 }
'''
anchor = ' static object PrepareBrowser42(){'
assert text.count(anchor) == 1
text = text.replace(anchor, insertion + anchor)
old_identity = 'Need(child!=null&&Hash(child.MainModule.FileName)==OldApp,"Browser preparation belongs to exact genuine public42.");'
assert text.count(old_identity) == 1
text = text.replace(old_identity, old_identity.replace('child.MainModule.FileName', 'NativeProcessImage(child)'))
old_close = '''   object pattern=null;Need(settings.Current.ProcessId==child.Id&&settings.TryGetCurrentPattern(WindowPattern.Pattern,out pattern),"Only owned public42 Settings dialog closes normally.");((WindowPattern)pattern).Close();
   Wait(()=>PreparationSettings(child.Id,mainHandle,false)==null,6000,"Settings did not close normally.");
   CloseApp(child,OldApp);child.Dispose();Wait(()=>bootstrap.HasExited,5000,"Fixed launcher did not exit after browser preparation.");'''
new_close = '''   long closedSettingsHandle=ClosePreparationSettings(settings,child,mainHandle);
   CloseLaunchApp(child,OldApp);child.Dispose();Wait(()=>bootstrap.HasExited,5000,"Fixed launcher did not exit after browser preparation.");'''
assert text.count(old_close) == 1
text = text.replace(old_close, new_close)
old_result = 'return new{publicUiOnly=true,clickedInstallRepairOnce=true,realChromeLoaded=false,manifestSha256=Hash(manifest),catalogSha256=Hash(catalog),runtimeManifestSha256=Hash(runtime),localListeningObserved=true,normalClose=true};'
assert text.count(old_result) == 1
text = text.replace(old_result, old_result.replace('normalClose=true', 'normalClose=true,settingsClosedHandle=closedSettingsHandle,settingsCloseProbe="native-handle-lifetime",processImageQuery="QueryFullProcessImageNameW"'))
text = text.replace('This supplement only retries public42 browser preparation; never install, launch-only, reseed or preserve.', 'This reviewed helper only prepares a fresh public42 browser baseline; never install, launch-only, reseed or preserve.')
assert 'PreparationSettings(child.Id,mainHandle,false)' not in text
src = package/'InstalledUpgradePrepareGuest.cs'
src.write_text(text, encoding='utf-8')
(package/'owned-close-native.diff').write_text(''.join(difflib.unified_diff(before.splitlines(True),text.splitlines(True),fromfile='retained3ECC/InstalledUpgradeDriverGuest.cs',tofile=src.name)), encoding='utf-8')
prov = json.loads((base/'r3-upgrade-healthy-v3/package/InstalledUpgradeDriverGuest.provenance.json').read_text(encoding='utf-8-sig'))
refs = [pathlib.Path(item['path']) for item in prov['inputs'][1:]]
for path,item in zip(refs,prov['inputs'][1:]):
    assert sha(path) == item['sha256']
compiler = pathlib.Path(r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe')
exe = package/'InstalledUpgradePrepareGuest.exe'
cmd = [str(compiler),'/nologo','/target:exe','/optimize+','/platform:x64','/codepage:65001','/out:'+str(exe)]+['/r:'+str(path) for path in refs]+[str(src)]
run = subprocess.run(cmd, capture_output=True, text=True, creationflags=0x08000000)
(root/'compile.log').write_text(run.stdout+run.stderr, encoding='utf-8')
assert run.returncode == 0, run.stdout+run.stderr
(package/'Mori.exe').write_bytes(exe.read_bytes())
provenance = dict(originalPrepareSourceSha256=sha(origin),nativeSourceSha256=sha(native),sourceSha256=sha(src),
    exeSha256=sha(exe),command=cmd,inputs=[dict(path=str(path),sha256=sha(path)) for path in [src]+refs],
    original1253RetainedFailed=True,original1276RetainedFailed=True,original1262RetainedFailed=True,
    hostExecution='Compiler/package only; helper never executed',freshBaselineRequired=True,
    destructiveMethodsRemainUnreachable=True,settingsCloseUsesNoDestroyingUiARead=True)
(package/'provenance.json').write_text(json.dumps(provenance,indent=2),encoding='utf-8')
archive = root/'owned-prepare-v3.zip'
with zipfile.ZipFile(archive,'x',zipfile.ZIP_DEFLATED,compresslevel=1) as z:
    for path in sorted(package.iterdir()):
        z.write(path,path.name)
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None and z.read('Mori.exe')==z.read(exe.name)
handoff = dict(path=str(archive),sha256=sha(archive),guest='C:\\NookLab\\Audit\\apps\\'+sha(archive)[:12],
    executable=exe.name,args=['--prepare-browser42'],report='prepare-browser42-result.json',
    sourceSha256=sha(src),exeSha256=sha(exe),original1276RetainedFailed=True,
    freshPublic42AndEmptyBrowserRequired=True,existing1304StateMustNotRunThisHelper=True,
    originalTimeoutsUnchanged=True,normalSettingsCloseRequired=True,normalAppCloseRequired=True,
    hostExecution='Compiler/package only; no helper/product execution',runAuthorized=False)
(root/'handoff.json').write_text(json.dumps(handoff,indent=2),encoding='utf-8')
print(json.dumps(handoff,indent=2))
