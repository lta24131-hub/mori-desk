using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using RapidOcrNet;
using SkiaSharp;

// No worker protocol or application preference can select these options.
// One VM-only invocation runs exactly one fixture, with a fresh model session.
internal static class ScreenOcrDiagnosticCommand
{
    private const int DeadlineGraceSeconds = 5;
    private static readonly HashSet<string> Fixtures = new(StringComparer.Ordinal)
        { "short-card", "craft", "lexicon-pipeline", "mixed-size-bilingual" };

    internal static void RequireTestVm()
    {
        if (!Environment.UserName.Equals("NookTester", StringComparison.OrdinalIgnoreCase) ||
            !AppContext.BaseDirectory.StartsWith(@"C:\NookLab\Audit\apps\", StringComparison.OrdinalIgnoreCase) ||
            !(Environment.GetEnvironmentVariable("MORI_DESK_DATA") ?? "").StartsWith(@"C:\NookLab\Audit\data\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This evaluation requires the dedicated Nook test VM.");
    }

    internal static int Run(string[] args)
    {
        RequireTestVm();
        var options = Parse(args);
        string prefix = Path.Combine(AppContext.BaseDirectory, "ocr-phase-" + options.CaseId);
        if (File.Exists(prefix + ".json") || File.Exists(prefix + ".jsonl"))
            throw new IOException("Diagnostic case already exists; use a unique case-id for every configuration/repetition.");
        using var diagnostics = new ScreenOcrDiagnostics(prefix + ".jsonl");
        using var cancel = new CancellationTokenSource();
        // InitModels has no cancellation parameter. Give cancellable inference
        // five seconds to unwind, then end only this standalone diagnostic process.
        // The hard watchdog never waits on the log lock or native inference.
        using var hardDeadline = new Timer(_ => Environment.Exit(124), null,
            TimeSpan.FromSeconds(options.TimeoutSeconds + DeadlineGraceSeconds), Timeout.InfiniteTimeSpan);
        using var deadline = new Timer(_ =>
        {
            string interruptedStage = diagnostics.CurrentStage;
            try { cancel.Cancel(); } catch (ObjectDisposedException) { }
            diagnostics.Stage("deadline-cancellation-requested", new { options.TimeoutSeconds, graceSeconds = DeadlineGraceSeconds, interruptedStage });
        }, null, TimeSpan.FromSeconds(options.TimeoutSeconds), Timeout.InfiniteTimeSpan);

        long requestStarted = Stopwatch.GetTimestamp();
        string startedUtc = DateTimeOffset.UtcNow.ToString("O");
        string status = "failed";
        string? error = null;
        ScreenPage? page = null;
        ScreenOcr? engine = null;
        string? sourceSha256 = null;
        int? width = null, height = null;
        double? metadataValidationMs = null, decodeMs = null;
        object? modelFiles = null, engineFiles = null;
        int exitCode = 1;
        diagnostics.Stage("request-start", new
        {
            options.CaseId, options.Fixture, options.Model, options.Threads, options.RecDop, options.TimeoutSeconds, options.GraphOptimization,
            options.AllowSpinning, spinningConfiguration = options.AllowSpinning.HasValue ? "explicit" : "upstream-native-default-unspecified",
            hardExitAfterSeconds = options.TimeoutSeconds + DeadlineGraceSeconds,
            productionDefaultsUnchanged = new { model = "v5", threads = 2, recDop = 1, graphOptimization = "extended" },
            scale = 2, overlapPixels = 64, resultCache = false, freshProcessRequired = true
        });
        try
        {
            diagnostics.Stage("metadata-validation-start");
            long metadataStarted = Stopwatch.GetTimestamp();
            string fixtureDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "fixtures"));
            string imagePath = Path.Combine(fixtureDirectory, options.Fixture + ".png");
            using var fixtureManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtureDirectory, "fixture-manifest.json")));
            var entries = fixtureManifest.RootElement.GetProperty("fixtures").EnumerateArray()
                .Where(entry => entry.GetProperty("fixture").GetString() == options.Fixture).ToArray();
            if (entries.Length != 1) throw new InvalidDataException("Fixture manifest must contain exactly one matching entry.");
            var fixture = entries[0];
            sourceSha256 = HashFile(imagePath);
            if (!sourceSha256.Equals(fixture.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase) ||
                new FileInfo(imagePath).Length != fixture.GetProperty("bytes").GetInt64())
                throw new InvalidDataException("Diagnostic fixture differs from the fixed comparison manifest.");

            string v5 = Path.Combine(AppContext.BaseDirectory, "models", "ch-v5");
            string recognizer = Path.Combine(v5, "ch_PP-OCRv5_rec_mobile_infer.onnx");
            string dictionary = Path.Combine(v5, "ppocrv5_dict.txt");
            if (options.Model == "v5det-v6tiny-rec")
            {
                string diagnosticModels = Path.Combine(AppContext.BaseDirectory, "diagnostic-models", "v6-rec-tiny");
                recognizer = Path.Combine(diagnosticModels, "PP-OCRv6_rec_tiny.onnx");
                dictionary = Path.Combine(diagnosticModels, "ppocrv6_tiny_dict.txt");
                VerifyDiagnosticFile(recognizer, 4489813, "E16E242DE5937AD92609223F19BC2AFF3727EE40B095F996907C24749BAD251B");
                VerifyDiagnosticFile(dictionary, 27156, "C5CBE34EF40C29C4DF07ED012BF96569CB69A2D2A01A07027E9F13CB832BD9CD");
            }
            else if (options.Model == "v5det-v6small-rec")
            {
                string diagnosticModels = Path.Combine(AppContext.BaseDirectory, "diagnostic-models", "v6-rec-small");
                recognizer = Path.Combine(diagnosticModels, "PP-OCRv6_rec_small.onnx");
                dictionary = Path.Combine(diagnosticModels, "ppocrv6_dict.txt");
                VerifyDiagnosticFile(recognizer, 21234383, "6F327246B50388F3C176AE304BD95767EA6DC0C9AE92153EF8CBE210B3C14884");
                VerifyDiagnosticFile(dictionary, 74947, "B5F2BFE2BDD9448429E3E82B51C789775D9B42F2403D082B00662EB77E401C5D");
            }
            modelFiles = new
            {
                detector = FileIdentity(Path.Combine(v5, "ch_PP-OCRv5_mobile_det.onnx")),
                classifier = FileIdentity(Path.Combine(v5, "ch_ppocr_mobile_v2.0_cls_infer.onnx")),
                recognizer = FileIdentity(recognizer), dictionary = FileIdentity(dictionary),
                detectorNormalization = "Unchanged PP-OCRv5 ImageNet normalization; no V6 detector preset used."
            };
            engineFiles = new
            {
                executable = FileIdentity(Environment.ProcessPath!),
                managedAssembly = FileIdentity(Assembly.GetExecutingAssembly().Location),
                rapidOcrAssembly = FileIdentity(typeof(RapidOcr).Assembly.Location),
                rapidOcrVersion = typeof(RapidOcr).Assembly.GetName().Version?.ToString()
            };
            metadataValidationMs = Stopwatch.GetElapsedTime(metadataStarted).TotalMilliseconds;
            diagnostics.Stage("metadata-validation-complete", new { sourceSha256, metadataValidationMs, modelFiles, engineFiles });
            cancel.Token.ThrowIfCancellationRequested();

            diagnostics.Stage("fixture-decode-start");
            long decodeStarted = Stopwatch.GetTimestamp();
            using var bitmap = SKBitmap.Decode(imagePath) ?? throw new InvalidDataException("Fixture decode failed.");
            decodeMs = Stopwatch.GetElapsedTime(decodeStarted).TotalMilliseconds;
            width = bitmap.Width; height = bitmap.Height;
            if (width != fixture.GetProperty("width").GetInt32() || height != fixture.GetProperty("height").GetInt32())
                throw new InvalidDataException("Decoded fixture dimensions differ from the manifest.");
            diagnostics.Stage("fixture-decode-complete", new { width, height, decodeMs });
            cancel.Token.ThrowIfCancellationRequested();

            engine = new ScreenOcr(diagnostics, options.Threads, options.RecDop, recognizer, dictionary, options.AllowSpinning, options.GraphOptimization);
            cancel.Token.ThrowIfCancellationRequested();
            diagnostics.Stage("read-start");
            long readStarted = Stopwatch.GetTimestamp();
            try { page = engine.Read(bitmap, cancel.Token); }
            finally { diagnostics.ReadWallMs = Stopwatch.GetElapsedTime(readStarted).TotalMilliseconds; }
            cancel.Token.ThrowIfCancellationRequested();
            status = "completed";
            exitCode = 0;
        }
        catch (OperationCanceledException ex)
        {
            status = "timed-out";
            error = ex.ToString();
            exitCode = 124;
        }
        catch (Exception ex)
        {
            error = ex.ToString();
        }
        finally
        {
            try { engine?.Dispose(); }
            catch (Exception ex)
            {
                status = "failed";
                error = (error is null ? "" : error + "\n") + "Disposal failed: " + ex;
                exitCode = 1;
            }
        }

        if (cancel.IsCancellationRequested && exitCode == 0)
        {
            status = "timed-out";
            error = "Diagnostic total deadline was exceeded before model disposal completed.";
            exitCode = 124;
        }
        double totalWallMs = Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds;
        using var process = Process.GetCurrentProcess();
        var text = page is null ? null : string.Join("\n", page.Words.GroupBy(word => word.Line)
            .Select(line => string.Concat(line.Select(word => word.Prefix + word.Text))));
        string lastWorkStage = diagnostics.CurrentStage;
        diagnostics.Stage("request-finished", new { status, exitCode, totalWallMs, lastWorkStage });
        var report = new
        {
            schemaVersion = 1, options.CaseId, options.Fixture, startedUtc, finishedUtc = DateTimeOffset.UtcNow,
            status, completed = status == "completed", exitCode, error, sourceSha256, width, height,
            engineIdentity = ScreenOcr.Identity, diagnosticPipelineIdentity = ScreenOcr.Identity + ";model=" + options.Model,
            parameters = new { model = options.Model, modelThreads = options.Threads, recognitionParallelism = options.RecDop,
                options.AllowSpinning, options.GraphOptimization, options.TimeoutSeconds, hardExitGraceSeconds = DeadlineGraceSeconds },
            productionDefaultsUnchanged = new { model = "v5", modelThreads = 2, recognitionParallelism = 1, graphOptimization = "extended" },
            graphOptimization = new
            {
                requested = options.GraphOptimization,
                sessionOptions = diagnostics.SessionOptions,
                actualOptimizationsMeasured = false,
                interpretation = "Records the requested level and actual managed SessionOptions passed to InitModels, not which native graph transformations fired. No optimized model is saved or reused.",
                productionSetting = "unchanged upstream ORT_ENABLE_EXTENDED via original InitModels(..., int) overload"
            },
            threadSpinning = new
            {
                requested = options.AllowSpinning?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "auto",
                configEntriesApplied = options.AllowSpinning.HasValue,
                intraOpSetting = options.AllowSpinning,
                interOpSetting = options.AllowSpinning,
                requestedConfigurationKnown = options.AllowSpinning.HasValue,
                nativeImplicitDefaultKnown = false,
                actualRuntimeSpinningMeasured = false,
                executionMode = "Unchanged ORT_SEQUENTIAL; inter-op thread pool is not created in this mode.",
                interpretation = options.AllowSpinning.HasValue
                    ? "Both session.intra_op.allow_spinning and session.inter_op.allow_spinning are explicitly configured. This records settings, not measured CPU spinning."
                    : "No spinning config entry written to the diagnostic SessionOptions. Native default depends on the ORT build and is unknown here.",
                productionSetting = "unchanged implicit native default; never overridden by this diagnostic CLI"
            },
            sameTileAndPixelPolicy = new { scale = 2, overlapPixels = 64, coreWidth = 960, coreHeight = 384,
                smallImageAreaThreshold = 450000, smallImageMaxSide = 1800, sourceDownscaled = false },
            modelFiles, engineFiles, metadataValidationMs, decodeMs, totalWallMs,
            peakProcessWorkingSetBytes = process.PeakWorkingSet64,
            logicalProcessorCount = Environment.ProcessorCount, processId = Environment.ProcessId,
            diagnostics,
            timingSemantics = new
            {
                totalWallMs = "From diagnostic request setup through OCR and model disposal, before final report serialization; includes validation, decode and JSONL logging. Not production IPC latency.",
                modelInitializationMs = "Actual InitModels call; modelVerificationMs separately measures the unchanged production V5 manifest validation.",
                detectCallWallMs = "Actual wall time around the full RapidOcr.Detect call, including PrepareDetectorInput.",
                reportedDbNetTimeMs = "Upstream detector GetTextBoxes wall time inside DetectOnce; excludes PrepareDetectorInput.",
                reportedDetectTimeMs = "Upstream DetectOnce wall time: detector, crops, classification, recognition and filtered-block construction; excludes PrepareDetectorInput.",
                reportedAfterDetectorMs = "ReportedDetectTime minus DbNetTime; measured accounting residual, NOT pure recognition wall time.",
                detectWallMinusReportedDetectMs = "Outer wall minus upstream DetectTime; accounting residual, NOT an isolated measured preprocessing phase.",
                returnedBlockCrnnTimeSumMs = "Sum over returned, already score-filtered TextBlocks only; omitted/empty crops are absent. Parallel crop durations overlap, so this sum is NOT recognition wall time.",
                recognitionProgress = "Synchronous upstream completed-crop counts; first completion already includes the first crop. First-to-last completion is NOT full recognition wall time.",
                peakProcessWorkingSetBytes = "OS process peak for this single CLI process, including models and native memory; not a GUI/process-tree measurement.",
                deadline = "Cooperative cancellation at timeoutSeconds. InitModels is not cancellable; isolated diagnostic process exits 124 after five seconds of grace if still blocked. JSONL survives an incomplete final report.",
                comparison = "No cache and one fixture per process. Alternate same-setting/control runs; change model, thread count, recognition DOP, explicit spinning setting or graph optimization independently. Auto/unspecified spinning is not assumed to mean 1."
            },
            text,
            words = page?.Words.Select(word => new
            {
                text = word.Text, prefix = word.Prefix, line = word.Line, tile = word.Tile,
                x = word.Box.Left, y = word.Box.Top, width = word.Box.Width, height = word.Box.Height,
                score = word.Confidence, confidenceSource = "Model"
            }).ToArray(),
            regions = page?.Paragraphs.Select(paragraph => new
            {
                text = paragraph.Text, x = paragraph.Box.Left, y = paragraph.Box.Top,
                width = paragraph.Box.Width, height = paragraph.Box.Height
            }).ToArray()
        };
        File.WriteAllText(prefix + ".json.pending", JsonSerializer.Serialize(report, ScreenOcrDiagnostics.JsonOptions));
        File.Move(prefix + ".json.pending", prefix + ".json");
        return exitCode;
    }

    private static string HashFile(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private static object FileIdentity(string path) => new
        { path, bytes = new FileInfo(path).Length, sha256 = HashFile(path) };

    private static void VerifyDiagnosticFile(string path, long expectedBytes, string expectedSha256)
    {
        if (new FileInfo(path).Length != expectedBytes || !HashFile(path).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Diagnostic recognizer or dictionary does not match its verified manifest: " + Path.GetFileName(path));
    }

    private static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "fixture", "case-id", "threads", "rec-dop", "model", "timeout-seconds", "allow-spinning", "graph-opt" };
        foreach (var arg in args)
        {
            int separator = arg.IndexOf('=');
            if (!arg.StartsWith("--", StringComparison.Ordinal) || separator <= 2)
                throw new ArgumentException("Diagnostic arguments use --name=value.");
            string key = arg[2..separator], value = arg[(separator + 1)..];
            if (!allowed.Contains(key) || !values.TryAdd(key, value))
                throw new ArgumentException("Unknown or repeated diagnostic option: " + key);
        }
        if (!values.TryGetValue("fixture", out string? fixture) || !Fixtures.Contains(fixture))
            throw new ArgumentException("A fixed fixture name is required.");
        if (!values.TryGetValue("case-id", out string? caseId) || !Regex.IsMatch(caseId, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("case-id must be 1-64 filename-safe characters, beginning with a letter or digit.");
        int Number(string key, int fallback, int min, int max)
        {
            if (!values.TryGetValue(key, out var value)) return fallback;
            if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int parsed) || parsed < min || parsed > max)
                throw new ArgumentException("Invalid diagnostic " + key + ".");
            return parsed;
        }
        string model = values.GetValueOrDefault("model", "v5");
        if (model is not ("v5" or "v5det-v6tiny-rec" or "v5det-v6small-rec")) throw new ArgumentException("Unknown diagnostic model.");
        string spinning = values.GetValueOrDefault("allow-spinning", "auto");
        if (spinning is not ("auto" or "0" or "1")) throw new ArgumentException("allow-spinning must be auto, 0 or 1.");
        string graphOptimization = values.GetValueOrDefault("graph-opt", "extended");
        if (graphOptimization is not ("extended" or "all")) throw new ArgumentException("graph-opt must be extended or all.");
        return new(fixture, caseId, Number("threads", 2, 1, 2), Number("rec-dop", 1, 1, 2),
            model, Number("timeout-seconds", 300, 5, 900), spinning == "auto" ? null : spinning == "0" ? 0 : 1, graphOptimization);
    }

    private sealed record Options(string Fixture, string CaseId, int Threads, int RecDop, string Model, int TimeoutSeconds, int? AllowSpinning, string GraphOptimization);
}
