using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;
using MoriDeskInstaller;

namespace MoriDesk {
 internal sealed class UpdateArtifactResult {
  public int downloads,backups,retained;
  public long bytes;
  public bool retry;
 }
 // Only app-owned update storage is eligible. Never search Downloads, Temp,
 // media, portable installations, or arbitrary paths from JSON for victims.
 internal static class UpdateArtifactMaintenance {
  internal const string Receipt="download.json",Journal="metadata-cleanup.json";
  static readonly Regex DownloadName=new Regex(@"\Aupdate-[a-f0-9]{32}\z",RegexOptions.CultureInvariant);
  static readonly Regex BackupName=new Regex(@"\A(?:library\.db|settings\.json|capture-settings\.json|pins\.json|canvas-[a-f0-9]{32}-(?:project|mature-canvas)\.json)\z",RegexOptions.CultureInvariant);
  static readonly Regex Digest=new Regex(@"\A[a-fA-F0-9]{64}\z",RegexOptions.CultureInvariant);
  static readonly StringComparer Names=StringComparer.OrdinalIgnoreCase;
  static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=4*1024*1024,RecursionLimit=8};}
  internal static void RecordDownload(string folder,UpdateRelease release,DateTime utc){
   WriteNew(Path.Combine(folder,Receipt),Json().Serialize(new{schemaVersion=1,version=AppUpdateCoordinator.VersionText(release.Version),sha256=release.ExpectedSha256,bytes=release.Size,createdUtc=utc.ToString("O")}));
  }
  static void WriteNew(string path,string text){AppUpdateCoordinator.PlainPath(path);byte[] bytes=Encoding.UTF8.GetBytes(text);using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){stream.Write(bytes,0,bytes.Length);stream.Flush(true);}}
  static string Text(FileStream file,int maximum){if(file.Length<1||file.Length>maximum)throw new InvalidDataException("Invalid maintenance record size.");file.Position=0;using(var reader=new StreamReader(file,new UTF8Encoding(false,true),false,1024,true))return reader.ReadToEnd();}
  static Dictionary<string,object> Fields(string text,params string[] names){var fields=Json().Deserialize<Dictionary<string,object>>(text);if(fields==null||fields.Count!=names.Length||names.Any(n=>!fields.ContainsKey(n)))throw new InvalidDataException("Unexpected maintenance record.");return fields;}
  static long Number(object value){if(!(value is int||value is long))throw new InvalidDataException("Invalid count.");return Convert.ToInt64(value,CultureInfo.InvariantCulture);}
  static DateTime Time(object value){DateTime time;if(!(value is string)||!DateTime.TryParseExact((string)value,"O",CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out time)||time.Kind!=DateTimeKind.Utc)throw new InvalidDataException("Invalid time.");return time;}
  static Version VersionOf(object value){Version version;if(!(value is string)||!Version.TryParse((string)value,out version)||version.Build<0)throw new InvalidDataException("Invalid version.");return AppUpdateService.NumericVersion(version);}
  static bool Same(string a,string b){return Names.Equals(Path.GetFullPath(a).TrimEnd('\\'),Path.GetFullPath(b).TrimEnd('\\'));}
  static bool Overlaps(string a,string b){a=Path.GetFullPath(a).TrimEnd('\\');b=Path.GetFullPath(b).TrimEnd('\\');return Same(a,b)||a.StartsWith(b+"\\",StringComparison.OrdinalIgnoreCase)||b.StartsWith(a+"\\",StringComparison.OrdinalIgnoreCase);}
  static void SafeTarget(string folder,string[] protectedPaths){if(protectedPaths==null||protectedPaths.Length==0||protectedPaths.Any(p=>Overlaps(folder,p)))throw new InvalidDataException("Protected update storage.");}
  static string[] Children(string folder){var result=Directory.EnumerateDirectories(folder).Take(513).ToArray();if(result.Length>512)throw new InvalidDataException("Too many update directories.");return result;}
  // All files and the directory stay physically locked from validation through
  // deletion. Extra entries, junctions and in-use files veto the whole folder.
  sealed class Flat : IDisposable {
   internal readonly Dictionary<string,FileStream> Files=new Dictionary<string,FileStream>(Names);
   readonly SafeFileHandle directory;
   internal Flat(string folder,bool delete){try{
    directory=InstalledVersionCleanup.OpenLocked(folder,true,delete);
    foreach(string path in Directory.EnumerateFileSystemEntries(folder)){
     if(Files.Count>=20010||(File.GetAttributes(path)&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0)throw new InvalidDataException("Unknown update contents.");
     Files.Add(Path.GetFileName(path),new FileStream(InstalledVersionCleanup.OpenLocked(path,false,delete),FileAccess.Read));
    }
   }catch{Dispose();throw;}}
   internal string Read(string name,int maximum){return Text(Files[name],maximum);}
   internal void Delete(string last,Action guard){guard();foreach(var pair in Files.Where(p=>!Names.Equals(p.Key,last)))Remove(pair.Value);if(last!=null)Remove(Files[last]);InstalledVersionCleanup.MarkDelete(directory);}
   static void Remove(FileStream stream){InstalledVersionCleanup.MarkDelete(stream.SafeFileHandle);stream.Dispose();}
   public void Dispose(){foreach(var stream in Files.Values)stream.Dispose();if(directory!=null)directory.Dispose();}
  }
  sealed class Backup {
   internal string Session;
   internal DateTime Completed;
   internal bool Successful;
  }
  static DateTime ValidateBackup(Flat files,DateTime now){
   if(!files.Files.ContainsKey("complete.json")||!files.Files.ContainsKey("library.db")||files.Files.Keys.Any(n=>n!="complete.json"&&n!=AppUpdateMetadataBackup.InventoryName&&!BackupName.IsMatch(n)))throw new InvalidDataException("Unknown backup contents.");
   var record=Fields(files.Read("complete.json",4096),"schemaVersion","completedUtc","metadataOnly","sourceMediaIncluded","credentialsIncluded","bytes");
   DateTime completed=Time(record["completedUtc"]);long bytes=Number(record["bytes"]);
   if(Number(record["schemaVersion"])!=1||!Equals(record["metadataOnly"],true)||!Equals(record["sourceMediaIncluded"],false)||!Equals(record["credentialsIncluded"],false)||completed>now||bytes<0||bytes>AppUpdateMetadataBackup.MaximumBytes||files.Files.Any(p=>p.Value.Length>AppUpdateMetadataBackup.MaximumFileBytes)||files.Files.Where(p=>p.Key!="complete.json").Sum(p=>p.Value.Length)!=bytes)throw new InvalidDataException("Incomplete backup.");
   if(files.Files.ContainsKey(AppUpdateMetadataBackup.InventoryName)){
    var manifest=Fields(files.Read(AppUpdateMetadataBackup.InventoryName,4*1024*1024),"schemaVersion","files");var hashes=manifest["files"] as Dictionary<string,object>;
    if(Number(manifest["schemaVersion"])!=1||hashes==null||!new HashSet<string>(hashes.Keys,Names).SetEquals(files.Files.Keys.Where(n=>n!="complete.json"&&n!=AppUpdateMetadataBackup.InventoryName))||hashes.Any(p=>!(p.Value is string)||!Digest.IsMatch((string)p.Value)||!Names.Equals((string)p.Value,InstalledVersionCleanup.Hash(files.Files[p.Key]))))throw new InvalidDataException("Backup integrity mismatch.");
   }
   return completed;
  }
  static bool Successful(string session,Version current){
   string result=Path.Combine(session,"result.json");if(InstalledVersionCleanup.ConfirmedMissing(result))return false;
   var fields=Fields(InstalledCleanupState.ReadPhysicalText(result,16384),"schemaVersion","status","expectedVersion","message");
   return Number(fields["schemaVersion"])==1&&Equals(fields["status"],"installed")&&VersionOf(fields["expectedVersion"])<=current;
  }
  static Backup InspectBackup(string session,Version current,DateTime now){
   string folder=Path.Combine(session,"metadata-backup");if(InstalledVersionCleanup.ConfirmedMissing(folder))return null;
   using(var parent=InstalledVersionCleanup.OpenLocked(session,true,false))using(var files=new Flat(folder,false))return new Backup{Session=session,Completed=ValidateBackup(files,now),Successful=Successful(session,current)};
  }
  static Dictionary<string,string> Inventory(Flat files){return files.Files.ToDictionary(p=>p.Key,p=>InstalledVersionCleanup.Hash(p.Value),Names);}
  static Dictionary<string,string> ReadJournal(string text,DateTime keptTime){
   var fields=Fields(text,"schemaVersion","completedUtc","files");if(Number(fields["schemaVersion"])!=1||Time(fields["completedUtc"])>=keptTime)throw new InvalidDataException("Invalid backup journal.");
   var values=fields["files"] as Dictionary<string,object>;if(values==null||values.Count<2||values.Count>20010||!values.ContainsKey("library.db")||!values.ContainsKey("complete.json"))throw new InvalidDataException("Invalid backup inventory.");
   var result=new Dictionary<string,string>(Names);foreach(var pair in values){if(pair.Key!="complete.json"&&pair.Key!=AppUpdateMetadataBackup.InventoryName&&!BackupName.IsMatch(pair.Key)||!(pair.Value is string)||!Digest.IsMatch((string)pair.Value))throw new InvalidDataException("Invalid backup entry.");result.Add(pair.Key,(string)pair.Value);}return result;
  }
  static void RemoveBackup(string session,string keeper,Version current,DateTime now,Action guard,UpdateArtifactResult result){
   string folder=Path.Combine(session,"metadata-backup"),journal=Path.Combine(session,Journal);
   using(var parent=InstalledVersionCleanup.OpenLocked(session,true,false))
   using(var keptParent=InstalledVersionCleanup.OpenLocked(keeper,true,false))
   using(var kept=new Flat(Path.Combine(keeper,"metadata-backup"),false)){
    DateTime keptTime=ValidateBackup(kept,now);
    if(!Successful(keeper,current)||!Successful(session,current))throw new InvalidDataException("Backup session not committed.");
    using(var files=new Flat(folder,true)){
     if(InstalledVersionCleanup.ConfirmedMissing(journal)){
      if(ValidateBackup(files,now)>=keptTime)throw new InvalidDataException("Keep newest complete backup.");
      WriteNew(journal,Json().Serialize(new{schemaVersion=1,completedUtc=ValidateBackup(files,now).ToString("O"),files=Inventory(files)}));
     }
     using(var inventory=new FileStream(InstalledVersionCleanup.OpenLocked(journal,false,true),FileAccess.Read)){
      var hashes=ReadJournal(Text(inventory,4*1024*1024),keptTime);
      if(files.Files.Any(p=>!hashes.ContainsKey(p.Key)||!Names.Equals(hashes[p.Key],InstalledVersionCleanup.Hash(p.Value))))throw new InvalidDataException("Backup changed since cleanup began.");
      long bytes=files.Files.Sum(p=>p.Value.Length);
      // Keep complete.json until all payload files have been removed. Journal
      // remains outside the folder so interrupted deletion can resume safely.
      files.Delete(files.Files.ContainsKey("complete.json")?"complete.json":null,guard);
      result.bytes+=bytes;result.backups++;InstalledVersionCleanup.MarkDelete(inventory.SafeFileHandle);
     }
    }
   }
  }
  static void RemoveDownload(string folder,Version current,DateTime now,Action guard,UpdateArtifactResult result){
   Version legacyVersion=null;string legacyHash=null;
   if(InstalledVersionCleanup.ConfirmedMissing(Path.Combine(folder,Receipt))&&!InstalledVersionCleanup.ConfirmedMissing(Path.Combine(folder,AppUpdateService.InstallerName))){
    using(var legacy=new FileStream(InstalledVersionCleanup.OpenLocked(Path.Combine(folder,AppUpdateService.InstallerName),false,false),FileAccess.Read)){
     var identity=FileVersionInfo.GetVersionInfo(Path.Combine(folder,AppUpdateService.InstallerName));legacyVersion=VersionOf(identity.FileVersion);
     if(!AppUpdateCoordinator.InstallerIdentity(identity.ProductName,identity.OriginalFilename,identity.FileVersion,legacyVersion))throw new InvalidDataException("Not a Nook installer.");
     legacyHash=InstalledVersionCleanup.Hash(legacy);
    }
   }
   using(var files=new Flat(folder,true)){
    bool receipt=files.Files.ContainsKey(Receipt);Version version;DateTime created=DateTime.MinValue;bool expired=false;
    if(files.Files.Count==0){files.Delete(null,guard);return;}
    if(files.Files.Keys.Any(n=>n!=Receipt&&n!=AppUpdateService.InstallerName&&n!=AppUpdateService.InstallerName+".partial"))throw new InvalidDataException("Unknown download contents.");
    if(receipt){
     var fields=Fields(files.Read(Receipt,4096),"schemaVersion","version","sha256","bytes","createdUtc");version=VersionOf(fields["version"]);created=Time(fields["createdUtc"]);long expected=Number(fields["bytes"]);
     if(Number(fields["schemaVersion"])!=1||!(fields["sha256"] is string)||!Digest.IsMatch((string)fields["sha256"])||expected<=0||expected>AppUpdateService.MaxInstallerBytes||created>now)throw new InvalidDataException("Invalid download receipt.");
     expired=now-created>=TimeSpan.FromDays(7);
     FileStream installer;if(files.Files.TryGetValue(AppUpdateService.InstallerName,out installer)&&(installer.Length!=expected||!Names.Equals(InstalledVersionCleanup.Hash(installer),(string)fields["sha256"])))throw new InvalidDataException("Downloaded installer changed.");
     if(files.Files.ContainsKey(AppUpdateService.InstallerName+".partial")&&(!expired||files.Files[AppUpdateService.InstallerName+".partial"].Length>expected))return;
    }else{
     // Upgrade compatibility: historical downloads have no receipt. Require
     // the generated cache name, exact contents and Nook installer identity.
     if(files.Files.Count!=1||!files.Files.ContainsKey(AppUpdateService.InstallerName))throw new InvalidDataException("Unowned download.");
     version=legacyVersion;if(version==null||legacyHash!=InstalledVersionCleanup.Hash(files.Files[AppUpdateService.InstallerName]))throw new InvalidDataException("Not an unchanged Nook installer.");
    }
    if(version>current&&!expired)return;
    long bytes=files.Files.Sum(p=>p.Value.Length);files.Delete(receipt?Receipt:null,guard);result.bytes+=bytes;result.downloads++;
   }
  }
  internal static UpdateArtifactResult Clean(string updatesRoot,Version current,DateTime now,string[] protectedPaths,Action guard){
   if(guard==null||current==null||now.Kind!=DateTimeKind.Utc)throw new ArgumentException("Missing cleanup evidence.");
   guard();current=AppUpdateService.NumericVersion(current);updatesRoot=AppUpdateMetadataBackup.PlainPath(updatesRoot);var result=new UpdateArtifactResult();
   if(InstalledVersionCleanup.ConfirmedMissing(updatesRoot))return result;
   using(var rootLock=InstalledVersionCleanup.OpenLocked(updatesRoot,true,false)){
    string[] sessions=Children(updatesRoot).Where(p=>{Guid id;return Guid.TryParseExact(Path.GetFileName(p),"N",out id);}).ToArray();
    var backups=new List<Backup>();
    foreach(string session in sessions)try{SafeTarget(Path.Combine(session,"metadata-backup"),protectedPaths);var backup=InspectBackup(session,current,now);if(backup!=null)backups.Add(backup);}catch{result.retained++;}
    var keeper=backups.Where(b=>b.Successful).OrderByDescending(b=>b.Completed).ThenBy(b=>b.Session,StringComparer.Ordinal).FirstOrDefault();
    if(keeper!=null)foreach(string session in sessions){
     if(Same(session,keeper.Session))continue;
     try{
      string folder=Path.Combine(session,"metadata-backup");SafeTarget(folder,protectedPaths);
      if(InstalledVersionCleanup.ConfirmedMissing(folder))continue;
      var candidate=backups.FirstOrDefault(b=>Same(b.Session,session));
      if(candidate!=null&&(!candidate.Successful||candidate.Completed>=keeper.Completed))continue;
      if(candidate==null&&InstalledVersionCleanup.ConfirmedMissing(Path.Combine(session,Journal)))continue;
      RemoveBackup(session,keeper.Session,current,now,guard,result);
     }catch(Exception error){Retained(result,error);}
    }
    string downloads=Path.Combine(updatesRoot,"downloads");
    if(!InstalledVersionCleanup.ConfirmedMissing(downloads))using(var downloadLock=InstalledVersionCleanup.OpenLocked(downloads,true,false)){
     foreach(string folder in Children(downloads).Where(p=>DownloadName.IsMatch(Path.GetFileName(p))))try{SafeTarget(folder,protectedPaths);RemoveDownload(folder,current,now,guard,result);}catch(Exception error){Retained(result,error);}
    }
   }
   return result;
  }
  static void Retained(UpdateArtifactResult result,Exception error){result.retained++;if(InstalledVersionCleanup.RetryFor(error)==InstalledCleanupRetry.FileInUse)result.retry=true;}
  internal static void SaveReport(string dataRoot,UpdateArtifactResult result){
   string folder=Path.Combine(dataRoot,"maintenance");AppUpdateCoordinator.PlainPath(folder);Directory.CreateDirectory(folder);
   string target=Path.Combine(folder,"artifact-cleanup.json"),temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";
   try{WriteNew(temporary,Json().Serialize(new{schemaVersion=1,utc=DateTime.UtcNow.ToString("O"),downloads=result.downloads,backups=result.backups,bytes=result.bytes,retained=result.retained}));AppUpdateCoordinator.PlainPath(target);if(File.Exists(target))File.Replace(temporary,target,null);else File.Move(temporary,target);}
   finally{if(File.Exists(temporary)){AppUpdateCoordinator.PlainPath(temporary);File.Delete(temporary);}}
  }
  internal static string ReadSummary(string dataRoot){
   try{var r=Fields(InstalledCleanupState.ReadPhysicalText(Path.Combine(dataRoot,"maintenance","artifact-cleanup.json"),4096),"schemaVersion","utc","downloads","backups","bytes","retained");if(Number(r["schemaVersion"])!=1||Number(r["bytes"])<0||Number(r["downloads"])<0||Number(r["backups"])<0||Number(r["retained"])<0)return "";
    return "\n更新缓存：上次清理 "+Number(r["downloads"])+" 个安装包、"+Number(r["backups"])+" 份过期备份，释放 "+(Number(r["bytes"])/1024d/1024).ToString("0.0",CultureInfo.InvariantCulture)+" MB。最近一次有效备份保留。"+(Number(r["retained"])>0?" 部分文件仍在使用或无法核验，暂时保留。":"");
   }catch{return "";}
  }
 }
}
