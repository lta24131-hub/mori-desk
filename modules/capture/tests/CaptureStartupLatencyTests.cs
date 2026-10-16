using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QingJie;

static class CaptureStartupFixture {
    internal static readonly BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    internal static object Field(CaptureWindow window,string name){return typeof(CaptureWindow).GetField(name,Hidden).GetValue(window);}
    internal static object Call(CaptureWindow window,string name,params object[] args){return typeof(CaptureWindow).GetMethod(name,Hidden).Invoke(window,args);}
    internal static BitmapSource Image(int width,int height){
        var visual=new DrawingVisual();using(var d=visual.RenderOpen()){
            d.DrawRectangle(Brushes.WhiteSmoke,null,new Rect(0,0,width,height));
            d.DrawRectangle(Brushes.White,new Pen(Brushes.DarkGray,1),new Rect(40,30,width-80,height-60));
            d.DrawRectangle(Brushes.LightGray,new Pen(Brushes.DimGray,1),new Rect(100,80,180,130));
        }
        var result=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);result.Render(visual);result.Freeze();return result;
    }
    internal static CaptureWindow Window(BitmapSource image,bool layout=true){
        var host=new CaptureTarget(new Rect(30,20,image.PixelWidth-60,image.PixelHeight-40));
        var targets=new CaptureTargets(new List<CaptureTarget>{host});
        var window=new CaptureWindow(new Snapshot {Image=image,Bounds=new System.Drawing.Rectangle(0,0,image.PixelWidth,image.PixelHeight)},targets);
        if(layout){var surface=Surface(window);surface.Measure(new Size(image.PixelWidth,image.PixelHeight));surface.Arrange(new Rect(0,0,image.PixelWidth,image.PixelHeight));}return window;
    }
    internal static ScreenshotSurface Surface(CaptureWindow window){return ((Grid)window.Content).Children.OfType<ScreenshotSurface>().Single();}
    internal static void PumpUntil(Func<bool> ready){
        var watch=Stopwatch.StartNew();
        while(!ready()){
            if(watch.ElapsedMilliseconds>10000)throw new InvalidOperationException("Synthetic capture worker did not finish within the test timeout.");
            var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);Thread.Sleep(1);
        }
    }
}

// Compatible with the previous binary too. Timings deliberately include only
// synthetic bitmap/window construction, not a real keypress, compositor capture,
// monitor inventory, first paint, native target scan or user-perceived latency.
static class CaptureStartupConstructorBenchmark {
    internal static void Run(Action<bool,string> check){
        Console.WriteLine("SYNTHETIC constructor benchmark only; not end-to-end screenshot latency.");
        foreach(var size in new[]{new[]{600,400},new[]{3840,2160}}){
            var image=CaptureStartupFixture.Image(size[0],size[1]);var timings=new List<double>();
            for(int i=0;i<7;i++){
                CaptureWindow window=null;var watch=Stopwatch.StartNew();
                try{window=CaptureStartupFixture.Window(image,false);watch.Stop();timings.Add(watch.Elapsed.TotalMilliseconds);}
                finally{if(window!=null)window.Close();}
            }
            var warm=timings.Skip(1).OrderBy(value=>value).ToArray();
            Console.WriteLine("SYNTHETIC_CTOR "+size[0]+"x"+size[1]+" first_ms="+timings[0].ToString("0.00")+" warm_median_ms="+((warm[2]+warm[3])/2).ToString("0.00")+" warm_max_ms="+warm.Last().ToString("0.00")+" samples="+string.Join(",",timings.Select(value=>value.ToString("0.00"))));
            check(image.IsFrozen&&timings.Count==7,"synthetic constructor benchmark uses frozen generated pixels and closes every unshown fixture: "+size[0]+"x"+size[1]);
        }
    }
}

static class CaptureStartupLatencyTests {
    static int Settle(bool hotkey,bool hidden){return (int)typeof(CaptureStartGate).GetMethod("SettleDelayMilliseconds",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{hotkey,hidden});}
    static CaptureVisualRegions Read(BitmapSource image,System.Drawing.Rectangle bounds,CancellationToken token){
        var method=typeof(CaptureVisualRegions).GetMethod("Read",BindingFlags.Static|BindingFlags.NonPublic,null,new[]{typeof(BitmapSource),typeof(System.Drawing.Rectangle),typeof(CancellationToken)},null);
        try{return (CaptureVisualRegions)method.Invoke(null,new object[]{image,bounds,token});}
        catch(TargetInvocationException error){throw error.InnerException??error;}
    }
    internal static void Run(Action<bool,string> check){
        var image=CaptureStartupFixture.Image(600,400);var bounds=new System.Drawing.Rectangle(0,0,600,400);
        using(var cleanup=new WindowCleanup(CaptureStartupFixture.Window(image))){
            check(CaptureStartupFixture.Field(cleanup.Window,"visualTargets")==null,"capture construction does not synchronously scan screenshot pixels for visual targets");
            check(CaptureStartupFixture.Field(cleanup.Window,"visualTargetCancel")==null,"unshown capture constructor does not start an eager pixel-analysis worker");
        }
        check(Settle(true,false)==40,"direct screenshot hotkey uses the reduced 40ms settle policy");
        check(Settle(false,false)==130&&Settle(false,true)==130&&Settle(true,true)==130,"menu launches and hidden welcome screens keep a 130ms settle interval");
        int uiThread=Thread.CurrentThread.ManagedThreadId,workerThread=0;
        var task=Task.Run(()=>{workerThread=Thread.CurrentThread.ManagedThreadId;return Read(image,bounds,CancellationToken.None);});
        task.GetAwaiter().GetResult();
        check(image.IsFrozen&&workerThread!=uiThread&&task.Result.Count>0,"frozen synthetic pixels are analysed successfully off the UI thread");
        using(var cancel=new CancellationTokenSource()){
            cancel.Cancel();bool cancelled=false;
            try{Read(image,bounds,cancel.Token);}catch(OperationCanceledException){cancelled=true;}
            check(cancelled,"visual analysis observes cancellation before allocating or scanning screenshot pixels");
        }
        var context=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try{AsyncHover(image,check);ClosePending(image,check);}
        finally{SynchronizationContext.SetSynchronizationContext(context);}
    }
    sealed class WindowCleanup : IDisposable {
        internal readonly CaptureWindow Window;internal WindowCleanup(CaptureWindow window){Window=window;}
        public void Dispose(){Window.Close();}
    }
    static void AsyncHover(BitmapSource image,Action<bool,string> check){
        using(var cleanup=new WindowCleanup(CaptureStartupFixture.Window(image))){
            var window=cleanup.Window;var surface=CaptureStartupFixture.Surface(window);var point=new Point(150,120);
            CaptureStartupFixture.Call(window,"UpdateSnap",point);window.CycleSnap(point,1);var previous=surface.HoverSelection;
            CaptureStartupFixture.Call(window,"PrepareVisualTargets");var first=CaptureStartupFixture.Field(window,"visualTargetCancel");
            check(first!=null&&CaptureStartupFixture.Field(window,"visualTargets")==null,"visual-card detection returns to the dispatcher before applying worker output");
            CaptureStartupFixture.Call(window,"PrepareVisualTargets");check(ReferenceEquals(first,CaptureStartupFixture.Field(window,"visualTargetCancel")),"repeated prepare requests share one pending pixel-analysis worker");
            CaptureStartupFixture.PumpUntil(()=>CaptureStartupFixture.Field(window,"visualTargetCancel")==null);
            var regions=(CaptureVisualRegions)CaptureStartupFixture.Field(window,"visualTargets");
            check(regions!=null&&regions.Count>0&&surface.Selection.IsEmpty,"completed visual detection stores only geometry and never confirms a screenshot crop");
            CaptureStartupFixture.Call(window,"PrepareVisualTargets");
            check(ReferenceEquals(regions,CaptureStartupFixture.Field(window,"visualTargets"))&&CaptureStartupFixture.Field(window,"visualTargetCancel")==null,"restoring a frozen monitor reuses visual geometry instead of starting another pixel scan");
            check(surface.HoverSelection==previous,"late visual analysis never replaces the user's existing wheel-selected hover region");
            CaptureStartupFixture.Call(window,"ClearSnap");CaptureStartupFixture.Call(window,"UpdateSnap",point);
            check(!surface.HoverSelection.IsEmpty&&surface.HoverSelection.Width*surface.HoverSelection.Height<previous.Width*previous.Height,"new visual rectangles participate naturally on a later pointer update");
        }
    }
    static void ClosePending(BitmapSource image,Action<bool,string> check){
        var window=CaptureStartupFixture.Window(image);var surface=CaptureStartupFixture.Surface(window);
        try{
            CaptureStartupFixture.Call(window,"PrepareVisualTargets");var source=(CancellationTokenSource)CaptureStartupFixture.Field(window,"visualTargetCancel");var token=source.Token;
            window.Close();
            check(token.IsCancellationRequested&&surface.Image==null&&CaptureStartupFixture.Field(window,"visualTargets")==null,"closing a screenshot cancels pending detection and immediately releases displayed pixels");
            CaptureStartupFixture.PumpUntil(()=>CaptureStartupFixture.Field(window,"visualTargetCancel")==null);
            check(CaptureStartupFixture.Field(window,"visualTargets")==null&&surface.Image==null,"a cancelled worker cannot publish visual rectangles or reattach pixels after the window closes");
            CaptureStartupFixture.Call(window,"PrepareVisualTargets");
            check(CaptureStartupFixture.Field(window,"visualTargetCancel")==null&&CaptureStartupFixture.Field(window,"visualTargets")==null,"a deferred prepare callback after closure is a harmless no-op");
        }finally{window.Close();}
    }
}
