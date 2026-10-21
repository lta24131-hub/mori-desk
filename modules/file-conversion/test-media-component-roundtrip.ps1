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
 if(!$process.WaitForExit(600000)){$process.Kill($true);throw 'Isolated component operation exceeded ten minutes.'}
 $process.WaitForExit()
 $reply=[IO.File]::ReadAllText((Join-Path $session 'stdout.txt'))|ConvertFrom-Json
 if($process.ExitCode -ne 0 -or $reply.status -ne 'ok'){throw ($operation+': '+($reply|ConvertTo-Json -Depth 6))}
 return $reply
}
function Run-Program([string]$program,[string[]]$arguments){
 $start=[Diagnostics.ProcessStartInfo]::new($program)
 $start.UseShellExecute=$false
 $start.CreateNoWindow=$true
 $start.RedirectStandardOutput=$true
 $start.RedirectStandardError=$true
 foreach($argument in $arguments){$start.ArgumentList.Add($argument)}
 $process=[Diagnostics.Process]::Start($start)
 $stdout=$process.StandardOutput.ReadToEndAsync()
 $stderr=$process.StandardError.ReadToEndAsync()
 if(!$process.WaitForExit(60000)){$process.Kill($true);throw 'Media fixture timed out.'}
 $process.WaitForExit()
 $text=$stdout.GetAwaiter().GetResult()
 $errorText=$stderr.GetAwaiter().GetResult()
 if($process.ExitCode -ne 0){throw ('Media fixture failed: '+$errorText)}
 return $text
}
$installed=Request 'components-install' 'ffmpeg'
$component=$installed.components|Where-Object id -eq 'ffmpeg'
Assert ($component.state -eq 'installed' -and $component.canUninstall) 'pinned FFmpeg package downloads, verifies, extracts and activates'
Assert ($component.installedVersion -eq $component.availableVersion) 'managed media version matches approval'
$manifest=[IO.File]::ReadAllText((Join-Path $store 'active.json'))
$state=$manifest|ConvertFrom-Json
$snapshot=Join-Path $store $state.Entries.ffmpeg.Snapshot
$ffmpeg=Join-Path $snapshot 'ffmpeg/bin/ffmpeg.exe'
$ffprobe=Join-Path $snapshot 'ffmpeg/bin/ffprobe.exe'
Assert ((Test-Path -LiteralPath $ffmpeg) -and (Test-Path -LiteralPath $ffprobe)) 'actual media programs exist inside isolated managed snapshot'
Assert (@(Get-ChildItem -LiteralPath (Join-Path $store '.staging') -Directory).Count -eq 0) 'successful media install leaves no stage snapshots'
$video=Join-Path $root 'sample.mp4'
$null=Run-Program $ffmpeg @('-hide_banner','-loglevel','error','-f','lavfi','-i','testsrc=size=64x48:rate=12','-f','lavfi','-i','sine=frequency=440:sample_rate=44100','-t','1','-c:v','libx264','-pix_fmt','yuv420p','-c:a','aac',$video)
$sourceHash=(Get-FileHash -LiteralPath $video -Algorithm SHA256).Hash
$caps=Request 'capabilities' '' @($video)
Assert (@($caps.targets.id) -contains 'webm' -and @($caps.targets.id) -contains 'mp3') 'managed snapshot enables video conversion and audio extraction'
Assert (@($caps.missingComponentIds) -notcontains 'ffmpeg') 'ready media engine is not offered for download again'
$converted=Request 'convert' '' @($video) 'webm'
Assert (@($converted.outputs).Count -eq 1 -and (Test-Path -LiteralPath $converted.outputs[0])) 'video conversion publishes an independent result'
$webm=$converted.outputs[0]
$probe=(Run-Program $ffprobe @('-v','error','-show_streams','-show_format','-of','json',$webm))|ConvertFrom-Json
Assert (@($probe.streams.codec_type) -contains 'video' -and @($probe.streams.codec_type) -contains 'audio') 'managed video conversion preserves video and audio'
Assert ([double]$probe.format.duration -ge 0.9 -and [double]$probe.format.duration -le 1.3) 'managed video duration is preserved'
$audio=Request 'convert' '' @($video) 'mp3'
$probe=(Run-Program $ffprobe @('-v','error','-show_streams','-of','json',$audio.outputs[0]))|ConvertFrom-Json
Assert (@($probe.streams.codec_type).Count -eq 1 -and $probe.streams[0].codec_type -eq 'audio') 'managed video audio extraction produces actual audio-only file'
$flac=Request 'convert' '' @($audio.outputs[0]) 'flac'
$probe=(Run-Program $ffprobe @('-v','error','-show_streams','-of','json',$flac.outputs[0]))|ConvertFrom-Json
Assert ($probe.streams[0].codec_name -eq 'flac') 'managed audio-to-audio conversion produces actual FLAC'
$resultHashes=@{}
foreach($path in @($webm,$audio.outputs[0],$flac.outputs[0])){$resultHashes[$path]=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
$null=Request 'components-update' 'ffmpeg'
Assert ([IO.File]::ReadAllText((Join-Path $store 'active.json')) -eq $manifest) 'up-to-date media update does not replace active snapshot'
$removed=Request 'components-uninstall' 'ffmpeg'
Assert (($removed.components|Where-Object id -eq 'ffmpeg').state -eq 'missing') 'media uninstall removes its active pointer'
Assert (!(Test-Path -LiteralPath $snapshot)) 'media uninstall reclaims only owned snapshot'
Assert ((Get-FileHash -LiteralPath $video -Algorithm SHA256).Hash -eq $sourceHash) 'managed lifecycle preserves source video bytes'
foreach($path in $resultHashes.Keys){Assert ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $resultHashes[$path]) 'media uninstall preserves converted output bytes'}
$capsAfter=Request 'capabilities' '' @($video)
Assert (@($capsAfter.targets).Count -eq 0 -and @($capsAfter.missingComponentIds) -contains 'ffmpeg') 'uninstalled media engine is not rediscovered from system or sibling test builds'
Write-Output ('Passed '+$script:passed+' real managed media checks. Isolated store: '+$store)
