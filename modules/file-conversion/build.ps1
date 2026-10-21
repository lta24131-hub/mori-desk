param(
 [Parameter(Mandatory=$true)][string]$DotnetPath,
 [string]$CacheDirectory,
 [string]$OutputDirectory,
 [string]$BundledEngineDirectory
)
$ErrorActionPreference='Stop'
$moduleRoot=$PSScriptRoot
$suiteRoot=[IO.Path]::GetFullPath((Join-Path $moduleRoot '..\..'))
if(!$CacheDirectory){$CacheDirectory=Join-Path $suiteRoot 'outputs\file-conversion-build'}
$cache=[IO.Path]::GetFullPath($CacheDirectory)
if(!$OutputDirectory){$OutputDirectory=Join-Path $cache ('worker-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))}
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'OutputDirectory must not already exist; this script never overwrites a build.'}
if(!(Test-Path -LiteralPath $DotnetPath -PathType Leaf)){throw 'Provide a .NET 10 SDK dotnet.exe path; no system installation is performed.'}
$bundledEngines=@()
if($BundledEngineDirectory){
 $engineRoot=[IO.Path]::GetFullPath($BundledEngineDirectory)
 if(!(Test-Path -LiteralPath $engineRoot -PathType Container)){throw 'BundledEngineDirectory must be an explicitly provisioned component directory.'}
 $allowedEngines=@('ffmpeg','libreoffice','pandoc','ghostscript','calibre','rhwp','7zip','vcruntime','h2orestart','jre')
 foreach($entry in Get-ChildItem -LiteralPath $engineRoot -Directory -Force){
  if($entry.Name -eq '.downloads'){continue}
  if($allowedEngines -notcontains $entry.Name){throw ('Unknown bundled engine directory: '+$entry.Name)}
  if(($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Bundled engines must not be reparse points.'}
  foreach($child in Get-ChildItem -LiteralPath $entry.FullName -Recurse -Force){
   if(($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Bundled engine contents must not be reparse points.'}
  }
  $bundledEngines+=$entry
 }
}
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$pin=Get-Content -LiteralPath (Join-Path $moduleRoot 'upstream.json') -Raw | ConvertFrom-Json
$archive=Join-Path $cache ($pin.commit+'.zip')
if(!(Test-Path -LiteralPath $archive)){
 Invoke-WebRequest -UseBasicParsing -Uri $pin.archiveUrl -OutFile $archive
}
if((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $pin.archiveSha256){
 throw 'Filee source archive checksum mismatch. Nothing was built or executed.'
}
# Extract a fresh copy. No upstream build script, solution, app, installer or hooks run.
$buildRoot=Join-Path $cache ('build-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $buildRoot | Out-Null
Expand-Archive -LiteralPath $archive -DestinationPath $buildRoot
$source=Join-Path $buildRoot ('Filee-'+$pin.commit)
$enginePins=(Get-Content -LiteralPath (Join-Path $source 'src\Filee.Engines\Infrastructure\engines.json') -Raw | ConvertFrom-Json).components
$engineInventory=@()
foreach($engine in $bundledEngines){
 $enginePin=$enginePins.($engine.Name)
 $markerPath=Join-Path $engine.FullName '.filee-component'
 if(!$enginePin -or !(Test-Path -LiteralPath $markerPath -PathType Leaf)){throw ('Missing pinned engine provenance: '+$engine.Name)}
 $marker=(Get-Content -LiteralPath $markerPath -Raw).Trim()
 if($marker -ne $enginePin.sha256){throw ('Engine package marker does not match the pinned archive: '+$engine.Name)}
}
$projects=@('Directory.Build.props','Directory.Packages.props','src\Filee.Core\Filee.Core.csproj','src\Filee.Engines\Filee.Engines.csproj')
foreach($relative in $projects){
 $text=Get-Content -LiteralPath (Join-Path $source $relative) -Raw
 if($text -match '<\s*(Exec|UsingTask|Import|Target)\b'){throw ('Unexpected executable build configuration in '+$relative)}
}
if(Get-ChildItem -LiteralPath $source -Recurse -File -Filter '*.targets'){throw 'Unreviewed upstream MSBuild targets.'}
& (Join-Path $moduleRoot 'Patch-Upstream.ps1') -SourceRoot $source
$saved=@{}
foreach($name in @('DOTNET_CLI_HOME','DOTNET_SKIP_FIRST_TIME_EXPERIENCE','DOTNET_CLI_TELEMETRY_OPTOUT','NUGET_PACKAGES','DOTNET_NOLOGO')){
 $saved[$name]=[Environment]::GetEnvironmentVariable($name)
}
try{
 $env:DOTNET_CLI_HOME=Join-Path $cache 'dotnet-home'
 $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
 $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
 $env:DOTNET_NOLOGO='1'
 $env:NUGET_PACKAGES=Join-Path $cache 'nuget'
 $project=Join-Path $moduleRoot 'Nook.FileConversion.csproj'
 & $DotnetPath restore $project --configfile (Join-Path $moduleRoot 'nuget.config') --runtime win-x64 "-p:FileeSourceRoot=$source"
 if($LASTEXITCODE -ne 0){throw 'Worker dependency restore failed.'}
 $rawPublish=Join-Path $buildRoot 'publish'
 & $DotnetPath publish $project -c Release -r win-x64 --self-contained true --no-restore "-p:FileeSourceRoot=$source" -o $rawPublish
 if($LASTEXITCODE -ne 0){throw 'Worker build failed.'}
 # Native NuGet packages include large debugging symbols. Keep them in the build
 # directory, not in the deployable component. Never remove an existing output.
 New-Item -ItemType Directory -Path $output | Out-Null
 Get-ChildItem -LiteralPath $rawPublish -Recurse -File | Where-Object Extension -ne '.pdb' | ForEach-Object {
  $relative=$_.FullName.Substring($rawPublish.Length).TrimStart('\')
  $destination=Join-Path $output $relative
  New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
  Copy-Item -LiteralPath $_.FullName -Destination $destination
 }
 if($bundledEngines.Count -gt 0){
  $engineOutput=Join-Path $output 'engines'
  New-Item -ItemType Directory -Path $engineOutput | Out-Null
  foreach($engine in $bundledEngines){
   Copy-Item -LiteralPath $engine.FullName -Destination $engineOutput -Recurse -Force
   $copiedRoot=Join-Path $engineOutput $engine.Name
   $fileInventory=@(Get-ChildItem -LiteralPath $copiedRoot -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
    [ordered]@{path=$_.FullName.Substring($output.Length).TrimStart('\').Replace('\','/');length=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
   })
   $enginePin=$enginePins.($engine.Name)
   $engineInventory+=([ordered]@{id=$engine.Name;version=$enginePin.version;archiveSha256=$enginePin.sha256;archiveUrl=$enginePin.url;marker=(Get-Content -LiteralPath (Join-Path $copiedRoot '.filee-component') -Raw).Trim();files=$fileInventory})
  }
 }
 $notices=Join-Path $output 'licenses'
 New-Item -ItemType Directory -Path $notices | Out-Null
 Copy-Item -LiteralPath (Join-Path $source 'LICENSE') -Destination (Join-Path $notices 'LICENSE-Filee.txt')
 Copy-Item -LiteralPath (Join-Path $source 'THIRD-PARTY-NOTICES.md') -Destination $notices
 Copy-Item -LiteralPath (Join-Path $source 'src\Filee.Engines\Infrastructure\engines.json') -Destination (Join-Path $notices 'engines.json')
 Copy-Item -LiteralPath (Join-Path $moduleRoot 'Patch-Upstream.ps1') -Destination (Join-Path $notices 'Nook-Filee-discovery-adapter.ps1')
 Copy-Item -LiteralPath (Join-Path $moduleRoot 'upstream.json') -Destination $output
 Copy-Item -LiteralPath (Join-Path $moduleRoot 'README.md') -Destination $output
 # Preserve package metadata and supplied notices for all actually resolved packages.
 # Public distribution still requires a dependency-license review, including native delegates.
 Get-ChildItem -LiteralPath $env:NUGET_PACKAGES -Recurse -File |
  Where-Object { $_.Name -match '^(LICENSE|NOTICE|COPYING)' -or $_.Extension -eq '.nuspec' } |
  ForEach-Object {
   $relative=$_.FullName.Substring($env:NUGET_PACKAGES.Length).TrimStart('\')
   $destination=Join-Path $notices ('nuget\'+$relative)
   New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
   Copy-Item -LiteralPath $_.FullName -Destination $destination
  }
 $metadata=[ordered]@{
  schemaVersion=1; component='file-conversion'; experimental=$true
  upstreamVersion=$pin.tag; upstreamCommit=$pin.commit
  applicationSha256=(Get-FileHash -LiteralPath (Join-Path $output 'Nook.FileConversion.exe') -Algorithm SHA256).Hash
  applicationAssemblySha256=(Get-FileHash -LiteralPath (Join-Path $output 'Nook.FileConversion.dll') -Algorithm SHA256).Hash
  externalEnginesEnabled=($bundledEngines.Count -gt 0)
  managedComponentsEnabled=$true
  bundledEngines=@($bundledEngines | ForEach-Object {$_.Name})
  engineInventory=$engineInventory
  publishedUtc=[DateTime]::UtcNow.ToString('o')
 }
 $metadata | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $output 'component.json') -Encoding UTF8
 Write-Output "Built isolated experimental worker: $output"
}finally{
 foreach($name in $saved.Keys){[Environment]::SetEnvironmentVariable($name,$saved[$name])}
}
