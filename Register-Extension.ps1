param([Parameter(Mandatory=$true)][ValidatePattern('^[a-p]{32}$')][string]$ExtensionId)
$ErrorActionPreference='Stop'
$suiteExe=Join-Path $PSScriptRoot 'Mori.exe'
if(-not(Test-Path -LiteralPath $suiteExe)){$suiteExe=Join-Path $PSScriptRoot 'dist\Mori.exe'}
if(-not(Test-Path -LiteralPath $suiteExe)){throw 'Nook executable not found'}
$suiteProcess=Start-Process -FilePath $suiteExe -ArgumentList '--register-extension',$ExtensionId -WindowStyle Hidden -Wait -PassThru
if($suiteProcess.ExitCode -ne 0){throw 'Nook connection failed'}
Write-Output 'Nook connected. Existing Mori registration and other connected extension IDs are unchanged.'
