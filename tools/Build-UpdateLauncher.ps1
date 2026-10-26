param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$suiteRoot=Split-Path $PSScriptRoot -Parent
$target=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $target){throw 'Use a fresh directory. Protocol 1 bootstrap artifacts must be frozen and reused, not rebuilt for each release.'}
New-Item -ItemType Directory -Path $target | Out-Null
$framework='C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$files=@('installer\LauncherAssemblyInfo.cs','src\InstalledApplicationState.cs','src\InstalledApplicationLauncher.cs','src\PhysicalUserContext.cs')
$sources=$files | ForEach-Object {Join-Path $suiteRoot $_}
$refs=@('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Windows.Forms.dll') | ForEach-Object {'/r:'+(Join-Path $framework $_)}
$executable=Join-Path $target 'Mori.exe'
& "$framework\csc.exe" /nologo /target:winexe /main:MoriDesk.InstalledApplicationLauncher /platform:x64 /optimize+ /codepage:65001 ('/out:'+$executable) ('/win32icon:'+(Join-Path $suiteRoot 'assets\mori.ico')) @refs @sources
if($LASTEXITCODE -ne 0){throw 'Stable launcher build failed'}
$record=[ordered]@{schemaVersion=1;protocolVersion=1;sha256=(Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash;compilerSha256=(Get-FileHash -LiteralPath "$framework\csc.exe" -Algorithm SHA256).Hash;sources=@{}}
foreach($file in $files){$record.sources[$file]=(Get-FileHash -LiteralPath (Join-Path $suiteRoot $file) -Algorithm SHA256).Hash}
[IO.File]::WriteAllText((Join-Path $target 'launcher-provenance.json'),($record|ConvertTo-Json -Depth 5),(New-Object Text.UTF8Encoding($false)))
Write-Output 'TEST CANDIDATE ONLY. Validate native pipes, launch routing and migration in the VM before freezing this bootstrap for releases.'
Write-Output $target
