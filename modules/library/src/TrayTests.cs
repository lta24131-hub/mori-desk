using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Mori {
 public static class TrayTests {
  sealed class FakeTray:ITrayHost {public int Hidden;public bool Disposed;public void NotifyHidden(){Hidden++;}public void Dispose(){Disposed=true;}}
  static object Field(MainWindow main,string name){return typeof(MainWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(main);}
  static void Invoke(MainWindow main,string method,params object[] args){typeof(MainWindow).GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic,null,args.Select(a=>a.GetType()).ToArray(),null).Invoke(main,args);}
  static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
  public static void Run(string output,Action<bool,string> check){
   Directory.CreateDirectory(output);var app=Application.Current??new Application();var mode=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
   try{using(var lib=new Library(Path.Combine(output,"library"))){
    var a=lib.ImportBookmark("https://example.com/","测试网址","").Item;var main=new MainWindow(lib){PreviewMode=true};var tray=new FakeTray();main.AttachTray(tray);
    try{
     main.Window.ShowActivated=false;main.Window.WindowStartupLocation=WindowStartupLocation.Manual;main.Window.Left=-20000;main.Window.Top=-20000;main.Window.Show();Pump();
     check(((List<Asset>)Field(main,"visible")).Count==0,"ordinary image library excludes bookmarks");main.SetBookmarkMode(true);Pump();check(((List<Asset>)Field(main,"visible")).Single().Id==a.Id,"independent bookmark area displays bookmarks");
     ((HashSet<string>)Field(main,"selection")).Add(a.Id);Invoke(main,"UpdateInspector");var notes=(TextBox)main.Window.FindName("Notes");notes.Text="尚未保存的笔记";
     main.Window.Close();Pump();check(!main.Window.IsVisible&&!tray.Disposed&&tray.Hidden==1,"window close hides to tray rather than exiting");
     check(main.BackgroundSuspended&&((ListBox)main.Window.FindName("Gallery")).ItemsSource==null&&!((DispatcherTimer)Field(main,"externalTimer")).IsEnabled,"tray suspension releases gallery controls and stops polling");
     check(((BookmarkIcon)main.Window.FindName("DetailSiteIcon")).DataContext==null,"tray suspension also releases inspector icon");
     lib.ImportBookmark("https://example.org/","后台收藏","");main.RestoreFromTray();Pump();
     check(main.Window.IsVisible&&!main.BackgroundSuspended&&((List<Asset>)Field(main,"visible")).Count==2,"tray restore refreshes browser imports");
     check(notes.Text=="尚未保存的笔记"&&((HashSet<string>)Field(main,"selection")).Contains(a.Id),"tray restore preserves pending notes and selection");
     check(((BookmarkIcon)main.Window.FindName("DetailSiteIcon")).DataContext is Asset,"tray restore rebinds selected inspector without replacing edits");
     notes.Text="";main.CloseForReplacement();Pump();check(tray.Disposed&&!main.Window.IsVisible,"replacement or explicit exit really closes and disposes tray");
    }finally{if(!tray.Disposed)main.CloseForReplacement();}
   }
   string eventName="Local\\Mori-test-"+Guid.NewGuid().ToString("N");using(var handle=new EventWaitHandle(false,EventResetMode.AutoReset,eventName)){check(TrayHost.ActivateExisting(eventName)&&handle.WaitOne(100),"second launch activation uses named event without polling");}check(!TrayHost.ActivateExisting(eventName),"missing activation event fails safely");
   string png=Path.Combine(output,"decode.png");using(var bitmap=new System.Drawing.Bitmap(8,6))bitmap.Save(png,System.Drawing.Imaging.ImageFormat.Png);var decoded=ThumbnailImage.ReadThumbnail(png);check(decoded.IsFrozen&&decoded.PixelWidth==8&&decoded.PixelHeight==6,"thumbnail is fully decoded/frozen off UI thread");using(var unlocked=new FileStream(png,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(unlocked.Length>0,"decoded thumbnail releases file handle immediately");
   }finally{app.ShutdownMode=mode;}
  }
 }
}
