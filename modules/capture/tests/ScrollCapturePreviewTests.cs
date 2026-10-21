using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QingJie;
using MoriDesk;

// Generated image / unshown WPF tree / injected in-memory collection only.
// Never invokes output buttons, native acquisition, desktop input or services.
static class ScrollCapturePreviewTests {
    sealed class TestSource : PresentationSource {
        public override Visual RootVisual { get; set; }
        public override bool IsDisposed { get { return false; } }
        protected override CompositionTarget GetCompositionTargetCore() { return null; }
    }
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static T Field<T>(ScrollCapturePreviewWindow window, string name) { return (T)typeof(ScrollCapturePreviewWindow).GetField(name, Hidden).GetValue(window); }
    static bool Closed(ScrollCapturePreviewWindow window) { return Field<bool>(window, "closed"); }
    static bool Inert(ScrollCapturePreviewWindow window) { return !window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero; }
    static Task Collect(ScrollCapturePreviewWindow window) { return (Task)typeof(ScrollCapturePreviewWindow).GetMethod("Collect", Hidden).Invoke(window, null); }
    static ScrollViewer Viewport(ScrollCapturePreviewWindow window) { return ((DockPanel)window.Content).Children.OfType<ScrollViewer>().Single(); }
    static void Drain() {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
    }
    static void Await(Task task) {
        var stop = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < stop) { Drain(); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new InvalidOperationException("Synthetic long screenshot collection timed out.");
        task.GetAwaiter().GetResult();
    }
    static BitmapSource Image(int width = 1200, int height = 3200) {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) {
            int p = (y * width + x) * 4; pixels[p] = (byte)(x % 251); pixels[p + 1] = (byte)(y % 241); pixels[p + 2] = (byte)((x + y * 3) % 239); pixels[p + 3] = 255;
        }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); image.Freeze(); return image;
    }
    static KeyEventArgs Escape(ScrollCapturePreviewWindow window) {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, new TestSource(), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        window.RaiseEvent(args); return args;
    }
    static void Arrange(ScrollCapturePreviewWindow window, double width, double height) {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); Drain(); root.UpdateLayout();
    }
    internal static void Run(Action<bool, string> check) {
        var collect = DeskRuntime.CollectImage; var pins = AppState.Pins; var context = SynchronizationContext.Current;
        int windows = Application.Current.Windows.Count, captures = AppState.Captures.Count;
        AppState.Pins = null; DeskRuntime.CollectImage = null;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try { Collection(check); LateResults(check); Pinning(check); Layout(check); }
        finally { DeskRuntime.CollectImage = collect; AppState.Pins = pins; SynchronizationContext.SetSynchronizationContext(context); }
        check(Application.Current.Windows.Count == windows && AppState.Captures.Count == captures,
            "long screenshot preview fixtures leave all application windows and capture membership counts unchanged");
    }
    static void Pinning(Action<bool,string> check){
        var pending=new TaskCompletionSource<bool>();var source=Image(40,900);var window=new ScrollCapturePreviewWindow(source);int calls=0;CancellationToken lifetime=CancellationToken.None;
        window.PinImageAsync=(image,cancel)=>{calls++;lifetime=cancel;check(ReferenceEquals(image,source),"recovered long screenshot pin receives complete source pixels");return pending.Task;};
        try{
            var task=window.Pin();check(!task.IsCompleted&&calls==1&&!Field<StackPanel>(window,"actions").IsEnabled&&Inert(window),"recovered long screenshot pin yields without showing a native window or acknowledging unfinished encoding");
            Await(window.Pin());check(calls==1,"pending recovered screenshot pin refuses duplicate submission");
            pending.SetException(new InvalidOperationException("Synthetic pin save failed"));Await(task);
            check(!Closed(window)&&Field<StackPanel>(window,"actions").IsEnabled&&ReferenceEquals(Field<BitmapSource>(window,"image"),source)&&Field<TextBlock>(window,"status").Text.Contains("Synthetic pin save failed"),"asynchronous recovered pin failure keeps the original preview ready to retry");
            pending=new TaskCompletionSource<bool>();task=window.Pin();var escaped=Escape(window);
            check(escaped.Handled&&Closed(window)&&lifetime.IsCancellationRequested&&Field<BitmapSource>(window,"image")==null,"one Escape cancels recovered pin encoding and releases its preview source");
            pending.SetCanceled();Await(task);check(Closed(window)&&!Field<bool>(window,"collecting"),"late cancelled pin cannot reopen its old recovered preview");
        }finally{pending.TrySetCanceled();if(!Closed(window))window.Close();}
    }
    static void Layout(Action<bool, string> check) {
        DeskRuntime.CollectImage = null;
        var image = Image(); var window = new ScrollCapturePreviewWindow(image);
        try {
            var preview = Field<System.Windows.Controls.Image>(window, "preview"); var viewport = Viewport(window);
            check(Inert(window) && ReferenceEquals(preview.Source, image) && ReferenceEquals(Field<BitmapSource>(window, "image"), image),
                "long screenshot preview retains its frozen source without constructing a native or visible window");
            check(Field<TextBlock>(window, "status").Text.Contains("1200 × 3200") && Field<StackPanel>(window, "actions").Children.Count == 2,
                "unconnected preview exposes dimensions and local output actions without a fake collection or desktop-pin action");
            check(viewport.VerticalScrollBarVisibility == ScrollBarVisibility.Auto && viewport.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto && !viewport.CanContentScroll,
                "long screenshot viewport supports continuous pixel scrolling with overflow scrollbars rather than truncating the image");
            Arrange(window, 800, 580);
            check(preview.ActualWidth > 0 && preview.ActualWidth <= viewport.ViewportWidth && viewport.ExtentHeight > viewport.ViewportHeight,
                "wide long image fits available preview width while its complete height remains vertically scrollable (image=" + preview.ActualWidth + "x" + preview.ActualHeight + ", viewport=" + viewport.ViewportWidth + "x" + viewport.ViewportHeight + ", extent=" + viewport.ExtentWidth + "x" + viewport.ExtentHeight + ")");
            check(Math.Abs(preview.ActualHeight / preview.ActualWidth - (double)image.PixelHeight / image.PixelWidth) < .001,
                "responsive long screenshot preview preserves image aspect ratio without squeezing document text");
            double wide = preview.ActualWidth; Arrange(window, 470, 360);
            check(preview.ActualWidth > 0 && preview.ActualWidth < wide && preview.ActualWidth <= viewport.ViewportWidth,
                "narrowing the unshown preview recomputes width instead of keeping a desktop-sized horizontal canvas");
            viewport.ScrollToVerticalOffset(viewport.ExtentHeight); viewport.UpdateLayout(); Drain(); viewport.UpdateLayout();
            check(viewport.VerticalOffset > 0 && viewport.VerticalOffset <= viewport.ScrollableHeight,
                "scrolling can reach content below the first preview screen without changing source resolution");
            check(ReferenceEquals(preview.Source, image) && image.PixelWidth == 1200 && image.PixelHeight == 3200 && image.IsFrozen && Inert(window),
                "width changes and scrolling retain every original source pixel and never create a native window");
            var escape = Escape(window);
            check(escape.Handled && Closed(window) && preview.Source == null && Field<BitmapSource>(window, "image") == null && Inert(window),
                "one preview Escape closes immediately and releases source references without a second key or native input");
        } finally { if (!Closed(window)) window.Close(); }
        var small = new ScrollCapturePreviewWindow(Image(100, 1800));
        try {
            Arrange(small, 800, 580);
            check(Field<System.Windows.Controls.Image>(small, "preview").ActualWidth <= 100 && Viewport(small).ExtentHeight > Viewport(small).ViewportHeight,
                "narrow long screenshots are not blurred by upscaling and still keep their full scrollable height");
        } finally { small.Close(); }
        var existing = Application.Current.Windows.Cast<Window>().ToArray(); bool rejected = false;
        try { new ScrollCapturePreviewWindow(null); } catch (ArgumentNullException) { rejected = true; }
        finally {
            // WPF registers the base Window before the derived constructor can
            // validate its argument. Clean up only this fixture's unshown base.
            foreach (var orphan in Application.Current.Windows.Cast<Window>().Except(existing).OfType<ScrollCapturePreviewWindow>().ToArray()) {
                if (!Inert(orphan)) throw new InvalidOperationException("Invalid-source fixture created native resources."); orphan.Close();
            }
        }
        check(rejected, "missing long screenshot source is rejected explicitly without creating native preview resources");
    }
    static void Collection(Action<bool, string> check) {
        int calls = 0; BitmapSource delivered = null; var pending = new TaskCompletionSource<bool>();
        DeskRuntime.CollectImage = image => { calls++; delivered = image; return pending.Task; };
        var source = Image(160, 900); var window = new ScrollCapturePreviewWindow(source);
        try {
            var actions = Field<StackPanel>(window, "actions");
            check(actions.Children.Count == 3 && actions.Children.OfType<Button>().Any(button => (button.ToolTip as string) == "收藏到素材库") && Inert(window),
                "available in-memory collection callback adds an accessible collection action without touching pin storage");
            var collecting = Collect(window);
            check(calls == 1 && ReferenceEquals(delivered, source) && delivered.IsFrozen && delivered.PixelWidth == 160 && delivered.PixelHeight == 900 && !collecting.IsCompleted,
                "collection receives the original frozen full-resolution long screenshot exactly once");
            check(Field<bool>(window, "collecting") && !actions.IsEnabled && !Closed(window) && Field<TextBlock>(window, "status").Text.Contains("正在收藏"),
                "pending collection disables duplicate output actions and preserves the preview instead of reporting premature success");
            Await(Collect(window));
            check(calls == 1 && !Closed(window), "repeated collection while a save is pending is a no-op and cannot create duplicate imports");
            pending.SetResult(false); Await(collecting);
            check(!Closed(window) && !Field<bool>(window, "collecting") && actions.IsEnabled && ReferenceEquals(Field<System.Windows.Controls.Image>(window, "preview").Source, source) && Field<TextBlock>(window, "status").Text.Contains("已保留"),
                "unsuccessful collection preserves the long image and reenables output with clear retry guidance");
            DeskRuntime.CollectImage = image => { throw new InvalidOperationException("Synthetic collector unavailable"); };
            Await(Collect(window));
            check(!Closed(window) && actions.IsEnabled && Field<TextBlock>(window, "status").Text.Contains("Synthetic collector unavailable"),
                "synchronous collection exception is contained inline without losing the long screenshot or leaving actions locked");
            var fault = new TaskCompletionSource<bool>(); DeskRuntime.CollectImage = image => fault.Task; var faultTask = Collect(window);
            fault.SetException(new InvalidOperationException("Synthetic delayed collection failure")); Await(faultTask);
            check(!Closed(window) && actions.IsEnabled && !Field<bool>(window, "collecting") && Field<TextBlock>(window, "status").Text.Contains("Synthetic delayed collection failure"),
                "asynchronous collection fault returns to an enabled retryable preview rather than escaping as an unhandled task error");
            DeskRuntime.CollectImage = null; string before = Field<TextBlock>(window, "status").Text; Await(Collect(window));
            check(!Closed(window) && actions.IsEnabled && !Field<bool>(window, "collecting") && Field<TextBlock>(window, "status").Text == before,
                "collection callback disappearing after preview creation is a safe no-op, not a false success or a stuck busy state");
            DeskRuntime.CollectImage = image => Task.FromResult(true); Await(Collect(window));
            check(Closed(window) && Field<BitmapSource>(window, "image") == null && Field<System.Windows.Controls.Image>(window, "preview").Source == null && Inert(window),
                "successful collection closes only its completed preview and releases retained image references");
        } finally { pending.TrySetResult(false); if (!Closed(window)) window.Close(); }
    }
    static void LateResults(Action<bool, string> check) {
        foreach (int result in new[] { 0, 1, 2 }) {
            var pending = new TaskCompletionSource<bool>(); DeskRuntime.CollectImage = image => pending.Task;
            var old = new ScrollCapturePreviewWindow(Image(180, 1100)); ScrollCapturePreviewWindow next = null; Task task = null;
            try {
                task = Collect(old); var escaped = Escape(old);
                check(escaped.Handled && Closed(old) && !task.IsCompleted && Field<BitmapSource>(old, "image") == null && Inert(old),
                    "one Escape remains available during collection and closes the old preview without waiting for a slow callback, result " + result);
                DeskRuntime.CollectImage = image => Task.FromResult(false); var nextImage = Image(200, 700); next = new ScrollCapturePreviewWindow(nextImage);
                string status = Field<TextBlock>(next, "status").Text;
                if (result == 2) pending.SetException(new InvalidOperationException("Synthetic late failure")); else pending.SetResult(result == 1);
                Await(task);
                check(!Closed(next) && ReferenceEquals(Field<BitmapSource>(next, "image"), nextImage) && ReferenceEquals(Field<System.Windows.Controls.Image>(next, "preview").Source, nextImage) &&
                    Field<TextBlock>(next, "status").Text == status && Field<StackPanel>(next, "actions").IsEnabled && Inert(next),
                    "late prior success, refusal or exception cannot close, mutate or disable a newer long screenshot preview, result " + result);
                check(Closed(old) && !Field<bool>(old, "collecting") && Field<BitmapSource>(old, "image") == null && Field<System.Windows.Controls.Image>(old, "preview").Source == null,
                    "late collection completion never resurrects the old preview or restores its released source, result " + result);
            } finally { pending.TrySetResult(false); if (task != null) Await(task); if (!Closed(old)) old.Close(); if (next != null && !Closed(next)) next.Close(); }
        }
    }
}
