using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Mori;

namespace MoriDesk {
 // Guest ScenarioRunner only: hidden real MainWindow, isolated settings and
 // fixture libraries. Never Show a window or start browser/capture services.
 internal static class MainWindowRouteProbeTests {
  const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
  static T Field<T>(MainWindow main,string name){return (T)typeof(MainWindow).GetField(name,Hidden).GetValue(main);}
  static void Guard(MainWindow main,LibraryWriteGuard guard){typeof(MainWindow).GetField("libraryWrites",Hidden).SetValue(main,guard);}
  static void Probe(MainWindow main){
   try{typeof(MainWindow).GetMethod("CheckLibraryRoute",Hidden).Invoke(main,null);}
   catch(TargetInvocationException error){ExceptionDispatchInfo.Capture(error.InnerException??error).Throw();throw;}
  }
  static TextBlock Status(MainWindow main){return (TextBlock)main.Window.FindName("Status");}
  static void StopTimers(MainWindow main){foreach(string name in new[]{"externalTimer","searchTimer","layoutTimer","sizeSaveTimer","sidebarSaveTimer"})Field<DispatcherTimer>(main,name).Stop();}
  static byte[] IndexBytes(string root){using(var input=new FileStream(Path.Combine(root,".mori","library.db"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var bytes=new MemoryStream()){input.CopyTo(bytes);return bytes.ToArray();}}
  static void OtherWriter(string root,Action action){
   Exception failure=null;
   using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false)){
    var worker=new Thread(delegate(){
     try{using(var lease=LibraryTransfer.TryAcquireWrite(root)){
      if(lease==null)throw new IOException("Fixture writer could not own the root lease.");
      entered.Set();if(!release.WaitOne(15000))throw new TimeoutException("Fixture writer release timed out.");
     }}catch(Exception error){failure=error;}finally{entered.Set();}
    });
    worker.IsBackground=true;worker.Start();
    try{if(!entered.WaitOne(5000))throw new TimeoutException("Fixture writer did not start.");if(failure!=null)throw failure;action();}
    finally{release.Set();if(!worker.Join(10000))throw new TimeoutException("Fixture writer did not finish.");}
   }
   if(failure!=null)throw failure;
  }
  public static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);
   string data=Environment.GetEnvironmentVariable("MORI_DESK_DATA"),theme=Theme.Current;
   bool classic=Entry.ClassicEnabled,board=Entry.BoardEnabled,interactive=Entry.InteractiveShell;
   var previousLibrary=ThumbnailImage.CurrentLibrary;var context=SynchronizationContext.Current;
   var app=Application.Current??new Application();var shutdown=app.ShutdownMode;
   app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
   Environment.SetEnvironmentVariable("MORI_DESK_DATA",Path.Combine(root,"isolated-profile"));
   Entry.ClassicEnabled=true;Entry.BoardEnabled=false;Entry.InteractiveShell=false;
   SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
   string path=Path.Combine(root,"library"),settings=Path.Combine(Settings.ConfigDir,"settings.json");
   try{
    new Settings{LibraryPath=path,CloseToTray=false}.SaveTo(settings);
    using(var library=new Library(path)){
     var main=new MainWindow(library,true){PreviewMode=true};main.DeferDeskServices();StopTimers(main);
     var guard=Field<LibraryWriteGuard>(main,"libraryWrites");
     try{
      const string userMessage="Fixture user operation has completed.";
      Status(main).Text=userMessage;byte[] before=IndexBytes(path);
      OtherWriter(path,delegate{
       for(int attempt=0;attempt<3;attempt++)Probe(main);
       check(Status(main).Text==userMessage&&!guard.IsRetired,"busy periodic route probes preserve the current user message and do not falsely retire the library");
       bool admissionRefused=false;
       try{guard.ValidateCurrent();}catch(IOException error){admissionRefused=error.Message.Contains("本次尚未保存");}
       check(admissionRefused,"explicit write admission still refuses an occupied root lease instead of adopting monitor-only deferral");
       bool called=false,refused=false;
       try{guard.Run(delegate{called=true;library.CreateFolder("must-not-save","");});}
       catch(IOException error){refused=error.Message.Contains("本次尚未保存");}
       check(refused&&!called&&library.Folders().Count==0&&IndexBytes(path).SequenceEqual(before),"real guarded fixture write still refuses an occupied root lease explicitly before any database mutation");
       Status(main).Text="A newer fixture user message.";
       Probe(main);
       check(Status(main).Text=="A newer fixture user message.","another busy route probe cannot overwrite a later user-operation message");
      });
      Probe(main);
      check(Status(main).Text=="A newer fixture user message."&&!guard.IsRetired,"successful route recovery is silent and preserves the latest user-operation message");
      guard.Run(delegate{library.CreateFolder("after-release","");});
      check(library.Folders().Count==1&&library.Folders()[0].Name=="after-release","the original guarded write works normally once its thread-affine lease is released");

      // Inject only the current-route read callback; keep the real MainWindow
      // monitor and production mutex/check code unchanged in both red and green.
      foreach(bool denied in new[]{false,true}){
       string reason=denied?"fixture route permission denied":"fixture route read failed";
       var broken=new LibraryWriteGuard(path,delegate{if(denied)throw new UnauthorizedAccessException(reason);throw new IOException(reason);});
       Guard(main,broken);Status(main).Text="Before a genuine route-read failure.";Probe(main);
       check(Status(main).Text=="保存位置暂无法核对："+reason&&!broken.IsRetired,"a genuine "+(denied?"permission":"I/O")+" failure remains visible instead of being mistaken for lease contention");
       bool invoked=false,propagated=false;
       try{broken.Run(delegate{invoked=true;});}
       catch(Exception error){propagated=error.Message==reason&&(denied?error is UnauthorizedAccessException:error is IOException);}
       check(propagated&&!invoked,"a genuine "+(denied?"permission":"I/O")+" failure still rejects a real guarded write with its original exception");
       Guard(main,guard);Status(main).Text="User message after a genuine error.";Probe(main);
       check(Status(main).Text=="User message after a genuine error.","a later successful probe does not erase a user message posted after a genuine error");
      }

      Guard(main,guard);Settings.RememberLibraryAt(settings,Path.Combine(root,"other-library"));
      Status(main).Text="Before a real route change.";Probe(main);
      check(guard.IsRetired&&Status(main).Text==LibraryWriteGuard.RetiredMessage&&main.Window.Title.Contains("旧图库"),"a confirmed current-route change still retires the window and publishes the stopped-saving warning");
      OtherWriter(path,delegate{Status(main).Text="Must not hide retirement.";Probe(main);check(Status(main).Text==LibraryWriteGuard.RetiredMessage,"an already retired route stays visibly retired even while another writer owns its old root");});
      bool retiredCalled=false,retiredRejected=false;
      try{guard.Run(delegate{retiredCalled=true;});}catch(LibraryRouteChangedException){retiredRejected=true;}
      check(retiredRejected&&!retiredCalled&&library.Folders().Count==1,"retirement continues to fail closed without invoking or adding a later write");
      check(!main.Window.IsVisible&&!Field<bool>(main,"deskServicesActive"),"route-probe integration never displays a desktop window or starts browser/capture services");
     }finally{
      StopTimers(main);Settings.RememberLibraryAt(settings,path);Guard(main,new LibraryWriteGuard(path,()=>path));
      main.PendingInformationChoice=()=>MessageBoxResult.No;main.CloseForReplacement();
     }
    }
   }finally{
    Environment.SetEnvironmentVariable("MORI_DESK_DATA",data);Entry.ClassicEnabled=classic;Entry.BoardEnabled=board;Entry.InteractiveShell=interactive;
    Theme.Set(theme);ThumbnailImage.CurrentLibrary=previousLibrary;app.ShutdownMode=shutdown;SynchronizationContext.SetSynchronizationContext(context);
   }
  }
 }
}

