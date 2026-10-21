using System.Text;

namespace Nook.FileConversion;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var response = new Response();
        Request? request = null;
        var exitCode = 2;
        try
        {
            if (args.Length != 2 || args[0] != "--request")
                throw new RequestException("用法：Nook.FileConversion.exe --request <绝对请求文件路径>");
            request = Protocol.Read(args[1]);
            response.Id = request.Id;
            response.Operation = request.Operation;
            var session = Path.GetDirectoryName(Path.GetFullPath(args[1]))!;
            var cancelPath = Path.Combine(session, "cancel.flag");
            using var cancellation = new CancellationTokenSource();
            if (File.Exists(cancelPath))
                cancellation.Cancel();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            using var poll = new Timer(_ =>
            {
                try
                {
                    if (File.Exists(cancelPath))
                        cancellation.Cancel();
                }
                catch (ObjectDisposedException) { } // A final timer callback may race process shutdown.
            }, null, 0, 100);
            cancellation.Token.ThrowIfCancellationRequested();
            if (request.Operation.StartsWith("components-", StringComparison.Ordinal))
            {
                var store = new ComponentStore(request.ComponentRoot);
                var progress = new ComponentProgressSink(session, request.Id);
                switch (request.Operation)
                {
                    case "components-install":
                        await store.InstallAsync(request.ComponentId, progress, cancellation.Token);
                        break;
                    case "components-update":
                        await store.UpdateAsync(request.ComponentId, progress, cancellation.Token);
                        break;
                    case "components-uninstall":
                        cancellation.Token.ThrowIfCancellationRequested();
                        store.Uninstall(request.ComponentId);
                        break;
                }
                response.Components = store.List().ToArray();
            }
            else
            {
                await RunConversionAsync(request, response, session, cancellation.Token);
            }
            // A cancellation arriving after the atomic publish must not hide committed outputs.
            if (request.Operation is "capabilities" or "components-list")
                cancellation.Token.ThrowIfCancellationRequested();
            response.Status = "ok";
            exitCode = 0;
        }
        catch (OperationCanceledException)
        {
            response.Status = "cancelled";
            response.Outputs = [];
            response.Error = new("cancelled",
                request?.Operation.StartsWith("components-", StringComparison.Ordinal) == true
                    ? "已取消组件操作；未完成的下载不会启用，原文件没有改动。"
                    : "已取消转换，原文件没有改动。");
            exitCode = 3;
        }
        catch (Exception ex)
        {
            response.Status = "error";
            response.Outputs = [];
            var invalid = ex is RequestException or System.Text.Json.JsonException;
            var componentOperation = request?.Operation.StartsWith("components-", StringComparison.Ordinal) == true;
            response.Error = new(invalid ? "invalid_request" : componentOperation ? "component_failed" : "conversion_failed",
                invalid ? ex.Message : (componentOperation ? "组件操作失败：" : "转换失败，原文件没有改动：") + ex.Message);
            exitCode = invalid ? 2 : 1;
        }
        try
        {
            Protocol.Write(response, request?.ResponsePath);
        }
        catch (Exception ex)
        {
            // Never report success when the durable response was not committed.
            response.Status = "error";
            response.Outputs = [];
            response.Error = new("response_write_failed", ex.Message);
            Protocol.Write(response, null);
            exitCode = 1;
        }
        return exitCode;
    }

    private static async Task RunConversionAsync(Request request, Response response, string session,
        CancellationToken token)
    {
        IDisposable? lease = null;
        IReadOnlyDictionary<string, string>? copies = null;
        string? componentWarning = null;
        try
        {
            try
            {
                var store = new ComponentStore(request.ComponentRoot);
                lease = store.AcquireReadLease();
                copies = store.GetOwnCopies();
            }
            catch (ComponentStoreBusyException)
            {
                // A writer may be switching executable snapshots. Never bypass
                // its lease and never pretend a maintenance race is a missing engine.
                throw;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException
                or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                // Optional storage cannot break built-in image/PDF conversion.
                // Continue with no managed executable paths, without repairing,
                // adopting or deleting anything in an unrecognized directory.
                lease?.Dispose();
                lease = null;
                componentWarning = "扩展转换组件暂不可用，仍可使用内置转换：" + ex.Message;
            }
            using var engine = new ConversionService(session, request.Id, copies);
            response.Files = engine.Capabilities(request.Inputs);
            response.Targets = engine.CommonTargets(response.Files);
            response.MissingEngines = componentWarning is null ? engine.MissingEngines
                : [.. engine.MissingEngines, componentWarning];
            response.MissingComponentIds = engine.MissingComponentIds;
            if (request.Operation == "convert")
            {
                response.Outputs = await engine.ConvertAsync(request, token);
                response.Files = [response.Files[0] with
                {
                    Outputs = response.Outputs,
                    Warnings = ConversionService.Warnings(response.Files[0].Format, request.Target)
                }];
            }
        }
        finally { lease?.Dispose(); }
    }
}

// Synchronous progress avoids queued callbacks overwriting the final status after
// a component operation has completed. The file is diagnostic only: a transient
// reader/antivirus lock must not abort an otherwise verified installation.
internal sealed class ComponentProgressSink(string session, string id) : IProgress<ComponentOperationProgress>
{
    private readonly object _gate = new();
    private string _lastPhase = "";
    private long _lastTick;

    public void Report(ComponentOperationProgress value)
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (value.Phase == _lastPhase && now - _lastTick < 150
                && (value.TotalBytes <= 0 || value.BytesReceived < value.TotalBytes)) return;
            _lastPhase = value.Phase;
            _lastTick = now;
            try { Protocol.WriteProgress(session, id, value); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
