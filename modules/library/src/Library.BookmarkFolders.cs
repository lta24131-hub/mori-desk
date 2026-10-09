using System;
using System.Collections.Generic;
using System.Linq;

namespace Mori {
 public sealed partial class Library {
  // One transactional upgrade. Shared folders are mirrored, never removed; pure
  // bookmark trees keep their ids. Image memberships and asset bytes stay intact.
  void InitializeBookmarkFolders(){
   Db.Transaction(delegate{
    if(Db.Query("PRAGMA table_info(folders)").Any(c=>c["name"]=="scope"))return;
    Db.Exec("ALTER TABLE folders ADD COLUMN scope TEXT NOT NULL DEFAULT 'media'");
    var folders=Folders();var assets=All();var bookmarks=new HashSet<string>(assets.Where(a=>a.IsBookmark).Select(a=>a.Id));
    foreach(var root in folders.Where(f=>f.Parent=="")){
     var members=Members(root.Id,true);
     if(members.Any(bookmarks.Contains)&&members.All(bookmarks.Contains)){
      var branch=new HashSet<string>{root.Id};bool changed=true;while(changed){changed=false;foreach(var f in folders)if(branch.Contains(f.Parent)&&branch.Add(f.Id))changed=true;}
      foreach(string id in branch)Db.Exec("UPDATE folders SET scope='bookmark' WHERE id="+Q(id));
     }
    }
    var links=Db.Query("SELECT m.asset,m.folder FROM memberships m JOIN assets a ON a.id=m.asset JOIN folders f ON f.id=m.folder WHERE a.ext='.url' AND f.scope<>'bookmark'");
    foreach(var link in links){string target=BookmarkFolder(link["folder"]);Db.Exec("INSERT OR IGNORE INTO memberships VALUES("+Q(link["asset"])+","+Q(target)+")");Db.Exec("DELETE FROM memberships WHERE asset="+Q(link["asset"])+" AND folder="+Q(link["folder"]));}
   });
  }
  // Older browser extensions can still pass a media category: map its path to
  // the independent bookmark namespace, without mixing the two libraries.
  internal string BookmarkFolder(string id){
   if(string.IsNullOrEmpty(id))return "";var folders=Folders();var current=folders.FirstOrDefault(f=>f.Id==id);
   if(current==null)throw new InvalidOperationException("文件夹已不存在，请重新选择");if(current.Scope=="bookmark")return id;
   var path=new List<Category>();var seen=new HashSet<string>();
   while(current!=null){if(!seen.Add(current.Id))throw new InvalidOperationException("文件夹层级无效");path.Insert(0,current);current=folders.FirstOrDefault(f=>f.Id==current.Parent);}
   string parent="";foreach(var part in path){var found=Folders(true).FirstOrDefault(f=>f.Parent==parent&&f.Name==part.Name);parent=found==null?CreateFolder(part.Name,parent,true):found.Id;}return parent;
  }
  public void MoveBookmarks(IEnumerable<string> ids,string folder){
   var selected=ids.Distinct().ToArray();Db.Transaction(delegate{
    if(folder!=""&&!Folders(true).Any(f=>f.Id==folder))throw new InvalidOperationException("网址文件夹已不存在");
    var valid=new HashSet<string>(All().Where(a=>a.IsBookmark).Select(a=>a.Id));if(selected.Any(id=>!valid.Contains(id)))throw new InvalidOperationException("只能移动网址到网址文件夹");
    foreach(string id in selected){Db.Exec("DELETE FROM memberships WHERE asset="+Q(id));if(folder!="")AddToFolder(new[]{id},folder);}
   });
  }
 }
}
