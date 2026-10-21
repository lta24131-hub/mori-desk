param([Parameter(Mandatory=$true)][string]$WorkerPath,[string]$TestRoot)
$ErrorActionPreference='Stop'
$suite=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if(!$TestRoot){$TestRoot=Join-Path $suite ('outputs\conversion-engine-tests\'+[Guid]::NewGuid().ToString('N'))}
$root=[IO.Path]::GetFullPath($TestRoot)
if(Test-Path -LiteralPath $root){throw 'Use a fresh test directory.'}
New-Item -ItemType Directory -Path $root | Out-Null
$WorkerPath=[IO.Path]::GetFullPath($WorkerPath)
$workerRoot=Split-Path -Parent $WorkerPath
$engineRoot=Join-Path $workerRoot 'engines'
$script:passed=0
$script:index=0
function Assert($condition,[string]$message){
 if(!$condition){throw ('FAIL: '+$message)}
 $script:passed++
 Write-Host ('PASS: '+$message)
}
function Run-Program([string]$program,[string[]]$arguments){
 $script:index++
 $stdout=Join-Path $root ('process-'+$script:index+'.stdout.txt')
 $stderr=Join-Path $root ('process-'+$script:index+'.stderr.txt')
 $encoded=@($arguments | ForEach-Object {'"'+$_.Replace('"','\"')+'"'}) -join ' '
 $process=Start-Process -FilePath $program -ArgumentList $encoded -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
 if(!$process.WaitForExit(180000)){$process.Kill($true);throw 'Fixture process exceeded test timeout.'}
 $process.WaitForExit()
 if($process.ExitCode -ne 0){throw ([IO.File]::ReadAllText($stderr))}
 return [IO.File]::ReadAllText($stdout)
}
function Convert-Fixture([string]$source,[string]$target,[switch]$ExpectFailure){
 $id=[Guid]::NewGuid().ToString('N')
 $session=Join-Path $root $id
 New-Item -ItemType Directory -Path $session | Out-Null
 $request=[ordered]@{schemaVersion=1;id=$id;operation='convert';inputs=@($source);target=$target;quality=90;outputDirectory=(Join-Path $session 'output');responsePath=(Join-Path $session 'response.json');componentRoot=(Join-Path $root 'component-store')}
 $requestPath=Join-Path $session 'request.json'
 [IO.File]::WriteAllText($requestPath,($request | ConvertTo-Json -Depth 4),(New-Object Text.UTF8Encoding($false)))
 $stdout=Join-Path $session 'stdout.txt'
 $stderr=Join-Path $session 'stderr.txt'
 $hash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
 $sourceLock=[IO.File]::Open($source,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
 try{
  $process=Start-Process -FilePath $WorkerPath -ArgumentList ('--request "'+$requestPath+'"') -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
  if(!$process.WaitForExit(180000)){$process.Kill($true);throw 'Conversion exceeded 3 minute fixture timeout.'}
  $process.WaitForExit()
 }finally{$sourceLock.Dispose()}
 $reply=[IO.File]::ReadAllText($stdout) | ConvertFrom-Json
 if($ExpectFailure){
  Assert ($process.ExitCode -ne 0 -and $reply.status -eq 'error' -and @($reply.outputs).Count -eq 0) ("unsafe "+[IO.Path]::GetFileName($source)+" rejected without publication")
 }else{
  if($process.ExitCode -ne 0){throw ('Worker: '+$reply.error.message)}
  Assert ($reply.status -eq 'ok' -and @($reply.outputs).Count -gt 0) ([IO.Path]::GetFileName($source)+' -> '+$target+' converts')
 }
 Assert ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -eq $hash) ([IO.Path]::GetFileName($source)+' source bytes unchanged')
 Assert (!(Test-Path -LiteralPath (Join-Path ([IO.Path]::GetTempPath()) ('Nook-ConversionNative\'+$id)))) 'short native workspace is reclaimed after exit'
 return $reply
}
function Fixture([string]$name,[string]$text){
 $path=Join-Path $root $name
 [IO.File]::WriteAllText($path,$text,(New-Object Text.UTF8Encoding($false)))
 return $path
}
function Zip-Fixture([string]$name,[hashtable]$entries){
 Add-Type -AssemblyName System.IO.Compression
 $path=Join-Path $root $name
 $stream=[IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write)
 $archive=New-Object IO.Compression.ZipArchive($stream,[IO.Compression.ZipArchiveMode]::Create,$false)
 try{
  foreach($key in $entries.Keys){
   $entry=$archive.CreateEntry($key)
   $writer=New-Object IO.StreamWriter($entry.Open(),(New-Object Text.UTF8Encoding($false)))
   try{$writer.Write($entries[$key])}finally{$writer.Dispose()}
  }
 }finally{$archive.Dispose();$stream.Dispose()}
 return $path
}
function Odt-Fixture([string]$name,[string]$body,[hashtable]$extras=@{}){
 $entries=@{
  'mimetype'='application/vnd.oasis.opendocument.text'
  'content.xml'=('<?xml version="1.0" encoding="UTF-8"?><office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0" xmlns:svg="urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0" xmlns:script="urn:oasis:names:tc:opendocument:xmlns:script:1.0" xmlns:xlink="http://www.w3.org/1999/xlink" xmlns:dom="http://www.w3.org/2001/xml-events" office:version="1.3">'+$body+'</office:document-content>')
  'META-INF/manifest.xml'='<?xml version="1.0"?><manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0" manifest:version="1.3"><manifest:file-entry manifest:full-path="/" manifest:media-type="application/vnd.oasis.opendocument.text"/><manifest:file-entry manifest:full-path="content.xml" manifest:media-type="text/xml"/><manifest:file-entry manifest:full-path="Basic/Standard/Module1.xml" manifest:media-type="text/xml"/><manifest:file-entry manifest:full-path="Basic/Standard/script-lb.xml" manifest:media-type="text/xml"/><manifest:file-entry manifest:full-path="Basic/script-lc.xml" manifest:media-type="text/xml"/></manifest:manifest>'
 }
 foreach($key in $extras.Keys){$entries[$key]=$extras[$key]}
 return Zip-Fixture $name $entries
}

$ffmpeg=Join-Path $engineRoot 'ffmpeg\bin\ffmpeg.exe'
$ffprobe=Join-Path $engineRoot 'ffmpeg\bin\ffprobe.exe'
if(!(Test-Path -LiteralPath $ffmpeg)){$ffmpeg=Join-Path $engineRoot 'ffmpeg\ffmpeg.exe';$ffprobe=Join-Path $engineRoot 'ffmpeg\ffprobe.exe'}
if(Test-Path -LiteralPath $ffmpeg){
 $video=Join-Path $root 'sample.mp4'
 [void](Run-Program $ffmpeg @('-hide_banner','-loglevel','error','-f','lavfi','-i','testsrc=size=64x48:rate=12','-f','lavfi','-i','sine=frequency=440:sample_rate=44100','-t','1','-c:v','libx264','-pix_fmt','yuv420p','-c:a','aac',$video))
 $webm=Convert-Fixture $video 'webm'
 Assert ((@($webm.targets | Where-Object id -eq 'webm')[0].warning) -match '其他音轨、字幕') 'video stream-loss warning is visible on target before conversion'
 $probe=(Run-Program $ffprobe @('-v','error','-show_streams','-show_format','-of','json',$webm.outputs[0])) | ConvertFrom-Json
 Assert (@($probe.streams.codec_type) -contains 'video' -and @($probe.streams.codec_type) -contains 'audio') 'video conversion retains video and audio tracks'
 Assert ([double]$probe.format.duration -ge 0.9 -and [double]$probe.format.duration -le 1.3) 'video duration remains one second'
 $mp3=Convert-Fixture $video 'mp3'
 $probe=(Run-Program $ffprobe @('-v','error','-show_streams','-show_format','-of','json',$mp3.outputs[0])) | ConvertFrom-Json
 Assert (@($probe.streams.codec_type).Count -eq 1 -and $probe.streams[0].codec_type -eq 'audio') 'video audio extraction creates playable audio only'
 Assert (($mp3.files[0].warnings -join ' ') -match '不包含视频') 'audio extraction describes loss of video before/result'
 $flac=Convert-Fixture $mp3.outputs[0] 'flac'
 $probe=(Run-Program $ffprobe @('-v','error','-show_streams','-of','json',$flac.outputs[0])) | ConvertFrom-Json
 Assert ($probe.streams[0].codec_name -eq 'flac') 'audio-to-audio target is actually FLAC'
}else{Write-Output 'SKIP: FFmpeg component is not bundled.'}

if(Test-Path -LiteralPath (Join-Path $engineRoot 'pandoc')){
 $rst=Fixture 'sample.rst' "Nook conversion`n===============`n`nA self-contained document with **bold** text.`n"
 $docx=Convert-Fixture $rst 'docx'
 Add-Type -AssemblyName System.IO.Compression.FileSystem
 $archive=[IO.Compression.ZipFile]::OpenRead($docx.outputs[0])
 try{
  $entry=$archive.GetEntry('word/document.xml')
  $reader=New-Object IO.StreamReader($entry.Open())
  try{$xml=$reader.ReadToEnd()}finally{$reader.Dispose()}
  Assert ($xml.Contains('Nook conversion') -and $xml.Contains('self-contained')) 'Pandoc document contains expected text'
 }finally{$archive.Dispose()}
}else{Write-Output 'SKIP: Pandoc component is not bundled.'}

if(Test-Path -LiteralPath (Join-Path $engineRoot 'rhwp')){
 $text=Fixture 'sample.txt' 'Nook conversion with a full-page document.'
 $pdf=Convert-Fixture $text 'pdf'
 Assert ([Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($pdf.outputs[0]),0,5) -eq '%PDF-') 'document route through bundled renderer creates a PDF'
}else{Write-Output 'SKIP: rhwp component is not bundled.'}

if(Test-Path -LiteralPath (Join-Path $engineRoot 'ghostscript')){
 $eps=Fixture 'sample.eps' "%!PS-Adobe-3.0 EPSF-3.0`n%%BoundingBox: 0 0 72 48`n1 0 0 setrgbcolor`n0 0 72 48 rectfill`nshowpage`n%%EOF`n"
 $pdf=Convert-Fixture $eps 'pdf'
 Assert ([Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($pdf.outputs[0]),0,5) -eq '%PDF-') 'PostScript converts through bundled Ghostscript'
}else{Write-Output 'SKIP: Ghostscript component is not bundled.'}

if(Test-Path -LiteralPath (Join-Path $engineRoot '7zip')){
 $archiveSource=Join-Path $root 'archive-input'
 New-Item -ItemType Directory -Path $archiveSource | Out-Null
 [IO.File]::WriteAllText((Join-Path $archiveSource 'keep.txt'),'Nook archive content')
 $zip=Join-Path $root 'sample.zip'
 Add-Type -AssemblyName System.IO.Compression.FileSystem
 [IO.Compression.ZipFile]::CreateFromDirectory($archiveSource,$zip)
 $seven=Convert-Fixture $zip '7z'
 $roundtrip=Convert-Fixture $seven.outputs[0] 'zip'
 $archive=[IO.Compression.ZipFile]::OpenRead($roundtrip.outputs[0])
 try{
  $entry=$archive.Entries | Where-Object {$_.Name -eq 'keep.txt'} | Select-Object -First 1
  $reader=New-Object IO.StreamReader($entry.Open())
  try{$text=$reader.ReadToEnd()}finally{$reader.Dispose()}
  Assert ($text -eq 'Nook archive content') 'archive round trip retains file bytes'
 }finally{$archive.Dispose()}
}else{Write-Output 'SKIP: 7-Zip component is not bundled.'}

if(Test-Path -LiteralPath (Join-Path $engineRoot 'libreoffice')){
 $odt=Odt-Fixture 'office.odt' '<office:body><office:text><text:p>Nook Office conversion keeps this sentence.</text:p></office:text></office:body>'
 $doc=Convert-Fixture $odt 'doc'
 $pdf=Convert-Fixture $doc.outputs[0] 'pdf'
 $text=Convert-Fixture $pdf.outputs[0] 'txt'
 Assert ([IO.File]::ReadAllText($text.outputs[0]).Contains('Nook Office conversion')) 'legacy Word route preserves readable text via LibreOffice'
 $csv=Fixture 'office.csv' "Name,Value`r`nNook,42`r`n"
 $xls=Convert-Fixture $csv 'xls'
 $xlsx=Convert-Fixture $xls.outputs[0] 'xlsx'
 $sheet=Convert-Fixture $xlsx.outputs[0] 'csv'
 Assert ([IO.File]::ReadAllText($sheet.outputs[0]).Contains('Nook') -and [IO.File]::ReadAllText($sheet.outputs[0]).Contains('42')) 'legacy Excel round trip preserves cells'
 $marker=Join-Path $root 'macro-must-not-run.txt'
 $macroCode="Sub Main`nOpen "+'"'+$marker+'"'+" For Output As #1`nPrint #1, "+'"macro ran"'+"`nClose #1`nEnd Sub"
 $extras=@{
  'Basic/Standard/Module1.xml'=('<script:module xmlns:script="http://openoffice.org/2000/script" script:name="Module1" script:language="StarBasic">'+[Security.SecurityElement]::Escape($macroCode)+'</script:module>')
  'Basic/Standard/script-lb.xml'='<library:library xmlns:library="http://openoffice.org/2000/library" library:name="Standard" library:readonly="false" library:passwordprotected="false"><library:element library:name="Module1"/></library:library>'
  'Basic/script-lc.xml'='<library:libraries xmlns:library="http://openoffice.org/2000/library"><library:library library:name="Standard" library:link="false"/></library:libraries>'
 }
 $body='<office:scripts><office:event-listeners><script:event-listener script:language="ooo:script" script:event-name="dom:load" xlink:href="vnd.sun.star.script:Standard.Module1.Main?language=Basic&amp;location=document" xlink:type="simple"/></office:event-listeners></office:scripts><office:body><office:text><text:p>Nook macro safety fixture.</text:p></office:text></office:body>'
 $macro=Odt-Fixture 'macro.odt' $body $extras
 [void](Convert-Fixture $macro 'doc')
 Assert (!(Test-Path -LiteralPath $marker)) 'document load-event macro did not execute'
 $listener=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0)
 $listener.Start()
 try{
  $port=([Net.IPEndPoint]$listener.LocalEndpoint).Port
  $body='<office:body><office:text><text:p>External image must not load.</text:p><draw:frame svg:width="1cm" svg:height="1cm"><draw:image xlink:type="simple" xlink:href="http://127.0.0.1:'+$port+'/image.png"/></draw:frame></office:text></office:body>'
  $external=Odt-Fixture 'external-resource.odt' $body
  [void](Convert-Fixture $external 'doc' -ExpectFailure)
  Assert (!$listener.Pending()) 'external document resource was rejected without any HTTP request'
  $variant=Zip-Fixture 'external-template.dotm' @{
   'word/_rels/document.xml.rels'=('<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" TargetMode="External" Target="http://127.0.0.1:'+$port+'/image.png"/></Relationships>')
  }
  $variantReply=Convert-Fixture $variant 'pdf' -ExpectFailure
  Assert ($variantReply.error.message -match '外部图片') 'OOXML macro/template variants use external-resource preflight'
  Assert (!$listener.Pending()) 'macro/template external resource was rejected without any HTTP request'
 }finally{$listener.Stop()}
}else{Write-Output 'SKIP: LibreOffice component is not bundled.'}

if(Test-Path -LiteralPath (Join-Path $engineRoot 'calibre')){
 $text=Fixture 'ebook-source.txt' 'Nook ebook fixture preserves this text across ebook formats.'
 $epub=Convert-Fixture $text 'epub'
 $azw3=Convert-Fixture $epub.outputs[0] 'azw3'
 $epubAgain=Convert-Fixture $azw3.outputs[0] 'epub'
 Add-Type -AssemblyName System.IO.Compression.FileSystem
 $archive=[IO.Compression.ZipFile]::OpenRead($epubAgain.outputs[0])
 try{
  $found=$false
  foreach($entry in $archive.Entries | Where-Object {$_.FullName -match '\.(xhtml|html|htm)$'}){
   $reader=New-Object IO.StreamReader($entry.Open())
   try{if($reader.ReadToEnd().Contains('Nook ebook fixture')){$found=$true}}finally{$reader.Dispose()}
  }
  Assert $found 'calibre AZW3 round trip preserves ebook text'
 }finally{$archive.Dispose()}
}else{Write-Output 'SKIP: calibre component is not bundled.'}

$unsafe=Fixture 'script.md' "# Example`n<script>fetch('https://example.invalid')</script>"
[void](Convert-Fixture $unsafe 'html' -ExpectFailure)
$unsafe=Fixture 'remote-image.md' '# Example ![image](https://example.invalid/pixel.png)'
[void](Convert-Fixture $unsafe 'html' -ExpectFailure)
$unsafe=Fixture 'local-image.md' '# Example ![image](near.png)'
[void](Convert-Fixture $unsafe 'docx' -ExpectFailure)
$unsafe=Fixture 'event.html' '<html><body><img src="data:image/png;base64,AA==" onerror="alert(1)"></body></html>'
[void](Convert-Fixture $unsafe 'docx' -ExpectFailure)
$unsafe=Fixture 'remote-svg.svg' '<svg xmlns="http://www.w3.org/2000/svg" width="24" height="16"><image href="near.png"/></svg>'
[void](Convert-Fixture $unsafe 'svgz' -ExpectFailure)
$unsafe=Fixture 'active-svg.svg' '<svg xmlns="http://www.w3.org/2000/svg" onload="alert(1)" width="24" height="16"/>'
[void](Convert-Fixture $unsafe 'svgz' -ExpectFailure)
$safe=Fixture 'safe.md' '# Safe note with a [normal link](https://example.com/).'
$html=Convert-Fixture $safe 'html'
Assert ([IO.File]::ReadAllText($html.outputs[0]).Contains('Content-Security-Policy')) 'HTML output carries a deny-active-content policy'
$email=Fixture 'sample.eml' "From: sender@example.invalid`r`nTo: receiver@example.invalid`r`nSubject: Nook mail`r`nMIME-Version: 1.0`r`nContent-Type: multipart/mixed; boundary=nook-boundary`r`n`r`n--nook-boundary`r`nContent-Type: text/plain`r`n`r`nMail body remains in original.`r`n--nook-boundary`r`nContent-Type: text/plain; name=keep.txt`r`nContent-Disposition: attachment; filename=keep.txt`r`nContent-Transfer-Encoding: base64`r`n`r`na2VlcCBhdHRhY2htZW50`r`n--nook-boundary--`r`n"
$attachments=Convert-Fixture $email 'zip'
Assert ((@($attachments.targets | Where-Object id -eq 'zip')[0].warning) -match '仅导出邮件附件') 'email attachment-only warning is visible on target before conversion'
Write-Output ('Passed '+$script:passed+' checks. Fixtures: '+$root)
