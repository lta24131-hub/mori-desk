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
    // Owned by the UI dispatcher. A ticket also rejects a timer callback that
    // was already queued when CancelCapture stopped its timer.
    internal sealed class CaptureStartGate {
        long generation,pending;
        bool executing;
        public bool IsPending {get{return pending!=0;}}
        public bool TryQueue(out long ticket){ticket=0;if(IsPending)return false;ticket=++generation;pending=ticket;return true;}
        public bool IsCurrent(long ticket){return ticket!=0&&pending==ticket;}
        public void Complete(long ticket){if(IsCurrent(ticket)){pending=0;executing=false;}}
        public void Cancel(){pending=0;executing=false;}
        public void Run(long ticket,Action action){if(!IsCurrent(ticket)||executing)return;executing=true;try{action();}finally{Complete(ticket);}}
        internal static int SettleDelayMilliseconds(bool directHotkey,bool hidWelcome){return directHotkey&&!hidWelcome?40:130;}
    }
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
        public static event Action ShortcutsChanged;
        public static void RefreshShortcuts(){var keys=Settings.Shortcuts;if(Tray!=null&&Tray.ContextMenuStrip!=null){var items=Tray.ContextMenuStrip.Items;UpdateTrayShortcut(items,"CaptureShortcut","截图  "+keys.Capture);UpdateTrayShortcut(items,"PinShortcut","找回贴图 / 粘贴  "+keys.Pin);UpdateTrayShortcut(items,"TogglePinsShortcut","显示 / 隐藏全部贴图  "+keys.TogglePins);UpdateTrayShortcut(items,"TranslateShortcut","输入翻译  "+keys.Translate);string tip="Nook · "+keys.Capture+" 截图 / "+keys.Pin+" 找回贴图";Tray.Text=tip.Length>63?tip.Substring(0,63):tip;}if(Welcome!=null)Welcome.RefreshShortcuts();if(ShortcutsChanged!=null)ShortcutsChanged();}
        static void UpdateTrayShortcut(Forms.ToolStripItemCollection items,string name,string text){var item=items[name];if(item!=null)item.Text=text;}
        static SettingsWindow settingsWindow;
        static WriteTranslationWindow writeTranslation;
        static readonly CaptureStartGate captureStart=new CaptureStartGate();
        static System.Windows.Threading.DispatcherTimer captureDelay;
        static CaptureWindow selectedCapture;
        static bool endingCapture;
        static EventHandler captureDisplayChanged;
        public static void ShowTranslator(){if(Captures.Count>0){Notify("请先完成或取消当前截图，再打开输入翻译。");return;}if(writeTranslation==null){writeTranslation=new WriteTranslationWindow();writeTranslation.Closed+=(s,e)=>writeTranslation=null;}writeTranslation.Show();if(writeTranslation.WindowState==WindowState.Minimized)writeTranslation.WindowState=WindowState.Normal;writeTranslation.Activate();}
        public static void ShowSettings(){if(MoriDesk.DeskRuntime.OpenSettings!=null){MoriDesk.DeskRuntime.OpenSettings();return;}if(settingsWindow==null){settingsWindow=new SettingsWindow();settingsWindow.Closed+=(s,e)=>settingsWindow=null;}settingsWindow.Show();settingsWindow.Activate();}
        public static void Notify(string message){if(MoriDesk.DeskRuntime.Notify!=null)MoriDesk.DeskRuntime.Notify(message);if(Tray!=null){Tray.BalloonTipTitle="Nook";Tray.BalloonTipText=message;Tray.ShowBalloonTip(3000);}}
        // Keep the other frozen monitor images until this session completes.
        // Returning to region selection must not take another screenshot of an
        // overlay, or force the user to restart the shortcut on the other screen.
        public static void ChooseCapture(CaptureWindow chosen){
            if(endingCapture||chosen==null||!Captures.Contains(chosen)||!chosen.HasCaptureRegion)return;
            if(selectedCapture!=null&&selectedCapture!=chosen)return;
            if(selectedCapture==chosen)return;
            selectedCapture=chosen;
            foreach(var w in Captures.ToList())if(w!=chosen)w.SuspendCaptureOverlay();
            chosen.FocusCaptureOverlay();
        }
        internal static void SyncCaptureSelection(CaptureWindow window){
            if(endingCapture||!Captures.Contains(window)||window.HasCaptureGesture)return;
            if(window.HasCaptureRegion){ChooseCapture(window);return;}
            if(selectedCapture!=window)return;
            selectedCapture=null;
            foreach(var w in Captures.ToList())w.ResumeCaptureOverlay();
            if(Captures.Any(w=>w.IsVisible))FocusCurrentCapture();
        }
        internal static bool CanEnterCapture(CaptureWindow window){return !endingCapture&&(selectedCapture==null||selectedCapture==window)&&!Captures.Any(w=>w!=window&&w.HasCaptureGesture);}
        internal static bool CanFocusCaptureFromPointer(CaptureWindow window){return Captures.Contains(window)&&selectedCapture==null&&!Captures.Any(w=>w.HasCaptureGesture);}
        internal static CaptureWindow CaptureForPoint(System.Drawing.Point point){
            if(selectedCapture!=null&&Captures.Contains(selectedCapture))return selectedCapture;
            return CaptureMonitorGeometry.AtPoint(Captures,point);
        }
        internal static void FocusCurrentCapture(){var chosen=CaptureForPoint(Forms.Cursor.Position);if(chosen!=null)chosen.FocusCaptureOverlay();}
        internal static void CaptureClosed(CaptureWindow window){
            if(!Captures.Remove(window))return;
            if(selectedCapture==window)selectedCapture=null;
            // Any completion/close belongs only to the session containing that
            // window. A late async result from a previous session is harmless.
            if(!endingCapture)EndCaptureWindows();
        }
        static void EndCaptureWindows(){
            if(endingCapture)return;endingCapture=true;
            try{foreach(var w in Captures.ToList())w.Close();}
            finally{selectedCapture=null;endingCapture=false;StopCaptureDisplayWatch();}
        }
        static void WatchCaptureDisplayChanges(){
            StopCaptureDisplayWatch();var dispatcher=Application.Current.Dispatcher;
            EventHandler handler=null;handler=(sender,args)=>dispatcher.BeginInvoke(new Action(()=>{
                if(captureDisplayChanged!=handler||Captures.Count==0)return;
                CancelCapture();Notify("屏幕布局已变化，当前截图已取消；请重新截图。");
            }));
            captureDisplayChanged=handler;Microsoft.Win32.SystemEvents.DisplaySettingsChanged+=handler;
        }
        static void StopCaptureDisplayWatch(){if(captureDisplayChanged==null)return;Microsoft.Win32.SystemEvents.DisplaySettingsChanged-=captureDisplayChanged;captureDisplayChanged=null;}
        public static void CancelCapture(){captureStart.Cancel();if(captureDelay!=null){captureDelay.Stop();captureDelay=null;}EndCaptureWindows();}
        public static void BeginCapture(string testImage=null,bool directHotkey=false){
            long timingOrigin=System.Diagnostics.Stopwatch.GetTimestamp();
            if(Quitting)return;
            if(Captures.Count>0){FocusCurrentCapture();return;}
            long ticket;if(!captureStart.TryQueue(out ticket))return;
            var timing=CaptureStartupTiming.Start(timingOrigin,directHotkey,testImage!=null);
            CaptureStartupTiming.Mark(timing,CaptureStartupStage.Queued);
            try{
                bool hidWelcome=Welcome!=null&&Welcome.IsVisible;
                if(Welcome!=null)Welcome.Hide();
                var delay=new System.Windows.Threading.DispatcherTimer {Interval=TimeSpan.FromMilliseconds(CaptureStartGate.SettleDelayMilliseconds(directHotkey,hidWelcome))};captureDelay=delay;
                delay.Tick+=(s,e)=>{delay.Stop();if(ReferenceEquals(captureDelay,delay))captureDelay=null;captureStart.Run(ticket,()=>{
                    CaptureStartupTiming.Mark(timing,CaptureStartupStage.Settle);
                    if(Quitting||Captures.Count>0)return;
                    var opened=new List<CaptureWindow>();
                    try{
                        var shots=new List<Snapshot>();
                        if(testImage!=null){var img=Snapshot.Load(testImage);var screen=Forms.Screen.PrimaryScreen;shots.Add(new Snapshot{Image=img,Bounds=new System.Drawing.Rectangle(screen.Bounds.X,screen.Bounds.Y,img.PixelWidth,img.PixelHeight)});}
                        else foreach(var screen in Forms.Screen.AllScreens)shots.Add(Snapshot.Screen(screen));
                        CaptureStartupTiming.Mark(timing,CaptureStartupStage.Screens,-1,shots.Count);
                        // Geometry must be captured before showing ANY monitor's overlay.
                        CaptureTargets targets=null;if(testImage==null)try{targets=CaptureTargets.Read();}catch{ /* Manual capture remains available. */ }
                        CaptureStartupTiming.Mark(timing,CaptureStartupStage.Targets);
                        WatchCaptureDisplayChanges();
                        foreach(var shot in shots){
                            if(Quitting||!captureStart.IsCurrent(ticket))break;
                            int windowIndex=opened.Count;
                            var w=new CaptureWindow(shot,targets);opened.Add(w);Captures.Add(w);
                            CaptureStartupTiming.Mark(timing,CaptureStartupStage.Constructed,windowIndex);
                            EventHandler rendered=null;rendered=(sender,args)=>{w.ContentRendered-=rendered;CaptureStartupTiming.Mark(timing,CaptureStartupStage.ContentRendered,windowIndex);};w.ContentRendered+=rendered;
                            w.Show();CaptureStartupTiming.Mark(timing,CaptureStartupStage.Shown,windowIndex);
                        }
                        FocusCurrentCapture();
                    }catch(Exception ex){CaptureStartupTiming.Mark(timing,CaptureStartupStage.Failed);foreach(var w in opened.ToList())try{w.Close();}catch{}StopCaptureDisplayWatch();MessageBox.Show("无法截屏："+ex.Message,"Nook");}
                });};delay.Start();
            }catch{CaptureStartupTiming.Mark(timing,CaptureStartupStage.Failed);if(captureDelay!=null){captureDelay.Stop();captureDelay=null;}captureStart.Complete(ticket);throw;}
        }
        public static void Paste(bool recover=true){try{if(recover&&Pins.Recover())return;if(Clipboard.ContainsImage())Pins.New(Clipboard.GetImage());else Notify("没有可找回的贴图。先截图并复制，再按 "+Settings.Shortcuts.Pin+" 贴到屏幕。");}catch(Exception ex){Notify("贴图失败："+ex.Message);}}
        public static void TogglePins(){try{if(Pins==null){Notify("贴图功能尚未就绪，请稍后再试。");return;}bool? visible=Pins.ToggleVisibility();Notify(!visible.HasValue?"当前没有贴图，按 "+Settings.Shortcuts.Pin+" 找回贴图或粘贴图片。":visible.Value?"已显示全部贴图。":"已隐藏全部贴图，再按 "+Settings.Shortcuts.TogglePins+" 显示。");}catch(Exception ex){Notify("切换贴图失败："+ex.Message);}}
        public static void ShowWelcome(){if(Welcome==null){Welcome=new WelcomeWindow();Welcome.Closed+=(s,e)=>Welcome=null;}Welcome.Show();Welcome.Activate();}
    }
    public sealed class WelcomeWindow : Window {
        readonly TextBlock shortcuts=new TextBlock {FontSize=14,LineHeight=28};
        readonly Button captureButton,writeButton;
        public void RefreshShortcuts(){var keys=AppState.Settings.Shortcuts;shortcuts.Text=keys.Capture+"     截图，选区后展开工具栏\n"+keys.Pin+"     找回贴图 / 粘贴剪贴板图片\n"+keys.TogglePins+"     显示 / 隐藏全部贴图\n滚轮   标注时调粗细，贴图时调大小";captureButton.Content="开始截图  "+keys.Capture;writeButton.Content="输入翻译 · 中译英  "+keys.Translate;}
        public WelcomeWindow(){Title="Nook";Icon=Ui.AppIcon;Width=450;Height=510;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterScreen;Background=Brushes.White;FontFamily=new FontFamily("Microsoft YaHei UI");
            var panel=new StackPanel {Margin=new Thickness(28)};panel.Children.Add(new TextBlock {Text="Nook",FontSize=30,FontWeight=FontWeights.SemiBold,Foreground=Ui.Ink});panel.Children.Add(new TextBlock {Text="截图 · 标注 · 贴图 · 文字翻译",Foreground=Ui.Brush("#718078"),Margin=new Thickness(0,6,0,23)});
            panel.Children.Add(shortcuts);
            captureButton=Ui.TextButton("",()=>AppState.BeginCapture(),true);panel.Children.Add(captureButton);
            writeButton=Ui.TextButton("",AppState.ShowTranslator);panel.Children.Add(writeButton);RefreshShortcuts();
            panel.Children.Add(Ui.TextButton("打开图片 · 识别 / 翻译",()=>{var dialog=new Microsoft.Win32.OpenFileDialog {Filter="图片|*.png;*.jpg;*.jpeg;*.bmp"};if(dialog.ShowDialog(this)==true){try{new TextWindow(Snapshot.Load(dialog.FileName)).Show();Hide();}catch(Exception ex){MessageBox.Show(this,ex.Message,"Nook");}}}));
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
            app.DispatcherUnhandledException+=(s,e)=>{MessageBox.Show("操作未完成："+e.Exception.Message,"Nook");e.Handled=true;};
            app.SessionEnding+=(s,e)=>{AppState.Quitting=true;try{AppState.Pins.PersistAll();}catch{}};
            app.Startup+=(s,e)=>{
                Trace("Startup");
                AppState.Pins=new PinStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"QingJie","pins"));
                Trace("Pins loaded");
                var param=new HwndSourceParameters("QingJie.Hotkeys"){WindowStyle=0,Width=0,Height=0,ParentWindow=new IntPtr(-3)};hotkeys=new HwndSource(param);hotkeys.AddHook(Hotkey);
                Trace("Hotkey source");
                AppState.Tray=new Forms.NotifyIcon {Text="Nook · F1 截图 / F3 贴图找回",Icon=System.Drawing.Icon.ExtractAssociatedIcon(typeof(Program).Assembly.Location),Visible=true};
                Trace("Tray");
                var menu=new Forms.ContextMenuStrip();menu.Items.Add("截图  F1",null,(o,a)=>AppState.BeginCapture()).Name="CaptureShortcut";menu.Items.Add("找回贴图 / 粘贴  F3",null,(o,a)=>AppState.Paste()).Name="PinShortcut";menu.Items.Add("显示 / 隐藏全部贴图  Shift+F3",null,(o,a)=>AppState.TogglePins()).Name="TogglePinsShortcut";menu.Items.Add("粘贴剪贴板图片",null,(o,a)=>AppState.Paste(false));menu.Items.Add("输入翻译 · 中译英  Ctrl+Alt+T",null,(o,a)=>AppState.ShowTranslator()).Name="TranslateShortcut";menu.Items.Add("设置",null,(o,a)=>AppState.ShowSettings());menu.Items.Add("使用说明",null,(o,a)=>AppState.ShowWelcome());menu.Items.Add(new Forms.ToolStripSeparator());menu.Items.Add("退出（下次恢复贴图）",null,(o,a)=>Quit());AppState.Tray.ContextMenuStrip=menu;AppState.Tray.DoubleClick+=(o,a)=>AppState.BeginCapture();
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
        static IntPtr Hotkey(IntPtr hwnd,int msg,IntPtr w,IntPtr l,ref bool handled){if(msg==0x312&&AppState.Hotkeys!=null){int id=w.ToInt32(),action=AppState.Hotkeys.ActionFor(id);if(action<0)return IntPtr.Zero;handled=true;if(AppState.ShortcutRecorder!=null){AppState.ShortcutRecorder(AppState.Hotkeys.KeyFor(id));return IntPtr.Zero;}if(action==0)AppState.BeginCapture(null,true);else if(action==1)AppState.Paste();else if(action==2)AppState.ShowTranslator();else if(action==3)AppState.TogglePins();}return IntPtr.Zero;}
        static void Quit(){try{AppState.Pins.PersistAll();AppState.Quitting=true;Application.Current.Shutdown();}catch(Exception ex){MessageBox.Show("贴图状态保存失败，未退出："+ex.Message,"Nook");}}
    }
}
