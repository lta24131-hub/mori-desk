using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Mori;

namespace MoriDesk {
 // Explicit, user-driven updates. Merely opening settings never makes a
 // network request, reads a credential, or starts an installer.
 internal sealed class AppUpdatePanel : ScrollViewer, IDisposable {
  internal readonly Button Check=new Button{Content="检查更新",Name="CheckAppUpdate"};
  internal readonly Button Install=new Button{Content="下载并重启更新",Name="InstallAppUpdate",Visibility=Visibility.Collapsed};
  internal readonly Button Cancel=new Button{Content="取消",Name="CancelAppUpdate",Visibility=Visibility.Collapsed};
  internal readonly CheckBox Previews=new CheckBox{Content="包含预览版本",IsChecked=true,Margin=new Thickness(0,16,0,8)};
  internal readonly TextBlock Status=new TextBlock{TextWrapping=TextWrapping.Wrap,LineHeight=21,Margin=new Thickness(0,14,0,6)};
  internal readonly ProgressBar Progress=new ProgressBar{Height=4,Minimum=0,Maximum=100,Visibility=Visibility.Collapsed,Margin=new Thickness(0,8,0,0)};
  internal readonly TextBox Notes=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,AcceptsReturn=true,BorderThickness=new Thickness(0),Padding=new Thickness(0),MinHeight=60,MaxHeight=230,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Visibility=Visibility.Collapsed};
  readonly TextBlock releaseTitle=new TextBlock{FontSize=14,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,24,0,10),Visibility=Visibility.Collapsed};
  readonly Window owner;
  readonly bool preview;
  readonly bool autoUpdateAvailable;
  const string ManualUpdateHint="当前从便携版或非标准安装位置运行，请手动升级。先正常退出 Nook，再运行 GitHub 安装包，或把完整便携包解压到新文件夹；不要只替换 Mori.exe。";
  readonly Version current;
  readonly string downloadRoot;
  readonly Func<AppUpdateService> serviceFactory;
  readonly Func<Window,string,Version,string,CancellationToken,Task<string>> handoff;
  CancellationTokenSource operation;
  UpdateRelease release;
  bool disposed,busy;long generation;

  internal AppUpdatePanel(Window owner,bool preview,Func<AppUpdateService> serviceFactory=null,Func<Window,string,Version,string,CancellationToken,Task<string>> handoff=null,string downloadRoot=null,Func<bool> autoUpdateAvailable=null){
   this.owner=owner;this.preview=preview;this.serviceFactory=serviceFactory??(()=>new AppUpdateService());this.handoff=handoff??AppUpdateCoordinator.BeginAsync;
   this.autoUpdateAvailable=!preview&&(autoUpdateAvailable??CanAutoUpdateHere)();
   this.downloadRoot=downloadRoot??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MoriDesk","updates","downloads");
   current=AppUpdateService.NumericVersion(Assembly.GetExecutingAssembly().GetName().Version);
   VerticalScrollBarVisibility=ScrollBarVisibility.Auto;HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled;
   SetResourceReference(BackgroundProperty,"Canvas");SetResourceReference(ForegroundProperty,"Text");
   if(owner!=null){FontFamily=owner.FontFamily;FontSize=owner.FontSize;}
   var body=new StackPanel{Margin=new Thickness(28,22,28,24)};Content=body;
   body.Children.Add(new TextBlock{Text="关于与更新",FontSize=18,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,20)});
   body.Children.Add(new TextBlock{Text="Nook",FontSize=25,FontWeight=FontWeights.SemiBold});
   AddHint(body,"当前版本  "+current.ToString(3),new Thickness(0,5,0,18));
   AddHint(body,this.autoUpdateAvailable?"从 GitHub 获取新版本，无需登录。你决定何时下载和重启。":preview?"测试预览不会下载或安装更新。":ManualUpdateHint,new Thickness(0,0,0,4));
   var row=new WrapPanel{Margin=new Thickness(0,12,0,0)};body.Children.Add(row);
   foreach(var button in new[]{Check,Install,Cancel}){
    button.Padding=new Thickness(14,8,14,8);button.Margin=new Thickness(0,0,8,6);button.MinHeight=36;
    button.SetResourceReference(BackgroundProperty,"Control");button.SetResourceReference(ForegroundProperty,"Text");button.SetResourceReference(BorderBrushProperty,"Border");
    AutomationProperties.SetName(button,Convert.ToString(button.Content));row.Children.Add(button);
   }
   Install.SetResourceReference(BackgroundProperty,"Primary");Install.SetResourceReference(ForegroundProperty,"OnPrimary");
   Progress.SetResourceReference(ForegroundProperty,"Accent");Progress.SetResourceReference(BackgroundProperty,"Divider");body.Children.Add(Progress);
   Status.SetResourceReference(TextBlock.ForegroundProperty,"Muted");AutomationProperties.SetName(Status,"更新状态");body.Children.Add(Status);
   body.Children.Add(Previews);AddHint(body,"预览版包含新功能，可能仍需完善。关闭后只检查正式版。",new Thickness(22,0,0,0));
   body.Children.Add(releaseTitle);Notes.SetResourceReference(BackgroundProperty,"Canvas");Notes.SetResourceReference(ForegroundProperty,"Text");AutomationProperties.SetName(Notes,"更新说明");body.Children.Add(Notes);
   var divider=new Border{Height=1,Margin=new Thickness(0,24,0,16)};divider.SetResourceReference(Border.BackgroundProperty,"Divider");body.Children.Add(divider);
   AddHint(body,this.autoUpdateAvailable?"更新前先保存并备份资料记录和设置；新版成功启动后自动清理已核验的旧程序。更新失败保留旧版，原始素材不搬动、不删除。":"这里只检查发布信息，不会为当前运行位置下载或启动自动安装器。手动升级前请保留旧版和资料备份。",new Thickness(0,0,0,8));
   if(this.autoUpdateAvailable&&!preview)AddHint(body,InstalledVersionMaintenance.ReadSummary(),new Thickness(0,0,0,8));
   AddHint(body,this.autoUpdateAvailable?"下载会校验完整性。连接失败时可以重试，不影响当前版本；安装需要正常退出 Nook，未完成的截图会关闭。":"连接失败时可以重试，不影响当前版本。",new Thickness(0,0,0,12));
   var project=new Button{Content=this.autoUpdateAvailable||preview?"在 GitHub 查看项目":"前往 GitHub 手动升级",Padding=new Thickness(0,6,0,6),HorizontalAlignment=HorizontalAlignment.Left,BorderThickness=new Thickness(0)};
   project.SetResourceReference(BackgroundProperty,"Canvas");project.SetResourceReference(ForegroundProperty,"Accent");project.IsEnabled=!preview;
   string projectUrl="https://github.com/"+AppUpdateService.Repository+(this.autoUpdateAvailable||preview?"":"/releases");
   project.Click+=(s,e)=>{try{Process.Start(new ProcessStartInfo(projectUrl){UseShellExecute=true});}catch{Status.Text="无法打开浏览器，请访问 "+projectUrl;}};body.Children.Add(project);
   Check.Click+=async(s,e)=>await CheckNowAsync();Install.Click+=async(s,e)=>await InstallNowAsync();Cancel.Click+=(s,e)=>CancelOperation();
   Previews.Click+=(s,e)=>{ApplyRelease(null);Status.Text="版本范围已改变，请重新检查。";};
   Status.Text=preview?"测试预览：不会联网、安装或更改个人设置。":this.autoUpdateAvailable?"点击“检查更新”获取最新发布。":"可以检查最新版本；当前运行位置仅支持手动升级。";
   SetBusy(false);
  }
  static void AddHint(Panel body,string text,Thickness margin){var label=new TextBlock{Text=text,FontSize=12,TextWrapping=TextWrapping.Wrap,LineHeight=20,Margin=margin};label.SetResourceReference(TextBlock.ForegroundProperty,"Muted");body.Children.Add(label);}
  internal bool IsBusy {get{return busy;}}
  internal bool IsDisposed {get{return disposed;}}
  internal UpdateRelease AvailableRelease {get{return release;}}
  internal static bool IsManagedUpdateLocation(string executable,string programRoot){
   try{return !string.IsNullOrWhiteSpace(programRoot)&&AppUpdateCoordinator.ManagedStartupExecutable(executable,programRoot);}
   catch(ArgumentException){return false;}catch(IOException){return false;}catch(InvalidDataException){return false;}catch(NotSupportedException){return false;}
  }
  static bool CanAutoUpdateHere(){
   try{
    string executable=Assembly.GetExecutingAssembly().Location,programRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","MoriDesk");
    if(!IsManagedUpdateLocation(executable,programRoot))return false;
    AppUpdateCoordinator.PlainPath(executable);return true;
   }catch(ArgumentException){return false;}catch(IOException){return false;}catch(InvalidDataException){return false;}catch(UnauthorizedAccessException){return false;}catch(System.Security.SecurityException){return false;}catch(NotSupportedException){return false;}
  }
  internal void ApplyRelease(UpdateRelease value){
   release=value;releaseTitle.Text=value==null?"":"可更新至 "+value.Tag;
   releaseTitle.Visibility=Notes.Visibility=value==null?Visibility.Collapsed:Visibility.Visible;
   Install.Visibility=value!=null&&(autoUpdateAvailable||preview)?Visibility.Visible:Visibility.Collapsed;
   Notes.Text=value==null?"":string.IsNullOrWhiteSpace(value.Notes)?"此版本没有填写更新说明。":value.Notes;
   Install.Content=value==null?"下载并重启更新":"下载并重启更新 · "+SizeLabel(value.Size);SetBusy(busy);
  }
  internal static string SizeLabel(long size){return size<1048576?Math.Max(1,size/1024)+" KB":(size/1048576d).ToString("0.#",System.Globalization.CultureInfo.InvariantCulture)+" MB";}
  void SetBusy(bool value){
   busy=value;Check.IsEnabled=Previews.IsEnabled=!preview&&!disposed&&!value;Install.IsEnabled=!preview&&autoUpdateAvailable&&!disposed&&!value&&release!=null;
   Cancel.Visibility=value?Visibility.Visible:Visibility.Collapsed;Cancel.IsEnabled=!disposed&&value;
   if(!value){Progress.IsIndeterminate=false;Progress.Visibility=Visibility.Collapsed;}
  }
  CancellationToken StartOperation(){generation++;operation=new CancellationTokenSource();SetBusy(true);return operation.Token;}
  void FinishOperation(){generation++;var finished=operation;operation=null;if(finished!=null)finished.Dispose();SetBusy(false);}
  internal void CancelOperation(){if(operation!=null){operation.Cancel();Cancel.IsEnabled=false;Status.Text="正在取消，不会关闭当前版本…";}}
  internal async Task CheckNowAsync(){
   if(preview||disposed||busy)return;ApplyRelease(null);var token=StartOperation();Status.Text="正在检查 GitHub 新版本…";
   try{
    using(var service=serviceFactory()){
     var result=await service.CheckAsync(current,"",Previews.IsChecked==true,token);token.ThrowIfCancellationRequested();if(disposed)return;
     ApplyRelease(result);Status.Text=result==null?(Previews.IsChecked==true?"没有更新的可安装版本。":"没有更新的正式版；你可以勾选“包含预览版本”。"):autoUpdateAvailable?"发现新版本。点击后将下载，完成校验再保存资料并重启更新。":"发现新版本。请点击“前往 GitHub 手动升级”；当前运行位置不能自动安装。";
    }
   }catch(OperationCanceledException){if(!disposed)Status.Text="已取消检查。";}
   catch(Exception error){if(!disposed)Status.Text="检查失败，当前版本不受影响。\n"+error.Message;}
   finally{FinishOperation();}
  }
  internal async Task InstallNowAsync(){
   if(preview||disposed||busy||release==null)return;
   if(!autoUpdateAvailable){Status.Text=ManualUpdateHint;return;}
   var selected=release;var token=StartOperation();long attempt=generation;Progress.Value=0;Progress.Visibility=Visibility.Visible;Status.Text="正在准备下载…";
   var progress=new Progress<UpdateProgress>(value=>{
    if(disposed||!busy||attempt!=generation||token.IsCancellationRequested)return;
    Progress.IsIndeterminate=value.Stage!="downloading"||value.Total<=0;
    if(value.Stage=="downloading"){Progress.Value=value.Total>0?Math.Min(100,100d*value.Downloaded/value.Total):0;Status.Text="正在下载 "+SizeLabel(value.Downloaded)+" / "+SizeLabel(value.Total);}
    else Status.Text=value.Stage=="verifying"?"下载完成，正在校验文件…":"文件校验通过，正在准备安全更新…";
   });
   try{
    string path;using(var service=serviceFactory())path=await service.DownloadAsync(selected,"",downloadRoot,progress,token);
    token.ThrowIfCancellationRequested();if(disposed)return;Status.Text="正在准备更新。请先保存编辑内容，安装准备好后会正常退出 Nook。";Progress.IsIndeterminate=true;
    generation++; // Queued download progress must not overwrite handoff feedback.
    string result=await handoff(owner,path,selected.Version,selected.ExpectedSha256,token);token.ThrowIfCancellationRequested();if(!disposed)Status.Text=result;
   }catch(OperationCanceledException){if(!disposed)Status.Text="已取消更新，继续使用当前版本。";}
   catch(Exception error){if(!disposed)Status.Text="更新未完成，当前版本保留，可以重试。\n"+error.Message;}
   finally{FinishOperation();}
  }
  public void Dispose(){if(disposed)return;disposed=true;if(operation!=null)operation.Cancel();SetBusy(false);}
 }
}
