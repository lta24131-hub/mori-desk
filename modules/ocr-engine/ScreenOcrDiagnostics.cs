using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RapidOcrNet;
using SkiaSharp;

// Only instantiated by the VM-only CLI. Production does not create a writer,
// progress observer, diagnostic tile collection or timing snapshots.
internal sealed class ScreenOcrDiagnostics : IDisposable
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object gate = new();
    private readonly StreamWriter writer;
    private readonly long started = Stopwatch.GetTimestamp();
    private long sequence;
    private bool disposed;
    internal string CurrentStage { get; private set; } = "created";
    public double? ModelVerificationMs { get; set; }
    public double? ModelInitializationMs { get; set; }
    public ScreenOcrSessionOptionsDiagnostic? SessionOptions { get; set; }
    public double? ReadWallMs { get; set; }
    public double? FragmentMergeMs { get; set; }
    public double? LayoutMs { get; set; }
    public int? WordsBeforeFragmentMerge { get; set; }
    public int? WordsAfterFragmentMerge { get; set; }
    public List<ScreenOcrTileDiagnostic> Tiles { get; } = [];

    internal ScreenOcrDiagnostics(string jsonlPath)
    {
        writer = new StreamWriter(new FileStream(jsonlPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false)) { AutoFlush = true };
    }

    internal void Stage(string stage, object? data = null)
    {
        lock (gate)
        {
            if (disposed) return;
            CurrentStage = stage;
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                sequence = ++sequence, utc = DateTimeOffset.UtcNow,
                elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds, stage, data
            }, JsonOptions));
        }
    }

    internal ScreenOcrTileDiagnostic BeginTile(int id, SKRectI core, SKRectI context)
    {
        var tile = new ScreenOcrTileDiagnostic
        {
            Id = id, Core = ScreenOcrDiagnosticRect.From(core), Context = ScreenOcrDiagnosticRect.From(context)
        };
        Tiles.Add(tile);
        Stage("tile-region-plan-start", tile);
        return tile;
    }

    internal void FinishTile(ScreenOcrTileDiagnostic tile)
    {
        tile.Completed = true;
        Stage("tile-complete", tile);
    }

    internal IProgress<(int Completed, int Total)> CreateProgress(ScreenOcrTileDiagnostic tile, long detectStarted) =>
        new RecognitionProgress(this, tile, detectStarted);

    internal void DetectCompleted(ScreenOcrTileDiagnostic tile, OcrResult result)
    {
        tile.ReportedDbNetTimeMs = result.DbNetTime;
        tile.ReportedDetectTimeMs = result.DetectTime;
        tile.ReportedAfterDetectorMs = result.DetectTime - result.DbNetTime;
        tile.DetectWallMinusReportedDetectMs = tile.DetectCallWallMs - result.DetectTime;
        tile.ReturnedBlockCount = result.TextBlocks.Length;
        tile.ReturnedWordCount = result.TextBlocks.Sum(block => block.WordResults?.Length ?? 0);
        tile.ReturnedBlockCrnnTimeSumMs = result.TextBlocks.Sum(block => (double)block.CrnnTime);
        tile.RecognitionCropCount ??= 0; // Empty detector output has no progress callback.
        tile.FilteredOrEmptyCropCount = tile.RecognitionCropCount - tile.ReturnedBlockCount;
        tile.Blocks = result.TextBlocks.Select((block, index) => new ScreenOcrBlockDiagnostic
        {
            Index = index, Text = block.Text, CharacterCount = block.CharScores?.Length ?? 0,
            WordCount = block.WordResults?.Length ?? 0, CrnnTimeMs = block.CrnnTime
        }).ToArray();
        Stage("tile-detect-complete", tile);
    }

    private sealed class RecognitionProgress(ScreenOcrDiagnostics owner, ScreenOcrTileDiagnostic tile, long detectStarted)
        : IProgress<(int Completed, int Total)>
    {
        private double lastLoggedMs = double.NegativeInfinity;

        public void Report((int Completed, int Total) value)
        {
            // Upstream serializes callbacks even under DOP > 1; additionally
            // serialize the writer against the CLI deadline watchdog.
            double elapsed = Stopwatch.GetElapsedTime(detectStarted).TotalMilliseconds;
            tile.RecognitionCropCount = value.Total;
            tile.RecognitionCompletedCropCount = value.Completed;
            tile.FirstRecognitionCompletionFromDetectStartMs ??= elapsed;
            tile.LastRecognitionCompletionFromDetectStartMs = elapsed;
            if (value.Completed == 1 || value.Completed == value.Total || elapsed - lastLoggedMs >= 1000)
            {
                lastLoggedMs = elapsed;
                owner.Stage("tile-recognition-progress", new
                {
                    tileId = tile.Id, value.Completed, value.Total, elapsedFromDetectStartMs = elapsed
                });
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            writer.Dispose();
        }
    }
}

internal sealed record ScreenOcrDiagnosticRect(int X, int Y, int Width, int Height)
{
    internal static ScreenOcrDiagnosticRect From(SKRectI value) => new(value.Left, value.Top, value.Width, value.Height);
}

// Configuration read from the actual managed SessionOptions passed to InitModels.
// This does not claim that every graph optimization fired or measure native spinning.
internal sealed record ScreenOcrSessionOptionsDiagnostic(
    string RequestedGraphOptimization, string AppliedGraphOptimization, int GraphOptimizationLevel,
    string ExecutionMode, int IntraOpThreads, int InterOpThreads,
    int? AllowSpinning, bool SpinningConfigEntriesApplied);

internal sealed class ScreenOcrTileDiagnostic
{
    public int Id { get; init; }
    public required ScreenOcrDiagnosticRect Core { get; init; }
    public required ScreenOcrDiagnosticRect Context { get; init; }
    public ScreenOcrDiagnosticRect? Content { get; set; }
    public bool Skipped { get; set; }
    public bool Completed { get; set; }
    public int? DetectorBitmapWidth { get; set; }
    public int? DetectorBitmapHeight { get; set; }
    public double? RegionPlanningMs { get; set; }
    public double? TileRenderMs { get; set; }
    public double? DetectCallWallMs { get; set; }
    public double? ReportedDbNetTimeMs { get; set; }
    public double? ReportedDetectTimeMs { get; set; }
    public double? ReportedAfterDetectorMs { get; set; }
    public double? DetectWallMinusReportedDetectMs { get; set; }
    public double? ReturnedBlockCrnnTimeSumMs { get; set; }
    public double? WordMappingMs { get; set; }
    public int? RecognitionCropCount { get; set; }
    public int? RecognitionCompletedCropCount { get; set; }
    public double? FirstRecognitionCompletionFromDetectStartMs { get; set; }
    public double? LastRecognitionCompletionFromDetectStartMs { get; set; }
    public int? ReturnedBlockCount { get; set; }
    public int? ReturnedWordCount { get; set; }
    public int? FilteredOrEmptyCropCount { get; set; }
    public int? KeptWordCount { get; set; }
    public ScreenOcrBlockDiagnostic[]? Blocks { get; set; }
}

internal sealed class ScreenOcrBlockDiagnostic
{
    public int Index { get; init; }
    public required string Text { get; init; }
    public int CharacterCount { get; init; }
    public int WordCount { get; init; }
    public double CrnnTimeMs { get; init; }
}
