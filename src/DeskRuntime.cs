using System;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using QingJie;
namespace MoriDesk {
 public static class DeskRuntime {
  static HwndSource source;
  static CaptureLease lease;static System.Windows.Threading.DispatcherTimer retry;static string failure;
  public static Func<BitmapSource,System.Threading.Tasks.Task<bool>> CollectImage;
  public static Action<string> Notify;
  public static bool Started;
  public static void Start(bool preview,string libraryRoot){
   if(Started)return;
   try{
   if(!preview&&lease==null){
    lease=CaptureLease.TryAcquire(Path.Combine(Entry.DataRoot,"capture"));
    if(lease==null){if(retry==null){retry=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromSeconds(2)};retry.Tick+=(s,e)=>Start(preview,libraryRoot);retry.Start();if(Notify!=null)Notify("截图与翻译由另一个 Mori Desk 窗口使用；它完全退出后，本窗口会自动接管。");}return;}
   }
   if(retry!=null){retry.Stop();retry=null;}
   if(preview){Preferences.TestFilePath=Path.Combine(libraryRoot,".mori","test-capture.json");OcrLanguagePacks.TestFolder=Path.Combine(libraryRoot,".mori","test-languages");OfflinePacks.TestFolder=Path.Combine(libraryRoot,".mori","test-offline");}
   AppState.Settings=Preferences.Load();AppState.Quitting=false;
   AppState.Pins=new PinStore(Path.Combine(preview?Path.Combine(libraryRoot,".mori"):Entry.DataRoot,"capture","pins"));
   if(!preview){
    source=new HwndSource(new HwndSourceParameters("MoriDesk.Hotkeys"){WindowStyle=0,Width=0,Height=0,ParentWindow=new IntPtr(-3)});source.AddHook(Hotkey);
    AppState.Hotkeys=new HotkeyManager(source.Handle);string error=AppState.Hotkeys.Start(AppState.Settings.Shortcuts);
    if(error!=null&&Notify!=null)Notify("快捷键冲突（旧 Luma 可能仍在运行）。可从“工具”使用或在设置中换快捷键。 "+error);
    AppState.Pins.Restore();
   }
   Started=true;failure=null;
   }catch(Exception ex){ReleaseTools();failure="截图工具暂时无法启动："+ex.Message;if(Notify!=null)Notify(failure);}
  }
  static IntPtr Hotkey(IntPtr hwnd,int message,IntPtr w,IntPtr l,ref bool handled){if(message!=0x312||AppState.Hotkeys==null)return IntPtr.Zero;int action=AppState.Hotkeys.ActionFor(w.ToInt32());if(action<0)return IntPtr.Zero;handled=true;if(AppState.ShortcutRecorder!=null){AppState.ShortcutRecorder(AppState.Hotkeys.KeyFor(w.ToInt32()));return IntPtr.Zero;}if(action==0)AppState.BeginCapture();if(action==1)AppState.Paste();if(action==2)AppState.ShowTranslator();return IntPtr.Zero;}
  public static bool PrepareExit(Window owner){if(!Started)return true;if(AppState.Captures.Count>0&&MessageBox.Show(owner,"还有未完成的截图，退出将关闭当前截图。已保存的贴图会保留。是否退出？","退出 Mori Desk",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return false;try{AppState.Pins.PersistAll();AppState.Quitting=true;return true;}catch(Exception ex){MessageBox.Show(owner,"贴图状态保存失败，暂未退出："+ex.Message);return false;}}
  public static bool RequireTools(){if(Started)return true;if(Notify!=null)Notify(failure??"请在正在使用截图工具的 Mori Desk 窗口操作，或完全退出该窗口；此窗口会自动接管。关闭右上角 × 只是收进托盘。");return false;}
  static void ReleaseTools(){if(retry!=null){retry.Stop();retry=null;}if(AppState.Hotkeys!=null){AppState.Hotkeys.Dispose();AppState.Hotkeys=null;}if(source!=null){source.Dispose();source=null;}if(lease!=null){lease.Dispose();lease=null;}Started=false;}
  public static void Stop(){ReleaseTools();CollectImage=null;Notify=null;}
 }
}
