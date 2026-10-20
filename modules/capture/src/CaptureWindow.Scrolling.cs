using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace QingJie {
    public sealed partial class CaptureWindow {
        ExternalScrollCaptureSession scrollingCapture;
        internal long CaptureGeneration;
        internal Action<ScrollCapturePreviewWindow> PresentScrollRecovery;
        internal Func<ExternalScrollCaptureSession,Func<ExternalScrollResult,Task<ExternalScrollExportReply>>,Func<Task<bool>>,Task<ExternalScrollResult>> RunScrollSession=(session,output,handoff)=>session.RunAsync(output,handoff);
        internal bool IsScrollingCapture {get{return scrollingCapture!=null;}}
        internal static bool AcceptCaptureWheel(bool isClosed,bool isCollecting,bool isSuspended,bool hasExternalSession){return !isClosed&&!isCollecting&&!isSuspended&&!hasExternalSession;}
        internal static System.Drawing.Rectangle ScrollRegion(Snapshot snapshot,Rect selection,double scaleX,double scaleY){
            if(snapshot==null||snapshot.Image==null||selection.IsEmpty||selection.Width<3||selection.Height<3)throw new ArgumentException("请先选择截图区域。");
            if(double.IsNaN(scaleX)||double.IsInfinity(scaleX)||scaleX<=0||double.IsNaN(scaleY)||double.IsInfinity(scaleY)||scaleY<=0)throw new ArgumentException("截图缩放比例无效，请重新截图。");
            var crop=Geometry.PixelCrop(selection,scaleX,scaleY,snapshot.Image.PixelWidth,snapshot.Image.PixelHeight);
            return new System.Drawing.Rectangle(snapshot.Bounds.X+crop.X,snapshot.Bounds.Y+crop.Y,crop.Width,crop.Height);
        }
        async void BeginScrollingCapture(){
            if(!captureFeatures.Scrolling||closed||collecting||captureOverlaySuspended||dragging||scrollingCapture!=null||!HasCaptureRegion)return;
            ExternalScrollCaptureSession session=null;
            var timing=ScrollCaptureStartupTiming.Start();ScrollCaptureStartupTiming.Mark(timing,ScrollCaptureStartupStage.Click);
            Exception sessionFailure=null;
            try{
            try{
                FinishShapeGesture();FinishTextGesture();CommitText();
                if(surface.Draft!=null){
                    if(HasDrawingContent(surface.Draft)){if(surface.Draft.Kind=="mosaic")surface.PrepareMosaic(surface.Draft);surface.Marks.Add(surface.Draft);RecordAnnotationAdded(surface.Draft);}
                    surface.Draft=null;
                }
                var region=ScrollRegion(shot,surface.Selection,surface.ScaleX,surface.ScaleY);
                var marks=ExternalScrollCaptureSession.ConvertAnnotations(surface.Marks,surface.Selection,surface.ScaleX,surface.ScaleY,region.X-shot.Bounds.X,region.Y-shot.Bounds.Y);
                session=new ExternalScrollCaptureSession(AppDomain.CurrentDomain.BaseDirectory,region,shot.Bounds,surface.ScaleX,marks,captureFeatures,AppState.ClaimScrollWarm(this),timing);
                ResetTranslation();
                scrollingCapture=session;
                // Keep the frozen source, selection, original marks and history
                // alive until the helper has committed a verified final result.
                // A startup error/crash returns to this exact editing overlay.
                // Keep the selection visible during the Electron cold start.
                // Only a validated prepared message may hide it; the helper
                // must wait for our start acknowledgement before sampling.
                SetScrollingPreparationUi(true);
                TranslationStatus("正在准备长截图… 按 Esc 取消");
                var result=await RunScrollSession(session,output=>Dispatcher.InvokeAsync(()=>ExportScrollingImage(session,output)).Task.Unwrap(),
                    ()=>Dispatcher.InvokeAsync(()=>HandoffScrollingCaptureAsync(session)).Task.Unwrap());
                if(closed||scrollingCapture!=session||AppState.Quitting)return;
                if(result.status=="cancelled"){scrollingCapture=null;session.Dispose();session.CleanupCompletedFiles(result);Close();return;}
                if(result.status=="error")throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.message)?"滚动截屏未完成，原截图和标注已保留。":result.message);
                if(result.exportId>0){
                    // The helper only commits after this parent confirmed the
                    // output. Do not save/copy twice when its terminal arrives.
                    scrollingCapture=null;session.Dispose();session.CleanupCompletedFiles(result,true);Close();return;
                }
                var image=await System.Threading.Tasks.Task.Run(()=>session.LoadImage(result));
                if(closed||scrollingCapture!=session||AppState.Quitting)return;
                bool completed=false;string failure=null;
                try{
                    if(result.action=="copy"){ImageFiles.Copy(image);completed=true;}
                    else if(result.action=="save")completed=ImageFiles.Save(image,this);
                    else if(result.action=="pin"&&captureFeatures.Pin){
                        if(AppState.Pins==null)throw new InvalidOperationException("贴图服务暂不可用。");
                        AppState.Pins.New(image);completed=true;
                    }else if(result.action=="collect"&&captureFeatures.Collect){
                        var collect=MoriDesk.DeskRuntime.CollectImage;if(collect==null)throw new InvalidOperationException("素材库暂不可用。");
                        collecting=true;try{completed=await collect(image);}finally{collecting=false;}
                    }
                }catch(Exception error){failure=error.Message;}
                if(closed||scrollingCapture!=session||AppState.Quitting)return;
                if(!completed){
                    // Save cancelled or an export failed: never discard the
                    // finished long image, and do not retake the live desktop.
                    var preview=new ScrollCapturePreviewWindow(image,captureFeatures);preview.Show();
                    if(!string.IsNullOrWhiteSpace(failure))AppState.Notify("未完成："+failure+"；长截图已保留，可在预览中重试。");
                }
                scrollingCapture=null;session.Dispose();if(completed)session.CleanupCompletedFiles(result,true);Close();
            }catch(Exception ex){
                sessionFailure=ex;
            }
            if(sessionFailure!=null)await HandleScrollingFailureAsync(session,sessionFailure);
            }finally{if(session!=null)session.Dispose();}
        }
        internal async Task<bool> HandoffScrollingCaptureAsync(ExternalScrollCaptureSession session){
            if(closed||AppState.Quitting||session==null||scrollingCapture!=session||!session.CanReleaseStart)return false;
            SuspendCaptureOverlay();
            // One bounded composition settle, not a per-frame sleep. The
            // first live frame must not contain the frozen screenshot mask.
            await Task.Delay(40);
            return !closed&&!AppState.Quitting&&scrollingCapture==session&&session.CanReleaseStart&&captureOverlaySuspended;
        }
        internal async Task HandleScrollingFailureAsync(ExternalScrollCaptureSession session,Exception error){
            if(closed||AppState.Quitting||(session!=null&&scrollingCapture!=session))return;
            var recovery=session==null?null:session.RecoverySnapshot();
            if(recovery!=null&&recovery.CompletedExportId>0){
                // The destination already committed. A failed reply/crashed
                // helper must never prompt a second Save/Copy/Collect.
                scrollingCapture=null;session.Dispose();Close();return;
            }
            if(recovery!=null&&recovery.VerifiedImage!=null){
                ScrollCapturePreviewWindow preview=null;
                try{
                    var image=await Task.Run(()=>session.LoadImage(recovery.VerifiedImage));
                    if(closed||AppState.Quitting||scrollingCapture!=session)return;
                    preview=new ScrollCapturePreviewWindow(image,captureFeatures);
                    if(PresentScrollRecovery!=null)PresentScrollRecovery(preview);else preview.Show();
                    if(closed||AppState.Quitting||scrollingCapture!=session){preview.Close();return;}
                    // Transfer ownership only once a complete long image has
                    // been presented. Failure keeps the exact source editor.
                    scrollingCapture=null;session.Dispose();Close();return;
                }catch(Exception recoveryError){
                    if(preview!=null)preview.Close();
                    error=new InvalidOperationException(error.Message+"；长截图恢复失败："+recoveryError.Message,recoveryError);
                }
            }
            if(closed||AppState.Quitting||(session!=null&&scrollingCapture!=session))return;
            if(scrollingCapture==session)scrollingCapture=null;
            if(session!=null)session.Dispose();
            ResumeCaptureOverlay();SetScrollingPreparationUi(false);FocusCaptureOverlay();TranslationStatus("无法完成滚动截屏："+error.Message+"；原截图和标注已保留。");
        }
        async System.Threading.Tasks.Task<ExternalScrollExportReply> ExportScrollingImage(ExternalScrollCaptureSession session,ExternalScrollResult output){
            if(closed||scrollingCapture!=session||AppState.Quitting)return ExternalScrollExportReply.Cancelled();
            if(output!=null&&((output.action=="pin"&&!captureFeatures.Pin)||(output.action=="collect"&&!captureFeatures.Collect)))return ExternalScrollExportReply.Failure("此截图功能已关闭，可在设置中开启后重新截图。");
            try{
                var image=await System.Threading.Tasks.Task.Run(()=>session.LoadImage(output));
                if(closed||scrollingCapture!=session||AppState.Quitting)return ExternalScrollExportReply.Cancelled();
                collecting=true;bool completed=false;
                try{
                    if(output.action=="copy")completed=await OutputActions.Copy(image,outputLifetime.Token);
                    else if(output.action=="save")completed=await OutputActions.Save(image,this,outputLifetime.Token);
                    else if(output.action=="pin"){
                        var region=ScrollRegion(shot,surface.Selection,surface.ScaleX,surface.ScaleY);
                        completed=await OutputActions.Pin(image,new Int32Rect(region.X,region.Y,region.Width,region.Height),outputLifetime.Token);
                    }else if(output.action=="collect"){
                        var collect=MoriDesk.DeskRuntime.CollectImage;
                        if(collect==null)throw new InvalidOperationException("素材库暂不可用。");
                        completed=await collect(image);
                    }
                }finally{collecting=false;}
                return completed?ExternalScrollExportReply.Success():ExternalScrollExportReply.Cancelled();
            }catch(OperationCanceledException){return ExternalScrollExportReply.Cancelled();}
            catch(Exception error){return ExternalScrollExportReply.Failure(error.Message);}
        }
        void DisposeScrollingCapture(){var session=scrollingCapture;scrollingCapture=null;if(session!=null)session.Dispose();}
    }
}
