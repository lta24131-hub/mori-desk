using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;

// Generated pixels, explicit modifiers and inert windows only. Output shortcuts
// use fake services: no native output dialog, pin or clipboard is touched.
static class CaptureInputUsabilityTests {
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    sealed class TestSource : PresentationSource {
        public override Visual RootVisual {get;set;}
        public override bool IsDisposed {get{return false;}}
        protected override CompositionTarget GetCompositionTargetCore(){return null;}
    }
    static object Call(CaptureWindow window,string name,params object[] args){return typeof(CaptureWindow).GetMethod(name,Hidden).Invoke(window,args);}
    static T Field<T>(CaptureWindow window,string name){return (T)typeof(CaptureWindow).GetField(name,Hidden).GetValue(window);}
    static System.Collections.Generic.IEnumerable<DependencyObject> Tree(DependencyObject node){yield return node;foreach(object value in LogicalTreeHelper.GetChildren(node)){var child=value as DependencyObject;if(child!=null)foreach(var item in Tree(child))yield return item;}}
    static int History(CaptureWindow window){var stack=Field<object>(window,"annotationUndo");return (int)stack.GetType().GetProperty("Count").GetValue(stack,null);}
    sealed class OutputProbe : ICaptureOutputActions {
        internal int Saves,Copies,Pins;
        internal Func<BitmapSource,Task<bool>> SaveResult=image=>Task.FromResult(false);
        public Task<bool> Save(BitmapSource image,Window owner,CancellationToken cancel){Saves++;return SaveResult(image);}
        public Task<bool> Copy(BitmapSource image,CancellationToken cancel){Copies++;return Task.FromResult(false);}
        public Task<bool> Pin(BitmapSource image,Int32Rect bounds,CancellationToken cancel){Pins++;return Task.FromResult(false);}
    }
    sealed class Fixture : IDisposable {
        internal readonly CaptureWindow Window;
        internal readonly ScreenshotSurface Surface;
        internal readonly OutputProbe Outputs=new OutputProbe();
        internal readonly Rect Crop=new Rect(40,30,480,300);
        internal Fixture(){
            var image=BitmapSource.Create(600,400,96,96,PixelFormats.Bgra32,null,new byte[600*400*4],2400);image.Freeze();
            Window=new CaptureWindow(new Snapshot {Image=image,Bounds=new System.Drawing.Rectangle(0,0,600,400)});
            Window.OutputActions=Outputs;
            Surface=((Grid)Window.Content).Children.OfType<ScreenshotSurface>().Single();Surface.Measure(new Size(600,400));Surface.Arrange(new Rect(0,0,600,400));Surface.Selection=Crop;
        }
        internal bool Key(Key key,ModifierKeys modifiers=ModifierKeys.None){return Window.HandleCaptureKey(key,modifiers);}
        internal void Tool(string tool){Call(Window,"SetTool",tool);}
        internal Annotation Draw(string tool){Tool(tool);Window.BeginPointer(new Point(140,120));Window.EndPointer(new Point(240,190));return Surface.Marks.Last();}
        public void Dispose(){Window.Close();AppState.Captures.Remove(Window);}
    }
    internal static void Run(Action<bool,string> check){
        var preferences=AppState.Settings;AppState.Settings=new Preferences {Stroke=4,Color="#EF4444"};
        try{ToolKeys(check);HistoryKeys(check);TextKeys(check);EditorOutputKeys(check);CompletionGuards(check);RightCancel(check);EscapeOnce(check);ClosedPen(check);OverlapCursor(check);}
        finally{AppState.Settings=preferences;}
    }
    static void ToolKeys(Action<bool,string> check){
        using(var f=new Fixture()){
            var keys=new[]{Key.R,Key.O,Key.A,Key.L,Key.P,Key.T,Key.M,Key.V};
            var names=new[]{"rect","ellipse","arrow","line","pen","text","mosaic","select"};
            for(int i=0;i<keys.Length;i++)check(f.Key(keys[i])&&f.Window.SelectedTool==names[i]&&f.Surface.Selection==f.Crop&&f.Surface.Marks.Count==0,"tool key chooses "+names[i]+" without changing screenshot pixels or region");
            check(!f.Key(Key.R,ModifierKeys.Alt)&&f.Window.SelectedTool=="select","Alt combinations do not unexpectedly switch screenshot tools");
            check(!f.Key(Key.R,ModifierKeys.Control)&&f.Window.SelectedTool=="select","Control combinations cannot trigger plain-letter tool shortcuts");
            check(!f.Key(Key.T,ModifierKeys.Shift)&&f.Window.SelectedTool=="select","modified text input is not mistaken for a tool shortcut");
            check(f.Key(Key.R,ModifierKeys.None)&&f.Window.SelectedTool=="rect","shared key dispatcher consumes a recognized tool key with explicit modifiers");
            f.Tool("select");check(f.Key(Key.Right)&&f.Surface.Selection==new Rect(41,30,480,300),"real key dispatcher moves an unselected crop by one logical pixel");
            check(f.Key(Key.Down,ModifierKeys.Shift)&&f.Surface.Selection==new Rect(41,30,480,301),"real key dispatcher routes Shift arrows to crop resizing instead of moving it");
            check(f.Key(Key.Left,ModifierKeys.Control)&&f.Surface.Selection==new Rect(31,30,480,301),"real key dispatcher routes Control arrows to accelerated crop movement");
            f.Surface.Selection=Rect.Empty;
            check(!f.Key(Key.T)&&f.Window.SelectedTool=="select","tool shortcut does not enter an unusable annotation tool before a crop exists");
            check(!f.Key(Key.Enter)&&!f.Key(Key.C,ModifierKeys.Control)&&!f.Key(Key.S,ModifierKeys.Control),"completion keys with no crop do not run output actions or display a modal export error");
        }
    }
    static void HistoryKeys(Action<bool,string> check){
        using(var f=new Fixture()){
            var mark=f.Draw("rect");
            check(f.Key(Key.Z,ModifierKeys.Control)&&f.Surface.Marks.Count==0,"Control Z dispatches undo and consumes the shortcut");
            check(f.Key(Key.Z,ModifierKeys.Control|ModifierKeys.Shift)&&f.Surface.Marks.Single()==mark,"Control Shift Z dispatches redo, not a second undo");
            check(f.Key(Key.Z,ModifierKeys.Control)&&f.Key(Key.Y,ModifierKeys.Control)&&f.Surface.Marks.Single()==mark,"Control Y remains a second supported redo shortcut");
            check(!f.Key(Key.Z,ModifierKeys.Alt|ModifierKeys.Control)&&f.Surface.Marks.Single()==mark,"Alt Control combinations cannot accidentally mutate annotation history");
            f.Tool("select");f.Window.BeginPointer(new Point(180,120));f.Window.EndPointer(new Point(180,120));
            check(f.Key(Key.Right)&&mark.Start==new Point(141,120)&&f.Surface.Selection==f.Crop,"arrow keys still move the selected annotation instead of resizing the crop");
            check(f.Key(Key.Delete)&&f.Surface.Marks.Count==0&&f.Surface.Selection==f.Crop,"Delete removes only the selected annotation through the shared key dispatcher");
            check(f.Key(Key.Z,ModifierKeys.Control)&&f.Surface.Marks.Single()==mark,"annotation deletion remains reversible after keyboard movement");
        }
    }
    static void TextKeys(Action<bool,string> check){
        var collect=MoriDesk.DeskRuntime.CollectImage;int calls=0;
        MoriDesk.DeskRuntime.CollectImage=image=>{calls++;return Task.FromResult(true);};
        try{using(var f=new Fixture()){
            f.Key(Key.T);f.Window.BeginPointer(new Point(120,100));var editor=Field<TextBox>(f.Window,"textEditor");editor.Text="Text remains editable";
            foreach(var key in new[]{Key.R,Key.V,Key.P,Key.Delete,Key.Enter,Key.Left})check(!f.Key(key)&&Field<TextBox>(f.Window,"textEditor")==editor&&f.Surface.Marks.Count==0,"live text editor owns ordinary typing/navigation: "+key);
            foreach(var key in new[]{Key.C,Key.X,Key.V,Key.A,Key.Z,Key.Y,Key.Left,Key.Right})check(!f.Key(key,ModifierKeys.Control)&&Field<TextBox>(f.Window,"textEditor")==editor&&calls==0&&f.Outputs.Copies==0&&f.Outputs.Saves==0,"live text editor retains native clipboard, selection, undo and navigation: Control "+key);
            check(!f.Key(Key.Z,ModifierKeys.Control|ModifierKeys.Shift)&&Field<TextBox>(f.Window,"textEditor")==editor&&History(f.Window)==0,"live text editor retains Control Shift Z rather than touching annotation history");
            check(f.Key(Key.Enter,ModifierKeys.Control)&&Field<TextBox>(f.Window,"textEditor")==null&&f.Surface.Marks.Single().Text=="Text remains editable","Control Enter commits text once while keeping screenshot open");
        }}finally{MoriDesk.DeskRuntime.CollectImage=collect;}
    }
    static void EditorOutputKeys(Action<bool,string> check){
        var previous=MoriDesk.DeskRuntime.CollectImage;
        try{
            foreach(var key in new[]{Key.S,Key.D})foreach(string outcome in new[]{"cancelled","failed","success"})using(var f=new Fixture()){
                var earlier=f.Draw("arrow");int before=History(f.Window),calls=0;bool sawText=false;BitmapSource exported=null;
                Func<BitmapSource,Task<bool>> respond=image=>{
                    calls++;exported=image;sawText=f.Surface.Marks.Count==2&&f.Surface.Marks.Any(mark=>mark.Kind=="text"&&mark.Text=="文字 / Caption")&&Field<TextBox>(f.Window,"textEditor")==null;
                    if(outcome=="failed")throw new InvalidOperationException("Synthetic output failure");
                    return Task.FromResult(outcome=="success");
                };
                f.Outputs.SaveResult=respond;MoriDesk.DeskRuntime.CollectImage=respond;
                f.Key(Key.T);f.Window.BeginPointer(new Point(280,210));var editor=Field<TextBox>(f.Window,"textEditor");editor.Text="文字 / Caption";
                string label="live text Control "+key+" / "+outcome;
                check(f.Key(key,ModifierKeys.Control)&&calls==1&&f.Outputs.Saves==(key==Key.S?1:0)&&f.Outputs.Copies==0&&f.Outputs.Pins==0,label+" routes once to the intended screenshot output, not native text copy or another service");
                check(sawText&&exported!=null&&exported.IsFrozen&&exported.PixelWidth==480&&exported.PixelHeight==300,label+" commits the caption before exporting the selected immutable pixels");
                if(outcome=="success")check(Field<bool>(f.Window,"closed")&&f.Surface.Image==null,label+" completes only after successful output acknowledgement");
                else{
                    var text=f.Surface.Marks.Single(mark=>mark.Kind=="text");
                    check(!Field<bool>(f.Window,"closed")&&f.Surface.IsEnabled&&f.Surface.Selection==f.Crop&&f.Surface.Marks.Contains(earlier)&&text.Text=="文字 / Caption"&&History(f.Window)==before+1,label+" cancellation/failure retains editable text, original mark, crop and one commit history entry");
                    check(f.Key(Key.Z,ModifierKeys.Control)&&f.Surface.Marks.Single()==earlier,label+" Undo removes only the newly committed caption");
                    check(f.Key(Key.Y,ModifierKeys.Control)&&f.Surface.Marks.Contains(text)&&text.Text=="文字 / Caption"&&calls==1,label+" Redo restores the same caption without repeating the output");
                }
            }
            using(var f=new Fixture()){
                int collected=0;MoriDesk.DeskRuntime.CollectImage=image=>{collected++;return Task.FromResult(false);};
                f.Key(Key.T);f.Window.BeginPointer(new Point(120,100));var editor=Field<TextBox>(f.Window,"textEditor");editor.Text="Still typing";
                foreach(var key in new[]{Key.S,Key.D})foreach(var modifiers in new[]{ModifierKeys.None,ModifierKeys.Control|ModifierKeys.Shift,ModifierKeys.Control|ModifierKeys.Alt})
                    check(!f.Key(key,modifiers)&&Field<TextBox>(f.Window,"textEditor")==editor&&f.Outputs.Saves==0&&collected==0,"modified/plain output letter stays in live editor rather than accidentally submitting: "+modifiers+" "+key);
                MoriDesk.DeskRuntime.CollectImage=null;
                check(!f.Key(Key.D,ModifierKeys.Control)&&Field<TextBox>(f.Window,"textEditor")==editor&&f.Surface.Marks.Count==0,"Control D with no collection service leaves live text untouched");
                MoriDesk.DeskRuntime.CollectImage=image=>{collected++;return Task.FromResult(false);};f.Surface.Selection=Rect.Empty;
                check(!f.Key(Key.S,ModifierKeys.Control)&&!f.Key(Key.D,ModifierKeys.Control)&&Field<TextBox>(f.Window,"textEditor")==editor&&f.Outputs.Saves==0&&collected==0,"even a live editor cannot submit screenshot pixels without a valid crop");
            }
        }finally{MoriDesk.DeskRuntime.CollectImage=previous;}
    }
    static void CompletionGuards(Action<bool,string> check){
        using(var f=new Fixture()){
            check(f.Window.CanCompleteCaptureAt(new Point(350,250)),"double-click completion is eligible only on empty crop interior");
            foreach(var p in new[]{new Point(0,0),new Point(580,380),f.Crop.TopLeft,new Point(f.Crop.Right,f.Crop.Top+f.Crop.Height/2)})check(!f.Window.CanCompleteCaptureAt(p),"double-click outside crop or on a resize handle cannot complete capture: "+p);
            f.Window.BeginPointer(new Point(560,350),2);f.Window.EndPointer(new Point(560,350));
            check(f.Surface.Selection==f.Crop&&f.Surface.Image!=null,"actual outside double click leaves capture open and crop unchanged");
            var mark=f.Draw("rect");f.Tool("select");
            check(!f.Window.CanCompleteCaptureAt(new Point(180,120)),"double-clicking an annotation selects it instead of completing capture");
            f.Window.BeginPointer(new Point(180,120),2);f.Window.EndPointer(new Point(180,120));
            check(f.Surface.SelectedAnnotation==mark&&f.Surface.Selection==f.Crop,"actual annotation double click stays on its editable object");
            f.Window.BeginPointer(f.Crop.TopLeft,2);f.Window.EndPointer(f.Crop.TopLeft);
            check(f.Surface.Selection==f.Crop&&f.Surface.Image!=null,"actual handle double click cannot trigger an export action");
        }
    }
    static void RightCancel(Action<bool,string> check){
        using(var f=new Fixture()){
            var mark=f.Draw("arrow");int history=History(f.Window);f.Tool("pen");
            f.Window.BeginPointer(new Point(300,200));f.Window.ContinuePointer(new Point(350,240));
            f.Window.CancelFromPointer();
            check(!Field<bool>(f.Window,"dragging")&&f.Surface.Draft==null&&f.Surface.Marks.Single()==mark&&History(f.Window)==history,"right click rolls back an active pen gesture without leaving a ghost draft or history entry");
            f.Window.CancelFromPointer();check(f.Window.SelectedTool=="select"&&f.Surface.Selection==f.Crop,"next right click returns from drawing tool to selection before clearing crop");
            f.Window.BeginPointer(f.Crop.TopLeft);f.Window.ContinuePointer(f.Crop.TopLeft+new Vector(15,12));f.Window.CancelFromPointer();
            check(f.Surface.Selection==f.Crop&&f.Surface.Marks.Single()==mark&&History(f.Window)==history,"right click during crop resize restores original crop and earlier annotations");
            f.Tool("text");f.Window.BeginPointer(new Point(300,200));Field<TextBox>(f.Window,"textEditor").Text="cancel me";f.Window.CancelFromPointer();
            check(Field<TextBox>(f.Window,"textEditor")==null&&f.Surface.Marks.Single()==mark&&f.Surface.Selection==f.Crop,"right click cancels uncommitted text rather than unexpectedly exporting or ending capture");
        }
    }
    static void EscapeOnce(Action<bool,string> check){
        var collect=MoriDesk.DeskRuntime.CollectImage;int submissions=0;
        MoriDesk.DeskRuntime.CollectImage=image=>{submissions++;return Task.FromResult(true);};
        try{
            foreach(string state in new[]{"empty","crop","tool","selected-shape","drawing","shape-move","text-new","text-edit","text-move","text-resize","translation","language-menu"})using(var f=new Fixture()){
                AppState.Captures.Add(f.Window);Annotation original=null;CancellationTokenSource translation=null;KeyEventArgs e;
                if(state=="empty")f.Surface.Selection=Rect.Empty;
                if(state=="tool")f.Tool("pen");
                if(state=="selected-shape"||state=="shape-move"){
                    var mark=f.Draw("rect");f.Tool("select");f.Window.BeginPointer(new Point(180,120));
                    if(state=="shape-move")f.Window.ContinuePointer(new Point(210,140));else f.Window.EndPointer(new Point(180,120));
                }
                if(state=="drawing"){f.Tool("pen");f.Window.BeginPointer(new Point(140,120));f.Window.ContinuePointer(new Point(240,190));}
                if(state=="text-new"){f.Tool("text");f.Window.BeginPointer(new Point(140,120));Field<TextBox>(f.Window,"textEditor").Text="must not be committed";}
                if(state=="text-edit"||state=="text-move"||state=="text-resize"){
                    original=new Annotation {Kind="text",Text="original text",Start=new Point(140,120),Width=4};f.Surface.Marks.Add(original);f.Tool("select");
                    if(state=="text-edit"){f.Window.BeginPointer(new Point(original.Bounds.Left+original.Bounds.Width/2,original.Bounds.Top+original.Bounds.Height/2),2);Field<TextBox>(f.Window,"textEditor").Text="must not replace original";}
                    else{
                        Call(f.Window,"SelectText",original);
                        var point=state=="text-resize"?ScreenshotSurface.Handles(ScreenshotSurface.TextFrame(original))[4]:new Point(original.Bounds.Left+original.Bounds.Width/2,original.Bounds.Top+original.Bounds.Height/2);
                        f.Window.BeginPointer(point);f.Window.ContinuePointer(point+new Vector(20,10));
                    }
                }
                if(state=="translation"){
                    translation=new CancellationTokenSource();typeof(CaptureWindow).GetField("translationCancel",Hidden).SetValue(f.Window,translation);
                    f.Surface.TranslationImage=f.Surface.Image;f.Surface.ShowTranslation=true;
                }
                e=new KeyEventArgs(Keyboard.PrimaryDevice,new TestSource(),Environment.TickCount,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};
                if(state=="language-menu"){
                    // Exercise the actual popup handler without opening a native
                    // popup: this context menu is intentionally always closed.
                    var menu=Tree(Field<StackPanel>(f.Window,"toolbar")).OfType<Button>().Single(button=>button.ContextMenu!=null).ContextMenu;
                    menu.RaiseEvent(e);
                }else {
                    // Escape is independent of all modifiers in production;
                    // retain this actual event-handler wiring regression.
                    Call(f.Window,"KeyDownHandler",f.Window,e);
                }
                check(e.Handled&&Field<bool>(f.Window,"closed")&&AppState.Captures.Count==0&&f.Surface.Image==null&&f.Surface.Marks.Count==0&&f.Surface.Draft==null&&Field<TextBox>(f.Window,"textEditor")==null,"one Escape exits screenshot immediately from "+state+" without intermediate cancellation");
                check(submissions==0&&History(f.Window)==0&&!Field<bool>(f.Window,"dragging"),"Escape never submits unfinished pixels or retains a gesture/history: "+state);
                if(state=="text-edit")check(original.Text=="original text","Escape closes a live reopened editor without committing replacement text");
                if(translation!=null){check(translation.IsCancellationRequested&&f.Surface.TranslationImage==null,"Escape cancels in-flight translation and releases its visual result");translation.Dispose();}
                check(!f.Key(Key.Escape),"a duplicate Escape delivered to an already-closed screenshot is ignored: "+state);
            }
        }finally{MoriDesk.DeskRuntime.CollectImage=collect;}
    }
    static void ClosedPen(Action<bool,string> check){
        using(var f=new Fixture()){
            f.Tool("pen");f.Window.BeginPointer(new Point(120,100));f.Window.ContinuePointer(new Point(200,100));f.Window.ContinuePointer(new Point(200,180));f.Window.ContinuePointer(new Point(120,180));f.Window.EndPointer(new Point(120,100));
            var mark=f.Surface.Marks.Single();
            check(mark.Kind=="pen"&&mark.Start==mark.End&&mark.Points.Count>=5,"closed freehand stroke is retained even when final point returns exactly to its start");
            check(f.Key(Key.Z,ModifierKeys.Control)&&f.Surface.Marks.Count==0&&f.Key(Key.Z,ModifierKeys.Control|ModifierKeys.Shift)&&f.Surface.Marks.Single()==mark,"closed freehand stroke is one undoable and redoable action");
            int history=History(f.Window);f.Window.BeginPointer(new Point(300,200));f.Window.EndPointer(new Point(300,200));
            check(f.Surface.Marks.Single()==mark&&History(f.Window)==history,"motionless pen click remains a no-op without phantom history");
        }
    }
    static void OverlapCursor(Action<bool,string> check){
        using(var f=new Fixture()){
            var text=new Annotation {Kind="text",Start=new Point(130,110),Text="Overlapping text",Width=4};
            var cover=new Annotation {Kind="mosaic",Start=new Point(100,80),End=new Point(370,190),Width=4};
            f.Surface.Marks.Add(text);f.Surface.Marks.Add(cover);Call(f.Window,"SelectText",text);var point=ScreenshotSurface.TextFrame(text).TopLeft;
            Call(f.Window,"UpdateBrushCursor",point);check(f.Surface.Cursor==Cursors.SizeAll,"covering shape advertises its movement cursor, not a hidden text resize handle");
            f.Window.BeginPointer(point);f.Window.EndPointer(point);
            check(f.Surface.SelectedAnnotation==cover&&f.Surface.SelectedText==null,"overlapping click target agrees with the topmost shape advertised by the cursor");
        }
    }
}
