using System;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;

// Synthetic pixels only: no screen capture, input, windows, files or services.
static class ScrollCaptureStitcherTests {
#if SCROLL_CAPTURE_TEST_RUNNER
    [STAThread]
    static int Main() {
        int checks = 0;
        try {
            Run((condition, message) => { if (!condition) throw new InvalidOperationException(message); checks++; Console.WriteLine("PASS " + message); });
            Console.WriteLine("PASS " + checks + " synthetic-only scroll stitch checks"); return 0;
        } catch (Exception error) { Console.Error.WriteLine("FAIL after " + checks + " checks: " + error); return 1; }
    }
#endif
    static byte[] Page(int width, int height, bool text) {
        var result = new byte[width * height * 4];
        var random = new Random(90717);
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) {
            int p = (y * width + x) * 4;
            if (!text) {
                result[p] = (byte)random.Next(30, 241); result[p + 1] = (byte)random.Next(30, 241); result[p + 2] = (byte)random.Next(30, 241);
            } else result[p] = result[p + 1] = result[p + 2] = 249;
            result[p + 3] = 255;
        }
        if (text) {
            for (int y = 7; y + 8 < height; y += 17) {
                int cursor = 7;
                while (cursor + 8 < width - 7) {
                    int word = random.Next(2, 7);
                    for (int letter = 0; letter < word && cursor + 6 < width - 7; letter++, cursor += 7) {
                        int mask = random.Next(1, 65535);
                        for (int dy = 0; dy < 8; dy++) for (int dx = 0; dx < 5; dx++) {
                            if (((mask >> ((dy * 5 + dx) % 16)) & 1) == 0) continue;
                            int p = ((y + dy) * width + cursor + dx) * 4;
                            result[p] = result[p + 1] = result[p + 2] = (byte)(20 + (y % 80));
                        }
                    }
                    cursor += random.Next(6, 15);
                }
            }
        }
        return result;
    }
    static BitmapSource Bitmap(byte[] pixels, int width, int height) {
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        result.Freeze(); return result;
    }
    static BitmapSource Frame(byte[] page, int width, int height, int offset, int top = 0, int bottom = 0, int noise = 0) {
        var pixels = new byte[width * height * 4];
        Buffer.BlockCopy(page, offset * width * 4, pixels, top * width * 4, (height - top - bottom) * width * 4);
        for (int y = 0; y < top; y++) for (int x = 0; x < width; x++) {
            int p = (y * width + x) * 4;
            pixels[p] = (byte)((x * 37 + y * 19) % 255); pixels[p + 1] = 31; pixels[p + 2] = 87; pixels[p + 3] = 255;
        }
        for (int y = height - bottom; y < height; y++) for (int x = 0; x < width; x++) {
            int p = (y * width + x) * 4;
            pixels[p] = 48; pixels[p + 1] = (byte)((x * 19 + y * 11) % 255); pixels[p + 2] = 210; pixels[p + 3] = 255;
        }
        if (noise > 0) for (int i = 0; i < pixels.Length; i += 4)
            for (int c = 0; c < 3; c++) pixels[i + c] = (byte)Math.Max(0, Math.Min(255, pixels[i + c] + ((i / 4 + c) % (noise * 2 + 1)) - noise));
        return Bitmap(pixels, width, height);
    }
    static byte[] Bytes(BitmapSource image) {
        var result = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(result, image.PixelWidth * 4, 0); return result;
    }
    static bool Equal(byte[] a, byte[] b) {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
    static byte[] Slice(byte[] page, int width, int rows) {
        var result = new byte[width * rows * 4]; Buffer.BlockCopy(page, 0, result, 0, result.Length); return result;
    }
    internal static void Run(Action<bool, string> check) {
        const int width = 240, frameHeight = 180;
        foreach (bool text in new[] { false, true }) {
            var page = Page(width, 1600, text);
            using (var stitch = new ScrollCaptureStitcher(Frame(page, width, frameHeight, 0))) {
                check(stitch.Width == width && stitch.Height == frameHeight && stitch.FrameCount == 1, "scroll stitch initializes one synthetic " + (text ? "text" : "texture") + " frame");
                int position = 0;
                foreach (int shift in new[] { 57, 83, 29, 121, 66 }) {
                    position += shift;
                    var result = stitch.TryAppend(Frame(page, width, frameHeight, position));
                    check(result.Status == ScrollCaptureAppendStatus.Added && result.AddedHeight == shift && result.TotalHeight == frameHeight + position,
                        "unique downward " + shift + "px displacement adds exact rows for " + (text ? "text" : "texture"));
                }
                var rendered = stitch.Render();
                check(rendered.IsFrozen && rendered.PixelWidth == width && rendered.PixelHeight == frameHeight + position,
                    "scroll result is frozen with exact cumulative dimensions");
                check(Equal(Bytes(rendered), Slice(page, width, frameHeight + position)), "scroll result retains every synthetic source pixel without seams, duplicated overlap or missing rows");
                var duplicate = stitch.TryAppend(Frame(page, width, frameHeight, position));
                check(duplicate.Status == ScrollCaptureAppendStatus.Duplicate && duplicate.AddedHeight == 0 && stitch.FrameCount == 6,
                    "duplicate frame is a no-op and does not consume frame budget");
            }
            foreach (int shift in new[] { 1, 2, 143, 144 }) using (var stitch = new ScrollCaptureStitcher(Frame(page, width, frameHeight, 0))) {
                var result = stitch.TryAppend(Frame(page, width, frameHeight, shift));
                check(result.Status == ScrollCaptureAppendStatus.Added && result.AddedHeight == shift && Equal(Bytes(stitch.Render()), Slice(page, width, frameHeight + shift)),
                    "smallest and maximum-safe " + shift + "px displacement preserves exact " + (text ? "text" : "texture") + " pixels");
            }
        }
        var texture = Page(width, 1800, false);
        using (var stitch = new ScrollCaptureStitcher(Frame(texture, width, frameHeight, 0))) {
            var result = stitch.TryAppend(Frame(texture, width, frameHeight, 76, 0, 0, 2));
            check(result.Status == ScrollCaptureAppendStatus.Added && result.AddedHeight == 76, "bounded two-level RGB capture noise preserves exact alignment");
            int oldHeight = stitch.Height, oldCount = stitch.FrameCount; var oldPixels = Bytes(stitch.Render());
            using (var cancel = new CancellationTokenSource()) {
                cancel.Cancel(); var cancelled = stitch.TryAppend(Frame(texture, width, frameHeight, 130), cancel.Token);
                check(cancelled.Status == ScrollCaptureAppendStatus.Cancelled && stitch.Height == oldHeight && stitch.FrameCount == oldCount && Equal(oldPixels, Bytes(stitch.Render())), "cancelled append leaves counters and every committed output pixel unchanged");
            }
            var invalid = stitch.TryAppend(Bitmap(Page(width + 1, frameHeight, false), width + 1, frameHeight));
            check(invalid.Status == ScrollCaptureAppendStatus.InvalidFrame && stitch.Height == oldHeight && Equal(oldPixels, Bytes(stitch.Render())), "changed capture dimensions are rejected transactionally");
            var tooFar = stitch.TryAppend(Frame(texture, width, frameHeight, 600));
            check(tooFar.Status == ScrollCaptureAppendStatus.NoReliableOverlap && Equal(oldPixels, Bytes(stitch.Render())), "large jump without overlap never silently produces a guessed composite");
            var reverse = stitch.TryAppend(Frame(texture, width, frameHeight, 20));
            check(reverse.Status == ScrollCaptureAppendStatus.NoReliableOverlap && stitch.Height == oldHeight, "upward movement is clearly refused by downward-only capture");
            var shifted = Bytes(Frame(texture, width, frameHeight, 130));
            for (int y = 0; y < frameHeight; y++) Buffer.BlockCopy(shifted, (y * width + 1) * 4, shifted, y * width * 4, (width - 1) * 4);
            check(stitch.TryAppend(Bitmap(shifted, width, frameHeight)).Status == ScrollCaptureAppendStatus.NoReliableOverlap, "one-pixel horizontal drift is refused instead of smearing small text");
            var animation = Bytes(Frame(texture, width, frameHeight, 130));
            for (int y = 15; y < 90; y++) for (int x = 10; x < 210; x++) { int p = (y * width + x) * 4; animation[p] ^= 255; animation[p + 1] ^= 255; }
            check(stitch.TryAppend(Bitmap(animation, width, frameHeight)).Status == ScrollCaptureAppendStatus.NoReliableOverlap,
                "large animation or changed content in overlap does not claim successful stitching");
            var recovered = stitch.TryAppend(Frame(texture, width, frameHeight, 130));
            check(recovered.Status == ScrollCaptureAppendStatus.Added && recovered.AddedHeight == 54 && stitch.FrameCount == oldCount + 1,
                "a rejected capture never corrupts the previous-frame anchor and a later valid frame still appends");
        }
        using (var stitch = new ScrollCaptureStitcher(Frame(texture, width, frameHeight, 0, 17, 13))) {
            var result = stitch.TryAppend(Frame(texture, width, frameHeight, 73, 17, 13));
            var result2 = stitch.TryAppend(Frame(texture, width, frameHeight, 137, 17, 13));
            check(result.Status == ScrollCaptureAppendStatus.Added && result.AddedHeight == 73 && result2.Status == ScrollCaptureAppendStatus.Added && result2.AddedHeight == 64,
                "pixel-stable textured header and footer are detected conservatively across multiple frames");
            var image = Bytes(stitch.Render());
            var expected = new byte[width * (frameHeight + 137) * 4];
            var initial = Bytes(Frame(texture, width, frameHeight, 0, 17, 13));
            Buffer.BlockCopy(initial, 0, expected, 0, 17 * width * 4);
            Buffer.BlockCopy(texture, 0, expected, 17 * width * 4, (frameHeight - 17 - 13 + 137) * width * 4);
            Buffer.BlockCopy(initial, (frameHeight - 13) * width * 4, expected, expected.Length - 13 * width * 4, 13 * width * 4);
            check(Equal(image, expected), "fixed header and footer appear exactly once around all document content");
            var changedHeader = Bytes(Frame(texture, width, frameHeight, 190, 17, 13)); changedHeader[0] ^= 127;
            check(stitch.TryAppend(Bitmap(changedHeader, width, frameHeight)).Status == ScrollCaptureAppendStatus.NoReliableOverlap,
                "changing a committed fixed band is rejected without concealing missing information");
        }
        foreach (var limits in new[] {
            new ScrollCaptureLimits { MaxHeight = frameHeight + 40 },
            new ScrollCaptureLimits { MaxPixels = (long)width * (frameHeight + 40) },
            new ScrollCaptureLimits { MaxFrames = 1 }
        }) using (var stitch = new ScrollCaptureStitcher(Frame(texture, width, frameHeight, 0), limits)) {
            var before = Bytes(stitch.Render()); var result = stitch.TryAppend(Frame(texture, width, frameHeight, 75));
            check(result.Status == ScrollCaptureAppendStatus.LimitReached && result.AddedHeight == 0 && stitch.FrameCount == 1 && Equal(before, Bytes(stitch.Render())),
                "height, total-pixel and frame budgets reject before any output mutation");
        }
        var blank = new byte[width * frameHeight * 4];
        for (int i = 0; i < blank.Length; i++) blank[i] = 255;
        using (var stitch = new ScrollCaptureStitcher(Bitmap(blank, width, frameHeight))) {
            var changed = (byte[])blank.Clone();
            for (int y = 50; y < 90; y++) for (int x = 20; x < 60; x++) changed[(y * width + x) * 4] = 0;
            check(stitch.TryAppend(Bitmap(changed, width, frameHeight)).Status == ScrollCaptureAppendStatus.NoReliableOverlap,
                "untextured background cannot serve as false evidence for a displacement");
        }
        var periodic = new byte[width * 900 * 4];
        for (int y = 0; y < 900; y++) for (int x = 0; x < width; x++) {
            int p = (y * width + x) * 4; periodic[p] = (byte)((y % 12) * 20); periodic[p + 1] = (byte)((x % 7) * 30); periodic[p + 2] = 100; periodic[p + 3] = 255;
        }
        using (var stitch = new ScrollCaptureStitcher(Frame(periodic, width, frameHeight, 0)))
            check(stitch.TryAppend(Frame(periodic, width, frameHeight, 7)).Status == ScrollCaptureAppendStatus.NoReliableOverlap,
                "ambiguous repeated row patterns are refused even when several offsets match perfectly");
        var immutableBudget = new ScrollCaptureLimits { MaxFrames = 1 };
        using (var stitch = new ScrollCaptureStitcher(Frame(texture, width, frameHeight, 0), immutableBudget)) {
            immutableBudget.MaxFrames = 100;
            check(stitch.TryAppend(Frame(texture, width, frameHeight, 80)).Status == ScrollCaptureAppendStatus.LimitReached,
                "caller changes cannot silently enlarge an established session memory or frame budget");
            check(stitch.TryAppend(Frame(texture, width, frameHeight, 0, 0, 0, 2)).Status == ScrollCaptureAppendStatus.Duplicate && stitch.FrameCount == 1,
                "bounded noise on an unmoved frame remains a duplicate even at the session limit");
            check(stitch.TryAppend(null).Status == ScrollCaptureAppendStatus.InvalidFrame && stitch.FrameCount == 1,
                "missing frame is a safe visible error rather than an exception or a cleared session");
        }
        bool initialLimit = false, invalidBudget = false;
        try { using (new ScrollCaptureStitcher(Frame(texture, width, frameHeight, 0), new ScrollCaptureLimits { MaxPixels = 100 })) { } }
        catch (ArgumentException) { initialLimit = true; }
        try { using (new ScrollCaptureStitcher(Frame(texture, width, frameHeight, 0), new ScrollCaptureLimits { MaxFrames = 0 })) { } }
        catch (ArgumentOutOfRangeException) { invalidBudget = true; }
        check(initialLimit && invalidBudget, "oversized first image and invalid limits are rejected before retaining pixel data");
        var largePage = Page(1440, 2400, true);
        using (var stitch = new ScrollCaptureStitcher(Frame(largePage, 1440, 1080, 0))) {
            var result = stitch.TryAppend(Frame(largePage, 1440, 1080, 467));
            check(result.Status == ScrollCaptureAppendStatus.Added && result.AddedHeight == 467 && Equal(Bytes(stitch.Render()), Slice(largePage, 1440, 1547)),
                "desktop-sized dense text is aligned using bounded sampling but rendered without downsampling any pixel");
        }
        bool disposedThrows = false; var disposed = new ScrollCaptureStitcher(Frame(texture, width, frameHeight, 0)); disposed.Dispose(); disposed.Dispose();
        try { disposed.Render(); } catch (ObjectDisposedException) { disposedThrows = true; }
        check(disposedThrows, "disposing the stitcher releases retained frame references and refuses reuse");
    }
}
