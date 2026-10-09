using System;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace Mori {
 public static class EagleFileImportTests {
  public static void Run(string output,Action<bool,string> check){
   string eagle=Path.Combine(output,"fixture.library"),info=Path.Combine(eagle,"images","item.info");Directory.CreateDirectory(info);
   string file=Path.Combine(info,"参考动图.gif"),meta=Path.Combine(info,"metadata.json");byte[] original={71,73,70,56,57,97,1,2,3,4};File.WriteAllBytes(file,original);
   var json=new JavaScriptSerializer();string url="https://www.example.com/gallery/123?tracking_source=design";
   string contents=json.Serialize(new{id="item",name="参考动图",ext="gif",url=url,tags=new[]{"设计","机械"},annotation="原始备注",btime=1234567890123});File.WriteAllText(meta,contents);
   using(var lib=new Library(Path.Combine(output,"direct"))){
    var result=lib.ImportLocalFile(file,"");
    check(result.Item.Source==url&&result.Item.Name=="参考动图","Eagle file drag imports the sidecar source URL and name");
    check(result.Item.Tags=="设计, 机械"&&result.Item.Notes=="原始备注","Eagle file drag preserves tags and notes");
    check(result.Item.Ext==".gif"&&File.ReadAllBytes(lib.FullPath(result.Item)).SequenceEqual(original),"Eagle file copy preserves original GIF bytes, not a clipboard bitmap");
    check(result.Item.Created.StartsWith("2009-02-13"),"Eagle file drag preserves btime");
    check(File.ReadAllText(meta)==contents&&File.ReadAllBytes(file).SequenceEqual(original),"Eagle source and metadata remain untouched");
   }
   using(var lib=new Library(Path.Combine(output,"duplicates"))){
    var existing=lib.Import(file,"","自定义名称","","自定义标签","自定义备注").Item;
    check(lib.ImportLocalFile(file,"").ChoiceRequired&&lib.All().Single().Source=="","duplicate prompt alone never modifies missing URL");
    check(lib.ImportLocalFile(file,"","skip").Skipped&&lib.All().Single().Source=="","skip leaves existing metadata unchanged");
    var reused=lib.ImportLocalFile(file,"","reuse");
    check(reused.Duplicate&&reused.Item.Source==url&&lib.All().Count==1,"reuse backfills missing Eagle URL without another original");
    check(reused.Item.Name=="自定义名称"&&reused.Item.Tags=="自定义标签"&&reused.Item.Notes=="自定义备注","URL backfill preserves user's name tags and notes");
    existing=lib.All().Single();existing.Source="https://example.com/edited";lib.Update(existing);
    check(lib.ImportLocalFile(file,"","reuse").Item.Source==existing.Source,"Eagle metadata never overwrites an established URL");
    var kept=lib.ImportLocalFile(file,"","keep");
    check(kept.KeptCopy&&kept.Item.Source==url&&lib.All().Count==2,"keep extra copy retains Eagle URL on the new copy only");
   }
   using(var lib=new Library(Path.Combine(output,"migration"))){
    lib.Import(file,"",null,"","","");lib.MigrateEagle(eagle,delegate{});
    check(lib.All().Count==1&&lib.All().Single().Source==url,"full Eagle migration also backfills URLs on reused imports");
   }
   using(var lib=new Library(Path.Combine(output,"migration-merge"))){
    lib.Import(file,"","Keep my title","https://example.com/edited","My tag","My note");
    lib.MigrateEagle(eagle,delegate{});var item=lib.All().Single();
    check(item.Name=="Keep my title"&&item.Source=="https://example.com/edited","full migration keeps edited Mori name and URL");
    check(item.Tags.Contains("My tag")&&item.Tags.Contains("设计")&&item.Tags.Contains("机械"),"full migration unions tags on byte-identical originals");
    check(item.Notes.Contains("My note")&&item.Notes.Contains("原始备注")&&item.Notes.Contains(url),"full migration retains both notes and alternative Eagle source");
    string notes=item.Notes,tags=item.Tags;lib.MigrateEagle(eagle,delegate{});item=lib.All().Single();
    check(item.Notes==notes&&item.Tags==tags&&lib.All().Count==1,"repeated metadata merge cannot duplicate text or original files");
   }
   string idFile=Path.Combine(info,"item.gif");File.Copy(file,idFile);
   check(Library.EagleMetadataForFile(idFile)!=null,"Eagle ID-based original filenames are supported too");
   string thumb=Path.Combine(info,"参考动图_thumbnail.gif");File.Copy(file,thumb);
   check(Library.EagleMetadataForFile(thumb)==null,"same-extension thumbnails cannot inherit original metadata");
   string unrelated=Path.Combine(info,"other.gif");File.Copy(file,unrelated);
   check(Library.EagleMetadataForFile(unrelated)==null,"unrelated files cannot inherit an Eagle URL");
   File.WriteAllText(meta,json.Serialize(new{id="different",name="参考动图",ext="gif",url=url}));
   check(Library.EagleMetadataForFile(file)==null,"mismatched Eagle IDs are rejected");
   File.WriteAllText(meta,"{invalid");
   using(var lib=new Library(Path.Combine(output,"bad-meta"))){check(lib.ImportLocalFile(file,"").Item.Source=="","malformed metadata falls back to a normal original-file import");}
   File.WriteAllText(meta,json.Serialize(new{id="item",name="参考动图",ext="gif",url="javascript:alert(1)"}));
   using(var lib=new Library(Path.Combine(output,"unsafe"))){check(lib.ImportLocalFile(file,"").Item.Source=="","unsafe URLs are not adopted from sidecars");}
   string normal=Path.Combine(output,"ordinary");Directory.CreateDirectory(normal);File.Copy(file,Path.Combine(normal,"参考动图.gif"));File.WriteAllText(Path.Combine(normal,"metadata.json"),contents);
   check(Library.EagleMetadataForFile(Path.Combine(normal,"参考动图.gif"))==null,"ordinary folder sidecars are not treated as Eagle metadata");
  }
 }
}
