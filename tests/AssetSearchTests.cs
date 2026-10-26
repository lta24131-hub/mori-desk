using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using MoriDesk;

namespace Mori {
 internal static class AssetSearchTests {
  const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
  internal static void Run(string root,Action<bool,string> check){Pure(check);Integration(root,check);}
  static void Pure(Action<bool,string> check){
   var item=new Asset{Name="红色 Building 01.png",Tags="建筑 城市 concept",Notes="Blue sky above the river",Source="https://example.org/作品?id=42&size=large",ReferencePath=@"D:\My Files\项目A\Building 01.png"};
   check(AssetSearch.Parse("红色").Matches(item),"single Chinese keyword preserves substring search");
   check(AssetSearch.Parse("building").Matches(item),"English matching is case insensitive");
   check(AssetSearch.Parse("红色 建筑 river example.org 项目A").Matches(item),"all search words can match across name tags notes source and local path");
   check(!AssetSearch.Parse("红色 不存在").Matches(item),"every ordinary search word is required rather than OR matching");
   check(AssetSearch.Parse(" \t红色\r\n 建筑\u3000river\u00a0example.org  ").Matches(item),"repeated ASCII and Unicode whitespace separates ordinary search words");
   check(AssetSearch.Parse("BUILDING building Building").Matches(item),"duplicate case-insensitive terms do not change matching");
   check(AssetSearch.Parse("\"Blue sky\" 建筑").Matches(item),"quoted phrase keeps its order and can combine with another field");
   check(!AssetSearch.Parse("\"sky Blue\"").Matches(item),"quoted phrase cannot match reversed words");
   check(!AssetSearch.Parse("\"红色 建筑\"").Matches(item),"quoted phrase cannot combine text from separate metadata fields");
   check(!AssetSearch.Parse("\"Blue  sky\"").Matches(item),"quoted phrase retains literal internal whitespace");
   check(AssetSearch.Parse("\"Blue sky").Matches(item),"unfinished quote remains searchable without an exception");
   check(AssetSearch.Parse("\"D:\\My Files\\项目A\\Building 01.png\"").Matches(item),"quoted Windows path preserves backslashes and spaces");
   check(AssetSearch.Parse(@"D:\My 项目A").Matches(item),"unquoted local path words can match in all-material metadata");
   check(AssetSearch.Parse("https://example.org/作品?id=42&size=large").Matches(item),"URL punctuation stays literal");
   check(AssetSearch.Parse("id=42&size=large").Matches(item),"source URL substring remains searchable");
   var punctuation=new Asset{Name="a+b -draft OR tag:red (final).png",ReferencePath=@"\\offline-server\share\sample.png"};
   check(AssetSearch.Parse("a+b -draft OR tag:red (final)").Matches(punctuation),"punctuation and operator-like words have no hidden query semantics");
   check(AssetSearch.Parse(@"\\offline-server\share").Matches(punctuation),"offline UNC address is matched as metadata without touching the disk");
   var emptyFields=new Asset();
   foreach(string query in new[]{null,""," \t\r\n\u3000","\"\"","\" \" \"\""}){
    var parsed=AssetSearch.Parse(query);
    check(parsed.IsEmpty&&parsed.Matches(emptyFields),"empty or empty-quoted query does not filter assets: "+(query??"<null>"));
   }
   check(!AssetSearch.Parse("something").Matches(emptyFields),"null metadata fields safely fail a nonempty search");
   check(!AssetSearch.Parse("").Matches(null)&&!AssetSearch.Parse("x").Matches(null),"null asset is never a search result");
   var fields=new[]{new Asset{Name="needle"},new Asset{Tags="needle"},new Asset{Notes="needle"},new Asset{Source="needle"},new Asset{ReferencePath="needle"}};
   var sameQuery=AssetSearch.Parse("NEEDLE");
   for(int i=0;i<fields.Length;i++)check(sameQuery.Matches(fields[i]),"each searchable field independently matches reusable query "+i);
   check(!AssetSearch.Parse("needle").Matches(new Asset{Name="nee",Tags="dle"}),"single word cannot accidentally straddle fields");
   var culture=Thread.CurrentThread.CurrentCulture;
   try{Thread.CurrentThread.CurrentCulture=new CultureInfo("tr-TR");check(AssetSearch.Parse("BUILDING").Matches(item),"matching stays ordinal and predictable under Turkish culture");}
   finally{Thread.CurrentThread.CurrentCulture=culture;}
   check(item.Name=="红色 Building 01.png"&&item.Tags=="建筑 城市 concept"&&item.Notes=="Blue sky above the river"&&item.Source=="https://example.org/作品?id=42&size=large"&&item.ReferencePath==@"D:\My Files\项目A\Building 01.png","search never changes original metadata");
  }
  static object Field(MainWindow main,string name){return typeof(MainWindow).GetField(name,Flags).GetValue(main);}
  static TextBox Text(MainWindow main,string name){return (TextBox)main.Window.FindName(name);}
  static HashSet<string> Visible(MainWindow main){return new HashSet<string>(((List<Asset>)Field(main,"visible")).Select(a=>a.Id));}
  static void Search(MainWindow main,string value){Text(main,"Search").Text=value;main.RefreshView();}
  static void Select(MainWindow main,string id){var selected=(HashSet<string>)Field(main,"selection");selected.Clear();selected.Add(id);typeof(MainWindow).GetMethod("UpdateInspector",Flags).Invoke(main,null);}
  static string Fixture(string directory,string name){string path=Path.Combine(directory,name);File.WriteAllText(path,"inert search metadata fixture: "+name);return path;}
  static void Integration(string root,Action<bool,string> check){
   // Synthetic metadata files only. Never Show the WPF window, so no thumbnail
   // decoder, screenshot service, browser listener or real desktop is started.
   string originalData=Environment.GetEnvironmentVariable("MORI_DESK_DATA"),oldTheme=Theme.Current;
   bool classic=Entry.ClassicEnabled,board=Entry.BoardEnabled,interactive=Entry.InteractiveShell,toolsStarted=DeskRuntime.Started;
   var oldLibrary=ThumbnailImage.CurrentLibrary;var app=Application.Current??new Application();var shutdown=app.ShutdownMode;
   Environment.SetEnvironmentVariable("MORI_DESK_DATA",Path.Combine(root,"search-config"));
   Entry.ClassicEnabled=true;Entry.BoardEnabled=false;Entry.InteractiveShell=false;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
   try{
    new Settings{CloseToTray=false}.Save();
    string originals=Path.Combine(root,"目录标记 search originals");Directory.CreateDirectory(originals);
    using(var library=new Library(Path.Combine(root,"search-library"))){
     library.EnableReferenceAssets();string category=library.CreateFolder("搜索项目","");
     string referencePath=Fixture(originals,"local.png"),hiddenPath=Fixture(originals,"hidden.png");
     string referenceId=library.AddReferenceAssets(new[]{referencePath},category).Single(),hiddenId=library.AddReferenceAssets(new[]{hiddenPath},category).Single();
     var reference=library.All().Single(a=>a.Id==referenceId);reference.Name="红色 LocalPreview";reference.Tags="建筑";reference.Notes="saved note";library.Update(reference);
     var hidden=library.All().Single(a=>a.Id==hiddenId);hidden.Name="红色 HiddenPreview";library.Update(hidden);library.SetHiddenFromAll(new[]{hiddenId},true);
     string localGroup=library.CreateLocalCollection("本地分组");library.MoveToLocalCollection(new[]{hiddenId},localGroup);
     var outside=library.Import(Fixture(originals,"outside.png"),"","红色 Outside","","","").Item;
     var video=library.Import(Fixture(originals,"video.mp4"),category,"红色 Video","","","").Item;
     library.Import(Fixture(originals,"document.pdf"),"","红色 Document","","","");var rootBookmark=library.ImportBookmark("https://example.org/search-fixture","红色 Website","").Item;
     string bookmarkFolder=library.CreateFolder("网址分组","",true),bookmarkChild=library.CreateFolder("网址子分组",bookmarkFolder,true);
     var folderBookmark=library.ImportBookmark("https://example.org/search-folder","红色 Folder Website",bookmarkFolder).Item;
     var nestedBookmark=library.ImportBookmark("https://example.org/search-nested","红色 Nested Website",bookmarkChild).Item;
     var trashed=library.Import(Fixture(originals,"trashed.png"),"","红色 Trash","","","").Item;library.Trash(new[]{trashed.Id},true);
     var members=library.Members(category,false);var overview=new HashSet<string>(new[]{referenceId,outside.Id,video.Id});int records=library.All().Count;
     var main=new MainWindow(library){PreviewMode=true};main.DeferDeskServices();int prompts=0;main.PendingInformationChoice=()=>{prompts++;return MessageBoxResult.Cancel;};
     try{
      main.SetSection("assets");Search(main,"目录标记 红色");
      check(Visible(main).SetEquals(new[]{referenceId})&&(string)Field(main,"mediaSection")=="assets","actual overview search ANDs local path with name and does not reveal hidden matches");
      Search(main," \t目录标记\u3000 红色\r\n ");
      check(Visible(main).SetEquals(new[]{referenceId}),"actual search refresh handles mixed repeated Unicode whitespace");
      Search(main,"");
      check(Visible(main).SetEquals(overview)&&library.All().Single(a=>a.Id==hiddenId).HiddenFromAll,"clearing overview search restores its original scope without showing hidden documents websites or trash");
      main.RequestNavigate(category);var filter=(ComboBox)main.Window.FindName("MediaFilter");filter.SelectedIndex=1;Search(main,"红色");
      check(Visible(main).SetEquals(new[]{referenceId,hiddenId})&&(string)Field(main,"view")==category,"search stays inside category and picture filter while category may still show overview-hidden items");
      Search(main," \t\u3000 ");
      check(Visible(main).SetEquals(new[]{referenceId,hiddenId})&&filter.SelectedIndex==1&&(string)Field(main,"view")==category&&library.Members(category,false).SetEquals(members)&&library.All().Single(a=>a.Id==hiddenId).HiddenFromAll,"empty search preserves category media filter membership and hidden preference");
      Select(main,referenceId);Text(main,"Notes").Text="pending search draft";Search(main,"不存在 missing");
      check(Visible(main).SetEquals(new[]{referenceId,hiddenId})&&Text(main,"Notes").Text=="pending search draft"&&((HashSet<string>)Field(main,"selection")).SetEquals(new[]{referenceId})&&(bool)Field(main,"refreshDeferred")&&prompts==0&&library.All().Single(a=>a.Id==referenceId).Notes=="saved note","passive search preserves unsaved draft selection and results without prompting or saving");
      main.SetSection("assets");
      check(prompts==1&&(string)Field(main,"view")==category&&filter.SelectedIndex==1&&Text(main,"Search").Text=="不存在 missing"&&Text(main,"Notes").Text=="pending search draft","canceling navigation cannot bypass draft protection or silently reset pending search and filter");
      main.PendingInformationChoice=()=>MessageBoxResult.No;main.SetSection("assets");
      check(Visible(main).SetEquals(overview)&&library.All().Single(a=>a.Id==referenceId).Notes=="saved note"&&library.Members(category,false).SetEquals(members)&&library.All().Single(a=>a.Id==hiddenId).HiddenFromAll&&library.All().Count==records&&library.Db.Query("PRAGMA foreign_key_check").Count==0&&!main.Window.IsVisible&&DeskRuntime.Started==toolsStarted,"explicit discard resumes overview with all source records classifications and preferences intact without displaying a window or starting services");
      main.SetSection("bookmarks");Search(main,"红色");
      check(Visible(main).SetEquals(new[]{rootBookmark.Id,folderBookmark.Id,nestedBookmark.Id}),"nonempty bookmark home search still includes classified website matches");
      foreach(string emptyQuery in new[]{"\"\"","\" \" \"\""}){
       Search(main,emptyQuery);
       check(Visible(main).SetEquals(new[]{rootBookmark.Id})&&(string)Field(main,"view")=="home"&&((WrapPanel)main.Window.FindName("BookmarkFolderList")).Children.Count==1&&((TextBlock)main.Window.FindName("BookmarkItemsHeading")).Text.StartsWith("未分类网址",StringComparison.Ordinal),"empty-quoted bookmark home search keeps its unfiled browsing scope and folder cards: "+emptyQuery);
      }
      main.RequestNavigate(bookmarkFolder);Search(main,"红色");
      check(Visible(main).SetEquals(new[]{folderBookmark.Id,nestedBookmark.Id}),"nonempty bookmark category search still includes child-folder matches");
      Search(main,"\"\"");
      check(Visible(main).SetEquals(new[]{folderBookmark.Id})&&(string)Field(main,"view")==bookmarkFolder&&((WrapPanel)main.Window.FindName("BookmarkFolderList")).Children.Count==1&&((TextBlock)main.Window.FindName("BookmarkItemsHeading")).Text.StartsWith("本层网址",StringComparison.Ordinal),"empty-quoted bookmark category search keeps child-folder cards without expanding into them");
      main.SetSection("shortcuts");Search(main,"红色");
      check(Visible(main).SetEquals(new[]{referenceId,hiddenId}),"nonempty local root search still finds shortcuts inside classification folders");
      foreach(string emptyQuery in new[]{"\"\"","\" \" \"\""}){
       Search(main,emptyQuery);
       check(Visible(main).SetEquals(new[]{referenceId})&&((WrapPanel)main.Window.FindName("LocalCollectionList")).Children.Count==1&&(string)Field(main,"localCollection")=="","empty-quoted local root search keeps grouped shortcuts inside their visible folder: "+emptyQuery);
      }
      main.OpenLocalCollection(localGroup);Search(main,"\"\"");
      check(Visible(main).SetEquals(new[]{hiddenId})&&(string)Field(main,"localCollection")==localGroup&&library.All().Count==records&&library.Db.Query("PRAGMA foreign_key_check").Count==0,"empty-quoted search inside a local folder preserves its scope without changing records");
     }finally{main.PendingInformationChoice=()=>MessageBoxResult.No;main.CloseForReplacement();}
    }
   }finally{Environment.SetEnvironmentVariable("MORI_DESK_DATA",originalData);Entry.ClassicEnabled=classic;Entry.BoardEnabled=board;Entry.InteractiveShell=interactive;ThumbnailImage.CurrentLibrary=oldLibrary;Theme.Set(oldTheme);app.ShutdownMode=shutdown;}
  }
 }
}
