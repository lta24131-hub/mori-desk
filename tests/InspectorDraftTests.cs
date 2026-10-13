using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Mori;

namespace MoriDesk {
 internal static class InspectorDraftTests {
  const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
  static object Field(MainWindow main,string name){return typeof(MainWindow).GetField(name,Flags).GetValue(main);}
  static void Call(MainWindow main,string name,params object[] args){typeof(MainWindow).GetMethod(name,Flags).Invoke(main,args);}
  static TextBox Text(MainWindow main,string name){return (TextBox)main.Window.FindName(name);}
  static void Select(MainWindow main,params string[] ids){var selected=(HashSet<string>)Field(main,"selection");selected.Clear();foreach(string id in ids)selected.Add(id);Call(main,"UpdateInspector");}
  static List<Asset> Visible(MainWindow main){return (List<Asset>)Field(main,"visible");}
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
  }
 }
}
