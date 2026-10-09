using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace QingJie {
    // Image-first viewer: the full OCR transcript remains visible while any range is selected.
    public sealed class TextWindow : Window {
        readonly BitmapSource image;
        BitmapSource translatedImage;
        readonly Image picture=new Image {Stretch=Stretch.Fill};
        readonly OcrHighlights highlights=new OcrHighlights {Cursor=Cursors.IBeam};
        readonly TextBox original=new TextBox {IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,FontSize=14,Padding=new Thickness(16,18,14,12),BorderThickness=new Thickness(0),Background=Ui.Brush("#FAFAFA"),Foreground=Ui.Ink,IsInactiveSelectionHighlightEnabled=true,SelectionBrush=Ui.Brush("#80DBB5"),SelectionOpacity=.65};
        readonly TextBox result=new TextBox {IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MaxHeight=150,MinHeight=40,BorderThickness=new Thickness(0),Padding=new Thickness(8),Background=Ui.Brush("#ECF7F1"),FontSize=14};
        readonly StackPanel selectionActions=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(6),Visibility=Visibility.Collapsed};
        readonly Border resultCard=new Border {Background=Ui.Brush("#ECF7F1"),BorderBrush=Ui.Brush("#D6E5DB"),BorderThickness=new Thickness(0,1,0,0),Visibility=Visibility.Collapsed};
        readonly TextBlock status=new TextBlock {Foreground=Brushes.White,TextWrapping=TextWrapping.Wrap,FontSize=12,Margin=new Thickness(8)};
        readonly Border statusCard;
        readonly ScrollViewer scroll=new ScrollViewer {HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalContentAlignment=HorizontalAlignment.Center,VerticalContentAlignment=VerticalAlignment.Center};
        readonly Viewbox preview;
        readonly ColumnDefinition sidebarColumn=new ColumnDefinition {Width=new GridLength(284)};
        readonly Border sidebar;
        readonly Button translateSelection,translatePicture,showText,copyResult;
        readonly CancellationTokenSource cancel=new CancellationTokenSource();
        readonly Func<BitmapSource,CancellationToken,Task<OcrPage>> recognize;
        CancellationTokenSource imageRequest,selectionRequest;
        string translationTarget;
        Task<OcrPage> ocrTask;
        OcrPage page;
        bool closed,selectingImage,draggingText,showTranslated,fitMode=true;
        int imageAnchor=-1,textAnchor;
        double zoom=1;

        public TextWindow(BitmapSource source,string targetLanguage="zh"):this(source,targetLanguage,OcrService.Read){}
        internal TextWindow(BitmapSource source,string targetLanguage,Func<BitmapSource,CancellationToken,Task<OcrPage>> recognize) {
            this.recognize=recognize;
            translationTarget=Translation.TargetLanguage(targetLanguage);
            image=source;Title="Mori Desk · 图片与文字";Icon=Ui.AppIcon;Width=1120;Height=620;MinWidth=740;MinHeight=360;
            WindowStartupLocation=WindowStartupLocation.CenterScreen;WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.CanResize;
            Background=Ui.Brush("#F7F7F7");FontFamily=new FontFamily("Segoe UI, Microsoft YaHei UI");UseLayoutRounding=true;
            WindowChrome.SetWindowChrome(this,new WindowChrome {CaptionHeight=37,ResizeBorderThickness=new Thickness(5),GlassFrameThickness=new Thickness(0),CornerRadius=new CornerRadius(0)});
            var root=new Grid();root.RowDefinitions.Add(new RowDefinition {Height=new GridLength(37)});root.RowDefinitions.Add(new RowDefinition());
            Content=new Border {Child=root,BorderBrush=Ui.Brush("#D9D9DA"),BorderThickness=new Thickness(1)};
            var top=new DockPanel {Background=Ui.Brush("#E8E8EA")};root.Children.Add(top);
            var caption=new StackPanel {Orientation=Orientation.Horizontal};DockPanel.SetDock(caption,Dock.Right);top.Children.Add(caption);WindowChrome.SetIsHitTestVisibleInChrome(caption,true);
            caption.Children.Add(Symbol("\uE921","最小化",()=>WindowState=WindowState.Minimized,44));
            caption.Children.Add(Symbol("\uE922","最大化 / 还原",()=>WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized,44));
            caption.Children.Add(Symbol("\uE8BB","关闭",Close,44));
            var bar=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(3,0,0,0)};top.Children.Add(bar);WindowChrome.SetIsHitTestVisibleInChrome(bar,true);
            // Standalone image viewers do not own the shared pin store. Only the
            // main suite process writes pin history, avoiding competing writers.
            if(AppState.Pins!=null){bar.Children.Add(Symbol("\uE718","贴到屏幕",()=>Try(()=>AppState.Pins.New(Displayed))));Separator(bar);}
            bar.Children.Add(Symbol("\uE8A3","放大",()=>ChangeZoom(zoom*1.2)));bar.Children.Add(Symbol("\uE71F","缩小",()=>ChangeZoom(zoom/1.2)));
            bar.Children.Add(Symbol("\uE9A6","适应窗口",()=>{fitMode=true;Fit();}));bar.Children.Add(Symbol("\uE740","实际大小 1:1",()=>ChangeZoom(1)));Separator(bar);
            bar.Children.Add(Symbol("\uE8C8","复制图片",()=>Try(()=>ImageFiles.Copy(Displayed))));
            translatePicture=Symbol("\uE8C1","整图翻译 / 原图 · 使用设置中选择的服务",ToggleImageTranslation);bar.Children.Add(translatePicture);
            bar.Children.Add(Ui.TranslationTarget(translationTarget,ChangeTarget));
            showText=Symbol("\uE8A5","显示 / 隐藏全部识别文字",ToggleSidebar);bar.Children.Add(showText);Mark(showText,true);
            bar.Children.Add(Symbol("\uE74E","保存图片",()=>Try(()=>ImageFiles.Save(Displayed,this))));
            var columns=new Grid();columns.ColumnDefinitions.Add(new ColumnDefinition());columns.ColumnDefinitions.Add(sidebarColumn);Grid.SetRow(columns,1);root.Children.Add(columns);
            var imageArea=new Grid {Background=Ui.Brush("#F7F7F7")};columns.Children.Add(imageArea);imageArea.Children.Add(scroll);
            var drawing=new Grid {Width=image.PixelWidth,Height=image.PixelHeight};picture.Source=image;drawing.Children.Add(picture);drawing.Children.Add(highlights);
            preview=new Viewbox {Child=drawing,Stretch=Stretch.Fill};scroll.Content=preview;scroll.SizeChanged+=(s,e)=>{if(fitMode)Fit();};
            scroll.PreviewMouseWheel+=(s,e)=>{if((Keyboard.Modifiers&ModifierKeys.Control)!=0){ChangeZoom(zoom*(e.Delta>0?1.12:1/1.12));e.Handled=true;}};
            statusCard=new Border {Child=status,Background=Ui.Brush("#DD29322D"),CornerRadius=new CornerRadius(4),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(12),MaxWidth=480,Visibility=Visibility.Collapsed};imageArea.Children.Add(statusCard);
            var right=new Grid();right.RowDefinitions.Add(new RowDefinition());right.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});right.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            sidebar=new Border {Child=right,Background=Ui.Brush("#FAFAFA"),BorderBrush=Ui.Brush("#ECECEE"),BorderThickness=new Thickness(1,0,0,0)};Grid.SetColumn(sidebar,1);columns.Children.Add(sidebar);
            right.Children.Add(original);Grid.SetRow(selectionActions,1);right.Children.Add(selectionActions);
            selectionActions.Children.Add(ActionButton("复制",()=>CopyText(original.SelectedText)));
            translateSelection=ActionButton("翻译所选",TranslateSelected,true);translateSelection.ToolTip="翻译所选文字 · Ctrl+Enter";selectionActions.Children.Add(translateSelection);
            var resultPanel=new StackPanel();var resultHeader=new DockPanel {Margin=new Thickness(8,4,4,0)};
            var closeResult=Symbol("\uE8BB","收起译文",()=>resultCard.Visibility=Visibility.Collapsed,28);DockPanel.SetDock(closeResult,Dock.Right);resultHeader.Children.Add(closeResult);
            copyResult=ActionButton("复制译文",()=>CopyText(result.Text));copyResult.IsEnabled=false;DockPanel.SetDock(copyResult,Dock.Right);resultHeader.Children.Add(copyResult);resultHeader.Children.Add(new TextBlock {Text="译文",Foreground=Ui.Ink,VerticalAlignment=VerticalAlignment.Center});
            resultPanel.Children.Add(resultHeader);resultPanel.Children.Add(result);resultCard.Child=resultPanel;Grid.SetRow(resultCard,2);right.Children.Add(resultCard);
            ConfigureTextSelection();ConfigureImageSelection();
            original.SelectionChanged+=(s,e)=>{ResetSelectedTranslation();selectionActions.Visibility=original.SelectionLength>0?Visibility.Visible:Visibility.Collapsed;HighlightSelected();};
            Loaded+=async(s,e)=>{Fit();try{await EnsureOcr();}catch(Exception ex){if(!closed)Status(ex.Message);}};
            AppState.TranslationProviderChanged+=RefreshProvider;
            Closed+=(s,e)=>{closed=true;AppState.TranslationProviderChanged-=RefreshProvider;cancel.Cancel();picture.Source=null;translatedImage=null;highlights.SetSelection(null,0,0);};
            PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape){Close();e.Handled=true;}else if(e.Key==Key.Enter&&(Keyboard.Modifiers&ModifierKeys.Control)!=0){TranslateSelected();e.Handled=true;}else if(e.Key==Key.S&&(Keyboard.Modifiers&ModifierKeys.Control)!=0){Try(()=>ImageFiles.Save(Displayed,this));e.Handled=true;}};
        }
        BitmapSource Displayed {get{return showTranslated&&translatedImage!=null?translatedImage:image;}}
        // Segoe MDL2 Assets is Windows' installed UI icon library; no image assets or web runtime.
        static Button Symbol(string glyph,string title,Action action,double width=38){
            var b=new Button {Width=width,Height=35,Background=Brushes.Transparent,BorderThickness=new Thickness(0),Focusable=false,ToolTip=title,Cursor=Cursors.Hand,Content=new TextBlock {Text=glyph,FontFamily=new FontFamily("Segoe MDL2 Assets"),FontSize=15,Foreground=Ui.Brush("#5C5C61"),VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center}};
            var border=new FrameworkElementFactory(typeof(Border));border.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));var content=new FrameworkElementFactory(typeof(ContentPresenter));content.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);content.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);border.AppendChild(content);b.Template=new ControlTemplate(typeof(Button)){VisualTree=border};
            b.MouseEnter+=(s,e)=>{if(b.Tag==null)b.Background=Ui.Brush("#DCDDE0");};b.MouseLeave+=(s,e)=>{if(b.Tag==null)b.Background=Brushes.Transparent;};b.Click+=(s,e)=>action();System.Windows.Automation.AutomationProperties.SetName(b,title);return b;
        }
        static Button ActionButton(string name,Action action,bool primary=false){var b=Ui.TextButton(name,action,primary);b.Focusable=false;return b;}
        static void Separator(Panel p){p.Children.Add(new Border {Width=1,Height=20,Background=Ui.Brush("#D0D0D4"),Margin=new Thickness(6,0,6,0),VerticalAlignment=VerticalAlignment.Center});}
        static void Mark(Button b,bool active){b.Tag=active?(object)true:null;b.Background=active?Ui.Brush("#CEEBDD"):Brushes.Transparent;}
        void Status(string message){status.Text=message;statusCard.Visibility=Visibility.Visible;}
        void ClearStatus(){statusCard.Visibility=Visibility.Collapsed;}
        void Try(Action action){try{action();}catch(Exception ex){Status(ex.Message);}}
        void CopyText(string text){if(!string.IsNullOrEmpty(text))Try(()=>Clipboard.SetText(text));}
        void Fit(){zoom=Math.Max(.02,Math.Min(1,Math.Min(Math.Max(1,scroll.ActualWidth-4)/image.PixelWidth,Math.Max(1,scroll.ActualHeight-4)/image.PixelHeight)));ApplyZoom();}
        void ChangeZoom(double amount){fitMode=false;zoom=Geometry.Clamp(amount,.02,8);ApplyZoom();}
        void ApplyZoom(){preview.Width=image.PixelWidth*zoom;preview.Height=image.PixelHeight*zoom;}
        void ToggleSidebar(){bool show=sidebar.Visibility!=Visibility.Visible;sidebar.Visibility=show?Visibility.Visible:Visibility.Collapsed;sidebarColumn.Width=new GridLength(show?284:0);Mark(showText,show);}
        internal async Task<OcrPage> EnsureOcr(){
            if(page!=null)return page;if(ocrTask==null){Status("正在本机识别文字…");ocrTask=recognize(image,cancel.Token);}var request=ocrTask;OcrPage read;
            try{read=await request;}catch{if(ocrTask==request)ocrTask=null;throw;}if(closed)return read;
            if(page==null){page=read;original.Text=page.Text;ClearStatus();if(page.Words.Count==0)Status("未识别到文字，原图保留。");}return page;
        }
        async void ToggleImageTranslation(){
            if(imageRequest!=null)return;if(translatedImage!=null){showTranslated=!showTranslated;picture.Source=Displayed;Mark(translatePicture,showTranslated);HighlightSelected();return;}
            var request=CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);imageRequest=request;string target=translationTarget;translatePicture.IsEnabled=false;
            try{var text=await EnsureOcr();request.Token.ThrowIfCancellationRequested();if(closed||text.Words.Count==0)return;Status(Translation.ProgressText);var regions=ImageTranslation.Regions(text);var translated=await Translation.TranslateImage(regions.Select(r=>r.Text).ToArray(),request.Token,target);if(closed||imageRequest!=request||request.IsCancellationRequested)return;translatedImage=ImageTranslation.Render(image,regions,translated);showTranslated=true;picture.Source=translatedImage;Mark(translatePicture,true);HighlightSelected();ClearStatus();}
            catch(OperationCanceledException){if(!closed&&imageRequest==request&&!request.IsCancellationRequested)Status("翻译超时，原图保留。可再次点击翻译。");}catch(Exception ex){if(!closed&&imageRequest==request)Status(ex.Message);}
            finally{if(imageRequest==request){imageRequest=null;if(!closed)translatePicture.IsEnabled=true;}request.Dispose();}
        }
        void ResetSelectedTranslation(){if(selectionRequest!=null){selectionRequest.Cancel();selectionRequest=null;}translateSelection.IsEnabled=true;result.Clear();copyResult.IsEnabled=false;resultCard.Visibility=Visibility.Collapsed;}
        void ChangeTarget(string language){translationTarget=language;if(imageRequest!=null){imageRequest.Cancel();imageRequest=null;}ResetSelectedTranslation();translatedImage=null;showTranslated=false;picture.Source=image;translatePicture.IsEnabled=true;Mark(translatePicture,false);ClearStatus();HighlightSelected();}
        void RefreshProvider(){ChangeTarget(translationTarget);}
        async void TranslateSelected(){
            string selected=original.SelectedText;if(string.IsNullOrWhiteSpace(selected)||selectionRequest!=null)return;var request=CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);selectionRequest=request;string target=translationTarget;translateSelection.IsEnabled=false;copyResult.IsEnabled=false;resultCard.Visibility=Visibility.Visible;result.Text="正在翻译所选文字…";
            try{string translated=await Translation.Translate(selected,request.Token,target);if(!closed&&selectionRequest==request&&!request.IsCancellationRequested){result.Text=translated;copyResult.IsEnabled=true;}}
            catch(OperationCanceledException){if(!closed&&selectionRequest==request&&!request.IsCancellationRequested)result.Text="请求超时，请重试。";}catch(Exception ex){if(!closed&&selectionRequest==request)result.Text=ex.Message;}
            finally{if(selectionRequest==request){selectionRequest=null;if(!closed)translateSelection.IsEnabled=true;}request.Dispose();}
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
