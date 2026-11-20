using System.Text.Json;
using SkiaSharp;

internal static class ScreenRegionRegressionFixtures
{
    sealed class FragmentedPixels(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(7, buffer.Length)]);
    }
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

        bool exactBounds = true;
        var sub = new SKRectI(43, 19, 368, 222);
        for (int i = 0; i < 48; i++)
        {
            bitmap.Erase(SKColors.White);
            bitmap.SetPixel(sub.Left - 1, sub.Top + 15, SKColors.Black);
            int x = random.Next(60, 240), y = random.Next(26, 140);
            int w = random.Next(1, 96), h = random.Next(1, 37);
            bitmap.SetPixel(x, y, SKColors.Black);
            bitmap.SetPixel(x + w - 1, y + h - 1, new SKColor(254, 255, 255));
            var expected = new SKRectI(Math.Max(sub.Left, x - 32), Math.Max(sub.Top, y - 32),
                Math.Min(sub.Right, x + w + 32), Math.Min(sub.Bottom, y + h + 32));
            exactBounds &= ScreenOcrRegionPlan.ContentRegion(bitmap, sub, default) == expected;
        }
        Check("exact bounds of narrow, off-centre and faint end pixels match scalar geometry", exactBounds);

        var blank = new UniformOpaqueScan();
        Check("no input is not a completed blank image", !blank.OnlyBackground);
        blank.Observe([0, 0, 0, 255, 0, 0, 0, 255]); blank.Observe([0, 0, 0, 255]);
        Check("exactly matching opaque strips are blank", blank.OnlyBackground);
        blank.Observe([0, 0, 0, 255, 0, 0, 1, 255]); blank.Observe([0, 0, 0, 255]);
        Check("one faint pixel at a strip's last position permanently requires OCR", !blank.OnlyBackground);
        var alpha = new UniformOpaqueScan(); alpha.Observe([255, 255, 255, 254]);
        Check("uniform translucency must go through normal composition and OCR", !alpha.OnlyBackground);

        var wire = new byte[17 * 35 * 4 + 1];
        for(int i = 0; i < wire.Length - 1; i += 4){wire[i] = 11;wire[i+1] = 22;wire[i+2] = 33;wire[i+3] = 255;}
        wire[^5] = 12;wire[^1] = 0x7b; // one changed pixel, then an out-of-band sentinel
        foreach(int stride in new[]{68,80}){
            using var target = new SKBitmap(new SKImageInfo(17,35,SKColorType.Bgra8888,SKAlphaType.Unpremul),stride);
            target.GetPixelSpan().Fill(0xc7);
            using var reader = new BinaryReader(new FragmentedPixels(wire));
            var scan = PipeWorker.ReadPixels(reader,target);
            var pixels = target.GetPixelSpan();bool equal = true;
            for(int y=0;y<35;y++){
                equal &= pixels.Slice(y*stride,68).SequenceEqual(wire.AsSpan(y*68,68));
                if(stride>68 && y<34) equal &= pixels.Slice(y*stride+68,stride-68).IndexOfAnyExcept((byte)0xc7)<0;
            }
            Check("fragmented transport preserves every byte, final odd strip, stride padding and frame boundary: "+stride,
                equal && !scan.OnlyBackground && reader.ReadByte()==0x7b);
        }
        using(var target=new SKBitmap(17,35,SKColorType.Bgra8888,SKAlphaType.Unpremul))
        using(var truncated=new BinaryReader(new FragmentedPixels(wire[..^2]))){
            bool rejected=false;try{PipeWorker.ReadPixels(truncated,target);}catch(EndOfStreamException){rejected=true;}
            Check("missing final pixel byte cannot yield a partial OCR image",rejected);
        }

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
