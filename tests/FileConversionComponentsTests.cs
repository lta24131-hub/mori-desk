using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MoriDesk {
 // Detached controls and injected services only: no Window/Application, picker,
 // clipboard, network, conversion process or real component/data root.
 internal static class FileConversionComponentsTests {
  static void Drain(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
  static void Pump(Task task){DateTime until=DateTime.UtcNow.AddSeconds(15);while(!task.IsCompleted){if(DateTime.UtcNow>until)throw new TimeoutException("Component panel test timed out.");Drain();Thread.Sleep(1);}task.GetAwaiter().GetResult();Drain();}
  static void Until(Func<bool> predicate){DateTime until=DateTime.UtcNow.AddSeconds(15);while(!predicate()){if(DateTime.UtcNow>until)throw new TimeoutException("Injected component service did not start.");Drain();Thread.Sleep(1);}Drain();}
  static IEnumerable<DependencyObject> Tree(DependencyObject root){
   yield return root;
   foreach(object child in LogicalTreeHelper.GetChildren(root)){var value=child as DependencyObject;if(value!=null)foreach(var nested in Tree(value))yield return nested;}
  }
  static Button Button(DependencyObject root,string name){return Tree(root).OfType<Button>().FirstOrDefault(button=>button.Name==name);}
  static string Text(DependencyObject root){return string.Join("\n",Tree(root).OfType<TextBlock>().Select(value=>value.Text));}
  static void Click(Button button){if(button==null)throw new Exception("Expected button is missing.");button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));Drain();}
  static ConversionComponentInfo Item(string id="ffmpeg",string state="missing",bool uninstall=true){
   return new ConversionComponentInfo{Id=id,Name=id=="ffmpeg"?"视频与音频":id=="libreoffice"?"Office 文档":id=="calibre"?"电子书":"共享辅助组件",
    Description="用途说明：仅在实际转换时启动；不改动源文件。",State=state,InstalledVersion=state=="missing"?"":"1.0",AvailableVersion="2.0",
    DownloadBytes=150L*1024*1024,InstalledBytes=state=="missing"?0:300L*1024*1024,CanUninstall=uninstall,Dependencies=new string[0],Message=""};
  }
  static Task<ConversionComponentInfo[]> Ready(params ConversionComponentInfo[] values){return Task.FromResult(values);}
  static void Theme(FrameworkElement panel,bool dark){
   var resources=new Grid();
   typeof(FileConversionUiTests).GetMethod("ApplyActualTheme",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{resources,dark});
   panel.Resources=resources.Resources;
  }
  static void Layout(FrameworkElement panel,double width,double height){panel.Width=width;panel.Height=height;panel.Measure(new Size(width,height));panel.Arrange(new Rect(0,0,width,height));panel.UpdateLayout();Drain();}
  static void PassiveAndPreview(Action<bool,string> check){
   int listed=0,managed=0;
   using(var panel=new ConversionComponentsPanel(false,token=>{listed++;return Ready(Item());},(id,action,progress,token)=>{managed++;return Ready(Item(id,"installed"));})){
    check(listed==0&&managed==0&&panel.Components.Length==0&&!panel.IsBusy,"component panel constructor does not probe disk, install, or start any service");
    Pump(panel.ExecuteAsync("ffmpeg","install"));
    check(managed==0,"component installation cannot start before local state is loaded");
    Pump(panel.RefreshAsync());
    check(listed==1&&managed==0&&panel.Components.Single().State=="missing"&&panel.Status.Text.Contains("不会自动下载"),"refresh checks state only and explicitly communicates that it does not download");
   }
   using(var panel=new ConversionComponentsPanel(true,token=>{listed++;return Ready(Item());},(id,action,progress,token)=>{managed++;return Ready(Item());})){
    Pump(panel.RefreshAsync());Pump(panel.ExecuteAsync("ffmpeg","install"));
    check(listed==1&&managed==0&&panel.Components.Length==0&&panel.Status.Text.Contains("不会读取"),"settings preview never reads or changes real optional components");
   }
  }
  static void StatesAndActions(Action<bool,string> check){
   var items=new[]{Item("ffmpeg"),Item("libreoffice","installed"),Item("calibre","update-available"),Item("pandoc","broken"),
    Item("ghostscript","bundled",false),Item("7zip","in-use",false),Item("jre","installed",false),Item("rhwp","newer_version")};
   items[6].Message="Office 文档仍需要此组件。";
   using(var panel=new ConversionComponentsPanel(false,token=>Ready(items),null,new[]{"calibre","unknown","calibre"})){
    Pump(panel.RefreshAsync());
    check(panel.Items.Children.OfType<StackPanel>().First().Children.OfType<TextBlock>().First().Text.Contains("电子书"),"only valid requested component rows are promoted above unrelated packs");
    check(Button(panel,"Component_install_ffmpeg")!=null&&Button(panel,"Component_update_ffmpeg")==null&&Button(panel,"Component_uninstall_ffmpeg")==null,"missing component offers only explicit installation");
    check(Button(panel,"Component_uninstall_libreoffice")!=null&&Button(panel,"Component_install_libreoffice")==null&&Button(panel,"Component_update_libreoffice")==null,"up-to-date installed component does not invite redundant download");
    check(Button(panel,"Component_update_calibre")!=null&&Button(panel,"Component_uninstall_calibre")!=null,"approved newer version offers update separately from uninstall");
    check(Button(panel,"Component_install_pandoc")!=null&&Convert.ToString(Button(panel,"Component_install_pandoc").Content).Contains("重新安装"),"broken component offers recognizable repair rather than falsely claiming installed");
    check(Button(panel,"Component_uninstall_ghostscript")==null&&Button(panel,"Component_install_ghostscript")==null,"bundled conversion core cannot be uninstalled from optional settings");
    check(Button(panel,"Component_install_rhwp")==null&&Button(panel,"Component_update_rhwp")==null&&Text(panel).Contains("组件版本较新"),"newer installed component directs the user to update Nook and never offers a downgrade");
    check(!Button(panel,"Component_uninstall_7zip").IsEnabled&&!Button(panel,"Component_uninstall_jre").IsEnabled&&Convert.ToString(Button(panel,"Component_uninstall_jre").ToolTip).Contains("Office"),"active or dependency-protected components explain why uninstall is unavailable");
    check(Text(panel).Contains("图片、基础 PDF")&&Text(panel).Contains("原文件和转换结果")&&Text(panel).Contains("已验证"),"settings explains built-in core, preservation and the reviewed-version update policy");
    check(Tree(panel).OfType<Button>().All(button=>!string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))),"component management buttons have readable accessibility names");
    check(panel.HorizontalScrollBarVisibility==ScrollBarVisibility.Disabled&&panel.VerticalScrollBarVisibility==ScrollBarVisibility.Auto,"component list scrolls vertically without introducing a horizontal-scroll workflow");
    check(ConversionComponentsPanel.StateLabel(items[2]).Contains("1.0")&&ConversionComponentsPanel.StateLabel(items[2]).Contains("2.0"),"update row keeps current and available versions distinguishable");
    check(ConversionComponentsPanel.SizeLabel(150L*1024*1024)=="150 MB"&&ConversionComponentsPanel.SizeLabel(2L*1024*1024*1024)=="2 GB","download and disk sizes use readable units");
   }
   var legacy=Item("pandoc","update-available");legacy.AvailableVersion=legacy.InstalledVersion;
   legacy.Message="旧版组件尚无关键文件完整性清单，暂保留兼容使用；请重新安装并校验。";
   using(var panel=new ConversionComponentsPanel(false,token=>Ready(legacy),null)){
    Pump(panel.RefreshAsync());
    check(ConversionComponentsPanel.StateLabel(legacy).Contains("待验证")&&!ConversionComponentsPanel.StateLabel(legacy).Contains("→"),"legacy same-version component explains pending integrity verification instead of showing a fake version upgrade");
    check(Convert.ToString(Button(panel,"Component_update_pandoc").Content).Contains("重新安装并校验")&&Text(panel).Contains("暂保留兼容使用"),"legacy compatibility state offers an explicit reinstall-and-verify action without claiming the old bytes were verified");
   }
  }
  static void SuccessfulOperations(Action<bool,string> check){
   int calls=0,changed=0,confirmed=0;string requestId=null,requestAction=null;
   using(var panel=new ConversionComponentsPanel(false,token=>Ready(Item()),(id,action,report,token)=>{
    calls++;requestId=id;requestAction=action;return Ready(Item(id,action=="uninstall"?"missing":"installed"));
   })){
    panel.Changed=()=>changed++;panel.ConfirmUninstall=component=>{confirmed++;return true;};
    Pump(panel.RefreshAsync());Pump(panel.ExecuteAsync("unknown","install"));Pump(panel.ExecuteAsync("ffmpeg","arbitrary"));Pump(panel.ExecuteAsync("ffmpeg","update"));
    check(calls==0&&changed==0,"unknown operation and unavailable update cannot reach the installation service");
    Pump(panel.ExecuteAsync("ffmpeg","install"));
    check(calls==1&&changed==1&&requestId=="ffmpeg"&&requestAction=="install"&&panel.Components.Single().State=="installed","explicit installation calls exactly the chosen component and refreshes its durable state");
    check(panel.Status.Text.Contains("已安装")&&Button(panel,"Component_install_ffmpeg")==null&&Button(panel,"Component_uninstall_ffmpeg")!=null,"successful first install immediately changes the available action to uninstall");
    Pump(panel.ExecuteAsync("ffmpeg","install"));
    check(calls==1,"an installed pack cannot be accidentally reinstalled by repeating the earlier action");
    Pump(panel.ExecuteAsync("ffmpeg","uninstall"));
    check(calls==2&&changed==2&&confirmed==1&&requestAction=="uninstall"&&panel.Components.Single().State=="missing","confirmed uninstall refreshes only component state");
    check(panel.Status.Text.Contains("原文件和转换结果保留")&&Button(panel,"Component_install_ffmpeg")!=null,"uninstall offers installation again and confirms original/results preservation");
   }
   using(var panel=new ConversionComponentsPanel(false,token=>Ready(Item("calibre","update-available")),(id,action,p,t)=>{requestAction=action;return Ready(Item(id,"installed"));})){
    panel.Changed=()=>{throw new Exception("detached secondary surface no longer exists");};
    Pump(panel.RefreshAsync());Pump(panel.ExecuteAsync("calibre","update"));
    check(requestAction=="update"&&panel.Status.Text.Contains("已更新")&&panel.Components.Single().State=="installed","a secondary refresh callback failure cannot undo or misreport a committed update");
   }
  }
  static void UninstallProtection(Action<bool,string> check){
   int managed=0,confirmed=0;
   using(var panel=new ConversionComponentsPanel(false,token=>Ready(Item("libreoffice","installed")),(id,action,p,t)=>{managed++;return Ready(Item());})){
    panel.ConfirmUninstall=component=>{confirmed++;return false;};Pump(panel.RefreshAsync());Pump(panel.ExecuteAsync("libreoffice","uninstall"));
    check(confirmed==1&&managed==0&&panel.Components.Single().State=="installed","cancelling uninstall confirmation neither calls the service nor changes installed status");
    panel.ConfirmUninstall=null;Pump(panel.ExecuteAsync("libreoffice","uninstall"));
    check(managed==0,"missing confirmation handler defaults to no destructive component change");
   }
   using(var panel=new ConversionComponentsPanel(false,token=>Ready(Item("jre","installed",false)),(id,action,p,t)=>{managed++;return Ready(Item());})){
    panel.ConfirmUninstall=component=>{confirmed++;return true;};Pump(panel.RefreshAsync());Pump(panel.ExecuteAsync("jre","uninstall"));
    check(managed==0&&confirmed==1&&panel.Status.Text.Contains("不能卸载"),"dependency-protected removal is rejected even when invoked programmatically");
   }
  }
  static void BusyAndProgress(Action<bool,string> check){
   var gate=new TaskCompletionSource<ConversionComponentInfo[]>();int listed=0,managed=0,changed=0;
   IProgress<ConversionComponentProgress> progress=null;CancellationToken observed=CancellationToken.None;
   using(var panel=new ConversionComponentsPanel(false,token=>{listed++;return Ready(Item());},(id,action,report,token)=>{
    managed++;progress=report;observed=token;return gate.Task;
   })){
    panel.Changed=()=>changed++;Pump(panel.RefreshAsync());Task pending=panel.ExecuteAsync("ffmpeg","install");
    check(panel.IsBusy&&!panel.Refresh.IsEnabled&&panel.Cancel.Visibility==Visibility.Visible&&!Button(panel,"Component_install_ffmpeg").IsEnabled,"active installation disables duplicate mutations and exposes cancellation");
    Pump(panel.ExecuteAsync("ffmpeg","install"));Pump(panel.RefreshAsync());
    check(managed==1&&listed==1,"repeated clicks and refresh cannot race against an active installation");
    progress.Report(new ConversionComponentProgress{Stage="download",ComponentId="ffmpeg",BytesDone=64L*1024*1024,BytesTotal=128L*1024*1024,Fraction=.5});Drain();
    check(panel.Status.Text.Contains("正在下载")&&panel.Status.Text.Contains("64 MB")&&panel.Status.Text.Contains("128 MB"),"download progress is readable and tied to the active requested component");
    panel.CancelOperation();string cancelling=panel.Status.Text;
    progress.Report(new ConversionComponentProgress{Stage="install",BytesDone=1,BytesTotal=1,Fraction=1});Drain();
    check(observed.IsCancellationRequested&&!panel.Cancel.IsEnabled&&panel.Status.Text==cancelling&&panel.IsBusy,"cancel requests cooperative shutdown and ignores stale progress while waiting for worker completion");
    gate.SetCanceled();Pump(pending);
    check(!panel.IsBusy&&panel.Refresh.IsEnabled&&panel.Cancel.Visibility==Visibility.Collapsed&&changed==0&&panel.Components.Single().State=="missing","cancelled installation does not claim a successful component and restores controls");
    string final=panel.Status.Text;progress.Report(new ConversionComponentProgress{Stage="install",BytesDone=1,BytesTotal=1,Fraction=1});Drain();
    check(panel.Status.Text==final,"late progress after completion cannot overwrite the final result");
   }
   var committedGate=new TaskCompletionSource<ConversionComponentInfo[]>();
   using(var panel=new ConversionComponentsPanel(false,token=>Ready(Item()),(id,a,p,t)=>committedGate.Task)){
    panel.Changed=()=>changed++;Pump(panel.RefreshAsync());Task pending=panel.ExecuteAsync("ffmpeg","install");
    panel.CancelOperation();committedGate.SetResult(new[]{Item("ffmpeg","installed")});Pump(pending);
    check(panel.Components.Single().State=="installed"&&panel.Status.Text.Contains("已安装")&&changed==1,"durable worker commit wins a last-moment cancellation in the UI");
   }
  }
  static void FailureRetryAndDispose(Action<bool,string> check){
   int listed=0,managed=0,changed=0;
   using(var panel=new ConversionComponentsPanel(false,token=>{listed++;if(listed==1)throw new IOException("synthetic unavailable state");return Ready(Item("calibre","update-available"));},
    (id,action,p,t)=>{managed++;if(managed==1)throw new IOException("synthetic network failure");return Ready(Item(id,"installed"));})){
    panel.Changed=()=>changed++;Pump(panel.RefreshAsync());
    check(panel.Status.Text.Contains("无法读取")&&panel.Refresh.IsEnabled&&panel.Components.Length==0,"failed local-state check stays recoverable without inventing missing or installed status");
    Pump(panel.RefreshAsync());Pump(panel.ExecuteAsync("calibre","update"));
    check(panel.Status.Text.Contains("操作未完成")&&panel.Components.Single().InstalledVersion=="1.0"&&changed==0&&panel.Refresh.IsEnabled,"failed update preserves the previous installed version and offers retry");
    Pump(panel.ExecuteAsync("calibre","update"));
    check(managed==2&&changed==1&&panel.Status.Text.Contains("已更新"),"retrying a failed update can reach the durable success state");
   }
   var gate=new TaskCompletionSource<ConversionComponentInfo[]>();CancellationToken observed=CancellationToken.None;IProgress<ConversionComponentProgress> progress=null;
   var disposed=new ConversionComponentsPanel(false,token=>Ready(Item()),(id,action,report,token)=>{observed=token;progress=report;return gate.Task;});
   disposed.Changed=()=>changed++;Pump(disposed.RefreshAsync());Task pending=disposed.ExecuteAsync("ffmpeg","install");disposed.Dispose();disposed.Dispose();
   string closing=disposed.Status.Text;progress.Report(new ConversionComponentProgress{Stage="download",BytesDone=1,BytesTotal=2,Fraction=.5});Drain();
   gate.SetResult(new[]{Item("ffmpeg","installed")});Pump(pending);
   check(disposed.IsDisposed&&observed.IsCancellationRequested&&!disposed.Refresh.IsEnabled&&disposed.Status.Text==closing&&changed==1,"disposing during installation cancels cooperatively and ignores late result/progress without resurrecting the UI");
   Pump(disposed.RefreshAsync());Pump(disposed.ExecuteAsync("ffmpeg","install"));
   check(disposed.Components.Single().State=="missing","disposed panel cannot start later component operations or mutate its retained state");
   var listGate=new TaskCompletionSource<ConversionComponentInfo[]>();int listCalls=0;
   using(var panel=new ConversionComponentsPanel(false,token=>{listCalls++;return listGate.Task;},null)){
    Task listing=panel.RefreshAsync();Pump(panel.RefreshAsync());Pump(panel.ExecuteAsync("ffmpeg","install"));
    check(listCalls==1&&panel.IsBusy,"simultaneous check requests are coalesced and installation waits for a known local state");
    panel.Dispose();listGate.SetResult(new[]{Item("ffmpeg","installed")});Pump(listing);
    check(panel.Components.Length==0,"late local-state reply after panel disposal is ignored");
   }
  }
  static ConversionCapabilities Caps(string source,bool installed){
   return new ConversionCapabilities{Targets=installed?new[]{new ConversionTarget{Id="mp4",Label="MP4",Extension="mp4"}}:new ConversionTarget[0],
    Files=new[]{new ConversionInputInfo{Source=source,Format="webm",Targets=installed?new[]{"mp4"}:new string[0],Warnings=new string[0]}},
    MissingComponentIds=installed?new string[0]:new[]{"ffmpeg","ffmpeg","unknown"},MissingEngines=installed?new string[0]:new[]{"视频转换组件未安装"}};
  }
  static void QuickFirstUse(string root,Action<bool,string> check){
   string source=Path.Combine(root,"external-source.webm"),other=Path.Combine(root,"other-source.webm");
   File.WriteAllText(source,"SYNTHETIC SOURCE");File.WriteAllText(other,"SECOND SYNTHETIC SOURCE");
   string destination=Path.Combine(root,"should-not-be-created");int probed=0,converted=0,managed=0;bool installed=false;string[] asked=null;
   Func<string[],string,string,int,IProgress<ConversionProgress>,CancellationToken,Task<ConversionBatchResult>> convert=(p,t,d,q,r,c)=>{converted++;throw new Exception("Should require a separate explicit format click.");};
   using(var panel=new QuickFileConversionPanel(true,(paths,token)=>{Interlocked.Increment(ref probed);return Task.FromResult(Caps(paths.Single(),installed));},convert,destination)){
    panel.ManageComponents=ids=>{managed++;asked=ids;installed=true;return Task.FromResult(true);};
    Pump(panel.AddFilesAsync(new[]{source}));
    check(managed==0&&converted==0&&Button(panel,"QuickConversionComponents")!=null&&panel.FormatChoices.Children.Count==0,"dropping a file with a missing optional engine shows one explicit install entry without downloading or converting");
    Pump(panel.ManageMissingComponentsAsync());
    check(managed==1&&asked.SequenceEqual(new[]{"ffmpeg"})&&probed==2&&panel.Inputs.SequenceEqual(new[]{source}),"successful component management rechecks the same external source with only approved distinct component IDs");
    check(Button(panel,"QuickConversionComponents")==null&&Button(panel,"QuickConvert_mp4")!=null&&converted==0&&!Directory.Exists(destination),"new formats appear after installation but conversion still needs an explicit target choice");
    check(File.ReadAllText(source)=="SYNTHETIC SOURCE","first-use component workflow never replaces the source file");
   }
   installed=false;int previousProbes=probed;
   using(var panel=new QuickFileConversionPanel(true,(paths,t)=>{Interlocked.Increment(ref probed);return Task.FromResult(Caps(paths.Single(),installed));},convert,destination)){
    panel.ManageComponents=ids=>{managed++;return Task.FromResult(false);};Pump(panel.AddFilesAsync(new[]{source}));Pump(panel.ManageMissingComponentsAsync());
    check(probed==previousProbes+1&&panel.Inputs.SequenceEqual(new[]{source})&&Button(panel,"QuickConversionComponents")!=null&&converted==0,"closing first-use component management without changes preserves the file and does not start conversion");
    panel.ManageComponents=ids=>{throw new IOException("synthetic component window failure");};Pump(panel.ManageMissingComponentsAsync());
    check(panel.Status.Text.Contains("无法管理组件")&&panel.Inputs.SequenceEqual(new[]{source})&&Button(panel,"QuickConversionComponents")!=null,"failed component entry keeps a retry path and the user's current source");
   }
   var gate=new TaskCompletionSource<bool>();int opened=0;
   using(var panel=new QuickFileConversionPanel(true,(paths,t)=>Task.FromResult(Caps(paths.Single(),installed)),convert,destination)){
    panel.ManageComponents=ids=>{opened++;return gate.Task;};Pump(panel.AddFilesAsync(new[]{source}));Task pending=panel.ManageMissingComponentsAsync();
    Pump(panel.ManageMissingComponentsAsync());Pump(panel.AddFilesAsync(new[]{other}));installed=true;gate.SetResult(true);Pump(pending);
    check(opened==1&&panel.Inputs.SequenceEqual(new[]{other})&&Button(panel,"QuickConvert_mp4")!=null,"non-modal management does not open duplicate managers or restore an old source over a newer drag");
   }
   installed=false;var clearGate=new TaskCompletionSource<bool>();
   using(var panel=new QuickFileConversionPanel(true,(paths,t)=>Task.FromResult(Caps(paths.Single(),installed)),convert,destination)){
    panel.ManageComponents=ids=>clearGate.Task;Pump(panel.AddFilesAsync(new[]{source}));Task pending=panel.ManageMissingComponentsAsync();
    Click(panel.Clear);clearGate.SetResult(true);Pump(pending);
    check(panel.Inputs.Length==0&&panel.FormatChoices.Children.Count==0,"clearing the pending source while component management is open is respected when it closes");
   }
   var disposeGate=new TaskCompletionSource<bool>();
   var disposed=new QuickFileConversionPanel(true,(paths,t)=>Task.FromResult(Caps(paths.Single(),false)),convert,destination);
   disposed.ManageComponents=ids=>disposeGate.Task;Pump(disposed.AddFilesAsync(new[]{source}));Task closing=disposed.ManageMissingComponentsAsync();
   disposed.Dispose();disposeGate.SetResult(true);Pump(closing);
   check(disposed.IsDisposed&&converted==0&&!Directory.Exists(destination),"component manager completion cannot resurrect or convert through a disposed quick panel");
  }
  static void RenderPanels(string root,Action<bool,string> check){
   var rows=new[]{Item("ffmpeg"),Item("libreoffice","update-available"),Item("calibre","missing"),Item("pandoc","installed"),Item("ghostscript","installed"),Item("7zip","missing"),Item("jre","installed",false)};
   rows[1].Description="Word、Excel、PowerPoint 等文档格式转换。包含必要的共享组件；安装不会改变默认打开方式。";
   rows[6].Message="共享依赖仍被 Office 文档转换需要，请先卸载依赖它的组件。";
   foreach(bool dark in new[]{false,true})foreach(int width in new[]{620,480}){
    var operation=new TaskCompletionSource<ConversionComponentInfo[]>();
    using(var panel=new ConversionComponentsPanel(false,token=>Ready(rows),(id,action,progress,token)=>operation.Task,new[]{"libreoffice"})){
     Theme(panel,dark);Pump(panel.RefreshAsync());Layout(panel,width,440);
     check(panel.ViewportHeight>0&&panel.ScrollableHeight>0&&panel.ExtentWidth<=panel.ViewportWidth+1,"component settings is vertically reachable without horizontal clipping at "+width+"px "+(dark?"dark":"light"));
     var bitmap=new RenderTargetBitmap(width,440,96,96,PixelFormats.Pbgra32);bitmap.Render(panel);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
     string path=Path.Combine(root,"components-"+width+"-"+(dark?"dark":"light")+".png");using(var stream=File.Create(path))encoder.Save(stream);
     panel.ScrollToEnd();panel.UpdateLayout();Drain();panel.UpdateLayout();
     check(panel.VerticalOffset>0&&panel.VerticalOffset>=panel.ScrollableHeight-1,"the last optional component and explanation remain reachable at "+width+"px "+(dark?"dark":"light"));
     check(Tree(panel).OfType<Button>().Where(button=>button.IsVisible).All(button=>button.DesiredSize.Width<=width-56),"component action labels fit the available narrow content width at "+width+"px");
     Task pending=panel.ExecuteAsync("ffmpeg","install");panel.UpdateLayout();Drain();panel.UpdateLayout();
     check(panel.IsBusy&&panel.VerticalOffset<=1&&panel.Cancel.Visibility==Visibility.Visible,"starting installation from the scrolled list brings progress and cancel into view at "+width+"px "+(dark?"dark":"light"));
     check(Application.Current==null&&PresentationSource.FromVisual(panel)==null,"component render verification remains detached from Application and HWND at "+width+"px");
     operation.SetCanceled();Pump(pending);
    }
   }
  }
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);var previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
   try{PassiveAndPreview(check);StatesAndActions(check);SuccessfulOperations(check);UninstallProtection(check);BusyAndProgress(check);FailureRetryAndDispose(check);QuickFirstUse(root,check);RenderPanels(root,check);}
   finally{SynchronizationContext.SetSynchronizationContext(previous);}
  }
 }
}
