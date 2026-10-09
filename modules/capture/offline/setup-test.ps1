# Developer feasibility setup, isolated from the installed app and user data.
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot
$testRoot=Join-Path $project 'vendor\offline-research'
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$manifest=Get-Content (Join-Path $PSScriptRoot 'catalog.json') -Raw | ConvertFrom-Json
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach($artifact in $manifest.Artifacts){
    $archive=Join-Path $testRoot ($artifact.Id+'.zip')
    if(!(Test-Path -LiteralPath $archive)){
        & curl.exe -fsSL --connect-timeout 15 --max-time 180 -o $archive $artifact.Url
        if($LASTEXITCODE -ne 0){throw ('Download failed: '+$artifact.Id)}
    }
    if((Get-Item -LiteralPath $archive).Length -ne $artifact.Bytes -or (Get-FileHash -LiteralPath $archive).Hash -ne $artifact.Sha256){throw ('Hash failed: '+$artifact.Id)}
    $destination=Join-Path $testRoot 'runtime'
    if($artifact.Id -eq 'en_zh' -or $artifact.Id -eq 'zh_en'){$destination=Join-Path $testRoot 'models'}
    if($artifact.Id -ne 'python' -and $artifact.Id -ne 'en_zh' -and $artifact.Id -ne 'zh_en'){$destination=Join-Path $destination 'packages'}
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $zip=[IO.Compression.ZipFile]::OpenRead($archive)
    foreach($entry in $zip.Entries){
        $target=[IO.Path]::GetFullPath((Join-Path $destination $entry.FullName))
        if(!$target.StartsWith([IO.Path]::GetFullPath($destination)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Archive escaped extraction root'}
        if(!$entry.Name){New-Item -ItemType Directory -Path $target -Force | Out-Null;continue}
        New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$target,$true)
    }
    $zip.Dispose()
    Write-Output ($artifact.Id+' verified')
}
