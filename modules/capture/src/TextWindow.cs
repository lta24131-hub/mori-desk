using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace QingJie {
    // Image-first viewer: the full OCR transcript remains visible while any range is selected.
    public sealed class TextWindow : Window {
        readonly BitmapSource image;
        BitmapSource translatedImage;
        ImageTranslationResult imageTranslation;
        readonly Image picture=new Image {Name="OcrPicture",Stretch=Stretch.Fill};
        // A provider reply is readable/copyable before the bitmap is composed.
        string[] readyTranslations;
        bool translationFailed;
        readonly OcrHighlights highlights=new OcrHighlights {Cursor=Cursors.IBeam};
        readonly TextBox original=new TextBox {Name="RecognizedText",IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,FontSize=14,Padding=new Thickness(16,18,14,12),BorderThickness=new Thickness(0),IsInactiveSelectionHighlightEnabled=true,SelectionOpacity=.65};
        readonly TextBox result=new TextBox {Name="SelectedTranslation",IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MaxHeight=150,MinHeight=40,BorderThickness=new Thickness(0),Padding=new Thickness(8),FontSize=14};
        readonly StackPanel selectionActions=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(6),Visibility=Visibility.Collapsed};
        readonly StackPanel imageTranslationActions=new StackPanel {Name="ImageTranslationActions",Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(6),Visibility=Visibility.Collapsed};
        readonly TextBlock resultHeading=new TextBlock {Text="所选译文",VerticalAlignment=VerticalAlignment.Center};
        readonly Border resultCard=new Border {Background=Ui.Brush("#ECF7F1"),BorderBrush=Ui.Brush("#D6E5DB"),BorderThickness=new Thickness(0,1,0,0),Visibility=Visibility.Collapsed};
        readonly TextBlock status=new TextBlock {Foreground=Brushes.White,TextWrapping=TextWrapping.Wrap,FontSize=12,Margin=new Thickness(8)};
        readonly Border statusCard;
        readonly ScrollViewer scroll=new ScrollViewer {HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalContentAlignment=HorizontalAlignment.Center,VerticalContentAlignment=VerticalAlignment.Center};
        readonly Viewbox preview;
        readonly ColumnDefinition sidebarColumn=new ColumnDefinition {Width=new GridLength(284)};
        readonly Border sidebar;
        readonly Button translateSelection,translatePicture,showText,copyResult,correctText;
        readonly Button captureReturn,captionClose,copyImage,saveImage,pinImage;
        internal Func<BitmapSource,CancellationToken,Task> PinOutput=(source,token)=>AppState.Pins.NewAsync(source,token);
        internal Func<IList<string>,CancellationToken,string,string,Task<string[]>> TranslateImage=Translation.TranslateImage;
        internal Func<OcrPage,IList<TranslationRegion>,CancellationToken,Task<string[]>> PrepareImage;
        internal Func<BitmapSource,IList<TranslationRegion>,IList<string>,CancellationToken,Task<ImageTranslationResult>> RenderImage=ImageTranslation.RenderAsync;
        internal Func<string,CancellationToken,string,string,Task<string>> TranslateText=Translation.TranslateScreenshotSelection;
        Action exitCapture;
        bool outputBusy;
        readonly CancellationTokenSource cancel=new CancellationTokenSource();
        readonly Func<BitmapSource,CancellationToken,Task<OcrPage>> recognize;
        readonly Func<BitmapSource,CancellationToken,Task<OcrPage>> recognizeForTranslation;
        CancellationTokenSource activeRecognition;
        bool preferTranslationOcr;
        readonly bool allowTranslation,allowPin;
        CancellationTokenSource imageRequest,selectionRequest;
        string translationTarget;
        Task<OcrPage> ocrTask;
        OcrPage page;
        bool closed,selectingImage,draggingText,showTranslated,showingImageTranscript,fitMode=true;
        int imageAnchor=-1,textAnchor;
        double zoom=1;

        public TextWindow(BitmapSource source,string targetLanguage="zh",bool allowTranslation=true,bool allowPin=true):this(source,targetLanguage,OcrService.Read,allowTranslation,allowPin,OcrWorker.ReadForTranslation){}
        // A capture's partial image result already owns OCR and complete text.
        // Reusing them must not start another recognition or translation request.
        internal TextWindow(BitmapSource source,string targetLanguage,OcrPage knownPage,ImageTranslationResult knownTranslation,string provider,bool allowTranslation=true,bool allowPin=true):this(source,targetLanguage,allowTranslation,allowPin){
            if(knownPage!=null){page=knownPage;ocrTask=Task.FromResult(knownPage);original.Text=knownPage.Text;ClearStatus();}
            if(allowTranslation&&knownTranslation!=null&&provider==Translation.Provider)ApplyImageTranslation(knownTranslation,true);
        }
        internal TextWindow(BitmapSource source,string targetLanguage,Func<BitmapSource,CancellationToken,Task<OcrPage>> recognize,bool allowTranslation=true,bool allowPin=true,Func<BitmapSource,CancellationToken,Task<OcrPage>> recognizeForTranslation=null) {
            this.recognize=recognize;this.recognizeForTranslation=recognizeForTranslation;
            PrepareImage=(p,r,t)=>OcrTranslationInput.PrepareImageAsync(source,p,r,t);
            this.allowTranslation=allowTranslation;this.allowPin=allowPin;
            translationTarget=Translation.TargetLanguage(targetLanguage);
            image=source;Title="Nook · 图片与文字";Icon=Ui.AppIcon;Width=1120;Height=620;MinWidth=740;MinHeight=360;
            WindowStartupLocation=WindowStartupLocation.CenterScreen;WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.CanResize;
            SourceInitialized+=FitStandaloneToWorkArea;
            Ui.Theme(this,Window.BackgroundProperty,"Canvas");Ui.Theme(this,Window.ForegroundProperty,"Text");FontFamily=new FontFamily("Segoe UI, Microsoft YaHei UI");UseLayoutRounding=true;
            Ui.Theme(original,Control.BackgroundProperty,"Input");Ui.Theme(original,Control.ForegroundProperty,"Text");Ui.Theme(original,TextBox.SelectionBrushProperty,"AccentSoft");
            Ui.Theme(result,Control.BackgroundProperty,"Input");Ui.Theme(result,Control.ForegroundProperty,"Text");Ui.Theme(result,TextBox.SelectionBrushProperty,"AccentSoft");Ui.Theme(resultCard,Border.BackgroundProperty,"Surface");Ui.Theme(resultCard,Border.BorderBrushProperty,"Divider");Ui.Theme(status,TextBlock.ForegroundProperty,"Text");
            WindowChrome.SetWindowChrome(this,new WindowChrome {CaptionHeight=37,ResizeBorderThickness=new Thickness(5),GlassFrameThickness=new Thickness(0),CornerRadius=new CornerRadius(0)});
            var root=new Grid();root.RowDefinitions.Add(new RowDefinition {Height=new GridLength(37)});root.RowDefinitions.Add(new RowDefinition());
            Content=Ui.Theme(new Border {Child=root,BorderThickness=new Thickness(1)},Border.BorderBrushProperty,"Border");
            var top=Ui.Theme(new DockPanel {Name="OcrToolbar"},Panel.BackgroundProperty,"Panel");root.Children.Add(top);
            var caption=new StackPanel {Orientation=Orientation.Horizontal};DockPanel.SetDock(caption,Dock.Right);top.Children.Add(caption);WindowChrome.SetIsHitTestVisibleInChrome(caption,true);
            caption.Children.Add(Symbol("\uE921","最小化",()=>WindowState=WindowState.Minimized,44));
            caption.Children.Add(Symbol("\uE922","最大化 / 还原",()=>WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized,44));
            captionClose=Symbol("\uE8BB","关闭",Close,44);caption.Children.Add(captionClose);
            var bar=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(3,0,0,0)};top.Children.Add(bar);WindowChrome.SetIsHitTestVisibleInChrome(bar,true);
            captureReturn=ActionButton("返回截图",Close);captureReturn.Name="ReturnToCapture";captureReturn.ToolTip="返回原截图继续标注；Esc 退出整个截图";captureReturn.Visibility=Visibility.Collapsed;bar.Children.Add(captureReturn);
            // Standalone image viewers do not own the shared pin store. Only the
            // main suite process writes pin history, avoiding competing writers.
            if(allowPin&&AppState.Pins!=null){pinImage=Symbol("\uE718","贴到屏幕",PinImage);bar.Children.Add(pinImage);Separator(bar);}
            bar.Children.Add(Symbol("\uE8A3","放大",()=>ChangeZoom(zoom*1.2)));bar.Children.Add(Symbol("\uE71F","缩小",()=>ChangeZoom(zoom/1.2)));
            bar.Children.Add(Symbol("\uE9A6","适应窗口",()=>{fitMode=true;Fit();}));bar.Children.Add(Symbol("\uE740","实际大小 1:1",()=>ChangeZoom(1)));Separator(bar);
            copyImage=Symbol("\uE8C8","复制图片",async()=>await CopyImageAsync());bar.Children.Add(copyImage);
            translatePicture=ActionButton("翻译整图",ToggleImageTranslation);translatePicture.Name="ImageTranslationAction";translatePicture.Width=88;translatePicture.Padding=new Thickness(4,0,4,0);RefreshTranslationAction();if(allowTranslation)bar.Children.Add(translatePicture);
            if(allowTranslation){var targetMenu=Ui.TranslationTarget(translationTarget,language=>{bool wasTranslating=imageRequest!=null||readyTranslations!=null||imageTranslation!=null;ChangeTarget(language);if(wasTranslating)ToggleImageTranslation();});bar.Children.Add(targetMenu);
                targetMenu.ContextMenu.PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape){e.Handled=true;targetMenu.ContextMenu.IsOpen=false;ExitTextView();}};
            }
            showText=Symbol("\uE8A5","显示 / 隐藏全部识别文字",ToggleSidebar);bar.Children.Add(showText);Mark(showText,true);
            saveImage=Symbol("\uE74E","保存图片",async()=>await SaveImageAsync());bar.Children.Add(saveImage);
            var columns=new Grid();columns.ColumnDefinitions.Add(new ColumnDefinition());columns.ColumnDefinitions.Add(sidebarColumn);Grid.SetRow(columns,1);root.Children.Add(columns);
            var imageArea=Ui.Theme(new Grid {Name="OcrImageArea"},Panel.BackgroundProperty,"Canvas");columns.Children.Add(imageArea);imageArea.Children.Add(scroll);
            var drawing=new Grid {Width=image.PixelWidth,Height=image.PixelHeight};picture.Source=image;drawing.Children.Add(picture);drawing.Children.Add(highlights);
            preview=new Viewbox {Child=drawing,Stretch=Stretch.Fill};scroll.Content=preview;scroll.SizeChanged+=(s,e)=>{if(fitMode)Fit();};
            scroll.PreviewMouseWheel+=(s,e)=>{if((Keyboard.Modifiers&ModifierKeys.Control)!=0){ChangeZoom(zoom*(e.Delta>0?1.12:1/1.12));e.Handled=true;}};
            statusCard=new Border {Child=status,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(4),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(12),MaxWidth=480,Visibility=Visibility.Collapsed};Ui.Theme(statusCard,Border.BackgroundProperty,"Surface");Ui.Theme(statusCard,Border.BorderBrushProperty,"Border");imageArea.Children.Add(statusCard);
            var right=new Grid();right.RowDefinitions.Add(new RowDefinition());right.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});right.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});right.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            sidebar=new Border {Child=right,BorderThickness=new Thickness(1,0,0,0)};Ui.Theme(sidebar,Border.BackgroundProperty,"Surface");Ui.Theme(sidebar,Border.BorderBrushProperty,"Divider");Grid.SetColumn(sidebar,1);columns.Children.Add(sidebar);
            right.Children.Add(original);Grid.SetRow(imageTranslationActions,1);right.Children.Add(imageTranslationActions);
            var allText=ActionButton("完整译文",ShowImageTranscript);allText.Name="ShowImageTranslationTranscript";imageTranslationActions.Children.Add(allText);
            var copyAll=ActionButton("复制全部译文",()=>CopyText(AvailableTranscript));copyAll.Name="CopyImageTranslationTranscript";imageTranslationActions.Children.Add(copyAll);
            Grid.SetRow(selectionActions,2);right.Children.Add(selectionActions);
            correctText=ActionButton("校正",EditRecognizedRegion);correctText.Name="CorrectRecognizedText";correctText.ToolTip=allowTranslation?"校正选中文字所在的段落，再重新翻译；原图保留":"校正选中文字所在的段落；原图保留";selectionActions.Children.Add(correctText);
            selectionActions.Children.Add(ActionButton("复制",()=>CopyText(original.SelectedText)));
            translateSelection=ActionButton("翻译所选",TranslateSelected,true);translateSelection.ToolTip="翻译所选文字 · Ctrl+Enter";if(allowTranslation)selectionActions.Children.Add(translateSelection);
            var resultPanel=new StackPanel();var resultHeader=new DockPanel {Margin=new Thickness(8,4,4,0)};
            var closeResult=Symbol("\uE8BB","收起译文",()=>resultCard.Visibility=Visibility.Collapsed,28);DockPanel.SetDock(closeResult,Dock.Right);resultHeader.Children.Add(closeResult);
            copyResult=ActionButton("复制译文",()=>CopyText(result.Text));copyResult.IsEnabled=false;DockPanel.SetDock(copyResult,Dock.Right);resultHeader.Children.Add(copyResult);resultHeader.Children.Add(Ui.Theme(resultHeading,TextBlock.ForegroundProperty,"Text"));
            resultPanel.Children.Add(resultHeader);resultPanel.Children.Add(result);resultCard.Child=resultPanel;Grid.SetRow(resultCard,3);right.Children.Add(resultCard);
            ConfigureTextSelection();ConfigureImageSelection();
            original.SelectionChanged+=(s,e)=>{ResetSelectedTranslation();selectionActions.Visibility=original.SelectionLength>0?Visibility.Visible:Visibility.Collapsed;correctText.IsEnabled=OcrCorrection.FindRegion(page,original.SelectionStart,original.SelectionLength)>=0;HighlightSelected();};
            Loaded+=async(s,e)=>{Fit();try{await EnsureOcr();}catch(Exception ex){if(!closed)Status(ex.Message);}};
            if(allowTranslation)AppState.TranslationProviderChanged+=RefreshProvider;
            Closed+=(s,e)=>{closed=true;exitCapture=null;AppState.TranslationProviderChanged-=RefreshProvider;cancel.Cancel();picture.Source=null;translatedImage=null;imageTranslation=null;readyTranslations=null;highlights.SetSelection(null,0,0);};
            PreviewKeyDown+=async(s,e)=>{if(e.Key==Key.Escape){ExitTextView();e.Handled=true;}else if(e.Key==Key.Enter&&(Keyboard.Modifiers&ModifierKeys.Control)!=0){if(!e.IsRepeat)TranslateSelected();e.Handled=true;}else if(e.Key==Key.S&&(Keyboard.Modifiers&ModifierKeys.Control)!=0){e.Handled=true;await SaveImageAsync();}};
        }
        internal void AttachCaptureReturn(Action exit){
            exitCapture=exit;captureReturn.Visibility=Visibility.Visible;
            captionClose.ToolTip="返回截图继续标注";System.Windows.Automation.AutomationProperties.SetName(captionClose,"返回截图继续标注");
        }
        internal void DetachCaptureReturn(){
            // A new screenshot must not discard the previous recognition or
            // translation, nor let its old Return/Escape close the new session.
            exitCapture=null;captureReturn.Visibility=Visibility.Collapsed;
            captionClose.ToolTip="关闭";System.Windows.Automation.AutomationProperties.SetName(captionClose,"关闭");
        }
        internal void ExitTextView(){var exit=exitCapture;exitCapture=null;if(exit!=null)exit();if(!closed)Close();}
        async void PinImage(){await PinImageAsync();}
        internal Task PinImageAsync(){
            if(closed||!allowPin||AppState.Pins==null)return Task.FromResult(false);
            // Use the same cancellable, atomic path as screenshot/library pins.
            // A long image must not encode PNG on the viewer's dispatcher.
            return RunImageOutput(async()=>{await PinOutput(Displayed,cancel.Token);return true;},"已贴到屏幕。","正在贴到屏幕…");
        }
        async Task CopyImageAsync(){await RunImageOutput(async()=>{await ImageFiles.CopyAsync(Displayed,cancel.Token);return true;},"图片已复制。");}
        async Task SaveImageAsync(){await RunImageOutput(()=>ImageFiles.SaveAsync(Displayed,this,cancel.Token),"图片已保存。");}
        async Task RunImageOutput(Func<Task<bool>> action,string success,string pending=null){
            if(closed||outputBusy)return;outputBusy=true;copyImage.IsEnabled=false;saveImage.IsEnabled=false;if(pinImage!=null)pinImage.IsEnabled=false;
            if(!string.IsNullOrEmpty(pending))Status(pending);
            try{bool done=await action();if(!closed)Status(done?success:"保存已取消，图片已保留。");}
            catch(OperationCanceledException){if(!closed)Status("操作已取消，图片已保留。");}
            catch(Exception error){if(!closed)Status("未完成，图片已保留："+error.Message);}
            finally{outputBusy=false;if(!closed){copyImage.IsEnabled=true;saveImage.IsEnabled=true;if(pinImage!=null)pinImage.IsEnabled=true;}}
        }
        BitmapSource Displayed {get{return showTranslated&&translatedImage!=null?translatedImage:image;}}
        // Segoe MDL2 Assets is Windows' installed UI icon library; no image assets or web runtime.
        static Button Symbol(string glyph,string title,Action action,double width=38){
            var b=new Button {Width=width,Height=35,Background=Brushes.Transparent,BorderThickness=new Thickness(0),Focusable=false,ToolTip=title,Cursor=Cursors.Hand,Content=new TextBlock {Text=glyph,FontFamily=new FontFamily("Segoe MDL2 Assets"),FontSize=15,Foreground=Ui.Brush("#5C5C61"),VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center}};
            Ui.Theme((TextBlock)b.Content,TextBlock.ForegroundProperty,"Text");
            var border=new FrameworkElementFactory(typeof(Border));border.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));var content=new FrameworkElementFactory(typeof(ContentPresenter));content.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);content.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);border.AppendChild(content);b.Template=new ControlTemplate(typeof(Button)){VisualTree=border};
            b.MouseEnter+=(s,e)=>{if(b.Tag==null)Ui.Theme(b,Control.BackgroundProperty,"Hover");};b.MouseLeave+=(s,e)=>{if(b.Tag==null)b.Background=Brushes.Transparent;};b.Click+=(s,e)=>action();System.Windows.Automation.AutomationProperties.SetName(b,title);return b;
        }
        static Button ActionButton(string name,Action action,bool primary=false){var b=Ui.TextButton(name,action,primary);b.Focusable=false;return b;}
        static void Separator(Panel p){p.Children.Add(Ui.Theme(new Border {Width=1,Height=20,Margin=new Thickness(6,0,6,0),VerticalAlignment=VerticalAlignment.Center},Border.BackgroundProperty,"Divider"));}
        static void Mark(Button b,bool active){Ui.SetActive(b,active);}
        void Status(string message){status.Text=OcrService.WithNotice(message,page==null?null:page.Notice);statusCard.Visibility=string.IsNullOrEmpty(status.Text)?Visibility.Collapsed:Visibility.Visible;}
        void ClearStatus(){Status(null);}
        void Try(Action action){try{action();}catch(Exception ex){Status(ex.Message);}}
        void CopyText(string text){if(!string.IsNullOrEmpty(text))Try(()=>Clipboard.SetText(text));}
        void Fit(){Fit(VisualTreeHelper.GetDpi(this));}
        void Fit(DpiScale dpi){zoom=TextPreviewScale.Fit(new Size(image.PixelWidth,image.PixelHeight),new Size(Math.Max(1,scroll.ActualWidth-4),Math.Max(1,scroll.ActualHeight-4)),dpi);ApplyZoom(dpi);}
        void ChangeZoom(double amount){fitMode=false;zoom=Geometry.Clamp(amount,.02,8);ApplyZoom();}
        void ApplyZoom(){ApplyZoom(VisualTreeHelper.GetDpi(this));}
        void ApplyZoom(DpiScale dpi){var size=TextPreviewScale.LogicalSize(new Size(image.PixelWidth,image.PixelHeight),zoom,dpi);preview.Width=size.Width;preview.Height=size.Height;}
        protected override void OnDpiChanged(DpiScale oldDpi,DpiScale newDpi){
            base.OnDpiChanged(oldDpi,newDpi);
            if(preview==null||closed)return;
            // Keep the chosen physical magnification while moving between
            // monitors. A fit view instead refits to the new viewport scale.
            if(fitMode)Fit(newDpi);else ApplyZoom(newDpi);
        }
        void ToggleSidebar(){bool show=sidebar.Visibility!=Visibility.Visible;sidebar.Visibility=show?Visibility.Visible:Visibility.Collapsed;sidebarColumn.Width=new GridLength(show?284:0);Mark(showText,show);}
        string AvailableTranscript {get{return imageTranslation!=null?imageTranslation.Transcript:readyTranslations==null?null:string.Join("\n\n",readyTranslations);}}
        void RefreshTranslationAction(){
            string label=imageRequest!=null?"取消翻译":translatedImage!=null?(showTranslated?"查看原图":"查看译图"):translationFailed?(readyTranslations!=null?"重试排版":"重试翻译"):"翻译整图";
            translatePicture.Content=label;translatePicture.ToolTip=imageRequest!=null?"正在处理 · 点击取消，原图保留":label;
            System.Windows.Automation.AutomationProperties.SetName(translatePicture,label);Mark(translatePicture,imageRequest!=null||showTranslated);
        }
        void ShowImageTranscript(){
            string transcript=AvailableTranscript;if(string.IsNullOrWhiteSpace(transcript))return;
            ResetSelectedTranslation();
            showingImageTranscript=true;resultHeading.Text="整图译文";result.Text=transcript;copyResult.IsEnabled=true;resultCard.Visibility=Visibility.Visible;
            if(sidebar.Visibility!=Visibility.Visible)ToggleSidebar();
        }
        void ImageTranslationStatus(){
            if(imageTranslation!=null&&imageTranslation.UnplacedCount>0)Status("有 "+imageTranslation.UnplacedCount+" 处保留原文，可查看或复制完整译文。");
            else ClearStatus();
        }
        void ApplyImageTranslation(ImageTranslationResult translated,bool revealTranscript){
            imageTranslation=translated;translatedImage=translated.Image;readyTranslations=null;showTranslated=true;picture.Source=Displayed;RefreshTranslationAction();
            imageTranslationActions.Visibility=string.IsNullOrWhiteSpace(translated.Transcript)?Visibility.Collapsed:Visibility.Visible;
            if(revealTranscript||(translated.UnplacedCount>0&&CanSuggestTranscript))ShowImageTranscript();
            HighlightSelected();ImageTranslationStatus();
        }
        internal Task<OcrPage> EnsureOcr(){return EnsureReaderOcr(false,CancellationToken.None);}
        internal static bool SingleReaderLine(BitmapSource source,CancellationToken interest){
            try{
                Int32Rect bounds;var line=VerifiedLineOcr.ShortSelection(source,interest,out bounds);
                return line!=null&&DesktopLineEvidence.Analyze(line)!=null;
            }catch(InvalidOperationException){return false;}
            catch(NotSupportedException){return false;}
        }
        async Task<OcrPage> EnsureReaderOcr(bool translating,CancellationToken interest){
            if(page!=null)return page;
            if(translating&&recognizeForTranslation!=null&&!preferTranslationOcr&&VerifiedLineOcr.MayContainShortLine(image)){
                if(ocrTask==null)preferTranslationOcr=true;
                else if(!ocrTask.IsCompleted&&image.IsFrozen){
                    // Keep the full worker on dense text and photographs. Only a
                    // plausible single line warrants interrupting a read in flight.
                    bool single=await Task.Run(()=>SingleReaderLine(image,interest),interest);
                    interest.ThrowIfCancellationRequested();
                    if(single&&!preferTranslationOcr&&(ocrTask==null||!ocrTask.IsCompleted)){
                        preferTranslationOcr=true;
                        if(activeRecognition!=null)activeRecognition.Cancel();
                    }
                }
            }
            if(ocrTask==null){if(imageRequest==null)Status("正在本机识别文字…");ocrTask=ReadForCurrentAction();}
            var request=ocrTask;OcrPage read;
            try{read=await request;}catch{if(ocrTask==request){ocrTask=null;preferTranslationOcr=false;}throw;}if(closed)return read;
            if(page==null){page=read;original.Text=page.Text;ClearStatus();if(page.Words.Count==0)Status("未识别到文字，原图保留。");}return page;
        }
        async Task<OcrPage> ReadForCurrentAction(){
            using(var first=CancellationTokenSource.CreateLinkedTokenSource(cancel.Token)){
                activeRecognition=first;bool fast=preferTranslationOcr;
                try{return await (fast?recognizeForTranslation:recognize)(image,first.Token);}
                catch(OperationCanceledException){
                    if(fast||!preferTranslationOcr||!first.IsCancellationRequested||cancel.IsCancellationRequested)throw;
                }finally{activeRecognition=null;}
            }
            // The full reader has acknowledged cancellation. Both its waiting
            // sidebar and the explicit translation now share one recognition.
            cancel.Token.ThrowIfCancellationRequested();
            return await recognizeForTranslation(image,cancel.Token);
        }
        async void ToggleImageTranslation(){
            if(closed||!allowTranslation)return;
            if(imageRequest!=null){ResetImageTranslation(false);return;}if(translatedImage!=null){showTranslated=!showTranslated;picture.Source=Displayed;RefreshTranslationAction();HighlightSelected();ImageTranslationStatus();return;}
            var request=CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);imageRequest=request;string target=translationTarget,provider=Translation.Provider;translationFailed=false;RefreshTranslationAction();
            try{
                status.Text="";statusCard.Visibility=Visibility.Collapsed;
                var text=await EnsureReaderOcr(true,request.Token);request.Token.ThrowIfCancellationRequested();if(closed||text.Words.Count==0)return;
                statusCard.Visibility=Visibility.Collapsed;var regions=ImageTranslation.Regions(text);
                if(readyTranslations==null){
                    var translated=ImageTranslation.TranslateWholeLabel(text,regions,target,provider);
                    if(translated==null){
                        var input=await PrepareImage(text,regions,request.Token);request.Token.ThrowIfCancellationRequested();
                        translated=await TranslateImage(input,request.Token,target,provider);
                    }
                    if(closed||imageRequest!=request||request.IsCancellationRequested||translationTarget!=target||Translation.Provider!=provider)return;
                    readyTranslations=translated;imageTranslationActions.Visibility=Visibility.Visible;
                    // Do not steal a text selection or replace its own translation.
                    if(CanSuggestTranscript)ShowImageTranscript();
                }
                var rendered=await RenderImage(image,regions,readyTranslations,request.Token);
                if(!closed&&imageRequest==request&&!request.IsCancellationRequested&&translationTarget==target&&Translation.Provider==provider)ApplyImageTranslation(rendered,false);
            }
            catch(OperationCanceledException){if(!closed&&imageRequest==request&&!request.IsCancellationRequested){translationFailed=true;Status("翻译超时，原图保留。");}}catch(Exception ex){if(!closed&&imageRequest==request){translationFailed=true;if(readyTranslations!=null&&CanSuggestTranscript)ShowImageTranscript();Status(readyTranslations!=null?"译文已保留，可查看完整译文或重试图片排版。":ex.Message);}}
            finally{if(imageRequest==request){imageRequest=null;if(!closed)RefreshTranslationAction();}request.Dispose();}
        }
        bool CanSuggestTranscript {get{return sidebar.Visibility==Visibility.Visible&&selectionRequest==null&&original.SelectionLength==0;}}
        void FitStandaloneToWorkArea(object sender,EventArgs args){
            SourceInitialized-=FitStandaloneToWorkArea;
            // Capture output supplies a monitor-specific native placement.
            // Standalone OCR (from a file/preview) needs the same small-screen
            // sizing without overriding a caller's explicit placement.
            if(WindowStartupLocation!=WindowStartupLocation.CenterScreen||WindowState!=WindowState.Normal)return;
            var handle=new WindowInteropHelper(this).Handle;var source=HwndSource.FromHwnd(handle);
            var transform=source!=null&&source.CompositionTarget!=null?source.CompositionTarget.TransformToDevice:Matrix.Identity;
            double scaleX=transform.M11>0?transform.M11:1,scaleY=transform.M22>0?transform.M22:1;
            var bounds=CaptureWindow.TextViewerBounds(System.Windows.Forms.Screen.FromHandle(handle).WorkingArea,scaleX,scaleY);
            WindowStartupLocation=WindowStartupLocation.Manual;
            MinWidth=Math.Min(MinWidth,bounds.Width/scaleX);MinHeight=Math.Min(MinHeight,bounds.Height/scaleY);
            Width=bounds.Width/scaleX;Height=bounds.Height/scaleY;
            Native.SetWindowPos(handle,IntPtr.Zero,bounds.X,bounds.Y,bounds.Width,bounds.Height,0x0014);
        }
        void RefreshSelectionAction(bool failed=false){
            string label=selectionRequest!=null?"取消翻译":failed?"重试翻译":"翻译所选";
            if(Equals(translateSelection.Content,label))return;
            translateSelection.Content=label;translateSelection.ToolTip=label+" · Ctrl+Enter";
            System.Windows.Automation.AutomationProperties.SetName(translateSelection,label);
        }
        void ResetSelectedTranslation(){
            var previous=selectionRequest;selectionRequest=null;if(previous!=null)previous.Cancel();
            RefreshSelectionAction();
            if(showingImageTranscript&&(imageTranslation!=null||readyTranslations!=null))return;
            result.Clear();copyResult.IsEnabled=false;resultCard.Visibility=Visibility.Collapsed;
        }
        void ResetImageTranslation(bool invalidateSelection){
            // The image and selected text have independent requests but share a
            // transcript card. Cancelling the image must not clear the selection's
            // answer or cancel it in flight. Changing language/provider affects both.
            bool clearCard=invalidateSelection||showingImageTranscript;
            var previous=imageRequest;imageRequest=null;if(previous!=null)previous.Cancel();
            readyTranslations=null;translationFailed=false;imageTranslation=null;showingImageTranscript=false;imageTranslationActions.Visibility=Visibility.Collapsed;
            if(clearCard)ResetSelectedTranslation();
            translatedImage=null;showTranslated=false;picture.Source=image;RefreshTranslationAction();ClearStatus();HighlightSelected();
        }
        void ChangeTarget(string language){translationTarget=Translation.TargetLanguage(language);ResetImageTranslation(true);}
        void RefreshProvider(){ChangeTarget(translationTarget);}
        internal void CorrectRegion(int index,string text){
            var corrected=OcrCorrection.Apply(page,index,text);
            ChangeTarget(translationTarget);page=corrected;ocrTask=Task.FromResult(corrected);original.Text=corrected.Text;
            Status("识别文字已校正，点击整图翻译即可更新译文。");
        }
        void EditRecognizedRegion(){
            int index=OcrCorrection.FindRegion(page,original.SelectionStart,original.SelectionLength);if(index<0)return;
            var region=ImageTranslation.Regions(page)[index];
            var editor=new TextBox{Text=region.Text,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(10),FontSize=15,MinHeight=90,MaxLength=16000};
            Ui.Theme(editor,Control.BackgroundProperty,"Input");Ui.Theme(editor,Control.ForegroundProperty,"Text");
            var dialog=new Window{Title="校正识别文字",Owner=this,Width=480,Height=260,MinWidth=340,MinHeight=220,WindowStartupLocation=WindowStartupLocation.CenterOwner,ShowInTaskbar=false};
            Ui.Theme(dialog,Window.BackgroundProperty,"Canvas");Ui.Theme(dialog,Window.ForegroundProperty,"Text");
            var content=new DockPanel{Margin=new Thickness(16)};dialog.Content=content;
            var hint=new TextBlock{Text="修改这一段的识别结果，原图保持不变。",Margin=new Thickness(0,0,0,12),TextWrapping=TextWrapping.Wrap};DockPanel.SetDock(hint,Dock.Top);content.Children.Add(hint);
            var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};DockPanel.SetDock(actions,Dock.Bottom);content.Children.Add(actions);
            var cancelEdit=ActionButton("取消",()=>dialog.Close());cancelEdit.IsCancel=true;actions.Children.Add(cancelEdit);
            var save=ActionButton(allowTranslation?"保存并翻译":"保存",()=>{try{CorrectRegion(index,editor.Text);dialog.Close();if(allowTranslation)ToggleImageTranslation();}catch(Exception error){hint.Text=error.Message;}},true);actions.Children.Add(save);
            editor.TextChanged+=(s,e)=>save.IsEnabled=!string.IsNullOrWhiteSpace(editor.Text);content.Children.Add(editor);
            dialog.Loaded+=(s,e)=>{editor.Focus();editor.SelectAll();};dialog.ShowDialog();
        }
        async void TranslateSelected(){
            if(closed||!allowTranslation)return;
            if(selectionRequest!=null){ResetSelectedTranslation();return;}
            string selected=original.SelectedText;if(string.IsNullOrWhiteSpace(selected))return;
            showingImageTranscript=false;ResetSelectedTranslation();
            var request=CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);selectionRequest=request;RefreshSelectionAction();
            string target=translationTarget,provider=Translation.Provider,error=null;
            try{
                string translated=await TranslateText(Translation.ImageTranslationInput(selected),request.Token,target,provider);
                if(closed||selectionRequest!=request||request.IsCancellationRequested||translationTarget!=target||Translation.Provider!=provider)return;
                resultHeading.Text="所选译文";result.Text=translated;copyResult.IsEnabled=true;resultCard.Visibility=Visibility.Visible;
            }catch(OperationCanceledException){if(!request.IsCancellationRequested)error="请求超时，请重试。";}
            catch(Exception ex){error=ex.Message;}
            finally{
                if(selectionRequest==request){
                    selectionRequest=null;
                    if(!closed){
                        if(error!=null){resultHeading.Text="所选翻译失败";result.Text=error;resultCard.Visibility=Visibility.Visible;}
                        RefreshSelectionAction(error!=null);
                    }
                }
                request.Dispose();
            }
        }
        static bool IsScrollbar(DependencyObject source){while(source!=null){if(source is System.Windows.Controls.Primitives.ScrollBar)return true;if(source is Visual)source=VisualTreeHelper.GetParent(source);else break;}return false;}
        int CaretAt(Point point){int at=original.GetCharacterIndexFromPoint(point,true);if(at<0)return at;var leading=original.GetRectFromCharacterIndex(at,false);var trailing=original.GetRectFromCharacterIndex(at,true);if(!leading.IsEmpty&&!trailing.IsEmpty&&Math.Abs(leading.Y-trailing.Y)<1&&trailing.X>leading.X&&point.X>(leading.X+trailing.X)/2&&at<original.Text.Length)at++;return at;}
        void SelectTextTo(Point point){int at=CaretAt(point);if(at>=0)original.Select(Math.Min(textAnchor,at),Math.Abs(at-textAnchor));}
        void ConfigureTextSelection(){
            original.PreviewMouseLeftButtonDown+=(s,e)=>{int at=CaretAt(e.GetPosition(original));if(at<0||e.ClickCount!=1||IsScrollbar(e.OriginalSource as DependencyObject))return;original.Focus();textAnchor=(Keyboard.Modifiers&ModifierKeys.Shift)!=0?original.SelectionStart:at;draggingText=true;SelectTextTo(e.GetPosition(original));original.CaptureMouse();e.Handled=true;};
            original.PreviewMouseMove+=(s,e)=>{if(!draggingText||e.LeftButton!=MouseButtonState.Pressed)return;var p=e.GetPosition(original);if(p.Y<8)original.LineUp();else if(p.Y>original.ActualHeight-8)original.LineDown();SelectTextTo(p);e.Handled=true;};
            original.PreviewMouseLeftButtonUp+=(s,e)=>{if(!draggingText)return;SelectTextTo(e.GetPosition(original));draggingText=false;original.ReleaseMouseCapture();e.Handled=true;};
            original.LostMouseCapture+=(s,e)=>draggingText=false;
        }
        int WordAt(Point point,bool nearest){
            if(page==null)return -1;int best=-1;double distance=double.MaxValue;
            for(int i=0;i<page.Words.Count;i++){var box=page.Words[i].Box;if(box.Contains(point))return i;if(!nearest)continue;double dx=Math.Max(Math.Max(box.Left-point.X,0),point.X-box.Right),dy=Math.Max(Math.Max(box.Top-point.Y,0),point.Y-box.Bottom),d=dx*dx+dy*dy*4;if(d<distance){distance=d;best=i;}}return best;
        }
        void SelectImageTo(Point point){int end=WordAt(point,true);if(end<0||imageAnchor<0)return;var first=page.Words[Math.Min(imageAnchor,end)];var last=page.Words[Math.Max(imageAnchor,end)];original.Select(first.TextStart,last.TextStart+last.Text.Length-first.TextStart);original.ScrollToLine(original.GetLineIndexFromCharacterIndex(first.TextStart));}
        void ConfigureImageSelection(){
            highlights.MouseLeftButtonDown+=(s,e)=>{imageAnchor=WordAt(e.GetPosition(highlights),false);if(imageAnchor<0)return;selectingImage=true;highlights.CaptureMouse();SelectImageTo(e.GetPosition(highlights));e.Handled=true;};
            highlights.MouseMove+=(s,e)=>{if(selectingImage){SelectImageTo(e.GetPosition(highlights));e.Handled=true;}};
            highlights.MouseLeftButtonUp+=(s,e)=>{if(!selectingImage)return;SelectImageTo(e.GetPosition(highlights));selectingImage=false;highlights.ReleaseMouseCapture();if(sidebar.Visibility!=Visibility.Visible)ToggleSidebar();e.Handled=true;};
        }
        void HighlightSelected(){
            highlights.IsHitTestVisible=!showTranslated;highlights.SetSelection(showTranslated?null:page,original.SelectionStart,original.SelectionLength);
        }
    }
}
