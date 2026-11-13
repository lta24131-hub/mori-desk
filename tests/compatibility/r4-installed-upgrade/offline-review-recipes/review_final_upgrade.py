"""Offline verification of completed R4 journey; no VM or product execution."""
import hashlib, json, pathlib

BASE = pathlib.Path(__file__).resolve().parent.parent
ROOT = BASE / 'r4-healthy-upgrade'
OUT = ROOT / 'results-v2'
APP42 = '0023F5CF9C7B6021ED8071E9936EB20282C1FBBE467716B33146AA52110662EB'
APP45 = 'A26E4E47DF3474355622B4E5C0C31F823D3F57F1A7D8DF6BFA4221765D66FB22'
SETUP45 = '366A3208B1BEC55594CF7B8E07F9050AC48A3895BD9DD311D5504A1005AC5772'
LAUNCHER = '58F1D00650E3F7DD9EB93C7D382894BDDDD6B339053152D8E1979A5DBC2E0895'

def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest().upper()

baseline = read(OUT / 'baseline-independent-review.json')
assert baseline['passed'] and baseline['healthyBaselineScopeAccepted']
gate = read(ROOT / 'healthy42-reviewed-gate.json')
assert gate['approved'] and gate['baselineReviewSha256'] == sha(OUT / 'baseline-independent-review.json')
assert gate['planSha256'] == sha(ROOT / 'journey-plan.json')
for name, digest in gate['evidence'].items(): assert sha(OUT / name) == digest
progress = read(OUT / 'upgrade-progress.json')
assert progress['complete'] and progress['hostProductExecution'] is False
assert progress['planSha256'] == sha(ROOT / 'journey-plan.json')
assert progress['controllerSha256'] == sha(BASE / 'run_r4_healthy_journey_v2.py')
assert [x['id'] for x in progress['results']] == [
    '1381-r4-seed42', '1382-r4-launch42', '1383-r4-snapshot42',
    '1384-r4-real-upgrade', '1385-r4-launch45', '1386-r4-verify45',
    '1387-r4-final-inventory']
reports = {}
rows = []
for row in progress['results']:
    path = OUT / row['report']
    outer_path = OUT / (row['id'] + '.json')
    outer = read(outer_path)
    assert outer['type'] == 'run' and outer['status'] == 'passed' and outer['exitCode'] == 0
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

seed = reports['fixtureseed42-result.json']
snapshot = reports['fixturesnapshot42-result.json']
verify = reports['fixtureverify45-result.json']
for value, app in [(seed, APP42), (snapshot, APP42), (verify, APP45)]:
    assert value['actualInstalledModel'] and value['appSha256'] == app
    assert value['fixture'] == r'C:\NookLab\U42R4'
assert 'All fixture user fields, settings, membership, scene, pin state and original/resource hashes survive public 42 to final 45.' in verify['checks']
launch42 = reports['launch42-result.json']
launch45 = reports['launch45-result.json']
for value, app in [(launch42, APP42), (launch45, APP45)]:
    assert value['result']['appSha256'] == app and value['result']['normalClose']
    assert value['result']['launcherSha256'] == LAUNCHER
assert launch42['result']['launcher'] == launch45['result']['launcher']

install = reports['install45-result.json']
r = install['result']
tx = r['transaction']
evidence = tx['evidence']
assert install['operation'] == '--install45' and install['installationStage']
assert r['actualInstallerEntry'] == 'no arguments' and r['setupSha256'] == SETUP45
assert r['uiSteps'] == 4 and r['oldFolderRemoved'] and r['launcherSha256'] == LAUNCHER
assert tx['active']['version'] == '0.1.45.0' and tx['active']['sha256'].upper() == APP45
assert evidence['result.json']['status'] == 'installed'
assert evidence['candidate.json']['sha256'].upper() == APP45
assert evidence['startup-ready.json'] == tx['receipt']
assert tx['receipt']['expectedVersion'] == '0.1.45'
assert tx['receipt']['processId'] == tx['childId'] and tx['childNativeWindow'] != 0
cleanup = evidence['installer-cleanup.json']
assert cleanup['finished'] and cleanup['state'] == 'complete'
assert [x['action'] for x in install['uiActions']] == [
    'Keep existing default install location', 'Keep inherited installed components',
    'Start actual 0.1.45 replacement installation', 'Finish completed install and cleanup window']
assert 'Verified installed app accepts normal native close.' in install['checks']

inventory = reports['audit-installed-inventory-final-r4.json']
assert inventory['installationModified'] is False and inventory['running'] == []
assert inventory['committedState']['sha256'].upper() == APP45
assert inventory['launcherOwnership']['validForExecutable']
assert inventory['launcherOwnership']['executableSha256'] == LAUNCHER
assert not any(v['directory'].startswith('0.1.42-') for v in inventory['versions'])
assert inventory['configuredPaths']['LibraryPath'] == r'C:\NookLab\U42R4\Library'
assert inventory['configuredPaths']['FileConversionOutputPath'] == r'C:\NookLab\U42R4\Converted'
assert inventory['library']['counts']['assets'] == 1
assert inventory['library']['counts']['activeAssets'] == 1
assert inventory['canvases']['count'] == 1
assert all(v['target'].endswith('\\Launcher\\Mori.exe') for v in inventory['shortcuts'] if v['exists'])
assert all(v['matchesKnownManifest'] for row in inventory['registrations']['entries']
           if row['hive'] == 'CurrentUser' for v in row['native'])
release = read(OUT / 'upgrade-display-resume-lease-released.json')
assert release['released'] and release['reason'] == 'explicit-own-package-request'
original = read(BASE / 'r3-upgrade-native-image-continuation/results/install45-result.json')
assert original['passed'] is False

result = dict(schemaVersion=1, candidateAppSha256=APP45, candidateSetupSha256=SETUP45,
    scope='Actual public42-to-frozen-R4 installation in dedicated VM using generated fixtures; no physical desktop, taskbar click or real Chrome collection acceptance.',
    stages=rows, baselineReviewSha256=sha(OUT / 'baseline-independent-review.json'),
    healthyGateSha256=sha(ROOT / 'healthy42-reviewed-gate.json'),
    upgradeProgressSha256=sha(OUT / 'upgrade-progress.json'),
    allRawBrokerAndExtractedBytesMatch=True, actualNoArgumentInstaller=True,
    actualWizardSteps=4, startupReceiptAndNativeWindowVerified=True,
    old42DirectoryRemoved=True, productCleanupComplete=True,
    sameFixedLauncherSha256=LAUNCHER, normalExitAndOwnedJobDrainVerified=True,
    generatedAssetCount=1, generatedCanvasImages=1, generatedPinCount=1,
    pinActive=False, nondefaultStoragePathsPreserved=True,
    settingsMembershipCanvasPinAndOriginalResourceBytesPreserved=True,
    finalInventoryNoProcesses=True, displayLeasesReleasedNormally=True,
    original1304RetainedFailed=True, actualUpgradeScopePassed=True,
    installationElapsedSeconds=r['elapsedSeconds'],
    installationFirstCommittedSeconds=r['firstCommittedSeconds'],
    realChromeCollectionAccepted=False, hostProductExecuted=False)
target = OUT / 'final-journey-independent-review.json'
target.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
print(json.dumps(dict(reviewSha256=sha(target), stages=len(rows), passed=True)))
