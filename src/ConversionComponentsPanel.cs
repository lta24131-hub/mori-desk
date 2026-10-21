using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Mori;

namespace MoriDesk {
 // Detached/testable control: construction never touches disk or downloads.
 // Hosts load local status explicitly; each install/update needs a user click.
 internal sealed class ConversionComponentsPanel:ScrollViewer,IDisposable {
  internal readonly Button Refresh=new Button{Content="检查组件更新",Name="ConversionComponentsRefresh"};
  internal readonly Button Cancel=new Button{Content="取消",Name="ConversionComponentsCancel",Visibility=Visibility.Collapsed};
  internal readonly StackPanel Items=new StackPanel();
  internal readonly TextBlock Status=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,12)};
  internal Func<ConversionComponentInfo,bool> ConfirmUninstall;
  internal Action Changed;
  readonly bool preview;
  readonly Func<CancellationToken,Task<ConversionComponentInfo[]>> list;
  readonly Func<string,string,IProgress<ConversionComponentProgress>,CancellationToken,Task<ConversionComponentInfo[]>> manage;
  readonly HashSet<string> requested;
  readonly ProgressBar progress=new ProgressBar{Height=3,Minimum=0,Maximum=1,Visibility=Visibility.Collapsed};
  ConversionComponentInfo[] components=new ConversionComponentInfo[0];
  CancellationTokenSource operation;
  bool busy,disposed;
  int generation;
  internal bool IsBusy {get{return busy;}}
  internal bool IsDisposed {get{return disposed;}}
  internal ConversionComponentInfo[] Components {get{return (ConversionComponentInfo[])components.Clone();}}
  internal ConversionComponentsPanel(bool preview=false,
   Func<CancellationToken,Task<ConversionComponentInfo[]>> list=null,
   Func<string,string,IProgress<ConversionComponentProgress>,CancellationToken,Task<ConversionComponentInfo[]>> manage=null,
   string[] requestedIds=null){
   this.preview=preview;var client=new FileConversionClient();
   this.list=list??client.ListComponentsAsync;this.manage=manage??client.ManageComponentAsync;
   requested=new HashSet<string>((requestedIds??new string[0]).Where(FileConversionClient.IsComponentId),StringComparer.Ordinal);
   VerticalScrollBarVisibility=ScrollBarVisibility.Auto;HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled;
   SetResourceReference(BackgroundProperty,"Canvas");
   var body=new StackPanel{Margin=new Thickness(28,22,28,24)};Content=body;
   body.Children.Add(Text("转换组件",18,false));
   body.Children.Add(Text("按需安装，不用时可卸载。原文件和转换结果始终保留。",12,true,new Thickness(0,8,0,16)));
   body.Children.Add(Text("图片、基础 PDF 等转换核心随软件提供",13,false));
   body.Children.Add(Text("附加组件仅在转换时运行，不会一直占用运行内存。",12,true,new Thickness(0,5,0,14)));
   if(requested.Count>0)body.Children.Add(Text("当前文件需要的组件排在最前面。安装后返回，格式会重新检查。",12,true,new Thickness(0,0,0,12)));
   var tools=new WrapPanel();tools.Children.Add(Refresh);tools.Children.Add(Cancel);body.Children.Add(tools);
   foreach(var button in new[]{Refresh,Cancel})StyleButton(button);
   progress.SetResourceReference(ForegroundProperty,"Accent");body.Children.Add(progress);
   Status.SetResourceReference(TextBlock.ForegroundProperty,"Muted");AutomationProperties.SetName(Status,"转换组件状态");body.Children.Add(Status);
   body.Children.Add(Items);
   body.Children.Add(Text("更新只使用当前 Nook 已验证的组件版本；升级 Nook 后可检查新组件。组件保存在独立目录，不随软件升级重复下载。",11,true,new Thickness(0,14,0,0)));
   Refresh.Click+=async delegate{await RefreshAsync();};
   Cancel.Click+=delegate{CancelOperation();};
   ConfirmUninstall=component=>MessageBox.Show(Window.GetWindow(this),
    "卸载“"+component.Name+"”？\n\n只移除转换组件，不删除原文件、素材或转换结果。以后需要时可重新安装。",
    "卸载转换组件",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)==MessageBoxResult.Yes;
   Status.Text=preview?"测试预览不会安装、卸载或更改个人组件。":"正在等待读取组件状态。";
   UpdateControls();
  }
  static TextBlock Text(string value,double size,bool muted,Thickness? margin=null){
   var text=new TextBlock{Text=value,FontSize=size,TextWrapping=TextWrapping.Wrap,Margin=margin??new Thickness(0)};
   if(size>=14)text.FontWeight=FontWeights.SemiBold;
   text.SetResourceReference(TextBlock.ForegroundProperty,muted?"Muted":"Text");return text;
  }
  static void StyleButton(Button button){
   button.MinHeight=32;button.Padding=new Thickness(11,5,11,5);button.Margin=new Thickness(0,0,8,6);
   button.SetResourceReference(BackgroundProperty,"Control");button.SetResourceReference(ForegroundProperty,"Text");button.SetResourceReference(BorderBrushProperty,"Border");
   AutomationProperties.SetName(button,Convert.ToString(button.Content));
  }
  internal static string SizeLabel(long size){
   return size>=1024L*1024*1024?(size/(1024d*1024*1024)).ToString("0.##")+" GB":
    size>=1024*1024?(size/(1024d*1024)).ToString("0.#")+" MB":Math.Max(1,size/1024)+" KB";
  }
  internal static string StateLabel(ConversionComponentInfo component){
   switch(component.State){
    case "installed":return "已安装 "+component.InstalledVersion;
    case "update-available":return component.InstalledVersion==component.AvailableVersion?"待验证 · 可重新安装校验":"可更新 "+component.InstalledVersion+" → "+component.AvailableVersion;
    case "broken":return "组件不完整，可重新安装";
    case "bundled":return "随软件内置 "+component.InstalledVersion;
    case "in-use":return "正在使用，完成转换后可管理";
    case "newer_version":return "组件版本较新，请先更新 Nook";
    default:return "未安装";
   }
  }
  void Render(){
   Items.Children.Clear();
   foreach(var component in components.OrderByDescending(item=>requested.Contains(item.Id))){
    var divider=new Border{Height=1,Margin=new Thickness(0,2,0,14)};divider.SetResourceReference(Border.BackgroundProperty,"Divider");Items.Children.Add(divider);
    var row=new StackPanel{Margin=new Thickness(0,0,0,12)};
    row.Children.Add(Text(component.Name+(requested.Contains(component.Id)?" · 当前文件需要":""),14,false));
    row.Children.Add(Text(component.Description,12,true,new Thickness(0,4,0,6)));
    string size=component.InstalledBytes>0?(component.State=="missing"?" · 安装后约 ":" · 占用 ")+SizeLabel(component.InstalledBytes):"";
    row.Children.Add(Text(StateLabel(component)+size,12,true));
    if(!string.IsNullOrWhiteSpace(component.Message))row.Children.Add(Text(component.Message,11,true,new Thickness(0,4,0,0)));
    var actions=new WrapPanel{Margin=new Thickness(0,9,0,0)};row.Children.Add(actions);
    if(component.State=="missing"||component.State=="broken")
     AddAction(actions,component,"install",(component.State=="broken"?"重新安装":"安装")+" · 下载 "+SizeLabel(component.DownloadBytes));
    if(component.State=="update-available")AddAction(actions,component,"update",(component.InstalledVersion==component.AvailableVersion?"重新安装并校验":"更新")+" · 下载 "+SizeLabel(component.DownloadBytes));
    if(component.State!="missing"&&component.State!="bundled"){
     var uninstall=AddAction(actions,component,"uninstall","卸载");
     uninstall.IsEnabled=uninstall.IsEnabled&&component.CanUninstall;
     if(!component.CanUninstall){uninstall.ToolTip=string.IsNullOrWhiteSpace(component.Message)?"其他组件仍需要它，暂不能单独卸载。":component.Message;ToolTipService.SetShowOnDisabled(uninstall,true);}
    }
    Items.Children.Add(row);
   }
  }
  Button AddAction(Panel row,ConversionComponentInfo component,string action,string title){
   var button=new Button{Content=title,Name="Component_"+action+"_"+component.Id,Tag=component.Id,IsEnabled=!preview&&!busy&&!disposed};
   StyleButton(button);AutomationProperties.SetName(button,component.Name+" "+title);
   button.Click+=async delegate{await ExecuteAsync(component.Id,action);};row.Children.Add(button);return button;
  }
  void UpdateControls(){
   Refresh.IsEnabled=!busy&&!disposed;Cancel.Visibility=busy?Visibility.Visible:Visibility.Collapsed;Cancel.IsEnabled=busy&&!disposed;
   progress.Visibility=busy?Visibility.Visible:Visibility.Collapsed;
   Render();
  }
  internal async Task RefreshAsync(){
   if(disposed||busy)return;
   if(preview){Status.Text="测试预览不会读取或更改个人组件。";return;}
   var cancellation=new CancellationTokenSource();operation=cancellation;int current=++generation;busy=true;progress.IsIndeterminate=true;UpdateControls();
   Status.Text="正在检查已安装组件…";
   try{
    var result=await list(cancellation.Token);
    if(disposed||current!=generation)return;
    components=result??new ConversionComponentInfo[0];
    Status.Text=components.Any(value=>value.State=="update-available")?"有组件可更新或补充完整性验证，可按需处理。":"组件状态已刷新；不会自动下载。";
   }catch(OperationCanceledException){if(!disposed&&current==generation)Status.Text="已取消检查。";}
   catch(Exception error){if(!disposed&&current==generation)Status.Text="无法读取组件："+error.Message+" 可点击检查重试。";}
   finally{if(ReferenceEquals(operation,cancellation)){operation=null;busy=false;}cancellation.Dispose();if(!disposed)UpdateControls();}
  }
  internal async Task ExecuteAsync(string id,string action){
   if(disposed||preview||busy)return;
   var component=components.FirstOrDefault(item=>item.Id==id);
   if(component==null||!new[]{"install","update","uninstall"}.Contains(action,StringComparer.Ordinal))return;
   if(action=="uninstall"){
    if(!component.CanUninstall){Status.Text="该组件仍被使用，暂不能卸载。";return;}
    if(ConfirmUninstall==null||!ConfirmUninstall(component))return;
   }else if(action=="update"&&component.State!="update-available")return;
   else if(action=="install"&&component.State!="missing"&&component.State!="broken")return;
   var cancellation=new CancellationTokenSource();operation=cancellation;int current=++generation;busy=true;progress.IsIndeterminate=true;UpdateControls();
   Status.Text=(action=="uninstall"?"正在卸载 ":"正在准备 ")+component.Name+"…";
   ScrollToTop(); // Keep progress and Cancel visible when starting from a lower component row.
   try{
    var report=new Progress<ConversionComponentProgress>(value=>{
     if(disposed||current!=generation||!ReferenceEquals(operation,cancellation)||cancellation.IsCancellationRequested)return;
     progress.IsIndeterminate=value.BytesTotal<=0;progress.Value=Math.Max(0,Math.Min(1,value.Fraction));
     Status.Text=component.Name+" · "+ProgressLabel(value.Stage)+(value.BytesTotal>0?" "+SizeLabel(value.BytesDone)+" / "+SizeLabel(value.BytesTotal):"");
    });
    var result=await manage(id,action,report,cancellation.Token);
    if(disposed||current!=generation)return;
    components=result??new ConversionComponentInfo[0];
    Status.Text=component.Name+(action=="uninstall"?"已卸载；原文件和转换结果保留。":action=="update"?"已更新，可以继续转换。":"已安装，可以继续转换。");
    if(Changed!=null)try{Changed();}catch{/* Refreshing another surface cannot undo a committed component operation. */}
   }catch(OperationCanceledException){if(!disposed&&current==generation)Status.Text="已取消。原文件和结果保留；可检查组件状态后重试。";}
   catch(Exception error){if(!disposed&&current==generation)Status.Text="操作未完成："+error.Message+" 可检查状态后重试。";}
   finally{if(ReferenceEquals(operation,cancellation)){operation=null;busy=false;}cancellation.Dispose();if(!disposed)UpdateControls();}
  }
  static string ProgressLabel(string stage){
   switch((stage??"").ToLowerInvariant()){
    case "download":case "downloading":return "正在下载";
    case "verify":case "verifying":return "正在校验";
    case "extract":case "extracting":case "unpacking":return "正在解包";
    case "install":case "installing":return "正在安装";
    default:return "正在准备组件";
   }
  }
  internal void CancelOperation(){if(operation!=null){operation.Cancel();Status.Text="正在取消，保留已安装版本和个人文件…";Cancel.IsEnabled=false;}}
  public void Dispose(){if(disposed)return;disposed=true;generation++;if(operation!=null)operation.Cancel();UpdateControls();}
 }

 internal static class ConversionComponentsWindow {
  // Non-modal, so installing a large optional engine never blocks the library.
  internal static Task<bool> OpenAsync(Window owner,string[] requestedIds){
   var completion=new TaskCompletionSource<bool>();bool changed=false;
   var window=new Window{Title="Nook · 转换组件",Width=620,Height=650,MinWidth=480,MinHeight=400,
    ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.CenterOwner};
   if(owner!=null&&owner.IsVisible)window.Owner=owner;
   Theme.Attach(window,owner);
   var panel=new ConversionComponentsPanel(requestedIds:requestedIds);window.Content=panel;
   panel.Changed=()=>changed=true;
   window.Loaded+=async delegate{await panel.RefreshAsync();};
   window.Closed+=delegate{panel.Dispose();completion.TrySetResult(changed);};
   window.KeyDown+=delegate(object sender,System.Windows.Input.KeyEventArgs e){if(e.Key==System.Windows.Input.Key.Escape&&!e.Handled){window.Close();e.Handled=true;}};
   window.Show();return completion.Task;
  }
 }
}
