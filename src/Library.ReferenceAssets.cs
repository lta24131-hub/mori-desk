using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MoriDesk;

namespace Mori {
 public sealed partial class Library {
  public bool IncludeReferences {get;private set;}
  const string ReferencePrefix="ref-";
  internal void EnableReferenceAssets(){
   if(IncludeReferences)return;
   new BoardStore(this); // Preserve categories created by the previous version.
   var definitions=new Dictionary<string,string>{{"tags","TEXT DEFAULT ''"},{"notes","TEXT DEFAULT ''"},{"source","TEXT DEFAULT ''"},{"favorite","INTEGER DEFAULT 0"},{"added","TEXT DEFAULT ''"},{"created","TEXT DEFAULT ''"},{"bytes","INTEGER DEFAULT 0"},{"width","INTEGER DEFAULT 0"},{"height","INTEGER DEFAULT 0"},{"color","TEXT DEFAULT ''"},{"perceptual","TEXT DEFAULT ''"},{"collection","TEXT DEFAULT ''"},{"added_recovered","INTEGER DEFAULT 0"},{"hidden_all","INTEGER NOT NULL DEFAULT 0"},{"thumb_stamp","TEXT NOT NULL DEFAULT ''"}};
   Db.Transaction(()=>{var columns=new HashSet<string>(Db.Query("PRAGMA table_info(desk_refs)").Select(r=>r["name"]));foreach(var field in definitions)if(!columns.Contains(field.Key))Db.Exec("ALTER TABLE desk_refs ADD COLUMN "+field.Key+" "+field.Value);});
   Db.Exec("CREATE TABLE IF NOT EXISTS desk_local_collections(id TEXT PRIMARY KEY,name TEXT NOT NULL); CREATE INDEX IF NOT EXISTS desk_refs_collection ON desk_refs(collection)");
   // Legacy shortcuts had no registration time. Give them a stable migration
   // order, but do not present it as their original collection date.
   var legacy=Db.Query("SELECT id,path,is_dir FROM desk_refs WHERE added='' ORDER BY rowid");
   if(legacy.Count>0){var registered=DateTime.UtcNow;Db.Transaction(()=>{int i=0;foreach(var row in legacy){long bytes=0;string created="";try{if(row["is_dir"]!="1"){var info=new FileInfo(row["path"]);if(info.Exists){bytes=info.Length;created=FileCreated(row["path"]);}}}catch(IOException){}catch(UnauthorizedAccessException){}
    Db.Exec("UPDATE desk_refs SET added="+Q(registered.AddTicks(i++).ToString("o",CultureInfo.InvariantCulture))+",added_recovered=1,bytes="+bytes+",created="+Q(created)+" WHERE id="+Q(row["id"])+" AND added=''");}});}
   IncludeReferences=true;
  }
  internal bool ReferenceKey(string id){return IncludeReferences&&id!=null&&id.StartsWith(ReferencePrefix,StringComparison.Ordinal);}
  static string RawReference(string id){return id.Substring(ReferencePrefix.Length);}
  List<Asset> ReferenceAssets(){
   return Db.Query("SELECT * FROM desk_refs ORDER BY added DESC").Select(r=>{
    var file=new FileReference{Id=r["id"],Path=r["path"],Name=r["name"],Folder=r["folder"],Directory=r["is_dir"]=="1"};
    var item=new BoardItem{Reference=file,Path=file.Path};string section=item.Section=="websites"?"documents":item.Section;
    return new Asset{Id=ReferencePrefix+file.Id,Name=file.Name,Relative="",ReferencePath=file.Path,ReferenceSection=section,LocalCollection=r["collection"],AddedRecovered=r["added_recovered"]=="1",HiddenFromAll=r["hidden_all"]=="1",IsDirectory=file.Directory,Ext=Path.GetExtension(file.Path).ToLowerInvariant(),Tags=r["tags"],Notes=r["notes"],Source=r["source"],Favorite=r["favorite"]=="1",Added=r["added"],Created=r["created"],Bytes=long.Parse(r["bytes"]),Width=int.Parse(r["width"]),Height=int.Parse(r["height"]),Color=r["color"],Perceptual=r["perceptual"],ThumbnailSourceStamp=r["thumb_stamp"]};
   }).ToList();
  }
  // Only invoked for a requested thumbnail, never while listing/searching the
  // library. An unavailable disk keeps the last good preview intact. The path
  // is part of the fingerprint so re-linking an equal-size file invalidates it.
  static string ReferenceThumbnailStamp(Asset a,out long bytes){
   bytes=0;try{var file=new FileInfo(a.ReferencePath);if(!file.Exists)return null;bytes=file.Length;return file.FullName.ToUpperInvariant()+"|"+bytes.ToString(CultureInfo.InvariantCulture)+"|"+file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);}
   catch(IOException){return null;}catch(UnauthorizedAccessException){return null;}catch(ArgumentException){return null;}
  }
  internal string[] AddReferenceAssets(IEnumerable<string> paths,string folder){
   if(!IncludeReferences)throw new InvalidOperationException("快捷收藏未初始化。");
   folder=folder??"";
   var values=paths.Select(ReferenceStore.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
   foreach(var path in values)if(!File.Exists(path)&&!Directory.Exists(path))throw new FileNotFoundException("文件不存在或磁盘未连接："+path);
   var ids=new List<string>();
   Db.Transaction(()=>{if(folder!=""&&!Folders(false).Any(f=>f.Id==folder))throw new InvalidOperationException("目标素材分类已不存在，请刷新后重试；未添加任何收藏。");DateTime latest;string newest=Db.Query("SELECT MAX(added) AS newest FROM (SELECT added FROM assets UNION ALL SELECT added FROM desk_refs)")[0]["newest"];if(!DateTime.TryParse(newest,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out latest))latest=DateTime.MinValue;latest=latest.ToUniversalTime();foreach(var path in values){
    var existing=Db.Query("SELECT id FROM desk_refs WHERE path="+Q(path));
    if(existing.Count>0){string found=existing[0]["id"];if(folder!="")Db.Exec("UPDATE desk_refs SET folder="+Q(folder)+" WHERE id="+Q(found));ids.Add(ReferencePrefix+found);continue;}
    string id=Guid.NewGuid().ToString("N"),name=Path.GetFileName(path);bool directory=Directory.Exists(path);long bytes=directory?0:new FileInfo(path).Length;
    DateTime added=DateTime.UtcNow;if(added<=latest)added=latest.AddTicks(1);latest=added;
    Db.Exec("INSERT INTO desk_refs(id,path,name,folder,is_dir,bytes,added,created) VALUES("+Q(id)+","+Q(path)+","+Q(name.Length==0?path:name)+","+Q(folder)+","+(directory?1:0)+","+bytes+","+Q(added.ToString("o",CultureInfo.InvariantCulture))+","+Q(FileCreated(path))+")");ids.Add(ReferencePrefix+id);
   }});return ids.ToArray();
  }
  internal void RelinkReferenceAsset(string id,string path){
   if(!ReferenceKey(id))throw new InvalidOperationException("只有本地快捷收藏可以重新定位。");
   path=ReferenceStore.Normalize(path);string raw=RawReference(id);
   Db.Transaction(()=>{
    var rows=Db.Query("SELECT id,is_dir FROM desk_refs WHERE id="+Q(raw));if(rows.Count!=1)throw new InvalidOperationException("快捷收藏已变化，请刷新后重试。");
    bool directory=rows[0]["is_dir"]=="1";
    if(!MissingLocalFile.Exists(path,directory))throw new FileNotFoundException(directory?"请选择原文件夹的新位置，不能选择文件。":"请选择原文件的新位置，不能选择文件夹。",path);
    if(Db.Query("SELECT id FROM desk_refs WHERE path="+Q(path)+" AND id<>"+Q(raw)).Count>0)throw new InvalidOperationException("这个位置已经有快捷收藏。请选择原文件的新位置，不会合并或覆盖现有收藏。");
    long bytes=directory?0:new FileInfo(path).Length;
    Db.Exec("UPDATE desk_refs SET path="+Q(path)+",bytes="+bytes+",created="+Q(FileCreated(path))+" WHERE id="+Q(raw));
   });
  }
  bool AssignReference(string id,string folder){
   if(!ReferenceKey(id))return false;
   if(Db.Query("SELECT id FROM desk_refs WHERE id="+Q(RawReference(id))).Count!=1)throw new InvalidOperationException("快捷收藏已变化，请刷新后重试。");
   Db.Exec("UPDATE desk_refs SET folder="+Q(folder)+" WHERE id="+Q(RawReference(id)));return true;
  }
  void UnfileReferences(string folder){if(IncludeReferences)Db.Exec("UPDATE desk_refs SET folder='' WHERE folder="+Q(folder));}
 }
}
