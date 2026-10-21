param([Parameter(Mandatory=$true)][string]$WorkerPath,[string]$TestRoot)
$ErrorActionPreference='Stop'
$suite=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if(!$TestRoot){$TestRoot=Join-Path $suite ('outputs\conversion-performance\'+[Guid]::NewGuid().ToString('N'))}
$root=[IO.Path]::GetFullPath($TestRoot)
if(Test-Path -LiteralPath $root){throw 'Use a fresh test directory.'}
New-Item -ItemType Directory -Path $root | Out-Null
$WorkerPath=[IO.Path]::GetFullPath($WorkerPath)
Add-Type -AssemblyName System.Drawing
$source=Join-Path $root 'small-640x480.png'
$bitmap=New-Object Drawing.Bitmap(640,480)
$graphics=[Drawing.Graphics]::FromImage($bitmap)
try{
 $graphics.Clear([Drawing.Color]::CornflowerBlue)
 $graphics.FillRectangle([Drawing.Brushes]::White,40,40,320,200)
 $bitmap.Save($source,[Drawing.Imaging.ImageFormat]::Png)
}finally{$graphics.Dispose();$bitmap.Dispose()}
$originalHash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$measurements=@()
foreach($operation in @('capabilities','convert')){
 $id=[Guid]::NewGuid().ToString('N')
 $session=Join-Path $root $id
 New-Item -ItemType Directory -Path $session | Out-Null
 $request=[ordered]@{schemaVersion=1;id=$id;operation=$operation;inputs=@($source);target='jpg';quality=90;outputDirectory=(Join-Path $session 'output');responsePath=(Join-Path $session 'response.json');componentRoot=(Join-Path $root 'component-store')}
 $requestPath=Join-Path $session 'request.json'
 [IO.File]::WriteAllText($requestPath,($request | ConvertTo-Json -Depth 4),(New-Object Text.UTF8Encoding($false)))
 $stdout=Join-Path $session 'stdout.txt'
 $stderr=Join-Path $session 'stderr.txt'
 $watch=[Diagnostics.Stopwatch]::StartNew()
 $process=Start-Process -FilePath $WorkerPath -ArgumentList ('--request "'+$requestPath+'"') -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
 $peak=0L
 while(!$process.HasExited){
  $process.Refresh()
  try{$peak=[Math]::Max($peak,$process.PeakWorkingSet64)}catch [InvalidOperationException]{}
  if($watch.ElapsedMilliseconds -gt 30000){$process.Kill($true);throw 'Small-image probe exceeded 30 seconds.'}
  [void]$process.WaitForExit(10)
 }
 $process.WaitForExit()
 $watch.Stop()
 $reply=[IO.File]::ReadAllText($stdout) | ConvertFrom-Json
 if($process.ExitCode -ne 0 -or $reply.status -ne 'ok'){throw ('Probe failed: '+$reply.error.message)}
 if($operation -eq 'convert'){
  $result=[Drawing.Image]::FromFile($reply.outputs[0])
  try{if($result.Width -ne 640 -or $result.Height -ne 480){throw 'Output dimensions changed.'}}finally{$result.Dispose()}
 }
 if((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $originalHash){throw 'Source changed.'}
 if(Test-Path -LiteralPath (Join-Path ([IO.Path]::GetTempPath()) ('Nook-ConversionNative\'+$id))){throw 'Worker left its native workspace.'}
 $measurements+=[ordered]@{operation=$operation;elapsedMs=$watch.ElapsedMilliseconds;sampledPeakWorkingSetBytes=$peak}
}
$report=[ordered]@{
 fixture='Generated 640x480 PNG, converted to JPG; one first capability query followed by one conversion.'
 limitation='Worker process peak working set sampled every 10 ms; no child-engine total. Not a cold-OS or general performance benchmark.'
 measurements=$measurements
}
$json=$report | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $root 'performance.json'),$json,(New-Object Text.UTF8Encoding($false)))
Write-Output $json
