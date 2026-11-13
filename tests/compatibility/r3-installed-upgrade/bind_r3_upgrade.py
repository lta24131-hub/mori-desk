"""Bind a returned fresh read-only VM inventory to reviewed R3 upgrade plan. No VM operations."""
import argparse,hashlib,json,pathlib,re
p=argparse.ArgumentParser();p.add_argument('--inventory',required=True);p.add_argument('--guest-path',required=True);p.add_argument('--sha256',required=True);a=p.parse_args()
root=pathlib.Path(__file__).resolve().parent/'r3-upgrade-journey'
raw=pathlib.Path(a.inventory).read_bytes();digest=hashlib.sha256(raw).hexdigest().upper();assert digest==a.sha256.upper()
assert re.fullmatch(r'C:\\NookLab\\Audit\\apps\\[A-Fa-f0-9]{12}\\audit-installed-inventory-before42\.json',a.guest_path)
inventory=json.loads(raw.decode('utf-8-sig'));assert inventory['scope']=='guest-installed-read-only-inventory'
assert inventory['programRoot']==r'C:\Users\NookTester\AppData\Local\Programs\MoriDesk'
assert inventory['dataRoot']==r'C:\Users\NookTester\AppData\Local\MoriDesk'
assert inventory['running']==[]
plan=json.loads((root/'journey-plan-unbound.json').read_text(encoding='utf-8-sig'))
assert plan['inventory'] is None and not plan['runAuthorized']
plan['inventory']=dict(file=a.guest_path,sha256=digest,hostEvidence=str(pathlib.Path(a.inventory).resolve()),running=[],configuredPaths=inventory['configuredPaths'],committedState=inventory['committedState'])
plan['inventoryState']='Fresh returned inventory hash-bound; parent approval required before preservation. Preserve helper revalidates exact settings/active record/shortcut state immediately before moving.'
plan['stages'][0]['args']=['--preserve-reviewed-vm-state',a.guest_path,digest]
plan['runAuthorized']=False
target=root/'journey-plan-bound.json';assert not target.exists()
target.write_text(json.dumps(plan,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(dict(path=str(target),sha256=hashlib.sha256(target.read_bytes()).hexdigest().upper(),inventorySha256=digest,runAuthorized=False),indent=2))
