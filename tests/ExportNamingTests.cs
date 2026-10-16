using System;
using System.IO;
using System.Linq;
using Mori;

namespace MoriDesk {
 public static class ExportNamingTests {
  public static void Run(string root,Action<bool,string> check){
   string sources=Path.Combine(root,"sources");Directory.CreateDirectory(sources);byte[] bytes={137,80,78,71,13,10,26,10,1,2,3,4};
   string image=Path.Combine(sources,"参考图.png"),upper=Path.Combine(sources,"大写图片.PNG");File.WriteAllBytes(image,bytes);File.WriteAllBytes(upper,new byte[]{1,2,3,4,5});var written=File.GetLastWriteTimeUtc(image);
   using(var lib=new Library(Path.Combine(root,"library"))){
    lib.EnableReferenceAssets();var referenceId=lib.AddReferenceAssets(new[]{image},"").Single();var reference=lib.All().Single(a=>a.Id==referenceId);var managed=lib.Import(image,"","图库图片","","","").Item;
    string target=Path.Combine(root,"flat");int count=lib.Export(new[]{reference,managed},target,false,"");
    check(count==2&&File.Exists(Path.Combine(target,"参考图.png"))&&!File.Exists(Path.Combine(target,"参考图.png.png")),"ordinary local shortcut exports with exactly its existing extension");
    check(File.ReadAllBytes(Path.Combine(target,"参考图.png")).SequenceEqual(bytes)&&File.ReadAllBytes(Path.Combine(target,"图库图片.png")).SequenceEqual(bytes),"reference and managed exports preserve exact original bytes");
    string upperId=lib.AddReferenceAssets(new[]{upper},"").Single();var uppercase=lib.All().Single(a=>a.Id==upperId);target=Path.Combine(root,"uppercase");lib.Export(new[]{uppercase},target,false,"");
    check(File.Exists(Path.Combine(target,"大写图片.PNG"))&&Directory.GetFiles(target).Length==1,"extension matching is case-insensitive and preserves the existing display filename");
    managed.Name="logo.final";lib.Update(managed);target=Path.Combine(root,"dotted");lib.Export(new[]{managed},target,false,"");check(File.Exists(Path.Combine(target,"logo.final.png")),"a different dotted suffix in a display name is preserved before the true extension");
    managed.Name="手工名称.PNG";lib.Update(managed);target=Path.Combine(root,"named-extension");lib.Export(new[]{managed},target,false,"");check(File.Exists(Path.Combine(target,"手工名称.PNG"))&&Directory.GetFiles(target).Length==1,"managed asset display names that already have the correct suffix also avoid duplication");
    target=Path.Combine(root,"numbered");lib.Export(new[]{reference,managed},target,false,"batch.png");check(File.Exists(Path.Combine(target,"batch.png-0001.png"))&&File.Exists(Path.Combine(target,"batch.png-0002.png")),"numbered prefix export still applies per-item true extensions without stripping prefix text");
    string parent=lib.CreateFolder("项目",""),child=lib.CreateFolder("参考",parent);lib.AddToFolder(new[]{reference.Id},child);target=Path.Combine(root,"hierarchy");lib.Export(new[]{reference},target,true,"");check(File.Exists(Path.Combine(target,"项目","参考","参考图.png")),"hierarchical shortcut export keeps category paths and a single extension");
    target=Path.Combine(root,"collision");Directory.CreateDirectory(target);string existing=Path.Combine(target,"参考图.png");File.WriteAllText(existing,"keep existing");lib.Export(new[]{reference},target,false,"");check(File.ReadAllText(existing)=="keep existing"&&File.ReadAllBytes(Path.Combine(target,"参考图 (2).png")).SequenceEqual(bytes),"same-name export never overwrites an existing destination and numbers before the real extension");
    string vanished=Path.Combine(sources,"暂不可用.png");File.WriteAllBytes(vanished,bytes);var missingId=lib.AddReferenceAssets(new[]{vanished},"").Single();var missing=lib.All().Single(a=>a.Id==missingId);File.Delete(vanished);target=Path.Combine(root,"missing-batch");bool rejected=false;try{lib.Export(new[]{reference,missing,managed},target,false,"");}catch(FileNotFoundException){rejected=true;}
    check(rejected&&File.ReadAllBytes(Path.Combine(target,"参考图.png")).SequenceEqual(bytes)&&!File.Exists(Path.Combine(target,"暂不可用.png")),"missing-source batch export reports failure without corrupting prior successful output or creating a false missing-file copy");
    check(lib.All().Any(a=>a.Id==missingId)&&!File.Exists(vanished),"failed export retains the missing shortcut for later relocation and never recreates its original");
    check(File.ReadAllBytes(image).SequenceEqual(bytes)&&File.GetLastWriteTimeUtc(image)==written,"all export naming and failure checks leave source bytes and modification time unchanged");
   }
  }
 }
}
