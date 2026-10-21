param(
 [Parameter(Mandatory=$true)][string]$VerifiedFileeSource,
 [Parameter(Mandatory=$true)][string]$EngineDirectory
)
$ErrorActionPreference='Stop'
# Build-only extraction of the pinned official MSI. No normal installation,
# PATH changes or shell integration. The upstream generic MSI helper assumes
# LibreOffice/calibre and cannot currently extract the bundled 7-Zip package.
$suiteRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$outputs=[IO.Path]::GetFullPath((Join-Path $suiteRoot 'outputs')).TrimEnd('\')+'\'
$engineRoot=[IO.Path]::GetFullPath($EngineDirectory)
if(!$engineRoot.StartsWith($outputs,[StringComparison]::OrdinalIgnoreCase)){throw 'EngineDirectory must be inside this workspace outputs directory.'}
for($parent=$engineRoot;$parent;$parent=[IO.Path]::GetDirectoryName($parent)){
 if((Test-Path -LiteralPath $parent) -and ((Get-Item -LiteralPath $parent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Engine path cannot traverse a link.'}
}
$source=[IO.Path]::GetFullPath($VerifiedFileeSource)
$catalog=Get-Content -LiteralPath (Join-Path $source 'src\Filee.Engines\Infrastructure\engines.json') -Raw | ConvertFrom-Json
$component=$catalog.components.'7zip'
if($component.kind -ne 'msi' -or $component.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $component.url -notlike 'https://github.com/ip7z/7zip/releases/download/*'){throw 'Unexpected pinned 7-Zip component.'}
$destination=Join-Path $engineRoot '7zip'
if(Test-Path -LiteralPath $destination){throw '7-Zip destination already exists; it will not be overwritten.'}
$staging=Join-Path $outputs ('7zip-restore-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null
$archive=Join-Path $staging '7zip.msi'
Invoke-WebRequest -UseBasicParsing -Uri $component.url -OutFile $archive
if((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $component.sha256){throw '7-Zip MSI checksum mismatch; extraction not started.'}
$extracted=Join-Path $staging 'files'
New-Item -ItemType Directory -Path $extracted | Out-Null
$log=Join-Path $staging 'extract.log'
$process=Start-Process -FilePath "$env:WINDIR\System32\msiexec.exe" -ArgumentList @('/a',('"'+$archive+'"'),'/qn','/l*v',('"'+$log+'"'),('TARGETDIR="'+$extracted+'"')) -PassThru -WindowStyle Hidden
if(!$process.WaitForExit(300000)){throw ('7-Zip extraction is still running. Retained staging: '+$staging)}
if($process.ExitCode -ne 0){throw ('7-Zip files-only extraction failed: '+$process.ExitCode+'; '+$log)}
$binaries=@(Get-ChildItem -LiteralPath $extracted -File -Filter '7z.exe' -Recurse)
if($binaries.Count -ne 1){throw 'Expected exactly one 7z.exe in official MSI.'}
$program=$binaries[0].Directory.FullName
if(!(Test-Path -LiteralPath (Join-Path $program '7z.dll') -PathType Leaf)){throw '7z.dll missing.'}
if(@(Get-ChildItem -LiteralPath $extracted -Force -Recurse | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Extracted package contains a link.'}
New-Item -ItemType Directory -Path $destination | Out-Null
Get-ChildItem -LiteralPath $program -Force | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $destination -Recurse}
[IO.File]::WriteAllText((Join-Path $destination '.filee-component'),$component.sha256)
Write-Output ('VERIFIED 7zip '+$component.version+'; files extracted only; retained build staging: '+$staging)
