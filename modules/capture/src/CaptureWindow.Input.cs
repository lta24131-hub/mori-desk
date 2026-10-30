using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace QingJie {
    public sealed partial class CaptureWindow {
        // The old editor remains visible while the scrolling helper prepares.
        // Disabling buttons is presentation only: queued/routed input and direct
        // command callbacks must observe the same ownership boundary.
        internal bool CanEditCapture {get{return !closed&&!collecting&&!captureOverlaySuspended&&!scrollingPreparationUi&&scrollingCapture==null;}}
        // Keep the actual event route and synthetic regression tests on the
        // same dispatcher. Never infer modifiers from the physical keyboard
        // after an event has already been interpreted.
        internal bool HandleCaptureKey(Key key,ModifierKeys modifiers){
            if(closed||!AppState.CanEnterCapture(this))return false;
            // Escape is always the one-press emergency exit, even during text
            // editing, pointer gestures, translation or a submitted collection.
            // Closing (not committing) discards unfinished annotations and ends
            // only the monitor session containing this window. A collection
            // already handed to the library may finish, but its closed-window
            // continuation cannot reopen this overlay or affect a newer capture.
            if(key==Key.Escape){Close();return true;}
            if(!CanEditCapture)return true;
            if(textEditor!=null){
                if(key==Key.Enter&&modifiers==ModifierKeys.Control){CommitText();return true;}
                // Save/collect are explicit screenshot commands even while a
                // caption is being typed. Their normal output transaction will
                // commit that caption first and retain it if output fails.
                // Never intercept the editor's copy/cut/paste/select/undo keys.
                if(modifiers==ModifierKeys.Control&&!surface.Selection.IsEmpty){
                    if(key==Key.S){Save();return true;}
                    if(key==Key.D&&captureFeatures.Collect&&MoriDesk.DeskRuntime.CollectImage!=null){CollectToLibrary();return true;}
                }
                // Typing, selection, clipboard and undo belong to the editor.
                return false;
            }
            if(dragging)return true;
            if(key==Key.LeftCtrl||key==Key.RightCtrl){UpdateSnap(Mouse.GetPosition(surface));return false;}
            if(key==Key.Tab&&captureFeatures.SmartSelection&&(modifiers==ModifierKeys.None||modifiers==ModifierKeys.Shift)&&surface.Selection.IsEmpty&&tool=="select"&&snapTargets!=null){
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
                if(choice!=null&&!surface.Selection.IsEmpty&&captureFeatures.AllowsTool(choice)){SetTool(choice);return true;}
            }
            // Do not open a modal error or run an export while still choosing
            // the crop. This also prevents accidental whole-screen exports.
            if(surface.Selection.IsEmpty)return false;
            if(key==Key.Enter&&modifiers==ModifierKeys.None){Copy();return true;}
            if(modifiers==ModifierKeys.Control){
                if(key==Key.S){Save();return true;}
                if(key==Key.C){Copy();return true;}
                if(key==Key.D&&captureFeatures.Collect&&MoriDesk.DeskRuntime.CollectImage!=null){CollectToLibrary();return true;}
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
            return CanEditCapture&&!dragging&&textEditor==null&&tool=="select"&&!surface.Selection.IsEmpty&&surface.Selection.Contains(point)&&HitSelectionHandle(point)<0&&TopAnnotation(point)==null;
        }
        internal void CancelFromPointer(){
            if(closed||captureOverlaySuspended||!AppState.CanEnterCapture(this))return;
            // Preparing is not another editing step: do not clear the frozen
            // crop or marks while an external session still owns their copy.
            if(scrollingCapture!=null||scrollingPreparationUi){Close();return;}
            // A right click beside a caption means finish typing. Keep the text
            // selected for immediate adjustment; Undo can reverse the whole edit.
            // Escape remains the explicit one-press exit for the capture.
            if(CanEditCapture&&textEditor!=null){SetTool("select");return;}
            if(CanEditCapture)CancelCaptureStep();
        }
        static bool HasDrawingContent(Annotation mark){
            if(mark.Kind=="pen")return mark.Points.Any(point=>(point-mark.Start).LengthSquared>=4);
            return (mark.End-mark.Start).LengthSquared>=4;
        }
    }
}
