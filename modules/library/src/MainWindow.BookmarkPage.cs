using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mori {
 public partial class MainWindow {
  Dictionary<string,int> bookmarkFolderCounts=new Dictionary<string,int>();
  internal static bool SectionContains(Asset a,string section){return MediaTypes.Contains(a,section);}
  void BookmarkNavigate(string id){RequestNavigate(id);}
  void InitializeBookmarkBrowser(){
   B("BookmarkOverview").Click+=delegate{BookmarkNavigate(view=="all"?"home":"all");};
   Find<Grid>("Center").SizeChanged+=delegate{SizeBookmarkFolders();};
  }
  internal static double BookmarkCardWidth(double available){int columns=Math.Max(1,(int)(available/242));return Math.Max(100,Math.Floor(available/columns)-10);}
  void SizeBookmarkFolders(){
   if(!bookmarkMode)return;var scroll=Find<ScrollViewer>("BookmarkFoldersScroll");double available=Math.Max(120,Find<Grid>("Center").ActualWidth-48-14);double width=BookmarkCardWidth(available);
   foreach(Button card in Find<WrapPanel>("BookmarkFolderList").Children)card.Width=width;
   double height=Math.Max(140,Find<Grid>("Center").ActualHeight-110);scroll.MaxHeight=visible.Count==0?height:Math.Min(292,height*.48);
  }
  internal static Dictionary<string,int> BookmarkCounts(IEnumerable<Category> folders,IEnumerable<Dictionary<string,string>> memberships,HashSet<string> active){
   var map=folders.ToDictionary(f=>f.Id);var sets=map.Keys.ToDictionary(id=>id,id=>new HashSet<string>());
   foreach(var membership in memberships){string id=membership["folder"],asset=membership["asset"];if(!active.Contains(asset))continue;var seen=new HashSet<string>();Category node;
    while(map.TryGetValue(id,out node)&&seen.Add(id)){sets[id].Add(asset);id=node.Parent;}
   }return sets.ToDictionary(pair=>pair.Key,pair=>pair.Value.Count);
  }
  void UpdateBookmarkBrowserStatus(){
   if(!bookmarkMode)return;bool search=T("Search").Text.Trim().Length>0;var list=Find<WrapPanel>("BookmarkFolderList");
   if(view=="home"&&!search)L("ViewCount").Text=all.Count(a=>a.IsBookmark&&!a.Deleted)+" 个网址";
   int total;if(!search&&bookmarkFolderCounts.TryGetValue(view,out total))L("ViewCount").Text=total+" 个网址（含子文件夹）";
   L("BookmarkItemsHeading").Text=search?"搜索结果 · 包含子文件夹":view=="home"?"未分类网址 · "+visible.Count:view=="all"?"全部网址 · "+visible.Count:"本层网址 · "+visible.Count;
   L("BookmarkItemsHeading").Visibility=visible.Count>0?Visibility.Visible:Visibility.Collapsed;
   if(visible.Count==0&&list.Children.Count>0){Find<StackPanel>("Empty").Visibility=Visibility.Collapsed;}
   else if(visible.Count==0&&view=="home"&&!search){L("EmptyTitle").Text="收藏常用网站";L("EmptyHint").Text="导入浏览器书签后，会按原文件夹展示。\n也可以直接粘贴网址收藏。";}
   T("Search").ToolTip=CurrentFolder()==""?"搜索全部网址 · Ctrl+F":"搜索此文件夹及子文件夹 · Ctrl+F";
   SizeBookmarkFolders();
  }
  void UpdateBookmarkPage(){
   Find<StackPanel>("BookmarkPage").Visibility=bookmarkMode?Visibility.Visible:Visibility.Collapsed;
   Find<TreeView>("Folders").Visibility=bookmarkMode?Visibility.Collapsed:Visibility.Visible;Find<Grid>("MediaFolderHeading").Visibility=bookmarkMode?Visibility.Collapsed:Visibility.Visible;
   if(!bookmarkMode)return;
   bookmarkFolderCounts.Clear();var folders=library.Folders(true);var crumbs=Find<WrapPanel>("BookmarkBreadcrumb");var list=Find<WrapPanel>("BookmarkFolderList");crumbs.Children.Clear();list.Children.Clear();
   Action<string,string> crumb=delegate(string label,string id){if(crumbs.Children.Count>0){var divider=new TextBlock{Text="/",Margin=new Thickness(3,0,3,0),VerticalAlignment=VerticalAlignment.Center};divider.SetResourceReference(TextBlock.ForegroundProperty,"Muted");crumbs.Children.Add(divider);}var b=new Button{Content=new TextBlock{Text=label,TextTrimming=TextTrimming.CharacterEllipsis},Padding=new Thickness(6,5,6,5),Margin=new Thickness(0,0,2,0),ToolTip=label,MaxWidth=200,Background=Brushes.Transparent};System.Windows.Automation.AutomationProperties.SetName(b,label);b.Click+=delegate{BookmarkNavigate(id);};crumbs.Children.Add(b);};
   crumb("文件夹","home");
   var chain=new List<Category>();var node=folders.FirstOrDefault(f=>f.Id==view);var seen=new HashSet<string>();while(node!=null&&seen.Add(node.Id)){chain.Insert(0,node);node=folders.FirstOrDefault(f=>f.Id==node.Parent);}foreach(var item in chain)crumb(item.Name,item.Id);
   if(view!="home"&&chain.Count==0)crumb(view=="all"?"全部网址":view=="trash"?"回收站":view=="favorites"?"星标":"未分类",view);
   B("BookmarkOverview").Content=view=="all"?"按文件夹":"全部网址";UiIcons.SetGlyph(B("BookmarkOverview"),view=="all"?"\uE8B7":"\uEA37");
   if((view!="home"&&CurrentFolder()=="")||T("Search").Text.Trim().Length>0)return;
   var counts=BookmarkCounts(folders,library.Db.Query("SELECT asset,folder FROM memberships"),new HashSet<string>(all.Where(a=>a.IsBookmark&&!a.Deleted).Select(a=>a.Id)));
   bookmarkFolderCounts=counts;
   var childCounts=folders.GroupBy(f=>f.Parent).ToDictionary(g=>g.Key,g=>g.Count());
   foreach(var folder in folders.Where(f=>f.Parent==CurrentFolder())){
    var f=folder;var body=new Grid();body.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(36)});body.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
    var icon=new System.Windows.Shapes.Path{Data=Geometry.Parse("M1,4 L9,4 L12,7 L23,7 L23,21 L1,21 Z M1,9 L23,9"),Width=24,Height=22,Stretch=Stretch.Uniform,StrokeThickness=1.4,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Left};icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"Accent");body.Children.Add(icon);
    var labels=new StackPanel{VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(labels,1);body.Children.Add(labels);labels.Children.Add(new TextBlock{Text=f.Name,TextWrapping=TextWrapping.Wrap,TextTrimming=TextTrimming.CharacterEllipsis,MaxHeight=36,LineHeight=18,FontSize=13});int children;childCounts.TryGetValue(f.Id,out children);string summary=counts[f.Id]+" 个网址"+(children>0?" · "+children+" 个文件夹":"");var count=new TextBlock{Text=summary,FontSize=11,Margin=new Thickness(0,6,0,0)};count.SetResourceReference(TextBlock.ForegroundProperty,"Muted");labels.Children.Add(count);
    var button=new Button{Content=body,Width=232,Height=88,HorizontalContentAlignment=HorizontalAlignment.Stretch,Padding=new Thickness(14,10,14,10),Margin=new Thickness(0,0,10,10),ToolTip=f.Name+"\n"+summary+"（含子文件夹）\n单击打开 · 右键管理 · 可拖入网址",AllowDrop=true};button.SetResourceReference(Button.BackgroundProperty,"Surface");System.Windows.Automation.AutomationProperties.SetName(button,"网址文件夹："+f.Name);System.Windows.Automation.AutomationProperties.SetHelpText(button,summary);
    button.Click+=delegate{BookmarkNavigate(f.Id);};button.PreviewMouseRightButtonDown+=delegate(object sender,System.Windows.Input.MouseButtonEventArgs e){var menu=Theme.Menu(Window);Menu(menu,"新建子文件夹…",()=>NewBookmarkFolder(f.Id));Menu(menu,"重命名…",()=>RenameFolder(f.Id));Menu(menu,"删除文件夹（保留网址）…",()=>DeleteFolder(f.Id));menu.PlacementTarget=button;menu.IsOpen=true;e.Handled=true;};
    button.Drop+=delegate(object sender,DragEventArgs e){e.Handled=true;if(busy||!e.Data.GetDataPresent("Mori.AssetIds"))return;var ids=(string[])e.Data.GetData("Mori.AssetIds");if(!PrepareLibraryChange())return;try{Edit("移动网址到："+f.Name,()=>library.MoveBookmarks(ids,f.Id));Refresh(true);}catch(Exception error){Status(error.Message);}};
    list.Children.Add(button);
   }
   SizeBookmarkFolders();
  }
  void NewBookmarkFolder(string parent){
   if(busy||!PrepareLibraryChange())return;string name=Prompt("新建网址文件夹","文件夹名称","");if(name==null)return;
   try{Edit("新建网址文件夹",()=>library.CreateFolder(name,parent,true));Refresh(false);}catch(Exception error){MessageBox.Show(Window,error.Message,"无法新建文件夹");}
  }
  void ShowBookmarkMove(){
   var menu=Theme.Menu(Window);Menu(menu,"未分类",()=>MoveSelectedBookmarks("","未分类"));foreach(var folder in library.Folders(true)){var f=folder;Menu(menu,FolderLabel(f,library.Folders(true)),()=>MoveSelectedBookmarks(f.Id,f.Name));}menu.PlacementTarget=B("Categorize");menu.IsOpen=true;
  }
  void MoveSelectedBookmarks(string id,string name){if(!PrepareLibraryChange())return;Edit("移动网址到："+name,()=>library.MoveBookmarks(selection,id));Refresh(true);}
 }
}
