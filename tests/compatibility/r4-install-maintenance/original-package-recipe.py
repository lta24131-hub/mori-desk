"""R4 narrow regression packaging. Host compilation/static checks only."""
import hashlib, json, os, pathlib, shutil, subprocess, zipfile

BASE=pathlib.Path(r'D:\Nook-DevTestVM\audit\20261113-worker-race-rebind')
REPO=BASE.parent/'20261026-file-previews-source'
RELEASE=BASE.parent/'20261113-release45-r4'
BUILD=RELEASE/'release-build'
OUT=BASE/'r4-native-regressions'
APP='A26E4E47DF3474355622B4E5C0C31F823D3F57F1A7D8DF6BFA4221765D66FB22'
OLDAPP='97B122310B1033BE7EBDA63730F929C256D8D0972CBC3497C7A7D6539CCA50D6'
FRAMEWORK=pathlib.Path(r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319')
CSC=FRAMEWORK/'csc.exe'

def sha(p):
    with pathlib.Path(p).open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()
def read(p):return pathlib.Path(p).read_text(encoding='utf-8-sig')
def write(p,o):
    with pathlib.Path(p).open('x',encoding='utf-8') as f:json.dump(o,f,ensure_ascii=False,indent=2)
def copy(a,b):
    b=pathlib.Path(b);b.parent.mkdir(parents=True,exist_ok=True);assert not b.exists();shutil.copyfile(a,b);assert sha(a)==sha(b)
def compile_(folder,name,refs):
    source=folder/(name+'.cs');refs=[FRAMEWORK/p for p in refs]
    rows=[dict(kind=k,path=str(p),sha256=sha(p)) for k,paths in [('source',[source]),('reference',refs)] for p in paths]
    command=[str(CSC),'/nologo','/optimize+','/target:exe','/platform:x64','/codepage:65001','/out:'+str(folder/(name+'.exe'))]+['/r:'+str(p) for p in refs]+[str(source)]
    result=subprocess.run(command,capture_output=True,text=True)
    (folder/(name+'.compile.log')).write_text(result.stdout+result.stderr,encoding='utf-8')
    assert result.returncode==0,result.stdout+result.stderr
    assert all(sha(r['path'])==r['sha256'] for r in rows)
    write(folder/(name+'.provenance.json'),dict(candidateAppSha256=APP,hostExecution='Compiler only; no tests/product run',compilerSha256=sha(CSC),command=command,inputs=rows,output=dict(path=name+'.exe',sha256=sha(folder/(name+'.exe')))))
def seal(folder,name,extras=()):
    rows=[(p,p.relative_to(folder).as_posix()) for p in sorted(folder.rglob('*')) if p.is_file() and not p.name.endswith('.compile.log')]+list(extras)
    assert len({r.lower() for _,r in rows})==len(rows)
    path=OUT/(name+'.zip')
    with path.open('xb') as f:
        with zipfile.ZipFile(f,'w',compression=zipfile.ZIP_DEFLATED,compresslevel=1) as z:
            for p,n in rows:z.write(p,n)
        f.flush();os.fsync(f.fileno())
    with zipfile.ZipFile(path) as z:
        assert z.testzip() is None
        for p,n in rows:
            with z.open(n) as f:assert hashlib.file_digest(f,'sha256').hexdigest().upper()==sha(p)
    value=dict(path=str(path),sha256=sha(path),bytes=path.stat().st_size,members=len(rows));value['guest']='C:\\NookLab\\Audit\\apps\\'+value['sha256'][:12]
    return value

assert sha(BUILD/'Mori.exe')==APP
candidate=json.loads(read(RELEASE/'candidate-inputs.json'))
assert candidate['appSha256']==APP
for row in candidate['inputs']:assert sha(row['path'])==row['sha256'],row['path']
safety=json.loads(read(RELEASE/'installer-safety-inputs.json'))
assert sha(RELEASE/'installer-safety/InstallerSafetyTests.exe')==safety['executableSha256']
for row in safety['sources']:assert sha(REPO/row['path'])==row['sha256'],row['path']
assert not OUT.exists();OUT.mkdir()
copy(RELEASE/'candidate-inputs.json',OUT/'candidate-inputs.json')
copy(RELEASE/'installer-safety-inputs.json',OUT/'installer-safety-inputs.json')

maintenance=OUT/'maintenance';maintenance.mkdir();(maintenance/'plans').mkdir()
copy(BASE/'r3/priority/StageGuest.cs',maintenance/'StageGuest.cs')
compile_(maintenance,'StageGuest',['System.Core.dll','System.Management.dll','System.Web.Extensions.dll','System.Xaml.dll','System.Drawing.dll','WPF/PresentationCore.dll','WPF/PresentationFramework.dll','WPF/WindowsBase.dll'])
copy(BUILD/'Mori.exe.config',maintenance/'StageGuest.exe.config')
stages=[dict(type='MoriDesk.InstalledVersionMaintenanceTests',method='Run',args=['check']),dict(type='MoriDesk.UpdateArtifactMaintenanceTests',method='Run',args=['root','check'])]
write(maintenance/'plans/r4-install-maintenance.json',dict(appSha256=APP,captureSettings=True,timeoutSeconds=300,stages=stages))
sources=['tests/InstalledVersionMaintenanceTests.cs','tests/UpdateArtifactMaintenanceTests.cs','tests/SuiteTests.cs','src/InstalledVersionMaintenance.cs','src/UpdateArtifactMaintenance.cs','installer/InstalledVersionCleanup.cs']
for name in sources:copy(REPO/name,maintenance/'sources'/name)
minimal=[(p,p.name) for p in sorted(BUILD.iterdir()) if p.is_file() and (p.suffix.lower()=='.dll' or p.name in ['Mori.exe','Mori.exe.config'])]
write(maintenance/'product-files.json',dict(appSha256=APP,files=[dict(path=n,bytes=p.stat().st_size,sha256=sha(p)) for p,n in minimal],scope='Root binaries only. Original embedded maintenance/artifact suites; synthetic VM fixtures only; no actual install or production startup queue.'))
maintpkg=seal(maintenance,'r4-install-maintenance',minimal)

external=OUT/'installer-safety';external.mkdir();(external/'plans').mkdir()
old=BASE/'r3/external/ExternalGuest.cs';text=read(old)
assert text.count(OLDAPP)==1 and text.count(r'C:\NookLab\Audit\apps\72F61B078B2C')==1
text=text.replace(OLDAPP,APP).replace(r'C:\NookLab\Audit\apps\72F61B078B2C',maintpkg['guest'])
(external/'ExternalGuest.cs').write_text(text,encoding='utf-8-sig')
compile_(external,'ExternalGuest',['System.Core.dll','System.Management.dll','System.Web.Extensions.dll'])
copy(external/'ExternalGuest.exe',external/'Mori.exe')
for name in ['Mori.exe.config','ExternalGuest.exe.config']:copy(BUILD/'Mori.exe.config',external/name)
copy(RELEASE/'installer-safety/InstallerSafetyTests.exe',external/'InstallerSafetyTests.exe')
copy(RELEASE/'installer-safety-inputs.json',external/'installer-safety-inputs.json')
for row in safety['sources']:copy(REPO/row['path'],external/'sources'/row['path'])
refs=['System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.IO.Compression.dll','System.Web.Extensions.dll','Microsoft.CSharp.dll']
write(external/'InstallerSafetyTests.provenance.json',dict(exeSha256=safety['executableSha256'],sourceSealSha256=sha(RELEASE/'installer-safety-inputs.json'),references=[dict(path=str(FRAMEWORK/n),sha256=sha(FRAMEWORK/n)) for n in refs],compilerSha256=sha(CSC),origin='Root compiled with tests/installer-safety/run.ps1 -CompileOnly; byte-identical executable reused; no host test execution.'))
write(external/'ExternalGuest.rebind.json',dict(originalSourceSha256=sha(old),changes=['AppHash pins R4 product','Target pins this narrow root-binary package'],target=maintpkg['guest'],targetPackageSha256=maintpkg['sha256'],sourceSha256=sha(external/'ExternalGuest.cs')))
write(external/'plans/r4-installer-safety.json',dict(label='r4-installer-safety',exe='InstallerSafetyTests.exe',sha256=safety['executableSha256'],args=[],successRegex=r'^PASS (\d+) installer safety checks;',timeoutSeconds=300))
safetypkg=seal(external,'r4-installer-safety')
assert all(sha(row['path'])==row['sha256'] for row in candidate['inputs'])
assert all(sha(REPO/row['path'])==row['sha256'] for row in safety['sources'])
handoff=dict(candidateAppSha256=APP,scope='Only R4 directly affected existing native suites; isolated synthetic VM fixtures, not installation qualification. No original test assertions changed by packaging.',vmQueueTouched=False,physicalProductOrTestsExecuted=False,sourceRecipe=dict(path=str(pathlib.Path(__file__).resolve()),sha256=sha(__file__)),packages=dict(maintenance=maintpkg,installerSafety=safetypkg),commands=[dict(package='maintenance',exe='StageGuest.exe',args='--run r4-install-maintenance',report='r4-install-maintenance.json'),dict(package='installerSafety',exe='ExternalGuest.exe',args='--run r4-installer-safety',report='r4-installer-safety.json',requires=maintpkg['guest'])],wrapperScope='Unchanged StageGuest; ExternalGuest only product/hash target rebound; both retain Job, independent roots, fail-closed, no retry, preserved fixtures.')
write(OUT/'handoff.json',handoff)
print(json.dumps(dict(path=str(OUT/'handoff.json'),sha256=sha(OUT/'handoff.json'),packages=handoff['packages']),indent=2))
