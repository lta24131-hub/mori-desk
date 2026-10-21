using Filee.Engines.Infrastructure;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Nook.FileConversion;

internal sealed record ComponentPackageState(
    string Id, string Name, string Description, string InstalledVersion, string AvailableVersion,
    long DownloadBytes, long InstalledBytes, string State, bool CanUninstall, string[] Dependencies, string Message);

internal sealed record ComponentOperationProgress(string Id, string Phase, long BytesReceived, long TotalBytes, string Message);

internal sealed record ComponentPackageDefinition(
    string Id, string Name, string Description, IReadOnlyList<EngineComponent> Components, long InstalledBytes)
{
    internal string Version => Components[0].Version;
    internal long DownloadBytes => Components.Sum(c => c.Size);
    internal string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("\n", Components.Select(c => c.Id + ":" + c.Version + ":" + c.Sha256.ToUpperInvariant())))));
}

internal static class ComponentCatalog
{
    // App-reviewed embedded pins only. No remotely mutable catalog or "latest"
    // engine lookup: a later Nook release approves new compatible versions.
    internal static IReadOnlyList<ComponentPackageDefinition> Create()
    {
        if (!OperatingSystem.IsWindows() || EngineDownloads.RuntimeIdentifier != "win-x64")
            throw new PlatformNotSupportedException("转换组件目前支持 Windows x64。");
        var components = EngineDownloads.Components;
        ComponentPackageDefinition Make(string id, string name, string description, long bytes, params string[] dependencies) =>
            new(id, name, description, new[] { id }.Concat(dependencies)
                .Select(key => ComponentNetworkPolicy.ReviewedDownload(components[key])).ToArray(), bytes);
        return [
            Make("ffmpeg", "视频与音频", "视频格式转换、音频转换及音轨提取。", 272_000_000),
            Make("libreoffice", "Office 文档", "Word、Excel、PowerPoint 与 OpenDocument 转换。", 1_265_000_000, "h2orestart", "jre"),
            Make("calibre", "电子书", "EPUB、MOBI、AZW3 等电子书格式转换。", 663_000_000),
            Make("pandoc", "文本与文档", "Markdown、DOCX、文本等文档转换。", 236_000_000),
            Make("ghostscript", "PostScript", "EPS、PS 与 PDF 转换。", 31_000_000, "vcruntime"),
            Make("7zip", "压缩文件", "常见压缩包格式转换。", 7_000_000),
            Make("rhwp", "韩文文档", "HWP、HWPX 文档转换。", 60_000_000)
        ];
    }

    internal static void Validate(IReadOnlyList<ComponentPackageDefinition> packages)
    {
        if (packages.Count is < 1 or > 20 || packages.Select(p => p.Id).Distinct().Count() != packages.Count)
            throw new InvalidDataException("组件清单无效。");
        foreach (var package in packages)
        {
            if (!Regex.IsMatch(package.Id, "^[a-z0-9-]{1,32}$") || package.Components.Count is < 1 or > 10
                || package.Components[0].Id != package.Id)
                throw new InvalidDataException("组件编号无效。");
            foreach (var component in package.Components)
                if (!Regex.IsMatch(component.Id, "^[a-z0-9-]{1,32}$") || component.Size is <= 0 or > 2_000_000_000
                    || !Regex.IsMatch(component.Sha256, "^[a-fA-F0-9]{64}$")
                    || !Uri.TryCreate(component.Url, UriKind.Absolute, out var url)
                    || url.Scheme != "https" || url.Port != 443 || url.UserInfo.Length != 0 || url.Fragment.Length != 0)
                    throw new InvalidDataException("组件下载校验信息无效。");
        }
    }

    internal static string ComponentDirectory(string root, EngineComponent component) =>
        component.Id == "h2orestart"
            ? Path.Combine(root, "libreoffice", "share", "extensions", "H2Orestart")
            : Path.Combine(root, component.Id);
}

internal interface IComponentPackageInstaller
{
    Task RestoreAsync(ComponentPackageDefinition package, string staging,
        IProgress<ComponentOperationProgress>? progress, CancellationToken token);
}
