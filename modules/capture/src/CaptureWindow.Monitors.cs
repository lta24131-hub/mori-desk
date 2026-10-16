using System;
using System.Collections.Generic;
using System.Windows;

namespace QingJie {
    // Monitor rectangles are native desktop pixels, including negative origins.
    // Do not compare them with WPF logical coordinates or primary-screen bounds.
    internal static class CaptureMonitorGeometry {
        internal static CaptureWindow AtPoint(IEnumerable<CaptureWindow> windows,System.Drawing.Point point){
            CaptureWindow nearest=null;double distance=double.PositiveInfinity;
            foreach(var window in windows){
                var bounds=window.CaptureScreenBounds;if(bounds.Width<=0||bounds.Height<=0)continue;
                if(bounds.Contains(point))return window;
                double dx=Math.Max(Math.Max((double)bounds.Left-point.X,(double)point.X-bounds.Right),0);
                double dy=Math.Max(Math.Max((double)bounds.Top-point.Y,(double)point.Y-bounds.Bottom),0);
                double current=dx*dx+dy*dy;if(current<distance){distance=current;nearest=window;}
            }
            return nearest;
        }
    }
    public sealed partial class CaptureWindow {
        bool captureOverlaySuspended,resumeCaptureVisible;
        internal System.Drawing.Rectangle CaptureScreenBounds {get{return shot.Bounds;}}
        internal bool HasCaptureGesture {get{return dragging||textEditor!=null||collecting;}}
        internal bool HasCaptureRegion {get{return !surface.Selection.IsEmpty&&surface.Selection.Width>=3&&surface.Selection.Height>=3;}}
        internal bool IsCaptureOverlaySuspended {get{return captureOverlaySuspended;}}
        internal void SuspendCaptureOverlay(){
            if(closed||captureOverlaySuspended)return;
            captureOverlaySuspended=true;resumeCaptureVisible=IsVisible;ClearSnap();
            // Inert regression fixtures are never made native or visible.
            if(resumeCaptureVisible)Hide();
        }
        internal void ResumeCaptureOverlay(){
            if(closed||!captureOverlaySuspended)return;
            captureOverlaySuspended=false;
            if(resumeCaptureVisible){resumeCaptureVisible=false;Show();}
        }
        internal void FocusCaptureOverlay(){
            if(closed||captureOverlaySuspended||!IsVisible)return;
            if(!IsActive)Activate();
            if(textEditor!=null)textEditor.Focus();else Focus();
        }
    }
}
