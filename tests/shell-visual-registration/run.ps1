param([string]$OutputDirectory,[switch]$RunInVirtualMachine)
$ErrorActionPreference='Stop'
$suiteRoot=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if(!$OutputDirectory){throw 'Provide a fresh output directory. Compilation alone is the default; execution requires -RunInVirtualMachine.'}
$output=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$framework='C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$references=@('System.dll','System.Core.dll','System.Security.dll','System.Management.dll','System.Web.Extensions.dll') | ForEach-Object {'/r:'+(Join-Path $framework $_)}
$sources=@('src\ShellVisualRegistration.cs','src\OpenWithRegistration.cs','src\InstalledApplicationState.cs','src\PhysicalUserContext.cs','src\FileFormats.cs','tests\shell-visual-registration\Tests.cs') | ForEach-Object {Join-Path $suiteRoot $_}
$executable=Join-Path $output 'Mori.exe'
& "$framework\csc.exe" /nologo /target:exe /main:MoriDesk.ShellVisualTests /platform:x64 /codepage:65001 ('/out:'+$executable) @references @sources
if($LASTEXITCODE -ne 0){throw 'Shell registration fixture compilation failed'}
if($RunInVirtualMachine){
 $fixtures=Join-Path $output ('fixtures-'+[Guid]::NewGuid().ToString('N'))
 & $executable --isolated-fixtures $fixtures
 if($LASTEXITCODE -ne 0){throw 'Shell registration fixtures failed'}
}else{Write-Output ('Compiled only. Run in the development VM: "'+$executable+'" --isolated-fixtures <fresh directory>')}
