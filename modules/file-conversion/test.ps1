param([Parameter(Mandatory=$true)][string]$WorkerPath,[string]$TestRoot)
$ErrorActionPreference='Stop'
if(!$TestRoot){$TestRoot=Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))) ('outputs\file-conversion-tests\'+[Guid]::NewGuid().ToString('N'))}
$root=[IO.Path]::GetFullPath($TestRoot)
if(Test-Path -LiteralPath $root){throw 'Use a new, empty test root.'}
New-Item -ItemType Directory -Path $root | Out-Null
$WorkerPath=[IO.Path]::GetFullPath($WorkerPath)
$script:passed=0
function Assert($condition,[string]$message){
 if(!$condition){throw ('FAIL: '+$message)}
 $script:passed++
 Write-Output ('PASS: '+$message)
}
function Write-TwoPagePdf([string]$path,[int]$pageCount=2){
 $red="1 0 0 rg`n0 0 144 96 re f`n"
 $blue="0 0 1 rg`n0 0 144 96 re f`n"
 $children=((0..($pageCount-1) | ForEach-Object {($_+3).ToString()+' 0 R'}) -join ' ')
 $objects=@(
  '<< /Type /Catalog /Pages 2 0 R >>',
  ('<< /Type /Pages /Kids ['+$children+'] /Count '+$pageCount+' >>')
 )
 for($page=0;$page -lt $pageCount;$page++){
  $content=$pageCount+3+($page%2)
  $objects+=('<< /Type /Page /Parent 2 0 R /MediaBox [0 0 144 96] /Contents '+$content+' 0 R /Resources << >> >>')
 }
 $objects+=@(
  ('<< /Length '+$red.Length+" >>`nstream`n"+$red+'endstream'),
  ('<< /Length '+$blue.Length+" >>`nstream`n"+$blue+'endstream')
 )
 $buffer=New-Object Text.StringBuilder
 [void]$buffer.Append("%PDF-1.4`n")
 $offsets=@()
 for($i=0;$i -lt $objects.Count;$i++){
  $offsets+=$buffer.Length
  [void]$buffer.Append(($i+1).ToString()+" 0 obj`n"+$objects[$i]+"`nendobj`n")
 }
 $xref=$buffer.Length
 [void]$buffer.Append("xref`n0 "+($objects.Count+1)+"`n0000000000 65535 f `n")
 foreach($offset in $offsets){[void]$buffer.Append($offset.ToString('0000000000')+" 00000 n `n")}
 [void]$buffer.Append("trailer`n<< /Size "+($objects.Count+1)+" /Root 1 0 R >>`nstartxref`n"+$xref+"`n%%EOF`n")
 [IO.File]::WriteAllText($path,$buffer.ToString(),[Text.Encoding]::ASCII)
}
function Write-MinimalPsd([string]$path){
 # 2x2 RGB Photoshop v1, no resources/layers, uncompressed red composite.
 $bytes=[byte[]]@(
  0x38,0x42,0x50,0x53,0,1,0,0,0,0,0,0,0,3,0,0,0,2,0,0,0,2,0,8,0,3,
  0,0,0,0,0,0,0,0,0,0,0,0,0,0,255,255,255,255,0,0,0,0,0,0,0,0
 )
 [IO.File]::WriteAllBytes($path,$bytes)
}
function Run-Worker([string]$operation,[string[]]$inputs,[string]$target='', [int]$quality=90,[switch]$Cancel,[switch]$CancelDuring,[switch]$Occupied,[string]$OverrideId=''){
 $id=[Guid]::NewGuid().ToString('N')
 $session=Join-Path $root $id
 New-Item -ItemType Directory -Path $session | Out-Null
 $request=[ordered]@{schemaVersion=1;id=$id;operation=$operation;inputs=$inputs;responsePath=(Join-Path $session 'response.json');componentRoot=(Join-Path $root 'component-store')}
 if($OverrideId){$request.id=$OverrideId}
 if($operation -eq 'convert'){
  $request.target=$target
  $request.quality=$quality
  $request.outputDirectory=Join-Path $session 'output'
 }
 if($Occupied){
  New-Item -ItemType Directory -Path $request.outputDirectory | Out-Null
  [IO.File]::WriteAllText((Join-Path $request.outputDirectory 'preserve.txt'),'do not overwrite')
 }
 if($Cancel){[IO.File]::WriteAllText((Join-Path $session 'cancel.flag'),'cancel')}
 $requestPath=Join-Path $session 'request.json'
 [IO.File]::WriteAllText($requestPath,($request | ConvertTo-Json -Depth 5),(New-Object Text.UTF8Encoding($false)))
 $stdout=Join-Path $session 'stdout.txt'
 $stderr=Join-Path $session 'stderr.txt'
 $watch=[Diagnostics.Stopwatch]::StartNew()
 $process=Start-Process -FilePath $WorkerPath -ArgumentList ('--request "'+$requestPath+'"') -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
 if($CancelDuring){
  $started=Join-Path $session ('work-'+$id+'\outputs')
  while(!(Test-Path -LiteralPath $started) -and !$process.HasExited -and $watch.ElapsedMilliseconds -lt 10000){Start-Sleep -Milliseconds 10}
  if(!(Test-Path -LiteralPath $started)){throw 'Conversion did not enter the output stage for cancellation test.'}
  [IO.File]::WriteAllText((Join-Path $session 'cancel.flag'),'cancel')
 }
 if(!$process.WaitForExit(60000)){ $process.Kill($true); throw 'Worker exceeded 60 second test timeout.' }
 $process.WaitForExit()
 $watch.Stop()
 $raw=[IO.File]::ReadAllText($stdout)
 $reply=$raw | ConvertFrom-Json
 [PSCustomObject]@{reply=$reply;code=$process.ExitCode;exited=$process.HasExited;session=$session;milliseconds=$watch.ElapsedMilliseconds;request=$request}
}

Add-Type -AssemblyName System.Drawing
$png=Join-Path $root 'noise.png'
$bitmap=New-Object Drawing.Bitmap(128,96)
$random=New-Object Random(412)
try{
 for($y=0;$y -lt 96;$y++){for($x=0;$x -lt 128;$x++){
  $alpha=if($x -lt 16){0}else{255}
  $bitmap.SetPixel($x,$y,[Drawing.Color]::FromArgb($alpha,$random.Next(256),$random.Next(256),$random.Next(256)))
 }}
 $bitmap.Save($png,[Drawing.Imaging.ImageFormat]::Png)
}finally{$bitmap.Dispose()}
$originalHash=(Get-FileHash -LiteralPath $png -Algorithm SHA256).Hash
$caps=Run-Worker 'capabilities' @($png)
Assert ($caps.code -eq 0 -and $caps.reply.status -eq 'ok') 'capabilities exits successfully'
Assert (@($caps.reply.targets.id) -contains 'jpg' -and @($caps.reply.targets.id) -contains 'pdf') 'PNG offers real JPG and PDF routes'
Assert (@($caps.reply.targets.id) -notcontains 'png') 'no meaningless same-format action'
Assert (Test-Path -LiteralPath (Join-Path $caps.session 'response.json')) 'response is durably committed'
$low=Run-Worker 'convert' @($png) 'jpg' 20
$high=Run-Worker 'convert' @($png) 'jpg' 95
Assert ($low.code -eq 0 -and $high.code -eq 0) 'JPG conversion succeeds'
Assert ((Get-Item -LiteralPath $low.reply.outputs[0]).Length -lt (Get-Item -LiteralPath $high.reply.outputs[0]).Length) 'quality control changes JPEG encoding'
$decoded=[Drawing.Image]::FromFile($high.reply.outputs[0])
try{
 Assert ($decoded.Width -eq 128 -and $decoded.Height -eq 96) 'JPEG preserves source dimensions'
 $pixel=([Drawing.Bitmap]$decoded).GetPixel(2,2)
 Assert ($pixel.R -gt 245 -and $pixel.G -gt 245 -and $pixel.B -gt 245) 'transparent pixels become white in JPEG'
}finally{$decoded.Dispose()}
foreach($format in @('webp','bmp','tiff','gif','pdf')){
 $result=Run-Worker 'convert' @($png) $format
 Assert ($result.code -eq 0 -and $result.reply.outputs.Count -eq 1 -and (Test-Path -LiteralPath $result.reply.outputs[0])) ('PNG to '+$format+' works')
 if($format -eq 'pdf'){$pdf=$result.reply.outputs[0]}
}
$pdfCaps=Run-Worker 'capabilities' @($pdf)
Assert ($pdfCaps.code -eq 0 -and @($pdfCaps.reply.targets.id) -contains 'png') 'PDF has raster conversion'
$back=Run-Worker 'convert' @($pdf) 'png'
Assert ($back.code -eq 0 -and $back.reply.outputs.Count -eq 1) 'PDF renders through built-in PDFium'
$multiPdf=Join-Path $root 'two-pages.pdf'
Write-TwoPagePdf $multiPdf
$pages=Run-Worker 'convert' @($multiPdf) 'png'
Assert ($pages.code -eq 0 -and $pages.reply.outputs.Count -eq 2) 'multi-page PDF keeps both pages as separate images'
$first=[Drawing.Bitmap]::FromFile($pages.reply.outputs[0])
$second=[Drawing.Bitmap]::FromFile($pages.reply.outputs[1])
try{
 $c1=([Drawing.Bitmap]$first).GetPixel(30,30)
 $c2=([Drawing.Bitmap]$second).GetPixel(30,30)
 Assert ($c1.R -gt 240 -and $c1.B -lt 10 -and $c2.B -gt 240 -and $c2.R -lt 10) 'PDF pages preserve order and rendered content'
}finally{$first.Dispose();$second.Dispose()}
$multiTiff=Run-Worker 'convert' @($multiPdf) 'tiff'
Assert ($multiTiff.code -eq 0 -and $multiTiff.reply.outputs.Count -eq 1) 'multi-page PDF makes one TIFF'
$tiffImage=[Drawing.Image]::FromFile($multiTiff.reply.outputs[0])
try{Assert ($tiffImage.GetFrameCount([Drawing.Imaging.FrameDimension]::Page) -eq 2) 'TIFF output retains both PDF pages'}finally{$tiffImage.Dispose()}
$psd=Join-Path $root 'composite.psd'
Write-MinimalPsd $psd
$flat=Run-Worker 'convert' @($psd) 'png'
Assert ($flat.code -eq 0 -and $flat.reply.outputs.Count -eq 1) 'Photoshop composite converts to one flattened image'
Assert (($flat.reply.files[0].warnings -join ' ') -match '不保留图层') 'Photoshop conversion warns about editable layer loss'
$gif=Join-Path $root 'two-frames.gif'
$gifBytes=[byte[]]@(
  71,73,70,56,57,97,1,0,1,0,128,0,0,255,0,0,0,0,255,
  33,249,4,0,5,0,0,0,44,0,0,0,0,1,0,1,0,0,2,2,68,1,0,
  33,249,4,0,10,0,0,0,44,0,0,0,0,1,0,1,0,0,2,2,76,1,0,59
 )
[IO.File]::WriteAllBytes($gif,$gifBytes)
$animated=Run-Worker 'convert' @($gif) 'webp'
Assert ($animated.code -eq 0) 'animated GIF converts to WebP'
$roundtrip=Run-Worker 'convert' @($animated.reply.outputs[0]) 'gif'
Assert ($roundtrip.code -eq 0) 'animated WebP converts back to GIF'
$anim=[Drawing.Image]::FromFile($roundtrip.reply.outputs[0])
try{Assert ($anim.GetFrameCount([Drawing.Imaging.FrameDimension]::Time) -eq 2) 'animation round-trip retains both frames'}finally{$anim.Dispose()}
$still=Run-Worker 'convert' @($gif) 'png'
Assert ($still.code -eq 0 -and $still.reply.outputs.Count -eq 2 -and ($still.reply.files[0].warnings -join ' ') -match '全部帧') 'animation to still image preserves every frame'
$animationPdf=Run-Worker 'convert' @($gif) 'pdf'
Assert ($animationPdf.code -eq 0 -and $animationPdf.reply.outputs.Count -eq 1) 'animation converts to one multi-page PDF'
$animationPages=Run-Worker 'convert' @($animationPdf.reply.outputs[0]) 'png'
Assert ($animationPages.code -eq 0 -and $animationPages.reply.outputs.Count -eq 2) 'animation PDF preserves every frame as a page'
$mixed=Run-Worker 'capabilities' @($png,$pdf)
Assert (@($mixed.reply.targets.id) -contains 'jpg' -and @($mixed.reply.targets.id) -contains 'pdf') 'mixed selections allow skipping already-matching files'
$text=Join-Path $root 'unsupported.nookunknown'
[IO.File]::WriteAllText($text,'not an image')
$unsupported=Run-Worker 'capabilities' @($text)
Assert ($unsupported.code -eq 0 -and $unsupported.reply.targets.Count -eq 0) 'unsupported formats are not advertised'
$bad=Run-Worker 'convert' @($text) 'png'
Assert ($bad.code -ne 0 -and $bad.reply.outputs.Count -eq 0) 'unsupported conversion fails without output'
$invalidQuality=Run-Worker 'convert' @($png) 'jpg' 101
Assert ($invalidQuality.code -eq 2) 'out-of-range quality is rejected'
$occupied=Run-Worker 'convert' @($png) 'jpg' 90 -Occupied
Assert ($occupied.code -eq 2 -and [IO.File]::ReadAllText((Join-Path $occupied.request.outputDirectory 'preserve.txt')) -eq 'do not overwrite') 'nonempty output is never overwritten'
$cancelled=Run-Worker 'convert' @($png) 'jpg' 90 -Cancel
Assert ($cancelled.code -eq 3 -and $cancelled.reply.status -eq 'cancelled' -and $cancelled.reply.outputs.Count -eq 0) 'cancel before conversion publishes no output'
$largePdf=Join-Path $root 'cancel-many-pages.pdf'
Write-TwoPagePdf $largePdf 1000
$interrupted=Run-Worker 'convert' @($largePdf) 'png' 90 -CancelDuring
Assert ($interrupted.code -eq 3 -and $interrupted.reply.status -eq 'cancelled' -and $interrupted.reply.outputs.Count -eq 0) 'cancellation during PDF conversion publishes no successful result'
$invalid=Run-Worker 'capabilities' @($png) -OverrideId 'invalid-id'
Assert ($invalid.code -eq 2 -and !(Test-Path -LiteralPath (Join-Path $invalid.session 'response.json'))) 'invalid protocol cannot write arbitrary response'
$fake=Join-Path $root 'fake.png'
[IO.File]::WriteAllText($fake,"push graphic-context`nviewbox 0 0 1 1`nfill red`nrectangle 0,0 1,1`npop graphic-context")
$spoof=Run-Worker 'convert' @($fake) 'jpg'
Assert ($spoof.code -ne 0 -and $spoof.reply.outputs.Count -eq 0) 'disallowed coder disguised as PNG fails closed'
$markdown=Join-Path $root 'note.md'
[IO.File]::WriteAllText($markdown,"# Conversion test`n`nAn ordinary note.")
$html=Run-Worker 'convert' @($markdown) 'html'
Assert ($html.code -eq 0 -and [IO.File]::ReadAllText($html.reply.outputs[0]).Contains('Conversion test')) 'Markdown converts to readable HTML'
$note=Join-Path $root 'note.txt'
[IO.File]::WriteAllText($note,'Nook conversion keeps this sentence.')
$docx=Run-Worker 'convert' @($note) 'docx'
Assert ($docx.code -eq 0 -and $docx.reply.outputs.Count -eq 1) 'plain text converts to Word without external Office'
$json=Join-Path $root 'data.json'
[IO.File]::WriteAllText($json,'{"name":"Nook","count":2}')
$yaml=Run-Worker 'convert' @($json) 'yaml'
Assert ($yaml.code -eq 0 -and [IO.File]::ReadAllText($yaml.reply.outputs[0]).Contains('Nook')) 'structured data converts JSON to YAML'
$srt=Join-Path $root 'captions.srt'
[IO.File]::WriteAllText($srt,"1`r`n00:00:01,000 --> 00:00:02,000`r`nNook subtitle`r`n")
$vtt=Run-Worker 'convert' @($srt) 'vtt'
Assert ($vtt.code -eq 0 -and [IO.File]::ReadAllText($vtt.reply.outputs[0]).Contains('Nook subtitle')) 'subtitle text and timing convert'
$svg=Join-Path $root 'vector.svg'
[IO.File]::WriteAllText($svg,'<svg xmlns="http://www.w3.org/2000/svg" width="24" height="16"><rect width="24" height="16" fill="#ff0000"/></svg>')
$svgPdf=Run-Worker 'convert' @($svg) 'pdf'
Assert ($svgPdf.code -eq 0 -and $svgPdf.reply.outputs.Count -eq 1) 'SVG converts to vector PDF'
$stl=Join-Path $root 'triangle.stl'
[IO.File]::WriteAllText($stl,"solid triangle`nfacet normal 0 0 1`nouter loop`nvertex 0 0 0`nvertex 1 0 0`nvertex 0 1 0`nendloop`nendfacet`nendsolid triangle")
$model=Run-Worker 'convert' @($stl) 'glb'
Assert ($model.code -eq 0 -and $model.reply.outputs.Count -eq 1) '3D mesh converts to self-contained GLB'
$modelCaps=Run-Worker 'capabilities' @($stl)
Assert (@($modelCaps.reply.targets.id) -notcontains 'obj') 'dependent OBJ bundles are explicitly withheld until host supports resources'
$slides=Run-Worker 'convert' @($multiPdf) 'pptx'
Assert ($slides.code -eq 0 -and ($slides.reply.files[0].warnings -join ' ') -match '备注') 'PDF to slides reports page-image rather than editable-vector semantics'
$dummyVideo=Join-Path $root 'component-check.mp4'
[IO.File]::WriteAllText($dummyVideo,'Only a capability probe, not a valid video.')
$videoCaps=Run-Worker 'capabilities' @($dummyVideo)
if(!(Test-Path -LiteralPath (Join-Path (Split-Path -Parent $WorkerPath) 'engines\ffmpeg'))){
 Assert ($videoCaps.code -eq 0 -and $videoCaps.reply.targets.Count -eq 0 -and ($videoCaps.reply.missingEngines -join ' ') -match 'FFmpeg') 'missing FFmpeg reports dependency without fake video conversion'
}
$incomplete=Join-Path $root 'worker-without-pdfium'
New-Item -ItemType Directory -Path $incomplete | Out-Null
$workerDirectory=Split-Path -Parent $WorkerPath
Get-ChildItem -LiteralPath $workerDirectory -File | Where-Object {$_.Name -ne 'pdfium.dll'} |
 ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $incomplete}
$originalWorker=$WorkerPath
try{
 $WorkerPath=Join-Path $incomplete 'Nook.FileConversion.exe'
 $missingNative=Run-Worker 'capabilities' @($multiPdf)
 Assert ($missingNative.code -eq 0 -and @($missingNative.reply.targets.id) -notcontains 'png' -and @($missingNative.reply.targets.id) -notcontains 'pptx') 'missing PDFium fails closed without advertising image or slide conversion'
}finally{$WorkerPath=$originalWorker}
Assert ((Get-FileHash -LiteralPath $png -Algorithm SHA256).Hash -eq $originalHash) 'all operations preserve the original bytes'
Assert ($spoof.exited -and $cancelled.exited -and $caps.exited) 'finished and cancelled workers are no longer resident'
Write-Output ('Passed '+$script:passed+' checks. Fixtures and responses: '+$root)
