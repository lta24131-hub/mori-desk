using System;
using System.Windows;
using System.Windows.Controls;
using Mori;

namespace MoriDesk {
 internal sealed class CanvasSettingsPanel : ScrollViewer {
  internal readonly CheckBox Topmost=new CheckBox{Content="新建画布默认置顶"};
  internal readonly Slider SaveDelay=new Slider{Minimum=.5,Maximum=10,SmallChange=.5,LargeChange=1,TickFrequency=.5,IsSnapToTickEnabled=true},Cache=new Slider{Minimum=16,Maximum=128,SmallChange=16,LargeChange=32,TickFrequency=16,IsSnapToTickEnabled=true};
  internal readonly Button Save=new Button{Content="保存画布设置"},Reset=new Button{Content="恢复默认"};
  readonly TextBlock feedback=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0)};
  readonly Settings loaded;readonly bool preview;readonly Action<Settings> persist;
  internal CanvasSettingsPanel(Window owner,bool preview,Settings initial=null,Action<Settings> persist=null){
   this.preview=preview;loaded=initial??Settings.Load();this.persist=persist??(value=>value.Save());VerticalScrollBarVisibility=ScrollBarVisibility.Auto;HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled;
   var body=new StackPanel{Margin=new Thickness(28,22,28,24)};Content=body;SettingsLayout.Heading(body,"自由画布");SettingsLayout.Row(body,"置顶",Topmost);
   SettingsLayout.Row(body,"自动保存间隔",DelayRange());SettingsLayout.Hint(body,"停止编辑后按此间隔自动保存；关闭窗口时仍会保存。已建项目保留自己的置顶状态。");
   var advanced=new Expander{Header="性能与存储",IsExpanded=false,Margin=new Thickness(0,16,0,20)};var options=new StackPanel{Margin=new Thickness(0,16,0,0)};advanced.Content=options;body.Children.Add(advanced);SettingsLayout.Row(options,"图片预览缓存",SettingsLayout.Range(Cache," MB","图片预览缓存"));SettingsLayout.Hint(options,"这是解码图片的缓存预算，不是软件总内存上限。低值更省内存；高值减少重新加载。按需加载可见图片，不限制画布的图片数量。");
   SettingsLayout.Hint(body,"画布保存独立的图片副本，删除画布内图片不会删除原文件。参数在下次打开画布后生效。");SettingsLayout.Actions(body,Reset,Save);Save.IsEnabled=!preview;Reset.Click+=(s,e)=>{Load(new Settings());feedback.Text="已恢复默认选项，保存后生效。";};Save.Click+=(s,e)=>{try{SaveDraft();}catch(Exception ex){feedback.Text="未保存："+ex.Message;}};feedback.SetResourceReference(TextBlock.ForegroundProperty,"Muted");body.Children.Add(feedback);Load(loaded);if(preview)feedback.Text="测试窗口：不会保存个人设置。";
  }
  FrameworkElement DelayRange(){System.Windows.Automation.AutomationProperties.SetName(SaveDelay,"自动保存间隔");var panel=new DockPanel();var label=new TextBlock{Name="CanvasSaveDelayLabel",Width=58,Text=SaveDelay.Value.ToString("0.0")+" 秒",TextAlignment=TextAlignment.Right,VerticalAlignment=VerticalAlignment.Center};label.SetResourceReference(TextBlock.ForegroundProperty,"Muted");DockPanel.SetDock(label,Dock.Right);panel.Children.Add(label);panel.Children.Add(SaveDelay);SaveDelay.ValueChanged+=(s,e)=>label.Text=SaveDelay.Value.ToString("0.0")+" 秒";return panel;}
  internal void Load(Settings value){Topmost.IsChecked=value.CanvasDefaultTopmost;SaveDelay.Value=value.CanvasSaveDelay/1000.0;Cache.Value=value.CanvasCacheMB;}
  internal bool SaveDraft(){if(preview)return false;loaded.CanvasDefaultTopmost=Topmost.IsChecked==true;loaded.CanvasSaveDelay=(int)Math.Round(SaveDelay.Value*1000);loaded.CanvasCacheMB=(int)Cache.Value;persist(loaded);Load(loaded);feedback.Text="已保存，下次打开画布后生效。";return true;}
 }
}
