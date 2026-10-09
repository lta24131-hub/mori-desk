using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Mori {
 public static class LibraryTransferTests {
  static bool Rejected(Action action){try{action();return false;}catch{return true;}}
  static string Digest(string path){using(var input=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(input));}
  static string Metadata(Library lib){return string.Join("\n",new[]{"assets","folders","memberships"}.SelectMany(table=>lib.Db.Query("SELECT * FROM "+table).Select(row=>table+":"+string.Join("|",row.OrderBy(pair=>pair.Key).Select(pair=>pair.Key+"="+pair.Value))).OrderBy(row=>row)));}
  static bool NoStage(string output){return Directory.GetDirectories(output,".mori-transfer-*.partial").Length==0;}
  public static void Run(string output,Action<bool,string> check){
   check(LibraryTransfer.RequiredBytes(0,0,0)==0,"transfer space estimate supports empty library components");
   check(LibraryTransfer.RequiredBytes(123456,4096,789)==128341,"transfer space estimate includes originals, database and thumbnail cache");
   check(LibraryTransfer.RequiredBytes(long.MaxValue-20,10,10)==long.MaxValue,"transfer space estimate preserves large exact byte totals");
   check(Rejected(()=>LibraryTransfer.RequiredBytes(long.MaxValue,1,0)),"transfer space estimate rejects overflow");
   check(Rejected(()=>LibraryTransfer.RequiredBytes(-1,0,0)),"transfer space estimate rejects negative component sizes");
   Directory.CreateDirectory(output);string image=Path.Combine(output,"迁移样本.png");using(var bitmap=new Bitmap(96,64)){using(var graphics=Graphics.FromImage(bitmap))graphics.Clear(Color.DarkSeaGreen);bitmap.Save(image,ImageFormat.Png);}
   string root=Path.Combine(output,"原图库 with spaces");
   using(var source=new Library(root)){
    string parent=source.CreateFolder("父分类",""),folder=source.CreateFolder("子分类",parent);
    var first=source.Import(image,folder,"保留素材","https://example.com/original","标签甲, 标签乙","原备注","2020-01-02T03:04:05Z").Item;
    first.Favorite=true;source.Update(first);source.AddToFolder(new[]{first.Id},parent);source.BuildThumbnail(first);
    var duplicate=source.Import(image,folder,"手动保留的副本","https://example.com/copy","副本标签","副本备注",null,"keep").Item;source.Trash(new[]{duplicate.Id},true);
    string metadata=Metadata(source);var originalHashes=source.All().ToDictionary(item=>item.Id,item=>Digest(source.FullPath(item)));string target=Path.Combine(output,"目标图库 Unicode");bool activated=false;
    string result=LibraryTransfer.Transfer(source,target,null,()=>false,path=>{activated=true;check(File.Exists(Path.Combine(path,".mori","library.db")),"transfer activation runs only after final database exists");});
    check(activated&&LibraryTransfer.SameRoot(result,target),"transfer returns normalized committed destination");
    using(var copied=new Library(target)){
     check(Metadata(copied)==metadata,"transfer preserves all asset metadata, folders and memberships");
     check(copied.All().Count==2&&copied.All().Count(item=>item.Deleted)==1,"transfer includes trash and deliberately kept duplicates");
     check(copied.All().All(item=>Digest(copied.FullPath(item))==originalHashes[item.Id]),"all transferred originals retain exact SHA-256");
     check(copied.All().All(item=>File.GetCreationTimeUtc(copied.FullPath(item))==File.GetCreationTimeUtc(source.FullPath(item))&&File.GetLastWriteTimeUtc(copied.FullPath(item))==File.GetLastWriteTimeUtc(source.FullPath(item))),"transferred original file timestamps are preserved");
     check(File.Exists(copied.ThumbnailPath(first))&&Digest(copied.ThumbnailPath(first))==Digest(source.ThumbnailPath(first)),"transfer preserves complete cached thumbnails");
     check(copied.Db.Query("PRAGMA integrity_check")[0]["integrity_check"]=="ok","transferred SQLite integrity is valid");
    }
    check(Metadata(source)==metadata&&source.All().All(item=>Digest(source.FullPath(item))==originalHashes[item.Id]),"successful transfer never deletes or alters original library data");
    check(NoStage(output),"successful transfer leaves no staging directory");
    string empty=Path.Combine(output,"existing-empty");Directory.CreateDirectory(empty);LibraryTransfer.Transfer(source,empty,null,null);check(File.Exists(Path.Combine(empty,".mori","library.db")),"existing empty destination is committed without overwrite");
    check(Rejected(()=>LibraryTransfer.ValidateDestination(root,root)),"transfer rejects source as destination");
    check(Rejected(()=>LibraryTransfer.ValidateDestination(root,Path.Combine(root,"nested"))),"transfer rejects destination inside source");
    check(Rejected(()=>LibraryTransfer.ValidateDestination(root,output)),"transfer rejects source inside destination");
    check(Rejected(()=>LibraryTransfer.ValidateDestination(root,Path.GetPathRoot(root))),"transfer rejects disk root destination");
    check(Rejected(()=>LibraryTransfer.ValidateDestination(root,"relative")),"transfer rejects relative destination");
    string occupied=Path.Combine(output,"occupied");Directory.CreateDirectory(occupied);string userFile=Path.Combine(occupied,"user.txt");File.WriteAllText(userFile,"keep me");
    check(Rejected(()=>LibraryTransfer.Transfer(source,occupied,null,null))&&File.ReadAllText(userFile)=="keep me","transfer never overwrites a nonempty destination");
    string cancelled=Path.Combine(output,"cancelled");check(Rejected(()=>LibraryTransfer.Transfer(source,cancelled,null,()=>true))&&!Directory.Exists(cancelled)&&NoStage(output),"cancel before transfer creates no final or staging data");
    string raced=Path.Combine(output,"raced");Directory.CreateDirectory(raced);string racedFile=Path.Combine(raced,"arrived.txt");
    check(Rejected(()=>LibraryTransfer.Transfer(source,raced,message=>{if(message.StartsWith("校验通过"))File.WriteAllText(racedFile,"new user data");},null))&&File.ReadAllText(racedFile)=="new user data"&&NoStage(output),"new destination content arriving before commit is retained and blocks transfer");
    string failedActivation=Path.Combine(output,"activation-failed");check(Rejected(()=>LibraryTransfer.Transfer(source,failedActivation,null,null,path=>{throw new IOException("synthetic activation failure");}))&&File.Exists(Path.Combine(failedActivation,".mori","library.db")),"activation failure preserves verified destination for recovery");
    check(Metadata(source)==metadata&&NoStage(output),"activation failure preserves source and leaves no partial stage");
    string changed=Path.Combine(output,"source-updated");bool updated=false;
    check(Rejected(()=>LibraryTransfer.Transfer(source,changed,message=>{if(!updated&&message.StartsWith("复制并校验原图")){updated=true;source.CreateFolder("concurrent same connection","");}},null))&&!Directory.Exists(changed)&&NoStage(output),"same-connection changes during copy abort before activation");
    string otherChanged=Path.Combine(output,"external-updated");updated=false;
    check(Rejected(()=>LibraryTransfer.Transfer(source,otherChanged,message=>{if(!updated&&message.StartsWith("复制并校验原图")){updated=true;using(var other=new Library(root))other.CreateFolder("concurrent other connection","");}},null))&&!Directory.Exists(otherChanged)&&NoStage(output),"other-connection changes during copy abort before activation");
    string original=source.FullPath(first);byte[] valid=File.ReadAllBytes(original),corrupt=(byte[])valid.Clone();corrupt[corrupt.Length-1]^=1;File.WriteAllBytes(original,corrupt);
    string corruptTarget=Path.Combine(output,"corrupt-source");check(Rejected(()=>LibraryTransfer.Transfer(source,corruptTarget,null,null))&&!Directory.Exists(corruptTarget)&&File.ReadAllBytes(original).SequenceEqual(corrupt)&&NoStage(output),"SHA mismatch aborts without changing source or committing corrupt copy");File.WriteAllBytes(original,valid);
    source.Db.Exec("UPDATE assets SET path='原图/../outside.png' WHERE id="+Database.Q(first.Id));string escaped=Path.Combine(output,"escaped-source");
    check(Rejected(()=>LibraryTransfer.Transfer(source,escaped,null,null))&&!Directory.Exists(escaped)&&NoStage(output),"indexed paths escaping original folder are rejected");source.Db.Exec("UPDATE assets SET path="+Database.Q(first.Relative)+" WHERE id="+Database.Q(first.Id));
    using(var guard=LibraryTransfer.TryAcquireWrite(root)){
     check(guard!=null,"transfer writer lease acquired");bool blocked=Task.Run(()=>{using(var second=LibraryTransfer.TryAcquireWrite(root.ToUpperInvariant()))return second==null;}).Result;
     check(blocked,"writer lease excludes other threads with case-normalized paths");
    }
    using(var guard=LibraryTransfer.TryAcquireWrite(root))check(guard!=null,"writer lease releases after disposal");
    string linkParent=Path.Combine(output,"junction-parent");if(Directory.Exists(linkParent)&&(File.GetAttributes(linkParent)&FileAttributes.ReparsePoint)!=0){check(Rejected(()=>LibraryTransfer.ValidateDestination(root,Path.Combine(linkParent,"target"))),"destination junction ancestor is rejected");check(Rejected(()=>LibraryTransfer.ValidateDestination(Path.Combine(linkParent,"source"),Path.Combine(output,"junction-target"))),"source junction ancestor is rejected");}
   }
   // A large synthetic file checks cancellation between copy chunks.
   string large=Path.Combine(output,"large.mp4");using(var stream=File.Create(large)){byte[] bytes=new byte[262144];for(int index=0;index<12;index++){bytes[0]=(byte)index;stream.Write(bytes,0,bytes.Length);}}
   using(var source=new Library(Path.Combine(output,"cancel-source"))){source.Import(large,"","synthetic","","","");string originalDigest=Digest(large);bool copying=false;int polls=0;string target=Path.Combine(output,"cancel-during-copy");
    check(Rejected(()=>LibraryTransfer.Transfer(source,target,message=>{if(message.StartsWith("复制并校验原图"))copying=true;},()=>copying&&++polls>6))&&!Directory.Exists(target)&&NoStage(output),"chunk cancellation removes only owned staging data");
    check(Digest(large)==originalDigest&&source.All().Count==1&&File.Exists(source.FullPath(source.All()[0])),"cancelled copy preserves input and original library");
   }
  }
 }
}
