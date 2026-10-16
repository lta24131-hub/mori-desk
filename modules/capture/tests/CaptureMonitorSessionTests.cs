using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QingJie;

// In-memory monitor images and unshown STA windows only. No native monitor,
// pointer position, screenshot, registered shortcut or output action is used.
static class CaptureMonitorSessionTests {
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static T Field<T>(CaptureWindow window,string name){return (T)typeof(CaptureWindow).GetField(name,Hidden).GetValue(window);}
    static bool Closed(CaptureWindow window){return Field<bool>(window,"closed");}
    static bool Inert(CaptureWindow window){return !window.IsVisible&&new WindowInteropHelper(window).Handle==IntPtr.Zero;}
    sealed class Monitor {
        internal readonly Snapshot Shot;
        internal readonly CaptureWindow Window;
        internal readonly ScreenshotSurface Surface;
        internal readonly BitmapSource Image;
        internal Monitor(System.Drawing.Rectangle bounds,Size logical){
            Image=BitmapSource.Create(bounds.Width,bounds.Height,96,96,PixelFormats.Bgra32,null,new byte[bounds.Width*bounds.Height*4],bounds.Width*4);Image.Freeze();
            Shot=new Snapshot {Image=Image,Bounds=bounds};Window=new CaptureWindow(Shot);
            Surface=((Grid)Window.Content).Children.OfType<ScreenshotSurface>().Single();Surface.Measure(logical);Surface.Arrange(new Rect(new Point(),logical));
        }
        internal bool Key(Key key,ModifierKeys modifiers=ModifierKeys.None){return Window.HandleCaptureKey(key,modifiers);}
        internal void Drag(Rect crop){Window.BeginPointer(crop.TopLeft);Window.EndPointer(crop.BottomRight);}
    }
    sealed class Session : IDisposable {
        // The left monitor has both a negative desktop origin and a 200% scale;
        // the right monitor uses 100%, so native pixels cannot double as DIPs.
        internal readonly Monitor Left=new Monitor(new System.Drawing.Rectangle(-800,-100,800,600),new Size(400,300));
        internal readonly Monitor Right=new Monitor(new System.Drawing.Rectangle(0,0,600,400),new Size(600,400));
        internal readonly Rect Crop=new Rect(40,30,240,150);
        internal Session(){
            if(AppState.Captures.Count!=0)throw new InvalidOperationException("Monitor fixture requires an empty capture session.");
            AppState.Captures.Add(Left.Window);AppState.Captures.Add(Right.Window);
        }
        internal bool Open {get{return AppState.Captures.Count==2&&AppState.Captures.Contains(Left.Window)&&AppState.Captures.Contains(Right.Window)&&!Closed(Left.Window)&&!Closed(Right.Window);}}
        internal bool Resumed {get{return !Left.Window.IsCaptureOverlaySuspended&&!Right.Window.IsCaptureOverlaySuspended;}}
        public void Dispose(){if(!Closed(Left.Window))Left.Window.Close();if(!Closed(Right.Window))Right.Window.Close();}
    }
    static T Await<T>(Task<T> task){
        if(!task.IsCompleted){
            var frame=new DispatcherFrame();var timeout=new DispatcherTimer {Interval=TimeSpan.FromSeconds(5)};
            timeout.Tick+=(s,e)=>frame.Continue=false;task.ContinueWith(t=>frame.Continue=false);timeout.Start();
            try{Dispatcher.PushFrame(frame);}finally{timeout.Stop();}
            if(!task.IsCompleted)throw new InvalidOperationException("Synthetic collection continuation timed out.");
        }
        return task.GetAwaiter().GetResult();
    }
    internal static void Run(Action<bool,string> check){
        check(Thread.CurrentThread.GetApartmentState()==ApartmentState.STA&&AppState.Captures.Count==0,"monitor session fixtures start on STA without an existing capture");
        var settings=AppState.Settings;AppState.Settings=new Preferences {Stroke=4,Color="#EF4444"};
        try{MonitorCoordinates(check);UncommittedGestures(check);CommittedSelection(check);SameMonitorReselection(check);EscapeAndHistory(check);EscapeStages(check);SwitchMonitor(check);CompleteSession(check);LateSessionResult(check);}
        finally{AppState.Settings=settings;}
        check(AppState.Captures.Count==0,"monitor session fixtures leave no capture members behind");
    }
    static void MonitorCoordinates(Action<bool,string> check){
        using(var s=new Session()){
            check(s.Open&&s.Resumed&&Inert(s.Left.Window)&&Inert(s.Right.Window),"both generated monitor overlays begin unsuspended and never become native or visible");
            check(AppState.CaptureForPoint(new System.Drawing.Point(-799,-99))==s.Left.Window&&AppState.CaptureForPoint(new System.Drawing.Point(-1,499))==s.Left.Window,"negative-origin monitor lookup uses its complete native desktop rectangle");
            check(AppState.CaptureForPoint(new System.Drawing.Point(0,0))==s.Right.Window&&AppState.CaptureForPoint(new System.Drawing.Point(599,399))==s.Right.Window,"adjacent monitor origin and lower-right interior resolve to the correct screen");
            check(AppState.CaptureForPoint(new System.Drawing.Point(-2000,-1000))==s.Left.Window&&AppState.CaptureForPoint(new System.Drawing.Point(2000,2000))==s.Right.Window,"points outside the synthetic desktop resolve to the nearest monitor");
            check(s.Left.Surface.ScaleX==2&&s.Left.Surface.ScaleY==2&&s.Right.Surface.ScaleX==1&&s.Right.Surface.ScaleY==1,"monitor fixture preserves independent logical-to-pixel scales");
            AppState.ChooseCapture(s.Left.Window);AppState.SyncCaptureSelection(s.Left.Window);
            check(s.Resumed&&AppState.CaptureForPoint(new System.Drawing.Point(100,100))==s.Right.Window,"choosing or syncing an empty crop cannot claim the session or hide another monitor");
            check(AppState.CanFocusCaptureFromPointer(s.Left.Window)&&AppState.CanFocusCaptureFromPointer(s.Right.Window),"either monitor may receive capture focus before a selection gesture starts");
        }
    }
    static void UncommittedGestures(Action<bool,string> check){
        using(var s=new Session()){
            var left=s.Left;var start=new Point(40,30);
            check(left.Window.BeginPointer(start)&&left.Window.HasCaptureGesture&&s.Resumed,"pointer-down starts a crop gesture without suspending other monitors");
            left.Window.ContinuePointer(new Point(240,160));AppState.SyncCaptureSelection(left.Window);
            check(left.Window.HasCaptureRegion&&left.Window.HasCaptureGesture&&s.Resumed,"a temporarily valid crop during dragging cannot suspend another monitor before commit");
            check(!AppState.CanFocusCaptureFromPointer(s.Right.Window),"another monitor cannot steal focus during a capture gesture");
            left.Window.CancelFromPointer();
            check(!left.Window.HasCaptureGesture&&!left.Window.HasCaptureRegion&&s.Resumed&&s.Open,"right click rolls back the first crop gesture while preserving both monitor snapshots");
            left.Window.BeginPointer(start);left.Window.EndPointer(start);
            check(!left.Window.HasCaptureRegion&&s.Resumed&&s.Open,"an empty click without an automatic target leaves all overlays available");
            left.Window.BeginPointer(start);left.Window.EndPointer(new Point(240,31));
            check(!left.Window.HasCaptureRegion&&s.Resumed,"a thin invalid crop cannot claim a monitor session");
            left.Window.BeginPointer(start);left.Window.ContinuePointer(new Point(240,160));left.Window.EndPointer(start);
            check(!left.Window.HasCaptureRegion&&s.Resumed,"a valid drag returned to its origin remains uncommitted on all monitors");
            left.Window.BeginPointer(start);left.Window.ContinuePointer(new Point(240,160));left.Window.CancelFromPointer();
            check(!left.Window.HasCaptureGesture&&!left.Window.HasCaptureRegion&&s.Resumed,"pointer cancellation of the initial gesture retains every monitor overlay");
            left.Window.BeginPointer(start);left.Window.ContinuePointer(new Point(240,160));
            left.Surface.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,Environment.TickCount){RoutedEvent=Mouse.LostMouseCaptureEvent});
            check(!left.Window.HasCaptureGesture&&!left.Window.HasCaptureRegion&&s.Resumed,"unexpected capture loss restores the unselected multi-monitor session");
        }
    }
    static void CommittedSelection(Action<bool,string> check){
        using(var s=new Session()){
            s.Left.Drag(s.Crop);
            check(s.Left.Surface.Selection==s.Crop&&s.Left.Window.HasCaptureRegion&&!s.Left.Window.HasCaptureGesture&&!s.Left.Window.IsCaptureOverlaySuspended&&s.Right.Window.IsCaptureOverlaySuspended,"committing a valid crop suspends only the other monitor");
            check(s.Open&&ReferenceEquals(s.Left.Shot.Image,s.Left.Image)&&ReferenceEquals(s.Right.Shot.Image,s.Right.Image)&&ReferenceEquals(s.Right.Surface.Image,s.Right.Image),"suspension retains both frozen snapshots and all session members");
            var exported=s.Left.Surface.Export(false,false);
            check(exported.PixelWidth==480&&exported.PixelHeight==300,"negative-origin 200-percent monitor crop exports its own scaled pixel dimensions");
            check(AppState.CaptureForPoint(new System.Drawing.Point(200,100))==s.Left.Window&&AppState.CaptureForPoint(new System.Drawing.Point(-2000,-1000))==s.Left.Window,"a committed owner remains the active capture regardless of pointer monitor");
            check(!s.Right.Window.BeginPointer(new Point(50,50))&&!s.Right.Window.HasCaptureGesture&&s.Right.Surface.Selection.IsEmpty,"a suspended overlay rejects pointer-down without creating a crop gesture");
            foreach(var key in new[]{Key.Escape,Key.Right,Key.R,Key.Z})check(!s.Right.Key(key,key==Key.Z?ModifierKeys.Control:ModifierKeys.None)&&s.Open&&s.Left.Surface.Selection==s.Crop&&s.Right.Window.IsCaptureOverlaySuspended,"a suspended overlay rejects keyboard input without changing its owner: "+key);
            s.Right.Window.CancelFromPointer();s.Right.Window.ContinuePointer(new Point(180,120));s.Right.Window.EndPointer(new Point(180,120));AppState.ChooseCapture(s.Right.Window);
            check(s.Open&&s.Left.Surface.Selection==s.Crop&&s.Right.Surface.Selection.IsEmpty&&s.Right.Window.IsCaptureOverlaySuspended,"stale pointer events and an empty monitor cannot take over a committed session");
            check(Inert(s.Left.Window)&&Inert(s.Right.Window),"session ownership and suspension preserve the inert-window guarantee");
        }
    }
    static void SameMonitorReselection(Action<bool,string> check){
        using(var s=new Session()){
            var left=s.Left;s.Left.Drag(s.Crop);var outside=new Point(300,210);
            left.Window.BeginPointer(outside);AppState.SyncCaptureSelection(left.Window);
            check(left.Surface.Selection.IsEmpty&&left.Window.HasCaptureGesture&&s.Right.Window.IsCaptureOverlaySuspended,"temporary empty crop during reselection does not resume a suspended monitor");
            left.Window.ContinuePointer(new Point(380,280));left.Window.CancelFromPointer();
            check(left.Surface.Selection==s.Crop&&!left.Window.HasCaptureGesture&&s.Right.Window.IsCaptureOverlaySuspended,"cancelling same-monitor reselection restores the committed owner without resuming siblings");
            left.Window.BeginPointer(outside);left.Window.EndPointer(new Point(380,211));
            check(left.Surface.Selection==s.Crop&&s.Right.Window.IsCaptureOverlaySuspended,"invalid same-monitor replacement retains the previous crop and suspended siblings");
            var replacement=new Rect(300,210,80,70);left.Drag(replacement);
            check(left.Surface.Selection==replacement&&s.Right.Window.IsCaptureOverlaySuspended&&s.Open,"valid same-monitor replacement keeps the existing session ownership");
            check(left.Key(Key.Z,ModifierKeys.Control)&&left.Surface.Selection==s.Crop&&s.Right.Window.IsCaptureOverlaySuspended,"undo of crop replacement restores the previous valid region without exposing another screen");
        }
    }
    static void EscapeAndHistory(Action<bool,string> check){
        using(var s=new Session()){
            var left=s.Left;left.Drag(s.Crop);left.Key(Key.R);left.Window.BeginPointer(new Point(90,70));left.Window.EndPointer(new Point(160,120));var mark=left.Surface.Marks.Single();
            left.Window.CancelFromPointer();check(left.Window.SelectedTool=="select"&&left.Surface.Selection==s.Crop&&s.Right.Window.IsCaptureOverlaySuspended,"right click exits a drawing tool before resuming other monitors");
            left.Window.BeginPointer(new Point(90,90));left.Window.EndPointer(new Point(90,90));
            check(left.Surface.SelectedAnnotation==mark,"pointer can select an existing annotation before cancellation");left.Window.CancelFromPointer();
            check(left.Surface.SelectedAnnotation==null&&s.Right.Window.IsCaptureOverlaySuspended,"right click deselects a mark without clearing the crop or exposing siblings");
            left.Window.CancelFromPointer();check(left.Surface.Selection.IsEmpty&&left.Surface.Marks.Count==0&&s.Resumed&&s.Open,"right click clearing the crop resumes all original overlays without closing their snapshots");
            check(AppState.CaptureForPoint(new System.Drawing.Point(100,100))==s.Right.Window,"after clearing a region, point-based selection can target the other monitor");
            check(left.Key(Key.Z,ModifierKeys.Control)&&left.Surface.Selection==s.Crop&&left.Surface.Marks.Single()==mark&&s.Right.Window.IsCaptureOverlaySuspended,"undo of region clearing restores the original annotation object and monitor owner");
            check(left.Key(Key.Y,ModifierKeys.Control)&&left.Surface.Selection.IsEmpty&&s.Resumed,"redo of region clearing resumes every retained monitor");
            left.Key(Key.Z,ModifierKeys.Control);left.Key(Key.Z,ModifierKeys.Control);
            check(left.Surface.Selection==s.Crop&&left.Surface.Marks.Count==0&&s.Right.Window.IsCaptureOverlaySuspended,"undoing only an annotation cannot change the monitor-session owner");
            check(left.Key(Key.Z,ModifierKeys.Control)&&left.Surface.Selection.IsEmpty&&s.Resumed&&s.Open,"undo of the first region selection returns to an empty multi-monitor session");
            check(left.Key(Key.Z,ModifierKeys.Control|ModifierKeys.Shift)&&left.Surface.Selection==s.Crop&&s.Right.Window.IsCaptureOverlaySuspended,"redo of the first region reclaims its monitor and suspends siblings");
            check(left.Key(Key.Escape)&&AppState.Captures.Count==0&&Closed(left.Window)&&Closed(s.Right.Window),"one Escape from a committed crop closes the entire capture session without an intermediate cleared state");
        }
    }
    static void SwitchMonitor(Action<bool,string> check){
        using(var s=new Session()){
            s.Left.Drag(s.Crop);s.Left.Window.CancelFromPointer();var otherCrop=new Rect(60,50,260,180);s.Right.Drag(otherCrop);
            check(s.Right.Surface.Selection==otherCrop&&s.Left.Surface.Selection.IsEmpty&&s.Left.Window.IsCaptureOverlaySuspended&&!s.Right.Window.IsCaptureOverlaySuspended,"clearing one monitor allows a valid crop on the other to become the owner");
            check(AppState.CaptureForPoint(new System.Drawing.Point(-500,100))==s.Right.Window&&!s.Left.Key(Key.Z,ModifierKeys.Control)&&!s.Left.Window.BeginPointer(new Point(50,50)),"the old monitor's history and pointer path cannot steal a new monitor owner");
            s.Right.Window.CancelFromPointer();
            check(s.Resumed&&s.Open&&ReferenceEquals(s.Left.Shot.Image,s.Left.Image)&&ReferenceEquals(s.Right.Shot.Image,s.Right.Image),"switching screens and clearing again keeps the original frozen images available");
            check(s.Left.Key(Key.Z,ModifierKeys.Control)&&s.Left.Surface.Selection==s.Crop&&s.Right.Window.IsCaptureOverlaySuspended,"undo may reclaim the original monitor only after the newer owner has been cleared");
        }
    }
    static void EscapeStages(Action<bool,string> check){
        foreach(string stage in new[]{"empty","first-crop-gesture","selected","crop-adjust","new-drawing","live-text"})using(var s=new Session()){
            var left=s.Left;
            if(stage=="first-crop-gesture"){left.Window.BeginPointer(s.Crop.TopLeft);left.Window.ContinuePointer(s.Crop.BottomRight);}
            else if(stage!="empty"){
                left.Drag(s.Crop);
                if(stage=="crop-adjust"){left.Window.BeginPointer(s.Crop.TopLeft);left.Window.ContinuePointer(s.Crop.TopLeft+new Vector(10,10));}
                if(stage=="new-drawing"){left.Key(Key.R);left.Window.BeginPointer(new Point(90,70));left.Window.ContinuePointer(new Point(160,120));}
                if(stage=="live-text"){left.Key(Key.T);left.Window.BeginPointer(new Point(90,70));Field<TextBox>(left.Window,"textEditor").Text="unfinished";}
            }
            check(left.Key(Key.Escape)&&Closed(left.Window)&&Closed(s.Right.Window)&&AppState.Captures.Count==0,"one Escape ends every monitor overlay immediately from "+stage);
            check(left.Shot.Image==null&&s.Right.Shot.Image==null&&left.Surface.Image==null&&s.Right.Surface.Image==null&&left.Surface.Marks.Count==0&&left.Surface.Draft==null&&Field<TextBox>(left.Window,"textEditor")==null,"whole-session Escape releases original snapshots and unfinished edits: "+stage);
        }
    }
    static void CompleteSession(Action<bool,string> check){
        foreach(bool closeSuspended in new[]{false,true})using(var s=new Session()){
            s.Left.Drag(s.Crop);s.Left.Surface.Marks.Add(new Annotation {Kind="text",Start=new Point(80,70),Text="retained"});s.Right.Surface.Marks.Add(new Annotation {Kind="rect",Start=new Point(30,30),End=new Point(80,80)});
            s.Left.Surface.TranslationImage=s.Left.Image;s.Right.Surface.TranslationImage=s.Right.Image;
            (closeSuspended?s.Right.Window:s.Left.Window).Close();
            check(AppState.Captures.Count==0&&Closed(s.Left.Window)&&Closed(s.Right.Window)&&s.Left.Shot.Image==null&&s.Right.Shot.Image==null,"closing any member completes its entire monitor session: suspended="+closeSuspended);
            check(s.Left.Surface.Image==null&&s.Right.Surface.Image==null&&s.Left.Surface.TranslationImage==null&&s.Right.Surface.TranslationImage==null&&s.Left.Surface.Marks.Count==0&&s.Right.Surface.Marks.Count==0&&s.Left.Surface.Draft==null&&s.Right.Surface.Draft==null,"whole-session completion releases pixels, translations, annotations and drafts on both monitors");
            check(AppState.CaptureForPoint(new System.Drawing.Point(100,100))==null&&!s.Left.Window.HasCaptureGesture&&!s.Right.Window.HasCaptureGesture,"completed sessions retain neither an active owner nor an unfinished gesture");
        }
    }
    static void LateSessionResult(Action<bool,string> check){
        foreach(string outcome in new[]{"success","not-saved","error"})LateSessionResult(check,outcome);
    }
    static void LateSessionResult(Action<bool,string> check,string outcome){
        var collect=MoriDesk.DeskRuntime.CollectImage;var context=SynchronizationContext.Current;var pending=new TaskCompletionSource<bool>();Task<bool> result=null;
        MoriDesk.DeskRuntime.CollectImage=image=>pending.Task;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try{using(var old=new Session()){
            old.Left.Drag(old.Crop);result=old.Left.Window.CollectToLibraryAsync();
            check(!result.IsCompleted&&old.Left.Window.HasCaptureGesture&&old.Right.Window.IsCaptureOverlaySuspended,"synthetic pending collection keeps its original monitor session owned and suspended");
            check(old.Left.Key(Key.Escape)&&AppState.Captures.Count==0&&Closed(old.Left.Window)&&Closed(old.Right.Window),"one Escape closes every overlay even while an already-submitted library collection is pending");
            using(var current=new Session()){
                var crop=new Rect(60,50,260,180);current.Right.Drag(crop);
                if(outcome=="error")pending.SetException(new InvalidOperationException("synthetic collection failure"));else pending.SetResult(outcome=="success");
                check(Await(result)==(outcome=="success")&&current.Open&&current.Right.Surface.Selection==crop&&current.Left.Window.IsCaptureOverlaySuspended,"late collection result from a closed session cannot close or reveal a newer session: "+outcome);
                check(Field<TextBlock>(old.Left.Window,"translationStatus").Visibility==Visibility.Collapsed,"late collection result never restores status on its closed overlay: "+outcome);
                AppState.CaptureClosed(old.Left.Window);AppState.SyncCaptureSelection(old.Left.Window);AppState.ChooseCapture(old.Left.Window);
                check(current.Open&&AppState.CaptureForPoint(new System.Drawing.Point(-500,100))==current.Right.Window&&current.Left.Window.IsCaptureOverlaySuspended&&!old.Left.Key(Key.Escape),"late close and selection notifications from old windows cannot affect the new session owner");
            }
        }}finally{pending.TrySetResult(false);if(result!=null&&!result.IsCompleted)Await(result);SynchronizationContext.SetSynchronizationContext(context);MoriDesk.DeskRuntime.CollectImage=collect;}
    }
}
