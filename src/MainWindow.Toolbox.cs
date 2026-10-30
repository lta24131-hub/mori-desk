using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using MoriDesk;

namespace Mori {
 // Separate actions must not inherit the legacy hotkey's recover-or-paste
 // fallback. In particular, recovery never invokes the clipboard operation.
 internal static class ToolboxPinActions {
  internal static void Execute(string command,Func<bool> recover,Action paste,Action toggle,Action<string> notify){
   switch(command){
    case "paste-pin":paste();break;
    case "recover-pin":notify(recover()?"已找回最近关闭的贴图。":"没有可找回的贴图。可用“粘贴图片”添加新贴图。");break;
    case "toggle-pins":toggle();break;
    default:throw new ArgumentException("未知的贴图操作。","command");
   }
  }
 }

 // The toolbox presents existing actions; it owns no services, data or windows.
 internal sealed class ToolboxPanel : Border,IDisposable {
  static readonly string[] ThemeKeys={"Surface","Input","Border","Divider","Text","Muted","Accent","AccentSoft","Hover"};
  readonly Action<string> execute;
  readonly TextBlock captureShortcut;
  readonly Style tileStyle,smallButtonStyle;
  Action themeChangedHandler;
  bool disposed;
  internal readonly Button CaptureButton;

  internal ToolboxPanel(Action<string> execute){
   if(execute==null)throw new ArgumentNullException("execute");
   this.execute=execute;
   Name="ToolboxPanel";Width=356;Padding=new Thickness(16);CornerRadius=new CornerRadius(10);BorderThickness=new Thickness(1);
   RefreshThemeResources();
   SetResourceReference(BackgroundProperty,"Surface");SetResourceReference(BorderBrushProperty,"Border");
   KeyboardNavigation.SetTabNavigation(this,KeyboardNavigationMode.Cycle);
   KeyboardNavigation.SetControlTabNavigation(this,KeyboardNavigationMode.Cycle);
   KeyboardNavigation.SetDirectionalNavigation(this,KeyboardNavigationMode.Cycle);
   AutomationProperties.SetName(this,"工具箱");
   tileStyle=ButtonStyle(false);smallButtonStyle=ButtonStyle(true);
   var content=new StackPanel();Child=content;
   content.Children.Add(Heading("截图与贴图"));
   var captureRow=Row();content.Children.Add(captureRow);
   CaptureButton=Tile("ToolboxCapture","截图","\uE722","capture","开始截图、标注或识别文字");
   captureShortcut=new TextBlock{FontSize=10,TextAlignment=TextAlignment.Center,Margin=new Thickness(0,5,0,0),Visibility=Visibility.Collapsed};
   captureShortcut.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
   ((StackPanel)CaptureButton.Content).Children.Add(captureShortcut);
   Add(captureRow,CaptureButton,0);
   Add(captureRow,Tile("ToolboxTranslate","翻译","\uF2B7","translate","打开输入翻译"),1);
   Add(captureRow,PinCard(),2);
   var divider=new Border{Height=1,Margin=new Thickness(0,14,0,12)};divider.SetResourceReference(BackgroundProperty,"Divider");content.Children.Add(divider);
   content.Children.Add(Heading("文件工具"));
   var files=Row();content.Children.Add(files);
   Add(files,Tile("ToolboxConvert","格式转换","\uE8EE","convert","打开格式转换，保留源文件"),0);
   Add(files,Tile("ToolboxViewImage","看图","\uEB9F","image","打开图片预览，不收藏文件"),1);
   Add(files,Tile("ToolboxView3D","3D 查看","\uE7B8","model","打开 3D 模型预览，不收藏文件"),2);
   themeChangedHandler=WatchTheme(this);
  }

  internal void SetCaptureShortcut(string value){captureShortcut.Text=value??"";captureShortcut.Visibility=string.IsNullOrWhiteSpace(value)?Visibility.Collapsed:Visibility.Visible;}

  internal void RefreshThemeResources(){
   if(disposed||Application.Current==null||Dispatcher.HasShutdownStarted)return;
   if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(DispatcherPriority.DataBind,new Action(RefreshThemeResources));return;}
   // A closed Popup is detached from the Window resource tree. Application
   // resource invalidation does not reliably reach an already-resolved value
   // there. Updating this root's resources invalidates its real descendants,
   // including style/template DynamicResources, without fixing any colors.
   var palette=Application.Current.Resources;
   foreach(string key in ThemeKeys){
    if(!palette.Contains(key))continue;
    object value=palette[key];if(!Resources.Contains(key)||!Object.ReferenceEquals(Resources[key],value))Resources[key]=value;
   }
  }
  static Action WatchTheme(ToolboxPanel panel){
   // Keep isolated/discarded panels collectable even if their owner never
   // shows a window. Production also unsubscribes explicitly on Window.Closed.
   var weak=new WeakReference(panel);Action handler=null;
   handler=delegate{var target=weak.Target as ToolboxPanel;if(target==null){Theme.Changed-=handler;return;}target.RefreshThemeResources();};
   Theme.Changed+=handler;return handler;
  }
  public void Dispose(){if(disposed)return;disposed=true;if(themeChangedHandler!=null){Theme.Changed-=themeChangedHandler;themeChangedHandler=null;}}

  static TextBlock Heading(string text){var label=new TextBlock{Text=text,FontSize=12,Margin=new Thickness(0,0,0,8)};label.SetResourceReference(TextBlock.ForegroundProperty,"Muted");return label;}
  static Grid Row(){var row=new Grid();for(int i=0;i<5;i++)row.ColumnDefinitions.Add(new ColumnDefinition{Width=i%2==0?new GridLength(1,GridUnitType.Star):new GridLength(10)});return row;}
  static void Add(Grid row,UIElement item,int column){Grid.SetColumn(item,column*2);row.Children.Add(item);}
  static TextBlock Icon(string glyph,double size){var icon=new TextBlock{Text=glyph,FontFamily=new FontFamily("Segoe MDL2 Assets"),FontSize=size,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};icon.SetResourceReference(TextBlock.ForegroundProperty,"Accent");return icon;}
  Button Tile(string name,string label,string glyph,string command,string help){
   var body=new StackPanel{VerticalAlignment=VerticalAlignment.Center};body.Children.Add(Icon(glyph,24));
   body.Children.Add(new TextBlock{Text=label,FontSize=12,TextAlignment=TextAlignment.Center,Margin=new Thickness(0,8,0,0)});
   var button=ActionButton(name,label,command,help,tileStyle);button.Height=90;button.Content=body;return button;
  }
  Border PinCard(){
   var card=new Border{CornerRadius=new CornerRadius(7),BorderThickness=new Thickness(1),Height=90};card.SetResourceReference(BackgroundProperty,"Input");card.SetResourceReference(BorderBrushProperty,"Border");
   var body=new Grid();body.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});body.RowDefinitions.Add(new RowDefinition{Height=new GridLength(29)});card.Child=body;
   var title=new StackPanel{VerticalAlignment=VerticalAlignment.Center};title.Children.Add(Icon("\uE718",22));
   var label=new TextBlock{Text="桌面贴图",FontSize=12,TextAlignment=TextAlignment.Center,Margin=new Thickness(0,6,0,0)};label.SetResourceReference(TextBlock.ForegroundProperty,"Text");title.Children.Add(label);body.Children.Add(title);
   var actions=new UniformGrid{Columns=3,Margin=new Thickness(3,0,3,3)};Grid.SetRow(actions,1);body.Children.Add(actions);
   actions.Children.Add(PinButton("ToolboxPastePin","粘贴图片","\uE77F","paste-pin","将剪贴板图片钉在桌面"));
   actions.Children.Add(PinButton("ToolboxRecoverPin","找回贴图","\uE81C","recover-pin","找回最近关闭的贴图"));
   actions.Children.Add(PinButton("ToolboxTogglePins","显示 / 隐藏所有贴图","\uE890","toggle-pins","显示或隐藏全部桌面贴图"));
   return card;
  }
  Button PinButton(string name,string label,string glyph,string command,string help){var button=ActionButton(name,label,command,label+" · "+help,smallButtonStyle);button.Content=Icon(glyph,13);button.Padding=new Thickness(0);return button;}
  Button ActionButton(string name,string label,string command,string help,Style style){
   var button=new Button{Name=name,Style=style,ToolTip=help,Focusable=true};
   AutomationProperties.SetName(button,label);AutomationProperties.SetHelpText(button,help);
   ToolTipService.SetShowOnDisabled(button,true);button.Click+=(sender,args)=>{args.Handled=true;if(!disposed)execute(command);};return button;
  }
  static Style ButtonStyle(bool small){
   var style=new Style(typeof(Button));
   style.Setters.Add(new Setter(Control.BackgroundProperty,new DynamicResourceExtension("Input")));
   style.Setters.Add(new Setter(Control.ForegroundProperty,new DynamicResourceExtension("Text")));
   style.Setters.Add(new Setter(Control.BorderBrushProperty,new DynamicResourceExtension("Border")));
   style.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(small?0:1)));
   style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(small?2:8)));
   style.Setters.Add(new Setter(FrameworkElement.MarginProperty,new Thickness(small?1:0)));
   style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Center));
   style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty,VerticalAlignment.Center));
   style.Setters.Add(new Setter(FrameworkElement.CursorProperty,Cursors.Hand));
   var surface=new FrameworkElementFactory(typeof(Border));surface.Name="ToolSurface";
   surface.SetValue(Border.CornerRadiusProperty,new CornerRadius(small?4:7));
   surface.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));
   surface.SetValue(Border.BorderBrushProperty,new TemplateBindingExtension(Control.BorderBrushProperty));
   surface.SetValue(Border.BorderThicknessProperty,new TemplateBindingExtension(Control.BorderThicknessProperty));
   surface.SetValue(Border.PaddingProperty,new TemplateBindingExtension(Control.PaddingProperty));
   var content=new FrameworkElementFactory(typeof(ContentPresenter));
   content.SetValue(FrameworkElement.HorizontalAlignmentProperty,new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
   content.SetValue(FrameworkElement.VerticalAlignmentProperty,new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));surface.AppendChild(content);
   var template=new ControlTemplate(typeof(Button)){VisualTree=surface};
   var hover=new Trigger{Property=UIElement.IsMouseOverProperty,Value=true};hover.Setters.Add(new Setter(Border.BackgroundProperty,new DynamicResourceExtension("Hover"),"ToolSurface"));template.Triggers.Add(hover);
   var focus=new Trigger{Property=UIElement.IsKeyboardFocusedProperty,Value=true};focus.Setters.Add(new Setter(Border.BackgroundProperty,new DynamicResourceExtension("AccentSoft"),"ToolSurface"));focus.Setters.Add(new Setter(Border.BorderBrushProperty,new DynamicResourceExtension("Accent"),"ToolSurface"));focus.Setters.Add(new Setter(Border.BorderThicknessProperty,new Thickness(1),"ToolSurface"));template.Triggers.Add(focus);
   var disabled=new Trigger{Property=UIElement.IsEnabledProperty,Value=false};disabled.Setters.Add(new Setter(UIElement.OpacityProperty,.45));template.Triggers.Add(disabled);
   style.Setters.Add(new Setter(Control.TemplateProperty,template));return style;
  }
 }

 public partial class MainWindow {
  Popup toolboxPopup;ToolboxPanel toolboxPanel;Button toolboxAnchor;bool toolboxClosed,toolboxActionPending;
  internal Popup ToolboxPopup {get{return toolboxPopup;}}
  internal void InitializeToolbox(Button anchor){
   if(anchor==null)throw new ArgumentNullException("anchor");
   if(toolboxPopup!=null){if(Object.ReferenceEquals(toolboxAnchor,anchor))return;throw new InvalidOperationException("工具箱已连接到导航按钮。");}
   toolboxAnchor=anchor;toolboxPanel=new ToolboxPanel(ExecuteToolboxAction);
   toolboxPanel.Resources.MergedDictionaries.Add(Window.Resources);
   toolboxPopup=new Popup{Name="ToolboxPopup",PlacementTarget=anchor,Child=toolboxPanel,Placement=PlacementMode.Custom,AllowsTransparency=true,StaysOpen=false,PopupAnimation=PopupAnimation.None};
   toolboxPopup.CustomPopupPlacementCallback=(popup,target,offset)=>new[]{new CustomPopupPlacement(new Point(target.Width+8,target.Height-popup.Height),PopupPrimaryAxis.Vertical),new CustomPopupPlacement(new Point(-popup.Width-8,target.Height-popup.Height),PopupPrimaryAxis.Vertical)};
   anchor.ToolTip="截图、翻译、桌面贴图和文件工具";AutomationProperties.SetName(anchor,"工具箱");
   anchor.Click+=(sender,args)=>{args.Handled=true;if(toolboxPopup.IsOpen)CloseToolbox(false);else OpenToolbox();};
   toolboxPanel.PreviewKeyDown+=ToolboxKeyDown;Window.PreviewKeyDown+=ToolboxKeyDown;
   toolboxPopup.Opened+=(sender,args)=>Window.Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(()=>{if(!toolboxClosed&&toolboxPopup.IsOpen)toolboxPanel.CaptureButton.Focus();}));
   Window.Deactivated+=(sender,args)=>CloseToolbox(false);
   Window.IsVisibleChanged+=(sender,args)=>{if(!Window.IsVisible)CloseToolbox(false);};
   Window.LocationChanged+=(sender,args)=>CloseToolbox(false);
   Window.SizeChanged+=(sender,args)=>CloseToolbox(false);
   Window.Closing+=(sender,args)=>CloseToolbox(false);
   Window.Closed+=(sender,args)=>{
    toolboxClosed=true;CloseToolbox(false);Window.PreviewKeyDown-=ToolboxKeyDown;toolboxPanel.PreviewKeyDown-=ToolboxKeyDown;
    toolboxPanel.Dispose();toolboxPopup.Child=null;toolboxPopup.PlacementTarget=null;
   };
  }
  internal void OpenToolbox(){
   if(toolboxClosed||toolboxPopup==null||toolboxActionPending||!Window.IsVisible)return;
   toolboxPanel.RefreshThemeResources();
   var shortcuts=QingJie.AppState.Settings==null?null:QingJie.AppState.Settings.Shortcuts;
   toolboxPanel.SetCaptureShortcut(shortcuts==null||shortcuts.Capture==null?null:shortcuts.Capture.ToString());toolboxPopup.IsOpen=true;
  }
  void ToolboxKeyDown(object sender,KeyEventArgs args){if(toolboxPopup!=null&&toolboxPopup.IsOpen&&args.Key==Key.Escape){args.Handled=true;CloseToolbox(true);}}
  void CloseToolbox(bool restoreFocus){if(toolboxPopup==null||!toolboxPopup.IsOpen)return;toolboxPopup.IsOpen=false;if(restoreFocus&&!toolboxClosed&&Window.IsVisible&&Window.IsActive)toolboxAnchor.Focus();}
  void ExecuteToolboxAction(string command){
   if(toolboxClosed||toolboxActionPending)return;
   CloseToolbox(false);toolboxActionPending=true;
   // Run after the popup has closed and its capture/focus has been released.
   // Capture must never photograph the toolbox, and file pickers stay owned
   // by the main window rather than by a transient popup HWND.
   Window.Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>{
    try{
     if(toolboxClosed||!Window.IsVisible)return;
     switch(command){
      case "capture":if(DeskRuntime.RequireTools()){HideToTrayForCapture();QingJie.AppState.BeginCapture();}break;
      case "translate":if(DeskRuntime.RequireTools())QingJie.AppState.ShowTranslator();break;
      case "paste-pin":case "recover-pin":case "toggle-pins":
       if(DeskRuntime.RequireTools())ToolboxPinActions.Execute(command,()=>QingJie.AppState.Pins.Recover(),()=>QingJie.AppState.Paste(false),QingJie.AppState.TogglePins,Status);break;
      case "convert":OpenQuickFileConversion();break;
      case "image":FileRouter.Choose(Window,false);break;
      case "model":FileRouter.Choose(Window,true);break;
     }
    }catch(Exception error){Status("工具未能打开："+error.Message);}
    finally{toolboxActionPending=false;}
   }));
  }
 }
}
