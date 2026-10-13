using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace MoriDesk {
 // Fixed-size, virtualized cards: only visible rows own controls and decoded images.
 public sealed class ReferenceTilePanel : VirtualizingPanel, IScrollInfo {
  const double CardWidth=218,CardHeight=226;
  Size extent,viewport;double offset;int columns=1;
  protected override Size MeasureOverride(Size available){
   double width=double.IsInfinity(available.Width)?CardWidth:Math.Max(1,available.Width);
   double height=double.IsInfinity(available.Height)?CardHeight*3:Math.Max(1,available.Height);
   var owner=ItemsControl.GetItemsOwner(this);int count=owner==null?0:owner.Items.Count;
   columns=Math.Max(1,(int)(width/CardWidth));viewport=new Size(width,height);extent=new Size(width,Math.Ceiling((double)count/columns)*CardHeight);
   offset=Math.Max(0,Math.Min(offset,Math.Max(0,extent.Height-height)));if(ScrollOwner!=null)ScrollOwner.InvalidateScrollInfo();
   int first=Math.Max(0,((int)(offset/CardHeight)-1)*columns),last=Math.Min(count-1,((int)((offset+height)/CardHeight)+2)*columns-1);
   // Accessing InternalChildren attaches the generator on the first layout pass.
   var children=InternalChildren;var generator=ItemContainerGenerator;if(generator==null)return viewport;
   for(int i=InternalChildren.Count-1;i>=0;i--){int index=generator.IndexFromGeneratorPosition(new GeneratorPosition(i,0));if(index<first||index>last){generator.Remove(new GeneratorPosition(i,0),1);RemoveInternalChildRange(i,1);}}
   if(count>0){var start=generator.GeneratorPositionFromIndex(first);int childIndex=start.Offset==0?start.Index:start.Index+1;
    using(generator.StartAt(start,GeneratorDirection.Forward,true)){for(int i=first;i<=last;i++,childIndex++){bool fresh;var child=(UIElement)generator.GenerateNext(out fresh);if(fresh){if(childIndex>=InternalChildren.Count)AddInternalChild(child);else InsertInternalChild(childIndex,child);generator.PrepareItemContainer(child);}child.Measure(new Size(width/columns,CardHeight));}}
   }return viewport;
  }
  protected override Size ArrangeOverride(Size size){for(int i=0;i<InternalChildren.Count;i++){int index=ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i,0));InternalChildren[i].Arrange(new Rect((index%columns)*viewport.Width/columns,(index/columns)*CardHeight-offset,viewport.Width/columns,CardHeight));}return size;}
  protected override void OnItemsChanged(object sender,ItemsChangedEventArgs args){ItemContainerGenerator.RemoveAll();if(InternalChildren.Count>0)RemoveInternalChildRange(0,InternalChildren.Count);InvalidateMeasure();}
  protected override void BringIndexIntoView(int index){double top=(index/columns)*CardHeight;if(top<offset)SetVerticalOffset(top);else if(top+CardHeight>offset+viewport.Height)SetVerticalOffset(top+CardHeight-viewport.Height);}
  public bool CanHorizontallyScroll{get;set;}public bool CanVerticallyScroll{get;set;}
  public double ExtentWidth{get{return extent.Width;}}public double ExtentHeight{get{return extent.Height;}}public double ViewportWidth{get{return viewport.Width;}}public double ViewportHeight{get{return viewport.Height;}}public double HorizontalOffset{get{return 0;}}public double VerticalOffset{get{return offset;}}public ScrollViewer ScrollOwner{get;set;}
  public void SetVerticalOffset(double value){value=Math.Max(0,Math.Min(value,Math.Max(0,extent.Height-viewport.Height)));if(Math.Abs(offset-value)<.1)return;offset=value;InvalidateMeasure();if(ScrollOwner!=null)ScrollOwner.InvalidateScrollInfo();}
  public void SetHorizontalOffset(double value){}public void LineUp(){SetVerticalOffset(offset-40);}public void LineDown(){SetVerticalOffset(offset+40);}public void LineLeft(){}public void LineRight(){}public void MouseWheelUp(){SetVerticalOffset(offset-120);}public void MouseWheelDown(){SetVerticalOffset(offset+120);}public void MouseWheelLeft(){}public void MouseWheelRight(){}public void PageUp(){SetVerticalOffset(offset-viewport.Height);}public void PageDown(){SetVerticalOffset(offset+viewport.Height);}public void PageLeft(){}public void PageRight(){}
  public Rect MakeVisible(Visual visual,Rect rect){DependencyObject current=visual;while(current!=null&&VisualTreeHelper.GetParent(current)!=this)current=VisualTreeHelper.GetParent(current);if(current!=null){int index=ItemsControl.GetItemsOwner(this).ItemContainerGenerator.IndexFromContainer(current);if(index>=0)BringIndexIntoView(index);}return rect;}
 }
 public sealed class ReferenceThumbnail : Image {
  static readonly SemaphoreSlim slots=new SemaphoreSlim(2,2);int generation;
  public ReferenceThumbnail(){Loaded+=delegate{Load();};Unloaded+=delegate{generation++;Source=null;};IsVisibleChanged+=delegate{if(!IsVisible){generation++;Source=null;}else if(IsLoaded)Load();};DataContextChanged+=delegate{generation++;Source=null;if(IsLoaded&&IsVisible)Load();};}
  internal static bool CanPreview(FileReference row){return row!=null&&!row.Directory&&new[]{".jpg",".jpeg",".png",".bmp",".gif",".tif",".tiff",".ico"}.Contains(Path.GetExtension(row.Path).ToLowerInvariant());}
  internal static BitmapSource Read(string path){using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);var frame=decoder.Frames[0];if(frame.PixelWidth<=0||frame.PixelHeight<=0||frame.PixelWidth>30000||frame.PixelHeight>30000)return null;stream.Position=0;var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=stream;if(frame.PixelWidth>=frame.PixelHeight)bitmap.DecodePixelWidth=256;else bitmap.DecodePixelHeight=256;bitmap.EndInit();bitmap.Freeze();return bitmap;}}
  async void Load(){var row=DataContext as FileReference;if(!CanPreview(row))return;int stamp=++generation;await slots.WaitAsync();try{if(stamp!=generation||!IsLoaded||!IsVisible)return;var bitmap=await Task.Run(()=>Read(row.Path));if(stamp==generation&&IsLoaded&&IsVisible)Source=bitmap;}catch(Exception ex){System.Diagnostics.Debug.WriteLine("Local thumbnail: "+ex.Message);}finally{slots.Release();}}
 }
}
