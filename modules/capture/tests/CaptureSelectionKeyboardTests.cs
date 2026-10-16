using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;

// Synthetic STA fixtures only. These windows stay unshown and never acquire a
// native window handle; the source image is generated entirely in memory.
static class CaptureSelectionKeyboardTests {
    static readonly BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static object Call(CaptureWindow window,string name,params object[] args){return typeof(CaptureWindow).GetMethod(name,Hidden).Invoke(window,args);}
    static void Set(CaptureWindow window,string name,object value){typeof(CaptureWindow).GetField(name,Hidden).SetValue(window,value);}
    static int History(CaptureWindow window,string name="annotationUndo"){var stack=typeof(CaptureWindow).GetField(name,Hidden).GetValue(window);return (int)stack.GetType().GetProperty("Count").GetValue(stack,null);}
    static bool PreservesInk(BitmapSource before,BitmapSource after){
        var original=new byte[before.PixelWidth*before.PixelHeight*4];var adjusted=new byte[after.PixelWidth*after.PixelHeight*4];
        before.CopyPixels(original,before.PixelWidth*4,0);after.CopyPixels(adjusted,after.PixelWidth*4,0);bool hasInk=false;
        for(int y=0;y<before.PixelHeight;y++)for(int x=0;x<before.PixelWidth;x++){
            int index=(y*before.PixelWidth+x)*4;if(original[index+3]==0)continue;hasInk=true;
            if(x>=after.PixelWidth||y>=after.PixelHeight)return false;
            int target=(y*after.PixelWidth+x)*4;for(int channel=0;channel<4;channel++)if(original[index+channel]!=adjusted[target+channel])return false;
        }
        return hasInk;
    }
    sealed class Fixture : IDisposable {
        internal readonly CaptureWindow Window;
        internal readonly ScreenshotSurface Surface;
        internal readonly Rect Crop=new Rect(80,60,300,220);
        internal Fixture(int scale=1){
            int width=600*scale,height=400*scale;
            var image=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,new byte[width*height*4],width*4);image.Freeze();
            Window=new CaptureWindow(new Snapshot {Image=image,Bounds=new System.Drawing.Rectangle(0,0,width,height)});
            Surface=((Grid)Window.Content).Children.OfType<ScreenshotSurface>().Single();Surface.Measure(new Size(600,400));Surface.Arrange(new Rect(0,0,600,400));Surface.Selection=Crop;
        }
        internal bool Key(Key key,ModifierKeys modifiers=ModifierKeys.None){return Window.AdjustCropWithKeyboard(key,modifiers);}
        public void Dispose(){Window.Close();AppState.Captures.Remove(Window);}
    }
    internal static void Run(Action<bool,string> check){
        check(Thread.CurrentThread.GetApartmentState()==ApartmentState.STA,"crop keyboard fixtures run on an STA thread");
        var settings=AppState.Settings;AppState.Settings=new Preferences {Stroke=4,Color="#EF4444"};
        try{MovementAndResize(check);Boundaries(check);AnnotationProtection(check);Gates(check);LogicalCoordinates(check);}
        finally{AppState.Settings=settings;}
    }
    static void MovementAndResize(Action<bool,string> check){
        var keys=new[]{Key.Left,Key.Right,Key.Up,Key.Down};
        foreach(var modifiers in new[]{ModifierKeys.None,ModifierKeys.Control,ModifierKeys.Shift,ModifierKeys.Control|ModifierKeys.Shift})foreach(var key in keys){
            using(var f=new Fixture()){
                int step=(modifiers&ModifierKeys.Control)!=0?10:1;
                int dx=key==Key.Left?-step:key==Key.Right?step:0,dy=key==Key.Up?-step:key==Key.Down?step:0;
                bool resize=(modifiers&ModifierKeys.Shift)!=0;
                var expected=resize?new Rect(80,60,300+dx,220+dy):new Rect(80+dx,60+dy,300,220);
                check(f.Key(key,modifiers)&&f.Surface.Selection==expected&&History(f.Window)==1,"crop arrows use documented movement/resize direction and step: "+modifiers+" "+key);
                Call(f.Window,"Undo");check(f.Surface.Selection==f.Crop&&History(f.Window,"annotationRedo")==1,"keyboard crop adjustment is one reversible scene edit: "+modifiers+" "+key);
                Call(f.Window,"Redo");check(f.Surface.Selection==expected,"redo restores exact keyboard crop geometry: "+modifiers+" "+key);
            }
        }
        using(var f=new Fixture()){
            f.Key(Key.Right);Call(f.Window,"Undo");f.Key(Key.Down);
            check(History(f.Window)==1&&History(f.Window,"annotationRedo")==0,"a fresh crop adjustment after undo discards the obsolete redo branch");
        }
    }
    static void Boundaries(Action<bool,string> check){
        using(var f=new Fixture()){
            f.Surface.Selection=new Rect(0,0,600,400);
            foreach(var key in new[]{Key.Left,Key.Right,Key.Up,Key.Down})check(f.Key(key,ModifierKeys.Control)&&f.Surface.Selection==new Rect(0,0,600,400)&&History(f.Window)==0,"crop movement at surface edge consumes the arrow without no-op history: "+key);
            f.Surface.Selection=new Rect(597,397,3,3);
            foreach(var key in new[]{Key.Left,Key.Up})check(f.Key(key,ModifierKeys.Control|ModifierKeys.Shift)&&f.Surface.Selection==new Rect(597,397,3,3)&&History(f.Window)==0,"keyboard crop shrink retains the valid 3-DIP minimum: "+key);
            foreach(var key in new[]{Key.Right,Key.Down})check(f.Key(key,ModifierKeys.Control|ModifierKeys.Shift)&&f.Surface.Selection==new Rect(597,397,3,3)&&History(f.Window)==0,"keyboard crop growth cannot exceed the snapshot surface: "+key);
            f.Surface.Selection=new Rect(2,2,598,398);f.Key(Key.Left,ModifierKeys.Control);f.Key(Key.Up,ModifierKeys.Control);
            check(f.Surface.Selection==new Rect(0,0,598,398)&&History(f.Window)==2,"accelerated crop movement clamps to the remaining distance rather than overshooting");
            f.Key(Key.Right,ModifierKeys.Control|ModifierKeys.Shift);f.Key(Key.Down,ModifierKeys.Control|ModifierKeys.Shift);
            check(f.Surface.Selection==new Rect(0,0,600,400),"accelerated crop resizing clamps to the snapshot edge");
        }
    }
    static void AnnotationProtection(Action<bool,string> check){
        using(var f=new Fixture()){
            var mark=new Annotation {Kind="rect",Start=new Point(100,100),End=new Point(200,200),Width=12};f.Surface.Marks.Add(mark);
            f.Key(Key.Right,ModifierKeys.Control);f.Key(Key.Right,ModifierKeys.Control);
            check(f.Surface.Selection==new Rect(94,60,300,220)&&mark.Start==new Point(100,100)&&mark.End==new Point(200,200),"crop movement stops at a thick annotation's outer stroke without moving the mark");
            int history=History(f.Window);f.Key(Key.Right);
            check(f.Surface.Selection.Left==94&&History(f.Window)==history,"a protected annotation boundary does not create phantom crop history");
            Call(f.Window,"Undo");Call(f.Window,"Undo");
            check(f.Surface.Selection==f.Crop&&f.Surface.Marks.Single()==mark,"undo restores the crop while preserving the original annotation identity");
            for(int i=0;i<30;i++)f.Key(Key.Left,ModifierKeys.Control|ModifierKeys.Shift);
            for(int i=0;i<30;i++)f.Key(Key.Up,ModifierKeys.Control|ModifierKeys.Shift);
            check(f.Surface.Selection==new Rect(80,60,126,146)&&mark.Width==12,"keyboard crop shrink preserves the complete outer stroke on right and bottom edges");
        }
        using(var f=new Fixture()){
            var line=new Annotation {Kind="line",Start=new Point(70,110),End=new Point(150,110),Width=8};f.Surface.Marks.Add(line);
            check(f.Key(Key.Right)&&f.Surface.Selection==f.Crop&&History(f.Window)==0,"an annotation already clipped by the old crop cannot lose any more of its visible ink");
            f.Key(Key.Left);
            check(f.Surface.Selection.Left==79&&f.Surface.Marks.Single()==line,"a crop may expand visibility of an annotation that already extends beyond its edge");
        }
        using(var f=new Fixture()){
            // The short arrow's head reaches well beyond its endpoint rectangle.
            var arrow=new Annotation {Kind="arrow",Start=new Point(300,150),End=new Point(295,150),Width=12};f.Surface.Marks.Add(arrow);
            for(int i=0;i<30;i++)f.Key(Key.Left,ModifierKeys.Control|ModifierKeys.Shift);
            check(f.Surface.Selection.Right>337&&f.Surface.Selection.Right<338&&arrow.End==new Point(295,150),"crop shrink protects arrowhead ink beyond the arrow's endpoint bounds");
        }
        using(var f=new Fixture()){
            var pen=new Annotation {Kind="pen",Start=new Point(200,130),End=new Point(210,140),Width=10};pen.Points.Add(new Point(120,100));pen.Points.Add(new Point(250,210));f.Surface.Marks.Add(pen);
            var original=f.Surface.Export();
            for(int i=0;i<30;i++)f.Key(Key.Left,ModifierKeys.Control|ModifierKeys.Shift);
            // WPF's widened diagonal stroke bounds conservatively exceed the
            // analytical cap extremum by about 0.000107 DIP. Never undercut it.
            check(f.Surface.Selection.Right>=255&&f.Surface.Selection.Right<255.001&&pen.Points[1]==new Point(250,210),"crop shrink protects actual freehand points and their round caps");
            check(PreservesInk(original,f.Surface.Export()),"shrinking around a freehand stroke retains every exported ink pixel and color channel");
        }
    }
    static void Gates(Action<bool,string> check){
        using(var f=new Fixture()){
            check(!f.Key(Key.A)&&!f.Key(Key.Right,ModifierKeys.Alt)&&!f.Key(Key.Right,ModifierKeys.Windows)&&History(f.Window)==0,"crop controls ignore non-arrows and system modifiers");
            var mark=new Annotation {Kind="rect",Start=new Point(100,100),End=new Point(180,180)};f.Surface.Marks.Add(mark);f.Surface.SelectedAnnotation=mark;
            check(!f.Key(Key.Right)&&f.Surface.Selection==f.Crop&&mark.Start==new Point(100,100),"crop controls defer while a shape annotation is selected");f.Surface.SelectedAnnotation=null;
            f.Surface.SelectedText=new Annotation {Kind="text",Start=new Point(100,100),Text="Selected"};
            check(!f.Key(Key.Right)&&f.Surface.Selection==f.Crop,"crop controls defer while a text annotation is selected");f.Surface.SelectedText=null;
            Set(f.Window,"textEditor",new TextBox {Text="Editing"});
            check(!f.Key(Key.Right)&&f.Surface.Selection==f.Crop,"crop controls leave text-editor arrows untouched");Set(f.Window,"textEditor",null);
            Set(f.Window,"dragging",true);check(!f.Key(Key.Right)&&f.Surface.Selection==f.Crop,"crop controls cannot interfere with an active pointer gesture");Set(f.Window,"dragging",false);
            Set(f.Window,"collecting",true);check(!f.Key(Key.Right)&&f.Surface.Selection==f.Crop,"crop controls cannot change an image during collection");Set(f.Window,"collecting",false);
            Call(f.Window,"SetTool","rect");check(!f.Key(Key.Right)&&f.Surface.Selection==f.Crop,"crop arrows are available only in the selection tool");Call(f.Window,"SetTool","select");
            f.Surface.Selection=Rect.Empty;f.Surface.HoverSelection=f.Crop;
            check(!f.Key(Key.Right)&&f.Surface.Selection.IsEmpty&&History(f.Window)==0,"a hover preview is never promoted to a confirmed crop by an arrow key");
        }
    }
    static void LogicalCoordinates(Action<bool,string> check){
        using(var f=new Fixture(2)){
            f.Key(Key.Right);var moved=f.Surface.Export(false,false);
            check(f.Surface.Selection==new Rect(81,60,300,220)&&moved.PixelWidth==600&&moved.PixelHeight==440,"crop arrows move one logical pixel on a 200-percent snapshot without changing export dimensions");
            f.Key(Key.Right,ModifierKeys.Shift);var resized=f.Surface.Export(false,false);
            check(f.Surface.Selection==new Rect(81,60,301,220)&&resized.PixelWidth==602&&resized.PixelHeight==440,"keyboard resize shares logical crop geometry with scaled bitmap export");
        }
    }
}
