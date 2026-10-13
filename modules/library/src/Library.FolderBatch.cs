using System;
using System.Collections.Generic;
using System.Linq;

namespace Mori {
 internal static class FolderBatch {
  internal static HashSet<string> Validate(List<Category> folders,IEnumerable<string> ids){
   if(ids==null)throw new ArgumentNullException("ids");
   var selected=new HashSet<string>(ids);var valid=new HashSet<string>(folders.Select(f=>f.Id));
   if(selected.Any(id=>string.IsNullOrEmpty(id)||!valid.Contains(id)))throw new InvalidOperationException("所选分类已变化，请重新选择；素材和网址分类不能混合管理。");
   return selected;
  }
  internal static HashSet<string> Branches(List<Category> folders,IEnumerable<string> ids){
   var result=Validate(folders,ids);bool changed=true;
   while(changed){changed=false;foreach(var f in folders)if(result.Contains(f.Parent)&&result.Add(f.Id))changed=true;}
   return result;
  }
  internal static string[] Roots(List<Category> folders,IEnumerable<string> ids){
   var selected=Validate(folders,ids);var map=folders.ToDictionary(f=>f.Id);var result=new List<string>();
   foreach(string id in selected){string parent=map[id].Parent;var seen=new HashSet<string>{id};bool covered=false;
    while(parent!=""){if(!seen.Add(parent)||!map.ContainsKey(parent))throw new InvalidOperationException("分类层级无效，请刷新后重试。");if(selected.Contains(parent))covered=true;parent=map[parent].Parent;}
    if(!covered)result.Add(id);
   }
   return result.ToArray();
  }
 }
 public sealed partial class Library {
  // Reparent only selected roots; a selected child travels with its parent.
  // Original files and memberships are untouched, including nested categories.
  public void MoveFolders(IEnumerable<string> ids,string parent,bool bookmarks){
   if(parent==null)throw new ArgumentNullException("parent");
   lock(writeGate)Db.Transaction(delegate{
    var folders=Folders(bookmarks);var selected=FolderBatch.Validate(folders,ids);if(selected.Count==0)return;
    var roots=FolderBatch.Roots(folders,selected);
    if(parent!=""&&(!folders.Any(f=>f.Id==parent)||FolderBatch.Branches(folders,selected).Contains(parent)))throw new InvalidOperationException("不能移入所选分类自身、其子分类或其他类型的分类。");
    if(parent!="")FolderBatch.Roots(folders,new[]{parent});
    foreach(string id in roots)Db.Exec("UPDATE folders SET parent="+Q(parent)+" WHERE id="+Q(id));
   });
  }
  // Remove exactly the checked categories. Unchecked children find their nearest
  // surviving ancestor, independent of selection order. Never delete assets.
  public void DeleteFolders(IEnumerable<string> ids,bool bookmarks){
   lock(writeGate)Db.Transaction(delegate{
    var folders=Folders(bookmarks);var selected=FolderBatch.Validate(folders,ids);if(selected.Count==0)return;
    FolderBatch.Roots(folders,selected);var map=folders.ToDictionary(f=>f.Id);
    foreach(var folder in folders.Where(f=>!selected.Contains(f.Id)&&selected.Contains(f.Parent))){
     string parent=folder.Parent;while(selected.Contains(parent))parent=map[parent].Parent;
     Db.Exec("UPDATE folders SET parent="+Q(parent)+" WHERE id="+Q(folder.Id));
    }
    foreach(string id in selected)Db.Exec("DELETE FROM memberships WHERE folder="+Q(id));
    foreach(string id in selected)UnfileReferences(id);
    foreach(string id in selected)Db.Exec("DELETE FROM folders WHERE id="+Q(id));
   });
  }
 }
}
