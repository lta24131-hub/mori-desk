using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Mori;

namespace MoriDesk {
 // Offscreen WPF controls only. No Window/Application construction, HWND,
 // desktop input, browser, real network, real credential or installer launch.
 internal static class AppUpdatePanelTests {
  static readonly byte[] Bytes = Encoding.UTF8.GetBytes("PANEL SYNTHETIC INSTALLER; NEVER EXECUTE");
  sealed class Handler : HttpMessageHandler {
   internal readonly Queue<Func<CancellationToken,Task<HttpResponseMessage>>> Replies = new Queue<Func<CancellationToken,Task<HttpResponseMessage>>>();
   internal int Calls; internal bool Anonymous=true;
   internal void Reply(HttpResponseMessage response) { Replies.Enqueue(token=>Task.FromResult(response)); }
   protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) {
    Calls++;Anonymous=Anonymous&&request.Headers.Authorization==null;
    if(request.RequestUri.Host!="api.github.com")throw new Exception("Panel test attempted an unexpected host");
    if(Replies.Count==0)throw new Exception("No fake reply; network is forbidden in panel tests");
    return Replies.Dequeue()(token);
   }
  }
  sealed class HeldProgressContext : SynchronizationContext {
   readonly Dispatcher dispatcher;
   internal readonly Queue<Action> Held=new Queue<Action>();
   internal HeldProgressContext(Dispatcher value){dispatcher=value;}
   public override void Post(SendOrPostCallback callback,object state){
    if(state is UpdateProgress)Held.Enqueue(()=>callback(state));
    else dispatcher.BeginInvoke(DispatcherPriority.Normal,new Action(()=>callback(state)));
   }
   internal void Release(){while(Held.Count>0)Held.Dequeue()();}
  }
  static string Hash(){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Bytes)).Replace("-","").ToLowerInvariant();}
  static UpdateRelease Release(){return new UpdateRelease{Version=new Version(99,2,3,0),Tag="v99.2.3-preview",Notes="Synthetic notes\nNo external operation is allowed.",HtmlUrl="https://github.com/lta24131-hub/mori-desk/releases/tag/v99.2.3-preview",AssetId=1001,Size=Bytes.Length,ExpectedSha256=Hash()};}
  static HttpResponseMessage Response(byte[] bytes){return new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};}
  static HttpResponseMessage ReleaseResponse(){return Response(Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new[]{new{tag_name="v99.2.3-preview",draft=false,prerelease=true,body="Synthetic notes",assets=new[]{new{name="Nook-Setup.exe",id=1001,size=Bytes.Length,digest="sha256:"+Hash()}}}})));}
  static Task<string> NoHandoff(Window owner,string path,Version version,string hash,CancellationToken token){throw new Exception("This scenario must not invoke an installer handoff");}
  static AppUpdatePanel Panel(string root,bool preview,Func<AppUpdateService> factory,Func<Window,string,Version,string,CancellationToken,Task<string>> handoff=null,bool managed=true,Settings initial=null,Action<Settings> persist=null){return new AppUpdatePanel(null,preview,factory,handoff??NoHandoff,root,()=>managed,initial??new Settings(),persist??(settings=>{}));}
  static void Pump(Task task){
   DateTime deadline=DateTime.UtcNow.AddSeconds(8);
   while(!task.IsCompleted){if(DateTime.UtcNow>deadline)throw new TimeoutException("Offscreen update test did not finish");Drain();Thread.Sleep(1);}
   task.GetAwaiter().GetResult();Drain();
  }
  static void Drain(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
  static IEnumerable<T> Children<T>(DependencyObject root)where T:DependencyObject{
   if(root is T)yield return (T)root;
   foreach(object child in LogicalTreeHelper.GetChildren(root)){var dependency=child as DependencyObject;if(dependency!=null)foreach(var match in Children<T>(dependency))yield return match;}
  }
  static void Style(AppUpdatePanel panel){panel.Resources["Canvas"]=Brushes.White;panel.Resources["Text"]=Brushes.Black;panel.Resources["Muted"]=Brushes.Gray;panel.Resources["Control"]=Brushes.WhiteSmoke;panel.Resources["Border"]=Brushes.LightGray;panel.Resources["Primary"]=Brushes.MediumPurple;panel.Resources["OnPrimary"]=Brushes.White;panel.Resources["Accent"]=Brushes.MediumPurple;panel.Resources["Divider"]=Brushes.LightGray;}
  internal static void Run(string root,Action<bool,string> check){
   if(Thread.CurrentThread.GetApartmentState()!=ApartmentState.STA)throw new InvalidOperationException("Panel tests require STA but never a visible window");
   string fixtures=Path.Combine(root,"app-update-panel-fixtures");Directory.CreateDirectory(fixtures);
   var previous=SynchronizationContext.Current;
   SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
   try{Preview(fixtures,check);PreviewPreference(fixtures,check);ManualUpgrade(fixtures,check);CheckLifecycle(fixtures,check);CheckCancellation(fixtures,check);InstallLifecycle(fixtures,check);HandoffCancellation(fixtures,check);StaleProgress(fixtures,check);}
   finally{SynchronizationContext.SetSynchronizationContext(previous);}
  }
  static void Preview(string root,Action<bool,string> check){
   int factories=0,handoffs=0;
   using(var panel=Panel(Path.Combine(root,"preview"),true,()=>{factories++;throw new Exception("Preview must not create update service");},(owner,path,version,hash,token)=>{handoffs++;return Task.FromResult("not allowed");})){
    Style(panel);
    check(factories==0&&!panel.IsBusy&&!panel.IsDisposed&&panel.Previews.IsChecked==true,"opening preview update settings is passive and preview versions are an explicit visible option");
    check(!panel.Check.IsEnabled&&!panel.Previews.IsEnabled&&!panel.Install.IsEnabled&&panel.Cancel.Visibility==Visibility.Collapsed,"preview disables update inputs and hides cancellation");
    panel.ApplyRelease(Release());Pump(panel.CheckNowAsync());Pump(panel.InstallNowAsync());
    check(factories==0&&handoffs==0&&!panel.Install.IsEnabled,"preview cannot check/download/handoff even when release data is shown programmatically");
    check(panel.AvailableRelease.Tag=="v99.2.3-preview"&&panel.Notes.Text.Contains("Synthetic notes")&&panel.Install.Visibility==Visibility.Visible,"release information and notes render independently from service or network calls");
    foreach(double width in new[]{360d,780d}){
     panel.Measure(new Size(width,760));panel.Arrange(new Rect(0,0,width,760));panel.UpdateLayout();
     check(panel.ActualWidth==width&&panel.DesiredSize.Width<=width+0.01&&panel.Check.ActualHeight>=36&&PresentationSource.FromVisual(panel)==null,"offscreen update panel fits measured width without creating a presentation source / "+width);
    }
    var project=Children<Button>(panel).Single(b=>Convert.ToString(b.Content)=="在 GitHub 查看项目");
    check(!project.IsEnabled,"preview also disables external GitHub browser action");
    panel.ApplyRelease(null);check(panel.AvailableRelease==null&&panel.Notes.Visibility==Visibility.Collapsed&&panel.Install.Visibility==Visibility.Collapsed&&panel.Notes.Text=="","clearing release removes stale notes and installer affordance");
    panel.Dispose();panel.Dispose();Pump(panel.CheckNowAsync());Pump(panel.InstallNowAsync());
    check(panel.IsDisposed&&!panel.Check.IsEnabled&&!panel.Install.IsEnabled&&factories==0,"disposed preview is idempotent and cannot revive update operations");
   }
   check(AppUpdatePanel.SizeLabel(0)=="1 KB"&&AppUpdatePanel.SizeLabel(1024)=="1 KB"&&AppUpdatePanel.SizeLabel(1572864)=="1.5 MB","installer size labels are compact and do not confuse bytes with megabytes");
  }
  static void PreviewPreference(string root,Action<bool,string> check){
   string settingsPath=Path.Combine(root,"update-preference.json"),library=Path.Combine(root,"preference-library");
   File.WriteAllText(settingsPath,new JavaScriptSerializer().Serialize(new{LibraryPath=library}));
   var initial=Settings.LoadFrom(settingsPath);check(initial.IncludePreviewUpdates,"existing settings keep the established preview-release default");
   initial.IncludePreviewUpdates=false;initial.SaveTo(settingsPath);initial=Settings.LoadFrom(settingsPath);
   int factories=0,saves=0;Func<AppUpdateService> noNetwork=()=>{factories++;throw new Exception("Preference editing must remain offline");};
   using(var panel=Panel(root,false,noNetwork,initial:initial,persist:value=>{saves++;value.SaveTo(settingsPath);} )){
    check(panel.Previews.IsChecked==false&&factories==0&&saves==0,"opening update settings restores the saved release range without writes or network");
    var current=Settings.LoadFrom(settingsPath);current.LibraryPath=Path.Combine(root,"new-preference-library");current.Theme="light";current.SaveTo(settingsPath);
    panel.ApplyRelease(Release());panel.Previews.IsChecked=true;panel.Previews.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    var saved=Settings.LoadFrom(settingsPath);
    check(saved.IncludePreviewUpdates&&saved.LibraryPath==current.LibraryPath&&saved.Theme=="light"&&saves==1&&factories==0,"changing release range preserves a concurrent library route and theme change");
    check(panel.AvailableRelease==null&&panel.Status.Text.Contains("已保存"),"a committed range change invalidates the old release choice");
   }
   using(var reopened=Panel(root,false,noNetwork,initial:Settings.LoadFrom(settingsPath)))check(reopened.Previews.IsChecked==true,"reopening update settings restores the last committed choice");
   var draft=Settings.LoadFrom(settingsPath);bool fail=true;int attempts=0;
   using(var panel=Panel(root,false,noNetwork,initial:draft,persist:value=>{attempts++;if(fail)throw new IOException("synthetic settings lock");value.SaveTo(settingsPath);} )){
    var release=Release();panel.ApplyRelease(release);panel.Previews.IsChecked=false;panel.Previews.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    check(draft.IncludePreviewUpdates&&panel.Previews.IsChecked==true&&Settings.LoadFrom(settingsPath).IncludePreviewUpdates&&panel.AvailableRelease==release&&panel.Status.Text.Contains("未保存"),"failed preference write restores the control and draft while retaining the valid release choice");
    fail=false;panel.Previews.IsChecked=false;panel.Previews.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    check(attempts==2&&!Settings.LoadFrom(settingsPath).IncludePreviewUpdates&&panel.AvailableRelease==null,"a failed preference write can be retried and persists the user's new choice");
    panel.Dispose();panel.Previews.IsChecked=true;panel.Previews.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    check(attempts==2&&panel.Previews.IsChecked==false,"disposed settings cannot save a later programmatic range change");
   }
   using(var panel=Panel(root,true,noNetwork,initial:new Settings{IncludePreviewUpdates=false},persist:value=>{throw new Exception("Preview cannot save settings");})){
    panel.Previews.IsChecked=true;panel.Previews.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    check(panel.Previews.IsChecked==false&&factories==0,"preview restores its injected choice and never reads or writes personal settings");
   }
   var pending=new TaskCompletionSource<HttpResponseMessage>();var handler=new Handler();handler.Replies.Enqueue(token=>pending.Task);int busySaves=0;
   using(var panel=Panel(root,false,()=>new AppUpdateService(handler),persist:value=>busySaves++)){
    Task task=panel.CheckNowAsync();panel.Previews.IsChecked=false;panel.Previews.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    check(busySaves==0&&panel.Previews.IsChecked==true,"an in-flight check keeps the committed range even under programmatic input");
    pending.SetResult(ReleaseResponse());Pump(task);check(panel.AvailableRelease!=null,"the active check completes with its unchanged release range");
   }
  }
  static void ManualUpgrade(string root,Action<bool,string> check){
   string programs=@"C:\Generated\Programs\MoriDesk",installed=Path.Combine(programs,"0.1.24-fixture","Mori.exe");
   check(AppUpdatePanel.IsManagedUpdateLocation(installed,programs),"standard managed installation retains automatic update eligibility");
   check(AppUpdatePanel.IsManagedUpdateLocation(installed.ToUpperInvariant(),programs.ToLowerInvariant()+"\\"),"managed installation preflight normalizes case and trailing separator");
   foreach(string executable in new[]{null,"",@"D:\Nook-portable\Mori.exe",programs+@"-other\Mori.exe",Path.Combine(programs,"..","Other","Mori.exe"),Path.Combine(programs,"0.1.24-fixture","Nook.exe"),@"\\server\share\Mori.exe",@"C:relative\Mori.exe",installed+":stream"})
    check(!AppUpdatePanel.IsManagedUpdateLocation(executable,programs),"portable, external or invalid parent path cannot expose automatic installation");
   foreach(string programRoot in new[]{null,""})check(!AppUpdatePanel.IsManagedUpdateLocation(installed,programRoot),"unknown managed installation root fails closed");
   var handler=new Handler();handler.Reply(ReleaseResponse());int factories=0,handoffs=0;string downloadRoot=Path.Combine(root,"manual-upgrade-no-download");
   using(var panel=Panel(downloadRoot,false,()=>{factories++;return new AppUpdateService(handler);},(owner,path,version,hash,token)=>{handoffs++;return Task.FromResult("must not install");},false)){
    check(factories==0&&panel.Check.IsEnabled&&panel.Status.Text.Contains("手动升级")&&!panel.Install.IsEnabled,"portable settings explain manual upgrade before any network or download");
    check(Children<TextBlock>(panel).Any(label=>label.Text.Contains("便携版")&&label.Text.Contains("不要只替换 Mori.exe")),"portable guidance remains visible independently of changing operation status");
    check(Children<Button>(panel).Single(button=>Convert.ToString(button.Content)=="前往 GitHub 手动升级").IsEnabled,"portable mode offers a manual releases-page action without automatically opening a browser");
    Pump(panel.CheckNowAsync());
    check(factories==1&&handler.Calls==1&&panel.AvailableRelease!=null&&panel.Notes.Text.Contains("Synthetic notes")&&panel.Status.Text.Contains("手动升级"),"portable mode may discover release notes while clearly directing manual upgrade");
    check(panel.Install.Visibility==Visibility.Collapsed&&!panel.Install.IsEnabled,"available release does not expose a doomed portable auto-install button");
    Pump(panel.InstallNowAsync());
    check(factories==1&&handler.Calls==1&&handoffs==0&&!Directory.Exists(downloadRoot)&&!panel.IsBusy&&panel.Status.Text.Contains("便携版"),"programmatic portable install is rejected before service creation, staging writes or installer handoff");
   }
  }
  static void CheckLifecycle(string root,Action<bool,string> check){
   var reply=new TaskCompletionSource<HttpResponseMessage>();var handler=new Handler();handler.Replies.Enqueue(token=>reply.Task);int factories=0;
   using(var panel=Panel(Path.Combine(root,"check"),false,()=>{factories++;return new AppUpdateService(handler);})){
    check(factories==0&&panel.Check.IsEnabled&&!panel.IsBusy&&panel.Install.Visibility==Visibility.Collapsed,"opening live update settings neither creates service nor discovers releases automatically");
    Task pending=panel.CheckNowAsync();
    check(panel.IsBusy&&!panel.Check.IsEnabled&&!panel.Previews.IsEnabled&&panel.Cancel.IsEnabled&&panel.Cancel.Visibility==Visibility.Visible,"checking disables conflicting controls and exposes cancellation");
    Pump(panel.CheckNowAsync());check(factories==1&&handler.Calls==1,"rapid duplicate check does not create parallel requests");
    reply.SetResult(ReleaseResponse());Pump(pending);
    check(!panel.IsBusy&&panel.Check.IsEnabled&&panel.Install.IsEnabled&&panel.AvailableRelease.Tag=="v99.2.3-preview"&&panel.Notes.Text=="Synthetic notes","successful check exposes exact release and exits busy state");
    check(handler.Anonymous&&panel.Cancel.Visibility==Visibility.Collapsed,"public release check remains anonymous and clears cancellation controls");
    panel.Previews.IsChecked=false;panel.Previews.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    check(panel.AvailableRelease==null&&panel.Install.Visibility==Visibility.Collapsed&&panel.Status.Text.Contains("重新检查"),"changing preview filter invalidates an earlier install choice without another request");
   }
   handler=new Handler();handler.Reply(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
   using(var panel=Panel(Path.Combine(root,"check-failure"),false,()=>new AppUpdateService(handler))){Pump(panel.CheckNowAsync());check(!panel.IsBusy&&panel.Check.IsEnabled&&panel.AvailableRelease==null&&panel.Status.Text.Contains("检查失败"),"failed check restores retry controls and never claims no update");}
  }
  static void CheckCancellation(string root,Action<bool,string> check){
   var firstReply=new TaskCompletionSource<HttpResponseMessage>();var first=new Handler();first.Replies.Enqueue(token=>firstReply.Task);var second=new Handler();second.Reply(ReleaseResponse());int factories=0;
   using(var panel=Panel(Path.Combine(root,"check-cancel"),false,()=>new AppUpdateService(factories++==0?first:second))){
    Task pending=panel.CheckNowAsync();panel.CancelOperation();check(!panel.Cancel.IsEnabled&&panel.Status.Text.Contains("正在取消"),"cancel check immediately prevents repeated cancel input");
    firstReply.SetResult(ReleaseResponse());Pump(pending);
    check(!panel.IsBusy&&panel.AvailableRelease==null&&panel.Check.IsEnabled&&panel.Status.Text.Contains("已取消"),"late response after cancel cannot install a stale release");
    Pump(panel.CheckNowAsync());check(factories==2&&panel.Install.IsEnabled&&panel.AvailableRelease!=null,"cancelled check can retry cleanly using a fresh operation");
   }
   var late=new TaskCompletionSource<HttpResponseMessage>();var handler=new Handler();handler.Replies.Enqueue(token=>late.Task);CancellationToken observed=CancellationToken.None;
   handler.Replies.Clear();handler.Replies.Enqueue(token=>{observed=token;return late.Task;});factories=0;
   var disposedPanel=Panel(Path.Combine(root,"check-dispose"),false,()=>{factories++;return new AppUpdateService(handler);});Task task=disposedPanel.CheckNowAsync();string status=disposedPanel.Status.Text;disposedPanel.Dispose();
   check(observed.IsCancellationRequested&&disposedPanel.IsDisposed&&!disposedPanel.Check.IsEnabled,"disposing an active panel cancels its outstanding request and disables restart");
   late.SetResult(ReleaseResponse());Pump(task);Pump(disposedPanel.CheckNowAsync());
   check(disposedPanel.Status.Text==status&&disposedPanel.AvailableRelease==null&&factories==1&&!disposedPanel.IsBusy,"disposed panel ignores delayed release UI and cannot issue another request");
  }
  static void InstallLifecycle(string root,Action<bool,string> check){
   var handler=new Handler();handler.Reply(Response(Bytes));int calls=0;string received=null;var panel=Panel(Path.Combine(root,"install"),false,()=>new AppUpdateService(handler),(owner,path,version,hash,token)=>{
    calls++;received=path;check(owner==null&&version==new Version(99,2,3,0)&&hash==Hash()&&File.ReadAllBytes(path).SequenceEqual(Bytes),"handoff receives only verified synthetic package with selected version/hash");return Task.FromResult("Synthetic handoff complete");});
   using(panel){
    panel.ApplyRelease(Release());Pump(panel.InstallNowAsync());
    check(calls==1&&received.StartsWith(Path.Combine(root,"install"),StringComparison.OrdinalIgnoreCase)&&handler.Anonymous,"installer staging uses injected temporary root and anonymous download, not user profile");
    check(!panel.IsBusy&&panel.Status.Text=="Synthetic handoff complete"&&panel.Check.IsEnabled&&panel.Install.IsEnabled&&panel.Progress.Visibility==Visibility.Collapsed,"successful fake handoff leaves panel reusable with completed status");
   }
   handler=new Handler();handler.Reply(Response(Bytes));calls=0;
   using(var bad=Panel(Path.Combine(root,"install-bad-hash"),false,()=>new AppUpdateService(handler),(owner,path,version,hash,token)=>{calls++;return Task.FromResult("must not happen");})){
    var release=Release();release.ExpectedSha256=new string('0',64);bad.ApplyRelease(release);Pump(bad.InstallNowAsync());
    check(calls==0&&!bad.IsBusy&&bad.Install.IsEnabled&&bad.Status.Text.Contains("更新未完成"),"failed integrity check never invokes handoff and leaves a truthful retry state");
   }
  }
  static void HandoffCancellation(string root,Action<bool,string> check){
   var handler=new Handler();handler.Reply(Response(Bytes));var result=new TaskCompletionSource<string>();CancellationToken observed=CancellationToken.None;
   using(var panel=Panel(Path.Combine(root,"handoff-cancel"),false,()=>new AppUpdateService(handler),(owner,path,version,hash,token)=>{observed=token;return result.Task;})){
    panel.ApplyRelease(Release());Task pending=panel.InstallNowAsync();
    DateTime deadline=DateTime.UtcNow.AddSeconds(8);while(!observed.CanBeCanceled){if(DateTime.UtcNow>deadline)throw new TimeoutException("Synthetic handoff was not reached");Drain();Thread.Sleep(1);}
    panel.CancelOperation();result.SetResult("LATE SUCCESS MUST NOT APPEAR");Pump(pending);
    check(observed.IsCancellationRequested&&panel.Status.Text.Contains("已取消")&&!panel.Status.Text.Contains("LATE SUCCESS")&&!panel.IsBusy&&panel.Install.IsEnabled,"late handoff completion after cancellation cannot overwrite cancellation or disable retry");
   }
   handler=new Handler();handler.Reply(Response(Bytes));result=new TaskCompletionSource<string>();observed=CancellationToken.None;
   var disposed=Panel(Path.Combine(root,"handoff-dispose"),false,()=>new AppUpdateService(handler),(owner,path,version,hash,token)=>{observed=token;return result.Task;});disposed.ApplyRelease(Release());Task lateTask=disposed.InstallNowAsync();
   DateTime until=DateTime.UtcNow.AddSeconds(8);while(!observed.CanBeCanceled){if(DateTime.UtcNow>until)throw new TimeoutException("Synthetic dispose handoff was not reached");Drain();Thread.Sleep(1);}
   disposed.Dispose();string before=disposed.Status.Text;result.SetResult("LATE DISPOSE SUCCESS");Pump(lateTask);
   check(observed.IsCancellationRequested&&disposed.Status.Text==before&&!disposed.Install.IsEnabled&&!disposed.Check.IsEnabled,"disposing during handoff cancels and ignores late completion without reviving buttons");
   handler=new Handler();handler.Reply(Response(Bytes));int handoffCancels=0,normalCloses=0;bool committed=false;AppUpdatePanel closing=null;
   closing=Panel(Path.Combine(root,"handoff-owner-closed"),false,()=>new AppUpdateService(handler),async(owner,path,version,hash,token)=>{
    committed=await AppUpdateCoordinator.RunHandoffAsync(ready=>Task.FromResult(true),exit=>{normalCloses++;closing.Dispose();return Task.FromResult(true);},()=>handoffCancels++,token);
    return "SYNTHETIC COMMITTED CLOSE";
   });
   closing.ApplyRelease(Release());Pump(closing.InstallNowAsync());
   check(committed&&normalCloses==1&&handoffCancels==0&&closing.IsDisposed&&!closing.IsBusy&&!closing.Check.IsEnabled&&!closing.Install.IsEnabled,"synchronous settings disposal during committed normal close never sends installer cancellation or revives closed UI");
  }
  static void StaleProgress(string root,Action<bool,string> check){
   var oldContext=SynchronizationContext.Current;var context=new HeldProgressContext(Dispatcher.CurrentDispatcher);SynchronizationContext.SetSynchronizationContext(context);
   try{
    var first=new Handler();first.Reply(Response(Bytes));var second=new Handler();var secondReply=new TaskCompletionSource<HttpResponseMessage>();second.Replies.Enqueue(token=>secondReply.Task);int factories=0,handoffs=0;
    using(var panel=Panel(Path.Combine(root,"progress-generation"),false,()=>new AppUpdateService(factories++==0?first:second),(owner,path,version,hash,token)=>{handoffs++;return Task.FromResult("Synthetic completed operation");})){
     panel.ApplyRelease(Release());Pump(panel.InstallNowAsync());check(context.Held.Count>0&&handoffs==1,"synthetic synchronization context holds actual posted progress callbacks after first operation completes");
     Task pending=panel.InstallNowAsync();string status=panel.Status.Text;double progress=panel.Progress.Value;context.Release();Drain();
     check(panel.IsBusy&&panel.Status.Text==status&&panel.Progress.Value==progress,"queued progress from completed operation cannot mutate a newer download");
     secondReply.SetResult(Response(Bytes));Pump(pending);context.Release();Drain();
     check(handoffs==2&&!panel.IsBusy&&panel.Status.Text=="Synthetic completed operation","progress generation protection preserves clean completion of a retry");
    }
   }finally{SynchronizationContext.SetSynchronizationContext(oldContext);}
  }
 }
}
