using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Mori;

namespace MoriDesk {
 public static class ReferenceEagleMetadataTests {
  static readonly byte[] Image=Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
  static Dictionary<string,object> Metadata(string id){return new Dictionary<string,object>{{"id",id},{"name","Eagle 参考图"},{"ext","gif"},{"url","https://example.com/gallery/123?source=eagle"},{"tags",new[]{"设计","机械"}},{"annotation","Eagle 原始备注"},{"btime",1234567890123L},{"width",640},{"height",480}};}
  static string Fixture(string root,string id,Dictionary<string,object> metadata){string folder=Path.Combine(root,"fixture.library","images",id+".info");Directory.CreateDirectory(folder);string path=Path.Combine(folder,id+".gif");File.WriteAllBytes(path,Image);File.WriteAllText(Path.Combine(folder,"metadata.json"),new JavaScriptSerializer().Serialize(metadata));return path;}
  static Asset Collect(Library lib,string path){string id=lib.AddReferenceAssets(new[]{path},"").Single();return lib.All().Single(a=>a.Id==id);}
  public static void Run(string root,Action<bool,string> check){
   string image=Fixture(root,"direct",Metadata("direct")),meta=Path.Combine(Path.GetDirectoryName(image),"metadata.json");string contents=File.ReadAllText(meta);var written=File.GetLastWriteTimeUtc(image);string url="https://example.com/gallery/123?source=eagle";
   using(var lib=new Library(Path.Combine(root,"library"))){
    lib.EnableReferenceAssets();bool classic=Entry.ClassicEnabled,board=Entry.BoardEnabled;MainWindow main=null;
    try{Entry.ClassicEnabled=true;Entry.BoardEnabled=false;main=new MainWindow(lib){PreviewMode=true};main.CollectClassicPaths(new[]{image},"");}
    finally{if(main!=null)main.Window.Close();Entry.ClassicEnabled=classic;Entry.BoardEnabled=board;ThumbnailImage.CurrentLibrary=null;}
    var item=lib.All().Single();
    check(item.IsReference&&item.ReferencePath==image&&item.Source==url,"default desktop/Eagle collect retains source URL on a local shortcut");
    check(item.Name=="Eagle 参考图"&&item.Tags=="设计, 机械"&&item.Notes=="Eagle 原始备注","default collect retains Eagle title tags and notes");
    check(item.Created.StartsWith("2009-02-13")&&item.Width==640&&item.Height==480,"Eagle shortcut retains creation date and bounded dimensions");
    check(Directory.GetFiles(Path.Combine(lib.Root,"原图"),"*",SearchOption.AllDirectories).Length==0,"Eagle shortcut collection never copies original bytes into the library");
    item.Name="我的名称";item.Source="https://example.org/my-edited-source";item.Tags="我的标签";item.Notes="我的备注";item.Favorite=true;lib.Update(item);string id=item.Id,added=item.Added,created=item.Created;
    string category=lib.CreateFolder("侧栏分类",""),collection=lib.CreateLocalCollection("本地项目");lib.MoveToLocalCollection(new[]{id},collection);
    lib.AddReferenceAssets(new[]{image},category);item=lib.All().Single();
    check(item.Id==id&&item.Name=="我的名称"&&item.Source=="https://example.org/my-edited-source"&&item.Tags=="我的标签"&&item.Notes=="我的备注"&&item.Favorite,"repeated Eagle shortcut collection never overwrites user metadata or duplicates the shortcut");
    check(item.Added==added&&item.Created==created&&item.LocalCollection==collection&&lib.AssetFolders(id).Single().Id==category,"repeat collection preserves dates and local grouping while applying the explicit destination");
    string legacyPath=Fixture(root,"legacy",Metadata("legacy"));new ReferenceStore(lib.Db).Add(new[]{legacyPath},"");var legacy=lib.All().Single(a=>a.ReferencePath==legacyPath);legacy.Name="旧收藏名称";legacy.Tags="已编辑标签";lib.Update(legacy);string legacyId=legacy.Id;
    legacy=Collect(lib,legacyPath);
    check(legacy.Id==legacyId&&legacy.Name=="旧收藏名称"&&legacy.Tags=="已编辑标签"&&legacy.Source==url&&legacy.Notes=="Eagle 原始备注","repeat collection backfills missing Eagle URL and notes without replacing edited fields");
    check(legacy.Created.StartsWith("2009-02-13")&&legacy.Width==640&&legacy.Height==480,"legacy shortcut backfill includes missing creation date and dimensions");
    string ordinary=Path.Combine(root,"ordinary");Directory.CreateDirectory(ordinary);string ordinaryImage=Path.Combine(ordinary,"普通图片.gif");File.WriteAllBytes(ordinaryImage,Image);File.WriteAllText(Path.Combine(ordinary,"metadata.json"),contents);var normal=Collect(lib,ordinaryImage);
    check(normal.Name=="普通图片.gif"&&normal.Source==""&&normal.Tags==""&&normal.Notes=="","ordinary file collection ignores unrelated sidecars and preserves filename semantics");
    string folderId=lib.AddReferenceAssets(new[]{Path.GetDirectoryName(image)},"").Single();check(lib.All().Single(a=>a.Id==folderId).IsDirectory&&lib.All().Count(a=>a.ReferencePath==image)==1,"collecting an Eagle folder remains one shortcut and never expands its originals");
    string thumb=Path.Combine(Path.GetDirectoryName(image),"direct_thumbnail.gif");File.WriteAllBytes(thumb,Image);check(Collect(lib,thumb).Source=="","Eagle thumbnail cannot inherit the original's source metadata");
    var mismatch=Metadata("wrong");string mismatchPath=Fixture(root,"mismatch",mismatch);check(Collect(lib,mismatchPath).Source=="","mismatched Eagle IDs cannot attach metadata to a shortcut");
    var garbage=Metadata("garbage");garbage["name"]=new Dictionary<string,object>{{"unexpected","value"}};garbage["url"]="javascript:alert(1)";garbage["tags"]=new object[]{" 有效标签 ",42,new Dictionary<string,object>{{"a","b"}},"有效标签"};garbage["annotation"]=new[]{"not text"};garbage["width"]=int.MaxValue;var safe=Collect(lib,Fixture(root,"garbage",garbage));
    check(safe.Name=="garbage.gif"&&safe.Source==""&&safe.Notes==""&&safe.Tags=="有效标签"&&safe.Width==0&&safe.Height==0,"invalid metadata types unsafe URLs and oversized dimensions cannot leak into shortcut fields");
    var stringTags=Metadata("string-tags");stringTags["tags"]="not an array";check(Collect(lib,Fixture(root,"string-tags",stringTags)).Tags=="","malformed string tags are not split into character tags");
    string malformed=Fixture(root,"malformed",Metadata("malformed"));File.WriteAllText(Path.Combine(Path.GetDirectoryName(malformed),"metadata.json"),"{invalid");check(Collect(lib,malformed).Source=="","malformed Eagle sidecar falls back to a normal shortcut without blocking collection");
    string oversized=Fixture(root,"oversized",Metadata("oversized"));File.WriteAllText(Path.Combine(Path.GetDirectoryName(oversized),"metadata.json"),new string('x',4*1024*1024+1));check(Collect(lib,oversized).Source=="","oversized Eagle sidecar is ignored by the bounded reader");
   }
   check(File.ReadAllBytes(image).SequenceEqual(Image)&&File.GetLastWriteTimeUtc(image)==written&&File.ReadAllText(meta)==contents,"all metadata collection and backfill leaves the Eagle source and sidecar untouched");
  }
 }
}
