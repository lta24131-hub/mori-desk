param([string]$SuiteExecutable,[string]$OutputDirectory,[switch]$CompileOnly,[switch]$RunInVirtualMachine)
$ErrorActionPreference='Stop'
if($CompileOnly -and $RunInVirtualMachine){throw 'Choose compilation only or VM execution, not both.'}
$suiteRoot=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if(!$OutputDirectory){$OutputDirectory=Join-Path ([IO.Path]::GetTempPath()) ('Nook-installer-safety-'+[Guid]::NewGuid().ToString('N'))}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$framework='C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$refs=@('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.IO.Compression.dll','System.Web.Extensions.dll','Microsoft.CSharp.dll') | ForEach-Object {'/r:'+(Join-Path $framework $_)}
$testExecutable=Join-Path $OutputDirectory 'InstallerSafetyTests.exe'
# Keep this list aligned with the installer compilation in package.ps1: the
# tests exercise the real shared implementation, not a reduced stale subset.
$sources=@(
 'installer\Setup.cs','installer\InstallerProgress.cs','installer\ReplacementInstall.cs',
 'installer\ReplacementStaging.cs','installer\InstalledVersionCleanup.cs','installer\ManualMetadataBackup.cs',
 'src\AppUpdateMetadataBackup.cs','src\InstalledApplicationState.cs','src\InstalledCleanupState.cs',
 'src\OpenWithRegistration.cs','src\ShellVisualRegistration.cs','src\FileFormats.cs','src\PhysicalUserContext.cs',
 'tests\installer-safety\Tests.cs'
) | ForEach-Object {Join-Path $suiteRoot $_}
& "$framework\csc.exe" /nologo /target:exe /main:MoriDeskInstaller.InstallerSafetyTests /platform:x64 /codepage:65001 ('/out:'+$testExecutable) @refs @sources
if($LASTEXITCODE -ne 0){throw 'Installer safety test compilation failed'}
if($RunInVirtualMachine){
 # Even an accidental execution switch on the build host must not run tests
 # against the user's physical session. This suite belongs to the test guest.
 $hardware=Get-CimInstance -ClassName Win32_ComputerSystem
 $identity=([string]$hardware.Manufacturer)+' '+([string]$hardware.Model)
 if($identity -notmatch 'QEMU|KVM|Bochs' -or [Environment]::UserName -ne 'NookTester'){
  throw 'Execution is restricted to the dedicated NookTester development VM. Compile-only is the default.'
 }
 if($SuiteExecutable){& $testExecutable ([IO.Path]::GetFullPath($SuiteExecutable))}else{& $testExecutable}
 if($LASTEXITCODE -ne 0){throw 'Installer safety tests failed'}
}else{Write-Output ('Compiled only; no tests executed: '+$testExecutable)}
