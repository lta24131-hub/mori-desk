using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QingJie;

// Opt-in live benchmark in an interactive test desktop. The ordinary test suite
// never captures the screen or contacts a provider through this fixture.
internal static class ScreenshotTranslationTiming {
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int how);
    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint state);
    static T Field<T>(object owner, string name) {
        return (T)owner.GetType().GetField(name, Private).GetValue(owner);
    }

    static bool MarkerPresent(Snapshot shot, Point onScreen, Color marker) {
        int x = (int)Math.Round(onScreen.X) - shot.Bounds.Left;
        int y = (int)Math.Round(onScreen.Y) - shot.Bounds.Top;
        if (x < 0 || x >= shot.Image.PixelWidth || y < 0 || y >= shot.Image.PixelHeight) return false;
        var pixel = new CroppedBitmap(shot.Image, new Int32Rect(x, y, 1, 1));
        var bgra = new FormatConvertedBitmap(pixel, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[4];
        bgra.CopyPixels(bytes, 4, 0);
        return Math.Abs(bytes[0] - marker.B) <= 6 && Math.Abs(bytes[1] - marker.G) <= 6 && Math.Abs(bytes[2] - marker.R) <= 6;
    }


    internal static Task Run(string scenario) {
        return WithDisplay(() => Measure(scenario));
    }
    internal static async Task RunSequence(IEnumerable<string> scenarios) {
        var failures = new List<Exception>();
        foreach (var scenario in scenarios) {
            Console.WriteLine("Capture timing: " + scenario);
            try { await Run(scenario); }
            catch (Exception error) {
                Console.WriteLine(error.Message);
                failures.Add(new InvalidOperationException(scenario, error));
            }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }
    internal static async Task WithDisplay(Func<Task> measure) {
        const uint continuous = 0x80000000, displayRequired = 2;
        uint previous = SetThreadExecutionState(continuous | displayRequired);
        try { await measure(); }
        finally { if (previous != 0) SetThreadExecutionState(previous); }
    }

    static async Task Measure(string scenario) {
        string text;
        string[] accepted;
        switch (scenario) {
            case "menu":
            case "wide-menu":
                text = "New folder"; accepted = new[] {"新建文件夹"}; break;
            case "sentence":
                text = "Open the window.";
                accepted = new[] {"打开窗户","把窗户打开","打开窗口","打开窗子","开窗","开窗户","开窗子"}; break;
            case "dark":
                text = "Turn on the light.";
                accepted = new[] {"开灯","把灯打开","打开灯","打开电灯","把电灯打开","打开灯光","点亮灯"}; break;
            case "grey":
                text = "Close the gate.";
                accepted = new[] {"关门","关上门","关上大门","把门关上","把大门关上","关闭大门","关闭闸门","把闸门关上"}; break;
            case "status-battery":
                text = "The battery is low.";
                accepted = new[] {"电池电量低","电池电量不足","电量不足","电池快没电了","电池电量很低","电池电量已低","电池电量过低"}; break;
            case "status-retry":
                text = "Please try again.";
                accepted = new[] {"请重试","请再试一次","请再试一遍","请再次尝试","请再试下","请重新尝试"}; break;
            case "status-email":
                text = "Check your email.";
                accepted = new[] {"检查你的邮件","查看你的电子邮件","检查你的电子邮件","查看您的电子邮件","检查您的电子邮件","查看你的邮箱","查看您的邮箱","检查你的邮箱","查看您的邮件","查看你的邮件","检查你的电邮","查看电子邮件","请查看您的电子邮件","查收你的邮件","看看你的邮箱"}; break;
            default: throw new ArgumentException("Unknown live capture timing scenario: " + scenario);
        }
        bool dark = scenario == "dark";
        bool grey = scenario == "grey";
        var label = new TextBlock {
            Text = text, FontFamily = new FontFamily(grey ? "Arial" : "Segoe UI"), FontSize = grey ? 20 : 24,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
            Foreground = grey ? new SolidColorBrush(Color.FromRgb(51,51,51)) : dark ? Brushes.White : Brushes.Black
        };
        var seed = Guid.NewGuid().ToByteArray();
        var marker = Color.FromRgb((byte)(120 + seed[0] % 100), (byte)(12 + seed[1] % 80), (byte)(130 + seed[2] % 100));
        var panel = new Border { Background = grey ? new SolidColorBrush(Color.FromRgb(250,250,250)) : dark ? Brushes.Black : Brushes.White, Child = label,
            BorderBrush = new SolidColorBrush(marker), BorderThickness = new Thickness(8) };
        var sample = new Window {
            Title = "Nook translation timing fixture", Content = panel, Width = scenario=="wide-menu"?840:390, Height = scenario=="wide-menu"?160:82,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = 14, Top = 14, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false
        };
        Snapshot shot;
        Rect pixelArea;
        try {
            sample.Show();
            sample.Activate();
            sample.UpdateLayout();
            var origin = panel.PointToScreen(new Point(10, 10));
            var corner = panel.PointToScreen(new Point(panel.ActualWidth - 10, panel.ActualHeight - 10));
            var hwnd = new WindowInteropHelper(sample).Handle;
            ShowWindow(hwnd, 5);
            Native.SetWindowPos(hwnd, Native.Topmost, 0, 0, 0, 0, 0x0043);
            var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
            var frameWait = Stopwatch.StartNew();
            // The VM compositor can still return the previous window after WPF
            // reports Rendering. Only accept a frame bearing this run's markers.
            do {
                await Task.Delay(90);
                shot = Snapshot.Screen(screen);
                if (MarkerPresent(shot, panel.PointToScreen(new Point(4, 4)), marker) &&
                    MarkerPresent(shot, panel.PointToScreen(new Point(panel.ActualWidth-4, panel.ActualHeight-4)), marker))
                    break;
                if (frameWait.ElapsedMilliseconds > 6000)
                    throw new InvalidOperationException("Wake the interactive test display. This capture did not contain both fixture markers.");
            } while (true);
            pixelArea = new Rect(origin, corner);
            pixelArea.Offset(-shot.Bounds.Left, -shot.Bounds.Top);
            if (!new Rect(0, 0, shot.Image.PixelWidth, shot.Image.PixelHeight).Contains(pixelArea))
                throw new InvalidOperationException("Test window crosses display bounds.");
        } finally { sample.Close(); }

        await VerifySelection(shot, pixelArea, text, accepted);
    }

    internal static async Task VerifySelection(Snapshot shot, Rect pixelArea, string text, string[] accepted) {
        var window = new CaptureWindow(shot, null, new CaptureFeatures { Translation = true, SmartSelection = false });
        var surface = Field<ScreenshotSurface>(window, "surface");
        var status = Field<TextBlock>(window, "translationStatus");
        var clock = new Stopwatch();
        long readMs = -1, translationReadyMs = -1;
        string recognized = null;
        var read = window.ReadCaptureOcr;
        window.ReadCaptureOcr = async (image, token) => {
            var page = await read(image, token);
            readMs = clock.ElapsedMilliseconds;
            recognized = page.Text;
            return page;
        };
        var render = window.RenderCaptureTranslation;
        window.RenderCaptureTranslation = (source, regions, texts, token) => {
            translationReadyMs = clock.ElapsedMilliseconds;
            return render(source, regions, texts, token);
        };
        var visible = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        long firstFrame = -1;
        string answer = null;
        EventHandler rendered = (s, e) => {
            if (!clock.IsRunning || firstFrame >= 0) return;
            var values = Field<string[]>(window, "captureTranslatedText");
            if (values == null || values.Length != 1) return;
            var result = Field<ImageTranslationResult>(window, "imageTranslationResult");
            bool placed = result != null && result.UnplacedCount == 0 && surface.ShowTranslation
                && surface.IsVisible && surface.TranslationImage != null;
            bool displayed = status.IsVisible && status.Inlines.OfType<Run>().Any(r => r.Text == values[0]);
            if (!placed && !displayed) return;
            firstFrame = clock.ElapsedMilliseconds;
            answer = values[0];
            visible.TrySetResult(true);
        };
        try {
            window.Show();
            await window.Dispatcher.InvokeAsync(delegate {}, DispatcherPriority.ApplicationIdle);
            surface.Selection = new Rect(pixelArea.X / surface.ScaleX, pixelArea.Y / surface.ScaleY,
                pixelArea.Width / surface.ScaleX, pixelArea.Height / surface.ScaleY);
            typeof(CaptureWindow).GetMethod("PositionTools", Private).Invoke(window, null);
            surface.InvalidateVisual();
            window.UpdateLayout();
            CompositionTarget.Rendering += rendered;
            clock.Start();
            Field<Button>(window, "translateButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            long finishedAt = -1;
            while (!visible.Task.IsCompleted) {
                await Task.WhenAny(visible.Task, Task.Delay(100));
                if (visible.Task.IsCompleted) break;
                if (finishedAt < 0 && Field<CancellationTokenSource>(window,"translationCancel") == null)
                    finishedAt = clock.ElapsedMilliseconds;
                if (clock.ElapsedMilliseconds > 120000 || (finishedAt >= 0 && clock.ElapsedMilliseconds - finishedAt > 3000))
                    throw new TimeoutException("No readable frame. OCR=" + recognized + " status=" + status.Text);
            }
            Console.WriteLine("input={0} OCR={1} ocr_ms={2} first_readable_ms={3} result={4} translation_ready_ms={5}",
                text, recognized, readMs, firstFrame, answer, translationReadyMs);
            var layoutWait=Stopwatch.StartNew();
            while(Field<CancellationTokenSource>(window,"translationCancel")!=null&&layoutWait.ElapsedMilliseconds<12000)
                await Task.Delay(25);
            var complete=Field<ImageTranslationResult>(window,"imageTranslationResult");
            if(complete==null||!string.Equals(complete.Transcript,answer,StringComparison.Ordinal))
                throw new InvalidOperationException("The completed single-line transcript must match the first readable translation.");
            if (recognized != text || !accepted.Contains(answer.Trim().TrimEnd('。','！','.','!'), StringComparer.Ordinal))
                throw new InvalidOperationException("Incorrect transcription or translation; inspect the measured result.");
            if (firstFrame > 2000)
                throw new InvalidOperationException("First readable translation exceeded two seconds.");
        } finally { CompositionTarget.Rendering -= rendered; window.Close(); }
    }
}
