param([Parameter(Mandatory=$true)][string]$SuiteExecutable,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
$suiteExecutable=[IO.Path]::GetFullPath($SuiteExecutable)
if(!(Test-Path -LiteralPath $suiteExecutable -PathType Leaf)){throw 'Built suite executable is missing'}
if(!$OutputDirectory){$OutputDirectory=Join-Path ([IO.Path]::GetTempPath()) ('Nook-preview-copy-'+[Guid]::NewGuid().ToString('N'))}
$output=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$framework='C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$testExecutable=Join-Path $output 'PreviewCopyBackgroundTests.exe'
$refs=@('System.dll','System.Core.dll') | ForEach-Object {'/r:'+(Join-Path $framework $_)}
& "$framework\csc.exe" /nologo /target:exe /main:NookPreviewCopyBackgroundTests.Run /platform:x64 /codepage:65001 ('/out:'+$testExecutable) @refs (Join-Path $PSScriptRoot 'Run.cs')
if($LASTEXITCODE -ne 0){throw 'Preview copy runner compilation failed'}
$config=$suiteExecutable+'.config'
if(Test-Path -LiteralPath $config){Copy-Item -LiteralPath $config -Destination ($testExecutable+'.config') -Force}
$fixtures=Join-Path $output ('fixtures-'+[Guid]::NewGuid().ToString('N'))
# Invoke only the isolated test; no real Clipboard, Window, input or app launch.
& $testExecutable $suiteExecutable $fixtures
if($LASTEXITCODE -ne 0){throw 'Preview copy background checks failed'}
