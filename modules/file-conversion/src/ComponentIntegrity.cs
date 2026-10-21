using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nook.FileConversion;

// This is a corruption/provenance check, not protection against another process
// with the same user's write access to both the store and its active manifest.
internal static class ComponentIntegrity
{
    internal const string FileName = ".nook-component-integrity.json";
    private const int MaximumFiles = 20_000;
    private const int MaximumManifestBytes = 8 * 1024 * 1024;
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".dll", ".com", ".bin", ".pyd", ".jar", ".class", ".py", ".pyc", ".bat", ".cmd", ".ps1", ".so", ".dylib" };
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static async Task<string> CreateAsync(string root, string owner, string id, string fingerprint,
        IEnumerable<string> requiredPrograms, CancellationToken token)
    {
        ComponentPaths.InspectTree(root);
        var required = requiredPrograms.Select(path => Relative(root, path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inventory = new Inventory { Owner = owner, Id = id, Fingerprint = fingerprint };
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var relative = Relative(root, path);
            if (!required.Contains(relative) && !CodeExtensions.Contains(Path.GetExtension(path))) continue;
            ComponentPaths.NoLinks(path);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
            inventory.Files.Add(relative, new KeyFile
            {
                Length = input.Length, Sha256 = hash, LastWriteUtcTicks = File.GetLastWriteTimeUtc(path).Ticks
            });
            if (inventory.Files.Count > MaximumFiles) throw new InvalidDataException("组件关键文件数量异常。");
        }
        if (required.Count == 0 || !required.IsSubsetOf(inventory.Files.Keys))
            throw new InvalidDataException("组件完整性清单缺少必要程序。");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(inventory, Json);
        if (bytes.Length > MaximumManifestBytes) throw new InvalidDataException("组件完整性清单过大。");
        var destination = ComponentPaths.Child(root, FileName);
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await output.WriteAsync(bytes, token);
            output.Flush(true);
        }
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    internal static void Validate(string root, string owner, string id, string fingerprint, string manifestHash,
        IEnumerable<string> requiredPrograms, bool verifyContents)
    {
        var manifestPath = ComponentPaths.Child(root, FileName);
        if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > MaximumManifestBytes)
            throw new InvalidDataException("组件完整性清单丢失或异常，请重新安装修复。");
        byte[] bytes;
        using (var input = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (input.Length > MaximumManifestBytes) throw new InvalidDataException("组件完整性清单过大。");
            bytes = new byte[(int)input.Length];
            input.ReadExactly(bytes);
        }
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), manifestHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("组件完整性清单已改变，请重新安装修复。");
        var inventory = JsonSerializer.Deserialize<Inventory>(bytes, Json)
            ?? throw new InvalidDataException("组件完整性清单无效。");
        if (inventory.Schema != 1 || inventory.Owner != owner || inventory.Id != id || inventory.Fingerprint != fingerprint
            || inventory.Files is null || inventory.Files.Count is < 1 or > MaximumFiles)
            throw new InvalidDataException("组件完整性清单归属无效。");
        var required = requiredPrograms.Select(path => Relative(root, path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (required.Count == 0 || !required.IsSubsetOf(inventory.Files.Keys))
            throw new InvalidDataException("组件完整性清单缺少必要程序。");
        foreach (var pair in inventory.Files)
        {
            var path = ComponentPaths.Child(root, pair.Key);
            if (pair.Key.Length > 1024 || pair.Key != Relative(root, path) || pair.Value is null
                || pair.Value.Length is < 0 or > 5_000_000_000 || pair.Value.LastWriteUtcTicks <= 0
                || !Regex.IsMatch(pair.Value.Sha256 ?? "", "^[A-Fa-f0-9]{64}$"))
                throw new InvalidDataException("组件关键文件清单无效。");
            if (!File.Exists(path) || new FileInfo(path).Length != pair.Value.Length
                || File.GetLastWriteTimeUtc(path).Ticks != pair.Value.LastWriteUtcTicks)
                throw new InvalidDataException("组件关键文件已改变，请重新安装修复：" + pair.Key);
            if (verifyContents)
            {
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length != pair.Value.Length
                    || !string.Equals(Convert.ToHexString(SHA256.HashData(input)), pair.Value.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("组件关键文件校验失败，请重新安装修复：" + pair.Key);
            }
        }
    }

    private static string Relative(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (relative.Length > 1024 || relative != Path.GetRelativePath(root, ComponentPaths.Child(root, relative)).Replace('\\', '/'))
            throw new InvalidDataException("组件关键文件路径无效。");
        return relative;
    }

    private sealed class Inventory
    {
        public int Schema { get; set; } = 1;
        public string Owner { get; set; } = "";
        public string Id { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public Dictionary<string, KeyFile> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
    private sealed class KeyFile
    {
        public long Length { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public string Sha256 { get; set; } = "";
    }
}
