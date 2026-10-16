using System;
using System.Collections.Generic;
using System.Windows;
#if !CAPTURE_TOOLBAR_LAYOUT_ONLY
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
#endif

namespace QingJie {
    // Geometry only. Keeping this independent of Window/Application allows the
    // small-screen and edge cases to be tested without capturing the desktop.
    internal static class CaptureToolbarLayout {
        internal sealed class Flow {
            internal Rect[] Items;
            internal Rect[] Dividers;
            internal Size Size;
            internal int Rows;
        }
        internal sealed class Placement {
            internal Rect Toolbar,Properties,Status;
        }
        static double Length(double value){return double.IsNaN(value)||double.IsInfinity(value)||value<0?0:value;}
        static double Limit(double value,double minimum,double maximum){return Math.Max(minimum,Math.Min(maximum,value));}

        internal static Flow Arrange(Size[] sizes,int[] groups,double availableWidth) {
            if(sizes==null||groups==null||sizes.Length!=groups.Length)throw new ArgumentException("Toolbar items and groups must have matching lengths.");
            double width=Math.Max(1,Length(availableWidth)),x=0,y=0,rowHeight=0,usedWidth=0;
            const double groupGap=10,rowGap=4;
            var rects=new Rect[sizes.Length];for(int i=0;i<rects.Length;i++)rects[i]=Rect.Empty;
            var row=new List<int>();int rows=0;
            Action endRow=()=>{
                if(row.Count==0)return;
                foreach(int index in row){var r=rects[index];r.Y+=(rowHeight-r.Height)/2;rects[index]=r;}
                usedWidth=Math.Max(usedWidth,x);y+=rowHeight+rowGap;rows++;x=0;rowHeight=0;row.Clear();
            };
            int cursor=0;
            while(cursor<sizes.Length){
                int start=cursor,group=groups[cursor];double groupWidth=0;
                while(cursor<sizes.Length&&groups[cursor]==group){if(Length(sizes[cursor].Height)>0)groupWidth+=Math.Min(width,Length(sizes[cursor].Width));cursor++;}
                if(groupWidth<=0)continue;
                // Preserve a meaningful group whenever it can fit on a row.
                // A genuinely narrow screen can still wrap within that group.
                if(x>0&&groupWidth<=width&&x+groupGap+groupWidth>width)endRow();
                bool first=true;
                for(int i=start;i<cursor;i++){
                    double itemWidth=Math.Min(width,Length(sizes[i].Width)),itemHeight=Length(sizes[i].Height);
                    if(itemWidth<=0||itemHeight<=0)continue;
                    double gap=first&&x>0?groupGap:0;
                    if(x>0&&x+gap+itemWidth>width){endRow();gap=0;}
                    x+=gap;rects[i]=new Rect(x,y,itemWidth,itemHeight);row.Add(i);x+=itemWidth;rowHeight=Math.Max(rowHeight,itemHeight);first=false;
                }
            }
            endRow();
            var dividers=new List<Rect>();int previous=-1;
            for(int i=0;i<rects.Length;i++){
                var r=rects[i];if(r.IsEmpty)continue;
                if(previous>=0&&groups[previous]!=groups[i]){
                    var before=rects[previous];double overlapTop=Math.Max(before.Top,r.Top),overlapBottom=Math.Min(before.Bottom,r.Bottom);
                    // Dividers belong between groups on the same row only.
                    if(overlapBottom>overlapTop&&r.Left>=before.Right+groupGap-0.01){double h=Math.Min(22,overlapBottom-overlapTop);dividers.Add(new Rect((before.Right+r.Left-1)/2,overlapTop+(overlapBottom-overlapTop-h)/2,1,h));}
                }
                previous=i;
            }
            return new Flow {Items=rects,Dividers=dividers.ToArray(),Rows=rows,Size=new Size(usedWidth,rows==0?0:y-rowGap)};
        }

        internal static Placement Place(Size viewport,Rect selection,Size toolbar,Size properties,Size status) {
            double width=Math.Max(1,Length(viewport.Width)),height=Math.Max(1,Length(viewport.Height));
            double margin=Math.Min(8,Math.Min(width,height)/4),availableWidth=width-margin*2,availableHeight=height-margin*2;
            var natural=new[]{status,toolbar,properties};var measured=new Size[3];int visible=0;double totalHeight=0,blockWidth=0;
            for(int i=0;i<natural.Length;i++){
                double w=Math.Min(availableWidth,Length(natural[i].Width)),h=Length(natural[i].Height);
                if(w<=0||h<=0)continue;
                measured[i]=new Size(w,h);visible++;totalHeight+=h;blockWidth=Math.Max(blockWidth,w);
            }
            var result=new Placement {Toolbar=Rect.Empty,Properties=Rect.Empty,Status=Rect.Empty};if(visible==0)return result;
            double gap=Math.Min(5,availableHeight/(visible*2)),gaps=gap*(visible-1),budget=Math.Max(0,availableHeight-gaps);
            var heights=new double[3];double minimumTotal=0;
            for(int i=0;i<measured.Length;i++){heights[i]=Math.Min(measured[i].Height,i==0?18:32);minimumTotal+=heights[i];}
            if(totalHeight<=budget){for(int i=0;i<measured.Length;i++)heights[i]=measured[i].Height;}
            else if(minimumTotal<=budget){
                // Reserve one reachable scrolling row per action card before
                // sharing remaining height. Pure proportional scaling could
                // leave a short properties card with only 8 px of clickable UI.
                double extra=budget-minimumTotal,remaining=totalHeight-minimumTotal;
                for(int i=0;i<measured.Length;i++)heights[i]+=(measured[i].Height-heights[i])*(remaining>0?extra/remaining:0);
            }else {double scale=totalHeight>0?budget/totalHeight:0;for(int i=0;i<measured.Length;i++)heights[i]=measured[i].Height*scale;}
            double blockHeight=gaps;foreach(double allocated in heights)blockHeight+=allocated;
            double right=selection.IsEmpty?width-margin:selection.Right,top=selection.IsEmpty?margin:selection.Top,bottom=selection.IsEmpty?margin:selection.Bottom;
            if(double.IsNaN(right)||double.IsInfinity(right))right=width-margin;
            if(double.IsNaN(top)||double.IsInfinity(top))top=margin;
            if(double.IsNaN(bottom)||double.IsInfinity(bottom))bottom=margin;
            double x=Limit(right-blockWidth,margin,width-margin-blockWidth),y=bottom+9;
            if(y+blockHeight>height-margin){
                double above=top-9-blockHeight;
                if(above>=margin)y=above;
                else y=top-margin>height-margin-bottom?above:bottom+9;
            }
            y=Limit(y,margin,height-margin-blockHeight);
            for(int i=0;i<measured.Length;i++){
                var size=measured[i];if(size.Width<=0||size.Height<=0)continue;
                var rect=new Rect(x,y,size.Width,heights[i]);y+=rect.Height+gap;
                if(i==0)result.Status=rect;else if(i==1)result.Toolbar=rect;else result.Properties=rect;
            }
            return result;
        }
    }

#if !CAPTURE_TOOLBAR_LAYOUT_ONLY
    // A compact, original WPF implementation. No third-party capture source or
    // visual resources are embedded. Existing themed buttons/actions are reused.
    public sealed partial class CaptureWindow {
        CaptureToolFlow toolbarFlow,propertyFlow;
        bool toolbarLayoutReady;
        Button captureUndoButton,captureRedoButton;
        TextBlock capturePropertyHint;
        readonly List<Button> captureColorButtons=new List<Button>();

        static Button CaptureAction(string name,Button button){button.Name="Capture"+name;return button;}

        void InitializeCaptureToolbarLayout() {
            if(toolbarLayoutReady)return;toolbarLayoutReady=true;
            toolbarFlow=RegroupCaptureTools(toolbar,false);propertyFlow=RegroupCaptureTools(properties,true);
            toolbarCard.Child=null;propertiesCard.Child=null;
            toolbarCard.Child=CaptureToolScroll(toolbar);propertiesCard.Child=CaptureToolScroll(properties);
            UpdateCapturePropertyPresentation();RefreshCaptureToolbarHistory();
        }
        static ScrollViewer CaptureToolScroll(UIElement content){return new ScrollViewer {Content=content,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,PanningMode=PanningMode.VerticalOnly,Focusable=false};}
        CaptureToolFlow RegroupCaptureTools(StackPanel host,bool propertyBar) {
            var items=new List<UIElement>();foreach(UIElement child in host.Children)items.Add(child);host.Children.Clear();host.Orientation=Orientation.Vertical;
            var panel=new CaptureToolFlow();
            string[] colorNames={"红色","黄色","绿色","蓝色","黑色","白色"};
            foreach(var child in items){
                if(!propertyBar&&child is Border)continue;
                var button=child as Button;int group;
                if(propertyBar){
                    if(child==lineModes)group=0;
                    else if(button!=null){
                        group=1;int index=captureColorButtons.Count;captureColorButtons.Add(button);
                        string title="标注颜色："+(index<colorNames.Length?colorNames[index]:"自定义");
                        button.ToolTip=title;AutomationProperties.SetName(button,title);
                    }else {
                        group=2;
                        if(child is TextBlock&&child!=widthLabel){capturePropertyHint=(TextBlock)child;capturePropertyHint.Name="CapturePropertyHint";}
                    }
                }else {
                    group=CaptureActionGroup(child);
                    if(button!=null&&button.Name=="CaptureUndo"){
                        captureUndoButton=button;
                        captureRedoButton=CaptureAction("Redo",Ui.Tool("undo","重做 · Ctrl+Y",Redo));
                        var icon=(FrameworkElement)captureRedoButton.Content;icon.RenderTransformOrigin=new Point(.5,.5);icon.RenderTransform=new ScaleTransform(-1,1);
                        panel.Add(button,group);panel.Add(captureRedoButton,group);continue;
                    }
                }
                panel.Add(child,group);
            }
            host.Children.Add(panel);return panel;
        }
        int CaptureActionGroup(UIElement child){
            if(child==tools["select"])return 0;
            foreach(var entry in tools)if(child==entry.Value)return 1;
            var button=child as Button;if(button==null)return 4;
            if(button.Name=="CaptureUndo"||button.Name=="CaptureRedo")return 2;
            if(button.Name=="CaptureOcr"||button==translateButton||button.ContextMenu!=null)return 3;
            if(button.Name=="CaptureCancel"||button.Name=="CaptureCopy")return 5;
            return 4;
        }
        string CapturePropertyKind {
            get{return textEditor!=null||surface.SelectedText!=null?"text":surface.SelectedAnnotation!=null?surface.SelectedAnnotation.Kind:tool;}
        }
        bool HasCaptureProperties {get{string kind=CapturePropertyKind;return kind!="select"&&kind!="mosaic";}}
        void UpdateCapturePropertyPresentation(){
            string kind=CapturePropertyKind;bool text=kind=="text",visible=HasCaptureProperties;
            // Text Width stores one fifth of its rendered font size. Show the
            // actual text size even before the first label is created. These
            // are logical sizes; physical export pixels vary by monitor scale.
            double value=textEditor!=null?textEditor.FontSize:surface.SelectedText!=null?surface.SelectedText.TextFontSize:surface.SelectedAnnotation!=null?surface.SelectedAnnotation.Width:text?Math.Max(16,AppState.Settings.Stroke*5):AppState.Settings.Stroke;
            widthLabel.Text=(text?"字号 ":"粗细 ")+value.ToString("0");
            AutomationProperties.SetName(widthLabel,widthLabel.Text);
            widthLabel.Visibility=visible?Visibility.Visible:Visibility.Collapsed;
            foreach(var color in captureColorButtons)color.Visibility=visible?Visibility.Visible:Visibility.Collapsed;
            if(capturePropertyHint!=null){capturePropertyHint.Text=text?"滚轮调字号":"滚轮调粗细";capturePropertyHint.Visibility=visible?Visibility.Visible:Visibility.Collapsed;}
        }
        void RefreshCaptureToolbarHistory(){
            SetCaptureHistoryAvailability(captureUndoButton,annotationUndo.Count>0);
            SetCaptureHistoryAvailability(captureRedoButton,annotationRedo.Count>0);
        }
        static void SetCaptureHistoryAvailability(Button button,bool available){
            if(button==null)return;button.IsEnabled=available;button.Opacity=available?1:.35;ToolTipService.SetShowOnDisabled(button,true);
        }
        void PositionAdaptiveTools() {
            // The surface is the coordinate space of the crop and overlay. Its
            // arranged size also works before a native window exists, allowing
            // the real positioning path to be covered by desktop-free tests.
            double viewportWidth=surface.ActualWidth,viewportHeight=surface.ActualHeight;
            if(surface.Selection.IsEmpty||viewportWidth<=0||viewportHeight<=0){HideTools();return;}
            InitializeCaptureToolbarLayout();
            UpdateCapturePropertyPresentation();RefreshCaptureToolbarHistory();
            double margin=Math.Min(8,Math.Min(viewportWidth,viewportHeight)/4),maxWidth=viewportWidth-margin*2,maxHeight=viewportHeight-margin*2;
            bool hasProperties=HasCaptureProperties,hasStatus=translationStatus.Visibility==Visibility.Visible;
            toolbarCard.Visibility=Visibility.Visible;propertiesCard.Visibility=hasProperties?Visibility.Visible:Visibility.Collapsed;
            // Previous placements may have clipped a card in a short viewport.
            // Recover natural dimensions before measuring the next selection.
            toolbarCard.Width=toolbarCard.Height=propertiesCard.Width=propertiesCard.Height=double.NaN;
            translationStatus.Width=translationStatus.Height=double.NaN;
            toolbarFlow.MaxWidth=propertyFlow.MaxWidth=Math.Max(1,maxWidth-12);
            toolbarCard.MaxWidth=propertiesCard.MaxWidth=maxWidth;toolbarCard.MaxHeight=propertiesCard.MaxHeight=double.PositiveInfinity;
            toolbarCard.Measure(new Size(maxWidth,double.PositiveInfinity));
            if(hasProperties)propertiesCard.Measure(new Size(maxWidth,double.PositiveInfinity));
            translationStatus.MaxWidth=Math.Min(420,maxWidth);translationStatus.MaxHeight=double.PositiveInfinity;
            if(hasStatus)translationStatus.Measure(new Size(maxWidth,maxHeight));
            Size propertySize=hasProperties?propertiesCard.DesiredSize:new Size(),statusSize=hasStatus?translationStatus.DesiredSize:new Size();
            var placement=CaptureToolbarLayout.Place(new Size(viewportWidth,viewportHeight),surface.Selection,toolbarCard.DesiredSize,propertySize,statusSize);
            // In an unusually short viewport the controls remain available via
            // vertical scrolling, instead of drawing buttons outside the screen.
            toolbarCard.MaxHeight=placement.Toolbar.Height;
            if(hasProperties)propertiesCard.MaxHeight=placement.Properties.Height;
            if(hasStatus)translationStatus.MaxHeight=placement.Status.Height;
            toolbarCard.Measure(new Size(maxWidth,placement.Toolbar.Height));
            if(hasProperties)propertiesCard.Measure(new Size(maxWidth,placement.Properties.Height));
            if(hasStatus)translationStatus.Measure(new Size(maxWidth,placement.Status.Height));
            placement=CaptureToolbarLayout.Place(new Size(viewportWidth,viewportHeight),surface.Selection,toolbarCard.DesiredSize,hasProperties?propertiesCard.DesiredSize:new Size(),hasStatus?translationStatus.DesiredSize:new Size());
            PositionCaptureCard(toolbarCard,placement.Toolbar);
            if(hasProperties)PositionCaptureCard(propertiesCard,placement.Properties);
            if(hasStatus)PositionCaptureCard(translationStatus,placement.Status);
        }
        static void PositionCaptureCard(FrameworkElement element,Rect bounds){
            if(bounds.IsEmpty)return;
            // Canvas can arrange a TextBlock to its unclipped natural line
            // height even after a constrained Measure. Apply the final bounds
            // as dimensions so status text cannot overlap the next tool card.
            element.Width=bounds.Width;element.Height=bounds.Height;
            Canvas.SetLeft(element,bounds.Left);Canvas.SetTop(element,bounds.Top);
        }

        sealed class CaptureToolFlow : Panel {
            readonly List<int> groups=new List<int>();
            CaptureToolbarLayout.Flow flow;
            internal CaptureToolFlow(){UseLayoutRounding=true;Cursor=Cursors.Arrow;}
            internal void Add(UIElement child,int group){Children.Add(child);groups.Add(group);}
            CaptureToolbarLayout.Flow Layout(double width,bool measure){
                double available=double.IsInfinity(width)?MaxWidth:width;if(double.IsInfinity(available)||double.IsNaN(available))available=1024;available=Math.Max(1,available);
                var sizes=new Size[Children.Count];
                for(int i=0;i<Children.Count;i++){var child=Children[i];if(child.Visibility==Visibility.Collapsed)continue;if(measure)child.Measure(new Size(available,double.PositiveInfinity));sizes[i]=child.DesiredSize;}
                return CaptureToolbarLayout.Arrange(sizes,groups.ToArray(),available);
            }
            protected override Size MeasureOverride(Size availableSize){flow=Layout(availableSize.Width,true);return flow.Size;}
            protected override Size ArrangeOverride(Size finalSize){flow=Layout(finalSize.Width,false);for(int i=0;i<Children.Count;i++)Children[i].Arrange(flow.Items[i].IsEmpty?new Rect():flow.Items[i]);InvalidateVisual();return finalSize;}
            protected override void OnRender(DrawingContext drawing){base.OnRender(drawing);if(flow==null)return;foreach(var divider in flow.Dividers)drawing.DrawRectangle(Ui.Tone("Divider"),null,divider);}
        }
    }
#endif
}
