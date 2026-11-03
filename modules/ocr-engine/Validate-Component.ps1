function Assert-OcrComponent([string]$Directory){
 $root=[IO.Path]::GetFullPath($Directory).TrimEnd('\')
 for($ancestor=$root;$ancestor;$ancestor=Split-Path $ancestor -Parent){
  if((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked OCR component directory refused.'}
 }
 if(@(Get-ChildItem -LiteralPath $root -Force -Recurse | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Linked OCR component content refused.'}
 foreach($required in @('Nook.Ocr.exe','Nook.Ocr.dll','component.json','models.json','THIRD-PARTY-NOTICES.txt','licenses\STranslate-LICENSE.txt','licenses\RapidOcrNet-LICENSE.txt','licenses\RapidOCR-models-LICENSE.txt','licenses\dotnet-LICENSE.txt')){
  if(!(Test-Path -LiteralPath (Join-Path $root $required) -PathType Leaf)){throw ('Missing OCR component file: '+$required)}
 }
 $metadata=Get-Content -LiteralPath (Join-Path $root 'component.json') -Raw|ConvertFrom-Json
 if($metadata.schemaVersion -ne 1 -or $metadata.component -ne 'ocr-engine' -or $metadata.protocol -ne 3 -or $metadata.engineIdentity -ne 'rapidocrnet-4.2.0-ppocrv5-ch-screen2x-layout4'){throw 'Unsupported OCR component version.'}
 if(@($metadata.files).Count -lt 15){throw 'OCR component inventory is incomplete.'}
 foreach($item in $metadata.files){
  $file=[IO.Path]::GetFullPath((Join-Path $root $item.path))
  if(!$file.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $file -PathType Leaf)){throw 'Invalid OCR component inventory path.'}
  if((Get-Item -LiteralPath $file).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked OCR component file refused.'}
  if((Get-Item -LiteralPath $file).Length -ne $item.bytes -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $item.sha256){throw ('OCR component checksum mismatch: '+$item.path)}
 }
 foreach($model in (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'models.json') -Raw|ConvertFrom-Json)){
  $file=Join-Path $root ('models\ch-v5\'+$model.name)
  if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $model.sha256){throw 'Unpinned OCR model.'}
 }
}

function Copy-OcrComponent([string]$Source,[string]$Destination){
 Assert-OcrComponent $Source
 $sourceRoot=[IO.Path]::GetFullPath($Source).TrimEnd('\')
 $targetRoot=[IO.Path]::GetFullPath($Destination).TrimEnd('\')
 if(Test-Path -LiteralPath $targetRoot){throw 'Use a fresh OCR payload directory.'}
 $metadata=Get-Content -LiteralPath (Join-Path $sourceRoot 'component.json') -Raw|ConvertFrom-Json
 New-Item -ItemType Directory -Path $targetRoot | Out-Null
 # Evaluation screenshots, transcripts and debug symbols are never release files.
 foreach($relative in @('component.json')+@($metadata.files | ForEach-Object {$_.path})){
  $target=[IO.Path]::GetFullPath((Join-Path $targetRoot $relative))
  if(!$target.StartsWith($targetRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid OCR payload path.'}
  New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
  Copy-Item -LiteralPath (Join-Path $sourceRoot $relative) -Destination $target
 }
 Assert-OcrComponent $targetRoot
}
