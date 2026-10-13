using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mori;

namespace MoriDesk {
 public sealed class BoardItem {
  public Asset Asset {get;set;} public FileReference Reference {get;set;}
  public string Id {get{return Asset!=null?"a:"+Asset.Id:"r:"+Reference.Id;}}
  public string Name {get{return Asset!=null?Asset.Name:Reference.Name;}}
  public string Path {get;set;}
  public string Source {get{return Asset==null?"":Asset.Source;}}
  public bool Shortcut {get{return Reference!=null;}}
  public bool Directory {get{return Reference!=null&&Reference.Directory;}}
  public string Storage {get{return Shortcut?"快捷方式":"图库副本";}}
  public string Kind {get{return Directory?"文件夹":System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();}}
  public string Section {get{string ext=System.IO.Path.GetExtension(Path);return Directory?"folders":MediaTypes.Pictures.Contains(ext)?"pictures":MediaTypes.Videos.Contains(ext)?"videos":MediaTypes.Audio.Contains(ext)?"audio":FileRouter.Models.Contains(ext.ToLowerInvariant())?"models":ext.Equals(".url",StringComparison.OrdinalIgnoreCase)?"websites":"documents";}}
  public string Glyph {get{return Directory?"\uE8B7":Section=="pictures"?"\uEB9F":Section=="videos"?"\uE714":Section=="models"?"\uF158":Section=="audio"?"\uE8D6":Section=="websites"?"\uE774":"\uE8A5";}}
  public string[] Groups {get;set;}
 }
 public sealed class BoardBox {
  public string Id {get;set;} public string Name {get;set;} public List<BoardItem> Items {get;set;}
  public int Count {get{return Items.Count;}}
  public IEnumerable<BoardItem> Preview {get{return Items.Take(6);}}
 }
 public sealed class BoardStore {
  readonly Library library; readonly ReferenceStore refs; readonly UndoHistory history;
  public Library Library {get{return library;}}
  public BoardStore(Library library){
   this.library=library;refs=new ReferenceStore(library.Db);
   // Old reference categories become ordinary logical categories; no files move.
   library.Db.Exec("CREATE TABLE IF NOT EXISTS desk_board_state(name TEXT PRIMARY KEY,value TEXT)");
   library.Db.Transaction(()=>{if(library.Db.Query("SELECT name FROM desk_board_state WHERE name='categories-v1'").Count==0){foreach(var f in refs.Folders())if(!library.Folders().Any(x=>x.Id==f.Id))library.Db.Exec("INSERT INTO folders(id,name,parent,scope) VALUES("+Q(f.Id)+","+Q(f.Name)+",'','media')");library.Db.Exec("INSERT INTO desk_board_state VALUES('categories-v1','1')");}});
   history=new UndoHistory(library.Db,"board");
  }
  static string Q(string value){return Database.Q(value);}
  public List<BoardItem> All(){
   var memberships=library.Db.Query("SELECT asset,folder FROM memberships").GroupBy(r=>r["asset"]).ToDictionary(g=>g.Key,g=>g.Select(r=>r["folder"]).ToArray());
   var categories=new HashSet<string>(library.Folders().Select(f=>f.Id));
   var result=new List<BoardItem>();
   foreach(var a in library.All().Where(a=>!a.Deleted)){string[] groups;result.Add(new BoardItem{Asset=a,Path=library.FullPath(a),Groups=memberships.TryGetValue(a.Id,out groups)?groups:new string[0]});}
   foreach(var r in refs.All())result.Add(new BoardItem{Reference=r,Path=r.Path,Groups=categories.Contains(r.Folder)?new[]{r.Folder}:new string[0]});
   return result;
  }
  public static bool InSection(BoardItem item,string section){return section=="all"?item.Section!="documents"&&item.Section!="websites":section=="shortcuts"?item.Shortcut:section=="unfiled"?item.Section!="websites"&&item.Groups.Length==0:item.Section==section;}
  public List<Category> Categories(bool websites=false){return library.Folders(websites);}
  public List<BoardBox> Boxes(IEnumerable<BoardItem> rows,bool websites=false){var all=rows.ToList();return Categories(websites).Select(f=>new BoardBox{Id=f.Id,Name=CategoryName(f),Items=all.Where(i=>i.Groups.Contains(f.Id)).ToList()}).ToList();}
  string CategoryName(Category f){var parents=library.Folders();string text=f.Name;var visited=new HashSet<string>{f.Id};while(f.Parent!=""){f=parents.FirstOrDefault(p=>p.Id==f.Parent);if(f==null||!visited.Add(f.Id))break;text=f.Name+" / "+text;}return text;}
  void CheckCategory(string group){if(group!=""&&!Categories().Any(f=>f.Id==group))throw new InvalidOperationException("请选择素材分类框，网址分类不能接收本地文件。");}
  public int AddShortcuts(IEnumerable<string> paths,string group){
   CheckCategory(group);var values=paths.Select(ReferenceStore.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
   foreach(var p in values)if(!File.Exists(p)&&!Directory.Exists(p))throw new FileNotFoundException("文件不存在或磁盘未连接："+p);
   int count=0;history.Record("添加快捷收藏",()=>{foreach(var p in values){var existing=library.Db.Query("SELECT id FROM desk_refs WHERE path="+Q(p));if(existing.Count>0){if(group!="")library.Db.Exec("UPDATE desk_refs SET folder="+Q(group)+" WHERE id="+Q(existing[0]["id"]));continue;}library.Db.Exec("INSERT INTO desk_refs(id,path,name,folder,is_dir) VALUES("+Q(Guid.NewGuid().ToString("N"))+","+Q(p)+","+Q(System.IO.Path.GetFileName(p).Length==0?p:System.IO.Path.GetFileName(p))+","+Q(group)+","+(Directory.Exists(p)?1:0)+")");count++;}});return count;
  }
  public string CreateBox(string name){string id="";history.Record("新建分类框",()=>id=library.CreateFolder(name,""));return id;}
  public void RenameBox(string id,string name){CheckCategory(id);history.Record("重命名分类框",()=>library.RenameFolder(id,name));}
  public void DeleteBox(string id){DeleteBoxes(new[]{id});}
  public void DeleteBoxes(IEnumerable<string> ids){var groups=ids.Distinct().ToArray();foreach(var id in groups)CheckCategory(id);history.Record("删除分类框",()=>{foreach(var id in groups){library.Db.Exec("UPDATE desk_refs SET folder='' WHERE folder="+Q(id));library.DeleteFolder(id);}});}
  public void Move(IEnumerable<string> keys,string group){CheckCategory(group);var ids=new HashSet<string>(keys);var items=All().Where(i=>ids.Contains(i.Id)).ToList();if(items.Any(i=>i.Section=="websites"))throw new InvalidOperationException("网址请在网址页面内分类。");history.Record("移动到分类框",()=>{foreach(var item in items){if(item.Shortcut)library.Db.Exec("UPDATE desk_refs SET folder="+Q(group)+" WHERE id="+Q(item.Reference.Id));else{library.Db.Exec("DELETE FROM memberships WHERE asset="+Q(item.Asset.Id));if(group!="")library.AddToFolder(new[]{item.Asset.Id},group);}}});}
  public void Remove(IEnumerable<string> keys){var ids=new HashSet<string>(keys);var rows=All().Where(i=>ids.Contains(i.Id)).ToList();history.Record("移除所选素材",()=>{foreach(var r in rows){if(r.Shortcut)library.Db.Exec("DELETE FROM desk_refs WHERE id="+Q(r.Reference.Id));else library.Trash(new[]{r.Asset.Id},true);}});}
  public void Relink(BoardItem row,string path){if(!row.Shortcut)throw new InvalidOperationException("只有快捷方式需要重新定位。");path=ReferenceStore.Normalize(path);if(row.Directory?!Directory.Exists(path):!File.Exists(path))throw new FileNotFoundException("目标不存在或类型不一致。");history.Record("重新定位快捷方式",()=>library.Db.Exec("UPDATE desk_refs SET path="+Q(path)+" WHERE id="+Q(row.Reference.Id)));}
  public ImportResult CopyToLibrary(BoardItem row){if(!row.Shortcut||row.Directory||!Library.Extensions.Contains(System.IO.Path.GetExtension(row.Path)))throw new InvalidOperationException("此类型暂不支持保存图库副本。文件夹不会展开导入。");var result=library.ImportLocalFile(row.Path,row.Groups.FirstOrDefault()??"","reuse");library.Db.Exec("DELETE FROM desk_refs WHERE id="+Q(row.Reference.Id));history.Clear();return result;}
  public bool Undo(){return history.Undo()!=null;}
  public void UpdateInformation(BoardItem row,string name,string tags,string notes){name=(name??"").Trim();if(name.Length==0)throw new ArgumentException("名称不能为空。");history.Record("编辑素材信息",()=>{if(row.Shortcut)library.Db.Exec("UPDATE desk_refs SET name="+Q(name)+" WHERE id="+Q(row.Reference.Id));else{row.Asset.Name=name;row.Asset.Tags=tags;row.Asset.Notes=notes;library.Update(row.Asset);}});}
 }
}
