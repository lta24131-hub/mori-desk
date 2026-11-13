using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;

internal static class ImageTranslationSweepTests {
    static BitmapSource Solid(byte r,byte g,byte b){var pixels=new byte[200*60*4];for(int i=0;i<pixels.Length;i+=4){pixels[i]=b;pixels[i+1]=g;pixels[i+2]=r;pixels[i+3]=255;}var image=BitmapSource.Create(200,60,96,96,PixelFormats.Bgra32,null,pixels,800);image.Freeze();return image;}
    static byte[] Pixel(FrameworkElement control,int x,int y){control.Measure(new Size(200,60));control.Arrange(new Rect(0,0,200,60));control.UpdateLayout();var image=new RenderTargetBitmap(200,60,96,96,PixelFormats.Pbgra32);image.Render(control);var pixel=new byte[4];image.CopyPixels(new Int32Rect(x,y,1,1),pixel,4,0);return pixel;}
    internal static async Task Run(Action<bool,string> check){
        await BackgroundRendering(check);
        await PreparationCancellation(check);
        var sweep=new ImageTranslationSweep();var window=new Window{Content=sweep,Width=240,Height=110,ShowInTaskbar=false};window.Show();
        var progress=(DependencyProperty)typeof(ImageTranslationSweep).GetField("ProgressProperty",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
        try {
            sweep.Start(new Size(200,60),Rect.Empty);sweep.SetRegions(new[]{new TranslationRegion{Text="source",Box=new Rect(20,20,160,20)}});
            sweep.BeginAnimation(progress,null);sweep.SetValue(progress,.5d);
            check(sweep.IsRunning&&!sweep.IsHitTestVisible&&!sweep.Focusable&&VisualTreeHelper.GetChildrenCount(sweep)==0,"sweep uses one noninteractive drawing surface and never steals capture input");
            check(Pixel(sweep,100,30)[3]>0&&Pixel(sweep,100,5)[3]==0,"pending highlight illuminates text boxes and leaves surrounding image unchanged");
            sweep.SetRegions(new[]{new TranslationRegion{Box=new Rect(20,20,160,20)},new TranslationRegion{Box=new Rect(20,28,160,20)}});
            check(Pixel(sweep,100,30)[3]>0,"overlapping OCR rows keep a continuous highlight rather than cutting holes in the mask");
            using(var cancel=new CancellationTokenSource()){
                var reveal=sweep.RevealAsync(Solid(0,200,0),cancel.Token);sweep.BeginAnimation(progress,null);sweep.SetValue(progress,.5d);
                check(Pixel(sweep,10,30)[1]==200&&Pixel(sweep,190,30)[3]==0,"ready bitmap reveals behind sweep while unrevealed original remains visible");
                sweep.SetOrientation(0,true,false);
                check(Pixel(sweep,190,30)[1]==200&&Pixel(sweep,10,30)[3]==0,"flipped pinned image reveals in the same orientation as its original pixels");sweep.SetOrientation(0,false,false);
                cancel.Cancel();bool cancelled=false;try{await reveal;}catch(OperationCanceledException){cancelled=true;}
                check(cancelled&&!sweep.IsRunning&&!sweep.HasResult&&sweep.Visibility==Visibility.Collapsed,"cancelled reveal releases bitmap and animation instead of committing stale translation");
            }
            sweep.Start(new Size(200,60),Rect.Empty);var stopped=sweep.RevealAsync(Solid(200,0,0),CancellationToken.None);sweep.Stop();bool stopCancelled=false;try{await stopped;}catch(OperationCanceledException){stopCancelled=true;}
            check(stopCancelled&&!sweep.IsRunning&&!sweep.HasResult,"replacing or closing a translation cancels an outstanding reveal");
            sweep.Start(new Size(200,60),Rect.Empty);window.Hide();await sweep.RevealAsync(Solid(0,0,200),CancellationToken.None);
            check(sweep.HasResult,"hidden window completes ready result immediately without waiting on an invisible animation clock");
            sweep.Stop();window.Show();sweep.Start(new Size(200,60),Rect.Empty);sweep.SetOrientation(1,true,false);
            await sweep.RevealAsync(Solid(0,200,0),CancellationToken.None);sweep.Stop();
            check(!sweep.IsRunning&&!sweep.HasResult,"normal reveal finishes and releases resources for transformed pins");
        }finally{sweep.Stop();window.Close();}
        var page=new OcrPage();page.Words.Add(new WordBox{Text="Please open your file.",Box=new Rect(10,10,180,30),Line=0});OcrService.IndexText(page);
        var reply=new TaskCompletionSource<string[]>();CancellationToken request=CancellationToken.None;
        var viewer=new TextWindow(Solid(255,255,255),"zh",(image,token)=>Task.FromResult(page));
        viewer.TranslateImage=(source,token,target,provider)=>{request=token;return reply.Task;};
        var flags=BindingFlags.NonPublic|BindingFlags.Instance;
        var toggle=typeof(TextWindow).GetMethod("ToggleImageTranslation",flags);
        var viewerSweep=(ImageTranslationSweep)typeof(TextWindow).GetField("translationSweep",flags).GetValue(viewer);
        var status=(Border)typeof(TextWindow).GetField("statusCard",flags).GetValue(viewer);
        try{
            viewer.Show();await viewer.EnsureOcr();toggle.Invoke(viewer,null);
            check(viewerSweep.IsRunning&&status.Visibility==Visibility.Collapsed,"real image viewer starts sweep without progress explanation text");
            typeof(TextWindow).GetMethod("ChangeTarget",flags).Invoke(viewer,new object[]{"en"});
            reply.SetResult(new[]{"旧请求译文"});await System.Windows.Threading.Dispatcher.Yield();
            check(request.IsCancellationRequested&&!viewerSweep.IsRunning&&(BitmapSource)typeof(TextWindow).GetField("translatedImage",flags).GetValue(viewer)==null,"changing language cancels request and ignores its late response");
            var failed=new TaskCompletionSource<string[]>();viewer.TranslateImage=(source,token,target,provider)=>failed.Task;toggle.Invoke(viewer,null);failed.SetException(new InvalidOperationException("测试网络错误"));await System.Windows.Threading.Dispatcher.Yield();
            check(!viewerSweep.IsRunning&&status.Visibility==Visibility.Visible,"translation failure stops sweep and retains a visible actionable error");
            var rendering=new TaskCompletionSource<ImageTranslationResult>();CancellationToken renderingToken=CancellationToken.None;
            viewer.TranslateImage=(source,token,target,provider)=>Task.FromResult(new[]{"请打开文件。"});
            viewer.RenderImage=(source,regions,translations,token)=>{renderingToken=token;return rendering.Task;};toggle.Invoke(viewer,null);
            check(viewerSweep.IsRunning&&renderingToken.CanBeCanceled,"viewer keeps its sweep active while background layout is pending");
            typeof(TextWindow).GetMethod("ChangeTarget",flags).Invoke(viewer,new object[]{"zh"});
            rendering.SetResult(new ImageTranslationResult{Image=Solid(0,200,0),Transcript="旧译文"});await System.Windows.Threading.Dispatcher.Yield();
            check(renderingToken.IsCancellationRequested&&!viewerSweep.IsRunning&&(BitmapSource)typeof(TextWindow).GetField("translatedImage",flags).GetValue(viewer)==null,"changing language during layout cannot reveal or commit a stale image");
            viewer.RenderImage=ImageTranslation.RenderAsync;
            viewer.TranslateImage=(source,token,target,provider)=>Task.FromResult(new[]{"请打开文件。"});toggle.Invoke(viewer,null);
            for(int n=0;n<100&&viewerSweep.IsRunning;n++)await Task.Delay(20);
            check(!viewerSweep.IsRunning&&(BitmapSource)typeof(TextWindow).GetField("translatedImage",flags).GetValue(viewer)!=null,"successful image request commits translated bitmap after bounded reveal");
            var original=(TextBox)typeof(TextWindow).GetField("original",flags).GetValue(viewer);
            var selectionResult=(TextBox)typeof(TextWindow).GetField("result",flags).GetValue(viewer);
            original.Text="I keep getting Al responses.\nI asked for cancellation and refund";original.SelectAll();string query=null;
            viewer.TranslateText=(text,token,target,provider)=>{query=text;return Task.FromResult("我一直收到Al的回复。我要求取消和退款");};
            typeof(TextWindow).GetMethod("TranslateSelected",flags).Invoke(viewer,null);
            check(query=="I keep getting Al responses. I asked for cancellation and refund"&&original.SelectedText.Contains("Al responses.\n"),"selected text joins physical wraps but cannot guess Al spelling without word evidence or change copied source");
            check(selectionResult.Text=="我一直收到Al的回复。我要求取消和退款","selected text retains the provider result without reintroducing an evidence-free acronym correction");
        }finally{viewer.Close();}
    }
    static async Task PreparationCancellation(Action<bool,string> check){
        var flags=BindingFlags.NonPublic|BindingFlags.Instance;
        var toggle=typeof(TextWindow).GetMethod("ToggleImageTranslation",flags);
        var changeTarget=typeof(TextWindow).GetMethod("ChangeTarget",flags);
        foreach(bool closeWindow in new[]{false,true}){
            var page=new OcrPage();page.Words.Add(new WordBox{Text="settlngs",Box=new Rect(10,10,180,30),Line=0,Confidence=.8,ConfidenceSource=OcrConfidenceSource.Model});OcrService.IndexText(page);
            var pending=new TaskCompletionSource<string[]>();CancellationToken preparationToken=CancellationToken.None;
            int preparationCalls=0,translationCalls=0,renderCalls=0;bool closed=false;
            var viewer=new TextWindow(Solid(255,255,255),"zh",(image,token)=>Task.FromResult(page)){ShowInTaskbar=false};
            viewer.Closed+=(s,e)=>closed=true;
            viewer.PrepareImage=(source,regions,token)=>{preparationCalls++;preparationToken=token;return pending.Task;};
            viewer.TranslateImage=(source,token,target,provider)=>{translationCalls++;return Task.FromResult(new[]{"不应请求的译文"});};
            viewer.RenderImage=(source,regions,translated,token)=>{renderCalls++;return Task.FromResult(new ImageTranslationResult{Image=Solid(0,200,0),Transcript="不应提交的译文"});};
            var sweep=(ImageTranslationSweep)typeof(TextWindow).GetField("translationSweep",flags).GetValue(viewer);
            string action=closeWindow?"closing viewer":"changing target";
            try{
                viewer.Show();await viewer.EnsureOcr();toggle.Invoke(viewer,null);
                check(preparationCalls==1&&preparationToken.CanBeCanceled&&sweep.IsRunning&&translationCalls==0&&renderCalls==0,"viewer actually waits in asynchronous input preparation before "+action);
                if(closeWindow)viewer.Close();else changeTarget.Invoke(viewer,new object[]{"en"});
                check(preparationToken.IsCancellationRequested&&!sweep.IsRunning,action+" cancels pending preparation and immediately stops its sweep");
                // Deliberately finish successfully after cancellation: a worker
                // may already have its answer when cancellation is delivered.
                pending.SetResult(new[]{"settings"});await System.Windows.Threading.Dispatcher.Yield();
                check(translationCalls==0&&renderCalls==0&&typeof(TextWindow).GetField("translatedImage",flags).GetValue(viewer)==null&&
                    typeof(TextWindow).GetField("imageTranslation",flags).GetValue(viewer)==null&&typeof(TextWindow).GetField("imageRequest",flags).GetValue(viewer)==null,
                    "late prepared input after "+action+" cannot enter translation, render or commit a stale result");
            }finally{pending.TrySetCanceled();if(!closed)viewer.Close();}
        }
    }
    static byte[] Pixels(BitmapSource source){var data=new byte[source.PixelWidth*source.PixelHeight*4];source.CopyPixels(data,source.PixelWidth*4,0);return data;}
    static async Task BackgroundRendering(Action<bool,string> check){
        var image=Solid(255,255,255);var region=new TranslationRegion{Text="Please open your file.",Box=new Rect(10,10,180,30)};
        region.SourceLines.Add(new TranslationSourceLine{Text=region.Text,Box=region.Box});var regions=new[]{region};var values=new[]{"请打开文件。"};
        var expected=ImageTranslation.RenderWithStatus(image,regions,values);
        var actual=await ImageTranslation.RenderAsync(image,regions,values,CancellationToken.None);
        check(actual.Image.IsFrozen&&Pixels(actual.Image).SequenceEqual(Pixels(expected.Image))&&actual.Transcript==expected.Transcript&&actual.UnplacedCount==expected.UnplacedCount,"background renderer preserves exact pixels transcript and fallback counts");
        var mutable=(BitmapSource)image.Clone();
        var cloned=await ImageTranslation.RenderAsync(mutable,regions,values,CancellationToken.None);
        check(!mutable.IsFrozen&&cloned.Image.IsFrozen&&Pixels(cloned.Image).SequenceEqual(Pixels(expected.Image)),"unfrozen caller image is copied safely without freezing the caller's own object");
        var slot=(SemaphoreSlim)typeof(ImageTranslation).GetField("renderSlot",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
        Task<ImageTranslationResult> queued;
        await slot.WaitAsync();
        try{
            queued=ImageTranslation.RenderAsync(image,regions,values,CancellationToken.None);
            check(!queued.IsCompleted,"queued image waits before allocating its full-size render buffers");
            region.Box=new Rect(0,0,1,1);region.SourceLines.Clear();values[0]="changed after request";
            using(var stop=new CancellationTokenSource()){
                var cancelled=ImageTranslation.RenderAsync(image,regions,values,stop.Token);stop.Cancel();bool failed=false;
                try{await cancelled;}catch(OperationCanceledException){failed=true;}
                check(failed&&slot.CurrentCount==0,"cancelling a queued render leaves the active slot owned and returns without waiting for it");
            }
        }finally{slot.Release();}
        actual=await queued;
        check(Pixels(actual.Image).SequenceEqual(Pixels(expected.Image))&&actual.Transcript==expected.Transcript,"queued rendering snapshots geometry text and source lines before caller edits");
        using(var stop=new CancellationTokenSource()){
            stop.Cancel();bool failed=false;try{await ImageTranslation.RenderAsync(image,regions,values,stop.Token);}catch(OperationCanceledException){failed=true;}
            check(failed&&slot.CurrentCount==1,"pre-cancelled rendering does not allocate or consume a render slot");
        }
        bool invalid=false;try{await ImageTranslation.RenderAsync(image,regions,new string[0],CancellationToken.None);}catch(InvalidOperationException){invalid=true;}
        check(invalid&&slot.CurrentCount==1,"invalid layout input does not leak the slot or block later translations");
        var empty=await ImageTranslation.RenderAsync(image,new TranslationRegion[0],new string[0],CancellationToken.None);
        check(empty.Image==image&&empty.UnplacedCount==0&&empty.Transcript=="","empty background layout preserves its original image exactly");
    }
}
