using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mori {
 public static class BookmarkPageTests {
  static bool Reject(Action action){try{action();return false;}catch{return true;}}
  static object Field(MainWindow main,string name){return typeof(MainWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(main);}
  static void Call(MainWindow main,string method,params object[] args){typeof(MainWindow).GetMethod(method,BindingFlags.NonPublic|BindingFlags.Instance,null,args.Select(a=>a.GetType()).ToArray(),null).Invoke(main,args);}
  static byte[] Png(){using(var b=new System.Drawing.Bitmap(40,30)){using(var g=System.Drawing.Graphics.FromImage(b)){g.Clear(System.Drawing.Color.Teal);g.FillRectangle(System.Drawing.Brushes.LightGray,0,0,20,30);}using(var stream=new MemoryStream()){b.Save(stream,System.Drawing.Imaging.ImageFormat.Png);return stream.ToArray();}}}
  public static void Run(string output,Action<bool,string> check){
   Directory.CreateDirectory(output);string imagePath=Path.Combine(output,"sample.png");File.WriteAllBytes(imagePath,Png());string root=Path.Combine(output,"migration");string shared,pure,imageId,webId,pureId;byte[] original;
   using(var lib=new Library(root)){
    shared=lib.CreateFolder("共享旧分类","");pure=lib.CreateFolder("Chrome 旧书签","");imageId=lib.Import(imagePath,shared,"图片","","","").Item.Id;webId=lib.ImportBookmark("https://example.com/shared","共享网址","").Item.Id;pureId=lib.ImportBookmark("https://example.org/only","网址专属","").Item.Id;
    // Only fixtures deliberately recreate pre-namespace cross-type memberships.
    lib.Db.Exec("INSERT INTO memberships VALUES("+Database.Q(webId)+","+Database.Q(shared)+"); INSERT INTO memberships VALUES("+Database.Q(pureId)+","+Database.Q(pure)+")");
    original=File.ReadAllBytes(lib.FullPath(lib.All().Single(a=>a.Id==imageId)));
    // Recreate the exact three-column legacy folder schema in a fixture only.
    lib.Db.Exec("PRAGMA foreign_keys=OFF; CREATE TABLE old_folders(id TEXT PRIMARY KEY,name TEXT NOT NULL,parent TEXT NOT NULL DEFAULT ''); INSERT INTO old_folders SELECT id,name,parent FROM folders; DROP TABLE folders; ALTER TABLE old_folders RENAME TO folders; PRAGMA foreign_keys=ON;");
   }
   int count;
   using(var lib=new Library(root)){
    check(lib.Folders(false).Single().Id==shared&&lib.Folders(true).Count==2,"legacy pure bookmark tree leaves media sidebar; shared folder gets independent bookmark mirror");
    check(lib.Folders(true).Any(f=>f.Id==pure)&&lib.AssetFolders(pureId).Single().Id==pure,"pure bookmark migration preserves existing folder identity");
    check(lib.AssetFolders(imageId).Single().Id==shared&&lib.AssetFolders(webId).Single().Scope=="bookmark","migration keeps media membership and separates shared website membership");
    check(lib.All().Count==3&&File.ReadAllBytes(lib.FullPath(lib.All().Single(a=>a.Id==imageId))).SequenceEqual(original)&&lib.Db.Query("PRAGMA foreign_key_check").Count==0,"folder migration preserves asset count, original bytes and foreign keys");count=lib.Folders().Count;
    string target=lib.CreateFolder("设计","",true),child=lib.CreateFolder("工具",target,true);var h=new UndoHistory(lib.Db);string previous=lib.AssetFolders(webId).Single().Id;
    h.Record("移动网址",()=>lib.MoveBookmarks(new[]{webId,pureId},target));check(lib.AssetFolders(webId).Single().Id==target&&lib.AssetFolders(pureId).Single().Id==target,"batch move replaces previous bookmark memberships");h.Undo();check(lib.AssetFolders(webId).Single().Id==previous&&lib.AssetFolders(pureId).Single().Id==pure,"undo restores all original bookmark folders");
    h.Record("删除网址文件夹",()=>lib.DeleteFolder(pure));check(!lib.Classified().Contains(pureId)&&!lib.All().Single(a=>a.Id==pureId).Deleted,"deleting bookmark folder keeps its website in unfiled");h.Undo();check(lib.AssetFolders(pureId).Single().Scope=="bookmark","undo restores bookmark folder scope and membership");
    check(Reject(()=>lib.MoveBookmarks(new[]{imageId},target))&&Reject(()=>lib.CreateFolder("非法子文件夹",shared,true)),"bookmark moves and parent folders reject cross-media mixing");
    check(Reject(()=>lib.AddToFolder(new[]{imageId},target))&&Reject(()=>lib.AddToFolder(new[]{webId},shared)),"direct category assignment rejects cross-namespace memberships");
    var pasted=lib.Import(imagePath,target,"粘贴到网址页的图片","","","",null,"keep").Item;check(lib.AssetFolders(pasted.Id).Count==0,"media imported while in bookmark folder remains safely unfiled");lib.Db.Exec("DELETE FROM assets WHERE id="+Database.Q(pasted.Id));File.Delete(lib.FullPath(pasted));
    lib.MoveBookmarks(new[]{webId},child);check(lib.Members(target,true).Contains(webId)&&!lib.Members(target,false).Contains(webId),"nested bookmark browsing distinguishes direct items from descendants");
    string json=new JavaScriptSerializer().Serialize(NativeBridge.Handle(lib,new Dictionary<string,object>{{"action","folders"},{"scope","bookmark"}}));check(json.Contains("设计")&&!json.Contains(shared),"browser bookmark category list excludes media IDs");
    var app=Application.Current??new Application();var shutdown=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;var main=new MainWindow(lib){PreviewMode=true};
    try{
     main.SetBookmarkMode(true);check(((TreeView)main.Window.FindName("Folders")).Visibility==Visibility.Collapsed&&((WrapPanel)main.Window.FindName("BookmarkFolderList")).Children.Count==3,"website folders render in-page and never in sidebar");
     Call(main,"Navigate",target);check(((WrapPanel)main.Window.FindName("BookmarkFolderList")).Children.Count==1&&((List<Asset>)Field(main,"visible")).Count==0,"folder view exposes child folders without duplicating descendant links");
     main.SetSection("videos");check(((List<Asset>)Field(main,"visible")).Count==0&&((StackPanel)main.Window.FindName("BookmarkPage")).Visibility==Visibility.Collapsed,"video section hides website controls and excludes pictures");main.SetSection("pictures");check(((List<Asset>)Field(main,"visible")).Single().Id==imageId,"picture section excludes bookmarks");
     check(new[]{".png",".gif",".svg",".webp"}.All(ext=>MainWindow.SectionContains(new Asset{Ext=ext},"pictures"))&&MainWindow.SectionContains(new Asset{Ext=".mp4"},"videos")&&MainWindow.SectionContains(new Asset{Ext=".pdf"},"documents")&&!MainWindow.SectionContains(new Asset{Ext=".mp4"},"pictures"),"four navigation sections classify still images, GIF, SVG, videos and PDF correctly");
     var selection=(HashSet<string>)Field(main,"selection");selection.Add(imageId);Call(main,"UpdateInspector");check(((ThumbnailImage)main.Window.FindName("DetailPreview")).DataContext is Asset&&((TextBox)main.Window.FindName("Notes")).Height==54,"compact inspector binds preview and retains editable notes");
     check(new[]{"SaveMeta","Categorize","Star","Similar","ColorSearch","SelectionCanvas","RestoreTrash","Delete"}.All(name=>main.Window.FindName(name) is Button),"compact inspector retains bottom actions with canvas replacing fixed-count comparison");
     check(((Grid)main.Window.FindName("InspectorPreview")).Height==112&&((Button)main.Window.FindName("Star")).Parent==((Button)main.Window.FindName("Categorize")).Parent&&((TextBlock)main.Window.FindName("SelectionInfo")).Text=="1 张图片已选中","compact inspector pairs actions and uses natural selection copy");
     selection.Clear();Call(main,"UpdateInspector");check(((Grid)main.Window.FindName("InspectorPreview")).Visibility==Visibility.Collapsed&&((ThumbnailImage)main.Window.FindName("DetailPreview")).DataContext==null,"deselection clears preview and releases selected image");
     using(var input=new MemoryStream(Png())){var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=input;bitmap.EndInit();var colors=MainWindow.Palette(bitmap);check(colors.Count==2&&colors.Any(c=>c.G==128)&&colors.Any(c=>c.R==211),"palette samples actual thumbnail pixels, not decorative placeholder colors");}
    }finally{main.Window.Close();app.ShutdownMode=shutdown;ThumbnailImage.CurrentLibrary=null;}
    count=lib.Folders().Count;
   }
   using(var lib=new Library(root))check(lib.Folders().Count==count&&lib.All().Count==3,"schema migration is idempotent on restart");
   string inputJson="{\"roots\":{\"bookmark_bar\":{\"type\":\"folder\",\"name\":\"书签栏\",\"children\":[{\"type\":\"folder\",\"name\":\"设计\",\"children\":[{\"type\":\"url\",\"name\":\"工具\",\"url\":\"https://example.com/?a=1\"},{\"type\":\"url\",\"url\":\"javascript:bad\"}]}]}}}";
   var parsed=BookmarkFileImport.ParseJson(inputJson);check(parsed.Items.Count==1&&parsed.Skipped==1&&((string[])parsed.Items[0]["path"]).SequenceEqual(new[]{"书签栏","设计"}),"Chrome JSON import preserves nested folders and skips unsafe URLs");
   string html="<!DOCTYPE NETSCAPE-Bookmark-file-1><DL><p><DT><H3>收藏</H3><DL><p><DT><A HREF=\"https://example.org/?x=1&amp;y=2\">工具 &amp; 素材</A><DT><A HREF='file:///secret'>不导入</A></DL><DT><A HREF='https://example.net/'>顶层</A></DL>";
   parsed=BookmarkFileImport.ParseHtml(html);check(parsed.Items.Count==2&&parsed.Skipped==1&&((string[])parsed.Items[0]["path"]).Single()=="收藏"&&((string[])parsed.Items[1]["path"]).Length==0&&parsed.Items[0]["url"].ToString().Contains("&y=2"),"HTML export import restores folder stack, decodes entities and skips local files");
   check(Reject(()=>BookmarkFileImport.ParseHtml("<html>not bookmarks</html>"))&&Reject(()=>BookmarkFileImport.ParseJson("{}")),"non-bookmark input is rejected without import");
   string local=Path.Combine(output,"profiles");Directory.CreateDirectory(Path.Combine(local,"Google","Chrome","User Data","Default"));string profile=Path.Combine(local,"Google","Chrome","User Data","Default","Bookmarks");File.WriteAllText(profile,inputJson);check(BookmarkFileImport.Sources(local).Single().Path==profile,"native import locates a synthetic Chrome profile without an extension");string before=File.ReadAllText(profile);parsed=BookmarkFileImport.Read(profile);check(parsed.Items.Count==1&&File.ReadAllText(profile)==before,"direct profile import is read-only");
   using(var lib=new Library(Path.Combine(output,"import"))){var request=new Dictionary<string,object>{{"items",parsed.Items.ToArray()},{"rootName","Chrome 默认用户"}};BrowserBookmarkImport.Handle(lib,request);BrowserBookmarkImport.Handle(lib,request);check(lib.All().Count==1&&lib.Folders(false).Count==0&&lib.AssetFolders(lib.All().Single().Id).Single().Name=="设计","repeat native import deduplicates and keeps all folders out of media sidebar");}
   var links=BookmarkIcons.Discover(new Uri("https://example.com/"),Encoding.UTF8.GetBytes("<head><link rel='icon' href='/assets/logo.svg'><link rel='apple-touch-icon' href='/apple.png'><link rel='icon' href='http://127.0.0.1/private'></head><link rel='icon' href='/not-head.png'>"));check(links.Count()==2&&links.First().AbsolutePath=="/assets/logo.svg","favicon discovers declared root icons without fetching page path or private addresses");
   byte[] svg=Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='32' height='32'><rect width='32' height='32' fill='teal'/></svg>");var svgIcon=BookmarkIcons.DecodeCandidate(svg,new Uri("https://example.com/icon.svg"));check(svgIcon.PixelWidth==64&&svgIcon.IsFrozen,"declared SVG favicon is safely rendered in isolated bounded worker");
   var requests=new List<string>();var icon=BookmarkIcons.Load(output,"https://icons.example.com/private?secret=1",uri=>{requests.Add(uri.AbsoluteUri);if(uri.AbsolutePath=="/favicon.ico")throw new IOException();if(uri.AbsolutePath=="/")return Encoding.UTF8.GetBytes("<head><link rel='icon' href='/logo.png'></head>");return Png();});check(icon!=null&&requests.SequenceEqual(new[]{"https://icons.example.com/favicon.ico","https://icons.example.com/","https://icons.example.com/logo.png"}),"missing conventional favicon recovers declared icon without requesting saved path/query");
   string oldMiss=BookmarkIcons.PathFor(output,"https://retry.example.com/")+".miss";File.WriteAllBytes(oldMiss,new byte[]{0});check(BookmarkIcons.Load(output,"https://retry.example.com/",uri=>Png())!=null,"upgrade retries legacy failed-icon cache rather than waiting 24 hours");
  }
 }
}
