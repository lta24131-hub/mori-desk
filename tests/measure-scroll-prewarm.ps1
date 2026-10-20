param(
 [Parameter(Mandatory=$true)][string]$BuildDirectory,
 [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
# This benchmark only requests the new inert warm mode. It never publishes
# request.json/start.request, captures pixels, opens UI, or touches user data.
$taskBuild=[IO.Path]::GetFullPath($BuildDirectory)
$taskOutput=[IO.Path]::GetFullPath($OutputDirectory)
$taskHelper=Join-Path $taskBuild 'scroll-capture\Nook.ScrollCapture.exe'
$taskManifest=Get-Content -LiteralPath (Join-Path $taskBuild 'scroll-capture\component.json') -Raw | ConvertFrom-Json
if($taskManifest.capturePrewarm -ne $true){throw 'Candidate must explicitly support inert prewarming.'}
$taskMain=Join-Path $taskBuild 'scroll-capture\resources\app\out\main\main.js'
if((Get-FileHash -LiteralPath $taskMain -Algorithm SHA256).Hash -ne $taskManifest.applicationSha256){throw 'Helper integrity check failed.'}
if(Test-Path -LiteralPath $taskOutput){throw 'Use a new benchmark output directory.'}
New-Item -ItemType Directory -Path $taskOutput | Out-Null
$taskId=[Guid]::NewGuid().ToString('N')
$taskSession=Join-Path $taskOutput $taskId
New-Item -ItemType Directory -Path $taskSession | Out-Null
$taskWarmPath=Join-Path $taskSession 'warm.json'
$taskWarm=@{schemaVersion=1;sessionId=$taskId;outputDirectory=$taskSession;parentProcessId=$PID}
[IO.File]::WriteAllText($taskWarmPath,($taskWarm | ConvertTo-Json -Compress),(New-Object Text.UTF8Encoding($false)))
$taskResult=[ordered]@{schemaVersion=1;scope='hidden prewarm only; no activation, screen, clipboard or installation';helperApplicationSha256=$taskManifest.applicationSha256;warmReadyMilliseconds=$null;processCount=$null;workingSetBytes=$null;privateBytes=$null;exitedAfterCancel=$false;ownedDescendantsExited=$false;result='failed'}
$taskProcess=$null
$taskTracked=@()
try{
 $taskWatch=[Diagnostics.Stopwatch]::StartNew()
 $taskProcess=Start-Process -FilePath $taskHelper -ArgumentList @('--nook-warm',('"'+$taskWarmPath+'"')) -WorkingDirectory (Split-Path $taskHelper -Parent) -WindowStyle Hidden -PassThru
 $taskReadyPath=Join-Path $taskSession 'warm.ready.json'
 while(!(Test-Path -LiteralPath $taskReadyPath)){
  if($taskProcess.HasExited){throw 'Prewarm exited before declaring readiness.'}
  if($taskWatch.ElapsedMilliseconds -ge 20000){throw 'Prewarm did not become ready within 20 seconds.'}
  Start-Sleep -Milliseconds 30
 }
 $taskResult.warmReadyMilliseconds=$taskWatch.ElapsedMilliseconds
 $taskReady=Get-Content -LiteralPath $taskReadyPath -Raw | ConvertFrom-Json
 if($taskReady.schemaVersion -ne 1 -or $taskReady.sessionId -ne $taskId -or $taskReady.warm -ne $true){throw 'Invalid warm readiness identity.'}
 # Descendants are selected only from the exact process launched above. Read
 # process memory, not window state, titles, paths, or desktop content.
 $taskProcessIds=New-Object 'System.Collections.Generic.List[int]'
 $taskProcessIds.Add($taskProcess.Id)
 for($taskIndex=0;$taskIndex -lt $taskProcessIds.Count -and $taskIndex -lt 32;$taskIndex++){
  $taskParent=$taskProcessIds[$taskIndex]
  foreach($taskChild in @(Get-CimInstance Win32_Process -Filter ('ParentProcessId='+$taskParent))){
   if(!$taskProcessIds.Contains([int]$taskChild.ProcessId)){$taskProcessIds.Add([int]$taskChild.ProcessId)}
  }
 }
 $taskMemory=@(Get-Process -Id $taskProcessIds.ToArray() -ErrorAction SilentlyContinue)
 foreach($taskOwned in $taskMemory){
  if($taskOwned.Id -ne $taskProcess.Id){
   # Cache a handle while identity is known so cleanup cannot follow PID reuse.
   try{[void]$taskOwned.Handle;$taskTracked+=,$taskOwned}catch{}
  }
 }
 $taskResult.processCount=$taskMemory.Count
 $taskResult.workingSetBytes=($taskMemory | Measure-Object -Property WorkingSet64 -Sum).Sum
 $taskResult.privateBytes=($taskMemory | Measure-Object -Property PrivateMemorySize64 -Sum).Sum
 foreach($taskForbidden in @('request.json','prepared.json','start.request','ready.json','result.json','result.png')){
  if(Test-Path -LiteralPath (Join-Path $taskSession $taskForbidden)){throw ('Inert prewarm unexpectedly produced '+$taskForbidden)}
 }
 $taskResult.result='passed'
}finally{
 if($taskProcess){
  if(!$taskProcess.HasExited){
   [IO.File]::WriteAllText((Join-Path $taskSession 'cancel.request'),$taskId,(New-Object Text.UTF8Encoding($false)))
   $taskResult.exitedAfterCancel=$taskProcess.WaitForExit(5000)
   if(!$taskResult.exitedAfterCancel){
    $taskResult.result='failed'
    # Last resort is the exact owned Process object, not a name or broad tree.
    $taskProcess.Kill()
    [void]$taskProcess.WaitForExit(1000)
   }
  }else{$taskResult.exitedAfterCancel=$true}
  $taskProcess.Dispose()
 }
 $taskResult.ownedDescendantsExited=$true
 foreach($taskOwned in $taskTracked){
  try{
   if(!$taskOwned.WaitForExit(2000)){
    $taskResult.ownedDescendantsExited=$false
    $taskResult.result='failed'
    $taskOwned.Kill()
    [void]$taskOwned.WaitForExit(1000)
   }
  }catch{$taskResult.ownedDescendantsExited=$false;$taskResult.result='failed'}finally{$taskOwned.Dispose()}
 }
 [IO.File]::WriteAllText((Join-Path $taskOutput 'result.json'),($taskResult | ConvertTo-Json -Depth 4),(New-Object Text.UTF8Encoding($false)))
 $taskResult | ConvertTo-Json -Depth 4
}
if($taskResult.result -ne 'passed'){throw 'Hidden warm benchmark did not pass.'}
