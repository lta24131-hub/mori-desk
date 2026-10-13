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
  readonly bool preview;QingJie.CaptureSettingsPanel capture;
  internal StorageChangeRequest StorageRequest;
  internal string Section {get;private set;}
  internal DeskSettingsWindow(Window owner,bool preview,Library library){
   this.preview=preview;Owner=owner;Title="Mori Desk · 设置";Width=690;Height=Math.Min(730,SystemParameters.WorkArea.Height-50);MinWidth=570;MinHeight=440;WindowStartupLocation=WindowStartupLocation.CenterOwner;Theme.Attach(this,owner);
   var root=new DockPanel();root.SetResourceReference(Panel.BackgroundProperty,"Canvas");Content=root;
   var header=new StackPanel{Margin=new Thickness(28,22,28,16)};DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);
   header.Children.Add(new TextBlock{Text="设置",FontSize=24,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,18)});
   var tabs=new StackPanel{Orientation=Orientation.Horizontal};header.Children.Add(tabs);
   foreach(var item in new[]{new[]{"general","通用"},new[]{"capture","截图与翻译"},new[]{"browser","浏览器收藏"}}){
    string key=item[0];var button=new Button{Content=item[1],Padding=new Thickness(18,9,18,9),Margin=new Thickness(0,0,8,0)};button.Click+=(s,e)=>Select(key);tabs.Children.Add(button);navigation.Add(key,button);
   }
   var divider=new Border{Height=1};divider.SetResourceReference(Border.BackgroundProperty,"Divider");DockPanel.SetDock(divider,Dock.Top);root.Children.Add(divider);
   root.Children.Add(body);
   pages.Add("general",Mori.SettingsWindow.CreateContent(this,preview,library,request=>{if(request!=null){StorageRequest=request;Close();}},true));
   Closed+=(s,e)=>{if(capture!=null)capture.Dispose();};
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
    else if(DeskRuntime.Started){capture=new QingJie.CaptureSettingsPanel(true);page=capture;ThemeCapture(page);}
    else {page=new TextBlock{Text="截图工具正在由另一个 Mori Desk 窗口使用。该窗口退出后，请重新打开设置。",Margin=new Thickness(28),TextWrapping=TextWrapping.Wrap};}
    pages[section]=page;
   }
   body.Content=page;Section=section;
   foreach(var pair in navigation){pair.Value.SetResourceReference(Button.BackgroundProperty,pair.Key==section?"AccentSoft":"Canvas");pair.Value.SetResourceReference(Button.ForegroundProperty,pair.Key==section?"Accent":"Muted");}
  }
  // Adapt reusable capture controls to the library's current theme. Download
  // state and event handlers stay in the original panel, not a second window.
  void ThemeCapture(DependencyObject node){
   var element=node as FrameworkElement;
   if(element!=null){
    var button=element as Button;if(button!=null){button.Style=(Style)FindResource(typeof(Button));button.SetResourceReference(Control.BackgroundProperty,"Control");button.SetResourceReference(Control.ForegroundProperty,"Text");}
    foreach(var property in new[]{Control.ForegroundProperty,Control.BackgroundProperty,Control.BorderBrushProperty,TextBlock.ForegroundProperty,Border.BackgroundProperty,Border.BorderBrushProperty}){
     var brush=element.ReadLocalValue(property) as SolidColorBrush;if(brush==null)continue;
     string color=brush.Color.ToString(),key=null;
     if(color=="#FF303639")key="Text";
     else if(color=="#FF718078")key="Muted";
     else if(color=="#FF07A56B")key="Accent";
     else if(color=="#FFF3F6F4")key="Input";
     else if(color=="#FFEBEFED"||color=="#FFD9DFDF")key="Divider";
     else if(color=="#FFFFFFFF")key=property==Control.ForegroundProperty?"Text":"Control";
     if(key!=null)element.SetResourceReference(property,key);
    }
   }
   foreach(object child in LogicalTreeHelper.GetChildren(node)){var dependency=child as DependencyObject;if(dependency!=null)ThemeCapture(dependency);}
  }
 }
}
