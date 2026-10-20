using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QingJie;

// Synthetic pixels, unshown WPF objects and a unique runner-owned directory.
// Output dependencies are fake; no native screen/input or system copy operations.
internal static class CaptureOutputLifecycleTests {
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static T Field<T>(object instance,string name){return (T)instance.GetType().GetField(name,Hidden).GetValue(instance);}
    static bool Closed(CaptureWindow window){return Field<bool>(window,"closed");}
    static int History(CaptureWindow window,string name){var stack=Field<object>(window,name);return (int)stack.GetType().GetProperty("Count").GetValue(stack,null);}
    static Point TextBody(Annotation text){var bounds=text.Bounds;return new Point(bounds.Left+bounds.Width*.3,bounds.Top+bounds.Height*.5);}
    static IEnumerable<DependencyObject> Tree(DependencyObject parent){yield return parent;foreach(object child in LogicalTreeHelper.GetChildren(parent)){var node=child as DependencyObject;if(node!=null)foreach(var nested in Tree(node))yield return nested;}}
    static Button ButtonNamed(CaptureWindow window,string name){return Tree(window.Content as DependencyObject).OfType<Button>().Single(button=>button.Name==name);}
    static void Click(Button button){button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
    static BitmapSource Pixels(){var pixels=new byte[400*300*4];for(int i=0;i<pixels.Length;i+=4){pixels[i]=80;pixels[i+1]=150;pixels[i+2]=220;pixels[i+3]=255;}var image=BitmapSource.Create(400,300,96,96,PixelFormats.Bgra32,null,pixels,1600);image.Freeze();return image;}
    static T Await<T>(Task<T> task){
        var deadline=DateTime.UtcNow.AddSeconds(8);
        while(!task.IsCompleted&&DateTime.UtcNow<deadline){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);Thread.Sleep(1);}
        if(!task.IsCompleted)throw new InvalidOperationException("Synthetic output timed out.");return task.GetAwaiter().GetResult();
    }
    static bool Wait(Task task){return Await(Complete(task));}
    static async Task<bool> Complete(Task task){await task;return true;}
    sealed class FakeOutputs : ICaptureOutputActions {
        internal int Calls;internal string Kind;internal BitmapSource Image;internal Int32Rect Bounds;internal CancellationToken Token;
        internal Func<Task<bool>> Response=()=>Task.FromResult(true);
        Task<bool> Run(string kind,BitmapSource image,CancellationToken cancel){Calls++;Kind=kind;Image=image;Token=cancel;return Response();}
        public Task<bool> Copy(BitmapSource image,CancellationToken cancel){return Run("copy",image,cancel);}
        public Task<bool> Save(BitmapSource image,Window owner,CancellationToken cancel){return Run("save",image,cancel);}
        public Task<bool> Pin(BitmapSource image,Int32Rect bounds,CancellationToken cancel){Bounds=bounds;return Run("pin",image,cancel);}
    }
    sealed class Fixture : IDisposable {
        internal readonly CaptureWindow Window;
        internal readonly ScreenshotSurface Surface;
        internal readonly FakeOutputs Outputs=new FakeOutputs();
        internal readonly Rect Crop=new Rect(30,20,300,220);
        internal Fixture(){Window=new CaptureWindow(new Snapshot{Image=Pixels(),Bounds=new System.Drawing.Rectangle(-600,-100,400,300)});Window.OutputActions=Outputs;Surface=Field<ScreenshotSurface>(Window,"surface");Surface.Measure(new Size(400,300));Surface.Arrange(new Rect(0,0,400,300));Window.BeginPointer(Crop.TopLeft);Window.EndPointer(Crop.BottomRight);Window.HandleCaptureKey(Key.A,ModifierKeys.None);Window.BeginPointer(new Point(90,80));Window.EndPointer(new Point(180,130));}
        internal Task<bool> Output(string kind){return kind=="copy"?Window.CopyCaptureAsync():kind=="save"?Window.SaveCaptureAsync():Window.PinCaptureAsync();}
        public void Dispose(){if(!Closed(Window))Window.Close();}
    }
    sealed class PreparationFixture : IDisposable {
        internal readonly Fixture Capture=new Fixture();
        internal readonly ExternalScrollCaptureSession Session;
        internal PreparationFixture(){
            Session=new ExternalScrollCaptureSession(Path.GetFullPath(Environment.GetEnvironmentVariable("MORI_DESK_DATA")),new System.Drawing.Rectangle(-570,-80,300,220),Capture.Window.CaptureScreenBounds,1);
        }
        internal void Prepare(){Capture.Window.GetType().GetField("scrollingCapture",Hidden).SetValue(Capture.Window,Session);Capture.Window.SetScrollingPreparationUi(true);}
        public void Dispose(){Capture.Dispose();Session.Dispose();}
    }
    // Exercise the real window-to-helper output adapter, not a callback that
    // already returns the expected protocol status. The helper is never run.
    sealed class ScrollingFixture : IDisposable {
        internal readonly Fixture Capture=new Fixture();
        internal readonly ExternalScrollCaptureSession Session;
        internal readonly BitmapSource Image;
        internal ExternalScrollResult LastRequest;
        int nextExport;
        internal ScrollingFixture(){
            var monitor=Capture.Window.CaptureScreenBounds;
            Session=new ExternalScrollCaptureSession(Path.GetFullPath(Environment.GetEnvironmentVariable("MORI_DESK_DATA")),new System.Drawing.Rectangle(-570,-80,300,220),monitor,1);
            string expected=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"Nook","scroll-capture",Session.SessionId));
            if(Session.OutputDirectory!=expected||Directory.Exists(expected)||File.Exists(expected))throw new IOException("Refusing to reuse any existing screenshot session.");
            Directory.CreateDirectory(expected);
            var pixels=new byte[40*600*4];for(int i=0;i<pixels.Length;i+=4){pixels[i]=31;pixels[i+1]=96;pixels[i+2]=210;pixels[i+3]=255;}
            Image=BitmapSource.Create(40,600,96,96,PixelFormats.Bgra32,null,pixels,160);Image.Freeze();
            Capture.Window.GetType().GetField("scrollingCapture",Hidden).SetValue(Capture.Window,Session);
            Capture.Window.SuspendCaptureOverlay();
        }
        internal Task<ExternalScrollExportReply> Export(string action){
            int id=++nextExport;
            string path=Path.Combine(Session.OutputDirectory,"export-"+id+".png");ImageFiles.Write(Image,path);
            var request=new ExternalScrollResult{schemaVersion=1,sessionId=Session.SessionId,status="export",exportId=id,action=action,imagePath=path};
            LastRequest=request;
            return Session.ProcessExport(request,output=>Dispatch(output));
        }
        internal Exception FailReply(ExternalScrollExportReply reply){
            // Fail the actual transport write after the real export callback.
            // This is a new empty fixture-owned directory, never user data.
            string blocked=Path.Combine(Session.OutputDirectory,"export-"+LastRequest.exportId+".reply.json.tmp");
            Directory.CreateDirectory(blocked);
            try{Session.GetType().GetMethod("WriteExportReply",Hidden).Invoke(Session,new object[]{LastRequest,reply});}
            catch(TargetInvocationException error){return error.InnerException;}
            finally{Directory.Delete(blocked,false);}
            throw new InvalidOperationException("Expected synthetic transport write failure.");
        }
        internal Task<ExternalScrollExportReply> Dispatch(ExternalScrollResult request){
            return (Task<ExternalScrollExportReply>)Capture.Window.GetType().GetMethod("ExportScrollingImage",Hidden).Invoke(Capture.Window,new object[]{Session,request});
        }
        public void Dispose(){
            Capture.Dispose();Session.Dispose();
            string expected=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"Nook","scroll-capture",Session.SessionId));
            if(Session.OutputDirectory!=expected)throw new IOException("Unexpected screenshot sandbox path.");
            if(!Directory.Exists(expected))return;
            if((File.GetAttributes(expected)&FileAttributes.ReparsePoint)!=0)throw new IOException("Screenshot sandbox link refused.");
            for(int id=1;id<=nextExport;id++){
                string path=Path.Combine(expected,"export-"+id+".png");
                if(File.Exists(path)){if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("Screenshot image link refused.");File.Delete(path);}
            }
            // Nonrecursive: an unexpected file is never removed by this test.
            Directory.Delete(expected,false);
        }
    }
    internal static void Run(Action<bool,string> check){
        var oldContext=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var directory=Path.Combine(Environment.GetEnvironmentVariable("MORI_DESK_DATA"),"output-lifecycle-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try{ScrollingPreparation(check);Outputs(check);TextAfterUnsuccessfulSave(check);ScrollingOutputs(check);ScrollingRecovery(check);LateResult(check);ReaderReturn(check);ClipboardRetry(check);AtomicSave(directory,check);Pins(directory,check);Placement(check);}
        finally{SynchronizationContext.SetSynchronizationContext(oldContext);}
    }
    static void ScrollingPreparation(Action<bool,string> check){
        var preferences=AppState.Settings;var collector=MoriDesk.DeskRuntime.CollectImage;
        AppState.Settings=new Preferences {Stroke=4,TextSize=20,Color="#EF4444"};
        try{
            int collections=0;MoriDesk.DeskRuntime.CollectImage=image=>{collections++;return Task.FromResult(true);};
            using(var f=new PreparationFixture()){
                var window=f.Capture.Window;var surface=f.Capture.Surface;var image=surface.Image;var mark=surface.Marks.Single();
                Point start=mark.Start,end=mark.End;string color=mark.Color,kind=mark.Kind,tool=window.SelectedTool,target=Field<string>(window,"translationTarget");double width=mark.Width;
                int undo=History(window,"annotationUndo"),redo=History(window,"annotationRedo");
                f.Prepare();window.SetScrollingPreparationUi(true);
                check(!window.CanEditCapture&&window.IsScrollingCapture&&window.HasCaptureGesture&&!window.IsCaptureOverlaySuspended,"preparing scroll keeps the original overlay unsuspended and owns capture input");
                check(ButtonNamed(window,"CaptureCancel").IsEnabled&&Tree(Field<StackPanel>(window,"toolbar")).OfType<Button>().Where(button=>button.Name!="CaptureCancel").All(button=>!button.IsEnabled),"preparing scroll disables every main action except the still-usable Cancel");
                foreach(var key in new[]{Key.V,Key.R,Key.T,Key.Delete,Key.Left,Key.Right,Key.Enter})check(window.HandleCaptureKey(key,ModifierKeys.None),"preparing scroll consumes editing key "+key);
                foreach(var key in new[]{Key.Z,Key.Y,Key.S,Key.C,Key.D})check(window.HandleCaptureKey(key,ModifierKeys.Control),"preparing scroll consumes command key "+key);
                check(!window.BeginPointer(new Point(90,80),2)&&!window.BeginPointer(f.Capture.Crop.TopLeft)&&!window.AdjustCropWithKeyboard(Key.Right,ModifierKeys.None),"preparing scroll rejects text/shape/crop pointer starts and direct crop keyboard entry");
                window.ContinuePointer(new Point(230,190));window.EndPointer(new Point(260,200));
                check(window.HandleCaptureWheel(120,ModifierKeys.Control,new Point(150,140))&&!window.CanCompleteCaptureAt(new Point(300,200)),"preparing scroll consumes wheel and cannot double-click complete the short image");
                // Raising Click bypasses a disabled button's ordinary hit test.
                // Every real command must still enforce the ownership gate.
                foreach(string name in new[]{"CaptureTool_rect","CaptureUndo","CaptureRedo","CaptureSizeIncrease","CaptureSizeDecrease","LineMode_line","CaptureOcr","CaptureCopy","CaptureSave","CapturePin","CaptureCollect","CaptureScroll"})Click(ButtonNamed(window,name));
                Click(Tree(Field<StackPanel>(window,"properties")).OfType<Button>().First(button=>button.Width==18));
                Click(Field<Button>(window,"translateButton"));
                var language=Tree(Field<StackPanel>(window,"toolbar")).OfType<Button>().Single(button=>button.ContextMenu!=null);
                ((MenuItem)language.ContextMenu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                check(Field<string>(window,"translationTarget")==target&&Field<TextWindow>(window,"captureTextViewer")==null&&Field<CancellationTokenSource>(window,"translationCancel")==null,"preparing scroll blocks queued language/OCR/translation actions before opening a viewer or starting recognition");
                foreach(string action in new[]{"copy","save","pin"})check(!Await(f.Capture.Output(action)),"preparing scroll refuses direct short-image output "+action);
                check(!Await(window.CollectToLibraryAsync())&&collections==0&&f.Capture.Outputs.Calls==0,"preparing scroll sends no short-image output or collection to a destination");
                check(surface.Image==image&&surface.Selection==f.Capture.Crop&&surface.Marks.Count==1&&surface.Marks.Single()==mark&&mark.Start==start&&mark.End==end&&mark.Width==width&&mark.Color==color&&mark.Kind==kind&&window.SelectedTool==tool,"all queued preparing inputs preserve exact frozen pixels, crop and original annotation values");
                check(History(window,"annotationUndo")==undo&&History(window,"annotationRedo")==redo&&AppState.Settings.Stroke==4&&AppState.Settings.TextSize==20&&AppState.Settings.Color=="#EF4444","preparing scroll preserves Undo/Redo and drawing defaults without settings writes");
                Wait(window.HandleScrollingFailureAsync(f.Session,new IOException("synthetic cold start failed")));
                check(window.CanEditCapture&&!window.IsScrollingCapture&&!window.IsCaptureOverlaySuspended&&f.Session.IsDisposed&&surface.Image==image&&surface.Selection==f.Capture.Crop&&surface.Marks.Single()==mark,"failed cold start restores the exact editable source without requiring a previous hide");
                check(Field<StackPanel>(window,"properties").IsEnabled&&ButtonNamed(window,"CaptureUndo").IsEnabled&&!ButtonNamed(window,"CaptureRedo").IsEnabled&&ButtonNamed(window,"CaptureSizeIncrease").IsEnabled,"preparation failure restores tools and derives history/size availability from the retained scene");
                check(!window.IsVisible&&new System.Windows.Interop.WindowInteropHelper(window).Handle==IntPtr.Zero&&!Directory.Exists(f.Session.OutputDirectory),"preparation and recovery stay inert: no native window, helper or session directory");
                window.HandleCaptureKey(Key.Z,ModifierKeys.Control);check(surface.Marks.Count==0&&surface.Selection==f.Capture.Crop,"Undo remains usable after cold-start recovery");
            }
            using(var f=new PreparationFixture()){
                var window=f.Capture.Window;window.HandleCaptureKey(Key.T,ModifierKeys.None);window.BeginPointer(new Point(60,160));var editor=Field<TextBox>(window,"textEditor");editor.Text="minimum text";editor.FontSize=16;window.HandleCaptureKey(Key.Enter,ModifierKeys.Control);
                var text=f.Capture.Surface.SelectedText;f.Prepare();Wait(window.HandleScrollingFailureAsync(f.Session,new IOException("synthetic startup failure")));
                check(f.Capture.Surface.SelectedText==text&&text.TextFontSize==16&&!ButtonNamed(window,"CaptureSizeDecrease").IsEnabled&&ButtonNamed(window,"CaptureSizeIncrease").IsEnabled,"recovery preserves selected caption and does not enable a below-minimum font decrement");
            }
            using(var f=new PreparationFixture()){
                f.Prepare();using(var stale=new ExternalScrollCaptureSession(Path.GetFullPath(Environment.GetEnvironmentVariable("MORI_DESK_DATA")),new System.Drawing.Rectangle(-570,-80,300,220),f.Capture.Window.CaptureScreenBounds,1)){
                    check(!Await(f.Capture.Window.HandoffScrollingCaptureAsync(stale))&&!f.Capture.Window.IsCaptureOverlaySuspended,"a stale prepared callback cannot hide the current selection");
                }
                check(Await(f.Capture.Window.HandoffScrollingCaptureAsync(f.Session))&&f.Capture.Window.IsCaptureOverlaySuspended&&!f.Capture.Window.IsVisible&&new System.Windows.Interop.WindowInteropHelper(f.Capture.Window).Handle==IntPtr.Zero,"matching prepared handoff suspends only the inert source without ever presenting a native window");
                Wait(f.Capture.Window.HandleScrollingFailureAsync(f.Session,new IOException("synthetic post-handoff failure")));
                check(f.Capture.Window.CanEditCapture&&!f.Capture.Window.IsCaptureOverlaySuspended&&f.Capture.Surface.Selection==f.Capture.Crop&&f.Capture.Surface.Marks.Count==1,"post-handoff failure restores the original selection and editable marks");
                check(ButtonNamed(f.Capture.Window,"CaptureUndo").IsEnabled&&!ButtonNamed(f.Capture.Window,"CaptureRedo").IsEnabled&&ButtonNamed(f.Capture.Window,"CaptureSizeIncrease").IsEnabled,"post-handoff failure recomputes usable history and property buttons only after resuming the overlay");
            }
            foreach(string cancel in new[]{"escape","right-click","button","during-handoff"})using(var f=new PreparationFixture()){
                var window=f.Capture.Window;f.Prepare();Task<bool> handoff=null;
                if(cancel=="during-handoff")handoff=window.HandoffScrollingCaptureAsync(f.Session);
                if(cancel=="right-click")window.CancelFromPointer();
                else if(cancel=="button"){AppState.Captures.Add(window);Click(ButtonNamed(window,"CaptureCancel"));}
                else check(window.HandleCaptureKey(Key.Escape,ModifierKeys.None),"one Escape is accepted while preparing or handing off: "+cancel);
                check(Closed(window)&&f.Session.IsDisposed&&!window.IsVisible,"preparing cancellation closes the old editor and cancels its session: "+cancel);
                check(!Await(handoff??window.HandoffScrollingCaptureAsync(f.Session)),"late prepared handoff after cancellation cannot release or resurrect an overlay: "+cancel);
            }
        }finally{AppState.Settings=preferences;MoriDesk.DeskRuntime.CollectImage=collector;}
    }
    static void ScrollingRecovery(Action<bool,string> check){
        foreach(bool failed in new[]{false,true})using(var f=new ScrollingFixture()){
            f.Capture.Outputs.Response=()=>{if(failed)throw new IOException("synthetic save failed");return Task.FromResult(false);};
            var reply=Await(f.Export("save"));var transportFailure=f.FailReply(reply);ScrollCapturePreviewWindow preview=null;
            check(transportFailure is IOException||transportFailure is UnauthorizedAccessException,"actual reply-file write fails after an unsuccessful destination: "+failed);
            var snapshot=f.Session.RecoverySnapshot();snapshot.VerifiedImage.imagePath="changed snapshot only";
            check(f.Session.RecoverySnapshot().VerifiedImage.imagePath==f.LastRequest.imagePath,"recovery snapshots cannot mutate the session's verified metadata");
            f.Capture.Window.PresentScrollRecovery=window=>{
                preview=window;var pixels=Field<BitmapSource>(window,"image");
                check(!Closed(f.Capture.Window)&&f.Capture.Surface.Image!=null&&f.Capture.Window.IsCaptureOverlaySuspended,"source editor remains intact until long-image presentation succeeds");
                check(pixels.IsFrozen&&pixels.PixelWidth==40&&pixels.PixelHeight==600,"recovery presents the complete verified long image, never the short frozen crop");
            };
            Wait(f.Capture.Window.HandleScrollingFailureAsync(f.Session,transportFailure));
            check(preview!=null&&Closed(f.Capture.Window)&&f.Session.IsDisposed&&f.Capture.Outputs.Calls==1,"unsuccessful export plus broken reply transfers exactly once to retryable long-image preview");
            check(File.Exists(f.LastRequest.imagePath)&&!Field<bool>(preview,"closed"),"failed transport preserves image bytes and an independently live recovery preview");preview.Close();
        }
        var priorCollect=MoriDesk.DeskRuntime.CollectImage;
        try{
            foreach(string action in new[]{"copy","save","collect"})using(var f=new ScrollingFixture()){
                int collections=0,presented=0;MoriDesk.DeskRuntime.CollectImage=image=>{collections++;return Task.FromResult(true);};
                var reply=Await(f.Export(action));var failure=f.FailReply(reply);f.Capture.Window.PresentScrollRecovery=unused=>presented++;
                var state=f.Session.RecoverySnapshot();
                check(state.CompletedExportId==1&&state.CompletedExportAction==action,"local "+action+" acknowledgement survives reply-file failure");
                Wait(f.Capture.Window.HandleScrollingFailureAsync(f.Session,failure));
                check(Closed(f.Capture.Window)&&f.Session.IsDisposed&&presented==0&&f.Capture.Outputs.Calls+collections==1,"confirmed "+action+" finishes without another output or a duplicate-capable recovery preview");
                check(File.Exists(f.LastRequest.imagePath),"transport failure never deletes the verified output file: "+action);
            }
        }finally{MoriDesk.DeskRuntime.CollectImage=priorCollect;}
        foreach(string failure in new[]{"presenter","damaged","unverified"})using(var f=new ScrollingFixture()){
            var source=f.Capture.Surface.Image;var mark=f.Capture.Surface.Marks.Single();ScrollCapturePreviewWindow attempted=null;
            if(failure!="unverified"){f.Capture.Outputs.Response=()=>Task.FromResult(false);Await(f.Export("save"));}
            if(failure=="damaged")File.WriteAllBytes(f.LastRequest.imagePath,new byte[]{1,2,3});
            f.Capture.Window.PresentScrollRecovery=window=>{attempted=window;throw new IOException("synthetic presenter failure");};
            Wait(f.Capture.Window.HandleScrollingFailureAsync(f.Session,new IOException("synthetic helper failure")));
            check(!Closed(f.Capture.Window)&&!f.Capture.Window.IsCaptureOverlaySuspended&&!f.Capture.Window.IsScrollingCapture&&f.Session.IsDisposed,"failed long-image recovery restores the original editable overlay: "+failure);
            check(ReferenceEquals(source,f.Capture.Surface.Image)&&f.Capture.Surface.Selection==f.Capture.Crop&&f.Capture.Surface.Marks.Single()==mark,"recovery failure preserves frozen pixels, crop and exact annotation: "+failure);
            check(failure=="presenter"?attempted!=null&&Field<bool>(attempted,"closed"):attempted==null,"invalid or unpresentable recovery never leaves a partial preview alive: "+failure);
        }
        foreach(string lifecycle in new[]{"closed","replaced","quitting"})using(var f=new ScrollingFixture()){
            f.Capture.Outputs.Response=()=>Task.FromResult(false);Await(f.Export("save"));int presented=0;f.Capture.Window.PresentScrollRecovery=unused=>presented++;
            var priorQuitting=AppState.Quitting;
            try{
                if(lifecycle=="closed")f.Capture.Window.Close();
                else if(lifecycle=="replaced")f.Capture.Window.GetType().GetField("scrollingCapture",Hidden).SetValue(f.Capture.Window,null);
                else AppState.Quitting=true;
                Wait(f.Capture.Window.HandleScrollingFailureAsync(f.Session,new IOException("late helper failure")));
                check(presented==0&&f.Capture.Outputs.Calls==1&&(lifecycle=="closed"||!Closed(f.Capture.Window)),"late failure cannot recover or close an unrelated capture after "+lifecycle);
            }finally{AppState.Quitting=priorQuitting;}
        }
    }
    static void ScrollingOutputs(Action<bool,string> check){
        foreach(string outcome in new[]{"cancelled","failed","async-failed","cancelled-task"})using(var f=new ScrollingFixture()){
            var source=f.Capture.Surface.Image;var annotation=f.Capture.Surface.Marks.Single();
            var pending=new TaskCompletionSource<bool>();
            f.Capture.Outputs.Response=()=>{
                if(outcome=="failed")throw new IOException("synthetic scrolling save failure");
                if(outcome=="cancelled-task")throw new OperationCanceledException();
                return outcome=="async-failed"?pending.Task:Task.FromResult(false);
            };
            var save=f.Export("save");
            if(outcome=="async-failed"){
                // Image decoding is intentionally asynchronous; pump only the
                // test dispatcher until the fake destination receives it.
                var deadline=DateTime.UtcNow.AddSeconds(8);
                while(f.Capture.Outputs.Calls==0&&DateTime.UtcNow<deadline){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);Thread.Sleep(1);}
                check(!save.IsCompleted&&f.Capture.Outputs.Calls==1&&Field<bool>(f.Capture.Window,"collecting"),"pending scrolling save owns the output lifetime without starting a helper");
                pending.SetException(new IOException("synthetic scrolling save failure"));
            }
            var reply=Await(save);string expected=outcome=="failed"||outcome=="async-failed"?"error":"cancelled";
            check(reply.status==expected&&(expected!="error"||reply.message=="synthetic scrolling save failure"),"actual scrolling Save adapter reports recoverable "+outcome+" instead of acknowledging success");
            check(!Closed(f.Capture.Window)&&!f.Session.IsDisposed&&f.Capture.Window.IsScrollingCapture&&f.Capture.Window.IsCaptureOverlaySuspended&&!Field<bool>(f.Capture.Window,"collecting"),"unsuccessful scrolling Save retains the same session and releases output controls: "+outcome);
            check(ReferenceEquals(f.Capture.Surface.Image,source)&&f.Capture.Surface.Selection==f.Capture.Crop&&f.Capture.Surface.Marks.Single()==annotation,"unsuccessful scrolling Save preserves the frozen source, crop and annotations: "+outcome);
            check(f.Capture.Outputs.Kind=="save"&&f.Capture.Outputs.Calls==1&&f.Capture.Outputs.Image.IsFrozen&&f.Capture.Outputs.Image.PixelWidth==40&&f.Capture.Outputs.Image.PixelHeight==600,"scrolling Save uses the verified long image, not the original short crop: "+outcome);
            f.Capture.Outputs.Response=()=>Task.FromResult(true);
            var retry=Await(f.Export("copy"));
            check(retry.status=="ok"&&f.Capture.Outputs.Calls==2&&f.Capture.Outputs.Kind=="copy"&&!Field<bool>(f.Capture.Window,"collecting"),"real scrolling adapter can copy the same long image after unsuccessful Save: "+outcome);
            check(!Closed(f.Capture.Window)&&f.Capture.Window.IsCaptureOverlaySuspended&&!f.Session.IsDisposed,"confirmed scrolling output awaits its terminal message without premature teardown: "+outcome);
        }
        using(var f=new ScrollingFixture()){
            f.Capture.Window.Close();
            // A dispatch already queued by an old helper must not read its
            // missing image or invoke a destination after its source closed.
            var reply=Await(f.Dispatch(new ExternalScrollResult{schemaVersion=1,sessionId=f.Session.SessionId,status="export",exportId=1,action="save",imagePath=Path.Combine(f.Session.OutputDirectory,"export-1.png")}));
            check(reply.status=="cancelled"&&f.Capture.Outputs.Calls==0&&f.Session.IsDisposed,"closed scrolling source refuses queued output before image loading or save side effects");
        }
    }
    static void Outputs(Action<bool,string> check){
        foreach(string kind in new[]{"copy","save","pin"})using(var f=new Fixture()){
            var annotation=f.Surface.Marks.Single();
            f.Outputs.Response=()=>{throw new IOException("synthetic output unavailable");};
            check(!Await(f.Output(kind))&&!Closed(f.Window)&&f.Surface.Marks.Single()==annotation&&f.Surface.Selection==f.Crop,"failed "+kind+" preserves exact editable image, selection and annotation");
            check(f.Surface.IsEnabled&&Field<StackPanel>(f.Window,"toolbar").IsEnabled&&Field<TextBlock>(f.Window,"translationStatus").Text.Contains("截图已保留"),"failed "+kind+" restores controls with inline recovery guidance");
            check(ButtonNamed(f.Window,"CaptureUndo").IsEnabled&&!ButtonNamed(f.Window,"CaptureRedo").IsEnabled&&ButtonNamed(f.Window,"CaptureSizeIncrease").IsEnabled,"failed "+kind+" restores individual history/property commands after releasing its output gate");
            f.Outputs.Response=()=>Task.FromResult(false);
            check(!Await(f.Output(kind))&&!Closed(f.Window)&&f.Window.Topmost,"cancelled "+kind+" leaves the same screenshot and topmost state");
            var pending=new TaskCompletionSource<bool>();f.Outputs.Response=()=>pending.Task;
            var output=f.Output(kind);int calls=f.Outputs.Calls;
            check(!output.IsCompleted&&!Closed(f.Window)&&!f.Surface.IsEnabled,"pending "+kind+" keeps its source alive until acknowledgement");
            check(!Await(f.Output(kind))&&calls==f.Outputs.Calls,"duplicate "+kind+" is ignored while the same output is pending");
            if(kind=="pin")check(f.Outputs.Bounds==new Int32Rect(-570,-80,300,220),"pin handoff retains source-monitor physical origin and crop pixels");
            check(f.Outputs.Image.IsFrozen&&f.Outputs.Image.PixelWidth==300&&f.Outputs.Image.PixelHeight==220,"output "+kind+" uses immutable selected physical pixels");
            pending.SetResult(true);check(Await(output)&&Closed(f.Window),"successful "+kind+" acknowledgement is the only path that completes the screenshot");
        }
        using(var f=new Fixture()){
            f.Surface.Marks.Add(new Annotation{Kind="text",Text="offscreen caption",Start=new Point(320,230),Width=6});
            check(!Await(f.Window.CopyCaptureAsync())&&f.Outputs.Calls==0&&!Closed(f.Window),"text outside the crop prevents silent truncated export while leaving the editor open");
        }
        using(var f=new Fixture()){
            f.Surface.Selection=Rect.Empty;check(!Await(f.Window.CopyCaptureAsync())&&f.Outputs.Calls==0&&!Closed(f.Window),"an empty crop cannot accidentally copy the entire frozen screen");
        }
    }
    static void TextAfterUnsuccessfulSave(Action<bool,string> check){
        var preferences=AppState.Settings;AppState.Settings=new Preferences {TextSize=20,Stroke=4,Color="#EF4444"};
        try{
            foreach(string outcome in new[]{"cancelled","failed","async-failed","cancelled-task"})foreach(bool existing in new[]{false,true})using(var f=new Fixture()){
                var arrow=f.Surface.Marks.Single();Annotation originalText=null;Rect originalBounds=Rect.Empty;
                f.Window.HandleCaptureKey(Key.T,ModifierKeys.None);f.Window.BeginPointer(new Point(60,150));
                var editor=Field<TextBox>(f.Window,"textEditor");
                if(existing){
                    editor.Text="原文 / Original";f.Window.HandleCaptureKey(Key.Enter,ModifierKeys.Control);
                    originalText=f.Surface.Marks.Single(mark=>mark.Kind=="text");originalBounds=originalText.Bounds;
                    f.Window.BeginPointer(TextBody(originalText),2);editor=Field<TextBox>(f.Window,"textEditor");
                }
                string label=outcome+" "+(existing?"existing text":"new text");
                check(editor!=null&&editor==Field<TextBox>(f.Window,"textEditor"),label+" is actively editing before the output action");
                editor.Text="修改后的文字\nEdited caption";f.Window.HandleCaptureWheel(120,ModifierKeys.Control,new Point(90,180));
                double size=editor.FontSize,wrap=(double)editor.Tag;int before=History(f.Window,"annotationUndo");
                var pending=new TaskCompletionSource<bool>();
                f.Outputs.Response=()=>{
                    if(outcome=="failed")throw new IOException("synthetic save unavailable");
                    if(outcome=="cancelled-task")throw new OperationCanceledException("synthetic save cancelled");
                    return outcome=="async-failed"?pending.Task:Task.FromResult(false);
                };
                var save=f.Window.SaveCaptureAsync();
                if(outcome=="async-failed"){
                    check(!save.IsCompleted&&!f.Surface.IsEnabled&&Field<TextBox>(f.Window,"textEditor")==null,label+" commits the live editor before a pending immutable output, without closing the screenshot");
                    pending.SetException(new IOException("synthetic async encoder failure"));
                }
                check(!Await(save)&&f.Outputs.Calls==1&&!Closed(f.Window),label+" reports unsuccessful output without closing or retrying silently");
                var text=f.Surface.Marks.Single(mark=>mark.Kind=="text");var savedBounds=text.Bounds;
                check(f.Surface.Marks.Count==2&&f.Surface.Marks.Contains(arrow)&&(!existing||ReferenceEquals(text,originalText))&&f.Surface.Selection==f.Crop,label+" preserves the same text object, earlier arrow and screenshot selection");
                check(text.Text=="修改后的文字\nEdited caption"&&text.TextFontSize==size&&text.TextWrapWidth==wrap&&Field<TextBox>(f.Window,"textEditor")==null,label+" retains the full committed text, font size and wrapping metadata");
                check(f.Surface.SelectedText==text&&f.Surface.IsEnabled&&Field<StackPanel>(f.Window,"toolbar").IsEnabled&&Field<StackPanel>(f.Window,"properties").IsEnabled&&f.Window.Topmost,label+" returns to an enabled, visibly selected, editable caption");
                check(ButtonNamed(f.Window,"CaptureUndo").IsEnabled&&ButtonNamed(f.Window,"CaptureSizeDecrease").IsEnabled&&ButtonNamed(f.Window,"CaptureSizeIncrease").IsEnabled,label+" restores individual history and font controls after a committed-caption output fails");
                check(History(f.Window,"annotationUndo")==before+1&&History(f.Window,"annotationRedo")==0,label+" creates exactly one text-commit history entry and no output-only undo entry");
                f.Window.HandleCaptureKey(Key.Z,ModifierKeys.Control);
                check((existing?f.Surface.Marks.Contains(text)&&text.Text=="原文 / Original"&&text.TextFontSize==20&&text.Bounds==originalBounds:!f.Surface.Marks.Contains(text))&&f.Surface.Marks.Contains(arrow)&&f.Surface.Selection==f.Crop,label+" Undo restores the earlier caption or removes only the newly committed text");
                f.Window.HandleCaptureKey(Key.Y,ModifierKeys.Control);
                check(f.Surface.Marks.Contains(text)&&text.Text=="修改后的文字\nEdited caption"&&text.TextFontSize==size&&text.TextWrapWidth==wrap&&text.Bounds==savedBounds,label+" Redo restores the same text object and complete geometry after the failed save");
                f.Window.BeginPointer(TextBody(text),2);editor=Field<TextBox>(f.Window,"textEditor");
                check(editor!=null&&editor.Text==text.Text&&f.Surface.EditingText==text,label+" remains double-click editable through the normal pointer route");
                editor.Text="继续编辑 / Continued";f.Window.HandleCaptureKey(Key.Enter,ModifierKeys.Control);
                check(f.Surface.Marks.Count==2&&f.Surface.Marks.Contains(text)&&text.Text=="继续编辑 / Continued",label+" can commit a later edit without a duplicate caption");
                f.Window.HandleCaptureKey(Key.Z,ModifierKeys.Control);
                check(text.Text=="修改后的文字\nEdited caption"&&text.Bounds==savedBounds,label+" later edit Undo still reaches the retained save-attempt caption");
                f.Window.HandleCaptureKey(Key.Y,ModifierKeys.Control);
                check(text.Text=="继续编辑 / Continued"&&f.Surface.Marks.Contains(arrow)&&f.Surface.Selection==f.Crop,label+" later edit Redo preserves unrelated annotations and the crop");
            }
            foreach(bool failure in new[]{false,true})using(var f=new Fixture()){
                var arrow=f.Surface.Marks.Single();f.Window.HandleCaptureKey(Key.T,ModifierKeys.None);f.Window.BeginPointer(new Point(60,160));
                Field<TextBox>(f.Window,"textEditor").Text="保留重做 / Redo";f.Window.HandleCaptureKey(Key.Enter,ModifierKeys.Control);var text=f.Surface.Marks.Single(mark=>mark.Kind=="text");
                f.Window.HandleCaptureKey(Key.Z,ModifierKeys.Control);int undo=History(f.Window,"annotationUndo"),redo=History(f.Window,"annotationRedo");
                f.Outputs.Response=()=>{if(failure)throw new IOException("synthetic save unavailable");return Task.FromResult(false);};
                check(!Await(f.Window.SaveCaptureAsync())&&History(f.Window,"annotationUndo")==undo&&History(f.Window,"annotationRedo")==redo&&redo==1,"unsuccessful save with no live text preserves pre-existing Undo and Redo stacks: failure="+failure);
                f.Window.HandleCaptureKey(Key.Y,ModifierKeys.Control);
                check(f.Surface.Marks.Contains(text)&&f.Surface.Marks.Contains(arrow)&&text.Text=="保留重做 / Redo"&&f.Surface.Selection==f.Crop,"existing text Redo remains available after unsuccessful save: failure="+failure);
            }
        }finally{AppState.Settings=preferences;}
    }
    static void LateResult(Action<bool,string> check){
        using(var old=new Fixture()){
            AppState.Captures.Add(old.Window);var pending=new TaskCompletionSource<bool>();old.Outputs.Response=()=>pending.Task;var task=old.Window.CopyCaptureAsync();
            check(old.Window.HandleCaptureKey(Key.Escape,ModifierKeys.None)&&Closed(old.Window)&&old.Outputs.Token.IsCancellationRequested,"one Escape cancels retryable output lifetime and closes the screenshot");
            using(var current=new Fixture()){
                AppState.Captures.Add(current.Window);pending.SetResult(true);
                check(Await(task)&&!Closed(current.Window)&&current.Surface.Image!=null&&AppState.Captures.Contains(current.Window),"a late successful output cannot close a newly started screenshot");
            }
        }
    }
    static TextWindow OpenReader(Fixture f){
        f.Window.PresentTextViewer=viewer=>{};
        var ocr=(Button)LogicalTreeHelper.FindLogicalNode(f.Window,"CaptureOcr");
        ocr.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return Field<TextWindow>(f.Window,"captureTextViewer");
    }
    static void ReaderReturn(Action<bool,string> check){
        using(var f=new Fixture()){
            var annotation=f.Surface.Marks.Single();var reader=OpenReader(f);
            check(reader!=null&&!Closed(f.Window)&&f.Window.IsCaptureOverlaySuspended&&f.Surface.Marks.Single()==annotation,"actual OCR toolbar action opens an auxiliary view without destroying annotations");
            var back=(Button)LogicalTreeHelper.FindLogicalNode(reader,"ReturnToCapture");
            check(back!=null&&back.Visibility==Visibility.Visible,"OCR exposes a visible return-to-screenshot action");
            back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(!Closed(f.Window)&&!f.Window.IsCaptureOverlaySuspended&&f.Surface.Selection==f.Crop&&f.Surface.Marks.Single()==annotation,"return action resumes the exact old selection and editable object");
            check(f.Window.HandleCaptureKey(Key.Z,ModifierKeys.Control)&&f.Surface.Marks.Count==0,"return from OCR preserves the existing undo history");
        }
        using(var f=new Fixture()){
            var reader=OpenReader(f);reader.ExitTextView();
            check(Closed(f.Window)&&Field<bool>(reader,"closed")&&f.Surface.Image==null,"one reader Escape ends the entire capture rather than revealing another overlay");
        }
        using(var f=new Fixture()){
            var reader=OpenReader(f);f.Window.Close();
            check(Field<bool>(reader,"closed")&&Closed(f.Window)&&Field<TextWindow>(f.Window,"captureTextViewer")==null,"closing a source screenshot also closes its OCR view without resurrecting either window");
        }
        using(var f=new Fixture()){
            f.Window.PresentTextViewer=unused=>{throw new IOException("synthetic reader launch failed");};
            var viewer=new TextWindow(Pixels(),"zh",(image,token)=>Task.FromResult(new OcrPage()));bool failed=false;
            try{f.Window.OpenCaptureTextViewer(viewer);}catch(IOException){failed=true;}
            check(failed&&!Closed(f.Window)&&!f.Window.IsCaptureOverlaySuspended&&f.Surface.Marks.Count==1&&Field<bool>(viewer,"closed"),"failed OCR presentation restores the editable source and disposes only its failed child");
        }
    }
    static void ClipboardRetry(Action<bool,string> check){
        int attempts=0,waits=0;var gate=new TaskCompletionSource<bool>();
        var retry=ImageFiles.RetryClipboardAsync(()=>{if(++attempts<3)throw new ExternalException("busy",unchecked((int)0x800401D0));},(delay,token)=>{waits++;return waits==1?gate.Task:Task.FromResult(true);},CancellationToken.None);
        check(!retry.IsCompleted&&attempts==1&&waits==1,"busy output yields to the dispatcher instead of blocking it during a retry delay");
        gate.SetResult(true);check(Wait(retry)&&attempts==3&&waits==2,"transient clipboard busy failures use bounded retries and acknowledge success once");
        attempts=0;waits=0;bool failed=false;
        try{Wait(ImageFiles.RetryClipboardAsync(()=>{attempts++;throw new ExternalException("busy",unchecked((int)0x800401D0));},(delay,token)=>{waits++;return Task.FromResult(true);},CancellationToken.None));}catch(ExternalException){failed=true;}
        check(failed&&attempts==5&&waits==4,"persistent clipboard contention ends after five attempts rather than looping indefinitely");
        attempts=0;failed=false;
        try{Wait(ImageFiles.RetryClipboardAsync(()=>{attempts++;throw new ExternalException("not busy",unchecked((int)0x80004005));},(delay,token)=>{throw new InvalidOperationException("must not retry");},CancellationToken.None));}catch(ExternalException){failed=true;}
        check(failed&&attempts==1,"unrelated clipboard failures are not blindly retried");
        using(var cancel=new CancellationTokenSource()){
            attempts=0;var waiting=new TaskCompletionSource<bool>();var task=ImageFiles.RetryClipboardAsync(()=>{attempts++;throw new ExternalException("busy",unchecked((int)0x800401D0));},(delay,token)=>waiting.Task,cancel.Token);
            cancel.Cancel();waiting.SetResult(true);bool cancelled=false;try{Wait(task);}catch(OperationCanceledException){cancelled=true;}
            check(cancelled&&attempts==1,"Escape cancellation prevents any later retry from writing to the clipboard");
        }
    }
    static void AtomicSave(string directory,Action<bool,string> check){
        string path=Path.Combine(directory,"existing.png");byte[] original={1,2,3,4};File.WriteAllBytes(path,original);bool failed=false;
        try{ImageFiles.WriteAtomic(path,stream=>{stream.WriteByte(99);throw new IOException("synthetic encoder failure");},CancellationToken.None);}catch(IOException){failed=true;}
        check(failed&&File.ReadAllBytes(path).SequenceEqual(original)&&Directory.GetFiles(directory,"*.tmp").Length==0,"partial encoder failure leaves an existing target untouched and removes only its own temporary file");
        using(var cancel=new CancellationTokenSource()){
            bool cancelled=false;try{ImageFiles.WriteAtomic(path,stream=>{stream.WriteByte(88);cancel.Cancel();},cancel.Token);}catch(OperationCanceledException){cancelled=true;}
            check(cancelled&&File.ReadAllBytes(path).SequenceEqual(original),"cancelling before the atomic commit preserves the previous target");
        }
        using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){
            failed=false;try{ImageFiles.WriteAtomic(path,stream=>stream.WriteByte(55),CancellationToken.None);}catch(IOException){failed=true;}
            check(failed&&File.ReadAllBytes(path).SequenceEqual(original),"destination sharing failure cannot truncate the existing file");
        }
        ImageFiles.Write(Pixels(),path);var decoded=Snapshot.Load(path);
        check(decoded.PixelWidth==400&&decoded.PixelHeight==300&&Directory.GetFiles(directory,"*.tmp").Length==0,"successful atomic save replaces the target with a decodable complete PNG");
    }
    static void Pins(string directory,Action<bool,string> check){
        var store=new PinStore(Path.Combine(directory,"pins"));var prior=store.Add(Pixels());byte[] previous=File.ReadAllBytes(store.ImagePath(prior));bool failed=false;
        try{Wait(store.CreatePinAsync(Pixels(),new Int32Rect(-500,20,400,300),CancellationToken.None,(record,bounds)=>{throw new IOException("synthetic native presenter unavailable");}));}catch(IOException){failed=true;}
        check(failed&&store.Records.Count==1&&store.Records.Single()==prior&&File.ReadAllBytes(store.ImagePath(prior)).SequenceEqual(previous),"failed new pin presentation rolls back only the new record and keeps previous pin bytes");
        check(Directory.GetFiles(store.Folder,"*.png").Length==1&&new PinStore(store.Folder).Records.Count==1,"failed pin creation leaves no duplicate or active phantom in its persisted index");
        int presented=0;Wait(store.CreatePinAsync(Pixels(),new Int32Rect(-500,20,400,300),CancellationToken.None,(record,bounds)=>{presented++;check(File.Exists(store.ImagePath(record))&&store.Records.Contains(record),"pin presentation runs only after complete bytes and metadata have committed");}));
        check(presented==1&&store.Records.Count==2&&new PinStore(store.Folder).Records.Count==2,"successful asynchronous pin creation commits exactly one recoverable pin");
        using(var cancel=new CancellationTokenSource()){
            cancel.Cancel();bool cancelled=false;try{Wait(store.CreatePinAsync(Pixels(),new Int32Rect(0,0,20,20),cancel.Token,(record,bounds)=>presented++));}catch(OperationCanceledException){cancelled=true;}
            check(cancelled&&store.Records.Count==2&&presented==1,"cancelled pin encoding does not create an image, index entry or window");
        }
    }
    static void Placement(Action<bool,string> check){
        foreach(var work in new[]{new System.Drawing.Rectangle(-1920,-120,1920,1080),new System.Drawing.Rectangle(0,0,800,600),new System.Drawing.Rectangle(2560,0,3840,2160)})foreach(double dpi in new[]{1.0,1.5,2.0}){
            var bounds=CaptureWindow.TextViewerBounds(work,dpi,dpi);
            check(work.Contains(new System.Drawing.Rectangle(bounds.X,bounds.Y,bounds.Width,bounds.Height))&&bounds.Width<work.Width&&bounds.Height<work.Height,"reader placement stays non-fullscreen inside source monitor at "+work.X+" scale "+dpi);
        }
    }
}
