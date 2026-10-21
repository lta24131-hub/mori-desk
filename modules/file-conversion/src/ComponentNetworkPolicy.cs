using Filee.Engines.Infrastructure;
using System.Net;
using System.Net.Sockets;

namespace Nook.FileConversion;

// Catalog URLs are embedded app-reviewed pins, not user input. A supplier may
// redirect only within its reviewed delivery hosts; DNS is checked before GET.
internal sealed class ComponentNetworkPolicy
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;

    internal ComponentNetworkPolicy(Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null) =>
        _resolve = resolve ?? ((host, token) => Dns.GetHostAddressesAsync(host, token));

    internal static EngineComponent ReviewedDownload(EngineComponent component)
    {
        var original = new Uri(component.Url);
        // The TDF dispatcher chooses arbitrary global mirrors. Keep the same
        // pinned archive on an explicitly reviewed HTTPS mirror instead.
        return component.Id == "libreoffice" && original.Host == "download.documentfoundation.org"
            ? component with { Url = "https://mirror.aarnet.edu.au/pub/tdf" + original.AbsolutePath }
            : component;
    }

    internal static void ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0
            || uri.Fragment.Length != 0 || uri.IsLoopback || uri.Host.EndsWith('.')
            || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out _))
            throw new InvalidDataException("组件下载地址必须是可信供应商的 HTTPS 地址。");
    }

    internal static void ValidateRedirect(Uri initial, Uri redirect)
    {
        ValidateUri(redirect);
        var source = initial.IdnHost.ToLowerInvariant();
        var target = redirect.IdnHost.ToLowerInvariant();
        bool allowed = source == target || (source switch
        {
            "github.com" => target is "release-assets.githubusercontent.com" or "objects.githubusercontent.com",
            "conda.anaconda.org" => target is "anaconda.org" or "binstar-cio-packages-prod.s3.amazonaws.com",
            // These are the reviewed *exact* official mirror/CDN hosts, not
            // suffix checks such as *.edu.au or arbitrary TDF mirror redirects.
            "download.documentfoundation.org" => target == "mirror.aarnet.edu.au",
            _ => false
        });
        if (!allowed) throw new InvalidDataException("组件下载跳转到了未批准的网站，已停止。");
    }

    internal async Task<IPAddress[]> PublicAddressesAsync(string host, CancellationToken token)
    {
        var addresses = await _resolve(host, token);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublic(address)))
            throw new InvalidDataException("组件下载域名解析到了非公网地址，已停止。");
        return addresses.Distinct().ToArray();
    }

    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] is not (0 or 10 or 127) && bytes[0] < 224
                && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                && !(bytes[0] == 169 && bytes[1] == 254)
                && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                && !(bytes[0] == 192 && bytes[1] is 0 or 168)
                && !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2)
                && !(bytes[0] == 198 && bytes[1] is 18 or 19)
                && !(bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
                && !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
        // Accept global unicast only. Reject loopback, unspecified, link-local,
        // ULA, multicast, mapped/transition and other non-global address ranges.
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xE0) == 0x20
            && !(bytes[0] == 0x20 && bytes[1] == 0x02)
            && !(bytes[0] == 0x20 && bytes[1] == 0x01 && (bytes[2] < 2
                || bytes[2] == 0x0d && bytes[3] == 0xb8));
    }

    internal HttpMessageHandler CreateTransport() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(30),
        ConnectCallback = ConnectAsync
        // UseProxy intentionally remains true: an explicitly configured system
        // proxy is trusted by the user. Target DNS checks cannot constrain what
        // that proxy resolves remotely; do not claim absolute SSRF prevention.
    };

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var endpoint = context.DnsEndPoint;
        bool direct = string.Equals(endpoint.Host, context.InitialRequestMessage.RequestUri?.IdnHost,
            StringComparison.OrdinalIgnoreCase);
        var addresses = direct ? await PublicAddressesAsync(endpoint.Host, token)
            : await Dns.GetHostAddressesAsync(endpoint.Host, token); // User-trusted proxy, possibly loopback.
        Exception? failure = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                // Connect to the checked IP, not the hostname: no second DNS
                // lookup between validation and direct connection. TLS still
                // authenticates the original HTTPS hostname in SocketsHttpHandler.
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception error) when (error is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                if (error is OperationCanceledException) throw;
                failure = error;
            }
        }
        throw new IOException("无法连接组件下载服务器。", failure);
    }
}
