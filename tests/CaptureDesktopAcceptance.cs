// Explicitly launched, isolated desktop acceptance host. Never part of the app entry point.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms=System.Windows.Forms;
using QingJie;

internal static class CaptureDesktopAcceptance {
    static string root,build;
    [STAThread] static int Main(){
        root=AppDomain.CurrentDomain.BaseDirectory;
        build=Path.GetFullPath(Path.Combine(root,"..","capture-operation-candidate-20261016-v3"));
        if(!File.Exists(Path.Combine(root,".capture-desktop-acceptance")))return 2;
        Environment.SetEnvironmentVariable("MORI_DESK_DATA",Path.Combine(root,"data"));
        AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{var name=new AssemblyName(e.Name).Name;var file=Path.Combine(build,name=="Mori"?"Mori.exe":name+".dll");return File.Exists(file)?Assembly.LoadFrom(file):null;};
        try{return Run();}catch(Exception error){File.WriteAllText(Path.Combine(root,"fatal.txt"),error.ToString());return 1;}
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static int Run(){
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        Mori.Theme.Set("light");
        AppState.Settings=new Preferences{SaveFolder=Path.Combine(root,"exports")};
        AppState.Pins=new PinStore(Path.Combine(root,"pins"));
        AppState.Quitting=false;
        app.DispatcherUnhandledException+=(s,e)=>{File.AppendAllText(Path.Combine(root,"errors.txt"),e.Exception+Environment.NewLine);e.Handled=true;};
        var known=new HashSet<Window>();
        var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(150)};
        timer.Tick+=(s,e)=>{
            foreach(Window window in app.Windows){
                if(known.Add(window)){
                    var observed=window;
                    window.Closed+=(a,b)=>Log("closed "+observed.GetType().Name);
                    Log("opened "+window.GetType().Name);
                }
            }
            var states=app.Windows.Cast<Window>().Select(w=>w.GetType().Name+" visible="+w.IsVisible+" active="+w.IsActive+" dpi="+VisualTreeHelper.GetDpi(w).DpiScaleX+" left="+w.Left+" top="+w.Top+" width="+w.ActualWidth+" height="+w.ActualHeight);
            File.WriteAllLines(Path.Combine(root,"window-state.txt"),states);
        };
        app.Startup+=(s,e)=>{
            int index=0;
            foreach(var monitor in Forms.Screen.AllScreens){
                int number=++index;var screen=monitor;
                Log("monitor "+number+" bounds="+screen.Bounds+" work="+screen.WorkingArea+" primary="+screen.Primary);
                var panel=new StackPanel{Margin=new Thickness(24)};
                panel.Children.Add(new TextBlock{Text="Nook · 双屏验收 · "+number,FontSize=24});
                panel.Children.Add(new TextBlock{Text="仅测试截图窗口，不连接真实图库。\nF1 开始截图；Esc 取消。",Margin=new Thickness(0,16,0,18),FontSize=15});
                var start=new Button{Content="开始截图",Height=42,Margin=new Thickness(0,4,0,4)};
                start.Click+=(a,b)=>AppState.BeginCapture(null,true);panel.Children.Add(start);
                var pin=new Button{Content="测试贴图落点（合成图片）",Height=42,Margin=new Thickness(0,4,0,4)};
                pin.Click+=(a,b)=>AppState.Pins.New(Sample());panel.Children.Add(pin);
                var text=new Button{Content="测试预览比例（合成图片）",Height=42,Margin=new Thickness(0,4,0,4)};
                text.Click+=(a,b)=>new TextWindow(Sample()).Show();panel.Children.Add(text);
                var exit=new Button{Content="结束全部测试窗口",Height=42,Margin=new Thickness(0,18,0,4)};
                exit.Click+=(a,b)=>{AppState.Quitting=true;AppState.CancelCapture();app.Shutdown();};panel.Children.Add(exit);
                var window=new Window{Title="Nook · 双屏验收 · "+number,Width=470,Height=445,Background=Brushes.White,Content=panel,FontFamily=new FontFamily("Microsoft YaHei UI"),WindowStartupLocation=WindowStartupLocation.Manual};
                window.SourceInitialized+=(a,b)=>SetWindowPos(new WindowInteropHelper(window).Handle,IntPtr.Zero,screen.WorkingArea.Left+70,screen.WorkingArea.Top+70,470,445,0x0014);
                window.PreviewKeyDown+=(a,b)=>{if(b.Key==Key.F1){AppState.BeginCapture(null,true);b.Handled=true;}};
                window.Closing+=(a,b)=>{if(!AppState.Quitting){b.Cancel=true;window.Hide();}};
                window.Show();
            }
            timer.Start();
        };
        app.Exit+=(s,e)=>{timer.Stop();AppState.Quitting=true;AppState.CancelCapture();Log("exit");};
        return app.Run();
    }
    static BitmapSource Sample(){
        var visual=new DrawingVisual();using(var dc=visual.RenderOpen()){
            dc.DrawRectangle(Brushes.White,null,new Rect(0,0,640,400));
            dc.DrawRectangle(Brushes.MediumPurple,null,new Rect(20,20,180,120));
            dc.DrawRectangle(Brushes.Black,null,new Rect(220,20,100,100));
            for(int i=0;i<20;i++)dc.DrawLine(new Pen(i%2==0?Brushes.Black:Brushes.MediumPurple,1),new Point(20,180+i*6),new Point(600,180+i*6));
        }
        var image=new RenderTargetBitmap(640,400,96,96,PixelFormats.Pbgra32);image.Render(visual);image.Freeze();return image;
    }
    static void Log(string message){File.AppendAllText(Path.Combine(root,"events.txt"),DateTime.UtcNow.ToString("o")+" "+message+Environment.NewLine);}
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
}
