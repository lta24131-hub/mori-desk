using System.Runtime.InteropServices;
using SkiaSharp;

internal static class ScreenOcrRegionPlan
{
    internal const int ContextPixels = 32;

    // Remove only exact opaque background pixels. No sampled/contrast threshold
    // may classify faint punctuation or a tiny isolated label as empty.
    internal static SKRectI? ContentRegion(SKBitmap source, SKRectI region, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        if (region.Left < 0 || region.Top < 0 || region.Right > source.Width || region.Bottom > source.Height ||
            region.Width <= 0 || region.Height <= 0) throw new ArgumentOutOfRangeException(nameof(region));
        if (source.ColorType is not (SKColorType.Bgra8888 or SKColorType.Rgba8888) || source.RowBytes % 4 != 0)
            return region;

        var pixels = MemoryMarshal.Cast<byte, uint>(source.GetPixelSpan());
        int stride = source.RowBytes / 4;
        uint background = pixels[region.Top * stride + region.Left];
        // Disagreeing corners, transparency and textured edges use the original
        // detector input. The optimization never changes how they are composited.
        if ((background >> 24) != 255 ||
            pixels[region.Top * stride + region.Right - 1] != background ||
            pixels[(region.Bottom - 1) * stride + region.Left] != background ||
            pixels[(region.Bottom - 1) * stride + region.Right - 1] != background) return region;

        int left = region.Right, right = region.Left, top = region.Bottom, bottom = region.Top;
        for (int y = region.Top; y < region.Bottom; y++)
        {
            if ((y & 31) == 0) cancel.ThrowIfCancellationRequested();
            var row = pixels.Slice(y * stride, source.Width);
            for (int x = region.Left; x < region.Right; x++)
            {
                if (row[x] == background) continue;
                left = Math.Min(left, x); right = Math.Max(right, x + 1);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y + 1);
            }
        }
        if (left >= right) return null;
        var content = new SKRectI(Math.Max(region.Left, left - ContextPixels), Math.Max(region.Top, top - ContextPixels),
            Math.Min(region.Right, right + ContextPixels), Math.Min(region.Bottom, bottom + ContextPixels));
        // Avoid changing detector context for negligible savings.
        return (long)content.Width * content.Height * 10 >= (long)region.Width * region.Height * 9 ? region : content;
    }
}
