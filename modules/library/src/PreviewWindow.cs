using System;
using System.Collections.Generic;
using System.IO;
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
  public PreviewCanvas(){SetStyle(Forms.ControlStyles.Selectable|Forms.ControlStyles.OptimizedDoubleBuffer|Forms.ControlStyles.AllPaintingInWmPaint,true);TabStop=true;}
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
 sealed class PreviewPicture : Forms.Control {
  readonly Drawing.Image image;readonly PreviewViewport viewport;readonly EventHandler frameChanged;readonly Action repaint;bool animating;
  public PreviewPicture(Drawing.Image image,PreviewViewport viewport){
   this.image=image;this.viewport=viewport;frameChanged=FrameChanged;repaint=delegate{if(!IsDisposed)Invalidate();};
   SetStyle(Forms.ControlStyles.UserPaint|Forms.ControlStyles.AllPaintingInWmPaint|Forms.ControlStyles.OptimizedDoubleBuffer|Forms.ControlStyles.ResizeRedraw|Forms.ControlStyles.StandardClick|Forms.ControlStyles.StandardDoubleClick,true);Dock=Forms.DockStyle.Fill;
  }
  void Animation(bool enabled){if(enabled&&!animating&&Drawing.ImageAnimator.CanAnimate(image)){Drawing.ImageAnimator.Animate(image,frameChanged);animating=true;}else if(!enabled&&animating){Drawing.ImageAnimator.StopAnimate(image,frameChanged);animating=false;}}
  void FrameChanged(object sender,EventArgs e){if(IsDisposed||!IsHandleCreated)return;try{BeginInvoke(repaint);}catch(InvalidOperationException){}}
  protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);Animation(Visible);}
  protected override void OnHandleDestroyed(EventArgs e){Animation(false);base.OnHandleDestroyed(e);}
  protected override void OnVisibleChanged(EventArgs e){base.OnVisibleChanged(e);Animation(Visible&&IsHandleCreated);}
  protected override void OnPaint(Forms.PaintEventArgs e){
   // Only the viewport is double-buffered; zooming never creates a giant
   // PictureBox/backbuffer or a second full-size bitmap.
   if(animating)Drawing.ImageAnimator.UpdateFrames(image);
   e.Graphics.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
   e.Graphics.PixelOffsetMode=System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
   e.Graphics.DrawImage(image,new Drawing.RectangleF((float)viewport.X,(float)viewport.Y,(float)viewport.DisplayWidth,(float)viewport.DisplayHeight));base.OnPaint(e);
  }
  protected override void Dispose(bool disposing){if(disposing)Animation(false);base.Dispose(disposing);}
 }
 public static class PreviewWindow {
  internal static void AttachDoubleClickClose(Forms.Control picture,PreviewDragController drag,Action close){picture.MouseDoubleClick+=delegate(object sender,Forms.MouseEventArgs e){if(e.Button==Forms.MouseButtons.Left&&!drag.EndDoubleClick())close();};}
  static Window Create(Window owner,string title,bool closeOnSpace=true){var w=new Window{Owner=owner,Title=title,Width=1100,Height=800,MinWidth=500,MinHeight=360,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=new SolidColorBrush(Color.FromRgb(25,26,29)),Foreground=Brushes.White};Theme.Attach(w,owner);w.PreviewKeyDown+=delegate(object s,KeyEventArgs e){if(e.Key==Key.Escape||(closeOnSpace&&e.Key==Key.Space)){w.Close();e.Handled=true;}};return w;}
  internal static Grid ImageLayout(int count){var grid=new Grid();for(int i=0;i<count;i++){grid.ColumnDefinitions.Add(new ColumnDefinition());var panel=new DockPanel{Margin=count==1?new Thickness(0):new Thickness(4,0,4,0)};Grid.SetColumn(panel,i);grid.Children.Add(panel);}return grid;}
  public static async void Images(Window owner,string[] files,string[] names){var window=Create(owner,files.Length==1?names[0]+" · 预览":"并排比较");var grid=ImageLayout(files.Length);var dispose=new List<IDisposable>();bool closed=false;window.Content=grid;window.Closed+=delegate{closed=true;for(int j=dispose.Count-1;j>=0;j--)dispose[j].Dispose();};window.Show();for(int i=0;i<files.Length;i++){
    var panel=(DockPanel)grid.Children[i];
    try{string file=files[i];PreviewImage loaded;if(string.Equals(Path.GetExtension(file),".svg",StringComparison.OrdinalIgnoreCase)){var loading=new TextBlock{Text="正在预览 SVG…",Margin=new Thickness(20)};panel.Children.Add(loading);loaded=await Task.Run(()=>PreviewImage.Open(file));if(closed){loaded.Dispose();return;}panel.Children.Remove(loading);}else loaded=PreviewImage.Open(file);dispose.Add(loaded);var image=loaded.Image;var viewport=new PreviewViewport(image.Width,image.Height);var host=new WindowsFormsHost();var scroll=new PreviewCanvas{AutoScroll=false,Dock=Forms.DockStyle.Fill,BackColor=Theme.Current=="light"?Drawing.Color.FromArgb(250,250,249):Drawing.Color.FromArgb(29,31,34)};var picture=new PreviewPicture(image,viewport){BackColor=scroll.BackColor};Action updateTheme=delegate{scroll.BackColor=Theme.Current=="light"?Drawing.Color.FromArgb(250,250,249):Drawing.Color.FromArgb(29,31,34);picture.BackColor=scroll.BackColor;};Theme.Changed+=updateTheme;window.Closed+=delegate{Theme.Changed-=updateTheme;};scroll.Controls.Add(picture);host.Child=scroll;panel.Children.Add(host);dispose.Add(host);
     PreviewDragController drag=null;Action layout=delegate{picture.Invalidate();if(drag!=null)drag.UpdateCursor();};drag=new PreviewDragController(scroll,picture,viewport,layout);dispose.Add(drag);Action endDrag=drag.Cancel;
     EventHandler focus=delegate{scroll.Focus();};picture.MouseEnter+=focus;scroll.MouseEnter+=focus;
     Forms.MouseEventHandler wheel=delegate(object sender,Forms.MouseEventArgs e){if(e.Delta==0)return;var handled=e as Forms.HandledMouseEventArgs;if(handled!=null)handled.Handled=true;endDrag();var point=scroll.PointToClient(((Forms.Control)sender).PointToScreen(e.Location));viewport.ZoomAt(Math.Pow(1.15,e.Delta/120.0),point.X,point.Y);layout();};
     scroll.MouseWheel+=wheel;picture.MouseWheel+=wheel;
     AttachDoubleClickClose(picture,drag,window.Close);
     scroll.Resize+=delegate{endDrag();viewport.Resize(scroll.ClientSize.Width,scroll.ClientSize.Height);layout();};
     scroll.KeyDown+=delegate(object sender,Forms.KeyEventArgs e){if(e.KeyCode==Forms.Keys.Escape||e.KeyCode==Forms.Keys.Space){e.Handled=true;e.SuppressKeyPress=true;endDrag();window.Close();}};
     window.Deactivated+=delegate{endDrag();};window.Closed+=delegate{endDrag();};window.UpdateLayout();viewport.Resize(scroll.ClientSize.Width,scroll.ClientSize.Height);layout();
    }catch(Exception){if(closed)return;panel.Children.Clear();var text=new TextBlock{Text="此格式无法内置预览。\n请返回右键 → 用默认程序打开。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(20)};panel.Children.Add(text);}
   }}
  public static void Video(Window owner,string file,string name){var window=Create(owner,name,false);var player=new VideoPlayer(file);window.Content=player.Root;player.CloseRequested+=window.Close;window.PreviewKeyDown+=player.KeyDown;window.Loaded+=delegate{player.Start();};window.IsVisibleChanged+=delegate{player.SetVisible(window.IsVisible&&window.WindowState!=WindowState.Minimized);};window.StateChanged+=delegate{player.SetVisible(window.IsVisible&&window.WindowState!=WindowState.Minimized);};window.Closed+=delegate{player.Dispose();};window.Show();}
 }
}
