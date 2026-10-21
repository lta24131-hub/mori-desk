param([Parameter(Mandatory=$true)][string]$WorkerPath,[string]$TestRoot)
$ErrorActionPreference='Stop'
$suite=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if(!$TestRoot){$TestRoot=Join-Path $suite ('outputs\conversion-component-tests\'+[Guid]::NewGuid().ToString('N'))}
$root=[IO.Path]::GetFullPath($TestRoot)
if(Test-Path -LiteralPath $root){throw 'Use a fresh test directory.'}
New-Item -ItemType Directory -Path $root | Out-Null
$WorkerPath=[IO.Path]::GetFullPath($WorkerPath)
$store=Join-Path $root 'component-store'
$script:passed=0
function Assert($condition,[string]$message){
 if(!$condition){throw ('FAIL: '+$message)}
 $script:passed++
 Write-Host ('PASS: '+$message)
}
function Run-Request([string]$operation,[string]$componentId='', [string[]]$inputs=@(), [string]$overrideRoot='', [switch]$Cancel, [string]$Target=''){
 $id=[Guid]::NewGuid().ToString('N')
 $session=Join-Path $root $id
 New-Item -ItemType Directory -Path $session | Out-Null
 $request=[ordered]@{schemaVersion=1;id=$id;operation=$operation;inputs=@($inputs);responsePath=(Join-Path $session 'response.json');componentRoot=$store}
 if($componentId){$request.componentId=$componentId}
 if($overrideRoot){$request.componentRoot=$overrideRoot}
 if($Target){$request.target=$Target;$request.outputDirectory=Join-Path $session 'result'}
 if($Cancel){[IO.File]::WriteAllText((Join-Path $session 'cancel.flag'),'cancel')}
 $requestPath=Join-Path $session 'request.json'
 [IO.File]::WriteAllText($requestPath,($request | ConvertTo-Json -Depth 5),(New-Object Text.UTF8Encoding($false)))
 $stdout=Join-Path $session 'stdout.txt'
 $stderr=Join-Path $session 'stderr.txt'
 $process=Start-Process -FilePath $WorkerPath -ArgumentList ('--request "'+$requestPath+'"') -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
 if(!$process.WaitForExit(30000)){$process.Kill($true);throw 'Component protocol fixture exceeded 30 seconds.'}
 $process.WaitForExit()
 $reply=[IO.File]::ReadAllText($stdout) | ConvertFrom-Json
 return [PSCustomObject]@{code=$process.ExitCode;reply=$reply;session=$session}
}

$listed=Run-Request 'components-list'
Assert ($listed.code -eq 0 -and $listed.reply.status -eq 'ok') 'empty-input component list succeeds'
Assert (@($listed.reply.components).Count -eq 7) 'list contains seven reviewed optional packages'
$expected=@('ffmpeg','libreoffice','calibre','pandoc','ghostscript','7zip','rhwp')
Assert (@($expected | Where-Object {$_ -notin $listed.reply.components.id}).Count -eq 0) 'reviewed package IDs match host contract'
Assert (@($listed.reply.components | Where-Object {$_.state -ne 'missing' -or $_.canUninstall}).Count -eq 0) 'fresh store reports missing and not uninstallable'
Assert (@($listed.reply.components | Where-Object {$_.downloadBytes -le 0 -or !$_.availableVersion}).Count -eq 0) 'packages expose pinned version and download size'
Assert (@(($listed.reply.components | Where-Object id -eq 'libreoffice').dependencies) -contains 'jre') 'Office dependency is explicit'
Assert (@(($listed.reply.components | Where-Object id -eq 'ghostscript').dependencies) -contains 'vcruntime') 'PostScript runtime dependency is explicit'
Assert (Test-Path -LiteralPath (Join-Path $listed.session 'response.json')) 'component list response is durable'
Assert (@($listed.reply.outputs).Count -eq 0 -and @($listed.reply.files).Count -eq 0) 'component management never claims media outputs'

$unknown=Run-Request 'components-install' 'not-reviewed'
Assert ($unknown.code -ne 0 -and $unknown.reply.status -eq 'error') 'unknown package is rejected before downloading'
$invalid=Run-Request 'components-install' '../ffmpeg'
Assert ($invalid.code -eq 2 -and $invalid.reply.status -eq 'error') 'path-shaped component ID is rejected'
$invalid=Run-Request 'components-list' 'ffmpeg'
Assert ($invalid.code -eq 2) 'list cannot be confused with targeted mutation'
$invalid=Run-Request 'convert'
Assert ($invalid.code -eq 2) 'conversion still requires at least one input'
$invalid=Run-Request 'components-list' '' @() '\\server\share\components'
Assert ($invalid.code -eq 2) 'network component root is rejected'
$invalid=Run-Request 'components-list' '' @() 'relative-store'
Assert ($invalid.code -eq 2) 'relative component root is rejected'
$invalid=Run-Request 'components-list' '' @() ([IO.Path]::GetPathRoot($root))
Assert ($invalid.code -eq 2) 'drive-root component store is rejected'
$cancelled=Run-Request 'components-install' 'ffmpeg' @() '' -Cancel
Assert ($cancelled.code -eq 3 -and $cancelled.reply.status -eq 'cancelled') 'cancellation before install never downloads or enables a package'

$video=Join-Path $root 'sample.mp4'
[IO.File]::WriteAllBytes($video,[byte[]]@(0,0,0,20,102,116,121,112,105,115,111,109))
$caps=Run-Request 'capabilities' '' @($video)
Assert ($caps.code -eq 0 -and $caps.reply.status -eq 'ok') 'capabilities remain usable with an empty component store'
Assert (@($caps.reply.missingComponentIds).Count -eq 1 -and $caps.reply.missingComponentIds[0] -eq 'ffmpeg') 'video input offers only the relevant missing media component'
Assert (@($caps.reply.targets).Count -eq 0) 'missing media engine is not advertised as a working conversion'
$unknownFile=Join-Path $root 'unknown.nooknotformat'
[IO.File]::WriteAllText($unknownFile,'unknown')
$caps=Run-Request 'capabilities' '' @($unknownFile)
Assert (@($caps.reply.missingComponentIds).Count -eq 0) 'unknown input does not offer unrelated downloads'
$invalid=Run-Request 'components-list' '' @($video)
Assert ($invalid.code -eq 2) 'component operations cannot accept asset files'
$invalid=Run-Request 'capabilities' 'ffmpeg' @($video)
Assert ($invalid.code -eq 2) 'capabilities cannot mutate a component'
$readLease=[IO.File]::Open((Join-Path $store '.use.lock'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try{
 $busy=Run-Request 'components-uninstall' 'ffmpeg'
 Assert ($busy.code -ne 0 -and $busy.reply.error.message -match '正在转换或维护') 'cross-process read lease prevents component removal'
}finally{$readLease.Dispose()}
$writeLease=[IO.File]::Open((Join-Path $store '.use.lock'),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try{
 $busy=Run-Request 'capabilities' '' @($video)
 Assert ($busy.code -ne 0 -and $busy.reply.error.message -match '正在转换或维护') 'conversion cannot race an exclusive component commit'
}finally{$writeLease.Dispose()}
$listedAgain=Run-Request 'components-list'
Assert (@($listedAgain.reply.components | Where-Object {$_.state -ne 'missing'}).Count -eq 0) 'all protocol checks leave the isolated component inventory unchanged'
$png=Join-Path $root 'builtin.png'
Add-Type -AssemblyName System.Drawing
$bitmap=[Drawing.Bitmap]::new(16,16)
try{
 $bitmap.SetPixel(0,0,[Drawing.Color]::Red)
 $bitmap.Save($png,[Drawing.Imaging.ImageFormat]::Png)
}finally{$bitmap.Dispose()}
$pngHash=(Get-FileHash -LiteralPath $png -Algorithm SHA256).Hash
$unowned=Join-Path $root 'unowned-store'
New-Item -ItemType Directory -Path $unowned | Out-Null
$unownedFile=Join-Path $unowned 'keep.txt'
[IO.File]::WriteAllText($unownedFile,'User-owned content: never adopt or delete.')
$caps=Run-Request 'capabilities' '' @($png) $unowned
Assert ($caps.code -eq 0 -and @($caps.reply.targets.id) -contains 'jpg') 'unowned optional store cannot block built-in capabilities'
Assert (($caps.reply.missingEngines -join ' ') -match '扩展转换组件暂不可用') 'optional store fallback explains its limitation'
$converted=Run-Request 'convert' '' @($png) $unowned -Target 'jpg'
Assert ($converted.code -eq 0 -and (Test-Path -LiteralPath $converted.reply.outputs[0])) 'built-in image conversion still works with unowned optional storage'
Assert (@(Get-ChildItem -LiteralPath $unowned -Force).Count -eq 1 -and [IO.File]::ReadAllText($unownedFile) -eq 'User-owned content: never adopt or delete.') 'fallback never adopts or modifies unowned component storage'
$corrupt=Join-Path $root 'corrupt-store'
$null=Run-Request 'components-list' '' @() $corrupt
$manifest=Join-Path $corrupt 'active.json'
[IO.File]::WriteAllText($manifest,'{malformed')
$converted=Run-Request 'convert' '' @($png) $corrupt -Target 'jpg'
Assert ($converted.code -eq 0 -and (Test-Path -LiteralPath $converted.reply.outputs[0])) 'corrupt optional manifest cannot break built-in conversion'
Assert ([IO.File]::ReadAllText($manifest) -eq '{malformed') 'fallback leaves corrupt manifest untouched for diagnosis'
$strict=Run-Request 'components-list' '' @() $corrupt
Assert ($strict.code -ne 0) 'component management remains strict about corrupt inventory'
$caps=Run-Request 'capabilities' '' @($video) $corrupt
Assert ($caps.code -eq 0 -and @($caps.reply.targets).Count -eq 0 -and @($caps.reply.missingComponentIds) -contains 'ffmpeg') 'corrupt store cannot enable or execute missing media engines'
Assert ((Get-FileHash -LiteralPath $png -Algorithm SHA256).Hash -eq $pngHash) 'fallback tests preserve original image bytes'
Write-Output ('Passed '+$script:passed+' checks. Isolated store: '+$store)
