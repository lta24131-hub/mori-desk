using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Mori {
 public static class MediaBadgeTests {
  static void Layout(FrameworkElement root,double width,double height){root.Measure(new Size(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();}
  static T Named<T>(DependencyObject root,string name) where T:FrameworkElement{
   var current=root as T;if(current!=null&&current.Name==name)return current;
   for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var result=Named<T>(VisualTreeHelper.GetChild(root,i),name);if(result!=null)return result;}return null;
  }
  static ImageSource Fixture(){var drawing=new DrawingVisual();using(var dc=drawing.RenderOpen()){dc.DrawRectangle(Brushes.Gainsboro,null,new Rect(0,0,200,140));dc.DrawRoundedRectangle(Brushes.SlateGray,null,new Rect(65,36,70,68),8,8);dc.DrawEllipse(Brushes.LightSlateGray,null,new Point(100,62),22,14);}var result=new RenderTargetBitmap(200,140,96,96,PixelFormats.Pbgra32);result.Render(drawing);result.Freeze();return result;}
  public static void Run(string output,Action<bool,string> check){
   Directory.CreateDirectory(output);var app=Application.Current??new Application();var shutdown=app.ShutdownMode;var theme=Theme.Current??"dark";var oldLibrary=ThumbnailImage.CurrentLibrary;Window window=null;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;ThumbnailImage.CurrentLibrary=null;
   try{
    using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Mori.Main.xaml"))window=(Window)XamlReader.Load(stream);
    var template=(DataTemplate)window.Resources["Tile"];
    foreach(string appearance in new[]{"dark","light"}){
     Theme.Set(appearance);var sheet=new StackPanel{Background=Theme.Brush("Canvas"),Width=550};sheet.Resources.MergedDictionaries.Add(window.Resources);
     foreach(double width in new[]{64.0,160.0,300.0}){
      var presenter=new ContentPresenter{ContentTemplate=template};presenter.Resources.MergedDictionaries.Add(window.Resources);
      foreach(string ext in new[]{".gif",".GIF",".mp4",".mov",".m4v",".avi",".webm",".MP4",".png",".jpg",".svg",".webp",".pdf",".mp3",".url"}){
       var asset=new Asset{Name="示例",Ext=ext,Source="https://example.org/"};var tile=new GalleryTile{Asset=asset,Width=width,ImageHeight=100,ShowName=false,ShowDetails=false};presenter.Content=tile;Layout(presenter,width+12,120);
       var badge=Named<Border>(presenter,"MediaBadge");var text=Named<TextBlock>(presenter,"MediaBadgeText");var play=Named<System.Windows.Shapes.Path>(presenter,"VideoBadgePlay");bool video=MediaTypes.Videos.Contains(ext),gif=string.Equals(ext,".gif",StringComparison.OrdinalIgnoreCase);string label=appearance+" / "+width+" / "+ext+": ";
       check(badge!=null&&badge.Visibility==(video||gif?Visibility.Visible:Visibility.Collapsed),label+"badge appears only for GIF/video, including recycled tiles");
       if(video||gif){
        check(text.Text==(video?ext.TrimStart('.').ToUpperInvariant():"GIF")&&play.Visibility==(video?Visibility.Visible:Visibility.Collapsed),label+"actual file format and video play glyph are correct");
        var image=Named<ThumbnailImage>(presenter,"TileImage");var grid=(Grid)image.Parent;var origin=badge.TranslatePoint(new Point(),grid);
        check(Math.Abs(origin.X-5)<0.1&&Math.Abs(origin.Y-5)<0.1&&badge.ActualHeight==17&&badge.ActualWidth<57&&badge.ActualWidth+origin.X<=grid.ActualWidth,label+"compact fixed-size badge stays inside thumbnail top-left");
        check(!badge.IsHitTestVisible&&!badge.Focusable&&Panel.GetZIndex(badge)>0,label+"badge overlays image without intercepting selection, drag or double-click");
        check(((SolidColorBrush)text.Foreground).Color==Colors.White,label+"high contrast text remains white in both themes");
       }
       check(tile.LabelHeight==0&&presenter.ActualHeight<=120,label+"badge adds no row height and works with filename/details hidden");
      }
     }
     var row=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(12)};sheet.Children.Add(row);
     foreach(string ext in new[]{".gif",".mp4",".png"}){
      var presenter=new ContentPresenter{ContentTemplate=template,Content=new GalleryTile{Asset=new Asset{Name=ext==".gif"?"GIF 动图":ext==".mp4"?"视频":"普通图片",Ext=ext},Width=160,ImageHeight=112,ShowName=true,ShowDetails=false}};row.Children.Add(presenter);Layout(sheet,550,180);Named<ThumbnailImage>(presenter,"TileImage").Source=Fixture();
     }
     Layout(sheet,550,180);var bitmap=new RenderTargetBitmap(550,180,96,96,PixelFormats.Pbgra32);bitmap.Render(sheet);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(System.IO.Path.Combine(output,appearance+".png")))encoder.Save(file);
    }
    check(!window.IsVisible,"badge rendering tests use synthetic offline content without opening user library or moving the mouse");
   }finally{if(window!=null)window.Close();ThumbnailImage.CurrentLibrary=oldLibrary;Theme.Set(theme);app.ShutdownMode=shutdown;}
  }
 }
}
