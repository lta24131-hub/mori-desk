using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Reflection;
using Mori;
namespace MoriDesk {
 public sealed class ReferencePage : Grid {
  readonly ReferenceStore store;readonly Window owner;readonly Action<string> status;readonly Action<string,string> open;
  readonly ListBox folders;readonly ListView files;readonly TextBox search;readonly TextBlock info,empty;readonly Grid content;
  const string DragFormat="MoriDesk.ReferenceIds";
  Point dragStart;FileReference pressed;
  readonly DispatcherTimer searchDelay=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(160)};
  List<FileReference> cached=new List<FileReference>();List<FileReference> pendingChecks;bool checking;
  string folder="*";bool refreshing;volatile int version;
  public ReferencePage(Window owner,ReferenceStore store,Action back,Action<string> status,Action<string,string> open){
   this.owner=owner;this.store=store;this.status=status;this.open=open;SetResourceReference(BackgroundProperty,"Canvas");
   using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("MoriDesk.ReferencePage.xaml"))content=(Grid)XamlReader.Load(stream);Children.Add(content);
   folders=(ListBox)content.FindName("Folders");files=(ListView)content.FindName("Files");search=(TextBox)content.FindName("Search");info=(TextBlock)content.FindName("Info");empty=(TextBlock)content.FindName("Empty");
   Wire("AddFiles",ChooseFiles);Wire("AddDirectory",ChooseDirectory);Wire("NewFolder",NewFolder);Wire("Refresh",Refresh);Wire("Clear",()=>files.SelectedItems.Clear());Wire("Remove",Remove);Wire("Open",()=>Open(Selected().FirstOrDefault()));Wire("Reveal",()=>Try(()=>FileRouter.Reveal(Selected().Single().Path)));Wire("Move",ShowMoveMenu);Wire("ManageFolder",()=>{BuildFolderMenu();folders.ContextMenu.PlacementTarget=(Button)content.FindName("ManageFolder");folders.ContextMenu.IsOpen=true;});
   search.TextChanged+=(s,e)=>((TextBlock)content.FindName("SearchHint")).Visibility=search.Text.Length==0?Visibility.Visible:Visibility.Collapsed;
   files.MouseDoubleClick+=(s,e)=>{var row=ItemsControl.ContainerFromElement(files,e.OriginalSource as DependencyObject)as ListViewItem;if(row!=null)Open((FileReference)row.DataContext);};
   files.PreviewMouseRightButtonDown+=(s,e)=>{var row=ItemsControl.ContainerFromElement(files,e.OriginalSource as DependencyObject)as ListViewItem;if(row!=null&&!row.IsSelected){files.SelectedItems.Clear();row.IsSelected=true;}};
   files.PreviewMouseLeftButtonDown+=(s,e)=>{var item=ItemsControl.ContainerFromElement(files,e.OriginalSource as DependencyObject)as ListViewItem;pressed=item==null?null:item.DataContext as FileReference;dragStart=e.GetPosition(files);if(item==null&&Mori.MainWindow.IsGalleryBlank(e.OriginalSource as DependencyObject,files))files.SelectedItems.Clear();if(item!=null&&item.IsSelected&&Keyboard.Modifiers==ModifierKeys.None&&e.ClickCount==1)e.Handled=true;};
   files.PreviewMouseLeftButtonUp+=(s,e)=>{if(pressed!=null&&Keyboard.Modifiers==ModifierKeys.None){files.SelectedItems.Clear();files.SelectedItem=pressed;}pressed=null;};
   files.PreviewMouseMove+=(s,e)=>{if(pressed==null||e.LeftButton!=MouseButtonState.Pressed)return;var point=e.GetPosition(files);if(Math.Abs(point.X-dragStart.X)<SystemParameters.MinimumHorizontalDragDistance&&Math.Abs(point.Y-dragStart.Y)<SystemParameters.MinimumVerticalDragDistance)return;if(!files.SelectedItems.Contains(pressed))files.SelectedItem=pressed;var ids=Selected().Select(r=>r.Id).ToArray();pressed=null;DragDrop.DoDragDrop(files,new DataObject(DragFormat,ids),DragDropEffects.Move);};
   files.ContextMenu=new ContextMenu();files.ContextMenuOpening+=(s,e)=>BuildFileMenu();
   folders.SelectionChanged+=(s,e)=>{if(refreshing)return;var f=folders.SelectedItem as ReferenceFolder;if(f!=null){folder=f.Id;files.SelectedItems.Clear();RefreshFiles();}};
   folders.PreviewMouseRightButtonDown+=(s,e)=>{var item=ItemsControl.ContainerFromElement(folders,e.OriginalSource as DependencyObject)as ListBoxItem;if(item!=null)item.IsSelected=true;};
   folders.ContextMenu=new ContextMenu();folders.ContextMenuOpening+=(s,e)=>BuildFolderMenu();searchDelay.Tick+=(s,e)=>{searchDelay.Stop();RefreshFiles();};search.TextChanged+=(s,e)=>{searchDelay.Stop();searchDelay.Start();};
   files.SelectionChanged+=(s,e)=>UpdateActions();AllowDrop=true;PreviewDragOver+=OnDragOver;PreviewDrop+=OnDrop;
   IsVisibleChanged+=(s,e)=>{if(!IsVisible){version++;pendingChecks=null;searchDelay.Stop();}};
  }
  void Wire(string name,Action action){((Button)content.FindName(name)).Click+=(s,e)=>action();}
  void UpdateActions(){int count=files.SelectedItems.Count;((Border)content.FindName("Actions")).Visibility=count==0?Visibility.Collapsed:Visibility.Visible;((TextBlock)content.FindName("SelectedCount")).Text="已选 "+count+" 项";((Button)content.FindName("Open")).IsEnabled=((Button)content.FindName("Reveal")).IsEnabled=count==1;}
  ReferenceFolder DropFolder(DependencyObject source){var item=ItemsControl.ContainerFromElement(folders,source)as ListBoxItem;return item==null?null:item.DataContext as ReferenceFolder;}
  void OnDragOver(object sender,DragEventArgs e){if(e.Data.GetDataPresent(DragFormat)){var target=DropFolder(e.OriginalSource as DependencyObject);e.Effects=target!=null&&target.Id!="*"?DragDropEffects.Move:DragDropEffects.None;}else e.Effects=e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Link:DragDropEffects.None;e.Handled=true;}
  void OnDrop(object sender,DragEventArgs e){e.Handled=true;var target=DropFolder(e.OriginalSource as DependencyObject);if(e.Data.GetDataPresent(DragFormat)){if(target!=null&&target.Id!="*")MoveTo((string[])e.Data.GetData(DragFormat),target.Id);return;}if(e.Data.GetDataPresent(DataFormats.FileDrop))CollectPaths((string[])e.Data.GetData(DataFormats.FileDrop),target==null?(folder=="*"?"":folder):(target.Id=="*"?"":target.Id),target!=null&&target.Id!="*");}
  void MoveTo(IEnumerable<string> ids,string target){Try(()=>{store.Move(ids,target);Refresh();status("已移动分类 · 原文件位置不变 · Ctrl+Z 撤销");});}
  void ShowMoveMenu(){var menu=Theme.Menu(owner);var rows=Selected();foreach(var f in new[]{new ReferenceFolder{Id="",Name="未分类"}}.Concat(store.Folders())){var target=f;Item(menu,target.Name,()=>MoveTo(rows.Select(r=>r.Id),target.Id));}menu.PlacementTarget=(Button)content.FindName("Move");menu.IsOpen=true;}
  static GridViewColumn Column(string title,double width,string binding){return new GridViewColumn{Header=title,Width=width,DisplayMemberBinding=new Binding(binding)};}
  public static Button Button(string title,Action action){var button=new Button{Content=title,Margin=new Thickness(4),Padding=new Thickness(12,7,12,7)};button.SetResourceReference(Control.BackgroundProperty,"Control");button.SetResourceReference(Control.ForegroundProperty,"Text");button.Template=(ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><Border x:Name='Surface' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' CornerRadius='6' Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Surface' Property='Background' Value='{DynamicResource Hover}'/></Trigger><Trigger Property='IsEnabled' Value='False'><Setter TargetName='Surface' Property='Opacity' Value='0.45'/></Trigger></ControlTemplate.Triggers></ControlTemplate>");button.Click+=(s,e)=>action();return button;}
  void Try(Action action){try{action();}catch(Exception ex){MessageBox.Show(owner,ex.Message,"本地文件");}}
  public void Refresh(){string selected=folder;cached=store.All();refreshing=true;var list=new List<ReferenceFolder>{new ReferenceFolder{Id="*",Name="全部收藏",Count=cached.Count},new ReferenceFolder{Id="",Name="未分类",Count=cached.Count(r=>r.Folder=="")}};var groups=cached.GroupBy(r=>r.Folder).ToDictionary(g=>g.Key,g=>g.Count());foreach(var f in store.Folders()){int count;f.Count=groups.TryGetValue(f.Id,out count)?count:0;list.Add(f);}folders.ItemsSource=list;folders.SelectedItem=list.FirstOrDefault(f=>f.Id==selected)??list[0];folder=((ReferenceFolder)folders.SelectedItem).Id;refreshing=false;foreach(var r in cached)r.State="检查中…";version++;RefreshFiles();pendingChecks=cached;CheckAvailability();}
  internal static List<FileReference> Filter(IEnumerable<FileReference> rows,string folder,string query){query=(query??"").Trim();return rows.Where(r=>(folder=="*"||r.Folder==folder)&&(r.Name+" "+r.Path).IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0).ToList();}
  void RefreshFiles(){searchDelay.Stop();var selected=new HashSet<string>(Selected().Select(r=>r.Id));var rows=Filter(cached,folder,search.Text);files.ItemsSource=rows;foreach(var r in rows)if(selected.Contains(r.Id))files.SelectedItems.Add(r);empty.Visibility=rows.Count==0?Visibility.Visible:Visibility.Collapsed;empty.Text=search.Text.Trim().Length>0?"没有匹配的收藏\n换个关键词，或按 Esc 清空搜索":cached.Count==0?"拖入文件或电脑文件夹\n在这里显示大卡片，不复制原件":"这个分类还没有收藏\n拖入文件，或把卡片拖到上方分类";((TextBlock)content.FindName("Section")).Text=((ReferenceFolder)folders.SelectedItem).Name+" · "+rows.Count+" 项";((Button)content.FindName("ManageFolder")).Content=folder=="*"||folder==""?"新建分类…":"管理当前分类…";UpdateActions();}
  async void CheckAvailability(){
   // Only one probe worker per page. Search never restarts disk checks, and repeated
   // refreshes coalesce rather than accumulating workers on a disconnected drive.
   if(checking)return;checking=true;
   try{while(pendingChecks!=null){var rows=pendingChecks;pendingChecks=null;int stamp=version;
    await Task.Run(()=>{foreach(var row in rows){if(stamp!=version)return;bool exists=row.Exists;if(stamp!=version)return;if(Dispatcher.HasShutdownStarted)return;Dispatcher.Invoke(new Action(()=>{if(stamp==version)row.State=exists?"可打开":"失效 / 暂不可访问";}));}});
   }}catch(OperationCanceledException){}catch(InvalidOperationException){if(!Dispatcher.HasShutdownStarted)throw;}finally{checking=false;}
  }
  public void DropFiles(string[] paths){CollectPaths(paths,folder=="*"?"":folder,folder!="*");}
  internal void CollectPaths(string[] paths,string target,bool classifyExisting){Try(()=>{int count=store.Add(paths,target,classifyExisting);folder=target;search.Clear();Refresh();status("新增 "+count+" 个本地收藏"+(classifyExisting?" · 已有条目归入此分类":" · 已有条目不重复添加")+" · 没有复制文件，也没有导入文件夹内容");});}
  void ChooseFiles(){var d=new Microsoft.Win32.OpenFileDialog{Title="收藏本地文件（仅记录路径）",Multiselect=true,Filter="全部文件|*.*"};if(d.ShowDialog(owner)==true)DropFiles(d.FileNames);}
  void ChooseDirectory(){using(var d=new System.Windows.Forms.FolderBrowserDialog{Description="收藏文件夹快捷方式，不扫描文件夹内容"})if(d.ShowDialog()==System.Windows.Forms.DialogResult.OK)DropFiles(new[]{d.SelectedPath});}
  void NewFolder(){string name=Prompt("新建分类","分类名称","");if(name!=null)Try(()=>{folder=store.AddFolder(name);Refresh();});}
  string Prompt(string title,string hint,string value){var w=new Window{Title=title,Owner=owner,Width=410,Height=190,WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.NoResize};Theme.Attach(w,owner);var p=new StackPanel{Margin=new Thickness(20)};p.Children.Add(new TextBlock{Text=hint});var t=new TextBox{Text=value,Margin=new Thickness(0,12,0,12),Padding=new Thickness(8)};p.Children.Add(t);var ok=Button("确定",()=>w.DialogResult=true);ok.IsDefault=true;p.Children.Add(ok);w.Content=p;w.Loaded+=(s,e)=>{t.Focus();t.SelectAll();};w.PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape)w.Close();};return w.ShowDialog()==true?t.Text:null;}
  List<FileReference> Selected(){return files.SelectedItems.Cast<FileReference>().ToList();}
  void Item(ContextMenu menu,string text,Action action,bool enabled=true){var i=new MenuItem{Header=text,IsEnabled=enabled};i.Click+=(s,e)=>Try(action);menu.Items.Add(i);}
  void BuildFileMenu(){var menu=files.ContextMenu;menu.Items.Clear();menu.Resources.MergedDictionaries.Clear();menu.Resources.MergedDictionaries.Add(owner.Resources);var rows=Selected();var first=rows.FirstOrDefault();Item(menu,"打开",()=>Open(first),rows.Count==1);Item(menu,"显示原文件位置",()=>FileRouter.Reveal(first.Path),rows.Count==1);Item(menu,"复制原路径",()=>Clipboard.SetText(string.Join(Environment.NewLine,rows.Select(r=>r.Path))),rows.Count>0);Item(menu,"重新定位…",()=>Relink(first),rows.Count==1);var move=new MenuItem{Header="移动到分类",IsEnabled=rows.Count>0};foreach(var f in new[]{new ReferenceFolder{Id="",Name="未分类"}}.Concat(store.Folders())){var target=f;var item=new MenuItem{Header=f.Name};item.Click+=(s,e)=>Try(()=>{store.Move(rows.Select(r=>r.Id),target.Id);Refresh();});move.Items.Add(item);}menu.Items.Add(move);menu.Items.Add(new Separator());Item(menu,"移除收藏（保留原件）",Remove,rows.Count>0);Item(menu,"撤销上一步  Ctrl+Z",Undo);}
  void BuildFolderMenu(){var menu=folders.ContextMenu;menu.Items.Clear();Item(menu,"新建分类",NewFolder);if(folder=="*"||folder=="")return;var current=store.Folders().FirstOrDefault(f=>f.Id==folder);if(current==null)return;Item(menu,"重命名",()=>{string name=Prompt("重命名分类","分类名称",current.Name);if(name!=null){store.RenameFolder(current.Id,name);Refresh();}});Item(menu,"删除分类（内容转到未分类）",()=>{if(MessageBox.Show(owner,"仅删除分类，里面的收藏转到未分类，原文件不受影响。","删除分类",MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return;store.DeleteFolder(current.Id);folder="";Refresh();});}
  void Open(FileReference row){if(row==null)return;Try(()=>{if(!row.Exists){MessageBox.Show(owner,"原文件已移动、重命名，或磁盘暂时不可用。请右键“重新定位”，不会删除收藏。","暂时无法打开");return;}open(row.Path,row.Name);});}
  void Relink(FileReference row){if(row.Directory){using(var d=new System.Windows.Forms.FolderBrowserDialog())if(d.ShowDialog()==System.Windows.Forms.DialogResult.OK)store.Relink(row.Id,d.SelectedPath);}else{var d=new Microsoft.Win32.OpenFileDialog{Title="重新定位原文件",Filter="全部文件|*.*"};if(d.ShowDialog(owner)==true)store.Relink(row.Id,d.FileName);}Refresh();}
  void Remove(){var rows=Selected();if(rows.Count==0)return;Try(()=>{store.Remove(rows.Select(r=>r.Id));Refresh();status("已移除 "+rows.Count+" 个快捷收藏，原文件未动。Ctrl+Z 可恢复。");});}
  void Undo(){Try(()=>{status(store.Undo()?"已撤销本地收藏的上一步操作":"本地收藏暂无可撤销操作");Refresh();});}
  public void HandleKey(KeyEventArgs e){if(e.Key==Key.F&&Keyboard.Modifiers==ModifierKeys.Control){search.Focus();search.SelectAll();e.Handled=true;return;}if(e.Key==Key.Escape&&Keyboard.FocusedElement==search){if(search.Text.Length>0)search.Clear();else files.Focus();e.Handled=true;return;}if(Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)return;if(e.Key==Key.Z&&Keyboard.Modifiers==ModifierKeys.Control){Undo();e.Handled=true;}else if(e.Key==Key.A&&Keyboard.Modifiers==ModifierKeys.Control){files.SelectAll();e.Handled=true;}else if(e.Key==Key.Delete){Remove();e.Handled=true;}else if(e.Key==Key.Enter){if(Selected().Count==1)Open(Selected()[0]);e.Handled=true;}else if(e.Key==Key.F5){Refresh();e.Handled=true;}else if(e.Key==Key.Escape){files.SelectedItems.Clear();e.Handled=true;}else if(e.Key==Key.O&&Keyboard.Modifiers==ModifierKeys.Control){ChooseFiles();e.Handled=true;}}
 }
}
