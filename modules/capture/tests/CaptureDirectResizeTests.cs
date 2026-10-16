using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;

// Synthetic images and unshown WPF windows only. All pointer coordinates enter
// the same production dispatcher as physical capture interactions.
static class CaptureDirectResizeTests {
    static readonly BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static object Call(CaptureWindow window,string name,params object[] args){return typeof(CaptureWindow).GetMethod(name,Hidden).Invoke(window,args);}
    static T Field<T>(CaptureWindow window,string name){return (T)typeof(CaptureWindow).GetField(name,Hidden).GetValue(window);}
    static int History(CaptureWindow window){var stack=typeof(CaptureWindow).GetField("annotationUndo",Hidden).GetValue(window);return (int)stack.GetType().GetProperty("Count").GetValue(stack,null);}
    sealed class Fixture : IDisposable {
        internal readonly CaptureWindow Window;
        internal readonly ScreenshotSurface Surface;
        internal readonly Rect Crop=new Rect(80,60,360,260);
        internal Fixture(int scale=1){
            var pixels=new byte[600*400*4];for(int i=3;i<pixels.Length;i+=4)pixels[i]=255;
            BitmapSource image=BitmapSource.Create(600,400,96,96,PixelFormats.Bgra32,null,pixels,2400);image.Freeze();
            if(scale!=1){image=new TransformedBitmap(image,new ScaleTransform(scale,scale));image.Freeze();}
            Window=new CaptureWindow(new Snapshot {Image=image,Bounds=new System.Drawing.Rectangle(0,0,600,400)});
            Surface=((Grid)Window.Content).Children.OfType<ScreenshotSurface>().Single();Surface.Measure(new Size(600,400));Surface.Arrange(new Rect(0,0,600,400));Surface.Selection=Crop;
        }
        internal void Tool(string tool){Call(Window,"SetTool",tool);}
        public void Dispose(){Window.Close();AppState.Captures.Remove(Window);}
    }
    internal static void Run(Action<bool,string> check){
        var settings=AppState.Settings;AppState.Settings=new Preferences {Stroke=4,Color="#EF4444"};
        try{AllTools(check);ContinuousBorder(check);InteriorDrawing(check);TextPriority(check);HistoryAndCancel(check);CrossingCorners(check);OffsetAndDpi(check);Toolbar(check);}
        finally{AppState.Settings=settings;}
    }
    static Cursor CursorFor(int handle){return handle==0||handle==4?Cursors.SizeNWSE:handle==2||handle==6?Cursors.SizeNESW:handle==1||handle==5?Cursors.SizeNS:Cursors.SizeWE;}
    static Rect Adjusted(Rect crop,int handle,Point point){
        double left=crop.Left,right=crop.Right,top=crop.Top,bottom=crop.Bottom;
        if(handle==0||handle==6||handle==7)left=point.X;if(handle==2||handle==3||handle==4)right=point.X;
        if(handle==0||handle==1||handle==2)top=point.Y;if(handle==4||handle==5||handle==6)bottom=point.Y;
        return new Rect(new Point(Math.Min(left,right),Math.Min(top,bottom)),new Point(Math.Max(left,right),Math.Max(top,bottom)));
    }
    static void AllTools(Action<bool,string> check){
        foreach(string tool in new[]{"select","rect","ellipse","arrow","line","pen","text","mosaic"})for(int handle=0;handle<8;handle++)using(var f=new Fixture()){
            f.Tool(tool);var start=f.Surface.Handles()[handle];var end=start+new Vector(13,11);
            Call(f.Window,"UpdateBrushCursor",start);
            check(f.Surface.ShowHandles&&f.Surface.Cursor==CursorFor(handle)&&f.Surface.BrushPoint==null,"crop handle advertises resize instead of drawing cursor: "+tool+" "+handle);
            check(f.Window.BeginPointer(start),"crop handle begins without switching annotation tools: "+tool+" "+handle);
            f.Window.ContinuePointer(end);
            check(f.Surface.Draft==null&&f.Surface.Marks.Count==0&&f.Surface.Selection==Adjusted(f.Crop,handle,end),"direct resize creates no annotation draft in "+tool+" "+handle);
            f.Window.EndPointer(end);
            check(f.Window.SelectedTool==tool&&History(f.Window)==1&&!Field<bool>(f.Window,"dragging"),"direct resize retains active tool and finishes as one undoable step: "+tool+" "+handle);
        }
    }
    static void ContinuousBorder(Action<bool,string> check){
        foreach(string tool in new[]{"rect","text","pen"})foreach(double part in new[]{.12,.25,.39,.63,.81,.91})for(int edge=0;edge<4;edge++)using(var f=new Fixture()){
            f.Tool(tool);var c=f.Crop;var p=edge==0?new Point(c.Left+c.Width*part,c.Top):edge==1?new Point(c.Right,c.Top+c.Height*part):edge==2?new Point(c.Left+c.Width*part,c.Bottom):new Point(c.Left,c.Top+c.Height*part);
            int expected=edge==0?1:edge==1?3:edge==2?5:7;
            check((int)Call(f.Window,"HitSelectionHandle",p)==expected,"complete crop edge, not just midpoint, is adjustable: "+tool+" "+edge+" "+part);
            Call(f.Window,"UpdateBrushCursor",p);check(f.Surface.Cursor==CursorFor(expected),"edge cursor agrees with actual resize target: "+tool+" "+edge+" "+part);
            f.Window.BeginPointer(p);f.Window.EndPointer(p+new Vector(9,8));check(f.Surface.Selection==Adjusted(c,expected,p+new Vector(9,8))&&f.Surface.Draft==null,"continuous edge drag changes crop rather than drawing: "+tool+" "+edge+" "+part);
        }
        using(var f=new Fixture()){
            f.Tool("pen");var c=f.Crop;
            foreach(double offset in new[]{-5.0,-2.0,0.0,2.0,5.0})check((int)Call(f.Window,"HitSelectionHandle",new Point(c.Left+71,c.Top+offset))==1,"top edge accepts narrow inside/outside hit band: "+offset);
            foreach(double offset in new[]{-6.0,6.0,15.0})check((int)Call(f.Window,"HitSelectionHandle",new Point(c.Left+71,c.Top+offset))<0,"crop edge does not swallow wider drawing or reselection space: "+offset);
            f.Surface.Selection=new Rect(100,100,12,10);var center=new Point(106,105);
            check((int)Call(f.Window,"HitSelectionHandle",center)<0,"small crop retains a usable interior rather than becoming entirely resize targets");
            f.Window.BeginPointer(center);f.Window.EndPointer(center+new Vector(2,2));check(f.Surface.Marks.Single().Kind=="pen","tiny crop interior still accepts a genuine drawing gesture");
        }
    }
    static void InteriorDrawing(Action<bool,string> check){
        foreach(string tool in new[]{"rect","ellipse","arrow","line","pen","mosaic"})using(var f=new Fixture()){
            f.Tool(tool);var start=new Point(130,67);var end=new Point(240,140);
            check((int)Call(f.Window,"HitSelectionHandle",start)<0,"drawing remains available only seven DIP inside the crop edge: "+tool);
            f.Window.BeginPointer(start);f.Window.EndPointer(end);
            check(f.Surface.Selection==f.Crop&&f.Surface.Marks.Single().Kind==tool&&History(f.Window)==1,"interior drag creates the intended annotation without moving its crop: "+tool);
        }
    }
    static void TextPriority(Action<bool,string> check){
        using(var f=new Fixture()){
            f.Tool("text");var mark=new Annotation {Kind="text",Text="text overlaps edge",Start=new Point(80,100),Width=4,Color="#EF4444"};f.Surface.Marks.Add(mark);Call(f.Window,"SelectText",mark);
            var p=new Point(80,110);Call(f.Window,"UpdateBrushCursor",p);check(f.Surface.Cursor==Cursors.SizeWE,"crop edge has cursor priority over an overlapping text frame");
            f.Window.BeginPointer(p);f.Window.EndPointer(p+new Vector(-12,0));
            check(f.Surface.Selection==new Rect(68,60,372,260)&&f.Surface.Marks.Single()==mark&&Field<TextBox>(f.Window,"textEditor")==null,"text tool adjusts outer crop without moving or reopening overlapping text");
        }
        using(var f=new Fixture()){
            f.Tool("text");f.Window.BeginPointer(new Point(140,100));Field<TextBox>(f.Window,"textEditor").Text="keep committed text";
            var p=f.Crop.TopRight;f.Window.BeginPointer(p);f.Window.EndPointer(p+new Vector(20,-10));
            check(f.Surface.Selection==new Rect(80,50,380,270)&&f.Surface.Marks.Single().Text=="keep committed text"&&f.Window.SelectedTool=="text","clicking a crop edge commits active text safely and resumes text tool after resize");
        }
    }
    static void HistoryAndCancel(Action<bool,string> check){
        using(var f=new Fixture()){
            var mark=new Annotation {Kind="arrow",Start=new Point(140,120),End=new Point(240,190),Width=4,Color="#EF4444"};f.Surface.Marks.Add(mark);f.Tool("pen");var start=f.Crop.BottomLeft;var end=start+new Vector(-15,20);
            f.Window.BeginPointer(start);f.Window.EndPointer(end);var resized=f.Surface.Selection;
            check(History(f.Window)==1&&f.Surface.Marks.Single()==mark,"cross-tool resize preserves previous annotations and has exactly one scene history entry");
            Call(f.Window,"Undo");check(f.Surface.Selection==f.Crop&&f.Surface.Marks.Single()==mark,"undo restores crop without deleting previous drawings");
            Call(f.Window,"Redo");check(f.Surface.Selection==resized&&f.Surface.Marks.Single()==mark,"redo restores resized crop with exact drawing identity");
            start=f.Surface.Selection.TopLeft;f.Window.BeginPointer(start);f.Window.ContinuePointer(start+new Vector(30,20));f.Window.CancelFromPointer();
            check(f.Surface.Selection==resized&&f.Surface.Marks.Single()==mark&&f.Window.SelectedTool=="pen"&&f.Surface.Draft==null&&History(f.Window)==1,"right click cancels only in-progress resize while retaining active pen and prior crop");
            f.Window.BeginPointer(new Point(160,160));f.Window.EndPointer(new Point(190,190));check(f.Surface.Marks.Count==2&&f.Surface.Marks.Last().Kind=="pen","drawing resumes immediately after direct resize cancellation");
        }
    }
    static void CrossingCorners(Action<bool,string> check){
        foreach(int handle in new[]{0,2,4,6})foreach(int flip in new[]{0,1,2,3})using(var f=new Fixture()){
            f.Tool("rect");bool left=handle==0||handle==6,top=handle==0||handle==2;double x=left?f.Crop.Right:f.Crop.Left,y=top?f.Crop.Bottom:f.Crop.Top;
            bool newLeft=left^((flip&1)!=0),newTop=top^((flip&2)!=0);var end=new Point(x+(newLeft?-30:30),y+(newTop?-25:25));
            f.Window.BeginPointer(f.Surface.Handles()[handle]);f.Window.ContinuePointer(end);
            check(f.Surface.Cursor==(newLeft==newTop?Cursors.SizeNWSE:Cursors.SizeNESW),"direct resize follows corner crossing under active rect tool: "+handle+" "+flip);
            f.Window.EndPointer(end);check(f.Surface.Selection==Adjusted(f.Crop,handle,end)&&f.Window.SelectedTool=="rect"&&f.Surface.Draft==null,"crossed crop stays normalized and keeps rect mode: "+handle+" "+flip);
        }
    }
    static void OffsetAndDpi(Action<bool,string> check){
        foreach(int scale in new[]{1,2})foreach(string tool in new[]{"rect","pen","text"})foreach(double offset in new[]{-4.0,4.0})using(var f=new Fixture(scale)){
            f.Tool(tool);var start=new Point(f.Crop.Left+70,f.Crop.Top+offset);int before=History(f.Window);
            f.Window.BeginPointer(start);f.Window.EndPointer(start);
            check(f.Surface.Selection==f.Crop&&History(f.Window)==before&&f.Surface.Draft==null,"edge-band tap never jumps crop or creates a history entry: "+scale+" "+tool+" "+offset);
            f.Window.BeginPointer(start);f.Window.EndPointer(start+new Vector(10,13));
            check(f.Surface.Selection==new Rect(80,73,360,247)&&History(f.Window)==before+1&&f.Window.SelectedTool==tool,"edge-band resize follows displacement rather than initial pointer offset: "+scale+" "+tool+" "+offset);
            var exported=f.Surface.Export(false,false);check(exported.PixelWidth==360*scale&&exported.PixelHeight==247*scale,"direct crop resize preserves physical-pixel resolution at display scale: "+scale+" "+tool+" "+offset);
            Call(f.Window,"Undo");start=new Point(f.Crop.Left+offset,f.Crop.Top+90);f.Window.BeginPointer(start);f.Window.EndPointer(new Point(-100,start.Y));
            check(f.Surface.Selection.Left==0&&f.Surface.Selection.Right==f.Crop.Right,"edge-band resize clamps at real image boundary after pointer leaves surface: "+scale+" "+tool+" "+offset);
        }
    }
    static void Toolbar(Action<bool,string> check){
        using(var f=new Fixture()){
            var bar=Field<StackPanel>(f.Window,"toolbar");check(!bar.Children.OfType<Button>().Any(b=>b.Name=="CaptureTool_select"),"toolbar has no redundant Select / Adjust entry");
            f.Tool("rect");Call(f.Window,"PositionTools");f.Tool("select");Call(f.Window,"PositionTools");
            check(f.Window.SelectedTool=="select"&&f.Surface.Selection==f.Crop,"internal V selection mode and adaptive toolbar remain usable without a Select button");
        }
    }
}
