using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mori;

namespace MoriDesk {
 internal static class BrowserWorkspaceTests {
  const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
  static object Field(MainWindow main,string name){return typeof(MainWindow).GetField(name,Hidden).GetValue(main);}
  static void Call(MainWindow main,string name,params object[] args){typeof(MainWindow).GetMethod(name,Hidden).Invoke(main,args);}
  static TextBox Text(MainWindow main,string name){return (TextBox)main.Window.FindName(name);}
  public static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);bool classic=Entry.ClassicEnabled;string theme=Theme.Current;Entry.ClassicEnabled=true;
   using(var lib=new Library(Path.Combine(root,"library"))){
    var a=lib.ImportBookmark("https://example.invalid/design","Design","","reference","original note","reuse").Item;
    var main=new MainWindow(lib){PreviewMode=true};
    try{
     main.SetSection("bookmarks");Text(main,"Search").Text="Design";Call(main,"Refresh",true);
     var selection=(HashSet<string>)Field(main,"selection");selection.Add(a.Id);Call(main,"UpdateInspector");
     var workspace=(Grid)main.Window.FindName("Workspace");var center=(Grid)main.Window.FindName("Center");
     var searchParent=(UIElement)Text(main,"Search").Parent;var before=workspace.Children.Cast<UIElement>().ToDictionary(e=>e,e=>e.Visibility);
     string originalView=(string)Field(main,"view"),originalSection=(string)Field(main,"mediaSection");
     int windows=Application.Current.Windows.Count;
     check(!main.BrowseBookmark("javascript:alert(1)")&&!main.BrowserMode&&main.BrowserPane==null,"invalid URL cannot create a browser or change current collection");
     Text(main,"Notes").Text="unsaved draft";main.PendingInformationChoice=()=>MessageBoxResult.Cancel;
     check(!main.BrowseBookmark(a.Source)&&!main.BrowserMode&&main.BrowserPane==null&&Text(main,"Notes").Text=="unsaved draft","Cancel preserves metadata draft and prevents WebView construction");
     Text(main,"Notes").Text=a.Notes;main.PendingInformationChoice=null;
     check(main.BrowseBookmark(a.Source)&&main.BrowserMode&&main.BrowserPane!=null,"a bookmark opens one inline browsing pane instead of a second window");
     var first=main.BrowserPane;
     check(Application.Current.Windows.Count==windows&&!first.InitializationStarted&&!main.Window.IsVisible,"offscreen browser workspace never opens a window, initializes an engine or sends a web request");
     check(center.Visibility==Visibility.Collapsed&&searchParent.Visibility==Visibility.Collapsed,"browsing hides unrelated asset grid and asset-search input");
     foreach(string button in new[]{"Import","Paste","AddBookmark","Export"})check(((Button)main.Window.FindName(button)).Visibility==Visibility.Collapsed,"browsing hides unrelated "+button+" action");
     check(workspace.Children.Cast<UIElement>().Where(e=>Grid.GetColumn(e)<=1).All(e=>e.Visibility==before[e]),"browsing preserves current left-navigation presentation");
     check(main.BrowseBookmark("https://example.invalid/second")&&ReferenceEquals(first,main.BrowserPane)&&Application.Current.Windows.Count==windows,"another bookmark reuses the same single inline pane");
     new System.Windows.Interop.WindowInteropHelper(main.Window).EnsureHandle();
     var source=System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(main.Window).Handle);
     foreach(Key key in new[]{Key.Delete,Key.Space,Key.V,Key.A,Key.Z,Key.F5,Key.Escape}){
      var input=new KeyEventArgs(Keyboard.PrimaryDevice,source,0,key){RoutedEvent=Keyboard.PreviewKeyDownEvent};main.Window.RaiseEvent(input);
      check(main.BrowserMode&&selection.SetEquals(new[]{a.Id})&&lib.All().Count==1&&!lib.All()[0].Deleted,"webpage keyboard "+key+" cannot operate on hidden assets");
     }
     Call(main,"CollectBrowserBookmark","https://example.invalid/collected","New reference");
     check(lib.All().Count==2&&main.BrowserMode&&selection.SetEquals(new[]{a.Id}),"collecting a web page commits a bookmark without resetting hidden selection");
     Call(main,"Refresh",false);check(main.BrowserMode&&center.Visibility==Visibility.Collapsed&&Text(main,"Search").Text=="Design","external refresh leaves browsing mode and original collection query intact");
     Call(main,"PauseBrowser");check(!first.IsActiveForBrowsing&&!first.InitializationStarted,"tray pause deactivates the browser without creating an engine");
     Call(main,"ResumeBrowser");check(first.IsActiveForBrowsing&&!first.InitializationStarted,"tray resume preserves no-network preview protection");
     foreach(string appearance in new[]{"light","dark"}){Theme.Set(appearance);Render(main,Path.Combine(root,"browser-workspace-"+appearance+".png"));check(!first.InitializationStarted,"offscreen "+appearance+" workspace visual does not initialize WebView");}
     main.HideBrowser();
     check(!main.BrowserMode&&main.BrowserPane==null&&first.IsDisposed,"returning to collection disposes the inline engine and releases its host");
     check((string)Field(main,"view")==originalView&&(string)Field(main,"mediaSection")==originalSection&&Text(main,"Search").Text=="Design"&&selection.SetEquals(new[]{a.Id}),"return keeps collection, media scope, search and selection");
     check(center.Visibility==before[center]&&searchParent.Visibility==Visibility.Visible&&Text(main,"Notes").Text==a.Notes,"return restores the grid/search and original metadata");
     main.SetSection("assets");check(!main.BrowserMode,"navigation to another section cannot leave a browser active");
     main.SetSection("bookmarks");main.BrowseBookmark(a.Source);Call(main,"SuspendBackground");
     check(((ListBox)main.Window.FindName("Gallery")).ItemsSource==null,"tray suspension releases realised assets while a webpage is shown");
     typeof(MainWindow).GetField("backgroundSuspended",Hidden).SetValue(main,false);Call(main,"ResumeBrowser");main.HideBrowser();
     check(((ListBox)main.Window.FindName("Gallery")).ItemsSource!=null&&((ListBox)main.Window.FindName("Gallery")).Items.Count>0,"return after a browser tray cycle rebinds assets instead of showing an empty library");
     main.BrowseBookmark(a.Source);var last=main.BrowserPane;main.SetSection("bookmarks");check(!main.BrowserMode&&last.IsDisposed,"sidebar section switch disposes an active browser");
     var toolNames=main.CreateToolMenu().Items.OfType<MenuItem>().Select(i=>Convert.ToString(i.Header)).ToArray();
     check(toolNames.Contains("自由画布…")&&toolNames.Contains("快速浏览网址…"),"canvas and generic browsing remain low-profile tools-menu entries");
     var menu=main.CreateAssetMenu(a);check(menu.Items.OfType<MenuItem>().Any(i=>Convert.ToString(i.Header)=="快速浏览")&&menu.Items.OfType<MenuItem>().Any(i=>Convert.ToString(i.Header)=="用外部浏览器打开"),"bookmark context menu clearly offers inline and external browsing");
    }finally{main.HideBrowser();main.CloseForReplacement();ThumbnailImage.CurrentLibrary=null;Entry.ClassicEnabled=classic;Theme.Set(theme);}
   }
  }
  static void Render(MainWindow main,string path){
   var body=(FrameworkElement)main.Window.Content;body.Measure(new Size(1280,760));body.Arrange(new Rect(0,0,1280,760));body.UpdateLayout();
   var image=new RenderTargetBitmap(1280,760,96,96,PixelFormats.Pbgra32);image.Render(body);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var output=File.Create(path))encoder.Save(output);
  }
 }
}
