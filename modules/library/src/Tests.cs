using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace Mori {
 public static class Tests {
  static List<string> log=new List<string>();
  static void Check(bool ok,string name){if(!ok)throw new Exception("FAIL: "+name);log.Add("PASS: "+name);}
  static void Throws(Action work,string name){bool failed=false;try{work();}catch{failed=true;}Check(failed,name);}
  static void Draw(string path,Color color){using(var b=new Bitmap(640,400)){using(var g=Graphics.FromImage(b)){g.Clear(color);g.FillRectangle(Brushes.White,100,120,240,90);}b.Save(path,ImageFormat.Png);}}
  static string Json(object value){return new JavaScriptSerializer().Serialize(value);}
  internal static int Media(string output){Directory.CreateDirectory(output);BookmarkIcons.NetworkEnabled=false;int code=0;try{BrowserVideoTests.Run(Path.Combine(output,"webm"),Check);VideoBrowserTests.Run(Path.Combine(output,"video-browser"),Check);log.Add("RESULT: "+log.Count+" checks passed");}catch(Exception ex){log.Add(ex.ToString());code=1;}File.WriteAllLines(Path.Combine(output,"test-report.txt"),log,Encoding.UTF8);return code;}
  public static int Benchmark(string output){Directory.CreateDirectory(output);var clock=System.Diagnostics.Stopwatch.StartNew();using(var lib=new Library(Path.Combine(output,"library"))){string source=Path.Combine(output,"source.png");for(int i=0;i<1000;i++){using(var image=new Bitmap(800,600)){using(var g=Graphics.FromImage(image)){g.Clear(Color.FromArgb(50+i%120,80+i%140,60+i%150));g.DrawString("TEST "+i.ToString("D4"),SystemFonts.DefaultFont,Brushes.White,40,40);g.FillEllipse(Brushes.Gainsboro,40+i%350,70+i%200,200,220);}image.Save(source,ImageFormat.Png);}lib.Import(source,"","测试素材 "+i.ToString("D4"),"","性能测试","");}clock.Stop();long importMs=clock.ElapsedMilliseconds;clock.Restart();var items=lib.All();foreach(var item in items.Take(40))lib.BuildThumbnail(item);clock.Stop();File.WriteAllText(Path.Combine(output,"benchmark.txt"),"Synthetic 1000 x 800x600 PNG (includes fixture generation): "+importMs+" ms\r\nQuery 1000 metadata + 40 thumbnails: "+clock.ElapsedMilliseconds+" ms\r\nAssets: "+items.Count+"\r\nBytes: "+items.Sum(a=>a.Bytes));}return 0;}
  public static int Run(string output){Directory.CreateDirectory(output);int code=0;BookmarkIcons.NetworkEnabled=false;try{
   log.AddRange(PreferenceTests.Run(Path.Combine(output,"preferences")));log.AddRange(CreationMetadataTests.Run(Path.Combine(output,"creation")));SelectionTests.Run(Check);DuplicateTests.Run(Path.Combine(output,"duplicates"),Check);log.AddRange(ThemeTests.Run());log.AddRange(StorageSettingsTests.Run(Path.Combine(output,"storage-settings")));PreviewViewportTests.Run(Check);PreviewRenderingTests.Run(Check);ThumbnailGateTests.Run(Check);LibraryTransferTests.Run(Path.Combine(output,"transfer"),Check);
   PreviewImageTests.Run(Path.Combine(output,"preview-image"),Check);
   BrowserVideoTests.Run(Path.Combine(output,"webm"),Check);
   SvgTests.Run(Path.Combine(output,"svg"),Check);
   UndoHistoryTests.Run(Path.Combine(output,"undo"),Check);
   EagleFileImportTests.Run(Path.Combine(output,"eagle-file-import"),Check);
   GalleryLayoutTests.Run(Path.Combine(output,"gallery-layout"),Check);
   GalleryMenuTests.Run(Path.Combine(output,"gallery-menu"),Check);
   SidebarLayoutTests.Run(Path.Combine(output,"sidebar-layout"),Check);
   SidebarSpacingTests.Run(Path.Combine(output,"sidebar-spacing"),Check);
   TrashActionTests.Run(Path.Combine(output,"trash-confirmation"),Check);
   FolderManagementTests.Run(Path.Combine(output,"folder-management"),Check);
   FolderBatchTests.Run(Path.Combine(output,"folder-batch"),Check);
   GalleryHeadingTests.Run(Path.Combine(output,"gallery-heading"),Check);
   BookmarkTests.Run(Path.Combine(output,"bookmarks"),Check);
   BookmarkIconTests.Run(Path.Combine(output,"bookmark-icons"),Check);
   BookmarkPageTests.Run(Path.Combine(output,"bookmark-page"),Check);
   MenuLogicTests.Run(Path.Combine(output,"menu-logic"),Check);
   MediaNavigationTests.Run(Path.Combine(output,"media-navigation"),Check);
   MediaBadgeTests.Run(Path.Combine(output,"media-badges"),Check);
   BatchTests.Run(Path.Combine(output,"batch"),Check);
   VideoBrowserTests.Run(Path.Combine(output,"video-browser"),Check);
   TrayTests.Run(Path.Combine(output,"tray"),Check);
   string fixtures=Path.Combine(output,"fixtures");Directory.CreateDirectory(fixtures);string image=Path.Combine(fixtures,"测试 ' 单引号.png");Draw(image,Color.CadetBlue);byte[] before=File.ReadAllBytes(image);string root=Path.Combine(output,"library");
   using(var lib=new Library(root)){
    Check(lib.All().Count==0,"empty library");string parent=lib.CreateFolder("工业设计","");string folder=lib.CreateFolder("产品 / 概念",parent);var r=lib.Import(image,folder,null,"https://example.com/reference","蓝色, 圆角","原始备注 ' 不丢失");Check(!r.Duplicate,"first import");Check(File.Exists(lib.FullPath(r.Item)),"original copied");Check(before.SequenceEqual(File.ReadAllBytes(image)),"source untouched");Check(before.SequenceEqual(File.ReadAllBytes(lib.FullPath(r.Item))),"original bytes exact");Check(lib.All()[0].Name.Contains("单引号"),"Unicode and quoted name");Check(lib.Members(parent,true).Contains(r.Item.Id),"recursive categories");Check(!lib.Members(parent,false).Contains(r.Item.Id),"direct category filter");
    var dup=lib.Import(image,parent,null,"","","");Check(dup.Duplicate&&lib.All().Count==1,"SHA256 deduplication");Check(lib.AssetFolders(r.Item.Id).Count==2,"duplicate assigned to second category");lib.Trash(new[]{r.Item.Id},true);Check(lib.All()[0].Deleted,"soft delete");Check(File.Exists(lib.FullPath(r.Item)),"trash preserves original");lib.Import(image,"",null,"","","");Check(!lib.All()[0].Deleted,"reimport restores trash");
    r.Item.Favorite=true;r.Item.Name="新名称";r.Item.Tags="工业,蓝色";lib.Update(r.Item);Check(lib.All()[0].Favorite&&lib.All()[0].Name=="新名称","metadata persisted");lib.BuildThumbnail(r.Item);Check(File.Exists(lib.ThumbnailPath(r.Item)),"bounded thumbnail created");Check(lib.All()[0].Width==640&&lib.All()[0].Height==400,"dimensions indexed");Check(lib.All()[0].Perceptual.Length==16,"perceptual index");Check(Library.Similarity(r.Item.Perceptual,r.Item.Perceptual)==0,"similarity self distance");Check(Library.Similarity("0000000000000000","FFFFFFFFFFFFFFFF")==64,"similarity bit distance");
    string export=Path.Combine(output,"export");Check(lib.Export(lib.All(),export,false,"")==1,"flat export");lib.Export(lib.All(),export,false,"");Check(Directory.GetFiles(export).Length==2,"export collision never overwrites");Check(File.ReadAllBytes(Directory.GetFiles(export)[0]).SequenceEqual(before),"export original quality");string hierarchy=Path.Combine(output,"hierarchy");Check(lib.Export(lib.All(),hierarchy,true,"")==2,"multiple category hierarchy export");Check(Directory.Exists(Path.Combine(hierarchy,"工业设计","产品 _ 概念")),"sanitized hierarchy");
    Throws(()=>Library.Inside(root,"..\\escape.png"),"path traversal rejected");Throws(()=>Library.Inside(root,"C:\\outside.png"),"absolute path escape rejected");Check(Library.SafeName("CON")=="_CON","Windows reserved names");Check(Library.SafeName("../../bad").IndexOf('/')<0,"filename sanitized");Throws(()=>lib.Import(image,"missing-folder",null,"","",""),"invalid category does not leave imported file");
    string backup=Path.Combine(output,"library.mori.zip");lib.Backup(backup);Check(File.Exists(backup),"library backup");Throws(()=>lib.Backup(backup),"backup does not overwrite");string restored=Path.Combine(output,"restored");Library.Restore(backup,restored);using(var restoredLib=new Library(restored)){Check(restoredLib.All().Count==1,"backup restore metadata");Check(restoredLib.Folders().Count==2,"backup restore categories");Check(File.ReadAllBytes(restoredLib.FullPath(restoredLib.All()[0])).SequenceEqual(before),"backup restore originals");}Throws(()=>Library.Restore(backup,restored),"restore refuses nonempty destination");
    string malicious=Path.Combine(output,"malicious.zip");using(var zip=ZipFile.Open(malicious,ZipArchiveMode.Create)){zip.CreateEntry(".mori/library.db");zip.CreateEntry("../../escape.txt");}Throws(()=>Library.Restore(malicious,Path.Combine(output,"safe")),"zip slip rejected before extraction");
    string eagle=Path.Combine(output,"fixture.library");string info=Path.Combine(eagle,"images","test.info");Directory.CreateDirectory(info);File.WriteAllText(Path.Combine(eagle,"metadata.json"),Json(new{folders=new[]{new{id="p",name="参考图",children=new[]{new{id="c",name="子分类"}}}}}));Draw(Path.Combine(info,"test.png"),Color.DarkSeaGreen);File.WriteAllText(Path.Combine(info,"metadata.json"),Json(new{id="test",name="Eagle 测试",ext="png",url="https://example.com/eagle",annotation="迁移备注",tags=new[]{"标签一","标签二"},folders=new[]{"c"}}));string migration=lib.MigrateEagle(eagle,s=>{});Check(lib.All().Count==2,"Eagle fixture migrated");var imported=lib.All().First(a=>a.Name=="Eagle 测试");Check(imported.Tags.Contains("标签一")&&imported.Notes=="迁移备注"&&imported.Source.EndsWith("/eagle"),"Eagle metadata preserved");Check(lib.AssetFolders(imported.Id).Any(f=>f.Name=="子分类"),"Eagle hierarchy membership");Check(File.Exists(Path.Combine(info,"test.png")),"Eagle source untouched");
    Check(NativeBridge.Detect(before)==".png","web bridge sniffs format");Throws(()=>NativeBridge.Handle(lib,new Dictionary<string,object>{{"action","save"},{"data",Convert.ToBase64String(Encoding.UTF8.GetBytes("not an actual image data"))}}),"web bridge rejects nonimage response");var response=NativeBridge.Handle(lib,new Dictionary<string,object>{{"action","save"},{"data",Convert.ToBase64String(before)},{"folder",folder},{"source","https://example.com"},{"name","浏览器收藏"}});Check(Json(response).Contains("\"ok\":true"),"web bridge import protocol");Check(lib.All().Count==2,"web bridge deduplicates");Check(Json(NativeBridge.Handle(lib,new Dictionary<string,object>{{"action","folders"}})).Contains("工业设计"),"web bridge category list");
    var beforeCount=lib.All().Count;try{lib.Db.Transaction(delegate{lib.CreateFolder("rollback","");throw new Exception("test");});}catch{}Check(!lib.Folders().Any(f=>f.Name=="rollback"),"transaction rollback");
   }
   using(var reopened=new Library(root)){Check(reopened.All().Count==2,"library survives restart");}
   log.Add("RESULT: "+log.Count+" checks passed");
  }catch(Exception ex){code=1;log.Add(ex.ToString());}File.WriteAllLines(Path.Combine(output,"test-report.txt"),log,Encoding.UTF8);return code;}
 }
}
