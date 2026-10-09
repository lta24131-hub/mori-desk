using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mori {
 public partial class MainWindow {
  void InitializeInspectorPreview(){
   var image=Find<ThumbnailImage>("DetailPreview");var property=DependencyPropertyDescriptor.FromProperty(Image.SourceProperty,typeof(Image));EventHandler handler=delegate{UpdatePalette(image.Source as BitmapSource);};property.AddValueChanged(image,handler);Window.Closed+=delegate{property.RemoveValueChanged(image,handler);};
   Find<Grid>("InspectorPreview").MouseLeftButtonDown+=delegate(object sender,System.Windows.Input.MouseButtonEventArgs e){if(e.ClickCount==2){var a=Selected().FirstOrDefault();if(a!=null)Preview(a);}};
  }
  void UpdateInspectorPreview(){
   var items=Selected();var a=items.Count==1?items[0]:null;Find<Grid>("InspectorPreview").Visibility=a==null?Visibility.Collapsed:Visibility.Visible;
   var preview=Find<ThumbnailImage>("DetailPreview");preview.DataContext=a!=null&&!a.IsBookmark?a:null;preview.Visibility=a!=null&&!a.IsBookmark?Visibility.Visible:Visibility.Collapsed;
   var icon=Find<BookmarkIcon>("DetailSiteIcon");icon.DataContext=a!=null&&a.IsBookmark?a:null;icon.Visibility=a!=null&&a.IsBookmark?Visibility.Visible:Visibility.Collapsed;
   L("DetailAudioIcon").Visibility=a!=null&&a.IsAudio?Visibility.Visible:Visibility.Collapsed;
   if(a==null||a.IsBookmark||a.IsAudio||a.Ext==".pdf")UpdatePalette(null);B("Categorize").Content=bookmarkMode?"移动到…":"添加分类…";B("Categorize").ToolTip=bookmarkMode?"移动到网址文件夹":"添加到素材分类";
   L("SelectionInfo").Text=a!=null?(a.IsBookmark?"1 个网址":IsVideo(a)?"1 个视频":a.IsAudio?"1 个音频":a.Ext==".pdf"?"1 个文档":"1 张图片")+"已选中":items.Count>1?items.Count+" 个素材已选中":"选中素材查看详情";
  }
  internal static List<Color> Palette(BitmapSource source){
   if(source==null)return new List<Color>();double scale=Math.Min(1,32.0/Math.Max(source.PixelWidth,source.PixelHeight));var small=new FormatConvertedBitmap(new TransformedBitmap(source,new ScaleTransform(scale,scale)),PixelFormats.Bgra32,null,0);int stride=small.PixelWidth*4;byte[] bytes=new byte[stride*small.PixelHeight];small.CopyPixels(bytes,stride,0);
   var groups=new Dictionary<int,long[]>();for(int i=0;i<bytes.Length;i+=4){if(bytes[i+3]<160)continue;int key=(bytes[i+2]>>5)*64+(bytes[i+1]>>5)*8+(bytes[i]>>5);long[] c;if(!groups.TryGetValue(key,out c))groups[key]=c=new long[4];c[0]+=bytes[i+2];c[1]+=bytes[i+1];c[2]+=bytes[i];c[3]++;}
   return groups.Values.OrderByDescending(c=>c[3]).Take(5).Select(c=>Color.FromRgb((byte)(c[0]/c[3]),(byte)(c[1]/c[3]),(byte)(c[2]/c[3]))).ToList();
  }
  void UpdatePalette(BitmapSource source){
   var panel=Find<UniformGrid>("ColorSwatches");panel.Children.Clear();try{foreach(var color in Palette(source)){string hex=string.Format("#{0:X2}{1:X2}{2:X2}",color.R,color.G,color.B);var swatch=new Border{Background=new SolidColorBrush(color),CornerRadius=new CornerRadius(2),Margin=new Thickness(1,0,1,0),ToolTip=hex+" · 点击复制",Cursor=System.Windows.Input.Cursors.Hand};swatch.MouseLeftButtonDown+=delegate{try{Clipboard.SetText(hex);Status("已复制颜色 "+hex);}catch(Exception error){Status(error.Message);}};panel.Children.Add(swatch);}}catch{}panel.Visibility=panel.Children.Count>0?Visibility.Visible:Visibility.Collapsed;
  }
 }
}
