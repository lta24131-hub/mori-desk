param([string]$BuildDirectory='dist',[switch]$IncludeExperimentalFileConversion,[string]$LauncherDirectory='dependencies\update-launcher-v1')
$ErrorActionPreference='Stop'
$suiteRoot=$PSScriptRoot
. (Join-Path $suiteRoot 'tools\Assert-UpdateDataContract.ps1')
$dataContract=Assert-UpdateDataContract -SourceRoot $suiteRoot
$launcherRoot=if([IO.Path]::IsPathRooted($LauncherDirectory)){$LauncherDirectory}else{Join-Path $suiteRoot $LauncherDirectory}
$launcher=Join-Path $launcherRoot 'Mori.exe'
$provenance=Join-Path $launcherRoot 'launcher-provenance.json'
if(!(Test-Path -LiteralPath $launcher -PathType Leaf) -or !(Test-Path -LiteralPath $provenance -PathType Leaf)){throw 'A frozen, VM-validated protocol-1 launcher is required. Do not rebuild a different launcher for every release. Use tools\Build-UpdateLauncher.ps1 in an isolated candidate directory for validation first.'}
$launcherRecord=Get-Content -LiteralPath $provenance -Raw | ConvertFrom-Json
if($launcherRecord.schemaVersion -ne 1 -or $launcherRecord.protocolVersion -ne 1 -or $launcherRecord.sha256 -ne (Get-FileHash -LiteralPath $launcher -Algorithm SHA256).Hash){throw 'Stable launcher provenance mismatch'}
$launcherIdentity=[Diagnostics.FileVersionInfo]::GetVersionInfo($launcher)
if($launcherIdentity.ProductName -ne 'Nook' -or $launcherIdentity.OriginalFilename -ne 'Mori.exe' -or $launcherIdentity.FileVersion -ne '1.0.0.1'){throw 'Unexpected stable launcher identity'}
. (Join-Path $suiteRoot 'modules\file-conversion\Validate-Component.ps1')
$buildPath=if([IO.Path]::IsPathRooted($BuildDirectory)){$BuildDirectory}else{Join-Path $suiteRoot $BuildDirectory}
function Assert-NoMachineSecrets([string]$PathToCheck){
 $privateFiles=@(Get-ChildItem -LiteralPath $PathToCheck -Force -File -Recurse | Where-Object { $_.Name -like 'local-connection.js*' -or $_.Name -like 'local-key.txt*' -or $_.Name -eq 'github-token.dat' -or $_.Name -eq 'pairing.json' })
 if($privateFiles.Count -gt 0){throw 'This build contains a machine pairing key or a pairing temporary file. Use a fresh build directory to package a release.'}
}
function Assert-ExperimentalFileConversion([string]$ComponentPath){
 if(!(Test-Path -LiteralPath $ComponentPath -PathType Container)){throw 'Experimental conversion component is missing. Build with -FileConversionDirectory first.'}
 if((Get-Item -LiteralPath $ComponentPath).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Conversion component cannot be a redirected directory.'}
 $items=@(Get-ChildItem -LiteralPath $ComponentPath -Force -Recurse)
 if(@($items | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count -gt 0){throw 'Conversion component contains a redirected file or directory.'}
 $required=@(
  'Nook.FileConversion.exe','Nook.FileConversion.dll','Nook.FileConversion.runtimeconfig.json','Nook.FileConversion.deps.json',
  'component.json','upstream.json','Filee.Core.dll','Filee.Engines.dll',
  'coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll',
  'Magick.NET.Core.dll','Magick.NET-Q8-AnyCPU.dll','Magick.Native-Q8-x64.dll',
  'PdfSharp.dll','PDFtoImage.dll','pdfium.dll','SkiaSharp.dll','libSkiaSharp.dll','HarfBuzzSharp.dll','libHarfBuzzSharp.dll',
  'licenses\LICENSE-Filee.txt','licenses\THIRD-PARTY-NOTICES.md'
 )
 foreach($relative in $required){
  $file=Join-Path $ComponentPath $relative
  if(!(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -le 0){throw ('Incomplete experimental conversion component: '+$relative)}
 }
 if(!(Test-Path -LiteralPath (Join-Path $ComponentPath 'licenses\nuget') -PathType Container) -or
    @($items | Where-Object {$_.Extension -eq '.nuspec' -and $_.FullName.StartsWith((Join-Path $ComponentPath 'licenses\nuget\'),[StringComparison]::OrdinalIgnoreCase)}).Count -eq 0){throw 'Conversion component dependency license metadata is missing.'}
 $manifest=Get-Content -LiteralPath (Join-Path $ComponentPath 'component.json') -Raw | ConvertFrom-Json
 if($manifest.schemaVersion -ne 1 -or $manifest.component -ne 'file-conversion' -or $manifest.experimental -isnot [bool] -or $manifest.experimental -ne $true -or
    $manifest.upstreamVersion -ne 'v1.12.0' -or $manifest.upstreamCommit -ne '49911afab6eef81f144543710f1aa8021c617ff1' -or
    $manifest.externalEnginesEnabled -isnot [bool]){throw 'Unexpected experimental conversion component revision or engine configuration.'}
 Assert-ConversionEngineInventory $ComponentPath $manifest
 $pin=Join-Path $suiteRoot 'modules\file-conversion\upstream.json'
 if((Get-FileHash -LiteralPath (Join-Path $ComponentPath 'upstream.json') -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath $pin -Algorithm SHA256).Hash){throw 'Conversion component provenance does not match the pinned source manifest.'}
 foreach($item in @(@('Nook.FileConversion.exe','applicationSha256'),@('Nook.FileConversion.dll','applicationAssemblySha256'))){
  $expected=[string]$manifest.($item[1])
  if($expected -notmatch '^[a-fA-F0-9]{64}$' -or (Get-FileHash -LiteralPath (Join-Path $ComponentPath $item[0]) -Algorithm SHA256).Hash -ne $expected){
   throw ('Conversion component integrity check failed: '+$item[0])
  }
 }
 $license=Get-Content -LiteralPath (Join-Path $ComponentPath 'licenses\LICENSE-Filee.txt') -Raw
 if($license -notmatch 'MIT License' -or $license -notmatch 'Permission is hereby granted'){throw 'Filee MIT license is missing or incomplete.'}
}
function Conversion-FileInventory([string]$ComponentPath){
 $resolved=[IO.Path]::GetFullPath($ComponentPath).TrimEnd('\','/')
 $inventory=@{}
 Get-ChildItem -LiteralPath $resolved -Force -Recurse -File | ForEach-Object {
  $relative=$_.FullName.Substring($resolved.Length+1)
  $inventory[$relative]=[string]$_.Length+':'+(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
 }
 return $inventory
}
Assert-NoMachineSecrets $buildPath
$conversionSource=Join-Path $buildPath 'file-conversion'
if((Test-Path -LiteralPath $conversionSource) -and !$IncludeExperimentalFileConversion){
 throw 'The build contains experimental file conversion. Explicitly pass -IncludeExperimentalFileConversion; it must not be silently omitted from the installer.'
}
$conversionInventory=$null
if($IncludeExperimentalFileConversion){
 Assert-ExperimentalFileConversion $conversionSource
 $conversionInventory=Conversion-FileInventory $conversionSource
}
foreach($required in @('Nook.ScrollCapture.exe','component.json','LICENSE-eSearch.txt','NOTICES.md','corresponding-source')){
 if(!(Test-Path -LiteralPath (Join-Path $buildPath ('scroll-capture\'+$required)))){throw 'Release requires the verified scroll-capture runtime and corresponding source. Build with -ScrollCaptureDirectory.'}
}
$releaseRoot=Join-Path $suiteRoot ('releases\'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
$payload=Join-Path $releaseRoot 'Nook'
New-Item -ItemType Directory -Force $payload | Out-Null
$files=@('Mori.exe','Mori.exe.config','ExCSS.dll','Svg.dll','Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.Wpf.dll','WebView2Loader.dll','System.Buffers.dll','System.Memory.dll','System.Numerics.Vectors.dll','System.Runtime.CompilerServices.Unsafe.dll','Tesseract.dll','qingjie-icon.png')
$files+=@('Magick.NET.Core.dll','Magick.NET-Q8-x64.dll','Magick.Native-Q8-x64.dll','UglyToad.PdfPig.dll','UglyToad.PdfPig.Core.dll','UglyToad.PdfPig.DocumentLayoutAnalysis.dll','UglyToad.PdfPig.Fonts.dll','UglyToad.PdfPig.Package.dll','UglyToad.PdfPig.Tokenization.dll','UglyToad.PdfPig.Tokens.dll','Microsoft.Bcl.HashCode.dll','PREVIEW-NOTICES.md','CanvasIcons-NOTICE.txt')
foreach($file in $files){Copy-Item -LiteralPath (Join-Path $buildPath $file) -Destination $payload}
foreach($folder in @('x64','tessdata','web','browser-extension','svg-licenses','webview2-licenses','model-licenses','image-codecs','pdf-text','preview-support','scroll-capture')){Copy-Item -LiteralPath (Join-Path $buildPath $folder) -Destination $payload -Recurse}
if($IncludeExperimentalFileConversion){
 $conversionPayload=Join-Path $payload 'file-conversion'
 Copy-Item -LiteralPath $conversionSource -Destination $conversionPayload -Recurse
 Assert-ExperimentalFileConversion $conversionPayload
 $copiedInventory=Conversion-FileInventory $conversionPayload
 if($copiedInventory.Count -ne $conversionInventory.Count){throw 'Conversion component payload is incomplete.'}
 foreach($relative in $conversionInventory.Keys){
  if(!$copiedInventory.ContainsKey($relative) -or $copiedInventory[$relative] -ne $conversionInventory[$relative]){
   throw ('Conversion component changed or was omitted while packaging: '+$relative)
  }
 }
}
Copy-Item "$suiteRoot\modules\capture\licenses" "$payload\capture-licenses" -Recurse
Copy-Item "$suiteRoot\modules\capture\THIRD-PARTY-NOTICES.md" "$payload\CAPTURE-NOTICES.md"
Copy-Item "$suiteRoot\README.md","$suiteRoot\Register-Extension.ps1" $payload
Copy-Item "$suiteRoot\docs" "$payload\docs" -Recurse
Copy-Item "$suiteRoot\RELEASE-NOTES.md" $payload
Copy-Item -LiteralPath "$suiteRoot\docs\使用说明.md" -Destination "$payload\使用说明.txt"
$buildVersion=[System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $buildPath 'Mori.exe')).FileVersion
$zip=Join-Path $releaseRoot ('Nook-'+$buildVersion+'-portable.zip')
# Pairing can happen while a folder is being copied; validate the actual
# payload, not only the initial build directory, before publishing any bytes.
Assert-NoMachineSecrets $payload
Compress-Archive -Path "$payload\*" -DestinationPath $zip -CompressionLevel Optimal
$framework='C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$refs=@('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.IO.Compression.dll','System.Web.Extensions.dll','Microsoft.CSharp.dll') | ForEach-Object { '/r:'+(Join-Path $framework $_) }
& "$framework\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 ('/out:'+$releaseRoot+'\Nook-Setup.exe') ('/win32icon:'+$suiteRoot+'\assets\mori.ico') ('/resource:'+$zip+',MoriDesk.Payload.zip') ('/resource:'+$launcher+',MoriDesk.Launcher.exe') @refs "$suiteRoot\installer\Setup.cs" "$suiteRoot\installer\InstallerProgress.cs" "$suiteRoot\installer\ReplacementInstall.cs" "$suiteRoot\installer\ReplacementStaging.cs" "$suiteRoot\installer\InstalledVersionCleanup.cs" "$suiteRoot\installer\ManualMetadataBackup.cs" "$suiteRoot\src\AppUpdateMetadataBackup.cs" "$suiteRoot\src\InstalledApplicationState.cs" "$suiteRoot\src\InstalledCleanupState.cs" "$suiteRoot\src\OpenWithRegistration.cs" "$suiteRoot\src\FileFormats.cs" "$suiteRoot\src\PhysicalUserContext.cs"
if($LASTEXITCODE -ne 0){throw 'Installer build failed'}
$installerVersion=[System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $releaseRoot 'Nook-Setup.exe')).FileVersion
if([version]$installerVersion -ne [version]$buildVersion){throw 'Installer and application versions do not match. Update both before publishing an installable release.'}
Get-FileHash "$releaseRoot\Nook-Setup.exe",$zip -Algorithm SHA256 | Format-Table
Copy-Item -LiteralPath "$suiteRoot\docs\使用说明.md" -Destination "$releaseRoot\Nook-Guide.txt"
# The updater accepts this exact installer filename and authenticates its hash
# through the GitHub release API. Keep checksums beside, never inside, payloads.
$checksumLines=Get-FileHash "$releaseRoot\Nook-Setup.exe",$zip,"$releaseRoot\Nook-Guide.txt" -Algorithm SHA256 | ForEach-Object {$_.Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($_.Path)}
[IO.File]::WriteAllLines((Join-Path $releaseRoot 'SHA256SUMS.txt'),[string[]]$checksumLines,(New-Object Text.UTF8Encoding($false)))
Write-Output "Release: $releaseRoot"
