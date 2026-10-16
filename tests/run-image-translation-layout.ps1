param([string]$OutputDirectory,[switch]$CompileOnly)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path $PSScriptRoot -Parent
$allowedRoot=[IO.Path]::GetFullPath((Join-Path $repoRoot 'outputs'))
if(!$OutputDirectory){$OutputDirectory=Join-Path $allowedRoot ('image-translation-layout\'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))}
$runRoot=[IO.Path]::GetFullPath($OutputDirectory)
if(!$runRoot.StartsWith($allowedRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Output must be a new child directory under repository outputs.'}
if(Test-Path -LiteralPath $runRoot){throw 'Output exists; prior test evidence is never overwritten.'}
$production=Join-Path $repoRoot 'modules\capture\src\ImageTranslation.cs'
$test=Join-Path $repoRoot 'modules\capture\tests\ImageTranslationLayoutTests.cs'
# Only the renderer and inert script/data adapters are compiled. In particular,
# do not compile Translation.cs, Core.cs, AppMain, OcrService or application code.
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs=@('System.dll','System.Core.dll','System.Xaml.dll') | ForEach-Object {'/r:'+(Join-Path $framework $_)}
$refs+=@('WindowsBase.dll','PresentationCore.dll') | ForEach-Object {'/r:'+(Join-Path $framework ('WPF\'+$_))}
New-Item -ItemType Directory -Path $runRoot | Out-Null
New-Item -ItemType File -Path (Join-Path $runRoot '.image-translation-layout-sandbox') | Out-Null
$runner=Join-Path $runRoot 'ImageTranslationLayoutTests.exe'
[ordered]@{test='Image translation real-renderer offscreen layout';productionSource=$production;productionSha256=(Get-FileHash -LiteralPath $production -Algorithm SHA256).Hash;testSource=$test;testSha256=(Get-FileHash -LiteralPath $test -Algorithm SHA256).Hash;inferenceRuns=0;safety='Synthetic in-memory images and text only; no window/UI startup, actual OCR, network, AppState, settings, clipboard, desktop or real data.'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'manifest.json') -Encoding UTF8
& (Join-Path $framework 'csc.exe') /nologo /target:exe /define:IMAGE_TRANSLATION_LAYOUT_TESTS /main:ImageTranslationLayoutTests /platform:x64 /codepage:65001 ('/out:'+$runner) @refs $production $test 2>&1 | Tee-Object -FilePath (Join-Path $runRoot 'compile.txt')
if($LASTEXITCODE -ne 0){throw ('Offscreen translation layout compilation failed: '+$runRoot)}
Write-Output ('IMAGE_TRANSLATION_LAYOUT_ROOT='+$runRoot)
if($CompileOnly){Write-Output 'Compiled only; no renderer/test/application process started.';return}
$process=Start-Process -FilePath $runner -ArgumentList ('"'+$runRoot+'"') -WorkingDirectory $runRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runRoot 'stdout.txt') -RedirectStandardError (Join-Path $runRoot 'stderr.txt')
# Retain the OS handle before a short-lived child exits. Windows PowerShell
# can otherwise lose the exit code after WaitForExit/Refresh (null != 0).
$null=$process.Handle
if(!$process.WaitForExit(20000)){$process.Kill();throw ('Isolated offscreen test exceeded 20 seconds: '+$runRoot)}
$process.Refresh()
Get-Content -LiteralPath (Join-Path $runRoot 'stdout.txt') -Encoding UTF8
if($process.ExitCode -ne 0){Get-Content -LiteralPath (Join-Path $runRoot 'stderr.txt') -Encoding UTF8;throw ('Offscreen translation layout test failed: '+$runRoot)}
