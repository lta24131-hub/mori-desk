param(
 [Parameter(Mandatory=$true)][string]$ApplicationPath,
 [Parameter(Mandatory=$true)][string]$WorkerPath,
 [string]$OutputDirectory,
 [switch]$IncludeDocumentEngines,
 [switch]$IncludeRelatedRegressions,
 [switch]$IncludeComponents,
 [switch]$IncludeStorageSettings
)
$ErrorActionPreference='Stop'
$suiteRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$application=[IO.Path]::GetFullPath($ApplicationPath)
$worker=[IO.Path]::GetFullPath($WorkerPath)
if(!(Test-Path -LiteralPath $application -PathType Leaf) -or !(Test-Path -LiteralPath $worker -PathType Leaf)){throw 'Pass the explicitly built Nook assembly and isolated worker.'}
if(!$OutputDirectory){$OutputDirectory=Join-Path $suiteRoot ('outputs\file-conversion-background-'+[Guid]::NewGuid().ToString('N'))}
$resultRoot=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $resultRoot){throw 'Use a fresh output directory. This runner never replaces previous results.'}
New-Item -ItemType Directory -Path $resultRoot | Out-Null
$compiler='C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$clientSource=Join-Path $suiteRoot 'src\FileConversionClient.cs'
$formatsSource=Join-Path $suiteRoot 'src\ConversionFormats.cs'
$componentsSource=Join-Path $suiteRoot 'src\ConversionComponentClient.cs'
$runners=@('ClientRunner','IntegrationRunner','UiRunner','QuickRunner')
if($IncludeDocumentEngines){$runners+='DocumentRunner'}
if($IncludeRelatedRegressions){$runners+='RegressionRunner'}
if($IncludeComponents){$runners+=@('ComponentClientRunner','ComponentsRunner')}
if($IncludeStorageSettings){$runners+=@('StorageRunner','StorageUiRunner')}
foreach($name in $runners){
 $sources=@((Join-Path $PSScriptRoot ($name+'.cs')))
 if($name -in @('ClientRunner','IntegrationRunner','ComponentClientRunner')){$sources=@($clientSource,$formatsSource,$componentsSource)+$sources}
 & $compiler /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 /r:System.Core.dll /r:System.Web.Extensions.dll /r:System.Drawing.dll ('/out:'+(Join-Path $resultRoot ($name+'.exe'))) @sources
 if($LASTEXITCODE -ne 0){throw ('Could not compile '+$name)}
 if(Test-Path -LiteralPath ($application+'.config')){Copy-Item -LiteralPath ($application+'.config') -Destination (Join-Path $resultRoot ($name+'.exe.config'))}
}
function Run-BackgroundCheck([string]$Name,[string[]]$Arguments){
 $quoted=@($Arguments | ForEach-Object {'"'+$_+'"'})
 $output=Join-Path $resultRoot ($Name+'.txt')
 $errors=Join-Path $resultRoot ($Name+'-errors.txt')
 $process=Start-Process -FilePath (Join-Path $resultRoot ($Name+'.exe')) -ArgumentList $quoted -WindowStyle Hidden -PassThru -RedirectStandardOutput $output -RedirectStandardError $errors
 if(!$process.WaitForExit(60000)){
  $process.Kill()
  throw ($Name+' exceeded its background-test timeout; see its isolated results directory.')
 }
 $process.WaitForExit()
 if($process.ExitCode -ne 0){Get-Content -LiteralPath $output;Get-Content -LiteralPath $errors;throw ($Name+' failed')}
 Get-Content -LiteralPath $output | Select-Object -Last 1
}
Run-BackgroundCheck 'ClientRunner' @((Join-Path $resultRoot 'client-cases'))
Run-BackgroundCheck 'IntegrationRunner' @($worker,(Join-Path $resultRoot 'integration-cases'))
Run-BackgroundCheck 'UiRunner' @($application,(Join-Path $resultRoot 'ui-cases'))
Run-BackgroundCheck 'QuickRunner' @($application,$worker,(Join-Path $resultRoot 'quick-real-cases'))
if($IncludeDocumentEngines){Run-BackgroundCheck 'DocumentRunner' @($application,$worker,(Join-Path $resultRoot 'document-real-cases'),'all')}
if($IncludeRelatedRegressions){Run-BackgroundCheck 'RegressionRunner' @($application,(Join-Path $resultRoot 'related-regression-cases'))}
if($IncludeComponents){
 Run-BackgroundCheck 'ComponentClientRunner' @((Join-Path $resultRoot 'component-client-cases'))
 Run-BackgroundCheck 'ComponentsRunner' @($application,(Join-Path $resultRoot 'component-cases'))
}
if($IncludeStorageSettings){
 Run-BackgroundCheck 'StorageRunner' @($application,(Join-Path $resultRoot 'storage-cases'))
 Run-BackgroundCheck 'StorageUiRunner' @($application,(Join-Path $resultRoot 'storage-ui-cases'))
}
Write-Output ('All checks completed without Nook entry, desktop surfaces, clipboard, or personal materials: '+$resultRoot)
