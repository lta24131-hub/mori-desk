$ErrorActionPreference='Stop'
$suiteReviewRoot=Split-Path -Parent $PSScriptRoot
$reviewOutput=Join-Path $env:TEMP ('MoriDesk-browser-status-review-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $reviewOutput | Out-Null
$reviewCompiler='C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$reviewSources=@(
 (Join-Path $suiteReviewRoot 'src\BrowserExtensionManager.cs')
 (Join-Path $suiteReviewRoot 'src\BrowserSettingsPresentation.cs')
 (Join-Path $suiteReviewRoot 'src\BrowserLocalBridge.cs')
 (Join-Path $PSScriptRoot 'browser-status-review\Review.cs')
)
& $reviewCompiler /nologo /target:exe /main:MoriDesk.Review /platform:x64 /codepage:65001 ('/out:'+(Join-Path $reviewOutput 'Review.exe')) /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll @reviewSources
if($LASTEXITCODE -ne 0){throw 'Isolated browser-status fixture compile failed'}
& (Join-Path $reviewOutput 'Review.exe') $reviewOutput
if($LASTEXITCODE -ne 0){throw 'Isolated browser-status fixture failed'}
Write-Host ('Isolated synthetic fixture retained: '+$reviewOutput)
