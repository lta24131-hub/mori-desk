using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;
using Geometry = QingJie.Geometry;
class Tests {
    static int count;
    static void Check(bool condition,string name){if(!condition)throw new Exception(name);Console.WriteLine("PASS "+name);count++;}
    static T Await<T>(Task<T> task){var frame=new System.Windows.Threading.DispatcherFrame();task.ContinueWith(t=>frame.Continue=false);System.Windows.Threading.Dispatcher.PushFrame(frame);return task.GetAwaiter().GetResult();}
    static void Finish(Task task){Await(task.ContinueWith(t=>{t.GetAwaiter().GetResult();return true;}));}
    static void LineToolTests(){
        foreach(double width in new[]{1.0,3.0,12.0})foreach(var end in new[]{new Point(80,50),new Point(50,80),new Point(80,80),new Point(20,20)}){
            var line=new Annotation {Kind="line",Start=new Point(50,50),End=end,Width=width,Color="#EF4444"};
            var actualVisual=new DrawingVisual();using(var d=actualVisual.RenderOpen())line.Draw(d);
            var expectedVisual=new DrawingVisual();using(var d=expectedVisual.RenderOpen())d.DrawLine(new Pen(Ui.Brush(line.Color),width){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round},line.Start,line.End);
            var actual=new RenderTargetBitmap(100,100,96,96,PixelFormats.Pbgra32);actual.Render(actualVisual);var expected=new RenderTargetBitmap(100,100,96,96,PixelFormats.Pbgra32);expected.Render(expectedVisual);
            Check(SamePixels(actual,expected),"straight line has no arrowhead and preserves direction/color/width "+width+" "+end);
        }
        var image=new RenderTargetBitmap(300,180,96,96,PixelFormats.Pbgra32);image.Freeze();
        var capture=new CaptureWindow(new Snapshot {Image=image,Bounds=new System.Drawing.Rectangle(100,100,300,180)});
        var surface=((System.Windows.Controls.Grid)capture.Content).Children.OfType<ScreenshotSurface>().Single();surface.Selection=new Rect(0,0,200,100);
        Func<string,System.Windows.Controls.Button> button=name=>(System.Windows.Controls.Button)LogicalTreeHelper.FindLogicalNode(capture,name);
        Action<string> click=name=>button(name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        click("CaptureTool_arrow");Check(capture.SelectedTool=="arrow"&&(bool)button("LineMode_arrowFilled").Tag,"arrow tool starts in tapered filled mode with the matching subtool selected");
        var originalMark=new Annotation {Kind="arrow",Start=new Point(10,10),End=new Point(80,70)};surface.Marks.Add(originalMark);
        click("LineMode_line");Check(capture.SelectedTool=="line"&&(bool)button("LineMode_line").Tag&&button("LineMode_arrow").Tag==null,"line mode switches drawing kind and selected indicator");
        Check(originalMark.Kind=="arrow"&&surface.Marks.Count==1&&surface.Selection==new Rect(0,0,200,100),"changing line mode never rewrites existing annotations or selection");
        Check(System.Windows.Automation.AutomationProperties.GetName(button("CaptureTool_arrow")).Contains("直线"),"main line-tool label follows its active mode");
        click("CaptureTool_rect");Check(((System.Windows.Controls.Panel)button("LineMode_line").Parent).Visibility==Visibility.Collapsed,"line options hide for unrelated tools");
        click("CaptureTool_arrow");Check(capture.SelectedTool=="line"&&((System.Windows.Controls.Panel)button("LineMode_line").Parent).Visibility==Visibility.Visible,"returning to the linear tool remembers the chosen mode in this capture");
        click("LineMode_arrow");Check(capture.SelectedTool=="arrow"&&System.Windows.Automation.AutomationProperties.GetName(button("CaptureTool_arrow")).Contains("箭头"),"line switches back to arrow without losing existing marks");capture.Close();
    }
    static void PerformanceTests(){
        var raw=new byte[37*79*4];new Random(25).NextBytes(raw);using(var stream=new MemoryStream(raw))using(var reader=new BinaryReader(stream)){var received=OcrWorker.ReadBitmap(reader,37,79);var copied=new byte[raw.Length];received.CopyPixels(copied,37*4,0);Check(received.IsFrozen&&raw.SequenceEqual(copied),"streamed worker image preserves odd-width pixels and final partial strip");}
        bool truncated=false;using(var stream=new MemoryStream(raw.Take(raw.Length-1).ToArray()))using(var reader=new BinaryReader(stream))try{OcrWorker.ReadBitmap(reader,37,79);}catch(EndOfStreamException){truncated=true;}Check(truncated,"incomplete OCR pipe image is rejected instead of recognizing corrupt pixels");
        OcrResultContractTests.Run(Check);
        long time=0;var cache=new OcrResultCache(()=>time);
        var page=new OcrPage {Text="Hello",Words=new System.Collections.Generic.List<WordBox>{new WordBox {Text="Hello",Box=new Rect(2,3,20,8)}}};
        cache.Put("a",page);cache.Put("b",page);cache.Put("c",page);Check(cache.Get("a")!=null,"last three screenshots can reuse OCR without retaining images");cache.Put("d",page);Check(cache.Get("b")==null&&cache.Get("c")!=null,"OCR cache evicts least recently used transcript");
        time=120;Check(cache.Get("a")==null&&cache.Get("c")==null&&cache.Get("d")==null,"all OCR cache entries expire without a timer");
        var large=new OcrPage {Text=new string('x',15000),Words=page.Words};cache.Put("a",large);cache.Put("b",large);cache.Put("c",large);Check(cache.Get("a")==null&&cache.Get("b")!=null&&cache.Get("c")!=null,"OCR cache bounds total retained text");
        var dense=new OcrPage {Text="dense",Words=Enumerable.Range(0,500).Select(i=>new WordBox {Text="word",Box=new Rect(i%20*24,i/20*16,20,12),TextStart=i*5}).ToList()};cache.Put("d",dense);cache.Put("e",dense);cache.Put("f",dense);Check(cache.Get("d")==null&&cache.Get("e")!=null&&cache.Get("f")!=null,"OCR cache bounds total retained word boxes");
        foreach(int width in new[]{1,2,3,4,5,33,1001}){
            var samples=new byte[width*27];new Random(width).NextBytes(samples);var source=BitmapSource.Create(width,27,96,96,PixelFormats.Gray8,null,samples,width);source.Freeze();var pixels=new OcrPixels(source);
            byte[] encoded;using(var memory=new MemoryStream()){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(source));encoder.Save(memory);encoded=memory.ToArray();}
            using(var reference=Tesseract.Pix.LoadFromMemory(encoded))using(var direct=pixels.TesseractBitmap()){
                var a=reference.GetData();var b=direct.GetData();var rowA=new byte[a.WordsPerLine*4];var rowB=new byte[b.WordsPerLine*4];bool equal=reference.Depth==direct.Depth&&reference.XRes==direct.XRes&&reference.YRes==direct.YRes;
                for(int y=0;y<27;y++){System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(a.Data,y*rowA.Length),rowA,0,rowA.Length);System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(b.Data,y*rowB.Length),rowB,0,rowB.Length);for(int x=0;x<width;x++)if(rowA[x^3]!=rowB[x^3])equal=false;}
                Check(equal,"direct Tesseract pixels and DPI match lossless PNG at width "+width);
            }
            using(var bitmap=pixels.WindowsBitmap())using(var buffer=bitmap.LockBuffer(Windows.Graphics.Imaging.BitmapBufferAccessMode.Read))using(var reference=buffer.CreateReference()){
                IntPtr address;uint capacity;((IMemoryBufferByteAccess)reference).GetBuffer(out address,out capacity);var plane=buffer.GetPlaneDescription(0);var row=new byte[width];bool equal=bitmap.BitmapPixelFormat==Windows.Graphics.Imaging.BitmapPixelFormat.Gray8;
                for(int y=0;y<27;y++){System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(address,plane.StartIndex+y*plane.Stride),row,0,width);if(!row.SequenceEqual(samples.Skip(y*width).Take(width)))equal=false;}Check(equal,"direct Windows OCR samples match every grayscale pixel at width "+width);
            }
        }
        var highlights=new OcrHighlights();highlights.SetSelection(dense,0,dense.Words.Count*5);highlights.Measure(new Size(480,400));highlights.Arrange(new Rect(0,0,480,400));highlights.UpdateLayout();
        Check(VisualTreeHelper.GetChildrenCount(highlights)==0,"dense OCR selection allocates no per-word WPF controls");
        var rendered=new RenderTargetBitmap(480,400,96,96,PixelFormats.Pbgra32);rendered.Render(highlights);var pixel=new byte[4];rendered.CopyPixels(new Int32Rect(2,3,1,1),pixel,4,0);Check(pixel[3]>0,"single-surface highlights remain visible over selected words");highlights.SetSelection(null,0,0);highlights.UpdateLayout();var blank=new RenderTargetBitmap(480,400,96,96,PixelFormats.Pbgra32);blank.Render(highlights);blank.CopyPixels(new Int32Rect(2,3,1,1),pixel,4,0);Check(pixel[3]==0,"clearing selection releases transcript and clears highlights");
        int calls=0;var window=new TextWindow(rendered,"zh",(image,token)=>{calls++;if(calls==1){var failed=new TaskCompletionSource<OcrPage>();failed.SetException(new IOException("synthetic recognition failure"));return failed.Task;}return Task.FromResult(page);});
        bool failure=false;try{Await(window.EnsureOcr());}catch(IOException){failure=true;}Check(failure&&Await(window.EnsureOcr()).Text=="Hello"&&calls==2,"OCR failure can be retried without reopening the image");Await(window.EnsureOcr());Check(calls==2,"successful window OCR remains reused");window.Close();
    }
    static void WritingTests(){
        var previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        try{
            int calls=0;string target=null;CancellationToken token=CancellationToken.None;TaskCompletionSource<string> reply=null;
            var window=new WriteTranslationWindow((text,cancel,language)=>{calls++;target=language;token=cancel;reply=new TaskCompletionSource<string>();return reply.Task;});
            var input=(System.Windows.Controls.TextBox)LogicalTreeHelper.FindLogicalNode(window,"TranslationInput");
            var output=(System.Windows.Controls.TextBox)LogicalTreeHelper.FindLogicalNode(window,"TranslationOutput");
            var languageBox=(System.Windows.Controls.Button)LogicalTreeHelper.FindLogicalNode(window,"TranslationTarget");
            var copy=(System.Windows.Controls.Button)LogicalTreeHelper.FindLogicalNode(window,"CopyTranslation");
            var button=(System.Windows.Controls.Button)LogicalTreeHelper.FindLogicalNode(window,"TranslateInput");
            var resultArea=(System.Windows.Controls.StackPanel)LogicalTreeHelper.FindLogicalNode(window,"TranslationResultArea");
            Check(window.WindowStyle==WindowStyle.None&&window.Width==400&&window.SizeToContent==SizeToContent.Height&&resultArea.Visibility==Visibility.Collapsed&&copy.Visibility==Visibility.Collapsed,"minimal writer hides empty output and copy action without a title bar");
            Check(!button.IsEnabled&&!copy.IsEnabled&&languageBox.Content.ToString().Contains("英文"),"writing window defaults to English with no automatic request");
            input.Text="请确认交货日期，谢谢。";Check(calls==0&&button.IsEnabled,"typing alone never sends text or runs OCR");
            var work=window.TranslateNow();Check(calls==1&&target=="en"&&!copy.IsEnabled,"writing explicitly requests English and disables copying until success");reply.SetResult("Please confirm the delivery date. Thank you.");Finish(work);Check(output.Text.Contains("delivery")&&copy.IsEnabled,"writing result can be selected and copied");
            languageBox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));Check(output.Text==""&&!copy.IsEnabled&&calls==1,"changing target clears old translation without automatic upload");
            work=window.TranslateNow();Check(target=="zh","writing can also translate into Chinese");input.Text="新的内容";Check(token.IsCancellationRequested&&!copy.IsEnabled,"editing cancels old translation");reply.SetResult("obsolete");Finish(work);Check(output.Text=="","late response cannot overwrite changed input");
            Check(resultArea.Visibility==Visibility.Collapsed&&copy.Visibility==Visibility.Collapsed,"clearing obsolete translation also collapses unused result UI");
            work=window.TranslateNow();languageBox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));Check(token.IsCancellationRequested,"target switch cancels in-flight writing request");reply.SetResult("旧译文");Finish(work);Check(output.Text==""&&!copy.IsEnabled,"late response from previous language stays hidden");
            work=window.TranslateNow();AppState.Settings.TranslationProvider="google";AppState.ProviderChanged();Check(token.IsCancellationRequested,"provider switch cancels in-flight input request");reply.SetResult("previous service");Finish(work);Check(output.Text==""&&!copy.IsEnabled,"provider switch never mislabels an old translation");AppState.Settings.TranslationProvider="tencent";AppState.ProviderChanged();
            input.Text=new string('字',5001);int before=calls;Finish(window.TranslateNow());Check(calls==before&&!button.IsEnabled&&input.Text.Length==5001,"oversized input is explained without silently truncating or uploading");
            input.Text="测试";work=window.TranslateNow();reply.SetException(new InvalidOperationException("test service failure"));Finish(work);Check(output.Text==""&&!copy.IsEnabled&&button.IsEnabled,"service failure preserves input and permits retry");
            work=window.TranslateNow();window.Close();Check(token.IsCancellationRequested,"closing writing window cancels network work");reply.SetResult("late");Finish(work);Check(output.Text=="","closed writing window ignores completed request");
        }finally{SynchronizationContext.SetSynchronizationContext(previous);}
    }
    static void LanguageSelectionTests(){
        var preferences=new Preferences {OcrLanguage="kor"};var packs=new System.Collections.Generic.HashSet<string>{"kor","fra"};int saves=0,removes=0;bool failSave=false,failRemove=false;
        var selection=new OcrLanguageSelection(preferences,()=>{saves++;if(failSave)throw new IOException("synthetic save error");},id=>packs.Contains(id),id=>{removes++;if(failRemove)throw new IOException("synthetic delete error");packs.Remove(id);});
        Check(selection.CurrentIds.SequenceEqual(new[]{"kor"})&&saves==0,"legacy single language is preserved without writing settings");
        Check(OcrLanguagePacks.NormalizeSelection("fra+kor+kor+unknown+../eng")=="kor+fra"&&OcrLanguagePacks.NormalizeSelection(null)=="","multi-language list is allowlisted, deduplicated and canonical for cache keys");
        selection.SetEnabled("fra",true);Check(preferences.OcrLanguage=="kor+fra"&&saves==1,"checking a second OCR language adds it instead of replacing the first");
        selection.SetEnabled("fra",true);Check(saves==1,"checking an already active language is idempotent");
        var restored=new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Preferences>(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(preferences));Check(restored.OcrLanguage=="kor+fra","multiple enabled languages survive settings serialization");
        bool missing=false;try{selection.SetEnabled("jpn",true);}catch(InvalidOperationException){missing=true;}Check(missing&&preferences.OcrLanguage=="kor+fra"&&saves==1,"missing language cannot replace or change existing selections");
        selection.SetEnabled("kor",false);Check(preferences.OcrLanguage=="fra"&&packs.Contains("kor"),"unchecking one language preserves the other and keeps its downloaded model");
        failSave=true;bool failed=false;try{selection.SetEnabled("kor",true);}catch(IOException){failed=true;}Check(failed&&preferences.OcrLanguage=="fra","failed multi-select save rolls back the entire selection");failSave=false;selection.SetEnabled("kor",true);
        packs.Add("jpn");failSave=true;failed=false;try{selection.SetEnabled("jpn",true);}catch(IOException){failed=true;}Check(failed&&preferences.OcrLanguage=="kor+fra"&&packs.Contains("jpn"),"downloaded language is not falsely enabled after save failure");failSave=false;selection.SetEnabled("jpn",true);
        failSave=true;failed=false;try{selection.Remove("jpn");}catch(IOException){failed=true;}Check(failed&&packs.Contains("jpn")&&removes==0&&preferences.OcrLanguage=="kor+jpn+fra","failed disable save prevents active model deletion");failSave=false;
        failRemove=true;failed=false;try{selection.Remove("jpn");}catch(IOException){failed=true;}Check(failed&&packs.Contains("jpn")&&preferences.OcrLanguage=="kor+jpn+fra","failed uninstall restores all previous selections");failRemove=false;
        selection.Remove("jpn");Check(!packs.Contains("jpn")&&preferences.OcrLanguage=="kor+fra"&&packs.Contains("kor")&&packs.Contains("fra"),"uninstall only removes its own language and preserves other selected models");
        bool unknown=false;try{selection.SetEnabled("unknown",true);}catch(ArgumentException){unknown=true;}Check(unknown&&preferences.OcrLanguage=="kor+fra","invalid language cannot change the multi-selection");
        preferences.OcrLanguage="kor+jpn";Check(selection.CurrentIds.Length==2&&selection.EffectiveIds.SequenceEqual(new[]{"kor"}),"missing model is distinguished from effective languages without losing legacy intent");selection.Remove("jpn");Check(preferences.OcrLanguage=="kor","missing selection can be removed without touching working models");
        var previousPins=AppState.Pins;AppState.Pins=new PinStore(Path.Combine(Path.GetTempPath(),"Luma-Language-UI-"+Guid.NewGuid().ToString("N")));SettingsWindow window=null;
        var context=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        try{
            var uiPreferences=new Preferences {OcrLanguage="kor"};int uiSaves=0;bool uiFail=false;var uiSelection=new OcrLanguageSelection(uiPreferences,()=>{if(uiFail)throw new IOException("save error");uiSaves++;},id=>packs.Contains(id),id=>packs.Remove(id));window=new SettingsWindow(uiSelection);
            var korean=(System.Windows.Controls.CheckBox)LogicalTreeHelper.FindLogicalNode(window,"OcrLanguage_kor");var french=(System.Windows.Controls.CheckBox)LogicalTreeHelper.FindLogicalNode(window,"OcrLanguage_fra");var english=(System.Windows.Controls.CheckBox)LogicalTreeHelper.FindLogicalNode(window,"OcrBaseEnglish");var chinese=(System.Windows.Controls.CheckBox)LogicalTreeHelper.FindLogicalNode(window,"OcrBaseChinese");
            Check(korean.IsChecked==true&&french.IsChecked==false&&uiSaves==0&&english.IsChecked==true&&chinese.IsChecked==true&&!english.IsEnabled&&!chinese.IsEnabled,"settings use independent checkboxes with fixed Chinese and English");
            french.IsChecked=true;Check(uiPreferences.OcrLanguage=="kor+fra"&&korean.IsChecked==true&&uiSaves==1,"actual checkbox event enables two OCR languages immediately");
            korean.IsChecked=false;Check(uiPreferences.OcrLanguage=="fra"&&french.IsChecked==true&&uiSaves==2,"unchecking Korean does not uncheck French or English");
            uiFail=true;korean.IsChecked=true;Check(korean.IsChecked==false&&french.IsChecked==true&&uiPreferences.OcrLanguage=="fra","failed checkbox save restores visible state and active languages");uiFail=false;
            TaskCompletionSource<bool> reply=null;CancellationToken pending=CancellationToken.None;int downloads=0;
            using(var panel=new OcrLanguagePanel(uiSelection,(id,progress,cancel)=>{downloads++;pending=cancel;reply=new TaskCompletionSource<bool>();return reply.Task;})){
                var work=panel.Download("jpn");Check(downloads==1&&uiPreferences.OcrLanguage=="fra","starting download does not replace any active language");packs.Add("jpn");reply.SetResult(true);Finish(work);Check(uiPreferences.OcrLanguage=="jpn+fra","successful download adds one language alongside existing choices");
                work=panel.Download("rus");Finish(panel.Download("rus"));Check(pending.IsCancellationRequested,"second download click cancels the in-flight download");reply.SetCanceled();Finish(work);Check(uiPreferences.OcrLanguage=="jpn+fra","cancelled language download preserves every prior selection");
                work=panel.Download("rus");reply.SetException(new IOException("network failure"));Finish(work);Check(uiPreferences.OcrLanguage=="jpn+fra","failed language download preserves every prior selection");
                work=panel.Download("rus");panel.Dispose();reply.SetResult(true);Finish(work);Check(pending.IsCancellationRequested&&uiPreferences.OcrLanguage=="jpn+fra","closing language panel cancels download and refuses late activation");
            }
            using(var preview=new OcrLanguagePanel(new OcrLanguageSelection(new Preferences {OcrLanguage="kor+jpn"},()=>{},id=>packs.Contains(id),id=>{}))){
                var root=new System.Windows.Controls.Border {Background=Brushes.White,Padding=new Thickness(20),Child=preview};root.Measure(new Size(468,double.PositiveInfinity));root.Arrange(new Rect(0,0,468,root.DesiredSize.Height));root.UpdateLayout();var rendered=new RenderTargetBitmap(468,(int)Math.Ceiling(root.ActualHeight),96,96,PixelFormats.Pbgra32);rendered.Render(root);ImageFiles.Write(rendered,Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ocr-languages-preview.png"));
            }
        }finally{if(window!=null)window.Close();AppState.Pins=previousPins;SynchronizationContext.SetSynchronizationContext(context);}
    }
    static void MultilingualOcrTests(){
        var visual=new DrawingVisual();using(var d=visual.RenderOpen()){
            d.DrawRectangle(Brushes.White,null,new Rect(0,0,1200,400));
            var lines=new[]{new[]{"Save the screenshot","Arial"},new[]{"保存图片，确认设计方案。","Microsoft YaHei UI"},new[]{"새로운 경험을 위해 제품을 디자인했습니다.","Malgun Gothic"},new[]{"画像を保存してください。新しいデザインです。","Yu Gothic"},new[]{"Сохранить изображение. Новый дизайн.","Segoe UI"}};
            for(int i=0;i<lines.Length;i++)d.DrawText(new FormattedText(lines[i][0],System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface(lines[i][1]),24,Brushes.Black,1),new Point(24,24+i*70));
        }
        var image=new RenderTargetBitmap(1200,400,96,96,PixelFormats.Pbgra32);image.Render(visual);image.Freeze();ImageFiles.Write(image,Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"multi-language-test.png"));
        OcrLanguagePacks.TestSelection="";var baseline=Await(OcrService.Read(image));OcrLanguagePacks.TestSelection="kor+jpn+rus";
        var watch=System.Diagnostics.Stopwatch.StartNew();var multi=Await(OcrService.Read(image));Console.WriteLine("MULTI OCR "+watch.ElapsedMilliseconds+"ms: "+multi.Text);
        Check(multi.Text.Contains("Save the screenshot")&&multi.Text.Contains("保存图片"),"several optional models preserve English and Chinese in the same screenshot");
        Check(multi.Text.Contains("새로운 경험")&&multi.Text.Contains("画像を保存")&&multi.Text.Contains("Сохранить"),"Korean Japanese and Russian are recognized together in one worker request");
        Check(multi.Words.All(w=>w.Box.X>=0&&w.Box.Y>=0&&w.Box.Right<=1200&&w.Box.Bottom<=400),"multi-language word boxes stay in original image coordinates");
        OcrLanguagePacks.TestSelection="rus+kor+jpn+kor";Check(Await(OcrService.Read(image)).Text==multi.Text,"reordered duplicate language selection reuses the canonical OCR result");
        OcrLanguagePacks.TestSelection="";Check(Await(OcrService.Read(image)).Text==baseline.Text,"disabling optional models cannot reuse the previous multilingual cached result");
        var latinVisual=new DrawingVisual();using(var d=latinVisual.RenderOpen()){
            d.DrawRectangle(Brushes.White,null,new Rect(0,0,1000,260));string[] lines={"Save the screenshot","Enregistrer une image. Nouvelle expérience.","Bild speichern. Neue Möglichkeiten."};
            for(int i=0;i<lines.Length;i++)d.DrawText(new FormattedText(lines[i],System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),24,Brushes.Black,1),new Point(24,24+i*70));
        }
        var latinImage=new RenderTargetBitmap(1000,260,96,96,PixelFormats.Pbgra32);latinImage.Render(latinVisual);latinImage.Freeze();OcrLanguagePacks.TestSelection="fra+deu";var latin=Await(OcrService.Read(latinImage));Console.WriteLine("LATIN MULTI: "+latin.Text);
        Check(latin.Text.Contains("Save the screenshot")&&latin.Text.Contains("expérience")&&latin.Text.Contains("Möglichkeiten"),"multiple Latin language models retain English French and German accents together");
        string folder=OcrLanguagePacks.TestFolder;try{OcrLanguagePacks.TestFolder=Path.Combine(Path.GetTempPath(),"Luma-Missing-Models-"+Guid.NewGuid().ToString("N"));OcrLanguagePacks.TestSelection="kor+jpn";Check(OcrLanguagePacks.EffectiveSelectionKey=="","missing optional files are excluded from effective OCR and cache key");Check(Await(OcrService.ReadLocal(image,"kor+jpn")).Text==baseline.Text,"missing optional files never stop base Chinese-English recognition");}finally{OcrLanguagePacks.TestFolder=folder;OcrLanguagePacks.TestSelection="";}
    }
    static void ShortcutTests(){
        var keys=new ShortcutSet();Check(keys.Error()==null&&keys.Capture.ToString()=="F1"&&keys.Pin.ToString()=="F3"&&keys.Translate.ToString()=="Ctrl+Alt+T"&&keys.TogglePins.ToString()=="Shift+F3","default shortcuts preserve screenshot and pin behaviour with separate visibility toggle");
        var restored=new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Preferences>("{\"Stroke\":3}");Check(restored.Shortcuts.Error()==null&&restored.TranslationProvider=="tencent","old settings migrate with defaults without extra downloads");
        var legacyKeys=new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<ShortcutSet>("{\"Capture\":{\"Key\":71,\"Modifiers\":3},\"Pin\":{\"Key\":72,\"Modifiers\":3},\"Translate\":{\"Key\":73,\"Modifiers\":3}}");Check(legacyKeys.Error()==null&&legacyKeys.Capture.ToString()=="Ctrl+Alt+G"&&legacyKeys.Pin.ToString()=="Ctrl+Alt+H"&&legacyKeys.Translate.ToString()=="Ctrl+Alt+I"&&legacyKeys.TogglePins.ToString()=="Shift+F3","legacy custom shortcuts retain all old choices and add default visibility toggle");
        var legacyCollision=new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<ShortcutSet>("{\"Capture\":{\"Key\":114,\"Modifiers\":4},\"Pin\":{\"Key\":114,\"Modifiers\":3},\"Translate\":{\"Key\":114,\"Modifiers\":6}}");Check(legacyCollision.Error()==null&&legacyCollision.Capture.ToString()=="Shift+F3"&&legacyCollision.Pin.ToString()=="Ctrl+Alt+F3"&&legacyCollision.Translate.ToString()=="Ctrl+Shift+F3"&&legacyCollision.TogglePins.ToString()=="Ctrl+Alt+Shift+F3","legacy default-key collision preserves existing three custom actions");
        var copied=keys.Copy();copied.TogglePins.Key=0x73;Check(keys.TogglePins.Key==0x72,"copy isolates visibility shortcut values");
        var roundTrip=new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<ShortcutSet>(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(copied));Check(roundTrip.TogglePins.Same(copied.TogglePins),"custom visibility shortcut survives settings round trip");
        Check(Shortcut.FromKey(System.Windows.Input.Key.K,System.Windows.Input.ModifierKeys.Control|System.Windows.Input.ModifierKeys.Shift).ToString()=="Ctrl+Shift+K"&&!Shortcut.FromKey(System.Windows.Input.Key.K,System.Windows.Input.ModifierKeys.None).Valid(),"hotkey capture accepts modifiers but not unmodified typing");
        var duplicate=keys.Copy();duplicate.Pin=duplicate.Capture.Copy();Check(duplicate.Error()!=null,"duplicate hotkeys are rejected");
        duplicate=keys.Copy();duplicate.TogglePins=duplicate.Pin.Copy();Check(duplicate.Error()!=null,"visibility toggle cannot collide with existing pin action");
        var held=new System.Collections.Generic.Dictionary<int,Shortcut>();Shortcut blocked=null;int registrations=0;
        using(var manager=new HotkeyManager((id,key)=>{registrations++;if(key.Same(blocked)||held.Values.Any(k=>k.Same(key)))return false;held[id]=key.Copy();return true;},id=>held.Remove(id))){
            Check(manager.Start(keys)==null&&held.Count==4,"hotkeys start without a keyboard hook");int captureId=held.First(k=>k.Value.Same(keys.Capture)).Key;int pinId=held.First(k=>k.Value.Same(keys.Pin)).Key;int toggleId=held.First(k=>k.Value.Same(keys.TogglePins)).Key;int before=registrations;
            Check(manager.ActionFor(pinId)==1&&manager.ActionFor(toggleId)==3&&manager.KeyFor(toggleId).Same(keys.TogglePins),"pin recovery and visibility have distinct dispatch and recorder values");
            var swapped=keys.Copy();swapped.Capture=keys.Pin.Copy();swapped.Pin=keys.Capture.Copy();Check(manager.Apply(swapped,()=>{})==null&&registrations==before&&manager.ActionFor(captureId)==1&&manager.ActionFor(pinId)==0,"swapping owned shortcuts reuses registration with no failure window");
            var candidate=keys.Copy();candidate.Capture=new Shortcut {Key=0x4B,Modifiers=3};candidate.Pin=new Shortcut {Key=0x4C,Modifiers=3};blocked=candidate.Pin;bool saved=false;
            Check(manager.Apply(candidate,()=>saved=true)!=null&&!saved&&held.Count==4&&held.Values.Any(k=>k.Same(keys.Capture))&&!held.Values.Any(k=>k.Same(candidate.Capture)),"conflict releases only tentative registrations and preserves old shortcuts");
            blocked=null;Check(manager.Apply(candidate,()=>{throw new IOException("test settings failure");})!=null&&held.Count==4&&manager.ActionFor(captureId)==1,"settings write failure rolls back new hotkeys without losing old ones");
            Check(manager.Apply(candidate,()=>saved=true)==null&&saved&&held.Values.Any(k=>k.Same(candidate.Capture))&&!held.ContainsKey(captureId)&&manager.ActionFor(captureId)==-1,"successful apply atomically commits settings and retires old shortcuts");
            var changedToggle=candidate.Copy();changedToggle.TogglePins=new Shortcut{Key=0x73,Modifiers=4};blocked=changedToggle.TogglePins;Check(manager.Apply(changedToggle,()=>saved=false)!=null&&held.ContainsKey(toggleId)&&manager.ActionFor(toggleId)==3,"conflicting visibility change keeps previous visibility key registered");
            blocked=null;Check(manager.Apply(changedToggle,()=>{})==null&&!held.ContainsKey(toggleId)&&manager.ActionFor(held.Single(k=>k.Value.Same(changedToggle.TogglePins)).Key)==3,"successful visibility change retires old key and preserves action index");
        }Check(held.Count==0,"exit releases only app-owned hotkeys");
    }
    static BitmapSource LegacyPrepare(BitmapSource source,double ratio){
        var bgra=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);int width=source.PixelWidth,height=source.PixelHeight;var pixels=new byte[width*height*4];bgra.CopyPixels(pixels,width*4,0);
        int dark=0,samples=0;for(int i=0;i<pixels.Length;i+=64){if(pixels[i]*.0722+pixels[i+1]*.7152+pixels[i+2]*.2126<100)dark++;samples++;}
        for(int i=0;i<pixels.Length;i+=4){int gray=(int)(pixels[i]*.0722+pixels[i+1]*.7152+pixels[i+2]*.2126);gray=(gray*pixels[i+3]+255*(255-pixels[i+3]))/255;if(dark>samples*.6)gray=255-gray;pixels[i]=pixels[i+1]=pixels[i+2]=(byte)gray;pixels[i+3]=255;}
        var normalized=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);normalized.Freeze();var scaled=new TransformedBitmap(normalized,new ScaleTransform(ratio,ratio));scaled.Freeze();return scaled;
    }
    static bool SamePixels(BitmapSource a,BitmapSource b){if(a.PixelWidth!=b.PixelWidth||a.PixelHeight!=b.PixelHeight)return false;var bytesA=new byte[a.PixelWidth*a.PixelHeight*4];var bytesB=new byte[bytesA.Length];new FormatConvertedBitmap(a,PixelFormats.Bgra32,null,0).CopyPixels(bytesA,a.PixelWidth*4,0);new FormatConvertedBitmap(b,PixelFormats.Bgra32,null,0).CopyPixels(bytesB,b.PixelWidth*4,0);return bytesA.SequenceEqual(bytesB);}
    static class IconResources {
        internal delegate bool EnumNameProc(IntPtr module,IntPtr type,IntPtr name,IntPtr parameter);
        [System.Runtime.InteropServices.DllImport("kernel32.dll",EntryPoint="LoadLibraryExW",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true)]
        internal static extern IntPtr LoadLibraryEx(string path,IntPtr file,uint flags);
        [System.Runtime.InteropServices.DllImport("kernel32.dll",EntryPoint="EnumResourceNamesW",SetLastError=true)]
        internal static extern bool EnumResourceNames(IntPtr module,IntPtr type,EnumNameProc callback,IntPtr parameter);
        [System.Runtime.InteropServices.DllImport("kernel32.dll",EntryPoint="FindResourceW",SetLastError=true)]
        static extern IntPtr FindResource(IntPtr module,IntPtr name,IntPtr type);
        [System.Runtime.InteropServices.DllImport("kernel32.dll",SetLastError=true)]
        static extern uint SizeofResource(IntPtr module,IntPtr resource);
        [System.Runtime.InteropServices.DllImport("kernel32.dll",SetLastError=true)]
        static extern IntPtr LoadResource(IntPtr module,IntPtr resource);
        [System.Runtime.InteropServices.DllImport("kernel32.dll",SetLastError=true)]
        static extern IntPtr LockResource(IntPtr resource);
        [System.Runtime.InteropServices.DllImport("kernel32.dll",SetLastError=true)]
        internal static extern bool FreeLibrary(IntPtr module);
        internal static byte[] Read(IntPtr module,IntPtr name,int type){
            IntPtr resource=FindResource(module,name,new IntPtr(type));if(resource==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(),"native executable icon resource not found");
            uint size=SizeofResource(module,resource);if(size==0)throw new InvalidDataException("native executable icon resource is empty");
            IntPtr loaded=LoadResource(module,resource);if(loaded==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(),"native executable icon resource could not load");
            IntPtr address=LockResource(loaded);if(address==IntPtr.Zero)throw new InvalidDataException("native executable icon resource could not lock");
            var bytes=new byte[checked((int)size)];System.Runtime.InteropServices.Marshal.Copy(address,bytes,0,bytes.Length);return bytes;
        }
    }
    static void IconTests(){
        var product=(System.Reflection.AssemblyProductAttribute)Attribute.GetCustomAttribute(typeof(Ui).Assembly,typeof(System.Reflection.AssemblyProductAttribute));
        bool suiteNook=product!=null&&product.Product=="Nook"&&typeof(Ui).Assembly.GetName().Name=="Mori";
        if(suiteNook){
            // The suite's native icon lives in the actual EXE. A stale standalone
            // qingjie.ico beside the test runner must not influence this proof.
            IntPtr module=IconResources.LoadLibraryEx(typeof(Ui).Assembly.Location,IntPtr.Zero,0x00000002|0x00000020);
            Check(module!=IntPtr.Zero,"Nook executable icon resources load without executing the application");
            try{
                var groups=new System.Collections.Generic.List<byte[]>();Exception readError=null;
                IconResources.EnumNameProc callback=(handle,type,name,parameter)=>{try{groups.Add(IconResources.Read(handle,name,14));return true;}catch(Exception error){readError=error;return false;}};
                bool enumerated=IconResources.EnumResourceNames(module,new IntPtr(14),callback,IntPtr.Zero);GC.KeepAlive(callback);
                Check(enumerated&&readError==null&&groups.Count==1,"Nook actual EXE has one enumerated native icon group");
                int[] expectedSizes={16,24,32,48,64,128,256};
                string[] hashes={"b24e36e249845644be3059b858cda3f60d9c2f2acd2fb587b6982f897a2bc3aa","8529e5916b0f8586d2d5641f0078585bac2b2115b2a4e3ae511f73a1adf1442c","24fda89e52f3bce009ab782b525b7db2db3dc2f335c08b6da59179136ee0c423","a5262abd311285dd4e548fcb27dceaf2a61a53f7af35554a7578a8ffe9d9c37a","3a5720df75c0429ceacf0825c82e5f0ee087f943467195b604ead76a6be95434","60dcf9e92646f48c145114b3a233915266213f06787f6161f75bf6585240df24","2350bef57e46e3ef3adeaa871949c6e56df9037f2785c6c2a87f2a0d0a940481"};
                using(var group=new MemoryStream(groups.Single()))using(var reader=new BinaryReader(group)){
                    Check(reader.ReadUInt16()==0&&reader.ReadUInt16()==1&&reader.ReadUInt16()==expectedSizes.Length&&group.Length==6+14*expectedSizes.Length,"Nook EXE native icon group contains exactly seven 16/24/32/48/64/128/256 frames");
                    var sizes=new int[expectedSizes.Length];var lengths=new uint[sizes.Length];var ids=new ushort[sizes.Length];
                    for(int i=0;i<sizes.Length;i++){int width=reader.ReadByte(),height=reader.ReadByte();sizes[i]=width==0?256:width;Check(sizes[i]==expectedSizes[i]&&sizes[i]==(height==0?256:height),"Nook native icon directory preserves the exact square size at "+expectedSizes[i]+"px");reader.ReadBytes(6);lengths[i]=reader.ReadUInt32();ids[i]=reader.ReadUInt16();}
                    Check(ids.Distinct().Count()==ids.Length,"Nook native icon group references seven distinct RT_ICON resources");
                    for(int i=0;i<sizes.Length;i++){
                        int size=sizes[i];byte[] png=IconResources.Read(module,new IntPtr(ids[i]),3);byte[] signature={137,80,78,71,13,10,26,10};
                        Check(png.Length==lengths[i]&&png.Take(signature.Length).SequenceEqual(signature),"Nook EXE RT_ICON is lossless PNG with the directory's exact length at "+size+"px");
                        string hash;using(var sha=System.Security.Cryptography.SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(png)).Replace("-","").ToLowerInvariant();
                        Check(hash==hashes[i],"Nook EXE PNG matches the independently frozen selected asset at "+size+"px");
                        BitmapSource bitmap;using(var stream=new MemoryStream(png))bitmap=BitmapFrame.Create(stream,BitmapCreateOptions.None,BitmapCacheOption.OnLoad);
                        Check(bitmap.PixelWidth==size&&bitmap.PixelHeight==size,"Nook EXE decoded PNG matches its native directory size at "+size+"px");
                        var pixels=new byte[size*size*4];new FormatConvertedBitmap(bitmap,PixelFormats.Bgra32,null,0).CopyPixels(pixels,size*4,0);
                        Func<int,int,bool> purple=(x,y)=>{int at=(y*size+x)*4;return pixels[at]==197&&pixels[at+1]==96&&pixels[at+2]==98&&pixels[at+3]==255;};
                        Func<int,int,bool> white=(x,y)=>{int at=(y*size+x)*4;return pixels[at]==255&&pixels[at+1]==255&&pixels[at+2]==255&&pixels[at+3]==255;};
                        int low=size*5/16,high=size*11/16;
                        Check(purple(size/2,size/2)&&purple(low,low)&&white(high,low)&&purple(low,high)&&purple(high,high),"Nook purple RGB 98/96/197 tile has three hollow compartments and a white solid upper-right at "+size+"px");
                        var whiteQuadrants=new bool[4];for(int y=0;y<size;y++)for(int x=0;x<size;x++)if(white(x,y))whiteQuadrants[(y<size/2?0:2)+(x<size/2?0:1)]=true;
                        Check(whiteQuadrants.All(found=>found),"Nook selected artwork retains a white compartment in every quadrant at "+size+"px");
                        // Freeze the selected 16px asset's faint corner
                        // antialiasing too; every larger frame is fully clear.
                        int topAlpha=size==16?1:0,bottomAlpha=size==16?12:0;
                        Check(pixels[3]==topAlpha&&pixels[(size-1)*4+3]==topAlpha&&pixels[((size-1)*size)*4+3]==bottomAlpha&&pixels[(size*size-1)*4+3]==bottomAlpha,"Nook rounded tile keeps the exact transparent/antialiased outer corners at "+size+"px");
                        if(size==256)Check(SamePixels(bitmap,(BitmapSource)Ui.AppIcon),"Nook actual executable native 256px icon and frozen window icon use identical artwork");
                    }
                }
            }finally{IconResources.FreeLibrary(module);}
            return;
        }
        using(var stream=File.OpenRead(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"qingjie.ico")))using(var reader=new BinaryReader(stream)){
            Check(reader.ReadUInt16()==0&&reader.ReadUInt16()==1&&reader.ReadUInt16()==6,"application ICO contains six native icon sizes");
            var sizes=new int[6];var lengths=new int[6];var offsets=new int[6];
            for(int i=0;i<6;i++){int width=reader.ReadByte(),height=reader.ReadByte();sizes[i]=width==0?256:width;Check(sizes[i]==(height==0?256:height),"ICO frame is square at "+sizes[i]+"px");reader.ReadBytes(6);lengths[i]=reader.ReadInt32();offsets[i]=reader.ReadInt32();}
            for(int i=0;i<6;i++){
                stream.Position=offsets[i];BitmapSource bitmap;
                using(var png=new MemoryStream(reader.ReadBytes(lengths[i])))bitmap=BitmapFrame.Create(png,BitmapCreateOptions.None,BitmapCacheOption.OnLoad);
                int size=sizes[i];Check(bitmap.PixelWidth==size&&bitmap.PixelHeight==size,"ICO embedded PNG matches directory size at "+size+"px");
                var pixels=new byte[size*size*4];new FormatConvertedBitmap(bitmap,PixelFormats.Bgra32,null,0).CopyPixels(pixels,size*4,0);
                int center=((size/2)*size+size/2)*4,corner=((size/4)*size+size/4)*4;
                Check(pixels[3]==0&&pixels[center]==41&&pixels[center+1]==41&&pixels[center+2]==41&&pixels[center+3]==255&&pixels[corner]>180&&pixels[corner+3]==255,"charcoal tile, white capture corners and transparent outside at "+size+"px");
                bool symmetric=true;for(int y=0;y<size;y++)for(int x=0;x<size;x++)for(int c=0;c<4;c++)symmetric&=pixels[(y*size+x)*4+c]==pixels[(y*size+size-1-x)*4+c]&&pixels[(y*size+x)*4+c]==pixels[((size-1-y)*size+x)*4+c];
                Check(symmetric,"capture icon is horizontally and vertically symmetric at "+size+"px");
                if(size==256)Check(SamePixels(bitmap,(BitmapSource)Ui.AppIcon),"window icon and executable ICO use identical selected artwork");
            }
        }
    }
    [STAThread] static int Main(string[] args){try{
        Preferences.TestFilePath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-settings","settings.json");
        AppState.Settings=new Preferences();
        CaptureTests.Run(Check);
        CaptureDrawingExperienceTests.Run(Check);CaptureRenderingPerformanceTests.Run(Check);CaptureHistoryMemoryTests.Run(Check);CaptureInputUsabilityTests.Run(Check);CaptureDirectResizeTests.Run(Check);
        ScrollCaptureStitcherTests.Run(Check);ScrollCaptureSessionTests.Run(Check);ScrollCapturePreviewTests.Run(Check);ExternalScrollCaptureTests.Run(Check);
        if(args.Length==1&&args[0]=="--snap-only"){Console.WriteLine(count+" capture selection tests passed.");return 0;}
        if(args.Length==1&&args[0]=="--snap-native"){CaptureTests.Native(Check);Console.WriteLine(count+" capture tests passed.");return 0;}
        IconTests();
        if(args.Length==1&&args[0]=="--offline-live"){OfflineTests.Live(Check);Console.WriteLine(count+" offline live tests passed.");return 0;}
        OcrLanguagePacks.TestFolder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-languages");OcrLanguagePacks.TestSelection=Environment.GetEnvironmentVariable("QINGJIE_TEST_LANGUAGE")??"";
        PerformanceTests();
        if(args.Length==2&&args[0]=="--speed-probe"){
            var source=Snapshot.Load(args[1]);for(int run=0;run<3;run++){var timer=System.Diagnostics.Stopwatch.StartNew();var speedPage=Await(OcrService.Read(source));long ocr=timer.ElapsedMilliseconds;var speedRegions=ImageTranslation.Regions(speedPage);var translated=Await(Translation.TranslateImage(speedRegions.Select(r=>r.Text).ToArray(),CancellationToken.None));long network=timer.ElapsedMilliseconds-ocr;ImageTranslation.Render(source,speedRegions,translated);Console.WriteLine("run="+run+" ocr_ms="+ocr+" translate_ms="+network+" render_ms="+(timer.ElapsedMilliseconds-ocr-network)+" total_ms="+timer.ElapsedMilliseconds+" regions="+speedRegions.Count);Console.WriteLine(string.Join(" | ",translated));}return 0;
        }
        if(args.Length==2&&args[0]=="--tess-probe"){
            var source=Snapshot.Load(args[1]);var prepared=OcrService.Prepare(source,3);byte[] png;using(var memory=new MemoryStream()){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(prepared));encoder.Save(memory);png=memory.ToArray();}
            foreach(var mode in new[]{Tesseract.PageSegMode.SparseText,Tesseract.PageSegMode.Auto})using(var engine=new Tesseract.TesseractEngine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"tessdata"),"kor",Tesseract.EngineMode.LstmOnly)){engine.SetVariable("thresholding_method",2);engine.SetVariable("thresholding_kfactor",0.04);using(var pix=Tesseract.Pix.LoadFromMemory(png))using(var result=engine.Process(pix,mode))using(var it=result.GetIterator()){it.Begin();do{Console.WriteLine(mode+" confidence="+it.GetConfidence(Tesseract.PageIteratorLevel.TextLine)+" "+it.GetText(Tesseract.PageIteratorLevel.TextLine));}while(it.Next(Tesseract.PageIteratorLevel.TextLine));}}return 0;
        }
        if(args.Length==2&&args[0]=="--ocr-image"){Console.WriteLine("OCR languages: "+OcrService.Languages());Console.WriteLine(Await(OcrService.Read(Snapshot.Load(args[1]))).Text);return 0;}
        if(args.Length==3&&args[0]=="--translate-image"){var source=Snapshot.Load(args[1]);var recognized=Await(OcrService.Read(source));Console.WriteLine(recognized.Text);var regionsToTranslate=ImageTranslation.Regions(recognized);var translated=Translation.TranslateImage(regionsToTranslate.Select(r=>r.Text).ToArray(),CancellationToken.None).GetAwaiter().GetResult();Console.WriteLine(string.Join("\n",translated));ImageFiles.Write(ImageTranslation.Render(source,regionsToTranslate,translated),args[2]);return 0;}
        if(args.Length==1&&args[0]=="--context-probe"){Console.WriteLine(Translation.Translate("[[[0000]]]\nReady. Create. Fold.\n\n[[[0001]]]\nGimbal first, then body up. Body down, then gimbal folds.\n\n[[[0002]]]\nManually adjustable mast\n\n[[[0003]]]\nSet camera height by hand, independent of wheel lifts.",CancellationToken.None).GetAwaiter().GetResult());return 0;}
        Check(Geometry.Clamp(30,1,25)==25&&Geometry.Clamp(-2,1,25)==1,"line width clamped");
        long cacheTime=0;var textCache=new TranslationCache(40,2,1,()=>cacheTime);var cacheValue=new[]{"译文"};textCache.Put("first",cacheValue);cacheValue[0]="changed";Check(textCache.Get("first")[0]=="译文","translation cache isolates caller mutations");var cacheRead=textCache.Get("first");cacheRead[0]="changed";Check(textCache.Get("first")[0]=="译文","translation cache returns a copy");textCache.Put("second",new[]{"二"});textCache.Get("first");textCache.Put("third",new[]{"三"});Check(textCache.Get("second")==null&&textCache.Count==2,"translation cache evicts least recently used entries");textCache.Put(new string('x',50),new[]{"large"});Check(textCache.CharacterCount<=40,"translation cache refuses oversized entries");cacheTime=1000;Check(textCache.Count==0&&textCache.Get("first")==null,"translation cache expires without a resident timer");
        int translating=0,maxTranslating=0;var parallelResult=Await(Translation.TranslateImageCore(new[]{"alpha","제품 사용","omega"},CancellationToken.None,async(input,token)=>{translating++;maxTranslating=Math.Max(maxTranslating,translating);try{await Task.Delay(input.Contains("alpha")?100:10,token);return "[[[0000]]]\n"+(input.Contains("alpha")?"甲":input.Contains("omega")?"丙":"乙");}finally{translating--;}}));Check(parallelResult.SequenceEqual(new[]{"甲","乙","丙"})&&maxTranslating==2,"parallel language translation preserves order and two-request limit");
        bool invalidBlock=false;try{Await(Translation.TranslateImageCore(new[]{"alpha"},CancellationToken.None,(input,token)=>Task.FromResult("missing marker")));}catch(InvalidOperationException){invalidBlock=true;}Check(invalidBlock,"parallel malformed translation never accepted or cached");
        Check(Translation.TextCacheKey("hello","en")!=Translation.TextCacheKey("hello","zh")&&Translation.ImageCacheKey(new[]{"hello"},"en")!=Translation.ImageCacheKey(new[]{"hello"},"zh"),"both translation caches isolate target languages");
        var requestJson=new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string,object>>(Translation.RequestJson(new[]{"请确认交货日期"},"auto","en"));
        Check(((System.Collections.Generic.Dictionary<string,object>)requestJson["target"])["lang"].ToString()=="en"&&((System.Collections.Generic.Dictionary<string,object>)requestJson["source"])["lang"].ToString()=="auto","Chinese input request uses selected English target and automatic source");
        var englishGlossary=Await(Translation.TranslateImageCore(new[]{"gimbal Fold"},CancellationToken.None,(input,token)=>Task.FromResult("[[[0000]]]\ngimbal Fold"),"en"));Check(englishGlossary[0]=="gimbal Fold","Chinese-only device glossary never rewrites English output");
        bool invalidTarget=false;try{Translation.TargetLanguage("xx");}catch(ArgumentException){invalidTarget=true;}Check(invalidTarget,"unsupported target rejected before network request");
        string screenshotLanguage=null;int targetChanges=0;var screenshotTarget=Ui.TranslationTarget("zh",language=>{screenshotLanguage=language;targetChanges++;});
        var chineseTarget=(System.Windows.Controls.MenuItem)screenshotTarget.ContextMenu.Items[0];var englishTarget=(System.Windows.Controls.MenuItem)screenshotTarget.ContextMenu.Items[1];
        Check(chineseTarget.IsChecked&&!englishTarget.IsChecked&&targetChanges==0,"screenshot language menu defaults to Chinese without firing a change");
        Check(screenshotTarget.Width<=20&&screenshotTarget.BorderThickness==new Thickness(0)&&screenshotTarget.Content is System.Windows.Controls.Viewbox,"language selector is a borderless chevron with no text in the toolbar");
        englishTarget.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));Check(screenshotLanguage=="en"&&targetChanges==1&&englishTarget.IsChecked&&!chineseTarget.IsChecked&&System.Windows.Automation.AutomationProperties.GetName(screenshotTarget).Contains("英文"),"language menu switches to English once and updates checks and accessible label");
        englishTarget.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));Check(targetChanges==1&&englishTarget.IsChecked,"choosing the current target does not discard the existing translation");
        chineseTarget.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));Check(screenshotLanguage=="zh"&&targetChanges==2&&chineseTarget.IsChecked&&!englishTarget.IsChecked,"language menu switches back to Chinese with exactly one selected target");
        Check(((System.Windows.Controls.MenuItem)Ui.TranslationTarget("en",language=>{}).ContextMenu.Items[1]).IsChecked,"language menu preserves English when explicitly requested");
        LineToolTests();
        WritingTests();
        ShortcutTests();
        LanguageSelectionTests();
        OfflineTests.Run(Check);
        Check(Translation.TextCacheKey("hello","en","google")!=Translation.TextCacheKey("hello","en","tencent")&&Translation.ImageCacheKey(new[]{"hello"},"en","google")!=Translation.ImageCacheKey(new[]{"hello"},"en","tencent"),"both translation caches isolate providers");
        Check(GoogleTranslation.Parse("[[[\"Hello. \",\"你好。\",null,null],[\"Thank you.\",\"谢谢。\",null,null]],null,\"zh-CN\"]")=="Hello. Thank you.","Google response segments preserve text order");
        bool googleInvalid=false;try{GoogleTranslation.Parse("<html>blocked</html>");}catch(InvalidOperationException){googleInvalid=true;}Check(googleInvalid,"Google blocked or malformed responses never render as translation");
        string chunkSource=new string('中',698)+"[[[0001]]]\n"+new string('文',699)+"\U0001F600"+new string('字',1300);var chunks=GoogleTranslation.Chunks(chunkSource);Check(string.Concat(chunks)==chunkSource&&chunks.All(c=>c.Length<=700&&!char.IsHighSurrogate(c[c.Length-1]))&&chunks.Any(c=>c.Contains("[[[0001]]]")),"Google bounded requests preserve every character, surrogate and image marker");
        var alphaPixels=new byte[33*27*4];new Random(41).NextBytes(alphaPixels);var alphaSample=BitmapSource.Create(33,27,96,96,PixelFormats.Bgra32,null,alphaPixels,33*4);alphaSample.Freeze();
        foreach(double ratio in new[]{.5,1,2.5,3})Check(SamePixels(LegacyPrepare(alphaSample,ratio),OcrService.Prepare(alphaSample,ratio)),"compact grayscale is pixel-identical for alpha/color at scale "+ratio);
        var startupFolder=Path.Combine(Path.GetTempPath(),"QingJie-Startup-Test-"+Guid.NewGuid().ToString("N"));
        var startup=new StartupRegistration(startupFolder,typeof(Program).Assembly.Location);
        Check(!startup.Enabled,"startup is off when shortcut absent");startup.SetEnabled(true);Check(startup.Enabled,"startup enable creates own shortcut");startup.SetEnabled(true);Check(startup.Enabled,"startup enable is idempotent");startup.SetEnabled(false);Check(!startup.Enabled&&!File.Exists(startup.ShortcutPath),"startup disable removes only own shortcut");
        Check(Path.GetFileName(startup.ShortcutPath)=="Luma.lnk","new installations use Luma startup name");
        startup.SetEnabled(true);var legacyStartupPath=Path.Combine(startupFolder,"轻截.lnk");File.Move(startup.ShortcutPath,legacyStartupPath);Check(startup.Enabled&&startup.ShortcutPath==legacyStartupPath,"existing startup remains enabled after Luma rename");startup.SetEnabled(true);Check(File.Exists(legacyStartupPath)&&!File.Exists(Path.Combine(startupFolder,"Luma.lnk")),"legacy startup identity preserved for Windows disabled-startup preference");startup.SetEnabled(false);Check(!File.Exists(legacyStartupPath)&&!startup.Enabled,"disable removes legacy startup after branding upgrade");
        var otherStartup=new StartupRegistration(startupFolder,Path.Combine(startupFolder,"Other.exe"));otherStartup.SetEnabled(true);bool protectedShortcut=false;try{startup.SetEnabled(false);}catch(InvalidOperationException){protectedShortcut=true;}Check(protectedShortcut&&otherStartup.Enabled,"unrelated startup shortcut is protected");otherStartup.SetEnabled(false);Directory.Delete(startupFolder);
        var reverse=Geometry.FromPoints(new Point(80,40),new Point(20,10));Check(reverse.Width==60&&reverse.Height==30,"reverse drag selection");
        var samplePixels=new byte[]{1,0,0,255,2,0,0,255,3,0,0,255,4,0,0,255,5,0,0,255,6,0,0,255};var rotationSample=BitmapSource.Create(3,2,96,96,PixelFormats.Bgra32,null,samplePixels,12);rotationSample.Freeze();
        var rotated=PinTransforms.Apply(rotationSample,1,false,false);var rotatedBytes=new byte[24];rotated.CopyPixels(rotatedBytes,8,0);Check(rotated.PixelWidth==2&&rotated.PixelHeight==3&&new[]{rotatedBytes[0],rotatedBytes[4],rotatedBytes[8],rotatedBytes[12],rotatedBytes[16],rotatedBytes[20]}.SequenceEqual(new byte[]{4,1,5,2,6,3}),"clockwise rotation is pixel exact and swaps dimensions");
        var horizontal=PinTransforms.Apply(rotationSample,0,true,false);horizontal.CopyPixels(rotatedBytes,12,0);Check(rotatedBytes[0]==3&&rotatedBytes[8]==1&&rotatedBytes[12]==6,"horizontal flip mirrors on screen axis");var vertical=PinTransforms.Apply(rotationSample,0,false,true);vertical.CopyPixels(rotatedBytes,12,0);Check(rotatedBytes[0]==4&&rotatedBytes[12]==1,"vertical flip mirrors on screen axis");
        for(int i=0;i<3;i++)rotated=PinTransforms.Apply(rotated,1,false,false);rotated.CopyPixels(rotatedBytes,12,0);Check(samplePixels.SequenceEqual(rotatedBytes),"four rotations retain all original pixels");
        bool composed=true;for(int turns=0;turns<4;turns++)foreach(bool h in new[]{false,true})foreach(bool v in new[]{false,true})foreach(int direction in new[]{-1,1}){var orientation=new PinRecord{QuarterTurns=turns,FlipHorizontal=h,FlipVertical=v};var before=PinTransforms.Apply(rotationSample,turns,h,v);var expected=PinTransforms.Apply(before,direction,false,false);PinTransforms.Rotate(orientation,direction);var actual=PinTransforms.Apply(rotationSample,orientation.QuarterTurns,orientation.FlipHorizontal,orientation.FlipVertical);var a=new byte[24];var b=new byte[24];actual.CopyPixels(a,actual.PixelWidth*4,0);expected.CopyPixels(b,expected.PixelWidth*4,0);composed&=a.SequenceEqual(b);}Check(composed,"rotate after either mirror follows visible screen direction");
        Check(((BitmapSource)Ui.AppIcon).PixelWidth==256,"new application icon embedded in executable");
        var crop=Geometry.PixelCrop(new Rect(-3,-4,25,30),1.5,1.5,100,80);Check(crop.X==0&&crop.Y==0&&crop.Width==33&&crop.Height==39,"DPI crop clamps to image");
        var words=new[]{new WordBox{Text="Hello",Line=0},new WordBox{Text="world",Line=0},new WordBox{Text="你",Line=1},new WordBox{Text="好",Line=1}};Check(Geometry.JoinWords(words)=="Hello world"+Environment.NewLine+"你好","selected words reading order");
        var visual=new DrawingVisual();using(var d=visual.RenderOpen()){d.DrawRectangle(Brushes.White,null,new Rect(0,0,600,180));d.DrawText(new FormattedText("Save the screenshot",System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Arial"),30,Brushes.Black),new Point(20,35));}
        var bitmap=new RenderTargetBitmap(600,180,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);bitmap.Freeze();
        var indexed=new OcrPage();indexed.Words.AddRange(words);OcrService.IndexText(indexed);Check(indexed.Text=="Hello world"+Environment.NewLine+"你好","OCR sidebar retains all lines");Check(indexed.Words.All(w=>indexed.Text.Substring(w.TextStart,w.Text.Length)==w.Text),"word selection offsets match complete transcript");
        var ocrCache=new OcrResultCache();ocrCache.Put("one",indexed);var ocrCopy=ocrCache.Get("one");ocrCopy.Words[0].Text="mutated";Check(ocrCache.Get("one").Words[0].Text=="Hello"&&ocrCache.Get("different")==null,"OCR transcript cache is keyed and does not share mutable word boxes");Check(OcrResultCache.Fingerprint(bitmap,CancellationToken.None)==OcrResultCache.Fingerprint(bitmap.Clone(),CancellationToken.None),"identical pixels reuse OCR across different bitmap objects");
        var koreanWords=new OcrPage();koreanWords.Words.AddRange(new[]{new WordBox{Text="새로운",Line=0},new WordBox{Text="경험",Line=0},new WordBox{Text="Camera",Line=1},new WordBox{Text="설정",Line=1}});OcrService.IndexText(koreanWords);Check(koreanWords.Text=="새로운 경험"+Environment.NewLine+"Camera 설정"&&Geometry.JoinWords(koreanWords.Words)==koreanWords.Text,"Korean word spacing preserved in transcript and selection");Check(koreanWords.Words.All(w=>koreanWords.Text.Substring(w.TextStart,w.Text.Length)==w.Text),"Korean selected-text offsets match transcript");
        var surface=new ScreenshotSurface{Image=bitmap,Selection=new Rect(0,0,300,90)};surface.Measure(new Size(300,90));surface.Arrange(new Rect(0,0,300,90));surface.Marks.Add(new Annotation{Kind="rect",Start=new Point(5,5),End=new Point(100,80),Width=3});var exported=surface.Export();Check(exported.PixelWidth==600&&exported.PixelHeight==180,"annotated export preserves physical pixels");
        byte[] pixel=new byte[4];exported.CopyPixels(new Int32Rect(100,140,1,1),pixel,4,0);Check(pixel[0]==255&&pixel[1]==255&&pixel[2]==255,"rectangle center remains unfilled");
        surface.BrushPoint=new Point(120,70);surface.BrushDiameter=21;surface.ShowBrushSize=true;var withCursor=surface.Export();withCursor.CopyPixels(new Int32Rect(240,140,1,1),pixel,4,0);Check(pixel[0]==255&&pixel[1]==255&&pixel[2]==255,"brush-size preview excluded from screenshot export");
        var translatedVisual=new DrawingVisual();using(var d=translatedVisual.RenderOpen())d.DrawRectangle(Brushes.Green,null,new Rect(0,0,200,80));var translatedSample=new RenderTargetBitmap(200,80,96,96,PixelFormats.Pbgra32);translatedSample.Render(translatedVisual);translatedSample.Freeze();
        surface.Selection=new Rect(50,30,100,40);surface.TranslationBounds=surface.Selection;surface.TranslationImage=translatedSample;surface.ShowTranslation=true;
        var previewExport=surface.Export(false);previewExport.CopyPixels(new Int32Rect(100,40,1,1),pixel,4,0);Check(previewExport.PixelWidth==200&&previewExport.PixelHeight==80&&pixel[1]==128&&pixel[2]==0,"in-place translation export respects offset selection and DPI");
        surface.Export(false,false).CopyPixels(new Int32Rect(100,40,1,1),pixel,4,0);Check(pixel[2]==255,"OCR always reads original, not previous translation");
        surface.ShowTranslation=false;surface.Export(false).CopyPixels(new Int32Rect(100,40,1,1),pixel,4,0);Check(pixel[2]==255,"translation toggle restores original export");surface.ClearTranslation();Check(surface.TranslationImage==null&&!surface.ShowTranslation,"changing selection clears translated preview");
        var folder=Path.Combine(Path.GetTempPath(),"QingJie-Test-"+Guid.NewGuid().ToString("N"));var store=new PinStore(folder);var pin=store.Add(bitmap);pin.Left=-1200;pin.Top=80;pin.Width=410;pin.Opacity=.7;store.Save();var second=new PinStore(folder);Check(second.Records.Count==1&&second.Records[0].Active&&second.Records[0].Left==-1200,"active pin survives restart with placement");second.Records[0].Active=false;second.Records[0].ClosedAt=DateTime.UtcNow.Ticks;second.Save();var third=new PinStore(folder);Check(third.LastClosed()!=null&&File.Exists(third.ImagePath(third.LastClosed())),"closed pin remains recoverable");
        var visualRecord=new PinRecord{Id=Guid.NewGuid().ToString("N"),Left=200,Top=200,Width=410};var decoratedPin=new PinWindow(store,visualRecord,bitmap);decoratedPin.UpdateRecord();Check(visualRecord.Left==200&&visualRecord.Top==200&&visualRecord.Width==410,"shadow margin does not shift persisted image position or scale");Check(decoratedPin.AllowsTransparency&&decoratedPin.Width==434&&Math.Abs(decoratedPin.Height-(410*.3+24))<.001,"pin reserves transparent shadow space without changing image aspect ratio");decoratedPin.Close();
        var picture=new System.Windows.Controls.Image{Source=bitmap,Stretch=Stretch.Fill};var pinFrame=new PinFrame(picture);
        var previewRoot=new System.Windows.Controls.Grid{Background=new SolidColorBrush(Color.FromRgb(236,239,242))};previewRoot.Children.Add(pinFrame);pinFrame.Margin=new Thickness(20);previewRoot.Measure(new Size(664,244));previewRoot.Arrange(new Rect(0,0,664,244));previewRoot.UpdateLayout();
        var idlePreview=new RenderTargetBitmap(664,244,96,96,PixelFormats.Pbgra32);idlePreview.Render(previewRoot);ImageFiles.Write(idlePreview,Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"pin-shadow-idle.png"));pinFrame.SetHovered(true);previewRoot.UpdateLayout();var hoverPreview=new RenderTargetBitmap(664,244,96,96,PixelFormats.Pbgra32);hoverPreview.Render(previewRoot);ImageFiles.Write(hoverPreview,Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"pin-shadow-hover.png"));
        Check(picture.Effect==null&&ReferenceEquals(picture.Source,bitmap),"hover glow decorates only the frame and never changes saved image pixels");
        Check(third.Records[0].Width==410&&third.Records[0].Opacity==.7,"pin zoom and opacity persist");
        third.Records[0].QuarterTurns=3;third.Records[0].FlipHorizontal=true;third.Records[0].FlipVertical=true;third.Save();var orientationStore=new PinStore(folder);Check(orientationStore.Records[0].QuarterTurns==3&&orientationStore.Records[0].FlipHorizontal&&orientationStore.Records[0].FlipVertical,"rotation and both mirror states survive restart and recovery");
        third.Records[0].IsTranslation=true;third.SaveTranslation(third.Records[0],bitmap);var translatedPin=new PinStore(folder);Check(translatedPin.Records[0].IsTranslation&&File.Exists(translatedPin.TranslationPath(translatedPin.Records[0]))&&File.Exists(translatedPin.ImagePath(translatedPin.Records[0])),"translated pin and original are saved separately");
        third.Save();File.WriteAllText(Path.Combine(folder,"pins.json"),"{interrupted");var recovered=new PinStore(folder);Check(recovered.Records.Count==1&&recovered.LastClosed()!=null,"corrupt pin index recovers from atomic backup");
        File.WriteAllText(Path.Combine(folder,"pins.json.bak"),"{also broken");var orphaned=new PinStore(folder);Check(orphaned.LastClosed()!=null,"orphaned PNG stays recoverable if both indexes fail");orphaned.Save();Check(new PinStore(folder).LastClosed()!=null,"recovered index can be saved again");
        // Only test-generated files in a fresh random temporary directory are removed.
        foreach(var file in Directory.GetFiles(folder))File.Delete(file);Directory.Delete(folder);
        Console.WriteLine("OCR languages: "+OcrService.Languages());
        var frame=new System.Windows.Threading.DispatcherFrame();OcrPage page=null;Exception error=null;
        var task=OcrService.Read(bitmap);task.ContinueWith(t=>{if(t.IsFaulted)error=t.Exception;else page=t.Result;System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(()=>{}));frame.Continue=false;});System.Windows.Threading.Dispatcher.PushFrame(frame);
        if(error!=null)throw error;Check(page!=null&&page.Text.ToLowerInvariant().Contains("screenshot"),"local Windows OCR recognizes synthetic English sample");
        var regions=ImageTranslation.Regions(page);Check(regions.Count==1,"OCR sample creates one image translation region");
        var imageTranslation=ImageTranslation.Render(bitmap,regions,new[]{"保存截图"});Check(imageTranslation.PixelWidth==600&&imageTranslation.PixelHeight==180,"image translation preserves original dimensions");imageTranslation.CopyPixels(new Int32Rect(550,140,1,1),pixel,4,0);Check(pixel[0]==255&&pixel[1]==255&&pixel[2]==255,"image translation leaves non-text background intact");
        ImageFiles.Write(imageTranslation,Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"translation-preview.png"));
        var labels=new OcrPage();labels.Words.Add(new WordBox{Text="Save",Line=0,Box=new Rect(20,20,80,20)});labels.Words.Add(new WordBox{Text="Pin",Line=1,Box=new Rect(20,100,80,20)});Check(ImageTranslation.Regions(labels).Count==2,"separate labels retain separate positions");labels.Words[1].Box=new Rect(20,47,80,20);Check(ImageTranslation.Regions(labels).Count==1,"nearby paragraph lines translate together");labels.Words[1].Line=0;labels.Words[1].Box=new Rect(300,20,80,20);Check(ImageTranslation.Regions(labels).Count==2,"distant columns do not merge");
        var parsed=Translation.ParseResponse("{\"header\":{\"ret_code\":\"succ\"},\"auto_translation\":[\"保存\",\"贴图\"]}",2);Check(parsed.SequenceEqual(new[]{"保存","贴图"}),"batched translations preserve region order");bool rejected=false;try{Translation.ParseResponse("{\"header\":{\"ret_code\":\"succ\"},\"auto_translation\":[\"保存\"]}",2);}catch(InvalidOperationException){rejected=true;}Check(rejected,"incomplete translation never painted over original");
        var darkVisual=new DrawingVisual();using(var d=darkVisual.RenderOpen()){
            d.DrawRectangle(new SolidColorBrush(Color.FromRgb(9,9,9)),null,new Rect(0,0,960,420));
            string[] labelsText={"Ready. Create. Fold.","Gimbal first, then body up. Body down, then gimbal folds.","Manually adjustable mast","Set camera height by hand, independent of wheel lifts."};
            for(int i=0;i<labelsText.Length;i++)d.DrawText(new FormattedText(labelsText[i],System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),i%2==0?18:16,Brushes.White,1),new Point(80,60+i*60));
        }
        var darkImage=new RenderTargetBitmap(960,420,96,96,PixelFormats.Pbgra32);darkImage.Render(darkVisual);darkImage.Freeze();ImageFiles.Write(darkImage,Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"english-dark-test.png"));
        var english=Await(OcrService.Read(darkImage));Check(english.Text.Contains("Ready. Create. Fold."),"small dark-background Fold recognized by local English model");Check(english.Text.Contains("Manually adjustable mast"),"small English heading no longer becomes mixed symbols");
        Check(SamePixels(LegacyPrepare(darkImage,3),OcrService.Prepare(darkImage,3)),"dark small-text preprocessing retains every grayscale pixel");
        var inProcess=Await(OcrService.ReadLocal(darkImage));Check(english.Text==inProcess.Text&&english.Words.Zip(inProcess.Words,(a,b)=>a.Text==b.Text&&a.Line==b.Line&&a.Box==b.Box).All(v=>v)&&english.Words.Count==inProcess.Words.Count,"isolated OCR preserves text, reading order and exact word positions");
        using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();bool stopped=false;try{Await(OcrService.Read(darkImage,cancelled.Token));}catch(OperationCanceledException){stopped=true;}Check(stopped,"cancelled OCR never starts work");}
        using(var cancelled=new CancellationTokenSource()){cancelled.CancelAfter(30);bool stopped=false;try{Await(OcrService.Read(new CroppedBitmap(darkImage,new Int32Rect(0,0,959,420)),cancelled.Token));}catch(OperationCanceledException){stopped=true;}Check(stopped,"active OCR cancellation terminates worker");}
        var queued=Await(Task.WhenAll(OcrService.Read(darkImage),OcrService.Read(darkImage)));Check(queued.All(p=>p.Text==english.Text),"queued OCR requests keep identical independent results after cancellation");
        bool workerLeft=false;foreach(var process in System.Diagnostics.Process.GetProcessesByName("QingJie")){using(process){try{if(string.Equals(process.MainModule.FileName,typeof(Program).Assembly.Location,StringComparison.OrdinalIgnoreCase))workerLeft=true;}catch{}}}Check(!workerLeft,"OCR leaves no worker process or retained OCR engine");
        var mixedVisual=new DrawingVisual();using(var d=mixedVisual.RenderOpen()){d.DrawRectangle(Brushes.White,null,new Rect(0,0,1000,260));d.DrawText(new FormattedText("截图翻译 测试 / Camera height 120 mm",System.Globalization.CultureInfo.GetCultureInfo("zh-CN"),FlowDirection.LeftToRight,new Typeface("Microsoft YaHei UI"),24,Brushes.Black,1),new Point(30,50));d.DrawText(new FormattedText("保存图片  水平翻转  Rotate 90",System.Globalization.CultureInfo.GetCultureInfo("zh-CN"),FlowDirection.LeftToRight,new Typeface("Microsoft YaHei UI"),24,Brushes.Black,1),new Point(30,110));}var mixed=new RenderTargetBitmap(1000,260,96,96,PixelFormats.Pbgra32);mixed.Render(mixedVisual);mixed.Freeze();ImageFiles.Write(mixed,Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"mixed-test.png"));Check(SamePixels(LegacyPrepare(mixed,2.5),OcrService.Prepare(mixed,2.5)),"mixed Chinese-English preprocessing retains pixel values");var mixedText=Await(OcrService.Read(mixed));Check(mixedText.Text.Contains("截图翻译")&&mixedText.Text.Contains("Camera")&&mixedText.Text.Contains("保存图片"),"mixed Chinese-English OCR quality retained");
        Check(english.Words.All(w=>w.Box.X>=0&&w.Box.Y>=0&&w.Box.Right<=960&&w.Box.Bottom<=420),"upscaled OCR maps boxes back to original image coordinates");
        var koreanVisual=new DrawingVisual();using(var d=koreanVisual.RenderOpen()){
            d.DrawRectangle(new SolidColorBrush(Color.FromRgb(245,245,245)),null,new Rect(0,0,1100,260));
            d.DrawText(new FormattedText("Back to Retro.",System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Arial"),32,Brushes.Navy),new Point(20,20));
            string[] koreanLines={"새로운 경험을 위해 제품을 디자인했습니다.","제품 사용 시 새로운 인상을 남길 수 있습니다."};
            for(int i=0;i<2;i++)d.DrawText(new FormattedText(koreanLines[i],System.Globalization.CultureInfo.GetCultureInfo("ko-KR"),FlowDirection.LeftToRight,new Typeface("Malgun Gothic"),14,new SolidColorBrush(Color.FromRgb(184,184,184))),new Point(20,100+i*26));
        }
        OcrLanguagePacks.TestSelection="kor";
        var koreanImage=new RenderTargetBitmap(1100,260,96,96,PixelFormats.Pbgra32);koreanImage.Render(koreanVisual);koreanImage.Freeze();ImageFiles.Write(koreanImage,Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"korean-test.png"));var koreanText=Await(OcrService.Read(koreanImage));Console.WriteLine(koreanText.Text);Check(koreanText.Text.Contains("새로운 경험")&&koreanText.Text.Contains("제품 사용"),"faint small Korean paragraphs recognized with natural word spacing");Check(koreanText.Text.Contains("Back to Retro."),"Korean pass preserves existing English heading");Check(!Await(OcrService.Read(mixed)).Text.Any(Geometry.IsHangul),"Korean pass never replaces Chinese regression sample");Check(koreanText.Words.All(w=>w.Box.X>=0&&w.Box.Y>=0&&w.Box.Right<=1100&&w.Box.Bottom<=260),"Korean word boxes retain original coordinates");
        Check(Translation.ParseImageContext("[[[0000]]]\n保存\n\n[[[0001]]]\n贴图",2).SequenceEqual(new[]{"保存","贴图"}),"context-aware translation maps back to source regions");
        Check(Translation.ParseImageContext("[0000]\n保存\n\n[0001]\n贴图",2).SequenceEqual(new[]{"保存","贴图"}),"translation accepts normalized marker brackets");
        Check(Translation.SourceLanguage("새로운 경험을 위해") == "ko"&&Translation.SourceLanguage("Save the screenshot")=="auto","Korean translation explicitly selects Korean source language");
        var bilingual=new OcrPage();bilingual.Words.Add(new WordBox{Text="새로운 경험",Line=0,Box=new Rect(20,20,150,20)});bilingual.Words.Add(new WordBox{Text="New experience",Line=1,Box=new Rect(20,47,150,20)});Check(ImageTranslation.Regions(bilingual).Count==2,"adjacent Korean and English paragraphs translate separately");
        foreach(var shade in new[]{Colors.Black,Color.FromRgb(184,184,184)}){
            var chineseVisual=new DrawingVisual();using(var d=chineseVisual.RenderOpen()){d.DrawRectangle(Brushes.White,null,new Rect(0,0,1000,230));string[] chineseLines={"中文截图翻译，保留原来的识别效果。","保存图片和屏幕贴图，可以随时恢复。","新的设计让日常生活更加方便。"};for(int i=0;i<chineseLines.Length;i++)d.DrawText(new FormattedText(chineseLines[i],System.Globalization.CultureInfo.GetCultureInfo("zh-CN"),FlowDirection.LeftToRight,new Typeface("Microsoft YaHei"),18,new SolidColorBrush(shade),1),new Point(20,20+i*50));}
            var chineseImage=new RenderTargetBitmap(1000,230,96,96,PixelFormats.Pbgra32);chineseImage.Render(chineseVisual);chineseImage.Freeze();var chineseText=Await(OcrService.Read(chineseImage));Check(chineseText.Text.Contains("保存图片")&&chineseText.Text.Contains("日常生活")&&!chineseText.Text.Any(Geometry.IsHangul),"Chinese paragraphs protected at shade "+shade);
        }
        var languageSamples=new[]{
            new[]{"jpn","画像を保存してください。新しいデザインです。","画像","Yu Gothic"},new[]{"rus","Сохранить изображение. Новый дизайн.","Сохранить","Segoe UI"},
            new[]{"fra","Enregistrer une image. Nouvelle expérience.","Enregistrer","Segoe UI"},new[]{"deu","Bild speichern. Neue Möglichkeiten.","speichern","Segoe UI"},
            new[]{"spa","Guardar imagen. Nueva experiencia.","Guardar","Segoe UI"},new[]{"por","Salvar imagem. Nova experiência.","Salvar","Segoe UI"},new[]{"ita","Salva immagine. Nuova esperienza.","Salva","Segoe UI"},
            new[]{"ara","حفظ الصورة وترجمة النص","الصورة","Segoe UI"},new[]{"tha","บันทึกภาพและแปลข้อความ","ภาพ","Leelawadee UI"},new[]{"vie","Lưu hình ảnh và dịch văn bản","hình","Segoe UI"}
        };
        foreach(var sample in languageSamples){
            var pack=OcrLanguagePacks.Find(sample[0]);Check(OcrLanguagePacks.Verify(OcrLanguagePacks.ModelPath(pack.Id),pack),pack.Name+" pinned model checksum");OcrLanguagePacks.TestSelection=pack.Id;
            var languageVisual=new DrawingVisual();using(var d=languageVisual.RenderOpen()){d.DrawRectangle(Brushes.White,null,new Rect(0,0,1000,200));d.DrawText(new FormattedText(sample[1],System.Globalization.CultureInfo.InvariantCulture,sample[0]=="ara"?FlowDirection.RightToLeft:FlowDirection.LeftToRight,new Typeface(sample[3]),24,Brushes.Black,1),new Point(sample[0]=="ara"?900:20,50));}
            var languageImage=new RenderTargetBitmap(1000,200,96,96,PixelFormats.Pbgra32);languageImage.Render(languageVisual);languageImage.Freeze();var languageText=Await(OcrService.Read(languageImage));Console.WriteLine(pack.Id+": "+languageText.Text);Check(languageText.Text.Contains(sample[2]),pack.Name+" optional model recognizes synthetic sample");
        }
        MultilingualOcrTests();
        OcrLanguagePacks.TestSelection="";Check(OcrLanguagePacks.SelectionKey==""&&OcrLanguagePacks.EffectiveSelectionKey=="","English remains default with optional models installed");
        var defaultEnglish=Await(OcrService.Read(darkImage));Check(defaultEnglish.Text==english.Text,"switching back to English preserves original OCR result");
        var packTestFolder=Path.Combine(Path.GetTempPath(),"QingJie-Languages-Test-"+Guid.NewGuid().ToString("N"));var modelSource=OcrLanguagePacks.ModelPath("fra");OcrLanguagePacks.TestFolder=packTestFolder;
        try{
            OcrLanguagePacks.Import(modelSource,"fra");Check(OcrLanguagePacks.Installed("fra")&&OcrLanguagePacks.Verify(OcrLanguagePacks.ModelPath("fra"),OcrLanguagePacks.Find("fra")),"optional model imports atomically with checksum verification");
            var badModel=Path.Combine(packTestFolder,"bad.traineddata");File.WriteAllText(badModel,"invalid model");bool badModelRejected=false;try{OcrLanguagePacks.Import(badModel,"fra");}catch(InvalidOperationException){badModelRejected=true;}Check(badModelRejected&&OcrLanguagePacks.Verify(OcrLanguagePacks.ModelPath("fra"),OcrLanguagePacks.Find("fra")),"invalid language import cannot overwrite a working model");
            using(var modelCancel=new CancellationTokenSource()){modelCancel.Cancel();bool stopped=false;try{Await(OcrLanguagePacks.Install("jpn",null,modelCancel.Token).ContinueWith(t=>{t.GetAwaiter().GetResult();return true;}));}catch(OperationCanceledException){stopped=true;}Check(stopped&&!OcrLanguagePacks.Installed("jpn"),"cancelled model download leaves no partial installed model");}
            if(Environment.GetEnvironmentVariable("QINGJIE_LIVE_TEST")=="1"){Await(OcrLanguagePacks.Install("fra",null,CancellationToken.None).ContinueWith(t=>{t.GetAwaiter().GetResult();return true;}));Check(OcrLanguagePacks.Verify(OcrLanguagePacks.ModelPath("fra"),OcrLanguagePacks.Find("fra")),"live optional model download verifies pinned content");}
        }finally{OcrLanguagePacks.TestFolder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-languages");foreach(var file in Directory.GetFiles(packTestFolder))File.Delete(file);Directory.Delete(packTestFolder);}
        Check(Translation.DeviceTerms(new[]{"Fold your poker cards."},new[]{"弃牌。"})[0]=="弃牌。","equipment glossary does not alter unrelated meanings");
        bool wrongOrder=false;try{Translation.ParseImageContext("[[[0001]]]\n错位",1);}catch(InvalidOperationException){wrongOrder=true;}Check(wrongOrder,"incorrect translation region markers leave original intact");
        if(Environment.GetEnvironmentVariable("QINGJIE_LIVE_TEST")=="1"){var input=ImageTranslation.Regions(english);var translation=Translation.TranslateImage(input.Select(r=>r.Text).ToArray(),CancellationToken.None).GetAwaiter().GetResult();Console.WriteLine(string.Join(" | ",translation));Check(translation[0].Contains("折叠"),"Fold correctly translated to Chinese with image context via live service");Check(translation.Any(t=>t.Contains("手动")),"adjustable mast heading translated without OCR corruption");ImageFiles.Write(ImageTranslation.Render(darkImage,input,translation),Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"english-live-translation.png"));var multilingual=Translation.TranslateImage(new[]{"Back to Retro.","새로운 경험을 위해 제품을 디자인했습니다.","Save the screenshot"},CancellationToken.None).GetAwaiter().GetResult();Console.WriteLine(string.Join(" | ",multilingual));Check(multilingual.Length==3&&!multilingual.Any(t=>t.Any(Geometry.IsHangul))&&multilingual[1].Contains("产品")&&multilingual[2].Contains("截图"),"Korean-English mixed image translates each language to Chinese in order");var selected=Translation.Translate("제품 사용",CancellationToken.None).GetAwaiter().GetResult();Check(!selected.Any(Geometry.IsHangul)&&selected.Contains("产品"),"Korean selected-text translation uses explicit source");}
        if(Environment.GetEnvironmentVariable("QINGJIE_LIVE_TEST")=="1"){
            string englishReply=Translation.Translate("请确认交货日期，谢谢。",CancellationToken.None,"en").GetAwaiter().GetResult();Console.WriteLine("Chinese to English: "+englishReply);Check(System.Text.RegularExpressions.Regex.IsMatch(englishReply,"[a-zA-Z]{3}")&&!System.Text.RegularExpressions.Regex.IsMatch(englishReply,"[\\u4e00-\\u9fff]"),"live typed Chinese translates into English");
            string backToChinese=Translation.Translate(englishReply,CancellationToken.None).GetAwaiter().GetResult();Check(backToChinese.Contains("日期"),"default English to Chinese remains working");
            var imageEnglish=Translation.TranslateImage(new[]{"保存图片","复制文字"},CancellationToken.None,"en").GetAwaiter().GetResult();Console.WriteLine("Chinese image: "+string.Join(" | ",imageEnglish));Check(imageEnglish.Length==2&&imageEnglish.All(t=>System.Text.RegularExpressions.Regex.IsMatch(t,"[a-zA-Z]{3}")&&!System.Text.RegularExpressions.Regex.IsMatch(t,"[\\u4e00-\\u9fff]")),"live Chinese screenshot regions translate to English in order");
            var mixedRegions=ImageTranslation.Regions(mixedText);var mixedEnglish=Translation.TranslateImage(mixedRegions.Select(r=>r.Text).ToArray(),CancellationToken.None,"en").GetAwaiter().GetResult();var englishPicture=ImageTranslation.Render(mixed,mixedRegions,mixedEnglish);ImageFiles.Write(englishPicture,Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"chinese-to-english-test.png"));
            Check(englishPicture.PixelWidth==mixed.PixelWidth&&englishPicture.PixelHeight==mixed.PixelHeight&&mixedEnglish.All(t=>!System.Text.RegularExpressions.Regex.IsMatch(t,"[\\u4e00-\\u9fff]")),"Chinese-English screenshot OCR translates and renders English at original size");
            englishPicture.CopyPixels(new Int32Rect(900,220,1,1),pixel,4,0);Check(pixel[0]==255&&pixel[1]==255&&pixel[2]==255,"Chinese-to-English rendering keeps non-text background untouched");
        }
        Console.WriteLine(count+" tests passed.");return 0;
    }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
