using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Media;
using Forms=System.Windows.Forms;
using Drawing=System.Drawing;

namespace Mori {
 sealed class PreviewCanvas : Forms.Panel {
  internal Action CopyImage;
  public PreviewCanvas(){SetStyle(Forms.ControlStyles.Selectable|Forms.ControlStyles.OptimizedDoubleBuffer|Forms.ControlStyles.AllPaintingInWmPaint,true);TabStop=true;}
  internal bool TryCopyShortcut(Forms.Keys keys){if(keys!=(Forms.Keys.Control|Forms.Keys.C)||CopyImage==null)return false;CopyImage();return true;}
  protected override bool ProcessCmdKey(ref Forms.Message message,Forms.Keys keys){return TryCopyShortcut(keys)||base.ProcessCmdKey(ref message,keys);}
  protected override void Dispose(bool disposing){if(disposing)CopyImage=null;base.Dispose(disposing);}
 }
 sealed class PreviewDragController : IDisposable {
  readonly Forms.Control surface,picture;readonly PreviewViewport viewport;readonly Action changed;readonly bool captureMouse;
  bool dragging,moved,hadDrag;int lastDragTick;double startX,startY;Drawing.Point startMouse;Forms.Control capture;
  public bool IsDragging {get{return dragging;}}
  public PreviewDragController(Forms.Control surface,Forms.Control picture,PreviewViewport viewport,Action changed,bool captureMouse=true){
   this.surface=surface;this.picture=picture;this.viewport=viewport;this.changed=changed;this.captureMouse=captureMouse;
   foreach(var control in new[]{surface,picture}){control.MouseDown+=Down;control.MouseMove+=Move;control.MouseUp+=Up;control.MouseCaptureChanged+=CaptureChanged;}
  }
  public void UpdateCursor(){if(picture.IsDisposed||surface.IsDisposed)return;var cursor=dragging?Forms.Cursors.SizeAll:viewport.CanPan?Forms.Cursors.Hand:Forms.Cursors.Default;picture.Cursor=cursor;surface.Cursor=cursor;}
  public void Cancel(){if(dragging&&moved){hadDrag=true;lastDragTick=Environment.TickCount;}dragging=false;moved=false;var control=capture;capture=null;if(captureMouse&&control!=null&&!control.IsDisposed&&control.Capture)control.Capture=false;UpdateCursor();}
  public bool EndDoubleClick(){bool wasDrag=dragging&&moved;Cancel();return wasDrag||(hadDrag&&unchecked(Environment.TickCount-lastDragTick)<=Forms.SystemInformation.DoubleClickTime);}
  void Down(object sender,Forms.MouseEventArgs e){
   if(captureMouse)surface.Focus();if(e.Button!=Forms.MouseButtons.Left||e.Clicks>1||!viewport.CanPan)return;
   Cancel();capture=(Forms.Control)sender;
   // Event coordinates preserve the input sequence even when native messages
   // are queued and the physical pointer has already moved/released.
   startMouse=capture.PointToScreen(e.Location);startX=viewport.X;startY=viewport.Y;dragging=true;
   if(captureMouse)capture.Capture=true;UpdateCursor();
  }
  void Move(object sender,Forms.MouseEventArgs e){
   if(!dragging)return;var point=((Forms.Control)sender).PointToScreen(e.Location);int dx=point.X-startMouse.X,dy=point.Y-startMouse.Y;
   if(!moved&&Math.Abs(dx)<Math.Max(2,Forms.SystemInformation.DragSize.Width/2)&&Math.Abs(dy)<Math.Max(2,Forms.SystemInformation.DragSize.Height/2))return;
   moved=true;viewport.PanTo(startX+dx,startY+dy);changed();
  }
  void Up(object sender,Forms.MouseEventArgs e){if(e.Button==Forms.MouseButtons.Left)Cancel();}
  void CaptureChanged(object sender,EventArgs e){if(captureMouse&&dragging&&ReferenceEquals(sender,capture)&&!capture.Capture)Cancel();}
  public void Dispose(){Cancel();foreach(var control in new[]{surface,picture}){control.MouseDown-=Down;control.MouseMove-=Move;control.MouseUp-=Up;control.MouseCaptureChanged-=CaptureChanged;}}
 }
 sealed class PreviewMenuColors : Forms.ProfessionalColorTable {
  readonly Drawing.Color surface,hover,border,divider,selected;
  internal PreviewMenuColors(){UseSystemColors=false;surface=Theme.DrawingColor("Surface");hover=Theme.DrawingColor("Hover");border=Theme.DrawingColor("Border");divider=Theme.DrawingColor("Divider");selected=Theme.DrawingColor("AccentSoft");}
  public override Drawing.Color ToolStripDropDownBackground {get{return surface;}}
  public override Drawing.Color ImageMarginGradientBegin {get{return surface;}}
  public override Drawing.Color ImageMarginGradientMiddle {get{return surface;}}
  public override Drawing.Color ImageMarginGradientEnd {get{return surface;}}
  public override Drawing.Color MenuBorder {get{return border;}}
  public override Drawing.Color MenuItemBorder {get{return border;}}
  public override Drawing.Color MenuItemSelected {get{return hover;}}
  public override Drawing.Color MenuItemSelectedGradientBegin {get{return hover;}}
  public override Drawing.Color MenuItemSelectedGradientEnd {get{return hover;}}
  public override Drawing.Color CheckBackground {get{return selected;}}
  public override Drawing.Color CheckSelectedBackground {get{return selected;}}
  public override Drawing.Color CheckPressedBackground {get{return selected;}}
  public override Drawing.Color SeparatorDark {get{return divider;}}
  public override Drawing.Color SeparatorLight {get{return surface;}}
 }
 sealed class PreviewPicture : Forms.Control {
  readonly Drawing.Image image;readonly PreviewViewport viewport;readonly EventHandler frameChanged;readonly Action repaint;readonly bool animateImages;bool animating,playbackVisible=true;
  readonly bool mayHaveAlpha;readonly Forms.ContextMenuStrip backgroundMenu;Drawing.TextureBrush lightChecker,darkChecker;PreviewBackground backgroundMode;
  internal event Action PreviewBackgroundChanged;
  internal PreviewBackground BackgroundMode {get{return backgroundMode;}set{if(!Enum.IsDefined(typeof(PreviewBackground),value))throw new ArgumentOutOfRangeException("value");if(backgroundMode==value)return;backgroundMode=value;UpdateBackgroundChecks();Invalidate();if(PreviewBackgroundChanged!=null)PreviewBackgroundChanged();}}
  public PreviewPicture(Drawing.Image image,PreviewViewport viewport):this(image,viewport,Settings.Load()){}
  // Background and animation are defaults for this preview, not live global state.
  internal PreviewPicture(Drawing.Image image,PreviewViewport viewport,Settings preferences){
   if(preferences==null)throw new ArgumentNullException("preferences");
   this.image=image;this.viewport=viewport;animateImages=preferences.AnimateImages;backgroundMode=DefaultBackground(preferences.PreviewBackdrop);mayHaveAlpha=PreviewBackgrounds.MayHaveAlpha(image);frameChanged=FrameChanged;repaint=delegate{if(!IsDisposed)Invalidate();};
   SetStyle(Forms.ControlStyles.UserPaint|Forms.ControlStyles.AllPaintingInWmPaint|Forms.ControlStyles.OptimizedDoubleBuffer|Forms.ControlStyles.ResizeRedraw|Forms.ControlStyles.StandardClick|Forms.ControlStyles.StandardDoubleClick,true);Dock=Forms.DockStyle.Fill;
   AccessibleName="图片预览";AccessibleDescription="滚轮缩放，拖动查看细节，右键切换图片背景。";
   backgroundMenu=new Forms.ContextMenuStrip{AccessibleName="图片背景"};foreach(PreviewBackground value in Enum.GetValues(typeof(PreviewBackground))){var choice=value;var item=new Forms.ToolStripMenuItem(PreviewBackgrounds.Label(choice)){Tag=choice,Checked=choice==backgroundMode};item.Click+=delegate{BackgroundMode=choice;};backgroundMenu.Items.Add(item);}
   backgroundMenu.Opening+=delegate{UpdateBackgroundChecks();};ContextMenuStrip=backgroundMenu;
   UpdateMenuTheme();Theme.Changed+=UpdateMenuTheme;
  }
  void UpdateBackgroundChecks(){foreach(Forms.ToolStripItem entry in backgroundMenu.Items){var item=entry as Forms.ToolStripMenuItem;if(item!=null&&item.Tag is PreviewBackground)item.Checked=(PreviewBackground)item.Tag==BackgroundMode;}}
  void EnsureActionSeparator(){if(!backgroundMenu.Items.ContainsKey("ImageActionsSeparator"))backgroundMenu.Items.Insert(0,new Forms.ToolStripSeparator{Name="ImageActionsSeparator"});backgroundMenu.AccessibleName="图片操作";AccessibleDescription="滚轮缩放，拖动查看细节，Ctrl+C 复制图片，右键复制、钉在桌面或切换图片背景。";}
  internal void AttachCopy(Action copy){
   if(copy==null)throw new ArgumentNullException("copy");if(backgroundMenu.Items.ContainsKey("CopyImage"))return;
   EnsureActionSeparator();var item=new Forms.ToolStripMenuItem("复制图片"){Name="CopyImage",AccessibleName="复制图片",ShortcutKeyDisplayString="Ctrl+C",ToolTipText="复制完整预览图片，不含背景底纹；动图复制当前静态画面。"};
   item.Click+=delegate{copy();};backgroundMenu.Items.Insert(0,item);UpdateMenuTheme();
  }
  internal void AttachDesktopPin(Func<Task> pin){
   if(pin==null)throw new ArgumentNullException("pin");if(backgroundMenu.Items.ContainsKey("PinToDesktop"))return;
   var item=new Forms.ToolStripMenuItem("钉在桌面"){Name="PinToDesktop",AccessibleName="钉在桌面",ToolTipText="无边框置顶；拖动移动，滚轮缩放。动图保存当前静态画面。"};
   item.Click+=async delegate{if(!item.Enabled)return;item.Enabled=false;try{await pin();}finally{if(!item.IsDisposed)item.Enabled=true;}};
   EnsureActionSeparator();backgroundMenu.Items.Insert(backgroundMenu.Items.ContainsKey("CopyImage")?1:0,item);UpdateMenuTheme();
  }
  static PreviewBackground DefaultBackground(string value){switch(value){case "light-checker":return PreviewBackground.LightChecker;case "dark-checker":return PreviewBackground.DarkChecker;case "white":return PreviewBackground.White;case "dark":return PreviewBackground.Dark;default:return PreviewBackground.Automatic;}}
  void UpdateMenuTheme(){if(IsDisposed||Disposing||Application.Current==null||string.IsNullOrEmpty(Theme.Current))return;backgroundMenu.BackColor=Theme.DrawingColor("Surface");backgroundMenu.ForeColor=Theme.DrawingColor("Text");backgroundMenu.Renderer=new Forms.ToolStripProfessionalRenderer(new PreviewMenuColors());foreach(Forms.ToolStripItem item in backgroundMenu.Items){item.BackColor=backgroundMenu.BackColor;item.ForeColor=backgroundMenu.ForeColor;}}
  void Animation(bool enabled){enabled=enabled&&animateImages;if(enabled&&!animating&&Drawing.ImageAnimator.CanAnimate(image)){Drawing.ImageAnimator.Animate(image,frameChanged);animating=true;}else if(!enabled&&animating){Drawing.ImageAnimator.StopAnimate(image,frameChanged);animating=false;}}
  void FrameChanged(object sender,EventArgs e){if(IsDisposed||!IsHandleCreated)return;try{BeginInvoke(repaint);}catch(InvalidOperationException){}}
  internal void SetPlaybackVisible(bool value){if(IsDisposed||Disposing)return;playbackVisible=value;Animation(playbackVisible&&Visible&&IsHandleCreated);}
  protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);Animation(playbackVisible&&Visible);}
  protected override void OnHandleDestroyed(EventArgs e){Animation(false);base.OnHandleDestroyed(e);}
  protected override void OnVisibleChanged(EventArgs e){base.OnVisibleChanged(e);Animation(playbackVisible&&Visible&&IsHandleCreated);}
  protected override void OnPaint(Forms.PaintEventArgs e){
   // Only the viewport is double-buffered; zooming never creates a giant
   // PictureBox/backbuffer or a second full-size bitmap.
   if(animating)Drawing.ImageAnimator.UpdateFrames(image);
   var bounds=new Drawing.RectangleF((float)viewport.X,(float)viewport.Y,(float)viewport.DisplayWidth,(float)viewport.DisplayHeight);
   var visible=Drawing.RectangleF.Intersect(bounds,ClientRectangle);
   if(visible.Width>0&&visible.Height>0&&(backgroundMode!=PreviewBackground.Automatic||mayHaveAlpha)){
    if(backgroundMode==PreviewBackground.White)e.Graphics.FillRectangle(Drawing.Brushes.White,visible);
    else if(backgroundMode==PreviewBackground.Dark){using(var brush=new Drawing.SolidBrush(PreviewBackgrounds.SolidDark))e.Graphics.FillRectangle(brush,visible);}
    else{bool dark=backgroundMode==PreviewBackground.DarkChecker;if(dark){if(darkChecker==null)darkChecker=PreviewBackgrounds.Checker(true);}else if(lightChecker==null)lightChecker=PreviewBackgrounds.Checker(false);e.Graphics.FillRectangle(dark?darkChecker:lightChecker,visible);}
   }
   e.Graphics.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
   e.Graphics.PixelOffsetMode=System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
   e.Graphics.DrawImage(image,bounds);base.OnPaint(e);
  }
  protected override void Dispose(bool disposing){if(disposing){Theme.Changed-=UpdateMenuTheme;Animation(false);backgroundMenu.Dispose();if(lightChecker!=null){lightChecker.Dispose();lightChecker=null;}if(darkChecker!=null){darkChecker.Dispose();darkChecker=null;}}base.Dispose(disposing);}
 }
 public static class PreviewWindow {
  internal static void AttachDoubleClickClose(Forms.Control picture,PreviewDragController drag,Action close){picture.MouseDoubleClick+=delegate(object sender,Forms.MouseEventArgs e){if(e.Button==Forms.MouseButtons.Left&&!drag.EndDoubleClick())close();};}
  static Window Create(Window owner,string title,bool closeOnSpace=true,bool closeOnEscape=true){var w=new Window{Owner=owner,Title=title,Width=1100,Height=800,MinWidth=500,MinHeight=360,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=new SolidColorBrush(Color.FromRgb(25,26,29)),Foreground=Brushes.White};Theme.Attach(w,owner);MoriDesk.InitialWindowPlacement.Attach(w);w.PreviewKeyDown+=delegate(object s,KeyEventArgs e){if((closeOnEscape&&e.Key==Key.Escape)||(closeOnSpace&&e.Key==Key.Space)){w.Close();e.Handled=true;}};return w;}
  internal static Grid ImageLayout(int count){var grid=new Grid();for(int i=0;i<count;i++){grid.ColumnDefinitions.Add(new ColumnDefinition());var panel=new DockPanel{Margin=count==1?new Thickness(0):new Thickness(4,0,4,0)};Grid.SetColumn(panel,i);grid.Children.Add(panel);}return grid;}
  internal static TextBlock ImageActionsHint(string file,bool single){
   string actions=single?"右键：复制 / 钉在桌面 · Ctrl+C 复制 · Esc 关闭":"右键：复制 · Ctrl+C 复制当前图片 · Esc 关闭";
   string notice=FormatPreviewInfo.Notice(file),extension=Path.GetExtension(file).ToLowerInvariant();
   bool limited=extension==".psd"||extension==".psb"||extension==".ai";
   var hint=new TextBlock{Name="ImageActionsHint",Text=limited&&notice.Length>0?notice+" · "+actions:actions,ToolTip=notice.Length>0?notice:null,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(12,6,12,6)};
   DockPanel.SetDock(hint,Dock.Bottom);return hint;
  }
  internal static string FailureText(Exception error){if(error is FileNotFoundException||error is DirectoryNotFoundException)return "原文件已移动、删除或磁盘暂不可用。\n请返回素材库检查本地地址。";if(error is UnauthorizedAccessException)return "没有权限读取此文件。\n请检查本地文件的访问权限。";if(error is NotSupportedException)return error.Message+"\n请返回右键 → 用默认程序打开。";return "此格式无法内置预览，文件可能损坏或编码不受支持。\n请返回右键 → 用默认程序打开。";}
  public static void Images(Window owner,string[] files,string[] names){Images(owner,files,names,null);}
  // Tests configure only their own window before it is shown; the production
  // path and its async lifetime are otherwise identical.
  internal static async void Images(Window owner,string[] files,string[] names,Action<Window> configure){if(files.Length==1&&FormatPreviewInfo.UsesPagedPreview(files[0])){var document=new DocumentPreviewWindow(owner,files[0],names[0]);if(configure!=null)configure(document);document.Show();return;}var window=Create(owner,files.Length==1?names[0]+" · 预览":"并排比较");var grid=ImageLayout(files.Length);var dispose=new List<IDisposable>();var cancellation=new CancellationTokenSource();bool closed=false,loadingCompleted=false;window.Content=grid;window.Closed+=delegate{closed=true;cancellation.Cancel();if(loadingCompleted)cancellation.Dispose();for(int j=dispose.Count-1;j>=0;j--)dispose[j].Dispose();};if(configure!=null)configure(window);window.Show();try{for(int i=0;i<files.Length;i++){
    var panel=(DockPanel)grid.Children[i];
    try{string file=files[i];var loading=new TextBlock{Text=string.Equals(Path.GetExtension(file),".svg",StringComparison.OrdinalIgnoreCase)?"正在预览 SVG…":"正在加载…",Margin=new Thickness(20)};panel.Children.Add(loading);var loaded=await PreviewImage.OpenAsync(file,cancellation.Token);if(closed){loaded.Dispose();return;}panel.Children.Remove(loading);dispose.Add(loaded);var image=loaded.Image;var viewport=new PreviewViewport(image.Width,image.Height);var host=new WindowsFormsHost();var scroll=new PreviewCanvas{AutoScroll=false,Dock=Forms.DockStyle.Fill,BackColor=Theme.DrawingColor("Canvas")};var picture=new PreviewPicture(image,viewport){BackColor=scroll.BackColor};Action updateTheme=delegate{scroll.BackColor=Theme.DrawingColor("Canvas");picture.BackColor=scroll.BackColor;};Theme.Changed+=updateTheme;window.Closed+=delegate{Theme.Changed-=updateTheme;};scroll.Controls.Add(picture);host.Child=scroll;panel.Children.Add(ImageActionsHint(file,files.Length==1));panel.Children.Add(host);dispose.Add(host);
     if(files.Length==1)picture.AttachDesktopPin(()=>MoriDesk.DesktopImagePins.ShowAsync(window,image));
     Action copy=()=>MoriDesk.PreviewClipboard.CopyForWindow(window,image);picture.AttachCopy(copy);scroll.CopyImage=copy;
     if(files.Length==1)window.PreviewKeyDown+=(sender,args)=>{if(args.Key==Key.C&&Keyboard.Modifiers==ModifierKeys.Control){copy();args.Handled=true;}};
     PreviewDragController drag=null;Action layout=delegate{picture.Invalidate();if(drag!=null)drag.UpdateCursor();};drag=new PreviewDragController(scroll,picture,viewport,layout);dispose.Add(drag);Action endDrag=drag.Cancel;
     EventHandler focus=delegate{scroll.Focus();};picture.MouseEnter+=focus;scroll.MouseEnter+=focus;
     Forms.MouseEventHandler wheel=delegate(object sender,Forms.MouseEventArgs e){if(e.Delta==0)return;var handled=e as Forms.HandledMouseEventArgs;if(handled!=null)handled.Handled=true;endDrag();var point=scroll.PointToClient(((Forms.Control)sender).PointToScreen(e.Location));viewport.ZoomAt(Math.Pow(1.15,e.Delta/120.0),point.X,point.Y);layout();};
     scroll.MouseWheel+=wheel;picture.MouseWheel+=wheel;
     AttachDoubleClickClose(picture,drag,window.Close);
     scroll.Resize+=delegate{endDrag();viewport.Resize(scroll.ClientSize.Width,scroll.ClientSize.Height);layout();};
     scroll.KeyDown+=delegate(object sender,Forms.KeyEventArgs e){if(e.KeyCode==Forms.Keys.Escape||e.KeyCode==Forms.Keys.Space){e.Handled=true;e.SuppressKeyPress=true;endDrag();window.Close();}};
     Action playbackVisibility=delegate{picture.SetPlaybackVisible(window.IsVisible&&window.WindowState!=WindowState.Minimized);};window.IsVisibleChanged+=delegate{playbackVisibility();};window.StateChanged+=delegate{playbackVisibility();};playbackVisibility();
     window.Deactivated+=delegate{endDrag();};window.Closed+=delegate{endDrag();};window.UpdateLayout();viewport.Resize(scroll.ClientSize.Width,scroll.ClientSize.Height);layout();
    }catch(Exception error){if(closed)return;panel.Children.Clear();var text=new TextBlock{Text=FailureText(error),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(20)};panel.Children.Add(text);}
   }}finally{loadingCompleted=true;if(closed)cancellation.Dispose();}}
  public static void Video(Window owner,string file,string name){var window=Create(owner,name,false,false);var player=new VideoPlayer(file);player.AttachWindow(window);window.Content=player.Root;player.CloseRequested+=window.Close;window.PreviewKeyDown+=player.KeyDown;window.Loaded+=delegate{player.Start();};window.IsVisibleChanged+=delegate{player.SetVisible(window.IsVisible&&window.WindowState!=WindowState.Minimized);};window.StateChanged+=delegate{player.SetVisible(window.IsVisible&&window.WindowState!=WindowState.Minimized);};window.Closed+=delegate{player.Dispose();};window.Show();}
 }
}
