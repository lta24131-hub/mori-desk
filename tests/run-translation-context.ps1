param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$regressionRepo=Split-Path $PSScriptRoot -Parent
if(!$OutputDirectory){$OutputDirectory=Join-Path $regressionRepo ('outputs\translation-context-run-'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))}
$regressionOutput=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $regressionOutput){throw 'OutputDirectory must be new; existing files are never replaced.'}
New-Item -ItemType Directory -Path $regressionOutput | Out-Null
$regressionFramework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
# Deliberately compile only these three exact files. Do not use a source glob,
# build.ps1, the application's entry point, or the real AppState/settings.
$regressionSources=@(
 (Join-Path $regressionRepo 'modules\capture\src\Translation.cs'),
 (Join-Path $regressionRepo 'modules\capture\src\TranslationCache.cs'),
 (Join-Path $regressionRepo 'modules\capture\tests\TranslationContextTests.cs')
)
foreach($regressionSource in $regressionSources){if(!(Test-Path -LiteralPath $regressionSource -PathType Leaf)){throw ('Required test source missing: '+$regressionSource)}}
$regressionRefs=@('System.dll','System.Core.dll','System.Net.Http.dll','System.Web.Extensions.dll') | ForEach-Object {'/r:'+(Join-Path $regressionFramework $_)}
$regressionExe=Join-Path $regressionOutput 'TranslationContextTests.exe'
& (Join-Path $regressionFramework 'csc.exe') /nologo /target:exe /main:TranslationContextTests /define:TRANSLATION_CONTEXT_TESTS /platform:x64 /optimize+ /codepage:65001 ('/out:'+$regressionExe) @regressionRefs @regressionSources
if($LASTEXITCODE -ne 0){throw 'Translation-context regression compilation failed.'}
# This standalone runner has no application dependencies, window entry point or
# provider calls. Its adapters reject settings, offline and Google access; an
# assertion also proves the lazy production HTTP client was never constructed.
$regressionProcess=Start-Process -FilePath $regressionExe -WorkingDirectory $regressionOutput -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput (Join-Path $regressionOutput 'stdout.txt') -RedirectStandardError (Join-Path $regressionOutput 'stderr.txt')
Get-Content -LiteralPath (Join-Path $regressionOutput 'stdout.txt')
if($regressionProcess.ExitCode -ne 0){Get-Content -LiteralPath (Join-Path $regressionOutput 'stderr.txt');throw 'Translation-context regression failed.'}
Write-Output ('TRANSLATION_CONTEXT_RUN='+$regressionOutput)
