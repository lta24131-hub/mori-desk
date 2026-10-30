using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Mori {
 // One read-only entry point for owned assets and local shortcuts alike.
 internal static class FilePreview {
  internal static void Show(Window owner,string path,string name){
   string ext=Path.GetExtension(path);
   if(FormatPreviewInfo.UsesPagedPreview(path)){new DocumentPreviewWindow(owner,path,name).Show();return;}
   if(MediaTypes.Pictures.Contains(ext)){var viewer=new MoriDesk.ImageBrowser(path){Owner=owner};viewer.SetImageSequence(new[]{path},new[]{name});viewer.Show();return;}
   if(MediaTypes.Videos.Contains(ext)||MediaTypes.Audio.Contains(ext)){PreviewWindow.Video(owner,path,name);return;}
   if(MediaTypes.Documents.Contains(ext)){new DocumentPreviewWindow(owner,path,name).Show();return;}
   throw new NotSupportedException("此格式暂不支持内置预览，请用默认程序打开。");
  }
 }
 internal sealed partial class DocumentPreviewWindow:Window {
  readonly string file;readonly bool pdf;readonly DockPanel root=new DockPanel();
  readonly TextBlock notice=new TextBlock{Margin=new Thickness(14,8,14,10),TextWrapping=TextWrapping.Wrap};
  readonly TextBlock pages=new TextBlock{VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,8,0)};
  Button previous,next;readonly TextBox text;readonly PdfContinuousView document;
  Button chooseText;readonly Border selectionActions;readonly TextBlock selectionCount;
  CancellationTokenSource loading;bool closed;int index,count=1,generation;
  internal DocumentPreviewWindow(Window owner,string path,string name,bool captureMiddleMouse=true){
   file=Path.GetFullPath(path);pdf=FormatPreviewInfo.UsesPagedPreview(file);
   Owner=owner;Title=name+" · Nook";Width=1060;Height=790;MinWidth=540;MinHeight=380;WindowStartupLocation=owner==null?WindowStartupLocation.CenterScreen:WindowStartupLocation.CenterOwner;Theme.Attach(this,owner);Content=root;
   MoriDesk.InitialWindowPlacement.Attach(this);
   // A direct-open viewer has no main-window style dictionary to inherit.
   // Keep the same quiet controls and contrast in both entry points.
   Resources[typeof(Button)]=ToolbarStyle();root.SetResourceReference(Panel.BackgroundProperty,"Canvas");
   pages.SetResourceReference(TextBlock.ForegroundProperty,"Text");notice.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
   if(pdf)BuildPdfToolbar();
   else{var bar=new WrapPanel{Margin=new Thickness(10,8,10,8)};DockPanel.SetDock(bar,Dock.Top);root.Children.Add(bar);bar.Children.Add(Button("用默认程序打开",OpenDefault,"打开原文件；Nook 为默认程序时可选择其他应用"));bar.Children.Add(Button("文件位置",Reveal,"查看本地地址"));}
   DockPanel.SetDock(notice,Dock.Bottom);root.Children.Add(notice);
   if(pdf){
    document=new PdfContinuousView(captureMiddleMouse);document.PageChanged+=delegate(int page,int total){index=page;count=total;UpdateButtons();};document.StatusChanged+=SetPdfNotice;document.ZoomChanged+=UpdateZoom;
    var host=new Grid();host.Children.Add(document);selectionCount=new TextBlock{VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,8,0),FontSize=12};selectionCount.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
    var actions=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(4)};actions.Children.Add(selectionCount);var copy=Button("复制",CopyText,"复制选中文字 · Ctrl+C");copy.Name="PdfCopySelection";actions.Children.Add(copy);var translate=Button("翻译",TranslateText,"翻译选中文字 · 使用当前翻译服务");translate.Name="PdfTranslateSelection";actions.Children.Add(translate);
    selectionActions=new Border{Name="PdfSelectionActions",Child=actions,Visibility=Visibility.Collapsed,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(0,12,28,0),CornerRadius=new CornerRadius(6),BorderThickness=new Thickness(1)};selectionActions.SetResourceReference(Border.BackgroundProperty,"Panel");selectionActions.SetResourceReference(Border.BorderBrushProperty,"Border");host.Children.Add(selectionActions);AttachPdfBody(host);
    document.SelectionChanged+=delegate(string value){selectionActions.Visibility=string.IsNullOrEmpty(value)?Visibility.Collapsed:Visibility.Visible;selectionCount.Text=value.Length+" 字";};
   }
   else{text=new TextBox{IsReadOnly=true,AcceptsReturn=true,AcceptsTab=true,TextWrapping=TextWrapping.NoWrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,BorderThickness=new Thickness(0),Padding=new Thickness(22),FontSize=15};text.SetResourceReference(Control.BackgroundProperty,"Canvas");text.SetResourceReference(Control.ForegroundProperty,"Text");string ext=Path.GetExtension(file).ToLowerInvariant();if(new[]{".csv",".tsv",".json",".xml",".log",".ini",".yaml",".yml"}.Contains(ext))text.FontFamily=new FontFamily("Consolas");else text.TextWrapping=TextWrapping.Wrap;root.Children.Add(text);}
   PreviewKeyDown+=delegate(object s,KeyEventArgs e){if(pdf&&HandlePdfKey(e))return;if(e.Key==Key.Escape){Close();e.Handled=true;}else if(pdf&&!(e.OriginalSource is TextBox)&&(Keyboard.Modifiers&ModifierKeys.Control)!=0&&e.Key==Key.C&&!string.IsNullOrEmpty(document.SelectedText)){CopyText();e.Handled=true;}else if(pdf&&!(e.OriginalSource is TextBox)&&(Keyboard.Modifiers&ModifierKeys.Control)!=0&&e.Key==Key.A&&document.TextSelectionEnabled){document.SelectPageText();e.Handled=true;}else if(pdf&&!(e.OriginalSource is TextBox)&&(e.Key==Key.PageDown||e.Key==Key.PageUp)){Move(e.Key==Key.PageDown?1:-1);e.Handled=true;}};
   Deactivated+=delegate{if(document!=null)document.EndMiddleDrag();};
   Closed+=delegate{closed=true;generation++;if(loading!=null)loading.Cancel();if(thumbnailPane!=null)thumbnailPane.Dispose();if(document!=null)document.Dispose();if(text!=null)text.Clear();};Loaded+=delegate{LoadPage();};UpdateButtons();
  }
  static Style ToolbarStyle(){
   var style=new Style(typeof(Button));style.Setters.Add(new Setter(Control.BackgroundProperty,new DynamicResourceExtension("Control")));style.Setters.Add(new Setter(Control.ForegroundProperty,new DynamicResourceExtension("Text")));style.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(0)));style.Setters.Add(new Setter(FrameworkElement.CursorProperty,Cursors.Hand));
   var border=new FrameworkElementFactory(typeof(Border));border.Name="Surface";border.SetValue(Border.CornerRadiusProperty,new CornerRadius(4));border.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));border.SetValue(Border.PaddingProperty,new TemplateBindingExtension(Control.PaddingProperty));
   var content=new FrameworkElementFactory(typeof(ContentPresenter));content.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);content.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);border.AppendChild(content);
   var template=new ControlTemplate(typeof(Button)){VisualTree=border};var hover=new Trigger{Property=UIElement.IsMouseOverProperty,Value=true};hover.Setters.Add(new Setter(Border.BackgroundProperty,new DynamicResourceExtension("Hover"),"Surface"));template.Triggers.Add(hover);
   var disabled=new Trigger{Property=UIElement.IsEnabledProperty,Value=false};disabled.Setters.Add(new Setter(UIElement.OpacityProperty,.45,"Surface"));template.Triggers.Add(disabled);style.Setters.Add(new Setter(Control.TemplateProperty,template));return style;
  }
  static Button Button(string label,Action action,string hint){var b=new Button{Content=label,ToolTip=hint,Padding=new Thickness(8,5,8,5),Margin=new Thickness(2,0,2,0),MinHeight=30};System.Windows.Automation.AutomationProperties.SetName(b,hint);b.Click+=delegate{action();};return b;}
  static Button IconButton(string label,Action action,string hint){var button=Button(label,action,hint);button.Width=30;button.Padding=new Thickness(0,5,0,5);return button;}
  void UpdateButtons(){if(!pdf)return;bool ready=document!=null&&document.PageCount>0;previous.IsEnabled=ready&&index>0;next.IsEnabled=ready&&index<count-1&&!document.AtBottom;pages.Text=ready?"/ "+count:"/ —";if(pageInput!=null){pageInput.IsEnabled=ready;if(!pageInput.IsKeyboardFocused)pageInput.Text=ready?(index+1).ToString():"—";}if(thumbnailPane!=null)thumbnailPane.UpdatePage(index);UpdateZoom();}
  void Move(int delta){if(document!=null)document.Move(delta);}
  void Zoom(double factor){if(document!=null)document.SetZoom(document.ZoomFactor*factor);}
  void ToggleText(){if(document==null)return;document.SetTextSelection(!document.TextSelectionEnabled);chooseText.ToolTip=document.TextSelectionEnabled?"退出文字选择，恢复页面浏览":"选择 PDF 文字层 · 扫描件仍用截图翻译";chooseText.SetResourceReference(Control.BackgroundProperty,document.TextSelectionEnabled?"AccentSoft":"Panel");}
  void CopyText(){if(document==null||string.IsNullOrEmpty(document.SelectedText))return;try{Clipboard.SetText(document.SelectedText);notice.Text="选中文字已复制。";}catch{notice.Text="剪贴板暂时被占用，请再次点击复制。";}}
  void TranslateText(){if(document==null||string.IsNullOrWhiteSpace(document.SelectedText))return;if(document.SelectedText.Length>5000){notice.Text="每次最多翻译 5000 字，请缩小选区。";return;}new QingJie.WriteTranslationWindow(document.SelectedText,"zh"){Owner=this,WindowStartupLocation=WindowStartupLocation.CenterOwner}.Show();}
  async void LoadPage(){
   if(closed)return;if(loading!=null)loading.Cancel();var pending=new CancellationTokenSource();loading=pending;int stamp=++generation;notice.Text="正在读取本地文件…";
   try{
    if(pdf){await document.OpenAsync(file,pending.Token);if(closed||stamp!=generation)return;count=document.PageCount;SetPdfNotice("连续滚动");UpdateButtons();}
    else{var content=await DocumentContent.OpenAsync(file,pending.Token);if(closed||stamp!=generation)return;text.Text=content.Text;notice.Text=content.Limitation;}
   }catch(OperationCanceledException){}catch(Exception ex){if(!closed&&stamp==generation){notice.Text=PreviewWindow.FailureText(ex);if(text!=null)text.Text="";pages.Text="无法预览";}}
   finally{if(ReferenceEquals(loading,pending))loading=null;pending.Dispose();}
  }
 }
}
