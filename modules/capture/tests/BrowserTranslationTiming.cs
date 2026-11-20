using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;

// Re-display saved browser pixels on the interactive test desktop. Verify the
// actual screen capture before passing it through the normal Translate button.
internal static class BrowserTranslationTiming {
    internal static Task Run(string folder, IEnumerable<string> cases) {
        return ScreenshotTranslationTiming.WithDisplay(async () => {
            var errors = new List<Exception>();
            foreach (var name in cases) {
                Console.WriteLine("Browser crop: " + name);
                try { await Measure(folder, name); }
                catch (Exception error) {
                    Console.WriteLine(error.Message);
                    errors.Add(new InvalidOperationException(name, error));
                }
            }
            if (errors.Count > 0) throw new AggregateException(errors);
        });
    }

    static async Task Measure(string folder, string name) {
        string text;
        string[] accepted;
        switch (name) {
            case "file":
                text = "Open this file."; accepted = new[] { "打开此文件", "打开这个文件", "打开该文件", "把这个文件打开", "打开这份文件", "打开文件" }; break;
            case "door":
                text = "Close the door."; accepted = new[] { "关门", "关上门", "把门关上", "关上这扇门", "把门关起来" }; break;
            case "copy":
                text = "Copy link"; accepted = new[] { "复制链接", "拷贝链接" }; break;
            case "save":
                text = "Save changes"; accepted = new[] { "保存更改", "保存修改", "保存变更" }; break;
            default: throw new ArgumentException("Use file, door, copy or save.");
        }
        BitmapSource original;
        using (var stream = File.OpenRead(Path.Combine(folder, "browser-" + name + "-edge-real.png"))) {
            original = new WriteableBitmap(BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]);
            original.Freeze();
        }
        var frame = await CaptureExactPixels(original);
        await ScreenshotTranslationTiming.VerifySelection(frame.Item1, frame.Item2, text, accepted);
    }

    static FormatConvertedBitmap Bgra(BitmapSource source) {
        return new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
    }
    static async Task<Tuple<Snapshot, Rect>> CaptureExactPixels(BitmapSource original) {
        int width = original.PixelWidth, height = original.PixelHeight, stride = width * 4;
        var expected = new byte[stride * height];
        Bgra(original).CopyPixels(expected, stride, 0);
        var seed = Guid.NewGuid().ToByteArray();
        var marker = Color.FromRgb((byte)(110 + seed[0] % 125), (byte)(15 + seed[1] % 65), (byte)(120 + seed[2] % 115));
        var image = new Image { Source = original, Stretch = Stretch.Fill, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        var panel = new Border { Background = new SolidColorBrush(marker), Padding = new Thickness(8), Child = image };
        var window = new Window {
            Content = panel, Title = "Nook translation test", WindowStyle = WindowStyle.None, Topmost = true,
            ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize, UseLayoutRounding = true, SizeToContent = SizeToContent.WidthAndHeight
        };
        window.SourceInitialized += (s, e) => {
            var toDips = PresentationSource.FromVisual(window).CompositionTarget.TransformFromDevice;
            var size = toDips.Transform(new Vector(width, height));
            image.Width = size.X; image.Height = size.Y;
        };
        var cropped = new byte[expected.Length];
        var pixel = new byte[4];
        try {
            window.Show(); window.Activate(); window.UpdateLayout();
            var monitor = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(window).Handle);
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 10000) {
                await Task.Delay(100);
                var shot = Snapshot.Screen(monitor);
                var origin = image.PointToScreen(new Point(0, 0));
                int x = (int)Math.Round(origin.X) - shot.Bounds.Left, y = (int)Math.Round(origin.Y) - shot.Bounds.Top;
                if (x < 4 || y < 4 || x + width + 3 >= shot.Image.PixelWidth || y + height + 3 >= shot.Image.PixelHeight)
                    throw new InvalidOperationException("The test image must fit on one display.");
                var bgra = Bgra(shot.Image);
                bool visible = true;
                foreach (int px in new[] { x - 4, x + width + 3 })
                    foreach (int py in new[] { y - 4, y + height + 3 }) {
                        bgra.CopyPixels(new Int32Rect(px, py, 1, 1), pixel, 4, 0);
                        visible &= pixel[0] == marker.B && pixel[1] == marker.G && pixel[2] == marker.R;
                    }
                if (!visible) continue;
                bgra.CopyPixels(new Int32Rect(x, y, width, height), cropped, stride, 0);
                if (!cropped.SequenceEqual(expected))
                    throw new InvalidOperationException("The screen pixels differ from the saved browser image.");
                return Tuple.Create(shot, new Rect(x, y, width, height));
            }
            throw new InvalidOperationException("The complete marked test image was not visible.");
        } finally { window.Close(); }
    }
}
