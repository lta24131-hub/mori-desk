param(
 [Parameter(Mandatory=$true)][ValidateSet('browser-mv3-compat')][string]$Stage,
 [Parameter(Mandatory=$true)][string]$NodePath,
 [string]$BuildDirectory,
 [string]$CanvasDependencyRoot='C:\NookLab\Audit\apps\F5F88075D16C\web-tests\node_modules'
)
$ErrorActionPreference='Stop'
$hardware=Get-CimInstance Win32_ComputerSystem
if($env:USERNAME -ne 'NookTester' -or (($hardware.Manufacturer+' '+$hardware.Model) -notmatch 'QEMU|KVM|Bochs')){throw 'Dedicated NookTester QEMU guest required; nothing started.'}
$package=[IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
if($package -notmatch '^C:\\NookLab\\Audit\\apps\\[a-fA-F0-9]{12}$'){throw 'Hash-addressed VM package required.'}
function NoLink([string]$path){
 for($part=[IO.Path]::GetFullPath($path);$part;$part=[IO.Path]::GetDirectoryName($part)){
  if((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw ('Linked package path refused: '+$part)}
 }
}
NoLink $package
$manifest=Get-Content -Raw -LiteralPath (Join-Path $package 'js-files.json') | ConvertFrom-Json
foreach($item in $manifest.files){
 $relative=[string]$item.path
 if([IO.Path]::IsPathRooted($relative) -or ($relative -split '[/\\]') -contains '..'){throw 'Relative sealed payload required.'}
 $file=[IO.Path]::GetFullPath((Join-Path $package $relative))
 if(!$file.StartsWith($package+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Payload path escapes package.'}
 NoLink $file
 if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $item.sha256){throw ('Payload changed: '+$relative)}
}
$node=[IO.Path]::GetFullPath($NodePath)
if(!(Test-Path -LiteralPath $node -PathType Leaf) -or [IO.Path]::GetFileName($node) -ne 'node.exe'){throw 'Use a real Node executable; Electron fallback is not assumed compatible.'}
$repo=Join-Path $package 'repo'
$run=Join-Path $package ('js-results-'+$Stage+'-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
$results=New-Object 'System.Collections.Generic.List[object]'
$oldUpstream=$env:NOOK_ESEARCH_SOURCE
$oldFixtures=$env:NOOK_SCROLL_FIXTURES
$oldReady=$env:NOOK_VM_BROWSER_READY_TIMEOUT_MS
$env:NOOK_ESEARCH_SOURCE=Join-Path $repo 'outputs\esearch-15.6.0-source'
$env:NOOK_SCROLL_FIXTURES=Join-Path $repo 'outputs\scroll-match-fixtures19'
$env:NOOK_VM_BROWSER_READY_TIMEOUT_MS='120000'
$ok=$false
$failure=$null
function QuoteArg([string]$value){
 if($value.Contains('"') -or $value.EndsWith('\')){throw 'Unexpected quoted or trailing-slash process argument.'}
 return '"'+$value+'"'
}
function InvokeNode([string]$label,[string]$cwd,[string[]]$arguments,[int]$timeoutSeconds=240){
 $start=New-Object Diagnostics.ProcessStartInfo
 $start.FileName=$node
 $start.Arguments=($arguments | ForEach-Object {QuoteArg $_}) -join ' '
 $start.WorkingDirectory=$cwd
 $start.UseShellExecute=$false
 $start.CreateNoWindow=$true
 $start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
 $start.RedirectStandardOutput=$true
 $start.RedirectStandardError=$true
 $process=New-Object Diagnostics.Process
 $process.StartInfo=$start
 $watch=[Diagnostics.Stopwatch]::StartNew()
 $timedOut=$false
 try{
  if(!$process.Start()){throw ('Could not start '+$label)}
  $stdout=$process.StandardOutput.ReadToEndAsync()
  $stderr=$process.StandardError.ReadToEndAsync()
  if(!$process.WaitForExit($timeoutSeconds*1000)){
   $timedOut=$true
   # This PID is the still-running child created above; terminate only its tree.
   & "$env:SystemRoot\System32\taskkill.exe" /PID $process.Id /T /F | Out-Null
   if(!$process.WaitForExit(10000)){throw ('Owned test process did not stop: '+$label)}
  }
  $out=$stdout.GetAwaiter().GetResult()
  $err=$stderr.GetAwaiter().GetResult()
  [IO.File]::WriteAllText((Join-Path $run ($label+'.stdout.txt')),$out)
  [IO.File]::WriteAllText((Join-Path $run ($label+'.stderr.txt')),$err)
  $entry=[ordered]@{label=$label;exitCode=$process.ExitCode;timedOut=$timedOut;elapsedSeconds=$watch.Elapsed.TotalSeconds;arguments=$arguments;counts=$null}
  if($out -match '(?m)^# tests (\d+)\s*$'){
   $counts=[ordered]@{tests=[int]$Matches[1];pass=$null;fail=$null;skipped=$null;cancelled=$null}
   foreach($name in @('pass','fail','skipped','cancelled')){if($out -match ('(?m)^# '+$name+' (\d+)\s*$')){$counts[$name]=[int]$Matches[1]}}
   $entry.counts=$counts
  }
  $results.Add($entry)
  if($timedOut -or $process.ExitCode -ne 0){throw ('Stage failed or timed out: '+$label)}
 }finally{$watch.Stop();$process.Dispose()}
}
try{
 InvokeNode 'node-version' $repo @('--version') 30
 $version=(Get-Content -Raw -LiteralPath (Join-Path $run 'node-version.stdout.txt')).Trim()
 if($version -notmatch '^v(\d+)\.' -or [int]$Matches[1] -lt 20){throw 'Node 20 or later required.'}
 if($Stage -in @('browser-local','browser-update-host','native-protocol')){
  $build=[IO.Path]::GetFullPath($BuildDirectory).TrimEnd('\')
  if(!$build.StartsWith('C:\NookLab\Audit\',[StringComparison]::OrdinalIgnoreCase)){throw 'Isolated guest candidate required.'}
  $exe=Join-Path $build 'Mori.exe'
  NoLink $exe
  if((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -ne $manifest.candidateAppSha256){throw 'Final candidate app hash mismatch.'}
 }
 foreach($name in @('browser-lifecycle.cjs','browser-update-integration.cjs')){InvokeNode ([IO.Path]::GetFileNameWithoutExtension($name)) $repo @((Join-Path $repo ('tests\'+$name))) 300}
 $life=Get-Content -Raw -LiteralPath (Join-Path $run 'browser-lifecycle.stdout.txt')
 $integration=Get-Content -Raw -LiteralPath (Join-Path $run 'browser-update-integration.stdout.txt')
 if(!$life.Contains('PASS: real MV3 same-version generation repair and active-page repair after extension reload; busy injection preserves the same drag, idle injection retires prior collector; drag saves exactly once; Escape closes.')){throw 'Full reviewed lifecycle completion marker missing.'}
 if(!$integration.Contains('PASS real isolated Edge MV3 update integration; fixture: ')){throw 'Full original update integration completion marker missing.'}
 $ok=$true
}catch{$failure=$_.Exception.ToString()}
finally{
 $env:NOOK_ESEARCH_SOURCE=$oldUpstream
 $env:NOOK_SCROLL_FIXTURES=$oldFixtures
 $env:NOOK_VM_BROWSER_READY_TIMEOUT_MS=$oldReady
 [ordered]@{stage=$Stage;passed=$ok;failure=$failure;candidateAppSha256=$manifest.candidateAppSha256;scope=$manifest.scope;results=$results.ToArray();vmOnly=$true;hostExecuted=$false;processCleanupVerified=$false;cleanupNote='VM coordinator must separately verify browser/host child cleanup, especially on failure; this helper does not claim complete process-tree evidence.';dependencyScope='Sealed copied source/TypeScript/Playwright/picture_match; canvas SDK reuses version-checked guest dependency tree, not a full dependency integrity proof.'} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $run 'result.json') -Encoding UTF8
 Write-Output ('JS_RESULT_DIRECTORY='+$run)
}
if(!$ok){throw $failure}
