using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Forms=System.Windows.Forms;

namespace QingJie {
    public static class AppState {
        public static Preferences Settings=Preferences.Load();
        public static PinStore Pins;
        public static bool Quitting;
        public static readonly List<CaptureWindow> Captures=new List<CaptureWindow>();
        public static Forms.NotifyIcon Tray;
        public static WelcomeWindow Welcome;
        public static HotkeyManager Hotkeys;
        public static Action<Shortcut> ShortcutRecorder;
        public static event Action TranslationProviderChanged;
        public static void ProviderChanged(){if(TranslationProviderChanged!=null)TranslationProviderChanged();}
        public static string SaveShortcuts(ShortcutSet shortcuts){if(Hotkeys==null)return "测试窗口不修改系统快捷键；正式运行后可设置。";var before=Settings.Shortcuts;string error=Hotkeys.Apply(shortcuts,()=>{Settings.Shortcuts=shortcuts.Copy();try{Settings.SaveChecked();}catch{Settings.Shortcuts=before;throw;}});if(error==null)RefreshShortcuts();return error;}
        public static void RefreshShortcuts(){var keys=Settings.Shortcuts;if(Tray!=null&&Tray.ContextMenuStrip!=null){var items=Tray.ContextMenuStrip.Items;items[0].Text="截图  "+keys.Capture;items[1].Text="找回贴图 / 粘贴  "+keys.Pin;items[3].Text="输入翻译  "+keys.Translate;string tip="Luma · "+keys.Capture+" 截图 / "+keys.Pin+" 找回贴图";Tray.Text=tip.Length>63?tip.Substring(0,63):tip;}if(Welcome!=null)Welcome.RefreshShortcuts();}
        static SettingsWindow settingsWindow;
        static WriteTranslationWindow writeTranslation;
        public static void ShowTranslator(){if(Captures.Count>0){Notify("请先完成或取消当前截图，再打开输入翻译。");return;}if(writeTranslation==null){writeTranslation=new WriteTranslationWindow();writeTranslation.Closed+=(s,e)=>writeTranslation=null;}writeTranslation.Show();if(writeTranslation.WindowState==WindowState.Minimized)writeTranslation.WindowState=WindowState.Normal;writeTranslation.Activate();}
        public static void ShowSettings(){if(settingsWindow==null){settingsWindow=new SettingsWindow();settingsWindow.Closed+=(s,e)=>settingsWindow=null;}settingsWindow.Show();settingsWindow.Activate();}
        public static void Notify(string message){if(MoriDesk.DeskRuntime.Notify!=null)MoriDesk.DeskRuntime.Notify(message);if(Tray!=null){Tray.BalloonTipTitle="Luma";Tray.BalloonTipText=message;Tray.ShowBalloonTip(3000);}}
        public static void ChooseCapture(CaptureWindow chosen){foreach(var w in Captures.ToList())if(w!=chosen)w.Close();}
        public static void CancelCapture(){foreach(var w in Captures.ToList())w.Close();}
        public static void BeginCapture(string testImage=null){
            if(Captures.Count>0){Captures[0].Activate();return;}
            if(Welcome!=null)Welcome.Hide();
            var delay=new System.Windows.Threading.DispatcherTimer {Interval=TimeSpan.FromMilliseconds(130)};
            delay.Tick+=(s,e)=>{delay.Stop();try{
                var shots=new List<Snapshot>();
                if(testImage!=null){var img=Snapshot.Load(testImage);var screen=Forms.Screen.PrimaryScreen;shots.Add(new Snapshot{Image=img,Bounds=new System.Drawing.Rectangle(screen.Bounds.X,screen.Bounds.Y,img.PixelWidth,img.PixelHeight)});}
                else foreach(var screen in Forms.Screen.AllScreens)shots.Add(Snapshot.Screen(screen));
                // Geometry must be captured before showing ANY monitor's overlay.
                CaptureTargets targets=null;if(testImage==null)try{targets=CaptureTargets.Read();}catch{ /* Manual capture remains available. */ }
                foreach(var shot in shots){var w=new CaptureWindow(shot,targets);Captures.Add(w);w.Show();}
            }catch(Exception ex){MessageBox.Show("无法截屏："+ex.Message,"Luma");}};delay.Start();
        }
        public static void Paste(bool recover=true){try{if(recover&&Pins.Recover())return;if(Clipboard.ContainsImage())Pins.New(Clipboard.GetImage());else Notify("没有可找回的贴图。先截图并复制，再按 "+Settings.Shortcuts.Pin+" 贴到屏幕。");}catch(Exception ex){Notify("贴图失败："+ex.Message);}}
        public static void ShowWelcome(){if(Welcome==null){Welcome=new WelcomeWindow();Welcome.Closed+=(s,e)=>Welcome=null;}Welcome.Show();Welcome.Activate();}
    }
    public sealed class WelcomeWindow : Window {
        readonly TextBlock shortcuts=new TextBlock {FontSize=14,LineHeight=28};
        readonly Button captureButton,writeButton;
        public void RefreshShortcuts(){var keys=AppState.Settings.Shortcuts;shortcuts.Text=keys.Capture+"     截图，选区后展开工具栏\n"+keys.Pin+"     找回贴图 / 粘贴剪贴板图片\n滚轮   标注时调粗细，贴图时调大小";captureButton.Content="开始截图  "+keys.Capture;writeButton.Content="输入翻译 · 中译英  "+keys.Translate;}
        public WelcomeWindow(){Title="Luma";Icon=Ui.AppIcon;Width=450;Height=510;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterScreen;Background=Brushes.White;FontFamily=new FontFamily("Microsoft YaHei UI");
            var panel=new StackPanel {Margin=new Thickness(28)};panel.Children.Add(new TextBlock {Text="Luma",FontSize=30,FontWeight=FontWeights.SemiBold,Foreground=Ui.Ink});panel.Children.Add(new TextBlock {Text="截图 · 标注 · 贴图 · 文字翻译",Foreground=Ui.Brush("#718078"),Margin=new Thickness(0,6,0,23)});
            panel.Children.Add(shortcuts);
            captureButton=Ui.TextButton("",()=>AppState.BeginCapture(),true);panel.Children.Add(captureButton);
            writeButton=Ui.TextButton("",AppState.ShowTranslator);panel.Children.Add(writeButton);RefreshShortcuts();
            panel.Children.Add(Ui.TextButton("打开图片 · 识别 / 翻译",()=>{var dialog=new Microsoft.Win32.OpenFileDialog {Filter="图片|*.png;*.jpg;*.jpeg;*.bmp"};if(dialog.ShowDialog(this)==true){try{new TextWindow(Snapshot.Load(dialog.FileName)).Show();Hide();}catch(Exception ex){MessageBox.Show(this,ex.Message,"Luma");}}}));
            panel.Children.Add(Ui.TextButton("设置",AppState.ShowSettings));panel.Children.Add(new TextBlock {Text="文字选择：截图后点工具栏的文字识别按钮。\n翻译服务可在设置中切换；贴图保存在本机。\n关闭此窗口后继续在托盘运行。",FontSize=12,Foreground=Ui.Brush("#7F8983"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0)});Content=panel;
        }
    }
    public static class Program {
        static HwndSource hotkeys;static Mutex mutex;static EventWaitHandle wake,quitEvent;static RegisteredWaitHandle waiter,quitWaiter;
        static bool diagnostics;
        static void Trace(string message){if(diagnostics)File.AppendAllText(Path.Combine(Path.GetTempPath(),"QingJie-startup.log"),DateTime.Now.ToString("O")+" "+message+Environment.NewLine);}
        [STAThread] public static void Main(string[] args){
            string worker=args.FirstOrDefault(a=>a.StartsWith("--ocr-worker=",StringComparison.Ordinal));
            if(worker!=null){Environment.ExitCode=OcrWorker.Execute(worker.Substring("--ocr-worker=".Length));return;}
            diagnostics=args.Contains("--diagnostics");Trace("Main");
            bool created;mutex=new Mutex(true,"Local\\QingJie.NativeScreenshot",out created);
            if(!created){try{EventWaitHandle.OpenExisting(args.Contains("--quit")?"Local\\QingJie.QuitRequested":"Local\\QingJie.CaptureRequested").Set();}catch{}return;}
            if(args.Contains("--quit")){mutex.ReleaseMutex();mutex.Dispose();return;}
            wake=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\QingJie.CaptureRequested");
            quitEvent=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\QingJie.QuitRequested");
            var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            app.DispatcherUnhandledException+=(s,e)=>{MessageBox.Show("操作未完成："+e.Exception.Message,"Luma");e.Handled=true;};
            app.SessionEnding+=(s,e)=>{AppState.Quitting=true;try{AppState.Pins.PersistAll();}catch{}};
            app.Startup+=(s,e)=>{
                Trace("Startup");
                AppState.Pins=new PinStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"QingJie","pins"));
                Trace("Pins loaded");
                var param=new HwndSourceParameters("QingJie.Hotkeys"){WindowStyle=0,Width=0,Height=0,ParentWindow=new IntPtr(-3)};hotkeys=new HwndSource(param);hotkeys.AddHook(Hotkey);
                Trace("Hotkey source");
                AppState.Tray=new Forms.NotifyIcon {Text="Luma · F1 截图 / F3 贴图找回",Icon=System.Drawing.Icon.ExtractAssociatedIcon(typeof(Program).Assembly.Location),Visible=true};
                Trace("Tray");
                var menu=new Forms.ContextMenuStrip();menu.Items.Add("截图  F1",null,(o,a)=>AppState.BeginCapture());menu.Items.Add("找回贴图 / 粘贴  F3",null,(o,a)=>AppState.Paste());menu.Items.Add("粘贴剪贴板图片",null,(o,a)=>AppState.Paste(false));menu.Items.Add("使用说明",null,(o,a)=>AppState.ShowWelcome());menu.Items.Add(new Forms.ToolStripSeparator());menu.Items.Add("退出（下次恢复贴图）",null,(o,a)=>Quit());AppState.Tray.ContextMenuStrip=menu;AppState.Tray.DoubleClick+=(o,a)=>AppState.BeginCapture();
                menu.Items.Insert(3,new Forms.ToolStripMenuItem("设置",null,(o,a)=>AppState.ShowSettings()));
                menu.Items.Insert(3,new Forms.ToolStripMenuItem("输入翻译 · 中译英  Ctrl+Alt+T",null,(o,a)=>AppState.ShowTranslator()));
                AppState.Hotkeys=new HotkeyManager(hotkeys.Handle);string shortcutError=AppState.Hotkeys.Start(AppState.Settings.Shortcuts);if(shortcutError!=null)AppState.Notify(shortcutError);AppState.RefreshShortcuts();
                waiter=ThreadPool.RegisterWaitForSingleObject(wake,(o,t)=>app.Dispatcher.BeginInvoke(new Action(()=>AppState.BeginCapture())),null,-1,false);
                quitWaiter=ThreadPool.RegisterWaitForSingleObject(quitEvent,(o,t)=>app.Dispatcher.BeginInvoke(new Action(Quit)),null,-1,false);
                AppState.Pins.Restore();
                Trace("Pins restored");
                string test=args.FirstOrDefault(a=>a.StartsWith("--test-image="));
                if(test!=null)AppState.BeginCapture(test.Substring("--test-image=".Length));else if(args.Contains("--capture"))AppState.BeginCapture();else if(!args.Contains("--background"))AppState.ShowWelcome();
                Trace("Startup complete");
            };
            app.Exit+=(s,e)=>{if(AppState.Hotkeys!=null)AppState.Hotkeys.Dispose();if(hotkeys!=null)hotkeys.Dispose();if(AppState.Tray!=null)AppState.Tray.Dispose();if(waiter!=null)waiter.Unregister(null);if(quitWaiter!=null)quitWaiter.Unregister(null);wake.Dispose();quitEvent.Dispose();mutex.ReleaseMutex();mutex.Dispose();};
            app.Run();
        }
        static IntPtr Hotkey(IntPtr hwnd,int msg,IntPtr w,IntPtr l,ref bool handled){if(msg==0x312&&AppState.Hotkeys!=null){int id=w.ToInt32(),action=AppState.Hotkeys.ActionFor(id);if(action<0)return IntPtr.Zero;handled=true;if(AppState.ShortcutRecorder!=null){AppState.ShortcutRecorder(AppState.Hotkeys.KeyFor(id));return IntPtr.Zero;}if(action==0)AppState.BeginCapture();else if(action==1)AppState.Paste();else if(action==2)AppState.ShowTranslator();}return IntPtr.Zero;}
        static void Quit(){try{AppState.Pins.PersistAll();AppState.Quitting=true;Application.Current.Shutdown();}catch(Exception ex){MessageBox.Show("贴图状态保存失败，未退出："+ex.Message,"Luma");}}
    }
}
