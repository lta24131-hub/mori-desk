using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QingJie;

// An unshown capture window, generated pixels and a protected acronym: no desktop,
// network, OCR model, clipboard or real image library required.
internal static class CaptureTranslationInteractionTests {
    static T Field<T>(object instance,string name){return (T)instance.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(instance);}
    static void Click(Button button){button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
    static BitmapSource Pixels(){
        var data=new byte[240*120*4];for(int y=0;y<120;y++)for(int x=0;x<240;x++){int i=4*(240*y+x);data[i]=(byte)x;data[i+1]=(byte)y;data[i+2]=(byte)(x^y);data[i+3]=255;}
        var source=BitmapSource.Create(240,120,96,96,PixelFormats.Bgra32,null,data,960);source.Freeze();return source;
    }
    static byte[] Bytes(BitmapSource source){int stride=source.PixelWidth*4;var result=new byte[stride*source.PixelHeight];source.CopyPixels(result,stride,0);return result;}
    static OcrPage Page(){var page=new OcrPage();page.Words.Add(new WordBox{Text="AI",Box=new Rect(8,8,22,16),Line=0});OcrService.IndexText(page);return page;}
    static OcrPage SingleLine(string text){
        var page=new OcrPage();int x=5;
        foreach(string part in text.Split(' ')){
            int width=part.Length*8;
            page.Words.Add(new WordBox{Text=part,Box=new Rect(x,8,width,16),Line=0});x+=width+5;
        }
        OcrService.IndexText(page);return page;
    }
    static void CheckSingleCommands(Action<bool,string> check){
        Func<string,string,string,string[]> local=(text,target,provider)=>{
            var page=SingleLine(text);return ImageTranslation.TranslateWholeLabel(page,ImageTranslation.Regions(page),target,provider);
        };
        check((local("New folder","zh","tencent")??new string[0]).SequenceEqual(new[]{"新建文件夹"}),"the complete indexed menu command uses its local translation");
        check((local("另存为…","en","tencent")??new string[0]).SequenceEqual(new[]{"Save as…"}),"the complete Chinese command keeps its ellipsis on the English route");
        check(local("New folder","en","tencent")==null&&local("New folder","zh","google")==null&&local("New folder","zh","offline")==null,"language direction and explicitly selected providers remain authoritative");
        check(local("Save changes.","zh","tencent")==null&&local("Create a folder","zh","tencent")==null,"ordinary prose and final punctuation keep the contextual translation path");
        check(local("Save","zh","tencent").Single()=="保存 / 挽救"&&local("Copy","zh","tencent").Single()=="复制 / 副本",
            "one complete isolated word offers both the interface and ordinary senses");
        check(local("Open","zh","google")==null&&local("Open","zh","offline")==null&&local("Open","en","tencent")==null,
            "dictionary readings respect the requested provider and target");
        check(local("Open.","zh","tencent")==null&&local("OPEN","zh","tencent")==null&&local("open water","zh","tencent")==null,
            "a punctuated word, acronym and phrase keep their own context");
        var partial=SingleLine("open water");var visible=ImageTranslation.Regions(SingleLine("open"));
        check(ImageTranslation.TranslateWholeLabel(partial,visible,"zh","tencent")==null,
            "a single surviving region cannot supply a dictionary gloss for a larger source");
        var menu=SingleLine("New folder");var regions=ImageTranslation.Regions(menu);
        check(ImageTranslation.TranslateWholeLabel(menu,new[]{regions[0],regions[0]},"zh","tencent")==null,"two regions cannot take the one-command shortcut");
        menu.Text+=" !";
        check(ImageTranslation.TranslateWholeLabel(menu,regions,"zh","tencent")==null,"an extra symbol omitted from a region is not silently dropped");
        menu.Text="New folder";regions[0].SourceLines[0].Text="New";
        check(ImageTranslation.TranslateWholeLabel(menu,regions,"zh","tencent")==null,"a region that differs from its OCR line remains on the contextual path");
        regions[0].SourceLines[0].Text=menu.Text;
        regions[0].SourceLines.Add(new TranslationSourceLine{Text="folder"});
        check(ImageTranslation.TranslateWholeLabel(menu,regions,"zh","tencent")==null,"a combined region cannot be interpreted as a single source line");
    }
    static async Task Until(Func<bool> done,string stage){
        var timer=System.Diagnostics.Stopwatch.StartNew();while(!done()){
            if(timer.ElapsedMilliseconds>6000)throw new TimeoutException(stage);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
    }
    static async Task CheckPinnedWord(Action<bool,string> check,SemaphoreSlim renderSlot){
        string directory=Path.Combine(Path.GetTempPath(),"Nook-PinnedWord-"+Guid.NewGuid().ToString("N"));
        string previousProvider=AppState.Settings.TranslationProvider;
        PinWindow window=null;bool ownsSlot=false;
        try{
            AppState.Settings.TranslationProvider="tencent";
            var store=new PinStore(directory);var image=Pixels();var pin=store.Add(image);
            pin.IsTranslation=true;pin.ShowTranslation=false;
            window=new PinWindow(store,pin,image);
            var action=window.ContextMenu.Items.OfType<MenuItem>().Single(item=>item.Name=="PinTranslation");
            var status=Field<TextBlock>(window,"translationStatus");
            var card=Field<Border>(window,"statusCard");var picture=Field<Image>(window,"picture");
            var page=SingleLine("Save");int reads=0;BitmapSource recognized=null;
            window.ReadPinOcr=(source,token)=>{reads++;recognized=source;return Task.FromResult(page);};
            const string expected="保存 / 挽救";
            await renderSlot.WaitAsync();ownsSlot=true;
            action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(()=>status.Text.Contains(expected),"pinned word preview before layout");
            check(reads==1&&ReferenceEquals(recognized,image)&&image.IsFrozen&&card.Visibility==Visibility.Visible&&
                ReferenceEquals(picture.Source,image)&&Field<ImageTranslationResult>(window,"translationResult")==null,
                "a pinned image shows both meanings of Save on its original pixels while layout waits");
            renderSlot.Release();ownsSlot=false;
            await Until(()=>Field<CancellationTokenSource>(window,"translationRequest")==null,"pinned word final result");
            var result=Field<ImageTranslationResult>(window,"translationResult");
            check(result!=null&&result.Transcript==expected&&pin.ShowTranslation&&page.Text=="Save",
                "the pin's complete transcript agrees with its first readable answer and preserves the source word");
            action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            check(!pin.ShowTranslation&&ReferenceEquals(picture.Source,image)&&card.Visibility==Visibility.Collapsed&&reads==1,
                "the pin's original-image menu restores its source and hides the gloss without recognizing it again");
        }finally{
            if(window!=null)window.Discard();
            if(ownsSlot)renderSlot.Release();
            AppState.Settings.TranslationProvider=previousProvider;
            if(Directory.Exists(directory)){
                foreach(string file in Directory.GetFiles(directory))File.Delete(file);
                Directory.Delete(directory);
            }
        }
    }
    internal static async Task Run(Action<bool,string> check){
        CheckSingleCommands(check);
        var sources=new[]{new TranslationRegion{Text="AI"},new TranslationRegion{Text="Save changes."}};
        check(CaptureWindow.FirstTranslatedLine(sources,new[]{"AI","保存更改。\n第二行"})=="保存更改。","preview skips a preserved acronym and shows translated content rather than a status label");
        check(CaptureWindow.FirstTranslatedLine(sources,new[]{"AI",new string('字',51)+"😀结尾"})==new string('字',51)+"…","a clipped preview cannot split a surrogate pair");
        var image=Pixels();var win=new CaptureWindow(new Snapshot{Image=image,Bounds=new System.Drawing.Rectangle(-240,0,240,120)},null,new CaptureFeatures{Translation=true,SmartSelection=false});
        var surface=Field<ScreenshotSurface>(win,"surface");var button=Field<Button>(win,"translateButton");
        var glyph=button.Content as ToolIcon;
        check(glyph!=null&&AutomationProperties.GetName(button)=="翻译选区","capture initially displays the translation tool icon with an accessible action");
        Func<string,bool> iconAction=hint=>ReferenceEquals(button.Content,glyph)&&Equals(button.ToolTip,hint)&&AutomationProperties.GetName(button)==hint;
        var crop=new Rect(13,11,120,60);surface.Measure(new Size(240,120));surface.Arrange(new Rect(0,0,240,120));surface.Selection=crop;
        var pending=new TaskCompletionSource<OcrPage>();var firstStarted=new TaskCompletionSource<bool>();int reads=0;CancellationToken firstToken=CancellationToken.None;
        var page=Page();var expected=Bytes(new CroppedBitmap(image,new Int32Rect(13,11,120,60)));BitmapSource readCrop=null;
        var language=(Button)LogicalTreeHelper.FindLogicalNode(win,"CaptureLanguage");
        Action<int> target=i=>((MenuItem)language.ContextMenu.Items[i]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        win.ReadCaptureOcr=(source,token)=>{reads++;readCrop=source;if(reads==1){firstToken=token;firstStarted.TrySetResult(true);return pending.Task;}return Task.FromResult(page);};
        var slot=(SemaphoreSlim)typeof(ImageTranslation).GetField("renderSlot",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);bool ownsSlot=false;
        try{
            Click(button);check(iconAction("正在翻译 · 点击取消")&&button.IsEnabled&&Field<CancellationTokenSource>(win,"translationCancel")!=null&&!surface.ShowTranslation,"capture advertises a cancellable request immediately; source remains visible");
            await Until(()=>firstStarted.Task.IsCompleted,"OCR entry");
            target(1);check(reads==1&&!firstToken.IsCancellationRequested&&Field<string>(win,"translationTarget")=="en","changing target during cold OCR keeps the same worker and request");
            check(readCrop.IsFrozen&&readCrop.PixelWidth==120&&readCrop.PixelHeight==60&&Bytes(readCrop).SequenceEqual(expected),"OCR receives exactly the selected source pixels at their true origin");
            Click(button);check(firstToken.IsCancellationRequested&&Field<CancellationTokenSource>(win,"translationCancel")==null,"second click cancels while OCR is busy");
            pending.SetResult(Page());await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(Field<OcrPage>(win,"recognizedPage")==null&&surface.TranslationImage==null&&!surface.ShowTranslation,"late OCR cannot pollute the crop cache or reveal a cancelled result");
            await slot.WaitAsync();ownsSlot=true;Click(button);
            await Until(()=>Field<string[]>(win,"captureTranslatedText")!=null,"translated words before bitmap");
            var status=Field<TextBlock>(win,"translationStatus");check(ReferenceEquals(Field<OcrPage>(win,"recognizedPage"),page)&&status.Visibility==Visibility.Visible&&status.Inlines.OfType<System.Windows.Documents.Hyperlink>().Any()&&!surface.ShowTranslation,"copy action is available on the untouched original while bitmap layout is queued");
            check(status.Inlines.OfType<System.Windows.Documents.Run>().Any(r=>r.Text=="AI"),"capture shows the first returned text without waiting for bitmap layout or opening the clipboard");
            var obsolete=Field<CancellationTokenSource>(win,"translationCancel");target(0);
            await Until(()=>Field<string[]>(win,"captureTranslatedText")!=null,"retargeting during layout");
            check(reads==2&&obsolete.IsCancellationRequested&&!ReferenceEquals(obsolete,Field<CancellationTokenSource>(win,"translationCancel")),"retargeting during layout restarts automatically and reuses the exact recognized crop");
            Click(button);check(reads==2&&surface.TranslationImage==null,"capture can cancel downstream work after recognition without revealing an unfinished image");
            slot.Release();ownsSlot=false;Click(button);
            await Until(()=>surface.ShowTranslation&&Field<CancellationTokenSource>(win,"translationCancel")==null,"retry result");
            check(reads==2&&surface.TranslationBounds==crop,"retry on the same selection reuses its successful OCR and commits to the correct crop");
            target(1);await Until(()=>surface.ShowTranslation&&Field<CancellationTokenSource>(win,"translationCancel")==null,"switch completed translation");
            check(reads==2&&Field<string>(win,"imageTranslationTarget")=="en","switching the language of a visible result applies without an extra Translate click or OCR run");
            Click(button);check(iconAction("查看译图")&&!surface.ShowTranslation&&Bytes(surface.Export(false,false)).SequenceEqual(expected),"compare with original immediately restores exact source pixels");
            Click(button);check(iconAction("查看原图")&&surface.ShowTranslation&&reads==2,"switch back to translated view without another OCR job");
            typeof(CaptureWindow).GetMethod("ResetTranslation",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(win,null);
            var renderer=win.RenderCaptureTranslation;string[] firstTranslation=null;
            win.RenderCaptureTranslation=(source,regions,values,token)=>{firstTranslation=values as string[];throw new OperationCanceledException();};
            Click(button);await Until(()=>Field<CancellationTokenSource>(win,"translationCancel")==null,"layout timeout");
            check(firstTranslation!=null&&!surface.ShowTranslation&&status.Visibility==Visibility.Visible&&status.Inlines.OfType<System.Windows.Documents.Hyperlink>().Any()&&iconAction("重试图片排版，不重新翻译"),"layout timeout preserves copyable text, the original image, and a layout-only retry");
            bool sameTranslation=false;win.RenderCaptureTranslation=(source,regions,values,token)=>{sameTranslation=ReferenceEquals(firstTranslation,values);return renderer(source,regions,values,token);};
            Click(button);await Until(()=>surface.ShowTranslation&&Field<CancellationTokenSource>(win,"translationCancel")==null,"layout-only retry");
            check(sameTranslation&&reads==2,"layout-only retry uses the same returned translation and OCR");win.RenderCaptureTranslation=renderer;
            surface.Selection=new Rect(50,20,120,60);typeof(CaptureWindow).GetMethod("ResetTranslation",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(win,null);Click(button);
            await Until(()=>surface.ShowTranslation&&Field<CancellationTokenSource>(win,"translationCancel")==null,"second crop");
            check(reads==3&&Bytes(readCrop).SequenceEqual(Bytes(new CroppedBitmap(image,new Int32Rect(50,20,120,60)))),"same-size selection at a new origin must not reuse the old OCR");
            // Force only the layout outcome: OCR and the actual button interaction
            // still follow the normal path. Use the existing network-free fixture.
            var renderedResult=Field<ImageTranslationResult>(win,"imageTranslationResult");
            renderedResult.UnplacedCount=1;
            Click(button);check(!surface.ShowTranslation&&!status.Inlines.OfType<System.Windows.Documents.Run>().Any(r=>r.Text=="AI"),"comparing original hides the single-region fallback answer");
            Click(button);check(surface.ShowTranslation&&status.Inlines.OfType<System.Windows.Documents.Run>().Any(r=>r.Text=="AI"),"unplaceable single label stays readable without opening another window");
            var menu=SingleLine("New folder");
            string previousProvider=AppState.Settings.TranslationProvider;
            try{
                typeof(CaptureWindow).GetMethod("ResetTranslation",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(win,null);
                target(0);AppState.Settings.TranslationProvider="tencent";
                surface.Selection=new Rect(30,16,140,60);
                win.ReadCaptureOcr=(source,token)=>Task.FromResult(menu);
                await slot.WaitAsync();ownsSlot=true;Click(button);
                await Until(()=>Field<string[]>(win,"captureTranslatedText")!=null,"exact menu before bitmap");
                check(!surface.ShowTranslation&&status.Inlines.OfType<System.Windows.Documents.Run>().Any(r=>r.Text=="新建文件夹"),"the screenshot button shows the localized command while image rendering waits");
                slot.Release();ownsSlot=false;
                await Until(()=>surface.ShowTranslation&&Field<CancellationTokenSource>(win,"translationCancel")==null,"exact menu bitmap");
                check(menu.Text=="New folder"&&Field<ImageTranslationResult>(win,"imageTranslationResult").Transcript=="新建文件夹","the completed screenshot keeps the same translation and the original OCR text");
            }finally{AppState.Settings.TranslationProvider=previousProvider;}
            // Returning to OCR after discarding the translated bitmap (or a provider
            // failure) is still the same frozen selection. Opening the reader must
            // not wait for the full worker queue again.
            typeof(CaptureWindow).GetMethod("ResetTranslation",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(win,null);
            TextWindow reader=null;win.PresentTextViewer=v=>reader=v;
            Click((Button)LogicalTreeHelper.FindLogicalNode(win,"CaptureOcr"));
            try{check(reader!=null&&ReferenceEquals(Field<OcrPage>(reader,"page"),menu)&&Field<TextBox>(reader,"original").Text=="New folder","independent reader immediately shows the already recognized selection even without a translated bitmap");}
            finally{if(reader!=null)reader.Close();}
        }finally{if(ownsSlot)slot.Release();pending.TrySetCanceled();win.Close();}
        await CheckPinnedWord(check,slot);
    }
}
