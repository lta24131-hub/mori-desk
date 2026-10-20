// Explicit opt-in desktop fixture. Compiling this file never starts a window.
// It uses production capture UI but no production entry point, library or bridge.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms=System.Windows.Forms;
using QingJie;

internal static class ScrollCaptureDesktopAcceptance {
    const string Marker=".capture-scroll-desktop-acceptance";
    static readonly BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static readonly List<Window> hosts=new List<Window>();
    static readonly List<ScrollViewer> viewports=new List<ScrollViewer>();
    static readonly HashSet<Window> observed=new HashSet<Window>();
    static readonly Dictionary<Window,InputEvidence> inputEvidence=new Dictionary<Window,InputEvidence>();
    static string root,lastState="";
    static Application app;
    static DispatcherTimer timer;
    static bool stopping;
    static bool automationTarget;
    static int collectionCount,automationTargetCount;

    sealed class InputEvidence {
        public int KeyDown,TextInput,TextChanged,FocusEntered;
    }

    [STAThread] static int Main(){
        root=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
        if(!File.Exists(Path.Combine(root,Marker)))return 2;
        // This must precede every reference that could initialize AppState.
        Environment.SetEnvironmentVariable("MORI_DESK_DATA",Path.Combine(root,"data"));
        try{return Run();}
        catch(Exception error){Log("fatal "+error);Cleanup();return 1;}
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static int Run(){
        automationTarget=Environment.GetEnvironmentVariable("NOOK_CAPTURE_AUTOMATION_TARGET")=="1"||AutomationTargetMarkerEnabled();
        Preferences.TestFilePath=Within("data","capture","settings.json");
        OcrLanguagePacks.TestFolder=Within("data","languages");
        OfflinePacks.TestFolder=Within("data","offline");
        app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        Mori.Theme.Set("light");
        AppState.Settings=new Preferences {SaveFolder=Within("exports"),Stroke=4,Color="#EF4444"};
        Directory.CreateDirectory(AppState.Settings.SaveFolder);
        AppState.Pins=new PinStore(Within("data","pins"));AppState.Quitting=false;
        MoriDesk.DeskRuntime.CollectImage=Collect;
        MoriDesk.DeskRuntime.Notify=message=>Log("notification "+message);
        // Do not start DeskRuntime/Entry/Program: no tray, main hotkeys,
        // browser bridge, file registration or real library is initialized.
        Log("start pid="+System.Diagnostics.Process.GetCurrentProcess().Id+" version="+typeof(CaptureWindow).Assembly.GetName().Version+" isolated-data="+MoriDesk.Entry.DataRoot);
        Log("automation-target="+automationTarget+"; opt-in only changes capture title/taskbar enumerability; it does not validate production hidden-taskbar activation; input contents are never logged");
        timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(180)};
        timer.Tick+=(s,e)=>RecordState();
        app.DispatcherUnhandledException+=(s,e)=>{Log("dispatcher-error "+e.Exception);e.Handled=true;Stop(1);};
        app.Startup+=(s,e)=>{
            int number=0;
            // Keep ordinary acceptance checks on the primary screen. A second
            // fixture is opt-in only, so testing does not occupy the user's
            // other monitor without a specific multi-screen test request.
            bool allMonitors=Environment.GetEnvironmentVariable("NOOK_CAPTURE_TEST_ALL_MONITORS")=="1";
            foreach(var monitor in Forms.Screen.AllScreens){
                ++number;if(allMonitors||monitor.Primary)CreateHost(monitor,number);
            }
            RecordState();timer.Start();
        };
        app.Exit+=(s,e)=>{Cleanup();Log("exit code="+e.ApplicationExitCode);};
        return app.Run();
    }
    static string Within(params string[] parts){
        string path=root;foreach(string part in parts)path=Path.Combine(path,part);
        path=Path.GetFullPath(path);
        if(!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Test output must remain inside its sandbox.");
        return path;
    }
    static bool AutomationTargetMarkerEnabled(){
        string marker=Within("nook-capture-automation-target.txt");
        if(!File.Exists(marker))return false;
        // A single literal line "1" is accepted, with or without its normal
        // line terminator. Whitespace, other values and extra lines fail closed.
        string[] lines=File.ReadAllLines(marker);
        return lines.Length==1&&lines[0]=="1";
    }
    static void CreateHost(Forms.Screen screen,int number){
        Log("monitor "+number+" bounds="+screen.Bounds+" work="+screen.WorkingArea+" primary="+screen.Primary);
        var body=new DockPanel {Margin=new Thickness(16)};
        var header=new StackPanel();DockPanel.SetDock(header,Dock.Top);
        header.Children.Add(new TextBlock {Text="Nook · 滚动截屏验收 · 屏幕 "+number,FontSize=22,FontWeight=FontWeights.SemiBold});
        header.Children.Add(new TextBlock {Text="仅测试数据，不连接真实图库。选择下方彩色内容区 → 滚动截屏 → 在原内容区每次向下滚动约半屏 → 完成。\n截图中按一次 Esc 应立即退出。收藏按钮只把长图写入本测试目录。",TextWrapping=TextWrapping.Wrap,FontSize=13,Foreground=Brushes.DimGray,Margin=new Thickness(0,8,0,8)});
        var buttons=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,10)};
        var start=new Button {Content="开始截图（F1）",Padding=new Thickness(16,7,16,7),Margin=new Thickness(0,0,10,0)};
        start.Click+=(s,e)=>StartCapture(number);buttons.Children.Add(start);
        var reset=new Button {Content="回到第 1 条",Padding=new Thickness(14,7,14,7),Margin=new Thickness(0,0,10,0)};buttons.Children.Add(reset);
        var exit=new Button {Content="结束全部测试窗口",Padding=new Thickness(14,7,14,7)};exit.Click+=(s,e)=>Stop(0);buttons.Children.Add(exit);
        header.Children.Add(buttons);body.Children.Add(header);
        var footer=new TextBlock {Text="共 84 条独特内容 · 测试窗口可直接关闭 · 当前正式 Nook 不会退出",FontSize=12,Foreground=Brushes.DimGray,Margin=new Thickness(0,8,0,0)};DockPanel.SetDock(footer,Dock.Bottom);body.Children.Add(footer);
        var content=new StackPanel();
        for(int i=1;i<=84;i++)content.Children.Add(Row(number,i));
        var scroll=new ScrollViewer {Name="ScrollAcceptanceContent",VerticalScrollBarVisibility=ScrollBarVisibility.Visible,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,CanContentScroll=false,Background=Brushes.White,Content=content};
        reset.Click+=(s,e)=>scroll.ScrollToTop();
        scroll.ScrollChanged+=(s,e)=>{if(e.VerticalChange!=0)Log("scroll monitor="+number+" offset="+Number(e.VerticalOffset)+" viewport="+Number(e.ViewportHeight)+" extent="+Number(e.ExtentHeight));};
        viewports.Add(scroll);body.Children.Add(scroll);
        var window=new Window {Title="Nook · 滚动截屏验收 · "+number,Width=900,Height=800,MinWidth=540,MinHeight=420,Background=Brushes.White,Content=body,FontFamily=new FontFamily("Microsoft YaHei UI"),WindowStartupLocation=WindowStartupLocation.Manual};
        hosts.Add(window);
        window.SourceInitialized+=(s,e)=>{
            var work=screen.WorkingArea;int width=Math.Min(1050,Math.Max(540,work.Width-100)),height=Math.Min(940,Math.Max(420,work.Height-90));
            Native.SetWindowPos(new WindowInteropHelper(window).Handle,IntPtr.Zero,work.Left+(work.Width-width)/2,work.Top+(work.Height-height)/2,width,height,0x0014);
        };
        window.PreviewKeyDown+=(s,e)=>{if(e.Key==Key.F1&&Keyboard.Modifiers==ModifierKeys.None){StartCapture(number);e.Handled=true;}};
        window.Closed+=(s,e)=>{hosts.Remove(window);viewports.Remove(scroll);if(!stopping&&hosts.Count==0)Stop(0);};
        window.Show();
    }
    static Border Row(int monitor,int index){
        uint seed=unchecked((uint)(index*397+monitor*103)*2654435761u);
        byte red=(byte)(55+(seed&127)),green=(byte)(45+((seed>>8)&127)),blue=(byte)(60+((seed>>16)&127));
        var color=new SolidColorBrush(Color.FromRgb(red,green,blue));color.Freeze();
        var grid=new Grid();grid.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(86)});grid.ColumnDefinitions.Add(new ColumnDefinition());
        var stripe=new Border {Background=color,Child=new TextBlock {Text=index.ToString("000"),Foreground=Brushes.White,FontSize=27,FontWeight=FontWeights.Bold,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center}};grid.Children.Add(stripe);
        var text=new StackPanel {Margin=new Thickness(15,9,15,9)};Grid.SetColumn(text,1);
        string token=seed.ToString("X8")+"-"+(index*index+7919).ToString(CultureInfo.InvariantCulture);
        text.Children.Add(new TextBlock {Text="第 "+index.ToString("000")+" 条 / Screen "+monitor+" / "+token,FontSize=17,FontWeight=FontWeights.SemiBold,Foreground=Brushes.Black});
        text.Children.Add(new TextBlock {Text="独特段落 "+(index*37+11)+"：山川、设计、纹理与排版；检查上下接缝文字完整，不重叠、不缺失。",FontSize=13,Margin=new Thickness(0,5,0,0),TextWrapping=TextWrapping.Wrap,Foreground=Brushes.DarkSlateGray});
        text.Children.Add(new TextBlock {Text="ROW "+index+" · check="+(seed%100003)+" · length="+(index*13+419)+" · non-periodic marker",FontFamily=new FontFamily("Consolas"),FontSize=11,Margin=new Thickness(0,4,0,0),Foreground=color});
        grid.Children.Add(text);
        return new Border {Height=100+(index*7%19),Margin=new Thickness(0,0,0,3),BorderBrush=new SolidColorBrush(Color.FromRgb(210,214,220)),BorderThickness=new Thickness(1),Background=new SolidColorBrush(Color.FromRgb((byte)(246-index%9),(byte)(250-index%7),(byte)(251-index%11))),Child=grid};
    }
    static void StartCapture(int monitor){if(stopping)return;Log("begin-capture requested-from-monitor="+monitor);AppState.BeginCapture(null,true);}
    static Task<bool> Collect(BitmapSource image){
        if(stopping||image==null)return Task.FromResult(false);
        try{
            string file=Within("collected",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N").Substring(0,8)+".png");Directory.CreateDirectory(Path.GetDirectoryName(file));
            ImageFiles.Write(image,file);collectionCount++;LogImage("collected "+collectionCount+" file="+Path.GetFileName(file),image);
            File.WriteAllText(Within("last-collected.txt"),file+Environment.NewLine+"width="+image.PixelWidth+" height="+image.PixelHeight+Environment.NewLine);
            return Task.FromResult(true);
        }catch(Exception error){Log("collection-error "+error);return Task.FromResult(false);}
    }
    static object Field(object owner,string name){if(owner==null)return null;var field=owner.GetType().GetField(name,Hidden);return field==null?null:field.GetValue(owner);}
    static string Number(double value){return value.ToString("0.##",CultureInfo.InvariantCulture);}
    static void ObserveInput(Window window){
        var evidence=new InputEvidence();inputEvidence.Add(window,evidence);
        window.AddHandler(Keyboard.PreviewKeyDownEvent,new KeyEventHandler((s,e)=>evidence.KeyDown++),true);
        window.AddHandler(TextCompositionManager.PreviewTextInputEvent,new TextCompositionEventHandler((s,e)=>evidence.TextInput++),true);
        window.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,new TextChangedEventHandler((s,e)=>evidence.TextChanged++),true);
        window.AddHandler(Keyboard.GotKeyboardFocusEvent,new KeyboardFocusChangedEventHandler((s,e)=>evidence.FocusEntered++),true);
        window.Closed+=(s,e)=>inputEvidence.Remove(window);
        if(automationTarget&&window is CaptureWindow){
            // Test-only enumerability seam: never activate/focus the window here.
            // Changing ShowInTaskbar may itself affect native window state, so
            // this run cannot establish production hidden-taskbar activation.
            window.Title="Nook · 截图键盘验收 · "+(++automationTargetCount);
            window.ShowInTaskbar=true;
            Log("automation-target-added title="+window.Title+" show-in-taskbar="+window.ShowInTaskbar);
        }
    }
    static void RecordState(){
        if(app==null||stopping)return;
        try{
            var rows=new List<string> {"utc="+DateTime.UtcNow.ToString("o"),"capture-count="+AppState.Captures.Count+" collection-count="+collectionCount+" selected="+(typeof(AppState).GetField("selectedCapture",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null)!=null)};
            foreach(Window window in app.Windows.Cast<Window>().ToArray()){
                if(observed.Add(window)){
                    ObserveInput(window);
                    var current=window;Log("opened "+window.GetType().Name+" title="+window.Title);window.Closed+=(s,e)=>Log("closed "+current.GetType().Name+" title="+current.Title);
                    if(window is CaptureWindow){var shot=Field(window,"shot") as Snapshot;if(shot!=null)LogImage("capture-source bounds="+shot.Bounds,shot.Image);}
                    if(window.GetType().Name=="ScrollCapturePreviewWindow")LogImage("long-preview-source",Field(window,"image") as BitmapSource);
                }
                rows.Add(window.GetType().Name+" title="+window.Title+" visible="+window.IsVisible+" active="+window.IsActive+" dpi="+Number(VisualTreeHelper.GetDpi(window).DpiScaleX)+" left="+Number(window.Left)+" top="+Number(window.Top)+" width="+Number(window.ActualWidth)+" height="+Number(window.ActualHeight));
                InputEvidence evidence;
                if(inputEvidence.TryGetValue(window,out evidence))rows.Add(" input focus-within="+window.IsKeyboardFocusWithin+" key-down-events="+evidence.KeyDown+" text-input-events="+evidence.TextInput+" text-changed-events="+evidence.TextChanged+" focus-entered-events="+evidence.FocusEntered);
                var capture=window as CaptureWindow;
                if(capture!=null){
                    var surface=Field(capture,"surface") as ScreenshotSurface;var session=Field(capture,"scrollingCapture");
                    rows.Add(" capture bounds="+capture.CaptureScreenBounds+" crop="+surface.Selection+" marks="+surface.Marks.Count+" draft="+(surface.Draft!=null)+" tool="+capture.SelectedTool+" suspended="+capture.IsCaptureOverlaySuspended+" scrolling="+capture.IsScrollingCapture);
                    var editor=Field(capture,"textEditor") as TextBox;
                    rows.Add(" text-editor present="+(editor!=null)+" length="+(editor==null?0:editor.Text.Length)+" keyboard-focused="+(editor!=null&&editor.IsKeyboardFocused)+" focus-within="+(editor!=null&&editor.IsKeyboardFocusWithin));
                    if(session!=null){rows.Add(" external-session disposed="+Field(session,"disposed")+" started="+Field(session,"started")+" id="+Field(session,"SessionId")+" directory="+Field(session,"OutputDirectory"));}
                }
                if(window.GetType().Name=="ScrollCapturePreviewWindow"){
                    var image=Field(window,"image") as BitmapSource;var preview=Field(window,"preview") as Image;rows.Add(" preview pixels="+(image==null?"released":image.PixelWidth+"x"+image.PixelHeight)+" rendered="+(preview==null?"none":Number(preview.ActualWidth)+"x"+Number(preview.ActualHeight))+" status="+((Field(window,"status") as TextBlock)==null?"":((TextBlock)Field(window,"status")).Text));
                }
            }
            var focused=Keyboard.FocusedElement;
            var focusedObject=focused as DependencyObject;
            var focusWindow=focusedObject==null?null:Window.GetWindow(focusedObject);
            rows.Add("keyboard-focus type="+(focused==null?"none":focused.GetType().Name)+" owner-type="+(focusWindow==null?"none":focusWindow.GetType().Name)+" owner-title="+(focusWindow==null?"none":focusWindow.Title));
            foreach(var viewport in viewports){
                if(!viewport.IsVisible)continue;var point=viewport.PointToScreen(new Point(0,0));var end=viewport.PointToScreen(new Point(viewport.ViewportWidth,viewport.ViewportHeight));
                rows.Add(" scroll-viewport screen="+point+".."+end+" logical="+Number(viewport.ViewportWidth)+"x"+Number(viewport.ViewportHeight)+" offset="+Number(viewport.VerticalOffset));
            }
            File.WriteAllLines(Within("window-state.txt"),rows);
            string state=string.Join(Environment.NewLine,rows.Skip(1));if(state!=lastState){lastState=state;Log("state "+state.Replace(Environment.NewLine," || "));}
        }catch(Exception error){Log("state-error "+error);}
    }
    static void LogImage(string label,BitmapSource image){
        if(image==null){Log(label+" pixels=released");return;}
        try{
            BitmapSource source=image;if(source.Format!=PixelFormats.Bgra32){source=new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);source.Freeze();}
            var samples=new List<string>();var bytes=new List<byte>();
            foreach(var point in new[]{new Point(0,0),new Point(source.PixelWidth-1,0),new Point(source.PixelWidth/2,source.PixelHeight/2),new Point(0,source.PixelHeight-1),new Point(source.PixelWidth-1,source.PixelHeight-1)}){
                var pixel=new byte[4];source.CopyPixels(new Int32Rect((int)point.X,(int)point.Y,1,1),pixel,4,0);bytes.AddRange(pixel);samples.Add(point+":"+BitConverter.ToString(pixel));
            }
            string digest;using(var hash=SHA256.Create())digest=BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-","");
            Log(label+" pixels="+image.PixelWidth+"x"+image.PixelHeight+" format="+image.Format+" dpi="+Number(image.DpiX)+"/"+Number(image.DpiY)+" sample-sha256="+digest+" BGRA=["+string.Join(";",samples)+"]");
        }catch(Exception error){Log(label+" pixel-evidence-error="+error.Message);}
    }
    static void Stop(int code){if(stopping)return;stopping=true;Cleanup();if(app!=null)app.Shutdown(code);}
    static void Cleanup(){
        stopping=true;
        if(timer!=null)timer.Stop();
        try{AppState.Quitting=true;AppState.CancelCapture();MoriDesk.DeskRuntime.CollectImage=null;MoriDesk.DeskRuntime.Notify=null;}catch(Exception error){Log("capture-cleanup-error "+error.Message);}
        if(app!=null)foreach(Window window in app.Windows.Cast<Window>().ToArray())try{window.Close();}catch(Exception error){Log("window-cleanup-error "+error.Message);}
    }
    static void Log(string message){try{File.AppendAllText(Within("events.txt"),DateTime.UtcNow.ToString("o")+" "+message+Environment.NewLine,Encoding.UTF8);}catch{}}
}
