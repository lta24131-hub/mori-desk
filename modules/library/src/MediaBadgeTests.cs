using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mori {
 public static class MediaBadgeTests {
  static void Layout(FrameworkElement root,double width,double height){root.Measure(new Size(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();}
  static T Named<T>(DependencyObject root,string name) where T:FrameworkElement{
   var current=root as T;if(current!=null&&current.Name==name)return current;
   for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var result=Named<T>(VisualTreeHelper.GetChild(root,i),name);if(result!=null)return result;}return null;
  }
  static ImageSource Fixture(){var drawing=new DrawingVisual();using(var dc=drawing.RenderOpen()){dc.DrawRectangle(Brushes.WhiteSmoke,null,new Rect(0,0,200,140));dc.DrawRoundedRectangle(Brushes.SlateGray,null,new Rect(65,20,70,68),8,8);dc.DrawEllipse(Brushes.LightSlateGray,null,new Point(100,46),22,14);dc.DrawRectangle(Brushes.Silver,null,new Rect(25,106,150,3));dc.DrawRectangle(Brushes.Silver,null,new Rect(25,116,125,3));}var result=new RenderTargetBitmap(200,140,96,96,PixelFormats.Pbgra32);result.Render(drawing);result.Freeze();return result;}
  static ContentPresenter Presenter(Window window,DataTemplate template,GalleryTile tile){var result=new ContentPresenter{ContentTemplate=template,Content=tile};result.Resources.MergedDictionaries.Add(window.Resources);return result;}
  static void CheckTile(ContentPresenter presenter,GalleryTile tile,string label,Action<bool,string> check){
   var badge=Named<Border>(presenter,"MediaBadge");var text=Named<TextBlock>(presenter,"MediaBadgeText");var play=Named<System.Windows.Shapes.Path>(presenter,"VideoBadgePlay");var asset=tile.Asset;
   check(badge!=null&&badge.Visibility==(asset.ShowFormatBadge?Visibility.Visible:Visibility.Collapsed),label+"format is visible for files only, including recycled tiles");
   if(!asset.ShowFormatBadge)return;
   check(text.Text==asset.TypeLabel&&play.Visibility==(asset.IsVideo?Visibility.Visible:Visibility.Collapsed),label+"actual extension and video glyph are correct");
   var image=Named<ThumbnailImage>(presenter,"TileImage");var grid=(Grid)image.Parent;var origin=badge.TranslatePoint(new Point(),grid);
   check(Math.Abs(origin.X-5)<.1&&Math.Abs(origin.Y+badge.ActualHeight+5-grid.ActualHeight)<.1&&badge.ActualHeight==18,label+"compact badge stays at thumbnail bottom-left");
   check(badge.ActualWidth>0&&origin.X+badge.ActualWidth<=grid.ActualWidth-5+.1,label+"long format labels stay inside the smallest thumbnail");
   check(!badge.IsHitTestVisible&&!badge.Focusable&&Panel.GetZIndex(badge)>0,label+"format overlay does not intercept selection, drag or double-click");
   check(((SolidColorBrush)text.Foreground).Color==Colors.White&&text.FontSize==11,label+"readable format text is consistent in both themes");
   var select=Named<Border>(presenter,"SelectionMark");if(select.Visibility==Visibility.Visible){var mark=select.TranslatePoint(new Point(),grid);check(!new Rect(origin,badge.RenderSize).IntersectsWith(new Rect(mark,select.RenderSize)),label+"format and multi-select mark never overlap");}
  }
  public static void Run(string output,Action<bool,string> check){
   Directory.CreateDirectory(output);var app=Application.Current??new Application();var shutdown=app.ShutdownMode;var theme=Theme.Current??"dark";var oldLibrary=ThumbnailImage.CurrentLibrary;Window window=null;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;ThumbnailImage.CurrentLibrary=null;
   try{
    using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Mori.Main.xaml"))window=(Window)XamlReader.Load(stream);
    var template=(DataTemplate)window.Resources["Tile"];
    foreach(var pair in new[]{new[]{".pdf","PDF"},new[]{".ai","AI"},new[]{".PSD","PSD"},new[]{".MP4","MP4"},new[]{".step","STEP"},new[]{".png","PNG"}}){var file=new Asset{Ext=pair[0]};check(file.TypeLabel==pair[1]&&file.ShowFormatBadge,"file badge policy uses actual "+pair[1]+" extension rather than generic media type");}
    check(!new Asset{Ext=".pdf",IsDirectory=true}.ShowFormatBadge&&!new Asset{Ext=".url",Source="https://example.org/"}.ShowFormatBadge,"folders and website cards are independently excluded from format badges");
    var localUrl=new Asset{Ext=".url",ReferencePath="C:\\fixture\\website.url"};check(localUrl.ShowFormatBadge&&localUrl.TypeLabel=="URL","local shortcut URL files remain file formats, not website cards");
    foreach(string appearance in new[]{"dark","light"}){
     Theme.Set(appearance);
     foreach(double width in new[]{40.0,64.0,80.0,100.0,130.0,160.0,300.0}){
      var presenter=Presenter(window,template,null);
      foreach(string ext in new[]{".gif",".GIF",".mp4",".mov",".m4v",".avi",".webm",".MP4",".png",".jpg",".svg",".webp",".pdf",".ai",".psd",".mp3",".STEP",".docx",".markdown",".url","",null,".a_very_long_extension"}){
       var asset=new Asset{Name="同名参考",Ext=ext,Source="https://example.org/",Selecting=true,Selected=true};var tile=new GalleryTile{Asset=asset,Width=width,ImageHeight=66,ShowName=false,ShowDetails=false};presenter.Content=tile;Layout(presenter,width+12,86);
       string label=appearance+" / "+width+" / "+(ext??"null")+": ";CheckTile(presenter,tile,label,check);
       check(tile.LabelHeight==0&&presenter.ActualHeight<=86,label+"format remains visible when filename/details are hidden, without extra row height");
       asset.IsDirectory=true;presenter.Content=new GalleryTile{Asset=asset,Width=width,ImageHeight=66};Layout(presenter,width+12,86);
       check(Named<Border>(presenter,"MediaBadge").Visibility==Visibility.Collapsed,label+"folder never gets a file format badge even with a file-like name");
       // The same presenter receives a local .url file after a website card.
       var url=new Asset{Name="本地网站快捷文件",Ext=".url",ReferencePath="C:\\fixture\\website.url"};presenter.Content=new GalleryTile{Asset=url,Width=width,ImageHeight=66};Layout(presenter,width+12,86);
       CheckTile(presenter,(GalleryTile)presenter.Content,label+"local URL: ",check);
      }
     }
     foreach(bool names in new[]{false,true})foreach(bool details in new[]{false,true}){
      var tile=new GalleryTile{Asset=new Asset{Name="同名参考",Ext=".pdf"},Width=100,ImageHeight=66,ShowName=names,ShowDetails=details};var presenter=Presenter(window,template,tile);Layout(presenter,112,140);CheckTile(presenter,tile,appearance+" / label settings: ",check);
     }
     var files=new[]{".pdf",".ai",".psd",".png",".gif",".mp4",".step",".mp3"}.Select(ext=>new Asset{Name="同名参考"+ext,Ext=ext,ReferencePath="C:\\fixture\\file"+ext}).ToList();
     foreach(int size in new[]{GalleryLayout.MinSize,GalleryLayout.DefaultSize,GalleryLayout.MaxSize})foreach(var tile in GalleryLayout.BuildShortcuts(files,980,size).SelectMany(row=>row.Items)){
      var presenter=Presenter(window,template,tile);Layout(presenter,tile.Width+12,tile.ImageHeight+48);CheckTile(presenter,tile,appearance+" / real local layout "+size+": ",check);
      check(!tile.ShowDetails&&tile.Asset.ShowFormatBadge,"actual small local file cards always identify their format");
     }
     var sheet=new StackPanel{Background=Theme.Brush("Canvas"),Width=980,Margin=new Thickness(0)};sheet.Resources.MergedDictionaries.Add(window.Resources);
     foreach(bool small in new[]{true,false}){
      sheet.Children.Add(new TextBlock{Text=small?"本地收藏 · 文件":"素材预览",FontSize=13,Foreground=Theme.Brush("Text"),Margin=new Thickness(16,14,0,12)});
      var row=new WrapPanel{Margin=new Thickness(10,0,10,0)};sheet.Children.Add(row);
      foreach(var file in files){var tile=new GalleryTile{Asset=file,Width=small?100:160,ImageHeight=small?66:112,ShowName=true,ShowDetails=false};var presenter=Presenter(window,template,tile);row.Children.Add(presenter);Layout(sheet,980,480);Named<ThumbnailImage>(presenter,"TileImage").Source=Fixture();}
     }
     Layout(sheet,980,480);var bitmap=new RenderTargetBitmap(980,480,96,96,PixelFormats.Pbgra32);bitmap.Render(sheet);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(System.IO.Path.Combine(output,appearance+".png")))encoder.Save(file);
    }
    check(new Asset{Ext=null}.TypeLabel=="文件"&&new Asset{Ext=""}.TypeLabel=="文件","extensionless or incomplete metadata uses safe neutral label without guessing a format");
    check(!window.IsVisible,"badge rendering tests use synthetic offline content without opening user library or moving the mouse");
   }finally{if(window!=null)window.Close();ThumbnailImage.CurrentLibrary=oldLibrary;Theme.Set(theme);app.ShutdownMode=shutdown;}
  }
 }
}
