using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Mori {
 public static class DuplicateTests {
  static void Draw(string path,Color color){using(var bitmap=new Bitmap(96,64)){using(var graphics=Graphics.FromImage(bitmap))graphics.Clear(color);bitmap.Save(path,ImageFormat.Png);}}
  static Dictionary<string,object> Response(object value){var json=new JavaScriptSerializer();return json.Deserialize<Dictionary<string,object>>(json.Serialize(value));}
  static string Changes(Library lib){return lib.Db.Query("SELECT total_changes() AS count")[0]["count"];}
  static string Files(Library lib){return string.Join("\n",Directory.GetFiles(Path.Combine(lib.Root,"原图"),"*",SearchOption.AllDirectories).OrderBy(x=>x));}
  static string Snapshot(Library lib){return new JavaScriptSerializer().Serialize(new object[]{lib.Db.Query("SELECT * FROM assets ORDER BY id"),lib.Db.Query("SELECT * FROM memberships ORDER BY asset,folder")});}
  static bool Rejected(Action action){try{action();return false;}catch(IOException){return true;}}
  [DllImport("kernel32.dll",EntryPoint="CreateSymbolicLinkW",CharSet=CharSet.Unicode,SetLastError=true)]
  [return:MarshalAs(UnmanagedType.I1)]
  static extern bool CreateSymbolicLink(string link,string target,uint flags);
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
   RecoverOriginal(Path.Combine(output,"recover-original"),source,original,check);
  }
  static void RecoverOriginal(string output,string source,byte[] original,Action<bool,string> check){
   Directory.CreateDirectory(output);DateTime sourceTime=File.GetLastWriteTimeUtc(source);
   using(var lib=new Library(Path.Combine(output,"library"))){
    string firstFolder=lib.CreateFolder("Original",""),secondFolder=lib.CreateFolder("Recovered","");
    var item=lib.Import(source,firstFolder,"Original name","https://example.com/original","original tags","original notes","2020-01-02T03:04:05Z").Item;
    string path=lib.FullPath(item),added=item.Added,created=item.Created;lib.Trash(new[]{item.Id},true);File.Delete(path);
    var request=new Dictionary<string,object>{{"action","save"},{"data",Convert.ToBase64String(original)},{"name","Replacement name"},{"source","https://example.com/incoming"},{"folder",secondFolder},{"duplicateAction","reuse"}};
    var response=Response(NativeBridge.Handle(lib,request));var restored=lib.All().Single();
    check((bool)response["ok"]&&(bool)response["duplicate"]&&(string)response["id"]==item.Id&&File.ReadAllBytes(path).SequenceEqual(original),"native duplicate reuse restores a missing original before reporting success");
    check(restored.Name=="Original name"&&restored.Source=="https://example.com/original"&&restored.Tags=="original tags"&&restored.Notes=="original notes"&&restored.Added==added&&restored.Created==created&&!restored.Deleted,"missing-original recovery keeps canonical identity and original metadata");
    check(lib.Members(firstFolder,false).Contains(item.Id)&&lib.Members(secondFolder,false).Contains(item.Id)&&lib.All().Count==1,"missing-original recovery preserves old categories and adds only the chosen one");
    File.Delete(path);Directory.Delete(Path.GetDirectoryName(path));
    var recovered=lib.Import(source,"",null,"","","",null,"reuse");
    check(recovered.Item.Id==item.Id&&File.ReadAllBytes(path).SequenceEqual(original),"duplicate reuse safely recreates a missing original directory");
    DateTime originalTime=File.GetLastWriteTimeUtc(path);var sameFile=lib.Import(path,"",null,"","","",null,"reuse");
    check(sameFile.Item.Id==item.Id&&lib.All().Count==1&&File.GetLastWriteTimeUtc(path)==originalTime&&File.ReadAllBytes(path).SequenceEqual(original),"importing the canonical path itself reuses its already-hashed read handle without replacing original bytes");
    byte[] changed=(byte[])original.Clone();changed[changed.Length-1]^=1;File.WriteAllBytes(path,changed);lib.Trash(new[]{item.Id},true);string before=Snapshot(lib);
    check(Rejected(()=>lib.Import(source,secondFolder,"Changed","https://example.com/change","changed","changed",null,"reuse",true)),"byte-changed canonical original rejects reuse instead of silently succeeding");
    check(Snapshot(lib)==before&&File.ReadAllBytes(path).SequenceEqual(changed),"rejected changed-original reuse preserves every metadata field and external file bytes");
    File.Delete(path);Directory.CreateDirectory(path);before=Snapshot(lib);
    check(Rejected(()=>NativeBridge.Handle(lib,request))&&Snapshot(lib)==before&&Directory.Exists(path)&&!Directory.EnumerateFileSystemEntries(path).Any(),"directory occupying original path rejects native reuse without writes or revival");Directory.Delete(path);
    lib.Db.Exec("CREATE TRIGGER reject_revival BEFORE UPDATE OF deleted ON assets BEGIN SELECT RAISE(ABORT, 'intentional revival rollback'); END;");before=Snapshot(lib);bool failed=false;
    try{lib.Import(source,secondFolder,null,"","","",null,"reuse");}catch{failed=true;}
    check(failed&&Snapshot(lib)==before&&File.ReadAllBytes(path).SequenceEqual(original),"failed metadata commit rolls back revival/categories but retains safely restored original");
    check(!Directory.GetFiles(Path.GetDirectoryName(path),".nook-restore-*.partial").Any(),"successful repair followed by metadata failure leaves no pending file");lib.Db.Exec("DROP TRIGGER reject_revival");
    check(lib.Import(source,"",null,"","","",null,"reuse").Item.Id==item.Id&&!lib.All().Single().Deleted,"retry after rejected revival reuses the restored canonical safely");
    File.Delete(path);
    using(var locked=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read)){
     var results=new ImportResult[2];Task.WaitAll(Enumerable.Range(0,2).Select(i=>Task.Run(delegate{using(var connection=new Library(lib.Root))results[i]=connection.Import(source,"",null,"","","",null,"reuse");})).ToArray());
     check(results.All(r=>r.Duplicate&&r.Item.Id==item.Id)&&lib.All().Count==1&&File.ReadAllBytes(path).SequenceEqual(original),"independent connections serialize missing-original repair without duplicate copies");
    }
    check(!Directory.GetFiles(Path.GetDirectoryName(path),".nook-restore-*.partial").Any(),"concurrent original recovery cleans all of its unique pending files");
    File.Delete(path);string relative=item.Relative,outside=Path.Combine(output,"outside.png");File.WriteAllBytes(outside,changed);
    foreach(string bad in new[]{Path.GetFullPath(outside),Path.Combine("..","outside.png"),Path.Combine(".mori","protected.png"),Path.Combine("原图","alternate.png:stream")}){
     lib.Db.Exec("UPDATE assets SET path="+Database.Q(bad)+",deleted=1 WHERE id="+Database.Q(item.Id));before=Snapshot(lib);
     check(Rejected(()=>lib.Import(source,secondFolder,null,"https://example.com/change","","",null,"reuse",true))&&Snapshot(lib)==before,"unsafe stored original path rejects reuse before any metadata changes: "+bad);
    }
    check(File.ReadAllBytes(outside).SequenceEqual(changed)&&!File.Exists(Path.Combine(lib.Root,".mori","protected.png")),"unsafe recovery never writes outside the owned original directory");
    lib.Db.Exec("UPDATE assets SET path="+Database.Q(relative)+" WHERE id="+Database.Q(item.Id));before=Snapshot(lib);
    bool linked=CreateSymbolicLink(path,outside,2);
    if(linked){try{
     check(Rejected(()=>NativeBridge.Handle(lib,request))&&Snapshot(lib)==before&&File.ReadAllBytes(outside).SequenceEqual(changed)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0,"changed symbolic-link original rejects native reuse without replacing its outside target");
     File.WriteAllBytes(outside,original);DateTime outsideTime=File.GetLastWriteTimeUtc(outside);response=Response(NativeBridge.Handle(lib,request));
     check((bool)response["ok"]&&(string)response["id"]==item.Id&&File.ReadAllBytes(outside).SequenceEqual(original)&&File.GetLastWriteTimeUtc(outside)==outsideTime&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0,"matching symbolic-link original permits read-only reuse without modifying target or link");
    }finally{File.Delete(path);}}
    else check(true,"SKIP symbolic-link original fixture: Windows did not permit link creation (error "+Marshal.GetLastWin32Error()+")");
    string missingTarget=Path.Combine(output,"missing-link-target.png");linked=CreateSymbolicLink(path,missingTarget,2);
    if(linked){try{before=Snapshot(lib);check(Rejected(()=>NativeBridge.Handle(lib,request))&&Snapshot(lib)==before&&!File.Exists(missingTarget)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0,"dangling original link rejects recovery without creating an outside target or removing the link");}finally{File.Delete(path);}}
    else check(true,"SKIP dangling-link original fixture: Windows did not permit link creation (error "+Marshal.GetLastWin32Error()+")");
    string outsideDirectory=Path.Combine(output,"outside-directory"),link=Path.Combine(lib.Root,"原图","linked-directory");Directory.CreateDirectory(outsideDirectory);
    linked=CreateSymbolicLink(link,outsideDirectory,3);
    if(linked){
     try{
      string linkedOriginal=Path.Combine(outsideDirectory,"existing.png");File.WriteAllBytes(linkedOriginal,original);DateTime linkedTime=File.GetLastWriteTimeUtc(linkedOriginal);
      lib.Db.Exec("UPDATE assets SET path="+Database.Q(Path.Combine("原图","linked-directory","existing.png"))+" WHERE id="+Database.Q(item.Id));response=Response(NativeBridge.Handle(lib,request));
      check((bool)response["ok"]&&(string)response["id"]==item.Id&&File.ReadAllBytes(linkedOriginal).SequenceEqual(original)&&File.GetLastWriteTimeUtc(linkedOriginal)==linkedTime,"existing matching original under a linked directory remains reusable without rewriting it");
      lib.Db.Exec("UPDATE assets SET path="+Database.Q(Path.Combine("原图","linked-directory","missing.png"))+" WHERE id="+Database.Q(item.Id));before=Snapshot(lib);
      check(Rejected(()=>lib.Import(source,secondFolder,null,"","","",null,"reuse"))&&Snapshot(lib)==before&&!File.Exists(Path.Combine(outsideDirectory,"missing.png"))&&Directory.GetFiles(outsideDirectory).Length==1&&File.ReadAllBytes(linkedOriginal).SequenceEqual(original),"reparse-point ancestor still rejects recovery writes without changing existing outside files");
     }finally{Directory.Delete(link);}
    }else check(true,"SKIP symbolic-link directory fixture: Windows did not permit link creation (error "+Marshal.GetLastWin32Error()+")");
   }
   string linkedLibrary=Path.Combine(output,"linked-library"),actualLibrary=Path.Combine(output,"actual-library");Directory.CreateDirectory(actualLibrary);
   bool libraryLinked=CreateSymbolicLink(linkedLibrary,actualLibrary,3);
   if(libraryLinked){try{using(var linked=new Library(linkedLibrary)){
    var stored=linked.Import(source,"","linked library original","","","").Item;string storedPath=linked.FullPath(stored);DateTime storedTime=File.GetLastWriteTimeUtc(storedPath);
    var reused=linked.Import(source,"",null,"","","",null,"reuse");
    check(reused.Item.Id==stored.Id&&linked.All().Count==1&&File.ReadAllBytes(storedPath).SequenceEqual(original)&&File.GetLastWriteTimeUtc(storedPath)==storedTime,"library rooted through a directory link permits ordinary duplicate reuse without rewriting original bytes");
    File.Delete(storedPath);string before=Snapshot(linked);
    check(Rejected(()=>linked.Import(source,"",null,"","","",null,"reuse"))&&Snapshot(linked)==before&&!File.Exists(storedPath),"missing original under a linked library root rejects recovery writes and leaves metadata unchanged");
   }}finally{Directory.Delete(linkedLibrary);}}
   else check(true,"SKIP linked-library fixture: Windows did not permit link creation (error "+Marshal.GetLastWin32Error()+")");
   check(File.ReadAllBytes(source).SequenceEqual(original)&&File.GetLastWriteTimeUtc(source)==sourceTime,"all duplicate recovery decisions preserve incoming file bytes and modification time");
  }
 }
}
