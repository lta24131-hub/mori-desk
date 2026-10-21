using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MoriDesk {
 internal static class AppUpdateServiceTests {
  const string Token = "github_pat_SYNTHETIC_TEST_ONLY_NEVER_A_REAL_SECRET";
  static readonly byte[] Installer = Encoding.UTF8.GetBytes("SYNTHETIC INSTALLER BYTES; NEVER EXECUTE");
  sealed class Request {
   internal string Url, Authorization, Accept;
  }
  sealed class Handler : HttpMessageHandler {
   internal readonly List<Request> Requests = new List<Request>();
   internal readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> Replies = new Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>>();
   internal void Reply(HttpResponseMessage value) { Replies.Enqueue((request, cancellation) => Task.FromResult(value)); }
   protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
    Requests.Add(new Request { Url = request.RequestUri.AbsoluteUri, Authorization = request.Headers.Authorization == null ? "" : request.Headers.Authorization.ToString(), Accept = string.Join(",", request.Headers.Accept.Select(a => a.ToString())) });
    if (Replies.Count == 0) throw new InvalidOperationException("No synthetic response is configured. Real networking is forbidden in update tests.");
    return Replies.Dequeue()(request, cancellationToken);
   }
  }
  sealed class Progress : IProgress<UpdateProgress> {
   internal readonly List<UpdateProgress> Values = new List<UpdateProgress>();
   public void Report(UpdateProgress value) { Values.Add(value); }
  }
  sealed class ReadStream : Stream {
   readonly byte[] bytes; int position; readonly int chunk;
   internal bool WasDisposed;
   internal ReadStream(byte[] value, int maximumChunk) { bytes = value; chunk = maximumChunk; }
   public override bool CanRead { get { return true; } } public override bool CanSeek { get { return false; } } public override bool CanWrite { get { return false; } }
   public override long Length { get { throw new NotSupportedException(); } } public override long Position { get { return position; } set { throw new NotSupportedException(); } }
   public override int Read(byte[] buffer, int offset, int count) { count = Math.Min(count, Math.Min(chunk, bytes.Length - position)); Buffer.BlockCopy(bytes, position, buffer, offset, count); position += count; return count; }
   public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Read(buffer, offset, count)); }
   public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); } public override void SetLength(long length) { throw new NotSupportedException(); } public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
   protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
  }
  sealed class StalledStream : Stream {
   internal bool WasDisposed;
   readonly TaskCompletionSource<int> pending = new TaskCompletionSource<int>();
   readonly TaskCompletionSource<bool> entered = new TaskCompletionSource<bool>();
   internal Task ReadEntered { get { return entered.Task; } }
   public override bool CanRead { get { return true; } } public override bool CanSeek { get { return false; } } public override bool CanWrite { get { return false; } }
   public override long Length { get { throw new NotSupportedException(); } } public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
   public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) { entered.TrySetResult(true); return pending.Task; }
   public override int Read(byte[] buffer, int offset, int count) { throw new InvalidOperationException("Synchronous reads are forbidden"); }
   public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); } public override void SetLength(long length) { throw new NotSupportedException(); } public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
   protected override void Dispose(bool disposing) { WasDisposed = true; pending.TrySetException(new IOException("Synthetic disposed stream")); base.Dispose(disposing); }
  }
  static string Hash(byte[] value) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(value)).Replace("-", "").ToLowerInvariant(); }
  static Dictionary<string, object> Asset(string name, long id, long size, string digest) { return new Dictionary<string, object> { { "name", name }, { "id", id }, { "size", size }, { "digest", digest }, { "browser_download_url", "https://attacker.invalid/never-used" } }; }
  static Dictionary<string, object> Release(string tag, bool preview, bool draft, params Dictionary<string, object>[] assets) { return new Dictionary<string, object> { { "tag_name", tag }, { "prerelease", preview }, { "draft", draft }, { "body", "Synthetic release notes" }, { "html_url", "https://attacker.invalid/never-used" }, { "assets", assets } }; }
  static Dictionary<string, object> InstallerAsset() { return Asset("Nook-Setup.exe", 101, Installer.Length, "sha256:" + Hash(Installer)); }
  static HttpResponseMessage Bytes(byte[] value) { return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(value) }; }
  static HttpResponseMessage Json(params Dictionary<string, object>[] values) { return Bytes(Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(values))); }
  static HttpResponseMessage Redirect(string location) { var value = new HttpResponseMessage(HttpStatusCode.Found); value.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute); return value; }
  static UpdateRelease DownloadRelease() { return new UpdateRelease { Version = new Version(0, 2, 0, 0), Tag = "v0.2.0-preview", AssetId = 101, Size = Installer.Length, ExpectedSha256 = Hash(Installer) }; }
  static async Task<bool> Reject<T>(Func<Task> operation) where T : Exception { try { await operation().ConfigureAwait(false); return false; } catch (T) { return true; } }
  static async Task AwaitBounded(Task operation, string message) {
   if (await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false) != operation) throw new TimeoutException(message);
   await operation.ConfigureAwait(false);
  }
  static bool Reject<T>(Action operation) where T : Exception { try { operation(); return false; } catch (T) { return true; } }
  internal static void Run(string root, Action<bool, string> check) {
   string testRoot = Path.Combine(root, "app-update-fixtures"); Directory.CreateDirectory(testRoot);
   RunAsync(testRoot, check).GetAwaiter().GetResult();
  }
  static async Task RunAsync(string root, Action<bool, string> check) {
   await Versions(check).ConfigureAwait(false);
   await Checksums(check).ConfigureAwait(false);
   await Download(root, check).ConfigureAwait(false);
   await Boundaries(root, check).ConfigureAwait(false);
   await Cancellation(root, check).ConfigureAwait(false);
   Credentials(root, check);
  }
  static async Task Versions(Action<bool, string> check) {
   var releases = new[] {
    Release("v0.1.9", false, false, InstallerAsset()), Release("v0.1.11-preview", true, false, InstallerAsset()),
    Release("v9.0.0", false, true, InstallerAsset()), Release("v8.0.0", false, false), Release("not-a-version", false, false, InstallerAsset()),
    Release("v0.1.10", false, false, InstallerAsset()), Release("v0.1.12-preview", false, false, InstallerAsset())
   };
   var handler = new Handler(); handler.Reply(Json(releases));
   using (var service = new AppUpdateService(handler)) {
    var release = await service.CheckAsync(new Version(0, 1, 9, 0), "", false, CancellationToken.None).ConfigureAwait(false);
    check(release != null && release.Tag == "v0.1.10" && release.Version == new Version(0, 1, 10, 0), "updater compares numeric versions and excludes preview tags, drafts, invalid/source-only releases");
    check(release.ExpectedSha256 == Hash(Installer) && release.AssetId == 101 && release.Size == Installer.Length, "updater obtains exact installer identity and trusted API digest");
    check(release.HtmlUrl == "https://github.com/lta24131-hub/mori-desk/releases/tag/v0.1.10", "updater ignores untrusted release/asset web URLs");
    check(handler.Requests.Count == 1 && handler.Requests[0].Url == "https://api.github.com/repos/lta24131-hub/mori-desk/releases?per_page=100&page=1" && handler.Requests[0].Authorization == "", "public update check works anonymously against fixed official API only");
   }
   handler = new Handler(); handler.Reply(Json(releases));
   using (var service = new AppUpdateService(handler)) {
    var release = await service.CheckAsync(new Version(0, 1, 9), Token, true, CancellationToken.None).ConfigureAwait(false);
    check(release.Tag == "v0.1.12-preview" && handler.Requests[0].Authorization == "Bearer " + Token, "preview opt-in and optional private-repository bearer token are supported");
   }
   handler = new Handler(); handler.Reply(Json(Release("v0.1.10", false, false, InstallerAsset())));
   using (var service = new AppUpdateService(handler)) check(await service.CheckAsync(new Version(0, 1, 10, 0), "", true, CancellationToken.None).ConfigureAwait(false) == null, "equal numeric release version does not reinstall or downgrade");
   handler = new Handler(); handler.Reply(Json(Enumerable.Range(0, 100).Select(i => Release("v0.1.0", false, true)).ToArray())); handler.Reply(Json(Release("v0.2.0", false, false, InstallerAsset())));
   using (var service = new AppUpdateService(handler)) {
    var release = await service.CheckAsync(new Version(0, 1, 0), "", true, CancellationToken.None).ConfigureAwait(false);
    check(release.Tag == "v0.2.0" && handler.Requests.Count == 2 && handler.Requests[1].Url.EndsWith("page=2"), "release pagination uses bounded fixed repository pages, not arbitrary Link headers");
   }
   handler = new Handler(); handler.Reply(Json(Release("v0.2.0", false, false, InstallerAsset(), InstallerAsset())));
   using (var service = new AppUpdateService(handler)) check(await Reject<InvalidDataException>(async () => { await service.CheckAsync(new Version(0, 1, 0), "", true, CancellationToken.None); }), "duplicate exact installer assets fail closed");
   handler = new Handler(); handler.Reply(new HttpResponseMessage(HttpStatusCode.Forbidden));
   using (var service = new AppUpdateService(handler)) check(await Reject<UnauthorizedAccessException>(async () => { await service.CheckAsync(new Version(0, 1), Token, true, CancellationToken.None); }), "GitHub permission or rate-limit errors are not reported as already current");
  }
  static async Task Checksums(Action<bool, string> check) {
   string text = "\uFEFF" + new string('a', 64) + "  Another.exe\n" + Hash(Installer).ToUpperInvariant() + " *Nook-Setup.exe\r\n";
   byte[] checksum = Encoding.UTF8.GetBytes(text);
   var handler = new Handler(); handler.Reply(Json(Release("v0.2.0", false, false, Asset("Nook-Setup.exe", 101, Installer.Length, null), Asset("SHA256SUMS.txt", 102, checksum.Length, null)))); handler.Reply(Bytes(checksum));
   using (var service = new AppUpdateService(handler)) {
    var release = await service.CheckAsync(new Version(0, 1), Token, true, CancellationToken.None).ConfigureAwait(false);
    check(release.ExpectedSha256 == Hash(Installer) && handler.Requests[1].Url == "https://api.github.com/repos/lta24131-hub/mori-desk/releases/assets/102", "same-release SHA256SUMS exact installer entry is accepted via asset API");
    check(handler.Requests[1].Accept == "application/octet-stream" && handler.Requests[1].Authorization == "Bearer " + Token, "private checksum asset requests use correct API headers");
   }
   check(Reject<InvalidDataException>(() => AppUpdateService.ParseChecksums(Hash(Installer) + "  sub/Nook-Setup.exe\n")), "checksum path prefixes cannot impersonate exact installer name");
   check(Reject<InvalidDataException>(() => AppUpdateService.ParseChecksums(Hash(Installer) + "  Nook-Setup.exe\n" + Hash(Installer) + "  Nook-Setup.exe\n")), "duplicate checksum entries fail closed even if equal");
   check(Reject<InvalidDataException>(() => AppUpdateService.ParseChecksums("xyz  Nook-Setup.exe")), "non-SHA256 checksum does not authorize installer");
   handler = new Handler(); handler.Reply(Json(Release("v0.2.0", false, false, Asset("Nook-Setup.exe", 101, Installer.Length, null))));
   using (var service = new AppUpdateService(handler)) check(await Reject<InvalidDataException>(async () => { await service.CheckAsync(new Version(0, 1), "", true, CancellationToken.None); }), "missing trusted digest and checksum file blocks update rather than trusting download alone");
   handler = new Handler(); handler.Reply(Json(Release("v0.2.0", false, false, Asset("Nook-Setup.exe", 101, AppUpdateService.MaxInstallerBytes + 1, "sha256:" + Hash(Installer)))));
   using (var service = new AppUpdateService(handler)) check(await Reject<InvalidDataException>(async () => { await service.CheckAsync(new Version(0, 1), "", true, CancellationToken.None); }), "oversized release asset is rejected before download");
  }
  static async Task Download(string root, Action<bool, string> check) {
   var handler = new Handler(); handler.Reply(Redirect("https://release-assets.githubusercontent.com/github-production-release-asset/test?signature=SYNTHETIC")); handler.Reply(Bytes(Installer));
   var progress = new Progress();
   using (var service = new AppUpdateService(handler)) {
    string file = await service.DownloadAsync(DownloadRelease(), Token, Path.Combine(root, "success"), progress, CancellationToken.None).ConfigureAwait(false);
    check(File.Exists(file) && Path.GetFileName(file) == "Nook-Setup.exe" && File.ReadAllBytes(file).SequenceEqual(Installer), "verified download atomically publishes exact installer bytes without executing");
    check(!File.Exists(file + ".partial") && Directory.GetFiles(Path.GetDirectoryName(file)).Length == 1, "successful staging leaves no partial or unrelated executable");
    check(handler.Requests[0].Authorization == "Bearer " + Token && handler.Requests[1].Authorization == "", "private token is never forwarded to release CDN redirect");
    check(progress.Values.First().Downloaded == 0 && progress.Values.Last().Stage == "ready" && progress.Values.Last().Downloaded == Installer.Length && progress.Values.Any(p => p.Stage == "verifying"), "progress distinguishes download, verification, and ready only after verified rename");
   }
   handler = new Handler(); handler.Reply(Redirect("https://api.github.com/repos/lta24131-hub/mori-desk/releases/assets/101?redirect=1")); handler.Reply(Bytes(Installer));
   using (var service = new AppUpdateService(handler)) {
    await service.DownloadAsync(DownloadRelease(), Token, Path.Combine(root, "same-origin-redirect"), null, CancellationToken.None).ConfigureAwait(false);
    check(handler.Requests[1].Authorization == "", "even same-origin redirects cannot forward token");
   }
   handler = new Handler(); var stream = new ReadStream(Installer, 3); handler.Reply(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
   using (var service = new AppUpdateService(handler)) {
    string file = await service.DownloadAsync(DownloadRelease(), "", Path.Combine(root, "chunked"), null, CancellationToken.None).ConfigureAwait(false);
    check(File.ReadAllBytes(file).SequenceEqual(Installer) && stream.WasDisposed, "bounded streaming handles unknown Content-Length in multiple chunks and disposes source");
   }
  }
  static bool NoInstaller(string root) { return !Directory.Exists(root) || Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length == 0; }
  static async Task Boundaries(string root, Action<bool, string> check) {
   foreach (string url in new[] { "http://release-assets.githubusercontent.com/a", "https://release-assets.githubusercontent.com.attacker.invalid/a", "https://attacker.invalid/a", "https://user:pass@github.com/a", "https://github.com:444/a", "file:///C:/Windows/not-real.exe" }) {
    var handler = new Handler(); handler.Reply(Redirect(url)); string output = Path.Combine(root, "unsafe-" + Guid.NewGuid().ToString("N"));
    using (var service = new AppUpdateService(handler)) check(await Reject<InvalidDataException>(async () => { await service.DownloadAsync(DownloadRelease(), Token, output, null, CancellationToken.None); }) && handler.Requests.Count == 1 && NoInstaller(output), "unsafe redirect fails before token or data reaches " + new Uri(url).Host);
   }
   var h = new Handler(); h.Reply(Redirect("https://github.com/a"));
   using (var service = new AppUpdateService(h)) check(await Reject<InvalidDataException>(async () => { await service.CheckAsync(new Version(0, 1), Token, true, CancellationToken.None); }), "release metadata does not follow even GitHub redirects");
   h = new Handler(); for (int i = 0; i < 5; i++) h.Reply(Redirect("https://objects.githubusercontent.com/a"));
   using (var service = new AppUpdateService(h)) check(await Reject<InvalidDataException>(async () => { await service.DownloadAsync(DownloadRelease(), Token, Path.Combine(root, "redirect-loop"), null, CancellationToken.None); }) && h.Requests.Count == 5, "asset redirect loops are bounded");
   h = new Handler(); h.Reply(Bytes(Installer)); var release = DownloadRelease(); release.ExpectedSha256 = new string('0', 64); string folder = Path.Combine(root, "wrong-hash");
   using (var service = new AppUpdateService(h)) check(await Reject<InvalidDataException>(async () => { await service.DownloadAsync(release, "", folder, null, CancellationToken.None); }) && NoInstaller(folder), "hash mismatch never promotes a final executable and removes only its partial");
   h = new Handler(); h.Reply(Bytes(Installer.Take(Installer.Length - 1).ToArray())); folder = Path.Combine(root, "wrong-length");
   using (var service = new AppUpdateService(h)) check(await Reject<InvalidDataException>(async () => { await service.DownloadAsync(DownloadRelease(), "", folder, null, CancellationToken.None); }) && NoInstaller(folder), "Content-Length mismatch fails before writing installer");
   h = new Handler(); h.Reply(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ReadStream(Installer.Concat(new byte[] { 1 }).ToArray(), 7)) }); folder = Path.Combine(root, "stream-overflow");
   using (var service = new AppUpdateService(h)) check(await Reject<InvalidDataException>(async () => { await service.DownloadAsync(DownloadRelease(), "", folder, null, CancellationToken.None); }) && NoInstaller(folder), "unknown-length stream cannot grow past declared release size");
   h = new Handler(); h.Reply(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ReadStream(Installer.Take(Installer.Length - 1).ToArray(), 7)) }); folder = Path.Combine(root, "stream-truncated");
   using (var service = new AppUpdateService(h)) check(await Reject<InvalidDataException>(async () => { await service.DownloadAsync(DownloadRelease(), "", folder, null, CancellationToken.None); }) && NoInstaller(folder), "unknown-length truncated stream cannot produce verified result");
   check(Reject<InvalidDataException>(() => AppUpdateService.SafeStagingRoot("relative")) && Reject<InvalidDataException>(() => AppUpdateService.SafeStagingRoot("C:relative")) && Reject<InvalidDataException>(() => AppUpdateService.SafeStagingRoot(@"\relative")) && Reject<InvalidDataException>(() => AppUpdateService.SafeStagingRoot(@"C:\")) && Reject<InvalidDataException>(() => AppUpdateService.SafeStagingRoot(@"\\server\share\updates")), "staging refuses relative, drive-relative, disk-root and UNC paths");
   check(Reject<ArgumentException>(() => AppUpdateService.NormalizeToken("abc\r\nInjected: value")), "token header injection is rejected before requests");
  }
  static async Task Cancellation(string root, Action<bool, string> check) {
   var handler = new Handler(); using (var cancel = new CancellationTokenSource()) using (var service = new AppUpdateService(handler)) {
    cancel.Cancel(); check(await Reject<OperationCanceledException>(async () => { await service.DownloadAsync(DownloadRelease(), "", Path.Combine(root, "pre-cancel"), null, cancel.Token); }) && handler.Requests.Count == 0, "pre-cancelled download performs no request and no staging write");
   }
   handler = new Handler(); var undelivered = new ReadStream(Installer, 3); string unrequestedFolder = Path.Combine(root, "pre-cancel-unrequested");
   // A response queued in a fake handler belongs to the fixture until a request
   // actually obtains it. Pre-cancellation must not require disposing a stream
   // the service never received; the fixture releases it after this assertion.
   using (var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(undelivered) }) {
    handler.Reply(response);
    using (var cancel = new CancellationTokenSource()) using (var service = new AppUpdateService(handler)) {
     cancel.Cancel();
     check(await Reject<OperationCanceledException>(async () => { await service.DownloadAsync(DownloadRelease(), "", unrequestedFolder, null, cancel.Token); }) && handler.Requests.Count == 0 && NoInstaller(unrequestedFolder) && !undelivered.WasDisposed, "pre-cancelled download leaves no installer and never takes ownership of an undelivered response stream");
    }
   }
   handler = new Handler(); handler.Replies.Enqueue(async (request, cancellation) => { await Task.Delay(Timeout.Infinite, cancellation); return Bytes(Installer); });
   using (var service = new AppUpdateService(handler, TimeSpan.FromMilliseconds(30))) check(await Reject<TimeoutException>(async () => { await service.CheckAsync(new Version(0, 1), "", true, CancellationToken.None); }), "unresponsive response headers have bounded timeout");
   handler = new Handler(); var stalled = new StalledStream(); handler.Reply(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stalled) }); string folder = Path.Combine(root, "read-timeout");
   using (var service = new AppUpdateService(handler, TimeSpan.FromMilliseconds(30))) check(await Reject<TimeoutException>(async () => { await service.DownloadAsync(DownloadRelease(), "", folder, null, CancellationToken.None); }) && NoInstaller(folder) && stalled.WasDisposed, "stalled body times out, disposes network stream and removes partial");
   handler = new Handler(); stalled = new StalledStream(); handler.Reply(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stalled) }); folder = Path.Combine(root, "read-cancel");
   using (var cancel = new CancellationTokenSource()) using (var service = new AppUpdateService(handler, TimeSpan.FromSeconds(5))) {
    // Cancel only after the response stream is owned and its first read really
    // started. A 30ms wall-clock timer can fire before staging/headers on a slow
    // VM, which tests pre-cancellation instead of in-flight stream disposal.
    var cancelled = Reject<OperationCanceledException>(async () => { await service.DownloadAsync(DownloadRelease(), Token, folder, null, cancel.Token); });
    try {
     var first = await Task.WhenAny(stalled.ReadEntered, cancelled, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
     if (first != stalled.ReadEntered) {
      if (first == cancelled) { await cancelled.ConfigureAwait(false); throw new InvalidOperationException("Synthetic cancellation download completed before its first stream read."); }
      throw new TimeoutException("Synthetic cancellation stream did not enter ReadAsync within 30 seconds.");
     }
     cancel.Cancel();
     await AwaitBounded(cancelled, "In-flight user cancellation did not complete within 30 seconds.").ConfigureAwait(false);
     check(cancelled.Result && NoInstaller(folder) && stalled.WasDisposed, "user cancellation is distinct from timeout and cannot leave installable file");
    } finally { cancel.Cancel(); }
   }
  }
  static void Credentials(string root, Action<bool, string> check) {
   string config = Path.Combine(root, "credentials"); var credentials = new AppUpdateCredentials(config);
   check(!credentials.HasStoredToken && credentials.Load() == "", "optional missing credentials remain anonymous without creating files");
   credentials.Save(Token); string file = Path.Combine(config, "update", "github-token.dat");
   check(credentials.HasStoredToken && credentials.Load() == Token && !Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains(Token), "DPAPI round-trip encrypts synthetic token for current Windows account");
   credentials.Save(Token + "_CHANGED"); check(credentials.Load() == Token + "_CHANGED" && Directory.GetFiles(Path.GetDirectoryName(file)).Length == 1, "credential replacement is atomic and leaves no plaintext or temporary copy");
   File.WriteAllBytes(file, Encoding.UTF8.GetBytes("corrupted synthetic DPAPI bytes"));
   check(Reject<InvalidDataException>(() => credentials.Load()), "corrupt credentials fail clearly without silently using invalid token");
   credentials.Clear(); credentials.Clear(); check(!credentials.HasStoredToken && credentials.Load() == "", "clearing credentials is idempotent and allows anonymous update checks");
   check(Reject<ArgumentException>(() => credentials.Save("")), "blank credential cannot be stored as a pretend connected account");
  }
#if APP_UPDATE_STANDALONE
  public static int Main() {
   string root = Path.Combine(Path.GetTempPath(), "nook-update-tests-" + Guid.NewGuid().ToString("N")); int checks = 0;
   try { Run(root, (ok, message) => { checks++; if (!ok) throw new Exception(message); }); Console.WriteLine("PASS " + checks + " isolated updater checks. Fixtures: " + root); return 0; }
   catch (Exception ex) { Console.WriteLine("FAIL " + checks + " " + ex); return 1; }
  }
#endif
 }
}
#if APP_UPDATE_STANDALONE
namespace Mori { internal static class Settings { internal static string ConfigDir { get { throw new InvalidOperationException("Standalone tests must inject their own fixture root."); } } } }
#endif
