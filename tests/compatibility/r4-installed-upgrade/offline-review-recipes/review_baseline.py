"""Offline evidence review; never queues or runs a product/helper."""
import hashlib, json, pathlib

BASE = pathlib.Path(__file__).resolve().parent.parent
ROOT = BASE / 'r4-healthy-upgrade'
OUT = ROOT / 'results-v2'
APP42 = '0023F5CF9C7B6021ED8071E9936EB20282C1FBBE467716B33146AA52110662EB'
LAUNCHER = '58F1D00650E3F7DD9EB93C7D382894BDDDD6B339053152D8E1979A5DBC2E0895'

def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest().upper()

plan = read(ROOT / 'journey-plan.json')
progress = read(OUT / 'baseline-progress.json')
assert progress['complete'] and progress['hostProductExecution'] is False
assert progress['planSha256'] == sha(ROOT / 'journey-plan.json')
assert progress['controllerSha256'] == sha(BASE / 'run_r4_healthy_journey_v2.py')
assert [r['id'] for r in progress['results']] == [
    '1375-r4-preserve1304', '1376-r4-real-install42',
    '1377-r4-public42-browser-ui', '1378-r4-public42-health',
    '1379-r4-healthy42-inventory']
rows = []
reports = {}
for row in progress['results']:
    outer_path = OUT / (row['id'] + '.json')
    outer = read(outer_path)
    path = OUT / row['report']
    assert outer['status'] == 'passed' and outer['exitCode'] == 0
    assert outer['reports'][path.name].encode('utf-8') == path.read_bytes()
    assert row['passed'] and sha(path) == row['sha256']
    inner = read(path)
    if 'passed' in inner: assert inner['passed'] and inner.get('error') is None
    if 'jobActive' in inner:
        assert inner['jobActive'] == 1 and inner['jobActiveIncludesHelper']
        assert inner['forcedCleanupAtProcessExit'] is False
        assert 'All owned runtime descendants exited; only this helper remains.' in inner['checks']
        assert 'Installed app exits normally with code zero from its retained native handle.' in inner['checks']
    reports[path.name] = inner
    rows.append(dict(taskId=row['id'], outerReportSha256=sha(outer_path),
                     reportSha256=sha(path), passed=True,
                     checks=len(inner.get('checks', [])), outerExitCode=0))

preserve = reports['preserve-result.json']
assert preserve['noDeletion'] and preserve['mayContinueInstallation']
assert preserve['requiresManualReviewAfterPartialFailure'] is False
assert preserve['destination'] == r'C:\NookLab\P1304'
assert len(preserve['moves']) == 4

install = reports['install42-result.json']
r = install['result']
assert install['installationStage'] and r['actualInstallerEntry'] == 'no arguments'
assert r['setupSha256'] == 'DABCF2BA17300A7CE5B64AD342188207C81CBACE35E14AA28E67753A1F4F666B'
assert r['transaction']['active']['sha256'].upper() == APP42
assert r['launcherSha256'] == LAUNCHER

prepare = reports['prepare-browser42-result.json']
r = prepare['result']
assert r['publicUiOnly'] and r['clickedInstallRepairOnce']
assert r['localListeningObserved'] and r['normalClose']
assert r['realChromeLoaded'] is False
assert r['settingsCloseProbe'] == 'native-handle-lifetime'
assert r['processImageQuery'] == 'QueryFullProcessImageNameW'
assert r['settingsClosedHandle'] != 0

health = reports['healthy42-preflight-result.json']
hr = health['result']
assert health['hostExecuted'] is False and health['sourceWrites'] is False
assert hr['appSha256'] == APP42 and hr['launcherSha256'] == LAUNCHER
assert hr['manifestSha256'] == r['manifestSha256']
assert hr['catalogSha256'] == r['catalogSha256']
assert hr['realChromeLoaded'] is False
assert len([x for x in hr['registrations'] if x['hive'] == 'CurrentUser']) == 4
snap = health['productionSnapshot']
assert snap == hr['productionSnapshot']
assert snap['complete'] and snap['entrypointsSwitched']
assert snap['reason'] == 'ready' and snap['runningCount'] == 0

inventory = reports['audit-installed-inventory-healthy42-r4.json']
assert inventory['installationModified'] is False and inventory['running'] == []
assert inventory['committedState']['sha256'].upper() == APP42
assert inventory['committedState']['version'] == '0.1.42.0'
assert inventory['launcherOwnership']['validForExecutable']
assert inventory['launcherOwnership']['executableSha256'] == LAUNCHER
assert inventory['browser']['nativeManifest']['sha256'] == hr['manifestSha256']
native = inventory['registrations']['nativeManifest']
assert native['name'] == 'app.mori.desk' and native['type'] == 'stdio'
assert native['path'].endswith('\\Launcher\\Mori.exe')
assert len(inventory['browser']['managedInstalls']) == 1
assert len(inventory['browser']['runtime']) == 6
assert all(x['exists'] and x['bytes'] > 0 for x in inventory['browser']['runtime'])
assert all(x['matchesKnownManifest'] for v in inventory['registrations']['entries']
           if v['hive'] == 'CurrentUser' for x in v['native'])

release = read(OUT / 'baseline-display-resume-lease-released.json')
assert release['released'] and release['reason'] == 'explicit-own-package-request'
review = dict(passed=True, stages=rows,
    allRawBrokerAndExtractedBytesMatch=True,
    actualPublic42InstallAccepted=True, actualPublic42OwnedSettingsNormalClose=True,
    actualPublic42NormalExitAndJobDrain=True,
    original1304Preserved=True, sourceAndShortcutMovesVerified=True,
    productionHealthAndIndependentInventoryAgree=True,
    actualChromeCollectionAccepted=False, healthyBaselineScopeAccepted=True,
    displayLeaseReleasedNormally=True,
    scope='Dedicated VM baseline only; generated fixtures and no physical desktop execution. Original R3 failures and first R4 lease readiness failure retained.')
review_path = OUT / 'baseline-independent-review.json'
review_path.write_text(json.dumps(review, indent=2) + '\n', encoding='utf-8')
gate = dict(approved=True, planSha256=sha(ROOT / 'journey-plan.json'),
    baselineReviewSha256=sha(review_path), evidence={
        name: sha(OUT / name) for name in (
            'baseline-progress.json', 'healthy42-preflight-result.json',
            'audit-installed-inventory-healthy42-r4.json')},
    scope='Allow reviewed R4 upgrade phase only; no acceptance of future outcomes.')
target = ROOT / 'healthy42-reviewed-gate.json'
target.write_text(json.dumps(gate, indent=2) + '\n', encoding='utf-8')
print(json.dumps(dict(reviewSha256=sha(review_path), gateSha256=sha(target))))
