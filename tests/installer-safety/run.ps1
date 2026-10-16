param([string]$SuiteExecutable,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
$suiteRoot=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if(!$OutputDirectory){$OutputDirectory=Join-Path ([IO.Path]::GetTempPath()) ('Nook-installer-safety-'+[Guid]::NewGuid().ToString('N'))}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$framework='C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$refs=@('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.IO.Compression.dll','System.Web.Extensions.dll','Microsoft.CSharp.dll') | ForEach-Object {'/r:'+(Join-Path $framework $_)}
$testExecutable=Join-Path $OutputDirectory 'InstallerSafetyTests.exe'
& "$framework\csc.exe" /nologo /target:exe /main:MoriDeskInstaller.InstallerSafetyTests /platform:x64 /codepage:65001 ('/out:'+$testExecutable) @refs (Join-Path $suiteRoot 'installer\Setup.cs') (Join-Path $suiteRoot 'src\OpenWithRegistration.cs') (Join-Path $PSScriptRoot 'Tests.cs')
if($LASTEXITCODE -ne 0){throw 'Installer safety test compilation failed'}
if($SuiteExecutable){& $testExecutable $SuiteExecutable}else{& $testExecutable}
if($LASTEXITCODE -ne 0){throw 'Installer safety tests failed'}
