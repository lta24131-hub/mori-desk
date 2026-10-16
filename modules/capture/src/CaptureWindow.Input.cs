using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace QingJie {
    public sealed partial class CaptureWindow {
        // Keep the actual event route and synthetic regression tests on the
        // same dispatcher. Never infer modifiers from the physical keyboard
        // after an event has already been interpreted.
        internal bool HandleCaptureKey(Key key,ModifierKeys modifiers){
            if(closed||captureOverlaySuspended||!AppState.CanEnterCapture(this))return false;
            if(collecting)return true;
            if(key==Key.Escape){CancelCaptureStep();return true;}
            if(textEditor!=null){
                if(key==Key.Enter&&modifiers==ModifierKeys.Control){CommitText();return true;}
                // Typing, selection, clipboard and undo belong to the editor.
                return false;
            }
            if(dragging)return true;
            if(key==Key.LeftCtrl||key==Key.RightCtrl){UpdateSnap(Mouse.GetPosition(surface));return false;}
            if(key==Key.Tab&&(modifiers==ModifierKeys.None||modifiers==ModifierKeys.Shift)&&surface.Selection.IsEmpty&&tool=="select"&&snapTargets!=null){
                CycleSnap(Mouse.GetPosition(surface),modifiers==ModifierKeys.Shift?-1:1);return true;
            }
            if(modifiers==ModifierKeys.Control||modifiers==(ModifierKeys.Control|ModifierKeys.Shift)){
                if(key==Key.Z){if((modifiers&ModifierKeys.Shift)!=0)Redo();else Undo();return true;}
                if(key==Key.Y&&modifiers==ModifierKeys.Control){Redo();return true;}
            }
            if(key==Key.Delete&&modifiers==ModifierKeys.None&&SelectedAnnotation!=null){DeleteSelectedAnnotation();return true;}
            if(SelectedAnnotation!=null&&(modifiers==ModifierKeys.None||modifiers==ModifierKeys.Shift)&&NudgeSelection(key,modifiers==ModifierKeys.Shift))return true;
            if(AdjustCropWithKeyboard(key,modifiers))return true;
            if(modifiers==ModifierKeys.None){
                string choice=CaptureToolKey(key);
                if(choice!=null&&!surface.Selection.IsEmpty){SetTool(choice);return true;}
            }
            // Do not open a modal error or run an export while still choosing
            // the crop. This also prevents accidental whole-screen exports.
            if(surface.Selection.IsEmpty)return false;
            if(key==Key.Enter&&modifiers==ModifierKeys.None){Copy();return true;}
            if(modifiers==ModifierKeys.Control){
                if(key==Key.S){Save();return true;}
                if(key==Key.C){Copy();return true;}
                if(key==Key.D&&MoriDesk.DeskRuntime.CollectImage!=null){CollectToLibrary();return true;}
            }
            return false;
        }
        static string CaptureToolKey(Key key){
            switch(key){
                case Key.V:return "select";
                case Key.R:return "rect";
                case Key.O:return "ellipse";
                case Key.A:return "arrow";
                case Key.L:return "line";
                case Key.P:return "pen";
                case Key.T:return "text";
                case Key.M:return "mosaic";
                default:return null;
            }
        }
        internal bool CanCompleteCaptureAt(Point point){
            return !closed&&!collecting&&!dragging&&textEditor==null&&tool=="select"&&!surface.Selection.IsEmpty&&surface.Selection.Contains(point)&&HitSelectionHandle(point)<0&&TopAnnotation(point)==null;
        }
        internal void CancelFromPointer(){if(!closed&&!collecting&&!captureOverlaySuspended&&AppState.CanEnterCapture(this))CancelCaptureStep();}
        static bool HasDrawingContent(Annotation mark){
            if(mark.Kind=="pen")return mark.Points.Any(point=>(point-mark.Start).LengthSquared>=4);
            return (mark.End-mark.Start).LengthSquared>=4;
        }
    }
}
