using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Mori;

namespace MoriDesk {
 internal sealed class CanvasProjectRow {
  internal string Id,Name,Updated,Error,Summary,PreviewKey;internal int Images,Text,Drawing;internal bool Pinned;
 }
 // Project gallery: cards show a bounded view of each saved board, never a
 // count pretending to be a picture. Only an explicit card is a drop target.
 public sealed class CanvasProjectsPage:Grid {
  readonly FreeCanvasStore store;readonly Func<string,string[],bool> open;readonly Func<IDataObject,string[]> dropPaths,commitPaths;readonly Func<bool> canDrop,prepareCreate,defaultPinned;
  readonly Action<string> status;readonly WrapPanel rows=new WrapPanel{Orientation=Orientation.Horizontal};readonly TextBlock count,empty;readonly MatureCanvasStore matureStore;readonly Action createRequested;readonly bool destinationMode;
  readonly Dictionary<string,Button> cards=new Dictionary<string,Button>(StringComparer.Ordinal);
  readonly Dictionary<string,CanvasProjectRow> renderedRows=new Dictionary<string,CanvasProjectRow>(StringComparer.Ordinal);
  readonly ScrollViewer scroll;readonly DispatcherTimer refreshTimer;bool refreshPending,refreshReading,refreshImmediate;int refreshGeneration;
  ControlTemplate cardTemplate;Button dropTarget;
  internal Func<List<CanvasProjectRow>> ProjectQueryForTests;internal Func<bool> RefreshVisibleForTests;
  internal Task RefreshTaskForTests=Task.FromResult(false);internal int QueryCountForTests;
  internal bool RefreshPendingForTests {get{return refreshPending;}}
  internal ScrollViewer ScrollForTests {get{return scroll;}}
  internal readonly Button NewCanvasButton;internal List<CanvasProjectRow> ProjectsForTests=new List<CanvasProjectRow>();
  public CanvasProjectsPage(FreeCanvasStore store,Func<string,string[],bool> open,Func<IDataObject,string[]> dropPaths,Func<IDataObject,string[]> commitPaths,Func<bool> canDrop,Func<bool> prepareCreate,Func<bool> defaultPinned,Action<string> status,MatureCanvasStore matureStore=null,Action createRequested=null,bool destinationMode=false){
   this.store=store;this.open=open;this.dropPaths=dropPaths;this.commitPaths=commitPaths;this.canDrop=canDrop;this.prepareCreate=prepareCreate;this.defaultPinned=defaultPinned;this.status=status;this.matureStore=matureStore;this.createRequested=createRequested;this.destinationMode=destinationMode;
   Name="CanvasProjectsPage";Focusable=true;AllowDrop=true;SetResourceReference(BackgroundProperty,"Canvas");
   RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
   var heading=new Grid{Margin=new Thickness(24,20,24,18)};heading.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});heading.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
   var title=new StackPanel();title.Children.Add(Label(destinationMode?"加入画布":"项目",24,"Text",FontWeights.SemiBold));count=Label(destinationMode?"选择一个画布加入图片，或新建画布":"按项目保存，随时接着画",13,"Muted");count.Margin=new Thickness(0,6,0,0);title.Children.Add(count);heading.Children.Add(title);
   var actions=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};NewCanvasButton=ReferencePage.Button("新建画布",CreateProject);NewCanvasButton.Name="NewCanvasProject";NewCanvasButton.ToolTip="新建独立画布，不覆盖已保存项目";System.Windows.Automation.AutomationProperties.SetName(NewCanvasButton,"新建画布");actions.Children.Add(NewCanvasButton);var refresh=ReferencePage.Button("刷新",Refresh);refresh.ToolTip="重新读取已保存的画布（F5）";actions.Children.Add(refresh);Grid.SetColumn(actions,1);heading.Children.Add(actions);Children.Add(heading);
   var body=new Grid{Margin=new Thickness(24,0,24,24)};Grid.SetRow(body,1);Children.Add(body);
   scroll=new ScrollViewer{Content=rows,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,CanContentScroll=false};body.Children.Add(scroll);
   empty=Label(destinationMode?"还没有画布\n点击“新建画布”，用所选图片开始一个项目":"还没有画布\n点击“新建画布”，开始一个项目",15,"Muted");empty.TextAlignment=TextAlignment.Center;empty.LineHeight=28;empty.HorizontalAlignment=HorizontalAlignment.Center;empty.VerticalAlignment=VerticalAlignment.Center;body.Children.Add(empty);
   refreshTimer=new DispatcherTimer(DispatcherPriority.Background,Dispatcher){Interval=TimeSpan.FromMilliseconds(300)};
   refreshTimer.Tick+=(s,e)=>{refreshTimer.Stop();RefreshTaskForTests=RefreshPendingAsync();};
   Loaded+=(s,e)=>ScheduleRefresh();Unloaded+=(s,e)=>{refreshTimer.Stop();ClearDropTarget();refreshGeneration++;if(refreshReading)refreshPending=true;};
   IsVisibleChanged+=(s,e)=>{if(IsVisible)ScheduleRefresh();else{refreshTimer.Stop();refreshGeneration++;if(refreshReading)refreshPending=true;}};
   DragOver+=(s,e)=>HandleDragOver(e);Drop+=(s,e)=>HandleDrop(e);DragLeave+=(s,e)=>ClearDropTarget();Refresh();
  }
  static TextBlock Label(string text,double size,string resource,FontWeight? weight=null){var label=new TextBlock{Text=text,FontSize=size,FontWeight=weight??FontWeights.Normal,TextTrimming=TextTrimming.CharacterEllipsis};label.SetResourceReference(TextBlock.ForegroundProperty,resource);return label;}
  internal static string[] ImageFiles(IEnumerable<string> paths){return (paths??new string[0]).Where(p=>!string.IsNullOrWhiteSpace(p)&&Path.IsPathRooted(p)&&FreeCanvasStore.SupportedImage(p)&&File.Exists(p)&&!Directory.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();}
  internal static CanvasProjectRow Describe(FreeCanvasDocument d){int pictures=d.Items.Count(i=>i.Kind=="image"),texts=d.Items.Count(i=>i.Kind=="text"),drawings=d.Items.Count(i=>i.IsDrawing);return new CanvasProjectRow{Id=d.Id,Name=d.Name,Updated=d.Updated,Images=pictures,Text=texts,Drawing=drawings,Pinned=d.Pinned,Summary=d.Items.Count==0?"空白画布":"图片 "+pictures+"   文字 "+texts+"   绘图 "+drawings};}
  internal static List<CanvasProjectRow> QueryProjects(FreeCanvasStore store,MatureCanvasStore matureStore=null){return (matureStore==null?store.List():matureStore.List()).Select(info=>new CanvasProjectRow{Id=info.Id,Name=info.Name,Updated=info.Updated,Error=info.Error,Images=info.Images,Text=info.Text,Drawing=info.Drawing,Pinned=info.Pinned,Summary=string.IsNullOrEmpty(info.Error)?(info.Images+info.Text+info.Drawing==0?"空白画布":"图片 "+info.Images+"   文字 "+info.Text+"   绘图 "+info.Drawing):"记录暂时无法读取"}).ToList();}
  // F5 and initial navigation start as soon as visible, but disk reads and
  // manifest validation never block the dispatcher. Repeated requests merge.
  public void Refresh(){
   if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(new Action(Refresh));return;}
   refreshPending=true;refreshImmediate=true;refreshGeneration++;
   if(cards.Count==0){empty.Text="正在读取项目…";empty.Visibility=Visibility.Visible;}
   ScheduleRefresh();
  }
  public void RequestRefresh(){
   if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(new Action(RequestRefresh));return;}
   refreshPending=true;refreshGeneration++;ScheduleRefresh();
  }
  bool CanRefreshAutomatically {get{return RefreshVisibleForTests!=null?RefreshVisibleForTests():IsLoaded&&IsVisible;}}
  void ScheduleRefresh(){refreshTimer.Stop();if(refreshPending&&!refreshReading&&CanRefreshAutomatically){if(refreshImmediate)RefreshTaskForTests=RefreshPendingAsync();else refreshTimer.Start();}}
  internal Task FlushRefreshForTests(){refreshTimer.Stop();if(refreshReading)return Task.FromResult(false);return RefreshTaskForTests=RefreshPendingAsync();}
  async Task RefreshPendingAsync(){
   if(!refreshPending||refreshReading||!CanRefreshAutomatically)return;
   refreshPending=false;refreshImmediate=false;refreshReading=true;int generation=refreshGeneration;QueryCountForTests++;
   try{var projects=await Task.Run(()=>ReadProjects());if(generation==refreshGeneration&&CanRefreshAutomatically)ApplyProjects(projects);}
   catch(Exception error){if(generation==refreshGeneration&&CanRefreshAutomatically)RefreshError(error);}
   finally{refreshReading=false;ScheduleRefresh();}
  }
  List<CanvasProjectRow> ReadProjects(){
   var projects=ProjectQueryForTests!=null?ProjectQueryForTests():QueryProjects(store,matureStore);
   foreach(var row in projects){try{row.PreviewKey=string.IsNullOrEmpty(row.Error)?CanvasProjectThumbnail.CacheKey(store,matureStore,row):null;}catch{row.PreviewKey=null;}}
   return projects;
  }
  static bool SameCard(CanvasProjectRow left,CanvasProjectRow right){return left.Name==right.Name&&left.Updated==right.Updated&&left.Error==right.Error&&left.Summary==right.Summary&&left.Images==right.Images&&left.Text==right.Text&&left.Drawing==right.Drawing&&left.Pinned==right.Pinned&&left.PreviewKey==right.PreviewKey;}
  void ApplyProjects(List<CanvasProjectRow> projects){
   ClearDropTarget();
   double offset=scroll.VerticalOffset;string focusedId=ProjectAt(Keyboard.FocusedElement as DependencyObject);bool changed=false;
   var wanted=new HashSet<string>(projects.Select(p=>p.Id),StringComparer.Ordinal);
   foreach(string id in cards.Keys.Where(id=>!wanted.Contains(id)).ToArray()){rows.Children.Remove(cards[id]);cards.Remove(id);renderedRows.Remove(id);changed=true;}
   for(int index=0;index<projects.Count;index++){
    var row=projects[index];CanvasProjectRow previous;Button card;
    if(!renderedRows.TryGetValue(row.Id,out previous)||!SameCard(previous,row)){
     if(cards.TryGetValue(row.Id,out card)){rows.Children.Remove(card);cards.Remove(row.Id);}AddCard(row);changed=true;
    }
    card=cards[row.Id];if(index>=rows.Children.Count||!ReferenceEquals(rows.Children[index],card)){rows.Children.Remove(card);rows.Children.Insert(index,card);changed=true;}renderedRows[row.Id]=row;
   }
   ProjectsForTests=projects;count.Text=destinationMode?"选择一个画布加入图片，或新建画布":projects.Count==0?"按项目保存，随时接着画":projects.Count+" 个画布，点击打开，也可以把图片拖到画布上";
   empty.Text=destinationMode?"还没有画布\n点击“新建画布”，用所选图片开始一个项目":"还没有画布\n点击“新建画布”，开始一个项目";empty.Visibility=projects.Count==0?Visibility.Visible:Visibility.Collapsed;NewCanvasButton.IsEnabled=canDrop();
   if(changed){Button focused;if(focusedId!=null&&cards.TryGetValue(focusedId,out focused))focused.Focus();scroll.ScrollToVerticalOffset(offset);}
  }
  void RefreshError(Exception error){status("画布列表未能读取："+error.Message);if(cards.Count==0){empty.Text="画布列表暂时无法读取\n现有记录不会被覆盖，请稍后刷新";empty.Visibility=Visibility.Visible;}}
  void AddCard(CanvasProjectRow row){
   var card=new Button{Tag=row.Id,Width=252,Height=218,HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Stretch,Margin=new Thickness(0,0,16,20),Padding=new Thickness(10),AllowDrop=true,ToolTip=string.IsNullOrEmpty(row.Error)?(destinationMode?"加入此画布":"单击打开此画布；也可把图片拖到这里"):"记录无法读取；现有项目文件保持不变"};
   card.SetResourceReference(Control.BackgroundProperty,"Panel");card.SetResourceReference(Control.ForegroundProperty,"Text");card.SetResourceReference(Control.BorderBrushProperty,"Divider");
   if(cardTemplate==null)cardTemplate=(ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'><Border x:Name='Card' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='6' Padding='{TemplateBinding Padding}'><ContentPresenter HorizontalAlignment='Stretch'/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Card' Property='Background' Value='{DynamicResource Hover}'/></Trigger><Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Card' Property='BorderBrush' Value='{DynamicResource Accent}'/><Setter TargetName='Card' Property='BorderThickness' Value='2'/></Trigger></ControlTemplate.Triggers></ControlTemplate>");
   card.Template=cardTemplate;
   var content=new Grid();content.RowDefinitions.Add(new RowDefinition{Height=new GridLength(146)});content.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});content.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
   var board=new Grid{ClipToBounds=true};board.SetResourceReference(BackgroundProperty,"Canvas");var fallback=new StackPanel{HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};var glyph=new Label{Padding=new Thickness(0),HorizontalContentAlignment=HorizontalAlignment.Center};glyph.SetResourceReference(Control.ForegroundProperty,"Muted");glyph.Content=CanvasIcons.Create("image",glyph,24);fallback.Children.Add(glyph);var note=Label(string.IsNullOrEmpty(row.Error)?(row.Images+row.Text+row.Drawing==0?"空白画布":"正在读取预览") :"无法读取",12,"Muted");note.Margin=new Thickness(0,7,0,0);fallback.Children.Add(note);board.Children.Add(fallback);
   if(string.IsNullOrEmpty(row.Error)){var preview=new CanvasProjectThumbnailImage(store,matureStore,row);preview.PreviewResolved+=available=>{fallback.Visibility=available?Visibility.Collapsed:Visibility.Visible;if(!available&&row.Images+row.Text+row.Drawing>0)note.Text="当前页为空或预览暂不可用";};board.Children.Add(preview);}
   content.Children.Add(board);var name=Label(row.Name,14,"Text",FontWeights.SemiBold);name.Margin=new Thickness(0,9,0,0);name.ToolTip=row.Name;Grid.SetRow(name,1);content.Children.Add(name);
   var time=Label(string.IsNullOrEmpty(row.Error)?MainWindow.FormatTime(row.Updated):"记录无法读取",11,"Muted");time.Margin=new Thickness(0,4,0,0);Grid.SetRow(time,2);content.Children.Add(time);card.Content=content;
   System.Windows.Automation.AutomationProperties.SetName(card,row.Name+"，"+row.Summary);card.Click+=(s,e)=>{if(!string.IsNullOrEmpty(row.Error)){status("此画布记录无法读取，原文件已保留。");return;}open(row.Id,null);};cards.Add(row.Id,card);rows.Children.Add(card);
  }
  internal Button ProjectButtonForTests(string id){Button button;return cards.TryGetValue(id,out button)?button:null;}
  internal string ProjectAt(DependencyObject source){for(var current=source;current!=null;current=MainWindow.SelectionParent(current)){if(current==this)break;var button=current as Button;if(button!=null&&button.Tag is string&&cards.ContainsKey((string)button.Tag)&&ReferenceEquals(cards[(string)button.Tag],button))return (string)button.Tag;}return null;}
  internal bool AcceptsDrop(string id,IDataObject data){return canDrop()&&ProjectsForTests.Any(p=>p.Id==id&&string.IsNullOrEmpty(p.Error))&&dropPaths(data).Length>0;}
  internal bool DropOnProject(string id,IDataObject data){if(!AcceptsDrop(id,data)){status("请将已有图片文件拖到可读取的画布；文件夹不会展开导入。");return false;}var paths=commitPaths(data);if(paths.Length==0){status("拖入的图片已不可读取，未添加到画布；请检查原文件或刷新素材列表。");return false;}return open(id,paths);}
  internal void ClearDropTarget(){SetDropTarget(null);}
  void SetDropTarget(Button target){if(ReferenceEquals(dropTarget,target))return;if(dropTarget!=null)dropTarget.SetResourceReference(Control.BorderBrushProperty,"Divider");dropTarget=target;if(dropTarget!=null)dropTarget.SetResourceReference(Control.BorderBrushProperty,"Accent");}
  internal void HandleDragOver(DragEventArgs e){string id=ProjectAt(e.OriginalSource as DependencyObject);bool accepts=id!=null&&(e.AllowedEffects&DragDropEffects.Copy)!=0&&AcceptsDrop(id,e.Data);SetDropTarget(accepts?cards[id]:null);e.Effects=accepts?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;}
  internal void HandleDrop(DragEventArgs e){string id=ProjectAt(e.OriginalSource as DependencyObject);ClearDropTarget();e.Effects=id!=null&&(e.AllowedEffects&DragDropEffects.Copy)!=0&&DropOnProject(id,e.Data)?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;}
  internal void CreateProject(){if(!canDrop()){status("请等待当前画布保存图片后再新建。");return;}if(createRequested!=null){createRequested();return;}if(!prepareCreate())return;try{string name="画布 "+(ProjectsForTests.Count+1);while(ProjectsForTests.Any(p=>p.Name==name))name+=" 新";var document=store.Create(name);if(defaultPinned()){document.Pinned=true;store.Save(document);}if(!open(document.Id,null)){store.Trash(document.Id);status("新建画布未打开，空项目已移入可恢复的画布回收目录。");}Refresh();}catch(Exception error){status("新建画布未完成："+error.Message);}}
  public void HandleKey(KeyEventArgs e){if(e.Key==Key.F5){Refresh();e.Handled=true;}else if(e.Key==Key.N&&Keyboard.Modifiers==ModifierKeys.Control){CreateProject();e.Handled=true;}}
 }
}
