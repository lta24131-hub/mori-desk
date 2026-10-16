using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Mori {
 public partial class MainWindow {
  string localCollection="";
  sealed class LocalCollectionTarget {public string Id;}
  bool LocalCollectionsActive {get{return !canvasProjectsMode&&library.IncludeReferences&&!bookmarkMode&&mediaSection=="shortcuts"&&view=="all";}}
  void InitializeLocalCollections(){
   B("NewLocalCollection").Click+=delegate{NewLocalCollection(false);};
   B("LocalCollectionBack").Click+=delegate{OpenLocalCollection("");};
   B("LocalCollectionBack").Tag=new LocalCollectionTarget{Id=""};
   Find<StackPanel>("LocalCollectionPage").PreviewMouseRightButtonDown+=delegate(object s,MouseButtonEventArgs e){
    if(!LocalCollectionsActive)return;string target=LocalCollectionAt(e.OriginalSource as DependencyObject);var menu=target!=null&&target!=""?CreateLocalCollectionMenu(target):CreateGalleryMenu();menu.IsOpen=true;e.Handled=true;
   };
   Window.PreviewDragOver+=LocalCollectionDragOver;
   Window.PreviewDrop+=LocalCollectionDrop;
  }
  IEnumerable<Asset> FilterLocalCollections(IEnumerable<Asset> query,string search){
   if(!LocalCollectionsActive)return query;
   if(localCollection!=""&&!library.LocalCollections().Any(f=>f.Id==localCollection))localCollection="";
   // Searching the root also finds items inside folders; ordinary browsing
   // shows each shortcut only once, at the root or inside its collection.
   return localCollection==""&&search.Length>0?query:query.Where(a=>(a.LocalCollection??"")==localCollection);
  }
  void UpdateLocalCollections(){
   var page=Find<StackPanel>("LocalCollectionPage");if(page==null)return;
   page.Visibility=LocalCollectionsActive?Visibility.Visible:Visibility.Collapsed;
   var panel=Find<WrapPanel>("LocalCollectionList");panel.Children.Clear();if(!LocalCollectionsActive)return;
   var folders=library.LocalCollections();var current=folders.FirstOrDefault(f=>f.Id==localCollection);
   L("LocalCollectionBreadcrumb").Text=current==null?"":"/  "+current.Name;
   B("LocalCollectionBack").Content=current==null?"分类文件夹":"← 本地收藏";
   B("NewLocalCollection").Visibility=current==null?Visibility.Visible:Visibility.Collapsed;
   string search=T("Search").Text.Trim();
   if(current==null)foreach(var folder in folders.Where(f=>search.Length==0||f.Name.IndexOf(search,StringComparison.CurrentCultureIgnoreCase)>=0)){
    var items=all.Where(a=>a.IsReference&&a.LocalCollection==folder.Id).ToList();
    panel.Children.Add(LocalCollectionCard(folder,items));
   }
   Find<ScrollViewer>("LocalCollectionScroll").Visibility=panel.Children.Count>0?Visibility.Visible:Visibility.Collapsed;
   L("ViewCount").Text=(current==null?all.Count(a=>a.IsReference):visible.Count)+" 项";
   if(visible.Count==0){L("EmptyTitle").Text=search!=""?"没有匹配的文件":current!=null?"把文件拖进这个分类":"把文件拖到这里收藏";
    L("EmptyHint").Text=search!=""?"可以按名称、标签或本地地址搜索。":current!=null?"只整理快捷方式，不移动电脑上的原文件。":"空白处右键新建分类文件夹，再把快捷方式拖进去。";
    B("EmptyImport").Visibility=Visibility.Collapsed;
    // Existing folders are already an effective empty state for the root.
    if(current==null&&panel.Children.Count>0)Find<StackPanel>("Empty").Visibility=Visibility.Collapsed;
   }
  }
  Button LocalCollectionCard(LocalCollection folder,List<Asset> items){
   var button=new Button{Width=132,Padding=new Thickness(8),Margin=new Thickness(0,0,12,8),Background=Brushes.Transparent,Tag=new LocalCollectionTarget{Id=folder.Id},ToolTip=folder.Name+" · "+items.Count+" 项\n点击打开 · 拖入分类 · 右键管理"};
   System.Windows.Automation.AutomationProperties.SetName(button,"分类文件夹 "+folder.Name+"，"+items.Count+" 项");
   var body=new StackPanel();var mosaic=new UniformGrid{Rows=2,Columns=2,Margin=new Thickness(8)};
   foreach(var asset in items.Take(4)){
    var cell=new Border{Margin=new Thickness(3),CornerRadius=new CornerRadius(4),ClipToBounds=true};cell.SetResourceReference(Border.BackgroundProperty,"Panel");
    if(MediaTypes.Contains(asset,"pictures"))cell.Child=new ThumbnailImage{DataContext=asset,Stretch=Stretch.UniformToFill};
    else{var icon=new TextBlock{Text=asset.IsDirectory?"\uE8B7":asset.IsVideo?"\uE714":asset.IsAudio?"\uE8D6":asset.FileGlyph,FontFamily=new FontFamily("Segoe MDL2 Assets"),FontSize=18,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};icon.SetResourceReference(TextBlock.ForegroundProperty,"Accent");cell.Child=icon;}
    mosaic.Children.Add(cell);
   }
   var square=new Border{Width=88,Height=88,CornerRadius=new CornerRadius(17),BorderThickness=new Thickness(1),Child=mosaic};square.SetResourceReference(Border.BackgroundProperty,"Surface");square.SetResourceReference(Border.BorderBrushProperty,"Divider");
   if(items.Count==0){var empty=new TextBlock{Text="\uE8F4",FontFamily=new FontFamily("Segoe MDL2 Assets"),FontSize=28,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};empty.SetResourceReference(TextBlock.ForegroundProperty,"Muted");square.Child=empty;}
   body.Children.Add(square);body.Children.Add(new TextBlock{Text=folder.Name,TextAlignment=TextAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis,Margin=new Thickness(0,8,0,2)});
   var count=new TextBlock{Text=items.Count+" 项",TextAlignment=TextAlignment.Center,FontSize=11};count.SetResourceReference(TextBlock.ForegroundProperty,"Muted");body.Children.Add(count);button.Content=body;button.Click+=delegate{OpenLocalCollection(folder.Id);};
   return button;
  }
  internal void OpenLocalCollection(string id){if(!LocalCollectionsActive||busy||!PrepareLibraryChange())return;if(id!=""&&!library.LocalCollections().Any(f=>f.Id==id))return;localCollection=id;selection.Clear();anchor="";pressed=null;T("Search").Clear();Refresh(true);var scroll=VisualChild<ScrollViewer>(Find<ListBox>("Gallery"));if(scroll!=null)scroll.ScrollToTop();Find<ListBox>("Gallery").Focus();}
  internal string CreateLocalCollection(string name,string[] ids){if(canvasProjectsMode)return "";string id="";Edit("新建分类文件夹",()=>{id=library.CreateLocalCollection(name);if(ids!=null&&ids.Length>0)library.MoveToLocalCollection(ids,id);});Refresh(true);return id;}
  void NewLocalCollection(bool includeSelected){if(canvasProjectsMode||busy||!PrepareLibraryChange())return;string name=Prompt("新建分类文件夹","只在本地收藏内整理，不改变电脑上的文件位置。","新建分类");if(name==null)return;try{CreateLocalCollection(name,includeSelected?selection.ToArray():null);}catch(Exception ex){Status(ex.Message);}}
  internal void MoveLocalCollection(string[] ids,string target){if(canvasProjectsMode||busy)return;try{if(!SaveClassicPending())return;Edit(target==""?"移回本地收藏外层":"移入分类文件夹",()=>library.MoveToLocalCollection(ids,target));Refresh(true);}catch(Exception ex){Status(ex.Message);}}
  void ChooseLocalCollection(){if(canvasProjectsMode||busy||selection.Count==0||!PrepareLibraryChange())return;var choices=library.LocalCollections().Select(f=>new CategoryChoice{Id=f.Id,Label=f.Name}).ToList();choices.Insert(0,new CategoryChoice{Id="",Label="本地收藏外层"});string target=CategoryPicker.Show(Window,choices,"移动到分类文件夹","只整理快捷方式，不移动原文件。","移动");if(target!=null)MoveLocalCollection(selection.ToArray(),target);}
  internal ContextMenu CreateLocalCollectionMenu(string id){
   var menu=Theme.Menu(Window);var folder=library.LocalCollections().FirstOrDefault(f=>f.Id==id);if(folder==null)return menu;
   menu.Items.Add(ActionItem("打开分类文件夹",()=>OpenLocalCollection(id),"",!busy));
   menu.Items.Add(ActionItem("重命名…",()=>{if(!PrepareLibraryChange())return;string name=Prompt("重命名分类文件夹","分类文件夹名称",folder.Name);if(name==null)return;try{Edit("重命名分类文件夹",()=>library.RenameLocalCollection(id,name));Refresh(true);}catch(Exception ex){Status(ex.Message);}},"",!busy));
   menu.Items.Add(ActionItem("解散分类（保留内容）",()=>{if(!PrepareLibraryChange())return;try{Edit("解散分类文件夹",()=>library.DeleteLocalCollection(id));if(localCollection==id)localCollection="";Refresh(true);Status("分类已解散，内容回到本地收藏外层 · Ctrl+Z 撤销");}catch(Exception ex){Status(ex.Message);}},"",!busy));return menu;
  }
  string LocalCollectionAt(DependencyObject source){if(!LocalCollectionsActive)return null;for(var d=source;d!=null;d=SelectionParent(d)){var element=d as FrameworkElement;var target=element==null?null:element.Tag as LocalCollectionTarget;if(target!=null)return target.Id;}return null;}
  internal string ExternalLocalCollectionTarget(DependencyObject source){if(!LocalCollectionsActive)return null;return LocalCollectionAt(source)??localCollection;}
  bool IsCollectionDrag(IDataObject data){if(!data.GetDataPresent("Mori.AssetIds"))return false;var ids=data.GetData("Mori.AssetIds") as string[];return ids!=null&&ids.Length>0&&ids.All(id=>all.Any(a=>a.Id==id&&a.IsReference));}
  void LocalCollectionDragOver(object sender,DragEventArgs e){if(e.Handled||canvasProjectsMode||!LocalCollectionsActive||!e.Data.GetDataPresent("Mori.AssetIds"))return;string target=LocalCollectionAt(e.OriginalSource as DependencyObject);HighlightLocalCollection(e.OriginalSource as DependencyObject);if(target==null)return;e.Handled=true;e.Effects=!busy&&IsCollectionDrag(e.Data)?((e.AllowedEffects&DragDropEffects.Move)!=0?DragDropEffects.Move:ShortcutDropEffect(e.AllowedEffects)):DragDropEffects.None;}
  void LocalCollectionDrop(object sender,DragEventArgs e){if(e.Handled||canvasProjectsMode||!LocalCollectionsActive||!e.Data.GetDataPresent("Mori.AssetIds"))return;string target=LocalCollectionAt(e.OriginalSource as DependencyObject);HighlightLocalCollection(null);if(target==null)return;e.Handled=true;if(busy||!IsCollectionDrag(e.Data))return;var ids=(string[])e.Data.GetData("Mori.AssetIds");Window.Dispatcher.BeginInvoke(new Action(()=>MoveLocalCollection(ids,target)));}
  Button localDropHighlight;
  void HighlightLocalCollection(DependencyObject source){Button next=null;if(LocalCollectionsActive)for(var d=source;d!=null;d=SelectionParent(d)){var b=d as Button;if(b!=null&&b.Tag is LocalCollectionTarget){next=b;break;}}if(next==localDropHighlight)return;if(localDropHighlight!=null)localDropHighlight.Background=Brushes.Transparent;localDropHighlight=next;if(next!=null)next.SetResourceReference(Control.BackgroundProperty,"AccentSoft");}
 }
}
