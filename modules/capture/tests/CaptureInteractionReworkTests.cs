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

// The user-facing key/pointer dispatchers and actual buttons, synthetic pixels
// and unshown WPF trees only. Never run the application entry point or export.
internal static class CaptureInteractionReworkTests {
    const BindingFlags Hidden=BindingFlags.NonPublic|BindingFlags.Instance;
    static T Field<T>(CaptureWindow window,string name){return (T)typeof(CaptureWindow).GetField(name,Hidden).GetValue(window);}
    static void Position(CaptureWindow window){typeof(CaptureWindow).GetMethod("PositionTools",Hidden).Invoke(window,null);}
    static IEnumerable<DependencyObject> Tree(DependencyObject node){yield return node;foreach(object value in LogicalTreeHelper.GetChildren(node)){var child=value as DependencyObject;if(child!=null)foreach(var item in Tree(child))yield return item;}}
    static Button FindButton(CaptureWindow window,string name){return Tree((Grid)window.Content).OfType<Button>().Single(button=>button.Name==name);}
    static void Click(Button button){button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
    sealed class Fixture:IDisposable {
        internal CaptureWindow Window;
        internal ScreenshotSurface Surface;
        internal Rect Crop=new Rect(20,20,560,350);
        internal Fixture(){
            var pixels=new byte[600*400*4];for(int i=0;i<pixels.Length;i++)pixels[i]=255;
            var image=BitmapSource.Create(600,400,96,96,PixelFormats.Bgra32,null,pixels,2400);image.Freeze();
            Window=new CaptureWindow(new Snapshot {Image=image,Bounds=new System.Drawing.Rectangle(0,0,600,400)});
            Surface=Field<ScreenshotSurface>(Window,"surface");Surface.Selection=Crop;Layout(new Size(600,400));
        }
        internal void Layout(Size size){Window.Width=size.Width;Window.Height=size.Height;Window.Measure(size);Window.Arrange(new Rect(new Point(),size));var root=(Grid)Window.Content;root.Measure(size);root.Arrange(new Rect(new Point(),size));Position(Window);root.UpdateLayout();}
        internal void Tool(Key key){if(!Window.HandleCaptureKey(key,ModifierKeys.None))throw new InvalidOperationException("Tool key was not accepted: "+key);}
        internal Annotation Draw(Key key,Point start,Point end,ModifierKeys modifiers=ModifierKeys.None){Tool(key);Window.BeginPointer(start,1,modifiers);Window.EndPointer(end);return Surface.Marks.Last();}
        internal TextBox Editor {get{return Field<TextBox>(Window,"textEditor");}}
        internal Annotation Text(Point point,string value){Tool(Key.T);Window.BeginPointer(point);Editor.Text=value;Window.HandleCaptureKey(Key.Enter,ModifierKeys.Control);return Surface.Marks.Last();}
        public void Dispose(){Window.Close();AppState.Captures.Remove(Window);}
    }
    internal static void Run(Action<bool,string> check){
        var preferences=AppState.Settings;AppState.Settings=new Preferences {Stroke=4,TextSize=20,Color="#EF4444"};
        try{DirectEditing(check);ExplicitProperties(check);WrappedText(check);StableActions(check);}finally{AppState.Settings=preferences;}
    }
    static void DirectEditing(Action<bool,string> check){
        foreach(var tool in new[]{Key.R,Key.O,Key.A,Key.L,Key.P,Key.T,Key.M})using(var f=new Fixture()){
            var arrow=f.Draw(Key.A,new Point(120,100),new Point(240,180));var text=f.Text(new Point(120,240),"原来的文字");
            f.Tool(tool);var before=arrow.Start;var body=new Point(180,140);
            f.Window.BeginPointer(body);f.Window.EndPointer(body+new Vector(16,12));
            check(arrow.Start==before+new Vector(16,12)&&f.Surface.SelectedAnnotation==arrow&&f.Surface.Marks.Count==2&&f.Surface.Selection==f.Crop,"old arrow is directly movable from active tool without a hidden selection switch: "+tool);
            var endpoint=arrow.End;f.Window.BeginPointer(endpoint);f.Window.EndPointer(endpoint+new Vector(15,-5));
            check(arrow.End==endpoint+new Vector(15,-5)&&f.Surface.Marks.Count==2,"visible arrow endpoint remains editable in the same active tool: "+tool);
            var textBody=new Point(text.Bounds.Left+text.Bounds.Width/2,text.Bounds.Top+text.Bounds.Height/2);before=text.Start;
            f.Window.BeginPointer(textBody);f.Window.EndPointer(textBody+new Vector(12,6));
            check(text.Start==before+new Vector(12,6)&&f.Surface.SelectedText==text&&f.Surface.Marks.Count==2,"old text is directly movable after another drawing tool: "+tool);
            textBody=new Point(text.Bounds.Left+text.Bounds.Width/2,text.Bounds.Top+text.Bounds.Height/2);f.Window.BeginPointer(textBody,2);
            check(f.Editor!=null&&f.Editor.Text=="原来的文字","double click edits old text in every drawing tool: "+tool);
            f.Editor.Text="修改后的文字";f.Window.HandleCaptureKey(Key.Enter,ModifierKeys.Control);
            check(text.Text=="修改后的文字"&&f.Surface.Marks.Count==2,"text editing commits to the same object without a duplicate: "+tool);
        }
        using(var f=new Fixture()){
            var first=f.Draw(Key.A,new Point(120,100),new Point(240,180));var before=first.Start;f.Tool(Key.R);
            f.Window.BeginPointer(new Point(180,140),1,ModifierKeys.Alt);f.Window.EndPointer(new Point(300,220));
            check(f.Surface.Marks.Count==2&&f.Surface.Marks.Last().Kind=="rect"&&first.Start==before&&f.Surface.SelectedAnnotation==f.Surface.Marks.Last(),"Alt over an old mark explicitly creates a new selected object");
            var crop=f.Crop;f.Window.BeginPointer(crop.TopLeft,1,ModifierKeys.Alt);f.Window.EndPointer(crop.TopLeft-new Vector(5,5));
            check(f.Surface.Selection.TopLeft==crop.TopLeft-new Vector(5,5)&&f.Surface.Marks.Count==2,"crop border still wins over Alt drawing and object editing");
        }
        using(var f=new Fixture()){
            var line=f.Draw(Key.A,new Point(120,100),new Point(240,180));f.Tool(Key.R);f.Window.BeginPointer(new Point(180,140));f.Window.EndPointer(new Point(180,140));
            check(Field<StackPanel>(f.Window,"lineModes").Visibility==Visibility.Visible,"selected line exposes its own properties even while another drawing tool remains active");
            Click(FindButton(f.Window,"LineMode_line"));
            check(line.Kind=="line"&&f.Surface.SelectedAnnotation==line&&f.Window.SelectedTool=="rect","line/arrow property changes the visible selected object without switching the next drawing tool");
            f.Window.HandleCaptureKey(Key.Z,ModifierKeys.Control);check(line.Kind=="arrow","line-kind changes remain undoable");
            f.Tool(Key.A);var rectangle=new Annotation {Kind="rect",Start=new Point(300,200),End=new Point(400,280)};f.Surface.Marks.Add(rectangle);f.Window.BeginPointer(new Point(340,200));f.Window.EndPointer(new Point(340,200));
            check(Field<StackPanel>(f.Window,"lineModes").Visibility==Visibility.Collapsed,"rectangle selection never advertises misleading arrow properties from the active tool");
        }
    }
    static void ExplicitProperties(Action<bool,string> check){
        using(var f=new Fixture()){
            var first=f.Draw(Key.R,new Point(100,100),new Point(220,180));double old=first.Width,defaultWidth=AppState.Settings.Stroke;
            check(!f.Window.HandleCaptureWheel(120,ModifierKeys.None,new Point(300,200))&&first.Width==old&&AppState.Settings.Stroke==defaultWidth,"plain wheel never changes a selected drawing or next-tool defaults");
            Click(FindButton(f.Window,"CaptureSizeIncrease"));
            check(first.Width==old+1&&AppState.Settings.Stroke==defaultWidth,"explicit size control edits the visibly selected shape only");
            f.Tool(Key.P);f.Window.BeginPointer(new Point(400,280));f.Window.EndPointer(new Point(400,280));
            var color=Tree(Field<StackPanel>(f.Window,"properties")).OfType<Button>().First(button=>button.Width==18&&button.Background is SolidColorBrush&&((SolidColorBrush)button.Background).Color==Colors.White);
            string prior=first.Color;Click(color);Click(FindButton(f.Window,"CaptureSizeIncrease"));
            check(first.Color==prior&&first.Width==old+1&&AppState.Settings.Color=="#FFFFFF"&&AppState.Settings.Stroke==defaultWidth+1,"with no visible selection color and width change only next-annotation defaults, never last");
            double stroke=AppState.Settings.Stroke;f.Tool(Key.T);double size=AppState.Settings.TextSize;Click(FindButton(f.Window,"CaptureSizeIncrease"));
            check(AppState.Settings.TextSize==size+5&&AppState.Settings.Stroke==stroke,"text-size default is independent from pen/arrow thickness");
            f.Window.BeginPointer(new Point(300,200));check(f.Editor.FontSize==size+5,"new caption uses its independent remembered size");
            var editor=f.Editor;double editingSize=editor.FontSize;
            check(!f.Window.HandleCaptureWheel(-120,ModifierKeys.None,new Point(320,220))&&editor.FontSize==editingSize,"ordinary wheel cannot silently resize live text");
            check(f.Window.HandleCaptureWheel(-120,ModifierKeys.Control,new Point(320,220))&&editor.FontSize==editingSize-5,"Control-wheel is an explicit text-size shortcut");
            f.Window.CancelFromPointer();f.Tool(Key.M);double unchanged=AppState.Settings.Stroke;
            check(!f.Window.HandleCaptureWheel(120,ModifierKeys.Control,new Point(400,250))&&AppState.Settings.Stroke==unchanged,"mosaic does not mutate a hidden or meaningless width setting");
        }
        AppState.Settings=new Preferences {Stroke=4,TextSize=20,Color="#EF4444"};
    }
    static void WrappedText(Action<bool,string> check){
        using(var f=new Fixture()){
            f.Tool(Key.T);f.Window.BeginPointer(new Point(430,280));var editor=f.Editor;editor.Text="靠近边缘的长文字需要自动换行。A longer caption remains visible.";
            editor.Measure(new Size(600,400));editor.Arrange(new Rect(0,0,editor.DesiredSize.Width,editor.DesiredSize.Height));editor.UpdateLayout();
            check(editor.TextWrapping==TextWrapping.Wrap&&(double)editor.Tag==150,"live caption has an explicit visible wrapping width at the crop edge");
            int editingLines=editor.LineCount;
            f.Window.HandleCaptureKey(Key.Enter,ModifierKeys.Control);var text=f.Surface.Marks.Single();
            var oneLine=new Annotation {Kind="text",Text="字",Width=text.Width};int exportedLines=(int)Math.Round(text.Bounds.Height/oneLine.Bounds.Height);
            check(editingLines==exportedLines,"editor and committed caption agree on line count: editor="+editingLines+" export="+exportedLines);
            check(text.TextWrapWidth==150&&text.Bounds.Width<=150.1&&text.Bounds.Height>text.TextFontSize*2&&f.Window.ValidateCaptureTextBounds(),"committed caption wraps and is repositioned inside the crop rather than silently losing trailing text");
            var original=text.Text;double wrap=text.TextWrapWidth;var body=new Point(text.Bounds.Left+20,text.Bounds.Top+10);f.Window.BeginPointer(body,2);f.Editor.Text="新的短标题";f.Window.HandleCaptureKey(Key.Enter,ModifierKeys.Control);
            f.Window.HandleCaptureKey(Key.Z,ModifierKeys.Control);check(text.Text==original&&text.TextWrapWidth==wrap,"undo preserves wrapping metadata and previous text layout");
            f.Window.HandleCaptureKey(Key.Y,ModifierKeys.Control);check(text.Text=="新的短标题"&&text.TextWrapWidth==wrap,"redo restores text without dropping its wrapping width");
            body=new Point(text.Bounds.Left+20,text.Bounds.Top+10);f.Window.BeginPointer(body,2);f.Editor.Text=new string('长',600);f.Window.HandleCaptureKey(Key.Enter,ModifierKeys.Control);
            check(!f.Window.ValidateCaptureTextBounds()&&f.Surface.Marks.Single()==text&&Field<TextBlock>(f.Window,"translationStatus").Text.Contains("文字超出选区"),"oversized text keeps its editable object and explicit recovery feedback instead of allowing silent clipping");
            body=new Point(text.Bounds.Left+20,text.Bounds.Top+10);f.Window.BeginPointer(body,2);f.Editor.Text="修复";f.Window.HandleCaptureKey(Key.Enter,ModifierKeys.Control);
            check(f.Window.ValidateCaptureTextBounds()&&!Field<TextBlock>(f.Window,"translationStatus").Text.Contains("文字超出选区"),"fixing oversized text clears the blocking feedback without retaking a screenshot");
        }
    }
    static void StableActions(Action<bool,string> check){
        using(var f=new Fixture()){
            f.Crop=new Rect(100,500,500,210);f.Surface.Selection=f.Crop;f.Layout(new Size(1200,800));
            var card=Field<Border>(f.Window,"toolbarCard");var baseline=new Point(Canvas.GetLeft(card),Canvas.GetTop(card));
            foreach(var tool in new[]{Key.R,Key.T,Key.A,Key.M,Key.V}){
                f.Tool(tool);f.Layout(new Size(1200,800));check(new Point(Canvas.GetLeft(card),Canvas.GetTop(card))==baseline,"switching tool properties leaves primary buttons at the same location: "+tool);
                var status=Field<TextBlock>(f.Window,"translationStatus");status.Text="正在识别，原图保留。";status.Visibility=Visibility.Visible;f.Layout(new Size(1200,800));
                check(new Point(Canvas.GetLeft(card),Canvas.GetTop(card))==baseline,"asynchronous progress cannot push Copy/Save to the opposite side: "+tool);
                status.Visibility=Visibility.Collapsed;
            }
        }
    }
}
