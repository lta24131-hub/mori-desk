param(
 [Parameter(Mandatory=$true)][string]$Installer,
 [Parameter(Mandatory=$true)][string]$InventorySource,
 [Parameter(Mandatory=$true)][string]$SampleSvg,
 [Parameter(Mandatory=$true)][string]$SampleVideo,
 [Parameter(Mandatory=$true)][string]$TransportSession,
 [Parameter(Mandatory=$true)][string]$Session
)
$ErrorActionPreference='Stop'
$sessionRoot=[IO.Path]::GetFullPath($Session).TrimEnd('\')
if((Test-Path -LiteralPath $sessionRoot) -or !(Test-Path -LiteralPath (Split-Path $sessionRoot -Parent) -PathType Container)){throw 'A fresh session with an existing parent is required.'}
foreach($part in @('','package','source','queue','delivered','reports')){New-Item -ItemType Directory -Path (Join-Path $sessionRoot $part)|Out-Null}
$package=Join-Path $sessionRoot 'package';$utf8=[Text.UTF8Encoding]::new($false)
$compiler='C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
Copy-Item -LiteralPath $InventorySource -Destination (Join-Path $sessionRoot 'source\InstalledInventoryGuest.cs')
foreach($source in @('ShellInstalledGuest.cs','ShellBitmapEvidence.cs')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $source) -Destination (Join-Path (Join-Path $sessionRoot 'source') $source)}
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 ('/out:'+(Join-Path $package 'Mori.exe')) /r:System.dll /r:System.Core.dll /r:System.Management.dll /r:System.Web.Extensions.dll $InventorySource
if($LASTEXITCODE -ne 0){throw 'Read-only installed inventory compilation failed.'}
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 ('/out:'+(Join-Path $package 'ShellInstalledGuest.exe')) /r:System.dll /r:System.Core.dll /r:System.Management.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'ShellInstalledGuest.cs') (Join-Path $PSScriptRoot 'ShellBitmapEvidence.cs')
if($LASTEXITCODE -ne 0){throw 'Read-only installed shell helper compilation failed.'}
Copy-Item -LiteralPath $Installer -Destination (Join-Path $package 'Nook-Setup.exe')
New-Item -ItemType Directory -Path (Join-Path $package 'fixtures')|Out-Null
Copy-Item -LiteralPath $SampleSvg -Destination (Join-Path $package 'fixtures\actual-user.svg')
Copy-Item -LiteralPath $SampleVideo -Destination (Join-Path $package 'fixtures\counting.mp4')
$record=@{scope='genuine-installer-delivery-only';physicalDesktopTouched=$false;installerSha256=(Get-FileHash -LiteralPath $Installer).Hash;files=@(Get-ChildItem -LiteralPath $package -File -Recurse|ForEach-Object{@{relative=$_.FullName.Substring($package.Length+1).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})}
[IO.File]::WriteAllText((Join-Path $package 'delivery-record.json'),($record|ConvertTo-Json -Depth 5),$utf8)
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=Join-Path $sessionRoot 'genuine-installer-delivery.zip';[IO.Compression.ZipFile]::CreateFromDirectory($package,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
$sha=(Get-FileHash -LiteralPath $zip).Hash;$prior=Get-Content (Join-Path $TransportSession 'broker-config.json') -Raw|ConvertFrom-Json
[IO.File]::WriteAllText((Join-Path $sessionRoot 'broker-config.json'),(@{token=$prior.token;files=@{'Nook.zip'=$zip}}|ConvertTo-Json -Depth 4),$utf8)
[IO.File]::WriteAllText((Join-Path $sessionRoot 'manifest.json'),(@{sha256=$sha;dependencies=@()}|ConvertTo-Json),$utf8)
$preparation=@{session=$sessionRoot;scope=$record.scope;packageSha256=$sha;installerSha256=$record.installerSha256;guestInstaller=('C:\NookLab\Audit\apps\'+$sha.Substring(0,12)+'\Nook-Setup.exe');hostExecution=$false;guestExecution=$false;preparedUtc=[DateTime]::UtcNow.ToString('o')}
[IO.File]::WriteAllText((Join-Path $sessionRoot 'preparation.json'),($preparation|ConvertTo-Json),$utf8)
$preparation|ConvertTo-Json
