using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;

internal static class CaptureDrawingExperienceTests {
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static object Call(CaptureWindow w,string method,params object[] args){return typeof(CaptureWindow).GetMethod(method,Hidden).Invoke(w,args);}
    static T Field<T>(CaptureWindow w,string name){return (T)typeof(CaptureWindow).GetField(name,Hidden).GetValue(w);}
    sealed class Fixture:IDisposable {
        internal CaptureWindow Window;internal ScreenshotSurface Surface;
        internal Fixture(){
            var bytes=Enumerable.Repeat((byte)255,800*560*4).ToArray();var image=BitmapSource.Create(800,560,96,96,PixelFormats.Bgra32,null,bytes,3200);image.Freeze();
            Window=new CaptureWindow(new Snapshot{Image=image,Bounds=new System.Drawing.Rectangle(0,0,800,560)},null,new CaptureFeatures{SmartSelection=false});Surface=Field<ScreenshotSurface>(Window,"surface");
            var root=(Grid)Window.Content;root.Measure(new Size(800,560));root.Arrange(new Rect(0,0,800,560));Surface.Selection=new Rect(20,20,760,480);Call(Window,"PositionTools");
        }
        internal void Tool(string name){Call(Window,"SetTool",name);}
        internal Annotation Draw(Point start,Point end){Window.BeginPointer(start);Window.EndPointer(end);return Surface.Marks.Last();}
        internal void Wheel(int delta,ModifierKeys modifier=ModifierKeys.Control){Window.HandleCaptureWheel(delta,modifier,new Point(420,300));}
        public void Dispose(){Window.Close();AppState.Captures.Remove(Window);}
    }
    internal static void Run(Action<bool,string> check){
        var settings=AppState.Settings;string path=Preferences.TestFilePath;string temp=Path.Combine(Path.GetTempPath(),"capture-drawing-"+Guid.NewGuid().ToString("N"),"settings.json");Preferences.TestFilePath=temp;
        try{AppState.Settings=new Preferences{Stroke=4,TextSize=20};ContinuousPen(check);Arrows(check);Wheel(check);}
        finally{AppState.Settings=settings;Preferences.TestFilePath=path;if(File.Exists(temp))File.Delete(temp);if(Directory.Exists(Path.GetDirectoryName(temp)))Directory.Delete(Path.GetDirectoryName(temp));}
    }
    static void ContinuousPen(Action<bool,string> check){
        using(var f=new Fixture()){
            f.Tool("pen");var first=f.Draw(new Point(100,100),new Point(250,180));var before=first.Points.ToArray();
            check(f.Surface.SelectedAnnotation==null&&f.Window.SelectedTool=="pen","finishing a stroke leaves no selection box and keeps pen active");
            foreach(var start in new[]{new Point(175,140),first.End,first.Bounds.TopLeft}){
                Call(f.Window,"UpdateBrushCursor",start);check(f.Surface.Cursor==Cursors.None&&f.Surface.BrushPoint==start,"pen cursor remains a brush over previous stroke or bounds");
                f.Draw(start,start+new Vector(55,60));
                check(first.Points.SequenceEqual(before)&&f.Surface.SelectedAnnotation==null,"overlapping stroke never moves/resizes the preceding stroke");
            }
            check(f.Surface.Marks.Count==4,"four overlapping gestures make four independent strokes");
            f.Window.HandleCaptureKey(Key.Z,ModifierKeys.Control);check(f.Surface.Marks.Count==3&&first.Points.SequenceEqual(before),"one Undo removes exactly the last stroke");
            f.Window.HandleCaptureKey(Key.Y,ModifierKeys.Control);check(f.Surface.Marks.Count==4,"Redo restores the last stroke");
            f.Tool("select");f.Window.BeginPointer(new Point(130,116));f.Window.EndPointer(new Point(140,126));
            check(f.Surface.SelectedAnnotation==first&&first.Start==new Point(110,110),"V mode still explicitly selects and moves the original pen stroke");
            f.Tool("pen");var crop=f.Surface.Selection;f.Window.BeginPointer(new Point(crop.Right,250));f.Window.EndPointer(new Point(crop.Right-10,250));
            check(f.Surface.Selection.Right==crop.Right-10&&f.Surface.Marks.Count==4&&f.Window.SelectedTool=="pen","crop edges remain directly resizable without leaving pen mode");
        }
    }
    static byte[] Pixels(BitmapSource image){var bytes=new byte[image.PixelWidth*image.PixelHeight*4];image.CopyPixels(bytes,image.PixelWidth*4,0);return bytes;}
    static void Arrows(Action<bool,string> check){
        using(var f=new Fixture()){
            var button=(Button)LogicalTreeHelper.FindLogicalNode(f.Window,"CaptureTool_arrow");button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var mark=f.Draw(new Point(100,150),new Point(400,150));
            check(mark.ArrowStyle=="filled"&&(bool)((Button)LogicalTreeHelper.FindLogicalNode(f.Window,"LineMode_arrowFilled")).Tag,"new capture defaults to the user's tapered filled arrow through the actual toolbar");
            mark.Width=8;var exported=f.Surface.Export();var pixels=Pixels(exported);
            Func<int,int> inkAt=x=>Enumerable.Range(110,40).Count(y=>pixels[(y*exported.PixelWidth+x)*4+1]<180);
            check(inkAt(100)<inkAt(300)&&inkAt(300)>=5,"filled arrow exported shaft visibly widens from the fine tail toward the head");
        }
        byte[] open=null,filled=null,doubled=null;
        foreach(string mode in new[]{"arrow","arrowFilled","arrowDouble"})using(var f=new Fixture()){
            f.Tool("arrow");Call(f.Window,"SetCaptureLineMode",mode);var mark=f.Draw(new Point(100,150),new Point(400,150));string style=mode=="arrowFilled"?"filled":mode=="arrowDouble"?"double":"open";
            check(mark.Kind=="arrow"&&mark.ArrowStyle==style&&f.Surface.SelectedAnnotation==mark,mode+" creates the chosen arrow ready for endpoint editing");
            var heads=CaptureArrowGeometry.Heads(mark);
            foreach(var head in heads)check(CaptureAnnotationGeometry.Hit(mark,head[1]),mode+" visible wing participates in hit testing");
            var pixels=Pixels(f.Surface.Export());if(style=="open")open=pixels;else if(style=="filled")filled=pixels;else doubled=pixels;
            f.Tool("select");f.Window.BeginPointer(new Point(250,150));f.Window.EndPointer(new Point(250,150));
            Call(f.Window,"SetCaptureLineMode",mode=="arrowFilled"?"arrowDouble":"arrowFilled");
            f.Window.HandleCaptureKey(Key.Z,ModifierKeys.Control);check(mark.ArrowStyle==style,"Undo restores arrow appearance: "+mode);
            f.Window.HandleCaptureKey(Key.Y,ModifierKeys.Control);check(mark.ArrowStyle!=style,"Redo restores changed arrow appearance: "+mode);
            f.Window.BeginPointer(mark.End);f.Window.EndPointer(new Point(430,180));check(mark.End==new Point(430,180),"arrow endpoint stays editable: "+mode);
            mark.End=mark.Start+new Vector(3,0);mark.Width=25;
            check(CaptureArrowGeometry.Heads(mark).SelectMany(h=>h).All(p=>p.X>=mark.Start.X&&p.X<=mark.End.X),"short thick arrowhead never extends behind its tail: "+mode);
            mark.End=mark.Start;check(CaptureArrowGeometry.Heads(mark).Length==0,"zero-length arrow has safe geometry: "+mode);Pixels(f.Surface.Export());
        }
        check(!open.SequenceEqual(filled)&&!open.SequenceEqual(doubled)&&!filled.SequenceEqual(doubled),"all three styles produce distinct final exported pixels");
    }
    static void Wheel(Action<bool,string> check){
        AppState.Settings.Stroke=4;
        using(var f=new Fixture()){
            f.Tool("pen");f.Wheel(30);f.Wheel(30);f.Wheel(30);check(AppState.Settings.Stroke==4,"sub-notch events accumulate without excessive jumps");f.Wheel(30);check(AppState.Settings.Stroke==5,"four quarter notches make one size step");
            f.Wheel(360);check(AppState.Settings.Stroke==8,"multi-notch wheel preserves full scroll distance");
            f.Wheel(90);f.Wheel(-30);check(AppState.Settings.Stroke==8,"reverse direction discards stale partial motion");f.Wheel(-90);check(AppState.Settings.Stroke==7,"reverse motion accumulates independently");
            f.Wheel(120,ModifierKeys.Control|ModifierKeys.Shift);check(AppState.Settings.Stroke==7.5&&Field<TextBlock>(f.Window,"widthLabel").Text=="粗细 7.5"&&f.Surface.SizeHint.Contains("7.5"),"fine adjustment and toolbar/cursor feedback preserve half-pixel size");
            check(!f.Window.HandleCaptureWheel(120,ModifierKeys.None,new Point(400,300))&&AppState.Settings.Stroke==7.5,"ordinary scrolling never changes brush size");
            f.Tool("text");f.Wheel(120);check(AppState.Settings.TextSize==22&&AppState.Settings.Stroke==7.5,"text wheel uses a small independent two-point step");f.Wheel(120,ModifierKeys.Control|ModifierKeys.Shift);check(AppState.Settings.TextSize==23,"text fine wheel uses one point");
            f.Tool("pen");f.Wheel(60);f.Tool("text");f.Wheel(60);check(AppState.Settings.TextSize==23,"partial deltas cannot leak between tool targets");
            f.Tool("pen");var mark=f.Draw(new Point(100,100),new Point(300,180));double original=mark.Width;
            f.Tool("select");f.Window.BeginPointer(new Point(200,140));f.Window.EndPointer(new Point(200,140));f.Wheel(120);f.Wheel(120);f.Wheel(120);
            check(mark.Width==original+3&&AppState.Settings.Stroke==original&&f.Surface.SizeHint.Contains((original+3).ToString("0.#")),"selected-stroke adjustment affects only that mark and reports actual size");
            f.Window.HandleCaptureKey(Key.Z,ModifierKeys.Control);check(mark.Width==original&&f.Surface.Marks.Count==1,"one Undo restores an entire wheel burst without deleting the stroke");
            f.Window.HandleCaptureKey(Key.Y,ModifierKeys.Control);check(mark.Width==original+3,"Redo restores the complete wheel burst");
            f.Tool("pen");f.Wheel(int.MaxValue);check(AppState.Settings.Stroke==25&&f.Surface.SizeHint.Contains("最大"),"large positive deltas clamp with maximum feedback");f.Wheel(int.MinValue);check(AppState.Settings.Stroke==1&&f.Surface.SizeHint.Contains("最小"),"large negative deltas clamp without integer overflow");
            typeof(CaptureWindow).GetField("collecting",Hidden).SetValue(f.Window,true);f.Wheel(120);check(AppState.Settings.Stroke==1,"pending output rejects wheel mutations");typeof(CaptureWindow).GetField("collecting",Hidden).SetValue(f.Window,false);
            f.Tool("mosaic");check(!f.Window.HandleCaptureWheel(120,ModifierKeys.Control,new Point(400,300)),"mosaic does not consume meaningless size changes");
        }
        check(Preferences.Load().Stroke==1&&Preferences.Load().TextSize==23,"closing capture flushes debounced settings so the last adjustment is retained");
    }
}
