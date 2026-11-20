param(
 [Parameter(Mandatory=$true)][string]$ApplicationBuild,
 [Parameter(Mandatory=$true)][string]$TestDirectory
)
# Stages a separate test-only folder; never starts Nook or a test process.
$ErrorActionPreference='Stop'
$app=(Get-Item -LiteralPath $ApplicationBuild).FullName
$target=[IO.Path]::GetFullPath($TestDirectory)
if(Test-Path -LiteralPath $target){throw 'Choose a fresh test directory, not a release/build folder.'}
$folder=New-Item -ItemType Directory -Path $target
foreach($file in @('Mori.exe','Mori.exe.config','SymSpell.dll')){
 Copy-Item -LiteralPath (Join-Path $app $file) -Destination $folder.FullName
}
Copy-Item -LiteralPath (Join-Path $app 'Mori.exe.config') -Destination (Join-Path $target 'QingJie.Tests.exe.config')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../data/frequency_dictionary_en_82_765.txt') -Destination (Join-Path $target 'frequency-full-reference.txt')
foreach($file in @('frozen-short-card.json','frozen-mixed-terms.json')){
 Copy-Item -LiteralPath (Join-Path "$PSScriptRoot/lexicon-fixtures" $file) -Destination $target
}
$framework=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$refs=@('System.dll','System.Core.dll','System.Web.Extensions.dll','netstandard.dll','WPF/WindowsBase.dll','WPF/PresentationCore.dll','WPF/PresentationFramework.dll')|ForEach-Object{'/r:'+(Join-Path $framework $_)}
$refs+=@('Mori.exe','SymSpell.dll')|ForEach-Object{'/r:'+(Join-Path $target $_)}
& (Join-Path $framework 'csc.exe') /nologo /target:exe /main:OcrLexiconTests /platform:x64 /optimize+ /codepage:65001 /define:OCR_LEXICON_TESTS ("/out:$target/QingJie.Tests.exe") $refs (Join-Path $PSScriptRoot 'OcrLexiconTests.cs')
if($LASTEXITCODE -ne 0){throw 'OCR lexical oracle test compilation failed.'}
