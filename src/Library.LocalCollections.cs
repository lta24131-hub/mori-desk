using System;
using System.Collections.Generic;
using System.Linq;

namespace Mori {
 public sealed class LocalCollection {public string Id{get;set;}public string Name{get;set;}}
 public sealed partial class Library {
  internal List<LocalCollection> LocalCollections(){return IncludeReferences?Db.Query("SELECT id,name FROM desk_local_collections").Select(r=>new LocalCollection{Id=r["id"],Name=r["name"]}).OrderBy(f=>f.Name,StringComparer.CurrentCultureIgnoreCase).ToList():new List<LocalCollection>();}
  void CheckLocalCollection(string id){if(!IncludeReferences)throw new InvalidOperationException("本地收藏尚未初始化。");if(id!=""&&!LocalCollections().Any(f=>f.Id==id))throw new InvalidOperationException("分类文件夹已不存在，请刷新。");}
  string CollectionName(string name,string except){name=(name??"").Trim();if(name.Length==0||name.Length>80)throw new ArgumentException("名称请填写 1–80 个字。");if(LocalCollections().Any(f=>f.Id!=except&&string.Equals(f.Name,name,StringComparison.OrdinalIgnoreCase)))throw new ArgumentException("已有同名分类文件夹。");return name;}
  internal string CreateLocalCollection(string name){string id=Guid.NewGuid().ToString("N");Db.Transaction(()=>{CheckLocalCollection("");name=CollectionName(name,"");Db.Exec("INSERT INTO desk_local_collections(id,name) VALUES("+Q(id)+","+Q(name)+")");});return id;}
  internal void RenameLocalCollection(string id,string name){if(string.IsNullOrEmpty(id))throw new ArgumentException("请选择分类文件夹。");Db.Transaction(()=>{CheckLocalCollection(id);Db.Exec("UPDATE desk_local_collections SET name="+Q(CollectionName(name,id))+" WHERE id="+Q(id));});}
  internal void MoveToLocalCollection(IEnumerable<string> ids,string target){
   if(ids==null)throw new ArgumentNullException("ids");target=target??"";var keys=ids.Distinct().ToArray();
   Db.Transaction(()=>{
    CheckLocalCollection(target);var known=new HashSet<string>(Db.Query("SELECT id FROM desk_refs").Select(r=>ReferencePrefix+r["id"]));
    if(keys.Any(id=>!ReferenceKey(id)||!known.Contains(id)))throw new InvalidOperationException("只能整理已有的本地快捷收藏，未更改任何内容。");
    foreach(string id in keys)Db.Exec("UPDATE desk_refs SET collection="+Q(target)+" WHERE id="+Q(RawReference(id)));
   });
  }
  internal void DeleteLocalCollection(string id){if(string.IsNullOrEmpty(id))throw new ArgumentException("请选择分类文件夹。");Db.Transaction(()=>{CheckLocalCollection(id);Db.Exec("UPDATE desk_refs SET collection='' WHERE collection="+Q(id));Db.Exec("DELETE FROM desk_local_collections WHERE id="+Q(id));});}
 }
}
