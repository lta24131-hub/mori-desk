using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace QingJie {
    // Per-window dependencies, not global test switches. The same transaction
    // path is exercised with synthetic outputs without touching the desktop.
    internal interface ICaptureOutputActions {
        Task<bool> Copy(BitmapSource image,CancellationToken cancel);
        Task<bool> Save(BitmapSource image,Window owner,CancellationToken cancel);
        Task<bool> Pin(BitmapSource image,Int32Rect bounds,CancellationToken cancel);
    }
    internal sealed class CaptureOutputActions : ICaptureOutputActions {
        public async Task<bool> Copy(BitmapSource image,CancellationToken cancel){await ImageFiles.CopyAsync(image,cancel);return true;}
        public Task<bool> Save(BitmapSource image,Window owner,CancellationToken cancel){return ImageFiles.SaveAsync(image,owner,cancel);}
        public async Task<bool> Pin(BitmapSource image,Int32Rect bounds,CancellationToken cancel){
            cancel.ThrowIfCancellationRequested();
            if(AppState.Pins==null)throw new InvalidOperationException("贴图服务暂不可用，请稍后重试。");
            await AppState.Pins.NewAtAsync(image,bounds,cancel);return true;
        }
    }
    public sealed partial class CaptureWindow {
        internal ICaptureOutputActions OutputActions=new CaptureOutputActions();
        internal Action<TextWindow> PresentTextViewer;
        internal Func<BitmapSource,CancellationToken,Task<OcrPage>> ReadCaptureOcr=OcrService.Read;
        readonly CancellationTokenSource outputLifetime=new CancellationTokenSource();
        TextWindow captureTextViewer;

        internal async Task<bool> RunCaptureOutputAsync(string pending,Func<BitmapSource,CancellationToken,Task<bool>> submit){
            if(!CanEditCapture||submit==null)return false;
            collecting=true;
            try{
                CommitText();if(!ValidateCaptureTextBounds())return false;
                var image=surface.Export();
                surface.IsEnabled=false;toolbar.IsEnabled=false;properties.IsEnabled=false;
                TranslationStatus(pending);
                bool saved=await submit(image,outputLifetime.Token);
                // A request already committed may complete after Escape. Its
                // result belongs only to this window, never a newer capture.
                if(closed)return saved;
                if(saved){Close();return true;}
                TranslationStatus("尚未完成，截图已保留；可以继续编辑、重试或选择其他保存方式。");return false;
            }catch(OperationCanceledException){if(!closed)TranslationStatus("操作已取消，截图已保留。");return false;}
            catch(Exception error){if(!closed)TranslationStatus("未完成，截图已保留："+error.Message);return false;}
            finally{collecting=false;if(!closed){
                surface.IsEnabled=true;toolbar.IsEnabled=true;properties.IsEnabled=true;
                UpdateCapturePropertyPresentation();RefreshCaptureToolbarHistory();
            }}
        }
        internal Task<bool> CopyCaptureAsync(){return RunCaptureOutputAsync("正在复制图片…",(image,cancel)=>OutputActions.Copy(image,cancel));}
        internal Task<bool> SaveCaptureAsync(){return RunCaptureOutputAsync("选择保存位置；取消后仍可继续编辑。",async(image,cancel)=>{
            Topmost=false;
            try{return await OutputActions.Save(image,this,cancel);}
            finally{if(!closed)Topmost=true;}
        });}
        internal Task<bool> PinCaptureAsync(){if(!captureFeatures.Pin)return Task.FromResult(false);return RunCaptureOutputAsync("正在贴到屏幕…",(image,cancel)=>{
            var crop=Geometry.PixelCrop(surface.Selection,surface.ScaleX,surface.ScaleY,shot.Image.PixelWidth,shot.Image.PixelHeight);
            var bounds=new Int32Rect(shot.Bounds.X+crop.X,shot.Bounds.Y+crop.Y,crop.Width,crop.Height);
            return OutputActions.Pin(image,bounds,cancel);
        });}
        internal Task<bool> CollectToLibraryAsync(){
            if(!captureFeatures.Collect)return Task.FromResult(false);
            var collect=MoriDesk.DeskRuntime.CollectImage;
            return RunCaptureOutputAsync("正在收藏到素材库…",collect==null?null:(Func<BitmapSource,CancellationToken,Task<bool>>)((image,cancel)=>collect(image)));
        }
        async void Copy(){await CopyCaptureAsync();}
        async void Save(){await SaveCaptureAsync();}
        async void Pin(){await PinCaptureAsync();}
        async void CollectToLibrary(){await CollectToLibraryAsync();}

        void ReadText(bool translate){
            if(!CanEditCapture||(translate?!captureFeatures.Translation:!captureFeatures.Ocr))return;
            if(translate){TranslateInPlace();return;}
            Guard(()=>OpenCaptureTextViewer(new TextWindow(surface.Export(false,false),translationTarget,allowTranslation:captureFeatures.Translation,allowPin:captureFeatures.Pin)));
        }
        internal void OpenCaptureTextViewer(TextWindow viewer){
            if(viewer==null)throw new ArgumentNullException("viewer");
            if(!CanEditCapture||captureTextViewer!=null){viewer.Close();return;}
            captureTextViewer=viewer;
            viewer.AttachCaptureReturn(()=>{if(!closed)Close();});
            viewer.Closed+=(sender,args)=>{
                if(captureTextViewer!=viewer)return;
                captureTextViewer=null;
                if(closed||AppState.Quitting)return;
                ResumeCaptureOverlay();FocusCaptureOverlay();
            };
            SuspendCaptureOverlay();
            try{
                if(PresentTextViewer!=null)PresentTextViewer(viewer);else ShowCaptureTextViewer(viewer);
            }catch{
                captureTextViewer=null;viewer.Close();
                if(!closed){ResumeCaptureOverlay();FocusCaptureOverlay();}
                throw;
            }
        }
        void ShowCaptureTextViewer(TextWindow viewer){
            var working=System.Windows.Forms.Screen.FromRectangle(shot.Bounds).WorkingArea;
            double scaleX=surface.ActualWidth>0?shot.Bounds.Width/surface.ActualWidth:1;
            double scaleY=surface.ActualHeight>0?shot.Bounds.Height/surface.ActualHeight:1;
            var placement=TextViewerBounds(working,scaleX,scaleY);
            viewer.WindowStartupLocation=WindowStartupLocation.Manual;
            viewer.MinWidth=Math.Min(viewer.MinWidth,placement.Width/scaleX);viewer.MinHeight=Math.Min(viewer.MinHeight,placement.Height/scaleY);
            viewer.Width=placement.Width/scaleX;viewer.Height=placement.Height/scaleY;
            // Set native pixels, not mixed-DPI logical desktop coordinates.
            viewer.SourceInitialized+=(sender,args)=>Native.SetWindowPos(new WindowInteropHelper(viewer).Handle,IntPtr.Zero,placement.X,placement.Y,placement.Width,placement.Height,0x0014);
            viewer.Show();viewer.Activate();
        }
        internal static Int32Rect TextViewerBounds(System.Drawing.Rectangle work,double scaleX,double scaleY){
            if(work.Width<=0||work.Height<=0)throw new ArgumentException("显示区域无效。");
            if(double.IsNaN(scaleX)||double.IsInfinity(scaleX)||scaleX<=0)scaleX=1;
            if(double.IsNaN(scaleY)||double.IsInfinity(scaleY)||scaleY<=0)scaleY=1;
            int width=Math.Max(1,(int)Math.Min(1120*scaleX,work.Width*.92));
            int height=Math.Max(1,(int)Math.Min(620*scaleY,work.Height*.88));
            return new Int32Rect(work.Left+(work.Width-width)/2,work.Top+(work.Height-height)/2,width,height);
        }
        internal bool FocusCaptureTextViewer(){
            if(captureTextViewer==null||!captureTextViewer.IsVisible)return false;
            if(captureTextViewer.WindowState==WindowState.Minimized)captureTextViewer.WindowState=WindowState.Normal;
            captureTextViewer.Activate();return true;
        }
        void DisposeCaptureOutput(){
            outputLifetime.Cancel();outputLifetime.Dispose();
            var viewer=captureTextViewer;captureTextViewer=null;
            if(viewer!=null)viewer.Close();
        }
    }
}
