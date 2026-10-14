using System;
using System.Windows;
using System.Windows.Controls;
using Mori;

namespace MoriDesk {
 internal sealed class PreviewSettingsPanel : ScrollViewer {
  internal readonly ComboBox BackgroundChoice=new ComboBox();
  internal readonly Slider Volume=new Slider{Minimum=0,Maximum=100,TickFrequency=5,IsSnapToTickEnabled=true,SmallChange=5,LargeChange=10};
  internal readonly CheckBox Animate=new CheckBox{Content="预览时播放动图"},AutoPlay=new CheckBox{Content="打开音视频后自动播放"},Loop=new CheckBox{Content="循环播放"},Muted=new CheckBox{Content="默认静音"},InternalBrowser=new CheckBox{Content="双击网址时在 Nook 内快速浏览"};
  internal readonly Button Save=new Button{Content="保存预览设置"},Reset=new Button{Content="恢复默认"};
  readonly TextBlock feedback=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0)};
  readonly Settings loaded;readonly bool preview;readonly Action<Settings> persist;
  static readonly string[] backgrounds={"automatic","light-checker","dark-checker","white","dark"};
  internal PreviewSettingsPanel(Window owner,bool preview,Settings initial=null,Action<Settings> persist=null){
   this.preview=preview;loaded=initial??Settings.Load();this.persist=persist??(value=>value.Save());VerticalScrollBarVisibility=ScrollBarVisibility.Auto;HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled;
   var body=new StackPanel{Margin=new Thickness(28,22,28,24)};Content=body;
   SettingsLayout.Heading(body,"图片");foreach(var title in new[]{"自动（透明底纹）","浅色底纹","深色底纹","纯白","深灰"})BackgroundChoice.Items.Add(title);
   SettingsLayout.Row(body,"透明图片背景",BackgroundChoice);SettingsLayout.Row(body,"动图",Animate);
   SettingsLayout.Heading(body,"视频与音频");SettingsLayout.Row(body,"播放",AutoPlay);SettingsLayout.Row(body,"循环",Loop);SettingsLayout.Row(body,"声音",Muted);SettingsLayout.Row(body,"初始音量",SettingsLayout.Range(Volume,"%","初始音量"));
   SettingsLayout.Heading(body,"网址");SettingsLayout.Row(body,"快速浏览",InternalBrowser);
   SettingsLayout.Hint(body,"保存后对新打开的预览生效，不打断当前播放。图片背景不会写进原图。关闭内置浏览后，双击网址改用默认浏览器；右键始终可选择打开方式。");
   SettingsLayout.Actions(body,Reset,Save);Save.IsEnabled=!preview;Reset.Click+=(s,e)=>{Load(new Settings());feedback.Text="已恢复默认选项，保存后生效。";};Save.Click+=(s,e)=>{try{SaveDraft();}catch(Exception ex){feedback.Text="未保存："+ex.Message;}};
   feedback.SetResourceReference(TextBlock.ForegroundProperty,"Muted");body.Children.Add(feedback);Load(loaded);if(preview)feedback.Text="测试窗口：不会保存个人设置。";
  }
  internal void Load(Settings value){BackgroundChoice.SelectedIndex=Array.IndexOf(backgrounds,value.PreviewBackdrop);Animate.IsChecked=value.AnimateImages;AutoPlay.IsChecked=value.MediaAutoPlay;Loop.IsChecked=value.MediaLoop;Muted.IsChecked=value.MediaMuted;Volume.Value=value.MediaVolume;InternalBrowser.IsChecked=value.BookmarkOpenInApp;}
  internal bool SaveDraft(){if(preview)return false;loaded.PreviewBackdrop=BackgroundChoice.SelectedIndex>=0?backgrounds[BackgroundChoice.SelectedIndex]:"automatic";loaded.AnimateImages=Animate.IsChecked==true;loaded.MediaAutoPlay=AutoPlay.IsChecked==true;loaded.MediaLoop=Loop.IsChecked==true;loaded.MediaMuted=Muted.IsChecked==true;loaded.MediaVolume=(int)Volume.Value;loaded.BookmarkOpenInApp=InternalBrowser.IsChecked==true;persist(loaded);Load(loaded);feedback.Text="已保存，对新打开的预览生效。";return true;}
 }
 internal static class SettingsLayout {
  internal static void Heading(Panel parent,string title){parent.Children.Add(new TextBlock{Text=title,FontSize=16,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,16)});}
  internal static void Row(Panel parent,string label,FrameworkElement control){var row=new Grid{Margin=new Thickness(0,0,0,14)};row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(126)});row.ColumnDefinitions.Add(new ColumnDefinition());var text=new TextBlock{Text=label,VerticalAlignment=VerticalAlignment.Center,FontSize=12};text.SetResourceReference(TextBlock.ForegroundProperty,"Muted");row.Children.Add(text);Grid.SetColumn(control,1);row.Children.Add(control);parent.Children.Add(row);System.Windows.Automation.AutomationProperties.SetName(control,label);}
  internal static FrameworkElement Range(Slider slider,string suffix,string name){System.Windows.Automation.AutomationProperties.SetName(slider,name);var panel=new DockPanel();var label=new TextBlock{Width=58,Text=((int)slider.Value)+suffix,TextAlignment=TextAlignment.Right,VerticalAlignment=VerticalAlignment.Center};label.SetResourceReference(TextBlock.ForegroundProperty,"Muted");DockPanel.SetDock(label,Dock.Right);panel.Children.Add(label);panel.Children.Add(slider);slider.ValueChanged+=(s,e)=>label.Text=((int)slider.Value)+suffix;return panel;}
  internal static void Hint(Panel parent,string value){var hint=new TextBlock{Text=value,TextWrapping=TextWrapping.Wrap,LineHeight=21,FontSize=11,Margin=new Thickness(0,10,0,20)};hint.SetResourceReference(TextBlock.ForegroundProperty,"Muted");parent.Children.Add(hint);}
  internal static StackPanel Actions(Panel parent,Button reset,Button save){var panel=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};reset.Margin=new Thickness(0,0,8,0);save.Margin=new Thickness(0);panel.Children.Add(reset);panel.Children.Add(save);parent.Children.Add(panel);return panel;}
 }
}
