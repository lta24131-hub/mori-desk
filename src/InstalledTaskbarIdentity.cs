using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;

namespace MoriDesk {
 // Window-scoped identity avoids changing portable viewers, worker processes
 // and preview fixtures, even when they share this assembly.
 internal sealed class InstalledTaskbarIdentity {
  readonly Window window;readonly Func<bool> preview;readonly Func<string> resolve;
  HwndSource source;IntPtr handle;bool applied,closed;
  internal InstalledTaskbarIdentity(Window window,Func<bool> preview):this(window,preview,ResolveCurrentLauncher){}
  internal InstalledTaskbarIdentity(Window window,Func<bool> preview,Func<string> resolve){
   if(window==null||preview==null||resolve==null)throw new ArgumentNullException();
   this.window=window;this.preview=preview;this.resolve=resolve;
   window.SourceInitialized+=SourceInitialized;window.Closed+=Closed;
   if(new WindowInteropHelper(window).Handle!=IntPtr.Zero)SourceInitialized(null,EventArgs.Empty);
  }
  void SourceInitialized(object sender,EventArgs args){
   window.SourceInitialized-=SourceInitialized;
   try{handle=new WindowInteropHelper(window).Handle;source=HwndSource.FromHwnd(handle);if(source!=null)source.AddHook(WindowMessage);Refresh();}
   catch(Exception error){Trace.WriteLine("Nook taskbar window binding unavailable: "+error.GetType().Name);}
  }
  internal void Refresh(){
   if(closed||applied||handle==IntPtr.Zero)return;
   try{if(preview())return;string launcher=resolve();if(launcher==null)return;ShellTaskbarProperties.SetWindow(handle,launcher);applied=true;}
   catch(Exception error){Trace.WriteLine("Nook taskbar identity unavailable: "+error.GetType().Name);}
  }
  IntPtr WindowMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled){
   if(message==2){ // WM_DESTROY is sent only after a cancelable close succeeds.
    if(applied)try{ShellTaskbarProperties.ClearWindow(hwnd);}catch(Exception error){Trace.WriteLine("Nook taskbar identity cleanup: "+error.GetType().Name);}
    applied=false;handle=IntPtr.Zero;
   }
   return IntPtr.Zero;
  }
  void Closed(object sender,EventArgs args){closed=true;window.SourceInitialized-=SourceInitialized;window.Closed-=Closed;if(source!=null){try{source.RemoveHook(WindowMessage);}catch(ObjectDisposedException){}source=null;}}
  static string ResolveCurrentLauncher(){
   if(!Entry.InteractiveShell||!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MORI_DESK_DATA")))return null;
   bool pending=InstalledApplicationRouting.IsPendingCandidate;
   return ResolveInScope(InstalledApplicationState.ProgramRoot,typeof(Entry).Assembly.Location,false,true,null,pending,PhysicalUserContext.RequireInstalledContext);
  }
  // Reads synthetic installed fixtures in tests; production always supplies
  // the real executable/root/context. This never changes the active record.
  internal static string ResolveInScope(string root,string executable,bool preview,bool interactive,string dataOverride,bool pending,Action requireContext){
   if(preview||!interactive||!string.IsNullOrEmpty(dataOverride)||!InstalledApplicationRouting.ManagedPayload(executable,root))return null;
   requireContext();
   // pending is obtained only from TryRoute's validated update session. Its
   // window can be pinned before commit: the stable launcher will resolve the
   // committed version after the installer releases its lease, never this
   // candidate's version directory. This grants no service/registry ownership.
   if(!pending){var active=InstalledApplicationState.Read(root);if(active==null||!string.Equals(InstalledApplicationState.ResolveExecutable(root,active),Path.GetFullPath(executable),StringComparison.OrdinalIgnoreCase))return null;}
   string launcher=InstalledApplicationState.LauncherPath(root);AppUpdateCoordinator.PlainPath(launcher);
   if(!File.Exists(launcher))return null;var identity=FileVersionInfo.GetVersionInfo(launcher);
   return identity.ProductName=="Nook"&&string.Equals(identity.OriginalFilename,"Mori.exe",StringComparison.OrdinalIgnoreCase)?launcher:null;
  }
 }
}
