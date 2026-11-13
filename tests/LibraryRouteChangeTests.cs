using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Mori;

namespace MoriDesk {
 // Isolated settings and generated library files; no activation child is
 // launched and no window is shown. Real transfer/settings locks are retained.
 internal static class LibraryRouteChangeTests {
  const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
  static object Call(MainWindow main,string name,params object[] args){try{return typeof(MainWindow).GetMethod(name,Hidden).Invoke(main,args);}catch(TargetInvocationException error){ExceptionDispatchInfo.Capture(error.InnerException??error).Throw();throw;}}
  static void StopTimers(MainWindow main){foreach(string name in new[]{"externalTimer","searchTimer","layoutTimer","sizeSaveTimer","sidebarSaveTimer"})((DispatcherTimer)typeof(MainWindow).GetField(name,Hidden).GetValue(main)).Stop();}
  static bool Retired(Action action){try{action();return false;}catch(LibraryRouteChangedException){return true;}}
  static string Status(MainWindow main){return ((TextBlock)main.Window.FindName("Status")).Text;}
  static void WithMain(Library library,Action<MainWindow> action){var main=new MainWindow(library,true){PreviewMode=true};main.DeferDeskServices();StopTimers(main);try{action(main);}finally{StopTimers(main);main.PendingInformationChoice=()=>MessageBoxResult.No;main.CloseForReplacement();}}
  static Exception OtherRoute(string settings,string destination){
   var task=Task.Run(()=>{try{Settings.RememberLibraryAt(settings,destination);return null;}catch(Exception error){return error;}});
   if(!task.Wait(3000))throw new TimeoutException("A conflicting route writer blocked instead of refusing the occupied source lease.");return task.Result;
  }
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);string oldData=Environment.GetEnvironmentVariable("MORI_DESK_DATA"),oldTheme=Theme.Current;
   bool oldClassic=Entry.ClassicEnabled,oldBoard=Entry.BoardEnabled,oldInteractive=Entry.InteractiveShell;var oldLibrary=ThumbnailImage.CurrentLibrary;var oldContext=SynchronizationContext.Current;
   var app=Application.Current??new Application();var oldShutdown=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
   Environment.SetEnvironmentVariable("MORI_DESK_DATA",Path.Combine(root,"isolated-profile"));Entry.ClassicEnabled=true;Entry.BoardEnabled=false;Entry.InteractiveShell=false;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
   string settings=Path.Combine(Settings.ConfigDir,"settings.json"),original=Path.Combine(root,"source"),other=Path.Combine(root,"new-current"),target=Path.Combine(root,"copied-target"),race=Path.Combine(root,"racing-route"),image=Path.Combine(root,"source.png");
   try{
    new Settings{LibraryPath=original,CloseToTray=false}.SaveTo(settings);
    using(var bitmap=new Bitmap(24,13)){using(var graphics=Graphics.FromImage(bitmap))graphics.Clear(Color.MediumPurple);bitmap.Save(image,ImageFormat.Png);}
    using(var library=new Library(original)){
     var asset=library.Import(image,"","原始素材","https://example.com/source","原标签","原备注").Item;byte[] sourceBytes=File.ReadAllBytes(library.FullPath(asset));
     WithMain(library,main=>{
      check((bool)Call(main,"PrepareLibraryRouteChange"),"a current library window can pass the early route-change admission check");
      Settings.RememberLibraryAt(settings,other);byte[] before=File.ReadAllBytes(settings);bool called=false;
      check(Retired(()=>main.RunLibraryRouteChange(()=>{called=true;LibraryTransfer.Transfer(library,target,null,()=>false,p=>Settings.RememberLibraryAt(settings,p));}))&&!called,"a route that changes after UI admission is revalidated before any copy or activation executes");
      check(!Directory.Exists(target)&&!Directory.GetDirectories(root,".mori-transfer-*.partial").Any()&&File.ReadAllBytes(settings).SequenceEqual(before),"a retired copy request creates no target/stage and cannot overwrite the newly selected route");
      check(!(bool)Call(main,"PrepareLibraryRouteChange")&&Status(main)==LibraryWriteGuard.RetiredMessage,"stale route admission explains why the old library cannot become current again");
      int windows=app.Windows.Count;Call(main,"ChangeStorage",new StorageChangeRequest{Path=target});Call(main,"SwitchLibrary");
      check(app.Windows.Count==windows&&!main.Window.IsVisible&&File.ReadAllBytes(settings).SequenceEqual(before)&&!Directory.Exists(target),"actual stale ChangeStorage and SwitchLibrary entry points return before a picker, transfer dialog or activation child");
     });
     Settings.RememberLibraryAt(settings,original);
     WithMain(library,main=>{
      bool progressObserved=false,committed=false;byte[] before=File.ReadAllBytes(settings);
      main.RunLibraryRouteChange(()=>LibraryTransfer.Transfer(library,target,message=>{
       if(progressObserved)return;progressObserved=true;var denied=OtherRoute(settings,race);
       check(denied is IOException&&denied.Message.Contains("暂未切换")&&File.ReadAllBytes(settings).SequenceEqual(before),"another thread cannot switch the source route while the real verified copy owns its lease");
      },()=>false,path=>{
       check(LibraryTransfer.SameRoot(path,target)&&File.Exists(Path.Combine(path,".mori","library.db")),"activation sees the committed copy only after the actual transfer verification");
       var denied=OtherRoute(settings,race);check(denied is IOException&&LibraryTransfer.SameRoot(Settings.LoadFrom(settings).LibraryPath,original),"the source lease also excludes competing route changes during final activation");
       Settings.RememberLibraryAt(settings,path);committed=true;
      }));
      check(progressObserved&&committed&&LibraryTransfer.SameRoot(Settings.LoadFrom(settings).LibraryPath,target),"same-thread nested transfer and settings leases successfully commit the verified new route");
      using(var copied=new Library(target)){var saved=copied.All().Single();check(saved.Id==asset.Id&&saved.Name==asset.Name&&saved.Tags==asset.Tags&&saved.Notes==asset.Notes&&File.ReadAllBytes(copied.FullPath(saved)).SequenceEqual(sourceBytes),"route handoff preserves the actual original bytes and saved asset metadata");}
      bool called=false;check(Retired(()=>main.RunLibraryRouteChange(()=>{called=true;Settings.RememberLibraryAt(settings,race);}))&&!called,"the old source window cannot perform a second route change after its own successful handoff");
     });
     Settings.RememberLibraryAt(settings,original);
     WithMain(library,main=>{
      string failed=Path.Combine(root,"activation-failed");byte[] before=File.ReadAllBytes(settings);bool rejected=false;
      try{main.RunLibraryRouteChange(()=>LibraryTransfer.Transfer(library,failed,null,()=>false,path=>{throw new IOException("Synthetic activation failure.");}));}catch(IOException error){rejected=error.Message.Contains("Synthetic activation failure.");}
      check(rejected&&File.ReadAllBytes(settings).SequenceEqual(before)&&File.Exists(Path.Combine(failed,".mori","library.db")),"failed activation preserves the original route and verified copied target for recovery");
      bool cancelled=false;string canceled=Path.Combine(root,"cancelled-target");try{main.RunLibraryRouteChange(()=>LibraryTransfer.Transfer(library,canceled,null,()=>true,path=>Settings.RememberLibraryAt(settings,path)));}catch(OperationCanceledException){cancelled=true;}
      check(cancelled&&!Directory.Exists(canceled)&&File.ReadAllBytes(settings).SequenceEqual(before),"cancelled route copy never changes settings or creates the destination");
      check(OtherRoute(settings,other)==null&&LibraryTransfer.SameRoot(Settings.LoadFrom(settings).LibraryPath,other),"failure and cancellation release every recursively acquired source lease for a later route change");
     });
     check(File.ReadAllBytes(library.FullPath(asset)).SequenceEqual(sourceBytes)&&library.All().Count==1,"all guarded route scenarios leave the source library and original bytes intact");
    }
   }finally{Environment.SetEnvironmentVariable("MORI_DESK_DATA",oldData);Entry.ClassicEnabled=oldClassic;Entry.BoardEnabled=oldBoard;Entry.InteractiveShell=oldInteractive;Theme.Set(oldTheme);ThumbnailImage.CurrentLibrary=oldLibrary;app.ShutdownMode=oldShutdown;SynchronizationContext.SetSynchronizationContext(oldContext);}
  }
 }
}
