using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace Mori {
 public static class CreationMetadataTests {
  public static List<string> Run(string output){
   Directory.CreateDirectory(output);var log=new List<string>();
   Action<bool,string> check=delegate(bool ok,string name){if(!ok)throw new Exception("FAIL: "+name);log.Add("PASS: "+name);};
   string fixture=Path.Combine(output,"created-source.png");File.WriteAllBytes(fixture,new byte[]{1,2,3,4});
   File.SetCreationTimeUtc(fixture,new DateTime(2012,3,4,5,6,7,DateTimeKind.Utc));
   string expected=File.GetCreationTimeUtc(fixture).ToString("o",CultureInfo.InvariantCulture);
   string root=Path.Combine(output,"library");string id,backup=Path.Combine(output,"created-backup.zip");
   using(var library=new Library(root)){
    var first=library.Import(fixture,"","Original timestamp","","","");id=first.Item.Id;
    check(first.Item.Created==expected,"file import preserves the source creation timestamp");
    check(first.Item.Created!=first.Item.Added,"file creation and Mori import times are separate");
    File.SetCreationTimeUtc(fixture,DateTime.UtcNow);
    check(library.Import(fixture,"","Duplicate","","","").Item.Created==expected,"duplicate import does not replace an established creation timestamp");
    string temporary=Path.Combine(output,"temporary.png");File.WriteAllBytes(temporary,new byte[]{5,6,7,8});
    check(library.Import(temporary,"","Unknown source creation","","","","").Item.Created=="","temporary image imports can preserve unknown source creation time");
    library.Backup(backup);
   }
   using(var reopened=new Library(root))check(reopened.All().Single(a=>a.Id==id).Created==expected,"source creation time survives library reopen");
   string restored=Path.Combine(output,"restored");Library.Restore(backup,restored);
   using(var library=new Library(restored))check(library.All().Single(a=>a.Id==id).Created==expected,"backup and restore preserve source creation time");
   string oldRoot=Path.Combine(output,"legacy");Directory.CreateDirectory(Path.Combine(oldRoot,".mori"));Directory.CreateDirectory(Path.Combine(oldRoot,"原图"));
   string oldImage=Path.Combine(oldRoot,"原图","legacy.png");byte[] oldBytes=new byte[]{9,10,11,12};File.WriteAllBytes(oldImage,oldBytes);
   using(var oldDb=new Database(Path.Combine(oldRoot,".mori","library.db"))){
    oldDb.Exec("CREATE TABLE assets(id TEXT PRIMARY KEY, hash TEXT NOT NULL UNIQUE, name TEXT NOT NULL, path TEXT NOT NULL UNIQUE, ext TEXT NOT NULL, bytes INTEGER NOT NULL, tags TEXT DEFAULT '', notes TEXT DEFAULT '', source TEXT DEFAULT '', added TEXT NOT NULL, favorite INTEGER DEFAULT 0, deleted INTEGER DEFAULT 0, width INTEGER DEFAULT 0, height INTEGER DEFAULT 0, color TEXT DEFAULT '', perceptual TEXT DEFAULT '')");
    oldDb.Exec("INSERT INTO assets(id,hash,name,path,ext,bytes,added) VALUES('legacy','legacy-hash','Legacy image','原图/legacy.png','.png',4,'2020-01-02T03:04:05.0000000Z')");
   }
   using(var upgraded=new Library(oldRoot)){
    var legacy=upgraded.All().Single();
    check(legacy.Id=="legacy"&&legacy.Name=="Legacy image"&&legacy.Added=="2020-01-02T03:04:05.0000000Z","schema migration preserves existing asset metadata");
    check(legacy.Created=="","schema migration leaves historical creation time unknown");
    check(upgraded.Db.Query("PRAGMA table_info(assets)").Count(c=>c["name"]=="created")==1,"schema migration adds one creation column");
    check(File.ReadAllBytes(upgraded.FullPath(legacy)).SequenceEqual(oldBytes),"schema migration leaves original image bytes untouched");
    check(!string.IsNullOrEmpty(upgraded.Import(fixture,"","New after upgrade","","","").Item.Created),"new imports after schema migration retain creation time");
   }
   using(var reopened=new Library(oldRoot))check(reopened.All().Single(a=>a.Id=="legacy").Created==""&&reopened.All().Count==2,"creation schema migration is safe to run again");
   string eagle=Path.Combine(output,"eagle"),validInfo=Path.Combine(eagle,"images","valid.info"),invalidInfo=Path.Combine(eagle,"images","invalid.info");
   Directory.CreateDirectory(validInfo);Directory.CreateDirectory(invalidInfo);
   File.WriteAllBytes(Path.Combine(validInfo,"valid.png"),new byte[]{13,14,15,16});File.WriteAllBytes(Path.Combine(invalidInfo,"invalid.png"),new byte[]{17,18,19,20});
   var json=new JavaScriptSerializer();long btime=1234567890123;
   File.WriteAllText(Path.Combine(validInfo,"metadata.json"),json.Serialize(new{id="valid",ext="png",name="Eagle valid",btime=btime}));
   File.WriteAllText(Path.Combine(invalidInfo,"metadata.json"),json.Serialize(new{id="invalid",ext="png",name="Eagle invalid",btime="unknown"}));
   using(var library=new Library(Path.Combine(output,"eagle-target"))){
    library.MigrateEagle(eagle,delegate(string status){});
    string eagleExpected=new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddMilliseconds(btime).ToString("o",CultureInfo.InvariantCulture);
    check(library.All().Single(a=>a.Name=="Eagle valid").Created==eagleExpected,"Eagle migration preserves a valid original btime");
    check(library.All().Single(a=>a.Name=="Eagle invalid").Created=="","Eagle migration keeps invalid creation metadata unknown");
   }
   return log;
  }
 }
}
