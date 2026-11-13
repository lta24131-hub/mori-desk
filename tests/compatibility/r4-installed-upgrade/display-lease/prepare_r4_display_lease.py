"""Compile bounded VM display lease with an explicit own-package release marker."""
import difflib,hashlib,json,os,pathlib,shutil,subprocess,zipfile
BASE=pathlib.Path(__file__).resolve().parent
ORIGINAL=BASE/'r3-healthy-upgrade-display-lease/lease.zip'
OUT=BASE/'r4-upgrade-display-lease'
FRAMEWORK=pathlib.Path(r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319')
def sha(p):return hashlib.sha256(pathlib.Path(p).read_bytes()).hexdigest().upper()
def write(p,v):
    with pathlib.Path(p).open('x',encoding='utf-8') as f:json.dump(v,f,ensure_ascii=False,indent=2)
assert sha(ORIGINAL)=='26AACDE0AB826765734550CAE9E10C5CA8B6EE5F4096790EE1F343B288C05B4C'
assert not OUT.exists();OUT.mkdir()
with zipfile.ZipFile(ORIGINAL) as z:oldbytes=z.read('DisplayLeaseGuest.cs')
original=oldbytes.decode('utf-8-sig');text=original.replace('\r\r\n','\n').replace('\r\n','\n')
text=text.replace('1200','3600')
before='bool acquired=false; string package=null; var clock=Stopwatch.StartNew();'
after='bool acquired=false; string package=null,releaseReason="deadline"; var clock=Stopwatch.StartNew();'
assert text.count(before)==1;text=text.replace(before,after)
before='Need(args.Length==1&&args[0]=="--bounded-upgrade-resume-lease-3600s", "Fixed 3600-second lease only");'
after='Need(args.Length==1&&(args[0]=="--bounded-upgrade-resume-lease-3600s"||args[0]=="--release-own-lease"), "Fixed bounded lease or explicit own-package release only");'
assert text.count(before)==1;text=text.replace(before,after)
before='   Need(!File.Exists(Path.Combine(package,"display-resume-lease-started.json")),"One lease per immutable package");'
after='''   string marker=Path.Combine(package,"display-resume-lease-release.request.json");Plain(marker);
   if(args[0]=="--release-own-lease") {
    Need(File.Exists(Path.Combine(package,"display-resume-lease-started.json")),"Own-package lease has started");
    Need(!File.Exists(Path.Combine(package,"display-resume-lease-released.json")),"Own-package lease has not already released");
    Save(marker,new {vmGuard=true,userGuard=true,package=package,requesterPid=Process.GetCurrentProcess().Id,releaseOwnPackageOnly=true,utc=DateTime.UtcNow.ToString("o")});
    return 0;
   }
   Need(!File.Exists(marker),"Release marker must not predate the one-shot lease");
   Need(!File.Exists(Path.Combine(package,"display-resume-lease-started.json")),"One lease per immutable package");'''
assert text.count(before)==1;text=text.replace(before,after)
before='while(clock.Elapsed.TotalSeconds<3600) Thread.Sleep(200);'
after='while(clock.Elapsed.TotalSeconds<3600) { if(File.Exists(marker)) { Plain(marker);releaseReason="explicit-own-package-request";break; } Thread.Sleep(200); }'
assert text.count(before)==1;text=text.replace(before,after)
before='new {released=released!=0,elapsedSeconds=clock.Elapsed.TotalSeconds,utc=DateTime.UtcNow.ToString("o")}'
after='new {released=released!=0,reason=releaseReason,elapsedSeconds=clock.Elapsed.TotalSeconds,utc=DateTime.UtcNow.ToString("o")}'
assert text.count(before)==1;text=text.replace(before,after)
(OUT/'DisplayLeaseGuest.cs').write_text(text,encoding='utf-8-sig')
(OUT/'source.diff').write_text(''.join(difflib.unified_diff(original.replace('\r\r\n','\n').replace('\r\n','\n').splitlines(True),text.splitlines(True),fromfile='original/DisplayLeaseGuest.cs',tofile='r4/DisplayLeaseGuest.cs')),encoding='utf-8')
refs=[FRAMEWORK/n for n in ['System.Management.dll','System.Web.Extensions.dll']]
command=[str(FRAMEWORK/'csc.exe'),'/nologo','/optimize+','/target:exe']+['/r:'+str(p) for p in refs]+['/out:'+str(OUT/'DisplayLeaseGuest.exe'),str(OUT/'DisplayLeaseGuest.cs')]
result=subprocess.run(command,capture_output=True,text=True)
(OUT/'compile.log').write_text(result.stdout+result.stderr,encoding='utf-8');assert result.returncode==0,result.stdout+result.stderr
shutil.copyfile(OUT/'DisplayLeaseGuest.exe',OUT/'Mori.exe')
write(OUT/'provenance.json',dict(originalPackageSha256=sha(ORIGINAL),originalSourceSha256=hashlib.sha256(oldbytes).hexdigest().upper(),sourceSha256=sha(OUT/'DisplayLeaseGuest.cs'),exeSha256=sha(OUT/'DisplayLeaseGuest.exe'),compilerSha256=sha(FRAMEWORK/'csc.exe'),references=[dict(path=str(p),sha256=sha(p)) for p in refs],compileCommand=command,hostExecution='Compiler only; no helper/product/VM execution',changes=['Bound 1200s -> 3600s','Same VM/user/package guards precede explicit --release-own-lease; CreateNew own-package marker only','Original loop observes marker then returns normally; finally releases same-thread lease and writes reason receipt','Line endings normalized; no process killing, power-plan change, desktop input or install action']))
zip_path=OUT/'lease.zip'
with zip_path.open('xb') as f:
    with zipfile.ZipFile(f,'w',compression=zipfile.ZIP_DEFLATED,compresslevel=1) as z:
        for n in ['DisplayLeaseGuest.cs','DisplayLeaseGuest.exe','Mori.exe','provenance.json','source.diff']:z.write(OUT/n,n)
    f.flush();os.fsync(f.fileno())
with zipfile.ZipFile(zip_path) as z:
    assert z.testzip() is None
    for n in z.namelist():assert hashlib.sha256(z.read(n)).hexdigest().upper()==sha(OUT/n)
write(OUT/'handoff.json',dict(path=str(zip_path),sha256=sha(zip_path),guest='C:\\NookLab\\Audit\\apps\\'+sha(zip_path)[:12],executable='DisplayLeaseGuest.exe',args=['--bounded-upgrade-resume-lease-3600s'],releaseArgs=['--release-own-lease'],maxSeconds=3600,startedReport='display-resume-lease-started.json',releaseRequest='display-resume-lease-release.request.json',releasedReport='display-resume-lease-released.json',globalPowerPlanChanged=False,productExecuted=False,vmQueueTouched=False,sourceSha256=sha(OUT/'DisplayLeaseGuest.cs'),exeSha256=sha(OUT/'DisplayLeaseGuest.exe')))
print((OUT/'handoff.json').read_text(encoding='utf-8'))
