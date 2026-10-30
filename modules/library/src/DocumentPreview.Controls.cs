using System;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Mori {
 internal sealed partial class DocumentPreviewWindow {
  TextBox pageInput;
  Button zoomButton,sidebarButton;
  Border pdfToolbar;
  PdfThumbnailPane thumbnailPane;
  bool focusMode,sidebarVisible;
  ContextMenu pdfMore,zoomMenu;
  void OpenDefault(){try{MoriDesk.FileRouter.OpenDefault(this,file);}catch(Exception ex){notice.Text=ex.Message;}}
  void Reveal(){try{MoriDesk.FileRouter.Reveal(file);}catch(Exception ex){notice.Text=ex.Message;}}
  static void CompatibleChrome(Visual visual){var source=PresentationSource.FromVisual(visual)as System.Windows.Interop.HwndSource;if(source!=null&&source.CompositionTarget!=null)source.CompositionTarget.RenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;}
  Button PdfTool(string name,string icon,string hint,Action action){
   var b=new Button{Name=name,Width=32,Height=32,Padding=new Thickness(0),Margin=new Thickness(1,0,1,0),ToolTip=hint};
   b.Content=MoriDesk.CanvasIcons.Create(icon,b,18);AutomationProperties.SetName(b,hint.Split('·')[0].Trim());b.Click+=delegate{action();};return b;
  }
  ContextMenu PdfMenu(){var m=Theme.Menu(this);m.Opened+=delegate{CompatibleChrome(m);};return m;}
  static MenuItem PdfItem(string label,string shortcut,Action action){var item=new MenuItem{Header=label,InputGestureText=shortcut};item.Click+=delegate{action();};return item;}
  static void PopMenu(Button b,ContextMenu m){m.PlacementTarget=b;m.Placement=PlacementMode.Bottom;m.IsOpen=true;}
  void BuildPdfToolbar(){
   Resources[typeof(Button)]=PdfButtonStyle();
   SourceInitialized+=delegate{CompatibleChrome(this);};
   var bar=new Grid{Name="PdfToolbar",Margin=new Thickness(10,6,10,6)};
   bar.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});bar.ColumnDefinitions.Add(new ColumnDefinition());bar.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
   var left=new StackPanel{Orientation=Orientation.Horizontal};bar.Children.Add(left);
   sidebarButton=PdfTool("PdfThumbnails","gallery","页面缩略图",ToggleSidebar);left.Children.Add(sidebarButton);
   previous=PdfTool("PdfPrevious","previous","上一页 · Page Up",()=>Move(-1));next=PdfTool("PdfNext","next","下一页 · Page Down",()=>Move(1));left.Children.Add(previous);
   pageInput=new TextBox{Name="PdfPageNumber",Width=42,Height=28,MaxLength=6,Padding=new Thickness(2),TextAlignment=TextAlignment.Center,VerticalContentAlignment=VerticalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,BorderThickness=new Thickness(0),ToolTip="输入页码并按 Enter · Ctrl+G"};
   pageInput.SetResourceReference(Control.BackgroundProperty,"Control");pageInput.SetResourceReference(Control.ForegroundProperty,"Text");AutomationProperties.SetName(pageInput,"跳转页码");
   pageInput.GotKeyboardFocus+=delegate{pageInput.SelectAll();};pageInput.LostKeyboardFocus+=delegate{pageInput.Text=document!=null&&document.PageCount>0?(index+1).ToString():"—";};
   pageInput.KeyDown+=delegate(object s,KeyEventArgs e){if(e.Key==Key.Enter){CommitPage();e.Handled=true;}};
   left.Children.Add(pageInput);pages.Margin=new Thickness(6,0,4,0);pages.MinWidth=34;pages.FontSize=12;left.Children.Add(pages);left.Children.Add(next);
   var middle=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center};Grid.SetColumn(middle,1);bar.Children.Add(middle);
   middle.Children.Add(PdfTool("PdfZoomOut","minimize","缩小 · Ctrl+滚轮",()=>Zoom(1/1.2)));
   zoomButton=new Button{Name="PdfZoom",Width=58,Height=32,Padding=new Thickness(0),Margin=new Thickness(1,0,1,0),Content="—",ToolTip="缩放比例与适应方式"};AutomationProperties.SetName(zoomButton,"缩放比例与适应方式");middle.Children.Add(zoomButton);
   zoomMenu=PdfMenu();zoomMenu.Items.Add(PdfItem("适应宽度","Ctrl+0",()=>document.FitWidth()));zoomMenu.Items.Add(PdfItem("适应整页","",()=>document.FitPage()));zoomMenu.Items.Add(new Separator());
   foreach(double scale in new[]{.25,.5,.75,1,1.25,1.5,2,3}){double value=scale;zoomMenu.Items.Add(PdfItem((value*100)+"%","",()=>document.SetDisplayZoom(value)));}
   zoomButton.Click+=delegate{PopMenu(zoomButton,zoomMenu);};middle.Children.Add(PdfTool("PdfZoomIn","plus","放大 · Ctrl+滚轮",()=>Zoom(1.2)));middle.Children.Add(PdfTool("PdfFitWidth","fit","适应宽度 · Ctrl+0",()=>document.FitWidth()));
   var right=new StackPanel{Orientation=Orientation.Horizontal};Grid.SetColumn(right,2);bar.Children.Add(right);
   chooseText=PdfTool("PdfTextToggle","text","选择文字 · 选中后复制或翻译",ToggleText);right.Children.Add(chooseText);
   right.Children.Add(PdfTool("PdfFocus","eye","专注阅读 · F11",()=>SetPdfFocus(!focusMode)));
   var more=PdfTool("PdfMore","more","更多",delegate{});right.Children.Add(more);pdfMore=PdfMenu();more.Click+=delegate{PopMenu(more,pdfMore);};
   pdfMore.Items.Add(PdfItem("用默认程序打开","",OpenDefault));pdfMore.Items.Add(PdfItem("文件位置","F3",Reveal));pdfMore.Items.Add(new Separator());pdfMore.Items.Add(PdfItem("页面缩略图","",ToggleSidebar));pdfMore.Items.Add(PdfItem("专注阅读","F11",()=>SetPdfFocus(!focusMode)));
   pdfToolbar=new Border{Child=bar,BorderThickness=new Thickness(0,0,0,1)};pdfToolbar.SetResourceReference(Border.BackgroundProperty,"Panel");pdfToolbar.SetResourceReference(Border.BorderBrushProperty,"Border");DockPanel.SetDock(pdfToolbar,Dock.Top);root.Children.Add(pdfToolbar);
   notice.FontSize=11;notice.Margin=new Thickness(14,5,14,5);notice.TextWrapping=TextWrapping.NoWrap;notice.TextTrimming=TextTrimming.CharacterEllipsis;notice.ToolTip="连续滚动 · 按住鼠标中键拖动 · Ctrl+滚轮缩放 · F11 专注阅读";
  }
  void AttachPdfBody(Grid pagesHost){
   var body=new DockPanel();thumbnailPane=new PdfThumbnailPane(document){Visibility=Visibility.Collapsed};DockPanel.SetDock(thumbnailPane,Dock.Left);body.Children.Add(thumbnailPane);body.Children.Add(pagesHost);root.Children.Add(body);
   var menu=PdfMenu();var tools=PdfItem("隐藏工具栏","F11",()=>SetPdfFocus(!focusMode));menu.Items.Add(tools);menu.Items.Add(PdfItem("适应宽度","Ctrl+0",()=>document.FitWidth()));menu.Items.Add(PdfItem("适应整页","",()=>document.FitPage()));menu.Items.Add(PdfItem("页面缩略图","",ToggleSidebar));menu.Opened+=delegate{tools.Header=focusMode?"显示工具栏":"隐藏工具栏";};document.ContextMenu=menu;
  }
  void CommitPage(){
   int requested;if(document==null||document.PageCount==0)return;
   if(!int.TryParse(pageInput.Text.Trim(),out requested)||requested<1||requested>document.PageCount){notice.Text="请输入 1–"+document.PageCount+" 之间的页码";pageInput.SelectAll();return;}
   document.GoTo(requested-1);document.Scroller.Focus();
  }
  void UpdateZoom(){if(zoomButton!=null&&document!=null)zoomButton.Content=document.PageCount==0?"—":Math.Round(document.DisplayZoom*100)+"%";}
  void ToggleSidebar(){sidebarVisible=!sidebarVisible;if(focusMode)SetPdfFocus(false);ApplySidebar();}
  void ApplySidebar(){if(thumbnailPane==null)return;bool show=sidebarVisible&&!focusMode;thumbnailPane.SetOpen(show);sidebarButton.SetResourceReference(Control.BackgroundProperty,show?"AccentSoft":"Panel");}
  internal void SetPdfFocus(bool value){if(focusMode==value)return;focusMode=value;pdfToolbar.Visibility=value?Visibility.Collapsed:Visibility.Visible;notice.Visibility=value?Visibility.Collapsed:Visibility.Visible;ApplySidebar();document.Scroller.Focus();}
  bool HandlePdfKey(KeyEventArgs e){
   bool control=(Keyboard.Modifiers&ModifierKeys.Control)!=0;
   if(e.Key==Key.F11){SetPdfFocus(!focusMode);e.Handled=true;return true;}
   if(e.Key==Key.Escape&&pageInput.IsKeyboardFocused){pageInput.Text=(index+1).ToString();document.Scroller.Focus();e.Handled=true;return true;}
   if(e.Key==Key.Escape&&focusMode){SetPdfFocus(false);e.Handled=true;return true;}
   if(control&&e.Key==Key.G){SetPdfFocus(false);pageInput.Focus();pageInput.SelectAll();e.Handled=true;return true;}
   if(control&&(e.Key==Key.D0||e.Key==Key.NumPad0)){document.FitWidth();e.Handled=true;return true;}
   if(e.Key==Key.F3){Reveal();e.Handled=true;return true;}return false;
  }
  void SetPdfNotice(string value){
   bool idle=value=="连续滚动"||(!string.IsNullOrEmpty(value)&&(value.StartsWith("连续滚动 ·")||value.StartsWith("AI 已导出页面 ·")));
   notice.Text=idle?(string.Equals(Path.GetExtension(file),".ai",StringComparison.OrdinalIgnoreCase)?"AI · 已保存的画板预览":"PDF · 连续滚动"):value;
   notice.ToolTip=idle?"中键拖动 · Ctrl+滚轮缩放 · Ctrl+G 跳页 · F11 专注阅读":value;
  }
  static Style PdfButtonStyle(){
   var s=ToolbarStyle();s.Setters.Add(new Setter(Control.BackgroundProperty,new DynamicResourceExtension("Panel")));
   var border=new FrameworkElementFactory(typeof(Border));border.Name="Surface";border.SetValue(Border.CornerRadiusProperty,new CornerRadius(6));border.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));border.SetValue(Border.PaddingProperty,new TemplateBindingExtension(Control.PaddingProperty));
   var content=new FrameworkElementFactory(typeof(ContentPresenter));content.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);content.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);border.AppendChild(content);
   var template=new ControlTemplate(typeof(Button)){VisualTree=border};foreach(var property in new[]{UIElement.IsMouseOverProperty,UIElement.IsKeyboardFocusedProperty}){var trigger=new Trigger{Property=property,Value=true};trigger.Setters.Add(new Setter(Border.BackgroundProperty,new DynamicResourceExtension("Hover"),"Surface"));template.Triggers.Add(trigger);}var disabled=new Trigger{Property=UIElement.IsEnabledProperty,Value=false};disabled.Setters.Add(new Setter(UIElement.OpacityProperty,.35,"Surface"));template.Triggers.Add(disabled);s.Setters.Add(new Setter(Control.TemplateProperty,template));return s;
  }
 }
}
