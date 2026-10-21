param(
 [Parameter(Mandatory=$true)][string]$WorkerPath,
 [Parameter(Mandatory=$true)][string]$TestRoot
)
$ErrorActionPreference='Stop'
$suite=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$root=[IO.Path]::GetFullPath($TestRoot)
if(!$root.StartsWith((Join-Path $suite 'outputs\'),[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $root)){
 throw 'Use a fresh isolated directory under workspace outputs.'
}
New-Item -ItemType Directory -Path $root | Out-Null
$WorkerPath=[IO.Path]::GetFullPath($WorkerPath)
$store=Join-Path $root 'store'
$script:passed=0
function Assert($condition,[string]$message){
 if(!$condition){throw ('FAIL: '+$message)}
 $script:passed++
 Write-Host ('PASS: '+$message)
}
function Request([string]$operation,[string]$component='', [string[]]$inputs=@(), [string]$target=''){
 $id=[Guid]::NewGuid().ToString('N')
 $session=Join-Path $root $id
 New-Item -ItemType Directory -Path $session | Out-Null
 $request=[ordered]@{schemaVersion=1;id=$id;operation=$operation;inputs=@($inputs);componentRoot=$store;responsePath=(Join-Path $session 'response.json')}
 if($component){$request.componentId=$component}
 if($target){$request.target=$target;$request.outputDirectory=Join-Path $session 'result'}
 $requestPath=Join-Path $session 'request.json'
 [IO.File]::WriteAllText($requestPath,($request|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
 $process=Start-Process -FilePath $WorkerPath -ArgumentList ('--request "'+$requestPath+'"') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $session 'stdout.txt') -RedirectStandardError (Join-Path $session 'stderr.txt')
 if(!$process.WaitForExit(300000)){$process.Kill($true);throw 'Isolated component test exceeded five minutes.'}
 $process.WaitForExit()
 $reply=[IO.File]::ReadAllText((Join-Path $session 'stdout.txt'))|ConvertFrom-Json
 if($process.ExitCode -ne 0 -or $reply.status -ne 'ok'){throw ($operation+': '+($reply|ConvertTo-Json -Depth 6))}
 return $reply
}
$installed=Request 'components-install' '7zip'
$component=$installed.components|Where-Object id -eq '7zip'
Assert ($component.state -eq 'installed' -and $component.canUninstall) 'official pinned 7-Zip download and files-only extraction completed'
Assert ($component.installedVersion -eq '26.03') 'installed version matches embedded approval'
Assert (@(Get-ChildItem -LiteralPath (Join-Path $store '.staging') -Directory).Count -eq 0) 'successful activation leaves no staging snapshots'
$before=[IO.File]::ReadAllText((Join-Path $store 'active.json'))
$current=Request 'components-update' '7zip'
Assert ([IO.File]::ReadAllText((Join-Path $store 'active.json')) -eq $before) 'up-to-date update is idempotent'
$input=Join-Path $root 'sample.zip'
$zip=[IO.Compression.ZipFile]::Open($input,[IO.Compression.ZipArchiveMode]::Create)
try{
 $entry=$zip.CreateEntry('sample.txt')
 $writer=[IO.StreamWriter]::new($entry.Open())
 try{$writer.Write('Independent conversion component roundtrip.')}finally{$writer.Dispose()}
}finally{$zip.Dispose()}
$sourceHash=(Get-FileHash -LiteralPath $input -Algorithm SHA256).Hash
$caps=Request 'capabilities' '' @($input)
Assert (@($caps.targets.id) -contains '7z') 'installed optional component enables its actual conversion route'
$converted=Request 'convert' '' @($input) '7z'
Assert (@($converted.outputs).Count -eq 1 -and (Test-Path -LiteralPath $converted.outputs[0])) 'archive conversion uses private component and publishes independent result'
$output=$converted.outputs[0]
$outputHash=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
$removed=Request 'components-uninstall' '7zip'
Assert (($removed.components|Where-Object id -eq '7zip').state -eq 'missing') 'uninstall removes active component pointer'
Assert (@(Get-ChildItem -LiteralPath (Join-Path $store 'versions\7zip') -Directory).Count -eq 0) 'uninstall reclaims only the owned inactive snapshot'
$again=Request 'components-uninstall' '7zip'
Assert (($again.components|Where-Object id -eq '7zip').state -eq 'missing') 'repeat uninstall is idempotent'
Assert ((Get-FileHash -LiteralPath $input -Algorithm SHA256).Hash -eq $sourceHash) 'uninstall preserves original input unchanged'
Assert ((Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash -eq $outputHash) 'uninstall preserves previously converted result unchanged'
$capsAfter=Request 'capabilities' '' @($input)
Assert (@($capsAfter.targets.id) -notcontains '7z') 'uninstalled engine is not silently rediscovered from the system'
Write-Output ('Passed '+$script:passed+' real component checks. Isolated store: '+$store)
