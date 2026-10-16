using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Automation;

namespace Mori {
 public partial class MainWindow {
  internal void SetBookmarkMode(bool value){SetSection(value?"bookmarks":"assets");}
  internal void SetSection(string section){if(busy||!PrepareLibraryChange())return;HideCanvasProjects();HideBrowser();if(MoriDesk.Entry.BoardEnabled&&section!="bookmarks"){ShowBoard(section);return;}HideBoard();localCollection="";mediaSection=section;bookmarkMode=section=="bookmarks";Find<ComboBox>("MediaFilter").SelectedIndex=0;T("Search").Clear();Navigate(bookmarkMode?"home":"all");}
  internal void AddBookmark(string initial){
   if(canvasProjectsMode)return;
   if(busy||!PrepareLibraryChange())return;
   var dialog=new Window{Owner=Window,Title="收藏网址",Width=480,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner};Theme.Attach(dialog,Window);
   var body=new StackPanel{Margin=new Thickness(24)};dialog.Content=body;
   body.Children.Add(new TextBlock{Text="收藏网址",FontSize=19,Margin=new Thickness(0,0,0,16)});
   body.Children.Add(new TextBlock{Text="网址",Margin=new Thickness(0,0,0,6)});
   var address=new TextBox{Text=initial??"",MaxLength=8000};AutomationProperties.SetName(address,"收藏网址地址");body.Children.Add(address);
   body.Children.Add(new TextBlock{Text="名称（可选）",Margin=new Thickness(0,14,0,6)});
   var title=new TextBox{MaxLength=250};AutomationProperties.SetName(title,"网址名称");body.Children.Add(title);
   body.Children.Add(new TextBlock{Text="分类",Margin=new Thickness(0,14,0,6)});
   var categories=library.Folders(true);var folders=new ComboBox{SelectedValuePath="Tag",Margin=new Thickness(0,0,0,14)};
   folders.Items.Add(new ComboBoxItem{Content="未分类",Tag=""});foreach(var f in categories)folders.Items.Add(new ComboBoxItem{Content=FolderLabel(f,categories),Tag=f.Id});folders.SelectedValue=bookmarkMode?CurrentFolder():"";AutomationProperties.SetName(folders,"网址分类");body.Children.Add(folders);
   var hint=new TextBlock{Text="只保存链接，不下载网页；双击在软件内快速浏览，右键可用外部浏览器打开。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,14)};hint.SetResourceReference(TextBlock.ForegroundProperty,"Muted");body.Children.Add(hint);
   var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};var cancelButton=new Button{Content="取消",IsCancel=true};var save=new Button{Content="收藏",IsDefault=true};actions.Children.Add(cancelButton);actions.Children.Add(save);body.Children.Add(actions);
   cancelButton.Click+=delegate{dialog.Close();};save.Click+=delegate{
    try{
     string url=Library.BookmarkAddress(address.Text);string folder=Convert.ToString(folders.SelectedValue);
     history.Clear();var result=library.ImportBookmark(url,title.Text,folder);
     if(result.ChoiceRequired){var choice=MessageBox.Show(dialog,"这个网址已收藏。\n\n是：使用已有条目并加入所选分类（若在回收站则恢复）。\n否：另存一份。\n取消：不做修改。","网址已存在",MessageBoxButton.YesNoCancel);if(choice==MessageBoxResult.Cancel)return;result=library.ImportBookmark(url,title.Text,folder,"","",choice==MessageBoxResult.Yes?"reuse":"keep");}
     dialog.Close();HideBoard();bookmarkMode=true;mediaSection="bookmarks";view=folder==""?"home":folder;similarId=colorId="";T("Search").Clear();Find<ComboBox>("MediaFilter").SelectedIndex=0;selection.Clear();selection.Add(result.Item.Id);Refresh(true);Status(result.KeptCopy?"网址已另存一份":result.Duplicate?"已使用已有网址，原有标签和备注保持不变":"网址已收藏 · 双击在浏览器打开");
    }catch(Exception error){hint.Text=error.Message;address.Focus();}
   };
   dialog.Loaded+=delegate{address.Focus();address.SelectAll();};dialog.ShowDialog();
  }
 }
}
