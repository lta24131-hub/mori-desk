using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mori {
 public static class FolderBatchTests {
  static string Rows(Library lib,string table){return string.Join("\n",lib.Db.Query("SELECT * FROM "+table).Select(r=>string.Join("|",r.OrderBy(p=>p.Key).Select(p=>p.Key+"="+p.Value))).OrderBy(r=>r));}
  static string Snapshot(Library lib){return Rows(lib,"folders")+Rows(lib,"memberships")+Rows(lib,"assets");}
  static bool Fails(Action action){try{action();return false;}catch{return true;}}
  static object Field(MainWindow main,string name){return typeof(MainWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(main);}
  static HashSet<string> Selected(MainWindow main){return (HashSet<string>)Field(main,"folderSelection");}
  static T Find<T>(MainWindow main,string name)where T:class{return main.Window.FindName(name)as T;}
  static TreeViewItem Node(ItemCollection items,string id){foreach(TreeViewItem node in items){if((string)node.Tag==id)return node;var child=Node(node.Items,id);if(child!=null)return child;}return null;}
  public static void Run(string output,Action<bool,string> check){
   Directory.CreateDirectory(output);var app=Application.Current??new Application();var shutdown=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
   using(var lib=new Library(Path.Combine(output,"library"))){
    string root=lib.CreateFolder("01 产品参考",""),parent=lib.CreateFolder("02 机器人",root),child=lib.CreateFolder("03 关节结构",parent),grandchild=lib.CreateFolder("04 细节",child),other=lib.CreateFolder("05 交通工具",""),target=lib.CreateFolder("06 目标分类",""),web=lib.CreateFolder("网址","",true);
    string file=Path.Combine(output,"fixture.png");using(var bmp=new System.Drawing.Bitmap(64,48)){using(var g=System.Drawing.Graphics.FromImage(bmp))g.Clear(System.Drawing.Color.SteelBlue);bmp.Save(file,System.Drawing.Imaging.ImageFormat.Png);}
    var a=lib.Import(file,parent,"分类素材","https://example.org/","标签","备注").Item;
    var shared=lib.Import(file,parent,"多归属","","","",null,"keep").Item;lib.AddToFolder(new[]{shared.Id},other);
    var nested=lib.Import(file,child,"子分类素材","","","",null,"keep").Item;
    var trashed=lib.Import(file,parent,"回收站素材","","","",null,"keep").Item;lib.Trash(new[]{trashed.Id},true);
    var bookmark=lib.ImportBookmark("https://example.org/folder","网址",web).Item;
    var originals=lib.All().ToDictionary(x=>lib.FullPath(x),x=>File.ReadAllBytes(lib.FullPath(x)));
    string initial=Snapshot(lib),assetRows=Rows(lib,"assets"),members=Rows(lib,"memberships");var history=new UndoHistory(lib.Db);
    check(history.Record("移动分类",()=>lib.MoveFolders(new[]{parent,child,other,parent},target,false)),"folder batch move records one undo step and accepts duplicate selected IDs");
    check(lib.Folders().Single(f=>f.Id==parent).Parent==target&&lib.Folders().Single(f=>f.Id==other).Parent==target&&lib.Folders().Single(f=>f.Id==child).Parent==parent&&lib.Folders().Single(f=>f.Id==grandchild).Parent==child,"batch folder move reparents roots only and preserves selected parent-child hierarchy");
    check(Rows(lib,"assets")==assetRows&&Rows(lib,"memberships")==members,"folder move preserves all asset records and category memberships");
    history.Undo();check(Snapshot(lib)==initial,"single undo restores every moved category");
    foreach(string invalid in new[]{parent,child,grandchild,web,"missing"})check(Fails(()=>lib.MoveFolders(new[]{parent,other},invalid,false))&&Snapshot(lib)==initial,"folder move rejects cycle, mixed scope or missing parent atomically: "+(invalid==web?"bookmark":invalid=="missing"?"missing":"branch"));
    check(Fails(()=>lib.MoveFolders(new[]{parent,"missing"},target,false))&&Snapshot(lib)==initial,"missing source blocks entire folder move");
    check(Fails(()=>lib.DeleteFolders(new[]{parent,web},false))&&Snapshot(lib)==initial,"mixed-scope folder delete cannot delete part of selection");
    check(Fails(()=>lib.DeleteFolders(new[]{parent,"missing"},false))&&Snapshot(lib)==initial,"missing folder blocks entire batch delete");
    lib.Db.Exec("CREATE TRIGGER reject_batch_move BEFORE UPDATE OF parent ON folders WHEN OLD.id="+Database.Q(other)+" BEGIN SELECT RAISE(ABORT,'test'); END");
    check(Fails(()=>lib.MoveFolders(new[]{parent,other},target,false))&&Snapshot(lib)==initial,"failed folder move rolls back earlier roots");lib.Db.Exec("DROP TRIGGER reject_batch_move");
    lib.Db.Exec("CREATE TRIGGER reject_batch_delete BEFORE DELETE ON folders WHEN OLD.id="+Database.Q(parent)+" BEGIN SELECT RAISE(ABORT,'test'); END");
    check(Fails(()=>lib.DeleteFolders(new[]{child,parent},false))&&Snapshot(lib)==initial,"failed batch delete rolls back memberships, promotion and earlier deletes");lib.Db.Exec("DROP TRIGGER reject_batch_delete");
    history.Record("删除分类",()=>lib.DeleteFolders(new[]{parent,child},false));
    check(lib.Folders().Single(f=>f.Id==grandchild).Parent==root&&!lib.Folders().Any(f=>f.Id==parent||f.Id==child),"delete selected nested categories promotes unchecked descendants to nearest surviving ancestor");
    check(lib.AssetFolders(a.Id).Count==0&&lib.AssetFolders(nested.Id).Count==0&&lib.AssetFolders(shared.Id).Single().Id==other,"deleted category content becomes unfiled while other category memberships survive");
    check(Rows(lib,"assets")==assetRows&&lib.AssetFolders(bookmark.Id).Single().Id==web,"batch folder deletion preserves all metadata, trash flags and bookmarks");history.Undo();check(Snapshot(lib)==initial,"one undo restores deleted hierarchy and original memberships");
    history.Record("删除顶层",()=>lib.DeleteFolders(new[]{root,parent},false));check(lib.Folders().Single(f=>f.Id==child).Parent==""&&lib.Folders().Single(f=>f.Id==grandchild).Parent==child,"unchecked branch survives deleting selected top-level ancestors");history.Undo();
    var excluded=FolderBatch.Branches(lib.Folders(false),new[]{parent});var choices=CategoryPicker.ParentChoices(lib.Folders(false),excluded);
    check(choices[0].Id==""&&choices[0].Label=="顶层分类"&&choices.All(c=>!excluded.Contains(c.Id))&&!choices.Any(c=>c.Id==web),"folder destination picker offers top level, excludes selected subtrees and bookmarks");
    history.Record("顶层",()=>lib.MoveFolders(new[]{child},"",false));check(lib.Folders().Single(f=>f.Id==child).Parent==""&&lib.Folders().Single(f=>f.Id==grandchild).Parent==child,"move to top level keeps nested hierarchy");history.Undo();
    lib.MoveFolders(new string[0],target,false);lib.DeleteFolders(new string[0],false);check(Snapshot(lib)==initial,"empty batch is a no-op");
    var main=new MainWindow(lib){PreviewMode=true};
    try{
     var tree=Find<TreeView>(main,"Folders");check(Find<StackPanel>(main,"FolderBatchBar").Visibility==Visibility.Collapsed,"normal sidebar has no batch-toolbar space");
     main.SetFolderManaging(true);check(Find<StackPanel>(main,"FolderBatchBar").Visibility==Visibility.Visible&&!Find<Button>(main,"FolderDelete").IsEnabled,"manage reveals category-only toolbar with disabled empty actions");
     string current=(string)Field(main,"view");main.SelectFolder(parent,ModifierKeys.None);main.SelectFolder(other,ModifierKeys.None);
     check(Selected(main).SetEquals(new[]{parent,other})&&(string)Field(main,"view")==current,"ordinary category multi-clicks preserve gallery view");
     check(FolderSelection.GetChecked(Node(tree.Items,parent))&&Find<TextBlock>(main,"FolderSelectedCount").Text=="已选 2","selected folders have checked state and count");
     main.SelectFolder(parent,ModifierKeys.None);check(Selected(main).SetEquals(new[]{other}),"second click unchecks only one folder");main.SelectFolder(parent,ModifierKeys.None);
     var menu=main.CreateFolderMenu(parent);check(menu.Items.OfType<MenuItem>().Any(m=>m.Header.ToString()=="删除 2 个分类…")&&!menu.Items.OfType<MenuItem>().Any(m=>m.Header.ToString()=="重命名…"),"multi-folder context menu targets full selection and hides ambiguous rename");
     check(main.FolderActionIds(parent).Length==2&&main.FolderActionIds(target).SequenceEqual(new[]{target}),"context action resolves checked group or unselected row precisely");
     main.ApplyFolderMove(Selected(main).ToArray(),target);check(lib.Folders().Single(f=>f.Id==parent).Parent==target&&Selected(main).Count==2,"production move preserves selection across tree rebuild");main.UndoLast();check(Snapshot(lib)==initial,"production folder move integrates Ctrl+Z history");
     main.ApplyFolderDelete(new[]{parent,child});check(lib.Folders().Single(f=>f.Id==grandchild).Parent==root&&!Selected(main).Contains(parent),"production delete removes stale selections and keeps unchecked descendants");main.UndoLast();check(Snapshot(lib)==initial,"production batch delete undo restores exact library state");
     main.SetFolderManaging(false);check(Selected(main).Count==0&&Find<StackPanel>(main,"FolderBatchBar").Visibility==Visibility.Collapsed,"done clears only category batch state");
     main.RequestNavigate(parent);main.SelectFolder(other,ModifierKeys.Control);check(Selected(main).SetEquals(new[]{parent,other}),"Ctrl-click starts folder multi-selection from currently viewed category");
     main.SetFolderManaging(false);main.SetFolderManaging(true);main.SelectFolder(parent,ModifierKeys.None);main.SelectFolder(grandchild,ModifierKeys.Shift);check(Selected(main).SetEquals(new[]{parent,child,grandchild}),"Shift selects contiguous visible tree range");
     Node(tree.Items,parent).IsExpanded=false;main.SelectAllFolders();check(Selected(main).Count==4&&!Selected(main).Contains(child)&&!Selected(main).Contains(grandchild),"folder select-all includes expanded rows only, never hidden descendants or bookmarks");
     main.SetSection("bookmarks");check(Selected(main).Count==0&&Find<StackPanel>(main,"FolderBatchBar").Visibility==Visibility.Collapsed,"leaving media sidebar clears hidden folder selections");
     main.SetSection("pictures");var item=((List<Asset>)Field(main,"visible")).First();main.SelectTile(item,ModifierKeys.None);main.FinishTileSelection(item,false);Find<TextBox>(main,"Name").Text="待保存";main.PendingInformationChoice=()=>MessageBoxResult.Cancel;main.SetFolderManaging(true);
     check(!(bool)Field(main,"folderManaging")&&Find<TextBox>(main,"Name").Text=="待保存","canceling pending inspector edits prevents entering folder management");int prompts=0;main.PendingInformationChoice=()=>{prompts++;return MessageBoxResult.No;};main.SetFolderManaging(true);main.SelectFolder(root,ModifierKeys.None);main.SelectFolder(other,ModifierKeys.None);check(prompts==1&&Find<TextBox>(main,"Name").Text==item.Name,"discarding pending information prompts once, not again on every folder click");main.PendingInformationChoice=null;
     typeof(MainWindow).GetField("busy",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(main,true);main.SelectFolder(target,ModifierKeys.None);check(Selected(main).Count==2,"busy app blocks category selection changes");typeof(MainWindow).GetField("busy",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(main,false);
     var content=(FrameworkElement)main.Window.Content;content.SetResourceReference(Panel.BackgroundProperty,"Canvas");var workspace=Find<Grid>(main,"Workspace");
     foreach(string theme in new[]{"dark","light"})foreach(int width in new[]{160,208,320}){
      Theme.Set(theme);((SidebarLayout)Field(main,"sidebars")).SetPreferred(true,width);content.Measure(new Size(1100,760));content.Arrange(new Rect(0,0,1100,760));content.UpdateLayout();
      typeof(MainWindow).GetMethod("BuildRows",BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(bool)},null).Invoke(main,new object[]{true});content.UpdateLayout();
      check(Math.Abs(workspace.ColumnDefinitions[0].ActualWidth-width)<1,theme+" / "+width+" verifies actual sidebar width");
      var bar=Find<StackPanel>(main,"FolderBatchBar");var button=Find<Button>(main,"FolderDelete");var point=button.TranslatePoint(new Point(button.ActualWidth,button.ActualHeight),bar);
      check(point.X<=bar.ActualWidth+1&&point.Y<=bar.ActualHeight+1,theme+" / "+width+" folder management actions fit narrow sidebar");
      var node=Node(tree.Items,root);var tick=(Border)node.Template.FindName("FolderCheck",node);check(tick.Visibility==Visibility.Visible&&FolderSelection.GetChecked(node),theme+" / "+width+" folder checkboxes remain visible");
      var bitmap=new RenderTargetBitmap(1100,760,96,96,PixelFormats.Pbgra32);bitmap.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(Path.Combine(output,theme+"-"+width+".png")))encoder.Save(stream);
     }
    }finally{main.Window.Close();Theme.Set("dark");ThumbnailImage.CurrentLibrary=null;app.ShutdownMode=shutdown;}
    check(originals.All(pair=>File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),"all batch-folder tests preserve every original byte");
    check(lib.Db.Query("PRAGMA foreign_key_check").Count==0&&lib.Db.Query("PRAGMA integrity_check")[0]["integrity_check"]=="ok","folder batch operations retain database integrity");
   }
  }
 }
}
