using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace MoriDeskInstaller {
 // The caller owns startup/shortcut/native-host verification. No registry,
 // process manipulation, user-data discovery or recursive delete happens here.
 internal sealed class InstalledCleanupGate {
  internal bool StartupReady, EntrypointsSwitched, SnapshotComplete;
  internal string ReadyExecutable;
  internal string[] RunningExecutables, ReferencedExecutables, ProtectedPaths;
 }
 internal sealed class InstalledAbandonedStageGate {
  // These facts come from the caller's sealed installer session and current
  // committed descriptor, freshly read while it owns the installation lease.
  // They do not imply that an abandoned candidate ever started successfully.
  internal bool SessionOwnershipVerified,Uncommitted,SnapshotComplete;
  internal string ActiveExecutable,ExpectedManifestSha256,ExpectedExecutableSha256;
  internal string[] RunningExecutables,ReferencedExecutables,ProtectedPaths;
 }
 internal sealed class InstalledCleanupItem {
  internal string Directory, Reason;
  internal InstalledCleanupItem(string directory,string reason){Directory=directory;Reason=reason;}
 }
 internal sealed class InstalledCleanupResult {
  internal readonly List<InstalledCleanupItem> Deleted=new List<InstalledCleanupItem>();
  internal readonly List<InstalledCleanupItem> Pending=new List<InstalledCleanupItem>();
  internal readonly List<InstalledCleanupItem> Skipped=new List<InstalledCleanupItem>();
  internal bool Complete {get{return Pending.Count==0&&Skipped.Count==0;}}
 }
 internal sealed class InstalledPayloadManifest {
  public int schemaVersion;
  public string directory,product,originalFilename,fileVersion;
  public Dictionary<string,string> files;
  public string[] directories;
 }
 internal sealed class InstalledCleanupProgress {
  public int schemaVersion;
  public string directory,manifestSha256;
 }
 internal sealed class InstalledCleanupJournal {
  public int schemaVersion;
  public string directory,manifestSha256,manifestJson;
 }
 internal static class InstalledVersionCleanup {
  internal const string ManifestName=".nook-installed-files.json";
  internal const string ProgressName=".nook-cleanup-progress.json";
  internal const string JournalDirectoryName=".nook-cleanup-journal";
  const string JournalOwner="Nook installed-version cleanup v1\n";
  const int MaximumFiles=20000,MaximumRecordBytes=8*1024*1024;
  const uint GenericRead=0x80000000,DeleteAccess=0x00010000,ReadAttributes=0x80;
  const uint ShareRead=1,ShareWrite=2,OpenExisting=3,BackupSemantics=0x02000000,OpenReparsePoint=0x00200000;
  static readonly Regex VersionDirectory=new Regex(@"\A([0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?)-([0-9]{14})(?:[0-9]{3}-[0-9a-f]{8})?\z",RegexOptions.CultureInvariant);
  static readonly Regex Digest=new Regex(@"\A[0-9A-F]{64}\z",RegexOptions.CultureInvariant);
  static readonly StringComparer Names=StringComparer.OrdinalIgnoreCase;
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,StringBuilder path,uint count,uint flags);
  [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetFileInformationByHandle(SafeFileHandle handle,int type,ref Disposition info,uint bytes);
  [StructLayout(LayoutKind.Sequential)]struct Disposition {public int DeleteFile;}
  static Exception WinError(string operation){return new IOException(operation,new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));}
  static string Full(string path){
   if(string.IsNullOrWhiteSpace(path)||path.Length<4||!char.IsLetter(path[0])||path[1]!=':'||(path[2]!='\\'&&path[2]!='/')||path.Substring(2).IndexOf(':')>=0||path.IndexOfAny(new[]{'"','\0','\r','\n'})>=0)throw new InvalidDataException("仅允许完整本地安装路径。");
   return Path.GetFullPath(path).TrimEnd('\\','/');
  }
  static void Plain(string path){
   for(string part=Full(path);!string.IsNullOrEmpty(part);part=Path.GetDirectoryName(part))
    if((File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new IOException("安装目录含链接，未清理旧版本。");
  }
  static string Final(SafeFileHandle handle){var value=new StringBuilder(32768);uint count=GetFinalPathNameByHandle(handle,value,(uint)value.Capacity,0);if(count==0||count>=value.Capacity)throw WinError("无法确认文件实际位置。");string path=value.ToString();return path.StartsWith(@"\\?\",StringComparison.Ordinal)?path.Substring(4):path;}
  static SafeFileHandle OpenLocked(string path,bool directory,bool deletion,bool exclusive=false){
   // Exclusive old-file handles also prevent a new old-version process from
   // loading between the final process snapshot and deletion.
   Plain(path);var handle=CreateFile(path,(directory?ReadAttributes:GenericRead)|(deletion?DeleteAccess:0),directory?ShareRead|ShareWrite:((deletion||exclusive)?0:ShareRead),IntPtr.Zero,OpenExisting,OpenReparsePoint|(directory?BackupSemantics:0),IntPtr.Zero);
   if(handle.IsInvalid){handle.Dispose();throw WinError("文件正在使用或无法读取，旧版本稍后重试。");}
   try{if(!Names.Equals(Full(path),Full(Final(handle))))throw new IOException("文件实际位置与安装目录不同，未清理。");Plain(path);return handle;}catch{handle.Dispose();throw;}
  }
  static string Hash(Stream input){input.Position=0;using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(input)).Replace("-","");}
  static string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");}
  internal static bool ConfirmedMissing(string path){
   // Exists() collapses permission and I/O failures into false. Recovery may
   // retire its journal only when the filesystem explicitly reports absence.
   try{File.GetAttributes(path);return false;}catch(FileNotFoundException){return true;}catch(DirectoryNotFoundException){return true;}
  }
  static byte[] Read(Stream input,int maximum){if(input.Length<1||input.Length>maximum)throw new InvalidDataException("安装清单大小无效。");input.Position=0;byte[] bytes=new byte[(int)input.Length];int offset=0;while(offset<bytes.Length){int count=input.Read(bytes,offset,bytes.Length-offset);if(count==0)throw new EndOfStreamException();offset+=count;}return bytes;}
  static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=MaximumRecordBytes,RecursionLimit=8};}
  static T Deserialize<T>(byte[] bytes){try{return Json().Deserialize<T>(new UTF8Encoding(false,true).GetString(bytes));}catch(Exception error){throw new InvalidDataException("安装清单无法读取。",error);}}
  static void WriteNew(string path,object value){byte[] bytes=new UTF8Encoding(false).GetBytes(Json().Serialize(value));using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(bytes,0,bytes.Length);output.Flush(true);}}
  static Version FolderVersion(string folder){
   var match=VersionDirectory.Match(Path.GetFileName(folder));Version version;DateTime date;
   if(!match.Success||!Version.TryParse(match.Groups[1].Value,out version)||!DateTime.TryParseExact(match.Groups[2].Value,"yyyyMMddHHmmss",CultureInfo.InvariantCulture,DateTimeStyles.None,out date))throw new InvalidDataException("不属于受管版本目录。");
   return Normalize(version);
  }
  static Version Normalize(Version version){return new Version(version.Major,version.Minor,Math.Max(0,version.Build),Math.Max(0,version.Revision));}
  static void Child(string root,string folder){if(!Names.Equals(Path.GetDirectoryName(Full(folder)),Full(root)))throw new InvalidDataException("只允许清理安装根目录的直接子目录。");FolderVersion(folder);}
  static bool Product(string product,string original){return (Names.Equals(product,"Nook")||Names.Equals(product,"Mori Desk"))&&Names.Equals(original,"Mori.exe");}
  static FileVersionInfo Identity(string executable,Version expected){var info=FileVersionInfo.GetVersionInfo(executable);Version version;if(!Product(info.ProductName,info.OriginalFilename)||!Version.TryParse(info.FileVersion,out version)||Normalize(version)!=expected)throw new InvalidDataException("程序身份或版本与安装清单不符。");return info;}
  static string Relative(string folder,string path){string prefix=Full(folder)+"\\";string absolute=Full(path);if(!absolute.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("安装文件越出版本目录。");return absolute.Substring(prefix.Length);}
  static string Resolve(string folder,string relative){
   if(string.IsNullOrWhiteSpace(relative)||Path.IsPathRooted(relative)||relative.IndexOfAny(new[]{':','/','\0','\r','\n'})>=0||relative.Split('\\').Any(p=>p.Length==0||p=="."||p==".."||p.EndsWith(".",StringComparison.Ordinal)||p.EndsWith(" ",StringComparison.Ordinal)))throw new InvalidDataException("安装清单含无效相对路径。");
   string path=Full(Path.Combine(folder,relative));if(!Names.Equals(Relative(folder,path),relative))throw new InvalidDataException("安装清单含非规范路径。");return path;
  }
  sealed class LockedTree : IDisposable {
   internal readonly List<KeyValuePair<string,SafeFileHandle>> Directories=new List<KeyValuePair<string,SafeFileHandle>>();
   internal readonly Dictionary<string,FileStream> Files=new Dictionary<string,FileStream>(Names);
   internal LockedTree(string folder,bool deletion){try{Scan(folder,deletion);}catch{Dispose();throw;}}
   void Scan(string folder,bool deletion){
    if(Directories.Count+Files.Count>=MaximumFiles)throw new InvalidDataException("安装文件数量超出清理上限。");
    Directories.Add(new KeyValuePair<string,SafeFileHandle>(folder,OpenLocked(folder,true,deletion)));
    foreach(string path in Directory.EnumerateFileSystemEntries(folder)){
     var attributes=File.GetAttributes(path);if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("旧目录含链接，未清理。");
     if((attributes&FileAttributes.Directory)!=0)Scan(path,deletion);else{if(Directories.Count+Files.Count>=MaximumFiles)throw new InvalidDataException("安装文件数量超出清理上限。");Files.Add(path,new FileStream(OpenLocked(path,false,deletion),FileAccess.Read));}
    }
   }
   public void Dispose(){foreach(var file in Files.Values)file.Dispose();foreach(var directory in Directories)directory.Value.Dispose();}
  }
  internal static void WriteManifest(string folder){WriteManifestCore(folder,null,null);}
  internal static void VerifyReadOnly(string folder){
   // A healthy main executable is insufficient: a missing runtime, browser
   // resource or empty directory can still make the installed build unusable.
   // Inspect the whole sealed payload under read-only physical-path locks.
   // Never mint ownership, resume deletion, or repair files during this check.
   folder=Full(folder);Version version=FolderVersion(folder);
   using(var tree=new LockedTree(folder,false)){
    FileStream manifestStream;string marker=Path.Combine(folder,ManifestName);
    if(!tree.Files.TryGetValue(marker,out manifestStream))throw new InvalidDataException("安装清单缺失，无法确认现有程序完整性。");
    var manifest=ValidateManifest(Read(manifestStream,MaximumRecordBytes),folder);
    string executable=Path.Combine(folder,"Mori.exe");
    if(!tree.Files.ContainsKey(executable))throw new InvalidDataException("安装目录缺少主程序。");
    Identity(executable,version);
    var allowed=new HashSet<string>(manifest.files.Keys,Names);allowed.Add(ManifestName);
    string pairing=Path.Combine(folder,@"browser-extension\local-connection.js");FileStream pairingStream;
    if(tree.Files.TryGetValue(pairing,out pairingStream)&&!manifest.files.ContainsKey(@"browser-extension\local-connection.js")){
     if(!GeneratedPairingFile(Read(pairingStream,4096)))throw new IOException("安装目录中的本机连接副本无法确认，已保留待修复。");
     allowed.Add(@"browser-extension\local-connection.js");
    }
    if(!allowed.SetEquals(tree.Files.Keys.Select(path=>Relative(folder,path))))throw new IOException("现有安装文件缺失或含清单外文件，需要修复。");
    if(!new HashSet<string>(manifest.directories,Names).SetEquals(tree.Directories.Skip(1).Select(pair=>Relative(folder,pair.Key))))throw new IOException("现有安装目录缺失或含清单外目录，需要修复。");
    foreach(var file in manifest.files)if(Hash(tree.Files[Resolve(folder,file.Key)])!=file.Value)throw new IOException("现有安装文件与原安装清单不同，需要修复。");
   }
  }
  internal static void WriteManifest(string folder,IEnumerable<string> acceptedRelativeFiles,IEnumerable<string> acceptedRelativeDirectories){
   if(acceptedRelativeFiles==null||acceptedRelativeDirectories==null)throw new ArgumentNullException();WriteManifestCore(folder,acceptedRelativeFiles,acceptedRelativeDirectories);
  }
  static void WriteManifestCore(string folder,IEnumerable<string> acceptedRelativeFiles,IEnumerable<string> acceptedRelativeDirectories){
   folder=Full(folder);Version version=FolderVersion(folder);string manifest=Path.Combine(folder,ManifestName);
   if(File.Exists(manifest)||File.Exists(Path.Combine(folder,ProgressName)))throw new IOException("安装清单已存在，未覆盖。");
   using(var tree=new LockedTree(folder,false)){
    if(acceptedRelativeFiles!=null){
     var files=new HashSet<string>(acceptedRelativeFiles,Names);var directories=new HashSet<string>(acceptedRelativeDirectories,Names);
     foreach(string file in files)Resolve(folder,file);foreach(string directory in directories)Resolve(folder,directory);
     if(!files.SetEquals(tree.Files.Keys.Select(path=>Relative(folder,path)))||!directories.SetEquals(tree.Directories.Skip(1).Select(pair=>Relative(folder,pair.Key))))throw new IOException("实际提取内容与安装包清单不符，未建立安装归属。");
    }
    string executable=Path.Combine(folder,"Mori.exe");if(!tree.Files.ContainsKey(executable))throw new InvalidDataException("安装目录缺少主程序。");var identity=Identity(executable,version);
    var hashes=new Dictionary<string,string>(Names);foreach(var file in tree.Files)hashes.Add(Relative(folder,file.Key),Hash(file.Value));
    var record=new InstalledPayloadManifest{schemaVersion=1,directory=Path.GetFileName(folder),product=identity.ProductName,originalFilename=identity.OriginalFilename,fileVersion=version.ToString(),files=hashes,directories=tree.Directories.Skip(1).Select(pair=>Relative(folder,pair.Key)).ToArray()};
    WriteNew(manifest,record);
   }
  }
  internal static void WriteVerifiedLegacyManifest(string folder,IDictionary<string,string> trustedFileHashes,IEnumerable<string> trustedRelativeDirectories){
   // Trust must originate outside this directory (for example a SHA-pinned
   // release archive). Never create this inventory from a legacy disk scan.
   if(trustedFileHashes==null||trustedRelativeDirectories==null)throw new ArgumentNullException();
   folder=Full(folder);Version version=FolderVersion(folder);string marker=Path.Combine(folder,ManifestName);
   if(!ConfirmedMissing(marker)||!ConfirmedMissing(Path.Combine(folder,ProgressName))||!ConfirmedMissing(JournalPath(Path.GetDirectoryName(folder),folder)))throw new IOException("此版本已有安装或恢复记录，未重新认领。");
   var hashes=new Dictionary<string,string>(Names);foreach(var file in trustedFileHashes){Resolve(folder,file.Key);if(hashes.ContainsKey(file.Key))throw new InvalidDataException("可信包清单含重复文件。");hashes.Add(file.Key,file.Value);}
   string[] directories=trustedRelativeDirectories.ToArray();
   using(var tree=new LockedTree(folder,false)){
    string executable=Path.Combine(folder,"Mori.exe");if(!tree.Files.ContainsKey(executable))throw new InvalidDataException("旧版缺少主程序，无法认证。");var identity=Identity(executable,version);
    var record=new InstalledPayloadManifest{schemaVersion=1,directory=Path.GetFileName(folder),product=identity.ProductName,originalFilename=identity.OriginalFilename,fileVersion=version.ToString(),files=hashes,directories=directories};
    ValidateManifest(new UTF8Encoding(false).GetBytes(Json().Serialize(record)),folder);
    var allowed=new HashSet<string>(hashes.Keys,Names);string pairing=Path.Combine(folder,@"browser-extension\local-connection.js");FileStream pairingStream;
    if(tree.Files.TryGetValue(pairing,out pairingStream)&&!hashes.ContainsKey(@"browser-extension\local-connection.js")){
     if(!GeneratedPairingFile(Read(pairingStream,4096)))throw new IOException("本机连接副本不符合规范，未认证旧版本。");allowed.Add(@"browser-extension\local-connection.js");
    }
    if(!allowed.SetEquals(tree.Files.Keys.Select(path=>Relative(folder,path)))||!new HashSet<string>(directories,Names).SetEquals(tree.Directories.Skip(1).Select(pair=>Relative(folder,pair.Key))))throw new IOException("旧版文件及目录与可信发行包不符，已保留。");
    foreach(var file in hashes)if(Hash(tree.Files[Resolve(folder,file.Key)])!=file.Value)throw new IOException("旧版文件内容与可信发行包不符，已保留。");
    // The same handles remain open through manifest creation: authenticated
    // files cannot be swapped or modified between comparison and ownership.
    WriteNew(marker,record);
   }
  }
  internal static bool GeneratedPairingFile(byte[] bytes){
   if(bytes==null||bytes.Length<1||bytes.Length>4096)return false;
   string text;try{text=new UTF8Encoding(false,true).GetString(bytes);}catch(DecoderFallbackException){return false;}
   return Regex.IsMatch(text,@"\A// Generated on this computer\. Private pairing key; do not publish\.\nself\.MoriLocalConnection = \{""endpoint"":""http://127\.0\.0\.1:47891/mori/v1"",""token"":""[0-9a-f]{64}""(?:,""installationId"":""[0-9a-f]{32}"")?\};\n\z",RegexOptions.CultureInvariant);
  }
  internal static string JournalPath(string root,string folder){return Path.Combine(root,JournalDirectoryName,Hash(Encoding.UTF8.GetBytes(Path.GetFileName(folder).ToUpperInvariant()))+".json");}
  sealed class JournalScope : IDisposable {
   internal string DirectoryPath;SafeFileHandle directory;FileStream owner;
   internal JournalScope(string root){
    try{
     DirectoryPath=Path.Combine(root,JournalDirectoryName);if(!Directory.Exists(DirectoryPath))Directory.CreateDirectory(DirectoryPath);directory=OpenLocked(DirectoryPath,true,false);
     string ownerPath=Path.Combine(DirectoryPath,"owner.txt");
     if(!File.Exists(ownerPath)){if(Directory.EnumerateFileSystemEntries(DirectoryPath).Any())throw new IOException("清理记录目录含未知内容，未使用。");using(var output=new FileStream(ownerPath,FileMode.CreateNew,FileAccess.Write,FileShare.None)){byte[] bytes=Encoding.UTF8.GetBytes(JournalOwner);output.Write(bytes,0,bytes.Length);output.Flush(true);}}
     owner=new FileStream(OpenLocked(ownerPath,false,false,true),FileAccess.Read);
     if(new UTF8Encoding(false,true).GetString(Read(owner,4096))!=JournalOwner)throw new IOException("清理记录归属无法验证。");
     foreach(string entry in Directory.EnumerateFileSystemEntries(DirectoryPath))if(!Names.Equals(entry,ownerPath)&&!Regex.IsMatch(Path.GetFileName(entry),@"\A[0-9A-F]{64}\.json\z",RegexOptions.CultureInvariant))throw new IOException("清理记录目录含未知内容，未使用。");
    }catch{Dispose();throw;}
   }
   public void Dispose(){if(owner!=null)owner.Dispose();if(directory!=null)directory.Dispose();}
  }
  static byte[] JournalManifest(FileStream stream,string folder){
   var record=Deserialize<InstalledCleanupJournal>(Read(stream,MaximumRecordBytes));
   if(record==null||record.schemaVersion!=1||record.directory!=Path.GetFileName(folder)||string.IsNullOrEmpty(record.manifestJson)||!Digest.IsMatch(record.manifestSha256??""))throw new InvalidDataException("外置清理记录身份无效。");
   byte[] bytes=new UTF8Encoding(false,true).GetBytes(record.manifestJson);if(Hash(bytes)!=record.manifestSha256)throw new InvalidDataException("外置清理记录内容无法验证。");ValidateManifest(bytes,folder);return bytes;
  }
  static bool Inside(string path,string folder){string full=Full(path),root=Full(folder);return Names.Equals(full,root)||full.StartsWith(root+"\\",StringComparison.OrdinalIgnoreCase);}
  static void Gate(InstalledCleanupGate gate,string current,string old){
   if(gate==null||!gate.StartupReady||!gate.EntrypointsSwitched||!gate.SnapshotComplete||gate.RunningExecutables==null||gate.ReferencedExecutables==null||gate.ProtectedPaths==null||!Names.Equals(Full(gate.ReadyExecutable),current))throw new IOException("新版启动或安装入口尚未确认，旧版本暂不清理。");
   if(gate.RunningExecutables.Any(path=>Inside(path,old)))throw new IOException("旧版本仍有进程在使用，退出后可重试清理。");
   if(gate.ReferencedExecutables.Any(path=>Inside(path,old)))throw new IOException("快捷方式、打开方式或浏览器连接仍引用此旧版本，修复入口后可重试。");
   if(gate.ProtectedPaths.Any(path=>Inside(path,old)||Inside(old,path)))throw new IOException("此版本目录与素材、设置、转换结果或组件目录重叠，已保留。");
  }
  static void AbandonedGate(InstalledAbandonedStageGate gate,string current,string stage){
   if(gate==null||!gate.SessionOwnershipVerified||!gate.Uncommitted||!gate.SnapshotComplete||gate.RunningExecutables==null||gate.ReferencedExecutables==null||gate.ProtectedPaths==null||!Digest.IsMatch((gate.ExpectedManifestSha256??"").ToUpperInvariant())||!Digest.IsMatch((gate.ExpectedExecutableSha256??"").ToUpperInvariant())||(current==null?gate.ActiveExecutable!=null:!Names.Equals(Full(gate.ActiveExecutable),current)))throw new IOException("暂存版本归属或未提交状态尚未确认，未清理。");
   if(gate.RunningExecutables.Any(path=>Inside(path,stage)))throw new IOException("暂存版本仍有进程在使用，未清理。");
   if(gate.ReferencedExecutables.Any(path=>Inside(path,stage)))throw new IOException("暂存版本仍被启动或浏览器入口引用，未清理。");
   if(gate.ProtectedPaths.Any(path=>Inside(path,stage)||Inside(stage,path)))throw new IOException("暂存版本与用户数据路径重叠，已保留。");
  }
  static InstalledPayloadManifest ValidateManifest(byte[] bytes,string folder){
   // Typed deserialization ignores unknown fields. Reject those instead of
   // declaring an unrecognized ownership format healthy or safe to remove.
   var shape=Deserialize<Dictionary<string,object>>(bytes);
   string[] fields={"schemaVersion","directory","product","originalFilename","fileVersion","files","directories"};
   if(shape==null||shape.Count!=fields.Length||fields.Any(field=>!shape.ContainsKey(field)))throw new InvalidDataException("安装清单格式无法识别。");
   var manifest=Deserialize<InstalledPayloadManifest>(bytes);Version version;
   if(manifest==null||manifest.schemaVersion!=1||manifest.directory!=Path.GetFileName(folder)||!Product(manifest.product,manifest.originalFilename)||!Version.TryParse(manifest.fileVersion,out version)||Normalize(version)!=FolderVersion(folder)||manifest.files==null||manifest.files.Count==0||manifest.files.Count>MaximumFiles||manifest.directories==null||manifest.directories.Length>MaximumFiles)throw new InvalidDataException("安装清单身份无效。");
   var unique=new HashSet<string>(Names);foreach(var file in manifest.files){Resolve(folder,file.Key);if(!unique.Add(file.Key)||!Digest.IsMatch(file.Value??"")||Names.Equals(file.Key,ManifestName)||Names.Equals(file.Key,ProgressName))throw new InvalidDataException("安装清单内容无效。");}
   foreach(string directory in manifest.directories){Resolve(folder,directory);if(!unique.Add(directory)||Names.Equals(directory,ManifestName)||Names.Equals(directory,ProgressName))throw new InvalidDataException("安装目录清单无效。");}
   if(!manifest.files.Keys.Any(name=>Names.Equals(name,"Mori.exe")))throw new InvalidDataException("安装清单未包含主程序。");return manifest;
  }
  static void MarkDelete(SafeFileHandle handle){var disposition=new Disposition{DeleteFile=1};if(!SetFileInformationByHandle(handle,4,ref disposition,4))throw WinError("文件仍在使用或删除被阻止，可稍后重试。");}
  static void DeleteFile(LockedTree tree,string path){var stream=tree.Files[path];MarkDelete(stream.SafeFileHandle);stream.Dispose();}
  static void DeleteOne(string root,string folder,Action validateState,Action<byte[],InstalledPayloadManifest> verifyOwnership=null){
   Child(root,folder);validateState();
   string marker=Path.Combine(folder,ManifestName),progressPath=Path.Combine(folder,ProgressName),journalPath=JournalPath(root,folder);
   string executable=Path.Combine(folder,"Mori.exe");
   // Metadata is read before exclusive locking; the subsequently locked file
   // must match the full inventoried hash, so replacement cannot substitute
   // different bytes after this check.
   if(File.Exists(executable))Identity(executable,FolderVersion(folder));
   FileStream journal=null;
   try{if(File.Exists(journalPath))journal=new FileStream(OpenLocked(journalPath,false,true),FileAccess.Read);
   using(var tree=new LockedTree(folder,true)){
    FileStream manifestStream;byte[] manifestBytes;
    if(tree.Files.TryGetValue(marker,out manifestStream))manifestBytes=Read(manifestStream,MaximumRecordBytes);
    else if(journal!=null)manifestBytes=JournalManifest(journal,folder);
    else throw new InvalidDataException("旧版本缺少可信安装清单，未删除。");
    var manifest=ValidateManifest(manifestBytes,folder);string digest=Hash(manifestBytes);if(verifyOwnership!=null)verifyOwnership(manifestBytes,manifest);
    if(journal!=null&&Hash(JournalManifest(journal,folder))!=digest)throw new InvalidDataException("内外安装清单不一致，未继续清理。");
    FileStream progressStream;bool localProgress=tree.Files.TryGetValue(progressPath,out progressStream),resuming=journal!=null||localProgress;
    if(localProgress){var progress=Deserialize<InstalledCleanupProgress>(Read(progressStream,4096));if(progress==null||progress.schemaVersion!=1||progress.directory!=Path.GetFileName(folder)||progress.manifestSha256!=digest)throw new InvalidDataException("旧版清理进度无法验证，未继续删除。");}
    var allowed=new HashSet<string>(manifest.files.Keys,Names);allowed.Add(ManifestName);if(localProgress)allowed.Add(ProgressName);
    string pairing=Path.Combine(folder,@"browser-extension\local-connection.js");FileStream pairingStream;
    if(tree.Files.TryGetValue(pairing,out pairingStream)&&!manifest.files.ContainsKey(@"browser-extension\local-connection.js")){
     if(!GeneratedPairingFile(Read(pairingStream,4096)))throw new IOException("旧程序中的本机连接副本不是规范生成文件，已保留待核对。");allowed.Add(@"browser-extension\local-connection.js");
    }
    foreach(var file in tree.Files)if(!allowed.Contains(Relative(folder,file.Key)))throw new IOException("旧安装目录含清单外文件，已保留，需先核对。");
    var expectedDirectories=new HashSet<string>(manifest.directories,Names);foreach(var directory in tree.Directories.Skip(1))if(!expectedDirectories.Contains(Relative(folder,directory.Key)))throw new IOException("旧安装目录含未知文件夹，已保留。");
    if(!resuming&&tree.Directories.Count-1!=expectedDirectories.Count)throw new IOException("旧版文件夹不完整，未删除。");
    foreach(var file in manifest.files){FileStream opened;if(!tree.Files.TryGetValue(Resolve(folder,file.Key),out opened)){if(!resuming)throw new IOException("旧版文件不完整，不能确认目录归属，未删除。");}else if(Hash(opened)!=file.Value)throw new IOException("旧版文件与安装清单不同（可能已单独更新插件），已保留待核对。");}
    if(!tree.Files.ContainsKey(executable)&&!resuming)throw new InvalidDataException("旧版主程序缺失，未删除。");
    validateState();
    if(journal==null){WriteNew(journalPath,new InstalledCleanupJournal{schemaVersion=1,directory=Path.GetFileName(folder),manifestSha256=digest,manifestJson=new UTF8Encoding(false,true).GetString(manifestBytes)});journal=new FileStream(OpenLocked(journalPath,false,true),FileAccess.Read);if(Hash(JournalManifest(journal,folder))!=digest)throw new IOException("外置清理记录未能完成核对。");}
    if(!localProgress){WriteNew(progressPath,new InstalledCleanupProgress{schemaVersion=1,directory=Path.GetFileName(folder),manifestSha256=digest});tree.Files.Add(progressPath,new FileStream(OpenLocked(progressPath,false,true),FileAccess.Read));}
    // The executable and records go last so a sharing violation early in the
    // pass leaves a provable, retryable inventory. No broad delete is used.
    foreach(string path in tree.Files.Keys.Where(path=>!Names.Equals(path,executable)&&!Names.Equals(path,marker)&&!Names.Equals(path,progressPath)).ToArray())DeleteFile(tree,path);
    foreach(var directory in tree.Directories.AsEnumerable().Reverse().Where(d=>!Names.Equals(d.Key,folder))){MarkDelete(directory.Value);directory.Value.Dispose();}
    foreach(string remaining in Directory.EnumerateFileSystemEntries(folder))if(!Names.Equals(remaining,executable)&&!Names.Equals(remaining,marker)&&!Names.Equals(remaining,progressPath))throw new IOException("清理期间出现新文件，已保留并暂停；核对后可重试。");
    if(tree.Files.ContainsKey(executable))DeleteFile(tree,executable);
    DeleteFile(tree,progressPath);if(tree.Files.ContainsKey(marker))DeleteFile(tree,marker);
    MarkDelete(tree.Directories[0].Value);tree.Directories[0].Value.Dispose();
   }
   if(!ConfirmedMissing(folder))throw new IOException("旧版目录仍存在，未将清理标记为完成。");
   MarkDelete(journal.SafeFileHandle);journal.Dispose();journal=null;
   }finally{if(journal!=null)journal.Dispose();}
  }
  internal static void RemoveAbandonedStage(string programRoot,string currentExecutable,string stageDirectory,Func<string,InstalledAbandonedStageGate> gate){
   if(gate==null)throw new ArgumentNullException("gate");
   string root=Full(programRoot),current=currentExecutable==null?null:Full(currentExecutable),stage=Full(stageDirectory),currentFolder=current==null?null:Path.GetDirectoryName(current);
   if(!Names.Equals(Path.GetFileName(root),"MoriDesk")||(current!=null&&!Names.Equals(Path.GetFileName(current),"Mori.exe")))throw new InvalidDataException("不是受管 Nook 安装目录。");
   if(current!=null)Child(root,currentFolder);Child(root,stage);if(Names.Equals(currentFolder,stage))throw new InvalidDataException("当前版本不能作为遗留暂存版本清理。");
   string expectedManifest=null,expectedExecutable=null;
   Action validate=()=>{
    var evidence=gate(stage);AbandonedGate(evidence,current,stage);
    string manifest=evidence.ExpectedManifestSha256.ToUpperInvariant(),executable=evidence.ExpectedExecutableSha256.ToUpperInvariant();
    if(expectedManifest==null){expectedManifest=manifest;expectedExecutable=executable;}
    else if(expectedManifest!=manifest||expectedExecutable!=executable)throw new IOException("暂存版本的独立归属校验值发生变化，未继续清理。");
   };
   Action<byte[],InstalledPayloadManifest> verify=(bytes,manifest)=>{
    if(Hash(bytes)!=expectedManifest||manifest.files.First(file=>Names.Equals(file.Key,"Mori.exe")).Value!=expectedExecutable)throw new IOException("暂存文件清单或程序与独立会话记录不同，未清理。");
   };
   using(var rootLock=OpenLocked(root,true,false))using(var currentLock=current==null?null:new FileStream(OpenLocked(current,false,false),FileAccess.Read))using(var journals=new JournalScope(root)){
    if(current!=null)Identity(current,FolderVersion(currentFolder));validate();
    if(!ConfirmedMissing(stage)){DeleteOne(root,stage,validate,verify);return;}
    // A crash may remove the exact stage before retiring its external record.
    // Only this explicitly authorized target's journal is inspected or removed.
    string journalPath=JournalPath(root,stage);if(ConfirmedMissing(journalPath))return;
    using(var journal=new FileStream(OpenLocked(journalPath,false,true),FileAccess.Read)){
     byte[] manifest=JournalManifest(journal,stage);verify(manifest,ValidateManifest(manifest,stage));validate();MarkDelete(journal.SafeFileHandle);
    }
   }
  }
  internal static InstalledCleanupResult Cleanup(string programRoot,string currentExecutable,Func<string,InstalledCleanupGate> gate,Func<string,bool> certifyLegacy=null){
   if(gate==null)throw new ArgumentNullException("gate");string root=Full(programRoot),current=Full(currentExecutable),currentFolder=Path.GetDirectoryName(current);
   if(!Names.Equals(Path.GetFileName(root),"MoriDesk")||!Names.Equals(Path.GetFileName(current),"Mori.exe"))throw new InvalidDataException("不是受管 Nook 安装目录。");
   Child(root,currentFolder);var result=new InstalledCleanupResult();
   using(var rootLock=OpenLocked(root,true,false))using(var currentLock=new FileStream(OpenLocked(current,false,false),FileAccess.Read))using(var journals=new JournalScope(root)){
    Version currentVersion=FolderVersion(currentFolder);Identity(current,currentVersion);
    foreach(string folder in Directory.EnumerateDirectories(root)){
     // The stable bootstrap is shared infrastructure, never a version payload.
     // Do not inspect, certify or report it as unfinished old-version cleanup;
     // its own installer migration owns any validation/replacement of it.
     if(Names.Equals(folder,currentFolder)||Names.Equals(folder,journals.DirectoryPath)||Names.Equals(folder,Path.Combine(root,"Launcher")))continue;
     try{
      Child(root,folder);Version oldVersion=FolderVersion(folder);
      if(oldVersion>currentVersion||(oldVersion==currentVersion&&string.Compare(Path.GetFileName(folder),Path.GetFileName(currentFolder),StringComparison.OrdinalIgnoreCase)>=0)){result.Skipped.Add(new InstalledCleanupItem(folder,"newer-or-ambiguous-version"));continue;}
      Plain(folder);if(!File.Exists(Path.Combine(folder,ManifestName))&&!File.Exists(JournalPath(root,folder))){
       if(certifyLegacy==null){result.Skipped.Add(new InstalledCleanupItem(folder,"legacy-version-without-manifest"));continue;}
       Gate(gate(folder),current,folder);
       if(!certifyLegacy(folder)){result.Skipped.Add(new InstalledCleanupItem(folder,"legacy-version-without-manifest"));continue;}
      }
      DeleteOne(root,folder,()=>Gate(gate(folder),current,folder));result.Deleted.Add(new InstalledCleanupItem(folder,"deleted"));
     }catch(Exception error){if(error is IOException||error is InvalidDataException||error is UnauthorizedAccessException||error is ArgumentException||error is System.ComponentModel.Win32Exception)result.Pending.Add(new InstalledCleanupItem(folder,error.Message));else throw;}
    }
    // A crash after directory removal but before journal removal is completed
    // separately. The journal never authorizes deleting unknown directory data.
    foreach(string journalPath in Directory.EnumerateFiles(journals.DirectoryPath,"*.json")){
     try{using(var journal=new FileStream(OpenLocked(journalPath,false,true),FileAccess.Read)){
      var record=Deserialize<InstalledCleanupJournal>(Read(journal,MaximumRecordBytes));if(record==null||string.IsNullOrEmpty(record.directory))throw new InvalidDataException("清理记录缺少版本目录。");
      string folder=Full(Path.Combine(root,record.directory));Child(root,folder);if(!Names.Equals(JournalPath(root,folder),journalPath))throw new InvalidDataException("清理记录文件名与目标不符。");JournalManifest(journal,folder);
      if(!ConfirmedMissing(folder))continue;
      if(FolderVersion(folder)>currentVersion||(FolderVersion(folder)==currentVersion&&string.Compare(Path.GetFileName(folder),Path.GetFileName(currentFolder),StringComparison.OrdinalIgnoreCase)>=0))throw new InvalidDataException("清理记录指向当前或较新版本。");
      Gate(gate(folder),current,folder);MarkDelete(journal.SafeFileHandle);result.Deleted.Add(new InstalledCleanupItem(folder,"completed-cleanup-journal"));
     }}catch(Exception error){if(error is IOException||error is InvalidDataException||error is UnauthorizedAccessException||error is ArgumentException||error is System.ComponentModel.Win32Exception)result.Pending.Add(new InstalledCleanupItem(journalPath,error.Message));else throw;}
    }
   }
   return result;
  }
 }
}
