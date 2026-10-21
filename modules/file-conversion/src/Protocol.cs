using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Nook.FileConversion;

internal sealed class Request
{
    public int SchemaVersion { get; set; }
    public string Id { get; set; } = "";
    public string Operation { get; set; } = "";
    public string[] Inputs { get; set; } = [];
    public string Target { get; set; } = "";
    public int Quality { get; set; } = 90;
    public string OutputDirectory { get; set; } = "";
    public string ResponsePath { get; set; } = "";
    public string ComponentId { get; set; } = "";
    public string? ComponentRoot { get; set; }
}

internal sealed class Response
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Operation { get; set; } = "";
    public string Status { get; set; } = "error";
    public TargetInfo[] Targets { get; set; } = [];
    public FileInfoResult[] Files { get; set; } = [];
    public string[] Outputs { get; set; } = [];
    public string[] MissingEngines { get; set; } = [];
    public string[] MissingComponentIds { get; set; } = [];
    public ComponentPackageState[] Components { get; set; } = [];
    public ErrorInfo? Error { get; set; }
}

internal sealed record ErrorInfo(string Code, string Message);
internal sealed record TargetInfo(string Id, string Label, string Extension, string Warning);
internal sealed record FileInfoResult(string Source, string Format, string[] Targets, string[] Outputs, string[] Warnings);
internal sealed class RequestException(string message) : Exception(message);

internal static class Protocol
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    internal static Request Read(string requestPath)
    {
        LocalPath(requestPath, mustExist: true);
        if (new FileInfo(requestPath).Length > 64 * 1024)
            throw new RequestException("请求过大。");
        var request = JsonSerializer.Deserialize<Request>(File.ReadAllText(requestPath), Json)
            ?? throw new RequestException("请求为空。");
        if (request.SchemaVersion != 1 || !Regex.IsMatch(request.Id, "^[a-f0-9]{32}$"))
            throw new RequestException("不支持的请求版本或任务编号。");
        if (request.Operation is not ("capabilities" or "convert"
            or "components-list" or "components-install" or "components-update" or "components-uninstall"))
            throw new RequestException("不支持的操作。");
        var componentOperation = request.Operation.StartsWith("components-", StringComparison.Ordinal);
        if (request.ComponentRoot is not null)
        {
            LocalPath(request.ComponentRoot);
            try { ComponentPaths.LocalRoot(request.ComponentRoot); }
            catch (InvalidDataException ex) { throw new RequestException(ex.Message); }
        }
        if (componentOperation)
        {
            if (request.Inputs is null || request.Inputs.Length != 0)
                throw new RequestException("组件管理不接受素材文件。");
            if (request.Operation == "components-list")
            {
                if (!string.IsNullOrEmpty(request.ComponentId))
                    throw new RequestException("列出组件时不需要指定组件。");
            }
            else if (string.IsNullOrEmpty(request.ComponentId)
                || !Regex.IsMatch(request.ComponentId, "^[a-z0-9-]{1,32}$"))
                throw new RequestException("请选择有效的转换组件。");
        }
        else
        {
            if (!string.IsNullOrEmpty(request.ComponentId))
                throw new RequestException("转换任务不能同时修改组件。");
            if (request.Inputs is null || request.Inputs.Length is < 1 or > 100 ||
                request.Inputs.Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Inputs.Length)
                throw new RequestException("请选择 1 至 100 个不同的本地文件。");
        }
        foreach (var input in request.Inputs)
        {
            LocalPath(input, mustExist: true);
            if (new FileInfo(input).Length > 8L * 1024 * 1024 * 1024)
                throw new RequestException("暂不处理大于 8 GB 的文件。");
        }
        LocalPath(request.ResponsePath);
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(request.ResponsePath)),
                Path.GetDirectoryName(Path.GetFullPath(requestPath)), StringComparison.OrdinalIgnoreCase) ||
            File.Exists(request.ResponsePath) || Directory.Exists(request.ResponsePath))
            throw new RequestException("回包必须是当前任务目录中尚不存在的新文件。");
        if (request.Operation == "convert")
        {
            if (request.Quality is < 1 or > 100)
                throw new RequestException("图片质量必须在 1 至 100 之间。");
            if (request.Inputs.Length != 1)
                throw new RequestException("每个转换任务只接受一个文件，请在主程序中排队。");
            LocalPath(request.OutputDirectory);
            if (File.Exists(request.OutputDirectory) ||
                Directory.Exists(request.OutputDirectory) && Directory.EnumerateFileSystemEntries(request.OutputDirectory).Any())
                throw new RequestException("输出目录必须是新建的空目录，不能覆盖已有文件。");
            if (request.Inputs.Any(input => IsInside(input, request.OutputDirectory)))
                throw new RequestException("输出目录不能包含原文件。");
            if (!Regex.IsMatch(request.Target, "^[a-z0-9]{1,10}$"))
                throw new RequestException("目标格式无效。");
        }
        return request;
    }

    internal static void LocalPath(string path, bool mustExist = false)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0 ||
            path.Contains('\0') || path.Length > 4096)
            throw new RequestException("只接受普通本地磁盘上的绝对路径，不接受网址、网络共享或设备路径。");
        var full = Path.GetFullPath(path);
        if (Path.GetPathRoot(full) == full)
            throw new RequestException("不能使用磁盘根目录。");
        for (var check = full; !string.IsNullOrEmpty(check); check = Path.GetDirectoryName(check))
        {
            if ((File.Exists(check) || Directory.Exists(check)) &&
                (File.GetAttributes(check) & FileAttributes.ReparsePoint) != 0)
                throw new RequestException("暂不处理符号链接或重解析目录。");
        }
        if (mustExist && !File.Exists(full))
            throw new RequestException("文件不存在或已移动。");
    }

    internal static bool IsInside(string path, string directory) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory))
            + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static void WriteProgress(string session, string id, ComponentOperationProgress progress)
    {
        var path = Path.Combine(session, "progress.json");
        var temporary = Path.Combine(session, "progress-" + id + ".partial");
        LocalPath(path);
        LocalPath(temporary);
        var total = Math.Max(0, progress.TotalBytes);
        var done = Math.Max(0, progress.BytesReceived);
        var json = JsonSerializer.Serialize(new
        {
            stage = progress.Phase,
            componentId = progress.Id,
            bytesDone = done,
            bytesTotal = total,
            fraction = total > 0 ? Math.Clamp((double)done / total, 0, 1) : 0,
            message = progress.Message
        }, Json);
        if (json.Length > 8192) return;
        File.WriteAllText(temporary, json, new System.Text.UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    internal static void Write(Response response, string? responsePath)
    {
        var json = JsonSerializer.Serialize(response, Json);
        if (responsePath is not null)
        {
            var temporary = responsePath + ".partial-" + response.Id;
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                writer.Write(json);
            File.Move(temporary, responsePath, overwrite: false);
        }
        Console.Out.WriteLine(json);
    }
}
