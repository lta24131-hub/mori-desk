using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace QingJie {
    public sealed partial class CaptureWindow {
        readonly DispatcherTimer strokeSaveTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(300)};
        Preferences pendingStrokeSettings;
        object wheelTarget;
        string wheelKind;
        bool wheelFine;
        int wheelRemainder,wheelTick;
        AnnotationChange wheelHistory;
        double CurrentCaptureSize {get{return textEditor!=null?textEditor.FontSize:surface.SelectedText!=null?surface.SelectedText.TextFontSize:surface.SelectedAnnotation!=null?surface.SelectedAnnotation.Width:tool=="text"?AppState.Settings.TextSize:AppState.Settings.Stroke;}}
        void ResetStrokeWheel(){wheelTarget=null;wheelKind=null;wheelRemainder=0;wheelHistory=null;}
        void QueueStrokeSettings(){
            if(pendingStrokeSettings!=null&&pendingStrokeSettings!=AppState.Settings)FlushStrokeSettings();
            pendingStrokeSettings=AppState.Settings;strokeSaveTimer.Stop();strokeSaveTimer.Start();
        }
        void FlushStrokeSettings(){strokeSaveTimer.Stop();var settings=pendingStrokeSettings;pendingStrokeSettings=null;if(settings!=null)settings.Save();}
        bool AdjustStrokeWheel(int delta,ModifierKeys modifiers,Point point){
            bool fine=modifiers==(ModifierKeys.Control|ModifierKeys.Shift);
            if((modifiers!=ModifierKeys.Control&&!fine)||surface.Selection.IsEmpty||!HasCaptureProperties||shapeDrag!=null||textDrag!=null||selectionBefore!=null){ResetStrokeWheel();return false;}
            object target=(object)textEditor??SelectedAnnotation??(object)AppState.Settings;
            int tick=Environment.TickCount;string kind=CapturePropertyKind;
            if(target!=wheelTarget||kind!=wheelKind||fine!=wheelFine||unchecked((uint)(tick-wheelTick))>600||wheelRemainder!=0&&Math.Sign(wheelRemainder)!=Math.Sign(delta))ResetStrokeWheel();
            wheelTarget=target;wheelKind=kind;wheelFine=fine;wheelTick=tick;
            // WPF wheel units are 120 per notch. Sub-notch touchpad/high-resolution
            // events accumulate; a multi-notch event retains its actual distance.
            long total=(long)wheelRemainder+delta;int steps=(int)(total/120);wheelRemainder=(int)(total%120);
            if(steps!=0)AdjustCaptureSize(steps*(fine?.5:1),steps*(fine?1:2),true);
            surface.SizeHintPoint=point;surface.SizeHint=(kind=="text"?"字号 ":"粗细 ")+CurrentCaptureSize.ToString("0.#")+((CurrentCaptureSize<=(kind=="text"?16:1))?" · 最小":CurrentCaptureSize>=(kind=="text"?250:25)?" · 最大":"");
            surface.ShowBrushSize=true;cursorLabelTimer.Stop();cursorLabelTimer.Start();UpdateBrushCursor(point);surface.InvalidateVisual();return true;
        }
        bool CanMergeStrokeEdit(Annotation mark){return wheelHistory!=null&&wheelHistory.Mark==mark&&annotationUndo.Count>0&&ReferenceEquals(annotationUndo.Peek(),wheelHistory);}
        void RecordStrokeEdit(Annotation mark,Annotation before,bool wheel){
            if(wheel&&CanMergeStrokeEdit(mark)){
                // Only width and the clamped text origin change during this
                // gesture. Keep the private history snapshot's point buffer.
                wheelHistory.After.Width=mark.Width;wheelHistory.After.Start=mark.Start;
                if(SameAnnotation(wheelHistory.Before,wheelHistory.After)){annotationUndo.Pop();wheelHistory=null;}
                annotationRedo.Clear();redo.Clear();RefreshCaptureToolbarHistory();
            }else{RecordAnnotationEdit(mark,before);if(wheel)wheelHistory=annotationUndo.Peek();}
        }
        void AdjustCaptureSize(double strokeDelta,double textDelta,bool wheel){
            if(!CanEditCapture)return;
            if(textEditor!=null){textEditor.FontSize=Geometry.Clamp(textEditor.FontSize+textDelta,16,250);UpdateTextEditorPlacement();UpdateWidth();return;}
            var mark=SelectedAnnotation;bool text=CapturePropertyKind=="text";
            if(CapturePropertyKind=="mosaic")return;
            double beforeValue=CurrentCaptureSize,value=Geometry.Clamp(beforeValue+(text?textDelta:strokeDelta),text?16:1,text?250:25);
            if(value==beforeValue)return;
            if(mark!=null){
                var before=wheel&&CanMergeStrokeEdit(mark)?null:CopyAnnotation(mark);mark.Width=text?value/5:value;
                if(text)mark.Start=ClampTextOrigin(mark.Start,mark.Bounds);
                RecordStrokeEdit(mark,before,wheel);
            }else{
                if(text)AppState.Settings.TextSize=value;else AppState.Settings.Stroke=value;
                if(surface.Draft!=null)surface.Draft.Width=value;
                QueueStrokeSettings();
            }
            UpdateWidth();surface.InvalidateVisual();
        }
    }
}
