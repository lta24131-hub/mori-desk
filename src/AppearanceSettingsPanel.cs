using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using Mori;

namespace MoriDesk {
 internal sealed class AppearanceSettingsPanel : ScrollViewer, IDisposable {
  internal readonly ComboBox Mode=new ComboBox{Name="AppearanceMode",MinWidth=160,Height=34};
  internal readonly Dictionary<string,Button> AccentButtons=new Dictionary<string,Button>();
  internal readonly Button Save=new Button{Content="保存外观",Name="SaveAppearance"},Reset=new Button{Content="恢复默认",Name="ResetAppearance"},Cancel=new Button{Content="还原未保存更改",Name="CancelAppearance"};
  readonly Dictionary<string,Ellipse> swatches=new Dictionary<string,Ellipse>();
  readonly Settings loadedSettings;
  readonly Action<Settings> persist;
  readonly bool preview;
  readonly Window owner;
  readonly TextBlock feedback=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0),LineHeight=21};
  string selectedAccent,committedMode,committedAccent;
  bool loading,disposed,dirty;
  long previewRevision;

  internal AppearanceSettingsPanel(Window owner,bool preview,Settings initial=null,Action<Settings> persist=null){
   this.owner=owner;this.preview=preview;
   loadedSettings=initial??(preview?new Settings{Theme=Theme.Current,ThemeAccent=Theme.AccentKey}:Settings.Load());this.persist=persist??(settings=>settings.Save());
   committedMode=Theme.Current=="light"?"light":"dark";committedAccent=Theme.AccentKey;
   VerticalScrollBarVisibility=ScrollBarVisibility.Auto;HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled;
   SetResourceReference(BackgroundProperty,"Canvas");SetResourceReference(ForegroundProperty,"Text");
   if(owner!=null){Resources.MergedDictionaries.Add(owner.Resources);FontFamily=owner.FontFamily;FontSize=owner.FontSize;owner.Closed+=OwnerClosed;}
   var body=new StackPanel{Margin=new Thickness(28,22,28,24)};Content=body;
   Heading(body,"外观");
   var hint=new TextBlock{Text="以黑白灰为底色，强调色只用于选中、焦点和重要操作。",TextWrapping=TextWrapping.Wrap,FontSize=12,LineHeight=21,Margin=new Thickness(0,0,0,20)};hint.SetResourceReference(TextBlock.ForegroundProperty,"Muted");body.Children.Add(hint);
   StyleMode(Mode);Mode.Items.Add("浅色");Mode.Items.Add("深色");AutomationProperties.SetName(Mode,"界面明暗");Mode.ToolTip="预览浅色或深色界面，不改变网页、图片或视频内容";
   var modeRow=new Grid{Margin=new Thickness(0,0,0,22)};modeRow.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(104)});modeRow.ColumnDefinitions.Add(new ColumnDefinition());
   var modeLabel=new TextBlock{Text="界面明暗",VerticalAlignment=VerticalAlignment.Center};modeLabel.SetResourceReference(TextBlock.ForegroundProperty,"Muted");modeRow.Children.Add(modeLabel);Grid.SetColumn(Mode,1);modeRow.Children.Add(Mode);body.Children.Add(modeRow);
   Heading(body,"强调色");
   var choices=new UniformGrid{Columns=3,Margin=new Thickness(-3,0,-3,18)};body.Children.Add(choices);
   string[][] options={new[]{"purple","紫"},new[]{"blue","蓝"},new[]{"teal","青"},new[]{"amber","琥珀"},new[]{"rose","玫瑰"},new[]{"mono","灰"}};
   foreach(var option in options){
    string key=option[0];var dot=new Ellipse{Width=14,Height=14,Margin=new Thickness(0,0,9,0),VerticalAlignment=VerticalAlignment.Center};swatches.Add(key,dot);
    var content=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center};content.Children.Add(dot);content.Children.Add(new TextBlock{Text=option[1],VerticalAlignment=VerticalAlignment.Center});
    var button=SmallButton(content);button.Name="AppearanceAccent_"+key;button.Tag=key;button.MinWidth=88;button.Height=42;button.Margin=new Thickness(3,0,3,7);
    AutomationProperties.SetName(button,option[1]+"色强调");button.ToolTip="只预览"+option[1]+"色强调，不保存设置";button.Click+=(s,e)=>SelectAccent(key);choices.Children.Add(button);AccentButtons.Add(key,button);
   }
   var sampleRow=new Grid();sampleRow.ColumnDefinitions.Add(new ColumnDefinition());sampleRow.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
   var sampleText=new StackPanel();var sampleTitle=new TextBlock{Text="文字与控件预览",FontSize=13,Margin=new Thickness(0,0,0,5)};sampleTitle.SetResourceReference(TextBlock.ForegroundProperty,"Text");sampleText.Children.Add(sampleTitle);
   var sampleHint=new TextBlock{Text="正文保持中性色",FontSize=11};sampleHint.SetResourceReference(TextBlock.ForegroundProperty,"Muted");sampleText.Children.Add(sampleHint);sampleRow.Children.Add(sampleText);
   var primaryText=new TextBlock{Text="操作示例",FontSize=12,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center};primaryText.SetResourceReference(TextBlock.ForegroundProperty,"OnPrimary");
   var primary=new Border{Child=primaryText,Padding=new Thickness(14,7,14,7),CornerRadius=new CornerRadius(4),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(16,0,0,0)};primary.SetResourceReference(Border.BackgroundProperty,"Primary");Grid.SetColumn(primary,1);sampleRow.Children.Add(primary);
   var sample=new Border{Child=sampleRow,Padding=new Thickness(16),CornerRadius=new CornerRadius(5),BorderThickness=new Thickness(1),Margin=new Thickness(0,0,0,20)};sample.SetResourceReference(Border.BackgroundProperty,"Surface");sample.SetResourceReference(Border.BorderBrushProperty,"Border");body.Children.Add(sample);
   var actions=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right};body.Children.Add(actions);
   foreach(var button in new[]{Cancel,Reset,Save}){button.Margin=new Thickness(7,0,0,7);button.Padding=new Thickness(12,8,12,8);button.SetResourceReference(Control.BackgroundProperty,"Control");button.SetResourceReference(Control.ForegroundProperty,"Text");button.SetResourceReference(Control.BorderBrushProperty,"Border");AutomationProperties.SetName(button,Convert.ToString(button.Content));actions.Children.Add(button);}
   Save.IsEnabled=!preview;Save.Click+=(s,e)=>{try{SaveDraft();}catch(Exception){/* SaveDraft exposes the failure inline; retain the draft for retry. */}};
   Reset.Click+=(s,e)=>{if(disposed)return;LoadControls("dark","purple");PreviewDraft();feedback.Text="已预览默认外观，点击保存后保留。";};
   Cancel.Click+=(s,e)=>RevertDraft();feedback.SetResourceReference(TextBlock.ForegroundProperty,"Muted");body.Children.Add(feedback);
   LoadControls(loadedSettings.Theme,loadedSettings.ThemeAccent);
   Mode.SelectionChanged+=(s,e)=>{if(!loading&&!disposed)PreviewDraft();};
   if(preview)feedback.Text="测试窗口：可以预览，不能保存个人设置。";
  }

  internal string SelectedAccent {get{return selectedAccent;}}
  internal bool IsDisposed {get{return disposed;}}
  void LoadControls(string mode,string accent){loading=true;Mode.SelectedIndex=mode=="light"?0:1;selectedAccent=Theme.NormalizeAccent(accent);RefreshChoices();loading=false;}
  void RefreshChoices(){
   foreach(var pair in AccentButtons){
    var brush=new SolidColorBrush(Theme.AccentColor(pair.Key,Mode.SelectedIndex==0?"light":"dark"));brush.Freeze();swatches[pair.Key].Fill=brush;
    pair.Value.SetResourceReference(Control.BorderBrushProperty,pair.Key==selectedAccent?"Accent":"Border");
    AutomationProperties.SetHelpText(pair.Value,pair.Key==selectedAccent?"当前选中":"点击预览");
   }
  }
  internal void SelectAccent(string accent){if(disposed)return;selectedAccent=Theme.NormalizeAccent(accent);PreviewDraft();}
  void ApplyAppearance(string mode,string accent){Theme.Apply(new Settings{Theme=mode,ThemeAccent=accent});previewRevision=Theme.Revision;}
  void PreviewDraft(){
   string mode=Mode.SelectedIndex==0?"light":"dark";ApplyAppearance(mode,selectedAccent);RefreshChoices();dirty=mode!=committedMode||selectedAccent!=committedAccent;
   feedback.Text=preview?"正在预览，不会保存个人设置。":"正在预览，点击保存后保留；关闭设置会还原未保存的更改。";
  }
  internal bool SaveDraft(){
   if(preview||disposed)return false;
   string oldMode=loadedSettings.Theme,oldAccent=loadedSettings.ThemeAccent;
   loadedSettings.Theme=Mode.SelectedIndex==0?"light":"dark";loadedSettings.ThemeAccent=selectedAccent;
   try{persist(loadedSettings);}
   catch(Exception ex){loadedSettings.Theme=oldMode;loadedSettings.ThemeAccent=oldAccent;feedback.Text="外观未保存，预览仍可调整或重试。\n"+ex.Message;throw;}
   // Settings may have merged a newer untouched appearance field on disk.
   committedMode=loadedSettings.Theme;committedAccent=Theme.NormalizeAccent(loadedSettings.ThemeAccent);dirty=false;LoadControls(committedMode,committedAccent);
   try{ApplyAppearance(committedMode,committedAccent);feedback.Text="已保存外观。";}
   catch(Exception ex){feedback.Text="外观已保存，但当前窗口未能立即更新。重新打开后生效。\n"+ex.Message;}
   return true;
  }
  internal void RevertDraft(){
   if(disposed)return;
   // A second editor can take over the live theme. Do not overwrite its preview/save.
   if(dirty&&Theme.Revision==previewRevision)ApplyAppearance(committedMode,committedAccent);
   LoadControls(committedMode,committedAccent);dirty=false;feedback.Text="已还原未保存的外观更改。";
  }
  void OwnerClosed(object sender,EventArgs args){Dispose();}
  public void Dispose(){if(disposed)return;RevertDraft();disposed=true;if(owner!=null)owner.Closed-=OwnerClosed;Save.IsEnabled=Reset.IsEnabled=Cancel.IsEnabled=Mode.IsEnabled=false;foreach(var button in AccentButtons.Values)button.IsEnabled=false;}
  static void Heading(Panel parent,string text){var label=new TextBlock{Text=text,FontSize=16,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,14)};label.SetResourceReference(TextBlock.ForegroundProperty,"Text");parent.Children.Add(label);}
  static void StyleMode(ComboBox mode){
   mode.SetResourceReference(Control.BackgroundProperty,"Control");mode.SetResourceReference(Control.ForegroundProperty,"Text");mode.SetResourceReference(Control.BorderBrushProperty,"Border");mode.FocusVisualStyle=null;
   // Own every painted surface: the system ComboBox template can retain a white face in standalone dark previews.
   mode.Template=(ControlTemplate)XamlReader.Parse(
    "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ComboBox'>"+
    "<Grid><Border x:Name='ModeSurface' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='4'/>"+
    "<ToggleButton x:Name='ModeToggle' Focusable='False' IsChecked='{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}'><ToggleButton.Template><ControlTemplate TargetType='ToggleButton'><Border Background='Transparent' CornerRadius='4'/></ControlTemplate></ToggleButton.Template></ToggleButton>"+
    "<ContentPresenter x:Name='ModeSelection' Margin='10,5,27,5' Content='{TemplateBinding SelectionBoxItem}' ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}' ContentTemplateSelector='{TemplateBinding ItemTemplateSelector}' TextElement.Foreground='{TemplateBinding Foreground}' VerticalAlignment='Center' IsHitTestVisible='False'/>"+
    "<TextBlock x:Name='ModeArrow' FontFamily='Segoe MDL2 Assets' Text='&#xE70D;' Foreground='{TemplateBinding Foreground}' FontSize='9' HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,10,0' IsHitTestVisible='False'/>"+
    "<Popup x:Name='PART_Popup' IsOpen='{TemplateBinding IsDropDownOpen}' Placement='Bottom' PlacementTarget='{Binding RelativeSource={RelativeSource TemplatedParent}}' AllowsTransparency='True' Focusable='False'>"+
    "<Border x:Name='ModePopupSurface' Background='{DynamicResource Control}' BorderBrush='{DynamicResource Border}' BorderThickness='1' CornerRadius='4' MinWidth='{TemplateBinding ActualWidth}' TextElement.Foreground='{DynamicResource Text}'>"+
    "<ScrollViewer x:Name='ModePopupScroll' Background='{DynamicResource Control}' MaxHeight='{TemplateBinding MaxDropDownHeight}' CanContentScroll='True' HorizontalScrollBarVisibility='Disabled' VerticalScrollBarVisibility='Auto'><StackPanel x:Name='ModeItems' Background='{DynamicResource Control}' IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Contained'/></ScrollViewer>"+
    "</Border></Popup></Grid><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='ModeSurface' Property='Background' Value='{DynamicResource Hover}'/></Trigger>"+
    "<Trigger Property='IsKeyboardFocusWithin' Value='True'><Setter TargetName='ModeSurface' Property='BorderBrush' Value='{DynamicResource Accent}'/></Trigger><Trigger Property='IsDropDownOpen' Value='True'><Setter TargetName='ModeSurface' Property='BorderBrush' Value='{DynamicResource Accent}'/></Trigger>"+
    "<Trigger Property='IsEnabled' Value='False'><Setter TargetName='ModeSurface' Property='Opacity' Value='0.45'/><Setter TargetName='ModeSelection' Property='Opacity' Value='0.45'/><Setter TargetName='ModeArrow' Property='Opacity' Value='0.45'/></Trigger></ControlTemplate.Triggers></ControlTemplate>");
   mode.ItemContainerStyle=(Style)XamlReader.Parse(
    "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ComboBoxItem'><Setter Property='Background' Value='{DynamicResource Control}'/><Setter Property='Foreground' Value='{DynamicResource Text}'/><Setter Property='Padding' Value='10,8'/><Setter Property='FocusVisualStyle' Value='{x:Null}'/>"+
    "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ComboBoxItem'><Border x:Name='ChoiceSurface' Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}'><ContentPresenter x:Name='ChoiceText' TextElement.Foreground='{TemplateBinding Foreground}'/></Border>"+
    "<ControlTemplate.Triggers><Trigger Property='IsHighlighted' Value='True'><Setter TargetName='ChoiceSurface' Property='Background' Value='{DynamicResource Hover}'/></Trigger><Trigger Property='IsSelected' Value='True'><Setter TargetName='ChoiceSurface' Property='Background' Value='{DynamicResource AccentSoft}'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>");
  }
  static Button SmallButton(object content){
   var button=new Button{Content=content,Padding=new Thickness(8,6,8,6)};button.SetResourceReference(Control.BackgroundProperty,"Surface");button.SetResourceReference(Control.ForegroundProperty,"Text");
   button.Template=(ControlTemplate)XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'><Border x:Name='Surface' BorderThickness='1' BorderBrush='{TemplateBinding BorderBrush}' Background='{TemplateBinding Background}' CornerRadius='5' Padding='{TemplateBinding Padding}'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Surface' Property='Background' Value='{DynamicResource Hover}'/></Trigger><Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Surface' Property='BorderBrush' Value='{DynamicResource Accent}'/></Trigger><Trigger Property='IsEnabled' Value='False'><Setter TargetName='Surface' Property='Opacity' Value='0.45'/></Trigger></ControlTemplate.Triggers></ControlTemplate>");return button;
  }
 }
}
