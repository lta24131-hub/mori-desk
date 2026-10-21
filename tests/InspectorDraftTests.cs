using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Mori;

namespace MoriDesk {
 internal static class InspectorDraftTests {
  const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
  static object Field(MainWindow main,string name){return typeof(MainWindow).GetField(name,Flags).GetValue(main);}
  static void Call(MainWindow main,string name,params object[] args){typeof(MainWindow).GetMethod(name,Flags).Invoke(main,args);}
  static TextBox Text(MainWindow main,string name){return (TextBox)main.Window.FindName(name);}
  static void Select(MainWindow main,params string[] ids){var selected=(HashSet<string>)Field(main,"selection");selected.Clear();foreach(string id in ids)selected.Add(id);Call(main,"UpdateInspector");}
  static List<Asset> Visible(MainWindow main){return (List<Asset>)Field(main,"visible");}
  sealed class CloseTray:ITrayHost {public int Hidden;public bool Disposed;public void NotifyHidden(){Hidden++;}public void NotifyMessage(string message){}public void Dispose(){Disposed=true;}}
  public static void Run(string root,Action<bool,string> check){
   using(var lib=new Library(Path.Combine(root,"draft-library"))){
    string id=lib.Import(Path.Combine(root,"reference-photo.png"),"","draft fixture","","original","old note").Item.Id;
    var main=new MainWindow(lib){PreviewMode=true};int prompts=0;main.PendingInformationChoice=()=>{prompts++;return MessageBoxResult.Cancel;};
    try{
     Select(main,id);Text(main,"Notes").Text="new draft";Text(main,"Search").Text="unmatched-search";Call(main,"Refresh",false);
     check(Text(main,"Notes").Text=="new draft"&&Visible(main).Count==1&&((HashSet<string>)Field(main,"selection")).Contains(id),"search keeps pending inspector draft and selection");
     check(lib.All().Single().Notes=="old note"&&prompts==0&&(bool)Field(main,"refreshDeferred"),"passive search never auto-saves or prompts");
     Text(main,"Name").Text=" ";Call(main,"SaveMetadata");
     check(Text(main,"Notes").Text=="new draft"&&(bool)Field(main,"refreshDeferred")&&lib.All().Single().Notes=="old note","invalid name keeps draft and postponed refresh");
     Text(main,"Name").Text="draft fixture";Call(main,"SaveMetadata");
     check(lib.All().Single().Notes=="new draft"&&Visible(main).Count==0&&!(bool)Field(main,"refreshDeferred"),"explicit save persists draft before applying pending search");
     Text(main,"Search").Clear();Call(main,"Refresh",true);Select(main,id);Text(main,"Tags").Text="unsaved tags";
     ((ComboBox)main.Window.FindName("MediaFilter")).SelectedIndex=2;((ComboBox)main.Window.FindName("Sort")).SelectedIndex=2;
     check(Text(main,"Tags").Text=="unsaved tags"&&Visible(main).Count==1&&prompts==0,"sort and media filter preserve draft without repeated prompts");
     main.SetSection("pictures");check(prompts==1&&Text(main,"Tags").Text=="unsaved tags"&&(bool)Field(main,"refreshDeferred"),"cancel navigation keeps pending edits intact");
     main.PendingInformationChoice=()=>{prompts++;return MessageBoxResult.No;};main.SetSection("assets");
     check(lib.All().Single().Tags=="original"&&Text(main,"Tags").Text==""&&Visible(main).Count==1&&!(bool)Field(main,"refreshDeferred"),"explicit discard permits navigation and never saves unwanted tags");
     Select(main,id);Text(main,"Source").Text="https://example.org/draft";int before=((List<Asset>)Field(main,"all")).Count;
     using(var external=new Library(lib.Root))external.ImportBookmark("https://example.org/external","browser fixture","");
     Call(main,"Refresh",false);Call(main,"Refresh",false);
     check(Text(main,"Source").Text=="https://example.org/draft"&&((List<Asset>)Field(main,"all")).Count==before&&lib.All().Count==before+1,"external import is retained while inspector refresh waits");
     main.PendingInformationChoice=()=>MessageBoxResult.Yes;main.SetSection("pictures");
     check(lib.All().Single(a=>a.Id==id).Source=="https://example.org/draft"&&((List<Asset>)Field(main,"all")).Count==before+1&&!(bool)Field(main,"refreshDeferred"),"save before navigation catches up latest browser import");
     Select(main,id);Text(main,"Notes").Text="discard me";Text(main,"Search").Text="unmatched-again";Call(main,"Refresh",false);
     main.PendingInformationChoice=()=>MessageBoxResult.No;Call(main,"ClearGallerySelection");
     check(Visible(main).Count==0&&Text(main,"Notes").Text==""&&lib.All().Single(a=>a.Id==id).Notes=="new draft"&&!(bool)Field(main,"refreshDeferred"),"discard and deselect resumes pending filter without draft loss");
    }finally{main.Window.Close();ThumbnailImage.CurrentLibrary=null;}
   }
   DirectClose(root,check);
  }
  static void DirectClose(string root,Action<bool,string> check){
   string originalData=Environment.GetEnvironmentVariable("MORI_DESK_DATA"),oldTheme=Theme.Current;
   var application=Application.Current??new Application();var shutdown=application.ShutdownMode;application.ShutdownMode=ShutdownMode.OnExplicitShutdown;
   Environment.SetEnvironmentVariable("MORI_DESK_DATA",Path.Combine(root,"draft-close-config"));
   try{
    var settings=new Settings{CloseToTray=false};settings.SaveTo(Path.Combine(Settings.ConfigDir,"settings.json"));
    using(var library=new Library(Path.Combine(root,"draft-close-library"))){
     string id=library.Import(Path.Combine(root,"reference-photo.png"),"","close fixture","","","saved note").Item.Id;
     var main=new MainWindow(library){PreviewMode=true};main.DeferDeskServices();var tray=new CloseTray();main.AttachTray(tray);int closed=0,prompts=0;main.Window.Closed+=delegate{closed++;};
     try{
      Select(main,id);Text(main,"Notes").Text="keep this draft";main.PendingInformationChoice=()=>{prompts++;return MessageBoxResult.Cancel;};
      // Exercise WPF's actual close path shared by the title-bar X and Alt+F4,
      // not ExitApplication (which already had its own draft guard).
      main.Window.Close();
      check(closed==0&&prompts==1&&!tray.Disposed&&tray.Hidden==0&&Text(main,"Notes").Text=="keep this draft","direct close with close-to-tray disabled honors Cancel and retains the draft");
      check(library.All().Single().Notes=="saved note"&&((DispatcherTimer)Field(main,"externalTimer")).IsEnabled,"canceling direct close leaves saved data and synchronization unchanged");
      main.PendingInformationChoice=()=>{prompts++;return MessageBoxResult.Yes;};main.Window.Close();
      check(closed==1&&prompts==2&&tray.Disposed&&library.All().Single().Notes=="keep this draft","saving during direct close commits notes before closing the window and tray");
      check(!((DispatcherTimer)Field(main,"externalTimer")).IsEnabled,"accepted direct close stops synchronization only after the window closes");
     }finally{if(closed==0){main.PendingInformationChoice=()=>MessageBoxResult.No;main.CloseForReplacement();}}

     main=new MainWindow(library){PreviewMode=true};main.DeferDeskServices();closed=0;main.Window.Closed+=delegate{closed++;};
     try{
      Select(main,id);Text(main,"Name").Text=" ";Text(main,"Notes").Text="do not save";main.PendingInformationChoice=()=>MessageBoxResult.Yes;main.Window.Close();
      check(closed==0&&Text(main,"Notes").Text=="do not save"&&library.All().Single().Name=="close fixture","direct close cannot claim to save an invalid blank name or discard its draft");
      main.PendingInformationChoice=()=>MessageBoxResult.No;main.Window.Close();
      check(closed==1&&library.All().Single().Notes=="keep this draft","direct close without a tray allows an explicit discard without overwriting saved notes");
     }finally{if(closed==0){main.PendingInformationChoice=()=>MessageBoxResult.No;main.CloseForReplacement();}}

     settings.CloseToTray=true;settings.SaveTo(Path.Combine(Settings.ConfigDir,"settings.json"));
     main=new MainWindow(library){PreviewMode=true};main.DeferDeskServices();tray=new CloseTray();main.AttachTray(tray);closed=0;main.Window.Closed+=delegate{closed++;};
     try{
      Select(main,id);Text(main,"Notes").Text="tray draft";main.PendingInformationChoice=()=>{throw new Exception("hiding to tray must not prompt for metadata");};main.Window.Close();
      check(closed==0&&!tray.Disposed&&tray.Hidden==1&&Text(main,"Notes").Text=="tray draft"&&library.All().Single().Notes=="keep this draft","close-to-tray still preserves pending edits without prompting or saving");
     }finally{main.PendingInformationChoice=()=>MessageBoxResult.No;if(!tray.Disposed)main.CloseForReplacement();}
    }
   }finally{Environment.SetEnvironmentVariable("MORI_DESK_DATA",originalData);Theme.Set(oldTheme);ThumbnailImage.CurrentLibrary=null;application.ShutdownMode=shutdown;}
  }
 }
}
