param([string]$BuildDirectory='dist')
$ErrorActionPreference='Stop'
$suiteRoot=$PSScriptRoot
$buildPath=if([IO.Path]::IsPathRooted($BuildDirectory)){$BuildDirectory}else{Join-Path $suiteRoot $BuildDirectory}
function Assert-NoMachineSecrets([string]$PathToCheck){
 $privateFiles=@(Get-ChildItem -LiteralPath $PathToCheck -File -Recurse | Where-Object { $_.Name -like 'local-connection.js*' -or $_.Name -like 'local-key.txt*' -or $_.Name -eq 'github-token.dat' -or $_.Name -eq 'pairing.json' })
 if($privateFiles.Count -gt 0){throw 'This build contains a machine pairing key or a pairing temporary file. Use a fresh build directory to package a release.'}
}
Assert-NoMachineSecrets $buildPath
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
& "$framework\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 ('/out:'+$releaseRoot+'\Nook-Setup.exe') ('/win32icon:'+$suiteRoot+'\assets\mori.ico') ('/resource:'+$zip+',MoriDesk.Payload.zip') @refs "$suiteRoot\installer\Setup.cs" "$suiteRoot\src\OpenWithRegistration.cs"
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
