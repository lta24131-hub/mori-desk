using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QingJie;
using Rectangle = System.Drawing.Rectangle;

// Strictly in-memory: pure geometry, generated pixels and an unstarted session.
// No visible/native windows, desktop reads, hooks, hotkeys, services or files.
static class ScrollCaptureSessionTests {
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static object Field(ScrollCaptureSession session, string name) {
        return typeof(ScrollCaptureSession).GetField(name, Hidden).GetValue(session);
    }
    static BitmapSource Pixels(int width, int height) {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) {
            int p = (y * width + x) * 4;
            pixels[p] = (byte)(x % 251); pixels[p + 1] = (byte)(y % 241); pixels[p + 2] = (byte)((x + y * 3) % 239); pixels[p + 3] = 255;
        }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); image.Freeze(); return image;
    }
    static bool Argument(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }
    static int Windows() { return Application.Current == null ? 0 : Application.Current.Windows.Count; }
    static bool Inert(ScrollCaptureSession session) {
        return Field(session, "controls") == null && Field(session, "outline") == null && Field(session, "hotkeySource") == null &&
            Field(session, "mouseHook") == null && (IntPtr)Field(session, "mouseHandle") == IntPtr.Zero &&
            !(bool)Field(session, "hotkeyRegistered") && !(bool)Field(session, "started") && !(bool)Field(session, "busy") &&
            !((DispatcherTimer)Field(session, "timer")).IsEnabled;
    }
    static bool AnyFreeSpace(Rectangle crop, Rectangle[] monitors, int width, int height) {
        foreach (var work in monitors) {
            int w = Math.Min(width, work.Width), h = Math.Min(height, work.Height);
            foreach (int x in new[] { work.Left, work.Right - w, crop.Left - w, crop.Right })
                foreach (int y in new[] { work.Top, work.Bottom - h, crop.Top - h, crop.Bottom }) {
                    var candidate = new Rectangle(x, y, w, h);
                    if (work.Contains(candidate) && !candidate.IntersectsWith(crop)) return true;
                }
        }
        return false;
    }
    internal static void Run(Action<bool, string> check) {
        check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "scroll session background fixture runs on STA without initiating desktop capture");
        Layout(check); Region(check); Lifetime(check);
    }
    static void Layout(Action<bool, string> check) {
        var monitors = new[] { new Rectangle(-2560, -160, 2560, 1400), new Rectangle(0, 0, 1920, 1040), new Rectangle(1920, 280, 1600, 900) };
        foreach (int index in new[] { 0, 1, 2 }) foreach (var physicalSize in new[] { new System.Drawing.Size(450, 76), new System.Drawing.Size(675, 114), new System.Drawing.Size(900, 152) }) {
            var screen = monitors[index]; var crop = new Rectangle(screen.Left + 110, screen.Top + 90, screen.Width - 220, screen.Height - 230);
            var placement = ScrollCaptureLayout.Place(crop, monitors, physicalSize.Width, physicalSize.Height);
            check(monitors.Any(work => work.Contains(placement)) && !placement.IntersectsWith(crop),
                "scroll controls remain inside physical work area and outside crop for monitor " + index + " at " + physicalSize.Width + "px control scale");
            check(placement.Width == physicalSize.Width && placement.Height == physicalSize.Height,
                "mixed-DPI control placement uses supplied physical dimensions without a second scale transform");
        }
        foreach (var crop in monitors) {
            var placement = ScrollCaptureLayout.Place(crop, monitors, 450, 76);
            check(monitors.Any(work => work.Contains(placement)) && !placement.IntersectsWith(crop),
                "full-monitor crop moves controls to another monitor instead of covering the target at " + crop.Left + "," + crop.Top);
        }
        var single = new[] { new Rectangle(-1500, -250, 1500, 1000) };
        var full = ScrollCaptureLayout.Place(single[0], single, 450, 76);
        check(single[0].Contains(full) && full.IntersectsWith(single[0]) && full.Width == 450 && full.Height == 76,
            "single-screen full-crop fallback stays visible in its own negative-origin work area for hide-during-acquisition mode");
        var tiny = new[] { new Rectangle(73, -92, 200, 50) };
        var small = ScrollCaptureLayout.Place(tiny[0], tiny, 450, 76);
        check(tiny[0].Contains(small) && small.Width > 0 && small.Height > 0 && small.Width <= 200 && small.Height <= 50,
            "oversized control request is clamped to a tiny physical working area instead of leaving all buttons off-screen");
        var exactWork = new[] { new Rectangle(0, 0, 800, 600) }; var exactCrop = new Rectangle(0, 0, 800, 524);
        var exact = ScrollCaptureLayout.Place(exactCrop, exactWork, 450, 76);
        check(exactWork[0].Contains(exact) && !exact.IntersectsWith(exactCrop),
            "an exact-height free strip is used without insisting on decorative eight-pixel clearance");
        var random = new Random(73219);
        for (int i = 0; i < 90; i++) {
            var work = monitors[i % monitors.Length]; int x = random.Next(0, work.Width - 32), y = random.Next(0, work.Height - 80);
            var crop = new Rectangle(work.Left + x, work.Top + y, random.Next(32, work.Width - x + 1), random.Next(80, work.Height - y + 1));
            int width = i % 3 == 0 ? 900 : 450, height = i % 3 == 0 ? 152 : 76;
            var placement = ScrollCaptureLayout.Place(crop, monitors, width, height);
            check(monitors.Any(area => area.Contains(placement)) && (!AnyFreeSpace(crop, monitors, width, height) || !placement.IntersectsWith(crop)),
                "seeded mixed-monitor placement prefers every feasible non-overlapping physical location, case " + i);
        }
        check(Argument(() => ScrollCaptureLayout.Place(new Rectangle(), null, 450, 76)) && Argument(() => ScrollCaptureLayout.Place(new Rectangle(), new Rectangle[0], 450, 76)),
            "missing monitor geometry is rejected before a control window could be created");
    }
    static void Region(Action<bool, string> check) {
        var image = Pixels(640, 480); var bounds = new Rectangle(-1600, -300, 640, 480);
        var snapshot = new Snapshot { Bounds = bounds, Image = image };
        foreach (double scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0 }) {
            var selection = new Rect(10.2, 20.3, 120.4, 70.1);
            var crop = CaptureWindow.ScrollRegion(snapshot, selection, scale, scale);
            int left = (int)Math.Floor(selection.Left * scale), top = (int)Math.Floor(selection.Top * scale);
            int right = (int)Math.Ceiling(selection.Right * scale), bottom = (int)Math.Ceiling(selection.Bottom * scale);
            check(crop == new Rectangle(bounds.Left + left, bounds.Top + top, right - left, bottom - top),
                "scroll region rounds outward once and preserves negative desktop origin at " + scale + "x DPI");
            var pixels = new CroppedBitmap(image, new Int32Rect(crop.Left - bounds.Left, crop.Top - bounds.Top, crop.Width, crop.Height));
            var firstPixel = new byte[4]; pixels.CopyPixels(new Int32Rect(0, 0, 1, 1), firstPixel, 4, 0);
            check(firstPixel[0] == left % 251 && firstPixel[1] == top % 241 && firstPixel[2] == (left + top * 3) % 239 && firstPixel[3] == 255,
                "physical scroll crop samples the intended synthetic source pixel rather than primary-monitor or DIP coordinates");
        }
        var separate = CaptureWindow.ScrollRegion(snapshot, new Rect(10.2, 20.3, 120.4, 70.1), 1.5, 2);
        check(separate == new Rectangle(-1585, -260, 181, 141), "non-uniform logical-to-physical scale uses independent X and Y axes");
        var clipped = CaptureWindow.ScrollRegion(snapshot, new Rect(-8, -9, 1000, 900), 1.5, 2);
        check(clipped == bounds, "oversized crop is clipped to frozen image pixels without extending into another monitor");
        check(ReferenceEquals(snapshot.Image, image) && image.IsFrozen && snapshot.Bounds == bounds,
            "scroll region calculation never mutates or replaces the original frozen monitor image");
        check(Argument(() => CaptureWindow.ScrollRegion(null, new Rect(0, 0, 100, 100), 1, 1)) &&
            Argument(() => CaptureWindow.ScrollRegion(new Snapshot { Bounds = bounds }, new Rect(0, 0, 100, 100), 1, 1)) &&
            Argument(() => CaptureWindow.ScrollRegion(snapshot, Rect.Empty, 1, 1)) &&
            Argument(() => CaptureWindow.ScrollRegion(snapshot, new Rect(0, 0, 2.9, 20), 1, 1)) &&
            Argument(() => CaptureWindow.ScrollRegion(snapshot, new Rect(0, 0, 20, 2.9), 1, 1)),
            "missing image and empty or too-thin selections are rejected before rolling session construction");
        foreach (double invalid in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            check(Argument(() => CaptureWindow.ScrollRegion(snapshot, new Rect(0, 0, 100, 100), invalid, 1)) &&
                Argument(() => CaptureWindow.ScrollRegion(snapshot, new Rect(0, 0, 100, 100), 1, invalid)),
                "invalid physical scaling is rejected explicitly on either axis: " + invalid);
    }
    static void Lifetime(Action<bool, string> check) {
        int windows = Windows(), captures = AppState.Captures.Count;
        var screen = new Rectangle(-1500, -250, 1500, 1000); var region = new Rectangle(-1400, -180, 240, 180);
        var image = Pixels(region.Width, region.Height); var expected = new byte[region.Width * region.Height * 4]; image.CopyPixels(expected, region.Width * 4, 0);
        var session = new ScrollCaptureSession(region, screen, image); int notifications = 0; session.Completed += result => notifications++;
        var stitcher = (ScrollCaptureStitcher)Field(session, "stitcher");
        check(!session.IsDisposed && Inert(session) && Windows() == windows && AppState.Captures.Count == captures,
            "constructing an unstarted rolling session creates no native window, observer, hotkey, enabled timer or capture registration");
        var retained = new byte[expected.Length]; stitcher.Render().CopyPixels(retained, region.Width * 4, 0);
        check(stitcher.Width == region.Width && stitcher.Height == region.Height && stitcher.FrameCount == 1 && retained.SequenceEqual(expected),
            "unstarted session retains exactly one original-resolution generated crop without changing pixels");
        session.Dispose(); session.Dispose();
        check(session.IsDisposed && Inert(session) && Field(session, "stitcher") == null && notifications == 0 && Windows() == windows && AppState.Captures.Count == captures,
            "unstarted disposal is idempotent, releases pixels and never manufactures completion, UI or native teardown resources");
        check(((CancellationTokenSource)Field(session, "cancel")).IsCancellationRequested, "disposing an unstarted rolling session marks all future asynchronous work cancelled");
        foreach (var invalid in new[] { new Rectangle(-1600, -180, 240, 180), new Rectangle(-1400, -180, 31, 180), new Rectangle(-1400, -180, 240, 79), new Rectangle(-100, -180, 240, 180) })
            check(Argument(() => { using (var rejected = new ScrollCaptureSession(invalid, screen, image)) { } }) && Windows() == windows && AppState.Captures.Count == captures,
                "invalid or cross-monitor rolling region is refused without acquiring resources: " + invalid);
        check(Argument(() => { using (var rejected = new ScrollCaptureSession(region, screen, null)) { } }) &&
            Argument(() => { using (var rejected = new ScrollCaptureSession(region, screen, Pixels(region.Width - 1, region.Height))) { } }) &&
            Argument(() => { using (var rejected = new ScrollCaptureSession(region, screen, Pixels(region.Width, region.Height - 1))) { } }),
            "first frame must exist and exactly match the physical crop width and height before any native rolling session can start");
        check(Windows() == windows && AppState.Captures.Count == captures, "scroll session geometry and lifecycle tests leave application window and capture membership counts unchanged");
    }
}
