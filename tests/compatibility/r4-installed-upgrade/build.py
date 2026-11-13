"""Compile archived native helpers only; never run helpers or product."""
import argparse,hashlib,json,os,pathlib,subprocess
ROOT=pathlib.Path(__file__).resolve().parent
def sha(p):return hashlib.sha256(pathlib.Path(p).read_bytes()).hexdigest().upper()
def main():
    parser=argparse.ArgumentParser();parser.add_argument('--build',type=pathlib.Path,required=True);parser.add_argument('--output',type=pathlib.Path,required=True);args=parser.parse_args()
    manifest=json.loads((ROOT/'source-provenance.json').read_text(encoding='utf-8'));build=args.build.resolve();output=args.output.resolve()
    if sha(build/'Mori.exe')!=manifest['appSha256']:raise ValueError('Exact frozen R4 build required')
    if output.exists() or output==build or build in output.parents or output==ROOT or ROOT in output.parents:raise ValueError('Fresh output outside product/source required')
    framework=pathlib.Path(os.environ['WINDIR'])/'Microsoft.NET/Framework64/v4.0.30319';compiler=framework/'csc.exe';output.mkdir(parents=True);receipts=[]
    for recipe in manifest['recipes']:
        if sha(compiler)!=recipe['compilerSha256']:raise ValueError('Compiler differs from reviewed version')
        sources=[ROOT/r['path'] for r in recipe['sources']];refs=[framework/r['relative'] for r in recipe['references']]
        for source,record in zip(sources,recipe['sources']):
            if sha(source)!=record['sha256']:raise ValueError('Archived source bytes changed: '+str(source))
        for reference,record in zip(refs,recipe['references']):
            if sha(reference)!=record['sha256']:raise ValueError('Framework reference changed: '+str(reference))
        target=output/recipe['name'];target.mkdir();exe=target/recipe['outputName']
        command=[str(compiler),'/nologo']+recipe['flags']+['/out:'+str(exe)]+['/r:'+str(p) for p in refs]+[str(p) for p in sources]
        result=subprocess.run(command,capture_output=True,text=True);(target/'compile.log').write_text(result.stdout+result.stderr,encoding='utf-8')
        if result.returncode:raise RuntimeError(result.stdout+result.stderr)
        receipts.append(dict(name=recipe['name'],sha256=sha(exe),command=command,hostExecution='Compiler only'))
    (output/'compile-provenance.json').write_text(json.dumps(dict(appSha256=manifest['appSha256'],outputs=receipts),indent=2),encoding='utf-8')
    print('Compiled only. New executable hashes require fresh reviewed package plans; no tests or product executed.')
if __name__=='__main__':main()
