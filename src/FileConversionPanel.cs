using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MoriDesk {
 internal sealed class ConversionCollectionResult {
  public string Message {get;set;}
  public string[] CollectedPaths {get;set;}
 }

 // A task lives independently of its visibility. No HWND, clipboard access,
 // native dialog, conversion process or library mutation at construction.
 internal sealed class FileConversionPanel:Grid,IDisposable {
  internal readonly ListBox Files;
  internal readonly ComboBox Target;
  internal readonly Slider Quality;
  internal readonly TextBox Output;
  internal readonly CheckBox Collect;
  internal readonly Button Add,Remove,Start,Cancel,Open,Collapse,Retry,CollectResults,Change;
  internal readonly TextBlock Status,Warning,Summary;
  internal readonly StackPanel SingleFileCard;
  internal readonly Expander BatchFiles;
  internal readonly WrapPanel FormatChoices;
  internal Func<string[]> ChooseFiles;
  internal Func<string> ChooseFolder;
  internal Action<string> OpenFolder,PreviewResult,CopyResult,RevealResult,LocateResult;
  internal Action CloseRequested;
  internal Func<string,string> SourceName,SourceDetail,SourceStem;
  internal Func<string,Task<ImageSource>> LoadPreview;
  internal Func<IList<ConvertedFile>,Task<ConversionCollectionResult>> CollectDetailed;
  internal event Action OperationEnded,StateChanged;
  readonly Func<string[],CancellationToken,Task<ConversionCapabilities>> capabilities;
  readonly Func<string[],string,string,int,IProgress<ConversionProgress>,CancellationToken,Task<ConversionBatchResult>> convert;
  readonly Func<IList<ConvertedFile>,Task<string>> collect;
  readonly bool available;
  readonly Button browse,moreFormats;
  readonly RadioButton libraryDestination,folderDestination;
  readonly Border libraryDestinationFrame,folderDestinationFrame;
  readonly StackPanel body,empty,folderArea,qualityArea,resultsArea,resultRows;
  readonly Expander advanced;
  readonly Image sourceImage;
  readonly TextBlock sourceTitle,sourceDetail,qualityText,generated,destinationHelp;
  readonly ProgressBar progress;
  static readonly SemaphoreSlim PreviewSlots=new SemaphoreSlim(1,1);
  readonly List<string> paths=new List<string>();
  readonly Dictionary<string,ConvertedFile> results=new Dictionary<string,ConvertedFile>(StringComparer.OrdinalIgnoreCase);
  readonly HashSet<string> collectedPaths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  readonly HashSet<string> generatedPaths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  List<string> generatedOutputs=new List<string>();
  CancellationTokenSource probe,operation,previewCancellation;
  int generation,previewGeneration,resultPage;
  bool checking,running,collecting,disposed,syncDestination;
  string resultFolder,taskTarget,taskDestination;
  int taskQuality;
  bool taskCollect;
  internal bool IsBusy {get{return running;}}
  internal bool IsChecking {get{return checking;}}
  internal bool IsDisposed {get{return disposed;}}
  internal bool SingleFileMode {get{return paths.Count==1;}}
  internal string[] Inputs {get{return paths.ToArray();}}
  internal string ResultFolder {get{return resultFolder;}}
  internal ConvertedFile[] ResultFiles {get{return paths.Where(results.ContainsKey).Select(p=>results[p]).ToArray();}}
  internal string[] FailedInputs {get{return taskTarget==null?new string[0]:paths.Where(p=>!results.ContainsKey(p)||!HasOutputs(results[p])).ToArray();}}
  bool HasCollector {get{return collect!=null||CollectDetailed!=null;}}

  internal FileConversionPanel(bool engineAvailable,Func<string[],CancellationToken,Task<ConversionCapabilities>> capabilityProvider,Func<string[],string,string,int,IProgress<ConversionProgress>,CancellationToken,Task<ConversionBatchResult>> converter,Func<IList<ConvertedFile>,Task<string>> collector=null,string defaultOutput=null){
   available=engineAvailable;capabilities=capabilityProvider;convert=converter;collect=collector;
   SetValue(TextElement.FontFamilyProperty,new FontFamily("Microsoft YaHei UI"));SetValue(TextElement.FontSizeProperty,13d);
   SetResourceReference(BackgroundProperty,"Surface");
   RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
   body=new StackPanel{Margin=new Thickness(20,0,20,24)};
   var scroller=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Focusable=false};Grid.SetRow(scroller,1);Children.Add(scroller);
   // Keep the return action accessible while long results or errors scroll.
   var heading=new DockPanel{Margin=new Thickness(20,18,20,20)};
   Collapse=MakeButton("收起","ConversionCollapse",()=>{if(CloseRequested!=null)CloseRequested();},false);
   Collapse.MinHeight=28;Collapse.Padding=new Thickness(8,3,8,3);Collapse.Margin=new Thickness(10,0,0,0);
   Collapse.ToolTip="返回详情；转换会在后台继续";DockPanel.SetDock(Collapse,Dock.Right);heading.Children.Add(Collapse);
   var title=Label("转换格式",17,false);title.FontWeight=FontWeights.SemiBold;title.VerticalAlignment=VerticalAlignment.Center;heading.Children.Add(title);Children.Add(heading);
   SingleFileCard=new StackPanel{Margin=new Thickness(0,0,0,20)};
   var card=new Grid();card.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(88)});card.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
   sourceImage=new Image{Width=88,Height=88,Stretch=Stretch.Uniform};
   var imageFrame=new Border{Width=88,Height=88,CornerRadius=new CornerRadius(4),Child=sourceImage};imageFrame.SetResourceReference(Border.BackgroundProperty,"Control");card.Children.Add(imageFrame);
   var info=new StackPanel{Margin=new Thickness(12,4,0,0),VerticalAlignment=VerticalAlignment.Center};
   sourceTitle=Label("",13,false);sourceTitle.FontWeight=FontWeights.SemiBold;sourceTitle.TextWrapping=TextWrapping.NoWrap;sourceTitle.TextTrimming=TextTrimming.CharacterEllipsis;
   sourceDetail=Label("",12,true,new Thickness(0,6,0,0));info.Children.Add(sourceTitle);info.Children.Add(sourceDetail);
   Change=MakeButton("更换…","ConversionChange",async()=>await ReplaceFilesAsync(ChooseFiles==null?null:ChooseFiles()),false);Change.MinHeight=24;Change.Padding=new Thickness(6,2,6,2);Change.Margin=new Thickness(0,6,0,0);Change.HorizontalAlignment=HorizontalAlignment.Left;info.Children.Add(Change);
   Grid.SetColumn(info,1);card.Children.Add(info);SingleFileCard.Children.Add(card);body.Children.Add(SingleFileCard);
   empty=new StackPanel{Margin=new Thickness(0,0,0,18)};
   empty.Children.Add(Label("选择一个文件开始转换",15,false));empty.Children.Add(Label("可从素材库右键转换，也可以拖入图片或 PDF。\n原文件保持不动。",12,true,new Thickness(0,8,0,10)));body.Children.Add(empty);
   Files=new ListBox{Name="ConversionFiles",SelectionMode=SelectionMode.Extended,BorderThickness=new Thickness(0),HorizontalContentAlignment=HorizontalAlignment.Stretch,MaxHeight=220};
   Files.SetResourceReference(Control.BackgroundProperty,"Panel");Files.SetResourceReference(Control.ForegroundProperty,"Text");ScrollViewer.SetHorizontalScrollBarVisibility(Files,ScrollBarVisibility.Disabled);
   AutomationProperties.SetName(Files,"待转换文件，可多选移除");
   Files.SelectionChanged+=delegate{UpdateControls();};
   Files.PreviewKeyDown+=async delegate(object sender,KeyEventArgs e){if(e.Key==Key.Delete&&!running){e.Handled=true;await RemoveSelectedAsync();}};
   Summary=Label("",13,false);BatchFiles=new Expander{Header=Summary,Content=Files,Margin=new Thickness(0,0,0,12)};BatchFiles.SetResourceReference(Control.ForegroundProperty,"Text");body.Children.Add(BatchFiles);
   var sourceTools=new WrapPanel{Margin=new Thickness(0,0,0,12)};
   Add=MakeButton("选择文件…","ConversionAdd",async()=>await AddFilesAsync(ChooseFiles==null?null:ChooseFiles()),false);
   Remove=MakeButton("移除所选","ConversionRemove",async()=>await RemoveSelectedAsync(),false);
   sourceTools.Children.Add(Add);sourceTools.Children.Add(Remove);body.Children.Add(sourceTools);
   body.Children.Add(Divider());
   body.Children.Add(Label("转为",14,false,new Thickness(0,16,0,10)));
   FormatChoices=new WrapPanel{Margin=new Thickness(0,0,0,0)};body.Children.Add(FormatChoices);
   moreFormats=MakeButton("更多…","ConversionMoreFormats",ShowMoreFormats,false);moreFormats.MinWidth=64;moreFormats.MinHeight=36;FormatChoices.Children.Add(moreFormats);moreFormats.ToolTip="其他可转换格式";moreFormats.Visibility=Visibility.Collapsed;
   Target=new ComboBox{Name="ConversionTarget",MinHeight=34,DisplayMemberPath="Label",Visibility=Visibility.Collapsed,Margin=new Thickness(0,8,0,0)};
   Target.SetResourceReference(Control.ForegroundProperty,"Text");Target.SetResourceReference(Control.BackgroundProperty,"Input");Target.SetResourceReference(Control.BorderBrushProperty,"Border");
   AutomationProperties.SetName(Target,"目标格式");Target.SelectionChanged+=delegate{UpdateTargetWarning();UpdateControls();};body.Children.Add(Target);
   var settings=new StackPanel();
   qualityArea=new StackPanel{Margin=new Thickness(0,8,0,4)};qualityArea.Children.Add(Label("图片质量",12,false));
   var qualityRow=new DockPanel();qualityText=Label("90",12,true);qualityText.MinWidth=32;qualityText.VerticalAlignment=VerticalAlignment.Center;DockPanel.SetDock(qualityText,Dock.Right);qualityRow.Children.Add(qualityText);
   Quality=new Slider{Name="ConversionQuality",Minimum=1,Maximum=100,Value=90,TickFrequency=1,IsSnapToTickEnabled=true,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,8,8,8),ToolTip="JPEG / WebP 有损质量，100 也不保证无损"};
   Quality.SetResourceReference(FrameworkElement.StyleProperty,"ThumbnailSlider");AutomationProperties.SetName(Quality,"有损图片质量");Quality.ValueChanged+=delegate{qualityText.Text=((int)Quality.Value).ToString();};qualityRow.Children.Add(Quality);qualityArea.Children.Add(qualityRow);settings.Children.Add(qualityArea);
   settings.Children.Add(Label("不改变图片尺寸。转换会另存文件，不覆盖原件。",12,true,new Thickness(0,6,0,8)));
   advanced=new Expander{Header="更多设置",Content=settings,Margin=new Thickness(0,16,0,16)};advanced.SetResourceReference(Control.ForegroundProperty,"Muted");body.Children.Add(advanced);
   Warning=Label("",12,true,new Thickness(0,0,0,12));body.Children.Add(Warning);
   body.Children.Add(Divider());body.Children.Add(Label("保存到",14,false,new Thickness(0,16,0,10)));
   var destinations=new Grid();destinations.ColumnDefinitions.Add(new ColumnDefinition());destinations.ColumnDefinitions.Add(new ColumnDefinition());
   libraryDestination=Destination("素材库","ConversionLibrary",new Thickness(0,0,5,0));folderDestination=Destination("文件夹","ConversionFolder",new Thickness(5,0,0,0));
   libraryDestinationFrame=DestinationFrame(libraryDestination,new Thickness(0,0,5,0));folderDestinationFrame=DestinationFrame(folderDestination,new Thickness(5,0,0,0));
   Grid.SetColumn(folderDestinationFrame,1);destinations.Children.Add(libraryDestinationFrame);destinations.Children.Add(folderDestinationFrame);body.Children.Add(destinations);
   // Kept as a programmatic compatibility surface. The two destinations above
   // are the only UI: there is no ambiguous extra 'also import' checkbox.
   Collect=new CheckBox{Name="ConversionCollect",IsChecked=collector!=null,Visibility=Visibility.Collapsed};
   Collect.Checked+=delegate{UpdateDestination();};Collect.Unchecked+=delegate{UpdateDestination();};
   libraryDestination.Checked+=delegate{if(!syncDestination)Collect.IsChecked=true;};
   folderDestination.Checked+=delegate{if(!syncDestination)Collect.IsChecked=false;};
   destinationHelp=Label("",12,true,new Thickness(0,9,0,10));body.Children.Add(destinationHelp);
   folderArea=new StackPanel{Margin=new Thickness(0,0,0,10)};
   Output=new TextBox{Name="ConversionOutput",Text=defaultOutput??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"Nook 转换"),MinHeight=32,VerticalContentAlignment=VerticalAlignment.Center,ToolTip="会创建独立结果文件夹，不覆盖原文件"};
   Output.SetResourceReference(Control.BackgroundProperty,"Input");Output.SetResourceReference(Control.ForegroundProperty,"Text");Output.SetResourceReference(Control.BorderBrushProperty,"Border");AutomationProperties.SetName(Output,"转换结果保存目录");Output.TextChanged+=delegate{UpdateControls();};folderArea.Children.Add(Output);
   browse=MakeButton("更改位置…","ConversionBrowse",()=>{if(running||ChooseFolder==null)return;string selected=ChooseFolder();if(!string.IsNullOrWhiteSpace(selected))Output.Text=selected;},false);browse.HorizontalAlignment=HorizontalAlignment.Left;browse.Margin=new Thickness(0,5,0,0);folderArea.Children.Add(browse);body.Children.Add(folderArea);
   generated=Label("",12,true,new Thickness(0,12,0,14));body.Children.Add(generated);
   Start=MakeButton("转换","ConversionStart",async()=>await StartAsync(),true);Start.MinHeight=42;Start.HorizontalAlignment=HorizontalAlignment.Stretch;Start.Margin=new Thickness(0);body.Children.Add(Start);
   progress=new ProgressBar{Height=3,Minimum=0,Maximum=1,Visibility=Visibility.Collapsed,Margin=new Thickness(0,12,0,0)};progress.SetResourceReference(Control.ForegroundProperty,"Accent");body.Children.Add(progress);
   Status=Label("",12,true,new Thickness(0,10,0,0));AutomationProperties.SetName(Status,"转换状态");body.Children.Add(Status);
   Cancel=MakeButton("取消剩余","ConversionCancel",CancelOperation,false);Cancel.HorizontalAlignment=HorizontalAlignment.Left;Cancel.Margin=new Thickness(0,8,0,0);body.Children.Add(Cancel);
   resultsArea=new StackPanel{Margin=new Thickness(0,18,0,0)};resultsArea.Children.Add(Divider());resultsArea.Children.Add(Label("转换结果",14,false,new Thickness(0,14,0,8)));
   resultRows=new StackPanel();resultsArea.Children.Add(resultRows);
   Retry=MakeButton("重试未完成","ConversionRetry",async()=>await RetryFailedAsync(),false);resultsArea.Children.Add(Retry);
   CollectResults=MakeButton("收藏结果到素材库","ConversionCollectResults",async()=>await RetryCollectionAsync(),false);resultsArea.Children.Add(CollectResults);
   Open=MakeButton("打开结果文件夹","ConversionOpen",OpenResults,false);resultsArea.Children.Add(Open);body.Children.Add(resultsArea);
   AllowDrop=true;
   PreviewDragOver+=delegate(object sender,DragEventArgs e){e.Handled=true;e.Effects=!running&&e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;};
   PreviewDrop+=async delegate(object sender,DragEventArgs e){e.Handled=true;if(!running&&e.Data.GetDataPresent(DataFormats.FileDrop))await AddFilesAsync(e.Data.GetData(DataFormats.FileDrop)as string[]);};
   RebuildFiles();Status.Text=available?"选择素材后，选一个格式即可转换。":"转换组件未安装，请安装包含格式转换的完整版本。";
   UpdateDestination();UpdateControls();
  }
  static TextBlock Label(string text,double size,bool muted,Thickness? margin=null){var label=new TextBlock{Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,Margin=margin??new Thickness()};label.SetResourceReference(TextBlock.ForegroundProperty,muted?"Muted":"Text");return label;}
  static Border Divider(){var divider=new Border{Height=1};divider.SetResourceReference(Border.BackgroundProperty,"Border");return divider;}
  static Button MakeButton(string text,string name,Action action,bool primary){var button=new Button{Name=name,Content=text,MinHeight=32,Padding=new Thickness(10,5,10,5),Margin=new Thickness(0,0,6,6),HorizontalContentAlignment=HorizontalAlignment.Center};button.SetResourceReference(Control.BackgroundProperty,primary?"Primary":"Control");button.SetResourceReference(Control.ForegroundProperty,primary?"OnPrimary":"Text");button.SetResourceReference(Control.BorderBrushProperty,"Border");AutomationProperties.SetName(button,text);button.Click+=delegate{action();};return button;}
  static RadioButton Destination(string label,string name,Thickness margin){var control=new RadioButton{Content=label,Name=name,MinHeight=26,Padding=new Thickness(4),VerticalContentAlignment=VerticalAlignment.Center};control.SetResourceReference(Control.ForegroundProperty,"Text");AutomationProperties.SetName(control,"保存到"+label);return control;}
  static Border DestinationFrame(RadioButton control,Thickness margin){var border=new Border{Child=control,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(4),Padding=new Thickness(8,5,8,5),Margin=margin};border.SetResourceReference(Border.BorderBrushProperty,"Border");return border;}
  string NameOf(string path){try{var name=SourceName==null?null:SourceName(path);return string.IsNullOrWhiteSpace(name)?Path.GetFileName(path):name;}catch{return Path.GetFileName(path);}}
  string DetailOf(string path){try{string detail=SourceDetail==null?null:SourceDetail(path);if(!string.IsNullOrEmpty(detail))return detail;long bytes=new FileInfo(path).Length;return Path.GetExtension(path).TrimStart('.').ToUpperInvariant()+" · "+(bytes>=1048576?(bytes/1048576d).ToString("0.#")+" MB":Math.Max(1,bytes/1024)+" KB");}catch{return "本地文件";}}
  internal async Task AddFilesAsync(IEnumerable<string> additions){
   if(disposed||running)return;int skipped=0,duplicates=0;bool changed=false;
   if(additions==null)return;
   foreach(string input in additions){
    if(string.IsNullOrWhiteSpace(input)){skipped++;continue;}string path;
    try{path=FileConversionClient.ValidateLocalPath(input,false);}catch{skipped++;continue;}
    if(Directory.Exists(path)||!File.Exists(path)){skipped++;continue;}
    if(paths.Contains(path,StringComparer.OrdinalIgnoreCase)){duplicates++;continue;}
    if(paths.Count>=100){skipped++;continue;}paths.Add(path);changed=true;
   }
   if(changed)ClearResults();
   RebuildFiles();await RefreshCapabilitiesAsync();
   if(!disposed&&!running&&(skipped>0||duplicates>0))Status.Text+=(skipped>0?" 已忽略 "+skipped+" 项（文件夹、失效路径或超过 100 个上限）。":"")+(duplicates>0?" 重复文件未重复添加。":"");
  }
  internal async Task RemoveSelectedAsync(){
   if(disposed||running)return;var remove=Files.SelectedItems.Cast<ListBoxItem>().Select(i=>i.Tag as string).ToArray();
   if(paths.RemoveAll(p=>remove.Contains(p,StringComparer.OrdinalIgnoreCase))>0)ClearResults();RebuildFiles();await RefreshCapabilitiesAsync();
  }
  internal async Task ReplaceFilesAsync(IEnumerable<string> replacements){
   if(disposed||running||replacements==null)return;
   var next=new List<string>();
   foreach(string input in replacements){try{string path=FileConversionClient.ValidateLocalPath(input,true);if(File.Exists(path)&&!Directory.Exists(path)&&!next.Contains(path,StringComparer.OrdinalIgnoreCase)&&next.Count<100)next.Add(path);}catch{}}
   if(next.Count==0){Status.Text="没有可用的替换文件，原任务和结果仍保留。";return;}
   paths.Clear();ClearResults();await AddFilesAsync(next);
  }
  void ClearResults(){results.Clear();collectedPaths.Clear();generatedPaths.Clear();generatedOutputs.Clear();resultFolder=null;taskTarget=null;resultPage=0;RebuildResults();}
  void RebuildFiles(){
   Files.Items.Clear();foreach(string path in paths){var row=new StackPanel{Margin=new Thickness(0,6,0,6)};row.Children.Add(Label(NameOf(path),13,false));row.Children.Add(Label(DetailOf(path),12,true));Files.Items.Add(new ListBoxItem{Content=row,Tag=path,ToolTip=path,HorizontalContentAlignment=HorizontalAlignment.Stretch});}
   Summary.Text=paths.Count+" 个文件";BatchFiles.Visibility=paths.Count>1?Visibility.Visible:Visibility.Collapsed;
   SingleFileCard.Visibility=SingleFileMode?Visibility.Visible:Visibility.Collapsed;empty.Visibility=paths.Count==0?Visibility.Visible:Visibility.Collapsed;
   Add.Content=paths.Count==0?"选择文件…":"添加其他文件…";Add.Visibility=paths.Count==1?Visibility.Collapsed:Visibility.Visible;Remove.Visibility=paths.Count>1?Visibility.Visible:Visibility.Collapsed;
   int mine=++previewGeneration;if(previewCancellation!=null)previewCancellation.Cancel();sourceImage.Source=null;
   if(SingleFileMode){sourceTitle.Text=NameOf(paths[0]);sourceTitle.ToolTip=paths[0];sourceDetail.Text=DetailOf(paths[0]);LoadSourcePreview(paths[0],mine);}
   UpdateControls();
  }
  async void LoadSourcePreview(string path,int mine){
   var cancellation=new CancellationTokenSource();previewCancellation=cancellation;bool entered=false;
   try{await PreviewSlots.WaitAsync(cancellation.Token);entered=true;if(disposed||mine!=previewGeneration)return;ImageSource image=LoadPreview==null?null:await LoadPreview(path);if(disposed||mine!=previewGeneration)return;if(image==null)image=await Task.Run(()=>DecodePreview(path,cancellation.Token));if(!disposed&&mine==previewGeneration)sourceImage.Source=image;}catch{/* A thumbnail failure must not block conversion. */}
   finally{if(entered)PreviewSlots.Release();if(ReferenceEquals(previewCancellation,cancellation))previewCancellation=null;cancellation.Dispose();}
  }
  internal static ImageSource DecodePreview(string path,CancellationToken cancellation){
   try{
    cancellation.ThrowIfCancellationRequested();if(new FileInfo(path).Length>256L*1024*1024)return null;
    int width,height;
    using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);var frame=decoder.Frames[0];width=frame.PixelWidth;height=frame.PixelHeight;}
    if(width<1||height<1||width>100000||height>100000||(long)width*height>32L*1024*1024)return null;
    cancellation.ThrowIfCancellationRequested();
    using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;if(width>=height)image.DecodePixelWidth=Math.Min(176,width);else image.DecodePixelHeight=Math.Min(176,height);image.StreamSource=stream;image.EndInit();cancellation.ThrowIfCancellationRequested();image.Freeze();return image;}
   }catch{return null;}
  }
  internal async Task RefreshCapabilitiesAsync(){
   if(disposed||running)return;int mine=++generation;if(probe!=null)probe.Cancel();probe=null;
   string selected=SelectedTargetId();Target.ItemsSource=null;FormatChoices.Children.Clear();checking=false;
   if(!available){Status.Text="转换组件未安装，请安装包含格式转换的完整版本。";UpdateControls();return;}
   if(paths.Count==0){Status.Text="选择素材后，选一个格式即可转换。";UpdateControls();return;}
   var current=new CancellationTokenSource();probe=current;checking=true;Status.Text="正在检查可转换的格式…";UpdateControls();
   try{
    var reply=await capabilities(paths.ToArray(),current.Token);if(disposed||mine!=generation||current.IsCancellationRequested)return;
    var targets=(reply==null||reply.Targets==null?new ConversionTarget[0]:reply.Targets).Where(t=>t!=null&&!string.IsNullOrEmpty(t.Id)&&SupportedTargets.Contains(t.Id)).OrderBy(t=>{int priority=Array.IndexOf(new[]{"png","jpg","webp"},t.Id);return priority<0?3:priority;}).ToArray();
    Target.ItemsSource=targets;Target.SelectedItem=targets.FirstOrDefault(t=>t.Id==selected)??targets.FirstOrDefault();
    foreach(var target in targets.Take(3)){var choice=target;var button=MakeButton(target.Label??target.Id.ToUpperInvariant(),"Format"+target.Id,()=>{Target.SelectedItem=choice;},false);button.MinWidth=72;button.MinHeight=36;button.Tag=target.Id;FormatChoices.Children.Add(button);}
    FormatChoices.Children.Add(moreFormats);
    Target.Visibility=Visibility.Collapsed;moreFormats.Visibility=targets.Length>3?Visibility.Visible:Visibility.Collapsed;
    Status.Text=targets.Length==0?"这些文件没有共同支持的目标格式。请移除不支持或损坏的文件后重试。":"";
   }catch(OperationCanceledException){if(!disposed&&mine==generation)Status.Text="已取消格式检查。";}
   catch(Exception error){if(!disposed&&mine==generation)Status.Text="无法检查格式："+error.Message;}
   finally{if(mine==generation){checking=false;probe=null;}current.Dispose();if(!disposed){UpdateTargetWarning();UpdateControls();}}
  }
  static readonly HashSet<string> SupportedTargets=new HashSet<string>(new[]{"png","jpg","jpeg","webp","bmp","tiff","tif","gif","pdf"},StringComparer.OrdinalIgnoreCase);
  string SelectedTargetId(){var selected=Target.SelectedItem as ConversionTarget;return selected==null?null:selected.Id;}
  void ShowMoreFormats(){
   if(disposed||running||checking)return;var menu=new ContextMenu();
   foreach(var target in Target.Items.Cast<ConversionTarget>().Skip(3)){var choice=target;var item=new MenuItem{Header=target.Label??target.Id.ToUpperInvariant(),IsCheckable=true,IsChecked=target==Target.SelectedItem};item.Click+=delegate{Target.SelectedItem=choice;};menu.Items.Add(item);}
   moreFormats.ContextMenu=menu;menu.PlacementTarget=moreFormats;menu.IsOpen=true;
  }
  void UpdateTargetWarning(){
   if(Warning==null||Quality==null)return;var selected=Target.SelectedItem as ConversionTarget;string id=selected==null?"":(selected.Id??"").ToLowerInvariant();
   bool lossy=id=="jpg"||id=="jpeg"||id=="webp";Quality.IsEnabled=lossy&&!running&&!disposed;qualityArea.Visibility=lossy?Visibility.Visible:Visibility.Collapsed;
   // Keep ordinary PNG conversion quiet, but do not hide consequential changes
   // (alpha loss, multiple output pages, rasterized PDF) in advanced options.
   bool multi=paths.Any(p=>new[]{".pdf",".gif",".tiff",".tif",".apng"}.Contains(Path.GetExtension(p).ToLowerInvariant()));
   string warning=id=="jpg"||id=="jpeg"?"JPG 不保留透明度，透明区域将变成背景色。":id=="pdf"?"PDF 以图片保存，不保留可编辑文字和图层。":paths.Any(p=>new[]{".psd",".psb"}.Contains(Path.GetExtension(p).ToLowerInvariant()))?"只转换合成画面，不保留可编辑图层。":"";
   if(multi)warning+=(warning.Length>0?"\n":"")+"多页或动图可能生成多个文件，转换后请检查页数和动画效果。";
   if(selected!=null&&!string.IsNullOrWhiteSpace(selected.Warning))warning=selected.Warning;
   Warning.Text=warning;Warning.Visibility=warning.Length>0?Visibility.Visible:Visibility.Collapsed;
   if(generated!=null){
    string hint="";
    if(paths.Count==1&&selected!=null){
     string stem=null;try{if(SourceStem!=null)stem=SourceStem(paths[0]);}catch{}
     if(string.IsNullOrWhiteSpace(stem))stem=Path.GetFileNameWithoutExtension(NameOf(paths[0]));
     hint="生成："+FileConversionClient.OutputNameFromStem(stem,id,0,1)+(multi&&id!="tiff"&&id!="gif"?"（可能多个文件）":"");
    }else if(paths.Count>1)hint="各自另存新文件，保留原文件名";
    generated.Text=hint;
   }
   foreach(Button chip in FormatChoices.Children){bool active=string.Equals(chip.Tag as string,id,StringComparison.OrdinalIgnoreCase);chip.SetResourceReference(Control.BackgroundProperty,active?"Primary":"Control");chip.SetResourceReference(Control.ForegroundProperty,active?"OnPrimary":"Text");chip.IsEnabled=!disposed&&!running&&!checking;}
   bool extra=Target.SelectedIndex>=3;moreFormats.Content=extra?(selected.Label??selected.Id.ToUpperInvariant())+"…":"更多…";moreFormats.SetResourceReference(Control.BackgroundProperty,extra?"Primary":"Control");moreFormats.SetResourceReference(Control.ForegroundProperty,extra?"OnPrimary":"Text");moreFormats.IsEnabled=!disposed&&!running&&!checking;
  }
  void UpdateDestination(){
   if(Collect==null||folderArea==null)return;syncDestination=true;
   try{bool intoLibrary=Collect.IsChecked==true&&HasCollector;libraryDestination.IsChecked=intoLibrary;folderDestination.IsChecked=!intoLibrary;folderArea.Visibility=intoLibrary?Visibility.Collapsed:Visibility.Visible;destinationHelp.Text=intoLibrary?"另存新素材，保留原文件和分类":"另存到独立结果文件夹，不覆盖原文件";
    libraryDestinationFrame.SetResourceReference(Border.BackgroundProperty,intoLibrary?"AccentSoft":"Surface");libraryDestinationFrame.SetResourceReference(Border.BorderBrushProperty,intoLibrary?"Primary":"Border");
    folderDestinationFrame.SetResourceReference(Border.BackgroundProperty,!intoLibrary?"AccentSoft":"Surface");folderDestinationFrame.SetResourceReference(Border.BorderBrushProperty,!intoLibrary?"Primary":"Border");
   }
   finally{syncDestination=false;}UpdateControls();
  }
  internal Task StartAsync(){return RunConversionAsync(false);}
  internal Task RetryFailedAsync(){return RunConversionAsync(true);}
  async Task RunConversionAsync(bool retry){
   if(disposed||running||checking||!available)return;
   var selected=Target.SelectedItem as ConversionTarget;
   if(retry&&taskTarget==null)return;
   if(!retry&&(paths.Count==0||selected==null)){Status.Text="先选择文件和可用的目标格式。";return;}
   string destination;
   try{destination=FileConversionClient.ValidateLocalPath(retry?taskDestination:Output.Text.Trim(),false);if(File.Exists(destination))throw new IOException("请选择完整的文件夹路径。");}
   catch(Exception error){Status.Text="结果目录无效："+error.Message;return;}
   string[] inputs=retry?FailedInputs:paths.ToArray();if(inputs.Length==0)return;
   if(!retry){ClearResults();taskTarget=selected.Id;taskDestination=destination;taskQuality=(int)Quality.Value;taskCollect=Collect.IsChecked==true&&HasCollector;}
   int mine=++generation;var current=new CancellationTokenSource();operation=current;running=true;collecting=false;
   Status.Text="正在转换 0 / "+inputs.Length+"…";progress.Maximum=inputs.Length;progress.Value=0;progress.Visibility=Visibility.Visible;UpdateControls();
   try{
    var report=new Progress<ConversionProgress>(p=>{if(disposed||mine!=generation||!running||collecting||current.IsCancellationRequested||p==null)return;progress.Value=Math.Max(0,Math.Min(inputs.Length,p.Completed));Status.Text="正在转换 "+p.Completed+" / "+p.Total+" · "+NameOf(p.FileName??"");});
    var batch=await convert(inputs,taskTarget,taskDestination,taskQuality,report,current.Token);
    if(disposed||mine!=generation)return;
    foreach(var file in batch==null||batch.Files==null?new List<ConvertedFile>():batch.Files){if(file==null||string.IsNullOrEmpty(file.Source)||!inputs.Contains(file.Source,StringComparer.OrdinalIgnoreCase))continue;results[file.Source]=file;}
    // File existence checks stay off the dispatcher even for a very long PDF.
    // Visual refreshes reuse this verified snapshot and create only one page.
    var candidates=results.Values.Where(f=>f!=null&&f.Outputs!=null).SelectMany(f=>f.Outputs).Where(p=>!string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var existing=await Task.Run(()=>candidates.Where(File.Exists).ToList());
    if(disposed||mine!=generation)return;generatedOutputs=existing;generatedPaths.Clear();foreach(string path in existing)generatedPaths.Add(path);
    bool cancelled=current.IsCancellationRequested||(batch!=null&&batch.Cancelled);
    int outputs=ValidOutputs().Count;int failed=FailedInputs.Length;
    Status.Text=(cancelled?"已取消剩余":"转换结束")+" · 已生成 "+outputs+" 个文件"+(failed>0?" · "+failed+" 项未完成":"")+"。原文件未改动。";
    // The current file's publication is atomic. Cancelling the remainder must
    // not orphan successfully generated outputs that were destined for library.
    if(taskCollect&&outputs>0){string prefix=Status.Text;await CollectPendingAsync(mine);if(!disposed&&mine==generation)Status.Text=prefix+"\n"+Status.Text;}
    if(outputs==0&&failed==0)Status.Text="未生成可用结果，请检查输入文件或换一个格式。原文件未改动。";
   }catch(OperationCanceledException){if(!disposed&&mine==generation)Status.Text="已取消剩余，已完成的结果保留，原文件未改动。";}
   catch(Exception error){if(!disposed&&mine==generation)Status.Text="转换未完成："+error.Message+" 原文件未改动。";}
   finally{current.Dispose();if(mine==generation){operation=null;running=false;collecting=false;}if(!disposed){progress.Visibility=Visibility.Collapsed;RebuildResults();UpdateControls();var ended=OperationEnded;if(ended!=null)ended();}}
  }
  bool HasOutputs(ConvertedFile file){return file!=null&&string.IsNullOrWhiteSpace(file.Error)&&file.Outputs!=null&&file.Outputs.Length>0&&file.Outputs.All(p=>!string.IsNullOrWhiteSpace(p)&&generatedPaths.Contains(p));}
  List<string> ValidOutputs(){return generatedOutputs;}
  async Task CollectPendingAsync(int mine){
   if(!HasCollector)return;
   // Receipts, not filesystem existence, decide what still needs collecting.
   // The collector verifies files off the dispatcher and reports missing ones;
   // silently filtering them here would turn a failed collection into success.
   var pending=results.Values.Where(f=>f!=null&&f.Outputs!=null).Select(f=>new ConvertedFile{Source=f.Source,Outputs=f.Outputs.Where(p=>!string.IsNullOrWhiteSpace(p)&&!collectedPaths.Contains(p)).ToArray()}).Where(f=>f.Outputs.Length>0).ToList();
   if(pending.Count==0){Status.Text=collectedPaths.Count>0?"结果已收藏到素材库。":"还没有可收藏的结果，请先完成转换。";return;}
   collecting=true;Status.Text="文件已生成，正在收藏到素材库…";UpdateControls();
   try{
    if(CollectDetailed!=null){var receipt=await CollectDetailed(pending);if(disposed||mine!=generation)return;if(receipt!=null){var submitted=new HashSet<string>(pending.SelectMany(f=>f.Outputs),StringComparer.OrdinalIgnoreCase);foreach(string path in receipt.CollectedPaths??new string[0])if(submitted.Contains(path))collectedPaths.Add(path);Status.Text=receipt.Message??"已完成收藏检查。";}else Status.Text="文件已生成，收藏未完成。可重试收藏，不必重新转换。";}
    else {string message=await collect(pending);if(disposed||mine!=generation)return;foreach(string path in pending.SelectMany(f=>f.Outputs))collectedPaths.Add(path);Status.Text=message;}
   }catch(Exception error){if(!disposed&&mine==generation)Status.Text="转换已完成，收藏未完成："+error.Message+"。请检查结果文件后重试收藏，不必重新转换已有文件。";}
   finally{if(!disposed&&mine==generation){collecting=false;UpdateControls();}}
  }
  internal async Task RetryCollectionAsync(){
   if(disposed||running||!HasCollector||ValidOutputs().Count==0)return;
   int mine=++generation;running=true;UpdateControls();
   try{await CollectPendingAsync(mine);}
   finally{if(mine==generation)running=false;if(!disposed){RebuildResults();UpdateControls();var ended=OperationEnded;if(ended!=null)ended();}}
  }
  void RebuildResults(){
   if(resultRows==null)return;resultRows.Children.Clear();var outputs=ValidOutputs();resultFolder=outputs.Count>0?Path.GetDirectoryName(outputs[0]):null;
   // A page includes both generated files and source errors. Even an entirely
   // failed batch must expose every detail, not just the first twelve errors.
   var entries=new List<Tuple<string,string,string>>();
   var remaining=new HashSet<string>(outputs,StringComparer.OrdinalIgnoreCase);
   foreach(string input in paths){
    ConvertedFile file;if(!results.TryGetValue(input,out file))continue;
    if(!string.IsNullOrEmpty(file.Error))entries.Add(Tuple.Create(input,(string)null,file.Error));
    foreach(string path in file.Outputs??new string[0])if(!string.IsNullOrWhiteSpace(path)&&remaining.Remove(path))entries.Add(Tuple.Create(input,path,(string)null));
   }
   const int pageSize=12;int total=entries.Count,pageCount=Math.Max(1,(total+pageSize-1)/pageSize);resultPage=Math.Max(0,Math.Min(resultPage,pageCount-1));
   if(total>pageSize){
    var paging=new WrapPanel();paging.Children.Add(Label("共 "+total+" 项结果 · "+(resultPage+1)+" / "+pageCount+" 页",12,true,new Thickness(0,6,8,6)));
    var previous=MakeButton("上一页","ConversionPreviousResults",()=>{resultPage--;RebuildResults();},false);previous.IsEnabled=resultPage>0;paging.Children.Add(previous);
    var next=MakeButton("下一页","ConversionNextResults",()=>{resultPage++;RebuildResults();},false);next.IsEnabled=resultPage+1<pageCount;paging.Children.Add(next);resultRows.Children.Add(paging);
   }
   foreach(var entry in entries.Skip(resultPage*pageSize).Take(pageSize)){
     if(entry.Item3!=null){var failed=Label(NameOf(entry.Item1)+"\n"+entry.Item3,12,false,new Thickness(0,3,0,10));failed.SetResourceReference(TextBlock.ForegroundProperty,"ErrorText");resultRows.Children.Add(failed);continue;}
     string path=entry.Item2;
     var row=new StackPanel{Margin=new Thickness(0,4,0,12)};
     row.Children.Add(Label(Path.GetFileName(path),13,false));row.Children.Add(Label(collectedPaths.Contains(path)?"已收藏到素材库":"文件已生成",12,true,new Thickness(0,3,0,6)));
     var actions=new WrapPanel();
     if(PreviewResult!=null)actions.Children.Add(ResultAction("预览",path,PreviewResult));
     if(CopyResult!=null)actions.Children.Add(ResultAction("复制文件",path,CopyResult));
     if(RevealResult!=null)actions.Children.Add(ResultAction("文件位置",path,RevealResult));
     if(LocateResult!=null&&collectedPaths.Contains(path))actions.Children.Add(ResultAction("在素材库定位",path,LocateResult,false));
     row.Children.Add(actions);Point origin=new Point();
     row.PreviewMouseLeftButtonDown+=delegate(object sender,MouseButtonEventArgs e){origin=e.GetPosition(row);};
     row.MouseMove+=delegate(object sender,MouseEventArgs e){if(running||e.LeftButton!=MouseButtonState.Pressed)return;Point here=e.GetPosition(row);if(Math.Abs(here.X-origin.X)<SystemParameters.MinimumHorizontalDragDistance&&Math.Abs(here.Y-origin.Y)<SystemParameters.MinimumVerticalDragDistance)return;try{if(File.Exists(path))DragDrop.DoDragDrop(row,new DataObject(DataFormats.FileDrop,new[]{path}),DragDropEffects.Copy);}catch(Exception error){Status.Text="无法拖出文件："+error.Message;}};
     row.ToolTip="可拖出已生成文件";resultRows.Children.Add(row);
   }
   UpdateControls();
  }
  Button ResultAction(string caption,string path,Action<string> action,bool requireFile=true){return MakeButton(caption,"",()=>{try{if(requireFile&&!File.Exists(path))throw new FileNotFoundException("结果文件已移动或删除。");action(path);}catch(Exception error){Status.Text=error.Message;}},false);}
  internal void CancelOperation(){if(disposed||!running||collecting||operation==null)return;operation.Cancel();Status.Text="正在取消剩余；已完成的结果会保留…";UpdateControls();}
  void OpenResults(){if(disposed||running||string.IsNullOrEmpty(resultFolder))return;try{if(!Directory.Exists(resultFolder))throw new DirectoryNotFoundException("结果目录已移动，请检查保存位置。");if(OpenFolder!=null)OpenFolder(resultFolder);}catch(Exception error){Status.Text="无法打开结果文件夹："+error.Message;}}
  void UpdateControls(){
   if(Start==null)return;
   Add.IsEnabled=Change.IsEnabled=!disposed&&!running;Remove.IsEnabled=!disposed&&!running&&Files.SelectedItems.Count>0;Files.IsEnabled=!disposed&&!running;
   Target.IsEnabled=!disposed&&!running&&!checking&&Target.Items.Count>0;Output.IsEnabled=browse.IsEnabled=!disposed&&!running;Collect.IsEnabled=!disposed&&!running&&HasCollector;
   libraryDestination.IsEnabled=Collect.IsEnabled;folderDestination.IsEnabled=!disposed&&!running;
   Start.IsEnabled=!disposed&&!running&&!checking&&available&&paths.Count>0&&Target.SelectedItem!=null&&!string.IsNullOrWhiteSpace(Output.Text);
   string id=SelectedTargetId();Start.Content=running?(collecting?"正在收藏…":"正在转换…"):"转换"+(id==null?"":"为 "+id.ToUpperInvariant());
   Cancel.Visibility=running?Visibility.Visible:Visibility.Collapsed;Cancel.Content=collecting?"正在收藏…":"取消剩余";Cancel.IsEnabled=!disposed&&!collecting&&operation!=null&&!operation.IsCancellationRequested;
   Collapse.IsEnabled=!disposed;
   var outputs=ValidOutputs();bool hasResult=taskTarget!=null||results.Count>0;
   resultsArea.Visibility=hasResult?Visibility.Visible:Visibility.Collapsed;Retry.Visibility=hasResult&&FailedInputs.Length>0?Visibility.Visible:Visibility.Collapsed;Retry.Content="重试未完成"+(taskTarget==null?"":" · "+taskTarget.ToUpperInvariant());Retry.IsEnabled=!disposed&&!running;
   CollectResults.Visibility=outputs.Any(p=>!collectedPaths.Contains(p))&&HasCollector?Visibility.Visible:Visibility.Collapsed;CollectResults.Content=taskCollect?"重试收藏（不重新转换）":"收藏结果到素材库";CollectResults.IsEnabled=!disposed&&!running;
   Open.Visibility=string.IsNullOrEmpty(resultFolder)?Visibility.Collapsed:Visibility.Visible;Open.IsEnabled=!disposed&&!running&&!string.IsNullOrEmpty(resultFolder);
   UpdateTargetWarning();var changed=StateChanged;if(changed!=null)changed();
  }
  public void Dispose(){if(disposed)return;disposed=true;generation++;previewGeneration++;if(probe!=null)probe.Cancel();if(operation!=null)operation.Cancel();if(previewCancellation!=null)previewCancellation.Cancel();sourceImage.Source=null;UpdateControls();}
 }
}
