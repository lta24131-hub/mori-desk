using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace QingJie {
    public sealed class CaptureWindow : Window {
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
        readonly SnapSelection snap=new SnapSelection();
        readonly CaptureSnapHover snapHover=new CaptureSnapHover();
        bool selectingNew;
        Annotation last;
        TextBox textEditor;
        Button translateButton;
        string translationTarget="zh";
        readonly TextBlock translationStatus=new TextBlock {Foreground=Brushes.White,Background=Ui.Brush("#E025302B"),Padding=new Thickness(9,5,9,5),MaxWidth=420,TextWrapping=TextWrapping.Wrap,Visibility=Visibility.Collapsed};
        CancellationTokenSource translationCancel;
        bool closed,collecting;
        readonly System.Windows.Threading.DispatcherTimer cursorLabelTimer=new System.Windows.Threading.DispatcherTimer {Interval=TimeSpan.FromMilliseconds(850)};
        public CaptureWindow(Snapshot snapshot) : this(snapshot,null) {}
        internal CaptureWindow(Snapshot snapshot,CaptureTargets targets) {
            snapTargets=targets;
            if(targets!=null)visualTargets=CaptureVisualRegions.Read(snapshot.Image,snapshot.Bounds);
            shot=snapshot;surface.Image=shot.Image;Title="Nook · 截图";WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;Topmost=true;Background=Brushes.Black;Cursor=Cursors.Cross;
            FontFamily=new FontFamily("Microsoft YaHei UI");UseLayoutRounding=true;
            root.Children.Add(surface);root.Children.Add(overlay);Content=root;
            toolbarCard=Ui.Card(toolbar);propertiesCard=Ui.Card(properties);overlay.Children.Add(toolbarCard);overlay.Children.Add(propertiesCard);HideTools();
            toolbarCard.Cursor=Cursors.Arrow;propertiesCard.Cursor=Cursors.Arrow;
            Ui.Theme(widthLabel,TextBlock.ForegroundProperty,"Text");Ui.Theme(translationStatus,TextBlock.ForegroundProperty,"Text");Ui.Theme(translationStatus,TextBlock.BackgroundProperty,"Surface");
            AddTool("select","调整选区");AddTool("rect","矩形 · 空心 · 滚轮调粗细");AddTool("ellipse","椭圆 · 空心");AddTool("arrow","箭头 / 直线 · 在下方切换");AddTool("pen","画笔");AddTool("text","文字标注");AddTool("mosaic","马赛克");
            Ui.Separator(toolbar);toolbar.Children.Add(Ui.Tool("undo","撤销 Ctrl+Z",Undo));
            toolbar.Children.Add(Ui.Tool("ocr","文字选择 / 翻译",()=>ReadText(false)));translateButton=Ui.Tool("translate","选区原位翻译 / 原图",()=>ReadText(true));toolbar.Children.Add(translateButton);
            var targetMenu=Ui.TranslationTarget(translationTarget,language=>{translationTarget=language;ResetTranslation();surface.InvalidateVisual();});toolbar.Children.Add(targetMenu);
            targetMenu.ContextMenu.PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape){e.Handled=true;targetMenu.ContextMenu.IsOpen=false;AppState.CancelCapture();}};
            toolbar.Children.Add(Ui.Tool("pin","贴到屏幕",Pin));toolbar.Children.Add(Ui.Tool("save","保存 Ctrl+S",Save));
            if(MoriDesk.DeskRuntime.CollectImage!=null){
                var collect=Ui.Tool("library","收藏到素材库 · 未分类 · Ctrl+D",async ()=>{await CollectToLibraryAsync();});
                collect.Name="CaptureCollect";toolbar.Children.Add(collect);
            }
            overlay.Children.Add(translationStatus);
            Ui.Separator(toolbar);toolbar.Children.Add(Ui.Tool("close","取消 Esc",AppState.CancelCapture));toolbar.Children.Add(Ui.Tool("done","复制并完成 Enter",Copy));
            properties.Children.Add(lineModes);
            foreach(string kind in new[]{"arrow","line"}){string choice=kind;var b=Ui.Tool(kind,kind=="arrow"?"箭头":"直线",()=>SetTool(choice),28);b.Name="LineMode_"+kind;lineModeButtons[kind]=b;lineModes.Children.Add(b);}Ui.Separator(lineModes);
            foreach(string color in new[]{"#EF4444","#F2B01E","#07A56B","#3478F6","#222222","#FFFFFF"}) {
                string chosen=color;var b=new Button {Width=18,Height=18,Margin=new Thickness(4),Background=Ui.Brush(color),BorderBrush=Ui.Brush("#B9C0C0"),BorderThickness=new Thickness(1),ToolTip="标注颜色",Focusable=false};
                Ui.Theme(b,Control.BorderBrushProperty,"Border");
                b.Click+=(s,e)=>{AppState.Settings.Color=chosen;AppState.Settings.Save();if(last!=null)last.Color=chosen;surface.InvalidateVisual();};properties.Children.Add(b);
            }
            properties.Children.Add(widthLabel);var strokeHint=new TextBlock {Text="滚轮调粗细",FontSize=11,Margin=new Thickness(4,0,7,0),VerticalAlignment=VerticalAlignment.Center};Ui.Theme(strokeHint,TextBlock.ForegroundProperty,"Muted");properties.Children.Add(strokeHint);UpdateWidth();
            SourceInitialized+=(s,e)=>Native.SetWindowPos(new WindowInteropHelper(this).Handle,Native.Topmost,shot.Bounds.X,shot.Bounds.Y,shot.Bounds.Width,shot.Bounds.Height,0x0040);
            Loaded+=(s,e)=>{Activate();Focus();UpdateSnap(Mouse.GetPosition(surface));surface.InvalidateVisual();if(!string.IsNullOrEmpty(shot.ColorWarning)){translationStatus.Text="HDR 兼容采集暂不可用，当前截图颜色可能有偏差。";translationStatus.Visibility=Visibility.Visible;Canvas.SetLeft(translationStatus,16);Canvas.SetTop(translationStatus,Math.Max(16,surface.ActualHeight-48));}};
            surface.MouseLeftButtonDown+=Down;surface.MouseMove+=Move;surface.MouseLeftButtonUp+=Up;surface.MouseWheel+=Wheel;
            surface.MouseLeave+=(s,e)=>{if(!dragging){surface.BrushPoint=null;ClearSnap();surface.InvalidateVisual();}};
            cursorLabelTimer.Tick+=(s,e)=>{cursorLabelTimer.Stop();surface.ShowBrushSize=false;surface.InvalidateVisual();};
            surface.MouseRightButtonDown+=(s,e)=>{if(tool!="select")SetTool("select");else AppState.CancelCapture();};
            PreviewKeyDown+=KeyDownHandler;
            PreviewKeyUp+=(s,e)=>{if(e.Key==Key.LeftCtrl||e.Key==Key.RightCtrl)UpdateSnap(Mouse.GetPosition(surface));};
            SizeChanged+=(s,e)=>surface.InvalidateVisual();
            AppState.TranslationProviderChanged+=RefreshProvider;
            Closed+=(s,e)=>{closed=true;snapTargets=null;visualTargets=null;ClearSnap();targetMenu.ContextMenu.IsOpen=false;AppState.TranslationProviderChanged-=RefreshProvider;ResetTranslation();cursorLabelTimer.Stop();AppState.Captures.Remove(this);surface.Image=null;surface.Marks.Clear();surface.Draft=null;redo.Clear();last=null;shot.Image=null;};
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
            CommitText();tool=name;last=null;surface.ShowHandles=name=="select";Cursor=Cursors.Arrow;UpdateBrushCursor(Mouse.GetPosition(surface));
            bool linear=name=="arrow"||name=="line";if(linear){lineKind=name;tools["arrow"].Content=new ToolIcon(name);tools["arrow"].ToolTip=(name=="line"?"直线":"箭头")+" · 在下方切换";System.Windows.Automation.AutomationProperties.SetName(tools["arrow"],tools["arrow"].ToolTip.ToString());}
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
            bool drawing=tool!="select"&&tool!="text"&&!surface.Selection.IsEmpty&&surface.Selection.Contains(point);
            if(tool=="select"){
                int handle=dragging?resize:HitSelectionHandle(point);
                bool moveCursor=dragging?moving:!surface.Selection.IsEmpty&&surface.Selection.Contains(point);
                surface.Cursor=handle>=0?ResizeCursor(handle):moveCursor?Cursors.SizeAll:Cursors.Cross;
            }else surface.Cursor=drawing?Cursors.None:tool=="text"?Cursors.IBeam:Cursors.Cross;
            surface.BrushPoint=drawing?(Point?)point:null;surface.BrushDiameter=AppState.Settings.Stroke;surface.InvalidateVisual();
        }
        Point ClampPoint(Point p) {var bounds=surface.Selection.IsEmpty?new Rect(0,0,surface.ActualWidth,surface.ActualHeight):surface.Selection;return new Point(Geometry.Clamp(p.X,bounds.Left,bounds.Right),Geometry.Clamp(p.Y,bounds.Top,bounds.Bottom));}
        void Down(object s,MouseButtonEventArgs e) {
            CommitText();var p=e.GetPosition(surface);
            if(e.ClickCount==2&&!surface.Selection.IsEmpty&&tool=="select"){Copy();return;}
            AppState.ChooseCapture(this);down=p;oldSelection=surface.Selection;moving=false;resize=-1;selectingNew=false;
            if(tool=="select") {
                ResetTranslation();
                if(!surface.Selection.IsEmpty){resize=HitSelectionHandle(p);moving=resize<0&&surface.Selection.Contains(p);}
                if(resize<0&&!moving){
                    if(surface.Selection.IsEmpty)UpdateSnap(p);else ClearSnap();
                    snap.Begin(p,(Keyboard.Modifiers&ModifierKeys.Control)!=0);snapHover.Clear();selectingNew=true;
                    surface.HoverSelection=Rect.Empty;surface.Selection=Rect.Empty;surface.Marks.Clear();redo.Clear();
                }
            } else {
                if(surface.Selection.IsEmpty||!surface.Selection.Contains(p))return;
                if(tool=="text"){StartText(p);return;}
                surface.Draft=new Annotation {Kind=tool,Start=p,End=p,Width=AppState.Settings.Stroke,Color=AppState.Settings.Color};surface.Draft.Points.Add(p);
            }
            dragging=true;UpdateBrushCursor(p);HideTools();surface.CaptureMouse();e.Handled=true;
        }
        void Move(object s,MouseEventArgs e) {
            UpdateBrushCursor(e.GetPosition(surface));
            if(!dragging){UpdateSnap(e.GetPosition(surface));return;}var p=e.GetPosition(surface);
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
            surface.InvalidateVisual();
        }
        void Up(object s,MouseButtonEventArgs e) {
            if(!dragging)return;Move(s,e);dragging=false;surface.ReleaseMouseCapture();
            if(surface.Draft!=null){if((surface.Draft.End-surface.Draft.Start).Length>=2){if(tool=="mosaic")surface.PrepareMosaic(surface.Draft);surface.Marks.Add(surface.Draft);last=surface.Draft;redo.Clear();}surface.Draft=null;}
            if(selectingNew){surface.Selection=snap.End(surface.Selection);selectingNew=false;}
            if(surface.Selection.Width<3||surface.Selection.Height<3)surface.Selection=Rect.Empty;
            if(surface.Selection.IsEmpty)UpdateSnap(e.GetPosition(surface));else ClearSnap();UpdateBrushCursor(e.GetPosition(surface));PositionTools();surface.InvalidateVisual();
        }
        void Wheel(object s,MouseWheelEventArgs e) {
            if(!dragging&&surface.Selection.IsEmpty&&tool=="select"&&snapTargets!=null){
                CycleSnap(e.GetPosition(surface),e.Delta<0?1:-1);e.Handled=true;return;
            }
            if(surface.Selection.IsEmpty||tool=="select")return;
            AppState.Settings.Stroke=Geometry.Clamp(AppState.Settings.Stroke+(e.Delta>0?1:-1),1,25);AppState.Settings.Save();UpdateWidth();
            if(surface.Draft!=null)surface.Draft.Width=AppState.Settings.Stroke;else if(last!=null)last.Width=AppState.Settings.Stroke;
            surface.ShowBrushSize=true;cursorLabelTimer.Stop();cursorLabelTimer.Start();UpdateBrushCursor(e.GetPosition(surface));
            surface.InvalidateVisual();e.Handled=true;
        }
        internal void CycleSnap(Point point,int direction){
            UpdateSnap(point);
            if(!snap.Candidate.IsEmpty&&snapHover.Choices.Count>1)ApplySnap(snapHover.Cycle(direction));
        }
        void UpdateWidth(){widthLabel.Text=AppState.Settings.Stroke+" px";}
        void HideTools(){toolbarCard.Visibility=Visibility.Collapsed;propertiesCard.Visibility=Visibility.Collapsed;}
        void PositionTools() {
            if(surface.Selection.IsEmpty){HideTools();return;}
            toolbarCard.Visibility=Visibility.Visible;toolbarCard.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));var r=surface.Selection;
            double w=toolbarCard.DesiredSize.Width,h=toolbarCard.DesiredSize.Height;
            double x=Geometry.Clamp(r.Right-w,8,Math.Max(8,ActualWidth-w-8)),y=r.Bottom+9;
            if(y+h+45>ActualHeight)y=r.Top-h-9;
            if(y<8)y=Geometry.Clamp(r.Bottom-h-9,8,Math.Max(8,ActualHeight-h-55));
            Canvas.SetLeft(toolbarCard,x);Canvas.SetTop(toolbarCard,y);
            Canvas.SetLeft(translationStatus,x);Canvas.SetTop(translationStatus,Math.Max(0,y-38));
            propertiesCard.Visibility=tool=="select"?Visibility.Collapsed:Visibility.Visible;
            Canvas.SetLeft(propertiesCard,x);Canvas.SetTop(propertiesCard,y+h+5);
        }
        void StartText(Point p){textEditor=new TextBox{MinWidth=140,MaxWidth=Math.Max(140,surface.Selection.Right-p.X),FontSize=Math.Max(16,AppState.Settings.Stroke*5),Foreground=Ui.Brush(AppState.Settings.Color),Padding=new Thickness(3),AcceptsReturn=true};Ui.Theme(textEditor,Control.BackgroundProperty,"Surface");Ui.Theme(textEditor,Control.BorderBrushProperty,"Primary");Canvas.SetLeft(textEditor,p.X);Canvas.SetTop(textEditor,p.Y);overlay.Children.Add(textEditor);textEditor.Focus();}
        void CommitText(){if(textEditor==null)return;if(!string.IsNullOrWhiteSpace(textEditor.Text)){var a=new Annotation{Kind="text",Text=textEditor.Text,Start=new Point(Canvas.GetLeft(textEditor),Canvas.GetTop(textEditor)),Color=AppState.Settings.Color,Width=AppState.Settings.Stroke};surface.Marks.Add(a);last=a;redo.Clear();}overlay.Children.Remove(textEditor);textEditor=null;surface.InvalidateVisual();}
        void Undo(){CommitText();if(surface.Marks.Count>0){redo.Push(surface.Marks[surface.Marks.Count-1]);surface.Marks.RemoveAt(surface.Marks.Count-1);}last=null;surface.InvalidateVisual();}
        void Redo(){if(redo.Count>0)surface.Marks.Add(redo.Pop());surface.InvalidateVisual();}
        void KeyDownHandler(object s,KeyEventArgs e){
            if(collecting){e.Handled=true;return;}
            if(e.Key==Key.Escape){e.Handled=true;AppState.CancelCapture();return;}
            if(e.Key==Key.Tab&&!dragging&&surface.Selection.IsEmpty&&tool=="select"&&snapTargets!=null){CycleSnap(Mouse.GetPosition(surface),(Keyboard.Modifiers&ModifierKeys.Shift)!=0?-1:1);e.Handled=true;return;}
            if(e.Key==Key.LeftCtrl||e.Key==Key.RightCtrl)UpdateSnap(Mouse.GetPosition(surface));
            if(e.Key==Key.D&&Keyboard.Modifiers==ModifierKeys.Control&&MoriDesk.DeskRuntime.CollectImage!=null){e.Handled=true;CollectToLibrary();return;}
            if(textEditor!=null){if(e.Key==Key.Enter&&(Keyboard.Modifiers&ModifierKeys.Control)!=0){CommitText();e.Handled=true;}return;}
            if(e.Key==Key.Enter){Copy();e.Handled=true;}else if((Keyboard.Modifiers&ModifierKeys.Control)!=0){if(e.Key==Key.Z)Undo();else if(e.Key==Key.Y)Redo();else if(e.Key==Key.S)Save();else if(e.Key==Key.C)Copy();}
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
        void ResetTranslation(){if(translationCancel!=null){translationCancel.Cancel();translationCancel=null;}surface.ClearTranslation();translationStatus.Visibility=Visibility.Collapsed;if(translateButton!=null){translateButton.IsEnabled=true;Ui.SetActive(translateButton,false);}}
        void RefreshProvider(){ResetTranslation();surface.InvalidateVisual();}
        void TranslationStatus(string text){translationStatus.Text=text;translationStatus.Visibility=Visibility.Visible;}
        async void TranslateInPlace(){
            if(translationCancel!=null)return;
            if(surface.TranslationImage!=null){surface.ShowTranslation=!surface.ShowTranslation;Ui.SetActive(translateButton,surface.ShowTranslation);surface.InvalidateVisual();return;}
            var cancel=new CancellationTokenSource();string target=translationTarget;translationCancel=cancel;translateButton.IsEnabled=false;
            try{
                CommitText();var selected=surface.Selection;var crop=surface.Export(false,false);TranslationStatus("正在本机识别文字…");
                var page=await OcrService.Read(crop,cancel.Token);cancel.Token.ThrowIfCancellationRequested();var regions=ImageTranslation.Regions(page);
                if(regions.Count==0){TranslationStatus("没有识别到文字，原图保留。");return;}
                TranslationStatus(Translation.ProgressText);
                var texts=await Translation.TranslateImage(regions.Select(r=>r.Text).ToArray(),cancel.Token,target);cancel.Token.ThrowIfCancellationRequested();
                if(closed||selected!=surface.Selection)return;
                surface.TranslationImage=ImageTranslation.Render(crop,regions,texts);surface.TranslationBounds=selected;surface.ShowTranslation=true;surface.InvalidateVisual();translationStatus.Visibility=Visibility.Collapsed;Ui.SetActive(translateButton,true);
            }catch(OperationCanceledException){if(!closed&&!cancel.IsCancellationRequested)TranslationStatus("翻译超时，原图保留，可点击重试。");}
            catch(Exception ex){if(!closed&&!cancel.IsCancellationRequested)TranslationStatus(ex.Message);}
            finally{if(translationCancel==cancel){translationCancel=null;if(!closed)translateButton.IsEnabled=true;}cancel.Dispose();}
        }
    }
}
