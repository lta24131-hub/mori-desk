using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Mori;
namespace MoriDesk {
 internal static class GalleryNavigationTests {
  const BindingFlags Hidden=BindingFlags.NonPublic|BindingFlags.Instance;
  static T Field<T>(MainWindow main,string key){return (T)typeof(MainWindow).GetField(key,Hidden).GetValue(main);}
  static IEnumerable<T> Children<T>(DependencyObject parent)where T:DependencyObject{for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T)yield return (T)child;foreach(var nested in Children<T>(child))yield return nested;}}
  static void Pump(){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(80)};timer.Tick+=delegate{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
  static void Stop(MainWindow main){foreach(var key in new[]{"externalTimer","searchTimer","layoutTimer","sizeSaveTimer","sidebarSaveTimer"})Field<DispatcherTimer>(main,key).Stop();}
  internal static void Run(string root,Action<bool,string> check){
   var oldLibrary=ThumbnailImage.CurrentLibrary;string oldTheme=Theme.Current;var app=Application.Current;var oldMain=app.MainWindow;var oldShutdown=app.ShutdownMode;bool classic=Entry.ClassicEnabled;Entry.ClassicEnabled=true;
   try{using(var lib=new Library(root)){
    lib.Db.Exec("BEGIN");for(int i=0;i<2000;i++)lib.Db.Exec("INSERT INTO assets(id,hash,name,path,ext,bytes,added) VALUES('g"+i+"','h"+i+"','Gallery "+i+"','missing"+i+".png','.png',128,'2026-10-13T00:00:00Z')");lib.Db.Exec("COMMIT");
    var main=new MainWindow(lib){PreviewMode=true};main.DeferDeskServices();main.Window.Width=1000;main.Window.Height=680;main.Window.WindowStartupLocation=WindowStartupLocation.Manual;main.Window.Left=0;main.Window.Top=0;
    try{main.Window.Show();Pump();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;Stop(main);var list=(ListBox)main.Window.FindName("Gallery");var top=(Button)main.Window.FindName("GalleryBackToTop");var scroll=Children<ScrollViewer>(list).First();
     foreach(string theme in new[]{"dark","light"}){
      Theme.Set(theme);scroll.ScrollToTop();Pump();check(top.Visibility==Visibility.Collapsed,"back-to-top stays unobtrusive at the beginning / "+theme);
      scroll.ScrollToEnd();Pump();Stop(main);check(scroll.ScrollableHeight>10000&&scroll.VerticalOffset>=scroll.ScrollableHeight-1,"large virtualized gallery reaches the final row / "+theme);
      check(top.Visibility==Visibility.Visible&&top.ActualWidth>=40&&top.ActualHeight>=40,"long scroll exposes a clear return-to-top button / "+theme);
      var bar=Children<ScrollBar>(list).First(b=>b.Orientation==Orientation.Vertical);bar.ApplyTemplate();var track=Children<Track>(bar).First();
      check(bar.ActualWidth>=16&&track.Thumb.ActualHeight>=43.5,"large collection retains a 16-wide rail and at least 44-high thumb / "+theme);
      double delta=track.ValueFromDistance(0,-track.ActualHeight);check(!double.IsNaN(delta)&&delta<0&&Math.Abs(delta)>=bar.Maximum,"drag math spans the full list despite minimum thumb size / "+theme);
      Field<HashSet<string>>(main,"selection").Add("g10");main.ReturnGalleryToTop();Pump();
      check(scroll.VerticalOffset<1&&top.Visibility==Visibility.Collapsed&&Field<HashSet<string>>(main,"selection").Contains("g10"),"return-to-top changes only scroll position, preserving selected assets / "+theme);
      Field<HashSet<string>>(main,"selection").Clear();
      check(Children<ListBoxItem>(list).Count()<80,"scrolling does not materialize every gallery row / "+theme);
     }
     scroll.ScrollToEnd();Pump();var search=(TextBox)main.Window.FindName("Search");search.Text="Gallery 1999";main.RefreshView();Pump();Stop(main);
     check(Field<List<Asset>>(main,"visible").Count==1&&scroll.VerticalOffset<1&&top.Visibility==Visibility.Collapsed,"search from the bottom opens its short result at the top");
    }finally{Stop(main);typeof(MainWindow).GetField("exitRequested",Hidden).SetValue(main,true);main.Window.Close();Pump();}
   }}finally{ThumbnailImage.CurrentLibrary=oldLibrary;Entry.ClassicEnabled=classic;Theme.Set(oldTheme);app.MainWindow=oldMain;app.ShutdownMode=oldShutdown;}
  }
 }
}
