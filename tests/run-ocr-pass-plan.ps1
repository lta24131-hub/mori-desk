param(
 [ValidateSet(1,2)][int]$ExpectedEnglishPasses=2,
 [string]$OutputDirectory,
 [switch]$CompileOnly
)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path $PSScriptRoot -Parent
$allowedRoot=[IO.Path]::GetFullPath((Join-Path $repoRoot 'outputs'))
if(!$OutputDirectory){$OutputDirectory=Join-Path $allowedRoot ('ocr-pass-plan\'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))}
$runRoot=[IO.Path]::GetFullPath($OutputDirectory)
if(!$runRoot.StartsWith($allowedRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'OutputDirectory must be a new child directory inside this repository outputs directory.'}
if(Test-Path -LiteralPath $runRoot){throw 'OutputDirectory must not already exist; prior evidence is never overwritten.'}
$productionSource=Join-Path $repoRoot 'modules\capture\src\EnglishOcr.cs'
$testSource=Join-Path $repoRoot 'modules\capture\tests\OcrPassPlanTests.cs'
foreach($source in @($productionSource,$testSource)){
 if(!(Test-Path -LiteralPath $source -PathType Leaf)){throw ('Required source missing: '+$source)}
}
# Compile precisely two sources. No app, real OCR wrapper/native DLLs, image,
# settings, profile, language models, library, network or UI assembly is loaded.
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler=Join-Path $framework 'csc.exe'
$refs=@('System.dll','System.Core.dll') | ForEach-Object {'/r:'+(Join-Path $framework $_)}
$refs+='/r:'+(Join-Path $framework 'WPF\WindowsBase.dll')
if(!(Test-Path -LiteralPath $compiler -PathType Leaf)){throw '.NET Framework console compiler is not available.'}
New-Item -ItemType Directory -Path $runRoot | Out-Null
$runner=Join-Path $runRoot 'OcrPassPlanTests.exe'
$manifest=[ordered]@{
 test='OCR pass-plan and complete-line merge inert-stub contract'; expectedEnglishPasses=$ExpectedEnglishPasses
 productionSource=$productionSource; productionSha256=(Get-FileHash -LiteralPath $productionSource -Algorithm SHA256).Hash
 testSource=$testSource; testSha256=(Get-FileHash -LiteralPath $testSource -Algorithm SHA256).Hash
 safety='Standalone console host; production EnglishOcr compiled unchanged with inert stubs; no actual inference, UI, app startup, data/settings access or network.'
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'manifest.json') -Encoding UTF8
& $compiler /nologo /target:exe /define:OCR_PASS_PLAN_TESTS /main:OcrPassPlanTests /platform:x64 /codepage:65001 ('/out:'+$runner) @refs $productionSource $testSource 2>&1 | Tee-Object -FilePath (Join-Path $runRoot 'compile.txt')
if($LASTEXITCODE -ne 0){throw ('Inert OCR test compilation failed: '+$runRoot)}
Write-Output ('OCR_PASS_PLAN_TEST_ROOT='+$runRoot)
if($CompileOnly){Write-Output 'Compiled only; no test host, application or OCR process was started.';return}
$stdout=Join-Path $runRoot 'stdout.txt'
$stderr=Join-Path $runRoot 'stderr.txt'
$process=Start-Process -FilePath $runner -ArgumentList ('--expected-english-passes='+$ExpectedEnglishPasses) -WorkingDirectory $runRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
if(!$process.WaitForExit(15000)){
 $process.Kill()
 throw ('Isolated inert console test exceeded 15 seconds: '+$runRoot)
}
$process.Refresh()
Get-Content -LiteralPath $stdout -Encoding UTF8
if($process.ExitCode -ne 0){
 Get-Content -LiteralPath $stderr -Encoding UTF8
 throw ('OCR pass-plan regression failed; expected English passes='+$ExpectedEnglishPasses+'. Evidence: '+$runRoot)
}
