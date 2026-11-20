using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;

internal static class BusyTranslationTests {
    static BitmapSource WithMargins(BitmapSource image,bool extraInk,int width=990,int left=285,int top=70){
        const int height=162;
        var pixels=new byte[width*height*4];
        var bgra=new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);
        var background=new byte[4];bgra.CopyPixels(new Int32Rect(0,0,1,1),background,4,0);
        for(int i=0;i<pixels.Length;i+=4){
            pixels[i]=background[0];pixels[i+1]=background[1];pixels[i+2]=background[2];pixels[i+3]=background[3];
        }
        bgra.CopyPixels(Int32Rect.Empty,pixels,width*4,(top*width+left)*4);
        if(extraInk)pixels[(8*width+21)*4+1]=(byte)(background[1]==255?254:background[1]+1);
        var output=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);
        output.Freeze();return output;
    }
    static async Task RetryDuringCancellation(BitmapSource menu,Action<bool,string> check){
        var entered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using(var stop=new CancellationTokenSource())
        using(var guard=new CancellationTokenSource(8000)){
            var retired=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var first=OcrWorker.ReadForTranslationObserved(menu,stop.Token,stage=>{
                if(stage=="worker-enter"){
                    entered.TrySetResult(true);
                    try{if(!resume.Task.Wait(8000))throw new TimeoutException("previous verifier was not released");}
                    finally{retired.TrySetResult(true);}
                }
            },0);
            try{
                var expired=Task.Delay(Timeout.Infinite,guard.Token);
                if(await Task.WhenAny(entered.Task,expired)!=entered.Task)throw new TimeoutException("first verifier did not start");
                stop.Cancel();bool cancelled=false;
                try{await first;}catch(OperationCanceledException){cancelled=stop.IsCancellationRequested;}
                check(cancelled&&!retired.Task.IsCompleted,"cancelling a capture returns while its previous recognition is still unwinding");
                using(var abandoned=CancellationTokenSource.CreateLinkedTokenSource(guard.Token)){
                    bool cancelledWait=false;
                    try{await OcrWorker.ReadForTranslationObserved(menu,abandoned.Token,stage=>{if(stage=="waiting-for-verifier")abandoned.Cancel();},0);}
                    catch(OperationCanceledException){cancelledWait=abandoned.IsCancellationRequested;}
                    guard.Token.ThrowIfCancellationRequested();
                    check(cancelledWait,"a second click can also cancel a retry that is waiting for the previous verifier");
                }
                Int32Rect bounds;var line=VerifiedLineOcr.ShortSelection(menu,guard.Token,out bounds);
                var unavailable=await VerifiedLineOcr.TryReadObserved(line,guard.Token,null,0);
                check(unavailable==null&&!retired.Task.IsCompleted,"a stuck previous verifier allows fallback without waiting for its own eventual exit");
                bool handoff=false;
                var retry=await OcrWorker.ReadForTranslationObserved(menu,guard.Token,stage=>{
                    if(stage=="waiting-for-verifier"){handoff=true;resume.TrySetResult(true);}
                },0);
                check(handoff&&retry!=null&&retry.Text=="New folder","immediate retry recognizes the selected label while the full-page worker remains busy");
            }finally{
                resume.TrySetResult(true);stop.Cancel();
            }
        }
    }
    internal static async Task Run(string fixtures,Action<bool,string> check){
        BitmapSource menu;
        using(var stream=File.OpenRead(Path.Combine(fixtures,"menu.png"))){
            menu=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];
            menu.Freeze();
        }
        // Hold the full-page worker slot, just as an unfinished large OCR job
        // would. No timing assumption about an underlying model is required.
        var model=(SemaphoreSlim)typeof(OcrWorker).GetField("queue",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
        await model.WaitAsync();
        try{
            await RetryDuringCancellation(WithMargins(menu,false,991),check);
            using(var stream=File.OpenRead(Path.Combine(fixtures,"dark-copy.png")))
            using(var deadline=new CancellationTokenSource(10000)){
                var caption=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];
                var word=new CroppedBitmap(caption,new Int32Rect(59,23,62,30));word.Freeze();
                var sourceInk=DesktopLineEvidence.Analyze(word,true).Bounds;
                var wide=await OcrWorker.ReadForTranslation(WithMargins(word,false),deadline.Token);
                var expected=new Rect(sourceInk.X+285,sourceInk.Y+70,sourceInk.Width,sourceInk.Height);
                check(wide.Text=="image"&&wide.Words.Count==1&&wide.Words[0].Box==expected,"a loosely selected dotted word can translate while the full model is occupied and keeps its exact position");
                Int32Rect area;
                check(VerifiedLineOcr.ShortSelection(WithMargins(word,true),deadline.Token,out area)==null,"an extra faint mark in a loose selection still rules out a lone dotted word");
            }
            using(var deadline=new CancellationTokenSource(10000)){
                var page=await OcrWorker.ReadForTranslation(menu,deadline.Token);
                check(page!=null&&page.Text=="New folder","another window's ongoing full-page OCR allows a proven new short translation");
                var wide=await OcrWorker.ReadForTranslation(WithMargins(menu,false),deadline.Token);
                bool aligned=wide.Words.Count==page.Words.Count&&wide.Text==page.Text;
                for(int i=0;aligned&&i<page.Words.Count;i++){
                    var box=page.Words[i].Box;box.Offset(285,70);
                    aligned=wide.Words[i].Box==box;
                }
                check(aligned,"a loosely framed menu retains exact word locations on the original selection");
                Int32Rect bounds;
                var marked=VerifiedLineOcr.ShortSelection(WithMargins(menu,true),deadline.Token,out bounds);
                check(marked==null,"a one-level green mark in the margin prevents recognition of only the central label");
            }
            // Neither worker may run. Only this image's previously verified
            // result can complete before the simulated second capture releases
            // the short-line slot. The timeout is just a guard for a regression.
            var lines=(SemaphoreSlim)typeof(OcrWorker).GetField("shortLines",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
            await lines.WaitAsync();
            try{
                OcrPage cached=null;
                using(var guard=new CancellationTokenSource(5000)){
                    try{cached=await OcrWorker.ReadForTranslation(menu,guard.Token);}
                    catch(OperationCanceledException){if(!guard.IsCancellationRequested)throw;}
                }
                check(cached!=null&&cached.Text=="New folder","a cached capture returns while another short line and the full model are both occupied");
                using(var guard=new CancellationTokenSource(5000)){
                    var moved=await OcrWorker.ReadForTranslation(WithMargins(menu,false,990,402,32),guard.Token);
                    bool aligned=moved.Text==cached.Text&&moved.Words.Count==cached.Words.Count;
                    for(int i=0;aligned&&i<cached.Words.Count;i++){
                        var expected=cached.Words[i].Box;expected.Offset(402,32);
                        aligned=expected==moved.Words[i].Box;
                    }
                    check(aligned,"reframing a wide selection reuses its verified pixels and puts every word at the new location");
                }
                using(var guard=new CancellationTokenSource(350)){
                    bool refused=false;
                    try{await OcrWorker.ReadForTranslation(WithMargins(menu,true,990,402,32),guard.Token);}
                    catch(OperationCanceledException){refused=guard.IsCancellationRequested;}
                    check(refused,"one new color level in fresh margins cannot reuse a previously translated crop");
                }
                using(var stopped=new CancellationTokenSource()){
                    stopped.Cancel();bool cancelled=false;
                    try{await OcrWorker.ReadForTranslation(menu,stopped.Token);}
                    catch(OperationCanceledException){cancelled=stopped.IsCancellationRequested;}
                    check(cancelled,"cancellation takes precedence over an available translation OCR result");
                }
            }finally{lines.Release();}
            // Recognition for copying still requires the requested full engine.
            using(var timeout=new CancellationTokenSource(350)){
                bool cancelled=false;
                try{await OcrWorker.Read(menu,timeout.Token);}
                catch(OperationCanceledException){cancelled=timeout.IsCancellationRequested;}
                check(cancelled,"plain OCR still waits for its own engine and can be cancelled while it is queued");
            }
        }finally{model.Release();}
    }
}
