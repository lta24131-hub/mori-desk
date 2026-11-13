using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

[assembly: AssemblyTitle("Nook VM read-only installed inventory - TEST ONLY")]
[assembly: AssemblyProduct("Nook VM read-only installed inventory - TEST ONLY")]
[assembly: AssemblyVersion("0.0.0.1")]
[assembly: AssemblyFileVersion("0.0.0.1")]

// Read-only evidence probe, never an installer or application. Its only write
// is its report in the hash-addressed guest audit package that contains it.
internal static class InstalledUpgradeInventoryGuest {
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024, RecursionLimit = 30 };
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SqlCallback(IntPtr context, int count, IntPtr values, IntPtr names);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] static extern int sqlite3_open16(string path, out IntPtr handle);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_exec(IntPtr handle, byte[] sql, SqlCallback callback, IntPtr context, out IntPtr error);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern void sqlite3_free(IntPtr value);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_close(IntPtr handle);
    static object Capture(Func<object> action) { try { return action(); } catch (Exception error) { return new { available = false, errorType = error.GetType().Name }; } }
    static object ProcessWindow(uint id) {
        using (var process = Process.GetProcessById((int)id)) {
            process.Refresh();
            return new { handle = process.MainWindowHandle.ToInt64(), title = process.MainWindowTitle, responding = process.Responding, startedUtc = process.StartTime.ToUniversalTime().ToString("O") };
        }
    }
    static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    static void Guest() {
        bool found = false;
        using (var query = new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem"))
            foreach (ManagementObject row in query.Get()) using (row)
                found |= System.Text.RegularExpressions.Regex.IsMatch(Convert.ToString(row["Manufacturer"]) + " " + Convert.ToString(row["Model"]), "QEMU|KVM|Bochs", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Require(found && Environment.UserName.Equals("NookTester", StringComparison.OrdinalIgnoreCase), "Dedicated NookTester QEMU guest required before reading installation data.");
    }
    static void NoLinks(string path) {
        for (string current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked inventory path refused.");
    }
    static string Hash(string file) { using (var input = File.OpenRead(file)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", ""); }
    static object LauncherOwnership(string program) {
        string file = Path.Combine(program, "Launcher", ".nook-launcher-v1"), executable = Path.Combine(program, "Launcher", "Mori.exe");
        NoLinks(file); NoLinks(executable);
        if (!File.Exists(file) || !File.Exists(executable)) return new { exists = File.Exists(file), executableExists = File.Exists(executable), validForExecutable = false };
        Require(new FileInfo(file).Length <= 4096, "Bounded public launcher ownership required.");
        string digest = Hash(executable), value = File.ReadAllText(file, new UTF8Encoding(false, true));
        return new { exists = true, executableExists = true, fileSha256 = Hash(file), executableSha256 = digest, validForExecutable = value == "Nook stable launcher protocol 1\n" + digest + "\n" };
    }
    static object FileRecord(string file, bool version) {
        NoLinks(file); if (!File.Exists(file)) return new { path = file, exists = false };
        var info = new FileInfo(file); Require(info.Length <= 128 * 1024 * 1024, "Inventory file exceeds bounded read.");
        var result = new Dictionary<string, object> { { "path", file }, { "exists", true }, { "bytes", info.Length }, { "sha256", Hash(file) } };
        if (version) { var v = FileVersionInfo.GetVersionInfo(file); result["fileVersion"] = v.FileVersion; result["product"] = v.ProductName; }
        return result;
    }
    static Dictionary<string, object> SelectedJson(string file, string[] allowed) {
        NoLinks(file); var result = new Dictionary<string, object>(); if (!File.Exists(file)) return result;
        Require(new FileInfo(file).Length <= 1024 * 1024, "Bounded metadata required.");
        var fields = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(file, Encoding.UTF8));
        foreach (var name in allowed) { object value; if (fields != null && fields.TryGetValue(name, out value) && (value == null || value is string || value is int || value is bool)) result[name] = value; }
        return result;
    }
    static object Shortcut(string file) {
        NoLinks(file); if (!File.Exists(file)) return new { path = file, exists = false };
        object shell = null, shortcut = null;
        try {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
            shortcut = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { file });
            var kind = shortcut.GetType();
            return new { path = file, exists = true, target = kind.InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null), arguments = kind.InvokeMember("Arguments", BindingFlags.GetProperty, null, shortcut, null) };
        } finally { if (shortcut != null) Marshal.FinalReleaseComObject(shortcut); if (shell != null) Marshal.FinalReleaseComObject(shell); }
    }
    static object RegistryValue(RegistryKey root, string path, string name) {
        using (var key = root.OpenSubKey(path, false)) { if (key == null) return null; object value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames); return value is string ? value : null; }
    }
    static object Registrations(string data) {
        var entries = new List<object>(); string nativePath = Path.Combine(data, "browser", "app.mori.desk.json");
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine }) foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            using (var root = RegistryKey.OpenBaseKey(hive, view)) {
                var native = new List<object>();
                foreach (string browser in new[] { @"Google\Chrome", @"Microsoft\Edge" }) {
                    object path = RegistryValue(root, @"Software\" + browser + @"\NativeMessagingHosts\app.mori.desk", "");
                    native.Add(new { browser, registeredPath = path, matchesKnownManifest = path is string && string.Equals(Path.GetFullPath((string)path), nativePath, StringComparison.OrdinalIgnoreCase) });
                }
                entries.Add(new {
                    hive = hive.ToString(), view = view.ToString(),
                    openWith = RegistryValue(root, @"Software\Classes\Applications\Mori.exe\shell\open\command", ""),
                    startup = RegistryValue(root, @"Software\Microsoft\Windows\CurrentVersion\Run", "MoriDesk"),
                    native
                });
            }
        return new { entries, nativeManifest = Capture(() => SelectedJson(nativePath, new[] { "name", "type", "path" })) };
    }
    static object BrowserMetadata(string data) {
        string browser = Path.Combine(data, "browser"), catalog = Path.Combine(browser, "extension-installs.json"); NoLinks(browser); NoLinks(catalog);
        var installs = new List<object>();
        if (File.Exists(catalog)) {
            Require(new FileInfo(catalog).Length <= 1024 * 1024, "Bounded browser catalog required.");
            var record = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(catalog, Encoding.UTF8)); object raw;
            if (record != null && record.TryGetValue("installs", out raw) && raw is System.Collections.IEnumerable)
                foreach (object entry in (System.Collections.IEnumerable)raw) {
                    Require(installs.Count < 4, "Bounded managed installations required."); var item = entry as Dictionary<string, object>; if (item == null) continue;
                    var summary = new Dictionary<string, object>();
                    foreach (string field in new[] { "installationId", "extensionId", "path", "version", "activeVersion", "generation", "awaitingReload", "legacy" }) { object value; if (item.TryGetValue(field, out value) && (value == null || value is string || value is bool)) summary[field] = value; }
                    object pending; summary["updatePending"] = item.TryGetValue("pending", out pending) && pending != null;
                    // Never serialize the catalog, client tokens, pairing material,
                    // or local-connection.js; only public identity and file hashes.
                    installs.Add(summary);
                }
        }
        string extension = Path.Combine(browser, "extension"); NoLinks(extension);
        var runtime = new[] { "manifest.json", "background.js", "content.js", "updater.js", "local-bridge.js", "runtime-client.js" }.Select(name => Capture(() => FileRecord(Path.Combine(extension, name), false))).ToArray();
        string nativeManifest = Path.Combine(browser, "app.mori.desk.json");
        var origins = new List<string>(); NoLinks(nativeManifest);
        if (File.Exists(nativeManifest)) {
            Require(new FileInfo(nativeManifest).Length <= 64 * 1024, "Bounded native manifest required.");
            var native = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(nativeManifest, Encoding.UTF8)); object raw;
            if (native != null && native.TryGetValue("allowed_origins", out raw) && raw is System.Collections.IEnumerable)
                foreach (object value in (System.Collections.IEnumerable)raw) {
                    Require(origins.Count < 4 && value is string && System.Text.RegularExpressions.Regex.IsMatch((string)value, @"\Achrome-extension://[a-p]{32}/\z"), "Only public extension origins may be reported.");
                    origins.Add((string)value);
                }
        }
        return new { catalogExists = File.Exists(catalog), managedInstalls = installs, nativeManifest = Capture(() => FileRecord(nativeManifest, false)), nativeAllowedOrigins = origins, runtime };
    }
    static object CanvasMetadata(string data) {
        string root = Path.Combine(data, "free-canvas"); NoLinks(root); if (!Directory.Exists(root)) return new { count = 0 };
        var folders = Directory.EnumerateDirectories(root).Take(257).ToArray(); Require(folders.Length <= 256, "Canvas inventory exceeded bound.");
        var entries = new List<object>();
        foreach (string folder in folders) { NoLinks(folder); Guid id; if (!Guid.TryParse(Path.GetFileName(folder), out id)) continue; entries.Add(new { id = id.ToString("N"), project = Capture(() => FileRecord(Path.Combine(folder, "project.json"), false)), drawing = Capture(() => FileRecord(Path.Combine(folder, "mature-canvas.json"), false)) }); }
        return new { count = entries.Count, entries };
    }
    static object FixtureImages(Dictionary<string, object> paths, string data) {
        object configured;
        if (!paths.TryGetValue("LibraryPath", out configured) || !(configured is string) ||
            !string.Equals(Path.GetFullPath((string)configured), @"C:\NookLab\LibraryFixture", StringComparison.OrdinalIgnoreCase))
            return new { included = false, reason = "not-the-generated-upgrade-fixture" };
        // Only the deliberately generated fixture's small images are read.
        // This is not a general scan of user media or other drive contents.
        var images = new List<object>();
        foreach (string root in new[] { Path.Combine((string)configured, "原图"), Path.Combine(data, "capture", "pins"), Path.Combine(data, "free-canvas") }) {
            NoLinks(root); if (!Directory.Exists(root)) continue;
            var pending = new Stack<string>(); pending.Push(root); int folders = 0;
            while (pending.Count != 0) {
                string folder = pending.Pop(); NoLinks(folder); Require(++folders <= 64, "Generated fixture image inventory exceeded directory bound.");
                foreach (string file in Directory.EnumerateFiles(folder)) {
                    NoLinks(file); if (!Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase)) continue;
                    Require(images.Count < 32 && new FileInfo(file).Length <= 4 * 1024 * 1024, "Generated fixture image inventory exceeded file bound.");
                    images.Add(FileRecord(file, false));
                }
                foreach (string child in Directory.EnumerateDirectories(folder)) { NoLinks(child); pending.Push(child); }
            }
        }
        return new { included = true, files = images };
    }
    static object UpdateProgress(string data) {
        string root = Path.Combine(data, "updates"); NoLinks(root); if (!Directory.Exists(root)) return new object[0];
        var folders = Directory.EnumerateDirectories(root).Take(33).ToArray(); Require(folders.Length <= 32, "Update progress exceeded bounded session inventory.");
        var sessions = new List<object>();
        foreach (string folder in folders) {
            NoLinks(folder); Guid id; if (!Guid.TryParseExact(Path.GetFileName(folder), "N", out id)) continue;
            var entries = new Dictionary<string, object>();
            var fileTimes = new Dictionary<string, object>();
            foreach (string file in new[] { "staging.json", "activation-attempted.json", "candidate.json", "ready.json", "startup-ready.json", "result.json" }) {
                entries[file] = Capture(() => SelectedJson(Path.Combine(folder, file), new[] { "schemaVersion", "status", "expectedVersion", "message", "directory", "version", "phase", "executable", "processId", "pid", "generation", "sha256", "payloadSha256", "executableSha256", "manifestSha256" }));
                string path = Path.Combine(folder, file); NoLinks(path);
                if (File.Exists(path)) { var info = new FileInfo(path); fileTimes[file] = new { createdUtc = info.CreationTimeUtc.ToString("O"), writtenUtc = info.LastWriteTimeUtc.ToString("O") }; }
            }
            var folderInfo = new DirectoryInfo(folder);
            sessions.Add(new { id = id.ToString("N"), createdUtc = folderInfo.CreationTimeUtc.ToString("O"), entries, fileTimes });
        }
        return sessions;
    }
    static object CleanupDiagnostics(string data) {
        string maintenance = Path.Combine(data, "maintenance", "version-cleanup.json");
        string staging = Path.Combine(data, "updates", "staging-cleanup.json");
        string connection = Path.Combine(data, "browser", "connection-status.log");
        // These product records contain lifecycle codes/counts only. Select a
        // fixed schema, never include a browser catalog or arbitrary log text.
        return new {
            versionCleanup = Capture(() => SelectedJson(maintenance, new[] { "schemaVersion", "utc", "version", "state", "reason", "complete", "deletedCount", "pendingCount", "skippedCount" })),
            stagingCleanup = Capture(() => SelectedJson(staging, new[] { "schemaVersion", "utc", "deletedCount", "pendingCount" })),
            localConnection = Capture(() => ConnectionLifecycle(connection))
        };
    }
    static object ConnectionLifecycle(string path) {
        NoLinks(path); if (!File.Exists(path)) return new { exists = false };
        Require(new FileInfo(path).Length <= 1024, "Bounded connection lifecycle record required.");
        string text = File.ReadAllText(path, new UTF8Encoding(false, true));
        var match = System.Text.RegularExpressions.Regex.Match(text, @"\A(?<utc>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z) (?<state>[a-z-]{1,64})(?: (?<error>[A-Za-z0-9]{1,128}) (?<code>0x[A-F0-9]{8}))?\z");
        Require(match.Success, "Only public connection lifecycle codes may be reported.");
        return new { exists = true, utc = match.Groups["utc"].Value, state = match.Groups["state"].Value, errorType = match.Groups["error"].Value, errorCode = match.Groups["code"].Value };
    }
    static long? Count(IntPtr db, string query) {
        long? value = null; SqlCallback read = (context, count, values, names) => { if (count == 1) { long parsed; if (long.TryParse(Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(values)), out parsed)) value = parsed; } return 0; };
        IntPtr error; int result = sqlite3_exec(db, Encoding.UTF8.GetBytes(query + "\0"), read, IntPtr.Zero, out error); if (error != IntPtr.Zero) sqlite3_free(error); GC.KeepAlive(read); return result == 0 ? value : null;
    }
    static object LibrarySnapshot(string package, Dictionary<string, object> paths) {
        object configured; if (!paths.TryGetValue("LibraryPath", out configured) || !(configured is string) || string.IsNullOrWhiteSpace((string)configured)) return new { available = false, reason = "no-configured-library" };
        string library = Path.GetFullPath((string)configured); Require(library.Length > 3 && char.IsLetter(library[0]) && library[1] == ':' && library[2] == '\\', "Local configured library required.");
        string database = Path.Combine(library, ".mori", "library.db"), wal = database + "-wal"; NoLinks(database); NoLinks(wal);
        if (!File.Exists(database)) return new { available = false, reason = "configured-index-missing", path = database };
        // Never ask SQLite to open the production database, even read-only:
        // WAL readers can create shared-memory sidecars. Acquire deny-write
        // source handles instead; if Nook is still using the DB, report busy.
        // Query only a consistent copied DB/WAL pair under this audit package.
        var inputs = new List<FileStream>(); string snapshot = null;
        try {
            inputs.Add(new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.Read));
            if (File.Exists(wal)) inputs.Add(new FileStream(wal, FileMode.Open, FileAccess.Read, FileShare.Read));
            Require(inputs.Sum(stream => stream.Length) <= 128 * 1024 * 1024, "Bounded index snapshot required.");
            snapshot = Path.Combine(package, "library-snapshot-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(snapshot);
            foreach (var input in inputs) using (var output = new FileStream(Path.Combine(snapshot, Path.GetFileName(input.Name)), FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
        } catch (IOException error) { return new { available = false, reason = "source-busy-or-unavailable", errorType = error.GetType().Name, path = database }; }
        finally { foreach (var input in inputs) input.Dispose(); }
        string copy = Path.Combine(snapshot, "library.db"); var index = FileRecord(copy, false); IntPtr db = IntPtr.Zero;
        try {
            Require(sqlite3_open16(copy, out db) == 0, "Copied snapshot could not be opened.");
            IntPtr error; int queryOnly = sqlite3_exec(db, Encoding.UTF8.GetBytes("PRAGMA query_only=ON;\0"), null, IntPtr.Zero, out error); if (error != IntPtr.Zero) sqlite3_free(error); Require(queryOnly == 0, "Snapshot query-only guard failed.");
            var counts = new Dictionary<string, long?>(); foreach (string table in new[] { "assets", "folders", "memberships", "desk_refs", "desk_ref_folders", "desk_local_collections", "desk_board_state" }) counts[table] = Count(db, "SELECT count(*) FROM " + table);
            counts["activeAssets"] = Count(db, "SELECT count(*) FROM assets WHERE deleted=0"); counts["deletedAssets"] = Count(db, "SELECT count(*) FROM assets WHERE deleted<>0");
            return new { available = true, source = database, sourceWrites = false, snapshot, index, counts };
        } finally { if (db != IntPtr.Zero) sqlite3_close(db); }
    }
    [STAThread]
    static int Main(string[] args) {
        try {
            Guest(); Require((args.Length == 1 || args.Length == 2) && (args[0] == "--read-installed-inventory" || args[0] == "--read-installation-progress" || args[0] == "--read-browser-state"), "Explicit read-only request required.");
            bool progressOnly = args[0] == "--read-installation-progress";
            bool browserOnly = args[0] == "--read-browser-state";
            string label = args.Length == 2 ? args[1] : ""; Require(label == "" || System.Text.RegularExpressions.Regex.IsMatch(label, "\\A[a-z0-9-]{1,40}\\z"), "Simple inventory label required.");
            string package = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');
            Require(System.Text.RegularExpressions.Regex.IsMatch(package, @"\AC:\\NookLab\\Audit\\apps\\[A-Fa-f0-9]{12}\z"), "Hash-addressed guest audit package required."); NoLinks(package);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string program = Path.Combine(local, "Programs", "MoriDesk"), data = Path.Combine(local, "MoriDesk");
            object configuredRoot = RegistryValue(Registry.CurrentUser, @"Software\Nook\Installation", "ProgramRoot");
            if (configuredRoot != null) Require(string.Equals(Path.GetFullPath((string)configuredRoot).TrimEnd('\\'), program, StringComparison.OrdinalIgnoreCase), "Existing custom VM installation requires separate review; do not assume default root.");
            NoLinks(program); NoLinks(data);
            var versions = new List<object>(); var children = new string[0];
            if (Directory.Exists(program)) {
                children = Directory.EnumerateFileSystemEntries(program).Take(129).ToArray(); Require(children.Length <= 128, "Program root inventory exceeded bound.");
                foreach (string child in children.Where(Directory.Exists)) { NoLinks(child); versions.Add(new { directory = Path.GetFileName(child), executable = Capture(() => FileRecord(Path.Combine(child, "Mori.exe"), true)) }); }
            }
            string settings = Path.Combine(data, "settings.json");
            var dataChildren = Directory.Exists(data) ? Directory.EnumerateFileSystemEntries(data).Take(129).ToArray() : new string[0];
            Require(dataChildren.Length <= 128, "Data root inventory exceeded bound.");
            foreach (string child in dataChildren) NoLinks(child);
            var metadata = new[] { "settings.json", "capture\\settings.json", "capture\\pins\\pins.json" }.Select(name => Capture(() => FileRecord(Path.Combine(data, name), false))).ToArray();
            var paths = SelectedJson(settings, new[] { "LibraryPath", "FileConversionOutputPath" });
            var shortcuts = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Nook.lnk"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Nook", "Nook.lnk") }.Select(file => Capture(() => Shortcut(file))).ToArray();
            var running = new List<object>();
            using (var query = new ManagementObjectSearcher("SELECT ProcessId,Name,ExecutablePath,WorkingSetSize,UserModeTime,KernelModeTime FROM Win32_Process WHERE Name='Mori.exe' OR Name='Nook.exe' OR Name='Nook-Setup.exe'"))
                foreach (ManagementObject row in query.Get()) using (row) running.Add(new { pid = row["ProcessId"], name = row["Name"], executable = row["ExecutablePath"], workingSetBytes = row["WorkingSetSize"], userModeTime = row["UserModeTime"], kernelModeTime = row["KernelModeTime"], window = Capture(() => ProcessWindow(Convert.ToUInt32(row["ProcessId"]))) });
            var report = new {
                schemaVersion = 1, scope = "guest-installed-read-only-inventory", physicalDesktopTouched = false, installationModified = false,
                programRoot = program, configuredProgramRoot = configuredRoot, programRootExists = Directory.Exists(program), programRootEmpty = !Directory.Exists(program) || children.Length == 0,
                programRootEntries = children.Select(Path.GetFileName).ToArray(), versions, launcherOwnership = Capture(() => LauncherOwnership(program)),
                committedState = SelectedJson(Path.Combine(program, "active-install.json"), new[] { "schemaVersion", "generation", "executable", "version", "sha256", "payloadSha256" }),
                dataRoot = data, dataRootExists = Directory.Exists(data), dataRootEntries = dataChildren.Select(child => new { name = Path.GetFileName(child), directory = Directory.Exists(child) }).ToArray(), metadata,
                configuredPaths = paths, preferences = SelectedJson(settings, new[] { "Theme", "ThumbnailSize" }), shortcuts, registrations = Capture(() => Registrations(data)), browser = progressOnly ? null : Capture(() => BrowserMetadata(data)), canvases = progressOnly || browserOnly ? null : Capture(() => CanvasMetadata(data)), library = progressOnly || browserOnly ? null : Capture(() => LibrarySnapshot(package, paths)), fixtureImages = progressOnly || browserOnly ? null : Capture(() => FixtureImages(paths, data)),
                updateProgress = Capture(() => UpdateProgress(data)), cleanupDiagnostics = Capture(() => CleanupDiagnostics(data)), progressOnly, browserOnly,
                running, inheritedIsolatedDataIgnored = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MORI_DESK_DATA")),
                completedUtc = DateTime.UtcNow.ToString("O")
            };
            string destination = Path.Combine(package, "audit-installed-inventory" + (label == "" ? "" : "-" + label) + ".json"); NoLinks(destination);
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) using (var writer = new StreamWriter(output, new UTF8Encoding(false))) writer.Write(Json.Serialize(report));
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 90; }
    }
}
