using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace Nook.FileConversion;

internal static class SafeOffice
{
    private static readonly HashSet<string> PackagedOffice =
    [
        "docx", "docm", "dotx", "dotm",
        "xlsx", "xlsm", "xltx", "xltm", "xlam", "xlsb",
        "pptx", "pptm", "potx", "potm", "ppsx", "ppsm", "sldx", "sldm",
        "odt", "ods", "odp", "odg", "ott", "ots", "otp"
    ];

    internal static void ValidateInput(string path, string format)
    {
        if (!PackagedOffice.Contains(format)) return;
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > 20000)
            throw new RequestException("文档内部项目过多，未进行转换。");
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)
                && entry.FullName != "content.xml") continue;
            if (entry.Length > 32L * 1024 * 1024)
                throw new RequestException("文档的资源关系描述过大，未进行转换。");
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 32L * 1024 * 1024
            });
            var document = XDocument.Load(reader);
            foreach (var element in document.Descendants())
            {
                if (element.Name.LocalName == "Relationship")
                {
                    var type = (string?)element.Attribute("Type") ?? "";
                    var target = (string?)element.Attribute("Target") ?? "";
                    if (string.Equals((string?)element.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
                    {
                        if (type.EndsWith("/hyperlink", StringComparison.Ordinal)
                            && IsOrdinaryLink(target)) continue;
                        Reject();
                    }
                    if (Uri.TryCreate(target, UriKind.Absolute, out var targetUri)
                        && targetUri.Scheme != "file" // Leading slash is an OOXML package-root path.
                        || target.Contains(':') || target.Contains('\\'))
                        Reject();
                }
                var href = (string?)element.Attribute(XName.Get("href", "http://www.w3.org/1999/xlink"));
                if (href is null || href.Length == 0 || href.StartsWith('#')) continue;
                // These events are never executed by the locked-down LO profile.
                if (element.Name.LocalName == "event-listener") continue;
                if (element.Name.LocalName == "a" && IsOrdinaryLink(href)) continue;
                if (Uri.TryCreate(href, UriKind.Absolute, out _) || href.StartsWith('/')
                    || href.Contains('\\') || href.Split('/').Contains(".."))
                    Reject();
                var resource = Uri.UnescapeDataString(href);
                if (resource.Split('/').Contains("..") || resource.Contains('\\')
                    || resource.Contains(':')) Reject();
                resource = string.Join('/', resource.Split('/').Where(part => part != "."));
                if (!archive.Entries.Any(candidate => candidate.FullName == resource
                        || resource.EndsWith('/') && candidate.FullName.StartsWith(resource, StringComparison.Ordinal)))
                    Reject();
            }
        }
    }

    private static bool IsOrdinaryLink(string value) =>
        value.StartsWith('#') || Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" or "mailto";

    private static void Reject() => throw new RequestException(
        "此文档包含外部图片、链接数据或依赖文件。为避免自动联网或生成缺少内容的结果，请先把资源嵌入文档再转换；原文件保留。");
}
