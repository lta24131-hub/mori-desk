using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Mori {
 public partial class MainWindow {
  bool waterfallLayout,showAssetNames=true,showAssetDetails=true;
  void InitializeGalleryView(Settings settings){
   waterfallLayout=settings.WaterfallLayout;showAssetNames=settings.ShowAssetNames;showAssetDetails=settings.ShowAssetDetails;
   sidebars.SetVisible(true,settings.LeftSidebarVisible);sidebars.SetVisible(false,settings.RightSidebarVisible);
   B("ToggleInspector").Click+=delegate{ToggleSidebar(false);};UpdateDetailsToggle();
   // Include the empty-state overlay, but not toolbar controls or scroll bars.
   Find<Grid>("Center").PreviewMouseRightButtonDown+=delegate(object s,MouseButtonEventArgs e){
    if(e.Handled||Tile(e.OriginalSource)!=null)return;
    var source=e.OriginalSource as DependencyObject;
    if(!IsGalleryBlank(source,Find<ListBox>("Gallery"))&&!IsGalleryBlank(source,Find<StackPanel>("Empty")))return;
    var menu=CreateGalleryMenu();menu.PlacementTarget=Find<ListBox>("Gallery");menu.IsOpen=true;e.Handled=true;
   };
  }
  void SaveGalleryView(){if(PreviewMode)return;try{var settings=Settings.Load();settings.WaterfallLayout=waterfallLayout;settings.ShowAssetNames=showAssetNames;settings.ShowAssetDetails=showAssetDetails;settings.LeftSidebarVisible=sidebars.LeftVisible;settings.RightSidebarVisible=sidebars.RightVisible;settings.Save();}catch(Exception ex){Status("显示设置暂未保存："+ex.Message);}}
  internal void SetGalleryLayout(bool waterfall){waterfallLayout=waterfall;BuildRows(true);SaveGalleryView();}
  internal void SetGalleryInformation(bool name,bool details){showAssetNames=name;showAssetDetails=details;BuildRows(true);SaveGalleryView();}
  internal void ApplyBrowsePreferences(Settings settings){
   waterfallLayout=settings.WaterfallLayout;showAssetNames=settings.ShowAssetNames;showAssetDetails=settings.ShowAssetDetails;
   sidebars.SetVisible(true,settings.LeftSidebarVisible);sidebars.SetVisible(false,settings.RightSidebarVisible);
   sidebars.SetPreferred(true,settings.LeftSidebarWidth);sidebars.SetPreferred(false,settings.RightSidebarWidth);
   UpdateDetailsToggle();
   Find<Slider>("ThumbnailSize").Value=settings.ThumbnailSize;Find<ComboBox>("Sort").SelectedIndex=settings.DefaultSort;
   Refresh(false);BuildRows(true);if(canvasProjectsMode)ApplyCanvasProjectsPresentation();
  }
  void UpdateDetailsToggle(){var button=B("ToggleInspector");bool visible=sidebars.RightVisible;string label=visible?"收起详情栏":"显示详情栏";button.Content=visible?"\uE89F":"\uE8A0";button.ToolTip=label+" · Ctrl+Alt+2";System.Windows.Automation.AutomationProperties.SetName(button,label);}
  internal void ToggleSidebar(bool left){sidebars.SetVisible(left,left?!sidebars.LeftVisible:!sidebars.RightVisible);UpdateDetailsToggle();SaveGalleryView();QueueLayout();}
  internal void RefreshGallery(){
   if(canvasProjectsMode){RefreshCanvasProjects();ApplyCanvasProjectsPresentation();return;}
   if(busy){Status("正在处理素材，请完成后再刷新。");return;}
   if(!PrepareLibraryChange())return;
   var scroll=VisualChild<ScrollViewer>(Find<ListBox>("Gallery"));double offset=scroll==null?0:scroll.VerticalOffset;
   string next=library.Db.Query("PRAGMA data_version")[0]["data_version"];if(dataVersion!=""&&next!=dataVersion)history.Clear();dataVersion=next;
   ReloadFolders();Refresh(true);Find<ListBox>("Gallery").UpdateLayout();if(scroll!=null)scroll.ScrollToVerticalOffset(offset);Status("已刷新 · 保留当前分类与筛选");
  }
  internal ContextMenu CreateGalleryMenu(){
   var menu=Theme.Menu(Window);var sort=new MenuItem{Header="排列方式"};var combo=Find<ComboBox>("Sort");
   if(LocalCollectionsActive){if(localCollection=="")menu.Items.Add(ActionItem("新建分类文件夹…",()=>NewLocalCollection(false),"",!busy));else{menu.Items.Add(ActionItem("返回本地收藏",()=>OpenLocalCollection("")));var actions=CreateLocalCollectionMenu(localCollection);while(actions.Items.Count>1){var entry=actions.Items[1];actions.Items.RemoveAt(1);menu.Items.Add(entry);}}menu.Items.Add(new Separator());}
   for(int i=0;i<combo.Items.Count;i++){int index=i;var choice=ViewChoice(Convert.ToString(((ComboBoxItem)combo.Items[i]).Content),"",combo.SelectedIndex==i,delegate{combo.SelectedIndex=index;});sort.Items.Add(choice);}menu.Items.Add(sort);
   var info=new MenuItem{Header="显示信息",IsEnabled=!bookmarkMode};
   info.Items.Add(ViewChoice("文件名","",showAssetNames,delegate{SetGalleryInformation(!showAssetNames,showAssetDetails);}));
   info.Items.Add(ViewChoice("尺寸 / 格式","",showAssetDetails,delegate{SetGalleryInformation(showAssetNames,!showAssetDetails);}));if(!bookmarkMode)menu.Items.Add(info);menu.Items.Add(new Separator());
   if(!bookmarkMode){menu.Items.Add(ViewChoice("瀑布布局","Alt+1",waterfallLayout,delegate{SetGalleryLayout(true);}));menu.Items.Add(ViewChoice("自适应布局","Alt+2",!waterfallLayout,delegate{SetGalleryLayout(false);}));menu.Items.Add(new Separator());}
   menu.Items.Add(ViewChoice("显示左栏","Ctrl+Alt+1",sidebars.LeftVisible,delegate{ToggleSidebar(true);}));
   menu.Items.Add(ViewChoice("显示右栏","Ctrl+Alt+2",sidebars.RightVisible,delegate{ToggleSidebar(false);}));menu.Items.Add(new Separator());
   var refresh=new MenuItem{Header="刷新",InputGestureText="F5",IsEnabled=!busy};refresh.Click+=delegate{RefreshGallery();};menu.Items.Add(refresh);
   return menu;
  }
  static MenuItem ViewChoice(string title,string shortcut,bool selected,Action action){var item=new MenuItem{Header=title,InputGestureText=shortcut,IsCheckable=true,IsChecked=selected};item.Click+=delegate{action();};return item;}
  bool HandleGalleryViewKey(KeyEventArgs e){
   var key=e.Key==Key.System?e.SystemKey:e.Key;var modifiers=Keyboard.Modifiers;
   if(key==Key.F5&&modifiers==ModifierKeys.None){RefreshGallery();e.Handled=true;return true;}
   if(key!=Key.D1&&key!=Key.D2)return false;
   if(modifiers==(ModifierKeys.Control|ModifierKeys.Alt)){ToggleSidebar(key==Key.D1);e.Handled=true;return true;}
   if(modifiers==ModifierKeys.Alt&&!bookmarkMode){SetGalleryLayout(key==Key.D1);e.Handled=true;return true;}return false;
  }
 }
}
