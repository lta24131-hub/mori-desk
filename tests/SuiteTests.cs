using System;
using System.IO;
using System.Linq;
using System.Text;
using Mori;
namespace MoriDesk {
 public static class SuiteTests {
  static int checks;
  static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
  static void Reject(Action action,string message){bool rejected=false;try{action();}catch{rejected=true;}Check(rejected,message);}
  public static int Run(){string root=Path.Combine(Path.GetTempPath(),"MoriDesk-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);string log=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"suite-tests.txt");try{
   string source=Path.Combine(root,"原始文件");Directory.CreateDirectory(source);string a=Path.Combine(source,"装配.step"),b=Path.Combine(source,"图纸'B.stl");File.WriteAllText(a,"REFERENCE FILE CONTENT; NEVER COPY",Encoding.UTF8);File.WriteAllText(b,"solid test\nendsolid test");var time=File.GetLastWriteTimeUtc(a);byte[] bytes=File.ReadAllBytes(a);string library=Path.Combine(root,"library");
   using(var lib=new Library(library)){
    var refs=new ReferenceStore(lib.Db);Check(refs.All().Count==0,"empty store");string f=refs.AddFolder("项目 A"),g=refs.AddFolder("另一个项目");Check(refs.Folders().Count==2,"create folders");Check(refs.Add(new[]{a,b},f)==2,"batch add");Check(refs.Add(new[]{a.ToUpperInvariant()},g)==0,"case-insensitive duplicate");Check(refs.All().All(r=>r.Folder==f),"duplicate doesn't move silently");Check(lib.All().Count==0,"refs aren't library assets");Check(Directory.GetFiles(Path.Combine(library,"原图"),"*",SearchOption.AllDirectories).Length==0,"no copies created");var first=refs.All().First(r=>r.Path==a);Check(first.Exists&&!first.Directory&&first.Kind=="STEP","file metadata");
    refs.Move(refs.All().Select(r=>r.Id),g);Check(refs.All().All(r=>r.Folder==g),"batch classify");Check(refs.Undo()&&refs.All().All(r=>r.Folder==f),"undo classify");refs.RenameFolder(f,"新项目");Check(refs.Folders().Any(r=>r.Name=="新项目"),"rename folder");refs.DeleteFolder(f);Check(refs.All().Count==2&&refs.All().All(r=>r.Folder==""),"delete folder unfiles references");Check(refs.Undo()&&refs.All().All(r=>r.Folder==f),"undo folder deletion");
    refs.Remove(refs.All().Select(r=>r.Id));Check(refs.All().Count==0,"batch remove");Check(File.Exists(a)&&File.Exists(b),"remove preserves originals");Check(refs.Undo()&&refs.All().Count==2,"undo remove");Check(refs.Add(new[]{source},"")==1,"folder reference");Check(refs.All().Count==3,"folder not recursively indexed");Check(refs.All().Single(r=>r.Directory).Exists,"folder exists");
    string moved=Path.Combine(source,"重新定位.step");File.Move(a,moved);Check(!first.Exists,"missing reference");refs.Relink(first.Id,moved);Check(refs.All().Single(r=>r.Id==first.Id).Exists,"relink");Check(refs.Undo()&&!refs.All().Single(r=>r.Id==first.Id).Exists,"undo relink");refs.Relink(first.Id,moved);Reject(()=>refs.Relink(first.Id,b),"relink collision rejected");Check(refs.All().Single(r=>r.Id==first.Id).Path==moved,"relink rollback");
    Reject(()=>refs.Add(new[]{b,Path.Combine(root,"missing.step")},""),"invalid batch rejected");Check(refs.All().Count==3,"invalid batch no partial result");Reject(()=>refs.Move(new[]{first.Id},"missing-category"),"invalid category rejected");Reject(()=>refs.AddFolder(" "),"blank folder rejected");Reject(()=>refs.AddFolder("新项目"),"duplicate folder rejected");Reject(()=>ReferenceStore.Normalize("relative.step"),"relative path rejected");Check(ReferenceStore.Normalize(source+"\\")==source,"normalize separator");Check(new ReferenceStore(lib.Db).All().Count==3,"persistence");Check(File.ReadAllBytes(moved).SequenceEqual(bytes)&&File.GetLastWriteTimeUtc(moved)==time,"original bytes and timestamp unchanged");
    string backup=Path.Combine(root,"test.mori.zip");lib.Backup(backup);Check(File.Exists(backup),"backup covers reference DB");
   }
   Check(Settings.ConfigDir.EndsWith("MoriDesk"),"independent settings");Check(new Settings().LibraryPath.StartsWith(Settings.ConfigDir),"independent library");Check(QingJie.OcrLanguagePacks.Folder.StartsWith(Settings.ConfigDir),"independent languages");Check(QingJie.OfflinePacks.Folder.StartsWith(Settings.ConfigDir),"independent offline packs");Check(StartupRegistration.ValueName=="MoriDesk","independent startup");foreach(string ext in FileRouter.Models)Check(FileRouter.Kind("test"+ext)=="model","route "+ext);foreach(string ext in FileRouter.Pictures)Check(FileRouter.Kind("test"+ext)=="image","route "+ext);Check(FileRouter.Kind("document.pdf")=="external","external fallback");
   IterationTests.Run(root,Check);
   File.WriteAllText(log,"PASS "+checks+" checks\r\nFixtures: "+root+"\r\n"+DateTime.Now.ToString("O"),Encoding.UTF8);return 0;
  }catch(Exception ex){File.WriteAllText(log,"FAIL after "+checks+" checks\r\n"+ex+"\r\nFixtures: "+root,Encoding.UTF8);return 1;}}
 }
}
