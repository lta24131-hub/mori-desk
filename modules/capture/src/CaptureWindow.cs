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
        // A running capture owns one feature snapshot. Preferences changed in
        // another window apply to the next capture, never halfway through a gesture.
        internal readonly CaptureFeatures captureFeatures;
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
        string arrowStyle="filled";
        Point down;
        Rect oldSelection;
        bool dragging;
        int resize=-1;
        bool moving;
        bool pointerForceNew;
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
        internal CaptureWindow(Snapshot snapshot,CaptureTargets targets,CaptureFeatures features=null) {
            captureFeatures=(features??AppState.Settings.CaptureFeatures??new CaptureFeatures()).Copy();
            snapTargets=captureFeatures.SmartSelection?targets:null;
            shot=snapshot;surface.Image=shot.Image;Title="Nook · 截图";WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;ShowActivated=false;Topmost=true;Background=Brushes.Black;Cursor=Cursors.Cross;
            FontFamily=new FontFamily("Microsoft YaHei UI");UseLayoutRounding=true;
            root.Children.Add(surface);root.Children.Add(overlay);Content=root;
            toolbarCard=Ui.Card(toolbar);propertiesCard=Ui.Card(properties);overlay.Children.Add(toolbarCard);overlay.Children.Add(propertiesCard);HideTools();
            toolbarCard.Cursor=Cursors.Arrow;propertiesCard.Cursor=Cursors.Arrow;
            Ui.Theme(widthLabel,TextBlock.ForegroundProperty,"Text");Ui.Theme(translationStatus,TextBlock.ForegroundProperty,"Text");Ui.Theme(translationStatus,TextBlock.BackgroundProperty,"Surface");
            AddTool("rect","矩形 · R · Ctrl+滚轮调粗细");AddTool("ellipse","椭圆 · O · Ctrl+滚轮调粗细");AddTool("arrow","箭头 A / 直线 L · 下方切换");AddTool("pen","画笔 · P · Ctrl+滚轮调粗细");AddTool("text","文字 · T · 双击已有文字编辑");AddTool("mosaic","马赛克 · M");
            Ui.Separator(toolbar);toolbar.Children.Add(CaptureAction("Undo",Ui.Tool("undo","撤销 · Ctrl+Z",Undo)));
            if(captureFeatures.Ocr)toolbar.Children.Add(CaptureAction("Ocr",Ui.Tool("ocr",captureFeatures.Translation?"文字选择 / 翻译":"文字识别",()=>ReadText(false))));
            if(captureFeatures.Translation){translateButton=CaptureAction("Translate",Ui.Tool("translate","选区原位翻译 / 原图",()=>ReadText(true)));toolbar.Children.Add(translateButton);}
            Button targetMenu=null;
            if(captureFeatures.Translation){
                targetMenu=CaptureAction("Language",Ui.TranslationTarget(translationTarget,language=>{if(!CanEditCapture)return;translationTarget=language;ResetTranslation();surface.InvalidateVisual();}));toolbar.Children.Add(targetMenu);
                targetMenu.ContextMenu.PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape&&HandleCaptureKey(e.Key,ModifierKeys.None))e.Handled=true;};
                targetMenu.ContextMenu.Closed+=(s,e)=>FocusCaptureOverlay();
            }
            if(captureFeatures.Scrolling)toolbar.Children.Add(CaptureAction("Scroll",Ui.Tool("scroll","滚动截屏 · 在选区内向下滚动",BeginScrollingCapture)));
            toolbar.Children.Add(CaptureAction("Save",Ui.Tool("save","保存 · Ctrl+S",Save)));
            if(captureFeatures.Collect&&MoriDesk.DeskRuntime.CollectImage!=null){
                var collect=Ui.Tool("library","收藏到素材库 · 未分类 · Ctrl+D",async ()=>{await CollectToLibraryAsync();});
                collect.Name="CaptureCollect";toolbar.Children.Add(collect);
            }
            overlay.Children.Add(translationStatus);
            Ui.Separator(toolbar);
            if(captureFeatures.Pin)toolbar.Children.Add(CapturePinButton());
            toolbar.Children.Add(CaptureAction("Cancel",Ui.Tool("close","退出截图 · Esc 立即退出 · 右键逐步返回",AppState.CancelCapture)));toolbar.Children.Add(CaptureAction("Copy",Ui.Tool("done","复制并完成 · Enter / Ctrl+C",Copy)));
            if(captureFeatures.HasAnnotations){
            if(captureFeatures.Lines){properties.Children.Add(lineModes);
            foreach(string kind in new[]{"arrowFilled","arrow","arrowDouble","line"}){string choice=kind;var b=Ui.Tool(kind,CaptureLineTitle(kind),()=>SetCaptureLineMode(choice),28);b.Name="LineMode_"+kind;lineModeButtons[kind]=b;lineModes.Children.Add(b);}Ui.Separator(lineModes);}
            foreach(string color in new[]{"#EF4444","#F2B01E","#07A56B","#3478F6","#222222","#FFFFFF"}) {
                string chosen=color;var b=new Button {Width=18,Height=18,Margin=new Thickness(4),Background=Ui.Brush(color),BorderBrush=Ui.Brush("#B9C0C0"),BorderThickness=new Thickness(1),ToolTip="标注颜色",Focusable=false};
                Ui.Theme(b,Control.BorderBrushProperty,"Border");
                b.Click+=(s,e)=>ApplyAnnotationColor(chosen);properties.Children.Add(b);
            }
            properties.Children.Add(CaptureSizeButton(false));properties.Children.Add(widthLabel);properties.Children.Add(CaptureSizeButton(true));var strokeHint=new TextBlock {Text="Ctrl+滚轮调整",FontSize=11,Margin=new Thickness(4,0,7,0),VerticalAlignment=VerticalAlignment.Center};Ui.Theme(strokeHint,TextBlock.ForegroundProperty,"Muted");properties.Children.Add(strokeHint);UpdateWidth();
            }
            SourceInitialized+=(s,e)=>Native.SetWindowPos(new WindowInteropHelper(this).Handle,Native.Topmost,shot.Bounds.X,shot.Bounds.Y,shot.Bounds.Width,shot.Bounds.Height,0x0050);
            Loaded+=(s,e)=>{UpdateSnap(Mouse.GetPosition(surface));surface.InvalidateVisual();if(snapTargets!=null)Dispatcher.BeginInvoke(new Action(PrepareVisualTargets),System.Windows.Threading.DispatcherPriority.Background);if(!string.IsNullOrEmpty(shot.ColorWarning)){translationStatus.Text="HDR 兼容采集暂不可用，当前截图颜色可能有偏差。";translationStatus.Visibility=Visibility.Visible;Canvas.SetLeft(translationStatus,16);Canvas.SetTop(translationStatus,Math.Max(16,surface.ActualHeight-48));}};
            MouseEnter+=(s,e)=>{if(AppState.CanFocusCaptureFromPointer(this))FocusCaptureOverlay();};
            surface.MouseLeftButtonDown+=Down;surface.MouseMove+=Move;surface.MouseLeftButtonUp+=Up;surface.MouseWheel+=Wheel;
            surface.LostMouseCapture+=(s,e)=>{if(dragging)CancelCaptureStep();};
            surface.MouseLeave+=(s,e)=>{if(!dragging){surface.BrushPoint=null;surface.ShowBrushSize=false;ClearSnap();surface.InvalidateVisual();}};
            strokeSaveTimer.Tick+=(s,e)=>FlushStrokeSettings();
            cursorLabelTimer.Tick+=(s,e)=>{cursorLabelTimer.Stop();surface.ShowBrushSize=false;surface.InvalidateVisual();};
            surface.MouseRightButtonDown+=(s,e)=>{CancelFromPointer();e.Handled=true;};
            PreviewKeyDown+=KeyDownHandler;
            PreviewKeyUp+=(s,e)=>{if(e.Key==Key.LeftCtrl||e.Key==Key.RightCtrl)UpdateSnap(Mouse.GetPosition(surface));};
            SizeChanged+=(s,e)=>{PositionTools();surface.InvalidateVisual();};
            if(captureFeatures.Translation)AppState.TranslationProviderChanged+=RefreshProvider;
            Closed+=(s,e)=>{closed=true;FlushStrokeSettings();DisposeCaptureOutput();DisposeScrollingCapture();if(visualTargetCancel!=null)visualTargetCancel.Cancel();snapTargets=null;visualTargets=null;ClearSnap();if(targetMenu!=null)targetMenu.ContextMenu.IsOpen=false;AppState.TranslationProviderChanged-=RefreshProvider;ResetTranslation();cursorLabelTimer.Stop();ClearTextState();surface.Image=null;surface.Marks.Clear();surface.Draft=null;redo.Clear();last=null;shot.Image=null;AppState.CaptureClosed(this);};
        }
        async void PrepareVisualTargets(){
            if(!captureFeatures.SmartSelection||closed||visualTargetCancel!=null||visualTargets!=null||shot.Image==null)return;
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
            if(!captureFeatures.SmartSelection||dragging||tool!="select"||!surface.Selection.IsEmpty||snapTargets==null||(Keyboard.Modifiers&ModifierKeys.Control)!=0||surface.ActualWidth<=0||surface.ActualHeight<=0){ClearSnap();return;}
            var screen=new Rect(shot.Bounds.X,shot.Bounds.Y,shot.Bounds.Width,shot.Bounds.Height);
            var physical=new Point(screen.X+point.X*screen.Width/surface.ActualWidth,screen.Y+point.Y*screen.Height/surface.ActualHeight);
            Rect visible;var choices=snapTargets.At(physical,screen,visualTargets,out visible);
            var candidate=snapHover.Update(choices,visible,physical,screen.Width/surface.ActualWidth,screen.Height/surface.ActualHeight);
            ApplySnap(candidate);
        }
        void ApplySnap(Rect physical){
            if(!captureFeatures.SmartSelection){ClearSnap();return;}
            snap.Candidate=CaptureTargets.ToLocal(physical,shot.Bounds,surface.ActualWidth,surface.ActualHeight);
            if(surface.HoverSelection!=snap.Candidate){surface.HoverSelection=snap.Candidate;surface.InvalidateVisual();}
        }
        void AddTool(string id,string title) {if(!captureFeatures.AllowsTool(id))return;var b=Ui.Tool(id=="arrow"?CurrentLineMode:id,title+(id=="pen"?" · 连续画图，右键 / V 调整旧标注":" · 点旧标注直接调整，Alt+拖动叠画"),()=>SetTool(id=="arrow"?lineKind:id));b.Name="CaptureTool_"+id;tools[id]=b;toolbar.Children.Add(b);}
        internal string SelectedTool {get{return tool;}}
        void SetTool(string name) {
            if(!CanEditCapture||!captureFeatures.AllowsTool(name))return;
            ResetStrokeWheel();FinishShapeGesture();FinishTextGesture();CommitText();tool=name;last=null;if(name!="select"&&(name!="text"||surface.SelectedAnnotation!=null))SelectText(null);else last=SelectedAnnotation;surface.ShowHandles=true;Cursor=Cursors.Arrow;UpdateBrushCursor(Mouse.GetPosition(surface));
            bool linear=name=="arrow"||name=="line";if(linear){lineKind=name;tools["arrow"].Content=new ToolIcon(CurrentLineMode);tools["arrow"].ToolTip=(name=="line"?"直线 · L":CaptureLineTitle(CurrentLineMode)+" · A")+" · 下方切换 · Ctrl+滚轮调粗细 · 点旧标注直接调整，Alt+拖动叠画";System.Windows.Automation.AutomationProperties.SetName(tools["arrow"],tools["arrow"].ToolTip.ToString());}
            lineModes.Visibility=linear?Visibility.Visible:Visibility.Collapsed;
            foreach(var kv in lineModeButtons)Ui.SetActive(kv.Value,kv.Key==CurrentLineMode);
            foreach(var kv in tools)Ui.SetActive(kv.Value,kv.Key==(linear?"arrow":name));PositionTools();surface.InvalidateVisual();
        }
        int HitSelectionHandle(Point point){
            if(surface.Selection.IsEmpty)return -1;
            var area=surface.Selection;
            // The complete border is adjustable in every annotation tool, not
            // just its eight painted dots. Keep a narrow interior hit band so
            // drawing near an edge remains possible, even for a tiny crop.
            double corner=Math.Min(8,Math.Min(area.Width,area.Height)/4),edge=Math.Min(5,Math.Min(area.Width,area.Height)/4);
            var points=surface.Handles();int handle=-1;double closest=corner*corner;
            for(int i=0;i<points.Length;i+=2){double distance=(point-points[i]).LengthSquared;if(distance<=closest){closest=distance;handle=i;}}
            if(handle>=0)return handle;
            if(point.X<area.Left-edge||point.X>area.Right+edge||point.Y<area.Top-edge||point.Y>area.Bottom+edge)return -1;
            double left=Math.Abs(point.X-area.Left),right=Math.Abs(point.X-area.Right),top=Math.Abs(point.Y-area.Top),bottom=Math.Abs(point.Y-area.Bottom);
            double nearest=Math.Min(Math.Min(left,right),Math.Min(top,bottom));if(nearest>edge)return -1;
            return nearest==top?1:nearest==right?3:nearest==bottom?5:7;
        }
        static Cursor ResizeCursor(int handle){
            if(handle==0||handle==4)return Cursors.SizeNWSE;
            if(handle==2||handle==6)return Cursors.SizeNESW;
            return handle==1||handle==5?Cursors.SizeNS:Cursors.SizeWE;
        }
        void UpdateBrushCursor(Point point){
            int cropHandle=ActiveSelectionHandle(point);
            if(cropHandle>=0&&textDrag==null&&shapeDrag==null){surface.Cursor=ResizeCursor(cropHandle);surface.BrushPoint=null;surface.InvalidateVisual();return;}
            bool drawOver=tool=="pen"||(dragging?pointerForceNew:(Keyboard.Modifiers&ModifierKeys.Alt)!=0&&tool!="select");
            Cursor textCursor=!drawOver&&(!dragging||textDrag!=null)?TextPointerCursor(point):null;
            if(!drawOver&&textCursor==null&&(!dragging||shapeDrag!=null))textCursor=ShapePointerCursor(point);
            if(textCursor!=null){surface.Cursor=textCursor;surface.BrushPoint=null;surface.InvalidateVisual();return;}
            bool drawing=tool!="select"&&tool!="text"&&!surface.Selection.IsEmpty&&surface.Selection.Contains(point);
            if(tool=="select"){
                int handle=ActiveSelectionHandle(point);
                bool moveCursor=dragging?moving:!surface.Selection.IsEmpty&&surface.Selection.Contains(point);
                surface.Cursor=handle>=0?ResizeCursor(handle):moveCursor?Cursors.SizeAll:Cursors.Cross;
            }else surface.Cursor=drawing?Cursors.None:tool=="text"?Cursors.IBeam:Cursors.Cross;
            surface.BrushPoint=drawing?(Point?)point:null;surface.BrushDiameter=CurrentCaptureSize;surface.InvalidateVisual();
        }
        Point ClampPoint(Point p) {var bounds=surface.Selection.IsEmpty?new Rect(0,0,surface.ActualWidth,surface.ActualHeight):surface.Selection;return new Point(Geometry.Clamp(p.X,bounds.Left,bounds.Right),Geometry.Clamp(p.Y,bounds.Top,bounds.Bottom));}
        void Down(object s,MouseButtonEventArgs e) {if(BeginPointer(e.GetPosition(surface),e.ClickCount,Keyboard.Modifiers))e.Handled=true;}
        internal bool BeginPointer(Point p,int clickCount=1,ModifierKeys modifiers=ModifierKeys.None) {
            if(!CanEditCapture||!AppState.CanEnterCapture(this))return false;
            ResetStrokeWheel();CommitText();
            down=p;oldSelection=surface.Selection;moving=false;resize=-1;selectingNew=false;pointerForceNew=tool=="pen"||(modifiers&ModifierKeys.Alt)!=0&&tool!="select";
            // Crop edges always win over annotation hit targets. The current
            // drawing tool remains selected after the resize, ready to resume.
            resize=HitSelectionHandle(p);
            if(resize>=0){
                ResetTranslation();selectionBefore=CaptureScene();SelectText(null);
                dragging=true;UpdateBrushCursor(p);HideTools();surface.CaptureMouse();return true;
            }
            if(!pointerForceNew&&BeginTextPointer(p,clickCount))return true;
            if(!pointerForceNew&&BeginShapePointer(p))return true;
            if(clickCount==2&&CanCompleteCaptureAt(p)){Copy();return true;}
            SelectText(null);
            if(tool=="select") {
                ResetTranslation();
                selectionBefore=CaptureScene();
                if(!surface.Selection.IsEmpty){resize=HitSelectionHandle(p);moving=resize<0&&surface.Selection.Contains(p);}
                if(resize<0&&!moving){
                    if(surface.Selection.IsEmpty)UpdateSnap(p);else ClearSnap();
                    snap.Begin(p,(modifiers&ModifierKeys.Control)!=0);snapHover.Clear();selectingNew=true;
                    surface.HoverSelection=Rect.Empty;surface.Selection=Rect.Empty;surface.Marks.Clear();last=null;
                }
            } else {
                if(surface.Selection.IsEmpty||!surface.Selection.Contains(p))return false;
                if(tool=="text"){StartText(p);return true;}
                surface.Draft=new Annotation {Kind=tool,ArrowStyle=arrowStyle,Start=p,End=p,Width=AppState.Settings.Stroke,Color=AppState.Settings.Color};surface.Draft.Points.Add(p);
            }
            dragging=true;UpdateBrushCursor(p);HideTools();surface.CaptureMouse();return true;
        }
        void Move(object s,MouseEventArgs e) {ContinuePointer(e.GetPosition(surface));}
        internal void ContinuePointer(Point p) {
            if(!CanEditCapture)return;
            UpdateBrushCursor(p);
            if(textDrag!=null){MoveSelectedText(p);surface.InvalidateVisual();return;}
            if(shapeDrag!=null){MoveSelectedShape(p);return;}
            if(!dragging){UpdateSnap(p);return;}
            if(selectionBefore!=null) {
                var pointer=p;
                p=new Point(Geometry.Clamp(p.X,0,surface.ActualWidth),Geometry.Clamp(p.Y,0,surface.ActualHeight));
                if(moving){double dx=Geometry.Clamp(p.X-down.X,-oldSelection.Left,surface.ActualWidth-oldSelection.Right),dy=Geometry.Clamp(p.Y-down.Y,-oldSelection.Top,surface.ActualHeight-oldSelection.Bottom);surface.Selection=new Rect(oldSelection.X+dx,oldSelection.Y+dy,oldSelection.Width,oldSelection.Height);}
                else if(resize>=0) {
                    double l=oldSelection.Left,r=oldSelection.Right,t=oldSelection.Top,b=oldSelection.Bottom;
                    // The hit band extends either side of the painted edge.
                    // Apply displacement, not its absolute pointer coordinate,
                    // so a press/release within that band never jumps the crop.
                    double dx=pointer.X-down.X,dy=pointer.Y-down.Y;
                    if(resize==0||resize==6||resize==7)l=Geometry.Clamp(l+dx,0,surface.ActualWidth);if(resize==2||resize==3||resize==4)r=Geometry.Clamp(r+dx,0,surface.ActualWidth);
                    if(resize==0||resize==1||resize==2)t=Geometry.Clamp(t+dy,0,surface.ActualHeight);if(resize==4||resize==5||resize==6)b=Geometry.Clamp(b+dy,0,surface.ActualHeight);
                    surface.Selection=Geometry.FromPoints(new Point(l,t),new Point(r,b));
                } else if(snap.Move(p))surface.Selection=Geometry.FromPoints(down,p);
            } else if(surface.Draft!=null) {p=ClampPoint(p);surface.Draft.End=p;if(tool=="pen")surface.Draft.Points.Add(p);}
            UpdateBrushCursor(p);surface.InvalidateVisual();
        }
        void Up(object s,MouseButtonEventArgs e) {EndPointer(e.GetPosition(surface));}
        internal void EndPointer(Point p) {
            if(!CanEditCapture)return;
            if(!dragging)return;ContinuePointer(p);if(textDrag!=null){FinishTextGesture();return;}if(shapeDrag!=null){FinishShapeGesture();return;}dragging=false;surface.ReleaseMouseCapture();
            if(surface.Draft!=null){if(HasDrawingContent(surface.Draft)){if(tool=="mosaic")surface.PrepareMosaic(surface.Draft);surface.Marks.Add(surface.Draft);last=surface.Draft;RecordAnnotationAdded(last);if(tool!="pen")SelectShape(surface.Draft);}surface.Draft=null;}
            if(selectionBefore!=null)FinishSelectionGesture();
            if(surface.Selection.IsEmpty)UpdateSnap(p);else ClearSnap();UpdateBrushCursor(p);PositionTools();surface.InvalidateVisual();
        }
        void Wheel(object s,MouseWheelEventArgs e) {
            e.Handled=HandleCaptureWheel(e.Delta,Keyboard.Modifiers,e.GetPosition(surface));
        }
        internal bool HandleCaptureWheel(int delta,ModifierKeys modifiers,Point point){
            if(!CanEditCapture){ResetStrokeWheel();return true;}
            if(delta==0)return false;
            if(modifiers==ModifierKeys.None&&!dragging&&surface.Selection.IsEmpty&&tool=="select"&&snapTargets!=null){
                CycleSnap(point,delta<0?1:-1);return true;
            }
            // Plain scrolling must never mutate an image. The explicit controls
            // and Control-wheel share exactly the same selected/default target.
            return AdjustStrokeWheel(delta,modifiers,point);
        }
        internal void CycleSnap(Point point,int direction){
            if(!captureFeatures.SmartSelection){ClearSnap();return;}
            UpdateSnap(point);
            if(!snap.Candidate.IsEmpty&&snapHover.Choices.Count>1)ApplySnap(snapHover.Cycle(direction));
        }
        void UpdateWidth(){UpdateCapturePropertyPresentation();}
        void HideTools(){toolbarCard.Visibility=Visibility.Collapsed;propertiesCard.Visibility=Visibility.Collapsed;}
        void PositionTools(){AppState.SyncCaptureSelection(this);PositionAdaptiveTools();}
        void Undo(){if(!CanEditCapture)return;CommitText();UndoAnnotation();}
        void Redo(){if(CanEditCapture)RedoAnnotation();}
        void KeyDownHandler(object s,KeyEventArgs e){
            if(HandleCaptureKey(e.Key,Keyboard.Modifiers))e.Handled=true;
        }
        void Guard(Action f){if(!CanEditCapture)return;try{CommitText();f();}catch(Exception ex){if(!closed)TranslationStatus("未完成，截图已保留："+ex.Message);}}
        string imageTranslationNotice;
        void ResetTranslation(){if(translationCancel!=null){translationCancel.Cancel();translationCancel=null;}imageTranslationResult=null;imageTranslationPage=null;imageTranslationSource=null;imageTranslationProvider=null;imageTranslationTarget=null;imageTranslationNotice=null;surface.ClearTranslation();translationStatus.Text="";translationStatus.Visibility=Visibility.Collapsed;if(translateButton!=null){translateButton.IsEnabled=true;Ui.SetActive(translateButton,false);}}
        void RefreshProvider(){if(!CanEditCapture)return;ResetTranslation();surface.InvalidateVisual();}
        void TranslationStatus(string text){translationStatus.Text=OcrService.WithNotice(text,imageTranslationNotice);translationStatus.Visibility=string.IsNullOrEmpty(translationStatus.Text)?Visibility.Collapsed:Visibility.Visible;PositionTools();}
        void ImageTranslationStatus(){
            if(imageTranslationResult==null||imageTranslationResult.UnplacedCount==0){TranslationStatus(null);return;}
            translationStatus.Inlines.Clear();translationStatus.Inlines.Add(new Run("有 "+imageTranslationResult.UnplacedCount+" 处保留原文 · "));
            var full=new Hyperlink(new Run("查看完整译文")) {Cursor=Cursors.Hand,Foreground=Ui.Tone("Accent")};if(Application.Current!=null&&Application.Current.TryFindResource("Accent")!=null)full.SetResourceReference(TextElement.ForegroundProperty,"Accent");
            full.Click+=(s,e)=>{e.Handled=true;ViewFullTranslation();};translationStatus.Inlines.Add(full);if(!string.IsNullOrEmpty(imageTranslationNotice))translationStatus.Inlines.Add(new Run(Environment.NewLine+imageTranslationNotice));translationStatus.Visibility=Visibility.Visible;PositionTools();
        }
        void ViewFullTranslation(){
            if(!CanEditCapture||!captureFeatures.Translation)return;
            if(imageTranslationResult==null||imageTranslationPage==null||imageTranslationSource==null||imageTranslationTarget!=translationTarget||imageTranslationProvider!=Translation.Provider)return;
            // The reader is an auxiliary view; keep the editable capture alive.
            var source=imageTranslationSource;var page=imageTranslationPage;var translated=imageTranslationResult;string target=imageTranslationTarget,provider=imageTranslationProvider;
            Guard(()=>OpenCaptureTextViewer(new TextWindow(source,target,page,translated,provider,allowTranslation:captureFeatures.Translation,allowPin:captureFeatures.Pin)));
        }
        async void TranslateInPlace(){
            if(!CanEditCapture||!captureFeatures.Translation||translationCancel!=null)return;
            if(surface.TranslationImage!=null){surface.ShowTranslation=!surface.ShowTranslation;Ui.SetActive(translateButton,surface.ShowTranslation);surface.InvalidateVisual();ImageTranslationStatus();return;}
            var cancel=new CancellationTokenSource();string target=translationTarget,provider=Translation.Provider;translationCancel=cancel;translateButton.IsEnabled=false;
            try{
                CommitText();var selected=surface.Selection;var crop=surface.Export(false,false);imageTranslationNotice=null;TranslationStatus("正在本机识别文字…");
                var page=await ReadCaptureOcr(crop,cancel.Token);cancel.Token.ThrowIfCancellationRequested();imageTranslationNotice=page.Notice;var regions=ImageTranslation.Regions(page);
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
