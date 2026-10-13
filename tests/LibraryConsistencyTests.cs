using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Mori;

namespace MoriDesk {
 public static class LibraryConsistencyTests {
  static Exception Failure(Action action){try{action();return null;}catch(Exception ex){return ex;}}
  static string Snapshot(Library library){return string.Join("|",library.Folders().OrderBy(f=>f.Id).Select(f=>f.Id+":"+f.Parent+":"+f.Name));}
  // A WAL reader sees the old target while the other connection owns the write
  // lock. Hold it until this connection has reached its BEGIN IMMEDIATE wait.
  // This reproduces a stale pre-transaction validation without timing a click.
  static bool AwaitWriteWait(Database database){
   var gate=typeof(Database).GetField("gate",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(database);
   var clock=Stopwatch.StartNew();long blockedSince=-1;
   while(clock.ElapsedMilliseconds<3000){
    if(Monitor.TryEnter(gate)){Monitor.Exit(gate);blockedSince=-1;}
    else {if(blockedSince<0)blockedSince=clock.ElapsedMilliseconds;if(clock.ElapsedMilliseconds-blockedSince>=100)return true;}
    Thread.Sleep(5);
   }
   return false;
  }
  static Exception DeleteWhileWaiting(Library library,string sql,Action action,Action<bool,string> check){
   Task<Exception> pending=null;bool waiting=false;
   using(var writer=new Database(Path.Combine(library.Root,".mori","library.db")))writer.Transaction(()=>{
    writer.Exec(sql);pending=Task.Run(()=>Failure(action));waiting=AwaitWriteWait(library.Db);
   });
   check(waiting,"concurrent fixture reaches an actual SQLite write-lock wait");
   check(pending.Wait(5000),"concurrent operation finishes after external write commits");
   return pending.Result;
  }
  public static void Run(string root,Action<bool,string> check){
   string path=Path.Combine(root,"consistency-library");
   using(var library=new Library(path)){
    var history=new UndoHistory(library.Db);
    string parent=library.CreateFolder("父分类",""),child=library.CreateFolder("子分类",parent),grandchild=library.CreateFolder("孙分类",child);
    history.Record("移动子分类",()=>library.MoveFolders(new[]{child},"",false));
    library.MoveFolders(new[]{parent},grandchild,false);library.RenameFolder(parent,"外部保留的名称");
    string snapshot=Snapshot(library);var error=Failure(()=>history.Undo());
    check(error is InvalidOperationException&&error.Message.Contains("循环分类"),"undo rejects a cycle caused by a newer external reparent");
    check(Snapshot(library)==snapshot&&history.Count==1,"rejected cycle undo is atomic and retains its history entry");
    library.MoveFolders(new[]{parent},"",false);
    check(history.Undo()=="移动子分类"&&library.Folders().Single(f=>f.Id==child).Parent==parent,"undo succeeds after conflicting hierarchy is resolved");
    check(library.Folders().Single(f=>f.Id==parent).Name=="外部保留的名称","hierarchy undo preserves unrelated external renaming");
    string renameParent=library.CreateFolder("将被删除的上级",""),renameChild=library.CreateFolder("原名称",renameParent);
    history.Record("分类改名",()=>library.RenameFolder(renameChild,"修改名称"));library.DeleteFolder(renameParent);
    check(history.Undo()=="分类改名"&&library.Folders().Single(f=>f.Id==renameChild).Name=="原名称","name-only undo is not blocked by deletion of an unrelated old parent");
    check(library.Folders().Single(f=>f.Id==renameChild).Parent=="","name-only undo preserves newer external category promotion");

    string old=library.CreateFolder("原分类",""),target=library.CreateFolder("当前分类","");
    var asset=library.Import(Path.Combine(root,"reference-photo.png"),old,"一致性图片","","","").Item;
    history.Record("移动图片",()=>library.MoveAssets(new[]{asset.Id},target,false));library.DeleteFolder(old);
    error=Failure(()=>history.Undo());
    check(error is InvalidOperationException&&error.Message.Contains("原分类已不存在"),"undo reports a missing original category before a raw FK error");
    check(library.AssetFolders(asset.Id).Single().Id==target&&history.Count==1,"missing-category undo preserves current membership and history");history.Clear();
    history.Record("批量删除分类",()=>library.DeleteFolders(new[]{parent,child},false));
    check(library.Folders().Single(f=>f.Id==grandchild).Parent=="","batch deletion promotes unchecked descendants to their nearest surviving parent");
    check(history.Undo()!=null&&library.Folders().Single(f=>f.Id==grandchild).Parent==child,"batch deletion undo restores a valid nested hierarchy");

    library.EnableReferenceAssets();history=new UndoHistory(library.Db,"board");
    string source=Path.Combine(root,"consistency-source.step");File.WriteAllText(source,"ISO-10303-21; synthetic shortcut; originals must remain");
    string shortcut=library.AddReferenceAssets(new[]{source},"").Single(),group=library.CreateLocalCollection("将被外部删除"),newGroup=library.CreateLocalCollection("仍存在");
    library.MoveToLocalCollection(new[]{shortcut},group);history.Record("移动快捷收藏",()=>library.MoveToLocalCollection(new[]{shortcut},newGroup));library.DeleteLocalCollection(group);
    error=Failure(()=>history.Undo());
    check(error is InvalidOperationException&&library.All().Single(a=>a.Id==shortcut).LocalCollection==newGroup,"undo cannot orphan a shortcut into a deleted local collection");history.Clear();
    string notesGroup=library.CreateLocalCollection("备注时所在的文件夹"),notesCategory=library.CreateFolder("备注时所在的分类","");
    library.MoveToLocalCollection(new[]{shortcut},notesGroup);library.AddToFolder(new[]{shortcut},notesCategory);
    var noteItem=library.All().Single(a=>a.Id==shortcut);string oldNote=noteItem.Notes;
    history.Record("修改备注",()=>{noteItem.Notes="已保存的备注修改";library.Update(noteItem);});
    library.DeleteLocalCollection(notesGroup);library.DeleteFolder(notesCategory);
    library.Db.Exec("UPDATE desk_refs SET source='https://example.org/newer-source' WHERE id="+Database.Q(shortcut.Substring(4)));
    check(history.Undo()=="修改备注"&&library.All().Single(a=>a.Id==shortcut).Notes==oldNote,"note-only undo is not blocked by deleted old local or sidebar categories");
    check(library.All().Single(a=>a.Id==shortcut).LocalCollection==""&&library.AssetFolders(shortcut).Count==0,"note-only undo preserves newer external unclassification");
    check(library.All().Single(a=>a.Id==shortcut).Source=="https://example.org/newer-source","note-only undo preserves unrelated external source edits");
    library.MoveToLocalCollection(new[]{shortcut},newGroup);

    string destination=library.CreateLocalCollection("并发移动目标");
    error=DeleteWhileWaiting(library,"DELETE FROM desk_local_collections WHERE id="+Database.Q(destination),()=>library.MoveToLocalCollection(new[]{shortcut},destination),check);
    check(error is InvalidOperationException&&error.Message.Contains("已不存在"),"local move revalidates its target after acquiring the write transaction");
    check(library.All().Single(a=>a.Id==shortcut).LocalCollection==newGroup,"concurrent target deletion never makes a shortcut disappear into an orphan group");

    string addTarget=library.CreateFolder("并发添加目标",""),extra=Path.Combine(root,"consistency-extra.step");File.WriteAllText(extra,"ISO-10303-21; second synthetic shortcut");
    error=DeleteWhileWaiting(library,"DELETE FROM folders WHERE id="+Database.Q(addTarget),()=>library.AddReferenceAssets(new[]{extra},addTarget),check);
    check(error is InvalidOperationException&&error.Message.Contains("已不存在"),"shortcut collection revalidates its category inside the write transaction");
    check(!library.All().Any(a=>a.ReferencePath==extra),"failed concurrent shortcut collection adds no partial record");

    string rename=library.CreateLocalCollection("并发重命名目标");
    error=DeleteWhileWaiting(library,"INSERT INTO desk_local_collections(id,name) VALUES('external-duplicate','相同名称')",()=>library.RenameLocalCollection(rename,"相同名称"),check);
    check(error is ArgumentException&&library.LocalCollections().Single(f=>f.Id==rename).Name=="并发重命名目标","rename rechecks concurrent duplicate names and preserves the old name");
    check(library.Db.Query("PRAGMA foreign_key_check").Count==0,"all consistency scenarios preserve database relationships");
    check(File.Exists(source)&&File.ReadAllText(source).Contains("originals must remain"),"classification and rejected undo never alter original files");
   }
  }
 }
}
