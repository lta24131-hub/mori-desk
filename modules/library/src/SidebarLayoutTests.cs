using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;

namespace Mori {
 public static class SidebarLayoutTests {
  public static void Run(string output,Action<bool,string> check){
   Directory.CreateDirectory(output);
   foreach(int width in new[]{1024,1200,1380,1920})foreach(int l in new[]{160,208,480})foreach(int r in new[]{240,252,520}){
    var fit=SidebarLayout.Fit(width,l,r);
    check(fit[0]>=160&&fit[0]<=480&&fit[1]>=240&&fit[1]<=520&&width-fit[0]-fit[1]-12>=479.99,"sidebar bounds and center space: "+width+" / "+l+" / "+r);
   }
   string path=Path.Combine(output,"settings.json");
   new Settings{LibraryPath=output,Theme="light",ThumbnailSize=290,LeftSidebarWidth=330,RightSidebarWidth=380}.SaveTo(path);
   var settings=Settings.LoadFrom(path);check(settings.LeftSidebarWidth==330&&settings.RightSidebarWidth==380&&settings.Theme=="light"&&settings.ThumbnailSize==290&&settings.LibraryPath==output,"sidebar settings round trip preserves other preferences");
   File.WriteAllText(path,"{\"Theme\":\"dark\"}");settings=Settings.LoadFrom(path);check(settings.LeftSidebarWidth==208&&settings.RightSidebarWidth==252,"legacy settings retain original sidebar sizes");
   File.WriteAllText(path,"{\"LeftSidebarWidth\":\"bad\",\"RightSidebarWidth\":null,\"ThumbnailSize\":250}");settings=Settings.LoadFrom(path);check(settings.LeftSidebarWidth==208&&settings.RightSidebarWidth==252&&settings.ThumbnailSize==250,"invalid optional widths do not corrupt other settings");
   settings.LeftSidebarWidth=-1;settings.RightSidebarWidth=999999;check(settings.LeftSidebarWidth==160&&settings.RightSidebarWidth==520,"stored sidebar widths are bounded");
   var app=Application.Current??new Application();var old=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;Window window=null;
   try{
    using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Mori.Main.xaml"))window=(Window)XamlReader.Load(stream);
    var content=(FrameworkElement)window.Content;window.Content=null;
    var grid=(Grid)window.FindName("Workspace");var left=(GridSplitter)window.FindName("LeftSidebarHandle");var right=(GridSplitter)window.FindName("RightSidebarHandle");
    var controller=new SidebarLayout(grid,left,right,320,360);int changes=0;controller.Changed+=delegate{changes++;};
    foreach(string theme in new[]{"dark","light"}){
     Theme.Set(theme);content.Width=1380;content.Height=860;content.Measure(new Size(1380,860));content.Arrange(new Rect(0,0,1380,860));content.UpdateLayout();
     check(left.ActualWidth==6&&right.ActualWidth==6&&left.ActualHeight>600&&right.ActualHeight>600,theme+" both separator hit areas occupy full panel height");
     check(left.ResizeDirection==GridResizeDirection.Columns&&right.ResizeBehavior==GridResizeBehavior.PreviousAndNext&&!left.ShowsPreview&&left.Focusable,theme+" native splitters support live dragging and keyboard focus");
     check(Math.Abs(grid.ColumnDefinitions[0].ActualWidth-320)<1&&Math.Abs(grid.ColumnDefinitions[4].ActualWidth-360)<1,theme+" stored widths applied to actual template");
    }
    controller.SetPreferred(true,480);controller.SetPreferred(false,520);content.UpdateLayout();
    content.Width=1024;content.Measure(new Size(1024,860));content.Arrange(new Rect(0,0,1024,860));content.UpdateLayout();
    check(grid.ColumnDefinitions[2].ActualWidth>=479.99&&controller.Left==480&&controller.Right==520&&changes==2,"narrow window protects gallery without overwriting preferred widths");
    content.Width=1920;content.Measure(new Size(1920,860));content.Arrange(new Rect(0,0,1920,860));content.UpdateLayout();
    check(Math.Abs(grid.ColumnDefinitions[0].ActualWidth-480)<1&&Math.Abs(grid.ColumnDefinitions[4].ActualWidth-520)<1,"window expansion restores preferred sidebar widths");
    grid.ColumnDefinitions[0].Width=new GridLength(300);left.RaiseEvent(new DragCompletedEventArgs(0,0,false){RoutedEvent=Thumb.DragCompletedEvent});
    check(controller.Left==300&&controller.Right==520&&changes==3,"completed left drag commits current widths");
    grid.ColumnDefinitions[4].Width=new GridLength(350);right.RaiseEvent(new DragCompletedEventArgs(0,0,false){RoutedEvent=Thumb.DragCompletedEvent});
    check(controller.Left==300&&controller.Right==350&&changes==4,"completed right drag commits current widths");
    right.RaiseEvent(new DragCompletedEventArgs(0,0,true){RoutedEvent=Thumb.DragCompletedEvent});check(changes==4,"canceled drag does not save widths");
    check(!window.IsVisible,"sidebar regression tests do not send desktop input");
   }finally{if(window!=null)window.Close();Theme.Set("dark");app.ShutdownMode=old;}
  }
 }
}
