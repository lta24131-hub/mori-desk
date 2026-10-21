param([Parameter(Mandatory=$true)][string]$PinnedCatalogPath)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Validate-Component.ps1')
$suiteRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$fixture=Join-Path $suiteRoot ('outputs\conversion-manifest-tests-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $fixture 'licenses') -Force | Out-Null
$passed=0
function Expect-Rejection([string]$Name,[scriptblock]$Action){
 $rejected=$false
 try{& $Action}catch{$rejected=$true}
 if(!$rejected){throw ('Expected rejection: '+$Name)}
 $script:passed++
 Write-Output ('PASS '+$Name)
}
$old=[pscustomobject]@{externalEnginesEnabled=$false}
Assert-ConversionEngineInventory $fixture $old
$passed++
Copy-Item -LiteralPath $PinnedCatalogPath -Destination (Join-Path $fixture 'licenses\engines.json')
$engine=Join-Path $fixture 'engines\7zip'
New-Item -ItemType Directory -Path $engine -Force | Out-Null
$pin=(Get-Content -LiteralPath $PinnedCatalogPath -Raw | ConvertFrom-Json).components.'7zip'
[IO.File]::WriteAllText((Join-Path $engine '.filee-component'),$pin.sha256)
[IO.File]::WriteAllText((Join-Path $engine 'fixture.bin'),'This is a test fixture, not an executable.')
$files=@(Get-ChildItem -LiteralPath $engine -Force -File | ForEach-Object {
 [pscustomobject]@{path='engines/7zip/'+$_.Name;length=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
})
$entry=[pscustomobject]@{id='7zip';version=$pin.version;archiveUrl=$pin.url;archiveSha256=$pin.sha256;marker=$pin.sha256;files=$files}
$manifest=[pscustomobject]@{externalEnginesEnabled=$true;bundledEngines=@('7zip');engineInventory=@($entry)}
Assert-ConversionEngineInventory $fixture $manifest
$passed++
Expect-Rejection 'undeclared engines' {Assert-ConversionEngineInventory $fixture $old}
$manifest.externalEnginesEnabled='true'
Expect-Rejection 'untyped engine flag' {Assert-ConversionEngineInventory $fixture $manifest}
$manifest.externalEnginesEnabled=$true
$entry.archiveSha256='0'*64
Expect-Rejection 'wrong source hash' {Assert-ConversionEngineInventory $fixture $manifest}
$entry.archiveSha256=$pin.sha256
$savedPath=$files[0].path
$files[0].path='engines/7zip/../../fixture.bin'
Expect-Rejection 'path traversal' {Assert-ConversionEngineInventory $fixture $manifest}
$files[0].path=$savedPath
$savedHash=$files[0].sha256
$files[0].sha256='0'*64
Expect-Rejection 'changed engine bytes' {Assert-ConversionEngineInventory $fixture $manifest}
$files[0].sha256=$savedHash
$entry.files=@($files[0])
Expect-Rejection 'unlisted file' {Assert-ConversionEngineInventory $fixture $manifest}
$entry.files=$files
$manifest.bundledEngines=@('7zip','7zip');$manifest.engineInventory=@($entry,$entry)
Expect-Rejection 'duplicate engine' {Assert-ConversionEngineInventory $fixture $manifest}
$manifest.bundledEngines=@('7zip');$manifest.engineInventory=@($entry)
[IO.File]::WriteAllText((Join-Path $fixture 'licenses\engines.json'),'{}')
Expect-Rejection 'changed download catalog' {Assert-ConversionEngineInventory $fixture $manifest}
Write-Output ("PASS $passed inventory validation cases; retained isolated fixture: $fixture")
