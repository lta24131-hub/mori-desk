using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using MoriDesk;

namespace MoriDeskInstaller {
 static partial class Program {
  // Even a same-package route repair must not silently accept lost settings
  // and advertise success before the next normal launch opens a blank C: DB.
  // This check only reads existing files; no schema initialization or backup.
  internal static void ValidateManualExistingLibrary(){
   PhysicalUserContext.RequireInstalledContext();ValidateManualExistingLibraryInScope(PhysicalUserContext.DefaultRoot);
  }
  internal static void ValidateManualExistingLibraryInScope(string dataRoot){
   dataRoot=AppUpdateMetadataBackup.PlainPath(dataRoot);
   string settings=AppUpdateMetadataBackup.PlainPath(Path.Combine(dataRoot,"settings.json"));
   using(var source=new FileStream(settings,FileMode.Open,FileAccess.Read,FileShare.Read)){
    if(source.Length<1||source.Length>4L*1024*1024)throw new InvalidDataException("原设置缺失或无效，尚未修复启动入口。");
    string text;using(var reader=new StreamReader(source,new UTF8Encoding(false,true),true,1024,true))text=reader.ReadToEnd();
    string library=ReadManualLibraryPath(text),database=AppUpdateMetadataBackup.PlainPath(Path.Combine(library,".mori","library.db"));
    using(var file=new FileStream(database,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)){
     byte[] signature=Encoding.ASCII.GetBytes("SQLite format 3\0"),actual=new byte[signature.Length];
     if(file.Read(actual,0,actual.Length)!=signature.Length)throw new InvalidDataException("原图库索引不完整，未使用默认图库，也未修复启动入口。");
     for(int i=0;i<signature.Length;i++)if(signature[i]!=actual[i])throw new InvalidDataException("原图库索引无效，未使用默认图库，也未修复启动入口。");
    }
   }
  }
  // Call after the existing app has exited, before any candidate starts. The
  // caller owns the install lease and must treat an exception as an update veto.
  // false means a verified first installation, not a successful backup.
  internal static bool CreateManualMetadataBackup(string sessionDirectory,bool requireExistingBackup=false){
   PhysicalUserContext.RequireInstalledContext();
   return CreateManualMetadataBackupInScope(sessionDirectory,PhysicalUserContext.DefaultRoot,requireExistingBackup);
  }
  internal static bool CreateManualMetadataBackupInScope(string sessionDirectory,string dataRoot,bool requireExistingBackup=false){
   dataRoot=AppUpdateMetadataBackup.PlainPath(dataRoot).TrimEnd('\\','/');sessionDirectory=AppUpdateMetadataBackup.PlainPath(sessionDirectory).TrimEnd('\\','/');
   Guid id;string updates=Path.Combine(dataRoot,"updates");
   if(!Guid.TryParseExact(Path.GetFileName(sessionDirectory),"N",out id)||!string.Equals(Path.GetDirectoryName(sessionDirectory),updates,StringComparison.OrdinalIgnoreCase)||!Directory.Exists(sessionDirectory))throw new InvalidDataException("手动安装备份必须属于本次更新会话。");
   string settings=AppUpdateMetadataBackup.PlainPath(Path.Combine(dataRoot,"settings.json"));
   if(!File.Exists(settings)){
    if(requireExistingBackup)throw new IOException("已有安装记录但原设置缺失，无法确认原图库，已停止更新；不会创建默认图库。");
    RequireEmptyFirstInstallation(dataRoot,sessionDirectory);return false;
   }
   // Hold the configured route against replacement throughout the snapshot;
   // never silently switch to the default C: library if this record is damaged.
   using(var source=new FileStream(settings,FileMode.Open,FileAccess.Read,FileShare.Read)){
    if(source.Length<1||source.Length>4L*1024*1024)throw new InvalidDataException("原设置文件缺失或过大，未切换图库，也未继续安装。");
    string text;using(var reader=new StreamReader(source,new UTF8Encoding(false,true),true,1024,true))text=reader.ReadToEnd();
    string library=ReadManualLibraryPath(text),database=AppUpdateMetadataBackup.PlainPath(Path.Combine(library,".mori","library.db"));
    if(!Directory.Exists(library)||!File.Exists(database))throw new IOException("原设置指定的图库不可用，未创建空图库，也未继续安装。请连接原磁盘或恢复原设置。");
    AppUpdateMetadataBackup.Create(Path.Combine(sessionDirectory,"metadata-backup"),dataRoot,library,target=>ManualMetadataDatabase.Snapshot(database,target));return true;
   }
  }
  static void RequireEmptyFirstInstallation(string dataRoot,string sessionDirectory){
   if(!Directory.Exists(dataRoot))return;
   foreach(string entry in Directory.EnumerateFileSystemEntries(dataRoot)){
    AppUpdateMetadataBackup.PlainPath(entry);
    if(!string.Equals(Path.GetFileName(entry),"updates",StringComparison.OrdinalIgnoreCase)||!Directory.Exists(entry))throw new IOException("发现旧数据但缺少原设置，无法确认原图库，已停止安装。请先恢复设置；不会改用默认位置。");
    // Installer downloads/failed staging are not user data. An older recovery
    // snapshot, however, is evidence of an existing installation with lost
    // settings and must never be mistaken for a blank first run.
    foreach(string previous in Directory.EnumerateDirectories(entry)){
     AppUpdateMetadataBackup.PlainPath(previous);
     if(Directory.Exists(Path.Combine(previous,"metadata-backup"))||File.Exists(Path.Combine(previous,"metadata-backup")))throw new IOException("已有元数据备份但原设置缺失，已停止安装；请先核对原图库。");
    }
   }
  }
  internal static string ReadManualLibraryPath(string text){
   if(string.IsNullOrWhiteSpace(text)||text.Length>4*1024*1024)throw new InvalidDataException("原设置内容无效。");
   var json=new JavaScriptSerializer{MaxJsonLength=4*1024*1024,RecursionLimit=16};Dictionary<string,object> data;
   try{data=json.Deserialize<Dictionary<string,object>>(text);RejectDuplicateRootSettings(text,json);}catch(Exception error){throw new InvalidDataException("原设置无法明确读取，未继续安装。",error);}
   object value;if(data==null||!data.TryGetValue("LibraryPath",out value)||!(value is string)||string.IsNullOrWhiteSpace((string)value))throw new InvalidDataException("原设置中的图库位置缺失或无效，未使用默认图库。");
   return AppUpdateMetadataBackup.PlainPath((string)value).TrimEnd('\\','/');
  }
  static void RejectDuplicateRootSettings(string text,JavaScriptSerializer json){
   int depth=0;var names=new HashSet<string>(StringComparer.Ordinal);
   for(int i=0;i<text.Length;i++){
    char c=text[i];if(c=='{'||c=='['){depth++;continue;}if(c=='}'||c==']'){depth--;continue;}if(c!='"')continue;
    int begin=i++;for(;i<text.Length;i++){if(text[i]=='\\'){i++;continue;}if(text[i]=='"')break;}
    int after=i+1;while(after<text.Length&&char.IsWhiteSpace(text[after]))after++;
    if(depth==1&&after<text.Length&&text[after]==':'&&!names.Add(json.Deserialize<string>(text.Substring(begin,i-begin+1))))throw new InvalidDataException("原设置包含重复字段。");
   }
  }
 }
 // No Library/Database constructor: those initialize and migrate schema. This
 // opens only an existing source READONLY and asks SQLite for a consistent
 // VACUUM INTO snapshot, including committed WAL pages, into a fresh target.
 internal sealed class ManualMetadataDatabase:IDisposable {
  IntPtr database;readonly string filename;
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int QueryCallback(IntPtr state,int count,IntPtr values,IntPtr names);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int ProgressCallback(IntPtr state);
  [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_open_v2(byte[] file,out IntPtr handle,int flags,IntPtr vfs);
  [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_exec(IntPtr handle,byte[] sql,QueryCallback callback,IntPtr state,out IntPtr error);
  [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_busy_timeout(IntPtr handle,int milliseconds);
  [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]static extern void sqlite3_progress_handler(IntPtr handle,int instructions,ProgressCallback progress,IntPtr state);
  [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]static extern void sqlite3_free(IntPtr memory);
  [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_close(IntPtr handle);
  static byte[] Bytes(string text){return Encoding.UTF8.GetBytes(text+"\0");}
  static string Utf8(IntPtr pointer){if(pointer==IntPtr.Zero)return "";int length=0;while(Marshal.ReadByte(pointer,length)!=0&&length<65536)length++;byte[] bytes=new byte[length];Marshal.Copy(pointer,bytes,0,length);return Encoding.UTF8.GetString(bytes);}
  ManualMetadataDatabase(string source){
   filename=AppUpdateMetadataBackup.PlainPath(source);
   if(!File.Exists(filename)||new FileInfo(filename).Length<16||new FileInfo(filename).Length>AppUpdateMetadataBackup.MaximumFileBytes)throw new IOException("原图库索引缺失、不完整或超过备份上限，未继续安装。");
   if(sqlite3_open_v2(Bytes(filename),out database,0x00000001|0x00010000,IntPtr.Zero)!=0){Dispose();throw new IOException("无法只读打开原图库索引，未创建或修改原图库。");}
   try{if(sqlite3_busy_timeout(database,10000)!=0)throw new IOException("无法为原图库设置有界备份等待。");}catch{Dispose();throw;}
  }
  string Scalar(string sql){string result=null;QueryCallback callback=(state,count,values,names)=>{if(count>0&&result==null)result=Utf8(Marshal.ReadIntPtr(values));return 0;};Execute(sql,callback);return result;}
  void Execute(string sql,QueryCallback callback=null){
   IntPtr error;int result=sqlite3_exec(database,Bytes(sql),callback,IntPtr.Zero,out error);string details=Utf8(error);if(error!=IntPtr.Zero)sqlite3_free(error);
   if(result!=0)throw new IOException("原图库一致性备份未完成（SQLite "+result.ToString(CultureInfo.InvariantCulture)+"）："+details);
  }
  internal static void Snapshot(string source,string target){
   source=AppUpdateMetadataBackup.PlainPath(source);target=AppUpdateMetadataBackup.PlainPath(target);
   if(File.Exists(target)||Directory.Exists(target)||!Directory.Exists(Path.GetDirectoryName(target)))throw new IOException("备份索引目标必须是本次会话中的新文件。");
   using(var sourceLease=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
   using(var input=new ManualMetadataDatabase(source)){
    if(input.Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('assets','folders','memberships')")!="3")throw new InvalidDataException("原图库缺少基础索引表，未初始化、迁移或继续安装。");
    long pages,size;if(!long.TryParse(input.Scalar("PRAGMA page_count"),NumberStyles.None,CultureInfo.InvariantCulture,out pages)||!long.TryParse(input.Scalar("PRAGMA page_size"),NumberStyles.None,CultureInfo.InvariantCulture,out size)||size<=0||pages<1||pages>AppUpdateMetadataBackup.MaximumFileBytes/size)throw new IOException("原图库有效索引超过一致性备份上限，未继续安装。");
    var elapsed=Stopwatch.StartNew();bool exceeded=false;ProgressCallback progress=state=>{try{exceeded=elapsed.ElapsedMilliseconds>60000||(File.Exists(target)&&new FileInfo(target).Length>AppUpdateMetadataBackup.MaximumFileBytes);return exceeded?1:0;}catch{return 1;}};
    sqlite3_progress_handler(input.database,2000,progress,IntPtr.Zero);
    try{input.Execute("VACUUM INTO '"+target.Replace("'","''")+"'");}
    finally{sqlite3_progress_handler(input.database,0,null,IntPtr.Zero);GC.KeepAlive(progress);}
    if(exceeded||!File.Exists(target)||new FileInfo(target).Length>AppUpdateMetadataBackup.MaximumFileBytes)throw new IOException("备份超过时间或容量上限，未继续安装。");
    using(var snapshot=new ManualMetadataDatabase(target))if(snapshot.Scalar("PRAGMA quick_check")!="ok")throw new IOException("一致性备份校验未通过，未继续安装。");
   }
  }
  public void Dispose(){if(database==IntPtr.Zero)return;sqlite3_close(database);database=IntPtr.Zero;}
 }
}
