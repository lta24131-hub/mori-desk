"""Compile the reviewed R3 remainder helper without executing the product or tests."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess

APP_SHA = '97B122310B1033BE7EBDA63730F929C256D8D0972CBC3497C7A7D6539CCA50D6'
SOURCE = Path(__file__).resolve().parent


def digest(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--build', type=Path, required=True, help='Exact frozen R3 full release-build directory')
    parser.add_argument('--output', type=Path, required=True, help='Fresh output directory; never the product directory')
    args = parser.parse_args()
    build, output = args.build.resolve(), args.output.resolve()
    if digest(build / 'Mori.exe') != APP_SHA:
        raise ValueError('This supplement is pinned to R3; review and rebind before testing another build.')
    if output.exists() or output == build or build in output.parents:
        raise ValueError('Use a fresh directory outside the product build.')
    output.mkdir(parents=True)
    framework = Path(os.environ['WINDIR']) / 'Microsoft.NET/Framework64/v4.0.30319'
    compiler = framework / 'csc.exe'
    refs = [framework / item for item in [
        'System.Core.dll', 'System.Drawing.dll', 'System.Web.Extensions.dll',
        'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll',
        'System.Xaml.dll', 'System.Windows.Forms.dll', 'WPF/WindowsBase.dll',
        'WPF/PresentationFramework.dll', 'WPF/PresentationCore.dll',
    ]] + [build / 'Mori.exe']
    files = [SOURCE / name for name in [
        'R3SidebarSpacingTests.cs', 'R3BatchTests.cs', 'R3MediaNavigationTests.cs',
        'R3CoreRemainderTests.cs', 'R3CoreRemainderRunner.cs',
    ]]
    receipts = []

    def compile_one(name, source, references, alias=None):
        target = output / name
        inputs = [{'name': str(item), 'sha256': digest(item)} for item in source + references]
        command = [str(compiler), '/nologo', '/optimize+', '/target:exe', '/platform:x64',
                   '/codepage:65001', '/out:' + str(target)]
        command += ['/r:' + str(item) for item in references] + [str(item) for item in source]
        subprocess.run(command, check=True)
        for item in inputs:
            if digest(item['name']) != item['sha256']:
                raise ValueError('Compile input changed: ' + item['name'])
        if alias:
            alias_path = output / alias
            shutil.copyfile(target, alias_path)
            target = alias_path
        receipts.append({'assemblyIdentity': name, 'file': target.name,
                         'sha256': digest(target), 'inputs': inputs})

    # Product InternalsVisibleTo requires this assembly identity. The filename
    # alias avoids replacing the product's independent capture-test assembly.
    compile_one('QingJie.Tests.exe', files, refs, 'R3CoreRemainderRunner.exe')
    compile_one('CoreRemainderGuest.exe', [SOURCE / 'CoreRemainderGuest.cs'],
                [framework / name for name in ['System.Core.dll', 'System.Management.dll', 'System.Web.Extensions.dll']])
    (output / 'compile-provenance.json').write_text(json.dumps({
        'appSha256': APP_SHA, 'hostExecution': 'Compiler only', 'outputs': receipts,
        'note': 'Fresh binary hashes must be reviewed and placed in the VM-only wrapper plan before execution.'
    }, indent=2), encoding='utf-8')
    print('Compiled only. No product or test process was executed.')


if __name__ == '__main__':
    main()
