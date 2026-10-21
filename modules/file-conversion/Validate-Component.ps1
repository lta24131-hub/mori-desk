# Shared build/package gate. Engines may only be explicit verified components,
# never whatever executable happens to be present on the packaging machine.
function Assert-ConversionEngineInventory([string]$ComponentPath,$Manifest){
 $root=[IO.Path]::GetFullPath($ComponentPath).TrimEnd('\','/')
 if((Get-Item -LiteralPath $root -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Conversion component cannot be a redirected directory.'}
 $all=@(Get-ChildItem -LiteralPath $root -Force -Recurse)
 if(@($all | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Conversion component contains a redirected path.'}
 if($Manifest.externalEnginesEnabled -isnot [bool]){throw 'Conversion engine mode must be explicitly declared.'}
 $ids=@($Manifest.bundledEngines | Where-Object {$_})
 $inventory=@($Manifest.engineInventory | Where-Object {$_})
 $engineRoot=Join-Path $root 'engines'
 if(!$Manifest.externalEnginesEnabled){
  if($ids.Count -or $inventory.Count -or (Test-Path -LiteralPath $engineRoot)){throw 'Undeclared conversion engines.'}
  return
 }
 if(!$ids.Count -or $inventory.Count -ne $ids.Count -or !(Test-Path -LiteralPath $engineRoot -PathType Container)){throw 'Bundled conversion inventory is incomplete.'}
 $catalogPath=Join-Path $root 'licenses\engines.json'
 if(!(Test-Path -LiteralPath $catalogPath -PathType Leaf) -or
    (Get-FileHash -LiteralPath $catalogPath -Algorithm SHA256).Hash -ne '2FD28C8B78D42B9BB77BC7BA4EB525F424AA6F9CF3F292DFD6D50B0210C3989B'){
  throw 'Bundled engines require the exact pinned Filee download catalog.'
 }
 $catalog=(Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json).components
 $allowed=@('ffmpeg','libreoffice','pandoc','ghostscript','calibre','rhwp','7zip','vcruntime','h2orestart','jre')
 $seenIds=@{};$expectedFiles=@{}
 foreach($entry in $inventory){
  $id=[string]$entry.id
  if($allowed -notcontains $id -or $ids -notcontains $id -or $seenIds.ContainsKey($id)){throw 'Unknown or duplicate bundled engine.'}
  $seenIds[$id]=$true
  $pin=$catalog.$id
  if(!$pin -or $entry.version -ne $pin.version -or $entry.archiveUrl -ne $pin.url -or
     $entry.archiveSha256 -ne $pin.sha256 -or $entry.marker -ne $pin.sha256){throw ('Engine provenance mismatch: '+$id)}
  $prefix='engines/'+$id+'/'
  $files=@($entry.files | Where-Object {$_})
  if(!$files.Count){throw ('Empty engine inventory: '+$id)}
  foreach($file in $files){
   $relative=[string]$file.path
   if(!$relative.StartsWith($prefix,[StringComparison]::Ordinal) -or $relative.Contains('\') -or
      $relative.Contains(':') -or @($relative.Split('/') | Where-Object {$_ -in @('','.','..')}).Count -or
      $expectedFiles.ContainsKey($relative) -or [string]$file.sha256 -notmatch '^[a-fA-F0-9]{64}$'){
    throw ('Invalid engine inventory path: '+$relative)
   }
   $absolute=[IO.Path]::GetFullPath((Join-Path $root $relative))
   if(!$absolute.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $absolute -PathType Leaf)){throw ('Engine file missing: '+$relative)}
   $actual=Get-Item -LiteralPath $absolute
   if($actual.Length -ne [long]$file.length -or (Get-FileHash -LiteralPath $absolute -Algorithm SHA256).Hash -ne $file.sha256){throw ('Engine file integrity mismatch: '+$relative)}
   $expectedFiles[$relative]=$true
  }
  $marker=Join-Path $engineRoot ($id+'\.filee-component')
  if(!(Test-Path -LiteralPath $marker -PathType Leaf) -or (Get-Content -LiteralPath $marker -Raw).Trim() -ne $pin.sha256){throw ('Engine package marker mismatch: '+$id)}
 }
 if($seenIds.Count -ne $ids.Count -or @($ids | Sort-Object -Unique).Count -ne $ids.Count){throw 'Duplicate engine declarations.'}
 $actualFiles=@(Get-ChildItem -LiteralPath $engineRoot -Force -Recurse -File)
 if($actualFiles.Count -ne $expectedFiles.Count){throw 'Bundled engines have unlisted or missing files.'}
 foreach($file in $actualFiles){
  $relative=$file.FullName.Substring($root.Length+1).Replace('\','/')
  if(!$expectedFiles.ContainsKey($relative)){throw ('Unlisted bundled engine file: '+$relative)}
 }
 foreach($directory in Get-ChildItem -LiteralPath $engineRoot -Directory -Force){
  if(!$seenIds.ContainsKey($directory.Name)){throw ('Unlisted bundled engine directory: '+$directory.Name)}
 }
}
