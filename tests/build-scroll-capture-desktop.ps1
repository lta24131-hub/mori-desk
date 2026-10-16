param(
 [Parameter(Mandatory=$true)][string]$BuildDirectory,
 [string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path $PSScriptRoot -Parent
$candidateRoot=[IO.Path]::GetFullPath($BuildDirectory)
$candidateExe=Join-Path $candidateRoot 'Mori.exe'
if(!(Test-Path -LiteralPath $candidateExe -PathType Leaf)){throw 'BuildDirectory must contain a separately compiled candidate Mori.exe.'}
$scrollComponent=Join-Path $candidateRoot 'scroll-capture'
foreach($required in @('Nook.ScrollCapture.exe','component.json','resources\app\out\main\main.js')){
 if(!(Test-Path -LiteralPath (Join-Path $scrollComponent $required) -PathType Leaf)){throw ('Candidate scroll capture component missing: '+$required)}
}
$outputsRoot=[IO.Path]::GetFullPath((Join-Path $repoRoot 'outputs'))
if(!$OutputDirectory){$OutputDirectory=Join-Path $outputsRoot ('scroll-capture-desktop-'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))}
$runRoot=[IO.Path]::GetFullPath($OutputDirectory)
if(!$runRoot.StartsWith($outputsRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Acceptance output must be a new directory underneath this repository outputs directory.'}
if(Test-Path -LiteralPath $runRoot){throw 'OutputDirectory already exists. This script never overwrites a test sandbox.'}
New-Item -ItemType Directory -Path $runRoot | Out-Null
New-Item -ItemType File -Path (Join-Path $runRoot '.capture-scroll-desktop-acceptance') | Out-Null
Copy-Item -LiteralPath $candidateExe -Destination $runRoot
Get-ChildItem -LiteralPath $candidateRoot -Filter '*.dll' -File | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $runRoot}
Copy-Item -LiteralPath $scrollComponent -Destination (Join-Path $runRoot 'scroll-capture') -Recurse
$appConfig=Join-Path $candidateRoot 'Mori.exe.config'
if(Test-Path -LiteralPath $appConfig){Copy-Item -LiteralPath $appConfig -Destination (Join-Path $runRoot 'QingJie.Tests.exe.config')}
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs=@('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xaml.dll') | ForEach-Object {'/r:'+(Join-Path $framework $_)}
$refs+=@('PresentationCore.dll','PresentationFramework.dll','WindowsBase.dll') | ForEach-Object {'/r:'+(Join-Path $framework ('WPF\'+$_))}
$refs+='/r:'+(Join-Path $runRoot 'Mori.exe')
$runner=Join-Path $runRoot 'QingJie.Tests.exe'
& (Join-Path $framework 'csc.exe') /nologo /target:winexe /main:ScrollCaptureDesktopAcceptance /platform:x64 /codepage:65001 ('/out:'+$runner) @refs (Join-Path $PSScriptRoot 'ScrollCaptureDesktopAcceptance.cs')
if($LASTEXITCODE -ne 0){throw 'Scroll-capture desktop acceptance host compilation failed. Sandbox retained for inspection.'}
Write-Output ('DESKTOP_ACCEPTANCE_EXE='+$runner)
Write-Output ('CANDIDATE_SHA256='+(Get-FileHash -LiteralPath (Join-Path $runRoot 'Mori.exe') -Algorithm SHA256).Hash)
Write-Output 'Compiled only. No UI was launched. Start the returned executable only after desktop-operation approval.'
