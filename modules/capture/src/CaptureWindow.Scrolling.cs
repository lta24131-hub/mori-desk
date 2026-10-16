using System;
using System.Windows;
using System.Windows.Media.Imaging;

namespace QingJie {
    public sealed partial class CaptureWindow {
        ExternalScrollCaptureSession scrollingCapture;
        internal bool IsScrollingCapture {get{return scrollingCapture!=null;}}
        internal static bool AcceptCaptureWheel(bool isClosed,bool isCollecting,bool isSuspended,bool hasExternalSession){return !isClosed&&!isCollecting&&!isSuspended&&!hasExternalSession;}
        internal static System.Drawing.Rectangle ScrollRegion(Snapshot snapshot,Rect selection,double scaleX,double scaleY){
            if(snapshot==null||snapshot.Image==null||selection.IsEmpty||selection.Width<3||selection.Height<3)throw new ArgumentException("请先选择截图区域。");
            if(double.IsNaN(scaleX)||double.IsInfinity(scaleX)||scaleX<=0||double.IsNaN(scaleY)||double.IsInfinity(scaleY)||scaleY<=0)throw new ArgumentException("截图缩放比例无效，请重新截图。");
            var crop=Geometry.PixelCrop(selection,scaleX,scaleY,snapshot.Image.PixelWidth,snapshot.Image.PixelHeight);
            return new System.Drawing.Rectangle(snapshot.Bounds.X+crop.X,snapshot.Bounds.Y+crop.Y,crop.Width,crop.Height);
        }
        async void BeginScrollingCapture(){
            if(closed||collecting||captureOverlaySuspended||dragging||scrollingCapture!=null||!HasCaptureRegion)return;
            ExternalScrollCaptureSession session=null;
            try{
                FinishShapeGesture();FinishTextGesture();CommitText();
                if(surface.Draft!=null){
                    if(HasDrawingContent(surface.Draft)){if(surface.Draft.Kind=="mosaic")surface.PrepareMosaic(surface.Draft);surface.Marks.Add(surface.Draft);RecordAnnotationAdded(surface.Draft);}
                    surface.Draft=null;
                }
                var region=ScrollRegion(shot,surface.Selection,surface.ScaleX,surface.ScaleY);
                var marks=ExternalScrollCaptureSession.ConvertAnnotations(surface.Marks,surface.Selection,surface.ScaleX,surface.ScaleY,region.X-shot.Bounds.X,region.Y-shot.Bounds.Y);
                session=new ExternalScrollCaptureSession(AppDomain.CurrentDomain.BaseDirectory,region,shot.Bounds,surface.ScaleX,marks);
                scrollingCapture=session;
                // Keep the frozen source, selection, original marks and history
                // alive until the helper has committed a verified final result.
                // A startup error/crash returns to this exact editing overlay.
                SuspendCaptureOverlay();
                var result=await session.RunAsync();
                if(closed||scrollingCapture!=session||AppState.Quitting)return;
                if(result.status=="cancelled"){scrollingCapture=null;session.Dispose();session.CleanupCompletedFiles(result);Close();return;}
                if(result.status=="error")throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.message)?"滚动截屏未完成，原截图和标注已保留。":result.message);
                var image=await System.Threading.Tasks.Task.Run(()=>session.LoadImage(result));
                if(closed||scrollingCapture!=session||AppState.Quitting)return;
                bool completed=false;string failure=null;
                try{
                    if(result.action=="copy"){ImageFiles.Copy(image);completed=true;}
                    else if(result.action=="save")completed=ImageFiles.Save(image,this);
                    else if(result.action=="pin"){
                        if(AppState.Pins==null)throw new InvalidOperationException("贴图服务暂不可用。");
                        AppState.Pins.New(image);completed=true;
                    }else if(result.action=="collect"){
                        var collect=MoriDesk.DeskRuntime.CollectImage;if(collect==null)throw new InvalidOperationException("素材库暂不可用。");
                        collecting=true;try{completed=await collect(image);}finally{collecting=false;}
                    }
                }catch(Exception error){failure=error.Message;}
                if(closed||scrollingCapture!=session||AppState.Quitting)return;
                if(!completed){
                    // Save cancelled or an export failed: never discard the
                    // finished long image, and do not retake the live desktop.
                    var preview=new ScrollCapturePreviewWindow(image);preview.Show();
                    if(!string.IsNullOrWhiteSpace(failure))AppState.Notify("未完成："+failure+"；长截图已保留，可在预览中重试。");
                }
                scrollingCapture=null;session.Dispose();if(completed)session.CleanupCompletedFiles(result,true);Close();
            }catch(Exception ex){
                if(!closed&&(session==null||scrollingCapture==session)){
                    if(scrollingCapture==session)scrollingCapture=null;
                    if(session!=null)session.Dispose();ResumeCaptureOverlay();FocusCaptureOverlay();TranslationStatus("无法完成滚动截屏："+ex.Message);
                }
            }finally{if(session!=null)session.Dispose();}
        }
        void DisposeScrollingCapture(){var session=scrollingCapture;scrollingCapture=null;if(session!=null)session.Dispose();}
    }
}
