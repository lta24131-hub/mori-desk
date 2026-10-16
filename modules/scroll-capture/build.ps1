param(
 [string]$UpstreamDirectory,
 [string]$OutputDirectory,
 [string]$NodePath='node',
 [string]$PnpmPath='pnpm',
 [switch]$SkipRestore
)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$commit='6f3f56241cccc8ada2c503df642566322dc67a3e'
if(!$UpstreamDirectory){$UpstreamDirectory=Join-Path $repoRoot 'outputs\esearch-15.6.0-source'}
if(!$OutputDirectory){$OutputDirectory=Join-Path $repoRoot ('outputs\scroll-helper-'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss'))}
$upstream=[IO.Path]::GetFullPath($UpstreamDirectory)
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Use a new OutputDirectory; this build never replaces an existing runtime.'}
if(!(Test-Path -LiteralPath $upstream)){
 & git clone --branch 15.6.0 --depth 1 https://github.com/xushengfeng/eSearch.git $upstream
 if($LASTEXITCODE -ne 0){throw 'eSearch source restore failed'}
}
$actual=(& git -C $upstream rev-parse HEAD).Trim()
if($LASTEXITCODE -ne 0 -or $actual -ne $commit){throw 'Unexpected eSearch revision; the capture patches require the pinned source.'}
$changed=@(& git -C $upstream status --porcelain --untracked-files=no)
if($LASTEXITCODE -ne 0 -or $changed.Count -ne 0){throw 'Upstream tracked source was modified. Use a clean pinned checkout.'}
Push-Location $upstream
try{
 if(!$SkipRestore){
  # Dependency lifecycle scripts are deliberately disabled. The full eSearch
  # beforePack hook downloads unrelated OCR/segmentation/FFmpeg components.
  if([IO.Path]::GetExtension($PnpmPath) -eq '.mjs') { & $NodePath $PnpmPath install --frozen-lockfile --ignore-scripts }
  else { & $PnpmPath install --frozen-lockfile --ignore-scripts }
  if($LASTEXITCODE -ne 0){throw 'Locked dependencies could not be restored'}
 }
 # Only the audited runtime downloader is allowed: Electron's packaged
 # checksums verify the archive. No application or input hooks are run.
 & $NodePath (Join-Path $PSScriptRoot 'restore-electron.mjs') $upstream
 if($LASTEXITCODE -ne 0){throw 'Electron runtime restore failed'}
}finally{Pop-Location}
$stage=Join-Path $repoRoot ('outputs\scroll-helper-stage-'+[Guid]::NewGuid().ToString('N'))
& $NodePath (Join-Path $PSScriptRoot 'host\prepare.mjs') --upstream $upstream --stage $stage
if($LASTEXITCODE -ne 0){throw 'Capture source adaptation failed'}
New-Item -ItemType Junction -Path (Join-Path $stage 'node_modules') -Target (Join-Path $upstream 'node_modules') | Out-Null
Push-Location $stage
try{
 & $NodePath (Join-Path $upstream 'node_modules\electron-vite\bin\electron-vite.js') build
 if($LASTEXITCODE -ne 0){throw 'Capture-only Electron build failed'}
}finally{Pop-Location}
& $NodePath (Join-Path $PSScriptRoot 'package-runtime.mjs') --upstream $upstream --stage $stage --output $output
if($LASTEXITCODE -ne 0){throw 'Capture runtime packaging failed'}
Write-Output ('SCROLL_CAPTURE_RUNTIME='+$output)
Write-Output ('SCROLL_CAPTURE_STAGE='+$stage)
