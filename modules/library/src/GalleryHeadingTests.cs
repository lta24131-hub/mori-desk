using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mori {
 public static class GalleryHeadingTests {
  public static void Run(string output,Action<bool,string> check){
   Directory.CreateDirectory(output);var app=Application.Current??new Application();var shutdown=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;Window window=null;
   try{
    using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Mori.Main.xaml"))window=(Window)XamlReader.Load(stream);
    var content=(FrameworkElement)window.Content;window.Content=null;content.Resources.MergedDictionaries.Add(window.Resources);
    content.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty,window.FontFamily);content.SetValue(System.Windows.Documents.TextElement.FontSizeProperty,window.FontSize);
    content.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty,"Text");
    var center=(Grid)window.FindName("Center");var heading=(Grid)window.FindName("GalleryHeading");var title=(TextBlock)window.FindName("ViewTitle");var count=(TextBlock)window.FindName("ViewCount");var clear=(Button)window.FindName("ClearFilter");var media=(ComboBox)window.FindName("MediaFilter");var sort=(ComboBox)window.FindName("Sort");
    foreach(string theme in new[]{"dark","light"})foreach(int width in new[]{1040,1380})foreach(bool filtering in new[]{false,true}){
     Theme.Set(theme);foreach(object key in app.Resources.Keys)content.Resources[key]=app.Resources[key];content.SetResourceReference(Panel.BackgroundProperty,"Canvas");
     ((Grid)window.FindName("Workspace")).ColumnDefinitions[0].Width=new GridLength(width==1040?296:208);
     title.Text=filtering?"一个很长的分类名称：图片与视频参考":"全部素材";count.Text="657 个素材 · 483.5 MB";clear.Visibility=filtering?Visibility.Visible:Visibility.Collapsed;
     content.Width=width;content.Height=680;content.Measure(new Size(width,680));content.Arrange(new Rect(0,0,width,680));content.UpdateLayout();
     string label=theme+" / "+width+" / filter "+filtering+": ";
     check(center.RowDefinitions[0].ActualHeight==44&&((ListBox)window.FindName("Gallery")).TranslatePoint(new Point(),center).Y==44,label+"gallery heading is 44px and returns 28px to images");
     double titleY=title.TranslatePoint(new Point(),heading).Y+title.ActualHeight/2,countY=count.TranslatePoint(new Point(),heading).Y+count.ActualHeight/2;
     check(Math.Abs(titleY-countY)<1&&title.FontSize==14&&title.ActualHeight<=24&&count.ActualHeight<=24,label+"title and count remain one compact vertically aligned line");
     double right=count.TranslatePoint(new Point(count.ActualWidth,0),center).X,left=media.TranslatePoint(new Point(),center).X;
     check(right<left&&media.ActualWidth>=94&&sort.ActualWidth>=109&&title.ActualWidth>20,label+"long titles and filter controls remain separated in minimum window");
     check(title.ToolTip.ToString()==title.Text&&count.ToolTip.ToString()==count.Text,label+"full category name and count retained in tooltips");
     if(!filtering){
      var bitmap=new RenderTargetBitmap(width,680,96,96,PixelFormats.Pbgra32);bitmap.Render(content);
      var origin=center.TransformToAncestor(content).Transform(new Point());var crop=new CroppedBitmap(bitmap,new Int32Rect((int)origin.X,(int)origin.Y,(int)center.ActualWidth,44));
      var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(crop));using(var file=File.Create(Path.Combine(output,theme+"-"+width+".png")))encoder.Save(file);
     }
    }
   }finally{if(window!=null)window.Close();Theme.Set("dark");app.ShutdownMode=shutdown;}
  }
 }
}
