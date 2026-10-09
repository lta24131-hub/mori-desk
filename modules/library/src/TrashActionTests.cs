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
 public static class TrashActionTests {
  static bool Rejected(Action action){try{action();return false;}catch{return true;}}
  static string Rows(Library lib,string table){return string.Join("\n",lib.Db.Query("SELECT * FROM "+table).Select(row=>string.Join("|",row.OrderBy(p=>p.Key).Select(p=>p.Key+"="+p.Value))).OrderBy(row=>row));}
  static void Invoke(MainWindow main,string method,params object[] args){typeof(MainWindow).GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic,null,args.Select(a=>a.GetType()).ToArray(),null).Invoke(main,args);}
  // Only synthetic originals in this test's isolated directory are moved.
  static string Journal(Library lib,Asset asset,bool move){
   string relative=Path.Combine(".mori","delete-pending",Guid.NewGuid().ToString("N")+".bin"),path=Library.Inside(lib.Root,relative);
   Directory.CreateDirectory(Path.GetDirectoryName(path));
   lib.Db.Exec("INSERT INTO pending_file_deletes VALUES("+Database.Q(asset.Id)+","+Database.Q(asset.Relative)+","+Database.Q(relative)+")");
   if(move)File.Move(lib.FullPath(asset),path);
   return path;
  }
  public static void Run(string output,Action<bool,string> check){
   Directory.CreateDirectory(output);string source=Path.Combine(output,"source.png");
   using(var image=new Bitmap(16,16)){using(var graphics=Graphics.FromImage(image))graphics.Clear(Color.Red);image.Save(source,ImageFormat.Png);}
   byte[] sourceBytes=File.ReadAllBytes(source);string root=Path.Combine(output,"library");
   using(var lib=new Library(root)){
    string folder=lib.CreateFolder("保留分类","");var assets=new List<Asset>();
    for(int i=0;i<4;i++)assets.Add(lib.Import(source,folder,"测试 "+i,"https://example.com/"+i,"标签","备注",null,"keep").Item);
    var ids=assets.Select(a=>a.Id).ToArray();var originals=assets.ToDictionary(lib.FullPath,a=>File.ReadAllBytes(lib.FullPath(a)));
    check(!TrashAction.Apply(lib,new string[0],false),"empty reversible trash is a no-op");
    check(TrashAction.Apply(lib,new[]{ids[0],ids[0],"",null},false)&&lib.All().Count(a=>a.Deleted)==1,"ordinary delete goes directly to recoverable trash without a permanent-delete prompt");
    check(TrashAction.Apply(lib,new[]{ids[0]},true)&&lib.All().All(a=>!a.Deleted),"restore is a separate non-destructive action");
    check(originals.All(pair=>File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),"move-to-trash and restore preserve all original bytes");
    TrashAction.Apply(lib,ids.Take(3),false);int prompts=0,count=0;var selected=new HashSet<string>(ids.Take(2));
    var plan=TrashAction.ConfirmPermanent(lib,selected,delegate(int n){prompts++;count=n;return false;});
    check(plan==null&&prompts==1&&count==2,"permanent-delete cancellation prompts exactly once with batch count");
    check(lib.All().Count==4&&lib.All().Count(a=>a.Deleted)==3&&originals.All(pair=>File.Exists(pair.Key)),"canceling permanent delete does not alter records or originals");
    check(TrashAction.ConfirmPermanent(lib,selected,null)==null,"permanent delete fails closed without confirmation");
    check(TrashAction.ConfirmPermanent(lib,new string[0],delegate{throw new Exception();})==null,"empty permanent selection never prompts");
    check(Rejected(()=>TrashAction.ConfirmPermanent(lib,new[]{ids[3]},delegate{prompts++;return true;}))&&prompts==1,"active assets cannot enter permanent-delete confirmation");
    check(Rejected(()=>TrashAction.ConfirmPermanent(lib,selected,delegate{throw new IOException("dialog failed");}))&&lib.All().Count==4,"confirmation failure cannot mutate the library");
    check(TrashAction.PermanentWarning(2).Contains("2 个")&&TrashAction.PermanentWarning(2).Contains("永久删除")&&TrashAction.PermanentWarning(2).Contains("无法从回收站恢复")&&TrashAction.PermanentWarning(2).Contains("不会删除最初导入"),"permanent warning states count, irreversibility and source-file preservation");
    plan=TrashAction.ConfirmPermanent(lib,selected,delegate(int n){selected.Clear();selected.Add(ids[2]);return true;});
    check(new HashSet<string>(plan).SetEquals(ids.Take(2)),"confirmation owns a stable snapshot when UI selection changes");
    var normalized=TrashAction.ConfirmPermanent(lib,new[]{ids[0],ids[0],"",null},delegate(int n){count=n;return true;});
    check(normalized.Length==1&&count==1,"permanent count deduplicates IDs and ignores blanks");
    lib.Trash(new[]{ids[0]},false);
    check(Rejected(()=>lib.PermanentlyDelete(plan))&&lib.All().Count==4&&originals.All(pair=>File.Exists(pair.Key)),"restored item invalidates the whole confirmed batch before any file operation");
    lib.Trash(new[]{ids[0]},true);
    check(Rejected(()=>lib.PermanentlyDelete(new[]{ids[0],ids[3]}))&&lib.All().Count==4,"mixed active and trash batch is rejected atomically");
    check(Rejected(()=>lib.PermanentlyDelete(new[]{"missing"}))&&lib.PermanentlyDelete(new string[0]).Count==0,"missing IDs rejected and empty backend delete is harmless");
    foreach(var asset in assets)lib.BuildThumbnail(asset);
    string before=Rows(lib,"assets")+Rows(lib,"memberships");
    lib.Db.Exec("CREATE TRIGGER fail_asset_delete BEFORE DELETE ON assets WHEN OLD.id="+Database.Q(ids[1])+" BEGIN SELECT RAISE(ABORT, 'synthetic failure'); END");
    check(Rejected(()=>lib.PermanentlyDelete(plan))&&before==Rows(lib,"assets")+Rows(lib,"memberships"),"database failure rolls back batch metadata and memberships");
    check(originals.All(pair=>File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value))&&assets.All(a=>File.Exists(lib.ThumbnailPath(a))),"database failure restores staged originals and thumbnails byte-for-byte");
    check(lib.Db.Query("SELECT * FROM pending_file_deletes").Count==0,"successful rollback clears only its completed journal records");
    lib.Db.Exec("DROP TRIGGER fail_asset_delete");
    using(var locked=new FileStream(lib.FullPath(assets[1]),FileMode.Open,FileAccess.Read,FileShare.Read)){
     check(Rejected(()=>lib.PermanentlyDelete(plan))&&before==Rows(lib,"assets")+Rows(lib,"memberships"),"locked original aborts the batch instead of hiding an undeleted file");
     check(originals.All(pair=>File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),"locked-file failure also restores earlier files in the batch");
    }
    string originalRelative=assets[0].Relative;
    lib.Db.Exec("UPDATE assets SET path='../source.png' WHERE id="+Database.Q(ids[0]));
    check(Rejected(()=>lib.PermanentlyDelete(new[]{ids[0]}))&&File.ReadAllBytes(source).SequenceEqual(sourceBytes),"traversal deletion path cannot reach source files");
    lib.Db.Exec("UPDATE assets SET path='.mori/library.db' WHERE id="+Database.Q(ids[0]));
    check(Rejected(()=>lib.PermanentlyDelete(new[]{ids[0]})),"permanent delete cannot target the database or arbitrary files within the library");
    lib.Db.Exec("UPDATE assets SET path="+Database.Q(originalRelative)+" WHERE id="+Database.Q(ids[0]));
    var result=lib.PermanentlyDelete(plan);
    check(result.Count==2&&string.IsNullOrEmpty(result.CleanupWarning)&&lib.All().Count==2,"confirmed batch really removes exactly the selected trash records");
    check(assets.Take(2).All(a=>!File.Exists(lib.FullPath(a))&&!File.Exists(lib.ThumbnailPath(a))),"permanent delete releases original and thumbnail files");
    check(lib.Folders().Count==1&&lib.AssetFolders(ids[2]).Count==1&&lib.AssetFolders(ids[3]).Count==1&&lib.AssetFolders(ids[0]).Count==0,"permanent delete removes only selected memberships, never categories or other memberships");
    check(assets.Skip(2).All(a=>File.ReadAllBytes(lib.FullPath(a)).SequenceEqual(sourceBytes))&&File.ReadAllBytes(source).SequenceEqual(sourceBytes),"unselected files, deliberate duplicate copies and external source remain intact");
    check(lib.Db.Query("SELECT * FROM pending_file_deletes").Count==0&&!Directory.EnumerateFileSystemEntries(Path.Combine(root,".mori","delete-pending")).Any(),"successful permanent delete leaves no retained staging copies");
    check(lib.Db.Query("PRAGMA foreign_key_check").Count==0&&lib.Db.Query("PRAGMA integrity_check")[0]["integrity_check"]=="ok","permanent delete preserves database integrity");
    File.Delete(lib.FullPath(assets[2]));
    check(lib.PermanentlyDelete(new[]{ids[2]}).Count==1&&lib.All().Count==1,"already missing trash file can be removed from index without touching unrelated files");
    lib.Trash(new[]{ids[3]},true);
    var app=Application.Current??new Application();var shutdown=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;MainWindow main=null;
    try{
     main=new MainWindow(lib){PreviewMode=true};
     check(((Button)main.Window.FindName("Delete")).Content.ToString()=="移到回收站"&&((Button)main.Window.FindName("RestoreTrash")).Visibility==Visibility.Collapsed,"ordinary view never labels soft delete as permanent");
     Invoke(main,"Navigate","trash");
     check(((Button)main.Window.FindName("Delete")).Content.ToString()=="永久删除…"&&((Button)main.Window.FindName("RestoreTrash")).Visibility==Visibility.Visible,"trash inspector exposes separate Restore and Permanent Delete buttons");
     var labels=main.CreateAssetMenu(assets[3]).Items.OfType<MenuItem>().Select(i=>i.Header.ToString()).ToArray();
     check(labels.Contains("恢复素材")&&labels.Contains("永久删除…")&&!labels.Contains("移到回收站"),"trash context menu separates recovery from irreversible deletion");
     Invoke(main,"Navigate","all");labels=main.CreateAssetMenu(assets[3]).Items.OfType<MenuItem>().Select(i=>i.Header.ToString()).ToArray();
     check(labels.Contains("移到回收站")&&!labels.Contains("永久删除…")&&!labels.Contains("恢复素材"),"ordinary context menu exposes only recoverable deletion");
     check(!main.Window.IsVisible,"trash UI tests never open a desktop window or real library");
    }finally{if(main!=null)main.Window.Close();app.ShutdownMode=shutdown;ThumbnailImage.CurrentLibrary=null;}
   }
   using(var reopened=new Library(root))check(reopened.All().Count==1&&reopened.All()[0].Deleted,"permanent deletion and retained trash survive reopen");
   string recoverRoot=Path.Combine(output,"recovery");Asset recoverAsset;string staged;
   using(var lib=new Library(recoverRoot)){
    recoverAsset=lib.Import(source,"","恢复测试","","","").Item;lib.Trash(new[]{recoverAsset.Id},true);staged=Journal(lib,recoverAsset,true);
    check(Rejected(()=>lib.Backup(Path.Combine(output,"blocked.mori.zip")))&&Rejected(()=>lib.EnsureDeletionComplete()),"pending file operations block incomplete backups and transfers");
   }
   using(var lib=new Library(recoverRoot)){
    check(lib.All().Count==1&&lib.All()[0].Deleted&&File.ReadAllBytes(lib.FullPath(recoverAsset)).SequenceEqual(sourceBytes)&&!File.Exists(staged),"startup recovers interrupted pre-commit deletion back into trash");
    check(lib.Db.Query("SELECT * FROM pending_file_deletes").Count==0,"startup recovery is idempotent and clears completed journal entries");
    staged=Journal(lib,recoverAsset,false);
   }
   using(var lib=new Library(recoverRoot)){
    check(File.Exists(lib.FullPath(recoverAsset))&&lib.Db.Query("SELECT * FROM pending_file_deletes").Count==0,"crash after planning but before moves leaves the original untouched");
    staged=Journal(lib,recoverAsset,true);File.WriteAllBytes(lib.FullPath(recoverAsset),new byte[]{1,2,3});
   }
   using(var lib=new Library(recoverRoot)){
    check(File.Exists(staged)&&lib.Db.Query("SELECT * FROM pending_file_deletes").Count==1&&!string.IsNullOrEmpty(lib.DeletionRecoveryWarning),"recovery never overwrites a file created in the original location");
    check(File.ReadAllBytes(lib.FullPath(recoverAsset)).SequenceEqual(new byte[]{1,2,3})&&File.ReadAllBytes(staged).SequenceEqual(sourceBytes),"recovery conflict preserves both byte streams");
    File.Delete(lib.FullPath(recoverAsset));
   }
   using(var lib=new Library(recoverRoot)){
    check(File.ReadAllBytes(lib.FullPath(recoverAsset)).SequenceEqual(sourceBytes)&&string.IsNullOrEmpty(lib.DeletionRecoveryWarning),"later startup completes recovery once conflict is resolved");
    staged=Journal(lib,recoverAsset,true);lib.Db.Exec("DELETE FROM assets WHERE id="+Database.Q(recoverAsset.Id));
   }
   using(var locked=new FileStream(staged,FileMode.Open,FileAccess.Read,FileShare.Read)){
    using(var lib=new Library(recoverRoot))check(lib.All().Count==0&&File.Exists(staged)&&lib.Db.Query("SELECT * FROM pending_file_deletes").Count==1&&!string.IsNullOrEmpty(lib.DeletionRecoveryWarning),"occupied staging file retains cleanup journal and reports incomplete disk cleanup");
   }
   using(var lib=new Library(recoverRoot))check(lib.All().Count==0&&!File.Exists(staged)&&lib.Db.Query("SELECT * FROM pending_file_deletes").Count==0,"startup finishes journaled cleanup after metadata commit without resurrecting deleted assets");
   string malformedRoot=Path.Combine(output,"malformed-journal");
   using(var lib=new Library(malformedRoot))lib.Db.Exec("INSERT INTO pending_file_deletes VALUES('absent','ignored','../source.png')");
   using(var lib=new Library(malformedRoot))check(lib.Db.Query("SELECT * FROM pending_file_deletes").Count==1&&!string.IsNullOrEmpty(lib.DeletionRecoveryWarning)&&File.ReadAllBytes(source).SequenceEqual(sourceBytes),"malformed cleanup journal cannot unlink files outside its staging directory");
   check(File.ReadAllBytes(source).SequenceEqual(sourceBytes),"every destructive fixture leaves its import source untouched");
  }
 }
}
