using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QingJie {
    public sealed class Annotation {
        public string Kind;
        public string ArrowStyle="open";
        public Point Start,End;
        public string Color="#EF4444";
        public double Width=3;
        public string Text="";
        // Zero preserves the metrics of legacy annotations. New captions carry
        // their visible wrapping width through editing, history and export.
        public double TextWrapWidth;
        public List<Point> Points=new List<Point>();
        public BitmapSource Mosaic;
        public double TextFontSize {get{return Math.Max(16,Width*5);}}
        // Each annotation owns just its current resources. Moving it does not
        // reparse its color or reformat the caption on every pointer frame.
        string inkColor,layoutText;
        Brush inkBrush;
        Pen inkPen;
        FormattedText textLayout;
        Brush layoutBrush;
        CultureInfo layoutCulture;
        double layoutSize,layoutWrap;
        int layoutThread;
        Brush Ink {get{if(inkBrush==null||inkColor!=Color){inkBrush=Ui.Brush(Color);inkColor=Color;inkPen=null;}return inkBrush;}}
        Pen Stroke {get{var brush=Ink;if(inkPen==null||inkPen.Thickness!=Width){inkPen=new Pen(brush,Width){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round};inkPen.Freeze();}return inkPen;}}
        FormattedText CreateText(){
            string value=Text??"";double size=TextFontSize,wrap=TextWrapWidth>0&&!double.IsNaN(TextWrapWidth)&&!double.IsInfinity(TextWrapWidth)?Math.Max(1,TextWrapWidth):0;
            var culture=CultureInfo.CurrentUICulture;int thread=System.Threading.Thread.CurrentThread.ManagedThreadId;
            if(textLayout==null||layoutText!=value||layoutSize!=size||layoutWrap!=wrap||!ReferenceEquals(layoutCulture,culture)||layoutThread!=thread){
                textLayout=new FormattedText(value,culture,FlowDirection.LeftToRight,new Typeface("Microsoft YaHei UI"),size,Brushes.Black);if(wrap>0)textLayout.MaxTextWidth=wrap;
                layoutText=value;layoutSize=size;layoutWrap=wrap;layoutCulture=culture;layoutThread=thread;
                layoutBrush=Brushes.Black;
            }
            return textLayout;
        }
        // Hit testing and the selection frame use the very same metrics as export,
        // including explicit newlines and trailing whitespace.
        public Rect Bounds {get{if(Kind=="pen"&&Points.Count>0){var box=new Rect(Points[0],new Size(0,0));for(int i=1;i<Points.Count;i++)box.Union(Points[i]);return box;}if(Kind!="text")return Geometry.FromPoints(Start,End);var text=CreateText();return new Rect(Start,new Size(Math.Max(1,text.WidthIncludingTrailingWhitespace),Math.Max(1,text.Height)));}}
        public void Draw(DrawingContext d) {
            if(Kind=="text"){var text=CreateText();var brush=Ink;if(!ReferenceEquals(layoutBrush,brush)){text.SetForegroundBrush(brush);layoutBrush=brush;}d.DrawText(text,Start);return;}
            var pen=Stroke;
            switch(Kind) {
                case "rect":d.DrawRectangle(null,pen,Bounds);break;
                case "ellipse":d.DrawEllipse(null,pen,new Point(Bounds.X+Bounds.Width/2,Bounds.Y+Bounds.Height/2),Bounds.Width/2,Bounds.Height/2);break;
                case "line":d.DrawLine(pen,Start,End);break;
                case "arrow":CaptureArrowGeometry.Draw(d,this,pen);break;
                case "pen": for(int i=1;i<Points.Count;i++) d.DrawLine(pen,Points[i-1],Points[i]);break;
                case "mosaic":
                    if(Mosaic!=null) {var group=new DrawingGroup();group.Children.Add(new ImageDrawing(Mosaic,Bounds));RenderOptions.SetBitmapScalingMode(group,BitmapScalingMode.NearestNeighbor);d.DrawDrawing(group);}
                    else d.DrawRectangle(Ui.Brush("#889294"),null,Bounds);
                    break;
            }
        }
    }
    public sealed class ScreenshotSurface : FrameworkElement {
        public BitmapSource Image;
        public BitmapSource TranslationImage;
        public Rect TranslationBounds=Rect.Empty;
        public bool ShowTranslation;
        public void ClearTranslation(){TranslationImage=null;TranslationBounds=Rect.Empty;ShowTranslation=false;InvalidateVisual();}
        public Rect Selection=Rect.Empty;
        public Rect HoverSelection=Rect.Empty;
        public List<Annotation> Marks=new List<Annotation>();
        public Annotation Draft;
        public Annotation SelectedText,EditingText;
        public Annotation SelectedAnnotation;
        public bool ShowHandles=true;
        public Point? BrushPoint;
        public double BrushDiameter=3;
        public bool ShowBrushSize;
        public Point? SizeHintPoint;
        public string SizeHint;
        public double ScaleX {get{return Image.PixelWidth/Math.Max(1,ActualWidth);}}
        public double ScaleY {get{return Image.PixelHeight/Math.Max(1,ActualHeight);}}
        protected override void OnRender(DrawingContext d) {
            if(Image==null)return;
            d.DrawImage(Image,new Rect(0,0,ActualWidth,ActualHeight));
            var mask=Ui.Brush("#80000000");
            var displayed=Selection.IsEmpty?HoverSelection:Selection;
            if(displayed.IsEmpty) d.DrawRectangle(mask,null,new Rect(0,0,ActualWidth,ActualHeight));
            else {
                var s=displayed;
                d.DrawRectangle(mask,null,new Rect(0,0,ActualWidth,Math.Max(0,s.Top)));
                d.DrawRectangle(mask,null,new Rect(0,s.Bottom,ActualWidth,Math.Max(0,ActualHeight-s.Bottom)));
                d.DrawRectangle(mask,null,new Rect(0,s.Top,Math.Max(0,s.Left),s.Height));
                d.DrawRectangle(mask,null,new Rect(s.Right,s.Top,Math.Max(0,ActualWidth-s.Right),s.Height));
                d.PushClip(new RectangleGeometry(s));if(ShowTranslation&&TranslationImage!=null)d.DrawImage(TranslationImage,TranslationBounds);foreach(var a in Marks)if(a!=EditingText)a.Draw(d);if(Draft!=null)Draft.Draw(d);d.Pop();
                if(SelectedText!=null&&Marks.Contains(SelectedText)&&SelectedText!=EditingText){var box=TextFrame(SelectedText);d.DrawRectangle(null,new Pen(Ui.Green,1),box);foreach(var p in Handles(box))d.DrawRectangle(Brushes.White,new Pen(Ui.Green,1),new Rect(p.X-3,p.Y-3,6,6));}
                if(SelectedAnnotation!=null&&Marks.Contains(SelectedAnnotation)){
                    var mark=SelectedAnnotation;var accent=Ui.Tone("Accent");
                    // Lines use their actual endpoints, never a misleading box.
                    if(!CaptureAnnotationGeometry.Linear(mark))d.DrawRectangle(null,new Pen(accent,1),mark.Bounds);
                    foreach(var p in CaptureAnnotationGeometry.Handles(mark))d.DrawEllipse(Brushes.White,new Pen(accent,1.2),p,3.5,3.5);
                }
                d.DrawRectangle(null,new Pen(Ui.Green,1.3),s);
                if(ShowHandles&&!Selection.IsEmpty)foreach(var p in Handles())d.DrawRectangle(Brushes.White,new Pen(Ui.Green,1),new Rect(p.X-3,p.Y-3,6,6));
                string size=string.Format("{0} × {1}",Math.Round(s.Width*ScaleX),Math.Round(s.Height*ScaleY));
                var ft=new FormattedText(size,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),12,Brushes.White);
                double sy=s.Top>25?s.Top-24:s.Top+8;d.DrawRoundedRectangle(Ui.Brush("#B5202727"),null,new Rect(s.Left,sy,ft.Width+16,21),3,3);d.DrawText(ft,new Point(s.Left+8,sy+2));
                if(Selection.IsEmpty){var hint=new FormattedText("单击选中 · 拖动框选 · 滚轮 / Tab 换边框 · Ctrl 自由选",CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,new Typeface("Microsoft YaHei UI"),12,Brushes.White);double x=Geometry.Clamp(s.Left,8,Math.Max(8,ActualWidth-hint.Width-24)),y=Geometry.Clamp(s.Bottom+8,8,Math.Max(8,ActualHeight-30));d.DrawRoundedRectangle(Ui.Brush("#DB202727"),null,new Rect(x,y,hint.Width+16,24),4,4);d.DrawText(hint,new Point(x+8,y+3));}
            }
            // Drawing-only cursor preview: never included in Export().
            if(BrushPoint.HasValue){var p=BrushPoint.Value;double radius=Math.Max(.5,BrushDiameter/2);d.DrawEllipse(null,new Pen(Brushes.White,2.5),p,radius,radius);d.DrawEllipse(null,new Pen(Ui.Ink,1),p,radius,radius);
            }
            if(ShowBrushSize&&(SizeHintPoint.HasValue||BrushPoint.HasValue)){var p=SizeHintPoint??BrushPoint.Value;var label=new FormattedText(SizeHint??(BrushDiameter.ToString("0.#")+" px"),CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Microsoft YaHei UI"),12,Brushes.White,VisualTreeHelper.GetDpi(this).PixelsPerDip);double radius=Math.Max(.5,BrushDiameter/2),x=Geometry.Clamp(p.X+radius+10,0,Math.Max(0,ActualWidth-label.Width-14)),y=Geometry.Clamp(p.Y+radius+10,0,Math.Max(0,ActualHeight-24));d.DrawRoundedRectangle(Ui.Brush("#DB202727"),null,new Rect(x,y,label.Width+12,23),4,4);d.DrawText(label,new Point(x+6,y+3));}
        }
        public Point[] Handles() {return Handles(Selection);}
        public static Rect TextFrame(Annotation mark){var bounds=mark.Bounds;double width=Math.Max(28,bounds.Width),height=Math.Max(28,bounds.Height);return new Rect(bounds.Left+(bounds.Width-width)/2,bounds.Top+(bounds.Height-height)/2,width,height);}
        public static Point[] Handles(Rect s) {return new[]{new Point(s.Left,s.Top),new Point(s.Left+s.Width/2,s.Top),new Point(s.Right,s.Top),new Point(s.Right,s.Top+s.Height/2),new Point(s.Right,s.Bottom),new Point(s.Left+s.Width/2,s.Bottom),new Point(s.Left,s.Bottom),new Point(s.Left,s.Top+s.Height/2)};}
        public BitmapSource Export(bool annotated=true,bool translated=true) {
            if(Selection.IsEmpty||Selection.Width<2||Selection.Height<2)throw new InvalidOperationException("请先框选截图区域。");
            var crop=Geometry.PixelCrop(Selection,ScaleX,ScaleY,Image.PixelWidth,Image.PixelHeight);
            var visual=new DrawingVisual();
            using(var d=visual.RenderOpen()) {
                d.DrawImage(new CroppedBitmap(Image,crop),new Rect(0,0,crop.Width,crop.Height));
                if(translated&&ShowTranslation&&TranslationImage!=null){d.PushTransform(new TranslateTransform(-crop.X,-crop.Y));d.PushTransform(new ScaleTransform(ScaleX,ScaleY));d.DrawImage(TranslationImage,TranslationBounds);d.Pop();d.Pop();}
                if(annotated) {d.PushTransform(new TranslateTransform(-crop.X,-crop.Y));d.PushTransform(new ScaleTransform(ScaleX,ScaleY));foreach(var a in Marks)a.Draw(d);d.Pop();d.Pop();}
            }
            var result=new RenderTargetBitmap(crop.Width,crop.Height,96,96,PixelFormats.Pbgra32);result.Render(visual);result.Freeze();return result;
        }
        public void PrepareMosaic(Annotation a) {
            if(a.Bounds.Width<1||a.Bounds.Height<1)return;
            var crop=Geometry.PixelCrop(a.Bounds,ScaleX,ScaleY,Image.PixelWidth,Image.PixelHeight);
            var image=new CroppedBitmap(Image,crop);
            var small=new TransformedBitmap(image,new ScaleTransform(Math.Max(1,Math.Round(crop.Width/14.0))/crop.Width,Math.Max(1,Math.Round(crop.Height/14.0))/crop.Height));small.Freeze();a.Mosaic=small;
        }
    }
}
