using System;
using System.Collections.Generic;
using System.Linq;

namespace Mori {
 internal static class TrashAction {
  internal static bool Apply(Library library,IEnumerable<string> selected,bool restore){
   var ids=selected.Where(id=>!string.IsNullOrEmpty(id)).Distinct().ToArray();
   if(ids.Length==0)return false;
   library.Trash(ids,!restore);
   return true;
  }
  internal static string PermanentWarning(int count){return "将永久删除选中的 "+count+" 个素材。\n\n图库内的文件和相关信息将被删除，无法从回收站恢复，也不能用 Ctrl+Z 撤销。\n不会删除最初导入的来源文件或 Eagle 原图库。\n\n确定永久删除吗？";}
  internal static string[] ConfirmPermanent(Library library,IEnumerable<string> selected,Func<int,bool> confirm){
   // Confirmation owns this stable snapshot, never a later selection.
   var ids=selected.Where(id=>!string.IsNullOrEmpty(id)).Distinct().ToArray();
   if(ids.Length==0)return null;
   var trash=new HashSet<string>(library.All().Where(a=>a.Deleted).Select(a=>a.Id));
   if(ids.Any(id=>!trash.Contains(id)))throw new InvalidOperationException("请选择回收站中的素材。");
   return confirm!=null&&confirm(ids.Length)?ids:null;
  }
 }
}
