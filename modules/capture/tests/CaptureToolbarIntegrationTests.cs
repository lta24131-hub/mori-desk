using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;

// Exercise the real positioning entry point and real button tree, not only the
// pure geometry helper. Every image is synthetic and every window stays inert.
internal static class CaptureToolbarIntegrationTests {
    static readonly BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    static T Field<T>(CaptureWindow window,string name){return (T)typeof(CaptureWindow).GetField(name,Private).GetValue(window);}
    static object Call(CaptureWindow window,string name,params object[] arguments){return typeof(CaptureWindow).GetMethod(name,Private).Invoke(window,arguments);}
    static Rect Bounds(FrameworkElement element){
        var rendered=new Rect(new Point(),element.RenderSize);
        // TextBlock retains whole-line RenderSize when its last line is clipped.
        // The layout clip is what is actually drawn and receives input.
        var clip=System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(element);
        if(clip!=null)rendered.Intersect(clip.Bounds);
        rendered.Offset(Canvas.GetLeft(element),Canvas.GetTop(element));return rendered;
    }
    static bool Inside(Rect bounds,Size size){return bounds.Left>=-.01&&bounds.Top>=-.01&&bounds.Right<=size.Width+.01&&bounds.Bottom<=size.Height+.01&&bounds.Width>0&&bounds.Height>0;}
    static bool Separate(Rect a,Rect b){return a.Right<=b.Left+.01||b.Right<=a.Left+.01||a.Bottom<=b.Top+.01||b.Bottom<=a.Top+.01;}
    static IEnumerable<DependencyObject> Descendants(DependencyObject node){
        yield return node;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)foreach(var child in Descendants(VisualTreeHelper.GetChild(node,i)))yield return child;
    }
    static void Layout(CaptureWindow window,Size size){
        window.Width=size.Width;window.Height=size.Height;
        window.Measure(size);window.Arrange(new Rect(new Point(),size));
        var root=(Grid)window.Content;root.Measure(size);root.Arrange(new Rect(new Point(),size));
        Call(window,"PositionTools");root.UpdateLayout();
    }
    internal static void Run(Action<bool,string> check){
        var oldPreferences=AppState.Settings;AppState.Settings=new Preferences();
        var pixels=new byte[4*4*4];for(int i=3;i<pixels.Length;i+=4)pixels[i]=255;
        var image=BitmapSource.Create(4,4,96,96,PixelFormats.Bgra32,null,pixels,16);image.Freeze();
        try{
            foreach(var viewport in new[]{new Size(1024,768),new Size(640,480),new Size(320,240),new Size(180,120)}){
                foreach(bool statusVisible in new[]{false,true}){
                  foreach(var crop in new[]{new Rect(0,0,viewport.Width,viewport.Height),new Rect(0,0,30,25),new Rect(viewport.Width-35,viewport.Height-30,35,30)}){
                    var window=new CaptureWindow(new Snapshot {Image=image,Bounds=new System.Drawing.Rectangle(0,0,4,4)});
                    try{
                        var surface=Field<ScreenshotSurface>(window,"surface");
                        surface.Selection=crop;
                        Call(window,"SetTool","arrow");
                        var status=Field<TextBlock>(window,"translationStatus");
                        status.Text="Synthetic status: original pixels retained. Read the complete transcript without repeating recognition.";
                        status.Visibility=statusVisible?Visibility.Visible:Visibility.Collapsed;
                        Layout(window,viewport);
                        check(surface.ActualWidth==viewport.Width&&surface.ActualHeight==viewport.Height,"offscreen capture fixture has its real surface viewport dimensions: "+viewport);
                        check(!window.IsVisible&&new WindowInteropHelper(window).Handle==IntPtr.Zero,"toolbar integration fixture never creates a native or visible window");
                        var toolbar=Field<Border>(window,"toolbarCard");var properties=Field<Border>(window,"propertiesCard");
                        var toolbarBounds=Bounds(toolbar);var propertiesBounds=Bounds(properties);
                        check(Inside(toolbarBounds,viewport)&&Inside(propertiesBounds,viewport),"production PositionTools keeps both actual cards on screen at "+viewport+" status="+statusVisible);
                        check(Separate(toolbarBounds,propertiesBounds),"production toolbar and attributes never cover one another at "+viewport+" status="+statusVisible);
                        if(statusVisible){var statusBounds=Bounds(status);check(Inside(statusBounds,viewport)&&Separate(toolbarBounds,statusBounds)&&Separate(propertiesBounds,statusBounds),"actual translation feedback stays inside viewport without covering tools at "+viewport+"; toolbar="+toolbarBounds+" properties="+propertiesBounds+" status="+statusBounds+" height="+status.Height+" max="+status.MaxHeight+" desired="+status.DesiredSize);}
                        check(toolbar.Child is ScrollViewer&&properties.Child is ScrollViewer,"production toolbar wraps actions in reachable scrolling cards rather than clipping their hit targets");
                        CheckButtons(surface,toolbar,check);CheckButtons(surface,properties,check);
                        surface.Selection=Rect.Empty;Call(window,"PositionTools");
                        check(toolbar.Visibility==Visibility.Collapsed&&properties.Visibility==Visibility.Collapsed,"clearing crop hides both actual tool cards at "+viewport);
                    }finally{window.Close();}
                  }
                }
            }
            GestureReturn(image,check);
        }finally{AppState.Settings=oldPreferences;}
    }
    static int History(CaptureWindow window){var history=typeof(CaptureWindow).GetField("annotationUndo",Private).GetValue(window);return (int)history.GetType().GetProperty("Count").GetValue(history,null);}
    static void GestureReturn(BitmapSource image,Action<bool,string> check){
        var window=new CaptureWindow(new Snapshot {Image=image,Bounds=new System.Drawing.Rectangle(0,0,4,4)});
        try{
            var surface=Field<ScreenshotSurface>(window,"surface");surface.Selection=new Rect(20,20,600,440);Layout(window,new Size(640,480));
            var text=new Annotation {Kind="text",Text="Drag return",Start=new Point(80,100),Width=4,Color="#EF4444"};
            var shape=new Annotation {Kind="rect",Start=new Point(260,120),End=new Point(360,220),Width=4,Color="#EF4444"};
            surface.Marks.Add(text);surface.Marks.Add(shape);Call(window,"SetTool","select");
            var point=new Point(text.Bounds.Left+text.Bounds.Width*.3,text.Bounds.Top+text.Bounds.Height*.5);var original=text.Start;
            window.BeginPointer(point);window.ContinuePointer(point+new Vector(32,18));
            window.ContinuePointer(point+new Vector(1,0));
            check(text.Start==original+new Vector(1,0),"text follows the final one-pixel return instead of stopping at an older drag position");
            window.EndPointer(point);
            check(text.Start==original&&History(window)==0,"returning text exactly to its origin adds no phantom undo operation");
            point=new Point(310,120);original=shape.Start;var end=shape.End;
            window.BeginPointer(point);check(surface.SelectedAnnotation==shape,"actual select-tool pointer path selects a drawn shape");
            window.ContinuePointer(point+new Vector(32,18));window.ContinuePointer(point+new Vector(1,0));
            check(shape.Start==original+new Vector(1,0)&&shape.End==end+new Vector(1,0),"shape follows the final one-pixel return without a stale movement offset");
            window.EndPointer(point);
            check(shape.Start==original&&shape.End==end&&History(window)==0,"returning a shape exactly to its origin is a reversible no-op");
            Layout(window,new Size(640,480));
            check(Field<Border>(window,"propertiesCard").Visibility==Visibility.Visible,"selecting an existing shape exposes its actual color and stroke attribute card");
            point=new Point(360,170);
            window.BeginPointer(point);window.ContinuePointer(point+new Vector(32,18));window.ContinuePointer(point+new Vector(1,0));window.EndPointer(point);
            check(shape.Start==original&&shape.End==end&&History(window)==0,"resize handles also return to their initial geometry with no phantom history");
        }finally{window.Close();}
    }
    static void CheckButtons(ScreenshotSurface surface,Border card,Action<bool,string> check){
        var scroll=card.Child as ScrollViewer;if(scroll==null)return;
        var content=(FrameworkElement)scroll.Content;var buttons=Descendants(content).OfType<Button>().Where(button=>button.Visibility!=Visibility.Collapsed).ToArray();
        check(buttons.Length>0,"each real scrolling tool card retains actionable controls");
        foreach(var button in buttons){
            // Pixel scrolling makes even the last row reachable in a short
            // viewport; no synthetic mouse events or desktop input is required.
            var initial=button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize));
            scroll.ScrollToVerticalOffset(Math.Max(0,initial.Top));scroll.UpdateLayout();
            var actual=button.TransformToAncestor(scroll).TransformBounds(new Rect(button.RenderSize));
            var visible=Rect.Intersect(actual,new Rect(0,0,scroll.ViewportWidth,scroll.ViewportHeight));
            check(button.ActualWidth>=18&&button.ActualHeight>=18&&visible.Width>=Math.Min(18,button.ActualWidth)-.1&&visible.Height>=Math.Min(18,button.ActualHeight)-.1,"real tool hit target remains reachable by scrolling: "+(button.Name.Length>0?button.Name:button.ToolTip)+" in "+surface.ActualWidth+"x"+surface.ActualHeight);
        }
    }
}
