param([string]$BuildDirectory='dist')
$ErrorActionPreference='Stop'
$suiteRoot=$PSScriptRoot
$buildPath=Join-Path $suiteRoot $BuildDirectory
$releaseRoot=Join-Path $suiteRoot ('releases\'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
$payload=Join-Path $releaseRoot 'Mori-Desk'
New-Item -ItemType Directory -Force $payload | Out-Null
$files=@('Mori.exe','Mori.exe.config','ExCSS.dll','Svg.dll','Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.Wpf.dll','WebView2Loader.dll','System.Buffers.dll','System.Memory.dll','System.Numerics.Vectors.dll','System.Runtime.CompilerServices.Unsafe.dll','Tesseract.dll','qingjie-icon.png')
foreach($file in $files){Copy-Item -LiteralPath (Join-Path $buildPath $file) -Destination $payload}
foreach($folder in @('x64','tessdata','web','browser-extension','svg-licenses','webview2-licenses','model-licenses')){Copy-Item -LiteralPath (Join-Path $buildPath $folder) -Destination $payload -Recurse}
Copy-Item "$suiteRoot\modules\capture\licenses" "$payload\capture-licenses" -Recurse
Copy-Item "$suiteRoot\modules\capture\THIRD-PARTY-NOTICES.md" "$payload\CAPTURE-NOTICES.md"
Copy-Item "$suiteRoot\README.md","$suiteRoot\Register-Extension.ps1" $payload
Copy-Item "$suiteRoot\docs" "$payload\docs" -Recurse
Copy-Item "$suiteRoot\RELEASE-NOTES.md" $payload
Copy-Item -LiteralPath "$suiteRoot\docs\使用说明.md" -Destination "$payload\使用说明.txt"
$zip=Join-Path $releaseRoot 'Mori-Desk-0.1.1-preview-portable.zip'
Compress-Archive -Path "$payload\*" -DestinationPath $zip -CompressionLevel Optimal
$framework='C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$refs=@('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.IO.Compression.dll','Microsoft.CSharp.dll') | ForEach-Object { '/r:'+(Join-Path $framework $_) }
& "$framework\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 ('/out:'+$releaseRoot+'\Mori-Desk-Setup.exe') ('/win32icon:'+$suiteRoot+'\assets\mori.ico') ('/resource:'+$zip+',MoriDesk.Payload.zip') @refs "$suiteRoot\installer\Setup.cs"
if($LASTEXITCODE -ne 0){throw 'Installer build failed'}
Get-FileHash "$releaseRoot\Mori-Desk-Setup.exe",$zip -Algorithm SHA256 | Format-Table
Copy-Item -LiteralPath "$suiteRoot\docs\使用说明.md" -Destination "$releaseRoot\Mori-Desk-Guide.txt"
Write-Output "Release: $releaseRoot"
