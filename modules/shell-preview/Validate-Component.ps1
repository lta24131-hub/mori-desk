function Assert-ShellPreviewComponent([string]$Directory) {
 $root=[IO.Path]::GetFullPath($Directory).TrimEnd('\')
 $manifest=Get-Content -LiteralPath (Join-Path $root 'manifest.json') -Raw | ConvertFrom-Json
 if($manifest.schemaVersion -ne 1 -or $manifest.componentVersion -ne '1'){throw 'Unexpected shell component schema.'}
 $expected=@{}
 foreach($entry in $manifest.files){
  if($entry.path -notmatch '^[A-Za-z0-9][A-Za-z0-9_.\-/]*$' -or $entry.path -match '(^|/)\.\.?(/|$)' -or $entry.path -match '(^|/)(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|/|$)' -or $entry.path -match '\.(/|$)' -or $expected.ContainsKey($entry.path)){throw 'Unsafe shell component inventory.'}
  $target=[IO.Path]::GetFullPath((Join-Path $root $entry.path))
  if(!$target.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Shell component path escaped.'}
  $item=Get-Item -LiteralPath $target
  if($item.PSIsContainer -or $item.Length -ne $entry.length -or $entry.sha256 -notmatch '^[0-9a-f]{64}$' -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $entry.sha256){throw ('Shell component integrity mismatch: '+$entry.path)}
  $expected[$entry.path]=$true
 }
 foreach($required in @('Nook.ShellPreview.dll','resvg.exe','icons/app.ico','icons/file.ico','licenses/LICENSE-MIT.txt','licenses/LICENSE-APACHE.txt','NOTICES.md','upstream.json')){if(!$expected.ContainsKey($required)){throw ('Missing shell component file: '+$required)}}
 foreach($node in @(Get-Item -LiteralPath $root)+@(Get-ChildItem -LiteralPath $root -Recurse -Force)){
  if(($node.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Linked shell component paths are not allowed.'}
  if(!$node.PSIsContainer){$relative=$node.FullName.Substring($root.Length+1).Replace('\','/');if($relative -ne 'manifest.json' -and !$expected.ContainsKey($relative)){throw ('Unexpected shell component file: '+$relative)}}
 }
}
