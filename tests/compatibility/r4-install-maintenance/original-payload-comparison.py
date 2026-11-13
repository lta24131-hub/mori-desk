"""Read sealed inventory metadata only; no resource extraction or test execution."""
import hashlib,json,pathlib
BASE=pathlib.Path(__file__).resolve().parent
AUDIT=BASE.parent
def sha(p):return hashlib.sha256(pathlib.Path(p).read_bytes()).hexdigest().upper()
def read(p):return json.loads(pathlib.Path(p).read_text(encoding='utf-8-sig'))
oldpath=AUDIT/'20261113-release45-r3/release-payload-identity.json'
newpath=AUDIT/'20261113-release45-r4/release-payload-identity.json'
old=read(oldpath);new=read(newpath)
def compare(a,b):
    same=sorted(k for k in set(a)&set(b) if a[k]==b[k])
    changed=[dict(path=k,old=a[k],new=b[k]) for k in sorted(set(a)&set(b)) if a[k]!=b[k]]
    return dict(oldCount=len(a),newCount=len(b),unchangedCount=len(same),unchangedPaths=same,changed=changed,removed=sorted(set(a)-set(b)),added=sorted(set(b)-set(a)))
build=compare(old['fingerprints']['build'],new['build'])
payload=compare(old['fingerprints']['portable'],new['payload'])
oldsource=read(AUDIT/'20261113-release45-r3/candidate-inputs.json')
newsource=read(AUDIT/'20261113-release45-r4/candidate-inputs.json')
source=compare({r['path']:r['sha256'] for r in oldsource['inputs']},{r['path']:r['sha256'] for r in newsource['inputs']})
expected={'installer/InstalledVersionCleanup.cs','src/InstalledVersionMaintenance.cs'}
reporoot=AUDIT/'20261026-file-previews-source'
source_changed={pathlib.Path(r['path']).relative_to(reporoot).as_posix() for r in source['changed']}
assert source_changed==expected and not source['added'] and not source['removed']
assert {r['path'] for r in build['changed']}=={'Mori.exe','compile.rsp'}
assert not build['added'] and not build['removed']
assert payload['added']==['docs/validation/0.1.45/evidence-summary.json','docs/validation/0.1.45/source-reuse-review.json']
assert not payload['removed']
non_docs=[r['path'] for r in payload['changed'] if r['path']!='Mori.exe' and not r['path'].startswith('docs/') and r['path'] not in ['README.md','RELEASE-NOTES.md','使用说明.txt']]
assert not non_docs,non_docs
dependencies={}
for prefix in ['browser-extension/','canvas/','file-conversion/','ocr/','offline/','languages/','lexicons/']:
    names=[n for n in new['payload'] if n.startswith(prefix)]
    if names:dependencies[prefix]=dict(files=len(names),unchanged=all(old['fingerprints']['portable'].get(n)==new['payload'][n] for n in names))
rootdll=[n for n in new['payload'] if '/' not in n and n.lower().endswith('.dll')]
all_runtime=[n for n in new['payload'] if n.lower().endswith(('.exe','.dll','.js','.html','.css','.wasm','.onnx','.traineddata','.py','.json','.tiktoken','.txt')) and n!='Mori.exe' and not n.startswith('docs/') and n not in ['使用说明.txt']]
report=dict(schemaVersion=1,scope='Metadata comparison of already sealed R3/R4 build/payload inventories; no runtime equivalence claim for changed Mori.exe',inventories=dict(r3=dict(path=str(oldpath),sha256=sha(oldpath)),r4=dict(path=str(newpath),sha256=sha(newpath))),apps=dict(r3=oldsource['appSha256'],r4=newsource['appSha256']),build={k:v for k,v in build.items() if k!='unchangedPaths'},payload={k:v for k,v in payload.items() if k!='unchangedPaths'},candidateInputs={k:v for k,v in source.items() if k!='unchangedPaths'},rootDlls=dict(count=len(rootdll),allUnchanged=all(n in payload['unchangedPaths'] for n in rootdll)),runtimeCodeAndData=dict(files=len(all_runtime),allUnchanged=all(n in payload['unchangedPaths'] for n in all_runtime)),dependencyDirectories=dependencies,reuseBoundary=['R3 component-only/browser/canvas/translation dependencies retain byte-identical packaged inputs.','420/422 main-application build inputs are identical; only cleanup progress and maintenance-stage reporting changed.','R3 functional evidence can be retained with explicit source/payload equivalence; this is not an R4 runtime test pass.','R4 installer safety and embedded maintenance regression plus actual public42-to-R4 upgrade remain required; original R3 installation failure remains failed.'],physicalProductExecuted=False,resourcePayloadReextracted=False,sourceOrIndexEdited=False)
output=BASE/'r4-native-regressions/r3-r4-payload-reuse-review.json'
with output.open('x',encoding='utf-8') as f:json.dump(report,f,ensure_ascii=False,indent=2)
print(json.dumps(dict(report=str(output),sha256=sha(output),build=report['build'],payload=report['payload'],rootDlls=report['rootDlls'],runtime=report['runtimeCodeAndData'],dependencies=dependencies),ensure_ascii=False,indent=2))
