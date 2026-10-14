using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Forms=System.Windows.Forms;

namespace Mori {
 public sealed class StorageChangeRequest {
  public string Path {get;set;}
  public bool Migrate {get;set;}
 }
 public static class SettingsWindow {
  internal static StorageChangeRequest CreateStorageRequest(string currentRoot,string requestedPath,bool preview){
   if(preview)return null;
   string target=(requestedPath??"").Trim();
   if(string.IsNullOrWhiteSpace(target))throw new IOException("请选择或填写新的图库保存位置。");
   if(LibraryTransfer.SameRoot(currentRoot,target))return null;
   LibraryTransfer.ValidateDestination(currentRoot,target);
   return new StorageChangeRequest{Path=System.IO.Path.GetFullPath(target),Migrate=true};
  }
  static bool LocationChanged(string currentRoot,string requestedPath){try{return !LibraryTransfer.SameRoot(currentRoot,(requestedPath??"").Trim());}catch{return true;}}
  internal static void ApplyGeneralPreferences(Settings current,bool originalResident,bool resident,bool originalConfirm,bool confirm){
   if(originalResident!=resident)current.CloseToTray=resident;
   if(originalConfirm!=confirm)current.ConfirmTrash=confirm;
  }
  public static StorageChangeRequest Show(Window owner,bool preview,Library library){
   var window=new Window{Owner=owner,Title="Nook · 设置",Width=580,MaxHeight=Math.Max(400,SystemParameters.WorkArea.Height-70),SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner};Theme.Attach(window,owner);
   StorageChangeRequest request=null;window.Content=CreateContent(window,preview,library,value=>{request=value;window.Close();},false);window.ShowDialog();return request;
  }
  internal static FrameworkElement CreateContent(Window window,bool preview,Library library,Action<StorageChangeRequest> onSave,bool embedded){
   if(library==null)throw new ArgumentNullException("library");
   var settings=Settings.Load();bool originalResident=settings.CloseToTray,originalConfirm=settings.ConfirmTrash;string before=Theme.Current,originalTheme=before;string exe=Assembly.GetExecutingAssembly().Location;bool originalStartup=StartupRegistration.IsEnabled(exe);
   var stack=new StackPanel{Margin=new Thickness(28)};var content=new ScrollViewer{Content=stack,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
   stack.Children.Add(new TextBlock{Text="默认保存位置",FontSize=18,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,10)});
   var current=new TextBlock{Text="当前图库："+library.Root,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};current.SetResourceReference(TextBlock.ForegroundProperty,"Muted");stack.Children.Add(current);
   var locationRow=new Grid{Margin=new Thickness(0,0,0,12)};locationRow.ColumnDefinitions.Add(new ColumnDefinition());locationRow.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});stack.Children.Add(locationRow);
   var location=new TextBox{Text=library.Root,ToolTip="填写新文件夹的完整路径，或选择一个空文件夹。",VerticalContentAlignment=VerticalAlignment.Center};System.Windows.Automation.AutomationProperties.SetName(location,"默认保存位置");locationRow.Children.Add(location);
   var browse=new Button{Content="选择文件夹…",Margin=new Thickness(10,0,0,0)};Grid.SetColumn(browse,1);locationRow.Children.Add(browse);
   var storageHint=new TextBlock{Text="更改位置会整体复制原图、分类、标签及备注。复制并校验成功后才切换默认保存位置；原图库保留在原处。请选择空文件夹，或填写一个尚未创建的文件夹路径。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,26),LineHeight=22};storageHint.SetResourceReference(TextBlock.ForegroundProperty,"Muted");stack.Children.Add(storageHint);
   browse.Click+=delegate{try{using(var picker=new Forms.FolderBrowserDialog{Description="选择空文件夹迁移整个图库；原图库会保留。",ShowNewFolderButton=false,SelectedPath=Directory.Exists(location.Text)?location.Text:library.Root}){if(picker.ShowDialog(new DialogHandle(window))!=Forms.DialogResult.OK)return;CreateStorageRequest(library.Root,picker.SelectedPath,false);location.Text=picker.SelectedPath;}}catch(Exception ex){MessageBox.Show(window,ex.Message,"无法使用此保存位置");}};
   ComboBox theme=null;
   if(!embedded){
    stack.Children.Add(new TextBlock{Text="外观",FontSize=18,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,14)});
    theme=new ComboBox{Margin=new Thickness(0,0,0,26)};theme.Items.Add("浅色");theme.Items.Add("深色");theme.SelectedIndex=before=="light"?0:1;stack.Children.Add(theme);
    System.Windows.Automation.AutomationProperties.SetName(theme,"界面主题");theme.SelectionChanged+=delegate{Theme.Set(theme.SelectedIndex==0?"light":"dark");};
   }
   stack.Children.Add(new TextBlock{Text="启动",FontSize=18,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,14)});
   var startup=new CheckBox{Content="登录 Windows 后在托盘启动 Nook",Margin=new Thickness(0,0,0,10),IsChecked=originalStartup,IsEnabled=!preview};stack.Children.Add(startup);
   var resident=new CheckBox{Content="关闭主窗口时收进托盘",IsChecked=settings.CloseToTray,Margin=new Thickness(0,0,0,10)};stack.Children.Add(resident);
   var confirmTrash=new CheckBox{Content="移到回收站 / 移除快捷收藏前确认",IsChecked=settings.ConfirmTrash,Margin=new Thickness(0,0,0,10)};stack.Children.Add(confirmTrash);
   var hint=new TextBlock{Text=preview?"测试窗口：可以预览设置，不会保存个人设置或迁移图库。":"默认关闭；只为当前 Windows 用户设置。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,26)};hint.SetResourceReference(TextBlock.ForegroundProperty,"Muted");stack.Children.Add(hint);
   var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};stack.Children.Add(buttons);
   var feedback=new TextBlock{Margin=new Thickness(0,12,0,0),TextWrapping=TextWrapping.Wrap};feedback.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
   var cancel=new Button{Content=embedded?"还原未保存更改":preview?"关闭":"取消",IsCancel=!embedded};cancel.Click+=delegate{if(!embedded){window.Close();return;}location.Text=library.Root;if(theme!=null)theme.SelectedIndex=before=="light"?0:1;originalStartup=StartupRegistration.IsEnabled(exe);startup.IsChecked=originalStartup;var currentSettings=Settings.Load();originalResident=currentSettings.CloseToTray;originalConfirm=currentSettings.ConfirmTrash;resident.IsChecked=originalResident;confirmTrash.IsChecked=originalConfirm;feedback.Text="已还原未保存的更改。";};buttons.Children.Add(cancel);
   var save=new Button{Content="保存设置",IsDefault=true,IsEnabled=!preview,Margin=new Thickness(0)};buttons.Children.Add(save);
   location.TextChanged+=delegate{save.Content=LocationChanged(library.Root,location.Text)?"保存并迁移图库":"保存设置";};
   save.Click+=delegate{bool changed=false,wasEnabled=false;try{var selected=CreateStorageRequest(library.Root,location.Text,preview);if(!preview){settings=Settings.Load();wasEnabled=StartupRegistration.IsEnabled(exe);if(originalStartup!=(startup.IsChecked==true)&&wasEnabled!=(startup.IsChecked==true)){StartupRegistration.SetEnabled(startup.IsChecked==true,exe);changed=true;}string chosenTheme=theme==null?originalTheme:theme.SelectedIndex==0?"light":"dark";if(chosenTheme!=originalTheme)settings.Theme=chosenTheme;ApplyGeneralPreferences(settings,originalResident,resident.IsChecked==true,originalConfirm,confirmTrash.IsChecked==true);settings.Save();originalResident=settings.CloseToTray;originalConfirm=settings.ConfirmTrash;resident.IsChecked=originalResident;confirmTrash.IsChecked=originalConfirm;originalStartup=StartupRegistration.IsEnabled(exe);startup.IsChecked=originalStartup;originalTheme=settings.Theme;}before=Theme.Current;feedback.Text="已保存设置。";onSave(selected);}catch(Exception ex){string message=ex.Message;if(changed)try{StartupRegistration.SetEnabled(wasEnabled,exe);}catch(Exception rollback){message+="\n开机启动恢复失败，请检查该选项："+rollback.Message;}MessageBox.Show(window,message,"设置未保存");}};
   stack.Children.Add(feedback);if(!embedded)window.Closed+=delegate{Theme.Set(before);};return content;
  }
  sealed class DialogHandle:Forms.IWin32Window {readonly Window window;public DialogHandle(Window value){window=value;}public IntPtr Handle{get{return new WindowInteropHelper(window).Handle;}}}
 }
}
