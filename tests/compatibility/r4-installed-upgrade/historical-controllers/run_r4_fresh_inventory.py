from pathlib import Path
import json,hashlib,runpy,sys
sys.stdout.reconfigure(encoding='utf-8')
b=Path(r'D:\Nook-DevTestVM\audit\20261113-worker-race-rebind\r4-healthy-upgrade')
h=json.loads((b/'handoff.json').read_text(encoding='utf-8-sig'));p=h['helper'];out=b/'inventory-before-results';out.mkdir(exist_ok=False)
origin=b.parent/'r3-upgrade-inventory/package/InstalledUpgradeInventoryGuest.cs'
source=b/'package/InstalledUpgradeInventoryGuest.cs'
assert source.read_text(encoding='utf-8-sig')==origin.read_text(encoding='utf-8-sig')
assert hashlib.sha256(source.read_bytes()).hexdigest().upper()=='FAEE92F5E6ECC2921C986F39F46924C17F3A1A3FB8E0E4A03B88A8A5DED3BCAB'
m=runpy.run_path(r'D:\Nook-DevTestVM\audit\20261113-full-regression\serial_followups.py');m['call'].__globals__['EVIDENCE']=out
m['deploy']('1360-r4-inventory-helper-deploy',Path(p['path']),p['sha256'])
report='audit-installed-inventory-before-r4.json'
r=m['call']('1361-r4-before-preservation-inventory',dict(type='run',exe='InstalledUpgradeInventoryGuest.exe',args=['--read-installed-inventory','before-r4'],reports=[report],timeout=180),240)
d=m['detail'](r,report)
assert r['status']=='passed' and d['installationModified'] is False and d['running']==[]
assert d['committedState']['sha256'].upper()=='97B122310B1033BE7EBDA63730F929C256D8D0972CBC3497C7A7D6539CCA50D6'
print(json.dumps(dict(completedUtc=d['completedUtc'],sha256=hashlib.sha256((out/report).read_bytes()).hexdigest().upper(),committedState=d['committedState'],configuredPaths=d['configuredPaths'],running=d['running']),indent=2),flush=True)
