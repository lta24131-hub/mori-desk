using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Mori {
 // Active only between middle-click and cancel; viewport-relative units also
 // work with logical scrolling controls, without loading additional images.
 internal sealed class MiddleScroll {
  readonly Window window;readonly DispatcherTimer timer;readonly Stopwatch clock=new Stopwatch();ScrollViewer target;Point origin;double desired;Cursor previousCursor;AdornerLayer layer;OriginMark mark;
  internal bool Active {get{return target!=null;}}
  internal static double Speed(double distance,double viewport){double d=Math.Abs(distance);if(d<=12||viewport<=0||double.IsNaN(d)||double.IsInfinity(d))return 0;return Math.Sign(distance)*Math.Min(6,Math.Pow((d-12)/90,1.35))*viewport;}
  public MiddleScroll(Window window){
   this.window=window;timer=new DispatcherTimer(DispatcherPriority.Input){Interval=TimeSpan.FromMilliseconds(16)};timer.Tick+=Tick;
   window.PreviewMouseDown+=Down;window.PreviewKeyDown+=delegate(object s,KeyEventArgs e){if(Active&&e.Key==Key.Escape){Stop();e.Handled=true;}};
   window.PreviewMouseWheel+=delegate{Stop();};window.Deactivated+=delegate{Stop();};window.Closed+=delegate{Stop();};window.IsVisibleChanged+=delegate{if(!window.IsVisible)Stop();};
   window.LostMouseCapture+=delegate{if(Active&&Mouse.Captured!=window)Stop();};
  }
  static DependencyObject Parent(DependencyObject node){if(node is Visual)return VisualTreeHelper.GetParent(node);var content=node as FrameworkContentElement;return content==null?null:content.Parent;}
  internal static ScrollViewer ScrollTarget(DependencyObject source){
   for(var node=source;node!=null;node=Parent(node)){if(node is TextBoxBase||node is Slider||node is ScrollBar)return null;var scroll=node as ScrollViewer;if(scroll!=null&&scroll.ScrollableHeight>0)return scroll;}return null;
  }
  void Down(object sender,MouseButtonEventArgs e){
   if(Active){Stop();e.Handled=true;return;}if(e.ChangedButton!=MouseButton.Middle)return;
   var scroll=ScrollTarget(e.OriginalSource as DependencyObject);if(scroll==null)return;
   origin=e.GetPosition(window);desired=scroll.VerticalOffset;target=scroll;previousCursor=Mouse.OverrideCursor;
   if(!Mouse.Capture(window,CaptureMode.SubTree)){target=null;return;}Mouse.OverrideCursor=Cursors.ScrollNS;
   layer=AdornerLayer.GetAdornerLayer(scroll);if(layer!=null){mark=new OriginMark(scroll,e.GetPosition(scroll));layer.Add(mark);}clock.Restart();timer.Start();e.Handled=true;
  }
  void Tick(object sender,EventArgs e){
   if(target==null||!window.IsActive||!target.IsVisible||Mouse.Captured!=window){Stop();return;}
   double seconds=Math.Min(.06,clock.Elapsed.TotalSeconds);clock.Restart();double delta=Mouse.GetPosition(window).Y-origin.Y;
   desired=Math.Max(0,Math.Min(target.ScrollableHeight,desired+Speed(delta,target.ViewportHeight)*seconds));target.ScrollToVerticalOffset(desired);
  }
  internal void Stop(){if(!Active)return;timer.Stop();clock.Stop();target=null;if(layer!=null&&mark!=null)layer.Remove(mark);layer=null;mark=null;if(Mouse.Captured==window)Mouse.Capture(null);if(Mouse.OverrideCursor==Cursors.ScrollNS)Mouse.OverrideCursor=previousCursor;}
  sealed class OriginMark:Adorner {
   readonly Point at;
   public OriginMark(UIElement owner,Point at):base(owner){this.at=at;IsHitTestVisible=false;}
   protected override void OnRender(DrawingContext dc){var ink=Theme.Brush("Text");var pen=new Pen(ink,1);dc.DrawEllipse(Theme.Brush("Surface"),new Pen(Theme.Brush("Border"),1),at,13,13);dc.DrawEllipse(ink,null,at,1.7,1.7);dc.DrawLine(pen,new Point(at.X-3,at.Y-6),new Point(at.X,at.Y-9));dc.DrawLine(pen,new Point(at.X,at.Y-9),new Point(at.X+3,at.Y-6));dc.DrawLine(pen,new Point(at.X-3,at.Y+6),new Point(at.X,at.Y+9));dc.DrawLine(pen,new Point(at.X,at.Y+9),new Point(at.X+3,at.Y+6));}
  }
 }
}
