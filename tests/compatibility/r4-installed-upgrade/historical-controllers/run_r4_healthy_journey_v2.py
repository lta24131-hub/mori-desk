"""VM-only fresh public42 to frozen R4. Stop on first failure; no retries."""
import datetime, hashlib, json, pathlib, runpy, sys, time

sys.stdout.reconfigure(encoding='utf-8')
BASE=pathlib.Path(__file__).resolve().parent
ROOT=BASE/'r4-healthy-upgrade'
assert len(sys.argv)==4 and sys.argv[1] in ('baseline','upgrade')
phase=sys.argv[1]

def sha(path):
    with pathlib.Path(path).open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()

def read(path):return json.loads(pathlib.Path(path).read_text(encoding='utf-8-sig'))

def bound(path,digest):
    assert sha(path)==digest,str(path)
    return read(path)

plan=bound(ROOT/'journey-plan.json',sys.argv[2])
gate=bound(ROOT/'pre-execution-resume-review.json',sys.argv[3])
assert gate['approved'] is True and gate['planSha256']==sys.argv[2] and gate['controllerSha256']==sha(__file__)
assert plan['hostProductExecution'] is False and plan['preservedRoot']==r'C:\NookLab\P1304' and plan['fixtureRoot']==r'C:\NookLab\U42R4'
assert plan['candidateAppSha256']=='A26E4E47DF3474355622B4E5C0C31F823D3F57F1A7D8DF6BFA4221765D66FB22'
assert plan['candidateSetupSha256']=='366A3208B1BEC55594CF7B8E07F9050AC48A3895BD9DD311D5504A1005AC5772'
helper,setup=plan['helper'],plan['setup']
lease=plan['lease'] if phase=='baseline' else plan['upgradeLease']
for package in (helper,setup,lease):assert sha(package['path'])==package['sha256']
assert helper['sha256']=='0D9E59A64489FC4B4FD05AECD89F1765E60BC3B9CD58E3585584FF745FE39E55'
assert lease['sha256']==('F863774650CF53A73BAE2797AD9B6D1FE2681980C7C6B903A92C9214E645DA10' if phase=='baseline' else '982ECD28E95818FAF5F761DE86E03B8D3805F135A2C693B59B11E093E10D96B2')
inventory=bound(plan['inventory']['hostEvidence'],plan['inventory']['sha256'])
assert inventory['running']==[] and inventory['installationModified'] is False
assert inventory['committedState']['sha256'].upper()=='97B122310B1033BE7EBDA63730F929C256D8D0972CBC3497C7A7D6539CCA50D6'
out=ROOT/'results-v2';out.mkdir(exist_ok=True)
progress=out/(phase+'-progress.json')
assert not progress.exists(),'Never replay an original completed or failed phase.'
m=runpy.run_path(r'D:\Nook-DevTestVM\audit\20261113-full-regression\serial_followups.py')
m['call'].__globals__['EVIDENCE']=out
items=[];lease_started=False;complete=False

def state(task):
    path=BASE.parent/'20261113-full-regression/operator-state.json'
    data=read(path)
    data.update(updated=datetime.datetime.now(datetime.timezone.utc).isoformat(),activeTask=task,controllerSession=None,currentGuestPackage=m['active_package'](),pending='Single R4 journey owner; no concurrent VM queue user; original failures retained. Physical product execution forbidden.')
    path.write_text(json.dumps(data,indent=2),encoding='utf-8')

def save(done=False):
    progress.write_text(json.dumps(dict(phase=phase,complete=done,planSha256=sys.argv[2],controllerSha256=sha(__file__),hostProductExecution=False,original1304RetainedFailed=True,results=items),indent=2),encoding='utf-8')

def deploy(ident,p):
    state(ident)
    m['deploy'](ident,pathlib.Path(p['path']),p['sha256'])
    assert m['active_package']()==p['guest'].upper()

def stage(ident,exe,args,report,timeout,kind):
    assert m['active_package']()==helper['guest'].upper()
    state(ident)
    r=m['call'](ident,dict(type='run',exe=exe,args=args,reports=[report],timeout=timeout),timeout+60)
    d=m['detail'](r,report)
    passed=r['status']=='passed' and (d.get('passed') is True or (kind=='inventory' and d.get('installationModified') is False and d.get('running')==[]))
    item=dict(id=ident,status=r['status'],passed=passed,report=report,sha256=sha(out/report),elapsedSeconds=r.get('elapsedSeconds'),error=d.get('error'))
    items.append(item);save();print(json.dumps(item,ensure_ascii=False),flush=True)
    assert passed,'STOP: retained failed stage; no automatic retry.'
    if kind in ('install','launch','prepare'):
        assert d['jobActiveIncludesHelper'] is True and d['jobActive']==1 and d['forcedCleanupAtProcessExit'] is False
    if kind=='preserve':assert d['mayContinueInstallation'] is True and d['noDeletion'] is True
    if kind=='health':
        snap=d['productionSnapshot'];assert snap['complete'] is True and snap['entrypointsSwitched'] is True and snap['reason']=='ready' and snap['runningCount']==0
    if kind=='install' and args[0]=='--install45':
        assert d['result']['setupSha256']==plan['candidateSetupSha256'] and d['result']['oldFolderRemoved'] is True
        cleanup=d['result']['transaction']['evidence']['installer-cleanup.json']
        assert cleanup['finished'] is True and cleanup['state']=='complete'
    return d

def ready(ident,package,name):
    deadline=time.monotonic()+35
    for i in range(30):
        r=m['call'](ident+'-'+str(i),dict(type='read',path='apps\\'+package+'\\'+name),30)
        text=r.get('text','')
        assert r['status']=='passed'
        if text and not text.startswith('missing:'):return text
        if time.monotonic()>=deadline:break
        time.sleep(.25)
    raise TimeoutError('Bounded asynchronous helper readiness exceeded35s; retain receipt.')

def release(prefix):
    deploy(prefix+'-lease-reactivate',lease)
    r=m['call'](prefix+'-lease-release',dict(type='run',exe=lease['executable'],args=lease['releaseArgs'],reports=[lease['releaseRequest']],timeout=30),60)
    assert r['status']=='passed'
    (out/(phase+'-'+lease['releaseRequest'])).write_text(r['reports'][lease['releaseRequest']],encoding='utf-8')
    for i in range(20):
        r=m['call'](prefix+'-lease-receipt-'+str(i),dict(type='read',path='apps\\'+lease['sha256'][:12]+'\\'+lease['releasedReport']),30)
        text=r.get('text','')
        if r['status']=='passed' and text and not text.startswith('missing:'):
            d=json.loads(text);assert d['released'] is True
            (out/(phase+'-'+lease['releasedReport'])).write_text(text,encoding='utf-8');return
        time.sleep(.25)
    raise AssertionError('No normal lease release receipt; preserve and inspect.')

save()
try:
    if phase=='baseline':
        # Preserve the first readiness race as preparation evidence. No product stage ran.
        prior=read(ROOT/'results/baseline-progress.json')
        assert prior['complete'] is False and prior['results']==[]
        start=read(ROOT/'results/1372-r4-display-lease-start.json')
        assert start['status']=='passed' and start['pid']==1156
        raw=ready('1396-r4-baseline-retained-lease-ready',lease['sha256'][:12],lease['startedReport'])
        d=json.loads(raw);assert d['vmGuard'] and d['userGuard'] and d['pid']==1156 and d['maxSeconds']==3600 and d['globalPowerPlanChanged'] is False and d['productExecuted'] is False
        age=(datetime.datetime.now(datetime.timezone.utc)-datetime.datetime.fromisoformat(d['utc'].replace('Z','+00:00'))).total_seconds()
        assert 0<=age<1200,'Retained baseline lease must cover all baseline stage budgets.'
        lease_started=True
        (out/(phase+'-'+lease['startedReport'])).write_text(raw,encoding='utf-8')
        deploy('1374-r4-baseline-helpers',helper)
        stage('1375-r4-preserve1304','InstalledUpgradePreserveGuest.exe',['--preserve-reviewed-vm-state',plan['inventory']['guestEvidence'],plan['inventory']['sha256']],'preserve-result.json',240,'preserve')
        stage('1376-r4-real-install42','InstalledUpgradeDriverGuest.exe',['--install42',plan['public42Setup']],'install42-result.json',840,'install')
        stage('1377-r4-public42-browser-ui','InstalledUpgradeDriverGuest.exe',['--prepare-browser42'],'prepare-browser42-result.json',100,'prepare')
        stage('1378-r4-public42-health','InstalledUpgradeHealthGuest.exe',['--validate-public42-browser'],'healthy42-preflight-result.json',150,'health')
        stage('1379-r4-healthy42-inventory','InstalledUpgradeInventoryGuest.exe',['--read-installed-inventory','healthy42-r4'],'audit-installed-inventory-healthy42-r4.json',180,'inventory')
        complete=True;save(True);state('R4 healthy public42 prepared; no app UI remains; await independent health audit before upgrade')
        print('R4_HEALTHY42_READY_FOR_INDEPENDENT_REVIEW',flush=True)
    else:
        previous=read(out/'baseline-progress.json');assert previous['complete'] is True and previous['planSha256']==sys.argv[2] and len(previous['results'])==5 and all(i['passed'] is True for i in previous['results'])
        healthy=read(ROOT/'healthy42-reviewed-gate.json')
        assert healthy['approved'] is True and healthy['planSha256']==sys.argv[2]
        for name in ('baseline-progress.json','healthy42-preflight-result.json','audit-installed-inventory-healthy42-r4.json'):
            assert sha(out/name)==healthy['evidence'][name]
        assert read(out/('baseline-'+lease['releasedReport']))['released'] is True
        deploy('1380a-r4-upgrade-own-lease-deploy',lease)
        r=m['call']('1380b-r4-upgrade-own-lease-start',dict(type='launch',exe=lease['executable'],args=lease['args']),60)
        assert r['status']=='passed';lease_started=True
        raw=ready('1380c-r4-upgrade-own-lease-started',lease['sha256'][:12],lease['startedReport'])
        d=json.loads(raw);assert d['vmGuard'] and d['userGuard'] and d['maxSeconds']==3600 and d['globalPowerPlanChanged'] is False and d['productExecuted'] is False
        (out/(phase+'-'+lease['startedReport'])).write_text(raw,encoding='utf-8')
        deploy('1380-r4-upgrade-helpers',helper)
        stage('1381-r4-seed42','InstalledUpgradeFixtureGuest.exe',['--seed42'],'fixtureseed42-result.json',120,'fixture')
        stage('1382-r4-launch42','InstalledUpgradeDriverGuest.exe',['--launch42'],'launch42-result.json',90,'launch')
        stage('1383-r4-snapshot42','InstalledUpgradeFixtureGuest.exe',['--snapshot42'],'fixturesnapshot42-result.json',120,'fixture')
        stage('1384-r4-real-upgrade','InstalledUpgradeDriverGuest.exe',['--install45',setup['guest']+'\\Nook-Setup.exe'],'install45-result.json',2700,'install')
        stage('1385-r4-launch45','InstalledUpgradeDriverGuest.exe',['--launch45'],'launch45-result.json',90,'launch')
        stage('1386-r4-verify45','InstalledUpgradeFixtureGuest.exe',['--verify45'],'fixtureverify45-result.json',120,'fixture')
        stage('1387-r4-final-inventory','InstalledUpgradeInventoryGuest.exe',['--read-installed-inventory','final-r4'],'audit-installed-inventory-final-r4.json',180,'inventory')
        complete=True;save(True);print('R4_REAL_UPGRADE_AND_FIXTURE_VERIFICATION_COMPLETE',flush=True)
finally:
    if lease_started:
        release('1397-r4-'+phase)
        state('R4 '+phase+' ended; normal display-lease release verified; no active controller')
