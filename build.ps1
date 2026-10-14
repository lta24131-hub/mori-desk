param([switch]$Test,[switch]$CaptureTests,[string]$OutputDirectory='dist')
$ErrorActionPreference='Stop'
$suiteRoot=$PSScriptRoot
$framework='C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$output=if([IO.Path]::IsPathRooted($OutputDirectory)){[IO.Path]::GetFullPath($OutputDirectory)}else{Join-Path $suiteRoot $OutputDirectory}
New-Item -ItemType Directory -Force $output | Out-Null
Get-ChildItem -LiteralPath "$suiteRoot\dependencies" | Where-Object {$_.Name -notin @('image-codecs','pdf-text','preview-support','Restore-PreviewComponents.ps1','PREVIEW-NOTICES.md')} | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $output -Recurse -Force}
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
$refs=@('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Net.Http.dll','System.Web.Extensions.dll','System.Xaml.dll','System.Xml.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','Microsoft.CSharp.dll','System.Runtime.WindowsRuntime.dll','System.Runtime.InteropServices.WindowsRuntime.dll','System.Runtime.dll','System.ObjectModel.dll','System.Threading.Tasks.dll') | ForEach-Object { '/r:'+(Join-Path $framework $_) }
$refs+=@('PresentationCore.dll','PresentationFramework.dll','WindowsBase.dll','WindowsFormsIntegration.dll') | ForEach-Object { '/r:'+(Join-Path $framework ('WPF\'+$_)) }
$refs+=@('Windows.Foundation.winmd','Windows.Globalization.winmd','Windows.Media.winmd','Windows.Graphics.winmd','Windows.Storage.winmd','Windows.Data.winmd') | ForEach-Object { '/r:'+(Join-Path $env:WINDIR ('System32\WinMetadata\'+$_)) }
$refs+=@('Svg.dll','Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.Wpf.dll','Tesseract.dll') | ForEach-Object { '/r:'+(Join-Path $output $_) }
$refs+=@('Magick.NET.Core.dll','Magick.NET-Q8-x64.dll','UglyToad.PdfPig.dll','UglyToad.PdfPig.Core.dll','UglyToad.PdfPig.DocumentLayoutAnalysis.dll','Microsoft.Bcl.HashCode.dll','System.Memory.dll','System.Buffers.dll','System.Numerics.Vectors.dll','System.Runtime.CompilerServices.Unsafe.dll') | ForEach-Object {'/r:'+(Join-Path $output $_)}
$standardFacade=Join-Path $framework 'netstandard.dll'
if(!(Test-Path -LiteralPath $standardFacade)){$standardFacade=Join-Path $framework 'Facades\netstandard.dll'}
$refs+='/r:'+$standardFacade
$refs+='/r:'+(Join-Path $framework 'System.Xml.Linq.dll')
$sources=@(Get-ChildItem "$suiteRoot\modules\library\src\*.cs"; Get-ChildItem "$suiteRoot\modules\capture\src\*.cs" | Where-Object Name -ne 'AssemblyInfo.cs'; Get-ChildItem "$suiteRoot\src\*.cs"; Get-ChildItem "$suiteRoot\tests\*.cs") | ForEach-Object FullName
$resources=@(
 '/resource:'+$suiteRoot+'\src\BoardPage.xaml,MoriDesk.BoardPage.xaml'
 '/resource:'+$suiteRoot+'\src\ReferencePage.xaml,MoriDesk.ReferencePage.xaml'
 '/resource:'+$suiteRoot+'\modules\library\Main.xaml,Mori.Main.xaml'
 '/resource:'+$suiteRoot+'\modules\library\assets\video-player.html,Mori.Video.html'
 '/resource:'+$suiteRoot+'\assets\nook-256.png,QingJie.Icon.png'
 '/resource:'+$suiteRoot+'\modules\capture\offline\catalog.json,QingJie.OfflineCatalog.json'
 '/resource:'+$suiteRoot+'\modules\capture\offline\worker.py,QingJie.OfflineWorker.py'
)
& "$framework\csc.exe" /nologo /target:winexe /main:MoriDesk.Entry /platform:x64 /optimize+ /codepage:65001 ('/out:'+$output+'\Mori.exe') ('/win32icon:'+$suiteRoot+'\assets\mori.ico') ('/win32manifest:'+$suiteRoot+'\modules\library\app.manifest') @resources @refs @sources
if($LASTEXITCODE -ne 0){throw 'Suite build failed'}
Copy-Item "$suiteRoot\modules\library\App.config" "$output\Mori.exe.config" -Force
Copy-Item -LiteralPath "$suiteRoot\assets\nook-256.png" -Destination "$output\qingjie-icon.png" -Force
New-Item -ItemType Directory -Force "$output\web" | Out-Null
Copy-Item "$suiteRoot\web\model" "$output\web" -Recurse -Force
Copy-Item "$suiteRoot\browser-extension" $output -Recurse -Force
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
