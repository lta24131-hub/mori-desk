using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Mori {
 // The caller may run Transfer on a worker thread. Keep the entire call and its
 // synchronous activation callback on that thread: Windows mutexes are affine.
 public static class LibraryTransfer {
  sealed class WriteLease : IDisposable {
   Mutex mutex;
   public WriteLease(Mutex value){mutex=value;}
   public void Dispose(){if(mutex!=null){mutex.ReleaseMutex();mutex.Dispose();mutex=null;}}
  }
  static string Normalize(string path){string full=Path.GetFullPath(path),root=Path.GetPathRoot(full);return full.Length>root.Length?full.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar):root;}
  public static bool SameRoot(string left,string right){return string.Equals(Normalize(left),Normalize(right),StringComparison.OrdinalIgnoreCase);}
  static string MutexName(string root){using(var sha=SHA256.Create())return "Local\\Mori-transfer-"+BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Normalize(root).ToUpperInvariant()))).Replace("-","");}
  public static IDisposable TryAcquireWrite(string root){
   var mutex=new Mutex(false,MutexName(root));bool acquired=false;
   try{try{acquired=mutex.WaitOne(0);}catch(AbandonedMutexException){acquired=true;}if(!acquired){mutex.Dispose();return null;}return new WriteLease(mutex);}catch{mutex.Dispose();throw;}
  }
  static void Cancel(Func<bool> cancelled){if(cancelled!=null&&cancelled())throw new OperationCanceledException("已取消迁移，原图库未改动。");}
  static void Report(Action<string> progress,string message){if(progress!=null)progress(message);}
  static bool Under(string child,string parent){return child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);}
  static void NoReparse(string path){
   string current=Path.GetFullPath(path);
   while(!string.IsNullOrEmpty(current)){
    if(File.Exists(current)||Directory.Exists(current))if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("迁移不支持符号链接或连接点，请选择实际文件夹："+current);
    var parent=Path.GetDirectoryName(current);if(parent==current)break;current=parent;
   }
  }
  static void CheckTree(string root,Func<bool> cancelled){
   var pending=new Stack<string>();pending.Push(root);
   while(pending.Count>0){Cancel(cancelled);string directory=pending.Pop();NoReparse(directory);foreach(var entry in Directory.EnumerateFileSystemEntries(directory)){Cancel(cancelled);var attributes=File.GetAttributes(entry);if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("图库包含符号链接或连接点，未开始迁移："+entry);if((attributes&FileAttributes.Directory)!=0)pending.Push(entry);}}
  }
  public static void ValidateDestination(string source,string destination){
   if(string.IsNullOrWhiteSpace(destination)||!Path.IsPathRooted(destination))throw new IOException("请选择完整的目标文件夹路径。");
   string target=Normalize(destination),original=Normalize(source);
   if(target.Length==0||SameRoot(target,Path.GetPathRoot(target)))throw new IOException("不能把磁盘根目录用作图库，请选择其中的空文件夹。");
   if(SameRoot(original,Path.GetPathRoot(original)))throw new IOException("原图库不能位于磁盘根目录，请先检查图库位置。");
   if(SameRoot(target,original)||Under(target,original)||Under(original,target))throw new IOException("目标图库不能与原图库相同，也不能位于原图库内部或包含原图库。");
   NoReparse(original);NoReparse(target);
   if(File.Exists(target))throw new IOException("目标路径已是文件，请选择新的文件夹。");
   if(!Directory.Exists(Path.GetDirectoryName(target)))throw new IOException("目标文件夹的上级目录不存在，请先选择一个实际目录。");
   if(Directory.Exists(target)&&Directory.EnumerateFileSystemEntries(target).Any())throw new IOException("目标文件夹必须为空，迁移不会覆盖已有数据。");
  }
  static string Token(Database db){return db.Query("PRAGMA data_version")[0]["data_version"]+":"+db.Query("SELECT total_changes() AS value")[0]["value"];}
  static string Counts(Database db){return string.Join("/",new[]{"assets","folders","memberships"}.Select(table=>db.Query("SELECT COUNT(*) AS value FROM "+table)[0]["value"]).Concat(new[]{db.Query("SELECT COUNT(*) AS value FROM assets WHERE deleted<>0")[0]["value"]}));}
  public static long RequiredBytes(long originals,long database,long thumbnails){
   if(originals<0||database<0||thumbnails<0)throw new IOException("图库大小记录无效，无法估算所需空间。");
   try{return checked(originals+database+thumbnails);}catch(OverflowException){throw new IOException("图库大小超出可估算范围，请检查图库记录。");}
  }
  static string DiskSize(long value){return value>=1073741824?(value/1073741824.0).ToString("0.0",CultureInfo.InvariantCulture)+" GB":(value/1048576.0).ToString("0.0",CultureInfo.InvariantCulture)+" MB";}
  static void CheckSpace(string parent,long required){
   long available;
   try{var drive=new DriveInfo(Path.GetPathRoot(parent));if(drive.DriveType==DriveType.Network||drive.DriveType==DriveType.Unknown||drive.DriveType==DriveType.NoRootDirectory||!drive.IsReady)return;available=drive.AvailableFreeSpace;}
   catch(IOException){return;}catch(UnauthorizedAccessException){return;}catch(ArgumentException){return;}catch(NotSupportedException){return;}
   if(available<required)throw new IOException("目标磁盘空间不足：完整复制至少需要 "+DiskSize(required)+"，当前可用 "+DiskSize(available)+"。原图库与默认保存位置未改变。");
  }
  static void Integrity(Database db){var result=db.Query("PRAGMA integrity_check");if(result.Count!=1||result[0].Values.Single()!="ok")throw new IOException("图库索引完整性检查未通过，未切换图库位置。");if(db.Query("PRAGMA foreign_key_check").Count!=0)throw new IOException("图库分类关联检查未通过，未切换图库位置。");}
  static string ExpectedHash(string key){string hash=(key??"").Split(':')[0];if(hash.Length!=64||hash.Any(c=>!Uri.IsHexDigit(c)))throw new IOException("图库原图校验记录无效，未切换图库位置。");return hash.ToUpperInvariant();}
  static string HashFile(string path,Func<bool> cancelled){
   using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))using(var sha=SHA256.Create()){
    byte[] buffer=new byte[262144];int count;while((count=input.Read(buffer,0,buffer.Length))>0){Cancel(cancelled);sha.TransformBlock(buffer,0,count,buffer,0);}sha.TransformFinalBlock(new byte[0],0,0);return BitConverter.ToString(sha.Hash).Replace("-","");
   }
  }
  static void Copy(string source,string target,long expectedBytes,string expectedHash,Func<bool> cancelled){
   Cancel(cancelled);NoReparse(source);NoReparse(Path.GetDirectoryName(target));Directory.CreateDirectory(Path.GetDirectoryName(target));
   var created=File.GetCreationTimeUtc(source);var modified=File.GetLastWriteTimeUtc(source);
   using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read))using(var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var sha=SHA256.Create()){
    if(expectedBytes>=0&&input.Length!=expectedBytes)throw new IOException("原图大小与图库记录不一致："+source);
    byte[] buffer=new byte[262144];int count;while((count=input.Read(buffer,0,buffer.Length))>0){Cancel(cancelled);output.Write(buffer,0,count);sha.TransformBlock(buffer,0,count,buffer,0);}sha.TransformFinalBlock(new byte[0],0,0);output.Flush(true);
    if(expectedHash!=null&&BitConverter.ToString(sha.Hash).Replace("-","")!=expectedHash)throw new IOException("原图 SHA-256 校验失败："+source);
   }
   if(expectedHash!=null&&HashFile(target,cancelled)!=expectedHash)throw new IOException("复制后的原图 SHA-256 校验失败："+target);
   File.SetCreationTimeUtc(target,created);File.SetLastWriteTimeUtc(target,modified);
  }
  static void RemoveStage(string stage,string parent){
   string full=Normalize(stage);if(!SameRoot(Path.GetDirectoryName(full),parent)||!Path.GetFileName(full).StartsWith(".mori-transfer-",StringComparison.Ordinal)||!Path.GetFileName(full).EndsWith(".partial",StringComparison.Ordinal))throw new IOException("暂存路径未通过安全检查，已保留："+stage);
   NoReparse(full);RemoveOwnedTree(full,full);
  }
  static void RemoveOwnedTree(string path,string stage){
   string full=Normalize(path);if(!SameRoot(full,stage)&&!Under(full,stage))throw new IOException("暂存清理路径越界，已停止。");
   if((File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)throw new IOException("暂存目录出现链接，已保留以便检查："+full);
   foreach(var entry in Directory.EnumerateFileSystemEntries(full)){
    var attributes=File.GetAttributes(entry);if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("暂存目录出现链接，已保留以便检查："+entry);
    if((attributes&FileAttributes.Directory)!=0)RemoveOwnedTree(entry,stage);else File.Delete(entry);
   }
   Directory.Delete(full,false);
  }

  // Copies a verified snapshot; it never deletes the original library or edits
  // preferences. Activation must update preferences atomically and only occurs
  // after the completed target exists, while native writes are still excluded.
  public static string Transfer(Library source,string destination,Action<string> progress,Func<bool> cancelled,Action<string> activateAfterVerified=null){
   if(source==null)throw new ArgumentNullException("source");
   ValidateDestination(source.Root,destination);string target=Normalize(destination),parent=Path.GetDirectoryName(target),stage=null;bool committed=false,removedEmpty=false;
   Cancel(cancelled);
   using(var lease=TryAcquireWrite(source.Root)){
    if(lease==null)throw new IOException("图库正在接收收藏，请稍后重试迁移。");
    try{
     source.EnsureDeletionComplete();Report(progress,"正在检查图库与目标位置…");CheckTree(source.Root,cancelled);ValidateDestination(source.Root,target);Cancel(cancelled);
     string before=Token(source.Db),expectedCounts=Counts(source.Db);
     stage=Path.Combine(parent,".mori-transfer-"+Guid.NewGuid().ToString("N")+".partial");Directory.CreateDirectory(stage);Directory.CreateDirectory(Path.Combine(stage,".mori"));Directory.CreateDirectory(Path.Combine(stage,"原图"));
     Report(progress,"正在创建一致的图库索引快照…");Cancel(cancelled);
     source.Db.Exec("VACUUM INTO "+Database.Q(Path.Combine(stage,".mori","library.db")));
     using(var snapshot=new Database(Path.Combine(stage,".mori","library.db"))){
      Integrity(snapshot);if(Counts(snapshot)!=expectedCounts)throw new IOException("图库在创建快照时发生变化，请稍后重试迁移。");
      var assets=snapshot.Query("SELECT id,path,hash,bytes FROM assets ORDER BY id");int index=0;
      long originalBytes=0,thumbnailBytes=0,databaseBytes=0;
      foreach(var row in assets){
       Cancel(cancelled);long size;if(!long.TryParse(row["bytes"],out size)||size<0)throw new IOException("图库原图大小记录无效。");originalBytes=RequiredBytes(originalBytes,size,0);
       string thumb=Library.Inside(source.Root,Path.Combine(".mori","thumbs",row["id"]+Library.ThumbnailSuffix));if(File.Exists(thumb))thumbnailBytes=RequiredBytes(thumbnailBytes,new FileInfo(thumb).Length,0);
      }
      foreach(var file in Directory.GetFiles(Path.Combine(stage,".mori")))databaseBytes=RequiredBytes(databaseBytes,new FileInfo(file).Length,0);
      Report(progress,"正在检查目标磁盘空间…");CheckSpace(parent,RequiredBytes(originalBytes,databaseBytes,thumbnailBytes));
      foreach(var row in assets){
       Cancel(cancelled);string relative=row["path"];if(Path.IsPathRooted(relative)||!relative.Replace('\\','/').StartsWith("原图/",StringComparison.Ordinal))throw new IOException("图库原图路径无效，未切换图库位置。");
       string original=Library.Inside(source.Root,relative),copy=Library.Inside(stage,relative);long size;if(!long.TryParse(row["bytes"],out size)||size<0)throw new IOException("图库原图大小记录无效。");
       if(!Under(Normalize(original),Normalize(Path.Combine(source.Root,"原图"))))throw new IOException("图库原图路径超出原图目录。");
       Report(progress,"复制并校验原图："+(++index)+" / "+assets.Count+" · "+Path.GetFileName(original));
       Copy(original,copy,size,ExpectedHash(row["hash"]),cancelled);
       // Thumbnails are optional caches; copy only complete existing entries.
       string thumb=Library.Inside(source.Root,Path.Combine(".mori","thumbs",row["id"]+Library.ThumbnailSuffix));
       if(File.Exists(thumb))Copy(thumb,Library.Inside(stage,Path.Combine(".mori","thumbs",row["id"]+Library.ThumbnailSuffix)),-1,null,cancelled);
      }
      Report(progress,"正在验证分类、回收站与图库索引…");Cancel(cancelled);Integrity(snapshot);if(Counts(snapshot)!=expectedCounts)throw new IOException("复制后的图库数量检查未通过。");
      snapshot.Exec("PRAGMA wal_checkpoint(TRUNCATE)");
     }
     Cancel(cancelled);if(Token(source.Db)!=before)throw new IOException("迁移期间原图库发生了更新，已停止切换。请等待收藏或缩略图处理完成后重试。");
     Report(progress,"校验通过，正在完成图库复制…");Cancel(cancelled);
     // Hold both this connection's gate and a SQLite write reservation during
     // final validation/activation so thumbnail completion or another writer
     // cannot slip a late update between the version check and location switch.
     // The activation callback must not query/dispose source.Db on another thread.
     source.Db.Transaction(delegate{
      if(Token(source.Db)!=before)throw new IOException("迁移期间原图库发生了更新，已停止切换。请稍后重试。");
      ValidateDestination(source.Root,target);NoReparse(stage);Cancel(cancelled);
      if(Directory.Exists(target)){Directory.Delete(target,false);removedEmpty=true;}
      Directory.Move(stage,target);stage=null;committed=true;
      if(activateAfterVerified!=null)activateAfterVerified(target);
     });
     Report(progress,"图库已复制并校验完成，原图库保留在原位置。");return target;
    }catch(Exception error){
     string cleanupError=null;
     if(stage!=null&&Directory.Exists(stage))try{RemoveStage(stage,parent);}catch(Exception cleanup){cleanupError=cleanup.Message+" 暂存目录："+stage;}
     if(removedEmpty&&!committed&&!Directory.Exists(target)&&!File.Exists(target))try{Directory.CreateDirectory(target);}catch{}
     if(committed)throw new IOException("已校验的图库副本保留在："+target+"。原图库也已保留；切换未能正常完成。\n"+error.Message,error);
     if(cleanupError!=null)throw new IOException(error.Message+"\n"+cleanupError,error);
     throw;
    }
   }
  }
 }
}
