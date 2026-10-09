using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Mori {
 public sealed class PermanentDeleteResult {
  public int Count {get;internal set;}
  public string CleanupWarning {get;internal set;}
 }
 public sealed partial class Library {
  public string DeletionRecoveryWarning {get;private set;}
  internal void EnsureDeletionComplete(){if(Db.Query("SELECT stage FROM pending_file_deletes LIMIT 1").Count>0)throw new IOException("请先重新打开图库完成上次删除的文件恢复或清理，再备份或迁移。");}
  sealed class DeleteFile {public string Asset,Original,Stage,Directory;}

  void InitializeDeletionRecovery(){
   Db.Exec("CREATE TABLE IF NOT EXISTS pending_file_deletes(asset TEXT NOT NULL, original TEXT NOT NULL, stage TEXT PRIMARY KEY)");
   using(var lease=LibraryTransfer.TryAcquireWrite(Root)){
    if(lease!=null)lock(writeGate)DeletionRecoveryWarning=RecoverDeleteFiles();
   }
  }
  static void NoDeleteLinks(string path){
   for(string current=Path.GetFullPath(path);!string.IsNullOrEmpty(current);current=Path.GetDirectoryName(current)){
    FileAttributes attributes;
    try{attributes=File.GetAttributes(current);}catch(FileNotFoundException){continue;}catch(DirectoryNotFoundException){continue;}
    if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("为避免误删，不支持删除位于符号链接或连接点中的文件。");
   }
  }
  string DeletePath(string relative,string ownedDirectory){
   if(string.IsNullOrWhiteSpace(relative)||Path.IsPathRooted(relative))throw new IOException("删除路径无效，操作已停止。");
   string path=Inside(Root,relative),owner=Inside(Root,ownedDirectory).TrimEnd('\\')+"\\";
   if(!path.StartsWith(owner,StringComparison.OrdinalIgnoreCase))throw new IOException("删除路径超出图库专用目录，操作已停止。");
   NoDeleteLinks(path);
   if(Directory.Exists(path))throw new IOException("删除目标不是素材文件，操作已停止。");
   return path;
  }
  string StagePath(string relative){
   string path=DeletePath(relative,Path.Combine(".mori","delete-pending"));Guid id;
   if(Path.GetDirectoryName(path)!=Path.Combine(Root,".mori","delete-pending")||Path.GetExtension(path)!=".bin"||!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path),"N",out id))throw new IOException("删除暂存路径无效，操作已停止。");
   return path;
  }
  List<Asset> DeleteCandidates(string[] ids){
   var byId=All().ToDictionary(a=>a.Id);
   var assets=new List<Asset>();
   foreach(string id in ids){
    Asset asset;Guid value;
    if(!Guid.TryParseExact(id,"N",out value)||!byId.TryGetValue(id,out asset)||!asset.Deleted)throw new InvalidOperationException("所选素材已恢复或发生变化，请重新选择回收站中的素材。");
    DeletePath(asset.Relative,"原图");
    assets.Add(asset);
   }
   return assets;
  }
  DeleteFile PlanFile(Asset asset,string relative,string directory){
   DeletePath(relative,directory);
   return new DeleteFile{Asset=asset.Id,Original=relative,Directory=directory,Stage=Path.Combine(".mori","delete-pending",Guid.NewGuid().ToString("N")+".bin")};
  }
  // The durable journal is committed before touching files. Until the asset
  // transaction commits, moved originals are recoverable at their exact paths.
  // After commit, only the journal's bounded staging files may be unlinked.
  public PermanentDeleteResult PermanentlyDelete(IEnumerable<string> selected){
   var ids=selected.Where(id=>!string.IsNullOrEmpty(id)).Distinct().ToArray();
   if(ids.Length==0)return new PermanentDeleteResult{Count=0};
   using(var lease=LibraryTransfer.TryAcquireWrite(Root)){
    if(lease==null)throw new IOException("图库正在迁移或接收收藏，请稍后重试删除。");
    lock(writeGate){
     string warning=RecoverDeleteFiles();
     if(!string.IsNullOrEmpty(warning))throw new IOException("上次删除尚未处理完成："+warning);
     var assets=DeleteCandidates(ids);var files=new List<DeleteFile>();
     foreach(var asset in assets){
      files.Add(PlanFile(asset,asset.Relative,"原图"));
      files.Add(PlanFile(asset,Path.Combine(".mori","thumbs",asset.Id+ThumbnailSuffix),Path.Combine(".mori","thumbs")));
     }
     foreach(var file in files)StagePath(file.Stage);
     Directory.CreateDirectory(Path.Combine(Root,".mori","delete-pending"));
     Db.Transaction(delegate{
      foreach(var file in files)Db.Exec("INSERT INTO pending_file_deletes(asset,original,stage) VALUES("+Q(file.Asset)+","+Q(file.Original)+","+Q(file.Stage)+")");
     });
     try{
      Db.Transaction(delegate{
       var current=DeleteCandidates(ids);
       if(current.Where((a,i)=>a.Relative!=assets[i].Relative).Any())throw new IOException("素材路径发生变化，请重新选择。");
       foreach(var file in files){
        string original=DeletePath(file.Original,file.Directory);
        string stage=StagePath(file.Stage);
        // File.Move must report access / sharing errors, not silently skip them.
        try{File.GetAttributes(original);}catch(FileNotFoundException){continue;}catch(DirectoryNotFoundException){continue;}
        File.Move(original,stage);
       }
       foreach(string id in ids){Db.Exec("DELETE FROM memberships WHERE asset="+Q(id));Db.Exec("DELETE FROM assets WHERE id="+Q(id)+" AND deleted=1");}
      });
     }catch(Exception error){
      string recovery=RecoverDeleteFiles();
      throw new IOException("永久删除未完成，素材仍在回收站。"+(string.IsNullOrEmpty(recovery)?"已还原暂存文件。":"部分文件等待恢复："+recovery)+"\n"+error.Message,error);
     }
     DeletionRecoveryWarning=RecoverDeleteFiles();
     return new PermanentDeleteResult{Count=ids.Length,CleanupWarning=DeletionRecoveryWarning};
    }
   }
  }
  string RecoverDeleteFiles(){
   var errors=new List<string>();
   foreach(var row in Db.Query("SELECT asset,original,stage FROM pending_file_deletes")){
    try{
     string stage=StagePath(row["stage"]);
     var assets=Db.Query("SELECT path FROM assets WHERE id="+Q(row["asset"]));
     if(assets.Count>0){
      string thumb=Path.Combine(".mori","thumbs",row["asset"]+ThumbnailSuffix),relative=row["original"];
      if(relative!=assets[0]["path"]&&relative!=thumb)throw new IOException("恢复路径与素材记录不符，暂存文件已保留。");
      string original=DeletePath(relative,relative==thumb?Path.Combine(".mori","thumbs"):"原图");
      if(File.Exists(stage)){
       if(File.Exists(original))throw new IOException("恢复位置已存在文件，未覆盖："+Path.GetFileName(original));
       Directory.CreateDirectory(Path.GetDirectoryName(original));File.Move(stage,original);
      }
     }else{
      // Never delete the original/source path here, only a journaled staging file.
      File.Delete(stage);
     }
     Db.Exec("DELETE FROM pending_file_deletes WHERE stage="+Q(row["stage"]));
    }catch(Exception error){if(errors.Count<3)errors.Add(error.Message);}
   }
   return string.Join("\n",errors);
  }
 }
}
