param(
 [Parameter(Mandatory=$true)][string]$Component,
 [Parameter(Mandatory=$true)][string]$RegistrationHarness,
 [Parameter(Mandatory=$true)][string]$NativeHarness,
 [Parameter(Mandatory=$true)][string]$SampleVideo,
 [Parameter(Mandatory=$true)][string]$TransportSession,
 [Parameter(Mandatory=$true)][string]$Session,
 [string]$SampleSvg,
 [switch]$SkipNative
)
$ErrorActionPreference='Stop'
$sessionRoot=[IO.Path]::GetFullPath($Session).TrimEnd('\')
if((Test-Path -LiteralPath $sessionRoot) -or !(Test-Path -LiteralPath (Split-Path $sessionRoot -Parent) -PathType Container)){throw 'A fresh session with an existing parent directory is required.'}
foreach($part in @('','package','source','queue','delivered','reports')){New-Item -ItemType Directory -Path (Join-Path $sessionRoot $part)|Out-Null}
$package=Join-Path $sessionRoot 'package';$utf8=[Text.UTF8Encoding]::new($false)
$compiler='C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ShellBitmapEvidence.cs') -Destination (Join-Path $sessionRoot 'source\ShellBitmapEvidence.cs')
foreach($build in @(@{source='ShellGuestRunner.cs';exe='Mori.exe'},@{source='ShellFactoryGuest.cs';exe='ShellFactoryGuest.exe'})){
 $src=Join-Path $PSScriptRoot $build.source
 Copy-Item -LiteralPath $src -Destination (Join-Path (Join-Path $sessionRoot 'source') $build.source)
 & $compiler /nologo /nowarn:0649 /target:exe /platform:x64 /codepage:65001 ('/out:'+(Join-Path $package $build.exe)) /r:System.dll /r:System.Core.dll /r:System.Management.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll $src (Join-Path $PSScriptRoot 'ShellBitmapEvidence.cs')
 if($LASTEXITCODE -ne 0){throw 'Guest-only helper compilation failed.'}
}
Copy-Item -LiteralPath $NativeHarness -Destination (Join-Path $package 'VerifyShellPreview.exe')
Copy-Item -LiteralPath $RegistrationHarness -Destination (Join-Path $package 'ShellRegistrationTests.exe')
$componentRoot=Join-Path $package 'component';New-Item -ItemType Directory -Path $componentRoot|Out-Null
$componentManifest=Get-Content (Join-Path $Component 'manifest.json') -Raw|ConvertFrom-Json
foreach($file in $componentManifest.files){
 $source=[IO.Path]::GetFullPath((Join-Path $Component $file.path));$destination=[IO.Path]::GetFullPath((Join-Path $componentRoot $file.path))
 if(!$source.StartsWith([IO.Path]::GetFullPath($Component).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or !$destination.StartsWith($componentRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Component path escaped.'}
 if((Get-FileHash -LiteralPath $source).Hash -ine $file.sha256){throw 'Component hash mismatch.'}
 New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force|Out-Null;Copy-Item -LiteralPath $source -Destination $destination
}
Copy-Item -LiteralPath (Join-Path $Component 'manifest.json') -Destination (Join-Path $componentRoot 'manifest.json')
New-Item -ItemType Directory -Path (Join-Path $package 'fixtures')|Out-Null
if($SampleSvg){Copy-Item -LiteralPath $SampleSvg -Destination (Join-Path $package 'fixtures\actual-user.svg')}
else{[IO.File]::WriteAllText((Join-Path $package 'fixtures\actual-user.svg'),'<svg xmlns="http://www.w3.org/2000/svg" width="160" height="80"><circle cx="80" cy="40" r="30" fill="#9364c8"/></svg>',$utf8)}
Copy-Item -LiteralPath $SampleVideo -Destination (Join-Path $package 'fixtures\counting.mp4')
$files=@(Get-ChildItem -LiteralPath $package -File -Recurse | ForEach-Object {@{relative=$_.FullName.Substring($package.Length+1).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
$manifest=@{schemaVersion=1;scope='synthetic-regressions-only-no-installation';files=$files;tests=@(
 @{id='shell-native';executable='VerifyShellPreview.exe';arguments=@('@package/component/Nook.ShellPreview.dll','@package/fixtures/actual-user.svg');timeoutSeconds=180},
 @{id='shell-registration';executable='ShellRegistrationTests.exe';arguments=@('--isolated-fixtures','@freshcase');timeoutSeconds=600},
 @{id='shell-factory';executable='ShellFactoryGuest.exe';arguments=@('@package/component/Nook.ShellPreview.dll','@package/fixtures/actual-user.svg','@package/fixtures/counting.mp4');timeoutSeconds=180}
)}
if($SkipNative){$manifest.tests=@($manifest.tests|Where-Object {$_.id -ne 'shell-native'})}
[IO.File]::WriteAllText((Join-Path $package 'isolated-tests.json'),($manifest|ConvertTo-Json -Depth 7),$utf8)
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=Join-Path $sessionRoot 'shell-tests.zip';[IO.Compression.ZipFile]::CreateFromDirectory($package,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
$sha=(Get-FileHash -LiteralPath $zip).Hash
$prior=Get-Content (Join-Path $TransportSession 'broker-config.json') -Raw|ConvertFrom-Json
[IO.File]::WriteAllText((Join-Path $sessionRoot 'broker-config.json'),(@{token=$prior.token;files=@{'Nook.zip'=$zip}}|ConvertTo-Json -Depth 4),$utf8)
[IO.File]::WriteAllText((Join-Path $sessionRoot 'manifest.json'),(@{sha256=$sha;dependencies=@()}|ConvertTo-Json),$utf8)
[pscustomobject]@{Session=$sessionRoot;PackageSha=$sha;PackageFiles=$files.Count;ZipBytes=(Get-Item $zip).Length;NativeDllSha=(Get-FileHash (Join-Path $componentRoot 'Nook.ShellPreview.dll')).Hash;GuestPackage=('C:\NookLab\Audit\apps\'+$sha.Substring(0,12))}|ConvertTo-Json
