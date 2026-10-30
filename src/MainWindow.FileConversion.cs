using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MoriDesk;

namespace Mori {
 public partial class MainWindow {
  FileConversionPanel fileConversionPanel;
  FileConversionCollectionSession fileConversionSession;
  Grid fileConversionHost;
  FrameworkElement fileConversionDetails;
  ScrollViewer fileConversionScroll;
  Button fileConversionTask;
  bool fileConversionVisible,fileConversionCollecting;
  bool quickConversionRunning;
  CancellationTokenSource quickConversionCancellation;
  QuickFileConversionWindow quickFileConversionWindow;
  sealed class QuickConversionResultSnapshot {
   internal readonly Library Library;
   internal readonly string[] Ids;
   internal QuickConversionResultSnapshot(Library origin,IEnumerable<string> ids){Library=origin;Ids=ids.Where(id=>!string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();}
  }
  QuickConversionResultSnapshot quickConversionResults;
  internal FileConversionPanel ActiveFileConversion {get{return fileConversionPanel;}}
  internal bool FileConversionVisible {get{return fileConversionVisible;}}
  internal bool QuickConversionRunning {get{return quickConversionRunning;}}
  bool FileConversionWorking {get{return quickConversionRunning||fileConversionCollecting||(fileConversionPanel!=null&&fileConversionPanel.IsBusy);}}
  sealed class QuickConversionSnapshot {
   internal Library Library;
   internal string[] Ids,Paths;
   internal Dictionary<string,string> SourcePaths;
   internal FileConversionCollectionSession Origin;
   internal Dictionary<string,string> CapabilityStamps;
  }
  internal static MenuItem BuildQuickConversionMenu(Action<string> convert,bool enabled){
   var menu=new MenuItem{Header="转为",IsEnabled=enabled};
   ToolTipService.SetShowOnDisabled(menu,true);
   ShowQuickConversionNotice(menu,"打开后检查可转换格式…");
   return menu;
  }
  internal static MenuItem BuildQuickConversionMenu(Action<string> convert,bool enabled,ConversionCapabilities capabilities){
   var menu=BuildQuickConversionMenu(convert,enabled);
   PopulateQuickConversionMenu(menu,capabilities,convert);return menu;
  }
  static MenuItem QuickConversionNotice(string text){
   var item=new MenuItem{Header=new TextBlock{Text=text,MaxWidth=350,TextWrapping=TextWrapping.Wrap},IsEnabled=false};
   ToolTipService.SetShowOnDisabled(item,true);item.ToolTip=text;return item;
  }
  static void ShowQuickConversionNotice(MenuItem menu,string message){
   menu.Items.Clear();menu.Items.Add(QuickConversionNotice(message));menu.ToolTip=message;
  }
  internal static void PopulateQuickConversionMenu(MenuItem menu,ConversionCapabilities capabilities,Action<string> convert){
   menu.Items.Clear();
   var targets=(capabilities==null||capabilities.Targets==null?new ConversionTarget[0]:capabilities.Targets)
    .Where(item=>item!=null&&ConversionFormats.IsTarget(item.Id)).GroupBy(item=>item.Id,StringComparer.Ordinal).Select(group=>group.First()).ToArray();
   var files=capabilities==null||capabilities.Files==null?new ConversionInputInfo[0]:capabilities.Files;
   // The worker owns the capability intersection; this second check prevents a
   // mixed selection from advertising a format available to only one source.
   targets=targets.Where(target=>files.All(file=>file!=null&&
    (string.Equals(file.Format,target.Id,StringComparison.Ordinal)||(file.Targets??new string[0]).Contains(target.Id,StringComparer.Ordinal)))).ToArray();
   if(targets.Length==0){
    menu.Items.Add(QuickConversionNotice("当前文件没有可用的转换格式"));
    foreach(string warning in files.Where(file=>file!=null).SelectMany(file=>file.Warnings??new string[0])
     .Where(value=>!string.IsNullOrWhiteSpace(value)).Distinct().Take(3))menu.Items.Add(QuickConversionNotice(warning));
   }else{
    var common=QuickConversionPriorities(files);
    targets=targets.OrderBy(target=>{int index=Array.IndexOf(common,target.Id);return index<0?common.Length:index;}).ToArray();
    // Keep the frequent single-file action short without removing any worker
    // capability. A source's own disabled format must not displace a useful
    // shortcut; it remains explained in More formats.
    var primary=targets.Where(target=>!files.All(file=>file.Format==target.Id)).Take(5).ToArray();
    if(primary.Length==0)primary=targets.Take(5).ToArray();
    MenuItem more=null;
    for(int index=0;index<targets.Length;index++){
     var target=targets[index];string id=target.Id;
     string label=string.IsNullOrWhiteSpace(target.Label)?ConversionFormats.Label(id):target.Label;
     string warning=QuickConversionWarning(target,files);
     bool same=files.Length>0&&files.All(file=>file.Format==id);
     var header=new StackPanel();
     header.Children.Add(new TextBlock{Text=label});
     if(!string.IsNullOrWhiteSpace(warning))header.Children.Add(new TextBlock{Text=warning,FontSize=11,Opacity=.72,MaxWidth=340,TextWrapping=TextWrapping.Wrap});
     var item=new MenuItem{Header=header,Tag=id,IsEnabled=!same};
     ToolTipService.SetShowOnDisabled(item,true);
     item.ToolTip=same?"已经是 "+label+"，无需重复转换":
      (string.IsNullOrWhiteSpace(target.Warning)?"":target.Warning+"\n")+"生成新副本并加入原分类；保留原文件和原记录";
     if(same)header.Children.Add(new TextBlock{Text="已是此格式",FontSize=11,Opacity=.65});
     item.Click+=delegate{if(menu.IsEnabled&&item.IsEnabled&&convert!=null)convert(id);};
     if(primary.Contains(target))menu.Items.Add(item);
     else{if(more==null)more=new MenuItem{Header="更多格式"};more.Items.Add(item);}
    }
    if(more!=null)menu.Items.Add(more);
   }
   var missing=capabilities==null?new string[0]:capabilities.MissingEngines??new string[0];
   if(missing.Any(value=>!string.IsNullOrWhiteSpace(value))){
    if(menu.Items.Count>0)menu.Items.Add(new Separator());
    foreach(string engine in missing.Where(value=>!string.IsNullOrWhiteSpace(value)).Distinct().Take(3))menu.Items.Add(QuickConversionNotice(engine));
   }
   menu.ToolTip="转换后自动加入原分类，原文件与原记录始终保留";
  }
  static string[] QuickConversionPriorities(ConversionInputInfo[] files){
   var categories=files.Where(file=>file!=null).Select(file=>{
    var format=ConversionFormats.All.FirstOrDefault(info=>info.Id==file.Format);return format==null?"":format.Category;
   }).Distinct(StringComparer.Ordinal).ToArray();
   // Prefer familiar outputs for this kind of source, but only advertise the
   // actual capability intersection checked above. Mixed kinds use neutral
   // defaults instead of inferring a route from the first selected file.
   switch(categories.Length==1?categories[0]:""){
    case "Image":return new[]{"png","jpg","webp","pdf","tiff","bmp"};
    case "Vector":return new[]{"png","pdf","svg","jpg","webp"};
    case "Pdf":return new[]{"png","jpg","docx","txt","webp"};
    case "Video":return new[]{"mp4","webm","mkv","mov","gif","mp3","wav"};
    case "Audio":return new[]{"mp3","wav","m4a","flac","ogg","aac"};
    case "Document":case "Hwp":return new[]{"pdf","docx","txt","odt","rtf"};
    case "Spreadsheet":return new[]{"xlsx","csv","pdf","ods","tsv"};
    case "Presentation":return new[]{"pdf","pptx","png","odp","jpg"};
    case "Text":return new[]{"pdf","docx","html","md","txt"};
    case "Data":return new[]{"json","csv","yaml","xml","toml"};
    case "Ebook":return new[]{"epub","azw3","mobi","pdf","txt"};
    case "Archive":return new[]{"zip","7z","tar","tgz","txz"};
    case "Cad":return new[]{"dxf","pdf","svg","dwg","png"};
    case "Font":return new[]{"ttf","otf","woff2","woff","eot"};
    case "Subtitle":return new[]{"srt","vtt","ass","smi","lrc"};
    case "Model3D":return new[]{"glb","stl","obj","gltf","3mf"};
    default:return new[]{"png","jpg","webp","pdf","mp4","mp3","wav","docx","xlsx","txt"};
   }
  }
  internal static string QuickConversionWarning(ConversionTarget target,ConversionInputInfo[] files){
   string id=target.Id,warning=target.Warning??"";
   bool document=files.Any(file=>file!=null&&new[]{"pdf","doc","docx","ppt","pptx","xls","xlsx","odt","ods","odp"}.Contains(file.Format));
   bool layered=files.Any(file=>file!=null&&new[]{"psd","psb","ai","svg","eps"}.Contains(file.Format));
   bool imageTarget=new[]{"jpg","png","webp","bmp","tiff","gif","avif","heic","ico","jxl"}.Contains(id);
   if(warning.Contains("第一帧"))return "动图仅保留第一帧"+(id=="jpg"?"；透明区域填白":"");
   if(id=="txt")return "纯文本，不保留图片和原排版";
   if(id=="md")return string.IsNullOrWhiteSpace(warning)?"Markdown 文本，原版式可能改变":warning;
   if(imageTarget&&document)return id=="tiff"||id=="gif"?"页面转为图像，不再可选文字":"逐页输出图片；文字变为画面";
   if(imageTarget&&layered)return "合并为平面图片，不保留图层";
   if(id=="pdf"&&files.Any(file=>file!=null&&new[]{"png","jpg","webp","bmp","gif","tiff","psd","psb","svg"}.Contains(file.Format)))return "图片页 PDF，不含可编辑文字";
   if(new[]{"mp3","wav","flac","aac","ogg","m4a","opus","wma","aiff"}.Contains(id)&&files.Any(file=>file!=null&&new[]{"mp4","mkv","mov","avi","webm","wmv","flv","m4v","mpeg","mpg","3gp"}.Contains(file.Format)))return "仅提取音频，不保留视频画面";
   if(id=="jpg")return "透明区域填充白色";
   // Preserve an explicit engine warning when no compact semantic description
   // applies. It is visible before clicking, not hidden behind a tooltip.
   return warning;
  }
  internal static bool QuickConversionSelectionMatches(string[] capturedIds,IEnumerable<Asset> current){
   var items=(current??Enumerable.Empty<Asset>()).ToArray();
   return capturedIds!=null&&capturedIds.Length>0&&capturedIds.All(id=>!string.IsNullOrEmpty(id))&&
    capturedIds.Distinct(StringComparer.Ordinal).Count()==capturedIds.Length&&items.Length==capturedIds.Length&&
    items.All(item=>item!=null&&!item.Deleted)&&
    items.Select(item=>item.Id).Distinct(StringComparer.Ordinal).Count()==items.Length&&
    new HashSet<string>(capturedIds,StringComparer.Ordinal).SetEquals(items.Select(item=>item.Id));
  }
  internal MenuItem CreateQuickConversionMenu(){
   QuickConversionSnapshot snapshot=null;string reason=null;
   var menu=BuildQuickConversionMenu(target=>StartQuickConversion(snapshot,target),quickConversionRunning||(!busy&&!FileConversionWorking));
   if(quickConversionRunning){
    ShowQuickConversionNotice(menu,"正在转换，可继续浏览；原文件保留");
    var cancelItem=new MenuItem{Header="取消本次转换"};cancelItem.Click+=delegate{CancelQuickConversion();};menu.Items.Add(cancelItem);
    menu.ToolTip="当前转换仍在继续；原文件始终保留";return menu;
   }
   try{
    if(busy||FileConversionWorking)reason="请等待当前操作完成";
    else if(!FileConversionClient.IsAvailable)reason="转换组件尚未安装，请使用包含转换组件的完整版本";
    else if(!CanConvertSelection())reason="请选择素材库中的文件";
    else {
     var selected=Selected().ToArray();
     var sourcePaths=selected.ToDictionary(item=>item.Id,item=>library.FullPath(item),StringComparer.Ordinal);
     string[] paths=sourcePaths.Values.ToArray();
     if(paths.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=paths.Length)reason="同一原文件被重复选中，请只选择其中一个收藏";
     else snapshot=new QuickConversionSnapshot{Library=library,Ids=selected.Select(item=>item.Id).ToArray(),Paths=paths,SourcePaths=sourcePaths};
    }
   }catch(Exception error){reason="无法读取原文件："+error.Message;}
   if(snapshot==null){ShowQuickConversionNotice(menu,reason??"请选择文件");return menu;}
   bool checkedFiles=false;int generation=0;CancellationTokenSource checkCancellation=null;
   menu.SubmenuOpened+=async delegate{
    if(checkCancellation!=null||checkedFiles)return;
    var cancellation=new CancellationTokenSource();checkCancellation=cancellation;int request=++generation;
    ShowQuickConversionNotice(menu,"正在检查可转换格式…");
    try{
     var capabilities=await Task.Run(()=>new FileConversionClient().GetCapabilitiesAsync(snapshot.Paths,cancellation.Token));
     if(request!=generation||cancellation.IsCancellationRequested)return;
     snapshot.CapabilityStamps=(capabilities.Targets??new ConversionTarget[0]).Where(item=>item!=null&&ConversionFormats.IsTarget(item.Id))
      .GroupBy(item=>item.Id,StringComparer.Ordinal).ToDictionary(group=>group.Key,group=>QuickConversionCapabilityStamp(capabilities,group.Key),StringComparer.Ordinal);
     PopulateQuickConversionMenu(menu,capabilities,target=>StartQuickConversion(snapshot,target));checkedFiles=true;
     var needed=(capabilities.MissingComponentIds??new string[0]).Where(FileConversionClient.IsComponentId).Distinct().ToArray();
     if(needed.Length>0){
      menu.Items.Add(new Separator());
      var install=new MenuItem{Header="安装所需组件…"};
      install.Click+=async delegate{
       try{
        menu.IsSubmenuOpen=false;
        if(await ConversionComponentsWindow.OpenAsync(Window,needed)){
         checkedFiles=false;Status("组件已变更；重新打开“转为”菜单即可查看新的转换格式。");
        }
       }catch(Exception error){Status("无法管理转换组件："+error.Message);}
      };menu.Items.Add(install);
     }
    }catch(OperationCanceledException){}
    catch(Exception error){
     if(request!=generation||cancellation.IsCancellationRequested)return;
     ShowQuickConversionNotice(menu,"无法检查转换格式："+error.Message);
     var retry=new MenuItem{Header="重试检查"};retry.Click+=delegate{checkedFiles=false;menu.IsSubmenuOpen=false;menu.IsSubmenuOpen=true;};menu.Items.Add(retry);
    }finally{if(ReferenceEquals(checkCancellation,cancellation))checkCancellation=null;cancellation.Dispose();}
   };
   menu.SubmenuClosed+=delegate{
    if(checkCancellation!=null){generation++;var pending=checkCancellation;checkCancellation=null;pending.Cancel();}
   };
   return menu;
  }
  internal void CancelQuickConversion(){
   if(!quickConversionRunning||quickConversionCancellation==null)return;
   quickConversionCancellation.Cancel();
   Status("正在停止后续转换；已生成的副本会继续加入素材库，原文件保留。");
  }
  bool ValidateQuickConversionSnapshot(QuickConversionSnapshot snapshot){
   if(snapshot==null||!ReferenceEquals(library,snapshot.Library)||!QuickConversionSelectionMatches(snapshot.Ids,Selected())||
      !ConversionSelectionAllowed(Selected(),visible,canvasProjectsMode||browserMode||boardMode||referenceMode||bookmarkMode,view)){
    Status("所选文件已变化，请重新打开“转为”菜单选择格式。");return false;
   }
   // A persistent toolbar menu can outlive saved metadata/category edits.
   // Freeze metadata only at the conversion click, never merely on selection.
   snapshot.Origin=CaptureQuickConversionOrigin(library,snapshot.Ids,snapshot.SourcePaths);
   return true;
  }
  internal static FileConversionCollectionSession CaptureQuickConversionOrigin(Library origin,string[] capturedIds,IDictionary<string,string> sourcePaths){
   if(origin==null||capturedIds==null||sourcePaths==null)throw new ArgumentException("转换来源不完整。");
   var ids=new HashSet<string>(capturedIds,StringComparer.Ordinal);
   var records=origin.All().Where(item=>ids.Contains(item.Id)).ToArray();
   if(sourcePaths.Count!=capturedIds.Length||!QuickConversionSelectionMatches(capturedIds,records)||records.Any(item=>
      !sourcePaths.ContainsKey(item.Id)||!string.Equals(origin.FullPath(item),sourcePaths[item.Id],StringComparison.OrdinalIgnoreCase)))
    throw new IOException("原素材已移动、删除或更换文件，未开始转换。请重新选择。");
   return new FileConversionCollectionSession(origin,records,true);
  }
  async void StartQuickConversion(QuickConversionSnapshot snapshot,string target){
   if(busy||FileConversionWorking){Status("请等待当前操作完成后再转换。");return;}
   try{if(!ValidateQuickConversionSnapshot(snapshot))return;}
   catch(Exception error){Status("未开始转换："+error.Message+"；原文件保留");return;}
   ClearQuickConversionResults();quickConversionRunning=true;quickConversionCancellation=new CancellationTokenSource();
   var cancellation=quickConversionCancellation;
   FileStream[] inputLocks=null;
   try{
    UpdateHomeSelection();
    var client=new FileConversionClient();
    client.SourceName=path=>ConversionSourceStem(snapshot.Origin.FindSource(path));
    string folder=Path.Combine(snapshot.Library.Root,".mori","conversions");
    Status("正在检查并转换为 "+ConversionFormats.Label(target)+"… 原文件保留，可继续浏览。");
    // Keep sources stable from fresh capability validation through conversion.
    // Read-only sharing permits previews and worker readers, never replacement.
    inputLocks=await Task.Run(()=>OpenQuickInputReadLocks(snapshot.Paths,cancellation.Token));
    var capabilities=await Task.Run(()=>client.GetCapabilitiesAsync(snapshot.Paths,cancellation.Token));
    var formats=ValidateQuickConversionCapabilities(snapshot.Paths,target,capabilities);
    string shownStamp;
    if(snapshot.CapabilityStamps==null||!snapshot.CapabilityStamps.TryGetValue(target,out shownStamp)||shownStamp!=QuickConversionCapabilityStamp(capabilities,target))
     throw new IOException("原文件的转换方式或内容提示已变化，请重新打开“转为”菜单查看后再转换");
    var progress=new Progress<ConversionProgress>(value=>{
     if(!quickConversionRunning||!ReferenceEquals(quickConversionCancellation,cancellation)||cancellation.IsCancellationRequested)return;
     Status("正在转换 "+Math.Min(value.Completed+1,value.Total)+" / "+value.Total+" 为 "+ConversionFormats.Label(target)+" · 原文件保留");
    });
    var outcome=await QuickConversionJob.RunAsync(snapshot.Paths,target,folder,
     (path,token)=>Task.Run(()=>{token.ThrowIfCancellationRequested();FileConversionClient.ValidateInputs(new[]{path});return formats[path];},token),
     client.ConvertAsync,snapshot.Origin.CollectAsync,progress,cancellation.Token);
    if(outcome.Collected>0){
     PublishQuickConversionResults(snapshot.Library,snapshot.Origin.CollectedAssetIds());
     try{RefreshAfterQuickConversion();}catch{outcome.Message+="；请按 F5 刷新列表";}
    }
    Status(outcome.Message);
   }catch(OperationCanceledException){Status("已取消转换；原文件保留。");}
   catch(Exception error){Status("转换未完成："+error.Message+"；原文件保留，已生成副本保留在图库 .mori\\conversions。");}
   finally{
    try{if(inputLocks!=null)foreach(var input in inputLocks)input.Dispose();}
    finally{quickConversionRunning=false;quickConversionCancellation=null;cancellation.Dispose();UpdateHomeSelection();}
   }
  }
  internal static Dictionary<string,string> ValidateQuickConversionCapabilities(string[] paths,string target,ConversionCapabilities capabilities){
   if(paths==null||paths.Length==0||paths.Any(string.IsNullOrWhiteSpace)||paths.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=paths.Length||
      !ConversionFormats.IsTarget(target)||capabilities==null||capabilities.Files==null||capabilities.Files.Length!=paths.Length)
    throw new IOException("文件或可转换格式已变化，请重新打开“转为”菜单选择格式。");
   var expected=new HashSet<string>(paths,StringComparer.OrdinalIgnoreCase);
   var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
   foreach(var file in capabilities.Files){
    if(file==null||string.IsNullOrWhiteSpace(file.Source)||!expected.Contains(file.Source)||result.ContainsKey(file.Source))
     throw new IOException("无法核对原文件，未开始转换。");
    string format=ConversionFormats.Detect(file.Source);
    if(string.IsNullOrWhiteSpace(format)||!string.Equals(format,file.Format,StringComparison.Ordinal))
     throw new IOException("原文件格式已变化，请重新选择。");
    if(format!=target&&!(file.Targets??new string[0]).Contains(target,StringComparer.Ordinal))
     throw new IOException("当前文件无法转为 "+ConversionFormats.Label(target)+"。"+string.Join("；",capabilities.MissingEngines??new string[0]));
    result.Add(file.Source,format);
   }
   if(result.Values.Any(format=>format!=target)&&!(capabilities.Targets??new ConversionTarget[0]).Any(item=>item!=null&&item.Id==target))
    throw new IOException("目标格式已不可用，请重新打开“转为”菜单选择。");
   return result;
  }
  internal static string QuickConversionCapabilityStamp(ConversionCapabilities capabilities,string target){
   if(capabilities==null)return "";
   var choice=(capabilities.Targets??new ConversionTarget[0]).FirstOrDefault(item=>item!=null&&item.Id==target);
   return (choice==null?"":choice.Id+"\n"+choice.Warning)+"\n"+
    string.Join("\n",(capabilities.Files??new ConversionInputInfo[0]).Where(file=>file!=null)
     .OrderBy(file=>file.Source,StringComparer.OrdinalIgnoreCase)
     .Select(file=>file.Source+"\t"+file.Format+"\t"+string.Join("；",(file.Warnings??new string[0]).OrderBy(value=>value,StringComparer.Ordinal))));
  }
  internal void OpenQuickFileConversion(){
   try{
   if(quickFileConversionWindow==null){
    quickFileConversionWindow=new QuickFileConversionWindow(Window);
    quickFileConversionWindow.Panel.Notify=message=>Status(message);
   }
   quickFileConversionWindow.RefreshOutputFolder();
   // Non-modal: the gallery, previews and other tools remain usable throughout.
   quickFileConversionWindow.Show();
   if(quickFileConversionWindow.WindowState==WindowState.Minimized)quickFileConversionWindow.WindowState=WindowState.Normal;
   quickFileConversionWindow.Activate();
   }catch(Exception error){Status("无法打开格式转换："+error.Message+"；请检查设置中的转换结果保存位置。");}
  }
  internal static FileStream[] OpenQuickInputReadLocks(string[] paths,CancellationToken token){
   var opened=new List<FileStream>();
   try{foreach(string path in paths){token.ThrowIfCancellationRequested();FileConversionClient.ValidateLocalPath(path,true);opened.Add(new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read));}return opened.ToArray();}
   catch{foreach(var input in opened)input.Dispose();throw;}
  }
  internal static bool ConversionSelectionAllowed(IEnumerable<Asset> selected,IEnumerable<Asset> visibleItems,bool hiddenPage,string currentView){
   if(hiddenPage||currentView=="trash")return false;
   var items=(selected??Enumerable.Empty<Asset>()).ToArray();
   var visibleIds=new HashSet<string>((visibleItems??Enumerable.Empty<Asset>()).Select(a=>a.Id),StringComparer.Ordinal);
   return items.Length>0&&items.Length<=100&&items.All(a=>a!=null&&!a.IsBookmark&&!a.IsDirectory&&!a.Deleted&&visibleIds.Contains(a.Id));
  }
  bool CanConvertSelection(){
   return ConversionSelectionAllowed(Selected(),visible,canvasProjectsMode||browserMode||boardMode||referenceMode||bookmarkMode,view);
  }
  void ConvertSelectedFiles(){
   if(busy||!CanConvertSelection())return;
   // The menu may outlive its originating selection. Resolve the current visible
   // records again; never act on gallery selection hidden behind another page.
   var items=Selected();var ids=new HashSet<string>(items.Select(a=>a.Id),StringComparer.Ordinal);
   var current=library.All().Where(a=>ids.Contains(a.Id)).ToList();
   if(current.Count!=ids.Count||!ConversionSelectionAllowed(current,visible,false,view)){Status("所选素材已变化，请刷新后重新选择。");return;}
   string[] paths;
   try{paths=current.Select(library.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();}
   catch(Exception error){Status("无法读取素材路径："+error.Message);return;}
   if(paths.Any(path=>!File.Exists(path))){Status("有原文件已移动或不可读取，请先重新定位原文件。");return;}
   OpenFileConversion(paths);
  }
  internal void OpenFileConversion(string[] inputs=null){
   if(busy){Status("请等待当前操作完成后再转换文件。");return;}
   if(!PrepareLibraryChange())return;
   bool fromLibrary=inputs!=null&&inputs.Length>0;
   if(inputs==null&&CanConvertSelection()){
    try{inputs=Selected().Select(library.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();fromLibrary=inputs.Length>0;}
    catch(Exception error){Status("无法读取所选素材："+error.Message);return;}
   }
   inputs=inputs??new string[0];
   if(fileConversionPanel!=null){
    bool same=inputs.Length==0||inputs.SequenceEqual(fileConversionPanel.Inputs,StringComparer.OrdinalIgnoreCase);
    if(FileConversionWorking||same){
     ShowFileConversion();
     if(FileConversionWorking&&!same)Status("当前转换仍在继续。完成后再开始另一项；原输入不会被新的选择替换。");
     return;
    }
    fileConversionPanel.Dispose();fileConversionScroll.Content=null;fileConversionPanel=null;
   }
   EnsureFileConversionHost();
   // Selected records win if several shortcuts point at the same local file.
   var origin=new FileConversionCollectionSession(library,Selected().Concat(library.All()));
   var client=new FileConversionClient();
   client.SourceName=path=>ConversionSourceStem(origin.FindSource(path));
   var panel=new FileConversionPanel(FileConversionClient.IsAvailable,client.GetCapabilitiesAsync,client.ConvertAsync,CollectConvertedFilesAsync);
   fileConversionSession=origin;fileConversionPanel=panel;fileConversionScroll.Content=panel;
   panel.Collect.IsChecked=fromLibrary&&inputs.All(path=>origin.FindSource(path)!=null);
   panel.CollectDetailed=results=>CollectConvertedFilesDetailedAsync(origin,results);
   panel.SourceName=path=>{var source=origin.FindSource(path);return source==null?null:ConversionSourceStem(source)+source.Extension;};
   panel.SourceStem=client.SourceName;
   panel.SourceDetail=path=>{var source=origin.FindSource(path);return source==null?null:source.Detail;};
   panel.LoadPreview=path=>Task.Run<ImageSource>(delegate{
    var source=origin.FindSource(path);
    if(source==null||string.IsNullOrEmpty(source.Thumbnail)||!File.Exists(source.Thumbnail))return null;
    try{return ThumbnailImage.ReadThumbnail(source.Thumbnail);}catch{return null;}
   });
   panel.ChooseFiles=()=>{var dialog=new Microsoft.Win32.OpenFileDialog{Title="选择要转换的文件 · 原文件保留",Multiselect=true,Filter="图片与 PDF|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff;*.gif;*.pdf;*.psd;*.psb;*.ico;*.heic;*.avif;*.apng;*.jxl|所有文件|*.*"};return dialog.ShowDialog(Window)==true?dialog.FileNames:null;};
   panel.ChooseFolder=()=>FolderPicker("选择转换结果文件夹 · 不覆盖原文件");
   panel.OpenFolder=FileRouter.Reveal;
   panel.PreviewResult=path=>FileRouter.Open(Window,path,Path.GetFileName(path));
   panel.RevealResult=FileRouter.Reveal;
   panel.CopyResult=path=>{var files=new System.Collections.Specialized.StringCollection();files.Add(path);Clipboard.SetFileDropList(files);Status("已复制转换结果，可粘贴到其他软件");};
   panel.LocateResult=path=>LocateConvertedFile(origin,path);
   panel.CloseRequested=CollapseFileConversion;
   panel.StateChanged+=UpdateFileConversionTask;
   ShowFileConversion();AddInitialConversionFiles(panel,inputs);
  }
  static string ConversionSourceStem(FileConversionCollectionSession.SourceInfo source){
   if(source==null)return null;
   string name=source.Name??"",extension=source.Extension??"";
   return extension.Length>0&&name.Length>extension.Length&&name.EndsWith(extension,StringComparison.OrdinalIgnoreCase)?name.Substring(0,name.Length-extension.Length):name;
  }
  async void AddInitialConversionFiles(FileConversionPanel panel,string[] paths){
   try{await panel.AddFilesAsync(paths);}catch(Exception error){if(!panel.IsDisposed)Status("无法添加转换文件："+error.Message);}
  }
  void EnsureFileConversionHost(){
   if(fileConversionHost!=null)return;
   var workspace=Find<Grid>("Workspace");
   var border=workspace.Children.OfType<Border>().First(item=>Grid.GetColumn(item)==4);
   fileConversionDetails=border.Child as FrameworkElement;border.Child=null;
   fileConversionHost=new Grid();fileConversionHost.Children.Add(fileConversionDetails);
   fileConversionScroll=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Disabled,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Visibility=Visibility.Collapsed};
   fileConversionScroll.AllowDrop=true;
   fileConversionScroll.PreviewDragOver+=delegate(object sender,DragEventArgs e){
    if(fileConversionPanel==null)return;e.Handled=true;
    e.Effects=!FileConversionWorking&&e.Data.GetDataPresent(DataFormats.FileDrop)&&(e.AllowedEffects&DragDropEffects.Copy)!=0?DragDropEffects.Copy:DragDropEffects.None;
   };
   fileConversionScroll.PreviewDrop+=async delegate(object sender,DragEventArgs e){
    e.Handled=true;HideDragHint();
    if(fileConversionPanel!=null&&!FileConversionWorking&&e.Data.GetDataPresent(DataFormats.FileDrop)&&(e.AllowedEffects&DragDropEffects.Copy)!=0)await fileConversionPanel.AddFilesAsync(e.Data.GetData(DataFormats.FileDrop)as string[]);
   };
   fileConversionHost.Children.Add(fileConversionScroll);border.Child=fileConversionHost;
   // Legacy inspector remains for isolated regression tests only. The product
   // entry point is the capability-driven context menu; no persistent task button.
  }
  void ShowFileConversion(){
   if(fileConversionPanel==null)return;
   HideCanvasProjects();HideBrowser();HideBoard();HideReferences();
   sidebars.SetTemporaryRightWidth(Math.Max(336,sidebars.Right));
   sidebars.SetVisible(false,true);UpdateDetailsToggle();
   fileConversionVisible=true;fileConversionDetails.Visibility=Visibility.Collapsed;fileConversionScroll.Visibility=Visibility.Visible;
   UpdateHomeInspector();
   UpdateFileConversionTask();
  }
  internal void CollapseFileConversion(){
   if(fileConversionPanel==null)return;
   fileConversionVisible=false;fileConversionScroll.Visibility=Visibility.Collapsed;fileConversionDetails.Visibility=Visibility.Visible;
   sidebars.SetTemporaryRightWidth(null);
   UpdateHomeInspector();
   UpdateFileConversionTask();Status(FileConversionWorking?"已收起；转换仍在继续，可从顶部“转换中”返回。":"已返回素材详情；可从顶部“转换”继续查看结果。");
  }
  void UpdateFileConversionTask(){
   if(fileConversionTask==null)return;
   fileConversionTask.Visibility=fileConversionPanel==null?Visibility.Collapsed:Visibility.Visible;
   fileConversionTask.Content=FileConversionWorking?"转换中":"转换";
   fileConversionTask.SetResourceReference(Button.BackgroundProperty,fileConversionVisible?"AccentSoft":"Panel");
  }
  bool InFileConversion(object source){
   if(!fileConversionVisible||fileConversionPanel==null)return false;
   for(var element=source as DependencyObject;element!=null;element=SelectionParent(element))if(ReferenceEquals(element,fileConversionPanel)||ReferenceEquals(element,fileConversionScroll))return true;
   return false;
  }
  bool HandleFileConversionKey(KeyEventArgs e){
   // The main gallery must not consume Delete, Space, Ctrl+C or Ctrl+V while
   // focus belongs to the converter. Its controls own these keys instead.
   bool inside=InFileConversion(e.OriginalSource)||InFileConversion(Keyboard.FocusedElement)||(fileConversionVisible&&fileConversionPanel!=null&&(fileConversionPanel.IsKeyboardFocusWithin||fileConversionPanel.Target.IsDropDownOpen));
   if(inside&&!e.Handled)HandleFileConversionEscape(e,fileConversionPanel.Target,CollapseFileConversion);
   return inside;
  }
  internal static bool HandleFileConversionEscape(KeyEventArgs e,ComboBox target,Action collapse){
   if(e.Key!=Key.Escape)return false;
   if(target.IsDropDownOpen)target.IsDropDownOpen=false;else collapse();
   e.Handled=true;return true;
  }
  bool PrepareFileConversionLibraryChange(){
   if(!FileConversionWorking)return true;
   Status("转换仍在进行，暂不能切换或移动图库。可以继续浏览，或在“更多”中取消本次转换。");return false;
  }
  void InitializeFileConversionLifecycle(){
   B("QuickConversionResults").Click+=delegate{ViewQuickConversionResults();};
   T("Search").TextChanged+=delegate{galleryNavigationGeneration++;};
   Window.PreviewMouseDown+=delegate{galleryNavigationGeneration++;};
   Window.PreviewMouseWheel+=delegate{galleryNavigationGeneration++;};
   Window.PreviewKeyDown+=delegate{galleryNavigationGeneration++;};
   Window.Closing+=delegate(object sender,System.ComponentModel.CancelEventArgs e){
    // The existing close-to-tray handler already cancels a tray hide. Only a
    // genuine exit needs a guard; cancelling explicitly happens in the panel.
    if(e.Cancel)return;
    if(quickFileConversionWindow!=null&&quickFileConversionWindow.Panel.IsBusy){
     e.Cancel=true;exitRequested=false;QingJie.AppState.Quitting=false;
     Status("文件转换仍在进行，可继续浏览；请从“工具 → 格式转换”取消剩余任务后再退出。");return;
    }
    if(!FileConversionWorking)return;
    e.Cancel=true;exitRequested=false;QingJie.AppState.Quitting=false;
    Status("转换仍在进行。请等待完成，或在“更多”中取消本次转换后退出。");
   };
   Window.Closed+=delegate{
    ClearQuickConversionResults();
    if(fileConversionPanel!=null){fileConversionPanel.Dispose();fileConversionPanel=null;}
    if(quickFileConversionWindow!=null){quickFileConversionWindow.CloseForExit();quickFileConversionWindow=null;}
   };
  }
  internal void ClearQuickConversionResults(){
   quickConversionResults=null;galleryNavigationGeneration++;
   B("QuickConversionResults").Visibility=Visibility.Collapsed;
  }
  internal void RefreshAfterQuickConversion(){
   var gallery=Find<ListBox>("Gallery");var scroll=VisualChild<ScrollViewer>(gallery);
   string topId=null;double fraction=0,offset=scroll==null?0:scroll.VerticalOffset;
   if(scroll!=null){double y=0;foreach(var row in galleryRows){if(y+row.Height>offset){topId=row.Items[0].Asset.Id;fraction=(offset-y)/row.Height;break;}y+=row.Height;}}
   Refresh(false);
   if(scroll==null||refreshDeferred||canvasProjectsMode||browserMode||boardMode||referenceMode)return;
   gallery.UpdateLayout();scroll=VisualChild<ScrollViewer>(gallery);if(scroll==null)return;
   if(topId!=null){double y=0;foreach(var row in galleryRows){if(row.Items.Any(tile=>tile.Asset.Id==topId)){offset=y+fraction*row.Height;break;}y+=row.Height;}}
   scroll.ScrollToVerticalOffset(offset);
  }
  internal void PublishQuickConversionResults(Library origin,IEnumerable<string> ids){
   if(!ReferenceEquals(origin,library))return;
   var snapshot=new QuickConversionResultSnapshot(origin,ids??Enumerable.Empty<string>());
   if(snapshot.Ids.Length==0){ClearQuickConversionResults();return;}
   quickConversionResults=snapshot;galleryNavigationGeneration++;
   var button=B("QuickConversionResults");button.Content="查看结果（"+snapshot.Ids.Length+"）";button.Visibility=Visibility.Visible;
   // Completion is only a receipt: it must not take the user away from a page,
   // search, selection or inspector draft while they continue working.
  }
  internal bool ViewQuickConversionResults(){
   var snapshot=quickConversionResults;if(snapshot==null)return false;
   if(busy||FileConversionWorking){Status("请等待当前操作完成后查看转换结果。");return false;}
   if(!ReferenceEquals(snapshot.Library,library)){ClearQuickConversionResults();Status("这些转换结果属于另一个图库，请返回原图库查看。");return false;}
   try{
    var wanted=new HashSet<string>(snapshot.Ids,StringComparer.Ordinal);
    var ids=library.All().Where(item=>wanted.Contains(item.Id)&&!item.Deleted).Select(item=>item.Id).ToArray();
    if(ids.Length==0){ClearQuickConversionResults();Status("本次转换结果已被移除；原文件未改动。");return false;}
    if(!PrepareLibraryChange())return false;
    HideCanvasProjects();HideBrowser();HideBoard();HideReferences();
    RevealImported(ids,"");Refresh(true);
    B("QuickConversionResults").Content="查看结果（"+selection.Count+"）";
    long navigation=galleryNavigationGeneration;
    Window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(delegate{
     if(!ReferenceEquals(quickConversionResults,snapshot))return;
     try{CompleteQuickConversionResultNavigation(navigation,ids);}
     catch{Status("已选中转换结果；列表滚动定位未完成，可按 F5 刷新。原文件保留。");}
    }));
    Status("已选中 "+selection.Count+" 个转换结果 · 原文件保留");return true;
   }catch(Exception error){Status("暂时无法查看转换结果："+error.Message+"；原文件保留，可稍后重试。");return false;}
  }
  internal bool CompleteQuickConversionResultNavigation(long navigation,string[] ids){
   // A delayed layout must never pull a later navigation or new selection back
   // to this receipt. Hidden-from-All remains a preference, not a flag to clear.
   if(quickConversionResults==null||navigation!=galleryNavigationGeneration||!ReferenceEquals(quickConversionResults.Library,library)||
      canvasProjectsMode||browserMode||boardMode||referenceMode||HasUnsavedMetadata()||ids==null||!selection.SetEquals(ids))return false;
   var wanted=new HashSet<string>(ids,StringComparer.Ordinal);
   var row=galleryRows.FirstOrDefault(item=>item.Items.Any(tile=>wanted.Contains(tile.Asset.Id)));if(row==null)return false;
   var gallery=Find<ListBox>("Gallery");gallery.ScrollIntoView(row);gallery.UpdateLayout();
   var scroll=VisualChild<ScrollViewer>(gallery);
   if(scroll!=null)scroll.ScrollToVerticalOffset(galleryRows.TakeWhile(item=>!ReferenceEquals(item,row)).Sum(item=>item.Height));
   return true;
  }
  void LocateConvertedFile(FileConversionCollectionSession origin,string path){
   string id=origin.CollectedAssetId(path);
   if(string.IsNullOrEmpty(id)){Status("这个结果还没有加入素材库；可先收藏或打开文件位置。");return;}
   if(!PrepareLibraryChange())return;
   var asset=library.All().FirstOrDefault(item=>item.Id==id&&!item.Deleted);
   if(asset==null){Status("素材已被移除，可通过打开位置查看生成文件。");return;}
   HideCanvasProjects();HideBrowser();HideBoard();HideReferences();
   RevealImported(new[]{id},"");if(asset.HiddenFromAll)mediaSection=asset.IsAudio?"audio":asset.IsVideo?"videos":SectionContains(asset,"pictures")?"pictures":"documents";
   Refresh(true);Status("已定位转换结果 · 原文件保留");
  }
  internal async Task<string> CollectConvertedFilesAsync(IList<ConvertedFile> results){
   var origin=fileConversionSession??new FileConversionCollectionSession(library,library.All());
   return (await CollectConvertedFilesDetailedAsync(origin,results)).Message;
  }
  async Task<ConversionCollectionResult> CollectConvertedFilesDetailedAsync(FileConversionCollectionSession origin,IList<ConvertedFile> results){
   if(busy||fileConversionCollecting)throw new InvalidOperationException("素材库正在处理其他操作。生成文件已保留，可稍后重试收藏。");
   fileConversionCollecting=true;UpdateFileConversionTask();
   try{
    var receipt=await origin.CollectAsync(results);
    if(receipt.CollectedPaths.Length>0){
     // Refresh without changing page, filters, selection or draft edits. Imports
     // never clear unrelated undo steps; the journal guards conflicting edits.
     try{Refresh(false);}catch{receipt.Message+="；列表暂未刷新，请按 F5。";}
    }
    Status(receipt.Message);return receipt;
   }finally{fileConversionCollecting=false;UpdateFileConversionTask();}
  }
 }
}

namespace MoriDesk {
 internal sealed class QuickConversionOutcome {
  internal int Total,Converted,Collected,Skipped,Failed,Remaining;
  internal int ConvertedSources,CollectedSources;
  internal bool Cancelled;
  internal string Message;
 }
 // This single-file-first job has no controls, dialogs, global selection or
 // application lifetime. The host supplies an immutable source snapshot and
 // a library-bound collector; tests exercise the same orchestration headlessly.
 internal static class QuickConversionJob {
  internal static async Task<QuickConversionOutcome> RunAsync(string[] inputs,string target,string outputFolder,
   Func<string,CancellationToken,Task<string>> validate,
   Func<string[],string,string,int,IProgress<ConversionProgress>,CancellationToken,Task<ConversionBatchResult>> convert,
   Func<IList<ConvertedFile>,Task<ConversionCollectionResult>> collect,
   IProgress<ConversionProgress> progress,CancellationToken token){
   inputs=inputs==null?null:(string[])inputs.Clone();
   var outcome=new QuickConversionOutcome{Total=inputs==null?0:inputs.Length};
   string error=null,collectionNote=null;
   if(inputs==null||inputs.Length==0||inputs.Length>100||inputs.Any(string.IsNullOrWhiteSpace)||
      inputs.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=inputs.Length||
      !ConversionFormats.IsTarget(target)||validate==null||convert==null||collect==null){
    outcome.Failed=outcome.Total;outcome.Message="未开始转换：文件或目标格式无效；原文件保留。";return outcome;
   }
   var pending=new List<string>();
   try{
    // Validate the complete frozen selection before starting any worker. The
    // host has already checked current per-file capabilities and their warnings.
    foreach(string path in inputs){
     token.ThrowIfCancellationRequested();string format=await validate(path,token);
     if(string.IsNullOrWhiteSpace(format)||format!=ConversionFormats.Detect(path))throw new IOException("原文件格式无法核对，未开始转换");
     if(format==target)outcome.Skipped++;else pending.Add(path);
    }
   }catch(OperationCanceledException){outcome.Cancelled=true;outcome.Remaining=outcome.Total-outcome.Skipped;outcome.Message=Summary(outcome,target,outputFolder,null,null);return outcome;}
   catch(Exception failure){outcome.Failed=outcome.Total-outcome.Skipped;outcome.Message=Summary(outcome,target,outputFolder,"未转换："+failure.Message,null);return outcome;}
   if(pending.Count==0){outcome.Message=Summary(outcome,target,outputFolder,null,null);return outcome;}
   ConversionBatchResult batch;
   try{
    token.ThrowIfCancellationRequested();
    batch=await convert(pending.ToArray(),target,outputFolder,90,progress,token);
    if(batch==null||batch.Files==null)throw new IOException("转换组件未返回完整结果");
    batch=new ConversionBatchResult{Cancelled=batch.Cancelled,Files=batch.Files.Select(file=>file==null?null:new ConvertedFile{
     Source=file.Source,Error=file.Error,Outputs=file.Outputs==null?null:(string[])file.Outputs.Clone()}).ToList()};
   }catch(OperationCanceledException){outcome.Cancelled=true;outcome.Remaining=pending.Count;outcome.Message=Summary(outcome,target,outputFolder,null,null);return outcome;}
   catch(Exception failure){outcome.Failed=pending.Count;outcome.Message=Summary(outcome,target,outputFolder,failure.Message,null);return outcome;}
   outcome.Cancelled=batch.Cancelled||token.IsCancellationRequested;
   var expected=new HashSet<string>(pending,StringComparer.OrdinalIgnoreCase);
   if(batch.Files.Any(file=>file==null||string.IsNullOrWhiteSpace(file.Source)||!expected.Contains(file.Source))){
    outcome.Failed=pending.Count;outcome.Message=Summary(outcome,target,outputFolder,"无法核对转换结果的原文件，未自动收藏",null);return outcome;
   }
   var grouped=batch.Files.ToLookup(file=>file.Source,StringComparer.OrdinalIgnoreCase);
   var repeated=new HashSet<string>(batch.Files.Where(file=>file.Outputs!=null&&file.Outputs.Length<=1000).SelectMany(file=>file.Outputs)
    .Select(CanonicalQuickPath).Where(path=>path!=null).GroupBy(path=>path,StringComparer.OrdinalIgnoreCase)
    .Where(group=>group.Count()>1).Select(group=>group.Key),StringComparer.OrdinalIgnoreCase);
   var originals=new HashSet<string>(inputs.Select(CanonicalQuickPath),StringComparer.OrdinalIgnoreCase);
   var successful=new List<ConvertedFile>();
   foreach(string source in pending){
    var files=grouped[source].ToArray();
    if(files.Length==0){
     if(outcome.Cancelled)outcome.Remaining++;else{outcome.Failed++;if(error==null)error="转换组件未返回部分文件的结果";}
     continue;
    }
    var file=files[0];string problem=null;
    if(files.Length!=1)problem="转换结果重复，未自动收藏";
    else if(!string.IsNullOrWhiteSpace(file.Error))problem=file.Error;
    else if(file.Outputs==null||file.Outputs.Length==0||file.Outputs.Length>1000||file.Outputs.Any(string.IsNullOrWhiteSpace))problem="转换未生成完整的副本清单";
    else {
     foreach(string output in file.Outputs){
      try{
       string canonical=CanonicalQuickPath(output);
       if(canonical==null||repeated.Contains(canonical)||originals.Contains(canonical)||
          !FileConversionClient.IsUnder(output,outputFolder)||!ConversionFormats.MatchesExtension(output,target))
        problem="转换结果路径重复、越界或格式不符，未自动收藏";
      }catch(Exception){problem="转换结果路径无效，未自动收藏";}
      if(problem==null)problem=await Task.Run(delegate{
       try{
        FileConversionClient.ValidateLocalPath(output,true);
        if(!File.Exists(output)||new FileInfo(output).Length==0)return "转换副本不存在或为空，未自动收藏";
        return null;
       }catch(Exception){return "转换副本已移动或无法读取，未自动收藏";}
      });
      if(problem!=null)break;
     }
    }
    if(problem!=null){outcome.Failed++;if(error==null)error=problem;}
    else{successful.Add(file);outcome.Converted+=file.Outputs.Length;outcome.ConvertedSources++;}
   }
   if(successful.Count>0){
    try{
     // Cancellation stops future conversions, not collection of already
     // committed copies. Original source records/files are never altered.
     var receipt=await collect(successful);
     if(receipt==null||receipt.CollectedPaths==null)throw new IOException("素材库未确认收藏结果");
     var published=new HashSet<string>(successful.SelectMany(file=>file.Outputs).Select(CanonicalQuickPath),StringComparer.OrdinalIgnoreCase);
     var acknowledged=receipt.CollectedPaths.Select(CanonicalQuickPath).ToArray();
     if(acknowledged.Any(path=>path==null||!published.Contains(path))||acknowledged.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=acknowledged.Length)
      throw new IOException("素材库返回的副本清单不匹配");
     var saved=new HashSet<string>(acknowledged,StringComparer.OrdinalIgnoreCase);
     outcome.Collected=saved.Count;
     outcome.CollectedSources=successful.Count(file=>file.Outputs.All(path=>saved.Contains(CanonicalQuickPath(path))));
     outcome.Failed+=successful.Count-outcome.CollectedSources;
     if(outcome.Collected<outcome.Converted&&error==null)error="部分副本未收藏："+receipt.Message;
     if(!string.IsNullOrWhiteSpace(receipt.Message)&&receipt.Message.Contains("原分类已变化"))collectionNote=receipt.Message;
    }catch(Exception failure){outcome.Failed+=successful.Count;if(error==null)error="副本未收藏："+failure.Message;}
   }
   outcome.Message=Summary(outcome,target,outputFolder,error,collectionNote);return outcome;
  }
  static string CanonicalQuickPath(string path){
   try{return string.IsNullOrWhiteSpace(path)||!Path.IsPathRooted(path)?null:Path.GetFullPath(path);}
   catch{return null;}
  }
  static string Summary(QuickConversionOutcome outcome,string target,string folder,string error,string note){
   var parts=new List<string>();
   string label=ConversionFormats.Label(target);
   if(outcome.Collected>0)parts.Add("已生成并收藏 "+outcome.Collected+" 个 "+label+" 副本");
   else if(outcome.Cancelled)parts.Add("已取消转换");
   if(outcome.Cancelled&&outcome.Collected>0)parts.Add("已取消后续转换");
   if(outcome.Skipped>0)parts.Add(outcome.Skipped+" 个文件已经是 "+label+"，未重复转换");
   if(outcome.Failed>0)parts.Add(outcome.Failed+" 个原文件未全部完成"+(string.IsNullOrWhiteSpace(error)?"":"："+error));
   if(outcome.Remaining>0)parts.Add(outcome.Remaining+" 个文件尚未转换");
   if(outcome.Converted>outcome.Collected)parts.Add("未收藏的副本保留在 "+folder);
   if(!string.IsNullOrWhiteSpace(note))parts.Add(note);
   parts.Add("原文件保留");return string.Join("；",parts)+"。";
  }
 }
}
