param([Parameter(Mandatory=$true)][ValidatePattern('^[a-p]{32}$')][string]$ExtensionId)
$ErrorActionPreference='Stop'
$suiteExe=Join-Path $PSScriptRoot 'Mori.exe'
if(-not(Test-Path -LiteralPath $suiteExe)){$suiteExe=Join-Path $PSScriptRoot 'dist\Mori.exe'}
if(-not(Test-Path -LiteralPath $suiteExe)){throw 'Nook executable not found'}
# A child process may inherit MSIX file/registry virtualization even when it
# has no package identity. Use the same file-handle probe as Nook and Setup,
# before launching a registration writer or claiming registration succeeded.
$suiteAssembly=[Reflection.Assembly]::LoadFrom($suiteExe)
if(![string]::Equals([IO.Path]::GetFullPath($suiteAssembly.Location),[IO.Path]::GetFullPath($suiteExe),[StringComparison]::OrdinalIgnoreCase)){throw 'The loaded Nook assembly is not the selected executable. Close this PowerShell session and retry.'}
$contextType=$suiteAssembly.GetType('MoriDesk.PhysicalUserContext',$false)
if(!$contextType){throw 'This Nook version cannot verify the real Windows user directory. Install the current Nook version before registering its extension.'}
$contextMethod=$contextType.GetMethod('RequireInstalledContext',[Reflection.BindingFlags]'Static,Public,NonPublic')
if(!$contextMethod){throw 'Nook physical user context verification is unavailable.'}
try{$contextMethod.Invoke($null,@()) | Out-Null}catch{if($_.Exception.InnerException){throw $_.Exception.InnerException.Message};throw}
$suiteProcess=Start-Process -FilePath $suiteExe -ArgumentList '--register-extension',$ExtensionId -WindowStyle Hidden -Wait -PassThru
if($suiteProcess.ExitCode -ne 0){throw 'Nook extension registration did not complete.'}
Write-Output 'Nook extension registration completed. The browser connection is confirmed only after the extension contacts Nook.'
