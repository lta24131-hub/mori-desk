using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;

// Inert pointer/keyboard fixtures only: windows are never shown, no desktop
// capture, native window handle, clipboard or installed data is used.
static class CaptureSelectionInteractionTests {
    sealed class TestSource : PresentationSource {public override Visual RootVisual {get;set;}public override bool IsDisposed {get{return false;}}protected override CompositionTarget GetCompositionTargetCore(){return null;}}
    static readonly BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static object Call(CaptureWindow window,string name,params object[] args){return typeof(CaptureWindow).GetMethod(name,Hidden).Invoke(window,args);}
    static int History(CaptureWindow window,string name="annotationUndo"){var value=typeof(CaptureWindow).GetField(name,Hidden).GetValue(window);return (int)value.GetType().GetProperty("Count").GetValue(value,null);}
    static bool Flag(CaptureWindow window,string name){return (bool)typeof(CaptureWindow).GetField(name,Hidden).GetValue(window);}
    static KeyEventArgs Key(CaptureWindow window,System.Windows.Input.Key key){var e=new KeyEventArgs(Keyboard.PrimaryDevice,new TestSource(),Environment.TickCount,key){RoutedEvent=Keyboard.PreviewKeyDownEvent};Call(window,"KeyDownHandler",window,e);return e;}
    sealed class Fixture : IDisposable {
        internal readonly CaptureWindow Window;
        internal readonly ScreenshotSurface Surface;
        internal readonly Rect Crop=new Rect(80,60,300,220);
        internal Annotation Mark;
        internal Fixture(bool addMark=true){
            var image=BitmapSource.Create(600,400,96,96,PixelFormats.Bgra32,null,new byte[600*400*4],2400);image.Freeze();
            Window=new CaptureWindow(new Snapshot {Image=image,Bounds=new System.Drawing.Rectangle(0,0,600,400)});
            Surface=((Grid)Window.Content).Children.OfType<ScreenshotSurface>().Single();Surface.Measure(new Size(600,400));Surface.Arrange(new Rect(0,0,600,400));Surface.Selection=Crop;
            if(addMark){
                Call(Window,"SetTool","arrow");Window.BeginPointer(new Point(130,120));Window.EndPointer(new Point(240,170));Mark=Surface.Marks.Single();Call(Window,"SetTool","select");
            }
        }
        public void Dispose(){Window.Close();AppState.Captures.Remove(Window);}
    }
    internal static void Run(Action<bool,string> check){
        var settings=AppState.Settings;AppState.Settings=new Preferences {Stroke=4,Color="#EF4444"};
        try{Reselect(check);ResizeUndo(check);Cancel(check);FlipCursors(check);}
        finally{AppState.Settings=settings;}
    }
    static void Reselect(Action<bool,string> check){
        using(var f=new Fixture()){
            var window=f.Window;var surface=f.Surface;int history=History(window);var outside=new Point(450,320);
            window.BeginPointer(outside);window.EndPointer(outside);
            check(surface.Selection==f.Crop&&surface.Marks.Count==1&&surface.Marks[0]==f.Mark&&History(window)==history,"outside tap retains the existing screenshot, annotation identities and undo history");
            window.BeginPointer(outside);window.EndPointer(outside+new Vector(25,0));
            check(surface.Selection==f.Crop&&surface.Marks.Single()==f.Mark&&History(window)==history,"invalid thin outside drag cannot erase a previous screenshot or its annotations");
            window.BeginPointer(outside);window.ContinuePointer(new Point(520,370));window.EndPointer(outside);
            check(surface.Selection==f.Crop&&surface.Marks.Single()==f.Mark&&History(window)==history,"reselection dragged back to origin restores the previous crop and annotations");
            var replacement=new Rect(430,300,100,70);window.BeginPointer(replacement.TopLeft);window.EndPointer(replacement.BottomRight);
            check(surface.Selection==replacement&&surface.Marks.Count==0&&History(window)==history+1,"intentional valid reselection replaces the crop and adds one reversible scene change");
            Call(window,"Undo");check(surface.Selection==f.Crop&&surface.Marks.Single()==f.Mark,"undo of a new crop restores the original annotation objects and screenshot region");
            Call(window,"Redo");check(surface.Selection==replacement&&surface.Marks.Count==0,"redo reapplies the replacement crop without stale annotations");
            Call(window,"Undo");Call(window,"Undo");check(surface.Selection==f.Crop&&surface.Marks.Count==0,"reselection does not destroy the older annotation undo entry");
        }
    }
    static void ResizeUndo(Action<bool,string> check){
        using(var f=new Fixture()){
            var window=f.Window;var surface=f.Surface;int before=History(window);var topLeft=surface.Handles()[0];
            window.BeginPointer(topLeft);window.EndPointer(topLeft+new Vector(15,12));var changed=new Rect(95,72,285,208);
            check(surface.Selection==changed&&surface.Marks.Single()==f.Mark&&History(window)==before+1,"crop resize preserves annotations and is recorded as exactly one undo step");
            Call(window,"Undo");check(surface.Selection==f.Crop&&surface.Marks.Single()==f.Mark,"undo restores crop dimensions without removing existing annotations");
            Call(window,"Redo");check(surface.Selection==changed&&surface.Marks.Single()==f.Mark,"redo restores crop dimensions and preserves annotation identity");
            var body=new Point(310,240);window.BeginPointer(body);window.EndPointer(body+new Vector(20,15));
            check(surface.Selection==new Rect(115,87,285,208)&&History(window)==before+2,"moving a crop creates a single scene undo step, separate from resize");
            Call(window,"Undo");check(surface.Selection==changed&&surface.Marks.Single()==f.Mark,"undo crop movement keeps the preceding resized region and annotations");
        }
    }
    static void Cancel(Action<bool,string> check){
        using(var f=new Fixture()){
            var window=f.Window;var surface=f.Surface;AppState.Captures.Add(window);int history=History(window);var topLeft=surface.Handles()[0];
            window.BeginPointer(topLeft);window.ContinuePointer(topLeft+new Vector(20,20));window.CancelFromPointer();
            check(AppState.Captures.Contains(window)&&surface.Image!=null&&surface.Selection==f.Crop&&surface.Marks.Single()==f.Mark&&!Flag(window,"dragging")&&History(window)==history,"right click during crop resizing cancels only the gesture and retains the screenshot session");
            window.BeginPointer(new Point(440,310));window.ContinuePointer(new Point(550,380));window.CancelFromPointer();
            check(surface.Selection==f.Crop&&surface.Marks.Single()==f.Mark&&!Flag(window,"dragging")&&History(window)==history,"right click during reselection restores the original crop and annotations without phantom history");
            Call(window,"SetTool","rect");window.BeginPointer(new Point(270,190));window.ContinuePointer(new Point(340,250));window.CancelFromPointer();
            check(AppState.Captures.Contains(window)&&surface.Selection==f.Crop&&surface.Marks.Single()==f.Mark&&surface.Draft==null&&!Flag(window,"dragging")&&History(window)==history,"right click during a new drawing cancels only the draft, keeping older annotations and screenshot");
            window.CancelFromPointer();check(window.SelectedTool=="select"&&surface.Selection==f.Crop,"right click exits a drawing tool before clearing the screenshot region");
            window.CancelFromPointer();check(surface.Selection.IsEmpty&&surface.Marks.Count==0&&AppState.Captures.Contains(window),"right click on a confirmed crop returns to region selection without ending capture");
            Call(window,"Undo");check(surface.Selection==f.Crop&&surface.Marks.Single()==f.Mark,"clearing a region with right click is reversible with its annotations intact");
        }
        foreach(string state in new[]{"crop","resize","reselect","draft"})using(var f=new Fixture()){
            var window=f.Window;var surface=f.Surface;AppState.Captures.Add(window);
            if(state=="resize"){var topLeft=surface.Handles()[0];window.BeginPointer(topLeft);window.ContinuePointer(topLeft+new Vector(20,20));}
            if(state=="reselect"){window.BeginPointer(new Point(440,310));window.ContinuePointer(new Point(550,380));}
            if(state=="draft"){Call(window,"SetTool","rect");window.BeginPointer(new Point(270,190));window.ContinuePointer(new Point(340,250));}
            var escaped=Key(window,System.Windows.Input.Key.Escape);
            check(escaped.Handled&&Flag(window,"closed")&&!AppState.Captures.Contains(window)&&surface.Image==null&&surface.Marks.Count==0&&surface.Draft==null&&!Flag(window,"dragging")&&History(window)==0,"one Escape immediately closes a screenshot and discards unfinished state: "+state);
        }
    }
    static void FlipCursors(Action<bool,string> check){
        using(var f=new Fixture(false)){
            f.Surface.Marks.Add(new Annotation {Kind="text",Start=new Point(180,140),Text="resize text",Width=4,Color="#EF4444"});
            var pressed=f.Surface.Handles()[0];f.Window.BeginPointer(pressed);f.Window.ContinuePointer(new Point(190,150));
            check(f.Surface.Cursor==Cursors.SizeNWSE,"active crop resize cursor cannot be stolen when its pointer crosses existing text");f.Window.EndPointer(new Point(190,150));
        }
        var corners=new[]{0,2,4,6};
        foreach(int handle in corners)foreach(int flip in new[]{0,1,2,3}){
            using(var f=new Fixture(false)){
                var surface=f.Surface;var window=f.Window;var pressed=surface.Handles()[handle];bool left=handle==0||handle==6,top=handle==0||handle==2;
                double anchorX=left?f.Crop.Right:f.Crop.Left,anchorY=top?f.Crop.Bottom:f.Crop.Top;
                bool currentLeft=left^((flip&1)!=0),currentTop=top^((flip&2)!=0);
                var point=new Point(anchorX+(currentLeft?-35:35),anchorY+(currentTop?-30:30));
                window.BeginPointer(pressed);window.ContinuePointer(point);
                var expected=currentLeft==currentTop?Cursors.SizeNWSE:Cursors.SizeNESW;
                check(surface.Cursor==expected,"active crop corner cursor follows horizontal/vertical direction flips: handle="+handle+" flip="+flip);
                window.EndPointer(point);check(surface.Selection==new Rect(new Point(Math.Min(point.X,anchorX),Math.Min(point.Y,anchorY)),new Point(Math.Max(point.X,anchorX),Math.Max(point.Y,anchorY))),"flipped crop retains normalized geometry without jumping: handle="+handle+" flip="+flip);
            }
        }
    }
}
