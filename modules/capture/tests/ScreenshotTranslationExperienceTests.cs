using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;

internal static class ScreenshotTranslationExperienceTests {
    static BitmapSource Solid(byte r,byte g,byte b){var pixels=new byte[200*60*4];for(int i=0;i<pixels.Length;i+=4){pixels[i]=b;pixels[i+1]=g;pixels[i+2]=r;pixels[i+3]=255;}var image=BitmapSource.Create(200,60,96,96,PixelFormats.Bgra32,null,pixels,800);image.Freeze();return image;}
    static readonly BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance;
    static T Field<T>(TextWindow viewer,string name){return (T)typeof(TextWindow).GetField(name,Private).GetValue(viewer);}
    static void Click(Button button){button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,button));}
    static void Target(TextWindow viewer,string target){typeof(TextWindow).GetMethod("ChangeTarget",Private).Invoke(viewer,new object[]{target});}
    static Task Tick(){return System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle).Task;}
    static OcrPage Fixture(){var page=new OcrPage();page.Words.Add(new WordBox{Text="Please open your file.",Box=new Rect(10,10,180,30),Line=0});OcrService.IndexText(page);return page;}
    internal static async Task Run(Action<bool,string> check){
        await BackgroundRendering(check);await PreparationCancellation(check);await ReaderTranslationRecognitionTests.Run(check);await SelectedTextFeedback(check);
        var source=Solid(255,255,255);var output=Solid(0,200,0);var page=Fixture();
        var viewer=new TextWindow(source,"zh",(image,token)=>Task.FromResult(page)){ShowInTaskbar=false};
        var button=Field<Button>(viewer,"translatePicture");var picture=Field<Image>(viewer,"picture");var status=Field<Border>(viewer,"statusCard");var transcript=Field<TextBox>(viewer,"result");
        var glyph=button.Content as TextBlock;
        check(glyph!=null&&glyph.Text=="\uE8C1"&&glyph.FontFamily.Source=="Segoe MDL2 Assets"&&AutomationProperties.GetName(button)=="整图翻译","reader initially displays its translation symbol and accessible action");
        Func<string,bool> iconAction=label=>ReferenceEquals(button.Content,glyph)&&AutomationProperties.GetName(button)==label;
        var reply=new TaskCompletionSource<string[]>();CancellationToken request=CancellationToken.None;
        viewer.PrepareImage=(p,rs,token)=>Task.FromResult(new[]{p.Text});viewer.TranslateImage=(input,token,to,provider)=>{request=token;return reply.Task;};
        try{
            viewer.Show();await viewer.EnsureOcr();Click(button);
            check(iconAction("取消翻译")&&picture.Source==source&&status.Visibility==Visibility.Collapsed,"pending work keeps original image untouched with cancellation on the toolbar");
            Click(button);reply.SetResult(new[]{"迟到的译文"});await Tick();
            check(request.IsCancellationRequested&&picture.Source==source&&Field<ImageTranslationResult>(viewer,"imageTranslation")==null,"cancelled provider reply cannot change the original image");
            reply=new TaskCompletionSource<string[]>();Click(button);Target(viewer,"en");reply.SetResult(new[]{"旧语言的结果"});await Tick();
            check(request.IsCancellationRequested&&Field<BitmapSource>(viewer,"translatedImage")==null,"changing target invalidates a late provider response");
            reply=new TaskCompletionSource<string[]>();Click(button);reply.SetException(new InvalidOperationException("测试网络错误"));await Tick();
            check(iconAction("重试翻译")&&picture.Source==source&&status.Visibility==Visibility.Visible,"network failure keeps source and exposes retry on the same action");
            var render=new TaskCompletionSource<ImageTranslationResult>();CancellationToken renderToken=CancellationToken.None;int calls=0;
            viewer.TranslateImage=(input,token,to,provider)=>{calls++;return Task.FromResult(new[]{"请打开文件。"});};viewer.RenderImage=(i,rs,t,token)=>{renderToken=token;return render.Task;};
            Click(button);await Tick();
            check(transcript.Text.Contains("请打开文件")&&picture.Source==source&&renderToken.CanBeCanceled,"translated text becomes readable before image composition finishes");
            Target(viewer,"zh");render.SetResult(new ImageTranslationResult{Image=output,Transcript="旧译文"});await Tick();
            check(renderToken.IsCancellationRequested&&picture.Source==source&&Field<BitmapSource>(viewer,"translatedImage")==null,"target switch during rendering prevents stale output from appearing");
            render=new TaskCompletionSource<ImageTranslationResult>();Click(button);render.SetException(new InvalidOperationException("测试排版失败"));await Tick();
            check(iconAction("重试排版")&&transcript.Text.Contains("请打开文件")&&picture.Source==source,"layout failure retains translated text and enables render-only retry");
            int before=calls;viewer.RenderImage=(i,rs,t,token)=>Task.FromResult(new ImageTranslationResult{Image=output,Transcript="请打开文件。",UnplacedCount=1});Click(button);await Tick();
            check(calls==before&&picture.Source==output&&iconAction("查看原图"),"layout retry commits immediately without repeating OCR or online translation");
            Click(button);check(picture.Source==source&&iconAction("查看译图"),"single click gives original");Click(button);check(picture.Source==output&&calls==before,"single click restores translated image without a network request");
            var original=Field<TextBox>(viewer,"original");
            var savedProvider=AppState.Settings.TranslationProvider;
            try{
                AppState.Settings.TranslationProvider="tencent";Target(viewer,"zh");
                original.Text="Select all";original.SelectAll();Click(Field<Button>(viewer,"translateSelection"));
                check(transcript.Text=="全选"&&Field<Button>(viewer,"copyResult").IsEnabled&&Field<CancellationTokenSource>(viewer,"selectionRequest")==null,"an exact UI command selected in the real reader is translated and copyable in the same click");
            }finally{AppState.Settings.TranslationProvider=savedProvider;}
            original.Text="I keep getting Al responses.\nI asked for cancellation and refund";original.SelectAll();string query=null;
            viewer.TranslateText=(text,token,target,provider)=>{query=text;return Task.FromResult("我一直收到Al的回复。我要求取消和退款");};typeof(TextWindow).GetMethod("TranslateSelected",Private).Invoke(viewer,null);
            check(query=="I keep getting Al responses. I asked for cancellation and refund"&&original.SelectedText.Contains("Al responses.\n"),"selection joins physical wraps without guessing acronym spellings or changing copied source");
            check(transcript.Text=="我一直收到Al的回复。我要求取消和退款","selection preserves the provider response");
            Target(viewer,"zh");var selectedReply=new TaskCompletionSource<string>();CancellationToken selectedToken=CancellationToken.None;
            viewer.TranslateText=(text,token,target,provider)=>{selectedToken=token;return selectedReply.Task;};
            typeof(TextWindow).GetMethod("TranslateSelected",Private).Invoke(viewer,null);Click(button);await Tick();
            check(!selectedToken.IsCancellationRequested&&Field<CancellationTokenSource>(viewer,"selectionRequest")!=null,"unplaced whole-image regions never cancel a user's active selected-text translation");
            selectedReply.SetResult("用户选择的译文");await Tick();
            check(transcript.Text=="用户选择的译文","selection result remains readable instead of being replaced by an automatic full transcript");
            Target(viewer,"zh");original.SelectAll();
            selectedReply=new TaskCompletionSource<string>();selectedToken=CancellationToken.None;
            typeof(TextWindow).GetMethod("TranslateSelected",Private).Invoke(viewer,null);
            var pendingImage=new TaskCompletionSource<string[]>();CancellationToken imageToken=CancellationToken.None;
            viewer.TranslateImage=(input,token,to,provider)=>{imageToken=token;return pendingImage.Task;};
            Click(button);await Tick();Click(button);pendingImage.SetResult(new[]{"过期的整图译文"});await Tick();
            check(imageToken.IsCancellationRequested&&selectedToken.CanBeCanceled&&!selectedToken.IsCancellationRequested&&Field<CancellationTokenSource>(viewer,"selectionRequest")!=null,"cancelling the whole image never cancels an independent selected-text request");
            selectedReply.SetResult("仅所选文字");await Tick();
            check(transcript.Text=="仅所选文字"&&picture.Source==source&&Field<BitmapSource>(viewer,"translatedImage")==null,"late image reply cannot replace the selected-text answer or the original image");
            pendingImage=new TaskCompletionSource<string[]>();Click(button);await Tick();Click(button);pendingImage.SetResult(new[]{"旧整图译文"});await Tick();
            check(transcript.Text=="仅所选文字"&&Field<Button>(viewer,"copyResult").IsEnabled,"cancel image also preserves previously completed selected text for copying");
        }finally{viewer.Close();}
    }
    static async Task SelectedTextFeedback(Action<bool,string> check){
        var viewer=new TextWindow(Solid(255,255,255),"zh",Fixture(),null,null);
        var original=Field<TextBox>(viewer,"original");var answer=Field<TextBox>(viewer,"result");
        var card=Field<Border>(viewer,"resultCard");var button=Field<Button>(viewer,"translateSelection");var copy=Field<Button>(viewer,"copyResult");
        var first=new TaskCompletionSource<string>();var next=new TaskCompletionSource<string>();int calls=0;
        CancellationToken oldToken=CancellationToken.None;
        viewer.TranslateText=(text,token,target,provider)=>{if(++calls==1){oldToken=token;return first.Task;}return next.Task;};
        try{
            original.Select(7,4);Click(button);
            check(original.SelectedText=="open"&&card.Visibility==Visibility.Collapsed&&answer.Text==""&&!copy.IsEnabled&&button.IsEnabled&&Equals(button.Content,"取消翻译"),
                "pending selected-text translation keeps the original selection visible with no placeholder result and can be cancelled");
            Click(button);Click(button);first.SetResult("过期回复");await Tick();
            check(oldToken.IsCancellationRequested&&calls==2&&card.Visibility==Visibility.Collapsed&&Equals(button.Content,"取消翻译"),
                "cancel then immediately retry ignores even a provider that replies after cancellation");
            next.SetException(new InvalidOperationException("连接失败"));await Tick();
            check(original.SelectedText=="open"&&card.Visibility==Visibility.Visible&&Equals(button.Content,"重试翻译")&&!copy.IsEnabled,
                "a failed selected-text request exposes retry and keeps its error out of the copy action");
            viewer.TranslateText=(text,token,target,provider)=>Task.FromResult("打开");Click(button);
            check(answer.Text=="打开"&&copy.IsEnabled&&card.Visibility==Visibility.Visible&&Equals(button.Content,"翻译所选"),
                "retry displays the actual selected translation and enables copying in the same click");
        }finally{viewer.Close();first.TrySetCanceled();next.TrySetCanceled();}
    }
    static async Task PreparationCancellation(Action<bool,string> check){
        foreach(bool closeWindow in new[]{false,true}){
            var page=Fixture();var pending=new TaskCompletionSource<string[]>();CancellationToken preparationToken=CancellationToken.None;int prepares=0,translates=0,renders=0;
            var viewer=new TextWindow(Solid(255,255,255),"zh",(image,token)=>Task.FromResult(page)){ShowInTaskbar=false};bool closed=false;viewer.Closed+=(s,e)=>closed=true;
            viewer.PrepareImage=(p,rs,token)=>{prepares++;preparationToken=token;return pending.Task;};viewer.TranslateImage=(s,t,to,p)=>{translates++;return Task.FromResult(new[]{"unexpected"});};viewer.RenderImage=(s,rs,ts,t)=>{renders++;return Task.FromResult(new ImageTranslationResult());};
            try{
                viewer.Show();await viewer.EnsureOcr();Click(Field<Button>(viewer,"translatePicture"));check(prepares==1&&preparationToken.CanBeCanceled&&translates==0&&renders==0,"preparation is asynchronous and individually cancellable");
                if(closeWindow)viewer.Close();else Target(viewer,"en");pending.SetResult(new[]{"settings"});await Tick();
                check(preparationToken.IsCancellationRequested&&translates==0&&renders==0&&Field<BitmapSource>(viewer,"translatedImage")==null&&Field<ImageTranslationResult>(viewer,"imageTranslation")==null&&Field<CancellationTokenSource>(viewer,"imageRequest")==null,"closing or changing target cannot commit stale preparation or start translation");
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
