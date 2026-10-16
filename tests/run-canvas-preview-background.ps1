param([Parameter(Mandatory=$true)][string]$BuildDirectory,[string]$OutputDirectory,[switch]$CompileOnly)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path $PSScriptRoot -Parent
$buildRoot=[IO.Path]::GetFullPath($BuildDirectory)
if(!(Test-Path -LiteralPath (Join-Path $buildRoot 'Mori.exe') -PathType Leaf)){throw 'BuildDirectory must contain a separately built Mori.exe. The application is never started.'}
if(!$OutputDirectory){$OutputDirectory=Join-Path $repoRoot ('outputs\canvas-preview-background\'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))}
$runRoot=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $runRoot){throw 'OutputDirectory must be new; existing files are not overwritten.'}
$source=Join-Path $repoRoot 'tests\CanvasPreviewBackgroundTests.cs'
$forbidden='\.Show(?:Dialog)?\s*\(|EnsureHandle\s*\(|BeginCapture\s*\(|Snapshot\.Screen\s*\(|CaptureTargets\.Read\s*\(|Clipboard\s*\.|RegisterHotKey\s*\(|SendKeys\s*\.|Process\s*\.\s*Start\s*\('
if((Get-Content -LiteralPath $source -Raw) -match $forbidden){throw 'Background safety tripwire rejected the test source.'}
New-Item -ItemType Directory -Path $runRoot | Out-Null
New-Item -ItemType File -Path (Join-Path $runRoot '.canvas-preview-background-sandbox') | Out-Null
Copy-Item -LiteralPath (Join-Path $buildRoot 'Mori.exe') -Destination $runRoot
Get-ChildItem -LiteralPath $buildRoot -Filter '*.dll' -File | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $runRoot}
if(Test-Path -LiteralPath (Join-Path $buildRoot 'Mori.exe.config')){Copy-Item -LiteralPath (Join-Path $buildRoot 'Mori.exe.config') -Destination (Join-Path $runRoot 'QingJie.Tests.exe.config')}
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs=@('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Xaml.dll') | ForEach-Object {'/r:'+(Join-Path $framework $_)}
$refs+=@('PresentationCore.dll','PresentationFramework.dll','WindowsBase.dll') | ForEach-Object {'/r:'+(Join-Path $framework ('WPF\'+$_))}
$refs+='/r:'+(Join-Path $runRoot 'Mori.exe')
$runner=Join-Path $runRoot 'QingJie.Tests.exe'
& (Join-Path $framework 'csc.exe') /nologo /target:exe /define:CANVAS_PREVIEW_BACKGROUND /main:CanvasPreviewBackgroundTests /platform:x64 /codepage:65001 ('/out:'+$runner) @refs $source
if($LASTEXITCODE -ne 0){throw 'Canvas preview background test compilation failed.'}
Write-Output ('BACKGROUND_TEST_ROOT='+$runRoot)
if($CompileOnly){Write-Output 'Compiled only; no process started.';return}
$oldData=$env:MORI_DESK_DATA
try{
 $env:MORI_DESK_DATA=Join-Path $runRoot 'data'
 $process=Start-Process -FilePath $runner -ArgumentList ('"'+$runRoot+'"') -WorkingDirectory $runRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runRoot 'background-stdout.txt') -RedirectStandardError (Join-Path $runRoot 'background-stderr.txt')
 $null=$process.Handle
 if(!$process.WaitForExit(60000)){$process.Kill();throw ('Canvas preview tests exceeded 60 seconds. See '+$runRoot)}
 $process.Refresh()
 if($process.ExitCode -ne 0){throw ('Canvas preview tests failed. See '+(Join-Path $runRoot 'background-stderr.txt'))}
 Get-Content -LiteralPath (Join-Path $runRoot 'canvas-preview-tests.txt')
}finally{$env:MORI_DESK_DATA=$oldData}
