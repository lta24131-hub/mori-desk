using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;

// Only synthetic pixels and unshown WPF trees. No desktop, clipboard, hotkey,
// settings save, or export action is used by these interaction checks.
internal static class CaptureToolbarUsabilityTests {
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static T Field<T>(CaptureWindow window,string name){return (T)typeof(CaptureWindow).GetField(name,Hidden).GetValue(window);}
    static object Call(CaptureWindow window,string name,params object[] args){return typeof(CaptureWindow).GetMethod(name,Hidden).Invoke(window,args);}
    static IEnumerable<DependencyObject> Tree(DependencyObject node){
        yield return node;
        foreach(object item in LogicalTreeHelper.GetChildren(node)){
            var child=item as DependencyObject;if(child==null)continue;
            foreach(var descendant in Tree(child))yield return descendant;
        }
    }
    static Button Action(CaptureWindow window,string name){return Tree(Field<StackPanel>(window,"toolbar")).OfType<Button>().Single(button=>button.Name=="Capture"+name);}
    static double Gap(Button first,Button second,Visual host){
        var a=first.TransformToAncestor(host).TransformBounds(new Rect(first.RenderSize));
        var b=second.TransformToAncestor(host).TransformBounds(new Rect(second.RenderSize));
        return b.Left-a.Right;
    }
    static void Layout(CaptureWindow window){Layout(window,new Size(1200,800));}
    static void Layout(CaptureWindow window,Size size){
        window.Width=size.Width;window.Height=size.Height;
        window.Measure(size);window.Arrange(new Rect(new Point(),size));
        var root=(Grid)window.Content;root.Measure(size);root.Arrange(new Rect(new Point(),size));
        Call(window,"PositionTools");root.UpdateLayout();
    }
    static void Click(Button button){button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
    internal static void Run(Action<bool,string> check){
        var preferences=AppState.Settings;AppState.Settings=new Preferences {Stroke=4,Color="#EF4444"};
        var pixels=new byte[16*16*4];for(int i=3;i<pixels.Length;i+=4)pixels[i]=255;
        var image=BitmapSource.Create(16,16,96,96,PixelFormats.Bgra32,null,pixels,64);image.Freeze();
        var window=new CaptureWindow(new Snapshot {Image=image,Bounds=new System.Drawing.Rectangle(0,0,16,16)});
        try{
            check(Field<object>(window,"toolbarFlow")==null,"capture does not measure or regroup toolbar before a crop exists");
            var surface=Field<ScreenshotSurface>(window,"surface");surface.Selection=new Rect(20,20,1160,760);Layout(window);
            var toolbar=Field<StackPanel>(window,"toolbar");var card=Field<Border>(window,"propertiesCard");
            var undo=Action(window,"Undo");var redo=Action(window,"Redo");
            var buttons=Tree(toolbar).OfType<Button>().ToArray();
            check(buttons.Select(button=>button.Name).Where(name=>name.Length>0).Distinct().Count()==buttons.Count(button=>button.Name.Length>0),"toolbar action identities remain unique after adaptive grouping");
            check(!undo.IsEnabled&&!redo.IsEnabled&&undo.Opacity<.5&&redo.Opacity<.5,"empty capture history visibly disables both undo and redo");
            check(ToolTipService.GetShowOnDisabled(undo)&&ToolTipService.GetShowOnDisabled(redo)&&AutomationProperties.GetName(redo).Contains("Ctrl+Y"),"disabled history still explains each action and its keyboard shortcut");
            check(!buttons.Any(button=>button.Name=="CaptureTool_select")&&Gap(Action(window,"Tool_rect"),Action(window,"Tool_ellipse"),toolbar)<5,"crop adjustment is direct on the border; drawing tools stay consecutive without a redundant selection button");
            check(Gap(undo,redo,toolbar)<5&&Gap(redo,Action(window,"Ocr"),toolbar)>=10,"undo and redo stay together before a distinct recognition group");
            var pin=Action(window,"Pin");var previousOutput=buttons.SingleOrDefault(button=>button.Name=="CaptureCollect")??Action(window,"Save");
            check(Gap(previousOutput,pin,toolbar)>=10&&Gap(pin,Action(window,"Cancel"),toolbar)<5&&Gap(Action(window,"Cancel"),Action(window,"Copy"),toolbar)<5,"frequent pin action joins the right finish group while Cancel and Copy keep their familiar order");
            var pinIcon=pin.Content as System.Windows.Shapes.Path;
            check(pinIcon!=null&&pinIcon.Stroke==Ui.Tone("Accent")&&pin.Background==Ui.Tone("AccentSoft")&&!Tree(pin).OfType<TextBlock>().Any()&&AutomationProperties.GetName(pin).StartsWith("钉到屏幕"),"pin is an accessible icon-only accent tile rather than another text command");
            pin.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,0){RoutedEvent=Mouse.MouseEnterEvent});pin.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,0){RoutedEvent=Mouse.MouseLeaveEvent});
            check(pin.Background==Ui.Tone("AccentSoft"),"leaving the pin action does not erase its persistent visual emphasis");
            window.SetScrollingPreparationUi(true);
            check(!pin.IsEnabled&&pin.Opacity<.5&&Action(window,"Cancel").IsEnabled,"scroll preparation still disables and dims the emphasized pin while retaining cancellation");
            window.SetScrollingPreparationUi(false);
            check(pin.IsEnabled&&pin.Opacity==1&&pin.Background==Ui.Tone("AccentSoft"),"returning from scroll preparation restores the pin action and its original emphasis");
            check(card.Visibility==Visibility.Collapsed,"crop adjustment has no irrelevant drawing properties");

            Call(window,"SetTool","rect");
            check(Field<TextBlock>(window,"widthLabel").Text=="粗细 4"&&card.Visibility==Visibility.Visible,"shape properties name the logical stroke width without mislabeling it as physical pixels");
            window.BeginPointer(new Point(120,120));window.EndPointer(new Point(260,220));
            var mark=surface.Marks.Single();
            check(undo.IsEnabled&&undo.Opacity==1&&!redo.IsEnabled,"drawing immediately enables undo while redo stays unavailable");
            Click(undo);
            check(surface.Marks.Count==0&&!undo.IsEnabled&&redo.IsEnabled&&redo.Opacity==1,"visible undo removes the drawing and makes redo available");
            Click(redo);
            check(surface.Marks.Single()==mark&&undo.IsEnabled&&!redo.IsEnabled,"visible redo restores the same annotation and refreshes history availability");
            Click(undo);window.BeginPointer(new Point(300,240));window.EndPointer(new Point(380,300));
            check(surface.Marks.Count==1&&surface.Marks[0]!=mark&&undo.IsEnabled&&!redo.IsEnabled,"drawing after undo clears the abandoned redo branch in the visible toolbar");

            Call(window,"SetTool","text");
            var label=Field<TextBlock>(window,"widthLabel");
            var hint=Tree(Field<StackPanel>(window,"properties")).OfType<TextBlock>().Single(block=>block.Name=="CapturePropertyHint");
            check(label.Text=="字号 20"&&hint.Text=="Ctrl+滚轮调字号","new text shows its independently remembered size and an explicit modifier hint");
            var palette=Tree(Field<StackPanel>(window,"properties")).OfType<Button>().Where(button=>button.Width==18&&button.Height==18).ToArray();
            check(palette.Length==6&&palette.Select(AutomationProperties.GetName).Distinct().Count()==6&&palette.All(button=>AutomationProperties.GetName(button).StartsWith("标注颜色：")),"every color swatch has a distinct accessible color name");
            window.BeginPointer(new Point(420,300));var editor=Field<TextBox>(window,"textEditor");editor.Text="Synthetic label";
            window.HandleCaptureWheel(120,ModifierKeys.Control,new Point(500,340));
            check(label.Text=="字号 22"&&editor.FontSize==22,"live text wheel updates the actual font-size property immediately");
            Call(window,"CommitText");var text=surface.Marks.Single(annotation=>annotation.Kind=="text");
            Call(window,"SetTool","select");
            check(surface.SelectedText==text&&label.Text=="字号 22"&&card.Visibility==Visibility.Visible,"selected text keeps text-specific properties while using crop adjustment");
            Call(window,"SelectShape",surface.Marks.First(annotation=>annotation.Kind=="rect"));
            check(label.Text=="粗细 4"&&hint.Text=="Ctrl+滚轮调粗细"&&card.Visibility==Visibility.Visible,"selecting a shape restores stroke-specific properties without a stale font-size hint");

            Call(window,"SetTool","mosaic");
            check(card.Visibility==Visibility.Collapsed,"mosaic hides color and width controls that do not affect mosaic pixels");
            var mosaic=new Annotation {Kind="mosaic",Start=new Point(600,400),End=new Point(650,450)};surface.Marks.Add(mosaic);
            Call(window,"SetTool","select");Call(window,"SelectShape",mosaic);
            check(card.Visibility==Visibility.Collapsed,"selected mosaic does not expose misleading drawing properties");
            Call(window,"SelectShape",new object[]{null});
            check(card.Visibility==Visibility.Collapsed,"clearing an annotation selection collapses its properties again");
            check(!window.IsVisible&&new WindowInteropHelper(window).Handle==IntPtr.Zero,"toolbar usability checks never create a native or visible capture window");
            PinPriority(image,check);
        }finally{window.Close();AppState.Settings=preferences;}
    }
    static void PinPriority(BitmapSource image,Action<bool,string> check){
        var collector=MoriDesk.DeskRuntime.CollectImage;
        try{
            foreach(bool collect in new[]{false,true})foreach(bool allowPin in new[]{false,true})foreach(double width in new[]{1200.0,320.0}){
                MoriDesk.DeskRuntime.CollectImage=collect?(Func<BitmapSource,System.Threading.Tasks.Task<bool>>)(pixels=>System.Threading.Tasks.Task.FromResult(false)):null;
                var window=new CaptureWindow(new Snapshot {Image=image,Bounds=new System.Drawing.Rectangle(0,0,16,16)},null,new CaptureFeatures {Pin=allowPin});
                try{
                    Field<ScreenshotSurface>(window,"surface").Selection=new Rect(10,10,width-20,220);Layout(window,new Size(width,400));
                    var toolbar=Field<StackPanel>(window,"toolbar");var buttons=Tree(toolbar).OfType<Button>().ToArray();
                    var expected=allowPin?new[]{"CapturePin","CaptureCancel","CaptureCopy"}:new[]{"CaptureCancel","CaptureCopy"};
                    check(buttons.Skip(buttons.Length-expected.Length).Select(button=>button.Name).SequenceEqual(expected)&&buttons.Count(button=>button.Name=="CapturePin")== (allowPin?1:0),"pin priority keeps the same terminal order with optional collection and pin: "+collect+"/"+allowPin+" at "+width);
                    check(buttons.Any(button=>button.Name=="CaptureCollect")==collect,"moving pin does not add a collection action without a connected collector");
                    if(allowPin){
                        var pin=Action(window,"Pin");var cancel=Action(window,"Cancel");var done=Action(window,"Copy");
                        double pinY=pin.TransformToAncestor(toolbar).Transform(new Point()).Y,cancelY=cancel.TransformToAncestor(toolbar).Transform(new Point()).Y,doneY=done.TransformToAncestor(toolbar).Transform(new Point()).Y;
                        check(Math.Abs(pinY-cancelY)<.1&&Math.Abs(pinY-doneY)<.1&&Gap(pin,cancel,toolbar)<5&&Gap(cancel,done,toolbar)<5,"narrow layouts keep pin, cancel and completion together instead of stranding pin among other exports at "+width);
                    }
                    check(!window.IsVisible&&new WindowInteropHelper(window).Handle==IntPtr.Zero,"priority variants remain synthetic and never show a native window");
                }finally{window.Close();}
            }
        }finally{MoriDesk.DeskRuntime.CollectImage=collector;}
    }
}
