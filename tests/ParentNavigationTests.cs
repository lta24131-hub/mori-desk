using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Mori;
namespace MoriDesk {
 internal static class ParentNavigationTests {
  const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
  static object Field(MainWindow main,string name){return typeof(MainWindow).GetField(name,Flags).GetValue(main);}
  static void Set(MainWindow main,string name,object value){typeof(MainWindow).GetField(name,Flags).SetValue(main,value);}
  static void Call(MainWindow main,string name){typeof(MainWindow).GetMethod(name,Flags).Invoke(main,null);}
  internal static void Run(string root,Action<bool,string> check){
   bool classic=Entry.ClassicEnabled,board=Entry.BoardEnabled;var oldLibrary=ThumbnailImage.CurrentLibrary;var app=Application.Current;var oldMain=app.MainWindow;var shutdown=app.ShutdownMode;
   Entry.ClassicEnabled=true;Entry.BoardEnabled=false;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
   try{using(var lib=new Library(root)){
    lib.EnableReferenceAssets();string a=lib.CreateFolder("一级网址","",true),b=lib.CreateFolder("二级网址",a,true),c=lib.CreateFolder("三级网址",b,true);
    string media=lib.CreateFolder("一级素材",""),mediaChild=lib.CreateFolder("二级素材",media),local=lib.CreateLocalCollection("本地项目");
    string id=lib.ImportBookmark("https://example.org/navigation","导航测试",c).Item.Id;
    var main=new MainWindow(lib){PreviewMode=true};main.DeferDeskServices();var up=(Button)main.Window.FindName("GalleryUp");
    try{
     main.SetBookmarkMode(true);check(up.Visibility==Visibility.Visible&&!up.IsEnabled&&!main.NavigateUp(),"website root shows disabled parent control and cannot loop");
     main.RequestNavigate(c);check(up.IsEnabled,"deep website folder exposes parent navigation");up.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));check((string)Field(main,"view")==b,"parent button returns exactly one level, not website root");
     var source=HwndSource.FromHwnd(new WindowInteropHelper(main.Window).EnsureHandle());
     var key=new KeyEventArgs(Keyboard.PrimaryDevice,source,0,Key.Up){RoutedEvent=Keyboard.PreviewKeyDownEvent};check(main.HandleParentNavigationKey(key,ModifierKeys.Alt)&&key.Handled&&(string)Field(main,"view")==a,"Alt+Up follows the same hierarchy");
     check(!main.HandleParentNavigationKey(new KeyEventArgs(Keyboard.PrimaryDevice,source,0,Key.Back),ModifierKeys.None)&&(string)Field(main,"view")==a,"Backspace is not commandeered for website navigation");
     main.NavigateUp();check((string)Field(main,"view")=="home"&&!up.IsEnabled,"top-level website folder returns to folder overview");
     main.RequestNavigate(c);Set(main,"busy",true);check(!main.NavigateUp()&&(string)Field(main,"view")==c,"active import blocks parent navigation");Set(main,"busy",false);
     ((HashSet<string>)Field(main,"selection")).Add(id);Call(main,"UpdateInspector");((TextBox)main.Window.FindName("Notes")).Text="未保存备注";main.PendingInformationChoice=()=>MessageBoxResult.Cancel;
     check(!main.NavigateUp()&&(string)Field(main,"view")==c&&((TextBox)main.Window.FindName("Notes")).Text=="未保存备注","cancelled draft prompt keeps hierarchy and pending edits");
     main.PendingInformationChoice=()=>MessageBoxResult.Yes;check(main.NavigateUp()&&lib.All().Single(x=>x.Id==id).Notes=="未保存备注","parent navigation saves an accepted metadata draft before leaving");
     main.SetSection("assets");main.RequestNavigate(mediaChild);check(up.IsEnabled&&main.NavigateUp()&&(string)Field(main,"view")==media,"material subcategory returns to its own parent");
     main.NavigateUp();check((string)Field(main,"view")=="all"&&(string)Field(main,"mediaSection")=="assets"&&up.Visibility==Visibility.Collapsed,"top material category restores all-material visibility preferences without phantom parent");
     main.SetSection("shortcuts");check(!up.IsEnabled,"local collection root cannot go above its root");main.OpenLocalCollection(local);check(up.IsEnabled&&main.NavigateUp()&&(string)Field(main,"localCollection")=="","local collection uses the same parent control");
     main.ShowCanvasProjects();check(!main.NavigateUp(),"project overview is not treated as a material subfolder");main.SetSection("assets");
     var broken=new[]{new Category{Id="a",Parent="missing"},new Category{Id="b",Parent="b"},new Category{Id="c",Parent="d"},new Category{Id="d",Parent="c"}};
     check(broken.All(x=>MainWindow.ParentNavigationTarget(broken,x.Id,"home")=="home"),"missing and cyclic parents recover to root rather than trapping navigation");
     check(lib.All().Count==1&&lib.Folders(true).Count==3&&lib.Folders(false).Count==2&&lib.LocalCollections().Count==1,"navigation changes no assets or folder memberships");
    }finally{foreach(string name in new[]{"externalTimer","searchTimer","layoutTimer","sizeSaveTimer","sidebarSaveTimer"})((DispatcherTimer)Field(main,name)).Stop();Set(main,"exitRequested",true);main.Window.Close();}
   }}finally{Entry.ClassicEnabled=classic;Entry.BoardEnabled=board;ThumbnailImage.CurrentLibrary=oldLibrary;app.MainWindow=oldMain;app.ShutdownMode=shutdown;}
  }
 }
}
