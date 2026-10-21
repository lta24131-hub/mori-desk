using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Markdig;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Nook.FileConversion;

/// <summary>
/// Conversion is not a browser session. Parse only, never resolve a URI, and reject
/// active or dependency-bearing markup instead of publishing a broken document.
/// </summary>
internal static class SafeMarkup
{
    private const long MaximumMarkupBytes = 32L * 1024 * 1024;
    private const string Policy = "default-src 'none'; script-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:; object-src 'none'; base-uri 'none'; form-action 'none';";
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
    private static readonly HashSet<string> ActiveElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "iframe", "frame", "frameset", "object", "embed", "applet",
        "base", "form", "input", "button", "textarea", "select", "portal",
        "foreignObject", "animate", "animateMotion", "animateTransform", "set"
    };
    private static readonly HashSet<string> ResourceAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "src", "srcset", "poster", "background", "data", "codebase", "archive",
        "action", "formaction", "ping"
    };

    internal static void ValidateInput(string path, string format)
    {
        if (format is "svg" or "svgz")
        {
            ValidateSvg(path, format == "svgz");
            return;
        }
        if (format is not ("md" or "html")) return;
        var text = Read(path);
        var html = format == "md" ? Markdown.ToHtml(text, Pipeline) : text;
        ValidateHtml(html);
    }

    internal static void ValidateAndProtectHtmlOutput(string path)
    {
        var document = ValidateHtml(Read(path));
        var meta = document.CreateElement("meta");
        meta.SetAttribute("http-equiv", "Content-Security-Policy");
        meta.SetAttribute("content", Policy);
        document.Head!.Prepend(meta);
        File.WriteAllText(path, "<!DOCTYPE html>\n" + document.DocumentElement.OuterHtml, new UTF8Encoding(false));
    }

    private static IDocument ValidateHtml(string html)
    {
        // HtmlParser alone has no loader or JS engine. In particular it never fetches
        // malicious src URLs while inspecting the user's content.
        var document = new HtmlParser().ParseDocument(html);
        foreach (var element in document.All)
        {
            if (ActiveElements.Contains(element.LocalName))
                Reject("文档包含脚本或交互式内容");
            if (element.LocalName.Equals("meta", StringComparison.OrdinalIgnoreCase)
                && element.GetAttribute("http-equiv") is { } directive
                && !directive.Equals("content-security-policy", StringComparison.OrdinalIgnoreCase)
                && !directive.Equals("content-type", StringComparison.OrdinalIgnoreCase))
                Reject("文档包含自动跳转或响应指令");
            if (element.LocalName.Equals("link", StringComparison.OrdinalIgnoreCase))
                Reject("文档依赖外部样式或资源");
            if (element.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase))
                ValidateCss(element.TextContent);
            foreach (var attribute in element.Attributes)
            {
                var name = attribute.Name;
                var value = attribute.Value.Trim();
                if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("srcdoc", StringComparison.OrdinalIgnoreCase))
                    Reject("文档包含可执行事件");
                if (name.Equals("style", StringComparison.OrdinalIgnoreCase)
                    || name is "fill" or "stroke" or "filter" or "mask" or "clip-path"
                        or "marker-start" or "marker-mid" or "marker-end" or "cursor")
                    ValidateCss(value);
                if (ResourceAttributes.Contains(name) && value.Length > 0 && !IsEmbeddedImage(value))
                    Reject("文档依赖未嵌入的图片或文件");
                if (name.Equals("href", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("xlink:href", StringComparison.OrdinalIgnoreCase))
                {
                    if (value.StartsWith('#') || value.Length == 0) continue;
                    // User-initiated regular hyperlinks are retained; automatic resource
                    // loads (SVG <image>, <use>, etc.) must be embedded.
                    if (element.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
                            && uri.Scheme is "http" or "https" or "mailto") continue;
                        Reject("文档含本地文件链接或不安全链接");
                    }
                    if (!IsEmbeddedImage(value)) Reject("文档依赖外部资源");
                }
            }
        }
        return document;
    }

    private static void ValidateSvg(string path, bool compressed)
    {
        using var source = File.OpenRead(path);
        using var decompressor = compressed ? new GZipStream(source, CompressionMode.Decompress, leaveOpen: true) : null;
        Stream input = decompressor is null ? source : decompressor;
        // Bound decompression before parsing; a tiny gzip must not expand without limit.
        using var bounded = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (bounded.Length + count > MaximumMarkupBytes)
                throw new RequestException("SVG 内容超过 32 MB，请简化后转换；原文件没有改动。");
            bounded.Write(buffer, 0, count);
        }
        bounded.Position = 0;
        using var reader = XmlReader.Create(bounded, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumMarkupBytes
        });
        var document = XDocument.Load(reader);
        if (document.Root?.Name.LocalName != "svg")
            Reject("文件不包含有效 SVG 根元素");
        foreach (var element in document.Root!.DescendantsAndSelf())
        {
            if (ActiveElements.Contains(element.Name.LocalName))
                Reject("SVG 包含脚本或活动内容");
            if (element.Name.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase))
                ValidateCss(element.Value);
            foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
            {
                var name = attribute.Name.LocalName;
                var value = attribute.Value.Trim();
                if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("base", StringComparison.OrdinalIgnoreCase))
                    Reject("SVG 包含可执行事件或外部基址");
                if (name.Equals("style", StringComparison.OrdinalIgnoreCase)
                    || name is "fill" or "stroke" or "filter" or "mask" or "clip-path"
                        or "marker-start" or "marker-mid" or "marker-end" or "cursor")
                    ValidateCss(value);
                if (ResourceAttributes.Contains(name) && value.Length > 0 && !IsEmbeddedImage(value))
                    Reject("SVG 依赖未嵌入的图片或文件");
                if (!name.Equals("href", StringComparison.OrdinalIgnoreCase)) continue;
                if (value.Length == 0 || value.StartsWith('#') || IsEmbeddedImage(value)) continue;
                if (element.Name.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase)
                    && Uri.TryCreate(value, UriKind.Absolute, out var uri)
                    && uri.Scheme is "https" or "http" or "mailto") continue;
                Reject("SVG 依赖未嵌入的图片或文件");
            }
        }
    }

    private static bool IsEmbeddedImage(string value) =>
        Regex.IsMatch(value, @"^data:image/(?:png|jpeg|gif|webp|avif|bmp);base64,[a-z0-9+/=\r\n]+$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static void ValidateCss(string css)
    {
        css = Regex.Replace(css, """url\s*\(\s*(['"]?)#[a-z0-9_-]+\1\s*\)""", "",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        // CSS escapes/comments can hide url()/imports, so reject those constructs too.
        // The converters' own presentational CSS doesn't require any of them.
        if (css.Contains('\\') || css.Contains("/*", StringComparison.Ordinal)
            || Regex.IsMatch(css, @"url\s*\(|@import|expression\s*\(|behavior\s*:|-moz-binding",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            Reject("文档样式依赖外部资源或活动内容");
    }

    private static string Read(string path)
    {
        if (new FileInfo(path).Length > MaximumMarkupBytes)
            throw new RequestException("HTML/Markdown 超过 32 MB，请拆分后转换；原文件没有改动。");
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static void Reject(string reason) =>
        throw new RequestException(reason + "，快捷转换不会加载这些内容或保存缺少资源的结果。请先导出为自包含文档；原文件保留。");
}
