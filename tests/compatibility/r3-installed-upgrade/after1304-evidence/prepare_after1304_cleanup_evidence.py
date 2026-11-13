import hashlib, json, pathlib, subprocess, zipfile, difflib

base = pathlib.Path(__file__).resolve().parent
old = base / 'r3-after1215-evidence-v2' / 'CleanupEvidenceGuest.cs'
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
assert sha(old) == '225ADF2190FB669BF0D339D82786B187E7A749BD7F3ADE85FBD9C3573FCF3081'
original = old.read_text(encoding='utf-8')
source = original.replace('07b9f49e81aa4ae5bd791a75bba772e1', '6ec57c3bede94645935f8e041f6a6dd4').replace('0.1.42-20261113132129723-14839d11', '0.1.42-20261113142121298-e4e817b1').replace('after1215', 'after1304')
source = source.replace(' const string Old=', ' const string Current="0.1.45-20261113145904278-57e527ec";\n const string Old=', 1)
start = source.index(' string preserved=')
end = source.index(' Capture("active-install.json"', start)
source = source[:start] + ''' Capture("current-native-manifest.json",Path.Combine(data,"browser","app.mori.desk.json"));
 var browserRoot=Path.Combine(data,"browser");var browserEntries=new List<object>();if(Directory.Exists(browserRoot))Tree(browserRoot,browserRoot,browserEntries);Artifact("current-browser-tree.json",browserRoot,Utf8.GetBytes(Json.Serialize(new{exists=Directory.Exists(browserRoot),entries=browserEntries,contentHashesComputed=false,sourceWrites=false})));
''' + source[end:]
source = source.replace('"activation-attempted.json"}', '"activation-attempted.json","cancel.request"}')
anchor = ' string journal=Path.Combine(program,".nook-cleanup-journal");'
addition = ''' string current=Path.Combine(program,Current);Plain(current);
 Capture("current-installed-files.json",Path.Combine(current,".nook-installed-files.json"));Capture("current-cleanup-progress.json",Path.Combine(current,".nook-cleanup-progress.json"));
 var currentEntries=new List<object>();if(Directory.Exists(current))Tree(current,current,currentEntries);Artifact("current-payload-tree.json",current,Utf8.GetBytes(Json.Serialize(new{exists=Directory.Exists(current),entries=currentEntries,contentHashesComputed=false,sourceWrites=false})));
 var roots=new List<object>();foreach(string folder in new[]{program,old,current,session,Path.Combine(data,"maintenance"),Path.Combine(program,".nook-cleanup-journal")}){Plain(folder);bool exists=Directory.Exists(folder);roots.Add(new{path=folder,exists,attributes=exists?(int?)File.GetAttributes(folder):null,writtenUtc=exists?File.GetLastWriteTimeUtc(folder).ToString("O"):null});}Artifact("cleanup-root-metadata.json",program,Utf8.GetBytes(Json.Serialize(new{entries=roots,sourceWrites=false})));
'''
assert source.count(anchor) == 1
source = source.replace(anchor, addition + anchor)
source = source.replace('oldDirectory=Old,artifacts=', 'oldDirectory=Old,currentDirectory=Current,artifacts=')
out = base / 'r3-after1304-evidence'
out.mkdir(exist_ok=False)
cs = out / 'CleanupEvidenceGuest.cs'
cs.write_bytes(source.replace('\n','\r\n').encode('utf-8'))
(out / 'readonly-diagnostic.diff').write_text(''.join(difflib.unified_diff(original.splitlines(True), source.splitlines(True), fromfile='original600C/CleanupEvidenceGuest.cs',tofile='after1304/CleanupEvidenceGuest.cs')),encoding='utf-8')
exe = out / 'CleanupEvidenceGuest.exe'
csc = r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
cmd = [csc,'/nologo','/optimize+','/target:exe','/r:System.Management.dll','/r:System.Web.Extensions.dll','/out:'+str(exe),str(cs)]
result = subprocess.run(cmd,capture_output=True)
(out / 'cleanup-evidence.compile.log').write_bytes(result.stdout+result.stderr)
assert result.returncode == 0, (result.stdout+result.stderr).decode(errors='replace')
archive = out / 'cleanup-evidence.zip'
with zipfile.ZipFile(archive,'x',zipfile.ZIP_DEFLATED,compresslevel=1) as z:
    z.write(cs,cs.name);z.write(exe,exe.name);z.write(exe,'Mori.exe')
handoff = dict(path=str(archive),sha256=sha(archive),sourceSha256=sha(cs),exeSha256=sha(exe),guest='C:\\NookLab\\Audit\\apps\\'+sha(archive)[:12],executable=exe.name,args=['--read-after1304-cleanup'],report='cleanup-evidence-result.json',command=cmd,hostExecution='Compiler only; helper not run',sourceWrites=False,sourceBaseSha256=sha(old),session='6ec57c3bede94645935f8e041f6a6dd4',oldDirectory='0.1.42-20261113142121298-e4e817b1',currentDirectory='0.1.45-20261113145904278-57e527ec')
(out / 'cleanup-evidence-handoff.json').write_text(json.dumps(handoff,indent=2),encoding='utf-8')
print(json.dumps(handoff,indent=2))
