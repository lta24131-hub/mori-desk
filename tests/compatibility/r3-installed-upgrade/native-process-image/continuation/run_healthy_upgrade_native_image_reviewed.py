"""Reviewed VM-only continuation after retained launch42 harness failure 1262."""
import hashlib, json, pathlib, runpy, sys, zipfile

sys.stdout.reconfigure(encoding='utf-8')
assert sys.argv[1:] == ['--reviewed-native-image-continuation']
BASE = pathlib.Path(__file__).resolve().parent
ROOT = BASE / 'r3-upgrade-native-image-continuation'
ORIGINAL = BASE / 'r3-upgrade-healthy-v3'
PLAN_HASH = '45267B338D5AB3361473F9317355670A7D738822E443F79A866602002D3E3EEB'
R3_APP = '97B122310B1033BE7EBDA63730F929C256D8D0972CBC3497C7A7D6539CCA50D6'
R3_SETUP = '15BD55114642B697723731D53D3D1B98ABCBF00F9A399F500FBE26AC16650994'
OLD_APP = '0023F5CF9C7B6021ED8071E9936EB20282C1FBBE467716B33146AA52110662EB'
HELPER = '95618CD835944970114766C2735E37C52074FA0C37792EBC655E8E708A11B0FE'

def sha(path):
    digest = hashlib.sha256()
    with pathlib.Path(path).open('rb') as stream:
        for data in iter(lambda: stream.read(1024*1024), b''):
            digest.update(data)
    return digest.hexdigest().upper()

def read(path):
    return json.loads(pathlib.Path(path).read_text(encoding='utf-8-sig'))

def bound(relative, digest):
    path = BASE / relative
    assert sha(path) == digest, str(relative)
    return read(path)

plan = bound('r3-upgrade-healthy-v3/journey-plan-bound.json', PLAN_HASH)
assert plan['runAuthorized'] is True and plan['scope'] == 'dedicated-NookTester-VM-only'
assert plan['hostAppExecution'] is False and plan['productFrozen'] is True
assert plan['candidateAppSha256'] == R3_APP and plan['candidateSetupSha256'] == R3_SETUP
assert len(plan['stages']) == 10
for package in plan['packages']:
    assert sha(package['path']) == package['sha256']
original = plan['packages'][2]
assert original['sha256'] == HELPER and original['guest'] == 'C:\\NookLab\\Audit\\apps\\' + HELPER[:12]
assert all(stage['guestPackage'] == original['guest'] for stage in plan['stages'])
assert plan['packages'][1]['entrySha256'] == R3_SETUP
assert sha(BASE/'run_healthy_upgrade_after_owned_prepare.py') == '74FEE5072E220EC56901019FF2A362DD4B8CA4E804DB87CBB9849657E2F9FCA7'
prior_gate = bound('r3-upgrade-healthy-v3/healthy42-reviewed-gate.json', '6E9FB71441189D26065B8BCC918A3C1488695AD126BE257CAEB9DB2B1B8D1FEE')
assert prior_gate['approved'] is True and prior_gate['planSha256'] == PLAN_HASH

# Every original failure remains immutable. No repeated seed or browser preparation.
recovery = bound('r3-preserve1251-recovery/results/preserve-recovery-result.json', '4C926D2F12BED310D40773D15B584D45BCF6E8B880BB576E6460D59A61318B7C')
assert recovery['passed'] is True and recovery['mayContinueInstallation'] is True
assert recovery['noDeletion'] is True and recovery['registryChanged'] is False
installed = bound('r3-upgrade-healthy-v3/results/install42-result.json', '83ABE38775B19128B07F92101C70F2E9DDCF601596B72C234B126CF6FEBD4D47')
assert installed['passed'] is True and installed['jobActive'] == 1 and installed['forcedCleanupAtProcessExit'] is False
prepare1 = bound('r3-upgrade-healthy-v3/results/prepare-browser42-result.json', 'DA04448ACBB26116361F912046E3C5335ACEAD20B2B079F9A45BDE681BE1D9CC')
prepare2 = bound('r3-prepare-owned-dialog/results/prepare-browser42-result.json', '072EE496759AD1399A01D34591C12B8E7AAA15509E0C66EA66A07F7404F71083')
assert prepare1['passed'] is False and prepare2['passed'] is False
health = bound('r3-prepare-owned-dialog/results/healthy42-preflight-result.json', '465BD84876BB7EAA419CD0E8D76BA67F731CB6C25BE6D082BBFBE77B7239A0AD')
snap = health['productionSnapshot']
assert health['passed'] is True and snap['complete'] is True and snap['entrypointsSwitched'] is True
assert snap['reason'] == 'ready' and snap['runningCount'] == 0
inventory = bound('r3-healthy42-independent-inventory/results/audit-installed-inventory-healthy42-preflight.json', '1DD0822CD7EE7F2D49995B8C1A307C7076424BBB880DFEF78B7D9D93EE2BACFD')
assert inventory['running'] == [] and inventory['installationModified'] is False
review = bound('r3-healthy42-independent-inventory/results/independent-review.json', '3A95C4F51509E53450450D2D78B3F4C82D6E85947400F5752B3AD57E76E10F4E')
assert review['healthyBaselineScopeAccepted'] is True and review['normalLaunchCloseStillRequired'] is True
seed = bound('r3-upgrade-healthy-v3/results/fixtureseed42-result.json', 'E69BE13766CCB9654B5CDDF158B95C030CCF15000D0F81B3ED5422A17A9B202E')
assert seed['passed'] is True and seed['operation'] == '--seed42' and seed['appSha256'] == OLD_APP
failure = bound('r3-upgrade-healthy-v3/results/launch42-result.json', 'E58B1BEE8CEBAB69EA0FF333D3E717CFB1E4FDBDE8F09D5136E3BCEC176EA13D')
assert failure['passed'] is False and failure['operation'] == '--launch42' and failure['uiActions'] == []
assert failure['result'] is None and failure['forcedCleanupAtProcessExit'] is True and failure['jobActive'] == 2
assert 'System.Diagnostics.ProcessModule.get_FileName()' in failure['error'] and 'System.NullReferenceException' in failure['error']
prior_progress = bound('r3-upgrade-healthy-v3/results/upgrade-owned-prepare-progress.json', '4D1040718E6359BA32D3B45D2ABE0A03863BA7B0233D77240794964ACBA28687')
assert prior_progress['complete'] is False
assert [(item['id'], item['passed']) for item in prior_progress['results']] == [('1261-r3-healthy-seed42', True), ('1262-r3-healthy-launch42', False)]
for stage in plan['stages'][6:]:
    assert not (ORIGINAL/'results'/stage['report']).exists(), 'Unexpected later original result; stop for review.'

gate = read(ROOT/'reviewed-gate.json')
assert gate['approved'] is True and gate['controllerSha256'] == sha(__file__)
assert gate['planSha256'] == PLAN_HASH and gate['original1262RetainedFailed'] is True
assert gate['repeatSeedOrPrepare'] is False and gate['originalInstallAndFixtureHelpersRequired'] is True
handoff_path = pathlib.Path(gate['launchHelperHandoff']['path'])
assert sha(handoff_path) == gate['launchHelperHandoff']['sha256']
handoff = read(handoff_path)
launch = gate['launchHelper']
assert sha(launch['path']) == launch['sha256']
assert launch['sha256'] == '7EA84EF4FBE1ABCBC896897BC06F4B4615BFC026149B8F0FDAEE32AE0578EB0E'
assert launch['guest'] == 'C:\\NookLab\\Audit\\apps\\' + launch['sha256'][:12]
assert launch['exe'] == 'InstalledUpgradeLaunchGuest.exe'
assert launch['path'] == handoff['path'] and launch['sha256'] == handoff['sha256']
assert launch['guest'] == handoff['guest'] and launch['exe'] == handoff['executable']
assert handoff['original1262RetainedFailed'] is True and handoff['existing42InstallationAndSeedReused'] is True
assert handoff['otherStagesMustUseOriginal95618'] is True
assert handoff['sourceSha256'] == '371B26653C5506338A99903D7AC76BDD50514F9DDB34AB14D960EA4E1975F705'
assert gate['allowedLaunchArgs'] == [['--launch42'], ['--launch45']]
assert handoff['allowedArgs'] == gate['allowedLaunchArgs']
with zipfile.ZipFile(launch['path']) as archive:
    assert 'Mori.exe' in archive.namelist() and launch['exe'] in archive.namelist() and archive.testzip() is None
    assert archive.read('Mori.exe') == archive.read(launch['exe'])
    assert hashlib.sha256(archive.read(launch['exe'])).hexdigest().upper() == '78635AB0695903FD4CEAC27DE0D5952C4F9BC0CACD6953C514BA0726B5A27E88'
with zipfile.ZipFile(original['path']) as archive:
    assert 'Mori.exe' in archive.namelist() and archive.testzip() is None

out = ROOT/'results'
out.mkdir(parents=True, exist_ok=True)
progress = out/'upgrade-native-image-progress.json'
assert not progress.exists(), 'Never replay a completed or failed continuation.'
m = runpy.run_path(r'D:\Nook-DevTestVM\audit\20261113-full-regression\serial_followups.py')
m['call'].__globals__['EVIDENCE'] = out
items = []

def save(complete=False):
    progress.write_text(json.dumps(dict(planSha256=PLAN_HASH, complete=complete, hostProductExecution=False,
        original1215RetainedFailed=True, original1251RetainedFailed=True, original1253RetainedFailed=True,
        original1276RetainedFailed=True, original1262RetainedFailed=True, seedRepeated=False,
        preparationRepeated=False, launchHelperSha256=launch['sha256'], results=items), indent=2), encoding='utf-8')

def activate(ident, package):
    m['deploy'](ident, pathlib.Path(package['path']), package['sha256'])
    assert m['active_package']() == package['guest'].upper()

def stage(ident, index, timeout, package):
    original_stage = plan['stages'][index]
    if index in (5, 8):
        assert package is launch and original_stage['args'] in gate['allowedLaunchArgs'] and timeout == 90
    else:
        assert package is original
    assert m['active_package']() == package['guest'].upper()
    stage_exe = launch['exe'] if index in (5, 8) else original_stage['exe']
    result = m['call'](ident, dict(type='run', exe=stage_exe, args=original_stage['args'],
        reports=[original_stage['report']], timeout=timeout), timeout+60)
    detail = m['detail'](result, original_stage['report'])
    item = dict(id=ident, originalStageIndex=index, originalReport=original_stage['report'],
        packageSha256=package['sha256'], status=result['status'], passed=detail.get('passed'), error=detail.get('error'))
    items.append(item)
    save()
    print(json.dumps(item, ensure_ascii=False), flush=True)
    assert result['status'] == 'passed' and detail.get('passed') is True, 'STOP: retain failure without automatic retry.'
    assert detail.get('hostExecuted') is False
    if index in (5, 7, 8):
        assert detail['jobActiveIncludesHelper'] is True and detail['jobActive'] == 1
        assert detail['forcedCleanupAtProcessExit'] is False
    if index in (5, 8):
        assert detail['processImageQuery'] == 'QueryFullProcessImageNameW'
        assert detail['result']['normalClose'] is True
        assert detail['result']['appSha256'] == (OLD_APP if index == 5 else R3_APP)
    elif index == 7:
        assert detail['result']['actualInstallerEntry'] == 'no arguments'
        assert detail['result']['setupSha256'] == R3_SETUP
        assert detail['result']['oldFolderRemoved'] is True
        cleanup = detail['result']['transaction']['evidence']['installer-cleanup.json']
        assert cleanup['finished'] is True and cleanup['state'] == 'complete'
    else:
        assert detail['actualInstalledModel'] is True
        assert detail['appSha256'] == (OLD_APP if index == 6 else R3_APP)
    return detail

# Only the narrow launcher image-query change is used in launch stages.
# Original helper 95618 owns snapshot, actual installation, and data verification.
# Both real installer packages are already deployed and are never redeployed.
save()
activate('1300-r3-native-image-launch-helper-deploy', launch)
stage('1301-r3-native-image-launch42', 5, 90, launch)
activate('1302-r3-native-image-original-helpers', original)
stage('1303-r3-native-image-snapshot42', 6, 120, original)
stage('1304-r3-native-image-install45', 7, 840, original)
activate('1305-r3-native-image-launch-helper-reactivate', launch)
stage('1306-r3-native-image-launch45', 8, 90, launch)
activate('1307-r3-native-image-original-helpers-verify', original)
stage('1308-r3-native-image-verify45', 9, 120, original)
save(complete=True)
print('HEALTHY_UPGRADE_NATIVE_IMAGE_CONTINUATION_COMPLETE', flush=True)
