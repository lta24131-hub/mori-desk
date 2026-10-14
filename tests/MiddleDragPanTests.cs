using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Mori;

namespace MoriDesk {
 internal static class MiddleDragPanTests {
  static MouseButtonEventArgs ButtonEvent(MouseButton button,RoutedEvent routed){return new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,button){RoutedEvent=routed};}
  internal static void Run(Action<bool,string> check){
   var scroll=new ScrollViewer{Content=new Border{Width=1600,Height=2200},HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,CanContentScroll=false};
   Action layout=()=>{scroll.Measure(new Size(320,240));scroll.Arrange(new Rect(0,0,320,240));scroll.UpdateLayout();};layout();scroll.ScrollToHorizontalOffset(400);scroll.ScrollToVerticalOffset(500);layout();double x=scroll.HorizontalOffset,y=scroll.VerticalOffset;
   check(x==400&&y==500&&scroll.ScrollableWidth>0&&scroll.ScrollableHeight>0,"pan fixture has actual scrollable horizontal and vertical extents");
   using(var pan=new MiddleDragPan(scroll,false)){
    foreach(var button in new[]{MouseButton.Left,MouseButton.Right}){var ignored=ButtonEvent(button,UIElement.PreviewMouseDownEvent);scroll.RaiseEvent(ignored);check(!pan.Dragging&&!ignored.Handled,"left selection and right context menu are not intercepted by middle pan / "+button);}
    var down=ButtonEvent(MouseButton.Middle,UIElement.PreviewMouseDownEvent);scroll.RaiseEvent(down);check(pan.Dragging&&down.Handled,"real middle-button routed entry starts the page drag");var up=ButtonEvent(MouseButton.Middle,UIElement.PreviewMouseUpEvent);scroll.RaiseEvent(up);check(!pan.Dragging&&up.Handled&&scroll.Cursor==null,"real middle-button release restores the inherited cursor");
    pan.Begin(new Point(10,10));scroll.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,Environment.TickCount){RoutedEvent=UIElement.LostMouseCaptureEvent});check(!pan.Dragging&&scroll.Cursor==null&&!scroll.ForceCursor,"loss of mouse capture ends drag state and restores cursor");
    pan.Begin(new Point(10,10));scroll.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));check(!pan.Dragging&&scroll.Cursor==null&&!scroll.ForceCursor,"unloading preview ends drag state without waiting for another mouse move");
    var thumb=new Thumb();var excluded=ButtonEvent(MouseButton.Middle,UIElement.PreviewMouseDownEvent);excluded.Source=thumb;scroll.RaiseEvent(excluded);check(!pan.Dragging&&!excluded.Handled,"embedded scrollbar thumbs do not start page dragging");
    var scrollbar=ButtonEvent(MouseButton.Middle,UIElement.PreviewMouseDownEvent);scrollbar.Source=new ScrollBar();scroll.RaiseEvent(scrollbar);check(!pan.Dragging&&!scrollbar.Handled,"scrollbar surfaces retain their own middle-button behavior");
    check(pan.Begin(new Point(100,100))&&pan.Dragging&&scroll.Cursor==Cursors.SizeAll&&scroll.ForceCursor,"middle drag uses a distinct movement cursor without a desktop mouse capture in tests");
    pan.MoveTo(new Point(150,130));layout();check(scroll.HorizontalOffset==x-50&&scroll.VerticalOffset==y-30,"PDF/AI page follows pointer on both axes rather than auto-scrolling");
    pan.MoveTo(new Point(100,100));layout();check(scroll.HorizontalOffset==x&&scroll.VerticalOffset==y,"returning pointer to drag origin has no incremental drift");
    pan.MoveTo(new Point(-10000,-10000));layout();check(scroll.HorizontalOffset==scroll.ScrollableWidth&&scroll.VerticalOffset==scroll.ScrollableHeight,"middle pan clamps to document end");
    pan.MoveTo(new Point(-9990,-9990));layout();check(scroll.HorizontalOffset==scroll.ScrollableWidth-10&&scroll.VerticalOffset==scroll.ScrollableHeight-10,"reversing after dragging beyond document end responds immediately on both axes");
    pan.MoveTo(new Point(10000,10000));layout();check(scroll.HorizontalOffset==0&&scroll.VerticalOffset==0,"middle pan clamps to document start");
    pan.MoveTo(new Point(9990,9990));layout();check(scroll.HorizontalOffset==10&&scroll.VerticalOffset==10,"reversing after dragging beyond document start has no dead-zone travel");
    pan.End();check(!pan.Dragging&&!scroll.ForceCursor&&scroll.ReadLocalValue(FrameworkElement.CursorProperty)==DependencyProperty.UnsetValue,"releasing middle button restores inherited cursor and leaves no forced cursor behind");
    scroll.Cursor=Cursors.IBeam;scroll.ForceCursor=true;check(pan.Begin(new Point(10,10)),"page can begin another middle drag after release");pan.End();check(scroll.Cursor==Cursors.IBeam&&scroll.ForceCursor,"existing text-selection cursor and force state survive page dragging");
    pan.Begin(new Point(10,10));pan.Dispose();check(!pan.Dragging&&scroll.Cursor==Cursors.IBeam&&!pan.Begin(new Point(10,10)),"disposing during a drag releases state and prevents late input");
    var late=ButtonEvent(MouseButton.Middle,UIElement.PreviewMouseDownEvent);scroll.RaiseEvent(late);check(!late.Handled&&!pan.Dragging&&scroll.Cursor==Cursors.IBeam,"disposed middle pan removes routed handlers and leaves late input untouched");
   }
   var fit=new ScrollViewer{Content=new Border{Width=30,Height=30}};fit.Measure(new Size(300,200));fit.Arrange(new Rect(0,0,300,200));fit.UpdateLayout();using(var pan=new MiddleDragPan(fit,false))check(!pan.Begin(new Point(10,10))&&fit.Cursor==null,"a fully fitting page does not capture middle button or change cursor");
   check(MiddleDragPan.Target(50,100,300)==0&&MiddleDragPan.Target(50,-1000,300)==300,"scroll target arithmetic remains bounded at both edges");
   using(var view=new PdfContinuousView()){var field=typeof(PdfContinuousView).GetField("middlePan",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);check(field.GetValue(view) is MiddleDragPan,"the actual shared PDF/AI continuous view is connected to middle drag");view.SetTextSelection(true);check(view.TextSelectionEnabled&&string.IsNullOrEmpty(view.SelectedText),"adding middle drag leaves left-button text-selection mode independent");}
  }
 }
}
