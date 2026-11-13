"""Package-only addition of the existing deployment cache sentinel."""
import hashlib,json,pathlib,shutil,zipfile
BASE=pathlib.Path(__file__).resolve().parent
OLD=BASE/'r3-native-launch-v2';OUT=BASE/'r3-native-launch-v3';assert not OUT.exists();OUT.mkdir();PKG=OUT/'package';PKG.mkdir()
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
h=json.loads((OLD/'handoff.json').read_text(encoding='utf-8'))
assert h['sha256']=='D777BCF0556DCA212F292277B02FB26A0023624C19077F61CFB5D9E3588AB06A'
for p in (OLD/'package').iterdir():
    shutil.copyfile(p,PKG/p.name);assert sha(p)==sha(PKG/p.name)
shutil.copyfile(PKG/h['executable'],PKG/'Mori.exe');assert sha(PKG/'Mori.exe')==h['exeSha256']
zip_path=OUT/'native-launch.zip'
with zipfile.ZipFile(zip_path,'x',zipfile.ZIP_DEFLATED,compresslevel=1) as z:
    for p in sorted(PKG.iterdir()):z.write(p,p.name)
with zipfile.ZipFile(zip_path) as z:assert z.testzip() is None
h.update(path=str(zip_path),sha256=sha(zip_path),guest='C:\\NookLab\\Audit\\apps\\'+sha(zip_path)[:12],deploymentSentinel='Mori.exe',sentinelSha256=h['exeSha256'],repackOnly=True,originalPackageSha256='D777BCF0556DCA212F292277B02FB26A0023624C19077F61CFB5D9E3588AB06A',sourceAndOriginalExeBytesUnchanged=True,hostExecution='Package only; no compiler/product/helper execution')
(OUT/'handoff.json').write_text(json.dumps(h,indent=2)+'\n',encoding='utf-8');print(json.dumps(h,indent=2))
