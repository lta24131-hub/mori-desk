using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mori;
using Forms=System.Windows.Forms;

namespace MoriDesk {
 public sealed partial class ImageBrowser {
  readonly TextBlock pageCount=new TextBlock{MinWidth=68,TextAlignment=TextAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
  readonly StackPanel strip=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center};
  readonly List<Button> imageTools=new List<Button>();
  FrameworkElement toolbar,footer,filmstrip;
  Style filmstripTileStyle;
  Button zoomButton,stripButton,focusButton;
  ContextMenu moreMenu,zoomMenu;
  bool focusMode,showFilmstrip=true,showEdgeNavigation=true;
  string focusRestorePath;bool focusRestoreFit;double focusRestoreZoom,focusRestoreX,focusRestoreY;
  Forms.Button edgePrevious,edgeNext;
  int stripVersion;
  string stripIdentity;
  readonly Dictionary<string,BitmapSource> stripCache=new Dictionary<string,BitmapSource>(StringComparer.OrdinalIgnoreCase);
  readonly Queue<string> stripCacheOrder=new Queue<string>();
  static readonly SemaphoreSlim stripSlots=new SemaphoreSlim(2,2);
  string[] providedSequence;
  readonly Dictionary<string,string> providedNames=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  internal void SetImageSequence(string[] paths,string[] names){
   if(paths==null||names==null||paths.Length!=names.Length)throw new ArgumentException("图片列表与名称数量不一致。");
   providedNames.Clear();var ordered=new List<string>();for(int i=0;i<paths.Length;i++){string path=Path.GetFullPath(paths[i]);if(providedNames.ContainsKey(path))continue;ordered.Add(path);providedNames[path]=names[i];}providedSequence=ordered.ToArray();
  }
  string DisplayName(string path){string name;return providedNames.TryGetValue(path,out name)?name:Path.GetFileName(path);}

  Button Tool(string name,string glyph,string hint,Action action){
   var button=new Button{Name=name,Width=34,Height=34,Margin=new Thickness(2,0,2,0),Padding=new Thickness(0),ToolTip=hint,
    Content=new TextBlock{Text=glyph,FontFamily=new FontFamily("Segoe MDL2 Assets"),FontSize=15,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center}};
   string icon=null;switch(name){case "PreviousImage":icon="previous";break;case "NextImage":icon="next";break;case "ZoomOut":icon="minimize";break;case "ZoomIn":icon="plus";break;case "FitImage":icon="fit";break;case "PinToDesktop":icon="pin";break;case "FocusImage":icon="eye";break;case "OpenImage":icon="folder-open";break;case "ImageMore":icon="more";break;case "ToggleThumbnails":icon="gallery";break;}if(icon!=null)button.Content=CanvasIcons.Create(icon,button,18);
   AutomationProperties.SetName(button,hint.Split('·')[0].Trim());button.Click+=delegate{action();};return button;
  }
  Button ImageTool(string name,string glyph,string hint,Action action){var button=Tool(name,glyph,hint,action);imageTools.Add(button);return button;}
  static void Divider(Panel panel){var border=new Border{Width=1,Height=18,Margin=new Thickness(8,0,8,0)};border.SetResourceReference(Border.BackgroundProperty,"Border");panel.Children.Add(border);}
  static void UseCompatibleChrome(Visual visual){var source=PresentationSource.FromVisual(visual)as System.Windows.Interop.HwndSource;if(source!=null&&source.CompositionTarget!=null)source.CompositionTarget.RenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;}
  ContextMenu Menu(){var menu=Theme.Menu(this);menu.SetResourceReference(Control.BackgroundProperty,"Panel");menu.SetResourceReference(Control.ForegroundProperty,"Text");menu.Opened+=delegate{UseCompatibleChrome(menu);};return menu;}
  MenuItem Item(string text,string shortcut,Action action){var item=new MenuItem{Header=text,InputGestureText=shortcut};item.Click+=delegate{action();};return item;}
  static void ShowMenu(Button button,ContextMenu menu){menu.PlacementTarget=button;menu.Placement=PlacementMode.Bottom;menu.IsOpen=true;}

  void InitializeViewerUi(){
   Resources[typeof(Button)]=ViewerButtonStyle();filmstripTileStyle=FilmstripButtonStyle();root.SetResourceReference(Panel.BackgroundProperty,"Canvas");
   var bar=new DockPanel{LastChildFill=true,Margin=new Thickness(10,5,10,5)};
   var left=new StackPanel{Orientation=Orientation.Horizontal};var right=new StackPanel{Orientation=Orientation.Horizontal};
   DockPanel.SetDock(left,Dock.Left);DockPanel.SetDock(right,Dock.Right);bar.Children.Add(left);bar.Children.Add(right);bar.Children.Add(new Border());
   previous=Tool("PreviousImage","\uE76B","上一张 · ←",()=>Step(-1));next=Tool("NextImage","\uE76C","下一张 · →",()=>Step(1));
   left.Children.Add(previous);left.Children.Add(next);Divider(left);
   left.Children.Add(ImageTool("ZoomOut","\uE71F","缩小 · −",()=>ZoomBy(1/1.15)));
   zoomButton=new Button{Name="ImageZoom",Content="—",Width=66,Height=32,Padding=new Thickness(2),Margin=new Thickness(2,0,2,0),ToolTip="缩放比例"};AutomationProperties.SetName(zoomButton,"缩放比例");left.Children.Add(zoomButton);imageTools.Add(zoomButton);
   zoomMenu=Menu();zoomMenu.Items.Add(Item("适应窗口","0",FitImage));foreach(double zoom in new[]{.25,.5,1,2,4}){double value=zoom;zoomMenu.Items.Add(Item((zoom*100)+"%",zoom==1?"1":"",()=>SetZoom(value)));}zoomButton.Click+=delegate{ShowMenu(zoomButton,zoomMenu);};
   left.Children.Add(ImageTool("ZoomIn","\uE710","放大 · +",()=>ZoomBy(1.15)));
   left.Children.Add(ImageTool("FitImage","\uE9A6","适应窗口 · 0",FitImage));
   var actual=new Button{Name="ActualSize",Content="1:1",Width=38,Height=32,Padding=new Thickness(0),Margin=new Thickness(2,0,2,0),ToolTip="原始大小 · 1"};AutomationProperties.SetName(actual,"原始大小");actual.Click+=delegate{SetZoom(1);};left.Children.Add(actual);imageTools.Add(actual);
   pinButton=Tool("PinToDesktop","\uE718","钉在桌面",async()=>await PinCurrentAsync());pinButton.ToolTip="钉在桌面 · 无边框置顶，可移动和缩放；动图钉住当前画面";right.Children.Add(pinButton);
   focusButton=Tool("FocusImage","\uE740","专注看图 · F11",ToggleFocus);right.Children.Add(focusButton);
   right.Children.Add(Tool("OpenImage","\uE8B7","打开图片 · Ctrl+O",OpenDialog));
   var more=Tool("ImageMore","\uE712","更多",delegate{});right.Children.Add(more);moreMenu=Menu();more.Click+=delegate{ShowMenu(more,moreMenu);};
   moreMenu.Items.Add(Item("复制图片","Ctrl+C",CopyCurrent));moreMenu.Items.Add(Item("识别 / 翻译","",Recognize));moreMenu.Items.Add(Item("文件位置","F3",SafeReveal));
   var backdrop=new MenuItem{Header="图片背景"};foreach(PreviewBackground value in Enum.GetValues(typeof(PreviewBackground))){var choice=value;var option=Item(PreviewBackgrounds.Label(choice),"",()=>{backgroundMode=choice;if(picture!=null)picture.BackgroundMode=choice;});option.IsCheckable=true;option.Tag=choice;backdrop.Items.Add(option);}moreMenu.Items.Add(backdrop);
   moreMenu.Items.Add(new Separator());var stripOption=Item("显示缩略图条","T",ToggleFilmstrip);stripOption.IsCheckable=true;moreMenu.Items.Add(stripOption);
   var edgeOption=Item("显示左右翻页图标","",()=>SetEdgeNavigation(!showEdgeNavigation));edgeOption.IsCheckable=true;moreMenu.Items.Add(edgeOption);
   moreMenu.Items.Add(Item("专注看图","F11",ToggleFocus));moreMenu.Opened+=delegate{stripOption.IsChecked=showFilmstrip;edgeOption.IsChecked=showEdgeNavigation;foreach(MenuItem option in backdrop.Items)option.IsChecked=(PreviewBackground)option.Tag==backgroundMode;};
   var shell=new Border{Child=bar};shell.SetResourceReference(Border.BackgroundProperty,"Surface");toolbar=shell;DockPanel.SetDock(shell,Dock.Top);root.Children.Add(shell);
   var bottom=new Grid{Margin=new Thickness(12,0,12,2),Height=28};bottom.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});bottom.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});bottom.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
   stripButton=Tool("ToggleThumbnails","\uE8B9","缩略图条 · T",ToggleFilmstrip);stripButton.Height=26;stripButton.HorizontalAlignment=HorizontalAlignment.Right;Grid.SetColumn(stripButton,2);bottom.Children.Add(stripButton);
   pageCount.FontSize=12;pageCount.SetResourceReference(TextBlock.ForegroundProperty,"Muted");Grid.SetColumn(pageCount,1);bottom.Children.Add(pageCount);
   label.TextTrimming=TextTrimming.CharacterEllipsis;label.VerticalAlignment=VerticalAlignment.Center;label.Margin=new Thickness(2,0,12,0);label.FontSize=12;label.SetResourceReference(TextBlock.ForegroundProperty,"Muted");bottom.Children.Add(label);footer=bottom;DockPanel.SetDock(bottom,Dock.Bottom);root.Children.Add(bottom);
   var stripBorder=new Border{Child=strip,Padding=new Thickness(6,5,6,1)};filmstrip=stripBorder;DockPanel.SetDock(stripBorder,Dock.Bottom);root.Children.Add(stripBorder);
   SizeChanged+=delegate{RefreshFilmstrip();};PreviewKeyDown+=ViewerKeyDown;UpdateInfo();RefreshFilmstrip();
  }
  static Style ViewerButtonStyle(){
   var style=new Style(typeof(Button));style.Setters.Add(new Setter(Control.BackgroundProperty,Brushes.Transparent));style.Setters.Add(new Setter(Control.ForegroundProperty,new DynamicResourceExtension("Text")));style.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(0)));style.Setters.Add(new Setter(FrameworkElement.CursorProperty,Cursors.Hand));
   var border=new FrameworkElementFactory(typeof(Border));border.Name="Surface";border.SetValue(Border.CornerRadiusProperty,new CornerRadius(7));border.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));border.SetValue(Border.BorderBrushProperty,new TemplateBindingExtension(Control.BorderBrushProperty));border.SetValue(Border.BorderThicknessProperty,new TemplateBindingExtension(Control.BorderThicknessProperty));border.SetValue(Border.PaddingProperty,new TemplateBindingExtension(Control.PaddingProperty));var content=new FrameworkElementFactory(typeof(ContentPresenter));content.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);content.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);border.AppendChild(content);
   var template=new ControlTemplate(typeof(Button)){VisualTree=border};foreach(var property in new[]{UIElement.IsMouseOverProperty,UIElement.IsKeyboardFocusedProperty}){var trigger=new Trigger{Property=property,Value=true};trigger.Setters.Add(new Setter(Border.BackgroundProperty,new DynamicResourceExtension("Hover"),"Surface"));template.Triggers.Add(trigger);}var disabled=new Trigger{Property=UIElement.IsEnabledProperty,Value=false};disabled.Setters.Add(new Setter(UIElement.OpacityProperty,.4,"Surface"));template.Triggers.Add(disabled);style.Setters.Add(new Setter(Control.TemplateProperty,template));return style;
  }
  static Style FilmstripButtonStyle(){
   // ImageGlass-style filmstrip: pictures on a continuous surface, no individual
   // cards. The lower mark indicates selection; keyboard focus has its own mark.
   var style=new Style(typeof(Button));style.Setters.Add(new Setter(Control.BackgroundProperty,Brushes.Transparent));style.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(0)));style.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty,null));style.Setters.Add(new Setter(FrameworkElement.CursorProperty,Cursors.Hand));
   var content=new FrameworkElementFactory(typeof(Grid));content.SetValue(Panel.BackgroundProperty,Brushes.Transparent);
   var presenter=new FrameworkElementFactory(typeof(ContentPresenter));presenter.Name="Picture";content.AppendChild(presenter);
   var focus=new FrameworkElementFactory(typeof(Border));focus.Name="FocusMark";focus.SetValue(FrameworkElement.WidthProperty,28.0);focus.SetValue(FrameworkElement.HeightProperty,2.0);focus.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);focus.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Top);focus.SetResourceReference(Border.BackgroundProperty,"Text");focus.SetValue(UIElement.VisibilityProperty,Visibility.Collapsed);content.AppendChild(focus);
   var template=new ControlTemplate(typeof(Button)){VisualTree=content};var hover=new Trigger{Property=UIElement.IsMouseOverProperty,Value=true};hover.Setters.Add(new Setter(UIElement.OpacityProperty,.78,"Picture"));template.Triggers.Add(hover);var focused=new Trigger{Property=UIElement.IsKeyboardFocusedProperty,Value=true};focused.Setters.Add(new Setter(UIElement.VisibilityProperty,Visibility.Visible,"FocusMark"));template.Triggers.Add(focused);style.Setters.Add(new Setter(Control.TemplateProperty,template));return style;
  }
  void OpenDialog(){var dialog=new Microsoft.Win32.OpenFileDialog{Filter=FileRouter.PictureFilter};if(dialog.ShowDialog(this)==true)Open(dialog.FileName);}
  void Recognize(){if(image==null||loading)return;try{new QingJie.TextWindow(QingJie.Snapshot.Load(currentPath)).Show();}catch(Exception error){label.Text="无法识别："+error.Message;}}
  void UpdateViewerControls(){bool ready=image!=null&&!loading;foreach(var button in imageTools)button.IsEnabled=ready;UpdateZoomLabel();RefreshEdgeVisibility();}
  void UpdateZoomLabel(){if(zoomButton==null)return;zoomButton.Content=viewport==null?"—":(viewport.Zoom*100).ToString("0.#")+"%";zoomButton.ToolTip=viewport!=null&&viewport.IsFit?"适应窗口 · 点击选择比例":"缩放比例 · 点击选择";}
  void DrawViewport(){if(viewport==null||picture==null||picture.IsDisposed)return;picture.Invalidate();if(drag!=null)drag.UpdateCursor();UpdateZoomLabel();}
  void FitImage(){if(viewport==null)return;if(drag!=null)drag.Cancel();viewport.Fit();DrawViewport();}
  void SetZoom(double value){if(viewport==null)return;if(drag!=null)drag.Cancel();viewport.ZoomAt(value/viewport.Zoom,viewport.Width/2,viewport.Height/2);DrawViewport();}
  void ZoomBy(double factor){if(viewport==null)return;if(drag!=null)drag.Cancel();viewport.ZoomAt(factor,viewport.Width/2,viewport.Height/2);DrawViewport();}
  void ToggleActualSize(double x,double y){if(viewport==null)return;if(viewport.IsFit){viewport.ZoomAt(1/viewport.Zoom,x,y);DrawViewport();}else FitImage();}
  void ToggleFocus(){SetFocusMode(!focusMode);}
  internal void SetFocusMode(bool value){
   if(focusMode==value)return;focusMode=value;if(drag!=null)drag.Cancel();
   if(value){focusRestorePath=viewport!=null&&!loading?currentPath:null;if(focusRestorePath!=null){focusRestoreFit=viewport.IsFit;focusRestoreZoom=viewport.Zoom;focusRestoreX=(viewport.Width/2-viewport.X)/viewport.Zoom;focusRestoreY=(viewport.Height/2-viewport.Y)/viewport.Zoom;}toolbar.Visibility=footer.Visibility=filmstrip.Visibility=Visibility.Collapsed;StopFilmstrip();}
   else{toolbar.Visibility=footer.Visibility=Visibility.Visible;RefreshFilmstrip();}
   UpdateLayout();
   if(!value&&viewport!=null&&!loading&&!focusRestoreFit&&focusRestorePath!=null&&string.Equals(focusRestorePath,currentPath,StringComparison.OrdinalIgnoreCase)){viewport.ZoomAt(focusRestoreZoom/viewport.Zoom,viewport.Width/2,viewport.Height/2);viewport.PanTo(viewport.Width/2-focusRestoreX*viewport.Zoom,viewport.Height/2-focusRestoreY*viewport.Zoom);DrawViewport();}else FitImage();
   if(!value)focusRestorePath=null;UpdatePlaybackVisibility();
  }
  void ToggleFilmstrip(){showFilmstrip=!showFilmstrip;RefreshFilmstrip();}
  internal void SetEdgeNavigation(bool value){showEdgeNavigation=value;RefreshEdgeVisibility();}
  void RefreshEdgeVisibility(){
   var canvas=host==null?null:host.Child;int x=-1;
   if(canvas!=null&&!canvas.IsDisposed&&IsActive){var pointer=canvas.PointToClient(Forms.Control.MousePosition);if(canvas.ClientRectangle.Contains(pointer))x=pointer.X;}
   UpdateEdgeVisibility(x);
  }
  bool HandleNativeKey(Forms.Keys keys){
   if(keys==(Forms.Keys.Shift|Forms.Keys.Oemplus))keys=Forms.Keys.Add;
   if(keys==(Forms.Keys.Control|Forms.Keys.C)){CopyCurrent();return true;}if(keys==(Forms.Keys.Control|Forms.Keys.O)){OpenDialog();return true;}
   if((keys&Forms.Keys.Modifiers)!=Forms.Keys.None)return false;
   switch(keys){case Forms.Keys.Escape:if(focusMode)SetFocusMode(false);else Close();return true;case Forms.Keys.F11:ToggleFocus();return true;case Forms.Keys.Left:case Forms.Keys.PageUp:Step(-1);return true;case Forms.Keys.Right:case Forms.Keys.PageDown:Step(1);return true;case Forms.Keys.D0:case Forms.Keys.NumPad0:FitImage();return true;case Forms.Keys.D1:case Forms.Keys.NumPad1:SetZoom(1);return true;case Forms.Keys.Add:case Forms.Keys.Oemplus:ZoomBy(1.15);return true;case Forms.Keys.Subtract:case Forms.Keys.OemMinus:ZoomBy(1/1.15);return true;case Forms.Keys.T:ToggleFilmstrip();return true;case Forms.Keys.F3:SafeReveal();return true;default:return false;}
  }
  void ViewerKeyDown(object sender,KeyEventArgs e){
   if(moreMenu.IsOpen||zoomMenu.IsOpen)return;
   var key=(Forms.Keys)KeyInterop.VirtualKeyFromKey(e.Key==Key.System?e.SystemKey:e.Key);
   if((Keyboard.Modifiers&ModifierKeys.Control)!=0)key|=Forms.Keys.Control;if((Keyboard.Modifiers&ModifierKeys.Alt)!=0)key|=Forms.Keys.Alt;if((Keyboard.Modifiers&ModifierKeys.Shift)!=0)key|=Forms.Keys.Shift;
   if(key==(Forms.Keys.Shift|Forms.Keys.Oemplus))key=Forms.Keys.Add;e.Handled=HandleNativeKey(key);
  }
  void AttachViewerSurface(PreviewCanvas canvas,PreviewPicture activePicture){
   edgePrevious=EdgeButton("\uE76B","上一张",()=>Step(-1));edgeNext=EdgeButton("\uE76C","下一张",()=>Step(1));canvas.Controls.Add(edgePrevious);canvas.Controls.Add(edgeNext);edgePrevious.BringToFront();edgeNext.BringToFront();
   Action place=()=>{if(edgePrevious==null)return;edgePrevious.Left=12;edgeNext.Left=Math.Max(12,canvas.ClientSize.Width-52);edgePrevious.Top=edgeNext.Top=Math.Max(4,(canvas.ClientSize.Height-48)/2);};canvas.Resize+=delegate{place();};place();
   foreach(var surface in new Forms.Control[]{canvas,activePicture}){
    surface.MouseMove+=(s,e)=>{var point=canvas.PointToClient(((Forms.Control)s).PointToScreen(e.Location));UpdateEdgeVisibility(point.X);};
    surface.MouseEnter+=delegate{RefreshEdgeVisibility();};
    surface.MouseLeave+=delegate{if(!canvas.ClientRectangle.Contains(canvas.PointToClient(Forms.Control.MousePosition)))UpdateEdgeVisibility(-1);};
   }
   foreach(var button in new[]{edgePrevious,edgeNext})button.MouseLeave+=delegate{RefreshEdgeVisibility();};
   var menu=activePicture.ContextMenuStrip;menu.Items.Insert(0,new Forms.ToolStripSeparator());var focus=new Forms.ToolStripMenuItem("专注看图"){ShortcutKeyDisplayString="F11"};focus.Click+=delegate{ToggleFocus();};menu.Items.Insert(0,focus);
   var edges=new Forms.ToolStripMenuItem("显示左右翻页图标");edges.Click+=delegate{SetEdgeNavigation(!showEdgeNavigation);};menu.Items.Insert(1,edges);
   menu.Opening+=delegate{focus.Text=focusMode?"退出专注看图":"专注看图";edges.Checked=showEdgeNavigation;};
  }
  sealed class EdgeNavigationButton:Forms.Button {
   protected override void OnSizeChanged(EventArgs e){
    base.OnSizeChanged(e);if(Width<20||Height<20)return;
    using(var shape=new System.Drawing.Drawing2D.GraphicsPath()){
     const int corner=20;shape.AddArc(0,0,corner,corner,180,90);shape.AddArc(Width-corner,0,corner,corner,270,90);shape.AddArc(Width-corner,Height-corner,corner,corner,0,90);shape.AddArc(0,Height-corner,corner,corner,90,90);shape.CloseFigure();
     var old=Region;Region=new System.Drawing.Region(shape);if(old!=null)old.Dispose();
    }
   }
  }
  Forms.Button EdgeButton(string glyph,string name,Action action){var font=new System.Drawing.Font("Segoe MDL2 Assets",14);var button=new EdgeNavigationButton{Text=glyph,AccessibleName=name,Width=40,Height=48,Visible=false,FlatStyle=Forms.FlatStyle.Flat,TabStop=false,BackColor=Theme.DrawingColor("Surface"),ForeColor=Theme.DrawingColor("Text"),Cursor=Forms.Cursors.Hand,Font=font};button.Disposed+=delegate{font.Dispose();};button.FlatAppearance.BorderSize=0;button.Click+=delegate{action();};return button;}
  internal void UpdateEdgeVisibility(int pointerX){if(edgePrevious==null||edgePrevious.IsDisposed)return;bool enabled=showEdgeNavigation&&!loading&&!scanning&&siblings!=null&&siblings.Length>1&&(drag==null||!drag.IsDragging);edgePrevious.Visible=enabled&&pointerX>=0&&pointerX<76;edgeNext.Visible=enabled&&host!=null&&pointerX>host.Child.ClientSize.Width-76&&pointerX<=host.Child.ClientSize.Width;}

  void StopFilmstrip(){stripVersion++;stripIdentity=null;strip.Children.Clear();}
  void RefreshFilmstrip(){
   if(filmstrip==null||closed)return;bool visible=showFilmstrip&&!focusMode&&siblings!=null&&siblings.Length>1;filmstrip.Visibility=visible?Visibility.Visible:Visibility.Collapsed;
   if(!visible){StopFilmstrip();return;}
   int count=Math.Min(11,Math.Max(3,(int)(Math.Max(300,ActualWidth)-40)/76));count=Math.Min(count,siblings.Length);int start=Math.Max(0,Math.Min(index-count/2,siblings.Length-count));
   string identity=directoryVersion+":"+start+":"+count+":"+index;if(identity==stripIdentity)return;StopFilmstrip();stripIdentity=identity;int version=stripVersion;
   var paths=new List<string>();var previews=new List<System.Windows.Controls.Image>();
   for(int i=start;i<start+count;i++){int target=i;var preview=new System.Windows.Controls.Image{Width=64,Height=44,Margin=new Thickness(0,2,0,8),Stretch=Stretch.Uniform};var cell=new Grid();var format=new TextBlock{Text=Path.GetExtension(siblings[i]).TrimStart('.').ToUpperInvariant(),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,0,6),FontSize=10};format.SetResourceReference(TextBlock.ForegroundProperty,"Muted");cell.Children.Add(format);cell.Children.Add(preview);
    if(i==index){var mark=new Border{Name="CurrentImageMark",Width=18,Height=3,CornerRadius=new CornerRadius(1.5),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(0,0,0,1)};mark.SetResourceReference(Border.BackgroundProperty,"Accent");cell.Children.Add(mark);}
    var tile=new Button{Style=filmstripTileStyle,Content=cell,Width=68,Height=54,Margin=new Thickness(3,0,3,0),Padding=new Thickness(0),ToolTip=DisplayName(siblings[i])};AutomationProperties.SetName(tile,"第 "+(i+1)+" 张 · "+DisplayName(siblings[i]));tile.Click+=delegate{if(!scanning&&siblings!=null&&target<siblings.Length)GoToImage(target);};strip.Children.Add(tile);
    BitmapSource cached;if(stripCache.TryGetValue(siblings[i],out cached)){preview.Source=cached;format.Visibility=Visibility.Collapsed;}else{paths.Add(siblings[i]);previews.Add(preview);}
   }
   LoadStripAsync(paths,previews,version);
  }
  async void LoadStripAsync(List<string> paths,List<System.Windows.Controls.Image> previews,int version){
   for(int i=0;i<paths.Count;i++){
    if(closed||version!=stripVersion)return;BitmapSource thumbnail=null;
    if(!stripCache.TryGetValue(paths[i],out thumbnail)){
     await stripSlots.WaitAsync();
     try{if(closed||version!=stripVersion)return;string path=paths[i];thumbnail=await Task.Run(()=>{int width,height;return PreviewThumbnail.Read(path,120,out width,out height);});}catch(Exception){/* A broken neighbour must never prevent viewing the current image. */}finally{stripSlots.Release();}
     if(closed||version!=stripVersion)return;if(thumbnail!=null){while(stripCache.Count>=32)stripCache.Remove(stripCacheOrder.Dequeue());stripCache[paths[i]]=thumbnail;stripCacheOrder.Enqueue(paths[i]);}
    }
    if(closed||version!=stripVersion)return;if(thumbnail!=null){previews[i].Source=thumbnail;((Grid)previews[i].Parent).Children[0].Visibility=Visibility.Collapsed;}
   }
  }
  async void GoToImage(int target){if(closed||scanning||siblings==null||target<0||target>=siblings.Length||target==index)return;var request=BeginRequest();int stamp=directoryVersion;index=target;currentPath=siblings[target];try{await Load(currentPath,request.Token,stamp);}finally{EndRequest(request);}}
 }
}
