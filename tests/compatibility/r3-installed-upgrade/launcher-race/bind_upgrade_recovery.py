"""Bind returned read-only state to one reviewed VM recovery; never access VM."""
import argparse,hashlib,json,pathlib,re
ROOT=pathlib.Path(__file__).resolve().parent/'r3-upgrade-launcher-race'
parser=argparse.ArgumentParser();parser.add_argument('--inventory',required=True);parser.add_argument('--guest-path',required=True);parser.add_argument('--sha256',required=True);args=parser.parse_args()
def sha(p):return hashlib.sha256(pathlib.Path(p).read_bytes()).hexdigest().upper()
path=pathlib.Path(args.inventory).resolve();assert sha(path)==args.sha256.upper()
assert re.fullmatch(r'C:\\NookLab\\Audit\\apps\\[a-fA-F0-9]{12}\\audit-installed-inventory-[A-Za-z0-9-]+\.json',args.guest_path)
v=json.loads(path.read_text(encoding='utf-8-sig'));assert v['scope']=='guest-installed-read-only-inventory'
assert v['programRoot']==r'C:\Users\NookTester\AppData\Local\Programs\MoriDesk' and v['dataRoot']==r'C:\Users\NookTester\AppData\Local\MoriDesk'
assert v['running']==[] and not v['installationModified'] and not v['physicalDesktopTouched']
active=v['committedState'];assert active['version']=='0.1.42.0' and active['sha256'].upper()=='0023F5CF9C7B6021ED8071E9936EB20282C1FBBE467716B33146AA52110662EB'
assert v['configuredPaths']==dict(LibraryPath=r'C:\NookLab\Upgrade42To45-20261113-r3\Library',FileConversionOutputPath=r'C:\NookLab\Upgrade42To45-20261113-r3\Converted')
assert v['launcherOwnership']['validForExecutable']
plan=json.loads((ROOT/'recovery-plan-unbound.json').read_text(encoding='utf-8'));assert plan['currentState'] is None and not plan['runAuthorized']
for r in plan['preservedSuccessfulStages']:assert sha(r['report'])==r['sha256'] and json.loads(pathlib.Path(r['report']).read_text(encoding='utf-8'))['passed']
assert sha(plan['originalFailure']['report'])==plan['originalFailure']['sha256']
assert sha(plan['helper']['path'])==plan['helper']['sha256']
plan['currentState']=dict(file=args.guest_path,hostEvidence=str(path),sha256=sha(path),running=v['running'],committedState=active,configuredPaths=v['configuredPaths'],launcherOwnership=v['launcherOwnership'],completedUtc=v['completedUtc'])
plan['currentStateRequired']=False;plan['runAuthorized']=False
plan['preflightPolicy']='Root and independent reviewer approve this bound state. Sole VM owner runs only the remaining stages in order. The unchanged NoProduct and exact Active guards recheck before launch/install; any failure stops without retry or reseed.'
out=ROOT/'recovery-plan-bound.json';assert not out.exists();out.write_text(json.dumps(plan,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(dict(path=str(out),sha256=sha(out),inventorySha256=sha(path),runAuthorized=False),indent=2))
