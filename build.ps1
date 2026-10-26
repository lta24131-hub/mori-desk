param([switch]$Test,[switch]$CaptureTests,[string]$OutputDirectory='dist',[string]$ScrollCaptureDirectory,[string]$FileConversionDirectory,[string]$ShellPreviewDirectory)
$ErrorActionPreference='Stop'
$suiteRoot=$PSScriptRoot
$framework='C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$output=if([IO.Path]::IsPathRooted($OutputDirectory)){[IO.Path]::GetFullPath($OutputDirectory)}else{Join-Path $suiteRoot $OutputDirectory}
New-Item -ItemType Directory -Force $output | Out-Null
Get-ChildItem -LiteralPath "$suiteRoot\dependencies" | Where-Object {$_.Name -notin @('image-codecs','pdf-text','preview-support','scroll-capture','Restore-PreviewComponents.ps1','PREVIEW-NOTICES.md')} | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $output -Recurse -Force}
foreach($component in @('image-codecs','pdf-text','preview-support')){
 $componentPath=Join-Path "$suiteRoot\dependencies" $component
 if(!(Test-Path -LiteralPath $componentPath)){throw 'Preview components missing. Run dependencies\Restore-PreviewComponents.ps1 first.'}
 foreach($required in @('LICENSE.txt')){if(!(Test-Path -LiteralPath (Join-Path $componentPath $required))){throw ('Preview component license missing: '+$component+'\'+$required)}}
 Get-ChildItem -LiteralPath $componentPath -Filter '*.dll' | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $output -Force}
 $noticePath=Join-Path $output $component
 New-Item -ItemType Directory -Force $noticePath | Out-Null
 Get-ChildItem -LiteralPath $componentPath -File | Where-Object {$_.Extension -in @('.txt','.nuspec')} | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $noticePath -Force}
 if(Test-Path -LiteralPath (Join-Path $componentPath 'config')){Copy-Item -LiteralPath (Join-Path $componentPath 'config') -Destination $noticePath -Recurse -Force}
}
Copy-Item -LiteralPath "$suiteRoot\dependencies\PREVIEW-NOTICES.md" -Destination $output -Force
$refs=@('System.dll','System.Core.dll','System.Security.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Net.Http.dll','System.Web.Extensions.dll','System.Xaml.dll','System.Xml.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','Microsoft.CSharp.dll','System.Runtime.WindowsRuntime.dll','System.Runtime.InteropServices.WindowsRuntime.dll','System.Runtime.dll','System.ObjectModel.dll','System.Threading.Tasks.dll') | ForEach-Object { '/r:'+(Join-Path $framework $_) }
$refs+=@('PresentationCore.dll','PresentationFramework.dll','WindowsBase.dll','WindowsFormsIntegration.dll') | ForEach-Object { '/r:'+(Join-Path $framework ('WPF\'+$_)) }
$refs+=@('Windows.Foundation.winmd','Windows.Globalization.winmd','Windows.Media.winmd','Windows.Graphics.winmd','Windows.Storage.winmd','Windows.Data.winmd') | ForEach-Object { '/r:'+(Join-Path $env:WINDIR ('System32\WinMetadata\'+$_)) }
$refs+=@('Svg.dll','Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.Wpf.dll','Tesseract.dll') | ForEach-Object { '/r:'+(Join-Path $output $_) }
$refs+=@('Magick.NET.Core.dll','Magick.NET-Q8-x64.dll','UglyToad.PdfPig.dll','UglyToad.PdfPig.Core.dll','UglyToad.PdfPig.DocumentLayoutAnalysis.dll','Microsoft.Bcl.HashCode.dll','System.Memory.dll','System.Buffers.dll','System.Numerics.Vectors.dll','System.Runtime.CompilerServices.Unsafe.dll') | ForEach-Object {'/r:'+(Join-Path $output $_)}
$standardFacade=Join-Path $framework 'netstandard.dll'
if(!(Test-Path -LiteralPath $standardFacade)){$standardFacade=Join-Path $framework 'Facades\netstandard.dll'}
$refs+='/r:'+$standardFacade
$refs+='/r:'+(Join-Path $framework 'System.Xml.Linq.dll')
$sources=@(Get-ChildItem "$suiteRoot\modules\library\src\*.cs"; Get-ChildItem "$suiteRoot\modules\capture\src\*.cs" | Where-Object Name -ne 'AssemblyInfo.cs'; Get-ChildItem "$suiteRoot\src\*.cs"; Get-ChildItem "$suiteRoot\tests\*.cs") | ForEach-Object FullName
$sources+=Join-Path $suiteRoot 'installer\InstalledVersionCleanup.cs'
$resources=@(
 '/resource:'+$suiteRoot+'\assets\legacy-installed-payloads.json,Nook.LegacyInstalledPayloads.json'
 '/resource:'+$suiteRoot+'\src\BoardPage.xaml,MoriDesk.BoardPage.xaml'
 '/resource:'+$suiteRoot+'\src\ReferencePage.xaml,MoriDesk.ReferencePage.xaml'
 '/resource:'+$suiteRoot+'\modules\library\Main.xaml,Mori.Main.xaml'
 '/resource:'+$suiteRoot+'\modules\library\assets\video-player.html,Mori.Video.html'
 '/resource:'+$suiteRoot+'\assets\nook-256.png,QingJie.Icon.png'
 '/resource:'+$suiteRoot+'\modules\capture\offline\catalog.json,QingJie.OfflineCatalog.json'
 '/resource:'+$suiteRoot+'\modules\capture\offline\worker.py,QingJie.OfflineWorker.py'
)
$compilerArgs=@('/nologo','/target:winexe','/main:MoriDesk.Entry','/platform:x64','/optimize+','/codepage:65001',('/out:'+$output+'\Mori.exe'),('/win32icon:'+$suiteRoot+'\assets\mori.ico'),('/win32manifest:'+$suiteRoot+'\modules\library\app.manifest'))+$resources+$refs+$sources
# A response file avoids Windows' command-line limit as the suite grows.
# It is build-local and is not part of the release payload allowlist.
$responseFile=Join-Path $output 'compile.rsp'
[IO.File]::WriteAllLines($responseFile,[string[]]($compilerArgs | ForEach-Object {'"'+$_+'"'}),(New-Object Text.UTF8Encoding($false)))
& "$framework\csc.exe" ('@'+$responseFile)
if($LASTEXITCODE -ne 0){throw 'Suite build failed'}
Copy-Item "$suiteRoot\modules\library\App.config" "$output\Mori.exe.config" -Force
Copy-Item -LiteralPath "$suiteRoot\assets\nook-256.png" -Destination "$output\qingjie-icon.png" -Force
New-Item -ItemType Directory -Force "$output\web" | Out-Null
Copy-Item "$suiteRoot\web\model" "$output\web" -Recurse -Force
if(Test-Path -LiteralPath "$suiteRoot\web\canvas\index.html"){
 Copy-Item -LiteralPath "$suiteRoot\web\canvas" -Destination "$output\web" -Recurse -Force
}
Copy-Item "$suiteRoot\browser-extension" $output -Recurse -Force
if($ScrollCaptureDirectory){
 $scrollRoot=[IO.Path]::GetFullPath($ScrollCaptureDirectory)
 foreach($required in @('Nook.ScrollCapture.exe','component.json','LICENSE-eSearch.txt','NOTICES.md','corresponding-source','resources\app\out\main\main.js')){
  if(!(Test-Path -LiteralPath (Join-Path $scrollRoot $required))){throw ('Incomplete scroll capture component: '+$required)}
 }
 $scrollManifest=Get-Content -LiteralPath (Join-Path $scrollRoot 'component.json') -Raw | ConvertFrom-Json
 if($scrollManifest.schemaVersion -ne 1 -or $scrollManifest.upstreamCommit -ne '6f3f56241cccc8ada2c503df642566322dc67a3e'){throw 'Unexpected scroll capture component revision'}
 $scrollHash=(Get-FileHash -LiteralPath (Join-Path $scrollRoot 'resources\app\out\main\main.js') -Algorithm SHA256).Hash
 if($scrollHash -ne $scrollManifest.applicationSha256){throw 'Scroll capture component integrity check failed'}
 $scrollTarget=Join-Path $output 'scroll-capture'
 if(Test-Path -LiteralPath $scrollTarget){throw 'Use a fresh build directory when including scroll capture; do not merge runtime versions.'}
 New-Item -ItemType Directory -Path $scrollTarget | Out-Null
 Get-ChildItem -LiteralPath $scrollRoot | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $scrollTarget -Recurse}
}else{Write-Warning 'Capture helper omitted from this development build. Release packaging requires -ScrollCaptureDirectory.'}
if($FileConversionDirectory){
 $conversionRoot=[IO.Path]::GetFullPath($FileConversionDirectory)
 foreach($required in @('Nook.FileConversion.exe','Nook.FileConversion.dll','Nook.FileConversion.runtimeconfig.json','component.json','upstream.json','licenses\LICENSE-Filee.txt','licenses\THIRD-PARTY-NOTICES.md')){
  if(!(Test-Path -LiteralPath (Join-Path $conversionRoot $required) -PathType Leaf)){throw ('Incomplete conversion component: '+$required)}
 }
 $conversionManifest=Get-Content -LiteralPath (Join-Path $conversionRoot 'component.json') -Raw | ConvertFrom-Json
 if($conversionManifest.schemaVersion -ne 1 -or $conversionManifest.component -ne 'file-conversion' -or $conversionManifest.upstreamCommit -ne '49911afab6eef81f144543710f1aa8021c617ff1'){throw 'Unexpected conversion component revision'}
 . (Join-Path $suiteRoot 'modules\file-conversion\Validate-Component.ps1')
 Assert-ConversionEngineInventory $conversionRoot $conversionManifest
 foreach($item in @(@('Nook.FileConversion.exe','applicationSha256'),@('Nook.FileConversion.dll','applicationAssemblySha256'))){
  if((Get-FileHash -LiteralPath (Join-Path $conversionRoot $item[0]) -Algorithm SHA256).Hash -ne $conversionManifest.($item[1])){throw ('Conversion component integrity check failed: '+$item[0])}
 }
 $conversionTarget=Join-Path $output 'file-conversion'
 if(Test-Path -LiteralPath $conversionTarget){throw 'Use a fresh build directory when including file conversion; do not merge runtime versions.'}
 New-Item -ItemType Directory -Path $conversionTarget | Out-Null
 Get-ChildItem -LiteralPath $conversionRoot | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $conversionTarget -Recurse}
}else{
 if(Test-Path -LiteralPath (Join-Path $output 'file-conversion')){throw 'An old conversion component is present. Use a fresh output directory or explicitly supply -FileConversionDirectory.'}
 Write-Warning 'Experimental file conversion component omitted. Tools will explain its unavailability without downloading or starting an engine.'
}
if($ShellPreviewDirectory){
 . (Join-Path $suiteRoot 'modules\shell-preview\Validate-Component.ps1')
 $shellSource=[IO.Path]::GetFullPath($ShellPreviewDirectory)
 Assert-ShellPreviewComponent $shellSource
 $shellTarget=Join-Path $output 'shell-preview'
 if(Test-Path -LiteralPath $shellTarget){throw 'Use a fresh output directory for shell previews.'}
 Copy-Item -LiteralPath $shellSource -Destination $shellTarget -Recurse
 Assert-ShellPreviewComponent $shellTarget
}else{Write-Warning 'Shell previews omitted from this development build. Release packaging requires -ShellPreviewDirectory.'}
if($Test){$testProcess=Start-Process "$output\Mori.exe" -ArgumentList '--suite-test' -Wait -PassThru -WindowStyle Hidden;if($testProcess.ExitCode -ne 0){throw 'Suite tests failed'}}
if($CaptureTests){
 $captureTestSources=Get-ChildItem "$suiteRoot\modules\capture\tests\*.cs" | ForEach-Object FullName
 & "$framework\csc.exe" /nologo /target:exe /platform:x64 /codepage:65001 ('/out:'+$output+'\QingJie.Tests.exe') @refs ('/r:'+$output+'\Mori.exe') @captureTestSources
 if($LASTEXITCODE -ne 0){throw 'Capture test compilation failed'}
 Copy-Item -LiteralPath "$output\Mori.exe.config" -Destination "$output\QingJie.Tests.exe.config" -Force
 $captureProcess=Start-Process "$output\QingJie.Tests.exe" -PassThru -WindowStyle Hidden -RedirectStandardOutput "$output\capture-tests.txt" -RedirectStandardError "$output\capture-errors.txt" -Wait
 if($captureProcess.ExitCode -ne 0){throw 'Capture regression tests failed; see capture-tests.txt'}
}
Write-Output "Built $output\Mori.exe"
