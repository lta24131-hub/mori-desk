"""Compile/package only. No VM mutation or physical product execution."""
import difflib, hashlib, json, pathlib, subprocess, zipfile

BASE = pathlib.Path(__file__).resolve().parent
OUT = BASE / 'r4-healthy-upgrade'
OUT.mkdir(exist_ok=False)
PKG = OUT / 'package'
PKG.mkdir()
OLD = BASE / 'r3-upgrade-healthy-v3/package'
APP = 'A26E4E47DF3474355622B4E5C0C31F823D3F57F1A7D8DF6BFA4221765D66FB22'
SETUP = '366A3208B1BEC55594CF7B8E07F9050AC48A3895BD9DD311D5504A1005AC5772'
FIXTURE = r'C:\NookLab\U42R4'
PRESERVED = r'C:\NookLab\P1304'

def sha(path):
    with pathlib.Path(path).open('rb') as f:
        return hashlib.file_digest(f, 'sha256').hexdigest().upper()

def read(path):
    return json.loads(pathlib.Path(path).read_text(encoding='utf-8-sig'))

def write(path, data):
    pathlib.Path(path).write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding='utf-8')

def change(s, a, b, count=1):
    assert s.count(a) == count, (a, s.count(a))
    return s.replace(a, b)

sources = []
prepare = BASE / 'r4-owned-prepare-v3/package/InstalledUpgradePrepareGuest.cs'
assert sha(prepare) == '8A156067C7A891E158FDA13E9F46355CA672912458BA50D15C73A2827AB375E6'
before = prepare.read_text(encoding='utf-8-sig')
after = before.replace('97B122310B1033BE7EBDA63730F929C256D8D0972CBC3497C7A7D6539CCA50D6', APP)
after = after.replace('15BD55114642B697723731D53D3D1B98ABCBF00F9A399F500FBE26AC16650994', SETUP)
after = after.replace(r'C:\NookLab\Upgrade42To45-20261113-r3-healthy', FIXTURE)
after = change(after, 'Need(args.Length==1&&Operation=="--prepare-browser42","This reviewed helper only prepares a fresh public42 browser baseline; never install, launch-only, reseed or preserve.");', '')
after = after.replace('child.MainModule.FileName', 'NativeProcessImage(child)')
after = after.replace('CloseApp(child,', 'CloseLaunchApp(child,')
after = change(after, 'int step=0;string session=null;object evidence=null;Process child=null;var clock=Stopwatch.StartNew();', 'int step=0;string session=null;object evidence=null;Process child=null;double firstCommittedSeconds=-1;var clock=Stopwatch.StartNew();')
old_loop = 'while(!process.HasExited){bool committed=TrySession(newer?"0.1.45":"0.1.42",out session);DriveInstaller(process,newer,ref step,committed);if(clock.Elapsed.TotalSeconds>720)throw new TimeoutException("Real installer exceeded its bounded VM budget.");Thread.Sleep(200);}'
new_loop = '''while(!process.HasExited){
    bool committed=TrySession(newer?"0.1.45":"0.1.42",out session);
    if(committed&&firstCommittedSeconds<0)firstCommittedSeconds=clock.Elapsed.TotalSeconds;
    DriveInstaller(process,newer,ref step,committed);
    double elapsed=clock.Elapsed.TotalSeconds;
    // R4 uses the unchanged production 30-minute cleanup / 90-second idle policy.
    // The test must allow that real policy after commit, not kill its live Job at 720s.
    if(!newer&&elapsed>720)throw new TimeoutException("Public42 real installer exceeded its original bounded VM budget.");
    if(newer&&((firstCommittedSeconds<0&&elapsed>720)||(firstCommittedSeconds>=0&&elapsed-firstCommittedSeconds>1860)||elapsed>2580))throw new TimeoutException("R4 real installer exceeded its reviewed commit/cleanup/exit budget.");
    Thread.Sleep(200);
   }'''
after = change(after, old_loop, new_loop)
after = change(after, 'elapsedSeconds=clock.Elapsed.TotalSeconds,uiSteps=step', 'elapsedSeconds=clock.Elapsed.TotalSeconds,firstCommittedSeconds,preCommitBudgetSeconds=720,postCommitBudgetSeconds=newer?1860:0,totalBudgetSeconds=newer?2580:720,uiSteps=step')
after = change(after, 'null,780000,Timeout.Infinite', 'null,Operation=="--install45"?2640000:780000,Timeout.Infinite')
sources.append(('InstalledUpgradeDriverGuest', prepare, before, after, OLD / 'InstalledUpgradeDriverGuest.provenance.json'))

for name in ['InstalledUpgradeFixtureGuest', 'InstalledUpgradePreserveGuest', 'InstalledUpgradeHealthGuest']:
    origin = OLD / (name+'.cs')
    before = origin.read_text(encoding='utf-8-sig')
    after = before.replace('97B122310B1033BE7EBDA63730F929C256D8D0972CBC3497C7A7D6539CCA50D6', APP)
    after = after.replace(r'C:\NookLab\Upgrade42To45-20261113-r3-healthy', FIXTURE)
    if name == 'InstalledUpgradePreserveGuest':
        after = change(after, r'C:\NookLab\Audit\upgrade42to45-preserved-20261113-r3-healthy', PRESERVED)
        after = change(after, r'audit-installed-inventory-before-healthy42\.json', r'audit-installed-inventory-before-r4\.json')
    sources.append((name, origin, before, after, OLD / (name+'.provenance.json')))

origin = BASE / 'r3-upgrade-inventory/package/InstalledUpgradeInventoryGuest.cs'
before = origin.read_text(encoding='utf-8-sig')
sources.append(('InstalledUpgradeInventoryGuest', origin, before, before, None))
compiler = pathlib.Path(r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe')
records = []
for name, origin, before, after, old_provenance in sources:
    src = PKG / (name+'.cs')
    src.write_text(after, encoding='utf-8')
    (PKG/(name+'.diff')).write_text(''.join(difflib.unified_diff(before.splitlines(True), after.splitlines(True), fromfile=str(origin), tofile=name+'.cs')), encoding='utf-8')
    if old_provenance:
        old = read(old_provenance)
        refs = [pathlib.Path(item['path']) for item in old['inputs'][1:]]
        for p, item in zip(refs, old['inputs'][1:]):
            assert sha(p) == item['sha256']
    else:
        refs = [compiler.parent / n for n in ['System.Core.dll', 'System.Management.dll', 'System.Web.Extensions.dll']]
    exe = PKG / (name+'.exe')
    command = [str(compiler), '/nologo', '/target:exe', '/optimize+', '/platform:x64', '/codepage:65001', '/out:'+str(exe)]+['/r:'+str(p) for p in refs]+[str(src)]
    result = subprocess.run(command, capture_output=True, creationflags=0x08000000)
    (OUT/(name+'.compile.log')).write_bytes(result.stdout+result.stderr)
    assert result.returncode == 0, (result.stdout+result.stderr).decode('utf-8', errors='replace')
    provenance = dict(origin=str(origin), originSha256=sha(origin), sourceSha256=sha(src), outputSha256=sha(exe), command=command, inputs=[dict(path=str(p), sha256=sha(p)) for p in [src]+refs], hostExecution='Compiler/package only')
    write(PKG/(name+'.provenance.json'), provenance)
    records.append(dict(name=name, sourceSha256=sha(src), exeSha256=sha(exe)))
(PKG/'Mori.exe').write_bytes((PKG/'InstalledUpgradeDriverGuest.exe').read_bytes())
write(PKG/'scope.json', dict(candidateAppSha256=APP, candidateSetupSha256=SETUP,fixtureRoot=FIXTURE,preservedRoot=PRESERVED,noDeletionByHelpers=True,preserveOld1304Failure=True,hostProductExecution=False,preCommitBudgetSeconds=720,postCommitBudgetSeconds=1860,totalBudgetSeconds=2580,watchdogSeconds=2640,brokerSeconds=2700,budgetReason='Original R3 failed cleanup before completion; R4 retains production30min/90s. Test now permits actual product policy without changing old failure/assertions.'))
archive = OUT/'helpers.zip'
with zipfile.ZipFile(archive, 'x', zipfile.ZIP_DEFLATED, compresslevel=1) as z:
    for p in sorted(PKG.iterdir()):
        z.write(p, p.name)
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None
helper = dict(path=str(archive),sha256=sha(archive),guest='C:\\NookLab\\Audit\\apps\\'+sha(archive)[:12],sources=records)
setup = pathlib.Path(r'D:\Nook-DevTestVM\audit\20261026-file-previews-source\releases\20261113-233447\Nook-Setup.exe')
assert sha(setup) == SETUP and setup.stat().st_size == 350510080
setup_zip = OUT/'setup-r4.zip'
with zipfile.ZipFile(setup_zip, 'x', zipfile.ZIP_STORED) as z:
    z.write(setup, 'Nook-Setup.exe')
with zipfile.ZipFile(setup_zip) as z:
    assert z.testzip() is None and hashlib.sha256(z.read('Nook-Setup.exe')).hexdigest().upper()==SETUP
setup_package = dict(path=str(setup_zip),sha256=sha(setup_zip),guest='C:\\NookLab\\Audit\\apps\\'+sha(setup_zip)[:12],entrySha256=SETUP)
handoff = dict(candidateAppSha256=APP,candidateSetupSha256=SETUP,helper=helper,setup=setup_package,fixtureRoot=FIXTURE,preservedRoot=PRESERVED,hostProductExecution=False,runAuthorized=False)
write(OUT/'handoff.json', handoff)
print(json.dumps(handoff,indent=2))
