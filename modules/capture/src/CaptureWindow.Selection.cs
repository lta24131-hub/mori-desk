using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace QingJie {
    public sealed partial class CaptureWindow {
        sealed class CaptureSceneState {
            internal Rect Selection;
            internal Annotation[] Marks,Values;
        }
        CaptureSceneState selectionBefore;
        // Crop keyboard controls use logical coordinates, like pointer editing:
        // arrows move, Shift resizes the right/bottom edge, Ctrl uses a 10-DIP step.
        // A selected mark keeps its own keyboard behavior in the annotation path.
        internal bool AdjustCropWithKeyboard(Key key,ModifierKeys modifiers){
            if(!CanEditCapture||tool!="select"||dragging||textEditor!=null||surface.EditingText!=null||SelectedAnnotation!=null||surface.Draft!=null||surface.Selection.IsEmpty)return false;
            if((modifiers&~(ModifierKeys.Control|ModifierKeys.Shift))!=0)return false;
            double step=(modifiers&ModifierKeys.Control)!=0?10:1;
            var delta=new Vector(key==Key.Left?-step:key==Key.Right?step:0,key==Key.Up?-step:key==Key.Down?step:0);
            if(delta.LengthSquared==0)return false;
            var area=surface.Selection;
            if(area.Width<3||area.Height<3||area.Left<0||area.Top<0||area.Right>surface.ActualWidth||area.Bottom>surface.ActualHeight)return false;
            // Preserve the portion of every mark already visible in this crop.
            // Drawing bounds include thick strokes, arrowheads and text ink; a
            // coordinate-only Bounds check would still let those pixels be cut off.
            var protectedArea=VisibleAnnotationBounds(area);
            Rect adjusted;
            if((modifiers&ModifierKeys.Shift)!=0){
                double minWidth=Math.Max(3,protectedArea.IsEmpty?0:protectedArea.Right-area.Left),minHeight=Math.Max(3,protectedArea.IsEmpty?0:protectedArea.Bottom-area.Top);
                adjusted=new Rect(area.Location,new Size(Geometry.Clamp(area.Width+delta.X,minWidth,surface.ActualWidth-area.Left),Geometry.Clamp(area.Height+delta.Y,minHeight,surface.ActualHeight-area.Top)));
            }else{
                double minX=-area.Left,maxX=surface.ActualWidth-area.Right,minY=-area.Top,maxY=surface.ActualHeight-area.Bottom;
                if(!protectedArea.IsEmpty){minX=Math.Max(minX,protectedArea.Right-area.Right);maxX=Math.Min(maxX,protectedArea.Left-area.Left);minY=Math.Max(minY,protectedArea.Bottom-area.Bottom);maxY=Math.Min(maxY,protectedArea.Top-area.Top);}
                adjusted=area;adjusted.Offset(Geometry.Clamp(delta.X,minX,maxX),Geometry.Clamp(delta.Y,minY,maxY));
            }
            // Consume a valid arrow even at a boundary, without adding no-op
            // history entries or invalidating a still-correct translation.
            if(adjusted==area)return true;
            var before=CaptureScene();surface.Selection=adjusted;ClearSnap();ResetTranslation();RememberScene(before);PositionTools();surface.InvalidateVisual();return true;
        }
        Rect VisibleAnnotationBounds(Rect area){
            var visible=Rect.Empty;
            foreach(var mark in surface.Marks){
                var drawing=new DrawingGroup();using(var context=drawing.Open())mark.Draw(context);
                var bounds=drawing.Bounds;bounds.Intersect(area);if(!bounds.IsEmpty)visible.Union(bounds);
            }
            return visible;
        }
        int ActiveSelectionHandle(Point point){
            if(!dragging)return HitSelectionHandle(point);
            if(resize<0||resize%2!=0||oldSelection.IsEmpty||oldSelection.Width<=0||oldSelection.Height<=0)return resize;
            // The crop geometry normalizes crossed edges. Its corner cursor must
            // follow the same physical corner, not the original pressed index.
            bool left=resize==0||resize==6,top=resize==0||resize==2;
            double anchorX=left?oldSelection.Right:oldSelection.Left,anchorY=top?oldSelection.Bottom:oldSelection.Top;
            double x=Geometry.Clamp((left?oldSelection.Left:oldSelection.Right)+point.X-down.X,0,surface.ActualWidth),y=Geometry.Clamp((top?oldSelection.Top:oldSelection.Bottom)+point.Y-down.Y,0,surface.ActualHeight);
            bool currentLeft=x==anchorX?left:x<anchorX,currentTop=y==anchorY?top:y<anchorY;
            return currentTop?(currentLeft?0:2):(currentLeft?6:4);
        }
        CaptureSceneState CaptureScene(){return new CaptureSceneState {Selection=surface.Selection,Marks=surface.Marks.ToArray(),Values=surface.Marks.Select(CopyAnnotation).ToArray()};}
        void RestoreScene(CaptureSceneState scene){
            surface.Selection=scene.Selection;surface.Marks.Clear();
            for(int i=0;i<scene.Marks.Length;i++){RestoreAnnotation(scene.Marks[i],scene.Values[i]);surface.Marks.Add(scene.Marks[i]);}
            ResetTranslation();SelectText(null);last=null;
        }
        void RememberScene(CaptureSceneState before){
            if(before==null||before.Selection==surface.Selection&&before.Marks.SequenceEqual(surface.Marks))return;
            RememberChange(new AnnotationChange {SceneBefore=before,SceneAfter=CaptureScene()});
        }
        void FinishSelectionGesture(){
            if(selectingNew){
                var chosen=snap.End(surface.Selection);
                // A tap outside the crop (or an invalid drag) is not a destructive
                // "new screenshot" command. Retain pixels, marks and undo history.
                if(selectionBefore!=null&&!selectionBefore.Selection.IsEmpty&&(!snap.HasDragged||chosen.IsEmpty||chosen.Width<3||chosen.Height<3))RestoreScene(selectionBefore);
                else {surface.Selection=chosen;if(!chosen.IsEmpty&&selectionBefore!=null&&!selectionBefore.Selection.IsEmpty)surface.Marks.Clear();}
                selectingNew=false;
            }
            if(!surface.Selection.IsEmpty&&(surface.Selection.Width<3||surface.Selection.Height<3)){
                if(selectionBefore!=null&&!selectionBefore.Selection.IsEmpty)RestoreScene(selectionBefore);else surface.Selection=Rect.Empty;
            }
            if(selectionBefore!=null&&selectionBefore.Selection!=surface.Selection)ResetTranslation();
            RememberScene(selectionBefore);selectionBefore=null;
        }
        void CancelCaptureStep(){
            if(textEditor!=null){CancelText();return;}
            if(dragging){
                if(shapeDrag!=null)RestoreAnnotation(shapeDrag,shapeBefore);
                if(textDrag!=null)RestoreAnnotation(textDrag,textDragBefore);
                if(selectionBefore!=null)RestoreScene(selectionBefore);
                shapeDrag=null;shapeBefore=null;shapeHandle=-1;textDrag=null;textDragBefore=null;textResize=-1;
                selectionBefore=null;selectingNew=false;surface.Draft=null;dragging=false;surface.ReleaseMouseCapture();PositionTools();surface.InvalidateVisual();return;
            }
            if(SelectedAnnotation!=null){SelectText(null);last=null;SetTool("select");return;}
            if(tool!="select"){SetTool("select");return;}
            if(!surface.Selection.IsEmpty){var before=CaptureScene();surface.Selection=Rect.Empty;surface.Marks.Clear();ResetTranslation();RememberScene(before);PositionTools();surface.InvalidateVisual();return;}
            AppState.CancelCapture();
        }
    }
}
