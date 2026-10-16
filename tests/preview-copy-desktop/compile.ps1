param([string]$BuildDirectory='outputs/nook-copy-final-20261016',[string]$OutputDirectory)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$buildRoot=if([IO.Path]::IsPathRooted($BuildDirectory)){[IO.Path]::GetFullPath($BuildDirectory)}else{[IO.Path]::GetFullPath((Join-Path $repoRoot $BuildDirectory))}
if(!(Test-Path -LiteralPath (Join-Path $buildRoot 'Mori.exe') -PathType Leaf)){throw 'BuildDirectory must contain the candidate Mori.exe.'}
if(!$OutputDirectory){$OutputDirectory=Join-Path $repoRoot ('outputs/preview-copy-desktop-'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss'))}
$outputRoot=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $outputRoot){throw 'Use a fresh output directory; existing fixture files are never overwritten.'}
New-Item -ItemType Directory -Path $outputRoot | Out-Null
New-Item -ItemType File -Path (Join-Path $outputRoot '.nook-preview-copy-desktop') | Out-Null
$framework='C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$refs=@('System.dll','System.Core.dll','System.Xaml.dll') | ForEach-Object {'/r:'+(Join-Path $framework $_)}
$refs+=@('PresentationCore.dll','PresentationFramework.dll','WindowsBase.dll') | ForEach-Object {'/r:'+(Join-Path $framework ('WPF\'+$_))}
$refs+='/r:'+(Join-Path $buildRoot 'Mori.exe')
$executable=Join-Path $outputRoot 'PreviewCopy.Desktop.exe'
& (Join-Path $framework 'csc.exe') /nologo /target:winexe /platform:x64 /codepage:65001 ('/out:'+$executable) @refs (Join-Path $PSScriptRoot 'Fixture.cs')
if($LASTEXITCODE -ne 0){throw 'Manual desktop fixture compilation failed.'}
Copy-Item -LiteralPath (Join-Path $buildRoot 'Mori.exe.config') -Destination ($executable+'.config')
Write-Output ('COMPILED_ONLY='+$executable)
Write-Output ('MANUAL_ARGUMENTS=--manual-desktop-acceptance "'+$buildRoot+'"')
Write-Output 'No fixture, application, window or clipboard action was started.'
