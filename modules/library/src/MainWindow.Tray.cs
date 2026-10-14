using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Mori {
 public partial class MainWindow {
  ITrayHost tray;bool exitRequested,backgroundSuspended;double suspendedOffset;
  public void EnableTray(string activationName,bool startup){
   if(PreviewMode||tray!=null)return;
   try{AttachTray(new TrayHost(activationName,Window.Dispatcher,RestoreFromTray,delegate{RestoreFromTray();ShowSettings();},ExitApplication));}
   catch(Exception error){Status("托盘未能启动，关闭窗口将退出 Nook："+error.Message);return;}
   if(startup){EventHandler ready=null;ready=delegate{Window.ContentRendered-=ready;HideToTray();};Window.ContentRendered+=ready;}
  }
  internal void AttachTray(ITrayHost host){
   tray=host;Window.Closed+=delegate{tray.Dispose();tray=null;};
   Window.IsVisibleChanged+=delegate{if(!Window.IsVisible)SuspendBackground();else ResumeBackground();};
   Window.StateChanged+=delegate{if(Window.WindowState==WindowState.Minimized)SuspendBackground();else ResumeBackground();};
  }
  internal bool BackgroundSuspended {get{return backgroundSuspended;}}
  internal void AllowSessionEnd(){exitRequested=true;cancel=true;}
  internal void HideToTray(){Window.Hide();SuspendBackground();if(tray!=null)tray.NotifyHidden();}
  internal void RestoreFromTray(){
   if(!Window.IsEnabled){Window.Show();Window.Activate();return;}
   Window.Show();if(Window.WindowState==WindowState.Minimized)Window.WindowState=WindowState.Normal;ResumeBackground();Window.Activate();
  }
  void SuspendBackground(){
   if(backgroundSuspended||busy)return;
   backgroundSuspended=true;externalTimer.Stop();searchTimer.Stop();layoutTimer.Stop();SaveThumbnailSize();SaveSidebarWidths();
   PauseBrowser();
   var gallery=Find<ListBox>("Gallery");var scroll=VisualChild<ScrollViewer>(gallery);suspendedOffset=scroll==null?0:scroll.VerticalOffset;
   // Release only the realised image controls and their bitmaps. Keep metadata,
   // selection, scroll position, inspector edits and original files unchanged.
   gallery.ItemsSource=null;galleryRows.Clear();Find<ThumbnailImage>("DetailPreview").DataContext=null;Find<BookmarkIcon>("DetailSiteIcon").DataContext=null;UpdatePalette(null);
  }
  void ResumeBackground(){
   if(!backgroundSuspended||!Window.IsVisible||Window.WindowState==WindowState.Minimized)return;
   backgroundSuspended=false;if(browserMode){ResumeBrowser();externalTimer.Start();return;}if(boardMode){boardPage.Refresh();externalTimer.Start();return;}if(referenceMode){referencePage.Refresh();externalTimer.Start();return;}ReloadFolders();Refresh(false);
   // A pending inspector draft postpones metadata refresh, but suspension has
   // released the gallery controls. Rebind the already-visible assets without
   // loading new metadata or replacing the user's unsaved edits.
   if(refreshDeferred)BuildRows();UpdateInspectorPreview();externalTimer.Start();
   Window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(delegate{var scroll=VisualChild<ScrollViewer>(Find<ListBox>("Gallery"));if(scroll!=null)scroll.ScrollToVerticalOffset(suspendedOffset);}));
  }
  internal void ExitApplication(){
   RestoreFromTray();if(busy){Status("正在完成当前操作，请完成后再退出。");return;}
   if(!PrepareLibraryChange())return;CloseForReplacement();
  }
  public void CloseForReplacement(){exitRequested=true;Window.Close();if(Window.IsVisible)exitRequested=false;}
 }
}
