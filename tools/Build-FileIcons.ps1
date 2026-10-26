param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $sourceRoot 'modules\shell-preview\icons' }
$iconOutput = [IO.Path]::GetFullPath($OutputDirectory)
Add-Type -Path @((Join-Path $sourceRoot 'src\FileFormats.cs'), (Join-Path $sourceRoot 'modules\shell-preview\tools\FileIconGenerator.cs')) -ReferencedAssemblies @('System.dll','System.Core.dll','System.Drawing.dll')
try { [MoriDesk.FileIconGenerator]::Generate($iconOutput, (Join-Path $sourceRoot 'assets\nook.ico')) }
catch { if ($_.Exception.InnerException) { Write-Output $_.Exception.InnerException.ToString() }; throw }
Write-Output ('Generated file icons in ' + $iconOutput)
