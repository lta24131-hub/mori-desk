using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mori;
namespace MoriDesk {
 public sealed class FileReference : System.ComponentModel.INotifyPropertyChanged {
  public string Id {get;set;} public string Name {get;set;} public string Path {get;set;} public string Folder {get;set;} public bool Directory {get;set;}
  public string Kind {get{return Directory?"文件夹":System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();}}
  // Checked only when opening/refreshing this page; never crawl or hash originals.
  public bool Exists {get{return Directory?System.IO.Directory.Exists(Path):File.Exists(Path);}}
  string state; public string State {get{return state;}set{if(state==value)return;state=value;var handler=PropertyChanged;if(handler!=null)handler(this,new System.ComponentModel.PropertyChangedEventArgs("State"));}}
  public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
 }
 public sealed class ReferenceFolder {public string Id {get;set;} public string Name {get;set;} public override string ToString(){return Name;}}
 public sealed class ReferenceStore {
  readonly Database db; readonly UndoHistory history;
  public ReferenceStore(Database database){db=database;db.Exec("CREATE TABLE IF NOT EXISTS desk_ref_folders(id TEXT PRIMARY KEY,name TEXT NOT NULL UNIQUE COLLATE NOCASE);CREATE TABLE IF NOT EXISTS desk_refs(id TEXT PRIMARY KEY,path TEXT NOT NULL UNIQUE COLLATE NOCASE,name TEXT NOT NULL,folder TEXT NOT NULL DEFAULT '',is_dir INTEGER NOT NULL);");history=new UndoHistory(db,true);}
  static string Q(string s){return Database.Q(s);}
  public List<FileReference> All(){return db.Query("SELECT * FROM desk_refs ORDER BY name COLLATE NOCASE").Select(r=>new FileReference{Id=r["id"],Name=r["name"],Path=r["path"],Folder=r["folder"],Directory=r["is_dir"]=="1"}).ToList();}
  public List<ReferenceFolder> Folders(){return db.Query("SELECT * FROM desk_ref_folders ORDER BY name COLLATE NOCASE").Select(r=>new ReferenceFolder{Id=r["id"],Name=r["name"]}).ToList();}
  public static string Normalize(string path){if(string.IsNullOrWhiteSpace(path)||path.IndexOf('\0')>=0||!System.IO.Path.IsPathRooted(path))throw new ArgumentException("请选择完整的本地文件路径。");var full=System.IO.Path.GetFullPath(path);return full.Length>System.IO.Path.GetPathRoot(full).Length?full.TrimEnd('\\','/'):full;}
  void Edit(Action action){history.Record("本地文件收藏",action);}
  void CheckFolder(string folder){if(folder!=""&&!Folders().Any(f=>f.Id==folder))throw new InvalidOperationException("分类已不存在，请重新选择。");}
  public int Add(IEnumerable<string> paths,string folder){CheckFolder(folder);var normalized=paths.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();foreach(var p in normalized)if(!File.Exists(p)&&!System.IO.Directory.Exists(p))throw new FileNotFoundException("文件已不存在或暂时无法访问："+p);int count=0;Edit(()=>{foreach(string p in normalized){if(db.Query("SELECT id FROM desk_refs WHERE path="+Q(p)).Count>0)continue;string name=System.IO.Path.GetFileName(p);if(name.Length==0)name=p;db.Exec("INSERT INTO desk_refs VALUES("+Q(Guid.NewGuid().ToString("N"))+","+Q(p)+","+Q(name)+","+Q(folder)+","+(System.IO.Directory.Exists(p)?1:0)+")");count++;}});return count;}
  public string AddFolder(string name){name=(name??"").Trim();if(name.Length==0||name.Length>100)throw new ArgumentException("分类名应为 1–100 个字符。");string id=Guid.NewGuid().ToString("N");Edit(()=>db.Exec("INSERT INTO desk_ref_folders VALUES("+Q(id)+","+Q(name)+")"));return id;}
  public void RenameFolder(string id,string name){name=(name??"").Trim();if(name.Length==0||name.Length>100)throw new ArgumentException("请输入分类名称。");CheckFolder(id);Edit(()=>db.Exec("UPDATE desk_ref_folders SET name="+Q(name)+" WHERE id="+Q(id)));}
  public void DeleteFolder(string id){CheckFolder(id);Edit(()=>db.Exec("UPDATE desk_refs SET folder='' WHERE folder="+Q(id)+";DELETE FROM desk_ref_folders WHERE id="+Q(id)));}
  public void Move(IEnumerable<string> ids,string folder){CheckFolder(folder);var selected=ids.ToArray();Edit(()=>{foreach(var id in selected)db.Exec("UPDATE desk_refs SET folder="+Q(folder)+" WHERE id="+Q(id));});}
  public void Remove(IEnumerable<string> ids){var selected=ids.ToArray();Edit(()=>{foreach(var id in selected)db.Exec("DELETE FROM desk_refs WHERE id="+Q(id));});}
  public void Relink(string id,string path){var before=All().FirstOrDefault(r=>r.Id==id);if(before==null)throw new InvalidOperationException("记录不存在。");path=Normalize(path);if(before.Directory?!System.IO.Directory.Exists(path):!File.Exists(path))throw new FileNotFoundException("目标不存在或类型不一致。");Edit(()=>db.Exec("UPDATE desk_refs SET path="+Q(path)+" WHERE id="+Q(id)));}
  public bool Undo(){return history.Undo()!=null;}
 }
}
