using Filee.Engines.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;

namespace Nook.FileConversion;

internal sealed class ComponentPackageInstaller : IComponentPackageInstaller
{
    private readonly Func<HttpMessageHandler>? _testTransport;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>>? _testResolver;

    internal ComponentPackageInstaller() { }

    // Test-only in-process transport. No production protocol field can select it.
    internal ComponentPackageInstaller(Func<HttpMessageHandler> transport,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null)
    {
        _testTransport = transport;
        _testResolver = resolver ?? ((_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
    }

    public async Task RestoreAsync(ComponentPackageDefinition package, string staging,
        IProgress<ComponentOperationProgress>? progress, CancellationToken token)
    {
        ComponentPaths.NoLinks(staging);
        if (!Directory.Exists(staging)) throw new DirectoryNotFoundException("组件临时目录不存在。");
        var network = new ComponentNetworkPolicy(_testResolver);
        using var handler = new ComponentDownloadHandler(package.Components,
            _testTransport?.Invoke() ?? network.CreateTransport(), network);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Nook-ComponentManager/1.0");
        if (package.Id == "7zip")
        {
            await RestoreSevenZipAsync(package, staging, http, progress, token);
            return;
        }
        var components = package.Components.ToDictionary(c => c.Id);
        var enginePackage = new EnginePackage(package.Id, package.Components.Select(c => c.Id).ToArray(),
            package.InstalledBytes, [package.Id]);
        // Reviewed upstream paths: zip uses BCL traversal protection; conda skips
        // links and contains every extracted path; signed/pinned MSI uses /a /qn
        // files-only extraction, never normal system installation or scripts.
        var installer = new EngineInstaller(http, staging, components);
        var adapter = new InlineProgress<EngineInstallProgress>(value =>
            progress?.Report(new(package.Id,
                value.Stage == EngineInstallStage.Downloading ? "downloading" : "unpacking",
                value.BytesDone, value.BytesTotal,
                value.Stage == EngineInstallStage.Downloading ? "正在下载已校验版本的组件。" : "正在解包组件，不会安装到系统。")));
        await installer.InstallAsync(enginePackage, adapter, token);
        if (!installer.IsUpToDate(enginePackage)) throw new InvalidDataException("组件版本校验未通过。");
        ComponentPaths.InspectTree(staging);
        var downloads = ComponentPaths.Child(staging, ".downloads");
        if (Directory.Exists(downloads)) ComponentPaths.DeleteTree(staging, downloads);
    }

    private static async Task RestoreSevenZipAsync(ComponentPackageDefinition package, string staging,
        HttpClient http, IProgress<ComponentOperationProgress>? progress, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("压缩组件目前支持 Windows。");
        var component = package.Components.Single();
        var download = ComponentPaths.Child(staging, "7zip.msi");
        var uri = new Uri(component.Url);
        HttpResponseMessage response;
        for (var redirects = 0; ; redirects++)
        {
            response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location && redirects < 10)
            {
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                response.Dispose();
                continue;
            }
            break;
        }
        using (response)
        {
            response.EnsureSuccessStatusCode();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = new FileStream(download, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[65536];
            long count = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, token)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                hash.AppendData(buffer, 0, read);
                count += read;
                progress?.Report(new(package.Id, "downloading", count, component.Size, "正在下载压缩组件。"));
            }
            if (count != component.Size || Convert.ToHexString(hash.GetHashAndReset()) != component.Sha256.ToUpperInvariant())
                throw new InvalidDataException("压缩组件下载校验不符，未运行解包。");
        }
        token.ThrowIfCancellationRequested();
        var extracted = ComponentPaths.Child(staging, "msi-files");
        Directory.CreateDirectory(extracted);
        var log = ComponentPaths.Child(staging, "msi-extract.log");
        var program = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
        progress?.Report(new(package.Id, "unpacking", component.Size, component.Size, "正在解包压缩组件。"));
        using var process = Process.Start(new ProcessStartInfo(program,
            $"/a \"{download}\" /qn /l*v \"{log}\" TARGETDIR=\"{extracted}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        }) ?? throw new IOException("无法启动组件解包。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        if (process.ExitCode != 0) throw new IOException("压缩组件解包失败：" + process.ExitCode);
        ComponentPaths.InspectTree(extracted);
        var binaries = Directory.GetFiles(extracted, "7z.exe", SearchOption.AllDirectories);
        if (binaries.Length != 1 || !File.Exists(Path.Combine(Path.GetDirectoryName(binaries[0])!, "7z.dll")))
            throw new InvalidDataException("压缩组件缺少必要程序文件。");
        var destination = ComponentPaths.Child(staging, "7zip");
        Directory.Move(Path.GetDirectoryName(binaries[0])!, destination);
        File.WriteAllText(Path.Combine(destination, ".filee-component"), component.Sha256);
        ComponentPaths.DeleteTree(staging, extracted);
        File.Delete(download);
        File.Delete(log);
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}

/// <summary>Restricts traffic to pinned HTTPS URLs and reviewed supplier/CDN
/// redirects, and bounds bytes even for chunked responses before disk fills.</summary>
internal sealed class ComponentDownloadHandler : DelegatingHandler
{
    private readonly Dictionary<string, EngineComponent> _approved;
    private readonly ComponentNetworkPolicy _network;

    internal ComponentDownloadHandler(IReadOnlyList<EngineComponent> components, HttpMessageHandler inner,
        ComponentNetworkPolicy? network = null) : base(inner)
    {
        _approved = components.ToDictionary(c => new Uri(c.Url).AbsoluteUri, StringComparer.Ordinal);
        _network = network ?? new ComponentNetworkPolicy();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var url = request.RequestUri ?? throw new InvalidDataException("组件下载地址缺失。");
        ComponentNetworkPolicy.ValidateUri(url);
        if (request.Method != HttpMethod.Get || !_approved.TryGetValue(url.AbsoluteUri, out var component))
            throw new InvalidDataException("组件下载地址不在已批准清单中。");
        await _network.PublicAddressesAsync(url.IdnHost, token);
        var response = await base.SendAsync(request, token);
        try
        {
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                var redirect = location.IsAbsoluteUri ? location : new Uri(url, location);
                ComponentNetworkPolicy.ValidateRedirect(new Uri(component.Url), redirect);
                await _network.PublicAddressesAsync(redirect.IdnHost, token);
                _approved[redirect.AbsoluteUri] = component;
                return response;
            }
            if (response.IsSuccessStatusCode)
            {
                if (response.Content.Headers.ContentLength is { } size && size != component.Size)
                    throw new InvalidDataException("组件下载大小与批准版本不符。");
                var original = response.Content;
                var stream = await original.ReadAsStreamAsync(token);
                response.Content = new StreamContent(new ComponentBoundedReadStream(stream, original, component.Size));
            }
            return response;
        }
        catch { response.Dispose(); throw; }
    }

}

internal sealed class ComponentBoundedReadStream(Stream stream, IDisposable owner, long expected) : Stream
{
    private long _received;
    private int Count(int count)
    {
        _received = checked(_received + count);
        if (_received > expected || count == 0 && _received != expected)
            throw new InvalidDataException("组件下载长度不符，已停止。");
        return count;
    }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => expected;
    public override long Position { get => _received; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => count == 0 ? 0 : Count(stream.Read(buffer, offset, count));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
        buffer.Length == 0 ? 0 : Count(await stream.ReadAsync(buffer, token));
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
        count == 0 ? 0 : Count(await stream.ReadAsync(buffer.AsMemory(offset, count), token));
    protected override void Dispose(bool disposing) { if (disposing) owner.Dispose(); base.Dispose(disposing); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
