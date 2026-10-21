using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Mori;

namespace MoriDesk {
 internal static class LibraryRestoreTests {
  static void Rewrite(string source,string output,Func<string,byte[],byte[]> transform,Action<ZipArchive> append=null){
   using(var input=ZipFile.OpenRead(source))using(var archive=ZipFile.Open(output,ZipArchiveMode.Create)){
    foreach(var entry in input.Entries){byte[] bytes;using(var file=entry.Open())using(var memory=new MemoryStream()){file.CopyTo(memory);bytes=memory.ToArray();}bytes=transform(entry.FullName,bytes);if(bytes==null)continue;using(var file=archive.CreateEntry(entry.FullName).Open())file.Write(bytes,0,bytes.Length);}
    if(append!=null)append(archive);
   }
  }
  static Exception Rejected(string archive,string target,string root,Action<bool,string> check,string label,Library.RestoreLimits limits=null){
   Exception failure=null;try{if(limits==null)Library.Restore(archive,target);else Library.Restore(archive,target,limits);}catch(Exception error){failure=error;}
   check(failure!=null,label);
   check(!Directory.Exists(target)||!Directory.EnumerateFileSystemEntries(target).Any(),label+" leaves the destination absent or empty and immediately retryable");
   check(Directory.GetDirectories(root,".nook-restore-*").Length==0,label+" cleans only its own staged extraction");
   return failure;
  }
  static bool StageContainsOriginal(string root){
   return Directory.GetDirectories(root,".nook-restore-*.partial").Any(stage=>Directory.Exists(Path.Combine(stage,"原图"))&&Directory.EnumerateFiles(Path.Combine(stage,"原图"),"*",SearchOption.AllDirectories).Any());
  }
  // Small synthetic archives only. Change central-directory lengths without
  // filling a disk or changing the actual compressed stream/local header.
  static void DeclareLength(string source,string output,string entryName,uint length){
   byte[] bytes=File.ReadAllBytes(source);int end=-1;
   for(int i=bytes.Length-22;i>=Math.Max(0,bytes.Length-65557);i--)if(BitConverter.ToUInt32(bytes,i)==0x06054b50&&i+22+BitConverter.ToUInt16(bytes,i+20)==bytes.Length){end=i;break;}
   if(end<0)throw new InvalidDataException("Synthetic ZIP end record missing.");
   int offset=checked((int)BitConverter.ToUInt32(bytes,end+16)),count=BitConverter.ToUInt16(bytes,end+10);bool changed=false;
   for(int i=0;i<count;i++){
    if(BitConverter.ToUInt32(bytes,offset)!=0x02014b50)throw new InvalidDataException("Synthetic ZIP central record missing.");
    int name=BitConverter.ToUInt16(bytes,offset+28),extra=BitConverter.ToUInt16(bytes,offset+30),comment=BitConverter.ToUInt16(bytes,offset+32);
    if(Encoding.UTF8.GetString(bytes,offset+46,name)==entryName){Buffer.BlockCopy(BitConverter.GetBytes(length),0,bytes,offset+24,4);changed=true;}
    offset+=46+name+extra+comment;
   }
   if(!changed)throw new InvalidDataException("Synthetic ZIP entry missing.");File.WriteAllBytes(output,bytes);
  }
  static void ResourceBudgets(string root,string backup,Action<bool,string> check){
   long total,indexLength;
   using(var archive=ZipFile.OpenRead(backup)){total=archive.Entries.Sum(entry=>entry.Length);indexLength=archive.GetEntry(".mori/library.db").Length;}
   const long reserve=4096;int probes=0;bool sawStage=false;
   Func<string,long?> ample=parent=>{probes++;sawStage|=Directory.GetDirectories(root,".nook-restore-*.partial").Length!=0;return long.MaxValue;};
   string existing=Path.Combine(root,"budget-existing-empty");Directory.CreateDirectory(existing);DateTime created=Directory.GetCreationTimeUtc(existing);
   Rejected(backup,existing,root,check,"expanded-byte budget rejects one byte over its limit",new Library.RestoreLimits(maximumExtractedBytes:total-1,minimumFreeBytes:reserve,availableBytes:ample));
   check(probes==0&&!sawStage&&Directory.GetCreationTimeUtc(existing)==created,"oversized declared totals are rejected before staging and preserve the existing empty directory");
   Rejected(backup,Path.Combine(root,"index-budget-target"),root,check,"metadata has a separate pre-extraction byte limit",new Library.RestoreLimits(maximumDatabaseBytes:indexLength-1,minimumFreeBytes:reserve,availableBytes:ample));
   check(probes==0&&!sawStage,"an oversized metadata index is rejected before any extraction or space probe");
   Rejected(backup,Path.Combine(root,"space-budget-target"),root,check,"preflight preserves the configured free-space reserve",new Library.RestoreLimits(minimumFreeBytes:reserve,availableBytes:parent=>total+reserve-1));
   string exact=Path.Combine(root,"exact-budget-target");Library.Restore(backup,exact,new Library.RestoreLimits(maximumDatabaseBytes:indexLength,maximumExtractedBytes:total,minimumFreeBytes:reserve,availableBytes:parent=>total+reserve));
   check(File.Exists(Path.Combine(exact,".mori","library.db")),"exact total, index and disk-reserve boundaries restore successfully without an off-by-one rejection");
   var unknown=new Func<string,long?>[]{parent=>null,parent=>-1,parent=>{throw new IOException("synthetic unavailable space probe");}};
   for(int i=0;i<unknown.Length;i++)Rejected(backup,Path.Combine(root,"unknown-space-"+i),root,check,"unknown, negative or throwing space probes fail closed "+i,new Library.RestoreLimits(minimumFreeBytes:reserve,availableBytes:unknown[i]));
   bool stoppedAfterIndex=false,originalSeen=false;
   string dynamicTarget=Path.Combine(root,"dynamic-space-empty");Directory.CreateDirectory(dynamicTarget);DateTime dynamicCreated=Directory.GetCreationTimeUtc(dynamicTarget);
   Rejected(backup,dynamicTarget,root,check,"a disk-space drop after metadata extraction stops before originals",new Library.RestoreLimits(minimumFreeBytes:reserve,availableBytes:parent=>{
    originalSeen|=StageContainsOriginal(root);
    foreach(string stage in Directory.GetDirectories(root,".nook-restore-*.partial")){string db=Path.Combine(stage,".mori","library.db");if(File.Exists(db)&&new FileInfo(db).Length==indexLength){stoppedAfterIndex=true;return reserve;}}
    return long.MaxValue;
   }));
   check(stoppedAfterIndex&&!originalSeen&&Directory.GetCreationTimeUtc(dynamicTarget)==dynamicCreated,"dynamic preflight failure never expands an original or replaces the user's empty directory");
   bool overflow=false,negative=false;try{Library.RestoreDeclaredBytes(new[]{long.MaxValue,1L},long.MaxValue);}catch(IOException){overflow=true;}try{Library.RestoreDeclaredBytes(new[]{-1L},long.MaxValue);}catch(IOException){negative=true;}
   check(overflow&&negative&&Library.RestoreDeclaredBytes(new[]{long.MaxValue},long.MaxValue)==long.MaxValue,"declared-byte accounting rejects negative lengths and checked overflow but accepts the exact Int64 boundary");

   string indexLast=Path.Combine(root,"invalid-index-last.zip");
   using(var archive=ZipFile.Open(indexLast,ZipArchiveMode.Create)){
    using(var file=archive.CreateEntry("原图/highly-compressed.png",CompressionLevel.Optimal).Open()){byte[] block=new byte[65536];for(int i=0;i<16;i++)file.Write(block,0,block.Length);}
    using(var file=archive.CreateEntry(".mori/library.db").Open()){byte[] bytes=Encoding.UTF8.GetBytes("not a SQLite database");file.Write(bytes,0,bytes.Length);}
   }
   bool indexStaged=false,invalidOriginalSeen=false;
   Rejected(indexLast,Path.Combine(root,"index-last-target"),root,check,"an invalid index is validated before originals even when it is last in ZIP order",new Library.RestoreLimits(minimumFreeBytes:reserve,availableBytes:parent=>{
    invalidOriginalSeen|=StageContainsOriginal(root);indexStaged|=Directory.GetDirectories(root,".nook-restore-*.partial").Any(stage=>File.Exists(Path.Combine(stage,".mori","library.db")));return long.MaxValue;
   }));
   check(indexStaged&&!invalidOriginalSeen,"invalid metadata cannot force expansion of a preceding high-compression original");

   string compressible=Path.Combine(root,"compressible.png"),plain=Path.Combine(root,"compressible-plain.zip"),packed=Path.Combine(root,"compressible-packed.zip"),entryName;
   byte[] compressibleBytes=new byte[1024*1024];File.WriteAllBytes(compressible,compressibleBytes);
   using(var library=new Library(Path.Combine(root,"compressible-library"))){var asset=library.Import(compressible,"","compressible","","","").Item;entryName=asset.Relative.Replace('\\','/');library.Backup(plain);}
   Rewrite(plain,packed,(name,bytes)=>bytes);long packedTotal;
   using(var archive=ZipFile.OpenRead(packed)){var fileEntry=archive.GetEntry(entryName);check(fileEntry.Length>fileEntry.CompressedLength*10,"synthetic legitimate original has a high compression ratio");packedTotal=archive.Entries.Sum(entry=>entry.Length);}
   string packedTarget=Path.Combine(root,"compressible-restored");Library.Restore(packed,packedTarget,new Library.RestoreLimits(maximumExtractedBytes:packedTotal,minimumFreeBytes:reserve,availableBytes:parent=>long.MaxValue));
   check(File.ReadAllBytes(Path.Combine(packedTarget,entryName.Replace('/',Path.DirectorySeparatorChar))).SequenceEqual(compressibleBytes),"legitimate high-compression originals are not rejected by an arbitrary ratio limit");
   for(int i=0;i<3;i++){
    int scenario=i;bool sawPartialOriginal=false;
    Rejected(packed,Path.Combine(root,"stream-space-"+i),root,check,"space becomes insufficient, unknown or unavailable during original streaming "+i,new Library.RestoreLimits(minimumFreeBytes:reserve,availableBytes:parent=>{
     foreach(string stage in Directory.GetDirectories(root,".nook-restore-*.partial")){string file=Path.Combine(stage,entryName.Replace('/',Path.DirectorySeparatorChar));if(File.Exists(file)&&new FileInfo(file).Length>0){sawPartialOriginal=true;if(scenario==0)return reserve;if(scenario==1)return null;throw new IOException("synthetic space loss during extraction");}}
     return long.MaxValue;
    }));
    check(sawPartialOriginal,"streaming space guard is exercised after some original bytes have been written "+i);
   }
   // The database agrees with the forged central-directory length. Rejection
   // must therefore happen while reading/validating actual bytes, not merely
   // because index metadata disagrees with the ZIP declaration.
   long[] forgedLengths={compressibleBytes.Length-1L,compressibleBytes.Length+1L,0L};
   for(int i=0;i<forgedLengths.Length;i++){
    string wrongIndex=Path.Combine(root,"wrong-size-index-"+i+".db"),prepared=Path.Combine(root,"wrong-size-prepared-"+i+".zip"),forged=Path.Combine(root,"wrong-size-stream-"+i+".zip");
    using(var archive=ZipFile.OpenRead(packed))using(var input=archive.GetEntry(".mori/library.db").Open())using(var output=File.Create(wrongIndex))input.CopyTo(output);
    using(var database=new Database(wrongIndex)){database.Exec("UPDATE assets SET bytes="+forgedLengths[i]);database.Exec("PRAGMA wal_checkpoint(TRUNCATE)");}
    Rewrite(packed,prepared,(name,bytes)=>name==".mori/library.db"?File.ReadAllBytes(wrongIndex):bytes);DeclareLength(prepared,forged,entryName,(uint)forgedLengths[i]);
    Exception failure=Rejected(forged,Path.Combine(root,"wrong-size-target-"+i),root,check,"actual inflated bytes must match the declared per-entry size "+i,new Library.RestoreLimits(minimumFreeBytes:reserve,availableBytes:parent=>long.MaxValue));
    string expected=i==1?"实际大小与声明不一致":"实际解压数据超过声明大小或恢复预算";
    check(failure is IOException&&failure.Message.IndexOf(expected,StringComparison.Ordinal)>=0,"the streaming guard, not only final original verification, rejects the forged size "+i);
   }
   check(File.ReadAllBytes(compressible).SequenceEqual(compressibleBytes),"resource-budget failures never modify the original source file");
  }
  public static void Run(string root,Action<bool,string> check){
   root=Path.Combine(root,"restore-safety");Directory.CreateDirectory(root);
   string source=Path.Combine(root,"original.png");byte[] original=Encoding.UTF8.GetBytes("synthetic restore original; never a real user file");File.WriteAllBytes(source,original);
   string backup=Path.Combine(root,"valid.mori.zip"),cyclic=Path.Combine(root,"cyclic-folders.zip"),id,reference;
   using(var library=new Library(Path.Combine(root,"library"))){
    string folder=library.CreateFolder("恢复分类","");var asset=library.Import(source,folder,"恢复素材","https://example.invalid/original","标签","备注").Item;id=asset.Id;asset.Favorite=true;library.Update(asset);library.Trash(new[]{id},true);library.Db.Exec("UPDATE assets SET hidden_all=1 WHERE id="+Database.Q(id));
    library.EnableReferenceAssets();reference=library.AddReferenceAssets(new[]{source},folder).Single();
    library.Db.Exec("CREATE TABLE restore_opaque_data(key TEXT PRIMARY KEY,value TEXT); INSERT INTO restore_opaque_data VALUES('future-data','must survive untouched')");
    library.Backup(backup);
    library.Db.Exec("UPDATE folders SET parent=id WHERE id="+Database.Q(folder));library.Backup(cyclic);
   }
   byte[] backupBytes=File.ReadAllBytes(backup);
   string restored=Path.Combine(root,"restored");Directory.CreateDirectory(restored);Library.Restore(backup,restored);
   using(var library=new Library(restored)){
    library.EnableReferenceAssets();var asset=library.All().Single(a=>a.Id==id);
    check(asset.Deleted&&asset.Favorite&&asset.HiddenFromAll&&asset.Tags=="标签"&&asset.Notes=="备注"&&asset.Source=="https://example.invalid/original","staged restore preserves trash, visibility and editable metadata");
    check(library.AssetFolders(id).Single().Name=="恢复分类"&&File.ReadAllBytes(library.FullPath(asset)).SequenceEqual(original),"staged restore preserves classifications and exact original bytes");
    check(library.All().Single(a=>a.Id==reference).ReferencePath==source&&library.Db.Query("SELECT value FROM restore_opaque_data").Single()["value"]=="must survive untouched","restore keeps local shortcuts and unknown future tables without schema replacement");
   }
   check(File.ReadAllBytes(backup).SequenceEqual(backupBytes)&&File.ReadAllBytes(source).SequenceEqual(original),"restore never modifies the backup archive or shortcut source");
   Rejected(cyclic,Path.Combine(root,"cyclic-target"),root,check,"cyclic category hierarchies are rejected rather than becoming inaccessible folders");

   string duplicate=Path.Combine(root,"duplicate.zip");Rewrite(backup,duplicate,(name,bytes)=>bytes,archive=>archive.CreateEntry(".mori/library.db"));
   string retry=Path.Combine(root,"retry");Rejected(duplicate,retry,root,check,"duplicate archive paths are rejected before publication");Library.Restore(backup,retry);
   check(File.Exists(Path.Combine(retry,".mori","library.db")),"a valid restore can immediately reuse the destination from a failed duplicate archive");

   string invalid=Path.Combine(root,"invalid-db.zip");Rewrite(backup,invalid,(name,bytes)=>name==".mori/library.db"?Encoding.UTF8.GetBytes("not a SQLite database"):bytes);
   Rejected(invalid,Path.Combine(root,"invalid-target"),root,check,"a non-SQLite database is rejected");
   string corrupt=Path.Combine(root,"corrupt-db.zip");Rewrite(backup,corrupt,(name,bytes)=>{if(name!=".mori/library.db")return bytes;var changed=new byte[128];Buffer.BlockCopy(bytes,0,changed,0,16);return changed;});
   Rejected(corrupt,Path.Combine(root,"corrupt-target"),root,check,"a corrupt database with a plausible header closes its native handle and is rejected");

   string emptyDatabase=Path.Combine(root,"empty.db");using(var database=new Database(emptyDatabase))database.Exec("CREATE TABLE unrelated(id TEXT)");
   string schema=Path.Combine(root,"wrong-schema.zip");Rewrite(backup,schema,(name,bytes)=>name==".mori/library.db"?File.ReadAllBytes(emptyDatabase):bytes);
   Rejected(schema,Path.Combine(root,"schema-target"),root,check,"a valid SQLite file without the library tables is rejected");

   string missing=Path.Combine(root,"missing-original.zip");Rewrite(backup,missing,(name,bytes)=>name.StartsWith("原图/",StringComparison.Ordinal)?null:bytes);
   string emptyTarget=Path.Combine(root,"existing-empty");Directory.CreateDirectory(emptyTarget);DateTime created=Directory.GetCreationTimeUtc(emptyTarget);
   Rejected(missing,emptyTarget,root,check,"missing originals are rejected after staging without polluting an existing target");
   check(Directory.GetCreationTimeUtc(emptyTarget)==created,"failed staged validation leaves the user's existing empty directory in place");Library.Restore(backup,emptyTarget);
   check(File.Exists(Path.Combine(emptyTarget,".mori","library.db")),"the original empty folder can be retried successfully after staged validation fails");

   string tampered=Path.Combine(root,"tampered-original.zip");Rewrite(backup,tampered,(name,bytes)=>{if(name.StartsWith("原图/",StringComparison.Ordinal)){bytes=(byte[])bytes.Clone();bytes[0]^=1;}return bytes;});
   Rejected(tampered,Path.Combine(root,"tampered-target"),root,check,"same-sized modified originals fail SHA-256 validation");

   string conflict=Path.Combine(root,"conflicting-file-directory.zip");Rewrite(backup,conflict,(name,bytes)=>bytes,archive=>{archive.CreateEntry("原图/conflict");archive.CreateEntry("原图/conflict/nested.png");});
   Rejected(conflict,Path.Combine(root,"conflict-target"),root,check,"a file-directory extraction conflict cleans its partially extracted staging directory");

   string alias=Path.Combine(root,"alias.zip");Rewrite(backup,alias,(name,bytes)=>bytes,archive=>archive.CreateEntry("原图/../outside.png"));
   Rejected(alias,Path.Combine(root,"alias-target"),root,check,"archive paths cannot use dot-segment aliases to bypass allowed locations");
   string occupied=Path.Combine(root,"occupied");Directory.CreateDirectory(occupied);string sentinel=Path.Combine(occupied,"keep.txt");File.WriteAllText(sentinel,"keep");bool rejected=false;try{Library.Restore(backup,occupied);}catch{rejected=true;}
   check(rejected&&Directory.GetFiles(occupied).Length==1&&File.ReadAllText(sentinel)=="keep","a nonempty destination remains untouched rather than being removed or replaced");
   ResourceBudgets(root,backup,check);
  }
 }
}
