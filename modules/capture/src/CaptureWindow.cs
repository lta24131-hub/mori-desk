using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QingJie {
    public sealed partial class CaptureWindow : Window {
        readonly Snapshot shot;
        readonly Grid root=new Grid();
        readonly Canvas overlay=new Canvas();
        readonly ScreenshotSurface surface=new ScreenshotSurface();
        readonly StackPanel toolbar=new StackPanel {Orientation=Orientation.Horizontal};
        readonly StackPanel properties=new StackPanel {Orientation=Orientation.Horizontal};
        readonly StackPanel lineModes=new StackPanel {Orientation=Orientation.Horizontal,Visibility=Visibility.Collapsed};
        readonly Dictionary<string,Button> lineModeButtons=new Dictionary<string,Button>();
        readonly Border toolbarCard,propertiesCard;
        readonly TextBlock widthLabel=new TextBlock { VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,8,0),FontSize=12 };
        readonly Dictionary<string,Button> tools=new Dictionary<string,Button>();
        readonly Stack<Annotation> redo=new Stack<Annotation>();
        string tool="select";
        string lineKind="arrow";
        Point down;
        Rect oldSelection;
        bool dragging;
        int resize=-1;
        bool moving;
        CaptureTargets snapTargets;
        CaptureVisualRegions visualTargets;
        CancellationTokenSource visualTargetCancel;
        readonly SnapSelection snap=new SnapSelection();
        readonly CaptureSnapHover snapHover=new CaptureSnapHover();
        bool selectingNew;
        Annotation last;
        TextBox textEditor;
        Button translateButton;
        string translationTarget="zh";
        readonly TextBlock translationStatus=new TextBlock {Foreground=Brushes.White,Background=Ui.Brush("#E025302B"),Padding=new Thickness(9,5,9,5),MaxWidth=420,TextWrapping=TextWrapping.Wrap,Visibility=Visibility.Collapsed};
        CancellationTokenSource translationCancel;
        ImageTranslationResult imageTranslationResult;
        OcrPage imageTranslationPage;
        BitmapSource imageTranslationSource;
        string imageTranslationProvider,imageTranslationTarget;
        bool closed,collecting;
        readonly System.Windows.Threading.DispatcherTimer cursorLabelTimer=new System.Windows.Threading.DispatcherTimer {Interval=TimeSpan.FromMilliseconds(850)};
        public CaptureWindow(Snapshot snapshot) : this(snapshot,null) {}
        internal CaptureWindow(Snapshot snapshot,CaptureTargets targets) {
            snapTargets=targets;
            shot=snapshot;surface.Image=shot.Image;Title="Nook · 截图";WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;ShowActivated=false;Topmost=true;Background=Brushes.Black;Cursor=Cursors.Cross;
            FontFamily=new FontFamily("Microsoft YaHei UI");UseLayoutRounding=true;
            root.Children.Add(surface);root.Children.Add(overlay);Content=root;
            toolbarCard=Ui.Card(toolbar);propertiesCard=Ui.Card(properties);overlay.Children.Add(toolbarCard);overlay.Children.Add(propertiesCard);HideTools();
            toolbarCard.Cursor=Cursors.Arrow;propertiesCard.Cursor=Cursors.Arrow;
            Ui.Theme(widthLabel,TextBlock.ForegroundProperty,"Text");Ui.Theme(translationStatus,TextBlock.ForegroundProperty,"Text");Ui.Theme(translationStatus,TextBlock.BackgroundProperty,"Surface");
            AddTool("select","选择 / 调整 · V · 方向键微调 · Shift 调大小 · Ctrl 加速");AddTool("rect","矩形 · R · 滚轮调粗细");AddTool("ellipse","椭圆 · O · 滚轮调粗细");AddTool("arrow","箭头 A / 直线 L · 下方切换");AddTool("pen","画笔 · P · 滚轮调粗细");AddTool("text","文字 · T · 双击已有文字编辑");AddTool("mosaic","马赛克 · M");
            Ui.Separator(toolbar);toolbar.Children.Add(CaptureAction("Undo",Ui.Tool("undo","撤销 · Ctrl+Z",Undo)));
            toolbar.Children.Add(CaptureAction("Ocr",Ui.Tool("ocr","文字选择 / 翻译",()=>ReadText(false))));translateButton=Ui.Tool("translate","选区原位翻译 / 原图",()=>ReadText(true));toolbar.Children.Add(translateButton);
            var targetMenu=Ui.TranslationTarget(translationTarget,language=>{translationTarget=language;ResetTranslation();surface.InvalidateVisual();});toolbar.Children.Add(targetMenu);
            targetMenu.ContextMenu.PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape){e.Handled=true;targetMenu.ContextMenu.IsOpen=false;}};
            targetMenu.ContextMenu.Closed+=(s,e)=>FocusCaptureOverlay();
            toolbar.Children.Add(CaptureAction("Pin",Ui.Tool("pin","贴到屏幕",Pin)));toolbar.Children.Add(CaptureAction("Save",Ui.Tool("save","保存 · Ctrl+S",Save)));
            if(MoriDesk.DeskRuntime.CollectImage!=null){
                var collect=Ui.Tool("library","收藏到素材库 · 未分类 · Ctrl+D",async ()=>{await CollectToLibraryAsync();});
                collect.Name="CaptureCollect";toolbar.Children.Add(collect);
            }
            overlay.Children.Add(translationStatus);
            Ui.Separator(toolbar);toolbar.Children.Add(CaptureAction("Cancel",Ui.Tool("close","退出截图 · Esc 逐步返回",AppState.CancelCapture)));toolbar.Children.Add(CaptureAction("Copy",Ui.Tool("done","复制并完成 · Enter / Ctrl+C",Copy)));
            properties.Children.Add(lineModes);
            foreach(string kind in new[]{"arrow","line"}){string choice=kind;var b=Ui.Tool(kind,kind=="arrow"?"箭头":"直线",()=>SetTool(choice),28);b.Name="LineMode_"+kind;lineModeButtons[kind]=b;lineModes.Children.Add(b);}Ui.Separator(lineModes);
            foreach(string color in new[]{"#EF4444","#F2B01E","#07A56B","#3478F6","#222222","#FFFFFF"}) {
                string chosen=color;var b=new Button {Width=18,Height=18,Margin=new Thickness(4),Background=Ui.Brush(color),BorderBrush=Ui.Brush("#B9C0C0"),BorderThickness=new Thickness(1),ToolTip="标注颜色",Focusable=false};
                Ui.Theme(b,Control.BorderBrushProperty,"Border");
                b.Click+=(s,e)=>ApplyAnnotationColor(chosen);properties.Children.Add(b);
            }
            properties.Children.Add(widthLabel);var strokeHint=new TextBlock {Text="滚轮调粗细",FontSize=11,Margin=new Thickness(4,0,7,0),VerticalAlignment=VerticalAlignment.Center};Ui.Theme(strokeHint,TextBlock.ForegroundProperty,"Muted");properties.Children.Add(strokeHint);UpdateWidth();
            SourceInitialized+=(s,e)=>Native.SetWindowPos(new WindowInteropHelper(this).Handle,Native.Topmost,shot.Bounds.X,shot.Bounds.Y,shot.Bounds.Width,shot.Bounds.Height,0x0050);
            Loaded+=(s,e)=>{UpdateSnap(Mouse.GetPosition(surface));surface.InvalidateVisual();if(snapTargets!=null)Dispatcher.BeginInvoke(new Action(PrepareVisualTargets),System.Windows.Threading.DispatcherPriority.Background);if(!string.IsNullOrEmpty(shot.ColorWarning)){translationStatus.Text="HDR 兼容采集暂不可用，当前截图颜色可能有偏差。";translationStatus.Visibility=Visibility.Visible;Canvas.SetLeft(translationStatus,16);Canvas.SetTop(translationStatus,Math.Max(16,surface.ActualHeight-48));}};
            MouseEnter+=(s,e)=>{if(AppState.CanFocusCaptureFromPointer(this))FocusCaptureOverlay();};
            surface.MouseLeftButtonDown+=Down;surface.MouseMove+=Move;surface.MouseLeftButtonUp+=Up;surface.MouseWheel+=Wheel;
            surface.LostMouseCapture+=(s,e)=>{if(dragging)CancelCaptureStep();};
            surface.MouseLeave+=(s,e)=>{if(!dragging){surface.BrushPoint=null;ClearSnap();surface.InvalidateVisual();}};
            cursorLabelTimer.Tick+=(s,e)=>{cursorLabelTimer.Stop();surface.ShowBrushSize=false;surface.InvalidateVisual();};
            surface.MouseRightButtonDown+=(s,e)=>{CancelFromPointer();e.Handled=true;};
            PreviewKeyDown+=KeyDownHandler;
            PreviewKeyUp+=(s,e)=>{if(e.Key==Key.LeftCtrl||e.Key==Key.RightCtrl)UpdateSnap(Mouse.GetPosition(surface));};
            SizeChanged+=(s,e)=>{PositionTools();surface.InvalidateVisual();};
            AppState.TranslationProviderChanged+=RefreshProvider;
            Closed+=(s,e)=>{closed=true;if(visualTargetCancel!=null)visualTargetCancel.Cancel();snapTargets=null;visualTargets=null;ClearSnap();targetMenu.ContextMenu.IsOpen=false;AppState.TranslationProviderChanged-=RefreshProvider;ResetTranslation();cursorLabelTimer.Stop();ClearTextState();surface.Image=null;surface.Marks.Clear();surface.Draft=null;redo.Clear();last=null;shot.Image=null;AppState.CaptureClosed(this);};
        }
        async void PrepareVisualTargets(){
            if(closed||visualTargetCancel!=null||visualTargets!=null||shot.Image==null)return;
            var cancel=new CancellationTokenSource();visualTargetCancel=cancel;
            var image=shot.Image;var bounds=shot.Bounds;
            try{
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                // First paint and manual selection never wait for card detection.
                // Frozen snapshot pixels are safe to analyse on a worker; only the
                // resulting rectangles return to this window's dispatcher.
                var regions=await System.Threading.Tasks.Task.Run(()=>CaptureVisualRegions.Read(image,bounds,cancel.Token));
                if(closed||cancel.IsCancellationRequested)return;
                visualTargets=regions;
                // Do not replace a wheel-chosen target or an in-progress gesture.
                // New geometry participates naturally on the next pointer move.
            }catch(OperationCanceledException){}
            catch{ /* Native targets and manual capture remain available. */ }
            finally{if(visualTargetCancel==cancel)visualTargetCancel=null;cancel.Dispose();}
        }
        void ClearSnap(){snap.Candidate=Rect.Empty;snapHover.Clear();if(!surface.HoverSelection.IsEmpty){surface.HoverSelection=Rect.Empty;surface.InvalidateVisual();}}
        void UpdateSnap(Point point){
            if(dragging||tool!="select"||!surface.Selection.IsEmpty||snapTargets==null||(Keyboard.Modifiers&ModifierKeys.Control)!=0||surface.ActualWidth<=0||surface.ActualHeight<=0){ClearSnap();return;}
            var screen=new Rect(shot.Bounds.X,shot.Bounds.Y,shot.Bounds.Width,shot.Bounds.Height);
            var physical=new Point(screen.X+point.X*screen.Width/surface.ActualWidth,screen.Y+point.Y*screen.Height/surface.ActualHeight);
            Rect visible;var choices=snapTargets.At(physical,screen,visualTargets,out visible);
            var candidate=snapHover.Update(choices,visible,physical,screen.Width/surface.ActualWidth,screen.Height/surface.ActualHeight);
            ApplySnap(candidate);
        }
        void ApplySnap(Rect physical){
            snap.Candidate=CaptureTargets.ToLocal(physical,shot.Bounds,surface.ActualWidth,surface.ActualHeight);
            if(surface.HoverSelection!=snap.Candidate){surface.HoverSelection=snap.Candidate;surface.InvalidateVisual();}
        }
        void AddTool(string id,string title) {var b=Ui.Tool(id,title,()=>SetTool(id=="arrow"?lineKind:id));b.Name="CaptureTool_"+id;tools[id]=b;toolbar.Children.Add(b);}
        internal string SelectedTool {get{return tool;}}
        void SetTool(string name) {
            FinishShapeGesture();FinishTextGesture();CommitText();tool=name;last=null;if(name!="select"&&(name!="text"||surface.SelectedAnnotation!=null))SelectText(null);else last=SelectedAnnotation;surface.ShowHandles=name=="select";Cursor=Cursors.Arrow;UpdateBrushCursor(Mouse.GetPosition(surface));
            bool linear=name=="arrow"||name=="line";if(linear){lineKind=name;tools["arrow"].Content=new ToolIcon(name);tools["arrow"].ToolTip=(name=="line"?"直线 · L":"箭头 · A")+" · 下方切换 · 滚轮调粗细";System.Windows.Automation.AutomationProperties.SetName(tools["arrow"],tools["arrow"].ToolTip.ToString());}
            lineModes.Visibility=linear?Visibility.Visible:Visibility.Collapsed;
            foreach(var kv in lineModeButtons)Ui.SetActive(kv.Value,kv.Key==lineKind);
            foreach(var kv in tools)Ui.SetActive(kv.Value,kv.Key==(linear?"arrow":name));PositionTools();surface.InvalidateVisual();
        }
        int HitSelectionHandle(Point point){
            if(surface.Selection.IsEmpty)return -1;
            var points=surface.Handles();int handle=-1;double closest=81;
            for(int i=0;i<points.Length;i++){double distance=(point-points[i]).LengthSquared;if(distance<closest){closest=distance;handle=i;}}
            return handle;
        }
        static Cursor ResizeCursor(int handle){
            if(handle==0||handle==4)return Cursors.SizeNWSE;
            if(handle==2||handle==6)return Cursors.SizeNESW;
            return handle==1||handle==5?Cursors.SizeNS:Cursors.SizeWE;
        }
        void UpdateBrushCursor(Point point){
            Cursor textCursor=(!dragging||textDrag!=null)?TextPointerCursor(point):null;
            if(textCursor==null&&(!dragging||shapeDrag!=null))textCursor=ShapePointerCursor(point);
            if(textCursor!=null){surface.Cursor=textCursor;surface.BrushPoint=null;surface.InvalidateVisual();return;}
            bool drawing=tool!="select"&&tool!="text"&&!surface.Selection.IsEmpty&&surface.Selection.Contains(point);
            if(tool=="select"){
                int handle=ActiveSelectionHandle(point);
                bool moveCursor=dragging?moving:!surface.Selection.IsEmpty&&surface.Selection.Contains(point);
                surface.Cursor=handle>=0?ResizeCursor(handle):moveCursor?Cursors.SizeAll:Cursors.Cross;
            }else surface.Cursor=drawing?Cursors.None:tool=="text"?Cursors.IBeam:Cursors.Cross;
            surface.BrushPoint=drawing?(Point?)point:null;surface.BrushDiameter=AppState.Settings.Stroke;surface.InvalidateVisual();
        }
        Point ClampPoint(Point p) {var bounds=surface.Selection.IsEmpty?new Rect(0,0,surface.ActualWidth,surface.ActualHeight):surface.Selection;return new Point(Geometry.Clamp(p.X,bounds.Left,bounds.Right),Geometry.Clamp(p.Y,bounds.Top,bounds.Bottom));}
        void Down(object s,MouseButtonEventArgs e) {if(BeginPointer(e.GetPosition(surface),e.ClickCount))e.Handled=true;}
        internal bool BeginPointer(Point p,int clickCount=1) {
            if(closed||collecting||captureOverlaySuspended||!AppState.CanEnterCapture(this))return false;
            CommitText();
            down=p;oldSelection=surface.Selection;moving=false;resize=-1;selectingNew=false;
            if(BeginTextPointer(p,clickCount))return true;
            if(BeginShapePointer(p))return true;
            if(clickCount==2&&CanCompleteCaptureAt(p)){Copy();return true;}
            SelectText(null);
            if(tool=="select") {
                ResetTranslation();
                selectionBefore=CaptureScene();
                if(!surface.Selection.IsEmpty){resize=HitSelectionHandle(p);moving=resize<0&&surface.Selection.Contains(p);}
                if(resize<0&&!moving){
                    if(surface.Selection.IsEmpty)UpdateSnap(p);else ClearSnap();
                    snap.Begin(p,(Keyboard.Modifiers&ModifierKeys.Control)!=0);snapHover.Clear();selectingNew=true;
                    surface.HoverSelection=Rect.Empty;surface.Selection=Rect.Empty;surface.Marks.Clear();last=null;
                }
            } else {
                if(surface.Selection.IsEmpty||!surface.Selection.Contains(p))return false;
                if(tool=="text"){StartText(p);return true;}
                surface.Draft=new Annotation {Kind=tool,Start=p,End=p,Width=AppState.Settings.Stroke,Color=AppState.Settings.Color};surface.Draft.Points.Add(p);
            }
            dragging=true;UpdateBrushCursor(p);HideTools();surface.CaptureMouse();return true;
        }
        void Move(object s,MouseEventArgs e) {ContinuePointer(e.GetPosition(surface));}
        internal void ContinuePointer(Point p) {
            UpdateBrushCursor(p);
            if(textDrag!=null){MoveSelectedText(p);surface.InvalidateVisual();return;}
            if(shapeDrag!=null){MoveSelectedShape(p);return;}
            if(!dragging){UpdateSnap(p);return;}
            if(tool=="select") {
                p=new Point(Geometry.Clamp(p.X,0,surface.ActualWidth),Geometry.Clamp(p.Y,0,surface.ActualHeight));
                if(moving){double dx=Geometry.Clamp(p.X-down.X,-oldSelection.Left,surface.ActualWidth-oldSelection.Right),dy=Geometry.Clamp(p.Y-down.Y,-oldSelection.Top,surface.ActualHeight-oldSelection.Bottom);surface.Selection=new Rect(oldSelection.X+dx,oldSelection.Y+dy,oldSelection.Width,oldSelection.Height);}
                else if(resize>=0) {
                    double l=oldSelection.Left,r=oldSelection.Right,t=oldSelection.Top,b=oldSelection.Bottom;
                    if(resize==0||resize==6||resize==7)l=p.X;if(resize==2||resize==3||resize==4)r=p.X;
                    if(resize==0||resize==1||resize==2)t=p.Y;if(resize==4||resize==5||resize==6)b=p.Y;
                    surface.Selection=Geometry.FromPoints(new Point(l,t),new Point(r,b));
                } else if(snap.Move(p))surface.Selection=Geometry.FromPoints(down,p);
            } else if(surface.Draft!=null) {p=ClampPoint(p);surface.Draft.End=p;if(tool=="pen")surface.Draft.Points.Add(p);}
            UpdateBrushCursor(p);surface.InvalidateVisual();
        }
        void Up(object s,MouseButtonEventArgs e) {EndPointer(e.GetPosition(surface));}
        internal void EndPointer(Point p) {
            if(!dragging)return;ContinuePointer(p);if(textDrag!=null){FinishTextGesture();return;}if(shapeDrag!=null){FinishShapeGesture();return;}dragging=false;surface.ReleaseMouseCapture();
            if(surface.Draft!=null){if(HasDrawingContent(surface.Draft)){if(tool=="mosaic")surface.PrepareMosaic(surface.Draft);surface.Marks.Add(surface.Draft);last=surface.Draft;RecordAnnotationAdded(last);}surface.Draft=null;}
            if(tool=="select")FinishSelectionGesture();
            if(surface.Selection.IsEmpty)UpdateSnap(p);else ClearSnap();UpdateBrushCursor(p);PositionTools();surface.InvalidateVisual();
        }
        void Wheel(object s,MouseWheelEventArgs e) {
            if(textEditor!=null){textEditor.FontSize=Geometry.Clamp(textEditor.FontSize+(e.Delta>0?5:-5),16,250);UpdateWidth();e.Handled=true;return;}
            if(!dragging&&surface.Selection.IsEmpty&&tool=="select"&&snapTargets!=null){
                CycleSnap(e.GetPosition(surface),e.Delta<0?1:-1);e.Handled=true;return;
            }
            if(surface.SelectedText!=null&&textEditor==null&&!dragging){ChangeSelectedTextSize(e.Delta>0?5:-5);e.Handled=true;return;}
            if(surface.Selection.IsEmpty||tool=="select"&&surface.SelectedAnnotation==null)return;
            ChangeStroke(e.Delta>0?1:-1);
            surface.ShowBrushSize=true;cursorLabelTimer.Stop();cursorLabelTimer.Start();UpdateBrushCursor(e.GetPosition(surface));
            surface.InvalidateVisual();e.Handled=true;
        }
        internal void CycleSnap(Point point,int direction){
            UpdateSnap(point);
            if(!snap.Candidate.IsEmpty&&snapHover.Choices.Count>1)ApplySnap(snapHover.Cycle(direction));
        }
        void UpdateWidth(){UpdateCapturePropertyPresentation();}
        void HideTools(){toolbarCard.Visibility=Visibility.Collapsed;propertiesCard.Visibility=Visibility.Collapsed;}
        void PositionTools(){AppState.SyncCaptureSelection(this);PositionAdaptiveTools();}
        void Undo(){CommitText();UndoAnnotation();}
        void Redo(){RedoAnnotation();}
        void KeyDownHandler(object s,KeyEventArgs e){
            if(HandleCaptureKey(e.Key,Keyboard.Modifiers))e.Handled=true;
        }
        void Guard(Action f){try{CommitText();f();}catch(Exception ex){MessageBox.Show(this,ex.Message,"Nook",MessageBoxButton.OK,MessageBoxImage.Information);}}
        async void CollectToLibrary(){await CollectToLibraryAsync();}
        internal async System.Threading.Tasks.Task<bool> CollectToLibraryAsync(){
            var collect=MoriDesk.DeskRuntime.CollectImage;
            if(closed||collecting||collect==null)return false;
            collecting=true;
            try{
                CommitText();var image=surface.Export();
                surface.IsEnabled=false;toolbar.IsEnabled=false;properties.IsEnabled=false;
                TranslationStatus("正在收藏到素材库…");
                bool saved=await collect(image);
                if(closed)return saved;
                if(saved){Close();return true;}
                TranslationStatus("尚未收藏，截图已保留；可以重试、复制或保存。");return false;
            }catch(Exception ex){if(!closed)TranslationStatus("收藏失败，截图已保留："+ex.Message);return false;}
            finally{collecting=false;if(!closed){surface.IsEnabled=true;toolbar.IsEnabled=true;properties.IsEnabled=true;}}
        }
        void Copy(){Guard(()=>{ImageFiles.Copy(surface.Export());Close();});}
        void Save(){Guard(()=>{Topmost=false;try{if(ImageFiles.Save(surface.Export(),this))Close();}finally{Topmost=true;}});}
        void Pin(){Guard(()=>{var image=surface.Export();var crop=Geometry.PixelCrop(surface.Selection,surface.ScaleX,surface.ScaleY,shot.Image.PixelWidth,shot.Image.PixelHeight);var bounds=new Int32Rect(shot.Bounds.X+crop.X,shot.Bounds.Y+crop.Y,crop.Width,crop.Height);Close();AppState.Pins.NewAt(image,bounds);});}
        void ReadText(bool translate){if(translate){TranslateInPlace();return;}Guard(()=>{var crop=surface.Export(false,false);Close();new TextWindow(crop,translationTarget).Show();});}
        void ResetTranslation(){if(translationCancel!=null){translationCancel.Cancel();translationCancel=null;}imageTranslationResult=null;imageTranslationPage=null;imageTranslationSource=null;imageTranslationProvider=null;imageTranslationTarget=null;surface.ClearTranslation();translationStatus.Text="";translationStatus.Visibility=Visibility.Collapsed;if(translateButton!=null){translateButton.IsEnabled=true;Ui.SetActive(translateButton,false);}}
        void RefreshProvider(){ResetTranslation();surface.InvalidateVisual();}
        void TranslationStatus(string text){translationStatus.Text=text;translationStatus.Visibility=Visibility.Visible;PositionTools();}
        void ImageTranslationStatus(){
            if(imageTranslationResult==null||imageTranslationResult.UnplacedCount==0){translationStatus.Visibility=Visibility.Collapsed;PositionTools();return;}
            translationStatus.Inlines.Clear();translationStatus.Inlines.Add(new Run("有 "+imageTranslationResult.UnplacedCount+" 处保留原文 · "));
            var full=new Hyperlink(new Run("查看完整译文")) {Cursor=Cursors.Hand,Foreground=Ui.Tone("Accent")};if(Application.Current!=null&&Application.Current.TryFindResource("Accent")!=null)full.SetResourceReference(TextElement.ForegroundProperty,"Accent");
            full.Click+=(s,e)=>{e.Handled=true;ViewFullTranslation();};translationStatus.Inlines.Add(full);translationStatus.Visibility=Visibility.Visible;PositionTools();
        }
        void ViewFullTranslation(){
            if(imageTranslationResult==null||imageTranslationPage==null||imageTranslationSource==null||imageTranslationTarget!=translationTarget||imageTranslationProvider!=Translation.Provider)return;
            // Keep references before Close clears this capture's cached state.
            var source=imageTranslationSource;var page=imageTranslationPage;var translated=imageTranslationResult;string target=imageTranslationTarget,provider=imageTranslationProvider;
            Guard(()=>{var viewer=new TextWindow(source,target,page,translated,provider);Close();viewer.Show();});
        }
        async void TranslateInPlace(){
            if(translationCancel!=null)return;
            if(surface.TranslationImage!=null){surface.ShowTranslation=!surface.ShowTranslation;Ui.SetActive(translateButton,surface.ShowTranslation);surface.InvalidateVisual();ImageTranslationStatus();return;}
            var cancel=new CancellationTokenSource();string target=translationTarget,provider=Translation.Provider;translationCancel=cancel;translateButton.IsEnabled=false;
            try{
                CommitText();var selected=surface.Selection;var crop=surface.Export(false,false);TranslationStatus("正在本机识别文字…");
                var page=await OcrService.Read(crop,cancel.Token);cancel.Token.ThrowIfCancellationRequested();var regions=ImageTranslation.Regions(page);
                if(regions.Count==0){TranslationStatus("没有识别到文字，原图保留。");return;}
                TranslationStatus(Translation.ProgressText);
                var texts=await Translation.TranslateImage(regions.Select(r=>r.Text).ToArray(),cancel.Token,target,provider);cancel.Token.ThrowIfCancellationRequested();
                if(closed||translationCancel!=cancel||selected!=surface.Selection||translationTarget!=target||Translation.Provider!=provider)return;
                var translated=ImageTranslation.RenderWithStatus(crop,regions,texts);
                imageTranslationResult=translated;imageTranslationPage=page;imageTranslationSource=crop;imageTranslationTarget=target;imageTranslationProvider=provider;
                surface.TranslationImage=translated.Image;surface.TranslationBounds=selected;surface.ShowTranslation=true;surface.InvalidateVisual();ImageTranslationStatus();Ui.SetActive(translateButton,true);
            }catch(OperationCanceledException){if(!closed&&!cancel.IsCancellationRequested)TranslationStatus("翻译超时，原图保留，可点击重试。");}
            catch(Exception ex){if(!closed&&!cancel.IsCancellationRequested)TranslationStatus(ex.Message);}
            finally{if(translationCancel==cancel){translationCancel=null;if(!closed)translateButton.IsEnabled=true;}cancel.Dispose();}
        }
    }
}
