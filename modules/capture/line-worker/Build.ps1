param([Parameter(Mandatory=$true)][string]$OutputDirectory,[Parameter(Mandatory=$true)][string]$TesseractAssembly)
$ErrorActionPreference='Stop'
$output=[IO.Path]::GetFullPath($OutputDirectory)
$tesseract=(Resolve-Path -LiteralPath $TesseractAssembly).ProviderPath
$null=New-Item -ItemType Directory -Force -Path $output
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$shared=Join-Path $PSScriptRoot '..\src\LineSymbolReader.cs'
& (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 ('/out:'+(Join-Path $output 'Nook.LineOcr.exe')) ('/r:'+$tesseract) (Join-Path $PSScriptRoot 'Program.cs') $shared
if($LASTEXITCODE -ne 0){throw 'Nook single-line OCR worker compilation failed.'}
