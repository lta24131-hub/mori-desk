using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace QingJie {
    // One drawing surface and one animation clock, regardless of OCR word count.
    // The bitmap is only revealed after translation/layout have succeeded.
    internal sealed class ImageTranslationSweep : FrameworkElement {
        static readonly DependencyProperty ProgressProperty=DependencyProperty.Register("Progress",typeof(double),typeof(ImageTranslationSweep),new FrameworkPropertyMetadata(0d,FrameworkPropertyMetadataOptions.AffectsRender));
        readonly LinearGradientBrush beam;
        System.Windows.Media.Geometry textMask;
        BitmapSource result;
        Size sourceSize;
        Rect destination=Rect.Empty;
        TaskCompletionSource<bool> completion;
        bool running;
        int turns;bool flipX,flipY;
        internal bool IsRunning {get{return running;}}
        internal bool HasResult {get{return result!=null;}}
        internal ImageTranslationSweep(){
            IsHitTestVisible=false;Focusable=false;Visibility=Visibility.Collapsed;
            beam=new LinearGradientBrush(new GradientStopCollection {
                new GradientStop(Color.FromArgb(0,153,133,255),0),
                new GradientStop(Color.FromArgb(85,153,133,255),.44),
                new GradientStop(Color.FromArgb(135,225,219,255),.62),
                new GradientStop(Color.FromArgb(0,153,133,255),1)
            },new Point(0,0),new Point(1,0));beam.Freeze();
            Unloaded+=(s,e)=>Stop();
            IsVisibleChanged+=(s,e)=>{if(!running)return;if(!IsVisible){if(completion!=null)Finish();else BeginAnimation(ProgressProperty,null);}else if(completion==null)Animate(false);};
        }
        internal void Start(Size size,Rect bounds){
            Stop();sourceSize=size;destination=bounds;textMask=null;running=true;Visibility=Visibility.Visible;Animate(false);
        }
        internal void SetRegions(IEnumerable<TranslationRegion> regions){
            if(!running)return;
            var group=new GeometryGroup{FillRule=FillRule.Nonzero};
            foreach(var region in regions){
                var lines=region.SourceLines.Count>0?region.SourceLines.Select(l=>l.Box):new[]{region.Box};
                foreach(var box in lines){if(box.IsEmpty||box.Width<=0||box.Height<=0)continue;var padded=box;padded.Inflate(2,2);group.Children.Add(new RectangleGeometry(padded,2,2));}
            }
            group.Freeze();textMask=group;InvalidateVisual();
        }
        internal void SetOrientation(int quarterTurns,bool horizontal,bool vertical){turns=PinTransforms.Normalize(quarterTurns);flipX=horizontal;flipY=vertical;InvalidateVisual();}
        internal async Task RevealAsync(BitmapSource translated,CancellationToken cancel){
            cancel.ThrowIfCancellationRequested();if(!running)throw new OperationCanceledException();
            result=translated;completion=new TaskCompletionSource<bool>();var pending=completion;
            using(cancel.Register(()=>{if(Dispatcher.HasShutdownStarted)pending.TrySetCanceled();else Dispatcher.BeginInvoke(new Action(()=>{if(completion==pending)Stop();}));})){
                if(!IsVisible||!SystemParameters.ClientAreaAnimation)Finish();else Animate(true);
                await pending.Task;
            }
            cancel.ThrowIfCancellationRequested();
        }
        void Animate(bool reveal){
            BeginAnimation(ProgressProperty,null);SetValue(ProgressProperty,0d);
            if(!SystemParameters.ClientAreaAnimation||!IsVisible){InvalidateVisual();return;}
            var animation=new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(reveal?320:1350)) {RepeatBehavior=reveal?new RepeatBehavior(1):RepeatBehavior.Forever};
            Timeline.SetDesiredFrameRate(animation,30);
            if(reveal)animation.Completed+=(s,e)=>Finish();
            BeginAnimation(ProgressProperty,animation);
        }
        void Finish(){BeginAnimation(ProgressProperty,null);SetValue(ProgressProperty,1d);var pending=completion;completion=null;if(pending!=null)pending.TrySetResult(true);}
        internal void Stop(){
            running=false;BeginAnimation(ProgressProperty,null);var pending=completion;completion=null;result=null;textMask=null;Visibility=Visibility.Collapsed;
            if(pending!=null)pending.TrySetCanceled();InvalidateVisual();
        }
        protected override void OnRender(DrawingContext drawing){
            if(!running||sourceSize.Width<=0||sourceSize.Height<=0)return;
            var target=destination.IsEmpty?new Rect(0,0,ActualWidth,ActualHeight):destination;
            if(target.Width<=0||target.Height<=0)return;
            drawing.PushClip(new RectangleGeometry(target));
            var orientation=Matrix.Identity;orientation.Rotate(turns*90);
            var oriented=new MatrixTransform(orientation).TransformBounds(new Rect(new Point(),sourceSize));
            orientation.Translate(-oriented.Left,-oriented.Top);orientation.Scale(flipX?-1:1,flipY?-1:1);orientation.Translate(flipX?oriented.Width:0,flipY?oriented.Height:0);
            orientation.Scale(target.Width/oriented.Width,target.Height/oriented.Height);orientation.Translate(target.Left,target.Top);
            drawing.PushTransform(new MatrixTransform(orientation));
            double progress=(double)GetValue(ProgressProperty),width=sourceSize.Width;
            double band=Math.Max(20,Math.Min(160,width*.18)),x=progress*(width+2*band)-band;
            if(result!=null){
                drawing.PushClip(new RectangleGeometry(new Rect(0,0,Math.Max(0,Math.Min(width,x)),sourceSize.Height)));
                drawing.DrawImage(result,new Rect(new Point(),sourceSize));drawing.Pop();
            }
            // Before OCR resolves, use one quiet sweep across the selected image.
            // Once boxes exist only text is illuminated; no status paragraph.
            if(textMask!=null)drawing.PushClip(textMask);
            if(SystemParameters.ClientAreaAnimation) drawing.DrawRectangle(beam,null,new Rect(x-band,0,band*2,sourceSize.Height));
            else drawing.DrawRectangle(new SolidColorBrush(Color.FromArgb(30,153,133,255)),null,new Rect(new Point(),sourceSize));
            if(textMask!=null)drawing.Pop();drawing.Pop();drawing.Pop();
        }
    }
}
