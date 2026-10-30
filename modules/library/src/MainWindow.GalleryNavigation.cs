using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Mori {
 public partial class MainWindow {
  ScrollViewer galleryNavigationScroll;
  void InitializeGalleryNavigation(){
   var gallery=Find<ListBox>("Gallery");var button=B("GalleryBackToTop");
   // WPF Track uses half this metric for its minimum thumb length. Scope it
   // to this gallery so a large collection still has a 44-DIP drag target.
   gallery.Resources[SystemParameters.VerticalScrollBarButtonHeightKey]=88.0;
   gallery.Loaded+=delegate{galleryNavigationScroll=VisualChild<ScrollViewer>(gallery);UpdateGalleryNavigation();};
   gallery.Unloaded+=delegate{galleryNavigationScroll=null;button.Visibility=Visibility.Collapsed;};
   gallery.IsVisibleChanged+=delegate{UpdateGalleryNavigation();};
   gallery.AddHandler(ScrollViewer.ScrollChangedEvent,new ScrollChangedEventHandler(delegate(object sender,ScrollChangedEventArgs e){
    if(galleryNavigationScroll==null)galleryNavigationScroll=VisualChild<ScrollViewer>(gallery);
    if(ReferenceEquals(e.OriginalSource,galleryNavigationScroll))UpdateGalleryNavigation();
   }));
   button.Click+=delegate{ReturnGalleryToTop();};
  }
  void UpdateGalleryNavigation(){
   var scroll=galleryNavigationScroll;
   bool show=Find<ListBox>("Gallery").IsVisible&&scroll!=null&&scroll.ScrollableHeight>0&&scroll.VerticalOffset>=Math.Max(160,scroll.ViewportHeight);
   B("GalleryBackToTop").Visibility=show?Visibility.Visible:Visibility.Collapsed;
  }
  internal void ReturnGalleryToTop(){
   var gallery=Find<ListBox>("Gallery");var scroll=galleryNavigationScroll??VisualChild<ScrollViewer>(gallery);
   if(scroll==null||!gallery.IsVisible)return;
   gallery.Focus();scroll.ScrollToTop();
  }
 }
}
