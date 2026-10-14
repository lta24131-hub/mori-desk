using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Mori {
 // A held middle button drags the page itself, rather than starting browser-
 // style auto-scroll. Left-button text selection stays entirely independent.
 internal sealed class MiddleDragPan : IDisposable {
  readonly ScrollViewer scroll;readonly bool captureMouse;
  Point start;double originX,originY;object oldCursor;bool oldForce,disposed;
  internal bool Dragging {get;private set;}
  internal MiddleDragPan(ScrollViewer value,bool captureMouse=true){
   scroll=value;this.captureMouse=captureMouse;
   scroll.PreviewMouseDown+=Down;scroll.PreviewMouseMove+=Move;scroll.PreviewMouseUp+=Up;scroll.LostMouseCapture+=Lost;scroll.Unloaded+=Unloaded;
  }
  static bool InteractiveScrollPart(DependencyObject source,ScrollViewer host){for(var value=source;value!=null&&!ReferenceEquals(value,host);value=MainWindow.SelectionParent(value))if(value is ScrollBar||value is Thumb)return true;return false;}
  void Down(object sender,MouseButtonEventArgs e){if(e.ChangedButton!=MouseButton.Middle||InteractiveScrollPart(e.OriginalSource as DependencyObject,scroll))return;if(Begin(e.GetPosition(scroll)))e.Handled=true;}
  void Move(object sender,MouseEventArgs e){if(!Dragging)return;if(e.MiddleButton!=MouseButtonState.Pressed){End();return;}MoveTo(e.GetPosition(scroll));e.Handled=true;}
  void Up(object sender,MouseButtonEventArgs e){if(e.ChangedButton!=MouseButton.Middle||!Dragging)return;End();e.Handled=true;}
  void Lost(object sender,MouseEventArgs e){if(Dragging&&!scroll.IsMouseCaptured)End();}
  void Unloaded(object sender,RoutedEventArgs e){End();}
  internal bool Begin(Point point){
   if(disposed||Dragging||(scroll.ScrollableWidth<=0&&scroll.ScrollableHeight<=0))return false;
   start=point;originX=scroll.HorizontalOffset;originY=scroll.VerticalOffset;oldCursor=scroll.ReadLocalValue(FrameworkElement.CursorProperty);oldForce=scroll.ForceCursor;Dragging=true;
   scroll.Cursor=Cursors.SizeAll;scroll.ForceCursor=true;
   if(captureMouse&&!scroll.CaptureMouse()){End();return false;}return true;
  }
  internal static double Target(double origin,double delta,double maximum){return Math.Max(0,Math.Min(Math.Max(0,maximum),origin-delta));}
  internal void MoveTo(Point point){
   if(!Dragging||disposed)return;
   double rawX=originX-(point.X-start.X),rawY=originY-(point.Y-start.Y);
   double x=Target(originX,point.X-start.X,scroll.ScrollableWidth),y=Target(originY,point.Y-start.Y,scroll.ScrollableHeight);
   // Once an edge is reached, discard overshoot independently on each axis.
   // Reversing then moves immediately instead of retracing a long dead zone.
   if(rawX!=x){originX=x;start.X=point.X;}if(rawY!=y){originY=y;start.Y=point.Y;}
   scroll.ScrollToHorizontalOffset(x);scroll.ScrollToVerticalOffset(y);
  }
  internal void End(){if(!Dragging)return;Dragging=false;if(captureMouse&&scroll.IsMouseCaptured)scroll.ReleaseMouseCapture();if(oldCursor==DependencyProperty.UnsetValue)scroll.ClearValue(FrameworkElement.CursorProperty);else scroll.SetValue(FrameworkElement.CursorProperty,oldCursor);scroll.ForceCursor=oldForce;}
  public void Dispose(){if(disposed)return;End();disposed=true;scroll.PreviewMouseDown-=Down;scroll.PreviewMouseMove-=Move;scroll.PreviewMouseUp-=Up;scroll.LostMouseCapture-=Lost;scroll.Unloaded-=Unloaded;}
 }
}
