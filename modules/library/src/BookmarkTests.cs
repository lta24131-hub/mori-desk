using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace Mori {
 public static class BookmarkTests {
  static bool Reject(Action action){try{action();return false;}catch{return true;}}
  public static void Run(string output,Action<bool,string> check){
   Directory.CreateDirectory(output);using(var lib=new Library(Path.Combine(output,"library"))){
    string folder=lib.CreateFolder("网页参考","");var first=lib.ImportBookmark("HTTPS://Example.COM:443/a?q=1#b","原名称",folder,"参考","原备注").Item;
    check(first.IsBookmark&&first.Source=="https://example.com/a?q=1#b"&&first.TypeLabel=="网址"&&first.SiteName=="example.com","bookmark canonical URL and separate media type");
    check(new FileInfo(lib.FullPath(first)).Length<1000&&Library.ReadBookmark(lib.FullPath(first))==first.Source,"bookmark is a portable tiny URL file, not a webpage download");
    var ask=lib.ImportBookmark(first.Source,"新名称","");check(ask.ChoiceRequired&&lib.All().Count==1,"duplicate bookmark does not write until decision");
    var reuse=lib.ImportBookmark(first.Source,"新名称","","","","reuse");check(reuse.Duplicate&&reuse.Item.Name=="原名称"&&reuse.Item.Notes=="原备注","reuse bookmark preserves prior metadata");
    var keep=lib.ImportBookmark(first.Source,"副本","","","","keep");check(keep.KeptCopy&&lib.All().Count==2,"explicit keep creates another bookmark");
    lib.Trash(new[]{first.Id},true);check(lib.All().Single(a=>a.Id==first.Id).Deleted,"bookmark supports recycle bin");lib.ImportBookmark(first.Source,"","","","","reuse");check(!lib.All().Single(a=>a.Id==first.Id).Deleted,"reuse explicitly restores trashed bookmark");
    foreach(string bad in new[]{"javascript:alert(1)","file:///C:/Windows/system.ini","https://user:password@example.com/","https://example.com/\r\nIconFile=evil","",new string('x',8001)})check(Reject(()=>lib.ImportBookmark(bad,"bad","")),"unsafe bookmark rejected: "+bad.Substring(0,Math.Min(28,bad.Length)).Replace("\r"," ").Replace("\n"," "));
    string shortcut=Path.Combine(output,"incoming.url");File.WriteAllText(shortcut,"[InternetShortcut]\r\nURL=https://example.org/\r\nIconFile=\\\\server\\secret.ico\r\n");var safe=lib.ImportLocalFile(shortcut,"").Item;check(!File.ReadAllText(lib.FullPath(safe)).Contains("IconFile"),"URL import strips remote icon and untrusted INI fields");
    lib.BuildThumbnail(first);check(!File.Exists(lib.ThumbnailPath(first)),"bookmarks allocate no decoded thumbnail");
    string export=Path.Combine(output,"export");lib.Export(new[]{first},export,false,"");check(Library.ReadBookmark(Directory.GetFiles(export).Single())==first.Source,"bookmark export retains exact target");
    string backup=Path.Combine(output,"bookmark.mori.zip"),restored=Path.Combine(output,"restored");lib.Backup(backup);Library.Restore(backup,restored);using(var reopened=new Library(restored))check(reopened.All().Count==3&&reopened.All().All(a=>Library.ReadBookmark(reopened.FullPath(a))==a.Source),"bookmark backup/restore includes URLs and metadata");
    var native=NativeBridge.Handle(lib,new Dictionary<string,object>{{"action","bookmark"},{"source","https://example.net/"},{"name","浏览器网址"}});check(new JavaScriptSerializer().Serialize(native).Contains("\"ok\":true")&&lib.All().Any(a=>a.Name=="浏览器网址"),"native bookmark route uses same local library");
    var batch=new Dictionary<string,object>{{"action","importBookmarks"},{"items",new object[]{new Dictionary<string,object>{{"url","https://example.com/browser"},{"name","Chrome 示例"},{"path",new[]{"书签栏","设计"}}},new Dictionary<string,object>{{"url","javascript:bad"}}}}};
    string reply=new JavaScriptSerializer().Serialize(NativeBridge.Handle(lib,batch));check(reply.Contains("\"added\":1")&&reply.Contains("\"skipped\":1"),"browser import skips unsafe URLs and imports valid bookmark");var browser=lib.All().Single(a=>a.Name=="Chrome 示例");check(lib.AssetFolders(browser.Id).Single().Name=="设计"&&lib.Folders().Any(f=>f.Name=="书签栏"),"Chrome folder hierarchy retained");
    browser.Notes="自己写的备注";lib.Update(browser);int foldersBefore=lib.Folders().Count;reply=new JavaScriptSerializer().Serialize(NativeBridge.Handle(lib,batch));check(reply.Contains("\"reused\":1")&&lib.Folders().Count==foldersBefore&&lib.All().Single(a=>a.Id==browser.Id).Notes=="自己写的备注","repeated browser import reuses root and preserves Mori edits");lib.Trash(new[]{browser.Id},true);reply=new JavaScriptSerializer().Serialize(NativeBridge.Handle(lib,batch));check(reply.Contains("\"skipped\":2")&&lib.All().Single(a=>a.Id==browser.Id).Deleted,"bulk browser import never resurrects intentionally trashed URLs");
   }
  }
 }
}
