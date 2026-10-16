using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QingJie {
    internal enum ScrollCaptureAppendStatus { Added, Duplicate, NoReliableOverlap, InvalidFrame, LimitReached, Cancelled }

    internal sealed class ScrollCaptureAppendResult {
        internal ScrollCaptureAppendStatus Status { get; private set; }
        internal int AddedHeight { get; private set; }
        internal int TotalHeight { get; private set; }
        internal string Message { get; private set; }
        internal ScrollCaptureAppendResult(ScrollCaptureAppendStatus status, int added, int total, string message) {
            Status = status; AddedHeight = added; TotalHeight = total; Message = message;
        }
    }

    internal sealed class ScrollCaptureLimits {
        internal long MaxPixels = 64000000;
        internal int MaxHeight = 32760;
        internal int MaxFrames = 150;
    }

    // Pure, offline, downward-only stitcher. No screen, input, timers, files or UI.
    // A rejected/cancelled append leaves all committed pixels and counters intact.
    // Matching deliberately requires textured evidence and a unique displacement;
    // repeated/blank pages are refused rather than guessed. Fixed edge bands are
    // removed only when they remain pixel-identical and contain actual texture.
    internal sealed class ScrollCaptureStitcher : IDisposable {
        sealed class Strip {
            internal byte[] Pixels;
            internal int Height;
            internal Strip(byte[] pixels, int height) { Pixels = pixels; Height = height; }
        }
        struct Score {
            internal double Error;
            internal int Evidence;
            internal int Rows;
        }
        readonly List<Strip> strips = new List<Strip>();
        readonly ScrollCaptureLimits limits;
        readonly int width, frameHeight, stride;
        byte[] first, previous;
        int height, frameCount, fixedTop, fixedBottom;
        bool boundsLocked, disposed;
        internal int Width { get { return width; } }
        internal int Height { get { return height; } }
        internal int FrameCount { get { return frameCount; } }

        internal ScrollCaptureStitcher(BitmapSource frame, ScrollCaptureLimits limits = null) {
            if (frame == null) throw new ArgumentNullException("frame");
            var requested = limits ?? new ScrollCaptureLimits();
            this.limits = new ScrollCaptureLimits { MaxPixels = requested.MaxPixels, MaxHeight = requested.MaxHeight, MaxFrames = requested.MaxFrames };
            if (this.limits.MaxPixels < 1 || this.limits.MaxHeight < 1 || this.limits.MaxFrames < 1)
                throw new ArgumentOutOfRangeException("limits");
            width = frame.PixelWidth; frameHeight = frame.PixelHeight;
            if (width < 16 || frameHeight < 48 || width > int.MaxValue / 4 ||
                (long)width * frameHeight > this.limits.MaxPixels || frameHeight > this.limits.MaxHeight ||
                (long)width * frameHeight * 4 > int.MaxValue)
                throw new ArgumentException("截图区域过小或超过长截图大小限制。", "frame");
            stride = width * 4;
            first = Pixels(frame); previous = first; height = frameHeight; frameCount = 1;
        }

        byte[] Pixels(BitmapSource source) {
            BitmapSource converted = source;
            if (source.Format != PixelFormats.Bgra32) {
                var conversion = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                conversion.Freeze(); converted = conversion;
            }
            var pixels = new byte[checked(stride * frameHeight)];
            converted.CopyPixels(pixels, stride, 0);
            return pixels;
        }
        ScrollCaptureAppendResult Result(ScrollCaptureAppendStatus status, string message, int added = 0) {
            return new ScrollCaptureAppendResult(status, added, height, message);
        }
        void Alive() { if (disposed) throw new ObjectDisposedException("ScrollCaptureStitcher"); }

        internal ScrollCaptureAppendResult TryAppend(BitmapSource frame, CancellationToken token = default(CancellationToken)) {
            Alive();
            if (token.IsCancellationRequested) return Result(ScrollCaptureAppendStatus.Cancelled, "已取消，本次画面未加入。");
            if (frame == null || frame.PixelWidth != width || frame.PixelHeight != frameHeight)
                return Result(ScrollCaptureAppendStatus.InvalidFrame, "截图区域尺寸改变，请重新开始滚动截屏。");
            try {
                var next = Pixels(frame);
                token.ThrowIfCancellationRequested();
                if (SameFrame(previous, next, token)) return Result(ScrollCaptureAppendStatus.Duplicate, "画面没有向下滚动。");
                int top = fixedTop, bottom = fixedBottom;
                if (!boundsLocked) FindFixedEdges(previous, next, out top, out bottom, token);
                else if (!FixedEdgesStillMatch(previous, next, top, bottom, token))
                    return Result(ScrollCaptureAppendStatus.NoReliableOverlap, "固定工具栏或页面布局已变化，请重新开始。");
                int contentHeight = frameHeight - top - bottom;
                int minimumOverlap = Math.Max(24, contentHeight / 5);
                int largestShift = contentHeight - minimumOverlap;
                int bestShift = 0; double bestError = double.MaxValue, otherError = double.MaxValue;
                for (int shift = 1; shift <= largestShift; shift++) {
                    if ((shift & 15) == 0) token.ThrowIfCancellationRequested();
                    Score score = Measure(previous, next, top, frameHeight - bottom, shift, 48, token);
                    if (score.Evidence < 32 || score.Rows < 5) continue;
                    if (score.Error < bestError) { otherError = bestError; bestError = score.Error; bestShift = shift; }
                    else if (score.Error < otherError) otherError = score.Error;
                }
                // Adjacent offsets are not interchangeable: accepting an uncertain
                // one-pixel shift repeatedly accumulates visible seams in text.
                if (bestShift == 0 || bestError > 5 || otherError - bestError < 1.5 ||
                    !Validate(previous, next, top, frameHeight - bottom, bestShift, token))
                    return Result(ScrollCaptureAppendStatus.NoReliableOverlap,
                        "无法可靠拼接。请只向下滚动约半屏，保持缩放不变，并避开动画或固定工具栏。");
                if (frameCount >= limits.MaxFrames || (long)height + bestShift > limits.MaxHeight ||
                    (long)width * ((long)height + bestShift) > limits.MaxPixels)
                    return Result(ScrollCaptureAppendStatus.LimitReached, "已达到长截图上限，请完成当前截图后分段继续。");
                var added = new byte[checked(stride * bestShift)];
                Buffer.BlockCopy(next, (frameHeight - bottom - bestShift) * stride, added, 0, added.Length);
                token.ThrowIfCancellationRequested();
                // All work and cancellation checks precede the small atomic commit.
                strips.Add(new Strip(added, bestShift)); previous = next;
                fixedTop = top; fixedBottom = bottom; boundsLocked = true;
                height += bestShift; frameCount++;
                return Result(ScrollCaptureAppendStatus.Added, "已加入 " + bestShift + " 像素。", bestShift);
            } catch (OperationCanceledException) {
                return Result(ScrollCaptureAppendStatus.Cancelled, "已取消，本次画面未加入。");
            }
        }

        bool SameFrame(byte[] a, byte[] b, CancellationToken token) {
            // Exact or bounded per-channel capture noise, not a low average error
            // that could silently classify a small scroll as a duplicate.
            for (int i = 0; i < a.Length; i += 4) {
                if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
                if (Math.Abs(a[i] - b[i]) > 4 || Math.Abs(a[i + 1] - b[i + 1]) > 4 || Math.Abs(a[i + 2] - b[i + 2]) > 4 || a[i + 3] != b[i + 3]) return false;
            }
            return true;
        }
        int Distance(byte[] a, int pa, byte[] b, int pb) {
            return (Math.Abs(a[pa] - b[pb]) + Math.Abs(a[pa + 1] - b[pb + 1]) + Math.Abs(a[pa + 2] - b[pb + 2])) / 3;
        }
        bool Textured(byte[] pixels, int p, int y) {
            int x = (p % stride) / 4;
            if (x > 2 && Distance(pixels, p, pixels, p - 12) > 14) return true;
            if (x + 3 < width && Distance(pixels, p, pixels, p + 12) > 14) return true;
            if (y > 1 && Distance(pixels, p, pixels, p - stride * 2) > 14) return true;
            return y + 2 < frameHeight && Distance(pixels, p, pixels, p + stride * 2) > 14;
        }
        Score Measure(byte[] a, byte[] b, int top, int end, int shift, int grid, CancellationToken token) {
            int overlap = end - top - shift;
            int stepY = Math.Max(1, overlap / grid), stepX = Math.Max(1, width / grid);
            double error = 0; int evidence = 0, rows = 0;
            for (int y = top + 2; y < end - shift - 2; y += stepY) {
                token.ThrowIfCancellationRequested(); int rowEvidence = 0;
                for (int x = 2; x < width - 2; x += stepX) {
                    int pa = (y + shift) * stride + x * 4, pb = y * stride + x * 4;
                    if (Textured(b, pb, y) || Textured(a, pa, y + shift)) {
                        error += Distance(a, pa, b, pb); evidence++; rowEvidence++;
                    }
                }
                if (rowEvidence > 1) rows++;
            }
            return new Score { Error = evidence == 0 ? double.MaxValue : error / evidence, Evidence = evidence, Rows = rows };
        }
        bool Validate(byte[] a, byte[] b, int top, int end, int shift, CancellationToken token) {
            int overlap = end - top - shift, stepX = Math.Max(1, width / 160), stepY = Math.Max(1, overlap / 160);
            long checkedPixels = 0, wrong = 0, texturedPixels = 0, texturedWrong = 0;
            int texturedRows = 0;
            for (int y = top; y < end - shift; y += stepY) {
                token.ThrowIfCancellationRequested(); int rowTexture = 0;
                for (int x = 0; x < width; x += stepX) {
                    int pa = (y + shift) * stride + x * 4, pb = y * stride + x * 4;
                    bool mismatch = Distance(a, pa, b, pb) > 10 || Math.Abs(a[pa + 3] - b[pb + 3]) > 4;
                    checkedPixels++; if (mismatch) wrong++;
                    if (Textured(b, pb, y) || Textured(a, pa, y + shift)) {
                        texturedPixels++; rowTexture++; if (mismatch) texturedWrong++;
                    }
                }
                if (rowTexture > 1) texturedRows++;
            }
            return texturedPixels >= 64 && texturedRows >= 6 && wrong <= checkedPixels * .015 && texturedWrong <= texturedPixels * .03;
        }
        bool RowSame(byte[] a, byte[] b, int y, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++) {
                int p = y * stride + x * 4;
                if (Distance(a, p, b, p) > 4 || a[p + 3] != b[p + 3]) return false;
            }
            return true;
        }
        bool BandHasTexture(byte[] pixels, int start, int end) {
            int textured = 0, rows = 0;
            for (int y = start; y < end; y++) {
                int inRow = 0;
                for (int x = 2; x < width - 2; x += Math.Max(1, width / 80))
                    if (Textured(pixels, y * stride + x * 4, y)) inRow++;
                if (inRow > 1) rows++;
                textured += inRow;
            }
            return textured >= 16 && rows >= 3;
        }
        void FindFixedEdges(byte[] a, byte[] b, out int top, out int bottom, CancellationToken token) {
            int prefix = 0, suffix = 0, maximumBand = frameHeight / 4;
            while (prefix < maximumBand && RowSame(a, b, prefix, token)) prefix++;
            while (suffix < maximumBand && RowSame(a, b, frameHeight - suffix - 1, token)) suffix++;
            // A uniform white page margin is not proof of a fixed toolbar.
            top = prefix >= 6 && prefix < maximumBand && BandHasTexture(a, 0, prefix) ? prefix : 0;
            bottom = suffix >= 6 && suffix < maximumBand && BandHasTexture(a, frameHeight - suffix, frameHeight) ? suffix : 0;
        }
        bool FixedEdgesStillMatch(byte[] a, byte[] b, int top, int bottom, CancellationToken token) {
            for (int y = 0; y < top; y++) if (!RowSame(a, b, y, token)) return false;
            for (int y = frameHeight - bottom; y < frameHeight; y++) if (!RowSame(a, b, y, token)) return false;
            return true;
        }

        internal BitmapSource Render() {
            Alive();
            // Write directly into the WPF output buffer, avoiding a second giant
            // managed full-result allocation at the end of a long capture.
            var output = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            int firstRows = frameHeight - fixedBottom;
            output.WritePixels(new Int32Rect(0, 0, width, firstRows), first, stride, 0);
            int row = firstRows;
            foreach (var strip in strips) {
                output.WritePixels(new Int32Rect(0, row, width, strip.Height), strip.Pixels, stride, 0); row += strip.Height;
            }
            if (fixedBottom > 0)
                output.WritePixels(new Int32Rect(0, row, width, fixedBottom), previous, stride, (frameHeight - fixedBottom) * stride);
            output.Freeze(); return output;
        }
        public void Dispose() {
            if (disposed) return;
            disposed = true; first = previous = null; strips.Clear();
        }
    }
}
