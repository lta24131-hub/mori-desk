using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;
using Mori;
namespace MoriDesk {
 public sealed class BoardPage:Grid {
  public const string DragFormat="MoriDesk.BoardIds";
  readonly BoardStore store;readonly Window owner;readonly Action<string> status;readonly Action<BoardItem> open;
  internal readonly Grid content;internal readonly ListView tiles;readonly TextBox search;
  List<BoardItem> cached=new List<BoardItem>();string section="all",group="";Point start;object pressed;bool dragging,working;
  readonly DispatcherTimer delay=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(180)};
  public Action PasteOther {get;set;}
  public Action SectionChanged {get;set;}
  public string Section {get{return section;}}public string Group {get{return group;}}
  public BoardPage(Window owner,BoardStore store,Action<string> status,Action<BoardItem> open){
   this.owner=owner;this.store=store;this.status=status;this.open=open;SetResourceReference(BackgroundProperty,"Canvas");
   using(var s=Assembly.GetExecutingAssembly().GetManifestResourceStream("MoriDesk.BoardPage.xaml"))content=(Grid)XamlReader.Load(s);Children.Add(content);tiles=Get<ListView>("Tiles");search=Get<TextBox>("Search");
   Wire("NewBox",NewBox);Wire("Add",Choose);Wire("Back",()=>{group="";search.Clear();Refresh();});Wire("Refresh",Refresh);
   Wire("Open",()=>OpenSelection());Wire("Reveal",()=>FileRouter.Reveal(Single().Path));Wire("CopyPath",()=>Clipboard.SetText(Single().Path));Wire("OpenSource",()=>{Uri url;if(SourceLink.TryParse(Single().Source,out url))Process.Start(new ProcessStartInfo(url.AbsoluteUri){UseShellExecute=true});});
   Wire("Remove",Remove);Wire("Move",MoveMenu);Wire("Relink",Relink);Wire("CopyToLibrary",CopyToLibrary);Wire("RenameBox",RenameBox);Wire("EditInfo",EditInfo);
   tiles.SelectionChanged+=(s,e)=>Inspector();tiles.MouseDoubleClick+=(s,e)=>{if(Row(e.OriginalSource as DependencyObject)!=null){OpenSelection();e.Handled=true;}};
   tiles.PreviewMouseLeftButtonDown+=(s,e)=>{pressed=Row(e.OriginalSource as DependencyObject);start=e.GetPosition(tiles);dragging=false;if(pressed==null&&MainWindow.IsGalleryBlank(e.OriginalSource as DependencyObject,tiles))tiles.SelectedItems.Clear();if(pressed is BoardItem&&tiles.SelectedItems.Contains(pressed)&&Keyboard.Modifiers==ModifierKeys.None&&e.ClickCount==1)e.Handled=true;};
   tiles.PreviewMouseLeftButtonUp+=(s,e)=>{if(!dragging&&pressed!=null&&Keyboard.Modifiers==ModifierKeys.None){tiles.SelectedItems.Clear();tiles.SelectedItem=pressed;}pressed=null;};
   tiles.PreviewMouseMove+=(s,e)=>{if(!(pressed is BoardItem)||e.LeftButton!=MouseButtonState.Pressed)return;Point p=e.GetPosition(tiles);if(Math.Abs(p.X-start.X)<SystemParameters.MinimumHorizontalDragDistance&&Math.Abs(p.Y-start.Y)<SystemParameters.MinimumVerticalDragDistance)return;if(!tiles.SelectedItems.Contains(pressed))tiles.SelectedItem=pressed;var ids=Selected().Select(i=>i.Id).ToArray();pressed=null;dragging=true;DragDrop.DoDragDrop(tiles,new DataObject(DragFormat,ids),DragDropEffects.Move);};
   tiles.PreviewMouseRightButtonDown+=(s,e)=>{object row=Row(e.OriginalSource as DependencyObject);if(row==null)tiles.SelectedItems.Clear();else if(!tiles.SelectedItems.Contains(row)){tiles.SelectedItems.Clear();tiles.SelectedItem=row;}};
   tiles.ContextMenu=new ContextMenu();tiles.ContextMenuOpening+=(s,e)=>BuildMenu();AllowDrop=true;
   PreviewDragOver+=(s,e)=>{if(e.Data.GetDataPresent(DragFormat)){e.Effects=GroupAt(e.OriginalSource as DependencyObject)!=null?DragDropEffects.Move:DragDropEffects.None;e.Handled=true;}};
   PreviewDrop+=(s,e)=>{if(!e.Data.GetDataPresent(DragFormat))return;e.Handled=true;string target=GroupAt(e.OriginalSource as DependencyObject);if(target!=null)Try(()=>{store.Move((string[])e.Data.GetData(DragFormat),target);Refresh();status("已整理到分类框 · 原文件位置不变 · Ctrl+Z 撤销");});};
   delay.Tick+=(s,e)=>{delay.Stop();Render();};search.TextChanged+=(s,e)=>{delay.Stop();delay.Start();};IsVisibleChanged+=(s,e)=>{if(!IsVisible)delay.Stop();};
  }
  T Get<T>(string name)where T:class{return content.FindName(name)as T;}
  void Wire(string name,Action action){Get<Button>(name).Click+=(s,e)=>Try(action);}
  void Try(Action action){try{if(!working)action();}catch(Exception ex){MessageBox.Show(owner,ex.Message,"素材桌面",MessageBoxButton.OK,MessageBoxImage.Information);}}
  object Row(DependencyObject source){var row=ItemsControl.ContainerFromElement(tiles,source)as ListViewItem;return row==null?null:row.DataContext;}
  internal string GroupAt(DependencyObject source){var box=Row(source)as BoardBox;return box==null?null:box.Id;}
  List<BoardItem> Selected(){return tiles.SelectedItems.OfType<BoardItem>().ToList();}
  List<BoardBox> SelectedBoxes(){return tiles.SelectedItems.OfType<BoardBox>().ToList();}
  BoardItem Single(){return Selected().Single();}
  public void ShowSection(string value){section=value=="assets"?"all":value;group="";search.Clear();Refresh();}
  public void Refresh(){cached=store.All();Render();}
  void Render(){
   delay.Stop();var ids=new HashSet<string>(Selected().Select(i=>i.Id));string query=search.Text.Trim();var rows=cached.Where(i=>BoardStore.InSection(i,section)).ToList();
   string title=section=="all"?"全部素材":section=="pictures"?"图片 · 含动图":section=="videos"?"视频":section=="audio"?"音频":section=="models"?"3D 模型":section=="documents"?"文档":section=="unfiled"?"未分类素材":"快捷收藏";
   var categories=store.Categories();if(group!=""&&!categories.Any(c=>c.Id==group))group="";var list=new List<object>();
   if(group!=""){title=categories.First(c=>c.Id==group).Name;rows=rows.Where(i=>i.Groups.Contains(group)).ToList();}
   if(query.Length>0)rows=rows.Where(i=>(i.Name+" "+i.Path+" "+i.Source+" "+(i.Asset==null?"":i.Asset.Tags+" "+i.Asset.Notes)).IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0).ToList();
   if(group==""&&query==""){list.AddRange(store.Boxes(rows).Where(b=>b.Count>0||section=="all"));list.AddRange(rows.Where(i=>i.Groups.Length==0));}else list.AddRange(rows);
   tiles.ItemsSource=list;foreach(var row in list.OfType<BoardItem>())if(ids.Contains(row.Id))tiles.SelectedItems.Add(row);
   Get<TextBlock>("Title").Text=title;Get<TextBlock>("Summary").Text=rows.Count+" 项 · "+rows.Count(i=>i.Shortcut)+" 个快捷方式 · "+rows.Count(i=>!i.Shortcut)+" 个图库副本";
   Get<Button>("Back").Visibility=group==""?Visibility.Collapsed:Visibility.Visible;Get<TextBlock>("Empty").Visibility=list.Count==0?Visibility.Visible:Visibility.Collapsed;
   Get<TextBlock>("Empty").Text=query!=""?"没有匹配的素材\n按 Esc 清空搜索":group!=""?"这个分类框还是空的\n把文件拖进来，只收藏位置":"把电脑上的文件拖到这里\n只收藏位置，不复制原文件";Inspector();
  }
  void Inspector(){
   var items=Selected();var box=tiles.SelectedItems.Count==1?tiles.SelectedItem as BoardBox:null;bool has=items.Count>0||box!=null;
   Get<StackPanel>("Inspector").Visibility=has?Visibility.Visible:Visibility.Collapsed;Get<TextBlock>("InspectorEmpty").Visibility=has?Visibility.Collapsed:Visibility.Visible;
   var first=items.Count==1?items[0]:null;Get<BoardImage>("DetailImage").DataContext=first;Get<TextBlock>("DetailGlyph").Text=first==null?"\uE8B7":first.Glyph;
   Get<TextBlock>("DetailName").Text=box!=null?box.Name:first!=null?first.Name:"已选择 "+items.Count+" 项";
   Get<TextBlock>("DetailStorage").Text=box!=null?"分类框 · "+box.Count+" 项":first!=null?first.Kind+" · "+first.Storage:items.Count(i=>i.Shortcut)+" 个快捷方式 / "+items.Count(i=>!i.Shortcut)+" 个图库副本";
   Get<StackPanel>("FileDetails").Visibility=first==null?Visibility.Collapsed:Visibility.Visible;Get<StackPanel>("SingleActions").Visibility=first==null&&box==null?Visibility.Collapsed:Visibility.Visible;
   Get<TextBox>("LocalPath").Text=first==null?"":first.Path;Get<TextBox>("Source").Text=first==null?"":first.Source;Uri url;Get<StackPanel>("SourceArea").Visibility=first!=null&&SourceLink.TryParse(first.Source,out url)?Visibility.Visible:Visibility.Collapsed;
   Get<Button>("CopyToLibrary").Visibility=first!=null&&first.Shortcut&&!first.Directory&&Library.Extensions.Contains(System.IO.Path.GetExtension(first.Path))?Visibility.Visible:Visibility.Collapsed;
   Get<Button>("Relink").Visibility=first!=null&&first.Shortcut?Visibility.Visible:Visibility.Collapsed;Get<Button>("RenameBox").Visibility=box!=null?Visibility.Visible:Visibility.Collapsed;Get<Button>("Move").Visibility=items.Count>0?Visibility.Visible:Visibility.Collapsed;
   Get<Button>("EditInfo").Visibility=first!=null?Visibility.Visible:Visibility.Collapsed;
   int boxes=SelectedBoxes().Count;bool mixed=boxes>0&&items.Count>0;Get<Button>("Remove").IsEnabled=!mixed;Get<Button>("Move").IsEnabled=!mixed;
   if(boxes>1){Get<StackPanel>("Inspector").Visibility=Visibility.Visible;Get<TextBlock>("InspectorEmpty").Visibility=Visibility.Collapsed;Get<TextBlock>("DetailName").Text="已选择 "+boxes+" 个分类框";Get<TextBlock>("DetailStorage").Text="仅删除分类，不删除里面的文件";}
   if(mixed)Get<TextBlock>("DetailName").Text="请分别选择分类框或素材";
   Get<Button>("Remove").Content=boxes>0?"删除分类框…":items.All(i=>i.Shortcut)?"移除快捷收藏":"移除所选素材…";
   Get<TextBlock>("Safety").Text=box!=null?"删除分类框后，内容回到未分类。不会删除文件。":items.All(i=>i.Shortcut)?"只记录原文件的位置。移除收藏不会删除原文件；移动原文件后可重新定位。":"图库副本移入回收站，快捷方式只移除入口。不会删除电脑上的源文件。";
  }
  BoardItem ResolveReference(BoardItem row){if(row==null||!row.Shortcut||MissingLocalFile.Exists(row.Path,row.Directory))return row;var choice=MissingLocalFile.Show(owner,row.Path,row.Directory);if(choice==MissingLocalFileAction.Relocate)return Relink(row)?store.All().FirstOrDefault(r=>r.Id==row.Id):null;if(choice==MissingLocalFileAction.RemoveShortcut){store.Remove(new[]{row.Id});Refresh();status("已仅移除收藏 · 原文件未删除 · Ctrl+Z 可恢复");}return null;}
  void RevealReference(BoardItem row){Try(()=>{row=ResolveReference(row);if(row!=null)FileRouter.Reveal(row.Path);});}
  void OpenSelection(){if(tiles.SelectedItems.Count!=1)return;var box=tiles.SelectedItem as BoardBox;if(box!=null){group=box.Id;search.Clear();Render();return;}if(Selected().Count==1)Try(()=>{var row=ResolveReference(Single());if(row!=null)open(row);});}
  string Prompt(string title,string value){var dialog=new Window{Owner=owner,Title=title,Width=390,Height=180,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner};Theme.Attach(dialog,owner);var body=new StackPanel{Margin=new Thickness(20)};var input=new TextBox{Text=value,Margin=new Thickness(0,0,0,12),Padding=new Thickness(8)};body.Children.Add(input);var ok=ReferencePage.Button("确定",()=>dialog.DialogResult=true);ok.IsDefault=true;body.Children.Add(ok);dialog.Content=body;dialog.Loaded+=(s,e)=>{input.Focus();input.SelectAll();};dialog.PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape)dialog.Close();};return dialog.ShowDialog()==true?input.Text:null;}
  void NewBox(){string name=Prompt("新建分类框","");if(name==null)return;store.CreateBox(name);Refresh();status("分类框已创建 · 把文件拖进框里即可分类");}
  void RenameBox(){var box=tiles.SelectedItem as BoardBox;if(box==null)return;string name=Prompt("重命名分类框",box.Name);if(name!=null){store.RenameBox(box.Id,name);Refresh();}}
  void Remove(){var boxes=SelectedBoxes();var rows=Selected();if(boxes.Count>0){if(rows.Count>0){status("请分别选择分类框或素材，再进行移除。");return;}if(MessageBox.Show(owner,"删除 "+boxes.Count+" 个分类框？\n里面的文件保留，失去分类的内容回到未分类。\nCtrl+Z 可撤销。","删除分类框",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes){store.DeleteBoxes(boxes.Select(b=>b.Id));Refresh();}return;}if(rows.Count==0)return;int copies=rows.Count(i=>!i.Shortcut);if(copies>0&&MessageBox.Show(owner,"将 "+copies+" 个图库副本移入回收站"+(rows.Count>copies?"，并移除 "+(rows.Count-copies)+" 个快捷入口":"")+"？\n原文件不动，Ctrl+Z 可撤销。","移除素材",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;store.Remove(rows.Select(i=>i.Id));Refresh();status("已移除 · 原文件保留 · Ctrl+Z 可恢复");}
  void MenuItem(ContextMenu menu,string name,Action action){var item=new System.Windows.Controls.MenuItem{Header=name};item.Click+=(s,e)=>Try(action);menu.Items.Add(item);}
  void BuildMenu(){var menu=tiles.ContextMenu;menu.Items.Clear();menu.Resources.MergedDictionaries.Clear();menu.Resources.MergedDictionaries.Add(owner.Resources);var boxes=SelectedBoxes();var rows=Selected();if(boxes.Count>0){if(rows.Count>0){menu.Items.Add(new System.Windows.Controls.MenuItem{Header="请分别选择分类框或素材",IsEnabled=false});}else{if(boxes.Count==1){MenuItem(menu,"打开分类框",OpenSelection);MenuItem(menu,"重命名…",RenameBox);}MenuItem(menu,"删除所选分类框（保留内容）…",Remove);}}else if(rows.Count>0){if(rows.Count==1){MenuItem(menu,"打开",OpenSelection);MenuItem(menu,"编辑信息…",EditInfo);MenuItem(menu,"打开本地位置",()=>RevealReference(Single()));if(Single().Shortcut){MenuItem(menu,"重新定位…",Relink);if(!Single().Directory&&Library.Extensions.Contains(System.IO.Path.GetExtension(Single().Path)))MenuItem(menu,"另存到素材库…",CopyToLibrary);}}MenuItem(menu,"复制文件  Ctrl+C",CopyFiles);MenuItem(menu,"移到分类框…",MoveMenu);MenuItem(menu,"移除所选素材…",Remove);}else{MenuItem(menu,"新建分类框…",NewBox);MenuItem(menu,"添加文件快捷方式…",Choose);MenuItem(menu,"添加文件夹快捷方式…",ChooseDirectory);MenuItem(menu,"粘贴  Ctrl+V",Paste);MenuItem(menu,"刷新",Refresh);}menu.Items.Add(new Separator());MenuItem(menu,"撤销上一步  Ctrl+Z",Undo);}
  void EditInfo(){var row=Single();var dialog=new Window{Owner=owner,Title="编辑素材信息",Width=450,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner};Theme.Attach(dialog,owner);var body=new StackPanel{Margin=new Thickness(22)};Func<string,string,int,TextBox> field=(label,value,height)=>{body.Children.Add(new TextBlock{Text=label,Margin=new Thickness(0,0,0,6)});var input=new TextBox{Text=value??"",Height=height,Padding=new Thickness(8),Margin=new Thickness(0,0,0,14),TextWrapping=TextWrapping.Wrap,AcceptsReturn=height>50};body.Children.Add(input);return input;};var name=field(row.Shortcut?"收藏名称（不改原文件名）":"素材名称",row.Name,36);TextBox tags=null,notes=null;if(row.Asset!=null){tags=field("标签（逗号分隔）",row.Asset.Tags,36);notes=field("备注",row.Asset.Notes,110);}body.Children.Add(ReferencePage.Button("保存",()=>{try{store.UpdateInformation(row,name.Text,tags==null?"":tags.Text,notes==null?"":notes.Text);dialog.DialogResult=true;}catch(Exception ex){MessageBox.Show(dialog,ex.Message,"未保存");}}));dialog.Content=body;dialog.PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape)dialog.Close();};if(dialog.ShowDialog()==true)Refresh();}
  void Paste(){if(Clipboard.ContainsFileDropList())OfferFiles(Clipboard.GetFileDropList().Cast<string>().ToArray(),group);else if(PasteOther!=null)PasteOther();}
  void CopyFiles(){var rows=Selected();if(rows.Count==0)return;var paths=new System.Collections.Specialized.StringCollection();paths.AddRange(rows.Select(r=>r.Path).ToArray());Clipboard.SetFileDropList(paths);status("已复制 "+rows.Count+" 个文件入口，可粘贴到其他应用");}
  void MoveMenu(){var rows=Selected().Select(i=>i.Id).ToArray();if(rows.Length==0)return;var menu=Theme.Menu(owner);MenuItem(menu,"未分类",()=>{store.Move(rows,"");Refresh();});foreach(var category in store.Categories()){var destination=category;MenuItem(menu,destination.Name,()=>{store.Move(rows,destination.Id);Refresh();});}menu.PlacementTarget=Get<Button>("Move");menu.IsOpen=true;}
  void Relink(){Relink(Single());}
  bool Relink(BoardItem row){if(row==null)return false;string path=null;if(row.Directory){using(var dialog=new System.Windows.Forms.FolderBrowserDialog{Description="重新指定原文件夹，不导入里面的文件"})if(dialog.ShowDialog()==System.Windows.Forms.DialogResult.OK)path=dialog.SelectedPath;}else{var dialog=new Microsoft.Win32.OpenFileDialog{Title="重新定位原文件"};if(dialog.ShowDialog(owner)==true)path=dialog.FileName;}if(path==null)return false;store.Relink(row,path);Refresh();status("已重新定位 · 原文件未移动 · Ctrl+Z 可撤销");return true;}
  async void CopyToLibrary(){if(working)return;var row=Single();if(MessageBox.Show(owner,"在素材库保存独立副本？\n原文件保持不变；成功后用副本替换这个快捷入口。","另存到素材库",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;working=true;IsEnabled=false;try{await Task.Run(()=>store.CopyToLibrary(row));Refresh();status("已另存到素材库 · 原文件保留");}catch(Exception ex){MessageBox.Show(owner,ex.Message,"保存未完成");}finally{working=false;IsEnabled=true;}}
  void Choose(){var dialog=new Microsoft.Win32.OpenFileDialog{Title="添加快捷方式，不复制文件",Multiselect=true,Filter="全部文件|*.*"};if(dialog.ShowDialog(owner)==true)OfferFiles(dialog.FileNames,group);}
  void ChooseDirectory(){using(var dialog=new System.Windows.Forms.FolderBrowserDialog{Description="只收藏文件夹入口，不导入里面的文件"})if(dialog.ShowDialog()==System.Windows.Forms.DialogResult.OK)OfferFiles(new[]{dialog.SelectedPath},group);}
  public bool OfferFiles(string[] paths,string target){
   var dialog=new Window{Owner=owner,Title="添加到 Nook",Width=460,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner};Theme.Attach(dialog,owner);
   var p=new StackPanel{Margin=new Thickness(24)};p.Children.Add(new TextBlock{Text="添加快捷收藏",FontSize=22,FontWeight=FontWeights.SemiBold});p.Children.Add(new TextBlock{Text=paths.Length+" 项 · 不复制、不移动原文件\n文件夹只收藏入口，不导入里面的内容",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,20),LineHeight=23});
   var labels=paths.Take(3).Select(System.IO.Path.GetFileName).ToArray();p.Children.Add(new TextBlock{Text=string.Join("\n",labels)+(paths.Length>3?"\n…":""),TextTrimming=TextTrimming.CharacterEllipsis,Margin=new Thickness(0,0,0,18)});p.Children.Add(new TextBlock{Text="放入分类框",Margin=new Thickness(0,0,0,6)});
   var choices=new List<Category>{new Category{Id="",Name="未分类"}};choices.AddRange(store.Categories());var select=new ComboBox{ItemsSource=choices,DisplayMemberPath="Name",SelectedValuePath="Id",SelectedValue=target??"",Margin=new Thickness(0,0,0,16),MinHeight=32};if(select.SelectedItem==null)select.SelectedIndex=0;p.Children.Add(select);
   var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};var cancel=ReferencePage.Button("取消",()=>dialog.Close());cancel.IsCancel=true;actions.Children.Add(cancel);var add=ReferencePage.Button("添加快捷方式",()=>{try{int added=store.AddShortcuts(paths,(string)select.SelectedValue);dialog.DialogResult=true;status("已添加 "+added+" 个快捷方式 · 原文件未复制");}catch(Exception ex){MessageBox.Show(dialog,ex.Message,"未添加");}});add.IsDefault=true;actions.Children.Add(add);p.Children.Add(actions);dialog.Content=p;
   if(dialog.ShowDialog()!=true)return false;
   var addedPaths=new HashSet<string>(paths.Select(ReferenceStore.Normalize),StringComparer.OrdinalIgnoreCase);var addedRows=store.All().Where(r=>addedPaths.Contains(r.Path)).ToList();section=addedRows.Count>0&&addedRows.All(r=>r.Section=="documents")?"documents":addedRows.Count>0&&addedRows.All(r=>r.Section=="websites")?"shortcuts":"all";group="";search.Clear();Refresh();if(SectionChanged!=null)SectionChanged();return true;
  }
  void Undo(){status(store.Undo()?"已撤销上一步 · 原文件未改动":"没有可撤销的操作");Refresh();}
  public void HandleKey(KeyEventArgs e){if(e.Key==Key.F&&Keyboard.Modifiers==ModifierKeys.Control){search.Focus();e.Handled=true;return;}if(e.Key==Key.Escape){if(search.Text!="")search.Clear();else tiles.SelectedItems.Clear();e.Handled=true;return;}if(Keyboard.FocusedElement is TextBoxBase||working)return;if(Keyboard.Modifiers==ModifierKeys.Control){if(e.Key==Key.Z)Try(Undo);else if(e.Key==Key.A)tiles.SelectAll();else if(e.Key==Key.V)Try(Paste);else if(e.Key==Key.C)Try(CopyFiles);else if(e.Key==Key.O)Try(Choose);else return;e.Handled=true;}else if(e.Key==Key.Delete){Try(Remove);e.Handled=true;}else if(e.Key==Key.Enter||e.Key==Key.Space){OpenSelection();e.Handled=true;}else if(e.Key==Key.F5){Refresh();e.Handled=true;}}
 }
}
