param([switch]$Test,[switch]$CaptureTests,[string]$OutputDirectory='dist')
$ErrorActionPreference='Stop'
$suiteRoot=$PSScriptRoot
$framework='C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$output=Join-Path $suiteRoot $OutputDirectory
New-Item -ItemType Directory -Force $output | Out-Null
Copy-Item "$suiteRoot\dependencies\*" $output -Recurse -Force
$refs=@('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Net.Http.dll','System.Web.Extensions.dll','System.Xaml.dll','System.Xml.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','Microsoft.CSharp.dll','System.Runtime.WindowsRuntime.dll','System.Runtime.InteropServices.WindowsRuntime.dll','System.Runtime.dll','System.ObjectModel.dll','System.Threading.Tasks.dll') | ForEach-Object { '/r:'+(Join-Path $framework $_) }
$refs+=@('PresentationCore.dll','PresentationFramework.dll','WindowsBase.dll','WindowsFormsIntegration.dll') | ForEach-Object { '/r:'+(Join-Path $framework ('WPF\'+$_)) }
$refs+=@('Windows.Foundation.winmd','Windows.Globalization.winmd','Windows.Media.winmd','Windows.Graphics.winmd','Windows.Storage.winmd') | ForEach-Object { '/r:'+(Join-Path $env:WINDIR ('System32\WinMetadata\'+$_)) }
$refs+=@('Svg.dll','Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.Wpf.dll','Tesseract.dll') | ForEach-Object { '/r:'+(Join-Path $output $_) }
$sources=@(Get-ChildItem "$suiteRoot\modules\library\src\*.cs"; Get-ChildItem "$suiteRoot\modules\capture\src\*.cs" | Where-Object Name -ne 'AssemblyInfo.cs'; Get-ChildItem "$suiteRoot\src\*.cs"; Get-ChildItem "$suiteRoot\tests\*.cs") | ForEach-Object FullName
$resources=@(
 '/resource:'+$suiteRoot+'\src\BoardPage.xaml,MoriDesk.BoardPage.xaml'
 '/resource:'+$suiteRoot+'\src\ReferencePage.xaml,MoriDesk.ReferencePage.xaml'
 '/resource:'+$suiteRoot+'\modules\library\Main.xaml,Mori.Main.xaml'
 '/resource:'+$suiteRoot+'\modules\library\assets\video-player.html,Mori.Video.html'
 '/resource:'+$suiteRoot+'\dependencies\qingjie-icon.png,QingJie.Icon.png'
 '/resource:'+$suiteRoot+'\modules\capture\offline\catalog.json,QingJie.OfflineCatalog.json'
 '/resource:'+$suiteRoot+'\modules\capture\offline\worker.py,QingJie.OfflineWorker.py'
)
& "$framework\csc.exe" /nologo /target:winexe /main:MoriDesk.Entry /platform:x64 /optimize+ /codepage:65001 ('/out:'+$output+'\Mori.exe') ('/win32icon:'+$suiteRoot+'\assets\mori.ico') ('/win32manifest:'+$suiteRoot+'\modules\library\app.manifest') @resources @refs @sources
if($LASTEXITCODE -ne 0){throw 'Suite build failed'}
Copy-Item "$suiteRoot\modules\library\App.config" "$output\Mori.exe.config" -Force
New-Item -ItemType Directory -Force "$output\web" | Out-Null
Copy-Item "$suiteRoot\web\model" "$output\web" -Recurse -Force
Copy-Item "$suiteRoot\browser-extension" $output -Recurse -Force
if($Test){$testProcess=Start-Process "$output\Mori.exe" -ArgumentList '--suite-test' -Wait -PassThru -WindowStyle Hidden;if($testProcess.ExitCode -ne 0){throw 'Suite tests failed'}}
if($CaptureTests){
 $captureTestSources=Get-ChildItem "$suiteRoot\modules\capture\tests\*.cs" | ForEach-Object FullName
 & "$framework\csc.exe" /nologo /target:exe /platform:x64 /codepage:65001 ('/out:'+$output+'\QingJie.Tests.exe') @refs ('/r:'+$output+'\Mori.exe') @captureTestSources
 if($LASTEXITCODE -ne 0){throw 'Capture test compilation failed'}
 $captureProcess=Start-Process "$output\QingJie.Tests.exe" -PassThru -WindowStyle Hidden -RedirectStandardOutput "$output\capture-tests.txt" -RedirectStandardError "$output\capture-errors.txt" -Wait
 if($captureProcess.ExitCode -ne 0){throw 'Capture regression tests failed; see capture-tests.txt'}
}
Write-Output "Built $output\Mori.exe"
