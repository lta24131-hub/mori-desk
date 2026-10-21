using Filee.Engines.Infrastructure;
using Nook.FileConversion;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

// Generated component trees, synthetic HTTP and a new fixture root only.
// None of the synthetic ".exe" files is launched. No desktop, actual download,
// installer, real component root, application entry or user material is touched.
internal static class Program
{
    static string Root = "";
    static int Checks;
    static readonly CancellationToken None = CancellationToken.None;
    static void Check(bool value, string message)
    {
        if (!value) throw new Exception("FAIL " + message);
        Checks++;
        Console.WriteLine("PASS " + message);
    }
    static async Task Reject(Func<Task> action, string message)
    {
        try { await action(); }
        catch { Check(true, message); return; }
        throw new Exception("FAIL " + message + " was accepted");
    }
    static Task Reject(Action action, string message) => Reject(() => { action(); return Task.CompletedTask; }, message);
    static string Fresh(string name) => Path.Combine(Root, name);
    static EngineComponent Component(string id, string version = "1.0", string? hash = null) =>
        new(id, version, "https://component-fixture.example/" + id + "-" + version + ".zip",
            hash ?? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id + version))), 100, "zip");
    static ComponentPackageDefinition Package(string id = "pandoc", string version = "1.0", params EngineComponent[] dependencies) =>
        new(id, id + " fixture", "Generated fixture, never executed",
            new[] { Component(id, version) }.Concat(dependencies).ToArray(), 1000);
    sealed class Installer : IComponentPackageInstaller
    {
        internal int Calls;
        internal Func<ComponentPackageDefinition, string, CancellationToken, Task>? Before;
        internal bool SkipPrograms, WrongMarker;
        public async Task RestoreAsync(ComponentPackageDefinition package, string stage,
            IProgress<ComponentOperationProgress>? progress, CancellationToken token)
        {
            Calls++;
            if (Before is not null) await Before(package, stage, token);
            token.ThrowIfCancellationRequested();
            foreach (var component in package.Components)
            {
                var directory = ComponentCatalog.ComponentDirectory(stage, component);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, ".filee-component"), WrongMarker ? new string('0', 64) : component.Sha256);
                if (!SkipPrograms)
                {
                    var programs = EngineEnvironment.OwnCopyPrograms.TryGetValue(component.Id, out var names)
                        ? names : component.Id == "jre" ? new[] { Path.Combine("bin", "java.exe") } : Array.Empty<string>();
                    foreach (var program in programs)
                    {
                        var path = Path.Combine(directory, program);
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        File.WriteAllText(path, "SYNTHETIC ONLY " + component.Id + " " + component.Version);
                    }
                }
            }
        }
    }
    sealed class CallbackProgress(Action<ComponentOperationProgress> report) : IProgress<ComponentOperationProgress>
    {
        public void Report(ComponentOperationProgress value) => report(value);
    }
    sealed class FakeHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            return send(request, token);
        }
    }
    sealed class NonSeekingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
    static ComponentStore Store(string name, Installer installer, params ComponentPackageDefinition[] packages) =>
        new(Fresh(name), packages, installer);
    static string Active(string name) => File.ReadAllText(Path.Combine(Fresh(name), "active.json"));
    static string OwnPath(ComponentStore store, string id = "pandoc") => store.GetOwnCopies()[id];
    static async Task Basics()
    {
        var installer = new Installer();
        var store = Store("basic", installer, Package());
        Check(installer.Calls == 0 && store.List().Single().State == "missing" && store.GetOwnCopies().Count == 0,
            "constructing/listing the owned store does not install or expose missing executables");
        await store.InstallAsync("pandoc", null, None);
        var installed = store.List().Single();
        string executable = Path.Combine(OwnPath(store), "pandoc.exe");
        Check(installed.State == "installed" && installed.InstalledVersion == "1.0" && installed.CanUninstall && File.Exists(executable),
            "valid generated package publishes an installed snapshot and its approved program path");
        string manifest = Active("basic");
        await store.UpdateAsync("pandoc", null, None);
        Check(installer.Calls == 1 && Active("basic") == manifest, "up-to-date update performs no download or pointer rewrite");
        using (var read = store.AcquireReadLease())
        {
            Check(store.List().Single().State == "installed", "multiple read leases can list an installed component");
            bool busy = false;
            try { store.Uninstall("pandoc"); } catch (ComponentStoreBusyException) { busy = true; }
            Check(busy, "running conversion read lease prevents uninstall with a distinguishable busy exception");
            await Reject(() => store.UpdateAsync("pandoc", null, None), "running conversion read lease prevents management");
            Check(File.Exists(executable) && Active("basic") == manifest, "rejected concurrent management leaves active files unchanged");
        }
        string outside = Fresh("original-and-converted-result.txt");
        File.WriteAllText(outside, "PERSONAL FILE SENTINEL OUTSIDE THE OWNED STORE");
        store.Uninstall("pandoc");
        Check(store.List().Single().State == "missing" && store.GetOwnCopies().Count == 0 && !File.Exists(executable),
            "uninstall removes only the active package and makes its conversion route unavailable");
        store.Uninstall("pandoc");
        Check(File.ReadAllText(outside).StartsWith("PERSONAL FILE"), "repeated uninstall is harmless and preserves originals/results outside the store");
        await Reject(() => store.InstallAsync("../pandoc", null, None), "path-like component ID is rejected before installer invocation");
        Check(installer.Calls == 1, "invalid component action cannot start an installer");
    }
    static async Task UpdatesAndCancellation()
    {
        var installer = new Installer();
        var old = Store("updating", installer, Package());
        await old.InstallAsync("pandoc", null, None);
        string oldPath = OwnPath(old), manifest = Active("updating");
        var newer = Store("updating", installer, Package("pandoc", "2.0"));
        Check(newer.List().Single().State == "update-available", "approved newer catalog exposes an update without auto downloading");
        installer.Before = (p,s,t) => throw new IOException("synthetic download failure");
        await Reject(() => newer.UpdateAsync("pandoc", null, None), "download failure prevents a new snapshot from publishing");
        Check(Active("updating") == manifest && OwnPath(old) == oldPath && File.Exists(Path.Combine(oldPath, "pandoc.exe")),
            "failed update retains the exact active pointer and old usable program");
        using (var cancel = new CancellationTokenSource())
        {
            installer.Before = (p,s,t) => { File.WriteAllText(Path.Combine(s, "partial.bin"), "PARTIAL"); cancel.Cancel(); return Task.CompletedTask; };
            await Reject(() => newer.UpdateAsync("pandoc", null, cancel.Token), "cancellation during restoration aborts publication");
        }
        Check(Active("updating") == manifest && !Directory.EnumerateDirectories(Path.Combine(Fresh("updating"), ".staging")).Any(),
            "cancelled update preserves installed state and reclaims only its owned staged files");
        installer.Before = null;
        await newer.UpdateAsync("pandoc", null, None);
        string updatedPath = OwnPath(newer);
        Check(newer.List().Single().InstalledVersion == "2.0" && updatedPath != oldPath && !Directory.Exists(oldPath),
            "successful update atomically switches to a new immutable snapshot then reclaims the old snapshot");
        Check(old.List().Single().State == "newer_version" && old.GetOwnCopies().Count == 0,
            "an older Nook catalog neither executes nor silently downgrades a newer installed component");
        string newerManifest = Active("updating");
        await Reject(() => old.UpdateAsync("pandoc", null, None), "old app version cannot downgrade an installed component");
        Check(Active("updating") == newerManifest && File.Exists(Path.Combine(updatedPath, "pandoc.exe")), "blocked downgrade preserves newer files and pointer");
        var late = Store("late-cancel", new Installer(), Package());
        using var cancellation = new CancellationTokenSource();
        await late.InstallAsync("pandoc", new CallbackProgress(value => { if (value.Phase == "complete") cancellation.Cancel(); }), cancellation.Token);
        Check(late.List().Single().State == "installed" && cancellation.IsCancellationRequested,
            "cancellation after durable commit does not misreport a successful installation as rolled back");
    }
    static async Task DependenciesAndConcurrency()
    {
        var installer = new Installer();
        var definitions = new[] { Package("libreoffice", "1.0", Component("jre")), Package("pandoc") };
        var store = Store("dependencies", installer, definitions);
        await store.InstallAsync("libreoffice", null, None);
        await store.InstallAsync("pandoc", null, None);
        string office = OwnPath(store, "libreoffice"), java = OwnPath(store, "jre"), pandoc = OwnPath(store);
        Check(store.List().Single(i => i.Id == "libreoffice").Dependencies.SequenceEqual(new[] { "jre" }) &&
            File.Exists(Path.Combine(java, "bin", "java.exe")), "package snapshot installs required runtime together and exposes approved dependency path");
        await Reject(() => store.Uninstall("jre"), "private dependency cannot be uninstalled as an independent package");
        store.Uninstall("libreoffice");
        Check(!Directory.Exists(office) && !Directory.Exists(java) && File.Exists(Path.Combine(pandoc, "pandoc.exe")),
            "removing one package reclaims its private dependencies without touching another package");
        var modern = Store("dependency-versions", installer, Package("libreoffice", "1.0", Component("jre", "2.0")));
        await modern.InstallAsync("libreoffice", null, None);
        var legacy = Store("dependency-versions", installer, Package("libreoffice", "1.0", Component("jre", "1.0")));
        Check(legacy.List().Single().State == "newer_version" && legacy.GetOwnCopies().Count == 0,
            "newer bundled runtime is protected from downgrade even when the main package version is unchanged");
        await Reject(() => legacy.UpdateAsync("libreoffice", null, None), "dependency downgrade cannot bypass package-version protection");
        var gate = new TaskCompletionSource();
        var began = new TaskCompletionSource();
        var pendingInstaller = new Installer { Before = async(p,s,t) => { began.SetResult(); await gate.Task; } };
        var concurrent = Store("concurrent", pendingInstaller, Package());
        Task pending = concurrent.InstallAsync("pandoc", null, None);
        await began.Task;
        await Reject(() => concurrent.InstallAsync("pandoc", null, None), "second component writer is rejected while installation is in progress");
        using (var lease = concurrent.AcquireReadLease())
        {
            gate.SetResult();
            await Reject(() => pending, "conversion that begins during download prevents final snapshot switch");
            Check(concurrent.List().Single().State == "missing" && concurrent.GetOwnCopies().Count == 0,
                "failed publish under a read lease leaves no fake installed state or half-published executables");
        }
    }
    static async Task CorruptionAndBoundaries()
    {
        var installer = new Installer();
        var store = Store("corrupt", installer, Package());
        await store.InstallAsync("pandoc", null, None);
        string path = OwnPath(store), program = Path.Combine(path, "pandoc.exe");
        File.Delete(program);
        Check(store.List().Single().State == "broken" && store.GetOwnCopies().Count == 0,
            "missing required executable is repairable and never supplied to a conversion");
        await store.InstallAsync("pandoc", null, None);
        Check(store.List().Single().State == "installed" && File.Exists(Path.Combine(OwnPath(store), "pandoc.exe")),
            "reinstall repairs a damaged snapshot even when its pinned version is unchanged");
        var missing = Store("missing-program", new Installer { SkipPrograms = true }, Package());
        await Reject(() => missing.InstallAsync("pandoc", null, None), "archive markers alone cannot publish a package without required programs");
        Check(missing.List().Single().State == "missing", "incomplete initial installation never leaves installed status");
        var wrong = Store("wrong-marker", new Installer { WrongMarker = true }, Package());
        await Reject(() => wrong.InstallAsync("pandoc", null, None), "incorrect archive provenance marker cannot publish a snapshot");
        await Reject(() => ComponentPaths.LocalRoot(@"C:\"), "volume root cannot become a component store");
        await Reject(() => ComponentPaths.LocalRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), "user profile cannot become a component store");
        await Reject(() => ComponentPaths.LocalRoot(@"\\server\share\components"), "network store roots are rejected");
        await Reject(() => ComponentPaths.Child(Fresh("safe"), @"..\escape"), "relative path traversal is rejected");
        await Reject(() => ComponentPaths.Child(Fresh("safe"), Fresh("elsewhere")), "absolute child injection is rejected");
        await Reject(() => ComponentPaths.Child(Fresh("safe"), "file:stream"), "alternate data stream path is rejected");
        string unknown = Fresh("unknown-owner"); Directory.CreateDirectory(unknown);File.WriteAllText(Path.Combine(unknown, "keep.txt"), "DO NOT ADOPT");
        await Reject(() => new ComponentStore(unknown, new[] { Package() }, new Installer()), "nonempty unrelated directory is never adopted as a component store");
        Check(File.ReadAllText(Path.Combine(unknown, "keep.txt")) == "DO NOT ADOPT", "rejected adoption preserves unowned contents");
        string manifest = Path.Combine(Fresh("corrupt"), "active.json"), original = File.ReadAllText(manifest);
        var json = JsonNode.Parse(original)!;json["Entries"]!["pandoc"]!["Snapshot"] = @"..\..\original-and-converted-result.txt";
        File.WriteAllText(manifest, json.ToJsonString());
        await Reject(() => store.List(), "tampered manifest cannot point an installed snapshot outside the store");
        Check(File.ReadAllText(Fresh("original-and-converted-result.txt")).StartsWith("PERSONAL FILE"), "tampered manifest cannot alter sibling material or output");
        File.WriteAllText(manifest, original);
        string repairedProgram = Path.Combine(OwnPath(store), "pandoc.exe");
        string owner = Path.Combine(Fresh("corrupt"), ".nook-component-store"); File.WriteAllText(owner, "FOREIGN OWNER");
        await Reject(() => store.Uninstall("pandoc"), "ownership changed after load aborts uninstall");
        Check(File.Exists(repairedProgram), "ownership failure never deletes retained component files");
        var missingLock = Store("missing-lock", new Installer(), Package());
        File.Delete(Path.Combine(Fresh("missing-lock"), ".use.lock"));
        bool missingIsNotBusy = false;
        try { using var lease = missingLock.AcquireReadLease(); }
        catch (ComponentStoreBusyException) { }
        catch (IOException) { missingIsNotBusy = true; }
        Check(missingIsNotBusy, "missing lock file is an ordinary I/O failure, not a busy conversion");
    }
    static async Task KeyFileIntegrityAndLegacy()
    {
        var installer = new Installer { Before = (package, stage, token) =>
        {
            var directory = Path.Combine(stage, "pandoc");Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "helper.dll"), "SYNTHETIC LIBRARY NEVER EXECUTED");
            File.WriteAllText(Path.Combine(directory, "__init__.py"), "");
            return Task.CompletedTask;
        }};
        var store = Store("key-integrity", installer, Package());
        await store.InstallAsync("pandoc", null, None);
        string program = Path.Combine(OwnPath(store), "pandoc.exe");
        Check(File.Exists(Path.Combine(Path.GetDirectoryName(OwnPath(store))!, ComponentIntegrity.FileName)),
            "new installs publish a separate key runtime file integrity inventory");
        using (new FileStream(program, FileMode.Open, FileAccess.Read, FileShare.None))
            Check(store.List().Single().State == "installed", "settings listing reads key-file metadata, not executable contents");
        void CorruptPreservingMetadata(string path)
        {
            var written = File.GetLastWriteTimeUtc(path);var bytes = File.ReadAllBytes(path);bytes[0] ^= 1;
            File.WriteAllBytes(path, bytes);File.SetLastWriteTimeUtc(path, written);
        }
        CorruptPreservingMetadata(program);
        Check(store.GetOwnCopies().Count == 0 && store.List().Single().State == "broken",
            "same-length same-timestamp executable corruption is denied before use and reported repairable");
        await store.InstallAsync("pandoc", null, None);
        Check(installer.Calls == 2 && store.List().Single().State == "installed" && store.GetOwnCopies().Count == 1,
            "same-version reinstall repairs changed bytes instead of trusting existence or archive markers");
        CorruptPreservingMetadata(Path.Combine(OwnPath(store), "helper.dll"));
        Check(store.GetOwnCopies().Count == 0, "same-size dependent DLL corruption is checked as well as the main executable");
        await store.InstallAsync("pandoc", null, None);
        string current = OwnPath(store), manifest = Path.Combine(Fresh("key-integrity"), "active.json");
        string snapshot = Path.GetDirectoryName(current)!;
        File.Delete(Path.Combine(snapshot, ComponentIntegrity.FileName));
        Check(store.List().Single().State == "broken" && store.GetOwnCopies().Count == 0,
            "deleting a new snapshot inventory cannot silently downgrade it into legacy compatibility mode");
        await store.InstallAsync("pandoc", null, None);
        Check(installer.Calls == 4 && store.List().Single().State == "installed", "missing integrity inventory can be repaired with the pinned package");

        var legacyInstaller = new Installer();var legacy = Store("legacy-integrity", legacyInstaller, Package());
        await legacy.InstallAsync("pandoc", null, None);
        string legacyPath = OwnPath(legacy), legacyManifest = Path.Combine(Fresh("legacy-integrity"), "active.json");
        var json = JsonNode.Parse(File.ReadAllText(legacyManifest))!;
        json["Entries"]!["pandoc"]!.AsObject().Remove("IntegritySha256");
        File.WriteAllText(legacyManifest, json.ToJsonString());
        File.Delete(Path.Combine(Path.GetDirectoryName(legacyPath)!, ComponentIntegrity.FileName));
        var state = legacy.List().Single();
        Check(state.State == "update-available" && state.Message.Contains("尚无关键文件完整性清单")
            && legacy.GetOwnCopies().Count == 1, "legacy components retain compatibility use with an explicit unverified and repair/update notice");
        await legacy.UpdateAsync("pandoc", null, None);
        Check(legacyInstaller.Calls == 2 && legacy.List().Single().State == "installed"
            && File.Exists(Path.Combine(Path.GetDirectoryName(OwnPath(legacy))!, ComponentIntegrity.FileName)),
            "explicit legacy update reinstalls the pinned package and establishes strict key-file checks");

        var shortcutInstaller = new Installer();var shortcut = Store("same-version-integrity", shortcutInstaller, Package());
        await shortcut.InstallAsync("pandoc", null, None);
        string originalProgram = Path.Combine(OwnPath(shortcut), "pandoc.exe");
        CorruptPreservingMetadata(originalProgram);
        await shortcut.UpdateAsync("pandoc", null, None); // No intervening GetOwnCopies may discover corruption for this check.
        Check(shortcutInstaller.Calls == 2 && shortcut.GetOwnCopies().Count == 1,
            "same-version already-installed shortcut independently hashes key contents before deciding not to repair");
    }
    static async Task CleanupIsBestEffort()
    {
        var store = Store("cleanup-link", new Installer(), Package(), Package("calibre"));
        string target = Fresh("cleanup-link-protected"), link = Path.Combine(Fresh("cleanup-link"), "versions", "calibre");
        Directory.CreateDirectory(target);File.WriteAllText(Path.Combine(target, "keep.txt"), "UNOWNED FIXTURE CONTENT");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception error) when (error is UnauthorizedAccessException || error is IOException)
        {
            Console.WriteLine("SKIP symlink cleanup scenario: this account cannot create a synthetic directory link. " + error.Message);
            return;
        }
        await store.InstallAsync("pandoc", null, None);
        Check(store.List().Single(item => item.Id == "pandoc").State == "installed",
            "post-commit cleanup skips an unrelated linked package directory without misreporting install failure");
        store.Uninstall("pandoc");
        Check(store.List().Single(item => item.Id == "pandoc").State == "missing",
            "post-commit cleanup skips an unrelated linked directory without misreporting uninstall failure");
        Check(File.ReadAllText(Path.Combine(target, "keep.txt")) == "UNOWNED FIXTURE CONTENT" &&
            (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0,
            "cleanup neither follows nor removes unowned linked content");
        Directory.Delete(link); // Exact synthetic link only; never recurse into its target.
    }
    static byte[] Zip(string entry, string text = "SYNTHETIC PROGRAM NEVER EXECUTED")
    {
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(archive.CreateEntry(entry).Open())) writer.Write(text);
        return bytes.ToArray();
    }
    static ComponentPackageDefinition Archive(byte[] bytes) =>
        new("pandoc", "Synthetic text package", "Offline fixture", new[] {
            new EngineComponent("pandoc","1.0","https://component-fixture.example/pandoc.zip",
                Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length,"zip") }, 1000);
    static HttpResponseMessage Response(byte[] bytes, bool knownLength = true)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = knownLength ? new ByteArrayContent(bytes) : new StreamContent(new NonSeekingStream(bytes));
        return response;
    }
    static async Task Downloads()
    {
        byte[] archive = Zip("pandoc.exe");
        var package = Archive(archive);
        int requests = 0;
        var downloader = new ComponentPackageInstaller(() => new FakeHttp((request, token) =>
        {
            requests++;return Task.FromResult(Response(archive));
        }));
        var store = new ComponentStore(Fresh("synthetic-download"), new[] { package }, downloader);
        await store.InstallAsync("pandoc", null, None);
        Check(requests == 1 && store.List().Single().State == "installed" &&
            File.ReadAllText(Path.Combine(OwnPath(store), "pandoc.exe")).Contains("NEVER EXECUTED"),
            "real download/hash/extract pipeline accepts an approved synthetic ZIP without executing it or using the network");
        var badHash = archive.ToArray();badHash[^1] ^= 1;
        await DownloadRejected("hash-mismatch", package, request => Response(badHash), "hash mismatch prevents extraction and publication");
        await DownloadRejected("oversize-header", package, request => Response(archive.Concat(new byte[] { 0 }).ToArray()), "Content-Length mismatch is rejected before accepting a package");
        await DownloadRejected("oversize-chunked", package, request => Response(archive.Concat(new byte[] { 0 }).ToArray(), false), "oversize chunked download is bounded even without Content-Length");
        await DownloadRejected("truncated-chunked", package, request => Response(archive.Take(archive.Length - 1).ToArray(), false), "truncated chunked download cannot pass as a complete archive");
        byte[] traversal = Zip("../escaped-program.exe");
        await DownloadRejected("zip-traversal", Archive(traversal), request => Response(traversal), "ZIP traversal is rejected before any snapshot is published");
        Check(!Directory.GetFiles(Fresh("zip-traversal"), "escaped-program.exe", SearchOption.AllDirectories).Any(), "traversal archive cannot write an executable outside its extraction directory");
        foreach (string redirect in new[] { "http://127.0.0.1/private", "http://192.168.1.2/private", "file:///C:/private", "https://user:secret@component-fixture.example/x", "https://printer.local/x" })
            await DownloadRejected("redirect-" + Checks, package, request => {
                var response = new HttpResponseMessage(HttpStatusCode.Found);response.Headers.Location = new Uri(redirect);return response;
            }, "unsafe redirect target is rejected: " + new Uri(redirect).Host);
        int forwarded = 0;
        using (var http = new HttpClient(new ComponentDownloadHandler(package.Components, new FakeHttp((request, token) => { forwarded++;return Task.FromResult(Response(archive)); }))))
        {
            await Reject(async () => { using var response = await http.GetAsync("https://unapproved.example/x"); }, "unapproved initial download URL never reaches transport");
            await Reject(async () => { using var response = await http.PostAsync(package.Components[0].Url, new StringContent("x")); }, "non-GET component request never reaches transport");
            Check(forwarded == 0, "rejected URLs and methods have zero network side effects");
        }
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var cancelled = new ComponentStore(Fresh("cancel-download"), new[] { package }, new ComponentPackageInstaller(() =>
            new FakeHttp(async (request, token) => { started.SetResult();await Task.Delay(Timeout.Infinite, token);return Response(archive); })));
        Task pending = cancelled.InstallAsync("pandoc", null, cancellation.Token);await started.Task;cancellation.Cancel();
        await Reject(() => pending, "cancel stops a pending network read without publishing");
        Check(cancelled.List().Single().State == "missing" && !Directory.EnumerateDirectories(Path.Combine(Fresh("cancel-download"), ".staging")).Any(),
            "cancelled download leaves no installed component or owned partial extraction");
    }
    static async Task DownloadNetworkBoundaries()
    {
        var component = Component("pandoc") with { Url = "https://github.com/owner/project/releases/download/v1/pkg.zip" };
        var publicIp = IPAddress.Parse("93.184.216.34");
        Task<IPAddress[]> Public(string host, CancellationToken token) => Task.FromResult(new[] { publicIp });
        foreach (var target in new[] {
            "http://release-assets.githubusercontent.com/pkg.zip", "https://arbitrary.example/pkg.zip",
            "https://github.com.evil.example/pkg.zip", "https://conda.anaconda.org/pkg.zip",
            "https://release-assets.githubusercontent.com:444/pkg.zip", "https://release-assets.githubusercontent.com/pkg.zip#fragment",
            "https://user:secret@release-assets.githubusercontent.com/pkg.zip", "https://127.0.0.1/pkg.zip" })
        {
            var transport = new FakeHttp((request, token) => {
                var response = new HttpResponseMessage(HttpStatusCode.Found);response.Headers.Location = new Uri(target);return Task.FromResult(response);
            });
            using var http = new HttpClient(new ComponentDownloadHandler(new[] { component }, transport, new ComponentNetworkPolicy(Public)));
            await Reject(async () => { using var response = await http.GetAsync(component.Url); }, "reject unsafe or unreviewed HTTPS supplier redirect: " + new Uri(target).Host);
            Check(transport.Calls == 1, "rejected redirect emits no second transport request");
        }
        foreach (var address in new[] { "127.0.0.1", "10.1.2.3", "172.16.1.1", "192.168.1.1", "169.254.169.254", "100.64.1.1", "224.0.0.1", "::1", "fc00::1", "fe80::1", "::ffff:10.1.1.1", "2002:0a00:0001::1" })
        {
            var transport = new FakeHttp((request, token) => Task.FromResult(Response(new byte[100])));
            var network = new ComponentNetworkPolicy((host, token) => Task.FromResult(new[] { IPAddress.Parse(address) }));
            using var http = new HttpClient(new ComponentDownloadHandler(new[] { component }, transport, network));
            await Reject(async () => { using var response = await http.GetAsync(component.Url); }, "deny nonpublic DNS answer: " + address);
            Check(transport.Calls == 0, "private DNS target is rejected before the initial transport request");
        }
        var privateRedirectTransport = new FakeHttp((request, token) => {
            var response = new HttpResponseMessage(HttpStatusCode.Found);response.Headers.Location = new Uri("https://release-assets.githubusercontent.com/pkg.zip");return Task.FromResult(response);
        });
        var privateRedirectNetwork = new ComponentNetworkPolicy((host, token) => Task.FromResult(new[] {
            host == "github.com" ? publicIp : IPAddress.Parse("10.1.2.3") }));
        using (var http = new HttpClient(new ComponentDownloadHandler(new[] { component }, privateRedirectTransport, privateRedirectNetwork)))
            await Reject(async () => { using var response = await http.GetAsync(component.Url); }, "trusted CDN hostname resolving privately is rejected too");
        Check(privateRedirectTransport.Calls == 1, "private DNS on an otherwise approved redirect causes no second request");
        var mixed = new ComponentNetworkPolicy((host, token) => Task.FromResult(new[] { publicIp, IPAddress.Parse("192.168.1.2") }));
        await Reject(async () => { await mixed.PublicAddressesAsync("github.com", None); }, "mixed public/private DNS responses are not partially trusted");
        var allowed = new FakeHttp((request, token) => {
            if (request.RequestUri!.Host == "github.com") {
                var response = new HttpResponseMessage(HttpStatusCode.Found);response.Headers.Location = new Uri("https://release-assets.githubusercontent.com/pkg.zip");return Task.FromResult(response);
            }
            return Task.FromResult(Response(new byte[100]));
        });
        using (var http = new HttpClient(new ComponentDownloadHandler(new[] { component }, allowed, new ComponentNetworkPolicy(Public))))
        {
            using var first = await http.GetAsync(component.Url);
            using var second = await http.GetAsync(first.Headers.Location);
            Check(first.StatusCode == HttpStatusCode.Found && second.IsSuccessStatusCode && allowed.Calls == 2,
                "reviewed GitHub Releases to release-assets CDN delivery remains accepted");
        }
        var lo = Component("libreoffice") with { Url = "https://download.documentfoundation.org/libreoffice/stable/26.2.6/win/x86_64/LibreOffice_26.2.6_Win_x86-64.msi" };
        var mirrored = ComponentNetworkPolicy.ReviewedDownload(lo);
        Check(mirrored.Url == "https://mirror.aarnet.edu.au/pub/tdf/libreoffice/stable/26.2.6/win/x86_64/LibreOffice_26.2.6_Win_x86-64.msi"
            && mirrored.Sha256 == lo.Sha256 && mirrored.Size == lo.Size, "LibreOffice uses the reviewed official-listed mirror with unchanged package hash and byte pin");
        ComponentNetworkPolicy.ValidateRedirect(new Uri("https://conda.anaconda.org/pkg"), new Uri("https://binstar-cio-packages-prod.s3.amazonaws.com/pkg"));
        Check(true, "reviewed conda delivery CDN is explicitly accepted");
        using var systemTransport = (SocketsHttpHandler)new ComponentNetworkPolicy(Public).CreateTransport();
        Check(systemTransport.UseProxy && !systemTransport.AllowAutoRedirect && systemTransport.ConnectCallback is not null,
            "production transport retains user system proxy and uses explicit redirects plus checked-IP direct connections");
        await Reject(() => ComponentCatalog.Validate(new[] { Package() with { Components = new[] { Component("pandoc") with { Url = "http://component-fixture.example/pkg.zip" } } } }),
            "catalog HTTP downgrade is rejected before installation");
    }
    static async Task DownloadRejected(string name, ComponentPackageDefinition package, Func<HttpRequestMessage, HttpResponseMessage> response, string message)
    {
        var store = new ComponentStore(Fresh(name), new[] { package }, new ComponentPackageInstaller(() => new FakeHttp((r,t) => Task.FromResult(response(r)))));
        await Reject(() => store.InstallAsync("pandoc", null, None), message);
        Check(store.List().Single().State == "missing" && store.GetOwnCopies().Count == 0, name + " never exposes an installed program");
    }
    static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length != 1) throw new ArgumentException("ComponentStoreTests <new-fixture-directory>");
            Root = Path.GetFullPath(args[0]);if (Directory.Exists(Root) || File.Exists(Root)) throw new IOException("Use a new isolated root.");
            Directory.CreateDirectory(Root);
            await Basics();await UpdatesAndCancellation();await DependenciesAndConcurrency();await CorruptionAndBoundaries();await KeyFileIntegrityAndLegacy();await CleanupIsBestEffort();await Downloads();await DownloadNetworkBoundaries();
            Console.WriteLine($"Component store and synthetic-download checks: {Checks} passed; 0 failed. No real component root, network, process or desktop.");return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error);return 1; }
    }
}
