using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MoriDesk {
 internal sealed class UpdateRelease {
  public Version Version { get; set; }
  public string Tag { get; set; }
  public string Notes { get; set; }
  public string HtmlUrl { get; set; }
  public long AssetId { get; set; }
  public long Size { get; set; }
  public string ExpectedSha256 { get; set; }
 }
 internal sealed class UpdateProgress {
  public long Downloaded { get; set; }
  public long Total { get; set; }
  public string Stage { get; set; }
 }

 // This class only discovers and stages a verified installer. It never runs
 // one, closes Nook, changes a library, or modifies an existing installation.
 internal sealed class AppUpdateService : IDisposable {
  internal const string Repository = "lta24131-hub/mori-desk";
  internal const string InstallerName = "Nook-Setup.exe";
  internal const long MaxInstallerBytes = 2147483648L;
  const int MaxMetadataBytes = 4194304;
  const int MaxChecksumBytes = 1048576;
  const string Api = "https://api.github.com/repos/" + Repository;
  const string Web = "https://github.com/" + Repository;
  static readonly Regex TagPattern = new Regex(@"^[vV](\d+)\.(\d+)\.(\d+)(?:\.(\d+))?(?:-([0-9A-Za-z][0-9A-Za-z.-]*))?$", RegexOptions.CultureInvariant);
  static readonly Regex HashPattern = new Regex(@"^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant);
  readonly HttpClient client;
  readonly TimeSpan inactivityTimeout;
  readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = MaxMetadataBytes, RecursionLimit = 80 };

  internal AppUpdateService() : this(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, MaxResponseHeadersLength = 64 }, TimeSpan.FromSeconds(30)) { }
  internal AppUpdateService(HttpMessageHandler handler) : this(handler, TimeSpan.FromSeconds(30)) { }
  internal AppUpdateService(HttpMessageHandler handler, TimeSpan timeout) {
   if (handler == null) throw new ArgumentNullException("handler");
   if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException("timeout");
   var http = handler as HttpClientHandler;
   if (http != null) { http.AllowAutoRedirect = false; http.UseCookies = false; http.UseDefaultCredentials = false; }
   inactivityTimeout = timeout;
   client = new HttpClient(handler, true) { Timeout = Timeout.InfiniteTimeSpan };
  }

  internal static string NormalizeToken(string value) {
   value = (value ?? "").Trim();
   if (value.Length > 4096 || value.Any(c => c < 33 || c > 126)) throw new ArgumentException("GitHub 访问令牌包含无效字符，请重新粘贴。");
   return value;
  }
  internal static Version NumericVersion(Version value) {
   if (value == null) throw new ArgumentNullException("value");
   return new Version(value.Major, value.Minor, Math.Max(0, value.Build), Math.Max(0, value.Revision));
  }
  static Version ParseTag(string tag) {
   var match = TagPattern.Match(tag ?? "");
   Version value;
   if (!match.Success || !Version.TryParse(match.Groups[1].Value + "." + match.Groups[2].Value + "." + match.Groups[3].Value + "." + (match.Groups[4].Success ? match.Groups[4].Value : "0"), out value)) return null;
   return value;
  }
  static object Value(Dictionary<string, object> value, string key) { object result; return value != null && value.TryGetValue(key, out result) ? result : null; }
  static string Text(Dictionary<string, object> value, string key) { return Value(value, key) as string ?? ""; }
  static long Number(Dictionary<string, object> value, string key) { long number; return long.TryParse(Convert.ToString(Value(value, key), CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : -1; }
  static bool Flag(Dictionary<string, object> value, string key) { return true.Equals(Value(value, key)); }
  static IEnumerable<Dictionary<string, object>> Objects(object value) {
   var sequence = value as IEnumerable;
   if (sequence == null || value is string) yield break;
   foreach (var item in sequence) { var dictionary = item as Dictionary<string, object>; if (dictionary != null) yield return dictionary; }
  }
  static string Digest(Dictionary<string, object> asset) {
   string digest = Text(asset, "digest");
   return digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && HashPattern.IsMatch(digest.Substring(7)) ? digest.Substring(7).ToLowerInvariant() : null;
  }
  static Dictionary<string, object> Asset(Dictionary<string, object> release, string name) {
   var matches = Objects(Value(release, "assets")).Where(a => Text(a, "name") == name).ToList();
   if (matches.Count > 1) throw new InvalidDataException("GitHub 发布包含重复的更新文件，已停止更新。");
   return matches.Count == 1 ? matches[0] : null;
  }

  internal async Task<UpdateRelease> CheckAsync(Version current, string token, bool includePrerelease, CancellationToken cancellationToken) {
   current = NumericVersion(current);
   token = NormalizeToken(token);
   var candidates = new List<KeyValuePair<Version, Dictionary<string, object>>>();
   // Bounded pagination handles recent source-only releases without trusting a
   // caller-provided API URL or a cross-origin Link header.
   for (int page = 1; page <= 3; page++) {
    cancellationToken.ThrowIfCancellationRequested();
    byte[] bytes;
    using (var response = await SendAsync(new Uri(Api + "/releases?per_page=100&page=" + page), token, false, cancellationToken).ConfigureAwait(false))
     bytes = await ReadBoundedAsync(response, MaxMetadataBytes, cancellationToken).ConfigureAwait(false);
    object decoded;
    try { decoded = json.DeserializeObject(Encoding.UTF8.GetString(bytes)); }
    catch (Exception ex) { throw new InvalidDataException("GitHub 更新信息无效，请稍后重试。", ex); }
    if (!(decoded is object[]) && !(decoded is ArrayList)) throw new InvalidDataException("GitHub 更新列表格式无效。");
    var releases = Objects(decoded).ToList();
    foreach (var release in releases) {
     string tag = Text(release, "tag_name");
     var version = ParseTag(tag);
     if (Flag(release, "draft") || version == null || version <= current) continue;
     if (!includePrerelease && (Flag(release, "prerelease") || tag.IndexOf('-') >= 0)) continue;
     if (Asset(release, InstallerName) != null) candidates.Add(new KeyValuePair<Version, Dictionary<string, object>>(version, release));
    }
    if (releases.Count < 100) break;
   }
   var latest = candidates.OrderByDescending(c => c.Key).ThenBy(c => Flag(c.Value, "prerelease")).FirstOrDefault();
   if (latest.Value == null) return null;
   var installer = Asset(latest.Value, InstallerName);
   long id = Number(installer, "id"), size = Number(installer, "size");
   if (id <= 0 || size <= 0 || size > MaxInstallerBytes) throw new InvalidDataException("安装包大小或身份无效，已停止更新。");
   string hash = Digest(installer);
   if (hash == null) {
    var checksum = Asset(latest.Value, "SHA256SUMS.txt");
    long checksumId = Number(checksum, "id"), checksumSize = Number(checksum, "size");
    if (checksumId <= 0 || checksumSize <= 0 || checksumSize > MaxChecksumBytes) throw new InvalidDataException("这次发布缺少可信的 SHA256 校验信息，不能安装。请联系发布者补齐。");
    byte[] bytes;
    using (var response = await SendAsync(AssetUri(checksumId), token, true, cancellationToken).ConfigureAwait(false)) {
     if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != checksumSize) throw new InvalidDataException("校验文件大小与发布记录不一致。");
     bytes = await ReadBoundedAsync(response, MaxChecksumBytes, cancellationToken).ConfigureAwait(false);
    }
    if (bytes.LongLength != checksumSize) throw new InvalidDataException("校验文件未完整下载。");
    hash = ParseChecksums(Encoding.UTF8.GetString(bytes));
   }
   string tagName = Text(latest.Value, "tag_name"), notes = Text(latest.Value, "body");
   return new UpdateRelease { Version = latest.Key, Tag = tagName, Notes = notes.Length > 32768 ? notes.Substring(0, 32768) : notes, HtmlUrl = Web + "/releases/tag/" + Uri.EscapeDataString(tagName), AssetId = id, Size = size, ExpectedSha256 = hash };
  }

  internal static string ParseChecksums(string text) {
   string found = null;
   foreach (string original in (text ?? "").TrimStart('\uFEFF').Split('\n')) {
    string line = original.TrimEnd('\r');
    var match = Regex.Match(line, @"^([a-fA-F0-9]{64})[ \t]+\*?Nook-Setup\.exe[ \t]*$", RegexOptions.CultureInvariant);
    if (!match.Success) continue;
    if (found != null) throw new InvalidDataException("SHA256 校验文件包含重复的安装包记录。");
    found = match.Groups[1].Value.ToLowerInvariant();
   }
   if (found == null) throw new InvalidDataException("SHA256 校验文件没有当前安装包的准确记录，不能安装。");
   return found;
  }
  static Uri AssetUri(long id) { return new Uri(Api + "/releases/assets/" + id.ToString(CultureInfo.InvariantCulture)); }
  static bool ApiOrigin(Uri uri) { return uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) && uri.Port == 443 && string.IsNullOrEmpty(uri.UserInfo); }
  internal static bool AllowedAssetRedirect(Uri uri) {
   if (uri == null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return false;
   string host = uri.Host.ToLowerInvariant();
   return host == "release-assets.githubusercontent.com" || host == "objects.githubusercontent.com" || host == "github-releases.githubusercontent.com" || host == "github.com" || host == "api.github.com";
  }
  static bool Redirect(HttpStatusCode code) { int c = (int)code; return c == 301 || c == 302 || c == 303 || c == 307 || c == 308; }
  async Task<HttpResponseMessage> SendAsync(Uri uri, string token, bool asset, CancellationToken cancellationToken) {
   bool first = true;
   for (int redirects = 0; ; redirects++) {
    cancellationToken.ThrowIfCancellationRequested();
    using (var request = new HttpRequestMessage(HttpMethod.Get, uri)) {
     request.Headers.UserAgent.ParseAdd("Nook-Updater/1.0");
     request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(asset ? "application/octet-stream" : "application/vnd.github+json"));
     request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
     // Never copy Authorization through even a same-origin redirect. In
     // particular signed GitHub CDN URLs must never receive the private PAT.
     if (first && ApiOrigin(uri) && token.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
     HttpResponseMessage response;
     using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)) {
      timeout.CancelAfter(inactivityTimeout);
      try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false); }
      catch (OperationCanceledException) { cancellationToken.ThrowIfCancellationRequested(); throw new TimeoutException("连接 GitHub 超时，请检查网络后重试。"); }
     }
     if (Redirect(response.StatusCode)) {
      Uri location = response.Headers.Location, next = null;
      bool valid = location != null && Uri.TryCreate(uri, location, out next) && AllowedAssetRedirect(next);
      response.Dispose();
      if (!asset || !valid || redirects >= 4) throw new InvalidDataException("GitHub 下载重定向不安全或次数过多，已停止更新。");
      uri = next; first = false; continue;
     }
     if (!response.IsSuccessStatusCode) {
      int status = (int)response.StatusCode; response.Dispose();
      if (status == 401 || status == 403 || status == 404) throw new UnauthorizedAccessException("暂时无法读取 GitHub 发布信息（"+status+"）。请检查网络或稍后重试，也可以在 GitHub 项目页查看是否有可用发布。");
      throw new HttpRequestException("GitHub 返回错误（" + status + "），请稍后重试。");
     }
     return response;
    }
   }
  }
  async Task<int> ReadAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken) {
   cancellationToken.ThrowIfCancellationRequested();
   var read = stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
   using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)) {
    var delay = Task.Delay(inactivityTimeout, timeout.Token);
    if (await Task.WhenAny(read, delay).ConfigureAwait(false) != read) {
     // Dispose the response at the caller before any subsequent operation.
     // Observe a late network read fault without allowing it to touch a file.
     ObserveLateFault(read);
     cancellationToken.ThrowIfCancellationRequested();
     throw new TimeoutException("下载长时间没有响应，请检查网络后重试。");
    }
    timeout.Cancel();
    return await read.ConfigureAwait(false);
   }
  }
  static void ObserveLateFault(Task task) { task.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted); }
  async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken cancellationToken) {
   if (response.Content == null) throw new InvalidDataException("GitHub 返回了空内容。");
   if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value > limit) throw new InvalidDataException("GitHub 返回内容超过安全限制。");
   using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
   using (var memory = new MemoryStream()) {
    var buffer = new byte[32768]; int count;
    while ((count = await ReadAsync(stream, buffer, cancellationToken).ConfigureAwait(false)) != 0) {
     if (memory.Length + count > limit) throw new InvalidDataException("GitHub 返回内容超过安全限制。");
     memory.Write(buffer, 0, count);
    }
    return memory.ToArray();
   }
  }
  internal static string SafeStagingRoot(string value) {
   if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value) || value.StartsWith(@"\\", StringComparison.Ordinal)) throw new InvalidDataException("更新暂存目录必须是本机的绝对路径。");
   string drive = Path.GetPathRoot(value);
   if (drive.Length != 3 || !char.IsLetter(drive[0]) || drive[1] != ':' || (drive[2] != '\\' && drive[2] != '/')) throw new InvalidDataException("更新暂存目录必须包含明确的本机盘符和完整路径。");
   string root = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
   if (root.Length <= Path.GetPathRoot(root).TrimEnd('\\', '/').Length) throw new InvalidDataException("不能把磁盘根目录用作更新暂存目录。");
   for (string p = root; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p)) {
    if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("更新暂存目录不能包含链接或重解析点。");
    if (p == Path.GetPathRoot(p)) break;
   }
   return root;
  }

  internal async Task<string> DownloadAsync(UpdateRelease release, string token, string stagingRoot, IProgress<UpdateProgress> progress, CancellationToken cancellationToken) {
   token = NormalizeToken(token);
   if (release == null || release.AssetId <= 0 || release.Size <= 0 || release.Size > MaxInstallerBytes || !HashPattern.IsMatch(release.ExpectedSha256 ?? "") || ParseTag(release.Tag) == null) throw new InvalidDataException("更新信息不完整，请重新检查更新。");
   cancellationToken.ThrowIfCancellationRequested();
   stagingRoot = SafeStagingRoot(stagingRoot);
   Directory.CreateDirectory(stagingRoot); SafeStagingRoot(stagingRoot);
   string directory = Path.Combine(stagingRoot, "update-" + Guid.NewGuid().ToString("N"));
   Directory.CreateDirectory(directory);
   string partial = Path.Combine(directory, InstallerName + ".partial"), final = Path.Combine(directory, InstallerName);
   long downloaded = 0;
   try {
    using (var response = await SendAsync(AssetUri(release.AssetId), token, true, cancellationToken).ConfigureAwait(false)) {
     if (response.Content == null) throw new InvalidDataException("安装包内容为空。");
     if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != release.Size) throw new InvalidDataException("安装包大小与发布记录不一致，已停止下载。");
     using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
     using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
     using (var hash = SHA256.Create()) {
      Report(progress, 0, release.Size, "downloading");
      var clock = Stopwatch.StartNew(); long lastProgress = 0;
      byte[] buffer = new byte[65536]; int count;
      while ((count = await ReadAsync(input, buffer, cancellationToken).ConfigureAwait(false)) != 0) {
       if (downloaded + count > release.Size || downloaded + count > MaxInstallerBytes) throw new InvalidDataException("安装包超过发布记录大小，已停止下载。");
       await output.WriteAsync(buffer, 0, count, cancellationToken).ConfigureAwait(false);
       hash.TransformBlock(buffer, 0, count, buffer, 0); downloaded += count;
       if (downloaded == release.Size || clock.ElapsedMilliseconds - lastProgress >= 100) { Report(progress, downloaded, release.Size, "downloading"); lastProgress = clock.ElapsedMilliseconds; }
      }
      if (downloaded != release.Size) throw new InvalidDataException("安装包没有完整下载，请重试。");
      Report(progress, downloaded, release.Size, "verifying");
      hash.TransformFinalBlock(new byte[0], 0, 0);
      string actual = BitConverter.ToString(hash.Hash).Replace("-", "");
      if (!actual.Equals(release.ExpectedSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装包 SHA256 校验失败，未安装任何内容。请重新下载。");
      await output.FlushAsync(cancellationToken).ConfigureAwait(false); output.Flush(true);
     }
    }
    cancellationToken.ThrowIfCancellationRequested();
    File.Move(partial, final);
    Report(progress, downloaded, release.Size, "ready");
    return final;
   } catch {
    try { if (File.Exists(partial)) File.Delete(partial); } catch { }
    throw;
   }
  }
  static void Report(IProgress<UpdateProgress> progress, long downloaded, long total, string stage) { if (progress != null) progress.Report(new UpdateProgress { Downloaded = downloaded, Total = total, Stage = stage }); }
  public void Dispose() { client.Dispose(); }
 }
}
