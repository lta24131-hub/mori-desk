using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms=System.Windows.Forms;
using Rectangle=System.Drawing.Rectangle;

namespace QingJie {
    // Pure desktop-pixel placement. Prefer space outside the captured region,
    // including a secondary monitor. Full-screen selections use a brief hidden
    // control bar during acquisition, never an embedded toolbar in the result.
    internal static class ScrollCaptureLayout {
        internal static Rectangle Place(Rectangle crop,Rectangle[] monitors,int width,int height){
            if(monitors==null||monitors.Length==0)throw new ArgumentException("没有可用显示器。");
            foreach(var work in monitors.OrderByDescending(r=>r.IntersectsWith(crop))){
                int w=Math.Min(width,work.Width),h=Math.Min(height,work.Height);
                var choices=new[]{new Rectangle(crop.Right-w,crop.Bottom+8,w,h),new Rectangle(crop.Right-w,crop.Top-h-8,w,h),new Rectangle(crop.Right+8,crop.Top,w,h),new Rectangle(crop.Left-w-8,crop.Top,w,h),new Rectangle(work.X+8,work.Y+8,w,h)};
                foreach(var candidate in choices)if(work.Contains(candidate)&&!candidate.IntersectsWith(crop))return candidate;
                // Tight crops may leave exactly one toolbar-height of space.
                // Prefer touching the crop edge over covering captured pixels.
                var tight=new[]{new Rectangle(work.X,crop.Bottom,w,h),new Rectangle(work.X,crop.Top-h,w,h),new Rectangle(crop.Right,work.Y,w,h),new Rectangle(crop.Left-w,work.Y,w,h)};
                foreach(var candidate in tight)if(work.Contains(candidate)&&!candidate.IntersectsWith(crop))return candidate;
            }
            var fallback=monitors.FirstOrDefault(r=>r.IntersectsWith(crop));if(fallback.Width<=0)fallback=monitors[0];
            int fw=Math.Min(width,fallback.Width),fh=Math.Min(height,fallback.Height);
            return new Rectangle(fallback.X+(fallback.Width-fw)/2,fallback.Y+8,fw,Math.Min(fh,Math.Max(1,fallback.Height-8)));
        }
    }

    // No native resources, desktop access or event hooks are created until Start.
    // A single scoped Escape hotkey works while the target application's scroll
    // pane has focus. Wheel observation never injects or blocks mouse events.
    internal sealed class ScrollCaptureSession : IDisposable {
        readonly Rectangle region,screenBounds;
        readonly CancellationTokenSource cancel=new CancellationTokenSource();
        readonly DispatcherTimer timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(80)};
        readonly System.Diagnostics.Stopwatch clock=System.Diagnostics.Stopwatch.StartNew();
        ScrollCaptureStitcher stitcher;
        Window controls,outline;
        TextBlock status;
        Button finishButton,appendButton;
        HwndSource hotkeySource;
        ScrollCaptureNative.MouseHook mouseHook;
        IntPtr mouseHandle;
        bool disposed,busy,pending,finishing,started,hotkeyRegistered,cancelDisposed;
        long lastInput,lastCapture;
        const int EscapeId=0x5343;
        internal event Action<BitmapSource> Completed;
        internal bool IsDisposed {get{return disposed;}}
        internal ScrollCaptureSession(Rectangle region,Rectangle screenBounds,BitmapSource initial){
            if(!screenBounds.Contains(region)||region.Width<32||region.Height<80)throw new ArgumentException("滚动截屏区域至少需要 32 × 80 像素，并位于同一块屏幕内。");
            if(initial==null||initial.PixelWidth!=region.Width||initial.PixelHeight!=region.Height)throw new ArgumentException("起始截图尺寸与选区不一致，请重新选择。");
            this.region=region;this.screenBounds=screenBounds;
            stitcher=new ScrollCaptureStitcher(initial,new ScrollCaptureLimits {MaxPixels=32000000,MaxHeight=24000,MaxFrames=150});
            timer.Tick+=(s,e)=>{if(!disposed&&!busy&&!finishing&&pending&&clock.ElapsedMilliseconds-lastInput>=220&&clock.ElapsedMilliseconds-lastCapture>=280)Acquire(false);};
        }
        internal void Start(){
            if(disposed||started)throw new InvalidOperationException("滚动截屏已结束或已经启动。");
            started=true;
            try{
                var parameters=new HwndSourceParameters("Nook.ScrollCapture.Escape") {ParentWindow=new IntPtr(-3),Width=0,Height=0,WindowStyle=0};
                hotkeySource=new HwndSource(parameters);hotkeySource.AddHook(Hotkey);
                if(!Native.RegisterHotKey(hotkeySource.Handle,EscapeId,0x4000,0x1B))throw new InvalidOperationException("Esc 退出键被占用，请结束其他截屏工具后重试。");
                hotkeyRegistered=true;
                mouseHook=ObserveMouse;mouseHandle=ScrollCaptureNative.SetWindowsHookEx(14,mouseHook,ScrollCaptureNative.GetModuleHandle(null),0);
                if(mouseHandle==IntPtr.Zero)throw new InvalidOperationException("无法监听滚动，请重新开始截图。");
                BuildControls();BuildOutline();
                outline.Show();controls.Show();timer.Start();
            }catch{Dispose();throw;}
        }
        IntPtr Hotkey(IntPtr hwnd,int message,IntPtr w,IntPtr l,ref bool handled){
            if(message==0x312&&w.ToInt32()==EscapeId){handled=true;End(null);}return IntPtr.Zero;
        }
        IntPtr ObserveMouse(int code,IntPtr message,IntPtr data){
            try{if(code>=0&&data!=IntPtr.Zero&&!disposed&&!finishing){
                int kind=message.ToInt32();
                if(kind==0x20A||kind==0x202){
                    var input=(ScrollCaptureNative.MouseData)Marshal.PtrToStructure(data,typeof(ScrollCaptureNative.MouseData));
                    if(region.Contains(input.Point.X,input.Point.Y)){pending=true;lastInput=clock.ElapsedMilliseconds;}
                }
            }}catch{ /* Observing input must never interrupt the target app. */ }
            return ScrollCaptureNative.CallNextHookEx(mouseHandle,code,message,data);
        }
        void BuildControls(){
            controls=new Window {Title="Nook · 滚动截屏",Icon=Ui.AppIcon,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,ShowActivated=false,Topmost=true,AllowsTransparency=true,Background=Brushes.Transparent,Width=450,Height=76,FontFamily=new FontFamily("Microsoft YaHei UI")};
            var body=new DockPanel {Margin=new Thickness(5)};
            var actions=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};DockPanel.SetDock(actions,Dock.Right);
            appendButton=Ui.Tool("scroll","追加当前画面 · 键盘或滚动条滚动后使用",()=>Acquire(false));appendButton.Name="ScrollCaptureAppend";
            finishButton=Ui.Tool("done","完成长截图",Finish);finishButton.Name="ScrollCaptureFinish";
            actions.Children.Add(appendButton);actions.Children.Add(finishButton);actions.Children.Add(Ui.Tool("close","取消滚动截屏 · Esc",()=>End(null)));body.Children.Add(actions);
            status=new TextBlock {Text="每次向下滚动约半屏，停下后自动拼接\n已截取 "+stitcher.Height+" px · Esc 退出",FontSize=12,VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(5,0,8,0)};Ui.Theme(status,TextBlock.ForegroundProperty,"Text");body.Children.Add(status);controls.Content=Ui.Card(body);
            var work=Forms.Screen.AllScreens.Select(s=>s.WorkingArea).ToArray();
            controls.SourceInitialized+=(s,e)=>{
                var handle=new WindowInteropHelper(controls).Handle;
                var source=HwndSource.FromHwnd(handle);var scale=source.CompositionTarget.TransformToDevice;
                var placement=ScrollCaptureLayout.Place(region,work,(int)Math.Ceiling(450*scale.M11),(int)Math.Ceiling(76*scale.M22));
                Native.SetWindowPos(handle,Native.Topmost,placement.X,placement.Y,placement.Width,placement.Height,0x0010);
            };
            controls.PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape){e.Handled=true;End(null);}};
            controls.Closed+=(s,e)=>{if(!disposed)End(null);};
        }
        void BuildOutline(){
            outline=new Window {Title="Nook · 滚动截屏范围",WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,ShowActivated=false,Topmost=true,AllowsTransparency=true,Background=Brushes.Transparent};
            // Four physical pixels of transparent clearance keep every outline
            // pixel outside the sampled rectangle, even at mixed monitor DPI.
            outline.Content=new Border {BorderBrush=Ui.Tone("Accent"),BorderThickness=new Thickness(1),Background=Brushes.Transparent,IsHitTestVisible=false};
            outline.SourceInitialized+=(s,e)=>{
                var handle=new WindowInteropHelper(outline).Handle;
                int style=ScrollCaptureNative.GetWindowLong(handle,-20);
                ScrollCaptureNative.SetWindowLong(handle,-20,style|0x20|0x80000|0x08000000|0x80);
                Native.SetWindowPos(handle,Native.Topmost,region.X-4,region.Y-4,region.Width+8,region.Height+8,0x0010);
            };
        }
        async void Acquire(bool finishAfter){
            if(disposed)return;
            if(busy){if(finishAfter)finishing=true;return;}
            busy=true;pending=false;lastCapture=clock.ElapsedMilliseconds;bool controlsHidden=false;
            if(finishAfter)finishing=true;
            appendButton.IsEnabled=false;finishButton.IsEnabled=false;
            try{
                // Normally the bar is outside the region. A full-screen crop
                // has no such space: hide it for one compositor frame before
                // capture and restore it without stealing target-app focus.
                if(finishing)await Task.Delay(220,cancel.Token);
                // WPF may resize a moved window after a mixed-DPI transition.
                // Check its current physical bounds, never the initial estimate.
                ScrollCaptureNative.WindowRect actual;
                bool known=ScrollCaptureNative.GetWindowRect(new WindowInteropHelper(controls).Handle,out actual);
                if(!known||new Rectangle(actual.Left,actual.Top,Math.Max(0,actual.Right-actual.Left),Math.Max(0,actual.Bottom-actual.Top)).IntersectsWith(region)){
                    controls.Hide();controlsHidden=true;await Task.Delay(65,cancel.Token);
                }
                cancel.Token.ThrowIfCancellationRequested();
                var result=await Task.Run(()=>{
                    cancel.Token.ThrowIfCancellationRequested();
                    var screen=Forms.Screen.FromRectangle(region);
                    if(screen.Bounds!=screenBounds||!screen.Bounds.Contains(region))throw new InvalidOperationException("屏幕布局已变化，请重新截图。");
                    var frame=Snapshot.Screen(screen);
                    try{
                        if(!string.IsNullOrEmpty(frame.ColorWarning))throw new InvalidOperationException("当前屏幕无法保持一致的截图颜色，请稍后重试。");
                        var image=new CroppedBitmap(frame.Image,new Int32Rect(region.X-screen.Bounds.X,region.Y-screen.Bounds.Y,region.Width,region.Height));image.Freeze();
                        return stitcher.TryAppend(image,cancel.Token);
                    }finally{frame.Image=null;}
                });
                if(disposed)return;
                string hint=result.Status==ScrollCaptureAppendStatus.Added?"继续向下滚动，完成后点 ✓":result.Status==ScrollCaptureAppendStatus.Duplicate?"画面未变化；继续向下滚动":result.Status==ScrollCaptureAppendStatus.LimitReached?"已达长度上限，请点 ✓ 完成":result.Message;
                status.Text=hint+"\n已截取 "+stitcher.Height+" px · Esc 退出";
                if(finishing&&(result.Status==ScrollCaptureAppendStatus.NoReliableOverlap||result.Status==ScrollCaptureAppendStatus.InvalidFrame)){
                    // Do not hide a rejected final frame behind a successful
                    // preview. A second Finish explicitly keeps only the
                    // already stitched part; returning to its anchor can retry.
                    finishing=false;status.Text="最后一屏未加入，请慢滚半屏重试；\n再次点 ✓ 仅完成已拼接的 "+stitcher.Height+" px";
                }
                if(result.Status==ScrollCaptureAppendStatus.LimitReached){timer.Stop();pending=false;}
                else if(!finishing&&(result.Status==ScrollCaptureAppendStatus.Added||result.Status==ScrollCaptureAppendStatus.Duplicate))timer.Start();
            }catch(OperationCanceledException){}
            catch(Exception ex){if(!disposed){finishing=false;status.Text="自动拼接已暂停："+ex.Message+"\n可点追加重试，或完成已拼接的内容";timer.Stop();}}
            finally{
                busy=false;
                if(disposed){ReleaseStitcher();}
                else{
                    if(controlsHidden)controls.Show();
                    appendButton.IsEnabled=true;finishButton.IsEnabled=true;
                    if(finishing){if(pending)Acquire(true);else RenderAndFinish();}
                }
            }
        }
        void Finish(){if(disposed||finishing)return;finishing=true;timer.Stop();if(busy)return;if(pending)Acquire(true);else RenderAndFinish();}
        async void RenderAndFinish(){
            if(disposed||busy)return;busy=true;appendButton.IsEnabled=false;finishButton.IsEnabled=false;status.Text="正在生成长截图…\nEsc 可取消";
            try{var image=await Task.Run(()=>{cancel.Token.ThrowIfCancellationRequested();var result=stitcher.Render();cancel.Token.ThrowIfCancellationRequested();return result;});if(!disposed)End(image);}
            catch(OperationCanceledException){}
            catch(Exception ex){if(!disposed){finishing=false;status.Text="生成失败："+ex.Message+"\n可重试完成或 Esc 退出";}}
            finally{busy=false;if(disposed)ReleaseStitcher();else{appendButton.IsEnabled=true;finishButton.IsEnabled=true;}}
        }
        void End(BitmapSource image){if(disposed)return;var complete=Completed;Completed=null;Dispose();if(complete!=null)complete(image);}
        void ReleaseStitcher(){if(stitcher!=null){stitcher.Dispose();stitcher=null;}if(disposed&&!cancelDisposed){cancel.Dispose();cancelDisposed=true;clock.Stop();}}
        public void Dispose(){
            if(disposed)return;disposed=true;cancel.Cancel();timer.Stop();Completed=null;
            if(mouseHandle!=IntPtr.Zero){ScrollCaptureNative.UnhookWindowsHookEx(mouseHandle);mouseHandle=IntPtr.Zero;}mouseHook=null;
            if(hotkeySource!=null){if(hotkeyRegistered)Native.UnregisterHotKey(hotkeySource.Handle,EscapeId);hotkeyRegistered=false;hotkeySource.RemoveHook(Hotkey);hotkeySource.Dispose();hotkeySource=null;}
            if(controls!=null){controls.Close();controls=null;}if(outline!=null){outline.Close();outline=null;}
            // Worker append/render owns the stitcher until its finally block.
            if(!busy)ReleaseStitcher();
        }
    }
    internal static class ScrollCaptureNative {
        [StructLayout(LayoutKind.Sequential)]internal struct WindowRect {internal int Left,Top,Right,Bottom;}
        [DllImport("user32.dll")]internal static extern bool GetWindowRect(IntPtr window,out WindowRect rect);
        [StructLayout(LayoutKind.Sequential)]internal struct Point {internal int X,Y;}
        [StructLayout(LayoutKind.Sequential)]internal struct MouseData {internal Point Point;internal uint Mouse,Flags,Time;internal UIntPtr Extra;}
        internal delegate IntPtr MouseHook(int code,IntPtr message,IntPtr data);
        [DllImport("user32.dll",SetLastError=true)]internal static extern IntPtr SetWindowsHookEx(int hook,MouseHook callback,IntPtr module,uint thread);
        [DllImport("user32.dll")]internal static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")]internal static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]internal static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")]internal static extern int GetWindowLong(IntPtr window,int index);
        [DllImport("user32.dll")]internal static extern int SetWindowLong(IntPtr window,int index,int value);
    }
}
