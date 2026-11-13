"""Compile reviewed VM-only helpers. This script never runs the helpers or product."""
import argparse,hashlib,json,os,pathlib,shutil,subprocess
ROOT=pathlib.Path(__file__).resolve().parent
def sha(p):
    with pathlib.Path(p).open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()
def main():
    parser=argparse.ArgumentParser();parser.add_argument('--build',type=pathlib.Path,required=True,help='Exact frozen R3 release-build');parser.add_argument('--output',type=pathlib.Path,required=True,help='Fresh directory outside the product build');args=parser.parse_args()
    manifest=json.loads((ROOT/'source-provenance.json').read_text(encoding='utf-8'));build=args.build.resolve();output=args.output.resolve()
    if sha(build/'Mori.exe')!=manifest['appSha256']:raise ValueError('Wrong product build; a reviewed rebind is required')
    if output.exists() or output==build or build in output.parents or output==ROOT or ROOT in output.parents:raise ValueError('Fresh output outside product/source required')
    framework=pathlib.Path(os.environ['WINDIR'])/'Microsoft.NET/Framework64/v4.0.30319';compiler=framework/'csc.exe';output.mkdir(parents=True);receipts=[]
    for recipe in manifest['recipes']:
        source=ROOT/recipe['source']
        if sha(source)!=recipe['sourceSha256']:raise ValueError('Reviewed helper source changed: '+str(source))
        target=output/recipe['name'];target.mkdir();assembly=target/recipe['assemblyIdentity'];refs=[(framework if item['root']=='framework' else build)/item['relative'] for item in recipe['references']]
        for reference,record in zip(refs,recipe['references']):
            if sha(reference)!=record['sha256']:raise ValueError('Reference differs from reviewed build: '+str(reference))
        command=[str(compiler),'/nologo','/target:exe','/optimize+','/platform:x64','/codepage:65001','/out:'+str(assembly)]+['/r:'+str(p) for p in refs]+[str(source)]
        result=subprocess.run(command,capture_output=True,text=True);(target/'compile.log').write_text(result.stdout+result.stderr,encoding='utf-8')
        if result.returncode:raise RuntimeError(result.stdout+result.stderr)
        if recipe['assemblyIdentity']!=recipe['outputName']:shutil.copyfile(assembly,target/recipe['outputName'])
        receipts.append(dict(name=recipe['name'],output=recipe['outputName'],sha256=sha(target/recipe['outputName']),sourceSha256=sha(source),command=command,hostExecution='Compiler only'))
    (output/'compile-provenance.json').write_text(json.dumps(dict(appSha256=manifest['appSha256'],outputs=receipts,note='Compilation is not a test pass. New binaries require new reviewed plan hashes and separate VM deployment; original package reports remain unchanged.'),indent=2),encoding='utf-8')
    print('Compiled only; no product/helper executed. Rebind new runner hashes in fresh VM package plans before use.')
if __name__=='__main__':main()
