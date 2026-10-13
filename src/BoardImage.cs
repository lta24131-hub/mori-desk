using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using Mori;
namespace MoriDesk {
 public sealed class BoardImage:Image {
  static readonly SemaphoreSlim slots=new SemaphoreSlim(2,2);int version;
  protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e){base.OnPropertyChanged(e);if(e.Property==SourceProperty){var grid=Parent as Grid;if(grid!=null){var glyph=grid.Children.OfType<TextBlock>().FirstOrDefault();if(glyph!=null)glyph.Visibility=e.NewValue==null?Visibility.Visible:Visibility.Collapsed;}}}
  public BoardImage(){Loaded+=delegate{Load();};Unloaded+=delegate{version++;Source=null;};DataContextChanged+=delegate{version++;Source=null;if(IsLoaded&&IsVisible)Load();};IsVisibleChanged+=delegate{version++;Source=null;if(IsVisible&&IsLoaded)Load();};}
  internal static BitmapSource Read(BoardItem item,Library library){if(item==null||item.Directory)return null;if(item.Asset!=null&&library!=null){library.BuildThumbnail(item.Asset);string cached=library.ThumbnailPath(item.Asset);return File.Exists(cached)?ReferenceThumbnail.Read(cached):null;}if(item.Section!="pictures")return null;if(item.Kind=="SVG"){int width,height;var result=SvgRaster.Render(item.Path,256,out width,out height);if(result!=null&&result.CanFreeze)result.Freeze();return result;}return ReferenceThumbnail.Read(item.Path);}
  internal static ImageSource FolderIcon(){var geometry=Geometry.Parse("M 2,7 L 2,23 Q 2,25 4,25 L 29,25 Q 31,25 31,23 L 31,9 Q 31,7 29,7 L 15,7 L 12,3 L 4,3 Q 2,3 2,5 Z");var image=new DrawingImage(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(110,140,123)),new Pen(new SolidColorBrush(Color.FromRgb(154,174,163)),1),geometry));image.Freeze();return image;}
  async void Load(){var item=DataContext as BoardItem;if(item==null)return;if(item.Directory){Source=FolderIcon();MaxWidth=64;MaxHeight=64;return;}MaxWidth=double.PositiveInfinity;MaxHeight=double.PositiveInfinity;int stamp=++version;var library=ThumbnailImage.CurrentLibrary;await slots.WaitAsync();try{if(stamp!=version||!IsLoaded||!IsVisible)return;var result=await Task.Run(()=>Read(item,library));if(stamp==version&&IsLoaded&&IsVisible)Source=result;}catch(Exception ex){System.Diagnostics.Debug.WriteLine("Board thumbnail: "+ex.Message);}finally{slots.Release();}}
 }
}
