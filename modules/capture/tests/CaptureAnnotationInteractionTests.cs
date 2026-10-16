using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;

// Real pointer/key entry points, inert windows and synthetic pixels only.
static class CaptureAnnotationInteractionTests {
    sealed class TestSource : PresentationSource {public override Visual RootVisual {get;set;}public override bool IsDisposed {get{return false;}}protected override CompositionTarget GetCompositionTargetCore(){return null;}}
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static object Call(CaptureWindow window,string name,params object[] args){return typeof(CaptureWindow).GetMethod(name,Hidden).Invoke(window,args);}
    static KeyEventArgs Key(CaptureWindow window,Key key){var e=new KeyEventArgs(Keyboard.PrimaryDevice,new TestSource(),Environment.TickCount,key){RoutedEvent=Keyboard.PreviewKeyDownEvent};Call(window,"KeyDownHandler",window,e);return e;}
    static byte[] Pixels(BitmapSource image){var pixels=new byte[image.PixelWidth*image.PixelHeight*4];image.CopyPixels(pixels,image.PixelWidth*4,0);return pixels;}
    internal static void Run(Action<bool,string> check){
        var settings=AppState.Settings;AppState.Settings=new Preferences {Stroke=4,Color="#EF4444"};
        try{
            foreach(string kind in new[]{"line","arrow","rect","ellipse","pen","mosaic"})Test(kind,check);
        }finally{AppState.Settings=settings;}
    }
    static void Test(string kind,Action<bool,string> check){
        var image=BitmapSource.Create(640,400,96,96,PixelFormats.Bgra32,null,new byte[640*400*4],2560);image.Freeze();
        var window=new CaptureWindow(new Snapshot {Image=image,Bounds=new System.Drawing.Rectangle(0,0,640,400)});
        var surface=((Grid)window.Content).Children.OfType<ScreenshotSurface>().Single();surface.Measure(new Size(640,400));surface.Arrange(new Rect(0,0,640,400));
        var crop=new Rect(20,20,600,360);surface.Selection=crop;
        try{
            Call(window,"SetTool",kind);window.BeginPointer(new Point(100,100));window.ContinuePointer(new Point(160,100));window.EndPointer(new Point(220,100+(kind=="line"||kind=="arrow"||kind=="pen"?0:80)));
            var mark=surface.Marks.Single();var start=mark.Start;var end=mark.End;
            check(mark.Kind==kind,kind+" is created through the actual drawing entry points");
            Call(window,"SetTool","select");
            var body=kind=="ellipse"?new Point(160,100):kind=="mosaic"?new Point(160,140):new Point(160,100);
            Call(window,"UpdateBrushCursor",body);check(surface.Cursor==Cursors.SizeAll,kind+" body advertises movement instead of moving the screenshot crop");
            window.BeginPointer(body);window.EndPointer(body+new Vector(25,18));
            check(surface.SelectedAnnotation==mark&&mark.Start==start+new Vector(25,18)&&mark.End==end+new Vector(25,18)&&surface.Selection==crop,kind+" movement edits the existing mark, not the crop or a duplicate");
            Call(window,"Undo");check(mark.Start==start&&mark.End==end&&surface.Marks.Single()==mark,kind+" move undo restores the same object");
            Call(window,"Redo");check(mark.Start==start+new Vector(25,18),kind+" move redo reapplies its displacement");Call(window,"Undo");
            window.BeginPointer(body);window.EndPointer(body);
            var handles=CaptureAnnotationGeometry.Handles(mark);var handle=handles[handles.Length-1];var beforeStart=mark.Start;var beforeEnd=mark.End;
            window.BeginPointer(handle);window.EndPointer(handle+new Vector(14,12));
            check(surface.Selection==crop&&surface.SelectedAnnotation==mark&&(mark.Start!=beforeStart||mark.End!=beforeEnd),kind+" visible adjustment handle edits its mark without resizing crop");
            Call(window,"Undo");check(mark.Start==beforeStart&&mark.End==beforeEnd,kind+" handle adjustment is one reversible change");
            window.BeginPointer(body);window.EndPointer(body);var baseline=Pixels(surface.Export());surface.SelectedAnnotation=null;
            check(baseline.SequenceEqual(Pixels(surface.Export())),kind+" selection outlines and handles never leak into exported pixels");
            window.BeginPointer(body);window.EndPointer(body);var nudge=Key(window,System.Windows.Input.Key.Right);
            check(nudge.Handled&&mark.Start==start+new Vector(1,0)&&surface.Selection==crop,kind+" arrow key nudges selected mark, not screenshot");Call(window,"Undo");
            window.BeginPointer(body);window.EndPointer(body);var wheel=new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,120){RoutedEvent=Mouse.MouseWheelEvent};double width=mark.Width;
            Call(window,"Wheel",surface,wheel);check(wheel.Handled&&mark.Width==width+1&&surface.Selection==crop,kind+" selected-mark wheel adjusts its width while using selection tool");Call(window,"Undo");
            window.BeginPointer(body);window.EndPointer(body);var deleted=Key(window,System.Windows.Input.Key.Delete);
            check(deleted.Handled&&surface.Marks.Count==0&&surface.Selection==crop,kind+" Delete removes only the selected mark");Call(window,"Undo");
            check(surface.Marks.Single()==mark&&mark.Start==start,kind+" Delete is reversible and preserves original annotation identity");
            Call(window,"SelectText",new object[]{null});var cropNudge=Key(window,System.Windows.Input.Key.Down);
            check(cropNudge.Handled&&surface.Selection==new Rect(20,21,600,360)&&mark.Start==start,kind+" arrow key nudges crop only when no annotation is selected");Call(window,"Undo");
            window.BeginPointer(crop.TopLeft);window.ContinuePointer(crop.TopLeft+new Vector(14,10));surface.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,Environment.TickCount){RoutedEvent=Mouse.LostMouseCaptureEvent});
            check(surface.Selection==crop&&surface.Marks.Single()==mark,kind+" unexpected loss of mouse capture safely rolls back an unfinished crop adjustment");
        }finally{window.Close();}
    }
}
