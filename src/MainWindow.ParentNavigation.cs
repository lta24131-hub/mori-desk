using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Mori {
 public partial class MainWindow {
  // Parent navigation follows the hierarchy, never the last visited page.
  internal static string ParentNavigationTarget(IEnumerable<Category> folders,string current,string root){
   if(current==root)return null;
   var map=folders.ToDictionary(f=>f.Id);Category folder,parent;
   if(!map.TryGetValue(current,out folder))return root;
   if(string.IsNullOrEmpty(folder.Parent)||folder.Parent==current||!map.TryGetValue(folder.Parent,out parent))return root;
   var seen=new HashSet<string>{current};var node=parent;
   while(node!=null){if(!seen.Add(node.Id))return root;if(!map.TryGetValue(node.Parent??"",out node))break;}
   return parent.Id;
  }
  void UpdateParentNavigation(){
   var button=B("GalleryUp");bool available=!canvasProjectsMode&&!browserMode&&!boardMode&&!referenceMode;
   bool scoped=available&&(bookmarkMode||LocalCollectionsActive||CurrentFolder()!="");
   button.Visibility=scoped?Visibility.Visible:Visibility.Collapsed;
   button.IsEnabled=scoped&&!busy&&(LocalCollectionsActive?localCollection!="":bookmarkMode?view!="home":CurrentFolder()!="");
   button.ToolTip=button.IsEnabled?"返回上一级 · Alt+↑":"已经在最外层";
  }
  internal bool NavigateUp(){
   if(canvasProjectsMode||browserMode||boardMode||referenceMode||busy)return false;
   if(LocalCollectionsActive){if(localCollection=="")return false;string before=localCollection;OpenLocalCollection("");return before!=localCollection;}
   if(!bookmarkMode&&CurrentFolder()=="")return false;
   string previous=view,target=ParentNavigationTarget(library.Folders(bookmarkMode),view,bookmarkMode?"home":"all");
   if(!bookmarkMode&&target=="all"){SetSection("assets");return view=="all"&&mediaSection=="assets";}
   if(target==null||!RequestNavigate(target))return false;
   if(bookmarkMode){
    var card=Find<WrapPanel>("BookmarkFolderList").Children.OfType<Button>().FirstOrDefault(b=>Equals(b.Tag,previous));
    if(card!=null){card.Focus();card.BringIntoView();}
   }
   return true;
  }
  internal bool HandleParentNavigationKey(KeyEventArgs e,ModifierKeys modifiers){
   if(e.Handled||modifiers!=ModifierKeys.Alt||(e.Key==Key.System?e.SystemKey:e.Key)!=Key.Up)return false;
   // Alt+Up first closes an open combo, as it does elsewhere in Windows.
   for(var node=e.OriginalSource as DependencyObject;node!=null;node=SelectionParent(node)){var combo=node as ComboBox;if(combo!=null&&combo.IsDropDownOpen)return false;}
   if(!NavigateUp())return false;e.Handled=true;return true;
  }
 }
}
