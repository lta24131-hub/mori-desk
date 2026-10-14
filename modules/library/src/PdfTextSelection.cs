using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Mori {
 // One painted element per visible page, never one WPF control per word.
 internal sealed class PdfTextSelection:FrameworkElement {
  static readonly Brush Highlight=FrozenHighlight();
  PdfTextPage page;int anchor=-1,endpoint=-1;bool dragging,enabled;
  internal event Action<PdfTextSelection,string> Changed;
  internal event Action<PdfTextSelection> Activated;
  internal string SelectedText {get{return page==null?"":page.Range(anchor,endpoint);}}
  internal PdfTextPage Page {get{return page;}}
  internal PdfTextSelection(){Focusable=true;ClipToBounds=true;IsHitTestVisible=false;System.Windows.Automation.AutomationProperties.SetName(this,"PDF 文字选择 · 拖动选择同一页文字");}
  // PDF paper stays white in both themes; use the same translucent brand ink
  // for selection, without tinting or changing the rendered page itself.
  static Brush FrozenHighlight(){var brush=new SolidColorBrush(Color.FromArgb(72,98,96,197));brush.Freeze();return brush;}
  internal void SetPage(PdfTextPage value,bool active){page=value;enabled=active;IsHitTestVisible=active;Cursor=Cursors.Arrow;Clear();}
  internal void SetActive(bool value){bool changed=enabled!=value;enabled=value;IsHitTestVisible=value;if(changed&&!value)Clear();if(changed)InvalidateVisual();}
  internal void Clear(){anchor=endpoint=-1;dragging=false;if(IsMouseCaptured)ReleaseMouseCapture();InvalidateVisual();Notify();}
  internal void Select(int first,int last){if(page==null||page.Words.Length==0)return;anchor=Math.Max(0,Math.Min(page.Words.Length-1,first));endpoint=Math.Max(0,Math.Min(page.Words.Length-1,last));InvalidateVisual();Notify();}
  internal int WordAt(Point point,bool nearest){
   if(page==null||page.Words.Length==0||ActualWidth<=0||ActualHeight<=0)return -1;
   double x=point.X/ActualWidth,y=point.Y/ActualHeight,dx=4/ActualWidth,dy=4/ActualHeight;int best=-1;double distance=double.MaxValue;
   for(int i=0;i<page.Words.Length;i++){
    var word=page.Words[i];if(x>=word.X-dx&&x<=word.X+word.Width+dx&&y>=word.Y-dy&&y<=word.Y+word.Height+dy)return i;
    if(nearest){double horizontal=Math.Max(word.X-x,Math.Max(0,x-word.X-word.Width))*ActualWidth,vertical=Math.Max(word.Y-y,Math.Max(0,y-word.Y-word.Height))*ActualHeight;double d=horizontal*horizontal+vertical*vertical*4;if(d<distance){best=i;distance=d;}}
   }return best;
  }
  void Notify(){if(Changed!=null)Changed(this,SelectedText);}
  protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e){
   base.OnMouseLeftButtonDown(e);if(!enabled||page==null)return;int hit=WordAt(e.GetPosition(this),false);
   if(Activated!=null)Activated(this);Clear();if(hit<0)return;Focus();anchor=endpoint=hit;dragging=true;CaptureMouse();Cursor=Cursors.IBeam;InvalidateVisual();Notify();e.Handled=true;
  }
  protected override void OnMouseMove(MouseEventArgs e){
   base.OnMouseMove(e);if(!enabled)return;
   if(dragging){int hit=WordAt(e.GetPosition(this),true);if(hit>=0&&hit!=endpoint){endpoint=hit;InvalidateVisual();Notify();}e.Handled=true;}
   else Cursor=WordAt(e.GetPosition(this),false)>=0?Cursors.IBeam:Cursors.Arrow;
  }
  protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e){base.OnMouseLeftButtonUp(e);if(!dragging)return;dragging=false;if(IsMouseCaptured)ReleaseMouseCapture();e.Handled=true;}
  protected override void OnLostMouseCapture(MouseEventArgs e){base.OnLostMouseCapture(e);dragging=false;}
  protected override void OnRender(DrawingContext drawing){
   base.OnRender(drawing);if(!enabled)return;drawing.DrawRectangle(Brushes.Transparent,null,new Rect(0,0,ActualWidth,ActualHeight));
   if(page==null||anchor<0||endpoint<0)return;int first=Math.Min(anchor,endpoint),last=Math.Max(anchor,endpoint);
   Rect segment=Rect.Empty;int line=-1;
   for(int i=first;i<=last;i++){
    var word=page.Words[i];var box=new Rect(word.X*ActualWidth,word.Y*ActualHeight,word.Width*ActualWidth,word.Height*ActualHeight);
    // Paint adjacent words as a line segment; a whole-page selection should
    // not issue tens of thousands of separate WPF drawing commands.
    if(!segment.IsEmpty&&line==word.Line&&box.Left>=segment.Left&&box.Left-segment.Right<=Math.Max(box.Height,segment.Height)*3)segment.Union(box);
    else{if(!segment.IsEmpty)drawing.DrawRectangle(Highlight,null,segment);segment=box;line=word.Line;}
   }if(!segment.IsEmpty)drawing.DrawRectangle(Highlight,null,segment);
  }
 }
}
