using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Mori {
 public static class DuplicateTests {
  static void Draw(string path,Color color){using(var bitmap=new Bitmap(96,64)){using(var graphics=Graphics.FromImage(bitmap))graphics.Clear(color);bitmap.Save(path,ImageFormat.Png);}}
  static Dictionary<string,object> Response(object value){var json=new JavaScriptSerializer();return json.Deserialize<Dictionary<string,object>>(json.Serialize(value));}
  static string Changes(Library lib){return lib.Db.Query("SELECT total_changes() AS count")[0]["count"];}
  static string Files(Library lib){return string.Join("\n",Directory.GetFiles(Path.Combine(lib.Root,"原图"),"*",SearchOption.AllDirectories).OrderBy(x=>x));}
  public static void Run(string output,Action<bool,string> check){
   Directory.CreateDirectory(output);string source=Path.Combine(output,"source.png");Draw(source,Color.CadetBlue);byte[] original=File.ReadAllBytes(source);
   using(var lib=new Library(Path.Combine(output,"library"))){
    string folder1=lib.CreateFolder("Original",""),folder2=lib.CreateFolder("New target","");
    var first=lib.Import(source,folder1,"Canonical","https://example.com/original","original tags","original note","2020-01-02T03:04:05.0000000Z");
    check(!first.Duplicate&&!first.ChoiceRequired&&!first.KeptCopy,"new imports remain compatible");
    string canonicalId=first.Item.Id,canonicalHash=lib.Db.Query("SELECT hash FROM assets")[0]["hash"],created=first.Item.Created;
    lib.Trash(new[]{canonicalId},true);string before=Changes(lib),files=Files(lib);
    var ask=lib.Import(source,folder2,"Replacement","https://example.com/new","new tags","new notes","", "ask");
    check(ask.Duplicate&&ask.ChoiceRequired&&!ask.Skipped&&ask.Item.Id==canonicalId,"ask identifies canonical duplicate");
    check(Changes(lib)==before&&Files(lib)==files,"ask performs no index/file changes");
    check(lib.All().Single().Deleted&&lib.Members(folder2,false).Count==0,"ask neither revives nor categorizes");
    check(ask.Item.Name=="Canonical"&&ask.Item.Source.EndsWith("/original")&&ask.Item.Created==created,"ask preserves original metadata/creation");
    var skip=lib.Import(source,folder2,null,"","","",null,"skip");
    check(skip.Skipped&&skip.Duplicate&&Changes(lib)==before&&Files(lib)==files,"skip duplicate is side-effect free");
    bool invalid=false;try{lib.Import(source,"",null,"","","",null,"overwrite");}catch(ArgumentException){invalid=true;}
    check(invalid&&Changes(lib)==before,"unknown duplicate action rejected without writes");
    var kept=lib.Import(source,folder2,"Separate copy","https://example.com/copy","copy tags","copy note","2021-02-03T04:05:06Z","keep");
    check(kept.Duplicate&&kept.KeptCopy&&!kept.ChoiceRequired&&kept.Item.Id!=canonicalId&&lib.All().Count==2,"keep creates separate asset");
    check(kept.Item.Relative!=first.Item.Relative&&File.ReadAllBytes(lib.FullPath(kept.Item)).SequenceEqual(original),"keep owns an identical separate original");
    check(kept.Item.Tags=="copy tags"&&kept.Item.Source.EndsWith("/copy")&&kept.Item.Created.StartsWith("2021-02-03"),"keep retains incoming independent metadata");
    check(lib.All().Single(a=>a.Id==canonicalId).Deleted&&lib.Members(folder2,false).SetEquals(new[]{kept.Item.Id}),"keep does not revive or categorize canonical");
    check(lib.Db.Query("SELECT id FROM assets WHERE hash="+Database.Q(canonicalHash)).Single()["id"]==canonicalId,"canonical exact SHA stays unique");
    check(lib.Import(source,"",null,"","","",null,"ask").Item.Id==canonicalId,"later duplicate detection still selects canonical");
    var reused=lib.Import(source,folder2,"Must not replace metadata","https://example.com/ignored","ignored","ignored");
    check(reused.Duplicate&&!reused.KeptCopy&&reused.Item.Id==canonicalId&&!reused.Item.Deleted&&lib.All().Count==2,"default reuse still revives canonical");
    check(lib.Members(folder2,false).SetEquals(new[]{canonicalId,kept.Item.Id})&&reused.Item.Name=="Canonical"&&reused.Item.Created==created,"reuse only adds category and preserves metadata");
    string exported=Path.Combine(output,"export");check(lib.Export(lib.All(),exported,false,"")==2&&Directory.GetFiles(exported).All(f=>File.ReadAllBytes(f).SequenceEqual(original)),"kept originals export separately without loss");
    int count=lib.All().Count;files=Files(lib);lib.Db.Exec("CREATE TRIGGER reject_test BEFORE INSERT ON assets WHEN NEW.name='Reject test' BEGIN SELECT RAISE(ABORT, 'intentional rollback'); END;");
    bool failed=false;try{lib.Import(source,folder2,"Reject test","","","",null,"keep");}catch{failed=true;}
    check(failed&&lib.All().Count==count&&Files(lib)==files,"failed keep rolls back index and copied original");lib.Db.Exec("DROP TRIGGER reject_test");
    check(File.ReadAllBytes(source).SequenceEqual(original)&&File.ReadAllBytes(lib.FullPath(first.Item)).SequenceEqual(original),"duplicate decisions never modify source/canonical bytes");
    var request=new Dictionary<string,object>{{"action","save"},{"data",Convert.ToBase64String(original)},{"name","Browser candidate"},{"source","https://example.com/page"},{"folder",folder2}};
    before=Changes(lib);files=Files(lib);var response=Response(NativeBridge.Handle(lib,request));
    check((bool)response["ok"]&&(bool)response["choiceRequired"]&&(bool)response["duplicate"],"native save defaults to explicit duplicate choice");
    var existing=(Dictionary<string,object>)response["existing"];var incoming=(Dictionary<string,object>)response["incoming"];
    check((string)existing["id"]==canonicalId&&(string)existing["name"]=="Canonical"&&(string)incoming["name"]=="Browser candidate","native comparison includes both identities");
    check(Convert.ToInt32(existing["width"])==96&&Convert.ToInt32(incoming["height"])==64&&(string)existing["type"]=="PNG","native comparison includes image dimensions/type");
    string thumbnail=(string)incoming["thumbnail"];check(thumbnail.StartsWith("data:image/png;base64,")&&thumbnail.Length<350000,"native preview is bounded PNG data URI");
    check(Changes(lib)==before&&Files(lib)==files&&Directory.GetFiles(Path.Combine(lib.Root,".mori","thumbs")).Length==0,"native ask has no persistent side effects");
    check(Encoding.UTF8.GetByteCount(new JavaScriptSerializer().Serialize(response))<1024*1024,"native duplicate response fits host message limit");
    request["duplicateAction"]="overwrite";failed=false;try{NativeBridge.Handle(lib,request);}catch(InvalidDataException){failed=true;}
    check(failed&&Changes(lib)==before,"native rejects unsupported duplicate action");
    request["duplicateAction"]="keep";response=Response(NativeBridge.Handle(lib,request));
    check((bool)response["keptCopy"]&&!(bool)response["choiceRequired"]&&lib.All().Count==count+1,"native explicit keep commits another copy");
    request["duplicateAction"]="reuse";response=Response(NativeBridge.Handle(lib,request));
    check(!(bool)response["keptCopy"]&&!(bool)response["choiceRequired"]&&(string)response["id"]==canonicalId&&lib.All().Count==count+1,"native explicit reuse returns canonical");
    string fresh=Path.Combine(output,"fresh.png");Draw(fresh,Color.DarkSeaGreen);before=Changes(lib);files=Files(lib);
    skip=lib.Import(fresh,"",null,"","","",null,"skip");check(skip.Skipped&&!skip.Duplicate&&skip.Item==null&&Changes(lib)==before&&Files(lib)==files,"skip new item never imports");
    request.Remove("duplicateAction");request["data"]=Convert.ToBase64String(File.ReadAllBytes(fresh));response=Response(NativeBridge.Handle(lib,request));
    check(!(bool)response["duplicate"]&&!(bool)response["choiceRequired"]&&lib.All().Count==count+2,"native nonduplicate imports without extra confirmation");
   }
   string concurrent=Path.Combine(output,"concurrent");using(var initialize=new Library(concurrent)){}
   var results=new ImportResult[2];Task.WaitAll(Enumerable.Range(0,2).Select(i=>Task.Run(delegate{using(var lib=new Library(concurrent))results[i]=lib.Import(source,"",null,"","","",null,"ask");})).ToArray());
   using(var lib=new Library(concurrent))check(lib.All().Count==1&&results.Count(r=>r.ChoiceRequired)==1,"independent connections serialize canonical duplicate decision");
  }
 }
}
