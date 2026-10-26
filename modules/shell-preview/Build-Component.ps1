param(
 [Parameter(Mandatory=$true)][string]$Compiler,
 [Parameter(Mandatory=$true)][string]$RendererDirectory,
 [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Use a fresh shell component output directory.'}
$renderer=[IO.Path]::GetFullPath($RendererDirectory)
$pins=@{
 'resvg.exe'='266b45d69cd80aafbd7ff2b6424a6a6c2e476888a535a4ce38d5a055bed95624'
 'licenses\LICENSE-MIT.txt'='f5d934dc281b44e0003ee461ac740b18b6629a454decd872c774d34e4ee0b21d'
 'licenses\LICENSE-APACHE.txt'='a6cba85bc92e0cff7a450b1d873c0eaa2e9fc96bf472df0247a26bec77bf3ff9'
}
foreach($relative in $pins.Keys){if((Get-FileHash -LiteralPath (Join-Path $renderer $relative) -Algorithm SHA256).Hash -ne $pins[$relative]){throw ('Pinned resvg input mismatch: '+$relative)}}
New-Item -ItemType Directory -Path $output | Out-Null
$native=Join-Path $PSScriptRoot 'native'
& $Compiler c++ -target x86_64-windows-gnu -std=c++17 -fms-extensions -DUNICODE -D_UNICODE -shared -O2 -g0 -s -Wall -Wextra (Join-Path $native 'NookShellPreview.cpp') (Join-Path $native 'NookShellPreview.def') -o (Join-Path $output 'Nook.ShellPreview.dll') -lole32 -lshell32 -lshlwapi -lwindowscodecs -lxmllite -ladvapi32 -luser32 -lgdi32 -luuid
if($LASTEXITCODE -ne 0){throw 'Shell preview native compilation failed.'}
# The compiler may emit a development symbol file even for an optimized DLL.
# It is never a runtime dependency and must not enter the component inventory.
$symbols=Join-Path $output 'Nook.ShellPreview.pdb'
if(Test-Path -LiteralPath $symbols){Remove-Item -LiteralPath $symbols}
$importLibrary=Join-Path $output 'NookShellPreview.lib'
if(Test-Path -LiteralPath $importLibrary){Remove-Item -LiteralPath $importLibrary}
foreach($relative in $pins.Keys){$target=Join-Path $output $relative; New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null; Copy-Item -LiteralPath (Join-Path $renderer $relative) -Destination $target}
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'licenses') | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $output 'licenses') -Recurse}
New-Item -ItemType Directory -Path (Join-Path $output 'icons') | Out-Null
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'icons') -Filter '*.ico' -File | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $output 'icons')}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'NOTICES.md') -Destination $output
$utf8=New-Object Text.UTF8Encoding($false)
$upstream=[ordered]@{name='resvg';version='0.49.0';source='https://github.com/linebender/resvg/tree/v0.49.0';archive='https://github.com/linebender/resvg/releases/download/v0.49.0/resvg-win-x86_64.zip';archiveSha256='38af9631b37e61118a32a5ef31e6be6af083eda63b4c17ffc4fdc63e449277df';executableSha256=$pins['resvg.exe']}
[IO.File]::WriteAllText((Join-Path $output 'upstream.json'),($upstream | ConvertTo-Json),$utf8)
$files=@(Get-ChildItem -LiteralPath $output -Recurse -File | Sort-Object FullName | ForEach-Object {[ordered]@{path=$_.FullName.Substring($output.Length+1).Replace('\','/');length=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})
[IO.File]::WriteAllText((Join-Path $output 'manifest.json'),([ordered]@{schemaVersion=1;componentVersion='1';files=$files} | ConvertTo-Json -Depth 6),$utf8)
. (Join-Path $PSScriptRoot 'Validate-Component.ps1')
Assert-ShellPreviewComponent $output
Write-Output ('Built shell component: '+$output)
