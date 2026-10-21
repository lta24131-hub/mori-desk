using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines;
using Filee.Engines.Infrastructure;
using ImageMagick;
using ImageMagick.Configuration;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Nook.FileConversion;

internal sealed class ConversionService : IDisposable
{
    private static readonly FileFormat[] Targets = FormatRegistry.Known
        .Where(f => f.Writable && f.Id != FormatRegistry.Folder).ToArray();
    private static readonly HashSet<string> AnimationFormats = ["gif", "webp", "apng", "mng"];
    private static readonly Dictionary<string, string> MagickNames = new()
    {
        ["png"] = "Png", ["jpg"] = "Jpeg", ["webp"] = "WebP", ["bmp"] = "Bmp",
        ["tiff"] = "Tiff", ["gif"] = "Gif", ["apng"] = "Png", ["ico"] = "Icon",
        ["psd"] = "Psd", ["psb"] = "Psb", ["heic"] = "Heic", ["avif"] = "Avif",
        ["jxl"] = "Jxl", ["jp2"] = "Jp2", ["tga"] = "Tga", ["ppm"] = "Ppm",
        ["xcf"] = "Xcf", ["raw"] = "Dng", ["mng"] = "Mng", ["qoi"] = "Qoi",
        ["dds"] = "Dds", ["pcx"] = "Pcx", ["cur"] = "Cur", ["xpm"] = "Xpm",
        ["xbm"] = "Xbm", ["wbmp"] = "Wbmp", ["pam"] = "Pam", ["pfm"] = "Pfm",
        ["sgi"] = "Sgi", ["ras"] = "Sun", ["miff"] = "Miff", ["dcm"] = "Dcm",
        ["pcd"] = "Pcd", ["exr"] = "Exr", ["hdr"] = "Hdr", ["emf"] = "Emf", ["wmf"] = "Wmf"
    };
    private readonly IConverter[] _converters;
    private readonly Dictionary<string, bool> _available;
    private readonly RoutePlanner _planner;
    private readonly string _work;
    private readonly string _sessionWork;
    private int _stepNumber;
    internal string[] MissingEngines { get; private set; } = [];
    internal string[] MissingComponentIds { get; private set; } = [];

    internal ConversionService(string session, string id, IReadOnlyDictionary<string, string>? ownCopies = null)
    {
        _sessionWork = Path.Combine(session, "work-" + id);
        if (Directory.Exists(_sessionWork) || File.Exists(_sessionWork))
            throw new RequestException("该任务已运行，请使用新的任务目录和编号。");
        Directory.CreateDirectory(_sessionWork);
        // ImageMagick and several native codecs still use MAX_PATH even when managed
        // I/O supports longer paths. Keep their workspace short; publication is managed.
        _work = Path.Combine(Path.GetTempPath(), "Nook-ConversionNative", id);
        Protocol.LocalPath(_work);
        if (_work.Length > 150 || Directory.Exists(_work) || File.Exists(_work))
            throw new RequestException("无法创建短路径转换工作目录，请重试。");
        Directory.CreateDirectory(_work);
        File.WriteAllText(Path.Combine(_work, "owner-session.txt"), Path.GetFullPath(session));
        var temp = Path.Combine(_work, "scratch");
        Directory.CreateDirectory(temp);
        // Never launch the Filee app or consult its global settings, downloads, hooks,
        // PATH or installed programs. Only explicitly verified managed components
        // and the app's own packaged engines/ may be discovered.
        EngineEnvironment.SearchSystem = false;
        EngineEnvironment.OwnCopies = ownCopies is null ? new Dictionary<string, string>()
            : new Dictionary<string, string>(ownCopies, StringComparer.Ordinal);
        EngineEnvironment.DownloadRoot = Path.Combine(_work, "disabled-engines");
        Environment.SetEnvironmentVariable("FILEE_DATA_DIR", Path.Combine(_work, "filee-state"));
        Environment.SetEnvironmentVariable("CALIBRE_CONFIG_DIRECTORY", Path.Combine(_work, "calibre-state"));
        Environment.SetEnvironmentVariable("CALIBRE_TEMP_DIR", temp);
        PrepareOfficeProfiles(Path.Combine(_work, "engine-state"));
        MagickNET.SetTempDirectory(temp);
        var config = ConfigurationFiles.Default;
        config.Policy.Data = """
            <?xml version="1.0" encoding="UTF-8"?>
            <policymap>
              <policy domain="delegate" rights="none" pattern="*" />
              <policy domain="filter" rights="none" pattern="*" />
              <policy domain="path" rights="none" pattern="@*" />
              <policy domain="coder" rights="none" pattern="*" />
              <!-- Filee's APNG compositor creates transparent in-memory canvases. XC
                   generates flat pixels only; delegates/filters and other coders stay off. -->
              <policy domain="coder" rights="read" pattern="XC" />
              <policy domain="coder" rights="read|write" pattern="{PNG,PNG8,PNG24,PNG32,PNG48,PNG64,JPEG,JPG,WEBP,BMP,BMP2,BMP3,TIFF,GIF,ICO,ICON,PSD,PSB,HEIC,AVIF,JXL,JP2,J2K,TGA,PPM,PGM,PBM,PNM,PAM,PFM,APNG,MNG,QOI,DDS,PCX,CUR,XPM,XBM,WBMP,SGI,SUN,RAS,MIFF,DCM,PCD,EXR,HDR,XCF,DNG,EMF,WMF}" />
              <policy domain="resource" name="memory" value="512MiB" />
              <policy domain="resource" name="map" value="1GiB" />
              <policy domain="resource" name="disk" value="2GiB" />
              <policy domain="resource" name="thread" value="2" />
              <policy domain="resource" name="time" value="180" />
              <policy domain="resource" name="width" value="64KP" />
              <policy domain="resource" name="height" value="64KP" />
              <policy domain="resource" name="list-length" value="2000" />
            </policymap>
            """;
        config.Delegates.Data = "<delegatemap />";
        var configuration = Path.Combine(_work, "configuration");
        Directory.CreateDirectory(configuration);
        MagickNET.Initialize(config, configuration);
        _converters = EngineRegistry.CreateAll(new EngineEnvironment(Path.Combine(_work, "engine-state"))).ToArray();
        _available = _converters.ToDictionary(c => c.Id, c =>
        {
            try { return c.GetStatus().IsAvailable; }
            catch { return false; }
        });
        var skia = NativeAvailable("libSkiaSharp.dll");
        _available["pdfium"] &= NativeAvailable("pdfium.dll") && skia;
        _available["pdf-slides"] &= _available["pdfium"];
        foreach (var idWithSkia in new[] { "vector", "cad", "model3d", "pdf-slides" })
            if (_available.ContainsKey(idWithSkia)) _available[idWithSkia] &= skia;
        // Keep upstream DirectOnly/ImageSourcesOnly/NotForImageSources semantics.
        _planner = new RoutePlanner(_converters.Where(c => _available[c.Id]).Select(c =>
            new FilteredConverter(c, c.Edges.Where(e => SafeEdge(c, e)).ToArray())));
    }

    internal FileInfoResult[] Capabilities(string[] inputs)
    {
        var missing = new HashSet<string>();
        var missingComponents = new HashSet<string>(StringComparer.Ordinal);
        var files = inputs.Select(input =>
        {
            var format = FormatRegistry.Detect(input)?.Id ?? "";
            var needsGs = format == "ai" && !IsPdfCompatibleAi(input) && !_available.GetValueOrDefault("ghostscript");
            if (needsGs)
            {
                missing.Add("Ghostscript：此 AI 文件为 PostScript，需要安装转换组件。");
                missingComponents.Add("ghostscript");
            }
            var targets = needsGs ? [] : Targets.Where(t => t.Id != format && FindRoute(format, t.Id) is not null)
                .Select(t => t.Id).ToArray();
            foreach (var converter in _converters.Where(c => !_available[c.Id]))
                if (converter.Edges.Any(e => e.From == format && SafeEdge(converter, e)))
                {
                    missing.Add(converter.DisplayName + "：尚未安装或组件不完整，相关转换暂不可用。");
                    if (ComponentForConverter(converter.Id) is { } componentId)
                        missingComponents.Add(componentId);
                }
            var warnings = Warnings(format, "").ToList();
            if (targets.Length == 0) warnings.Add("没有当前可用的转换路线；需要的组件安装完成后会自动解锁。");
            if (FormatRegistry.FindById(format)?.Category == FormatCategory.Video)
                warnings.Add("视频转单张截图、按帧导出需要专用选项；快捷转换不会默认只保存第一帧。");
            if (format is "glb" or "gltf" or "stl" or "obj" or "3mf" or "ply")
                warnings.Add("OBJ 输出需要材质和贴图依赖包，目前暂不提供；可选自包含 GLB/glTF。");
            return new FileInfoResult(input, format, targets, [], warnings.ToArray());
        }).ToArray();
        MissingEngines = missing.Order(StringComparer.Ordinal).ToArray();
        MissingComponentIds = missingComponents.Order(StringComparer.Ordinal).ToArray();
        return files;
    }

    private static string? ComponentForConverter(string id) => id switch
    {
        "archive" => "7zip",
        "rhwp" or "ffmpeg" or "libreoffice" or "pandoc" or "ghostscript" or "calibre" => id,
        _ => null // A damaged built-in codec requires core repair, not an unrelated download.
    };

    internal TargetInfo[] CommonTargets(FileInfoResult[] files) => Targets
        .Where(target => files.All(file => file.Targets.Contains(target.Id) || file.Format == target.Id)
            && files.Any(file => file.Format != target.Id))
        .Select(target => new TargetInfo(target.Id, target.DisplayName, target.PrimaryExtension,
            string.Join("；", files.Where(f => f.Format != target.Id).SelectMany(file => Warnings(file.Format, target.Id)).Distinct())))
        .ToArray();

    private Route? FindRoute(string source, string target)
    {
        // A quick "convert video" must not unexpectedly render an entire film into thousands
        // of still pictures via a GIF intermediate. Frame export needs its own explicit UI.
        if (FormatRegistry.FindById(source)?.Category == FormatCategory.Video
            && FormatRegistry.FindById(target)?.Category is not (FormatCategory.Video or FormatCategory.Audio or FormatCategory.Subtitle)
            && target is not ("gif" or "apng")) return null;
        return _planner.Plan(source, target);
    }

    private static bool SafeEdge(IConverter converter, ConversionEdge edge)
    {
        if (edge.To is FormatRegistry.Folder or "obj" || edge.From == FormatRegistry.Folder || edge.FrameSequence) return false;
        // Output bundles need host/library support before publication. Do not advertise
        // a "successful" HTML/Markdown/OBJ while silently dropping its resource files.
        if (edge.To is "html" or "md" && converter.Id is "ebook" or "pandoc" or "calibre" or "unhwp" or "libreoffice") return false;
        // Ordinary video -> still and LO Draw -> picture keep only the first frame/page.
        if (converter.Id == "ffmpeg" && FormatRegistry.FindById(edge.From)?.Category == FormatCategory.Video
            && FormatRegistry.FindById(edge.To)?.Category == FormatCategory.Image && edge.To is not ("gif" or "apng")) return false;
        if (converter.Id == "libreoffice" && edge.From != "svg"
            && (FormatRegistry.FindById(edge.To)?.Category == FormatCategory.Image || edge.To is "svg" or "emf" or "wmf"))
            return false;
        if (converter.Id is "magick" or "pdfsharp")
            if (MagickNames.ContainsKey(edge.From) && !MagickSupports(edge.From, read: true)) return false;
        if (MagickNames.ContainsKey(edge.To) && converter.Id is ("magick" or "pdfium" or "vector" or "cad" or "model3d")
            && !MagickSupports(edge.To, read: false)) return false;
        return true;
    }

    private static bool MagickSupports(string id, bool read)
    {
        if (id == "apng") id = "png"; // Filee's own APNG container; PNG pixels.
        return MagickNames.TryGetValue(id, out var name) && Enum.TryParse<MagickFormat>(name, true, out var format)
            && MagickNET.SupportedFormats.Any(info => info.Format == format && (read ? info.SupportsReading : info.SupportsWriting));
    }

    private static bool NativeAvailable(string name)
    {
        if (!NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, name), out var handle)) return false;
        NativeLibrary.Free(handle);
        return true;
    }

    private static void PrepareOfficeProfiles(string engineState)
    {
        // LibreOffice's dedicated per-task profiles never inherit a user's trusted
        // folders or macro policy. Explicitly disable every scripting runtime and
        // active embedded content before its headless process is allowed to start.
        const string settings = """
            <?xml version="1.0" encoding="UTF-8"?>
            <oor:items xmlns:oor="http://openoffice.org/2001/registry">
              <item oor:path="/org.openoffice.Office.Common/Security/Scripting">
                <prop oor:name="MacroSecurityLevel" oor:op="fuse"><value>3</value></prop>
                <prop oor:name="DisableMacrosExecution" oor:op="fuse"><value>true</value></prop>
                <prop oor:name="DisableActiveContent" oor:op="fuse"><value>true</value></prop>
                <prop oor:name="DisablePythonRuntime" oor:op="fuse"><value>true</value></prop>
                <prop oor:name="DisableOLEAutomation" oor:op="fuse"><value>true</value></prop>
                <prop oor:name="BlockUntrustedRefererLinks" oor:op="fuse"><value>true</value></prop>
              </item>
              <item oor:path="/org.openoffice.Office.Writer/Content/Update">
                <prop oor:name="Link" oor:op="fuse"><value>0</value></prop>
                <prop oor:name="Field" oor:op="fuse"><value>false</value></prop>
                <prop oor:name="Chart" oor:op="fuse"><value>false</value></prop>
              </item>
              <item oor:path="/org.openoffice.Office.Calc/Content/Update">
                <prop oor:name="Link" oor:op="fuse"><value>0</value></prop>
              </item>
            </oor:items>
            """;
        for (var worker = 0; worker < 2; worker++)
        {
            var profile = Path.Combine(engineState, "libreoffice-profiles", "worker-" + worker, "user");
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, "registrymodifications.xcu"), settings);
        }
    }

    private static bool IsPdfCompatibleAi(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[1024];
        return header[..stream.Read(header)].IndexOf("%PDF-"u8) >= 0;
    }

    internal async Task<string[]> ConvertAsync(Request request, CancellationToken token)
    {
        var input = request.Inputs[0];
        var from = FormatRegistry.Detect(input)?.Id ?? "";
        if (from == request.Target) throw new RequestException("请选择不同于原文件的目标格式。");
        if (!Targets.Any(t => t.Id == request.Target)
            || from == "ai" && !IsPdfCompatibleAi(input) && !_available.GetValueOrDefault("ghostscript"))
            throw new RequestException("此转换需要尚未安装的组件，或目标格式暂不支持。");
        var route = FindRoute(from, request.Target)
            ?? throw new RequestException("当前没有可用的转换路线，请安装所需组件后重试。");
        using var sourceLock = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read);
        SafeMarkup.ValidateInput(input, from);
        SafeOffice.ValidateInput(input, from);
        var stage = Path.Combine(_work, "outputs");
        Directory.CreateDirectory(stage);
        Directory.CreateDirectory(Path.Combine(_sessionWork, "outputs")); // Cooperative-cancel test/diagnostic marker.
        var preset = new Preset { TargetFormat = request.Target };
        preset.Image.Quality = request.Quality;
        preset.Pdf.CompressWithGhostscript = false;
        preset.Pdf.RenderDpi = 150;
        var engineInput = input;
        // These formats are self-contained. Never relocate dependency-bearing OBJ,
        // glTF, Markdown, HTML, SVG or other markup without an explicit bundle contract.
        if (MagickNames.ContainsKey(from) || from is "pdf" or "ai" or "ps" or "eps")
        {
            var inputDirectory = Path.Combine(_work, "input");
            Directory.CreateDirectory(inputDirectory);
            var name = Regex.Replace(FormatRegistry.NameWithoutExtension(input), "[^\\p{L}\\p{N} _-]", "_");
            if (name.Length > 70) name = name[..70];
            if (name.Length == 0) name = "source";
            engineInput = Path.Combine(inputDirectory, name + "." + FormatRegistry.ExtensionOf(input));
            using var copy = new FileStream(engineInput, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await sourceLock.CopyToAsync(copy, token);
        }
        var current = new List<string> { engineInput };
        foreach (var hop in route.Steps)
        {
            var next = new List<string>();
            foreach (var path in current)
            {
                token.ThrowIfCancellationRequested();
                next.AddRange(await ExecuteHop(hop, path, preset, stage, token));
                if (next.Count > 1000) throw new RequestException("结果超过 1000 个文件，请分批转换。");
            }
            current = next;
        }
        if (current.Count == 0) throw new InvalidOperationException("转换组件没有生成结果。");
        return Publish(current, request.OutputDirectory, token);
    }

    private async Task<List<string>> ExecuteHop(RouteStep hop, string input, Preset preset, string stage, CancellationToken token)
    {
        var animated = AnimationFormats.Contains(hop.From) || hop.From == "png" && IsAnimatedPng(input);
        if (hop.From == "png" && animated && hop.Converter.Id == "ffmpeg"
            && FormatRegistry.FindById(hop.To)?.Category == FormatCategory.Video)
            return await RunOne(hop.Converter, input, "apng", hop.To, preset, stage, token);
        // Upstream defaults to frame 0 for still exports. Multi-page TIFF normalization
        // keeps every coalesced frame before still/PDF conversion.
        if (animated && !AnimationFormats.Contains(hop.To)
            && FormatRegistry.FindById(hop.To)?.Category != FormatCategory.Video && hop.To != "tiff")
        {
            var magick = _converters.Single(c => c.Id == "magick");
            var normalized = await RunOne(magick, input, hop.From, "tiff", preset, stage, token);
            if (hop.Converter.Id is "magick" or "pdfsharp")
                return await RunOne(hop.Converter, normalized[0], "tiff", hop.To, preset, stage, token);
            return await ConvertEachFrame(normalized[0], hop.To, preset, stage, token);
        }
        if (hop.From == "tiff" && hop.Converter.Id is not ("magick" or "pdfsharp"))
            return await ConvertEachFrame(input, hop.To, preset, stage, token);
        return await RunOne(hop.Converter, input, hop.From, hop.To, preset, stage, token);
    }

    private async Task<List<string>> ConvertEachFrame(string tiff, string target, Preset preset, string stage, CancellationToken token)
    {
        var frames = await RunOne(_converters.Single(c => c.Id == "magick"), tiff, "tiff", "png", preset, stage, token);
        var alternate = _planner.Plan("png", target)
            ?? throw new RequestException("此目标无法完整保留多页图片或动画的每一帧。");
        var all = new List<string>();
        foreach (var frame in frames)
        {
            var paths = new List<string> { frame };
            foreach (var step in alternate.Steps)
            {
                var next = new List<string>();
                foreach (var path in paths) next.AddRange(await ExecuteHop(step, path, preset, stage, token));
                paths = next;
            }
            all.AddRange(paths);
            if (all.Count > 1000) throw new RequestException("结果超过 1000 个文件，请分批转换。");
        }
        return all;
    }

    private async Task<List<string>> RunOne(IConverter converter, string input, string from, string to,
        Preset preset, string stage, CancellationToken token)
    {
        var root = Path.Combine(stage, "part-" + (++_stepNumber).ToString("D4"));
        Directory.CreateDirectory(root);
        var scratch = Path.Combine(_work, "scratch", "step-" + _stepNumber);
        Directory.CreateDirectory(scratch);
        var allocator = new SafeOutputAllocator(root, FormatRegistry.NameWithoutExtension(input));
        SafeMarkup.ValidateInput(input, from);
        SafeOffice.ValidateInput(input, from);
        var results = await converter.ConvertAsync(new ConversionStep(input, from, to, preset, allocator, scratch), null, token);
        token.ThrowIfCancellationRequested();
        var files = VerifiedTree(root);
        if (results.Count == 0 || results.Any(path => !Protocol.IsInside(path, root) || !File.Exists(path)))
            throw new InvalidOperationException("转换组件没有生成完整的结果文件。");
        var primary = results.Where(path => FormatRegistry.Detect(path)?.Id == to).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (primary.Length == 0 || primary.Any(p => !allocator.Allocated.Contains(p) || new FileInfo(p).Length == 0))
            throw new InvalidOperationException("结果文件格式与所选格式不一致，未发布任何文件。");
        if (files.Except(primary, StringComparer.OrdinalIgnoreCase).Any())
            throw new RequestException("此转换生成附属资源文件，当前素材库尚不支持完整依赖包；未保存不完整的结果，原文件保留。");
        // LibreOffice stages actual export files in scratch and only moves its first
        // result to the allocator. Do not silently accept an incomplete HTML/bundle.
        if (converter.Id == "libreoffice" && Directory.EnumerateDirectories(scratch, "lo-*", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileName(path).StartsWith("lo-page-", StringComparison.Ordinal))
            .Any(path => VerifiedTree(path).Length != 0))
            throw new RequestException("此文档转换生成多个附属文件，尚不能完整保存，原文件保留。");
        if (to == "html")
            foreach (var path in primary) SafeMarkup.ValidateAndProtectHtmlOutput(path);
        return primary.ToList();
    }

    private static string[] VerifiedTree(string root)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        long bytes = 0;
        var directories = 0;
        while (pending.TryPop(out var folder))
        {
            if (++directories > 1000) throw new RequestException("结果目录过多，未发布结果。");
            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                Protocol.LocalPath(path);
                if (!Protocol.IsInside(path, root)) throw new InvalidOperationException("输出路径越界。");
                if (Directory.Exists(path)) pending.Push(path);
                else
                {
                    files.Add(path);
                    bytes += new FileInfo(path).Length;
                    if (files.Count > 1000 || bytes > 16L * 1024 * 1024 * 1024)
                        throw new RequestException("结果超过安全限制（1000 个文件或 16 GB），未发布结果。");
                }
            }
        }
        return files.ToArray();
    }

    private static string[] Publish(List<string> results, string output, CancellationToken token)
    {
        Protocol.LocalPath(output);
        if (File.Exists(output) || Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new RequestException("输出目录已被使用，请重新选择一个空目录。");
        var parent = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(parent);
        var transaction = Path.Combine(parent, ".nook-conversion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(transaction);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outputs = new List<string>();
        foreach (var file in results)
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            var extension = FormatRegistry.ExtensionOf(file);
            var stem = FormatRegistry.NameWithoutExtension(file);
            for (var n = 2; !names.Add(name); n++) name = stem + "_" + n + "." + extension;
            var destination = Path.Combine(transaction, name);
            Protocol.LocalPath(destination);
            if (!Protocol.IsInside(destination, transaction)) throw new InvalidOperationException("输出路径越界。");
            File.Copy(file, destination, overwrite: false);
            outputs.Add(Path.Combine(output, name));
        }
        VerifiedTree(transaction);
        token.ThrowIfCancellationRequested();
        Protocol.LocalPath(output);
        if (Directory.Exists(output)) Directory.Delete(output, recursive: false); // Verified empty, non-recursive only.
        Directory.Move(transaction, output); // Single commit; never overwrite an existing file.
        return outputs.ToArray();
    }

    private static bool IsAnimatedPng(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[8];
        if (stream.Read(header) != 8 || !header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return false;
        for (var n = 0; n < 65536 && stream.Position + 12 <= stream.Length; n++)
        {
            if (stream.Read(header) != 8) return false;
            var size = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (header[4..].SequenceEqual("acTL"u8)) return true;
            if (header[4..].SequenceEqual("IDAT"u8) || header[4..].SequenceEqual("IEND"u8)) return false;
            if (size > stream.Length - stream.Position - 4) return false;
            stream.Seek(size + 4L, SeekOrigin.Current);
        }
        return false;
    }

    internal static string[] Warnings(string source, string target)
    {
        var warnings = new List<string>();
        if (source is "psd" or "psb" or "xcf") warnings.Add("图像导出为合成平面图，不保留图层、文字或可编辑效果。");
        if (source is "ico" or "cur" or "dds") warnings.Add("图标或纹理仅转换最大尺寸图像，不保留尺寸变体或 mipmap。");
        if ((AnimationFormats.Contains(source) || source == "png") && target != ""
            && !AnimationFormats.Contains(target) && FormatRegistry.FindById(target)?.Category != FormatCategory.Video)
            warnings.Add("如原图为动图，将保留全部帧：静态格式分别导出，PDF/TIFF 保存为多页；静态结果不播放动画。");
        if (source == "pdf" && target != "" && MagickNames.ContainsKey(target))
            warnings.Add("保留 PDF 全部页面；静态格式每页一个文件，TIFF/GIF 可包含多页，结果不再是可选中文字的文档。");
        if (target == "jpg") warnings.Add("JPG 使用有损压缩，透明区域会填成白色。");
        if (target == "gif") warnings.Add("GIF 最多 256 色，可能出现颜色损失。");
        if (target is "jpg" or "webp" or "avif") warnings.Add("超过目标格式尺寸上限的长图会等比缩小。");
        if (source is "psd" or "psb" or "heic" or "avif" or "jxl" or "tiff" or "raw" or "exr" or "hdr")
            warnings.Add("当前为 8 位图像引擎，不保证保留高位深或 HDR。");
        if (target == "svg" && FormatRegistry.FindById(source)?.Category == FormatCategory.Image)
            warnings.Add("图片转 SVG 会描摹为矢量形状，外观可能变化，不等于原始矢量设计文件。");
        if (source == "pdf" && target == "txt") warnings.Add("仅提取 PDF 已有文字；扫描图片中的文字不会自动 OCR。");
        if (source == "pdf" && target == "pptx") warnings.Add("每页 PDF 成为一张幻灯片图片，文字写入备注，不还原为可编辑图形。");
        if (FormatRegistry.FindById(source)?.Category is FormatCategory.Document or FormatCategory.Spreadsheet or FormatCategory.Presentation or FormatCategory.Hwp)
            warnings.Add("文档格式转换可能改变排版、字体、宏或公式；原文件始终保留。");
        if (source is "md" or "html" || target == "html")
            warnings.Add("快捷转换仅处理自包含文档；脚本、外部图片和依赖文件不会自动加载，遇到此类内容会停止并保留原件。");
        if (FormatRegistry.FindById(source)?.Category == FormatCategory.Model3D)
            warnings.Add("3D 格式支持不同；骨骼动画、材质与单位可能变化。");
        if (FormatRegistry.FindById(source)?.Category == FormatCategory.Video
            && FormatRegistry.FindById(target)?.Category == FormatCategory.Video)
            warnings.Add("保留第一路视频和第一条音轨；其他音轨、字幕和附件不包含在结果中。");
        if (source is "eml" or "msg" && target == "zip")
            warnings.Add("仅导出邮件附件，不包含邮件正文；原邮件保留。");
        if (FormatRegistry.FindById(target)?.Category is FormatCategory.Audio or FormatCategory.Subtitle
            && FormatRegistry.FindById(source)?.Category == FormatCategory.Video)
            warnings.Add(target is "srt" or "ass" or "vtt" ? "提取第一个文本字幕轨道，不包含其他字幕轨或画面。" : "仅导出音频，不包含视频画面。");
        return warnings.ToArray();
    }

    public void Dispose()
    {
        try
        {
            Protocol.LocalPath(_work);
            if (Directory.Exists(_work))
                Directory.Delete(_work, recursive: true);
        }
        catch (IOException) { } // Host also reclaims this exact owned directory after worker exit.
        catch (UnauthorizedAccessException) { }
        catch (RequestException) { }
    }
}

internal sealed class FilteredConverter(IConverter inner, IReadOnlyList<ConversionEdge> edges) : IConverter
{
    public string Id => inner.Id;
    public string DisplayName => inner.DisplayName;
    public int MaxParallelism => inner.MaxParallelism;
    public IReadOnlyList<ConversionEdge> Edges => edges;
    public EngineStatus GetStatus() => inner.GetStatus();
    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken token) =>
        inner.ConvertAsync(step, progress, token);
}

internal sealed class SafeOutputAllocator(string root, string sourceName) : IOutputAllocator
{
    internal HashSet<string> Allocated { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Allocate(string extension, string? suffix = null)
    {
        if (!Regex.IsMatch(extension, "^[a-zA-Z0-9]{1,16}(?:\\.[a-zA-Z0-9]{1,16})?$")
            || suffix is not null && (suffix.Length > 100 || suffix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || !suffix.StartsWith('_') || suffix.Contains("..", StringComparison.Ordinal)))
            throw new InvalidOperationException("转换组件请求了无效的输出路径。");
        var clean = Regex.Replace(sourceName, "[^\\p{L}\\p{N} _-]", "_").Trim();
        if (clean.Length == 0) clean = "converted";
        if (clean.StartsWith("converted-", StringComparison.Ordinal)) clean = clean[10..];
        if (clean.Length > 70) clean = clean[..70];
        var path = Path.Combine(root, "converted-" + clean + suffix + "." + extension.ToLowerInvariant());
        if (!Protocol.IsInside(path, root) || !Allocated.Add(path) || File.Exists(path))
            throw new InvalidOperationException("转换组件重复使用了输出路径。");
        return path;
    }
}
