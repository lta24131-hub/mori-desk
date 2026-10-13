using System;
using System.Collections.Generic;
using System.Linq;

namespace Mori {
 public sealed partial class Library {
  // Logical move only: no original file is copied, renamed or deleted.
  public void MoveAssets(IEnumerable<string> ids,string folder,bool bookmarks){
   if(ids==null||folder==null)throw new ArgumentNullException();
   var selected=ids.Distinct().ToArray();if(selected.Length==0)return;
   lock(writeGate)Db.Transaction(delegate{
    if(folder!=""&&!Folders(bookmarks).Any(f=>f.Id==folder))throw new InvalidOperationException("目标分类已不存在，请重新选择。");
    var valid=new HashSet<string>(All().Where(a=>!a.Deleted&&a.IsBookmark==bookmarks).Select(a=>a.Id));
    if(selected.Any(id=>!valid.Contains(id)))throw new InvalidOperationException("所选内容已变化，请刷新后重试；网址与素材不能混合移动。");
    foreach(string id in selected){
     if(AssignReference(id,folder))continue;
     Db.Exec("DELETE FROM memberships WHERE asset="+Q(id));
     if(folder!="")Db.Exec("INSERT INTO memberships(asset,folder) VALUES("+Q(id)+","+Q(folder)+")");
    }
   });
  }
 }
}
