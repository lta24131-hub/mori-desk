param([Parameter(Mandatory=$true)][string]$OutputDirectory,[string]$Dotnet='dotnet',[string]$ModelCache)
$ErrorActionPreference='Stop'
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Use a new output directory; do not mix OCR runtime versions.'}
if(!$ModelCache){$ModelCache=Join-Path $PSScriptRoot 'models'}
New-Item -ItemType Directory -Force -Path $ModelCache | Out-Null
$models=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'models.json') -Raw | ConvertFrom-Json
foreach($model in $models){
 $file=Join-Path $ModelCache $model.name
 if(!(Test-Path -LiteralPath $file)){
  $pending=$file+'.download'
  try{Invoke-WebRequest -Uri $model.url -OutFile $pending; if((Get-FileHash -LiteralPath $pending -Algorithm SHA256).Hash -ne $model.sha256){throw 'OCR model checksum mismatch.'}; Move-Item -LiteralPath $pending -Destination $file}
  finally{if(Test-Path -LiteralPath $pending){Remove-Item -LiteralPath $pending}}
 }
 if((Get-Item -LiteralPath $file).Length -ne $model.bytes -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $model.sha256){throw ('Unexpected OCR model: '+$model.name)}
}
& $Dotnet publish (Join-Path $PSScriptRoot 'Nook.Ocr.csproj') -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -o $output
if($LASTEXITCODE -ne 0){throw 'OCR build failed.'}
$modelOutput=Join-Path $output 'models\ch-v5';New-Item -ItemType Directory -Path $modelOutput -Force | Out-Null
foreach($model in $models){Copy-Item -LiteralPath (Join-Path $ModelCache $model.name) -Destination $modelOutput}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'models.json') -Destination $output
$licenses=Join-Path $output 'licenses';New-Item -ItemType Directory -Path $licenses | Out-Null
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'licenses') -File | Copy-Item -Destination $licenses
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'STranslate\LICENSE') -Destination (Join-Path $licenses 'STranslate-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt') -Destination $output
$assets=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'obj\project.assets.json') -Raw | ConvertFrom-Json
$packageRoot=@($assets.packageFolders.PSObject.Properties.Name)[0]
foreach($entry in $assets.libraries.PSObject.Properties){
 if($entry.Value.type -ne 'package'){continue}
 $root=Join-Path $packageRoot $entry.Value.path
 $target=Join-Path $licenses ($entry.Name.Replace('/','-'));New-Item -ItemType Directory -Path $target | Out-Null
 Get-ChildItem -LiteralPath $root -File | Where-Object {$_.Name -match 'license|notice|\.nuspec$'} | Copy-Item -Destination $target
}
# Self-contained .NET runtime notices are also part of the redistributed code.
$sdkRoot=Split-Path (Get-Command $Dotnet).Source -Parent
foreach($file in @('LICENSE.txt','ThirdPartyNotices.txt')){Copy-Item -LiteralPath (Join-Path $sdkRoot $file) -Destination (Join-Path $licenses ('dotnet-'+$file))}
$inventory=@(Get-ChildItem -LiteralPath $output -File -Recurse | Where-Object Extension -ne '.pdb' | ForEach-Object {
 @{path=$_.FullName.Substring($output.Length+1).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
})
@{schemaVersion=1;component='ocr-engine';protocol=3;engineIdentity='rapidocrnet-4.2.0-ppocrv5-ch-screen2x-layout4';files=$inventory}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'component.json') -Encoding UTF8
. (Join-Path $PSScriptRoot 'Validate-Component.ps1')
Assert-OcrComponent $output
Write-Output ('Built OCR component: '+$output)
