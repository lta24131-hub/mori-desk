#if CAPTURE_BACKGROUND_TESTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using QingJie;

// Deliberately separate from Tests.Main and SuiteTests. No desktop screenshot,
// visible/native window, clipboard, registered hotkey, or production data is used.
internal static class CaptureBackgroundTests {
    static int checks;
    static string sandbox;
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
        Console.WriteLine("PASS " + message);
    }
    static void Run(string className) {
        var type = typeof(CaptureBackgroundTests).Assembly.GetType(className, true);
        var method = type.GetMethod("Run", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Action<bool, string>) }, null);
        if (method == null) throw new InvalidOperationException("Missing Run(Action<bool,string>) on " + className);
        AssertUnshownWindows();
        Console.WriteLine("GROUP " + className);
        try { method.Invoke(null, new object[] { (Action<bool, string>)Check }); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        AssertUnshownWindows();
        Check(Mouse.Captured == null, className + " leaves no WPF mouse capture");
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
            var groups = new List<string> { "CaptureVisualRegionTests", "CaptureHoverTests", "CaptureTextEditingTests" };
            groups.AddRange(args.Skip(1));
            foreach (var group in groups.Distinct(StringComparer.Ordinal)) Run(group);
            Check(AppState.Captures.Count == 0 && AppState.Hotkeys == null && AppState.Tray == null,
                "background runner never starts desktop capture, registered hotkeys or a tray service");
            string summary = "PASS " + checks + " checks\r\nGroups: " + string.Join(", ", groups) + "\r\nSandbox: " + sandbox;
            File.WriteAllText(Path.Combine(sandbox, "background-tests.txt"), summary);
            Console.WriteLine(summary);
            return 0;
        } catch (Exception error) {
            Console.Error.WriteLine(error);
            if (sandbox != null) File.WriteAllText(Path.Combine(sandbox, "background-tests.txt"), "FAIL after " + checks + " checks\r\n" + error);
            return 1;
        } finally {
            // This is the isolated runner process, never the installed Nook process.
            if (Application.Current != null)
                foreach (Window window in Application.Current.Windows.Cast<Window>().ToArray()) window.Close();
        }
    }
}
#endif
