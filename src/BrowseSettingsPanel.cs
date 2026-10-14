using System;
using System.Windows;
using System.Windows.Controls;
using Mori;

namespace MoriDesk {
 internal sealed class BrowseSettingsPanel : ScrollViewer {
  internal readonly ComboBox Home=new ComboBox(),Sort=new ComboBox(),LayoutChoice=new ComboBox();
  internal readonly Slider Size=new Slider{Minimum=120,Maximum=360,SmallChange=10,LargeChange=20,IsSnapToTickEnabled=true,TickFrequency=10};
  internal readonly CheckBox Names=new CheckBox{Content="显示文件名"},Details=new CheckBox{Content="显示尺寸 / 格式"},Left=new CheckBox{Content="显示左侧分类栏"},Right=new CheckBox{Content="显示右侧信息栏"};
  internal readonly Slider LeftWidth=new Slider{Minimum=160,Maximum=480,TickFrequency=10,IsSnapToTickEnabled=true},RightWidth=new Slider{Minimum=240,Maximum=520,TickFrequency=10,IsSnapToTickEnabled=true};
  readonly string[] sections={"assets","pictures","videos","audio","bookmarks","documents","shortcuts"};
  readonly TextBlock feedback=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0)};
  readonly bool preview;readonly Settings loadedSettings;readonly Action<Settings> persist,apply;
  internal readonly Button Reset=new Button{Content="恢复浏览默认",Margin=new Thickness(0,0,8,0)},Save=new Button{Content="保存浏览设置",Margin=new Thickness(0)};
  internal BrowseSettingsPanel(Window owner,bool preview,Action<Settings> apply,Settings initial=null,Action<Settings> persist=null){
   this.preview=preview;this.apply=apply;loadedSettings=initial??Settings.Load();this.persist=persist??(value=>value.Save());
   VerticalScrollBarVisibility=ScrollBarVisibility.Auto;HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled;
   var body=new StackPanel{Margin=new Thickness(28,22,28,24)};Content=body;
   Heading(body,"打开与浏览");foreach(string label in new[]{"全部素材","图片（含动图）","视频","音频","网址","文档","本地收藏"})Home.Items.Add(label);
   foreach(string label in new[]{"最新收藏","最早收藏","名称 A–Z","文件最大"})Sort.Items.Add(label);
   LayoutChoice.Items.Add("自适应 · 保持完整画面");LayoutChoice.Items.Add("瀑布布局 · 保持原始比例");
   Row(body,"打开后进入",Home);Row(body,"默认排序",Sort);Row(body,"缩略图排列",LayoutChoice);Row(body,"缩略图大小",Range(Size," px"));
   var info=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(126,2,0,20)};Names.Margin=new Thickness(0,0,22,0);info.Children.Add(Names);info.Children.Add(Details);body.Children.Add(info);
   Heading(body,"左右栏");Row(body,"分类栏",Left);Row(body,"分类栏宽度",Range(LeftWidth," px"));Row(body,"信息栏",Right);Row(body,"信息栏宽度",Range(RightWidth," px"));
   var hint=new TextBlock{Text="打开页面在下次启动时生效；其他选项保存后立即生效。\n电脑文件直接拖入只收藏位置；导入素材则保存独立副本。",TextWrapping=TextWrapping.Wrap,LineHeight=21,FontSize=11,Margin=new Thickness(0,10,0,20)};hint.SetResourceReference(TextBlock.ForegroundProperty,"Muted");body.Children.Add(hint);
   var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};body.Children.Add(actions);
   actions.Children.Add(Reset);Reset.Click+=(s,e)=>{Load(new Settings());feedback.Text="已恢复默认选项，点击保存后生效；不会改变素材与分类。";};
   Save.IsEnabled=!preview;actions.Children.Add(Save);Save.Click+=(s,e)=>{try{SaveDraft();}catch(Exception error){MessageBox.Show(owner,error.Message,"浏览设置未保存");}};
   feedback.SetResourceReference(TextBlock.ForegroundProperty,"Muted");body.Children.Add(feedback);
   foreach(var item in new[]{Home,Sort,LayoutChoice})System.Windows.Automation.AutomationProperties.SetName(item,item==Home?"打开后进入":item==Sort?"默认排序":"缩略图排列");
   Load(loadedSettings);if(preview)feedback.Text="测试窗口：选项可预览，不会保存个人设置。";
  }
  internal bool SaveDraft(){
   if(preview)return false;
   WriteTo(loadedSettings);persist(loadedSettings);
   // Persistence has committed, including any newer fields merged by Settings.
   // A failed page refresh must not look like a failed save or trigger rollback.
   Load(loadedSettings);
   try{if(apply!=null)apply(loadedSettings);}
   catch(Exception error){feedback.Text="浏览设置已保存，但当前页面未能立即更新。重新打开软件后生效。\n"+error.Message;return true;}
   feedback.Text="已保存浏览设置。";return true;
  }
  internal void Load(Settings value){Home.SelectedIndex=Array.IndexOf(sections,value.StartupSection);Sort.SelectedIndex=value.DefaultSort;LayoutChoice.SelectedIndex=value.WaterfallLayout?1:0;Size.Value=value.ThumbnailSize;Names.IsChecked=value.ShowAssetNames;Details.IsChecked=value.ShowAssetDetails;Left.IsChecked=value.LeftSidebarVisible;Right.IsChecked=value.RightSidebarVisible;LeftWidth.Value=value.LeftSidebarWidth;RightWidth.Value=value.RightSidebarWidth;}
  internal void WriteTo(Settings value){value.StartupSection=Home.SelectedIndex>=0?sections[Home.SelectedIndex]:"assets";value.DefaultSort=Sort.SelectedIndex;value.WaterfallLayout=LayoutChoice.SelectedIndex==1;value.ThumbnailSize=(int)Size.Value;value.ShowAssetNames=Names.IsChecked==true;value.ShowAssetDetails=Details.IsChecked==true;value.LeftSidebarVisible=Left.IsChecked==true;value.RightSidebarVisible=Right.IsChecked==true;value.LeftSidebarWidth=(int)LeftWidth.Value;value.RightSidebarWidth=(int)RightWidth.Value;}
  static FrameworkElement Range(Slider slider,string suffix){var panel=new DockPanel();var label=new TextBlock{Width=56,Text=((int)slider.Value)+suffix,TextAlignment=TextAlignment.Right,VerticalAlignment=VerticalAlignment.Center};label.SetResourceReference(TextBlock.ForegroundProperty,"Muted");DockPanel.SetDock(label,Dock.Right);panel.Children.Add(label);panel.Children.Add(slider);slider.ValueChanged+=(s,e)=>label.Text=((int)slider.Value)+suffix;return panel;}
  static void Heading(Panel parent,string title){parent.Children.Add(new TextBlock{Text=title,FontSize=16,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,16)});}
  static void Row(Panel parent,string label,FrameworkElement control){var row=new Grid{Margin=new Thickness(0,0,0,12)};row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(126)});row.ColumnDefinitions.Add(new ColumnDefinition());var text=new TextBlock{Text=label,VerticalAlignment=VerticalAlignment.Center,FontSize=12};text.SetResourceReference(TextBlock.ForegroundProperty,"Muted");row.Children.Add(text);Grid.SetColumn(control,1);row.Children.Add(control);parent.Children.Add(row);System.Windows.Automation.AutomationProperties.SetName(control,label);var range=control as DockPanel;if(range!=null)foreach(var slider in range.Children)if(slider is Slider)System.Windows.Automation.AutomationProperties.SetName((Slider)slider,label);}
 }
}
