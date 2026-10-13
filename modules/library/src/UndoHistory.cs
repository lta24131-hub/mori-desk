using System;
using System.Collections.Generic;
using System.Linq;

namespace Mori {
 // Only metadata deltas are retained, never image bytes or whole database copies.
 // Entries are session-local. Undo is transactional and checks expected state
 // before writing so it cannot silently overwrite a newer external edit.
 internal sealed class UndoHistory {
  internal const int MaximumSteps=30,MaximumCharacters=2*1024*1024;
  readonly Database db;readonly List<Entry> entries=new List<Entry>();int characters;
  readonly string[] Tables={"assets","folders","memberships"};
  readonly string[] Queries={"SELECT id,name,tags,notes,source,favorite,deleted,hidden_all FROM assets","SELECT id,name,parent,scope FROM folders","SELECT asset,folder FROM memberships"};
  readonly bool board;
  internal int Count {get{return entries.Count;}}
  internal string Label {get{return Count==0?"":entries[Count-1].Label;}}
  sealed class Change {public string Table,Key;public Dictionary<string,string> Before,After;public string[] Columns;}
  sealed class Entry {public string Label;public List<Change> Changes=new List<Change>();public int Size;}
  public UndoHistory(Database database,bool references=false){db=database;if(references){Tables=new[]{"desk_ref_folders","desk_refs"};Queries=new[]{"SELECT id,name FROM desk_ref_folders","SELECT id,path,name,folder,is_dir FROM desk_refs"};}}
  public UndoHistory(Database database,string mode):this(database){if(mode!="board")throw new ArgumentException("Unknown history mode");board=true;Tables=new[]{"assets","folders","memberships","desk_refs"};var columns=new HashSet<string>(db.Query("PRAGMA table_info(desk_refs)").Select(r=>r["name"]));string metadata=string.Join("",new[]{"tags","notes","source","favorite","added","created","bytes","collection","added_recovered","hidden_all"}.Where(columns.Contains).Select(c=>","+c));Queries=new[]{"SELECT id,name,tags,notes,source,favorite,deleted,hidden_all FROM assets","SELECT id,name,parent,scope FROM folders","SELECT asset,folder FROM memberships","SELECT id,path,name,folder,is_dir"+metadata+" FROM desk_refs"};if(db.Query("SELECT name FROM sqlite_master WHERE type='table' AND name='desk_local_collections'").Count>0){Tables=Tables.Concat(new[]{"desk_local_collections"}).ToArray();Queries=Queries.Concat(new[]{"SELECT id,name FROM desk_local_collections"}).ToArray();}}
  public void Clear(){entries.Clear();characters=0;}
  static string Key(string table,Dictionary<string,string> row){return table=="memberships"?row["asset"]+":"+row["folder"]:row["id"];}
  static string Where(string table,Dictionary<string,string> row){return table=="memberships"?"asset="+Database.Q(row["asset"])+" AND folder="+Database.Q(row["folder"]):"id="+Database.Q(row["id"]);}
  static bool RestoresColumn(Change change,string column){return change.Before!=null&&(change.After==null||change.Columns.Contains(column));}
  Dictionary<string,Dictionary<string,Dictionary<string,string>>> Capture(){var result=new Dictionary<string,Dictionary<string,Dictionary<string,string>>>();for(int i=0;i<Tables.Length;i++)result[Tables[i]]=db.Query(Queries[i]).ToDictionary(r=>Key(Tables[i],r));return result;}
  public bool Record(string label,Action action){
   var entry=new Entry{Label=label};
   db.Transaction(delegate{
    var before=Capture();action();var after=Capture();
    foreach(string table in Tables)foreach(string key in before[table].Keys.Union(after[table].Keys)){
     Dictionary<string,string> oldRow,newRow;before[table].TryGetValue(key,out oldRow);after[table].TryGetValue(key,out newRow);
     var columns=oldRow==null||newRow==null?(oldRow??newRow).Keys.ToArray():oldRow.Keys.Where(c=>oldRow[c]!=newRow[c]).ToArray();
     if(columns.Length==0)continue;
     // Import and permanent delete have different safety rules and must not use this editor journal.
     if(table=="assets"&&(oldRow==null||newRow==null))throw new InvalidOperationException("此操作不能加入信息撤销记录。");
     entry.Changes.Add(new Change{Table=table,Key=key,Before=oldRow,After=newRow,Columns=columns});
     entry.Size+=(oldRow==null?0:oldRow.Sum(p=>p.Key.Length+p.Value.Length))+(newRow==null?0:newRow.Sum(p=>p.Key.Length+p.Value.Length))+128;
    }
   });
   if(entry.Changes.Count==0)return false;
   if(entry.Size>MaximumCharacters){Clear();return false;}
   entries.Add(entry);characters+=entry.Size;
   while(entries.Count>MaximumSteps||characters>MaximumCharacters){characters-=entries[0].Size;entries.RemoveAt(0);}return true;
  }
  public string Undo(){
   if(Count==0)return null;var entry=entries[Count-1];
   db.Transaction(delegate{
    var current=Capture();
    foreach(var c in entry.Changes){
     Dictionary<string,string> row;current[c.Table].TryGetValue(c.Key,out row);
     if(c.After==null?row!=null:row==null||c.Columns.Any(k=>row[k]!=c.After[k]))throw new InvalidOperationException("相关内容已被其他操作修改，未撤销，以免覆盖新内容。");
     if(c.Table=="desk_ref_folders"&&c.Before==null&&current["desk_refs"].Values.Any(r=>r["folder"]==c.After["id"]&&!entry.Changes.Any(x=>x.Table=="desk_refs"&&x.Key==r["id"])))throw new InvalidOperationException("分类中已有新的收藏，不能撤销分类创建。");
     if(c.Table=="desk_local_collections"&&c.Before==null&&current["desk_refs"].Values.Any(r=>r["collection"]==c.After["id"]&&!entry.Changes.Any(x=>x.Table=="desk_refs"&&x.Key==r["id"])))throw new InvalidOperationException("分类文件夹中已有新的收藏，未撤销。");
     if(c.Table=="desk_refs"&&c.Before!=null&&c.Before.ContainsKey("collection")&&RestoresColumn(c,"collection")&&c.Before["collection"]!=""&&!current["desk_local_collections"].ContainsKey(c.Before["collection"])&&!entry.Changes.Any(x=>x.Table=="desk_local_collections"&&x.Key==c.Before["collection"]&&x.Before!=null))throw new InvalidOperationException("原分类文件夹已不存在，未撤销。");
     if(c.Table=="desk_refs"&&RestoresColumn(c,"folder")&&c.Before["folder"]!=""&&!current[board?"folders":"desk_ref_folders"].ContainsKey(c.Before["folder"])&&!entry.Changes.Any(x=>x.Table==(board?"folders":"desk_ref_folders")&&x.Key==c.Before["folder"]&&x.Before!=null))throw new InvalidOperationException("原分类已不存在，未撤销。");
     if(c.Table=="folders"&&RestoresColumn(c,"parent")&&c.Before["parent"]!=""&&!current["folders"].ContainsKey(c.Before["parent"])&&!entry.Changes.Any(x=>x.Table=="folders"&&x.Key==c.Before["parent"]&&x.Before!=null))throw new InvalidOperationException("原上级分类已不存在，未撤销，以免产生失效分类。");
     if(c.Table=="folders"&&c.Before==null){
      string id=c.After["id"];
      if(board&&current["desk_refs"].Values.Any(r=>r["folder"]==id&&!entry.Changes.Any(x=>x.Table=="desk_refs"&&x.Key==r["id"])))throw new InvalidOperationException("分类中已有新的快捷收藏，未撤销。");
      if(current["memberships"].Values.Any(r=>r["folder"]==id&&!entry.Changes.Any(x=>x.Table=="memberships"&&x.Key==Key("memberships",r)))||current["folders"].Values.Any(r=>r["parent"]==id&&!entry.Changes.Any(x=>x.Table=="folders"&&x.Key==r["id"])))throw new InvalidOperationException("分类中已有新的内容，未撤销，以免影响这些内容。");
     }
    }
    ValidateRestoredRelationships(entry,current);
    // Restore parent records before relationships. Remove new relationships before categories.
    foreach(var c in entry.Changes.Where(c=>c.After==null&&c.Table!="memberships"))Insert(c.Table,c.Before);
    foreach(var c in entry.Changes.Where(c=>c.Before!=null&&c.After!=null))db.Exec("UPDATE "+c.Table+" SET "+string.Join(",",c.Columns.Select(k=>k+"="+Database.Q(c.Before[k])))+" WHERE "+Where(c.Table,c.Before));
    foreach(var c in entry.Changes.Where(c=>c.Before==null).OrderBy(c=>c.Table=="memberships"?0:1))db.Exec("DELETE FROM "+c.Table+" WHERE "+Where(c.Table,c.After));
    foreach(var c in entry.Changes.Where(c=>c.After==null&&c.Table=="memberships"))Insert(c.Table,c.Before);
   });
   entries.RemoveAt(entries.Count-1);characters-=entry.Size;return entry.Label;
  }
  // The old parent can still exist while having moved under the category being
  // restored. Check the prospective graph, not merely parent existence, so an
  // external edit cannot turn a safe move undo into an inaccessible cycle.
  void ValidateRestoredRelationships(Entry entry,Dictionary<string,Dictionary<string,Dictionary<string,string>>> current){
   if(!current.ContainsKey("folders")||!entry.Changes.Any(c=>c.Before!=null&&(c.Table=="memberships"||(c.Table=="folders"&&(c.After==null||c.Columns.Contains("parent"))))))return;
   var folders=current["folders"].ToDictionary(p=>p.Key,p=>new Dictionary<string,string>(p.Value));
   foreach(var c in entry.Changes.Where(c=>c.Table=="folders")){
    if(c.Before==null){folders.Remove(c.Key);continue;}
    if(c.After==null)folders[c.Key]=new Dictionary<string,string>(c.Before);
    else foreach(string column in c.Columns)folders[c.Key][column]=c.Before[column];
   }
   foreach(var c in entry.Changes.Where(c=>c.Table=="folders"&&c.Before!=null&&(c.After==null||c.Columns.Contains("parent")))){
    string id=c.Key;var seen=new HashSet<string>();
    while(id!=""){
     Dictionary<string,string> folder;
     if(!folders.TryGetValue(id,out folder))throw new InvalidOperationException("原上级分类已不存在，未撤销，以免产生失效分类。");
     if(!seen.Add(id))throw new InvalidOperationException("分类层级已被其他操作改变，未撤销，以免形成循环分类。请先调整分类位置。");
     id=folder["parent"];
    }
   }
   foreach(var c in entry.Changes.Where(c=>c.Table=="memberships"&&c.Before!=null)){
    if(!folders.ContainsKey(c.Before["folder"]))throw new InvalidOperationException("原分类已不存在，未撤销。素材仍保留在当前分类中。");
    if(!current["assets"].ContainsKey(c.Before["asset"]))throw new InvalidOperationException("原素材已不存在，未撤销，以免恢复失效的分类记录。");
   }
  }
  void Insert(string table,Dictionary<string,string> row){db.Exec("INSERT INTO "+table+" ("+string.Join(",",row.Keys)+") VALUES ("+string.Join(",",row.Values.Select(Database.Q))+")");}
 }
}
