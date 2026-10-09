using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Controls;
using System.Reflection;

namespace Mori {
 public static class UndoHistoryTests {
  static bool Reject(Action action){try{action();return false;}catch{return true;}}
  public static void Run(string output,Action<bool,string> check){
   Directory.CreateDirectory(output);string file=Path.Combine(output,"undo.png");using(var b=new Bitmap(16,16)){b.Save(file,ImageFormat.Png);}var original=File.ReadAllBytes(file);
   using(var lib=new Library(Path.Combine(output,"library"))){
    var h=new UndoHistory(lib.Db);string parent=lib.CreateFolder("父分类",""),folder=lib.CreateFolder("删除后未分类",parent),child=lib.CreateFolder("保留的子分类",folder),other=lib.CreateFolder("另一个分类","");
    var one=lib.Import(file,folder,"图片","","标签","备注").Item;var web=lib.ImportBookmark("https://example.org/","网址",folder).Item;var shared=lib.Import(file,folder,"共享","","","",null,"keep").Item;lib.AddToFolder(new[]{shared.Id},other);
    check(h.Record("删除分类",()=>lib.DeleteFolder(folder))&&h.Count==1,"category delete becomes a single reversible action");
    check(lib.All().Count==3&&lib.All().All(a=>!a.Deleted)&&!lib.Classified().Contains(one.Id)&&lib.AssetFolders(web.Id).Single().Scope=="bookmark","deleting media category moves images to unfiled and preserves independent bookmark folder");
    check(lib.AssetFolders(shared.Id).Single().Id==other&&lib.Folders().Single(f=>f.Id==child).Parent==parent,"delete keeps other memberships and promotes child categories");
    check(h.Undo()=="删除分类"&&lib.Folders().Single(f=>f.Id==folder).Parent==parent&&lib.Folders().Single(f=>f.Id==child).Parent==folder&&lib.AssetFolders(one.Id).Single().Id==folder&&lib.AssetFolders(web.Id).Single().Name=="删除后未分类","Ctrl+Z restores media category identity, hierarchy and memberships without changing bookmark scope");
    check(File.ReadAllBytes(lib.FullPath(one)).SequenceEqual(original),"undo never rewrites or removes original media");
    h.Record("重命名",()=>lib.RenameFolder(folder,"新名称"));h.Record("分类",()=>lib.AddToFolder(new[]{one.Id},other));h.Record("回收站",()=>lib.Trash(new[]{one.Id,web.Id},true));
    check(h.Undo()=="回收站"&&lib.All().All(a=>!a.Deleted),"one undo restores all selected trashed assets");h.Undo();check(lib.AssetFolders(one.Id).Single().Id==folder,"undo category assignment removes only added membership");h.Undo();check(lib.Folders().Single(f=>f.Id==folder).Name=="删除后未分类","multiple Ctrl+Z steps run in reverse order");
    h.Record("信息",delegate{one.Name="new";one.Tags="new tags";one.Notes="new notes";one.Source="https://example.org/source";lib.Update(one);});h.Undo();var reread=lib.All().Single(a=>a.Id==one.Id);check(reread.Name=="图片"&&reread.Tags=="标签"&&reread.Notes=="备注"&&reread.Source=="","undo restores saved name, tags, notes and source");
    h.Record("星标",delegate{reread.Favorite=true;lib.Update(reread);});lib.Db.Exec("UPDATE assets SET notes='external note' WHERE id="+Database.Q(one.Id));h.Undo();check(!lib.All().Single(a=>a.Id==one.Id).Favorite&&lib.All().Single(a=>a.Id==one.Id).Notes=="external note","undo modifies only changed columns, preserving unrelated newer edits");
    h.Record("冲突",()=>lib.RenameFolder(folder,"recorded"));lib.RenameFolder(folder,"external");check(Reject(()=>h.Undo())&&h.Count==1&&lib.Folders().Single(f=>f.Id==folder).Name=="external","conflicting external edit is not overwritten and undo entry is not lost");h.Clear();
    check(Reject(()=>h.Record("失败",delegate{lib.RenameFolder(folder,"partial");throw new IOException();}))&&h.Count==0&&lib.Folders().Single(f=>f.Id==folder).Name=="external","failed action atomically rolls back nested changes without adding history");
    check(!h.Record("无变化",()=>lib.RenameFolder(folder,"external"))&&h.Count==0,"no-op leaves undo history unchanged");
    string created=null;h.Record("新建",()=>created=lib.CreateFolder("新分类",""));h.Undo();check(!lib.Folders().Any(f=>f.Id==created),"undo new category removes the empty logical category only");
    h.Record("新建",()=>created=lib.CreateFolder("后来有新内容",""));lib.AddToFolder(new[]{one.Id},created);check(Reject(()=>h.Undo())&&lib.AssetFolders(one.Id).Any(f=>f.Id==created),"undo creation cannot discard later external category membership");h.Clear();
    for(int i=0;i<40;i++){int n=i;h.Record("step "+i,()=>lib.RenameFolder(folder,"step "+n));}check(h.Count==UndoHistory.MaximumSteps,"undo history is bounded to 30 metadata-only steps");h.Clear();
    check(h.Undo()==null&&lib.Db.Query("PRAGMA foreign_key_check").Count==0,"empty history is safe and database relationships remain valid");
    var app=Application.Current??new Application();var old=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;var main=new MainWindow(lib){PreviewMode=true};
    try{var selection=(System.Collections.Generic.HashSet<string>)typeof(MainWindow).GetField("selection",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(main);selection.Add(one.Id);typeof(MainWindow).GetMethod("UpdateInspector",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(main,null);typeof(MainWindow).GetMethod("Trash",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(main,null);main.UndoLast();check(!lib.All().Single(a=>a.Id==one.Id).Deleted&&((TextBlock)main.Window.FindName("Status")).Text.Contains("已撤销"),"production window trash command records and production undo restores it");}finally{main.Window.Close();app.ShutdownMode=old;ThumbnailImage.CurrentLibrary=null;}
   }
  }
 }
}
