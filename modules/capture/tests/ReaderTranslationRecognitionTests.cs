using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QingJie;

internal static class ReaderTranslationRecognitionTests {
    static T Field<T>(TextWindow viewer,string name){
        return (T)typeof(TextWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(viewer);
    }
    static void Translate(TextWindow viewer){
        var button=Field<Button>(viewer,"translatePicture");button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,button));
    }
    static async Task Until(Func<bool> done,string stage){
        var timer=Stopwatch.StartNew();
        while(!done()){
            if(timer.ElapsedMilliseconds>6000)throw new TimeoutException(stage);
            await Dispatcher.Yield(DispatcherPriority.Background);
        }
    }
    static BitmapSource Pixels(bool twoRows){
        const int w=160,h=72;var data=Enumerable.Repeat((byte)255,w*h*4).ToArray();
        for(int y=12;y<(twoRows?60:28);y++){
            if(y>=28&&y<44)continue;
            for(int x=10;x<90;x++){
                if(x%12>=5)continue;
                int at=(y*w+x)*4;data[at]=data[at+1]=data[at+2]=24;
            }
        }
        var image=BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,data,w*4);image.Freeze();return image;
    }
    static OcrPage Page(){
        var page=new OcrPage();
        page.Words.Add(new WordBox{Text="New",Box=new Rect(10,12,28,16)});
        page.Words.Add(new WordBox{Text="folder",Box=new Rect(43,12,46,16)});
        OcrService.IndexText(page);return page;
    }
    internal static async Task Run(Action<bool,string> check){
        var source=Pixels(false);var recognized=Page();
        var full=new TaskCompletionSource<OcrPage>();var fast=new TaskCompletionSource<OcrPage>();
        CancellationToken fullToken=CancellationToken.None,fastToken=CancellationToken.None;
        int reads=0,quickReads=0,translations=0;string input=null;
        var reader=new TextWindow(source,"zh",(image,t)=>{reads++;fullToken=t;return full.Task;},
            recognizeForTranslation:(image,t)=>{quickReads++;fastToken=t;return fast.Task;});
        reader.TranslateImage=(texts,t,to,p)=>{translations++;input=texts[0];return Task.FromResult(new[]{"新建文件夹"});};
        reader.RenderImage=(image,regions,values,t)=>Task.FromResult(new ImageTranslationResult{Image=image,Transcript=values[0]});
        try{
            reader.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var sidebar=reader.EnsureOcr();
            check(reads==1&&quickReads==0,"opening the file reader alone starts its complete OCR reader once");
            Translate(reader);await Until(()=>fullToken.IsCancellationRequested,"short image hands off from full OCR");
            check(quickReads==0,"the short recognition waits for the previous worker to release the image");
            full.SetCanceled();await Until(()=>quickReads==1,"short reader starts");
            Translate(reader);
            check(!fastToken.IsCancellationRequested,"cancelling translation keeps the shared sidebar OCR alive");
            fast.SetResult(recognized);await sidebar;
            var original=Field<TextBox>(reader,"original");original.Select(4,6);
            check(original.SelectedText=="folder"&&OcrCorrection.FindRegion(recognized,4,6)==0&&translations==0,
                "the unmodified words and their positions are selectable after cancelling whole-image translation");
            reader.CorrectRegion(0,"Reviewed label");Translate(reader);
            await Until(()=>Field<ImageTranslationResult>(reader,"imageTranslation")!=null,"translation of corrected words");
            check(reads==1&&quickReads==1&&input=="Reviewed label"&&original.Text=="Reviewed label","translating a correction reuses the current edited text and its region");
        }finally{reader.Close();full.TrySetCanceled();fast.TrySetCanceled();}

        var dense=new TaskCompletionSource<OcrPage>();int denseFast=0;CancellationToken denseToken=CancellationToken.None;
        var pageReader=new TextWindow(Pixels(true),"zh",(image,t)=>{denseToken=t;return dense.Task;},
            recognizeForTranslation:(image,t)=>{denseFast++;return Task.FromResult(recognized);});
        pageReader.TranslateImage=(texts,t,to,p)=>Task.FromResult(new[]{"新建文件夹"});
        pageReader.RenderImage=(image,regions,values,t)=>Task.FromResult(new ImageTranslationResult{Image=image,Transcript=values[0]});
        try{
            var sidebar=pageReader.EnsureOcr();Translate(pageReader);dense.SetResult(recognized);
            await sidebar;await Until(()=>Field<ImageTranslationResult>(pageReader,"imageTranslation")!=null,"multi-line reader");
            check(!TextWindow.SingleReaderLine(Pixels(true),CancellationToken.None)&&!denseToken.IsCancellationRequested&&denseFast==0,
                "a second row within a small image keeps the original full reader and translation shares its result");
        }finally{pageReader.Close();dense.TrySetCanceled();}

        var pending=new TaskCompletionSource<OcrPage>();var opened=0;CancellationToken closing=CancellationToken.None;
        var direct=new TextWindow(source,"zh",(image,t)=>{opened++;return Task.FromResult(recognized);},
            recognizeForTranslation:(image,t)=>{closing=t;return pending.Task;});
        int late=0;direct.TranslateImage=(texts,t,to,p)=>{late++;return Task.FromResult(new[]{"过期"});};
        try{
            Translate(direct);direct.Close();pending.SetResult(recognized);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(opened==0&&closing.IsCancellationRequested&&late==0&&Field<OcrPage>(direct,"page")==null,
                "an explicit first translation starts exactly one short reader and closing drops its late answer");
        }finally{pending.TrySetCanceled();}
    }
}
