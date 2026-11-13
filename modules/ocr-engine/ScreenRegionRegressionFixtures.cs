using System.Text.Json;
using SkiaSharp;

internal static class ScreenRegionRegressionFixtures
{
    internal static void Run()
    {
        var checks = new List<object>(); int failed = 0;
        void Check(string name, bool passed)
        {
            checks.Add(new { name, passed }); if (!passed) failed++;
        }
        using var bitmap = new SKBitmap(400, 240, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var full = new SKRectI(0, 0, bitmap.Width, bitmap.Height);
        SKRectI? Plan() => ScreenOcrRegionPlan.ContentRegion(bitmap, full, CancellationToken.None);

        bitmap.Erase(SKColors.White);
        Check("exact opaque blank white is skipped", Plan() == null);
        bitmap.Erase(SKColors.Black);
        Check("exact opaque blank dark is skipped", Plan() == null);
        bitmap.Erase(SKColors.White);
        bitmap.SetPixel(200, 120, new SKColor(254, 254, 254));
        var faint = Plan();
        Check("one-level faint single pixel remains with context", faint.HasValue && faint.Value.Contains(200, 120) && faint.Value.Width >= 65 && faint.Value.Height >= 65);
        Check("planning never changes source pixels", bitmap.GetPixel(200, 120) == new SKColor(254, 254, 254) && bitmap.GetPixel(0, 0) == SKColors.White);
        bitmap.Erase(SKColors.White);
        bitmap.SetPixel(399, 120, SKColors.Black);
        Check("right-edge ink remains", Plan() is { } right && right.Right == 400 && right.Contains(399, 120));
        bitmap.Erase(SKColors.White);
        bitmap.SetPixel(0, 120, SKColors.Black);
        Check("left-edge ink remains", Plan() is { } left && left.Left == 0 && left.Contains(0, 120));
        bitmap.Erase(SKColors.White);
        bitmap.SetPixel(200, 0, SKColors.Black);
        Check("top-edge ink remains", Plan() is { } top && top.Top == 0 && top.Contains(200, 0));
        bitmap.Erase(SKColors.White);
        bitmap.SetPixel(200, 239, SKColors.Black);
        Check("bottom-edge ink remains", Plan() is { } bottom && bottom.Bottom == 240 && bottom.Contains(200, 239));
        bitmap.Erase(SKColors.White);
        bitmap.SetPixel(0, 0, SKColors.Black);
        Check("disagreeing corner keeps original region", Plan() == full);

        bitmap.Erase(SKColors.White);
        bitmap.SetPixel(130, 90, SKColors.Black);
        bitmap.SetPixel(5, 5, SKColors.Red);
        var offset = new SKRectI(50, 30, 350, 210);
        var cropped = ScreenOcrRegionPlan.ContentRegion(bitmap, offset, CancellationToken.None);
        Check("offset region uses source coordinates and ignores outside pixels", cropped == new SKRectI(98, 58, 163, 123));

        using var transparent = new SKBitmap(400, 240, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        transparent.Erase(SKColors.Transparent);
        Check("transparent pixels retain original composition path", ScreenOcrRegionPlan.ContentRegion(transparent, full, CancellationToken.None) == full);
        using var rgb = new SKBitmap(400, 240, SKColorType.Rgb565, SKAlphaType.Opaque);
        rgb.Erase(SKColors.White);
        Check("unsupported pixel format retains original path", ScreenOcrRegionPlan.ContentRegion(rgb, full, CancellationToken.None) == full);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool cancelledCorrectly = false;
        try { ScreenOcrRegionPlan.ContentRegion(bitmap, full, cancelled.Token); }
        catch (OperationCanceledException) { cancelledCorrectly = true; }
        Check("cancelled request does not scan or infer", cancelledCorrectly);

        var random = new Random(71319); bool allPixelsPreserved = true;
        for (int iteration = 0; iteration < 40; iteration++)
        {
            bitmap.Erase(SKColors.White); var points = new List<SKPointI>();
            for (int n = 0; n < 25; n++)
            {
                var point = new SKPointI(random.Next(1, 399), random.Next(1, 239)); points.Add(point);
                bitmap.SetPixel(point.X, point.Y, new SKColor((byte)random.Next(255), 254, 253));
            }
            var plan = Plan();
            allPixelsPreserved &= plan.HasValue && points.All(p => plan.Value.Contains(p.X, p.Y));
        }
        Check("all nonbackground pixels survive randomized sparse/dense positions", allPixelsPreserved);

        var fixtures = new List<object>();
        foreach (string name in new[] { "short-card", "craft", "lexicon-pipeline", "mixed-size-bilingual" })
        {
            string path = Path.Combine(AppContext.BaseDirectory, "fixtures", name + ".png");
            if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, "..", "fixtures", name + ".png");
            if (!File.Exists(path)) throw new FileNotFoundException("Diagnostic fixture required", path);
            using var source = SKBitmap.Decode(path);
            long originalPixels = 0, plannedPixels = 0; int tiles = 0, skipped = 0;
            foreach (var core in ScreenOcr.Tiles(source.Width, source.Height))
            {
                var region = new SKRectI(Math.Max(0, core.Left - 64), Math.Max(0, core.Top - 64),
                    Math.Min(source.Width, core.Right + 64), Math.Min(source.Height, core.Bottom + 64));
                originalPixels += (long)region.Width * region.Height * 4; tiles++;
                var plan = ScreenOcrRegionPlan.ContentRegion(source, region, CancellationToken.None);
                if (plan == null) skipped++;
                else plannedPixels += (long)plan.Value.Width * plan.Value.Height * 4;
            }
            fixtures.Add(new { name, tiles, skipped, originalDetectorPixels = originalPixels, plannedDetectorPixels = plannedPixels, retainedScale = 2 });
        }
        File.WriteAllText("region-fixtures.json", JsonSerializer.Serialize(new { passed = checks.Count - failed, failed, checks, fixtures }));
        if (failed != 0) throw new InvalidOperationException("Region regression fixtures failed.");
    }
}
