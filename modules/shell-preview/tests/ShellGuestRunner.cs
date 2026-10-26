using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

[assembly: AssemblyTitle("Nook update isolated regression runner - TEST ONLY")]
[assembly: AssemblyProduct("Nook update isolated regression runner - TEST ONLY")]
[assembly: AssemblyVersion("0.0.0.1")]
[assembly: AssemblyFileVersion("0.0.0.1")]

// This is not Nook or an installer. The historic guest agent expects a file
// called Mori.exe when deploying a package; this test-only runner satisfies
// that transport convention without carrying or launching a real application.
internal static class UpdateRegressionGuest {
    const string Audit = @"C:\NookLab\Audit";
    const int StreamLimit = 4 * 1024 * 1024;
    static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
    // An exceptional stream close may complete after the owning process has
    // been disposed. Late EOF must not throw on a ThreadPool callback.
    static void Signal(ManualResetEvent signal) { try { signal.Set(); } catch (ObjectDisposedException) { } }
    public sealed class Package {
        public int schemaVersion;
        public string scope;
        public FileEntry[] files;
        public TestEntry[] tests;
    }
    public sealed class FileEntry { public string relative, sha256; }
    public sealed class CopyEntry { public string source, relative; }
    public sealed class TestEntry { public string id, executable; public string[] arguments; public int timeoutSeconds; public CopyEntry[] fixtureFiles; }
    static void Require(bool condition, string reason) { if (!condition) throw new InvalidDataException(reason); }
    static string Hash(string file) { using (var input = File.OpenRead(file)) using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(input)).Replace("-", ""); }
    static string StrictPath(string root, string relative) {
        Require(!string.IsNullOrWhiteSpace(relative) && !Path.IsPathRooted(relative) && relative.IndexOf(':') < 0, "Only relative package paths are allowed.");
        string[] parts = relative.Replace('/', '\\').Split('\\');
        Require(parts.All(p => p.Length > 0 && p != "." && p != ".." && p.TrimEnd(' ', '.') == p), "Ambiguous package path refused.");
        string result = Path.GetFullPath(Path.Combine(root, relative));
        Require(result.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase), "Path escaped its scoped root.");
        NoLinks(result); return result;
    }
    static void NoLinks(string path) {
        for (string at = path; !string.IsNullOrEmpty(at); at = Path.GetDirectoryName(at))
            if ((File.Exists(at) || Directory.Exists(at)) && (File.GetAttributes(at) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked test path refused.");
    }
    static void Guest() {
        bool guest = false;
        using (var query = new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem"))
            foreach (ManagementObject row in query.Get()) using (row) {
                string hardware = Convert.ToString(row["Manufacturer"]) + " " + Convert.ToString(row["Model"]);
                guest |= System.Text.RegularExpressions.Regex.IsMatch(hardware, "QEMU|KVM|Bochs", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
        Require(guest, "REFUSED: dedicated QEMU guest required before writes or child execution.");
    }
    static string Quote(string value) {
        Require(value != null && value.IndexOf('"') < 0 && !value.EndsWith("\\", StringComparison.Ordinal), "Unsupported test argument quotation.");
        return "\"" + value + "\"";
    }
    static string Argument(string value, string package, string fixture) {
        if (value == "@case") return fixture;
        // Some destructive-safety suites create their own root and correctly
        // reject an existing directory. Keep their fresh root beneath this
        // run's owned envelope without pre-creating it.
        if (value == "@freshcase") return StrictPath(fixture, "fresh-case");
        if (value.StartsWith("@package/", StringComparison.Ordinal)) return StrictPath(package, value.Substring(9));
        Require(value.Length < 256 && value.IndexOf('@') < 0 && value.IndexOf('\\') < 0 && value.IndexOf('/') < 0 && value.IndexOf(':') < 0, "Test arguments must be literals or scoped path tokens.");
        return value;
    }
    static int Main(string[] args) {
        try {
            Guest(); Require(args.Length == 1 && args[0] == "--run-isolated-tests", "Explicit isolated test command required.");
            string package = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');
            Require(System.Text.RegularExpressions.Regex.IsMatch(package, @"\AC:\\NookLab\\Audit\\apps\\[A-Fa-f0-9]{12}\z"), "Pinned guest package directory required.");
            NoLinks(package);
            string manifestFile = StrictPath(package, "isolated-tests.json");
            Require(new FileInfo(manifestFile).Length <= 1024 * 1024, "Bounded test manifest required.");
            var manifest = Json.Deserialize<Package>(File.ReadAllText(manifestFile, Utf8));
            Require(manifest != null && manifest.schemaVersion == 1 && manifest.scope == "synthetic-regressions-only-no-installation", "Not a test-only package.");
            Require(manifest.files != null && manifest.files.Length > 0 && manifest.files.Length <= 512 && manifest.tests != null && manifest.tests.Length > 0 && manifest.tests.Length <= 20, "Bounded file/test inventory required.");
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.files) {
                string path = StrictPath(package, file.relative);
                Require(files.Add(path) && System.Text.RegularExpressions.Regex.IsMatch(file.sha256 ?? "", "\\A[A-Fa-f0-9]{64}\\z"), "Unique hash-pinned input required.");
                Require(Hash(path).Equals(file.sha256, StringComparison.OrdinalIgnoreCase), "Changed input: " + file.relative);
            }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var test in manifest.tests) {
                Require(System.Text.RegularExpressions.Regex.IsMatch(test.id ?? "", "\\A[a-z0-9-]{1,60}\\z") && ids.Add(test.id), "Unique test case ID required.");
                Require(files.Contains(StrictPath(package, test.executable)) && test.executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase), "Only a pinned test executable may run.");
                Require(test.arguments != null && test.arguments.Length <= 16 && test.timeoutSeconds >= 1 && test.timeoutSeconds <= 900, "Bounded test arguments/deadline required.");
                foreach (string argument in test.arguments) Argument(argument, package, Audit + @"\data\synthetic-placeholder");
                if (test.fixtureFiles != null) {
                    Require(test.fixtureFiles.Length <= 20, "Bounded synthetic fixture list required."); var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var copy in test.fixtureFiles) { Require(files.Contains(StrictPath(package, copy.source)), "Fixture source must be hash-pinned."); Require(destinations.Add(StrictPath(Audit + @"\data\synthetic-placeholder", copy.relative)), "Duplicate fixture destination refused."); }
                }
            }
            string run = StrictPath(Audit, "data\\sv-" + Guid.NewGuid().ToString("N").Substring(0,8));
            Directory.CreateDirectory(run);
            var results = new List<object>(); bool passed = true;
            foreach (var test in manifest.tests) {
                string fixture = StrictPath(run, test.id); Directory.CreateDirectory(fixture);
                string temporary = StrictPath(fixture, "temp"); Directory.CreateDirectory(temporary);
                foreach (var copy in test.fixtureFiles ?? new CopyEntry[0]) {
                    string source = StrictPath(package, copy.source), destination = StrictPath(fixture, copy.relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.Copy(source, destination, false);
                    Require(Hash(destination).Equals(manifest.files.Single(f => StrictPath(package, f.relative).Equals(source, StringComparison.OrdinalIgnoreCase)).sha256, StringComparison.OrdinalIgnoreCase), "Copied fixture changed.");
                }
                string executable = StrictPath(package, test.executable);
                string stdoutFile = StrictPath(fixture, "stdout.txt"), stderrFile = StrictPath(fixture, "stderr.txt");
                var output = new StringBuilder(); var errors = new StringBuilder(); object gate = new object(); bool overflow = false;
                var start = new ProcessStartInfo(executable, string.Join(" ", test.arguments.Select(a => Quote(Argument(a, package, fixture))))) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(executable) };
                start.EnvironmentVariables["MORI_DESK_DATA"] = StrictPath(fixture, "data"); start.EnvironmentVariables["TEMP"] = temporary; start.EnvironmentVariables["TMP"] = temporary;
                foreach (string key in new[] { "NOOK_UPDATE_REQUEST", "NOOK_UPDATE_EXPECTED_VERSION" }) start.EnvironmentVariables.Remove(key);
                var clock = Stopwatch.StartNew(); bool timedOut = false; int? exit = null; string failure = null;
                using (var outputClosed = new ManualResetEvent(false)) using (var errorClosed = new ManualResetEvent(false)) using (var process = new Process { StartInfo = start }) {
                    DataReceivedEventHandler receiveOutput = (sender, e) => { if (e.Data == null) { Signal(outputClosed); return; } lock (gate) { if (output.Length + e.Data.Length + 1 <= StreamLimit) output.AppendLine(e.Data); else overflow = true; } };
                    DataReceivedEventHandler receiveError = (sender, e) => { if (e.Data == null) { Signal(errorClosed); return; } lock (gate) { if (errors.Length + e.Data.Length + 1 <= StreamLimit) errors.AppendLine(e.Data); else overflow = true; } };
                    process.OutputDataReceived += receiveOutput; process.ErrorDataReceived += receiveError;
                    try {
                        Require(process.Start(), "Test process did not start."); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                        while (!process.WaitForExit(100)) { bool exceeded; lock (gate) exceeded = overflow; if (exceeded || clock.Elapsed.TotalSeconds >= test.timeoutSeconds) { timedOut = !exceeded; process.Kill(); break; } }
                        Require(process.WaitForExit(10000), "Owned test process did not exit after bounded stop.");
                        exit = process.ExitCode;
                        Require(outputClosed.WaitOne(5000) && errorClosed.WaitOne(5000), "Test output handles did not close; descendant lifetime is unresolved.");
                    } catch (Exception error) { failure = error.Message; try { if (!process.HasExited) { process.Kill(); process.WaitForExit(10000); } } catch { } }
                    finally { process.OutputDataReceived -= receiveOutput; process.ErrorDataReceived -= receiveError; }
                }
                lock (gate) { File.WriteAllText(stdoutFile, output.ToString(), Utf8); File.WriteAllText(stderrFile, errors.ToString(), Utf8); }
                bool success = exit == 0 && !timedOut && !overflow && failure == null; passed &= success;
                results.Add(new { id = test.id, passed = success, exitCode = exit, timedOut, outputOverflow = overflow, error = failure, elapsedMilliseconds = clock.ElapsedMilliseconds, stdout = output.ToString(), stderr = errors.ToString(), fixture });
                Console.WriteLine(test.id + ": " + (success ? "PASS" : "FAIL") + " (" + clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms)");
            }
            var report = new { schemaVersion = 1, scope = manifest.scope, guestGuardPassed = true, package, manifestSha256 = Hash(manifestFile), run, passed, physicalDesktopTouched = false, realInstallationTested = false, tests = results, completedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) };
            File.WriteAllText(StrictPath(package, "audit-update-regression.json"), Json.Serialize(report), Utf8);
            return passed ? 0 : 1;
        } catch (Exception error) { Console.Error.WriteLine(error); return 90; }
    }
}
