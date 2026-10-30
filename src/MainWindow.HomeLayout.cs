using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace Mori {
 public partial class MainWindow {
  bool homeLayoutReady,homeInspectorExplicit;
  string homeConversionKey;
  void InitializeHomeLayout(){
   homeLayoutReady=true;
   B("HomeAdd").Click+=delegate{ShowHomeMenu(B("HomeAdd"),CreateHomeAddMenu());};
   B("HomeTypePicker").Click+=delegate{ShowHomeMenu(B("HomeTypePicker"),CreateHomeTypeMenu());};
   B("HomeView").Click+=delegate{ShowHomeMenu(B("HomeView"),CreateGalleryMenu());};
   B("HomeNavigationToggle").Click+=delegate{ToggleSidebar(true);};
   B("SelectionMore").Click+=delegate{ShowHomeMenu(B("SelectionMore"),CreateSelectionActionsMenu());};
   Window.Closed+=delegate{homeLayoutReady=false;};
   UpdateHomeNavigation();UpdateHomeSelection();
  }
  static void ShowHomeMenu(FrameworkElement anchor,ContextMenu menu){
   if(menu==null||menu.Items.Count==0)return;menu.PlacementTarget=anchor;menu.Placement=PlacementMode.Bottom;menu.IsOpen=true;
  }
  internal ContextMenu CreateHomeAddMenu(){
   var menu=Theme.Menu(Window);bool enabled=!busy&&!canvasProjectsMode&&!browserMode;
   if(LocalCollectionsActive){
    menu.Items.Add(ActionItem("收藏本地文件…",ChooseReferenceFiles,"Ctrl+O",enabled));
    menu.Items.Add(ActionItem("收藏本地文件夹…",delegate{if(busy||!PrepareLibraryChange())return;string path=FolderPicker("收藏本地文件夹 · 只保存位置");if(path!=null)CollectClassicPaths(new[]{path},"",localCollection);},"",enabled));
    menu.Items.Add(ActionItem("新建分类文件夹…",()=>NewLocalCollection(false),"",enabled));
   }else if(bookmarkMode){
    menu.Items.Add(ActionItem("收藏网址…",()=>AddBookmark(""),"",enabled));
    menu.Items.Add(ActionItem("导入浏览器书签…",ShowBookmarkImport,"Ctrl+O",enabled));
   }else{
    menu.Items.Add(ActionItem("导入素材…",ChooseFiles,"Ctrl+O",enabled));
    menu.Items.Add(ActionItem("导入整个文件夹…",delegate{if(busy||!PrepareLibraryChange())return;string path=FolderPicker("导入文件夹 · 在图库保存副本");if(path!=null)ImportPaths(new[]{path},null);},"",enabled));
   }
   menu.Items.Add(new Separator());menu.Items.Add(ActionItem("粘贴收藏",Paste,"Ctrl+V",enabled));
   if(!bookmarkMode&&!LocalCollectionsActive)menu.Items.Add(ActionItem("收藏网址…",()=>AddBookmark(""),"",enabled));
   return menu;
  }
  internal ContextMenu CreateHomeTypeMenu(){
   var menu=Theme.Menu(Window);
   if(bookmarkMode||LocalCollectionsActive||canvasProjectsMode||browserMode)return menu;
   if(mediaSection=="media"){
    var filter=Find<ComboBox>("MediaFilter");
    for(int i=0;i<filter.Items.Count;i++){int index=i;var choice=ViewChoice(Convert.ToString(((ComboBoxItem)filter.Items[i]).Content),"",filter.SelectedIndex==index,delegate{filter.SelectedIndex=index;});choice.IsEnabled=!busy;menu.Items.Add(choice);}
   }else{
    string[] sections={"assets","pictures","videos","audio","documents"},labels={"全部类型","图片（含动图）","视频","音频","文档"};
    for(int i=0;i<sections.Length;i++){string section=sections[i];var choice=ViewChoice(labels[i],"",mediaSection==section,()=>SelectHomeTypeSection(section));choice.IsEnabled=!busy;menu.Items.Add(choice);}
   }
   return menu;
  }
  void SelectHomeTypeSection(string section){
   string query=T("Search").Text;SetSection(section);
   if(mediaSection!=section||view!="all"||bookmarkMode)return;
   if(T("Search").Text!=query){T("Search").Text=query;searchTimer.Stop();RefreshView();}
  }
  internal ContextMenu CreateSelectionCategoryMenu(){
   var menu=Theme.Menu(Window);bool enabled=!busy&&selection.Count>0&&view!="trash";
   if(LocalCollectionsActive){menu.Items.Add(ActionItem("移到分类文件夹…",ChooseLocalCollection,"",enabled));menu.Items.Add(ActionItem("新建分类并移入…",()=>NewLocalCollection(true),"",enabled));}
   else if(bookmarkMode)menu.Items.Add(ActionItem("移动到文件夹…",()=>ChooseCategory(true),"",enabled));
   else{menu.Items.Add(ActionItem("移动到分类…",()=>ChooseCategory(true),"",enabled));menu.Items.Add(ActionItem("添加到分类…",Categorize,"",enabled));}
   return menu;
  }
  internal ContextMenu CreateSelectionActionsMenu(){
   var items=Selected();if(items.Count==0||canvasProjectsMode||browserMode)return Theme.Menu(Window);
   // Reuse the real asset actions and their validation, rather than introducing
   // a second implementation of copy/export/delete for the toolbar.
   var menu=CreateAssetMenu(items[0]);
   string[] primary={"加入自由画布…","转为","移动到分类…","添加到分类…","移动到文件夹…","移动到分类文件夹…","新建分类并移入…"};
   foreach(var item in menu.Items.OfType<MenuItem>().Where(item=>primary.Contains(Convert.ToString(item.Header))).ToArray())menu.Items.Remove(item);
   if(items.Count==1&&view!="trash"&&SectionContains(items[0],"pictures")){
    int index=Math.Max(0,menu.Items.Count-2);menu.Items.Insert(index,ActionItem("查找相似图片",()=>SearchByImage(false),"",!busy&&ImageSimilarityIndex.CanCompare(items[0])));menu.Items.Insert(index+1,ActionItem("查找相近颜色",()=>SearchByImage(true),"",!busy&&ImageSimilarityIndex.CanCompare(items[0])));
   }
   for(int i=menu.Items.Count-1;i>=0;i--)if(menu.Items[i] is Separator&&(i==0||i==menu.Items.Count-1||menu.Items[i-1] is Separator))menu.Items.RemoveAt(i);
   return menu;
  }
  void UpdateHomeNavigation(){
   if(!homeLayoutReady)return;
   string navigation=sidebars.LeftVisible?"收起导航栏":"显示导航栏";B("HomeNavigationToggle").ToolTip=navigation+" · Ctrl+Alt+1";System.Windows.Automation.AutomationProperties.SetName(B("HomeNavigationToggle"),navigation);
   bool local=LocalCollectionsActive;Find<ComboBox>("MediaFilter").Visibility=Visibility.Collapsed;
   B("HomeTypePicker").Visibility=!bookmarkMode&&!local?Visibility.Visible:Visibility.Collapsed;
   string type="全部类型";
   if(mediaSection=="media"){var filter=Find<ComboBox>("MediaFilter");var selected=filter.SelectedItem as ComboBoxItem;if(selected!=null)type=Convert.ToString(selected.Content);}
   else if(mediaSection=="pictures")type="图片";else if(mediaSection=="videos")type="视频";else if(mediaSection=="audio")type="音频";else if(mediaSection=="documents")type="文档";
   B("HomeTypePicker").Content=type+" ▾";B("HomeTypePicker").IsEnabled=!busy;
   B("HomeAdd").IsEnabled=!busy;B("HomeAdd").ToolTip=local?"收藏文件位置、创建分组或粘贴":bookmarkMode?"收藏网址、导入书签或粘贴":"导入素材、粘贴或收藏网址";
   B("Unfiled").Content=bookmarkMode?"未分类网址":"未分类";B("Favorites").Content=bookmarkMode?"星标网址":"星标收藏";B("Trash").Content=bookmarkMode?"网址回收站":"回收站";
   if(!bookmarkMode&&!local&&!canvasProjectsMode)B("All").SetResourceReference(Button.BackgroundProperty,view=="all"?"AccentSoft":"Panel");
  }
  void UpdateHomeSelection(){
   if(!homeLayoutReady)return;
   var items=Selected();bool any=items.Count>0,hidden=canvasProjectsMode||browserMode||boardMode||referenceMode,trash=view=="trash";
   Find<Border>("BatchBar").Visibility=!hidden&&(any||batchSelecting)?Visibility.Visible:Visibility.Collapsed;
   B("BatchAll").Visibility=batchSelecting?Visibility.Visible:Visibility.Collapsed;
   B("BatchMove").Content="分类";B("BatchMove").ToolTip=LocalCollectionsActive?"整理快捷收藏，不移动原文件":"移动到分类，或保留原分类并添加";
   B("BatchMove").Visibility=any&&!trash?Visibility.Visible:Visibility.Collapsed;
   B("SelectionMore").Visibility=any?Visibility.Visible:Visibility.Collapsed;B("SelectionMore").IsEnabled=!busy&&any;
   B("BatchDelete").Visibility=Visibility.Collapsed;
   bool pictures=any&&items.All(a=>SectionContains(a,"pictures"));
   B("SelectionCanvas").Visibility=pictures&&!trash?Visibility.Visible:Visibility.Collapsed;B("SelectionCanvas").IsEnabled=!busy&&pictures&&!trash;
   Find<StackPanel>("InspectorLegacyActions").Visibility=Visibility.Collapsed;
   Find<StackPanel>("LegacyHomeActions").Visibility=Visibility.Collapsed;
   UpdateHomeConversion(items,hidden||trash);UpdateHomeInspector();UpdateHomeNavigation();
  }
  void UpdateHomeConversion(System.Collections.Generic.List<Asset> items,bool hidden,bool force=false){
   var host=Find<Menu>("SelectionConvert");bool available=!hidden&&items.Count>0&&items.All(a=>!a.IsBookmark&&!a.IsDirectory);
   host.Visibility=available?Visibility.Visible:Visibility.Collapsed;
   string key=available?string.Join("|",items.Select(a=>a.Id+":"+a.Relative+":"+a.ReferencePath).OrderBy(value=>value,StringComparer.Ordinal))+"/"+busy+"/"+quickConversionRunning+"/"+FileConversionWorking:"";
   if(!force&&key==homeConversionKey)return;homeConversionKey=key;
   foreach(var old in host.Items.OfType<MenuItem>())old.IsSubmenuOpen=false;host.Items.Clear();if(!available)return;
   var item=CreateQuickConversionMenu();if(item==null)return;item.Header="转换";item.Style=(Style)Window.FindResource("SelectionFormatMenu");
   System.Windows.Automation.AutomationProperties.SetName(item,"转换所选文件");host.Items.Add(item);
   item.SubmenuClosed+=delegate{Window.Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(delegate{if(!homeLayoutReady||!host.Items.Contains(item)||item.IsSubmenuOpen)return;homeConversionKey=null;UpdateHomeSelection();}));};
  }
  void UpdateHomeInspector(){
   if(!homeLayoutReady)return;
   bool hide=!fileConversionVisible&&!homeInspectorExplicit&&selection.Count==0;
   if(sidebars.RightTemporarilyHidden!=hide){sidebars.SetTemporaryRightHidden(hide);QueueLayout();}
   UpdateDetailsToggle();
   // SidebarLayout applies its own visibility; page overlays retain priority.
   ApplyBrowserPresentation();if(canvasProjectsMode)ApplyCanvasProjectsPresentation();
  }
 }
}
