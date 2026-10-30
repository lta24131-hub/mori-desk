using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;

static class CaptureTextEditingTests {
    sealed class TestSource : PresentationSource {public override Visual RootVisual {get;set;}public override bool IsDisposed {get{return false;}}protected override CompositionTarget GetCompositionTargetCore(){return null;}}
    static readonly BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    static object Call(CaptureWindow window,string method,params object[] args){return typeof(CaptureWindow).GetMethod(method,Private).Invoke(window,args);}
    static TextBox Editor(CaptureWindow window){return (TextBox)typeof(CaptureWindow).GetField("textEditor",Private).GetValue(window);}
    static int History(CaptureWindow window){var value=typeof(CaptureWindow).GetField("annotationUndo",Private).GetValue(window);return (int)value.GetType().GetProperty("Count").GetValue(value,null);}
    static Point Body(Annotation mark){var bounds=mark.Bounds;return new Point(bounds.Left+bounds.Width*.3,bounds.Top+bounds.Height*.5);}
    static void Select(CaptureWindow window,Annotation mark){var point=Body(mark);window.BeginPointer(point);window.EndPointer(point);}
    // The user's own modifier keys cannot alter an inert editor regression.
    static KeyEventArgs Key(CaptureWindow window,System.Windows.Input.Key key){var e=new KeyEventArgs(Keyboard.PrimaryDevice,new TestSource(),Environment.TickCount,key){RoutedEvent=Keyboard.PreviewKeyDownEvent};e.Handled=window.HandleCaptureKey(key,ModifierKeys.None);return e;}
    static bool Pixels(BitmapSource a,BitmapSource b){if(a.PixelWidth!=b.PixelWidth||a.PixelHeight!=b.PixelHeight)return false;var x=new byte[a.PixelWidth*a.PixelHeight*4];var y=new byte[x.Length];a.CopyPixels(x,a.PixelWidth*4,0);b.CopyPixels(y,b.PixelWidth*4,0);return x.SequenceEqual(y);}
    internal static void Run(Action<bool,string> check){
        var preferences=AppState.Settings;AppState.Settings=new Preferences {Stroke=4,Color="#EF4444"};
        var raw=new byte[600*400*4];for(int i=0;i<raw.Length;i+=4){raw[i]=235;raw[i+1]=235;raw[i+2]=235;raw[i+3]=255;}
        var image=BitmapSource.Create(600,400,96,96,PixelFormats.Bgra32,null,raw,2400);image.Freeze();
        var window=new CaptureWindow(new Snapshot {Image=image,Bounds=new System.Drawing.Rectangle(0,0,600,400)});
        var surface=((Grid)window.Content).Children.OfType<ScreenshotSurface>().Single();surface.Measure(new Size(600,400));surface.Arrange(new Rect(0,0,600,400));
        var crop=new Rect(20,20,560,350);surface.Selection=crop;
        try{
            var sample=new Annotation {Kind="text",Start=new Point(40,60),Width=4,Text="多行 Text  \n第二行"};
            var metrics=new FormattedText(sample.Text,CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,new Typeface("Microsoft YaHei UI"),sample.TextFontSize,Brushes.Black);
            check(sample.Bounds==new Rect(sample.Start,new Size(metrics.WidthIncludingTrailingWhitespace,metrics.Height))&&sample.Bounds.Height>sample.TextFontSize*2,"text hit bounds share exact multilingual, multiline and trailing-space export metrics without relying on End");
            Call(window,"SetTool","text");window.BeginPointer(new Point(160,140));var editor=Editor(window);
            check(editor!=null&&editor.FontFamily.Source=="Microsoft YaHei UI"&&editor.FontSize==20,"text creation opens a matching-font editor through the production pointer path");
            editor.Text="文字 / Annotation";Call(window,"CommitText");var mark=surface.Marks.Single();
            check(mark.Kind=="text"&&mark.Text=="文字 / Annotation"&&surface.SelectedText==mark&&surface.Selection==crop,"committed text remains selected with measurable bounds and leaves screenshot crop unchanged");
            Call(window,"SetTool","select");var origin=mark.Start;var point=Body(mark);window.BeginPointer(point);window.ContinuePointer(point+new Vector(35,25));window.EndPointer(point+new Vector(35,25));
            check(mark.Start==origin+new Vector(35,25)&&surface.Selection==crop&&surface.Marks.Count==1,"select tool drags only the existing text rather than moving the screenshot crop or creating a duplicate");
            Call(window,"Undo");check(mark.Start==origin&&surface.Marks.Single()==mark&&surface.Selection==crop,"Ctrl+Z path restores a text move without deleting the annotation");
            Call(window,"Redo");check(mark.Start==origin+new Vector(35,25)&&surface.Marks.Single()==mark,"Ctrl+Y path reapplies text movement on the same annotation");Call(window,"Undo");
            var directions=new[]{new Vector(-20,-12),new Vector(0,-12),new Vector(20,-12),new Vector(20,0),new Vector(20,12),new Vector(0,12),new Vector(-20,12),new Vector(-20,0)};
            var cursors=new[]{Cursors.SizeNWSE,Cursors.SizeNS,Cursors.SizeNESW,Cursors.SizeWE,Cursors.SizeNWSE,Cursors.SizeNS,Cursors.SizeNESW,Cursors.SizeWE};
            for(int i=0;i<8;i++){
                Select(window,mark);var oldStart=mark.Start;var oldWidth=mark.Width;var bounds=ScreenshotSurface.TextFrame(mark);var handle=ScreenshotSurface.Handles(bounds)[i];
                Call(window,"UpdateBrushCursor",handle);check(surface.Cursor==cursors[i],"text resize handle has the matching directional hover cursor: "+i);
                window.BeginPointer(handle);window.ContinuePointer(handle+directions[i]);Call(window,"UpdateBrushCursor",new Point(590,390));
                check(surface.Cursor==cursors[i],"active text resize keeps its directional cursor away from the handle: "+i);
                window.EndPointer(handle+directions[i]);var changed=mark.Width;
                check(changed>oldWidth&&surface.Selection==crop&&surface.Marks.Count==1&&surface.Draft==null,"text handle resizes the existing annotation without changing crop or creating a drawing: "+i);
                Call(window,"Undo");check(mark.Start==oldStart&&mark.Width==oldWidth,"text resize is one reversible operation: "+i);
                Call(window,"Redo");check(mark.Width==changed,"text resize redo restores the new font size: "+i);Call(window,"Undo");
            }
            Select(window,mark);point=Body(mark);window.BeginPointer(point);window.EndPointer(new Point(-200,-300));
            check(mark.Bounds.Left>=crop.Left&&mark.Bounds.Top>=crop.Top&&surface.Selection==crop,"text movement clamps at crop bounds without resizing the screenshot");Call(window,"Undo");
            Select(window,mark);int beforeClick=History(window);point=Body(mark);window.BeginPointer(point);window.EndPointer(point);
            check(History(window)==beforeClick,"plain selection without movement adds no phantom undo operation");
            Call(window,"SetTool","rect");var savedText=mark.Text;var savedStart=mark.Start;var drawPoint=Body(mark);window.BeginPointer(drawPoint,1,ModifierKeys.Alt);window.EndPointer(drawPoint+new Vector(35,30));
            check(surface.Marks.Count==2&&surface.Marks[1].Kind=="rect"&&mark.Start==savedStart&&mark.Text==savedText,"Alt explicitly draws over text without changing its existing annotation");
            Call(window,"Undo");check(surface.Marks.Single()==mark,"undo ordering removes the later drawing before any older text edits");
            Call(window,"SetTool","select");Select(window,mark);check(surface.SelectedText==mark,"text can be reselected naturally after switching to another drawing tool and back");
            Call(window,"SetTool","text");Select(window,mark);check(surface.SelectedText==mark&&Editor(window)==null&&surface.Marks.Count==1,"text-tool single click selects an existing text instead of creating a second editor");
            var baseline=surface.Export();surface.SelectedText=null;check(Pixels(baseline,surface.Export()),"text selection frame and handles never appear in exported screenshot pixels");Select(window,mark);
            point=Body(mark);window.BeginPointer(point,2);editor=Editor(window);var originalWidth=mark.Width;int beforeEditor=History(window);
            check(editor!=null&&editor.Text==savedText&&surface.EditingText==mark&&surface.Marks.Count==1,"double click reopens the same text in place rather than finishing the screenshot");
            editor.Text="未提交的新字";bool adjusted=window.HandleCaptureWheel(120,ModifierKeys.Control,new Point(250,200));
            check(adjusted&&editor.FontSize==mark.TextFontSize+2&&mark.Width==originalWidth&&History(window)==beforeEditor,"editing Control-wheel changes only the live editor and creates no hidden-mark or ghost undo change");
            var editKey=Key(window,System.Windows.Input.Key.Z);check(!editKey.Handled&&Editor(window)==editor&&History(window)==beforeEditor,"window leaves text-editor undo keystrokes unhandled instead of applying annotation undo");
            window.CancelFromPointer();check(mark.Text=="未提交的新字"&&Math.Abs(mark.Width-originalWidth-.4)<.0001&&Editor(window)==null&&History(window)==beforeEditor+1&&surface.SelectedText==mark&&window.SelectedTool=="select","right click completes reopened text with its new size and preserves selection for adjustment");
            Call(window,"Undo");check(mark.Text==savedText&&mark.Width==originalWidth&&History(window)==beforeEditor&&Pixels(baseline,surface.Export()),"one Undo restores original text, size and exported pixels after right-click completion");
            window.BeginPointer(Body(mark),2);editor=Editor(window);editor.Text="已修改\n第二行 / Edited";window.HandleCaptureWheel(120,ModifierKeys.Control,new Point(250,200));Call(window,"CommitText");
            check(surface.Marks.Count==1&&surface.Marks[0]==mark&&mark.Text=="已修改\n第二行 / Edited"&&Math.Abs(mark.Width-originalWidth-.4)<.0001&&surface.Selection==crop,"committing edited multiline text updates the existing item once without changing the crop");
            Call(window,"Undo");check(mark.Text==savedText&&mark.Width==originalWidth,"text content and font-size editing undo together as one operation");Call(window,"Redo");check(mark.Text.Contains("Edited")&&Math.Abs(mark.Width-originalWidth-.4)<.0001,"redo restores the complete committed text edit");Call(window,"Undo");
            Select(window,mark);window.BeginPointer(Body(mark),2);Editor(window).Text="";Call(window,"CommitText");check(surface.Marks.Count==0,"committing an empty reopened text removes only that annotation");Call(window,"Undo");check(surface.Marks.Single()==mark&&mark.Text==savedText,"undo restores an emptied text with its original content");
            Select(window,mark);var delete=Key(window,System.Windows.Input.Key.Delete);check(delete.Handled&&surface.Marks.Count==0&&surface.Selection==crop,"Delete removes a selected text without changing screenshot crop");Call(window,"Undo");check(surface.Marks.Single()==mark,"deleted text remains undoable");
            // A short one-character label must still have a body for movement and
            // double-click editing, rather than being swallowed by eight handles.
            mark.Text="字";Select(window,mark);point=new Point(mark.Bounds.Left+mark.Bounds.Width/2,mark.Bounds.Top+mark.Bounds.Height/2);var shortStart=mark.Start;
            window.BeginPointer(point);window.EndPointer(point+new Vector(12,8));check(mark.Start==shortStart+new Vector(12,8)&&mark.Width==originalWidth,"a single-character text has an unambiguous draggable interior despite its resize handles");
            window.BeginPointer(new Point(mark.Bounds.Left+mark.Bounds.Width/2,mark.Bounds.Top+mark.Bounds.Height/2),2);check(Editor(window)!=null&&Editor(window).Text=="字","double click on a tiny selected label still edits it");Call(window,"CancelText");
            foreach(double fontSize in new[]{16.0,20.0})foreach(string narrow in new[]{"i","."}){
                mark.Text=narrow;mark.Width=fontSize/5;Select(window,mark);point=new Point(mark.Bounds.Left+mark.Bounds.Width/2,mark.Bounds.Top+mark.Bounds.Height/2);shortStart=mark.Start;
                var frame=ScreenshotSurface.TextFrame(mark);check(frame.Width>=28&&frame.Height>=28&&frame.Contains(mark.Bounds),"tiny text gets the same readable minimum frame for rendering and hit testing: "+narrow+" "+fontSize);
                var smallHandle=ScreenshotSurface.Handles(frame)[3];var noJump=mark.Width;window.BeginPointer(smallHandle);window.EndPointer(smallHandle);check(mark.Width==noJump,"pressing a padded text-frame handle without moving never jumps the font size: "+narrow+" "+fontSize);
                Call(window,"UpdateBrushCursor",point);check(surface.Cursor==Cursors.SizeAll,"narrow text keeps a move-cursor interior instead of being swallowed by handles: "+narrow+" "+fontSize);
                window.BeginPointer(point);window.EndPointer(point+new Vector(12,8));check(mark.Start==shortStart+new Vector(12,8)&&mark.TextFontSize==fontSize,"narrow text remains draggable without accidental resizing: "+narrow+" "+fontSize);
                window.BeginPointer(new Point(mark.Bounds.Left+mark.Bounds.Width/2,mark.Bounds.Top+mark.Bounds.Height/2),2);check(Editor(window)!=null&&Editor(window).Text==narrow,"narrow text double click opens its editor: "+narrow+" "+fontSize);Call(window,"CancelText");
            }
            Call(window,"SetTool","select");Select(window,mark);var cropHandle=surface.Handles()[0];Call(window,"UpdateBrushCursor",cropHandle);check(surface.Cursor==Cursors.SizeNWSE,"screenshot crop handles keep their original directional cursor while a text is selected");
            window.BeginPointer(cropHandle);window.EndPointer(cropHandle+new Vector(5,5));check(surface.Selection==new Rect(25,25,555,345)&&surface.Marks.Count==1,"screenshot crop resize remains independent of text selection and text history");
            Select(window,mark);double boundedSize=mark.TextFontSize;
            for(int i=0;i<101;i++)Call(window,"ChangeSelectedTextSize",i%2==0?1.0:-1.0);
            check(History(window)==100,"long text-editing sessions retain at most 100 lightweight undo deltas");
            for(int i=0;i<100;i++)Call(window,"Undo");
            check(surface.Marks.Single()==mark&&mark.TextFontSize==boundedSize+1&&History(window)==0,"undoing the bounded history keeps text whose original add operation has expired");
            Call(window,"Undo");check(surface.Marks.Single()==mark&&mark.TextFontSize==boundedSize+1,"extra undo at the retained-history boundary is a no-op, not an accidental text deletion");
            for(int i=0;i<100;i++)Call(window,"Redo");check(surface.Marks.Single()==mark&&mark.TextFontSize==boundedSize+1&&History(window)==100,"bounded edit history redoes in order without mixing an older annotation-removal stack");
            HighDpi(image,check);
            ResizeGeometry(check);
            ThemeEditor(image,check);
            WritePreview(image);
        }finally{window.Close();AppState.Settings=preferences;}
        check(surface.SelectedText==null&&surface.EditingText==null&&Editor(window)==null&&History(window)==0&&surface.Image==null,"closing capture releases text editor, selection, gesture history and screenshot references");
    }
    static bool Near(Point a,Point b){return (a-b).Length<.002;}
    static Vector ResizeAxis(Rect frame,int handle){return new Vector(handle==0||handle==6||handle==7?-frame.Width:handle==2||handle==3||handle==4?frame.Width:0,handle==0||handle==1||handle==2?-frame.Height:handle==4||handle==5||handle==6?frame.Height:0);}
    static bool Fits(Rect bounds,Rect crop){var relaxed=crop;relaxed.Inflate(.002,.002);return relaxed.Contains(bounds);}
    static void ResizeGeometry(Action<bool,string> check){
        var pixels=new byte[1800*1000*4];var image=BitmapSource.Create(1800,1000,96,96,PixelFormats.Bgra32,null,pixels,7200);image.Freeze();
        var window=new CaptureWindow(new Snapshot {Image=image,Bounds=new System.Drawing.Rectangle(0,0,1800,1000)});
        var surface=((Grid)window.Content).Children.OfType<ScreenshotSurface>().Single();surface.Measure(new Size(1800,1000));surface.Arrange(new Rect(0,0,1800,1000));
        var crop=new Rect(20,20,1760,960);surface.Selection=crop;
        try{
            Call(window,"SetTool","select");
            foreach(string style in new[]{"long-line","wrapped","explicit-newlines"})foreach(int handleIndex in Enumerable.Range(0,8)){
                surface.Marks.Clear();Call(window,"ClearAnnotationHistory");
                var mark=new Annotation {Kind="text",Start=new Point(650,350),Width=4,Text=style=="explicit-newlines"?"第一行 / First line\n第二行 / Second line\n第三行 / Third line":new string('W',24),TextWrapWidth=style=="wrapped"?180:0};surface.Marks.Add(mark);Select(window,mark);
                var original=mark.Bounds;var originalStart=mark.Start;double originalWidth=mark.Width,wrap=mark.TextWrapWidth;
                var frame=ScreenshotSurface.TextFrame(mark);var handle=ScreenshotSurface.Handles(frame)[handleIndex];var axis=ResizeAxis(frame,handleIndex);var anchor=ScreenshotSurface.Handles(original)[(handleIndex+4)%8];
                window.BeginPointer(handle);window.ContinuePointer(handle+axis*.16);
                check(Math.Abs(mark.TextFontSize-23.2)<.0001&&Fits(mark.Bounds,crop),style+" handle "+handleIndex+" scales proportionally to its original diagonal/axis, not the shorter side");
                check(Near(ScreenshotSurface.Handles(mark.Bounds)[(handleIndex+4)%8],anchor),style+" handle "+handleIndex+" keeps its opposite corner or opposite-edge center fixed");
                check(Math.Abs(mark.TextWrapWidth-wrap*1.16)<.0001&&Math.Abs(mark.Bounds.Width/original.Width-mark.Bounds.Height/original.Height)<.015,style+" handle "+handleIndex+" scales font and wrap width together without adding a spurious line");
                window.ContinuePointer(handle);
                check(mark.Start==originalStart&&mark.Width==originalWidth&&mark.TextWrapWidth==wrap,style+" handle "+handleIndex+" returns exactly to its original geometry within the same gesture");
                window.ContinuePointer(handle+axis*.1);window.EndPointer(handle);
                check(History(window)==0&&mark.Start==originalStart&&mark.Width==originalWidth&&mark.TextWrapWidth==wrap,style+" handle "+handleIndex+" release back at origin leaves no phantom undo entry");
                window.BeginPointer(handle);window.EndPointer(handle+axis*.12);double resized=mark.Width;
                check(History(window)==1&&resized>originalWidth,style+" handle "+handleIndex+" committed resize is one operation");
                Call(window,"Undo");check(mark.Start==originalStart&&mark.Width==originalWidth&&mark.TextWrapWidth==wrap,style+" handle "+handleIndex+" undo restores origin, size and wrapping");
                Call(window,"Redo");check(mark.Width==resized&&Near(ScreenshotSurface.Handles(mark.Bounds)[(handleIndex+4)%8],anchor),style+" handle "+handleIndex+" redo restores the same anchored geometry");
            }
            surface.Marks.Clear();Call(window,"ClearAnnotationHistory");
            var single=new Annotation {Kind="text",Start=new Point(1079,400),Width=4,Text=new string('W',24)};surface.Marks.Add(single);Select(window,single);
            var singleOrigin=single.Start;var singleFrame=ScreenshotSurface.TextFrame(single);var corner=ScreenshotSurface.Handles(singleFrame)[4];
            check(single.Bounds.Width>350&&single.Bounds.Width<600&&single.Bounds.Height<30,"realistic long one-line fixture has the observed wide, shallow aspect ratio");
            window.BeginPointer(corner);window.ContinuePointer(corner+new Vector(117,44));
            check(single.TextFontSize>23&&single.TextFontSize<28&&single.Start==singleOrigin,"117-by-44 corner drag grows long caption moderately and never jumps its left origin as the old max-axis ratio did");
            window.ContinuePointer(corner);var diagonal=ResizeAxis(singleFrame,4);var perpendicular=new Vector(-diagonal.Y,diagonal.X);perpendicular.Normalize();
            window.EndPointer(corner+perpendicular*44);
            check(single.Width==4&&single.Start==singleOrigin&&History(window)==0,"perpendicular corner motion has no projected scale and creates no hidden movement or history");
            foreach(string style in new[]{"long-line","wrapped"})foreach(int handleIndex in Enumerable.Range(0,8)){
                surface.Marks.Clear();Call(window,"ClearAnnotationHistory");
                var mark=new Annotation {Kind="text",Start=new Point(650,350),Width=4,Text=new string('W',24),TextWrapWidth=style=="wrapped"?180:0};
                var size=mark.Bounds.Size;bool left=handleIndex==0||handleIndex==6||handleIndex==7,right=handleIndex==2||handleIndex==3||handleIndex==4,top=handleIndex==0||handleIndex==1||handleIndex==2,bottom=handleIndex==4||handleIndex==5||handleIndex==6;
                mark.Start=new Point(left?crop.Left+12:right?crop.Right-size.Width-12:650,top?crop.Top+12:bottom?crop.Bottom-size.Height-12:350);
                surface.Marks.Add(mark);Select(window,mark);var original=mark.Bounds;var anchor=ScreenshotSurface.Handles(original)[(handleIndex+4)%8];
                var frame=ScreenshotSurface.TextFrame(mark);var handle=ScreenshotSurface.Handles(frame)[handleIndex];var axis=ResizeAxis(frame,handleIndex);
                window.BeginPointer(handle);window.ContinuePointer(handle+axis*4);
                check(Fits(mark.Bounds,crop)&&mark.TextFontSize>=20,style+" boundary "+handleIndex+" caps enlargement to measured available space instead of cropping text");
                check(Near(ScreenshotSurface.Handles(mark.Bounds)[(handleIndex+4)%8],anchor),style+" boundary "+handleIndex+" never shifts the fixed anchor to fit an oversized result");
                var limited=mark.Bounds;double limitedWidth=mark.Width;
                window.ContinuePointer(handle+axis*8);
                check(mark.Bounds==limited&&mark.Width==limitedWidth,style+" boundary "+handleIndex+" further outward motion stays at a stable maximum");
                window.EndPointer(handle);
                check(mark.Bounds==original&&mark.Width==4&&History(window)==0,style+" boundary "+handleIndex+" returns from the limit exactly to the initial annotation");
                Select(window,mark);window.BeginPointer(handle);window.EndPointer(handle-axis*4);
                check(mark.TextFontSize==16&&Fits(mark.Bounds,crop)&&Near(ScreenshotSurface.Handles(mark.Bounds)[(handleIndex+4)%8],anchor),style+" boundary "+handleIndex+" inward crossing stops at minimum readable size without flipping or shifting the anchor");
            }
        }finally{window.Close();}
    }
    static void HighDpi(BitmapSource image,Action<bool,string> check){
        var doubled=new TransformedBitmap(image,new ScaleTransform(2,2));doubled.Freeze();
        var window=new CaptureWindow(new Snapshot {Image=doubled,Bounds=new System.Drawing.Rectangle(0,0,1200,800)});
        var surface=((Grid)window.Content).Children.OfType<ScreenshotSurface>().Single();surface.Measure(new Size(600,400));surface.Arrange(new Rect(0,0,600,400));
        var crop=new Rect(20,20,560,350);surface.Selection=crop;
        try{
            check(surface.ScaleX==2&&surface.ScaleY==2,"200% fixture maps two image pixels to each layout DIP without showing a desktop window");
            Call(window,"SetTool","text");window.BeginPointer(new Point(100,100));Editor(window).Text="双倍像素 / DPI 200%";Call(window,"CommitText");var mark=surface.Marks.Single();
            Call(window,"SetTool","select");var point=Body(mark);var start=mark.Start;window.BeginPointer(point);window.EndPointer(point+new Vector(25,15));
            check(surface.SelectedText==mark&&mark.Start==start+new Vector(25,15)&&surface.Selection==crop,"200% text selection and dragging use logical coordinates and never move or resize the crop");
            var actual=surface.Export();check(actual.PixelWidth==1120&&actual.PixelHeight==700,"200% screenshot exports its full physical-pixel dimensions instead of the smaller layout size");
            surface.SelectedText=null;var withoutFrame=surface.Export();check(Pixels(actual,withoutFrame),"200% text-selection outline and handles are excluded from every exported pixel");
            var textPixels=new Int32Rect((int)((mark.Bounds.Left-crop.Left)*2),(int)((mark.Bounds.Top-crop.Top)*2),(int)Math.Ceiling(mark.Bounds.Width*2),(int)Math.Ceiling(mark.Bounds.Height*2));
            var pixels=new byte[textPixels.Width*textPixels.Height*4];actual.CopyPixels(textPixels,pixels,textPixels.Width*4,0);int ink=0;
            for(int i=0;i<pixels.Length;i+=4)if(pixels[i+2]>170&&pixels[i+1]<140&&pixels[i]<140&&pixels[i+3]==255)ink++;
            check(ink>100,"200% exported text appears at its translated physical-pixel bounds with original annotation color");
        }finally{window.Close();}
    }
    static double Luminance(Color color){Func<byte,double> channel=value=>{double v=value/255.0;return v<=.04045?v/12.92:Math.Pow((v+.055)/1.055,2.4);};return .2126*channel(color.R)+.7152*channel(color.G)+.0722*channel(color.B);}
    static double Contrast(Color a,Color b){double x=Luminance(a),y=Luminance(b);return (Math.Max(x,y)+.05)/(Math.Min(x,y)+.05);}
    static int ThemeSubscriptions(CaptureWindow window){var changed=(Delegate)typeof(Mori.Theme).GetField("Changed",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);return changed==null?0:changed.GetInvocationList().Count(callback=>ReferenceEquals(callback.Target,window));}
    static void ThemeEditor(BitmapSource image,Action<bool,string> check){
        var app=Application.Current??new Application();var shutdown=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        var savedResources=app.Resources.Keys.Cast<object>().ToDictionary(key=>key,key=>app.Resources[key]);string oldMode=Mori.Theme.Current;var window=new CaptureWindow(new Snapshot {Image=image,Bounds=new System.Drawing.Rectangle(0,0,600,400)});
        var surface=((Grid)window.Content).Children.OfType<ScreenshotSurface>().Single();surface.Measure(new Size(600,400));surface.Arrange(new Rect(0,0,600,400));surface.Selection=new Rect(20,20,560,350);
        var mark=new Annotation {Kind="text",Text="主题文字 / Theme",Start=new Point(80,100),Width=6.4,Color="#EF4444"};surface.Marks.Add(mark);
        try{
            Call(window,"SetTool","text");window.BeginPointer(Body(mark),2);var editor=Editor(window);var initial=surface.Export();string color=mark.Color;
            check(ThemeSubscriptions(window)==1,"an open text editor has exactly one live-theme listener");
            foreach(string mode in new[]{"light","dark"}){
                Mori.Theme.Set(mode);editor.Measure(new Size(400,100));editor.Arrange(new Rect(0,0,400,100));editor.UpdateLayout();
                var expected=((SolidColorBrush)Mori.Theme.Brush("Surface")).Color;
                foreach(string ink in new[]{"#EF4444","#FFFFFF","#000000","#777777"}){
                    Call(window,"ApplyAnnotationColor",ink);
                    var foreground=((SolidColorBrush)editor.Foreground).Color;var background=((SolidColorBrush)editor.Background).Color;
                    check(editor.FontSize==32&&foreground==(Color)ColorConverter.ConvertFromString(ink),mode+" live text-color change keeps the exact chosen annotation ink: "+ink);
                    check(foreground.A==255&&background.A==255&&Contrast(foreground,background)>=4.5,mode+" text editor adapts its backing for readable 4.5:1 contrast: "+ink);
                    check(Contrast(foreground,expected)<4.5||background==expected,mode+" text editor prefers the current theme surface whenever its contrast is sufficient: "+ink);
                    check(mark.Color==color&&mark.Text=="主题文字 / Theme"&&Pixels(initial,surface.Export()),mode+" pending color and theme changes never recolor the original annotation or exported pixels: "+ink);
                }
            }
            Call(window,"ApplyAnnotationColor","#FFFFFF");Mori.Theme.Set("light");var lightBacking=((SolidColorBrush)editor.Background).Color;Mori.Theme.Set("dark");var darkBacking=((SolidColorBrush)editor.Background).Color;
            check(lightBacking!=darkBacking&&darkBacking==((SolidColorBrush)Mori.Theme.Brush("Surface")).Color&&Contrast(Colors.White,lightBacking)>=4.5,"theme changes immediately refresh an existing white-ink editor without recoloring the text");
            Call(window,"CancelText");check(mark.Color==color&&surface.SelectedText==mark&&ThemeSubscriptions(window)==0,"cancelling the themed editor preserves annotation color and releases its theme listener");
            window.BeginPointer(Body(mark),2);editor=Editor(window);check(((SolidColorBrush)editor.Foreground).Color==(Color)ColorConverter.ConvertFromString(color),"reopening text uses its annotation ink rather than the last uncommitted toolbar color");
            Call(window,"ApplyAnnotationColor","#FFFFFF");Call(window,"CommitText");
            check(mark.Color=="#FFFFFF"&&surface.Marks.Single()==mark&&ThemeSubscriptions(window)==0,"committing white text preserves the chosen output color rather than substituting readable theme text");
            window.BeginPointer(Body(mark),2);check(ThemeSubscriptions(window)==1,"repeated edits register only one theme listener before capture closes");
        }finally{
            window.Close();
            if(oldMode!=null)Mori.Theme.Set(oldMode);else typeof(Mori.Theme).GetProperty("Current").SetValue(null,null,null);
            foreach(var key in app.Resources.Keys.Cast<object>().ToArray())if(!savedResources.ContainsKey(key))app.Resources.Remove(key);
            foreach(var resource in savedResources)app.Resources[resource.Key]=resource.Value;
            app.ShutdownMode=shutdown;
        }
        check(Mori.Theme.Current==oldMode&&ThemeSubscriptions(window)==0,"theme-editor fixture restores the exact prior mode and palette and closing releases an active editor's listener");
    }
    static void WritePreview(BitmapSource image){
        var text=new Annotation {Kind="text",Text="文字标注 / Annotation\n拖动可移动 · 双击编辑",Start=new Point(80,100),Width=5,Color="#EF4444"};
        var narrow=new Annotation {Kind="text",Text="i",Start=new Point(300,220),Width=3.2,Color="#EF4444"};
        var panels=new BitmapSource[2];
        for(int i=0;i<2;i++){
            var preview=new ScreenshotSurface {Image=image,Selection=new Rect(20,20,560,350),SelectedText=i==0?text:narrow};preview.Marks.Add(text);preview.Marks.Add(narrow);preview.Measure(new Size(600,400));preview.Arrange(new Rect(0,0,600,400));
            var pixels=new RenderTargetBitmap(600,400,96,96,PixelFormats.Pbgra32);pixels.Render(preview);panels[i]=pixels;
        }
        var visual=new DrawingVisual();using(var d=visual.RenderOpen()){d.DrawImage(panels[0],new Rect(0,0,600,400));d.DrawImage(panels[1],new Rect(600,0,600,400));}
        var combined=new RenderTargetBitmap(1200,400,96,96,PixelFormats.Pbgra32);combined.Render(visual);ImageFiles.Write(combined,System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"capture-text-selection-preview.png"));
    }
}
