using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Mori {
 internal sealed class CategoryChoice {
  public string Id {get;set;} public string Label {get;set;}
 }
 internal static class CategoryPicker {
  internal static List<CategoryChoice> Choices(List<Category> folders,bool move){
   var result=new List<CategoryChoice>();if(move)result.Add(new CategoryChoice{Id="",Label="未分类"});
   foreach(var folder in folders){
    string label=folder.Name;var current=folder;var seen=new HashSet<string>{folder.Id};
    while(current.Parent!=""){current=folders.FirstOrDefault(f=>f.Id==current.Parent);if(current==null||!seen.Add(current.Id))break;label=current.Name+" / "+label;}
    result.Add(new CategoryChoice{Id=folder.Id,Label=label});
   }
   return result.OrderBy(c=>c.Id==""?0:1).ThenBy(c=>c.Label,StringComparer.CurrentCultureIgnoreCase).ToList();
  }
  internal static string Choose(Window owner,List<Category> folders,bool move,int count){
   return Show(owner,Choices(folders,move),move?"移动到分类":"添加分类","已选择 "+count+" 项 · "+(move?"替换原分类归属，可 Ctrl+Z 撤销":"保留原分类，同时添加到所选分类"),move?"移动":"添加");
  }
  internal static List<CategoryChoice> ParentChoices(List<Category> folders,HashSet<string> excluded){
   var choices=Choices(folders,false).Where(c=>!excluded.Contains(c.Id)).ToList();choices.Insert(0,new CategoryChoice{Id="",Label="顶层分类"});return choices;
  }
  internal static string ChooseParent(Window owner,List<Category> folders,HashSet<string> excluded,int count){
   return Show(owner,ParentChoices(folders,excluded),"移动分类","已选择 "+count+" 个分类 · 选择新的上级\n子分类和素材一起保留；父子同时选中时保留原层级。可 Ctrl+Z 撤销。","移动");
  }
  internal static string Show(Window owner,List<CategoryChoice> choices,string title,string description,string action){
   var dialog=new Window{Owner=owner,Title=title,Width=470,Height=480,MinWidth=380,MinHeight=340,WindowStartupLocation=WindowStartupLocation.CenterOwner,ShowInTaskbar=false};Theme.Attach(dialog,owner);
   var root=new Grid{Margin=new Thickness(20)};dialog.Content=root;
   foreach(var height in new[]{GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto})root.RowDefinitions.Add(new RowDefinition{Height=height});
   var hint=new TextBlock{Text=description,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};hint.SetResourceReference(TextBlock.ForegroundProperty,"Muted");root.Children.Add(hint);
   var search=new TextBox{Margin=new Thickness(0,0,0,10),ToolTip="搜索分类名称或路径"};System.Windows.Automation.AutomationProperties.SetName(search,"搜索分类");Grid.SetRow(search,1);root.Children.Add(search);
   var list=new ListBox{DisplayMemberPath="Label",ItemsSource=choices,BorderThickness=new Thickness(0),Padding=new Thickness(4)};list.SetResourceReference(Control.BackgroundProperty,"Surface");list.SetResourceReference(Control.ForegroundProperty,"Text");ScrollViewer.SetHorizontalScrollBarVisibility(list,ScrollBarVisibility.Disabled);Grid.SetRow(list,2);root.Children.Add(list);
   var row=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,14,0,0)};Grid.SetRow(row,3);root.Children.Add(row);
   var cancel=new Button{Content="取消",IsCancel=true,MinWidth=70};var confirm=new Button{Content=action,IsDefault=true,IsEnabled=false,MinWidth=82};confirm.SetResourceReference(Control.BackgroundProperty,"AccentSoft");row.Children.Add(cancel);row.Children.Add(confirm);
   list.SelectionChanged+=delegate{confirm.IsEnabled=list.SelectedItem!=null;};search.TextChanged+=delegate{string q=search.Text.Trim();list.ItemsSource=choices.Where(c=>c.Label.IndexOf(q,StringComparison.CurrentCultureIgnoreCase)>=0).ToList();};
   confirm.Click+=delegate{if(list.SelectedItem!=null)dialog.DialogResult=true;};dialog.Loaded+=delegate{search.Focus();};dialog.PreviewKeyDown+=delegate(object s,KeyEventArgs e){if(e.Key==Key.Escape){dialog.DialogResult=false;e.Handled=true;}};
   return dialog.ShowDialog()==true?((CategoryChoice)list.SelectedItem).Id:null;
  }
 }
}
