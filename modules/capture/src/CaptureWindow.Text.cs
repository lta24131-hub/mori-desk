using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QingJie {
    public sealed partial class CaptureWindow {
        sealed class AnnotationChange {
            internal Annotation Mark,Before,After;
            internal int Index;
            internal CaptureSceneState SceneBefore,SceneAfter;
        }
        readonly Stack<AnnotationChange> annotationUndo=new Stack<AnnotationChange>();
        readonly Stack<AnnotationChange> annotationRedo=new Stack<AnnotationChange>();
        Annotation editingText,textDrag,textDragBefore;
        Point textPosition;
        string textEditorColor;
        int textResize=-1;
        Rect oldTextBounds,oldTextFrame;

        static Annotation CopyAnnotation(Annotation a){return new Annotation {Kind=a.Kind,ArrowStyle=a.ArrowStyle,Start=a.Start,End=a.End,Color=a.Color,Width=a.Width,Text=a.Text,TextWrapWidth=a.TextWrapWidth,Points=new List<Point>(a.Points),Mosaic=a.Mosaic};}
        static void RestoreAnnotation(Annotation a,Annotation value){a.Kind=value.Kind;a.ArrowStyle=value.ArrowStyle;a.Start=value.Start;a.End=value.End;a.Color=value.Color;a.Width=value.Width;a.Text=value.Text;a.TextWrapWidth=value.TextWrapWidth;a.Points=new List<Point>(value.Points);a.Mosaic=value.Mosaic;}
        static bool SameAnnotation(Annotation a,Annotation b){return a.Kind==b.Kind&&a.ArrowStyle==b.ArrowStyle&&a.Start==b.Start&&a.End==b.End&&a.Color==b.Color&&a.Width==b.Width&&a.Text==b.Text&&a.TextWrapWidth==b.TextWrapWidth&&a.Points.SequenceEqual(b.Points);}
        void RememberAnnotation(Annotation mark,Annotation before,Annotation after,int index){
            RememberChange(new AnnotationChange {Mark=mark,Before=before,After=after,Index=index});
        }
        void RememberChange(AnnotationChange change){
            wheelHistory=null;annotationUndo.Push(change);
            // History retains annotation deltas, not screenshot-sized bitmaps or copies
            // of every pen stroke. Keep even a long editing session bounded.
            if(annotationUndo.Count>100){var keep=annotationUndo.Take(100).Reverse().ToArray();annotationUndo.Clear();foreach(var savedChange in keep)annotationUndo.Push(savedChange);}
            annotationRedo.Clear();redo.Clear();
            RefreshCaptureToolbarHistory();
        }
        void RecordAnnotationAdded(Annotation mark){RememberAnnotation(mark,null,CopyAnnotation(mark),surface.Marks.IndexOf(mark));}
        void RecordAnnotationEdit(Annotation mark,Annotation before){if(!SameAnnotation(mark,before))RememberAnnotation(mark,before,CopyAnnotation(mark),surface.Marks.IndexOf(mark));}
        void ApplyAnnotationChange(AnnotationChange change,bool forward){
            if(change.SceneBefore!=null){RestoreScene(forward?change.SceneAfter:change.SceneBefore);return;}
            var value=forward?change.After:change.Before;
            if(value==null)surface.Marks.Remove(change.Mark);
            else {RestoreAnnotation(change.Mark,value);if(!surface.Marks.Contains(change.Mark))surface.Marks.Insert(Math.Min(change.Index,surface.Marks.Count),change.Mark);}
        }
        void UndoAnnotation(){
            ResetStrokeWheel();FinishShapeGesture();FinishTextGesture();
            if(annotationUndo.Count>0){var change=annotationUndo.Pop();ApplyAnnotationChange(change,false);annotationRedo.Push(change);}
            SelectText(null);last=null;PositionTools();surface.InvalidateVisual();
        }
        void RedoAnnotation(){
            ResetStrokeWheel();FinishShapeGesture();FinishTextGesture();
            if(annotationRedo.Count>0){var change=annotationRedo.Pop();ApplyAnnotationChange(change,true);wheelHistory=null;annotationUndo.Push(change);}
            SelectText(null);last=null;PositionTools();surface.InvalidateVisual();
        }
        void ClearAnnotationHistory(){ResetStrokeWheel();annotationUndo.Clear();annotationRedo.Clear();redo.Clear();SelectText(null);}
        void ClearTextState(){
            shapeDrag=null;shapeBefore=null;selectionBefore=null;textDrag=null;textDragBefore=null;dragging=false;surface.ReleaseMouseCapture();
            Mori.Theme.Changed-=RefreshTextEditorBackground;
            if(textEditor!=null)overlay.Children.Remove(textEditor);textEditor=null;editingText=null;surface.EditingText=null;ClearAnnotationHistory();
        }
        void SelectText(Annotation mark){
            surface.SelectedAnnotation=null;
            var previous=surface.SelectedText;surface.SelectedText=mark;
            if(mark!=null)last=mark;else if(last==previous)last=null;
            UpdateWidth();surface.InvalidateVisual();
        }
        Annotation HitText(Point point){
            if(surface.Selection.IsEmpty||!surface.Selection.Contains(point))return null;
            for(int i=surface.Marks.Count-1;i>=0;i--){var mark=surface.Marks[i];if(mark.Kind!="text")continue;var bounds=mark.Bounds;bounds.Inflate(4,4);if(bounds.Contains(point))return mark;}
            return surface.SelectedText!=null&&ScreenshotSurface.TextFrame(surface.SelectedText).Contains(point)?surface.SelectedText:null;
        }
        int HitTextHandle(Point point){
            if(surface.SelectedText==null)return -1;
            var bounds=ScreenshotSurface.TextFrame(surface.SelectedText);var handles=ScreenshotSurface.Handles(bounds);int found=-1;
            // Always leave a draggable interior, even for a narrow "i" or ".".
            // Otherwise the eight generous handle targets swallow the entire label.
            var interior=bounds;interior.Inflate(-Math.Min(4,bounds.Width/4),-Math.Min(4,bounds.Height/4));if(interior.Contains(point))return -1;
            double closest=81;
            for(int i=0;i<handles.Length;i++){double distance=(handles[i]-point).LengthSquared;if(distance<closest){closest=distance;found=i;}}
            return found;
        }
        Cursor TextPointerCursor(Point point){
            if(textDrag!=null)return textResize>=0?ResizeCursor(textResize):Cursors.SizeAll;
            // The screenshot's existing resize targets keep precedence at its edges.
            if(HitSelectionHandle(point)>=0)return null;
            var top=TopAnnotation(point);if(top!=null&&top.Kind!="text")return null;
            int handle=HitTextHandle(point);if(handle>=0)return ResizeCursor(handle);
            return HitText(point)!=null?Cursors.SizeAll:null;
        }
        bool BeginTextPointer(Point point,int clickCount){
            if(HitSelectionHandle(point)>=0)return false;
            var top=TopAnnotation(point);if(top!=null&&top.Kind!="text")return false;
            var body=HitText(point);int handle=clickCount>=2&&body!=null?-1:HitTextHandle(point);var mark=handle>=0?surface.SelectedText:body;if(mark==null)return false;
            SelectText(mark);
            if(clickCount>=2&&handle<0){StartText(mark.Start,mark);return true;}
            textDrag=mark;textDragBefore=CopyAnnotation(mark);oldTextBounds=mark.Bounds;oldTextFrame=ScreenshotSurface.TextFrame(mark);textResize=handle;dragging=true;
            HideTools();surface.CaptureMouse();UpdateBrushCursor(point);return true;
        }
        Point ClampTextOrigin(Point point,Rect bounds){
            var area=surface.Selection;
            return new Point(Geometry.Clamp(point.X,area.Left,Math.Max(area.Left,area.Right-bounds.Width)),Geometry.Clamp(point.Y,area.Top,Math.Max(area.Top,area.Bottom-bounds.Height)));
        }
        void MoveSelectedText(Point point){
            // Always recompute from the original pointer origin. Ignoring the
            // last one-pixel return after a larger move leaves the text stranded
            // at an earlier position instead of following the actual pointer.
            if(textDrag==null)return;
            if(textResize<0){textDrag.Start=ClampTextOrigin(textDragBefore.Start+(point-down),oldTextBounds);return;}
            if(point==down){RestoreAnnotation(textDrag,textDragBefore);UpdateWidth();return;}
            bool left=textResize==0||textResize==6||textResize==7,right=textResize==2||textResize==3||textResize==4;
            bool top=textResize==0||textResize==1||textResize==2,bottom=textResize==4||textResize==5||textResize==6;
            // Project a corner gesture onto its original diagonal instead of
            // taking the larger axis ratio. A long one-line caption must not
            // double in size because the pointer moved one line-height down.
            // Edge handles use one axis and keep the perpendicular center fixed.
            var axis=new Vector(left?-oldTextFrame.Width:right?oldTextFrame.Width:0,top?-oldTextFrame.Height:bottom?oldTextFrame.Height:0);
            double ratio=1+Vector.Multiply(point-down,axis)/axis.LengthSquared;
            if(Math.Abs(ratio-1)<.000000000001){RestoreAnnotation(textDrag,textDragBefore);UpdateWidth();return;}
            double size=textDragBefore.TextFontSize;
            var area=surface.Selection;double centerX=oldTextBounds.Left+oldTextBounds.Width/2,centerY=oldTextBounds.Top+oldTextBounds.Height/2;
            double roomX=left?oldTextBounds.Right-area.Left:right?area.Right-oldTextBounds.Left:2*Math.Min(centerX-area.Left,area.Right-centerX);
            double roomY=top?oldTextBounds.Bottom-area.Top:bottom?area.Bottom-oldTextBounds.Top:2*Math.Min(centerY-area.Top,area.Bottom-centerY);
            double smallest=16/size,largest=Math.Min(250/size,Math.Min(roomX/oldTextBounds.Width,roomY/oldTextBounds.Height));
            // Never slide the whole caption away from the fixed opposite corner
            // to make it fit. Stop scaling at the remaining space instead.
            if(largest<smallest){RestoreAnnotation(textDrag,textDragBefore);UpdateWidth();return;}
            ratio=Geometry.Clamp(ratio,smallest,largest);
            var resized=ApplyTextResize(ratio,left,right,top,bottom);
            if(!TextResizeFits(resized,area)){
                // FormattedText rounds glyph metrics and wrapped line breaks.
                // Check measured geometry, not only an ideal proportional size.
                // The original anchor stays fixed throughout this correction.
                double low=smallest,high=ratio;
                if(!TextResizeFits(ApplyTextResize(low,left,right,top,bottom),area)){RestoreAnnotation(textDrag,textDragBefore);UpdateWidth();return;}
                for(int i=0;i<20;i++){double middle=(low+high)/2;if(TextResizeFits(ApplyTextResize(middle,left,right,top,bottom),area))low=middle;else high=middle;}
                ApplyTextResize(low,left,right,top,bottom);
            }
            UpdateWidth();
        }
        Rect ApplyTextResize(double ratio,bool left,bool right,bool top,bool bottom){
            textDrag.Width=textDragBefore.TextFontSize*ratio/5;
            // Scale font and wrap width together. Clamping the wrap independently
            // would unexpectedly add lines and break the pointer's aspect ratio.
            textDrag.TextWrapWidth=textDragBefore.TextWrapWidth>0?textDragBefore.TextWrapWidth*ratio:textDragBefore.TextWrapWidth;
            var measured=textDrag.Bounds;
            double x=left?oldTextBounds.Right-measured.Width:right?oldTextBounds.Left:oldTextBounds.Left+(oldTextBounds.Width-measured.Width)/2;
            double y=top?oldTextBounds.Bottom-measured.Height:bottom?oldTextBounds.Top:oldTextBounds.Top+(oldTextBounds.Height-measured.Height)/2;
            textDrag.Start=new Point(x,y);
            return new Rect(textDrag.Start,measured.Size);
        }
        static bool TextResizeFits(Rect bounds,Rect area){return bounds.Left>=area.Left-.001&&bounds.Top>=area.Top-.001&&bounds.Right<=area.Right+.001&&bounds.Bottom<=area.Bottom+.001;}
        void FinishTextGesture(){
            if(textDrag==null)return;
            var mark=textDrag;var before=textDragBefore;textDrag=null;textDragBefore=null;textResize=-1;dragging=false;surface.ReleaseMouseCapture();
            RecordAnnotationEdit(mark,before);PositionTools();UpdateWidth();surface.InvalidateVisual();
        }
        void StartText(Point point){StartText(point,null);}
        void StartText(Point point,Annotation mark){
            editingText=mark;textPosition=point;textEditorColor=mark!=null?mark.Color:AppState.Settings.Color;
            surface.EditingText=mark;SelectText(mark);
            double available=Math.Max(1,surface.Selection.Right-point.X),wrap=mark!=null&&mark.TextWrapWidth>0?Math.Min(mark.TextWrapWidth,available):available;
            textEditor=new TextBox {Name="CaptureTextEditor",Text=mark!=null?mark.Text:"",Width=wrap+6,MaxHeight=Math.Max(28,surface.Selection.Height),FontFamily=new System.Windows.Media.FontFamily("Microsoft YaHei UI"),FontSize=mark!=null?mark.TextFontSize:AppState.Settings.TextSize,Foreground=Ui.Brush(textEditorColor),Padding=new Thickness(0),BorderThickness=new Thickness(1),AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Hidden};
            // A scrollbar must not steal wrapping width from the editor while
            // its committed/exported caption retains the full width.
            textEditor.Language=System.Windows.Markup.XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentUICulture.IetfLanguageTag);
            textEditor.Tag=wrap;
            textEditor.PreviewMouseWheel+=(s,e)=>{e.Handled=HandleCaptureWheel(e.Delta,Keyboard.Modifiers,e.GetPosition(surface));};
            textEditor.TextChanged+=(s,e)=>UpdateTextEditorPlacement();textEditor.SizeChanged+=(s,e)=>UpdateTextEditorPlacement();
            Ui.Theme(textEditor,Control.BorderBrushProperty,"Primary");RefreshTextEditorBackground();Mori.Theme.Changed+=RefreshTextEditorBackground;
            // TextBoxView adds a two-DIP horizontal caret gutter inside its
            // border even with Padding=0. Include it so typed and rendered text
            // share both their actual origin and their wrapping width.
            Canvas.SetLeft(textEditor,point.X-3);Canvas.SetTop(textEditor,point.Y-1);overlay.Children.Add(textEditor);textEditor.Focus();textEditor.CaretIndex=textEditor.Text.Length;UpdateWidth();surface.InvalidateVisual();
        }
        void UpdateTextEditorPlacement(){
            if(textEditor==null||surface.Selection.IsEmpty)return;
            // The live editor may grow upward near the bottom edge. Its final
            // annotation uses this same visible origin and exact wrap width.
            double height=Math.Max(textEditor.FontSize*1.3,textEditor.ActualHeight);
            textPosition=new Point(textPosition.X,Geometry.Clamp(textPosition.Y,surface.Selection.Top,Math.Max(surface.Selection.Top,surface.Selection.Bottom-height+2)));
            Canvas.SetTop(textEditor,textPosition.Y-1);
        }
        static double TextEditorLuminance(Color color){Func<byte,double> channel=value=>{double v=value/255.0;return v<=.04045?v/12.92:Math.Pow((v+.055)/1.055,2.4);};return .2126*channel(color.R)+.7152*channel(color.G)+.0722*channel(color.B);}
        static double TextEditorContrast(Color ink,Color background){double a=TextEditorLuminance(ink),b=TextEditorLuminance(background);return (Math.Max(a,b)+.05)/(Math.Min(a,b)+.05);}
        void RefreshTextEditorBackground(){
            if(textEditor==null)return;
            var ink=textEditor.Foreground as SolidColorBrush;var preferred=Ui.Tone("Surface") as SolidColorBrush;if(ink==null||preferred==null)return;
            Brush background=preferred;
            if(TextEditorContrast(ink.Color,preferred.Color)<4.5){
                // This backing belongs only to the temporary editor. Never recolor
                // annotation ink (or exported pixels) to make it match the theme.
                var dark=Ui.Brush("#050505");background=TextEditorContrast(ink.Color,Colors.White)>=TextEditorContrast(ink.Color,dark.Color)?Brushes.White:dark;
            }
            textEditor.Background=background;
        }
        void RemoveTextEditor(){Mori.Theme.Changed-=RefreshTextEditorBackground;overlay.Children.Remove(textEditor);textEditor=null;editingText=null;surface.EditingText=null;Focus();}
        void CommitText(){
            if(textEditor==null)return;
            var mark=editingText;
            if(!string.IsNullOrWhiteSpace(textEditor.Text)){
                var before=mark!=null?CopyAnnotation(mark):null;
                if(mark==null)mark=new Annotation {Kind="text"};
                mark.Text=textEditor.Text;mark.Start=textPosition;mark.Width=textEditor.FontSize/5;mark.Color=textEditorColor;mark.TextWrapWidth=(double)textEditor.Tag;
                mark.Start=ClampTextOrigin(mark.Start,mark.Bounds);
                if(before==null){surface.Marks.Add(mark);RecordAnnotationAdded(mark);}else RecordAnnotationEdit(mark,before);
            }else if(mark!=null){RememberAnnotation(mark,CopyAnnotation(mark),null,surface.Marks.IndexOf(mark));surface.Marks.Remove(mark);mark=null;}
            RemoveTextEditor();SelectText(mark);PositionTools();surface.InvalidateVisual();
            ValidateCaptureTextBounds();
        }
        internal bool ValidateCaptureTextBounds(){
            if(surface.Selection.IsEmpty)return true;
            foreach(var mark in surface.Marks){if(mark.Kind!="text")continue;var bounds=mark.Bounds;var area=surface.Selection;area.Inflate(.5,.5);if(!area.Contains(bounds)){TranslationStatus("文字超出选区，请扩大选区或缩小字号后再完成。");return false;}}
            if(translationStatus.Text=="文字超出选区，请扩大选区或缩小字号后再完成。"){translationStatus.Text="";translationStatus.Visibility=Visibility.Collapsed;PositionTools();}
            return true;
        }
        void CancelText(){if(textEditor==null)return;var mark=editingText;RemoveTextEditor();SelectText(mark);PositionTools();surface.InvalidateVisual();}
        void DeleteSelectedText(){var mark=surface.SelectedText;if(mark==null)return;RememberAnnotation(mark,CopyAnnotation(mark),null,surface.Marks.IndexOf(mark));surface.Marks.Remove(mark);SelectText(null);PositionTools();surface.InvalidateVisual();}
        void ApplyAnnotationColor(string color){
            if(!CanEditCapture)return;
            if(textEditor!=null){textEditorColor=color;textEditor.Foreground=Ui.Brush(color);RefreshTextEditorBackground();}
            else if(SelectedAnnotation!=null){var mark=SelectedAnnotation;var before=CopyAnnotation(mark);mark.Color=color;RecordAnnotationEdit(mark,before);}
            else {AppState.Settings.Color=color;AppState.Settings.Save();}
            surface.InvalidateVisual();
        }
        void ChangeSelectedTextSize(double difference){
            var mark=surface.SelectedText;if(mark==null)return;var before=CopyAnnotation(mark);mark.Width=Geometry.Clamp(mark.TextFontSize+difference,16,250)/5;
            mark.Start=ClampTextOrigin(mark.Start,mark.Bounds);RecordAnnotationEdit(mark,before);UpdateWidth();surface.InvalidateVisual();
        }
    }
}
