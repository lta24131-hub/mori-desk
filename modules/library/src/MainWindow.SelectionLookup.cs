using System;
using System.Collections.Generic;

namespace Mori {
 public partial class MainWindow {
  // Metadata snapshots are replaced, never edited in place. Keep only positions
  // in the current snapshot so selection reads do not retain old Asset objects.
  Dictionary<string,int> selectionPositions;
  List<Asset> selectionIndexSource;
  List<Asset> Selected(){
   var items=new List<Asset>(Math.Min(selection.Count,all.Count));
   if(selection.Count==0)return items;
   // Large selections are cheaper in one linear pass and already in library
   // order. Small selections use the shared snapshot index, regardless of the
   // order in which the user Ctrl-clicked them.
   if(selection.Count>all.Count/8){
    foreach(var item in all)if(selection.Contains(item.Id))items.Add(item);
    return items;
   }
   if(!object.ReferenceEquals(selectionIndexSource,all)){
    var positions=new Dictionary<string,int>(all.Count,StringComparer.Ordinal);
    for(int i=0;i<all.Count;i++)positions.Add(all[i].Id,i);
    selectionPositions=positions;selectionIndexSource=all;
   }
   if(selection.Count==1){
    foreach(string id in selection){int position;if(selectionPositions.TryGetValue(id,out position))items.Add(all[position]);}
   }else{
    var positions=new List<int>(selection.Count);
    foreach(string id in selection){int position;if(selectionPositions.TryGetValue(id,out position))positions.Add(position);}
    positions.Sort();foreach(int position in positions)items.Add(all[position]);
   }
   return items;
  }
  void ApplyGallerySelection(){
   foreach(var asset in visible){
    bool selected=selection.Contains(asset.Id);
    // Asset.Selected also serves legacy views and always raises a notification.
    // Suppress unchanged writes here instead of repainting every visible card.
    if(asset.Selected!=selected)asset.Selected=selected;
    asset.Selecting=batchSelecting;
   }
  }
 }
}
