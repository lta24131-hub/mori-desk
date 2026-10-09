using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace Mori {
 public static class FolderManagementTests {
  static bool Rejected(Action action){try{action();return false;}catch{return true;}}
  static string Rows(Library lib,string table){return string.Join("\n",lib.Db.Query("SELECT * FROM "+table).Select(row=>string.Join("|",row.OrderBy(pair=>pair.Key).Select(pair=>pair.Key+"="+pair.Value))).OrderBy(row=>row));}
  static string Snapshot(Library lib){return Rows(lib,"assets")+Rows(lib,"folders")+Rows(lib,"memberships");}
  static void Invoke(MainWindow main,string method,params object[] args){typeof(MainWindow).GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic,null,args.Select(a=>a.GetType()).ToArray(),null).Invoke(main,args);}
  static TreeViewItem Find(ItemCollection items,string id){foreach(TreeViewItem node in items){if((string)node.Tag==id)return node;var result=Find(node.Items,id);if(result!=null)return result;}return null;}
  public static void Run(string output,Action<bool,string> check){
   Directory.CreateDirectory(output);string path=Path.Combine(output,"fixture.png");
   using(var image=new Bitmap(32,24)){using(var graphics=Graphics.FromImage(image))graphics.Clear(Color.SteelBlue);image.Save(path,ImageFormat.Png);}
   string libraryRoot=Path.Combine(output,"library");
   using(var lib=new Library(libraryRoot)){
    string root=lib.CreateFolder("根分类",""),parent=lib.CreateFolder("父分类",root),child=lib.CreateFolder("子分类",parent),grandchild=lib.CreateFolder("孙分类",child),other=lib.CreateFolder("其他分类","");
    var first=lib.Import(path,parent,"父分类素材","https://example.com/parent","标签","备注").Item;
    var shared=lib.Import(path,parent,"多分类素材","https://example.com/shared","其他标签","其他备注",null,"keep").Item;lib.AddToFolder(new[]{shared.Id},other);
    var nested=lib.Import(path,grandchild,"子分类素材","https://example.com/nested","","",null,"keep").Item;
    var trashed=lib.Import(path,parent,"原回收站素材","https://example.com/trash","","",null,"keep").Item;lib.Trash(new[]{trashed.Id},true);
    var files=lib.All().ToDictionary(a=>lib.FullPath(a),a=>File.ReadAllBytes(lib.FullPath(a)));
    string assets=Rows(lib,"assets"),members=Rows(lib,"memberships");
    lib.RenameFolder(parent,"  新分类 ' 中文 \"名称\"  ");
    check(lib.Folders().Single(f=>f.Id==parent).Name=="新分类 ' 中文 \"名称\""&&lib.Folders().Single(f=>f.Id==parent).Parent==root,"rename supports Unicode and quotes without changing category id or parent");
    check(Rows(lib,"assets")==assets&&Rows(lib,"memberships")==members&&lib.Folders().Single(f=>f.Id==child).Parent==parent,"rename preserves all assets, memberships and descendants");
    string before=Snapshot(lib);
    check(Rejected(()=>lib.RenameFolder(parent," \r\n "))&&Rejected(()=>lib.RenameFolder(parent,"\0"))&&Snapshot(lib)==before,"invalid category names are rejected without mutation");
    check(Rejected(()=>lib.RenameFolder("missing","name"))&&Rejected(()=>lib.DeleteFolder("missing"))&&Snapshot(lib)==before,"missing category actions cannot affect another category");
    // Force a failure after child promotion / membership removal to prove the
    // whole logical-category delete is atomic.
    lib.Db.Exec("CREATE TRIGGER reject_folder_delete BEFORE DELETE ON folders WHEN OLD.id="+Database.Q(parent)+" BEGIN SELECT RAISE(ABORT, 'synthetic failure'); END");
    check(Rejected(()=>lib.DeleteFolder(parent))&&Snapshot(lib)==before,"failed delete rolls back child promotion and membership removal");
    lib.Db.Exec("DROP TRIGGER reject_folder_delete");
    string parentResult=lib.DeleteFolder(parent);
    check(parentResult==root&&!lib.Folders().Any(f=>f.Id==parent)&&lib.Folders().Single(f=>f.Id==child).Parent==root&&lib.Folders().Single(f=>f.Id==grandchild).Parent==child,"delete removes only target and promotes immediate children while preserving deeper hierarchy");
    check(!lib.Classified().Contains(first.Id)&&!lib.Classified().Contains(trashed.Id),"assets without another category become unclassified, including retained trash membership state");
    check(lib.AssetFolders(shared.Id).Select(f=>f.Id).SequenceEqual(new[]{other})&&lib.AssetFolders(nested.Id).Single().Id==grandchild,"delete preserves other category memberships and descendant memberships");
    check(Rows(lib,"assets")==assets&&files.All(pair=>File.Exists(pair.Key)&&File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),"category deletion never changes asset metadata, trash flags or original bytes");
    lib.DeleteFolder(root);
    check(lib.Folders().Single(f=>f.Id==child).Parent==""&&lib.Folders().Single(f=>f.Id==grandchild).Parent==child,"deleting root promotes its children to top level");
    check(lib.Db.Query("PRAGMA foreign_key_check").Count==0&&lib.Db.Query("PRAGMA integrity_check")[0]["integrity_check"]=="ok","category mutations retain SQLite and foreign-key integrity");
    var app=Application.Current??new Application();var shutdown=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;MainWindow main=null;
    try{
     main=new MainWindow(lib){PreviewMode=true};var tree=(TreeView)main.Window.FindName("Folders");
     var selected=Find(tree.Items,grandchild);selected.IsSelected=true;var collapsed=Find(tree.Items,child);collapsed.IsExpanded=false;
     ((TextBox)main.Window.FindName("Name")).Text="pending inspector edit";
     lib.RenameFolder(child,"更新后的子分类");Invoke(main,"ReloadFolders");
     check(!Find(tree.Items,child).IsExpanded&&Find(tree.Items,grandchild).IsSelected,"category refresh preserves collapsed branches and viewed-category selection");
     check(((TextBox)main.Window.FindName("Name")).Text=="pending inspector edit","tree refresh does not discard inspector edits through selection events");
     Invoke(main,"ReloadFolders",grandchild);
     check(Find(tree.Items,child).IsExpanded&&Find(tree.Items,grandchild).IsSelected,"revealing a newly created category opens its ancestor path");
     var leafNode=Find(tree.Items,grandchild);
     check(MainWindow.FolderNode(leafNode,tree)==leafNode&&MainWindow.FolderNode(tree,tree)==null,"context target uses closest nested row instead of parent or current view");
     foreach(string theme in new[]{"dark","light"}){
      Theme.Set(theme);var menu=main.CreateFolderMenu(grandchild);
      check(menu.Items.OfType<MenuItem>().Select(item=>(string)item.Header).SequenceEqual(new[]{"新建子分类…","重命名…","移动分类到…","删除此分类…","批量管理分类…"})&&!menu.IsOpen,theme+" category menu exposes create, rename, move, safe-delete and batch management without opening UI");
      var top=main.CreateFolderMenu(null);check(top.Items.OfType<MenuItem>().Select(item=>(string)item.Header).SequenceEqual(new[]{"新建顶层分类…","批量管理分类…"}),theme+" blank sidebar menu offers top-level creation and batch management");
     }
     check(!main.Window.IsVisible,"folder management UI checks never open a desktop window");
    }finally{if(main!=null)main.Window.Close();Theme.Set("dark");app.ShutdownMode=shutdown;ThumbnailImage.CurrentLibrary=null;}
   }
   using(var reopened=new Library(libraryRoot))check(reopened.Folders().Any(f=>f.Name=="更新后的子分类")&&reopened.All().Count==4&&reopened.All().Count(a=>a.Deleted)==1,"renamed categories, promoted hierarchy and all assets survive library reopen");
  }
 }
}
