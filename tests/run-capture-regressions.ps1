param(
 [Parameter(Mandatory=$true)][string]$BuildDirectory,
 [string]$OutputDirectory,
 [string]$NodePath='node'
)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path $PSScriptRoot -Parent
$buildRoot=[IO.Path]::GetFullPath($BuildDirectory)
if(!(Test-Path -LiteralPath (Join-Path $buildRoot 'Mori.exe') -PathType Leaf)){throw 'Build a fresh candidate first. This runner never builds, launches, installs, or restarts Nook.'}
$helperRoot=Join-Path $buildRoot 'scroll-capture'
$helperManifest=Get-Content -LiteralPath (Join-Path $helperRoot 'component.json') -Raw | ConvertFrom-Json
$helperHash=(Get-FileHash -LiteralPath (Join-Path $helperRoot 'resources\app\out\main\main.js') -Algorithm SHA256).Hash
if($helperManifest.schemaVersion -ne 1 -or $helperHash -ne $helperManifest.applicationSha256){throw 'Candidate helper integrity check failed; rebuild before testing.'}
if(!$OutputDirectory){$OutputDirectory=Join-Path $repoRoot ('outputs\capture-regressions\'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))}
$runRoot=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $runRoot){throw 'Use a fresh OutputDirectory; previous evidence is never overwritten.'}
$nodeCommand=Get-Command $NodePath -CommandType Application -ErrorAction Stop
$nodeExe=$nodeCommand.Source
New-Item -ItemType Directory -Path $runRoot | Out-Null
$started=[DateTime]::UtcNow
$watch=[Diagnostics.Stopwatch]::StartNew()
$result=[ordered]@{
 schemaVersion=1
 scope='background-only; synthetic data; no desktop, clipboard, library, installation or release'
 buildDirectory=$buildRoot
 applicationSha256=(Get-FileHash -LiteralPath (Join-Path $buildRoot 'Mori.exe') -Algorithm SHA256).Hash
 helperApplicationSha256=$helperHash
 helperUpstreamCommit=$helperManifest.upstreamCommit
 startedUtc=$started.ToString('o')
 helperStatus='not-run'
 captureStatus='not-run'
 elapsedSeconds=0
 visualAcceptance='pending-separate-permission'
 result='failed'
}
try{
 # Explicit files avoid shell glob differences. These Node tests substitute the
 # Electron/native boundary and never start a browser or capture a real screen.
 $helperTests=@(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'modules\scroll-capture\tests') -Filter '*.test.mjs' -File | Sort-Object Name | ForEach-Object FullName)
 if($helperTests.Count -eq 0){throw 'No helper tests found.'}
 & $nodeExe --test @helperTests 2>&1 | Tee-Object -FilePath (Join-Path $runRoot 'helper-tests.txt')
 if($LASTEXITCODE -ne 0){throw 'Scrolling helper tests failed; see helper-tests.txt.'}
 $result.helperStatus='passed'
 # Use only the reviewed non-native allowlist, never build.ps1 -Test or
 # -CaptureTests: those broad runners may create desktop windows.
 $groups=@(
  'CaptureInteractionReworkTests','CaptureInputUsabilityTests',
  'CaptureSelectionInteractionTests','CaptureSelectionKeyboardTests',
  'MoriDesk.CaptureToolbarLayoutTests','CaptureAnnotationInteractionTests',
  'CaptureToolbarUsabilityTests','CaptureDirectResizeTests',
  'CaptureToolbarIntegrationTests','CaptureOutputLifecycleTests',
  'CaptureMonitorSessionTests','MoriDesk.ScreenshotCollectionTests',
  'ExternalScrollCaptureTests','CaptureFeatureSettingsTests',
  'CaptureFeatureRuntimeTests','CaptureFeatureBoundaryTests','ScrollCaptureWarmLeaseTests'
 )
 $sources=@($groups | ForEach-Object {
  $testFile=($_ -split '\.')[-1]+'.cs'
  $testFolder=if($_ -like 'MoriDesk.*'){'tests'}else{'modules\capture\tests'}
  Join-Path (Join-Path $repoRoot $testFolder) $testFile
 })
 & (Join-Path $PSScriptRoot 'run-capture-background.ps1') -BuildDirectory $buildRoot -OutputDirectory (Join-Path $runRoot 'capture') -AdditionalTestSource $sources -AdditionalTestClass $groups
 $result.captureStatus='passed'
 $result.result='passed'
}finally{
 $watch.Stop()
 $result.elapsedSeconds=[Math]::Round($watch.Elapsed.TotalSeconds,2)
 $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runRoot 'result.json') -Encoding UTF8
 Write-Output ('BACKGROUND_REGRESSION_EVIDENCE='+$runRoot)
}
