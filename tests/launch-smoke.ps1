param([string]$Runtime='routing-candidate',[ValidateSet('image','model','library','duplicate','verify')][string]$Scenario='image')
$ErrorActionPreference='Stop'
$suite=Split-Path $PSScriptRoot -Parent
$runtimePath=(Resolve-Path (Join-Path $suite $Runtime)).Path
$fixture=Join-Path $suite 'test-output\route-smoke'
$data=Join-Path $fixture 'MoriDesk'
$env:MORI_DESK_DATA=$data
$exe=Join-Path $runtimePath 'Mori.exe'
$image=Join-Path $fixture '测试 图片 (2).JPG'
$model=Join-Path $fixture '测试 模型.STL'
$library=Join-Path $fixture 'Library'
if($Scenario -eq 'image'){
 if(Test-Path -LiteralPath $fixture){throw 'Smoke fixtures must start in a new directory.'}
 New-Item -ItemType Directory -Path $fixture | Out-Null
 Add-Type -AssemblyName System.Drawing
 $bitmap=New-Object System.Drawing.Bitmap(640,360)
 $graphics=[System.Drawing.Graphics]::FromImage($bitmap)
 try{$graphics.Clear([System.Drawing.Color]::FromArgb(44,64,76));$graphics.FillRectangle([System.Drawing.Brushes]::LightSeaGreen,120,80,400,200);$bitmap.Save($image,[System.Drawing.Imaging.ImageFormat]::Jpeg)}finally{$graphics.Dispose();$bitmap.Dispose()}
 $child=Start-Process -FilePath $exe -ArgumentList ('"'+$image+'"') -PassThru -WindowStyle Normal
 [pscustomobject]@{Scenario=$Scenario;PID=$child.Id;File=$image}
}elseif($Scenario -eq 'model'){
 Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'route-fixture.stl') -Destination $model
 $child=Start-Process -FilePath $exe -ArgumentList ('"'+$model+'"') -PassThru -WindowStyle Normal
 [pscustomobject]@{Scenario=$Scenario;PID=$child.Id;File=$model}
}elseif($Scenario -eq 'library'){
 $child=Start-Process -FilePath $exe -ArgumentList ('--library "'+$library+'"') -PassThru -WindowStyle Normal
 [pscustomobject]@{Scenario=$Scenario;PID=$child.Id}
}elseif($Scenario -eq 'duplicate'){
 # Both launches must terminate themselves after activating the existing window.
 foreach($root in @($library,($library+'/'))){
  $child=Start-Process -FilePath $exe -ArgumentList ('--library "'+$root+'"') -PassThru -WindowStyle Hidden
  if(!$child.WaitForExit(8000)){throw ('Duplicate launch did not exit: '+$child.Id)}
  if($child.ExitCode -ne 0){throw ('Duplicate launch failed: '+$child.ExitCode)}
 }
 'PASS: normal and trailing-separator launches reused the existing library.'
}elseif($Scenario -eq 'verify'){
 if(Test-Path -LiteralPath (Join-Path $data 'settings.json')){throw 'Viewer changed persistent settings. Run this check before the library scenario.'}
 if(Test-Path -LiteralPath (Join-Path $data 'Library')){throw 'Viewer created a default library.'}
 if(Test-Path -LiteralPath (Join-Path $data 'capture')){throw 'Viewer initialized capture state.'}
 'PASS: viewers did not create a default library, settings or capture data.'
}
