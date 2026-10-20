using System;
using System.Collections.Generic;
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
using QingJie;

// Synthetic pixels and unshown windows only. Output destinations are fakes;
// recognition, native screenshots, clipboard and external helpers are not used.
internal static class CaptureFeatureRuntimeTests {
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static T Field<T>(object instance,string name){return (T)instance.GetType().GetField(name,Hidden).GetValue(instance);}
    static object Call(CaptureWindow window,string name,params object[] values){return typeof(CaptureWindow).GetMethod(name,Hidden).Invoke(window,values);}
    static IEnumerable<DependencyObject> Tree(DependencyObject root){yield return root;foreach(object child in LogicalTreeHelper.GetChildren(root)){var node=child as DependencyObject;if(node!=null)foreach(var nested in Tree(node))yield return nested;}}
    static HashSet<string> Buttons(CaptureWindow window){return new HashSet<string>(Tree((DependencyObject)window.Content).OfType<Button>().Select(button=>button.Name));}
    static CaptureFeatures None(){return new CaptureFeatures {SmartSelection=false,Rectangle=false,Ellipse=false,Lines=false,Pen=false,Text=false,Mosaic=false,Ocr=false,Translation=false,Scrolling=false,Pin=false,Collect=false};}
    static BitmapSource Pixels(){var data=new byte[400*300*4];for(int i=0;i<data.Length;i+=4){data[i]=41;data[i+1]=92;data[i+2]=170;data[i+3]=255;}var image=BitmapSource.Create(400,300,96,96,PixelFormats.Bgra32,null,data,1600);image.Freeze();return image;}
    sealed class FakeOutputs : ICaptureOutputActions {
        internal int Copies,Saves,Pins;
        public Task<bool> Copy(BitmapSource image,CancellationToken cancel){Copies++;return Task.FromResult(false);}
        public Task<bool> Save(BitmapSource image,Window owner,CancellationToken cancel){Saves++;return Task.FromResult(false);}
        public Task<bool> Pin(BitmapSource image,Int32Rect bounds,CancellationToken cancel){Pins++;return Task.FromResult(false);}
    }
    sealed class Fixture : IDisposable {
        internal readonly CaptureWindow Window;
        internal readonly ScreenshotSurface Surface;
        internal readonly FakeOutputs Outputs=new FakeOutputs();
        internal int OptionalStarts;
        internal Fixture(CaptureFeatures features=null,CaptureTargets targets=null){
            Window=new CaptureWindow(new Snapshot {Image=Pixels(),Bounds=new System.Drawing.Rectangle(0,0,400,300)},targets,features);
            Window.OutputActions=Outputs;Surface=Field<ScreenshotSurface>(Window,"surface");
            // A regression must fail without opening real UI, running models,
            // touching the network, or launching an external capture process.
            Window.PresentTextViewer=view=>{OptionalStarts++;view.Close();};
            Window.ReadCaptureOcr=(image,cancel)=>{OptionalStarts++;return Task.FromResult(new OcrPage());};
            Window.RunScrollSession=(session,output,handoff)=>{OptionalStarts++;return Task.FromResult(new ExternalScrollResult {status="error",message="Synthetic helper boundary"});};
            Surface.Measure(new Size(400,300));Surface.Arrange(new Rect(0,0,400,300));
        }
        internal void Crop(){Surface.Selection=new Rect(20,20,350,240);Layout();}
        internal void Layout(){var root=(Grid)Window.Content;root.Measure(new Size(400,300));root.Arrange(new Rect(0,0,400,300));Call(Window,"PositionTools");root.UpdateLayout();}
        public void Dispose(){if(!Field<bool>(Window,"closed"))Window.Close();}
    }
    internal static void Run(Action<bool,string> check){
        var previous=AppState.Settings;var collector=MoriDesk.DeskRuntime.CollectImage;
        AppState.Settings=new Preferences();
        int collections=0;MoriDesk.DeskRuntime.CollectImage=image=>{collections++;return Task.FromResult(false);};
        try{Disabled(check,()=>collections);IndividualTools(check);RecognitionOptions(check);Snapshots(check);Defaults(check);}
        finally{AppState.Settings=previous;MoriDesk.DeskRuntime.CollectImage=collector;}
    }
    static void Disabled(Action<bool,string> check,Func<int> collections){
        var target=new CaptureTarget(new Rect(40,40,200,160));
        var targets=new CaptureTargets(new List<CaptureTarget>{target});
        using(var f=new Fixture(None(),targets)){
            Call(f.Window,"PrepareVisualTargets");Call(f.Window,"UpdateSnap",new Point(80,80));f.Window.CycleSnap(new Point(80,80),1);
            check(Field<CaptureTargets>(f.Window,"snapTargets")==null&&Field<CaptureVisualRegions>(f.Window,"visualTargets")==null&&Field<CancellationTokenSource>(f.Window,"visualTargetCancel")==null&&f.Surface.HoverSelection.IsEmpty,"disabled smart selection never retains native targets or starts visual target analysis");
            check(!f.Window.HandleCaptureKey(Key.Tab,ModifierKeys.None)&&!f.Window.HandleCaptureWheel(120,ModifierKeys.None,new Point(80,80)),"disabled smart selection does not intercept cycling keys or plain wheel");
            f.Window.BeginPointer(new Point(30,30));f.Window.EndPointer(new Point(180,140));
            check(f.Surface.Selection==new Rect(30,30,150,110),"manual crop remains functional when all optional features are disabled");
            f.Crop();var buttons=Buttons(f.Window);
            foreach(string name in new[]{"CaptureTool_rect","CaptureTool_ellipse","CaptureTool_arrow","CaptureTool_pen","CaptureTool_text","CaptureTool_mosaic","LineMode_arrow","LineMode_line","CaptureOcr","CaptureTranslate","CaptureLanguage","CaptureScroll","CapturePin","CaptureCollect"})
                check(!buttons.Contains(name),"disabled feature does not create an empty toolbar slot: "+name);
            check(new[]{"CaptureSave","CaptureCopy","CaptureCancel","CaptureUndo","CaptureRedo"}.All(buttons.Contains),"basic output, cancellation and crop history remain available with every optional feature off");
            check(Field<Border>(f.Window,"propertiesCard").Visibility==Visibility.Collapsed&&Tree(Field<StackPanel>(f.Window,"properties")).OfType<Button>().Count()==0,"basic screenshot allocates no annotation property buttons");
            foreach(var pair in new[]{Tuple.Create(Key.R,"rect"),Tuple.Create(Key.O,"ellipse"),Tuple.Create(Key.A,"arrow"),Tuple.Create(Key.L,"line"),Tuple.Create(Key.P,"pen"),Tuple.Create(Key.T,"text"),Tuple.Create(Key.M,"mosaic")}){
                check(!f.Window.HandleCaptureKey(pair.Item1,ModifierKeys.None),"disabled tool shortcut is not active: "+pair.Item2);
                Call(f.Window,"SetTool",pair.Item2);check(f.Window.SelectedTool=="select","direct tool command cannot bypass feature gate: "+pair.Item2);
            }
            var line=new Annotation {Kind="line",Start=new Point(50,60),End=new Point(120,80)};f.Surface.SelectedAnnotation=line;
            Call(f.Window,"SetCaptureLineMode","arrow");check(line.Kind=="line"&&f.Window.SelectedTool=="select","line subtype command cannot bypass disabled line feature");f.Surface.SelectedAnnotation=null;
            int before=collections();
            check(!f.Window.PinCaptureAsync().GetAwaiter().GetResult()&&!f.Window.CollectToLibraryAsync().GetAwaiter().GetResult()&&!f.Window.HandleCaptureKey(Key.D,ModifierKeys.Control),"disabled pin and library collection reject both direct output and shortcut entry points");
            check(f.Outputs.Pins==0&&collections()==before,"disabled output features never call a destination");
            Call(f.Window,"ReadText",false);Call(f.Window,"ReadText",true);Call(f.Window,"TranslateInPlace");Call(f.Window,"BeginScrollingCapture");
            check(f.OptionalStarts==0,"disabled features never reach injected reader, OCR, or external-session boundaries");
            check(Field<TextWindow>(f.Window,"captureTextViewer")==null&&Field<CancellationTokenSource>(f.Window,"translationCancel")==null&&!f.Window.IsScrollingCapture&&Field<TextBlock>(f.Window,"translationStatus").Visibility==Visibility.Collapsed,"disabled recognition, translation and scrolling stop before creating an auxiliary session or pending state");
            check(!f.Window.CopyCaptureAsync().GetAwaiter().GetResult()&&!f.Window.SaveCaptureAsync().GetAwaiter().GetResult()&&f.Outputs.Copies==1&&f.Outputs.Saves==1,"required Copy and Save still reach their fake destinations in basic mode");
            check(!f.Window.IsVisible&&new WindowInteropHelper(f.Window).Handle==IntPtr.Zero,"feature runtime checks never create a native screenshot window");
            check(f.Window.HandleCaptureKey(Key.Escape,ModifierKeys.None)&&Field<bool>(f.Window,"closed"),"one Escape remains available in basic mode");
        }
    }
    static void IndividualTools(Action<bool,string> check){
        var choices=new[]{Tuple.Create("Rectangle","rect",Key.R),Tuple.Create("Ellipse","ellipse",Key.O),Tuple.Create("Lines","arrow",Key.A),Tuple.Create("Pen","pen",Key.P),Tuple.Create("Text","text",Key.T),Tuple.Create("Mosaic","mosaic",Key.M)};
        foreach(var choice in choices){
            var features=None();typeof(CaptureFeatures).GetField(choice.Item1).SetValue(features,true);
            using(var f=new Fixture(features)){
                f.Crop();var buttons=Buttons(f.Window);
                check(buttons.Contains("CaptureTool_"+choice.Item2)&&choices.Where(other=>other!=choice).All(other=>!buttons.Contains("CaptureTool_"+other.Item2)),"only the opted-in annotation tool is built: "+choice.Item1);
                check(f.Window.HandleCaptureKey(choice.Item3,ModifierKeys.None)&&f.Window.SelectedTool==choice.Item2,"opted-in annotation shortcut selects its tool: "+choice.Item1);
                if(choice.Item1=="Lines"){
                    check(f.Window.HandleCaptureKey(Key.L,ModifierKeys.None)&&f.Window.SelectedTool=="line","line and arrow share one feature and retain both keyboard choices");
                    Call(f.Window,"SetCaptureLineMode","arrow");check(f.Window.SelectedTool=="arrow"&&Buttons(f.Window).Contains("LineMode_line"),"enabled line family retains subtype adjustment controls");
                }else check(!buttons.Contains("LineMode_line")&&!buttons.Contains("LineMode_arrow"),"line adjustment controls are not built for an unrelated annotation feature: "+choice.Item1);
                Call(f.Window,"SetTool","select");check(f.Window.SelectedTool=="select","selection remains available independently of opted-in tools: "+choice.Item1);
            }
        }
    }
    static void RecognitionOptions(Action<bool,string> check){
        foreach(bool ocr in new[]{false,true})foreach(bool translate in new[]{false,true}){
            var features=None();features.Ocr=ocr;features.Translation=translate;
            using(var f=new Fixture(features)){
                f.Crop();var buttons=Buttons(f.Window);
                check(buttons.Contains("CaptureOcr")==ocr&&buttons.Contains("CaptureTranslate")==translate&&buttons.Contains("CaptureLanguage")==translate,"OCR and translation toggle independently; translation target appears only with translation enabled: "+ocr+"/"+translate);
                if(!translate){Call(f.Window,"ReadText",true);Call(f.Window,"TranslateInPlace");check(Field<CancellationTokenSource>(f.Window,"translationCancel")==null,"OCR-only mode does not start in-place translation");}
                if(!ocr){Call(f.Window,"ReadText",false);check(Field<TextWindow>(f.Window,"captureTextViewer")==null,"translation-only mode does not expose the OCR reader command");}
            }
        }
    }
    static void Snapshots(Action<bool,string> check){
        var original=new CaptureFeatures();AppState.Settings.CaptureFeatures=original;
        using(var active=new Fixture()){
            active.Crop();original.Pen=false;original.Scrolling=false;AppState.Settings.CaptureFeatures=None();
            check(active.Window.captureFeatures.Pen&&active.Window.captureFeatures.Scrolling&&!object.ReferenceEquals(active.Window.captureFeatures,original),"active capture holds a detached snapshot rather than mutable preferences");
            check(active.Window.HandleCaptureKey(Key.P,ModifierKeys.None)&&active.Window.SelectedTool=="pen"&&Buttons(active.Window).Contains("CaptureScroll"),"editing preferences does not remove an active capture's chosen tools");
            using(var next=new Fixture()){next.Crop();check(!Buttons(next.Window).Contains("CaptureTool_pen")&&!Buttons(next.Window).Contains("CaptureScroll"),"next capture uses the newly saved feature choices");}
        }
        var shared=None();shared.Rectangle=true;
        using(var monitor=new Fixture(shared)){
            shared.Rectangle=false;shared.Translation=true;monitor.Crop();
            check(monitor.Window.captureFeatures.Rectangle&&!monitor.Window.captureFeatures.Translation&&Buttons(monitor.Window).Contains("CaptureTool_rect")&&!Buttons(monitor.Window).Contains("CaptureTranslate"),"explicit multi-monitor feature snapshot is also copied and cannot be mutated by its caller");
        }
    }
    static void Defaults(Action<bool,string> check){
        AppState.Settings.CaptureFeatures=null;
        using(var f=new Fixture()){
            f.Crop();var buttons=Buttons(f.Window);
            check(new[]{"CaptureTool_rect","CaptureTool_ellipse","CaptureTool_arrow","CaptureTool_pen","CaptureTool_text","CaptureTool_mosaic","CaptureOcr","CaptureTranslate","CaptureLanguage","CaptureScroll","CapturePin","CaptureCollect"}.All(buttons.Contains),"missing old preference field defaults to the original full screenshot tool set");
        }
    }
}
