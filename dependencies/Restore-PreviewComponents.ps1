param([string]$CacheDirectory=(Join-Path (Split-Path $PSScriptRoot) 'component-cache'))
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
# Pinned official packages. Select one Windows architecture/framework, never
# copy a whole development package into the user installation.
$packages=@(
 @{Id='magick.net-q8-x64';Version='15.0.0';Hash='96BF50FCBC586E8A149B467A97FB3D1BF09C0362B5D8FF828A9936B23FDC5374';Folder='image-codecs';Entries=@('lib/netstandard20/Magick.NET-Q8-x64.dll','runtimes/win-x64/native/Magick.Native-Q8-x64.dll','Notice.txt')}
 @{Id='magick.net.core';Version='15.0.0';Hash='9936D4EA3ED1991A6672056E8DF47B75B43C00AFA884BFF78E136CD2141798B3';Folder='image-codecs';Entries=@('lib/netstandard20/Magick.NET.Core.dll')}
 @{Id='pdfpig';Version='0.1.16';Hash='D67171846EA8C28F50359137065FEC4514266D7A32B23EAE6C5F2EBED8FFCFC4';Folder='pdf-text';Prefix='lib/net471/'}
 @{Id='microsoft.bcl.hashcode';Version='6.0.0';Hash='F3B9B2BAB0BF8CC717D5FDF6D7AEE3EC54E36D9E85BD41347ACAE161319CBD6B';Folder='pdf-text';Entries=@('lib/net462/Microsoft.Bcl.HashCode.dll')}
 @{Id='system.memory';Version='4.6.0';Hash='3A10042B350CE9E11A1FDF4370668CCF6A452C6FEAFCDE0A556AAA881614385A';Folder='preview-support';Entries=@('lib/net462/System.Memory.dll')}
 @{Id='system.buffers';Version='4.6.0';Hash='736425805075E8895F066B3960BB1308543F41E2A84BAA61D5AF6675192AFC97';Folder='preview-support';Entries=@('lib/net462/System.Buffers.dll')}
 @{Id='system.runtime.compilerservices.unsafe';Version='6.1.0';Hash='372AAAA51707BA6CD2C69B2044382E0F9486C1D50D1C16E8D0E39DCCB4C8CC25';Folder='preview-support';Entries=@('lib/net462/System.Runtime.CompilerServices.Unsafe.dll')}
 @{Id='system.numerics.vectors';Version='4.6.0';Hash='7CA4B7B964361E647AF6F3610C7A8F2D834EB77AA98E25903976431EF444D475';Folder='preview-support';Entries=@('lib/net462/System.Numerics.Vectors.dll')}
)
New-Item -ItemType Directory -Force -Path $CacheDirectory | Out-Null
foreach($package in $packages){
 $file=Join-Path $CacheDirectory ($package.Id+'.'+$package.Version+'.nupkg')
 if(!(Test-Path -LiteralPath $file)){
  Invoke-WebRequest -Uri ('https://api.nuget.org/v3-flatcontainer/'+$package.Id+'/'+$package.Version+'/'+$package.Id+'.'+$package.Version+'.nupkg') -OutFile $file
 }
 if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $package.Hash){throw ('Package checksum mismatch: '+$package.Id)}
 $target=Join-Path $PSScriptRoot $package.Folder
 New-Item -ItemType Directory -Force -Path $target | Out-Null
 $archive=[IO.Compression.ZipFile]::OpenRead($file)
 try{
  if($package.Prefix){$entries=@($archive.Entries | Where-Object {$_.FullName.StartsWith($package.Prefix,[StringComparison]::Ordinal) -and $_.FullName.EndsWith('.dll',[StringComparison]::OrdinalIgnoreCase)})}
  else{$entries=@($package.Entries | ForEach-Object {$entry=$archive.GetEntry($_);if(!$entry){throw ('Missing pinned package entry: '+$_)};$entry})}
  foreach($entry in $entries){[IO.Compression.ZipFileExtensions]::ExtractToFile($entry,(Join-Path $target ([IO.Path]::GetFileName($entry.FullName))),$true)}
  $nuspec=@($archive.Entries | Where-Object {$_.FullName.EndsWith('.nuspec',[StringComparison]::OrdinalIgnoreCase)})[0]
  [IO.Compression.ZipFileExtensions]::ExtractToFile($nuspec,(Join-Path $target ($package.Id+'.nuspec')),$true)
 }finally{$archive.Dispose()}
}
Write-Output 'Restored pinned Windows x64 preview components. No models, VLC or external delegates were installed.'
