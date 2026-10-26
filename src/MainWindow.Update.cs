using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using MoriDesk;

namespace Mori {
 public partial class MainWindow {
  bool updateExitInProgress;
  void InitializeAppUpdate(){
   AppUpdateCoordinator.Register(Window,()=>PreviewMode,ExitForUpdateAsync);
   EventHandler rendered=null;rendered=(sender,args)=>{
    Window.ContentRendered-=rendered;
    if(!PreviewMode&&Entry.InteractiveShell&&Window.IsLoaded&&Window.IsVisible){
     // ReportStartupReady consumes these one-use environment values. Keep
     // their exact session for cleanup, which must wait for installer commit.
     string request=Environment.GetEnvironmentVariable("NOOK_UPDATE_REQUEST"),version=Environment.GetEnvironmentVariable("NOOK_UPDATE_EXPECTED_VERSION");
     bool updateLaunch=!string.IsNullOrEmpty(request)||!string.IsNullOrEmpty(version);
     bool startupReady=AppUpdateCoordinator.ReportStartupReady(Window);
     // An invalid/replayed update session must not turn into an ordinary
     // startup cleanup when the readiness check has rejected it.
     if(updateLaunch&&!startupReady)return;
     InstalledVersionMaintenance.Queue(request,version,library.Root,message=>{
      if(!Window.Dispatcher.HasShutdownStarted)Window.Dispatcher.BeginInvoke(new Action(()=>Status(message)));
     },()=>BrowserConnection.LocalState=="listening"||BrowserConnection.LocalState=="unpaired");
    }
   };
   Window.ContentRendered+=rendered;
  }
  async Task<bool> ExitForUpdateAsync(string backupDirectory,Func<bool> installerReady,CancellationToken token){
   if(PreviewMode||busy||updateExitInProgress||token.IsCancellationRequested)return false;
   updateExitInProgress=true;bool closed=false,sawClosing=false,waitingCanvas=false,cancelled=false,committed=false;
   CancelEventHandler closing=null;EventHandler onClosed=null;
   Action detach=()=>{Window.Closing-=closing;Window.Closed-=onClosed;};
   closing=(sender,args)=>{
    sawClosing=true;
    if(args.Cancel){waitingCanvas=matureCanvas!=null&&!matureCanvas.CanImportImages&&!matureCanvas.PreparedForExit;return;}
    waitingCanvas=false;
    try{
     if(cancelled||token.IsCancellationRequested||!installerReady()){
      args.Cancel=true;QingJie.AppState.Quitting=false;if(cancelled){detach();updateExitInProgress=false;}return;
     }
     if(!committed){
      // The usual debounce writers report failures without blocking close.
      // Updating must be stricter: persist the current layout with a throwing
      // save before advertising a complete recovery snapshot.
      var settings=Settings.Load();settings.ThumbnailSize=thumbnailSize;settings.LeftSidebarWidth=sidebars.Left;settings.RightSidebarWidth=sidebars.Right;settings.Save();
      AppUpdateMetadataBackup.Create(backupDirectory,Entry.DataRoot,library.Root,path=>library.Db.Exec("VACUUM INTO "+Database.Q(path)));committed=true;
     }
     if(token.IsCancellationRequested||!installerReady()){args.Cancel=true;QingJie.AppState.Quitting=false;}
    }catch(Exception error){args.Cancel=true;QingJie.AppState.Quitting=false;Status("更新前备份未完成，未退出："+error.Message);}
   };
   onClosed=(s,e)=>{closed=true;detach();};Window.Closing+=closing;Window.Closed+=onClosed;
   try{
    // Do not call Shutdown or bypass drafts. This is the same exit command as
    // the menu/tray: inspector choice -> canvas flush -> pin persistence.
    ExitApplication();
    while(!closed&&sawClosing&&waitingCanvas&&!token.IsCancellationRequested){
     if(matureCanvas==null||matureCanvas.CanImportImages)break;
     await Task.Delay(100,token);
    }
    return closed;
   }catch(OperationCanceledException){
    // Closing an owned Settings dialog cancels its UI token during the normal
    // main-window close. Once Closed fired the handoff has already committed;
    // do not tell the installer to cancel a successfully closed parent.
    return closed;
   }
   finally{
    cancelled=!closed;
    if(!closed){QingJie.AppState.Quitting=false;exitRequested=false;}
    // If a deferred canvas save is still in flight, retain a one-use guard:
    // its late Window.Close callback must not exit after update cancellation.
    if(!waitingCanvas||closed||matureCanvas==null||matureCanvas.CanImportImages){detach();updateExitInProgress=false;}
    else ObserveAbandonedUpdateExit(()=>closed,detach);
   }
  }
  async void ObserveAbandonedUpdateExit(Func<bool> closed,Action detach){
   try{while(!closed()&&matureCanvas!=null&&!matureCanvas.CanImportImages)await Task.Delay(100);}
   finally{detach();updateExitInProgress=false;}
  }
 }
}
