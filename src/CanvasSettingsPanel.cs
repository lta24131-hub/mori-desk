using System;
using System.Windows;
using System.Windows.Controls;
using Mori;

namespace MoriDesk {
 internal sealed class CanvasSettingsPanel : ScrollViewer {
  internal readonly CheckBox Topmost=new CheckBox{Content="新建画布默认置顶"};
  internal readonly CheckBox Grid=new CheckBox{Content="新建画布显示网格"},ShowTools=new CheckBox{Content="新建画布显示工具栏"},Snap=new CheckBox{Content="拖动时自动对齐图形"};
  internal readonly ComboBox Dash=new ComboBox(),SizeChoice=new ComboBox();
  internal readonly Slider ZoomSpeed=new Slider{Minimum=.5,Maximum=2,SmallChange=.1,LargeChange=.25,TickFrequency=.1,IsSnapToTickEnabled=true};
  internal readonly Slider SaveDelay=new Slider{Minimum=.5,Maximum=10,SmallChange=.5,LargeChange=1,TickFrequency=.5,IsSnapToTickEnabled=true},Cache=new Slider{Minimum=16,Maximum=128,SmallChange=16,LargeChange=32,TickFrequency=16,IsSnapToTickEnabled=true};
  internal readonly Button Save=new Button{Content="保存画布设置"},Reset=new Button{Content="恢复默认"};
  readonly TextBlock feedback=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0)};
  readonly Settings loaded;readonly bool preview;readonly Action<Settings> persist;
  static readonly string[] dashChoices={"solid","dashed","dotted","draw"},sizeChoices={"s","m","l","xl"};
  internal CanvasSettingsPanel(Window owner,bool preview,Settings initial=null,Action<Settings> persist=null){
   this.preview=preview;loaded=initial??Settings.Load();this.persist=persist??(value=>value.Save());VerticalScrollBarVisibility=ScrollBarVisibility.Auto;HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled;
   var body=new StackPanel{Margin=new Thickness(28,22,28,24)};Content=body;SettingsLayout.Heading(body,"自由画布");SettingsLayout.Row(body,"置顶",Topmost);
   SettingsLayout.Row(body,"自动保存间隔",DelayRange());SettingsLayout.Hint(body,"停止编辑后按此间隔自动保存；关闭窗口时仍会保存。已建项目保留自己的置顶状态。");
   SettingsLayout.Heading(body,"编辑默认选项");SettingsLayout.Row(body,"网格",Grid);SettingsLayout.Row(body,"工具栏",ShowTools);SettingsLayout.Row(body,"图形吸附",Snap);SettingsLayout.Row(body,"缩放速度",ZoomRange());
   foreach(var title in new[]{"实线","虚线","点线","手绘"})Dash.Items.Add(title);foreach(var title in new[]{"小","中","大","特大"})SizeChoice.Items.Add(title);SettingsLayout.Row(body,"默认线条",Dash);SettingsLayout.Row(body,"默认大小",SizeChoice);SettingsLayout.Hint(body,"网格和工具栏仅用于新建画布，已有画布保留自己的状态。默认大小影响新绘制图形的线宽和文字大小，不修改已有内容。");
   var advanced=new Expander{Header="旧版画布缓存",IsExpanded=false,Margin=new Thickness(0,16,0,20)};var options=new StackPanel{Margin=new Thickness(0,16,0,0)};advanced.Content=options;body.Children.Add(advanced);SettingsLayout.Row(options,"图片预览缓存",SettingsLayout.Range(Cache," MB","图片预览缓存"));SettingsLayout.Hint(options,"仅用于旧版画布，不控制当前成熟画布或软件总内存。低值更省缓存空间，高值减少重新加载。");
   SettingsLayout.Hint(body,"画布保存独立的图片副本，删除画布内图片不会删除原文件。参数在下次打开画布后生效。");SettingsLayout.Actions(body,Reset,Save);Save.IsEnabled=!preview;Reset.Click+=(s,e)=>{Load(new Settings());feedback.Text="已恢复默认选项，保存后生效。";};Save.Click+=(s,e)=>{try{SaveDraft();}catch(Exception ex){feedback.Text="未保存："+ex.Message;}};feedback.SetResourceReference(TextBlock.ForegroundProperty,"Muted");body.Children.Add(feedback);Load(loaded);if(preview)feedback.Text="测试窗口：不会保存个人设置。";
  }
  FrameworkElement DelayRange(){System.Windows.Automation.AutomationProperties.SetName(SaveDelay,"自动保存间隔");var panel=new DockPanel();var label=new TextBlock{Name="CanvasSaveDelayLabel",Width=58,Text=SaveDelay.Value.ToString("0.0")+" 秒",TextAlignment=TextAlignment.Right,VerticalAlignment=VerticalAlignment.Center};label.SetResourceReference(TextBlock.ForegroundProperty,"Muted");DockPanel.SetDock(label,Dock.Right);panel.Children.Add(label);panel.Children.Add(SaveDelay);SaveDelay.ValueChanged+=(s,e)=>label.Text=SaveDelay.Value.ToString("0.0")+" 秒";return panel;}
  FrameworkElement ZoomRange(){System.Windows.Automation.AutomationProperties.SetName(ZoomSpeed,"缩放速度");var panel=new DockPanel();var label=new TextBlock{Name="CanvasZoomSpeedLabel",Width=58,Text=ZoomSpeed.Value.ToString("0.0")+"×",TextAlignment=TextAlignment.Right,VerticalAlignment=VerticalAlignment.Center};label.SetResourceReference(TextBlock.ForegroundProperty,"Muted");DockPanel.SetDock(label,Dock.Right);panel.Children.Add(label);panel.Children.Add(ZoomSpeed);ZoomSpeed.ValueChanged+=(s,e)=>label.Text=ZoomSpeed.Value.ToString("0.0")+"×";return panel;}
  internal void Load(Settings value){Topmost.IsChecked=value.CanvasDefaultTopmost;SaveDelay.Value=value.CanvasSaveDelay/1000.0;Cache.Value=value.CanvasCacheMB;Grid.IsChecked=value.CanvasGrid;ShowTools.IsChecked=value.CanvasShowTools;Snap.IsChecked=value.CanvasSnap;ZoomSpeed.Value=value.CanvasZoomSpeed;Dash.SelectedIndex=Array.IndexOf(dashChoices,value.CanvasDash);SizeChoice.SelectedIndex=Array.IndexOf(sizeChoices,value.CanvasSize);}
  internal bool SaveDraft(){if(preview)return false;loaded.CanvasDefaultTopmost=Topmost.IsChecked==true;loaded.CanvasSaveDelay=(int)Math.Round(SaveDelay.Value*1000);loaded.CanvasCacheMB=(int)Cache.Value;loaded.CanvasGrid=Grid.IsChecked==true;loaded.CanvasShowTools=ShowTools.IsChecked==true;loaded.CanvasSnap=Snap.IsChecked==true;loaded.CanvasZoomSpeed=ZoomSpeed.Value;loaded.CanvasDash=Dash.SelectedIndex>=0?dashChoices[Dash.SelectedIndex]:"solid";loaded.CanvasSize=SizeChoice.SelectedIndex>=0?sizeChoices[SizeChoice.SelectedIndex]:"m";persist(loaded);Load(loaded);feedback.Text="已保存，下次打开画布后生效。";return true;}
 }
}
