#if CAPTURE_BACKGROUND_TESTS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using QingJie;

// Deliberately separate from Tests.Main and SuiteTests. No desktop screenshot,
// visible/native window, clipboard, registered hotkey, or production data is used.
internal static class CaptureBackgroundTests {
    // The VM compiler defines CAPTURE_BACKGROUND_FULL and compiles the complete
    // manually reviewed source closure. A sandbox-only invocation must then run
    // every safe group, not silently fall back to the historical four groups.
    // The generic legacy compiler still supports the smaller source closure.
    static readonly string[] DefaultGroups = {
        "CaptureVisualPixelTests", "CaptureVisualRegionTests", "CaptureHoverTests", "CaptureTextEditingTests",
#if CAPTURE_BACKGROUND_FULL
        "MoriDesk.CaptureStartGateTests", "MoriDesk.ScreenColorTests",
        "CaptureInteractionReworkTests", "CaptureInputUsabilityTests", "CaptureSelectionInteractionTests", "CaptureSelectionKeyboardTests",
        "MoriDesk.CaptureToolbarLayoutTests", "CaptureAnnotationInteractionTests", "CaptureToolbarUsabilityTests", "CaptureDirectResizeTests",
        "CaptureToolbarIntegrationTests", "CaptureOutputLifecycleTests", "CaptureMonitorSessionTests", "MoriDesk.ScreenshotCollectionTests",
        "ExternalScrollCaptureTests", "CaptureFeatureSettingsTests", "CaptureFeatureRuntimeTests", "CaptureFeatureBoundaryTests",
        "ScrollCaptureWarmLeaseTests", "ScrollCapturePreviewTests", "ScrollCaptureSessionTests", "ScrollCaptureStitcherTests",
        "CapturePinPlacementTests", "MoriDesk.PinRuntimeTests", "CaptureStartupConstructorBenchmark", "CaptureStartupLatencyTests", "CaptureTextZoomTests", "OcrResultContractTests",
#endif
    };
    static int checks;
    static int failedGroups;
    static int completedGroups;
    static bool completed;
    static string sandbox;
    static readonly StringBuilder report = new StringBuilder();
    static readonly StringBuilder groupSummary = new StringBuilder();
    static readonly Stopwatch elapsed = Stopwatch.StartNew();
    static string SummaryError(Exception error) {
        string text=error.ToString();return text.Length<=2000?text:text.Substring(0,2000)+"\r\n[exception clipped in summary; see full background-tests.txt]";
    }
    static void Log(string message) {
        report.AppendLine(message);
        Console.WriteLine(message);
    }
    static void SaveReport() {
        if (sandbox == null) return;
        File.WriteAllText(Path.Combine(sandbox, "background-tests.txt"), report.ToString());
        // The VM agent intentionally caps each collected file at 256,000 chars.
        // Keep all assertions in the full log and a small independent completion
        // summary, so a long PASS-heavy toolbar group cannot hide later failures.
        string pixel=CaptureVisualPixelTests.LastScanDiagnostic;
        File.WriteAllText(Path.Combine(sandbox,"background-summary.txt"),
            "Capture background summary\r\nCompleted="+completed+"; completedGroups="+completedGroups+"; successfulChecks="+checks+"; failedGroupsOrSafety="+failedGroups+"; elapsedMs="+elapsed.ElapsedMilliseconds+
            "\r\nPixelDiagnostic="+(pixel??"not run")+"\r\n"+groupSummary+"Sandbox: "+sandbox+"\r\n");
    }
    static void AssertUnshownWindows() {
        if (Application.Current == null) return;
        foreach (Window window in Application.Current.Windows) {
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("Background test created a visible or native window: " + window.GetType().FullName);
        }
    }
    static void Check(bool condition, string message) {
        AssertUnshownWindows();
        if (!condition) throw new InvalidOperationException(message);
        checks++;
        Log("PASS " + message);
    }
    static void CleanupUnshownWindows() {
        // A failure can abandon an inert editor or logical WPF mouse capture.
        // Never hide, close, initialize or otherwise operate a native surface.
        try { if (Mouse.Captured != null) Mouse.Capture(null); }
        catch (Exception error) { Log("CLEANUP ERROR releasing logical mouse capture: " + error);groupSummary.AppendLine("CLEANUP ERROR releasing logical mouse capture: "+SummaryError(error)); }
        if (Application.Current == null) return;
        foreach (Window window in Application.Current.Windows.Cast<Window>().ToArray()) {
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero) {
                Log("CLEANUP SKIPPED native/visible window: " + window.GetType().FullName);
                groupSummary.AppendLine("CLEANUP SKIPPED native/visible window: "+window.GetType().FullName);
                continue;
            }
            try { window.Close(); }
            catch (Exception error) { Log("CLEANUP ERROR " + window.GetType().FullName + ": " + error);groupSummary.AppendLine("CLEANUP ERROR "+window.GetType().FullName+": "+SummaryError(error)); }
        }
    }
    static void Run(string className) {
        int before = checks;
        var groupWatch=Stopwatch.StartNew();
        Log("GROUP " + className + (failedGroups == 0 ? "" : " [after earlier failure; shared static state may be contaminated]"));
        try {
            var type = typeof(CaptureBackgroundTests).Assembly.GetType(className, true);
            var method = type.GetMethod("Run", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Action<bool, string>) }, null);
            object[] parameters = { (Action<bool, string>)Check };
            // This reviewed pin-runtime group needs its own directory. It uses
            // Start(preview:true), synthetic pin bytes and no window/services.
            if (method == null && className == "MoriDesk.PinRuntimeTests") {
                method = type.GetMethod("Run", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string), typeof(Action<bool, string>) }, null);
                parameters = new object[] { Path.Combine(sandbox, "pin-runtime"), (Action<bool, string>)Check };
            }
            if (method == null) throw new InvalidOperationException("Missing reviewed background Run signature on " + className);
            AssertUnshownWindows();
            try { method.Invoke(null, parameters); }
            catch (TargetInvocationException error) {
                if(error.InnerException!=null)ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
            AssertUnshownWindows();
            Check(Mouse.Captured == null, className + " leaves no WPF mouse capture");
            Log("GROUP PASS " + className + ": " + (checks - before) + " checks");
            groupSummary.AppendLine("GROUP PASS "+className+": "+(checks-before)+" checks; elapsedMs="+groupWatch.ElapsedMilliseconds);
        } catch (Exception error) {
            failedGroups++;
            Log("GROUP FAIL " + className + " after " + (checks - before) + " successful checks\r\n" + error);
            groupSummary.AppendLine("GROUP FAIL "+className+" after "+(checks-before)+" successful checks; elapsedMs="+groupWatch.ElapsedMilliseconds+"\r\n"+SummaryError(error));
            CleanupUnshownWindows();
        } finally { completedGroups++;SaveReport(); }
    }
    [STAThread]
    static int Main(string[] args) {
        try {
            if (args.Length < 1 || !Path.IsPathRooted(args[0]))
                throw new ArgumentException("A unique absolute sandbox directory is required.");
            sandbox = Path.GetFullPath(args[0]);
            if (!Directory.Exists(sandbox) || !File.Exists(Path.Combine(sandbox, ".capture-background-sandbox")))
                throw new InvalidOperationException("Refusing to run outside the prepared background-test sandbox.");
            Environment.SetEnvironmentVariable("MORI_DESK_DATA", Path.Combine(sandbox, "data"), EnvironmentVariableTarget.Process);
            // QINGJIE_DATA_DIR is not a supported Nook setting. Set the actual
            // per-component test paths before AppState initializes its settings.
            Preferences.TestFilePath = Path.Combine(sandbox, "data", "capture", "settings.json");
            OcrLanguagePacks.TestFolder = Path.Combine(sandbox, "data", "languages");
            OfflinePacks.TestFolder = Path.Combine(sandbox, "data", "offline");
            AppState.Settings = new Preferences();
            var app = Application.Current ?? new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            bool only=args.Length>1&&args[1]=="--only";
            var selected=args.Skip(only?2:1).ToArray();
            if(only&&(selected.Length==0||selected.Any(name=>!DefaultGroups.Contains(name,StringComparer.Ordinal))))
                throw new ArgumentException("--only requires one or more reviewed groups compiled into this runner.");
            var groups = only?new List<string>(selected):new List<string>(DefaultGroups);
            if(!only)groups.AddRange(selected);
            groupSummary.AppendLine("Mode="+(only?"isolated explicit groups":"full default groups")+"; requestedGroups="+string.Join(", ",groups.Distinct(StringComparer.Ordinal)));
            foreach (var group in groups.Distinct(StringComparer.Ordinal)) Run(group);
            try {
                Check(AppState.Captures.Count == 0 && AppState.Hotkeys == null && AppState.Tray == null,
                    "background runner never starts desktop capture, registered hotkeys or a tray service");
            } catch (Exception error) { failedGroups++; Log("FINAL SAFETY FAIL\r\n" + error);groupSummary.AppendLine("FINAL SAFETY FAIL\r\n"+SummaryError(error)); }
            Log((failedGroups == 0 ? "PASS " : "FAIL ") + checks + " successful checks; " + failedGroups + " failed groups/safety checks\r\nGroups: " + string.Join(", ", groups.Distinct(StringComparer.Ordinal)) + "\r\nSandbox: " + sandbox);
            completed=true;SaveReport();
            return failedGroups == 0 ? 0 : 1;
        } catch (Exception error) {
            Console.Error.WriteLine(error);
            Log("RUNNER FAIL after " + checks + " successful checks\r\n" + error);
            groupSummary.AppendLine("RUNNER FAIL\r\n"+SummaryError(error));
            SaveReport();
            return 1;
        } finally {
            // This is the isolated runner process, never the installed Nook process.
            CleanupUnshownWindows();
            SaveReport();
        }
    }
}
#endif
