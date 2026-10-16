using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Mori;

namespace MoriDesk {
 internal sealed class DeskSettingsWindow : Window {
  readonly Dictionary<string,FrameworkElement> pages=new Dictionary<string,FrameworkElement>();
  readonly Dictionary<string,Button> navigation=new Dictionary<string,Button>();
  readonly ContentControl body=new ContentControl{HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Stretch};
  readonly bool preview;QingJie.CaptureSettingsPanel capture;AppearanceSettingsPanel appearance;AppUpdatePanel updates;
  internal StorageChangeRequest StorageRequest;
  internal string Section {get;private set;}
  internal DeskSettingsWindow(Window owner,bool preview,Library library,Action<Settings> applyBrowse=null){
   this.preview=preview;Owner=owner;Title="Nook · 设置";Width=780;Height=Math.Min(740,SystemParameters.WorkArea.Height-50);MinWidth=660;MinHeight=440;WindowStartupLocation=WindowStartupLocation.CenterOwner;Theme.Attach(this,owner);
   var root=new DockPanel();root.SetResourceReference(Panel.BackgroundProperty,"Canvas");Content=root;
   var header=new StackPanel{Margin=new Thickness(24,20,24,16)};DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);
   header.Children.Add(new TextBlock{Text="设置",FontSize=23,FontWeight=FontWeights.SemiBold});
   var divider=new Border{Height=1};divider.SetResourceReference(Border.BackgroundProperty,"Divider");DockPanel.SetDock(divider,Dock.Top);root.Children.Add(divider);
   var menu=new StackPanel{Width=146,Margin=new Thickness(10,16,10,12)};var menuScroll=new ScrollViewer{Content=menu,Width=166,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};DockPanel.SetDock(menuScroll,Dock.Left);root.Children.Add(menuScroll);
   foreach(var item in new[]{new[]{"general","通用与存储"},new[]{"appearance","外观"},new[]{"library","素材浏览"},new[]{"preview","文件预览"},new[]{"capture","截图与翻译"},new[]{"canvas","自由画布"},new[]{"browser","浏览器收藏"},new[]{"about","关于与更新"}}){
    string key=item[0];var button=new Button{Content=item[1],Padding=new Thickness(12,10,12,10),Margin=new Thickness(0,0,0,4),HorizontalContentAlignment=HorizontalAlignment.Left,MinHeight=40};System.Windows.Automation.AutomationProperties.SetName(button,item[1]);button.Click+=(s,e)=>Select(key);menu.Children.Add(button);navigation.Add(key,button);
   }
   var contentBorder=new Border{BorderThickness=new Thickness(1,0,0,0),Child=body};contentBorder.SetResourceReference(Border.BorderBrushProperty,"Divider");root.Children.Add(contentBorder);
   pages.Add("general",Mori.SettingsWindow.CreateContent(this,preview,library,request=>{if(request!=null){StorageRequest=request;Close();}},true));
   pages.Add("library",new BrowseSettingsPanel(this,preview,applyBrowse));
   pages.Add("preview",new PreviewSettingsPanel(this,preview));pages.Add("canvas",new CanvasSettingsPanel(this,preview));
   Closed+=(s,e)=>{if(capture!=null)capture.Dispose();if(appearance!=null)appearance.Dispose();if(updates!=null)updates.Dispose();};
   // Bubble after child controls: Esc first cancels a shortcut recording or
   // closes an expanded selector; a subsequent Esc dismisses settings.
   KeyDown+=(s,e)=>DismissOnEscape(e);Select("general");
  }
  internal void DismissOnEscape(KeyEventArgs e){
   if(e.Handled||e.Key!=Key.Escape||QingJie.AppState.ShortcutRecorder!=null)return;
   for(var node=e.OriginalSource as DependencyObject;node!=null;node=MainWindow.SelectionParent(node)){
    var combo=node as ComboBox;if(combo!=null&&combo.IsDropDownOpen)return;
   }
   Close();e.Handled=true;
  }
  internal void Select(string section){
   if(!navigation.ContainsKey(section))section="general";
   FrameworkElement page;
   if(!pages.TryGetValue(section,out page)){
    if(section=="browser")page=BrowserConnection.CreateContent(this,preview);
    else if(section=="appearance"){appearance=new AppearanceSettingsPanel(this,preview);page=appearance;}
    else if(section=="about"){updates=new AppUpdatePanel(this,preview);page=updates;}
    else if(DeskRuntime.Started){capture=new QingJie.CaptureSettingsPanel(true);page=capture;ThemeCapture(page);}
    else {page=new TextBlock{Text="截图工具正在由另一个 Nook 窗口使用。该窗口退出后，请重新打开设置。",Margin=new Thickness(28),TextWrapping=TextWrapping.Wrap};}
    pages[section]=page;
   }
   body.Content=page;Section=section;
   foreach(var pair in navigation){pair.Value.SetResourceReference(Button.BackgroundProperty,pair.Key==section?"AccentSoft":"Canvas");pair.Value.SetResourceReference(Button.ForegroundProperty,pair.Key==section?"Accent":"Muted");}
  }
  // Adapt reusable capture controls to the library's current theme. Download
  // state and event handlers stay in the original panel, not a second window.
  void ThemeCapture(DependencyObject node){
   // Capture controls bind to the same live tokens as the host, including
   // Primary/OnPrimary. Do not flatten primary actions into neutral buttons.
   QingJie.Ui.ThemeControls(node);
  }
 }
}
