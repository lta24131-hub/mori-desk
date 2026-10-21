param([Parameter(Mandatory=$true)][string]$SourceRoot)
$ErrorActionPreference='Stop'
$path=Join-Path $SourceRoot 'src\Filee.Engines\Infrastructure\EngineEnvironment.cs'
if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne 'A97D47AC8E8B7271BD8C534AD7F09CF96A065E4EC242460D73F7823A5A19EA11'){
 throw 'The reviewed EngineEnvironment source differs; do not apply the Nook discovery adapter.'
}
$source=[IO.File]::ReadAllText($path).Replace("`r`n","`n")
$before=@'
    public static string? FindBundled(string name)
    {
        var local = Path.Combine(AppContext.BaseDirectory, "engines", name);
        if (Directory.Exists(local))
            return local;
        var downloaded = Path.Combine(DownloadRoot, name);
        if (Directory.Exists(downloaded))
            return downloaded;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; dir is not null && depth < 8; depth++, dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "Filee.slnx")))
                continue;
            var candidate = Path.Combine(dir.FullName, "engines", name);
            return Directory.Exists(candidate) ? candidate : null;
        }
        return null;
    }
'@
$after=@'
    public static string? FindBundled(string name)
    {
        // Nook adapter: the component store provides explicit, verified immutable
        // roots. This also covers rhwp, 7-Zip and JRE, whose callers only use this
        // method instead of OwnProgram. Never discover a developer checkout or an
        // unrelated app's downloaded engines.
        if (OwnCopies.TryGetValue(name, out var managed))
            return Directory.Exists(managed) ? managed : null;
        var local = Path.Combine(AppContext.BaseDirectory, "engines", name);
        return Directory.Exists(local) ? local : null;
    }
'@
if(!$source.Contains($before)){throw 'The reviewed discovery method was not found.'}
[IO.File]::WriteAllText($path,$source.Replace($before,$after),(New-Object Text.UTF8Encoding($false)))

$installerPath=Join-Path $SourceRoot 'src\Filee.Engines\Infrastructure\EngineInstaller.cs'
if((Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash -ne '576707339306A11A5EABA46377CEDD55C060D4918C79648B70F585269D82A9AC'){
 throw 'The reviewed EngineInstaller source differs; do not adapt its MSI log path.'
}
$installer=[IO.File]::ReadAllText($installerPath)
$original='Path.Combine(Path.GetTempPath(), "filee-msiexec.log")'
$replacement='Path.Combine(_root, "nook-msiexec-" + component.Id + ".log")'
if(!$installer.Contains($original)){throw 'The reviewed MSI logging location was not found.'}
[IO.File]::WriteAllText($installerPath,$installer.Replace($original,$replacement),(New-Object Text.UTF8Encoding($false)))
