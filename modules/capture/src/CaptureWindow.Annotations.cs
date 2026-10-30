using System;
using System.Windows;
using System.Windows.Input;

namespace QingJie {
    public sealed partial class CaptureWindow {
        Annotation shapeDrag,shapeBefore;
        int shapeHandle=-1;
        Annotation SelectedAnnotation {get{return surface.SelectedText??surface.SelectedAnnotation;}}
        void SelectShape(Annotation mark){SelectText(null);surface.SelectedAnnotation=mark;if(mark!=null)last=mark;UpdateWidth();PositionTools();surface.InvalidateVisual();}
        Annotation TopAnnotation(Point point){
            if(surface.Selection.IsEmpty||!surface.Selection.Contains(point))return null;
            for(int i=surface.Marks.Count-1;i>=0;i--)if(CaptureAnnotationGeometry.Hit(surface.Marks[i],point))return surface.Marks[i];
            return null;
        }
        Cursor ShapePointerCursor(Point point){
            if(shapeDrag!=null)return shapeHandle<0?Cursors.SizeAll:CaptureAnnotationGeometry.Linear(shapeDrag)?Cursors.Cross:ResizeCursor(shapeHandle);
            if(HitSelectionHandle(point)>=0)return null;
            var selected=surface.SelectedAnnotation;int handle=CaptureAnnotationGeometry.HandleAt(selected,point);
            if(handle>=0)return CaptureAnnotationGeometry.Linear(selected)?Cursors.Cross:ResizeCursor(handle);
            var hit=TopAnnotation(point);return hit!=null&&hit.Kind!="text"?Cursors.SizeAll:null;
        }
        bool BeginShapePointer(Point point){
            if(HitSelectionHandle(point)>=0)return false;
            int handle=CaptureAnnotationGeometry.HandleAt(surface.SelectedAnnotation,point);
            var mark=handle>=0?surface.SelectedAnnotation:TopAnnotation(point);if(mark==null||mark.Kind=="text")return false;
            SelectShape(mark);shapeDrag=mark;shapeBefore=CopyAnnotation(mark);shapeHandle=handle;dragging=true;
            HideTools();surface.CaptureMouse();UpdateBrushCursor(point);return true;
        }
        void MoveSelectedShape(Point point){
            if(shapeDrag==null)return;
            if(shapeHandle<0)CaptureAnnotationGeometry.Move(shapeDrag,shapeBefore,point-down,surface.Selection);
            else CaptureAnnotationGeometry.Resize(shapeDrag,shapeBefore,shapeHandle,point-down,surface.Selection);
            surface.InvalidateVisual();
        }
        void FinishShapeGesture(){
            if(shapeDrag==null)return;
            var mark=shapeDrag;var before=shapeBefore;shapeDrag=null;shapeBefore=null;shapeHandle=-1;dragging=false;surface.ReleaseMouseCapture();
            if(mark.Kind=="mosaic"&&!SameAnnotation(mark,before))surface.PrepareMosaic(mark);
            RecordAnnotationEdit(mark,before);UpdateWidth();PositionTools();surface.InvalidateVisual();
        }
        void DeleteSelectedAnnotation(){
            if(surface.SelectedText!=null){DeleteSelectedText();return;}
            var mark=surface.SelectedAnnotation;if(mark==null)return;
            RememberAnnotation(mark,CopyAnnotation(mark),null,surface.Marks.IndexOf(mark));surface.Marks.Remove(mark);SelectShape(null);last=null;
        }
        void ChangeStroke(double difference){ResetStrokeWheel();AdjustCaptureSize(difference,difference*5,false);}
        bool NudgeSelection(Key key,bool large){
            if(dragging||surface.Selection.IsEmpty)return false;
            double amount=large?10:1;var delta=new Vector(key==Key.Left?-amount:key==Key.Right?amount:0,key==Key.Up?-amount:key==Key.Down?amount:0);
            if(delta.LengthSquared==0)return false;
            var mark=SelectedAnnotation;
            if(mark!=null){
                var before=CopyAnnotation(mark);
                if(mark.Kind=="text")mark.Start=ClampTextOrigin(mark.Start+delta,mark.Bounds);
                else CaptureAnnotationGeometry.Move(mark,before,delta,surface.Selection);
                if(mark.Kind=="mosaic"&&!SameAnnotation(mark,before))surface.PrepareMosaic(mark);
                RecordAnnotationEdit(mark,before);
            }else{
                var before=CaptureScene();var area=surface.Selection;
                delta=new Vector(Geometry.Clamp(delta.X,-area.Left,surface.ActualWidth-area.Right),Geometry.Clamp(delta.Y,-area.Top,surface.ActualHeight-area.Bottom));
                if(delta.LengthSquared>0){surface.Selection.Offset(delta);ResetTranslation();RememberScene(before);}
            }
            PositionTools();surface.InvalidateVisual();return true;
        }
    }
}
