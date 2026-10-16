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

        static Annotation CopyAnnotation(Annotation a){return new Annotation {Kind=a.Kind,Start=a.Start,End=a.End,Color=a.Color,Width=a.Width,Text=a.Text,Points=new List<Point>(a.Points),Mosaic=a.Mosaic};}
        static void RestoreAnnotation(Annotation a,Annotation value){a.Kind=value.Kind;a.Start=value.Start;a.End=value.End;a.Color=value.Color;a.Width=value.Width;a.Text=value.Text;a.Points=new List<Point>(value.Points);a.Mosaic=value.Mosaic;}
        static bool SameAnnotation(Annotation a,Annotation b){return a.Kind==b.Kind&&a.Start==b.Start&&a.End==b.End&&a.Color==b.Color&&a.Width==b.Width&&a.Text==b.Text&&a.Points.SequenceEqual(b.Points);}
        void RememberAnnotation(Annotation mark,Annotation before,Annotation after,int index){
            RememberChange(new AnnotationChange {Mark=mark,Before=before,After=after,Index=index});
        }
        void RememberChange(AnnotationChange change){
            annotationUndo.Push(change);
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
            FinishShapeGesture();FinishTextGesture();
            if(annotationUndo.Count>0){var change=annotationUndo.Pop();ApplyAnnotationChange(change,false);annotationRedo.Push(change);}
            SelectText(null);last=null;PositionTools();surface.InvalidateVisual();
        }
        void RedoAnnotation(){
            FinishShapeGesture();FinishTextGesture();
            if(annotationRedo.Count>0){var change=annotationRedo.Pop();ApplyAnnotationChange(change,true);annotationUndo.Push(change);}
            SelectText(null);last=null;PositionTools();surface.InvalidateVisual();
        }
        void ClearAnnotationHistory(){annotationUndo.Clear();annotationRedo.Clear();redo.Clear();SelectText(null);}
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
            if(tool!="select"&&tool!="text")return null;
            // The screenshot's existing resize targets keep precedence at its edges.
            if(HitSelectionHandle(point)>=0)return null;
            var top=tool=="select"?TopAnnotation(point):null;if(top!=null&&top.Kind!="text")return null;
            int handle=HitTextHandle(point);if(handle>=0)return ResizeCursor(handle);
            return HitText(point)!=null?Cursors.SizeAll:null;
        }
        bool BeginTextPointer(Point point,int clickCount){
            if(tool!="select"&&tool!="text")return false;
            if(HitSelectionHandle(point)>=0)return false;
            var top=tool=="select"?TopAnnotation(point):null;if(top!=null&&top.Kind!="text")return false;
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
            bool left=textResize==0||textResize==6||textResize==7,right=textResize==2||textResize==3||textResize==4;
            bool top=textResize==0||textResize==1||textResize==2,bottom=textResize==4||textResize==5||textResize==6;
            // Use pointer displacement, not the padded frame's absolute coordinate:
            // merely pressing a tiny label's handle must never jump its font size.
            double horizontal=1+(left?down.X-point.X:point.X-down.X)/oldTextFrame.Width;
            double vertical=1+(top?down.Y-point.Y:point.Y-down.Y)/oldTextFrame.Height;
            double ratio=left||right?horizontal:vertical;if((left||right)&&(top||bottom))ratio=Math.Max(horizontal,vertical);
            double size=textDragBefore.TextFontSize;
            double largest=Math.Min(250/size,Math.Min(surface.Selection.Width/oldTextBounds.Width,surface.Selection.Height/oldTextBounds.Height));
            ratio=Geometry.Clamp(ratio,16/size,Math.Max(16/size,largest));textDrag.Width=size*ratio/5;
            var measured=textDrag.Bounds;
            double x=left?oldTextBounds.Right-measured.Width:right?oldTextBounds.Left:oldTextBounds.Left+(oldTextBounds.Width-measured.Width)/2;
            double y=top?oldTextBounds.Bottom-measured.Height:bottom?oldTextBounds.Top:oldTextBounds.Top+(oldTextBounds.Height-measured.Height)/2;
            textDrag.Start=ClampTextOrigin(new Point(x,y),measured);UpdateWidth();
        }
        void FinishTextGesture(){
            if(textDrag==null)return;
            var mark=textDrag;var before=textDragBefore;textDrag=null;textDragBefore=null;textResize=-1;dragging=false;surface.ReleaseMouseCapture();
            RecordAnnotationEdit(mark,before);PositionTools();UpdateWidth();surface.InvalidateVisual();
        }
        void StartText(Point point){StartText(point,null);}
        void StartText(Point point,Annotation mark){
            editingText=mark;textPosition=point;textEditorColor=mark!=null?mark.Color:AppState.Settings.Color;
            surface.EditingText=mark;SelectText(mark);
            double available=Math.Max(1,surface.Selection.Right-point.X);
            textEditor=new TextBox {Name="CaptureTextEditor",Text=mark!=null?mark.Text:"",MinWidth=Math.Min(140,available),MaxWidth=available,FontFamily=new System.Windows.Media.FontFamily("Microsoft YaHei UI"),FontSize=mark!=null?mark.TextFontSize:Math.Max(16,AppState.Settings.Stroke*5),Foreground=Ui.Brush(textEditorColor),Padding=new Thickness(0),BorderThickness=new Thickness(1),AcceptsReturn=true,TextWrapping=TextWrapping.NoWrap};
            Ui.Theme(textEditor,Control.BorderBrushProperty,"Primary");RefreshTextEditorBackground();Mori.Theme.Changed+=RefreshTextEditorBackground;
            Canvas.SetLeft(textEditor,point.X-1);Canvas.SetTop(textEditor,point.Y-1);overlay.Children.Add(textEditor);textEditor.Focus();textEditor.CaretIndex=textEditor.Text.Length;UpdateWidth();surface.InvalidateVisual();
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
                mark.Text=textEditor.Text;mark.Start=textPosition;mark.Width=textEditor.FontSize/5;mark.Color=textEditorColor;
                if(before==null){surface.Marks.Add(mark);RecordAnnotationAdded(mark);}else RecordAnnotationEdit(mark,before);
            }else if(mark!=null){RememberAnnotation(mark,CopyAnnotation(mark),null,surface.Marks.IndexOf(mark));surface.Marks.Remove(mark);mark=null;}
            RemoveTextEditor();SelectText(mark);PositionTools();surface.InvalidateVisual();
        }
        void CancelText(){if(textEditor==null)return;var mark=editingText;RemoveTextEditor();SelectText(mark);PositionTools();surface.InvalidateVisual();}
        void DeleteSelectedText(){var mark=surface.SelectedText;if(mark==null)return;RememberAnnotation(mark,CopyAnnotation(mark),null,surface.Marks.IndexOf(mark));surface.Marks.Remove(mark);SelectText(null);PositionTools();surface.InvalidateVisual();}
        void ApplyAnnotationColor(string color){
            AppState.Settings.Color=color;AppState.Settings.Save();
            if(textEditor!=null){textEditorColor=color;textEditor.Foreground=Ui.Brush(color);RefreshTextEditorBackground();}
            else if(SelectedAnnotation!=null||last!=null){var mark=SelectedAnnotation??last;var before=CopyAnnotation(mark);mark.Color=color;RecordAnnotationEdit(mark,before);}
            surface.InvalidateVisual();
        }
        void ChangeSelectedTextSize(double difference){
            var mark=surface.SelectedText;if(mark==null)return;var before=CopyAnnotation(mark);mark.Width=Geometry.Clamp(mark.TextFontSize+difference,16,250)/5;
            mark.Start=ClampTextOrigin(mark.Start,mark.Bounds);RecordAnnotationEdit(mark,before);UpdateWidth();surface.InvalidateVisual();
        }
    }
}
