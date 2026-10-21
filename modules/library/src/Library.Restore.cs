using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Mori {
 public sealed partial class Library {
  const string RestorePrefix=".nook-restore-";
  // The index is metadata, not an original. Bound it independently before
  // asking SQLite to inspect it. Large originals remain limited by declared
  // bytes and the destination's actual free space, never a compression ratio.
  internal sealed class RestoreLimits {
   internal readonly long MaximumDatabaseBytes,MaximumExtractedBytes,MinimumFreeBytes;
   internal readonly Func<string,long?> AvailableBytes;
   internal RestoreLimits(long maximumDatabaseBytes=512L*1024*1024,long maximumExtractedBytes=long.MaxValue,long minimumFreeBytes=256L*1024*1024,Func<string,long?> availableBytes=null){
    if(maximumDatabaseBytes<1||maximumExtractedBytes<1||minimumFreeBytes<0)throw new ArgumentOutOfRangeException("恢复空间预算无效。");
    MaximumDatabaseBytes=maximumDatabaseBytes;MaximumExtractedBytes=maximumExtractedBytes;MinimumFreeBytes=minimumFreeBytes;AvailableBytes=availableBytes??RestoreAvailableBytes;
   }
  }
  sealed class RestoreOriginal {internal string Name,Hash;internal long Length;}
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
  [return:MarshalAs(UnmanagedType.Bool)]
  static extern bool GetDiskFreeSpaceEx(string directory,out ulong available,out ulong total,out ulong free);
  static long? RestoreAvailableBytes(string directory){
   ulong available,total,free;
   // Query the actual directory (including mounted volumes) and the caller's
   // quota, rather than the drive letter's total unallocated capacity.
   string query=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
   if(!GetDiskFreeSpaceEx(query,out available,out total,out free))return null;
   return available>(ulong)long.MaxValue?long.MaxValue:(long)available;
  }
  static void RestoreSpace(string parent,long required,RestoreLimits limits){
   long? available;
   try{available=limits.AvailableBytes(parent);}catch(Exception error){throw new IOException("无法确认恢复位置的可用空间，未继续解压。请检查磁盘后重试。",error);}
   if(!available.HasValue||available.Value<0)throw new IOException("无法确认恢复位置的可用空间，未继续解压。请检查磁盘后重试。");
   if(required<0||available.Value<limits.MinimumFreeBytes||required>available.Value-limits.MinimumFreeBytes)throw new IOException("恢复位置空间不足，已停止解压并保留原有数据。请清理空间或选择其他磁盘。");
  }
  internal static long RestoreDeclaredBytes(IEnumerable<long> lengths,long maximum){
   long total=0;
   try{foreach(long length in lengths){if(length<0)throw new IOException("备份中的文件大小无效。");total=checked(total+length);if(total>maximum)throw new IOException("备份展开后的大小超过恢复预算，未解压。");}}
   catch(OverflowException error){throw new IOException("备份展开后的总大小无效，未解压。",error);}
   return total;
  }
  static void CopyRestoreEntry(ZipArchiveEntry entry,string stage,string parent,RestoreLimits limits,long budget,ref long copied){
   string path=Inside(stage,entry.FullName);RestoreSafe(path);Directory.CreateDirectory(Path.GetDirectoryName(path));
   long written=0;
   using(var input=entry.Open())using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
    byte[] block=new byte[65536];int count;
    while((count=input.Read(block,0,block.Length))>0){
     // Check actual inflated bytes before writing. Central-directory sizes are
     // untrusted, and neither per-entry nor aggregate arithmetic may wrap.
     if(count>entry.Length-written||count>budget-copied)throw new IOException("备份实际解压数据超过声明大小或恢复预算，已停止解压。");
     RestoreSpace(parent,count,limits);output.Write(block,0,count);written+=count;copied+=count;
    }
    if(written!=entry.Length)throw new IOException("备份文件的实际大小与声明不一致，已停止解压。");
    output.Flush(true);
   }
  }
  static void RestoreSafe(string path){
   for(string current=Path.GetFullPath(path);!string.IsNullOrEmpty(current);current=Path.GetDirectoryName(current)){
    FileAttributes attributes;try{attributes=File.GetAttributes(current);}catch(FileNotFoundException){continue;}catch(DirectoryNotFoundException){continue;}
    if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("恢复路径包含符号链接或连接点，请选择实际文件夹。");
   }
  }
  static void RestoreDestination(string target){
   RestoreSafe(target);
   if(File.Exists(target))throw new IOException("恢复目标已是文件，请选择空文件夹。");
   if(Directory.Exists(target)&&Directory.EnumerateFileSystemEntries(target).Any())throw new IOException("请选一个空文件夹，避免覆盖原有数据。");
  }
  static string RestoreEntryName(string name){
   if(string.IsNullOrEmpty(name)||name.IndexOf('\\')>=0||Path.IsPathRooted(name)||name.Split('/').Any(part=>part.Length==0||part=="."||part==".."||part!=part.TrimEnd(' ','.')||part.IndexOfAny(Path.GetInvalidFileNameChars())>=0))throw new IOException("备份包含无效文件路径。");
   if(name!=".mori/library.db"&&!name.StartsWith("原图/",StringComparison.Ordinal))throw new IOException("备份包含未知文件。");
   return name;
  }
  static void RestoreColumns(Database database,string table,string required){
   var columns=new HashSet<string>(database.Query("PRAGMA table_info("+table+")").Select(row=>row["name"]),StringComparer.Ordinal);
   if(required.Split(' ').Any(column=>!columns.Contains(column)))throw new IOException("备份的图库索引缺少必要字段："+table);
  }
  static List<RestoreOriginal> ValidateRestoreIndex(string stage,HashSet<string> entries,Dictionary<string,long> declared){
   string index=Inside(stage,".mori/library.db");
   using(var file=File.OpenRead(index)){
    byte[] header=new byte[16];if(file.Read(header,0,header.Length)!=header.Length||Encoding.ASCII.GetString(header)!="SQLite format 3\0")throw new IOException("备份的图库索引无效或已损坏。");
   }
   using(var database=new Database(index)){
    var integrity=database.Query("PRAGMA integrity_check");
    if(integrity.Count!=1||integrity[0].Values.Single()!="ok")throw new IOException("备份的图库索引完整性检查未通过。");
    var tables=new HashSet<string>(database.Query("SELECT name FROM sqlite_master WHERE type='table'").Select(row=>row["name"]),StringComparer.Ordinal);
    if(new[]{"assets","folders","memberships"}.Any(table=>!tables.Contains(table)))throw new IOException("备份不包含完整的图库索引。");
    RestoreColumns(database,"assets","id hash name path ext bytes tags notes source added favorite deleted width height color perceptual");RestoreColumns(database,"folders","id name parent");RestoreColumns(database,"memberships","asset folder");
    if(tables.Contains("desk_refs"))RestoreColumns(database,"desk_refs","id path name folder is_dir");
    if(tables.Contains("desk_ref_folders"))RestoreColumns(database,"desk_ref_folders","id name");
    if(tables.Contains("desk_local_collections"))RestoreColumns(database,"desk_local_collections","id name");
    if(tables.Contains("pending_file_deletes"))RestoreColumns(database,"pending_file_deletes","asset original stage");
    if(database.Query("PRAGMA foreign_key_check").Count!=0||database.Query("SELECT 1 FROM memberships m LEFT JOIN assets a ON a.id=m.asset LEFT JOIN folders f ON f.id=m.folder WHERE a.id IS NULL OR f.id IS NULL LIMIT 1").Count!=0)throw new IOException("备份的素材分类关联不完整。");
    if(database.Query("SELECT 1 FROM folders f LEFT JOIN folders p ON p.id=f.parent WHERE f.parent<>'' AND p.id IS NULL LIMIT 1").Count!=0)throw new IOException("备份的上级分类已缺失。");
    var parents=database.Query("SELECT id,parent FROM folders").ToDictionary(row=>row["id"],row=>row["parent"]);var complete=new HashSet<string>(StringComparer.Ordinal);
    foreach(string folder in parents.Keys){var chain=new HashSet<string>(StringComparer.Ordinal);string current=folder;while(current!=""&&!complete.Contains(current)){if(!chain.Add(current))throw new IOException("备份的分类层级存在循环，未恢复。");current=parents[current];}complete.UnionWith(chain);}
    if(tables.Contains("pending_file_deletes")&&database.Query("SELECT 1 FROM pending_file_deletes LIMIT 1").Count!=0)throw new IOException("备份包含尚未完成的删除操作，暂不能安全恢复。");
    var originals=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var manifest=new List<RestoreOriginal>();
    foreach(var row in database.Query("SELECT id,path,hash,bytes FROM assets")){
     string name=RestoreEntryName(row["path"].Replace('\\','/'));
     if(!name.StartsWith("原图/",StringComparison.Ordinal)||!originals.Add(name)||!entries.Contains(name))throw new IOException("备份缺少原文件，或原文件路径重复。");
     long length,archived;if(!long.TryParse(row["bytes"],NumberStyles.Integer,CultureInfo.InvariantCulture,out length)||length<0||!declared.TryGetValue(name,out archived)||archived!=length)throw new IOException("备份的原文件大小记录无效，或与压缩包不一致。");
     string expected=row["hash"].Split(':')[0];if(expected.Length!=64||expected.Any(c=>!Uri.IsHexDigit(c)))throw new IOException("备份的原文件校验记录无效。");
     manifest.Add(new RestoreOriginal{Name=name,Length=length,Hash=expected});
    }
    if(entries.Count!=originals.Count+1)throw new IOException("备份包含未在图库索引登记的原文件。");
    // Do not run Library's schema upgrades/deletion recovery on unverified
    // input. Keep every table/column (including local shortcuts) byte-for-byte
    // logically intact, and close/checkpoint only this private extracted copy.
    database.Exec("PRAGMA wal_checkpoint(TRUNCATE)");
    return manifest;
   }
  }
  static void ValidateRestoreOriginals(string stage,IEnumerable<RestoreOriginal> originals){
   foreach(var item in originals)using(var original=File.OpenRead(Inside(stage,item.Name)))using(var sha=SHA256.Create()){
    if(original.Length!=item.Length||!string.Equals(BitConverter.ToString(sha.ComputeHash(original)).Replace("-",""),item.Hash,StringComparison.OrdinalIgnoreCase))throw new IOException("备份的原文件大小或 SHA-256 校验未通过："+item.Name);
   }
  }
  static void CleanRestoreStage(string stage,string parent){
   string full=Path.GetFullPath(stage);
   if(!string.Equals(Path.GetDirectoryName(full),parent,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(full).StartsWith(RestorePrefix,StringComparison.Ordinal)||!full.EndsWith(".partial",StringComparison.Ordinal))throw new IOException("恢复暂存目录未通过清理检查，已保留："+stage);
   RestoreSafe(full);CleanRestoreTree(full,full);
  }
  static void CleanRestoreTree(string path,string stage){
   string full=Path.GetFullPath(path);if(!string.Equals(full,stage,StringComparison.OrdinalIgnoreCase)&&!full.StartsWith(stage+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("恢复暂存清理路径越界，已停止。");
   RestoreSafe(full);
   foreach(string entry in Directory.EnumerateFileSystemEntries(full)){
    RestoreSafe(entry);if(Directory.Exists(entry))CleanRestoreTree(entry,stage);else File.Delete(entry);
   }
   Directory.Delete(full,false);
  }
  public static void Restore(string zip,string destination){Restore(zip,destination,new RestoreLimits());}
  internal static void Restore(string zip,string destination,RestoreLimits limits){
   if(limits==null)throw new ArgumentNullException("limits");
   if(string.IsNullOrWhiteSpace(destination)||!Path.IsPathRooted(destination))throw new IOException("请选择完整的恢复目标路径。");
   string target=Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar),parent=Path.GetDirectoryName(target);
   if(string.IsNullOrEmpty(parent)||!Directory.Exists(parent))throw new IOException("请选择磁盘中实际存在的上级文件夹，不要使用磁盘根目录。");
   RestoreDestination(target);string stage=null,previous=null;
   try{
    using(var archive=ZipFile.OpenRead(zip)){
     var entries=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var declared=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);ZipArchiveEntry index=null;
     foreach(var entry in archive.Entries){string name=RestoreEntryName(entry.FullName);if(!entries.Add(name))throw new IOException("备份包含重复的文件路径。");declared.Add(name,entry.Length);if(name==".mori/library.db")index=entry;}
     if(!entries.Contains(".mori/library.db"))throw new IOException("不是 Nook 图库备份。");
     long budget=RestoreDeclaredBytes(declared.Values,limits.MaximumExtractedBytes),copied=0;
     if(index.Length>limits.MaximumDatabaseBytes)throw new IOException("备份的图库索引超过恢复上限，未解压原文件。");
     RestoreSpace(parent,budget,limits);
     stage=Path.Combine(parent,RestorePrefix+Guid.NewGuid().ToString("N")+".partial");RestoreSafe(stage);Directory.CreateDirectory(stage);
     CopyRestoreEntry(index,stage,parent,limits,budget,ref copied);
     // Invalid metadata or unregistered originals are rejected before any
     // original is inflated, even when the ZIP puts its index last.
     var originals=ValidateRestoreIndex(stage,entries,declared);
     RestoreSpace(parent,budget-copied,limits);
     foreach(var entry in archive.Entries)if(entry!=index)CopyRestoreEntry(entry,stage,parent,limits,budget,ref copied);
     ValidateRestoreOriginals(stage,originals);
    }
    RestoreSafe(stage);RestoreDestination(target);
    if(Directory.Exists(target)){
     // Rename, never recursively delete, the previously verified empty target.
     // A failed publish restores that exact directory so a retry stays safe.
     previous=Path.Combine(parent,RestorePrefix+Guid.NewGuid().ToString("N")+".empty");Directory.Move(target,previous);
     if(Directory.EnumerateFileSystemEntries(previous).Any())throw new IOException("恢复目标刚被其他程序修改，未覆盖其中的文件。");
    }
    Directory.Move(stage,target);stage=null;
    if(previous!=null){try{RestoreSafe(previous);Directory.Delete(previous,false);}catch(IOException){}catch(UnauthorizedAccessException){}previous=null;}
   }catch(Exception error){
    string cleanup=null;
    if(previous!=null&&Directory.Exists(previous)&&!Directory.Exists(target)&&!File.Exists(target))try{Directory.Move(previous,target);previous=null;}catch(Exception recovery){cleanup="原空目录保留在："+previous+"。"+recovery.Message;}
    if(stage!=null&&Directory.Exists(stage))try{CleanRestoreStage(stage,parent);stage=null;}catch(Exception recovery){cleanup=(cleanup==null?"":cleanup+"\n")+"恢复暂存目录保留在："+stage+"。"+recovery.Message;}
    if(cleanup!=null)throw new IOException("恢复未完成，未覆盖已有数据。\n"+error.Message+"\n"+cleanup,error);
    throw;
   }
  }
 }
}
