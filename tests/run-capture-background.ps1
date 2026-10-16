param(
 [Parameter(Mandatory=$true)][string]$BuildDirectory,
 [string]$OutputDirectory,
 [string[]]$AdditionalTestSource=@(),
 [string[]]$AdditionalTestClass=@(),
 [switch]$CompileOnly
)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path $PSScriptRoot -Parent
$buildRoot=[IO.Path]::GetFullPath($BuildDirectory)
if(!(Test-Path -LiteralPath (Join-Path $buildRoot 'Mori.exe') -PathType Leaf)){throw 'BuildDirectory must contain a separately built Mori.exe. This runner never builds or starts the application.'}
if(!$OutputDirectory){$OutputDirectory=Join-Path $repoRoot ('outputs\capture-background\'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))}
$runRoot=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $runRoot){throw 'OutputDirectory must be a new directory; existing test artifacts are never overwritten.'}
$sources=@(
 (Join-Path $repoRoot 'modules\capture\tests\CaptureBackgroundTests.cs'),
 (Join-Path $repoRoot 'modules\capture\tests\CaptureVisualRegionTests.cs'),
 (Join-Path $repoRoot 'modules\capture\tests\CaptureHoverTests.cs'),
 (Join-Path $repoRoot 'modules\capture\tests\CaptureTextEditingTests.cs')
)
foreach($file in $AdditionalTestSource){$sources+=[IO.Path]::GetFullPath($file)}
if($AdditionalTestSource.Count -ne $AdditionalTestClass.Count){throw 'Each additional test source must have one explicit additional test class.'}
foreach($name in $AdditionalTestClass){if($name -notmatch '^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$'){throw ('Invalid test class: '+$name)}}
# An explicit allowlist avoids the broad Tests.Main / SuiteTests entry points.
# The source check is an additional tripwire, not a substitute for reviewing
# the production methods called by a newly added test group.
$forbidden='\.Show(?:Dialog)?\s*\(|EnsureHandle\s*\(|BeginCapture\s*\(|Snapshot\.Screen\s*\(|CaptureTargets\.Read\s*\(|Clipboard\s*\.|RegisterHotKey\s*\(|SendKeys\s*\.|Process\s*\.\s*Start\s*\(|CaptureTests\s*\.\s*(Run|Native)\s*\(|SuiteTests\s*\.\s*Run\s*\('
foreach($source in $sources){
 if(!(Test-Path -LiteralPath $source -PathType Leaf)){throw ('Test source missing: '+$source)}
 if((Get-Content -LiteralPath $source -Raw) -match $forbidden){throw ('Background safety tripwire rejected test source: '+$source)}
}
New-Item -ItemType Directory -Path $runRoot | Out-Null
New-Item -ItemType File -Path (Join-Path $runRoot '.capture-background-sandbox') | Out-Null
Copy-Item -LiteralPath (Join-Path $buildRoot 'Mori.exe') -Destination $runRoot
Get-ChildItem -LiteralPath $buildRoot -Filter '*.dll' -File | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $runRoot}
$appConfig=Join-Path $buildRoot 'Mori.exe.config'
if(Test-Path -LiteralPath $appConfig){Copy-Item -LiteralPath $appConfig -Destination (Join-Path $runRoot 'QingJie.Tests.exe.config')}
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs=@('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Xaml.dll') | ForEach-Object {'/r:'+(Join-Path $framework $_)}
$refs+=@('PresentationCore.dll','PresentationFramework.dll','WindowsBase.dll') | ForEach-Object {'/r:'+(Join-Path $framework ('WPF\'+$_))}
$refs+='/r:'+(Join-Path $runRoot 'Mori.exe')
$runner=Join-Path $runRoot 'QingJie.Tests.exe'
& (Join-Path $framework 'csc.exe') /nologo /target:exe /define:CAPTURE_BACKGROUND_TESTS /main:CaptureBackgroundTests /platform:x64 /codepage:65001 ('/out:'+$runner) @refs @sources
if($LASTEXITCODE -ne 0){throw 'Background test runner compilation failed.'}
Write-Output ('BACKGROUND_TEST_ROOT='+$runRoot)
if($CompileOnly){Write-Output 'Compiled only; no test process or application was started.';return}
$oldData=$env:MORI_DESK_DATA
try{
 $env:MORI_DESK_DATA=Join-Path $runRoot 'data'
 $arguments=@(('"'+$runRoot+'"'))+$AdditionalTestClass
 $process=Start-Process -FilePath $runner -ArgumentList $arguments -WorkingDirectory $runRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runRoot 'background-stdout.txt') -RedirectStandardError (Join-Path $runRoot 'background-stderr.txt')
 # Keep the native handle before a short-lived child exits; otherwise Windows
 # PowerShell can report a null exit code after waiting for an exited process.
 $null=$process.Handle
 if(!$process.WaitForExit(60000)){$process.Kill();throw ('Background tests exceeded 60 seconds. See '+$runRoot)}
 $process.Refresh()
 if($process.ExitCode -ne 0){throw ('Background tests failed. See '+(Join-Path $runRoot 'background-stderr.txt'))}
 Get-Content -LiteralPath (Join-Path $runRoot 'background-tests.txt')
}finally{$env:MORI_DESK_DATA=$oldData}
