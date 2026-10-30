using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;

namespace Mori {
 public partial class MainWindow {
  void ShowBookmarkImport(){
   if(canvasProjectsMode||busy||!PrepareLibraryChange())return;
   var dialog=new Window{Owner=Window,Title="导入书签",Width=460,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner};Theme.Attach(dialog,Window);
   var panel=new StackPanel{Margin=new Thickness(24)};dialog.Content=panel;panel.Children.Add(new TextBlock{Text="从浏览器导入",FontSize=19,Margin=new Thickness(0,0,0,16)});
   var sources=BookmarkFileImport.Sources(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));var choice=new ComboBox{ItemsSource=sources,SelectedIndex=sources.Count>0?0:-1};System.Windows.Automation.AutomationProperties.SetName(choice,"书签来源");panel.Children.Add(choice);
   panel.Children.Add(new TextBlock{Text=sources.Count==0?"没有找到本机书签，可选择浏览器导出的 HTML 文件。":"保留文件夹，重复网址自动跳过。\n只复制到 Nook，不会修改浏览器书签。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,14,0,18)});
   var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};var file=new Button{Content="选择书签文件…"};var import=new Button{Content="导入",IsDefault=true,IsEnabled=sources.Count>0};buttons.Children.Add(file);buttons.Children.Add(import);panel.Children.Add(buttons);
   import.Click+=delegate{var source=choice.SelectedItem as BookmarkSource;if(source!=null){dialog.Close();ImportBookmarkFile(source.Path,source.Name);}};
   file.Click+=delegate{var open=new Microsoft.Win32.OpenFileDialog{Title="选择浏览器书签文件",Filter="浏览器书签|*.html;*.htm;*.json;Bookmarks|所有文件|*.*"};if(open.ShowDialog(dialog)==true){dialog.Close();ImportBookmarkFile(open.FileName,Path.GetFileNameWithoutExtension(open.FileName));}};dialog.ShowDialog();
  }
  async void ImportBookmarkFile(string path,string label){
   if(canvasProjectsMode||busy)return;history.Clear();busy=true;cancel=false;B("Cancel").Visibility=Visibility.Visible;int added=0,reused=0,skipped=0,processed=0;string root="";
   try{Status("正在读取书签…");await Task.Run(delegate{
    var parsed=BookmarkFileImport.Read(path);skipped=parsed.Skipped;
    for(int at=0;at<parsed.Items.Count;at+=40){if(cancel)break;
     var response=BrowserBookmarkImport.Handle(library,new Dictionary<string,object>{{"items",parsed.Items.Skip(at).Take(40).ToArray()},{"root",root},{"rootName",label+" 书签"}});
     var serializer=new JavaScriptSerializer();var values=serializer.Deserialize<Dictionary<string,object>>(serializer.Serialize(response));root=Convert.ToString(values["root"]);added+=Convert.ToInt32(values["added"]);reused+=Convert.ToInt32(values["reused"]);skipped+=Convert.ToInt32(values["skipped"]);processed+=Math.Min(40,parsed.Items.Count-at);
     Window.Dispatcher.Invoke(new Action(()=>Status("已处理 "+processed+" / "+parsed.Items.Count+" · 新增 "+added+" · 已有 "+reused)));
    }
   });bookmarkMode=true;mediaSection="bookmarks";view=root==""?"home":root;similarId=colorId="";T("Search").Clear();selection.Clear();Find<ComboBox>("MediaFilter").SelectedIndex=0;ReloadFolders();Refresh(true);Status((cancel?"已停止":"导入完成")+" · 新增 "+added+" · 已有 "+reused+" · 跳过 "+skipped+"。按原文件夹浏览，浏览器原书签未改变。");
   }catch(Exception error){ReloadFolders();Refresh(true);MessageBox.Show(Window,"已完成的书签会保留，重试不会重复添加。\n"+error.Message,"导入未完成");}
   finally{busy=false;B("Cancel").Visibility=Visibility.Collapsed;UpdateBatchActions();}
  }
 }
}
