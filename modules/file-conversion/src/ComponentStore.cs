using Filee.Engines.Infrastructure;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nook.FileConversion;

/// <summary>
/// Store coordination is temporarily busy, not corrupt or unavailable. Callers
/// must not silently bypass its lease and start a managed conversion.
/// </summary>
internal sealed class ComponentStoreBusyException(string message, Exception innerException)
    : IOException(message, innerException) { }

/// <summary>
/// Private app-approved component store. Each package owns an immutable snapshot
/// including its dependencies; an active pointer is the only mutable state.
/// No package removal ever traverses another package's snapshot.
/// </summary>
internal sealed class ComponentStore
{
    private const string OwnerFile = ".nook-component-store";
    private const string SnapshotFile = ".nook-component-snapshot.json";
    private const string OwnerPrefix = "Nook-ConversionComponents-v1:";
    private readonly string _root;
    private readonly string _owner;
    private readonly IReadOnlyList<ComponentPackageDefinition> _catalog;
    private readonly IComponentPackageInstaller _installer;
    private readonly ConcurrentDictionary<string, byte> _integrityFailures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal ComponentStore(string? root = null) : this(
        root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nook", "ConversionComponents"),
        ComponentCatalog.Create(), new ComponentPackageInstaller()) { }

    // In-process test seam only. The request protocol cannot provide a catalog,
    // installer, hash, URL, version or executable path.
    internal ComponentStore(string root, IReadOnlyList<ComponentPackageDefinition> catalog, IComponentPackageInstaller installer)
    {
        _root = ComponentPaths.LocalRoot(root);
        ComponentCatalog.Validate(catalog);
        _catalog = catalog;
        _installer = installer;
        _owner = Initialize();
    }

    internal IReadOnlyList<ComponentPackageState> List()
    {
        EnsureOwner();
        using var lease = AcquireReadLease();
        var manifest = ReadManifest();
        return _catalog.Select(package =>
        {
            manifest.Entries.TryGetValue(package.Id, out var entry);
            var valid = false;
            var newer = false;
            var legacy = false;
            var message = "按需安装；只下载当前版本 Nook 已批准的组件。";
            if (entry is not null)
            {
                try
                {
                    var snapshot = ValidateSnapshot(entry, false);
                    valid = true;
                    newer = RequiresNewerApp(package, entry, snapshot);
                    legacy = string.IsNullOrEmpty(entry.IntegritySha256);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
                { message = "组件文件不完整，可以重新安装修复：" + ex.Message; }
            }
            var state = entry is null ? "missing" : !valid ? "broken" : newer ? "newer_version"
                : entry.Fingerprint == package.Fingerprint && !legacy ? "installed" : "update-available";
            if (valid) message = state == "installed" ? "已是当前 Nook 批准的版本。"
                : "有当前 Nook 批准的兼容版本可更新，更新失败会保留旧版。";
            if (newer) message = "该组件由较新版本 Nook 安装；请更新 Nook 后使用，不会降级或覆盖现有组件。";
            else if (valid && legacy) message = "旧版组件尚无关键文件完整性清单，暂保留兼容使用；请点更新重新安装并建立校验。原文件和转换结果不受影响。";
            return new ComponentPackageState(package.Id, package.Name, package.Description,
                entry?.Version ?? "", package.Version, package.DownloadBytes, entry?.InstalledBytes ?? package.InstalledBytes,
                state, entry is not null, package.Components.Skip(1).Select(c => c.Id).ToArray(), message);
        }).ToArray();
    }

    internal IDisposable AcquireReadLease()
    {
        EnsureOwner();
        return OpenLease(".use.lock", false);
    }

    // Caller holds AcquireReadLease until its child conversion process has exited.
    // "jre" is included explicitly for the reviewed EngineEnvironment adapter.
    internal IReadOnlyDictionary<string, string> GetOwnCopies()
    {
        EnsureOwner();
        var result = new Dictionary<string, string>();
        foreach (var entry in ReadManifest().Entries.Values)
        {
            try
            {
                var snapshot = ValidateSnapshot(entry, false, verifyContents: true);
                if (RequiresNewerApp(Package(entry.Id), entry, snapshot)) continue;
                var path = SnapshotPath(entry);
                foreach (var id in snapshot.Components.Keys.Where(id => id is not ("h2orestart" or "vcruntime")))
                    result[id] = ComponentPaths.Child(path, id);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
            {
                if (ex is InvalidDataException or JsonException) _integrityFailures.TryAdd(entry.Snapshot, 0);
                // A broken component must not be executed or hide built-in routes.
                // List() reports the repair action and the exact local problem.
            }
        }
        return result;
    }

    internal Task InstallAsync(string id, IProgress<ComponentOperationProgress>? progress, CancellationToken token) =>
        InstallOrUpdateAsync(id, progress, token);

    internal Task UpdateAsync(string id, IProgress<ComponentOperationProgress>? progress, CancellationToken token) =>
        InstallOrUpdateAsync(id, progress, token);

    private async Task InstallOrUpdateAsync(string id, IProgress<ComponentOperationProgress>? progress, CancellationToken token)
    {
        var package = Package(id);
        token.ThrowIfCancellationRequested();
        using var manager = OpenLease(".management.lock", true);
        EnsureOwner();
        // Fail promptly if a conversion is already using the store. During the
        // network stage conversions can still start; commit rechecks exclusively.
        using (OpenLease(".use.lock", true)) { }
        CleanAbandonedStages();
        var manifest = ReadManifest();
        if (manifest.Entries.TryGetValue(id, out var installed))
        {
            if (CompareVersions(installed.Version, package.Version) > 0)
                throw new IOException("已有较新组件，请更新 Nook 后管理；当前操作不会降级。");
            try
            {
                if (RequiresNewerApp(package, installed, ValidateSnapshot(installed, false)))
                    throw new NewerComponentException();
            }
            catch (NewerComponentException)
            {
                throw new IOException("已有较新组件或运行库，请更新 Nook 后管理；当前操作不会降级。");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException) { }
        }
        if (manifest.Entries.TryGetValue(id, out var existing) && existing.Fingerprint == package.Fingerprint)
        {
            try
            {
                ValidateSnapshot(existing, false, verifyContents: true);
                if (string.IsNullOrEmpty(existing.IntegritySha256))
                    throw new InvalidDataException("旧版组件需要重新安装并建立完整性校验。");
                progress?.Report(new(id, "complete", package.DownloadBytes, package.DownloadBytes, "已是当前批准版本。"));
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException) { }
        }
        var stage = ComponentPaths.Child(_root, Path.Combine(".staging", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(stage);
        var snapshot = new SnapshotInfo
        {
            Owner = _owner, Id = id, Fingerprint = package.Fingerprint,
            Components = package.Components.ToDictionary(c => c.Id, c => c.Sha256.ToUpperInvariant()),
            Versions = package.Components.ToDictionary(c => c.Id, c => c.Version)
        };
        WriteDurable(Path.Combine(stage, SnapshotFile), snapshot);
        string? newVersion = null;
        var committed = false;
        try
        {
            await _installer.RestoreAsync(package, stage, progress, token);
            token.ThrowIfCancellationRequested();
            progress?.Report(new(id, "verifying", package.DownloadBytes, package.DownloadBytes, "正在检查组件完整性。"));
            var bytes = ComponentPaths.InspectTree(stage);
            var candidate = new Entry
            {
                Id = id, Version = package.Version, Fingerprint = package.Fingerprint, InstalledBytes = bytes,
                Snapshot = Path.Combine(".staging", Path.GetFileName(stage))
            };
            var stagedInfo = ValidateSnapshotAt(candidate, stage, true);
            if (stagedInfo.Components.Count != package.Components.Count
                || package.Components.Any(component => !stagedInfo.Components.TryGetValue(component.Id, out var hash)
                    || !string.Equals(hash, component.Sha256, StringComparison.OrdinalIgnoreCase)
                    || !stagedInfo.Versions.TryGetValue(component.Id, out var version) || version != component.Version))
                throw new InvalidDataException("组件依赖或版本与批准清单不一致。");
            candidate.IntegritySha256 = await ComponentIntegrity.CreateAsync(stage, _owner, id, package.Fingerprint,
                RequiredPrograms(stagedInfo, stage), token);
            candidate.InstalledBytes = ComponentPaths.InspectTree(stage);
            // CreateAsync just read and hashed the critical files. Recheck the
            // anchored manifest/metadata here without re-reading all binaries.
            ValidateSnapshotAt(candidate, stage, false);
            token.ThrowIfCancellationRequested();
            using var publishLease = OpenLease(".use.lock", true);
            EnsureOwner();
            // Only this writer can mutate the manifest, but read again so even an
            // interrupted or externally changed manifest is never overwritten blindly.
            manifest = ReadManifest();
            newVersion = ComponentPaths.Child(_root, Path.Combine("versions", id, Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(Path.GetDirectoryName(newVersion)!);
            ComponentPaths.NoLinks(newVersion);
            Directory.Move(stage, newVersion);
            candidate.Snapshot = Path.GetRelativePath(_root, newVersion);
            token.ThrowIfCancellationRequested();
            manifest.Entries[id] = candidate;
            CommitManifest(manifest);
            committed = true;
            // Once the pointer is committed, cancellation must not report that
            // installation was rolled back. Old snapshot cleanup is best effort.
            CleanUnreferencedVersions(manifest);
            progress?.Report(new(id, "complete", package.DownloadBytes, package.DownloadBytes, "组件已就绪。"));
        }
        finally
        {
            TryDeleteOwnedSnapshot(stage);
            if (!committed && newVersion is not null) TryDeleteOwnedSnapshot(newVersion);
        }
    }

    internal void Uninstall(string id)
    {
        Package(id); // Dependencies cannot be uninstalled independently.
        using var manager = OpenLease(".management.lock", true);
        using var usage = OpenLease(".use.lock", true);
        EnsureOwner();
        var manifest = ReadManifest();
        if (!manifest.Entries.Remove(id)) return; // Idempotent, including after app restart.
        CommitManifest(manifest);
        CleanUnreferencedVersions(manifest);
        CleanAbandonedStages();
    }

    private ComponentPackageDefinition Package(string id) =>
        _catalog.SingleOrDefault(p => p.Id == id) ?? throw new InvalidDataException("未知组件；依赖项不能单独修改。");

    private static bool RequiresNewerApp(ComponentPackageDefinition package, Entry entry, SnapshotInfo snapshot)
    {
        if (CompareVersions(entry.Version, package.Version) > 0) return true;
        foreach (var component in package.Components)
            if (snapshot.Versions.TryGetValue(component.Id, out var installed)
                && CompareVersions(installed, component.Version) > 0) return true;
        return false;
    }

    private static int CompareVersions(string installed, string approved)
    {
        if (installed == approved) return 0;
        // Unknown version syntax cannot authorize a downgrade.
        if (!Version.TryParse(installed, out var previous) || !Version.TryParse(approved, out var current)) return 1;
        return previous.CompareTo(current);
    }

    private sealed class NewerComponentException : Exception { }

    private string Initialize()
    {
        ComponentPaths.NoLinks(_root);
        if (Directory.Exists(_root) && !File.Exists(Path.Combine(_root, OwnerFile))
            && Directory.EnumerateFileSystemEntries(_root).Any(p => Path.GetFileName(p) != ".initialize.lock"))
            throw new IOException("组件目录包含未知文件，未接管或修改该目录。");
        Directory.CreateDirectory(_root);
        using var initialize = OpenInitializeLease();
        var ownerPath = ComponentPaths.Child(_root, OwnerFile);
        if (!File.Exists(ownerPath))
        {
            if (Directory.EnumerateFileSystemEntries(_root).Any(p => Path.GetFileName(p) != ".initialize.lock"))
                throw new IOException("组件目录包含未知文件，未接管或删除该目录。");
            var owner = OwnerPrefix + Guid.NewGuid().ToString("N");
            using var output = new FileStream(ownerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var data = System.Text.Encoding.UTF8.GetBytes(owner);
            output.Write(data);
            output.Flush(true);
        }
        var text = ReadSmall(ownerPath, 256).Trim();
        if (!Regex.IsMatch(text, "^" + OwnerPrefix + "[a-f0-9]{32}$"))
            throw new IOException("组件目录归属标记无效，未修改任何文件。");
        foreach (var name in new[] { ".management.lock", ".use.lock" })
        {
            var path = ComponentPaths.Child(_root, name);
            if (!File.Exists(path))
                using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
        }
        return text;
    }

    private FileStream OpenInitializeLease()
    {
        var path = ComponentPaths.Child(_root, ".initialize.lock");
        for (var attempt = 0; ; attempt++)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when (IsSharingViolation(ex) && attempt < 20) { Thread.Sleep(15); }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                throw new ComponentStoreBusyException("组件目录正在初始化，请稍后重试。", ex);
            }
        }
    }

    private static bool IsSharingViolation(IOException exception) =>
        (exception.HResult & 0xffff) is 32 or 33; // Win32 sharing/lock violation.

    private void EnsureOwner()
    {
        ComponentPaths.NoLinks(_root);
        if (ReadSmall(ComponentPaths.Child(_root, OwnerFile), 256).Trim() != _owner)
            throw new IOException("组件目录归属发生变化，操作已停止。");
    }

    private FileStream OpenLease(string name, bool write)
    {
        ComponentPaths.NoLinks(_root);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(ComponentPaths.Child(_root, name), FileMode.Open,
                    write ? FileAccess.ReadWrite : FileAccess.Read, write ? FileShare.None : FileShare.Read);
            }
            catch (IOException ex) when (IsSharingViolation(ex) && write && name == ".use.lock" && attempt < 20)
            {
                // Brief inventory reads must not waste a completed download.
                // A long-running conversion still gets a bounded, clear busy result.
                Thread.Sleep(15);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                throw new ComponentStoreBusyException(name == ".management.lock"
                    ? "已有组件安装或维护任务，请完成后重试。"
                    : "组件正在转换或维护中，请任务结束后重试。", ex);
            }
        }
    }

    private StoreManifest ReadManifest()
    {
        var path = ComponentPaths.Child(_root, "active.json");
        if (!File.Exists(path)) return new() { Owner = _owner };
        var manifest = JsonSerializer.Deserialize<StoreManifest>(ReadSmall(path, 262_144), Json)
            ?? throw new InvalidDataException("组件清单损坏。");
        if (manifest.Schema != 1 || manifest.Owner != _owner || manifest.Entries is null || manifest.Entries.Count > 20)
            throw new InvalidDataException("组件清单归属或版本无效。");
        foreach (var pair in manifest.Entries)
        {
            var entry = pair.Value;
            Package(pair.Key);
            if (entry is null || entry.Id != pair.Key || string.IsNullOrEmpty(entry.Fingerprint)
                || !Regex.IsMatch(entry.Fingerprint, "^[A-Fa-f0-9]{64}$")
                || string.IsNullOrEmpty(entry.Version) || entry.Version.Length > 80
                || (!string.IsNullOrEmpty(entry.IntegritySha256) && !Regex.IsMatch(entry.IntegritySha256, "^[A-Fa-f0-9]{64}$"))
                || entry.InstalledBytes is < 0 or > 5_000_000_000 || string.IsNullOrEmpty(entry.Snapshot)
                || !Regex.IsMatch(entry.Snapshot.Replace('\\', '/'), "^versions/" + pair.Key + "/[a-f0-9]{32}$"))
                throw new InvalidDataException("组件清单路径无效。");
            SnapshotPath(entry);
        }
        return manifest;
    }

    private string SnapshotPath(Entry entry) => ComponentPaths.Child(_root, entry.Snapshot);

    private SnapshotInfo ValidateSnapshot(Entry entry, bool inspectTree, bool verifyContents = false) =>
        ValidateSnapshotAt(entry, SnapshotPath(entry), inspectTree, verifyContents);

    private SnapshotInfo ValidateSnapshotAt(Entry entry, string path, bool inspectTree, bool verifyContents = false)
    {
        if (_integrityFailures.ContainsKey(entry.Snapshot))
            throw new InvalidDataException("组件关键文件校验失败，请重新安装修复。");
        ComponentPaths.NoLinks(path);
        var info = JsonSerializer.Deserialize<SnapshotInfo>(ReadSmall(ComponentPaths.Child(path, SnapshotFile), 16_384), Json)
            ?? throw new InvalidDataException("组件快照标记丢失。");
        if (info.Owner != _owner || info.Id != entry.Id || info.Fingerprint != entry.Fingerprint
            || info.Components is null || info.Components.Count is < 1 or > 10 || !info.Components.ContainsKey(entry.Id)
            || info.Versions is null || info.Versions.Count > 10)
            throw new InvalidDataException("组件快照归属无效。");
        var allowed = _catalog.SelectMany(p => p.Components).Select(c => c.Id).ToHashSet();
        foreach (var component in info.Components)
        {
            if (!allowed.Contains(component.Key) || string.IsNullOrEmpty(component.Value)
                || !Regex.IsMatch(component.Value, "^[A-Fa-f0-9]{64}$"))
                throw new InvalidDataException("组件依赖清单无效。");
            var relative = component.Key == "h2orestart"
                ? Path.Combine("libreoffice", "share", "extensions", "H2Orestart") : component.Key;
            var directory = ComponentPaths.Child(path, relative);
            var marker = ReadSmall(ComponentPaths.Child(directory, ".filee-component"), 128).Trim();
            if (!string.Equals(marker, component.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("组件校验标记不一致：" + component.Key);
            if (EngineEnvironment.OwnCopyPrograms.ContainsKey(component.Key))
            {
                var programs = EngineEnvironment.FolderWithPrograms(component.Key, directory)
                    ?? throw new InvalidDataException("组件缺少程序文件：" + component.Key);
                ComponentPaths.NoLinks(programs);
                foreach (var program in EngineEnvironment.OwnCopyPrograms[component.Key])
                    ComponentPaths.NoLinks(Path.Combine(programs, program));
            }
            else if (component.Key == "jre")
            {
                var java = ComponentPaths.Child(directory, Path.Combine("bin", "java.exe"));
                if (!File.Exists(java)) throw new InvalidDataException("Office 组件缺少 Java 运行库。");
            }
        }
        if (inspectTree) ComponentPaths.InspectTree(path);
        if (!string.IsNullOrEmpty(entry.IntegritySha256))
            ComponentIntegrity.Validate(path, _owner, entry.Id, entry.Fingerprint, entry.IntegritySha256,
                RequiredPrograms(info, path), verifyContents);
        return info;
    }

    private static IEnumerable<string> RequiredPrograms(SnapshotInfo snapshot, string path)
    {
        foreach (var id in snapshot.Components.Keys)
        {
            var relative = id == "h2orestart" ? Path.Combine("libreoffice", "share", "extensions", "H2Orestart") : id;
            var directory = ComponentPaths.Child(path, relative);
            if (EngineEnvironment.OwnCopyPrograms.TryGetValue(id, out var names))
            {
                var programs = EngineEnvironment.FolderWithPrograms(id, directory)
                    ?? throw new InvalidDataException("组件缺少程序文件：" + id);
                foreach (var name in names) yield return ComponentPaths.Child(programs, name);
            }
            else if (id == "jre") yield return ComponentPaths.Child(directory, Path.Combine("bin", "java.exe"));
        }
    }

    private void CommitManifest(StoreManifest manifest)
    {
        EnsureOwner();
        var temporary = ComponentPaths.Child(_root, "active-" + Guid.NewGuid().ToString("N") + ".tmp");
        var target = ComponentPaths.Child(_root, "active.json");
        try
        {
            WriteDurable(temporary, manifest);
            if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WriteDurable<T>(string path, T value)
    {
        ComponentPaths.NoLinks(path);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(output, value, Json);
        output.Flush(true);
    }

    private static string ReadSmall(string path, long maxBytes)
    {
        ComponentPaths.NoLinks(path);
        if (!File.Exists(path) || new FileInfo(path).Length > maxBytes)
            throw new InvalidDataException("组件标记缺失或大小异常。");
        return File.ReadAllText(path);
    }

    private void CleanAbandonedStages()
    {
        try
        {
            var parent = ComponentPaths.Child(_root, ".staging");
            if (!Directory.Exists(parent)) return;
            foreach (var directory in Directory.EnumerateDirectories(parent)) TryDeleteOwnedSnapshot(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Cleanup is best effort, including its parent enumeration. A changed
            // cache must not turn a completed uninstall into an apparent failure.
        }
    }

    private void CleanUnreferencedVersions(StoreManifest manifest)
    {
        try
        {
            var retained = manifest.Entries.Values.Select(e => SnapshotPath(e)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var package in _catalog)
            {
                var parent = ComponentPaths.Child(_root, Path.Combine("versions", package.Id));
                if (!Directory.Exists(parent)) continue;
                foreach (var directory in Directory.EnumerateDirectories(parent))
                    if (!retained.Contains(directory)) TryDeleteOwnedSnapshot(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // The active pointer has already been committed. Leave uncertain
            // paths alone and retry safe cleanup on the next maintenance run.
        }
    }

    private void TryDeleteOwnedSnapshot(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            var safe = ComponentPaths.Child(_root, Path.GetRelativePath(_root, directory));
            var snapshot = JsonSerializer.Deserialize<SnapshotInfo>(
                ReadSmall(ComponentPaths.Child(safe, SnapshotFile), 16_384), Json);
            if (snapshot?.Owner != _owner || !_catalog.Any(p => p.Id == snapshot.Id)) return;
            ComponentPaths.DeleteTree(_root, safe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            // A locked/changed owned cache can be reclaimed at the next maintenance
            // operation. Never follow a link, remove unowned data, or roll back a
            // successful pointer switch because cleanup was unavailable.
        }
    }

    private sealed class StoreManifest
    {
        public int Schema { get; set; } = 1;
        public string Owner { get; set; } = "";
        public Dictionary<string, Entry> Entries { get; set; } = [];
    }

    private sealed class Entry
    {
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public string Snapshot { get; set; } = "";
        public long InstalledBytes { get; set; }
        public string IntegritySha256 { get; set; } = "";
    }

    private sealed class SnapshotInfo
    {
        public string Owner { get; set; } = "";
        public string Id { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public Dictionary<string, string> Components { get; set; } = [];
        public Dictionary<string, string> Versions { get; set; } = [];
    }
}
