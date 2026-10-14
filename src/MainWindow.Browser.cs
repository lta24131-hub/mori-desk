using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MoriDesk;

namespace Mori {
 public partial class MainWindow {
  SimpleBrowserPane browserPane;Grid browserHost;bool browserMode,browserRefreshPending,browserInspectorRefreshPending;
  double browserGalleryOffset;
  readonly Dictionary<UIElement,Visibility> browserCovered=new Dictionary<UIElement,Visibility>();
  internal bool BrowserMode {get{return browserMode;}}
  internal SimpleBrowserPane BrowserPane {get{return browserPane;}}
  void CoverForBrowser(UIElement element){if(element==null)return;if(!browserCovered.ContainsKey(element))browserCovered[element]=element.Visibility;element.Visibility=Visibility.Collapsed;}
  void ApplyBrowserPresentation(){
   if(!browserMode)return;
   foreach(UIElement child in Find<Grid>("Workspace").Children)if(child!=browserHost&&Grid.GetColumn(child)>=2)CoverForBrowser(child);
   foreach(string name in new[]{"Import","Paste","AddBookmark","Export"})CoverForBrowser(B(name));
   CoverForBrowser((UIElement)T("Search").Parent);CoverForBrowser((UIElement)Find<Slider>("ThumbnailSize").Parent);
  }
  internal bool BrowseBookmark(string address){
   Uri normalized;string error;if(!SimpleBrowserPane.TryNormalizeAddress(address,out normalized,out error)){Status(error??"网址无效，请使用 http 或 https 网址。");return false;}
   if(busy||(!browserMode&&!PrepareLibraryChange()))return false;
   if(browserMode)return browserPane.Navigate(normalized.AbsoluteUri);
   HideBoard();HideReferences();bool pendingSearch=searchTimer.IsEnabled;searchTimer.Stop();layoutTimer.Stop();
   var scroll=VisualChild<ScrollViewer>(Find<ListBox>("Gallery"));browserGalleryOffset=scroll==null?0:scroll.VerticalOffset;
   var workspace=Find<Grid>("Workspace");var host=new Grid();host.SetResourceReference(Panel.BackgroundProperty,"Canvas");
   host.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});host.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
   var bar=new DockPanel{Margin=new Thickness(12,7,12,0)};
   var back=new Button{Content="‹ 返回收藏",Padding=new Thickness(9,4,9,4),ToolTip="返回当前收藏列表；关闭网页，释放浏览器资源"};
   System.Windows.Automation.AutomationProperties.SetName(back,"返回收藏");back.Click+=delegate{HideBrowser();};DockPanel.SetDock(back,Dock.Left);bar.Children.Add(back);
   var hint=new TextBlock{Text="快速浏览",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,0,0)};hint.SetResourceReference(TextBlock.ForegroundProperty,"Muted");bar.Children.Add(hint);host.Children.Add(bar);
   var pane=PreviewMode?new SimpleBrowserPane(Entry.DataRoot,CollectBrowserBookmark,Status,false):new SimpleBrowserPane(Entry.DataRoot,CollectBrowserBookmark,Status);
   Grid.SetRow(pane,1);host.Children.Add(pane);Grid.SetColumn(host,2);Grid.SetColumnSpan(host,3);Panel.SetZIndex(host,20);
   browserHost=host;browserPane=pane;browserMode=true;browserRefreshPending=pendingSearch;browserInspectorRefreshPending=false;browserCovered.Clear();workspace.Children.Add(host);ApplyBrowserPresentation();
   if(!pane.Navigate(normalized.AbsoluteUri)){HideBrowser();return false;}Status("正在快速浏览 · 返回收藏会关闭网页");return true;
  }
  void CollectBrowserBookmark(string url,string title){
   if(busy)return;
   try{string folder=bookmarkMode?CurrentFolder():"";var added=library.ImportBookmark(url,title??"",folder,"","","reuse");history.Clear();browserRefreshPending=true;Status(added.Duplicate?"此网址已收藏":"网址已收藏；返回列表后查看");}catch(Exception ex){Status("未能收藏网址："+ex.Message);}
  }
  internal void HideBrowser(){
   if(!browserMode)return;browserMode=false;
   if(browserPane!=null){browserPane.Dispose();browserPane=null;}if(browserHost!=null){Find<Grid>("Workspace").Children.Remove(browserHost);browserHost=null;}
   foreach(var entry in browserCovered)entry.Key.Visibility=entry.Value;browserCovered.Clear();sidebars.Apply();
   bool refresh=browserRefreshPending,inspector=browserInspectorRefreshPending;browserRefreshPending=browserInspectorRefreshPending=false;if(refresh){ReloadFolders();Refresh(inspector);}else{UpdateNavigationPresentation();UpdateInspectorPresentation();}
   // The tray releases realised thumbnails even when a webpage is active.
   // Rebind from the existing metadata without discarding inspector drafts.
   BuildRows();UpdateInspectorPreview();
   string restoreView=view,restoreSection=mediaSection,restoreSearch=T("Search").Text;bool restoreBookmarks=bookmarkMode;double restoreOffset=browserGalleryOffset;
   Find<ListBox>("Gallery").Focus();Window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(delegate{if(browserMode||view!=restoreView||mediaSection!=restoreSection||bookmarkMode!=restoreBookmarks||T("Search").Text!=restoreSearch)return;var scroll=VisualChild<ScrollViewer>(Find<ListBox>("Gallery"));if(scroll!=null)scroll.ScrollToVerticalOffset(restoreOffset);}));
   Status("已返回收藏");
  }
  bool HandleBrowserKey(KeyEventArgs e){
   if(!browserMode)return false;
   // Never let an embedded page's Delete, paste, undo, select-all or Space act
   // on hidden assets. Leave these events to the address bar / webpage itself.
   if(e.Key==Key.Escape&&Keyboard.Modifiers==ModifierKeys.Control){HideBrowser();e.Handled=true;}
   return true;
  }
  void PauseBrowser(){if(browserMode&&browserPane!=null)browserPane.Pause();}
  void ResumeBrowser(){if(browserMode&&browserPane!=null){browserPane.Resume();ApplyBrowserPresentation();}}
  void DisposeBrowser(){if(browserPane!=null){browserPane.Dispose();browserPane=null;}}
 }
}
