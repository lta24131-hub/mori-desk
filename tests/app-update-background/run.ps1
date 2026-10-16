param([Parameter(Mandatory=$true)][string]$SuiteExecutable,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
$suiteExecutable=[IO.Path]::GetFullPath($SuiteExecutable)
if(!(Test-Path -LiteralPath $suiteExecutable -PathType Leaf)){throw 'Built suite executable is missing'}
if(!$OutputDirectory){$OutputDirectory=Join-Path ([IO.Path]::GetTempPath()) ('Nook-update-background-'+[Guid]::NewGuid().ToString('N'))}
$output=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$framework='C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$testExecutable=Join-Path $output 'AppUpdateBackgroundTests.exe'
$refs=@('System.dll','System.Core.dll') | ForEach-Object {'/r:'+(Join-Path $framework $_)}
& "$framework\csc.exe" /nologo /target:exe /main:NookBackgroundUpdateTests.Run /platform:x64 /codepage:65001 ('/out:'+$testExecutable) @refs (Join-Path $PSScriptRoot 'Run.cs')
if($LASTEXITCODE -ne 0){throw 'Background update test runner compilation failed'}
$config=$suiteExecutable+'.config'
if(Test-Path -LiteralPath $config){Copy-Item -LiteralPath $config -Destination ($testExecutable+'.config') -Force}
$fixtures=Join-Path $output ('fixtures-'+[Guid]::NewGuid().ToString('N'))
# This runner only loads test methods. It never invokes the Nook entry point,
# constructs/shows a Window, captures a display or touches personal data.
& $testExecutable $suiteExecutable $fixtures
if($LASTEXITCODE -ne 0){throw 'Background updater checks failed'}
