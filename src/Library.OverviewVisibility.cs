using System;
using System.Collections.Generic;
using System.Linq;

namespace Mori {
 public sealed partial class Library {
  internal static bool CanSetOverviewVisibility(Asset asset){return asset!=null&&!asset.Deleted&&MediaTypes.Contains(asset,"assets");}
  internal void SetHiddenFromAll(IEnumerable<string> ids,bool hidden){
   if(ids==null)throw new ArgumentNullException("ids");
   var keys=ids.Distinct(StringComparer.Ordinal).ToArray();
   if(keys.Length==0)throw new InvalidOperationException("请先选择素材。");
   lock(writeGate)Db.Transaction(delegate{
    var items=All().ToDictionary(a=>a.Id);
    foreach(string id in keys){Asset item;if(id==null||!items.TryGetValue(id,out item)||!CanSetOverviewVisibility(item))throw new InvalidOperationException("所选内容已变化，或不属于全部素材，请刷新后重试。");}
    foreach(string id in keys){var item=items[id];Db.Exec("UPDATE "+(item.IsReference?"desk_refs":"assets")+" SET hidden_all="+(hidden?1:0)+" WHERE id="+Q(item.IsReference?RawReference(id):id));}
   });
  }
 }
}
