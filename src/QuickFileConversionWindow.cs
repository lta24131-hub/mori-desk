using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Mori;

namespace MoriDesk {
 // The external-file tool never imports a source into the library. Its retained
 // non-modal window is only a view over a file -> format -> result operation.
 internal sealed class QuickFileConversionWindow:Window {
  internal readonly QuickFileConversionPanel Panel;
  bool exiting;
  internal QuickFileConversionWindow(Window owner){
   Title="Nook · 格式转换";Width=620;Height=590;MinWidth=480;MinHeight=440;
   WindowStartupLocation=WindowStartupLocation.CenterOwner;
   if(owner!=null&&owner.IsVisible)Owner=owner;
   ShowInTaskbar=true;Theme.Attach(this,owner);
   var client=new FileConversionClient();
   Panel=new QuickFileConversionPanel(FileConversionClient.IsAvailable,client.GetCapabilitiesAsync,client.ConvertAsync,
    Settings.Load().FileConversionOutputPath);
   Content=Panel;
   Panel.ManageComponents=ids=>ConversionComponentsWindow.OpenAsync(this,ids);
   Panel.ChooseFiles=()=>{var picker=new Microsoft.Win32.OpenFileDialog{Title="选择要转换的文件 · 不导入素材库",Multiselect=true,Filter="所有文件|*.*"};return picker.ShowDialog(this)==true?picker.FileNames:null;};
   Panel.OpenResult=path=>FileRouter.Open(this,path,Path.GetFileName(path));
   Panel.RevealResult=FileRouter.Reveal;
   Panel.CopyResults=paths=>{var files=new System.Collections.Specialized.StringCollection();files.AddRange(paths);Clipboard.SetFileDropList(files);};
   PreviewKeyDown+=delegate(object sender,KeyEventArgs e){if(e.Key==Key.Escape){e.Handled=true;Hide();}};
   Closing+=delegate(object sender,System.ComponentModel.CancelEventArgs e){if(!exiting){e.Cancel=true;Hide();}};
   Closed+=delegate{Panel.Dispose();};
  }
  internal void RefreshOutputFolder(){if(!Panel.IsBusy)Panel.TrySetOutputFolder(Settings.Load().FileConversionOutputPath);}
  internal void CloseForExit(){exiting=true;Panel.Dispose();Close();}
 }

 // Constructible without Application, HWND, clipboard or user-data access.
 // All file work starts only in AddFilesAsync / StartAsync and can be injected.
 internal sealed class QuickFileConversionPanel:Grid,IDisposable {
  internal readonly Button Add,Clear,Cancel,MoreFormats,CopyAll,OpenFolder;
  internal readonly ListBox Results;
  internal readonly TextBlock Status;
  internal readonly WrapPanel FormatChoices;
  internal Func<string[]> ChooseFiles;
  internal Action<string> OpenResult,RevealResult,Notify;
  internal Action<string[]> CopyResults;
  internal Func<string[],Task<bool>> ManageComponents;
  readonly Func<string[],CancellationToken,Task<ConversionCapabilities>> capabilities;
  readonly Func<string[],string,string,int,IProgress<ConversionProgress>,CancellationToken,Task<ConversionBatchResult>> convert;
  readonly bool available;
  string outputFolder;
  readonly Border dropZone;
  readonly TextBlock sourceTitle,sourceDetail,formatHeading,resultHeading,destinationHint;
  readonly StackPanel capabilityNotes;
  readonly ProgressBar progress;
  readonly ObservableCollection<ResultEntry> history=new ObservableCollection<ResultEntry>();
  readonly HashSet<string> historyPaths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  string[] inputs=new string[0];
  string[] latestResults=new string[0];
  ConversionCapabilities currentCapabilities;
  CancellationTokenSource checkingCancellation,operation;
  int generation;
  bool checking,running,disposed,resultDragReady,managingComponents;
  Point dragStart;
  internal bool IsBusy {get{return running;}}
  internal bool IsChecking {get{return checking;}}
  internal bool IsDisposed {get{return disposed;}}
  internal string[] Inputs {get{return (string[])inputs.Clone();}}
  internal string[] ResultPaths {get{return history.Select(item=>item.Path).ToArray();}}
  internal string OutputFolder {get{return outputFolder;}}
  internal bool TrySetOutputFolder(string destination){
   if(disposed||running)return false;
   outputFolder=Settings.ValidateConversionOutputPath(destination);
   destinationHint.Text="新文件保存到："+outputFolder;destinationHint.ToolTip=outputFolder;
   return true;
  }
  internal sealed class ResultEntry {
   public string Path {get;set;}
   public string Name {get{return System.IO.Path.GetFileName(Path);}}
  }
  internal QuickFileConversionPanel(bool engineAvailable,
   Func<string[],CancellationToken,Task<ConversionCapabilities>> capabilityProvider,
   Func<string[],string,string,int,IProgress<ConversionProgress>,CancellationToken,Task<ConversionBatchResult>> converter,
   string destination){
   if(capabilityProvider==null||converter==null)throw new ArgumentNullException("conversion");
   available=engineAvailable;capabilities=capabilityProvider;convert=converter;outputFolder=destination;
   FontSetup();SetResourceReference(BackgroundProperty,"Surface");
   RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
   RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
   var body=new StackPanel{Margin=new Thickness(22,20,22,14)};
   var scroller=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
   Children.Add(scroller);
   var heading=Text("拖入文件，选格式就完成",17,false);heading.FontWeight=FontWeights.SemiBold;body.Children.Add(heading);
   body.Children.Add(Text("不必收藏进素材库 · 原文件始终保留",12,true,new Thickness(0,6,0,16)));
   var dropContent=new StackPanel{Margin=new Thickness(18),VerticalAlignment=VerticalAlignment.Center};
   sourceTitle=Text("把文件拖到这里",15,false);sourceTitle.TextTrimming=TextTrimming.CharacterEllipsis;
   sourceTitle.TextWrapping=TextWrapping.NoWrap;dropContent.Children.Add(sourceTitle);
   sourceDetail=Text("图片、视频、音频、PDF、Office 等",12,true,new Thickness(0,6,0,10));
   sourceDetail.TextWrapping=TextWrapping.NoWrap;sourceDetail.TextTrimming=TextTrimming.CharacterEllipsis;dropContent.Children.Add(sourceDetail);
   var sourceActions=new WrapPanel();
   Add=Button("选择文件…","QuickConversionAdd",async()=>await ChooseAsync());sourceActions.Children.Add(Add);
   Clear=Button("清空待转换","QuickConversionClear",ClearInputs);sourceActions.Children.Add(Clear);dropContent.Children.Add(sourceActions);
   dropZone=new Border{MinHeight=130,CornerRadius=new CornerRadius(6),BorderThickness=new Thickness(1),Child=dropContent};
   dropZone.SetResourceReference(Border.BackgroundProperty,"Panel");dropZone.SetResourceReference(Border.BorderBrushProperty,"Border");body.Children.Add(dropZone);
   formatHeading=Text("转为",13,false,new Thickness(0,18,0,9));body.Children.Add(formatHeading);
   FormatChoices=new WrapPanel();body.Children.Add(FormatChoices);
   MoreFormats=Button("更多格式","QuickConversionMore",ShowMoreFormats);
   capabilityNotes=new StackPanel{Margin=new Thickness(0,4,0,0)};body.Children.Add(capabilityNotes);
   progress=new ProgressBar{Height=3,IsIndeterminate=true,Margin=new Thickness(0,8,0,0),Visibility=Visibility.Collapsed};
   progress.SetResourceReference(Control.ForegroundProperty,"Accent");body.Children.Add(progress);
   var statusRow=new DockPanel{Margin=new Thickness(0,10,0,0)};
   Cancel=Button("取消剩余","QuickConversionCancel",CancelOperation);DockPanel.SetDock(Cancel,Dock.Right);statusRow.Children.Add(Cancel);
   Status=Text("",12,true);Status.VerticalAlignment=VerticalAlignment.Center;AutomationProperties.SetName(Status,"文件转换状态");statusRow.Children.Add(Status);body.Children.Add(statusRow);
   resultHeading=Text("",13,false,new Thickness(0,18,0,8));body.Children.Add(resultHeading);
   Results=new ListBox{Name="QuickConversionResults",ItemsSource=history,DisplayMemberPath="Name",SelectionMode=SelectionMode.Extended,
    MinHeight=0,MaxHeight=180,BorderThickness=new Thickness(0),HorizontalContentAlignment=HorizontalAlignment.Stretch};
   Results.SetResourceReference(Control.BackgroundProperty,"Panel");Results.SetResourceReference(Control.ForegroundProperty,"Text");
   ScrollViewer.SetHorizontalScrollBarVisibility(Results,ScrollBarVisibility.Disabled);
   VirtualizingStackPanel.SetIsVirtualizing(Results,true);VirtualizingStackPanel.SetVirtualizationMode(Results,VirtualizationMode.Recycling);
   var rowStyle=new Style(typeof(ListBoxItem));rowStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,new Binding("Path")));Results.ItemContainerStyle=rowStyle;
   AutomationProperties.SetName(Results,"转换结果，可双击打开或拖出文件");
   Results.MouseDoubleClick+=delegate(object sender,MouseButtonEventArgs e){
    var row=ItemsControl.ContainerFromElement(Results,e.OriginalSource as DependencyObject)as ListBoxItem;
    var item=row==null?null:row.Content as ResultEntry;
    if(item!=null)RunResultAction(()=>{EnsureResult(item.Path);if(OpenResult!=null)OpenResult(item.Path);});
   };
   Results.PreviewMouseLeftButtonDown+=delegate(object sender,MouseButtonEventArgs e){
    dragStart=e.GetPosition(Results);resultDragReady=ItemsControl.ContainerFromElement(Results,e.OriginalSource as DependencyObject)is ListBoxItem;
   };
   Results.PreviewMouseLeftButtonUp+=delegate{resultDragReady=false;};
   Results.PreviewMouseMove+=DragResults;
   body.Children.Add(Results);
   var resultTools=new WrapPanel{Margin=new Thickness(0,7,0,0)};
   CopyAll=Button("复制结果","QuickConversionCopy",CopySelectedResults);resultTools.Children.Add(CopyAll);
   OpenFolder=Button("打开位置","QuickConversionReveal",RevealSelectedResult);resultTools.Children.Add(OpenFolder);body.Children.Add(resultTools);
   destinationHint=Text("新文件保存到："+outputFolder,11,true,new Thickness(22,7,22,12));
   destinationHint.TextWrapping=TextWrapping.NoWrap;destinationHint.TextTrimming=TextTrimming.CharacterEllipsis;
   destinationHint.ToolTip=outputFolder;Grid.SetRow(destinationHint,1);Children.Add(destinationHint);
   AllowDrop=true;
   PreviewDragOver+=delegate(object sender,DragEventArgs e){e.Handled=true;e.Effects=!running&&e.Data.GetDataPresent(DataFormats.FileDrop)&&(e.AllowedEffects&DragDropEffects.Copy)!=0?DragDropEffects.Copy:DragDropEffects.None;};
   PreviewDrop+=async delegate(object sender,DragEventArgs e){
    e.Handled=true;if(running){SetStatus("当前转换仍在继续；完成后再拖入另一个文件。");return;}
    if(e.Data.GetDataPresent(DataFormats.FileDrop)&&(e.AllowedEffects&DragDropEffects.Copy)!=0)await AddFilesAsync(e.Data.GetData(DataFormats.FileDrop)as string[]);
   };
   SetStatus(available?"选择格式后立即另存副本；不会覆盖原件。":"转换组件未安装，请安装包含转换组件的完整版本。");
   UpdateControls();
  }
  void FontSetup(){SetValue(System.Windows.Documents.TextElement.FontFamilyProperty,new FontFamily("Microsoft YaHei UI"));SetValue(System.Windows.Documents.TextElement.FontSizeProperty,13d);}
  static TextBlock Text(string value,double size,bool muted,Thickness? margin=null){
   var text=new TextBlock{Text=value,FontSize=size,TextWrapping=TextWrapping.Wrap,Margin=margin??new Thickness(0)};
   text.SetResourceReference(TextBlock.ForegroundProperty,muted?"Muted":"Text");return text;
  }
  static Button Button(string title,string name,Action action){
   var button=new Button{Content=title,Name=name,MinHeight=32,Padding=new Thickness(10,5,10,5),Margin=new Thickness(0,0,7,6)};
   button.SetResourceReference(Control.BackgroundProperty,"Control");button.SetResourceReference(Control.ForegroundProperty,"Text");button.SetResourceReference(Control.BorderBrushProperty,"Border");
   button.Click+=delegate{action();};return button;
  }
  async Task ChooseAsync(){if(disposed||running||ChooseFiles==null)return;try{await AddFilesAsync(ChooseFiles());}catch(Exception error){SetStatus(error.Message);}}
  internal async Task AddFilesAsync(string[] paths){
   if(disposed||paths==null||paths.Length==0)return;
   if(running){SetStatus("当前转换仍在继续；本次原文件不会被更换。");return;}
   var supplied=(string[])paths.Clone();
   CancelCheck();int request=++generation;var cancellation=new CancellationTokenSource();checkingCancellation=cancellation;
   checking=true;UpdateControls();SetStatus("正在检查文件和可转换格式…");
   try{
    var validated=await Task.Run(()=>FileConversionClient.ValidateInputs(supplied),cancellation.Token);
    if(disposed||request!=generation||cancellation.IsCancellationRequested)return;
    inputs=validated;currentCapabilities=null;BuildChoices();UpdateControls();
    if(!available){SetStatus("转换组件未安装，请安装包含转换组件的完整版本。");return;}
    var reply=await Task.Run(()=>capabilities(validated,cancellation.Token));
    if(disposed||request!=generation||cancellation.IsCancellationRequested)return;
    currentCapabilities=reply;BuildChoices();
    SetStatus(Choices().Length>0?"点一个格式立即转换；原文件不会改动。":"没有可用格式，请查看上方说明。");
   }catch(OperationCanceledException){}
   catch(Exception error){if(!disposed&&request==generation){
    currentCapabilities=null;BuildChoices();SetStatus("无法检查转换格式："+error.Message);
    if(inputs.Length>0&&available)capabilityNotes.Children.Add(Button("重试检查","QuickConversionRetry",async()=>await AddFilesAsync(inputs)));
   }}
   finally{
    if(ReferenceEquals(checkingCancellation,cancellation)){checkingCancellation=null;checking=false;UpdateControls();}
    cancellation.Dispose();
   }
  }
  void ClearInputs(){
   if(disposed||running)return;CancelCheck();generation++;inputs=new string[0];currentCapabilities=null;BuildChoices();UpdateControls();
   SetStatus("拖入另一个文件开始；已生成结果仍在下方。");
  }
  ConversionTarget[] Choices(){
   if(currentCapabilities==null)return new ConversionTarget[0];
   var menu=MainWindow.BuildQuickConversionMenu(null,true,currentCapabilities);
   var ids=TargetIds(menu).ToArray();
   return ids.Select(id=>(currentCapabilities.Targets??new ConversionTarget[0]).First(target=>target!=null&&target.Id==id)).ToArray();
  }
  static IEnumerable<string> TargetIds(MenuItem menu){
   foreach(var child in menu.Items.OfType<MenuItem>()){
    var id=child.Tag as string;if(id!=null&&child.IsEnabled)yield return id;
    foreach(string nested in TargetIds(child))yield return nested;
   }
  }
  void BuildChoices(){
   FormatChoices.Children.Clear();capabilityNotes.Children.Clear();var targets=Choices();
   foreach(var target in targets.Take(6)){
    var choice=target;var content=new StackPanel();
    content.Children.Add(Text(string.IsNullOrWhiteSpace(target.Label)?ConversionFormats.Label(target.Id):target.Label,13,false));
    string warning=MainWindow.QuickConversionWarning(target,currentCapabilities.Files??new ConversionInputInfo[0]);
    if(!string.IsNullOrWhiteSpace(warning))content.Children.Add(Text(warning,10,true,new Thickness(0,3,0,0)));
    var button=Button("", "QuickConvert_"+target.Id,async()=>await StartAsync(choice.Id));
    button.Content=content;button.Tag=target.Id;button.Width=163;button.MinHeight=42;button.HorizontalContentAlignment=HorizontalAlignment.Left;
    button.ToolTip=(target.Warning??"")+"\n另存新文件，保留原件";FormatChoices.Children.Add(button);
   }
   if(targets.Length>6)FormatChoices.Children.Add(MoreFormats);
   if(inputs.Length>0&&currentCapabilities!=null){
    if(targets.Length==0)capabilityNotes.Children.Add(Text("当前文件没有可用的转换格式",12,true));
    var componentIds=(currentCapabilities.MissingComponentIds??new string[0]).Where(FileConversionClient.IsComponentId).Distinct().ToArray();
    if(componentIds.Length>0&&ManageComponents!=null){
     capabilityNotes.Children.Add(Text(targets.Length==0?"安装相应组件后即可选择转换格式。":"还有更多格式可通过附加组件启用。",12,true,new Thickness(0,4,0,5)));
     var install=Button("安装所需组件…","QuickConversionComponents",async()=>await ManageMissingComponentsAsync());
     install.IsEnabled=!managingComponents&&!running&&!checking;capabilityNotes.Children.Add(install);
    }else foreach(string missing in (currentCapabilities.MissingEngines??new string[0]).Where(value=>!string.IsNullOrWhiteSpace(value)).Distinct().Take(3))
      capabilityNotes.Children.Add(Text(missing,12,true,new Thickness(0,4,0,0)));
    if(targets.Length==0)foreach(string warning in (currentCapabilities.Files??new ConversionInputInfo[0]).Where(file=>file!=null)
     .SelectMany(file=>file.Warnings??new string[0]).Where(value=>!string.IsNullOrWhiteSpace(value)).Distinct().Take(3))
      capabilityNotes.Children.Add(Text(warning,12,true,new Thickness(0,4,0,0)));
   }
   UpdateControls();
  }
  internal async Task ManageMissingComponentsAsync(){
   if(disposed||running||checking||managingComponents||ManageComponents==null||currentCapabilities==null)return;
   var ids=(currentCapabilities.MissingComponentIds??new string[0]).Where(FileConversionClient.IsComponentId).Distinct().ToArray();
   if(ids.Length==0)return;
   managingComponents=true;
   // A non-modal manager may stay open while the user selects another source.
   // Refresh the current source, never restore the old snapshot over their work.
   try{if(await ManageComponents(ids)){if(!disposed&&!running&&inputs.Length>0)await AddFilesAsync(inputs);}}
   catch(Exception error){if(!disposed)SetStatus("无法管理组件："+error.Message);}
   finally{managingComponents=false;if(!disposed)BuildChoices();}
  }
  void ShowMoreFormats(){
   if(disposed||running||checking)return;
   var owner=Window.GetWindow(this);var menu=owner==null?new ContextMenu():Theme.Menu(owner);
   foreach(var target in Choices().Skip(6)){
    var choice=target;var content=new StackPanel();content.Children.Add(Text(target.Label??ConversionFormats.Label(target.Id),13,false));
    string warning=MainWindow.QuickConversionWarning(target,currentCapabilities.Files??new ConversionInputInfo[0]);
    if(!string.IsNullOrWhiteSpace(warning)){var line=Text(warning,11,true);line.MaxWidth=300;content.Children.Add(line);}
    var item=new MenuItem{Header=content,Tag=target.Id,ToolTip=target.Warning};item.Click+=async delegate{await StartAsync(choice.Id);};menu.Items.Add(item);
   }
   menu.PlacementTarget=MoreFormats;menu.IsOpen=true;
  }
  internal async Task StartAsync(string target){
   if(disposed||running||checking)return;
   if(!available){SetStatus("转换组件未安装，未开始转换。");return;}
   if(inputs.Length==0||!Choices().Any(choice=>choice.Id==target)){SetStatus("请先选择文件及可用格式。");return;}
   string[] snapshot=(string[])inputs.Clone();string destination=outputFolder;string shownStamp=MainWindow.QuickConversionCapabilityStamp(currentCapabilities,target);var cancellation=new CancellationTokenSource();
   operation=cancellation;running=true;UpdateControls();SetStatus("正在检查并转换为 "+ConversionFormats.Label(target)+"…");
   FileStream[] locks=null;
   try{
    locks=await Task.Run(()=>MainWindow.OpenQuickInputReadLocks(snapshot,cancellation.Token));
    var fresh=await Task.Run(()=>capabilities(snapshot,cancellation.Token));
    var formats=MainWindow.ValidateQuickConversionCapabilities(snapshot,target,fresh);
    if(shownStamp!=MainWindow.QuickConversionCapabilityStamp(fresh,target)){
     currentCapabilities=fresh;BuildChoices();SetStatus("文件的转换方式或内容提示已变化，请查看格式说明后再点一次。");return;
    }
    var reported=new Progress<ConversionProgress>(value=>{if(!disposed&&ReferenceEquals(operation,cancellation)&&!cancellation.IsCancellationRequested)SetStatus("正在转换 "+Math.Min(value.Completed+1,value.Total)+" / "+value.Total+" 个文件… 可继续使用其他功能。");});
    var outcome=await QuickConversionJob.RunAsync(snapshot,target,destination,
     (path,token)=>Task.Run(()=>{token.ThrowIfCancellationRequested();FileConversionClient.ValidateInputs(new[]{path});return formats[path];},token),
     convert,RecordResultsAsync,reported,cancellation.Token);
    if(!disposed){SetStatus(outcome.Message.Replace("收藏","保存"));try{if(Notify!=null)Notify(Status.Text);}catch{/* A notification cannot turn a committed file into a failed conversion. */}}
   }catch(OperationCanceledException){if(!disposed)SetStatus("已取消后续转换；已生成结果和原文件均保留。");}
   catch(Exception error){if(!disposed)SetStatus("转换未完成："+error.Message+"；原文件保留。");}
   finally{if(locks!=null)foreach(var stream in locks)stream.Dispose();running=false;operation=null;cancellation.Dispose();if(!disposed)UpdateControls();}
  }
  Task<ConversionCollectionResult> RecordResultsAsync(IList<ConvertedFile> files){
   var paths=files.SelectMany(file=>file.Outputs).ToArray();
   // New jobs appear first; pages/frames within one job keep their source order.
   foreach(string path in paths.Reverse())if(historyPaths.Add(path))history.Insert(0,new ResultEntry{Path=path});
   latestResults=(string[])paths.Clone();
   if(!disposed){
    Results.SelectedItems.Clear();
    var latest=new HashSet<string>(latestResults,StringComparer.OrdinalIgnoreCase);
    foreach(var item in history.Where(item=>latest.Contains(item.Path)))Results.SelectedItems.Add(item);
    UpdateControls();
    Dispatcher.BeginInvoke(new Action(()=>{if(!disposed&&IsVisible)Results.BringIntoView();}),System.Windows.Threading.DispatcherPriority.Loaded);
   }
   return Task.FromResult(new ConversionCollectionResult{CollectedPaths=paths,Message="已保存结果"});
  }
  internal void CancelOperation(){
   if(operation!=null){operation.Cancel();SetStatus("正在停止后续转换；已经完成的文件会保留。");}
   else if(checking){CancelCheck();generation++;SetStatus("已停止格式检查。");UpdateControls();}
  }
  void CancelCheck(){if(checkingCancellation!=null){var old=checkingCancellation;checkingCancellation=null;old.Cancel();}checking=false;}
  void UpdateControls(){
   if(disposed)return;
   sourceTitle.Text=inputs.Length==0?"把文件拖到这里":inputs.Length==1?Path.GetFileName(inputs[0]):"已选择 "+inputs.Length+" 个文件";
   sourceTitle.ToolTip=inputs.Length==0?null:string.Join("\n",inputs.Take(12));
   sourceDetail.Text=inputs.Length==0?"图片、视频、音频、PDF、Office 等":inputs.Length==1?Path.GetDirectoryName(inputs[0]):string.Join("、",inputs.Take(3).Select(Path.GetFileName))+(inputs.Length>3?"…":"");
   sourceDetail.ToolTip=inputs.Length==0?null:string.Join("\n",inputs.Take(12));
   dropZone.MinHeight=inputs.Length==0?150:110;Add.Content=inputs.Length==0?"选择文件…":"换一个文件…";
   Add.IsEnabled=!running;Clear.IsEnabled=!running;Clear.Visibility=inputs.Length>0?Visibility.Visible:Visibility.Collapsed;
   formatHeading.Visibility=inputs.Length>0?Visibility.Visible:Visibility.Collapsed;
   FormatChoices.IsEnabled=!running&&!checking;progress.Visibility=running||checking?Visibility.Visible:Visibility.Collapsed;
   Cancel.Visibility=running||checking?Visibility.Visible:Visibility.Collapsed;
   resultHeading.Text="转换结果 · "+history.Count+" 个，可双击打开或拖出";
   resultHeading.Visibility=Results.Visibility=history.Count>0?Visibility.Visible:Visibility.Collapsed;
   CopyAll.Visibility=OpenFolder.Visibility=history.Count>0?Visibility.Visible:Visibility.Collapsed;
  }
  void SetStatus(string message){if(!disposed)Status.Text=message;}
  string[] SelectedResults(){
   var selected=Results.SelectedItems.Cast<ResultEntry>().Select(item=>item.Path).ToArray();
   return selected.Length>0?selected:(string[])latestResults.Clone();
  }
  static void EnsureResult(string path){FileConversionClient.ValidateLocalPath(path,true);if(!File.Exists(path))throw new IOException("结果文件已移动或删除。");}
  void RunResultAction(Action action){if(disposed)return;try{action();}catch(Exception error){SetStatus("无法处理结果："+error.Message);}}
  void CopySelectedResults(){RunResultAction(()=>{var paths=SelectedResults();foreach(string path in paths)EnsureResult(path);if(paths.Length>0&&CopyResults!=null){CopyResults(paths);SetStatus("已复制 "+paths.Length+" 个结果文件，可粘贴到其他软件。");}});}
  void RevealSelectedResult(){RunResultAction(()=>{string path=SelectedResults().FirstOrDefault();if(path!=null){EnsureResult(path);if(RevealResult!=null)RevealResult(path);}});}
  void DragResults(object sender,MouseEventArgs e){
   if(!resultDragReady||e.LeftButton!=MouseButtonState.Pressed)return;
   Point current=e.GetPosition(Results);
   if(Math.Abs(current.X-dragStart.X)<SystemParameters.MinimumHorizontalDragDistance&&Math.Abs(current.Y-dragStart.Y)<SystemParameters.MinimumVerticalDragDistance)return;
   var paths=Results.SelectedItems.Cast<ResultEntry>().Select(item=>item.Path).ToArray();if(paths.Length==0)return;
   resultDragReady=false;RunResultAction(()=>{foreach(string path in paths)EnsureResult(path);DragDrop.DoDragDrop(Results,new DataObject(DataFormats.FileDrop,paths),DragDropEffects.Copy);});
  }
  public void Dispose(){if(disposed)return;disposed=true;IsEnabled=false;generation++;CancelCheck();if(operation!=null)operation.Cancel();}
 }
}
