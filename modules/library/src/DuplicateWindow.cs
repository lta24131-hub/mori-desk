using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mori {
 public static class DuplicateWindow {
  public static string Ask(Window owner,Library library,Asset existing,string incoming,bool batch,out bool applyAll){
   string choice="skip";var window=new Window{Owner=owner,Title="Mori · 图片已存在",Width=620,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner};Theme.Attach(window,owner);
   var root=new StackPanel{Margin=new Thickness(26)};window.Content=root;
   root.Children.Add(new TextBlock{Text="图库中已存在相同内容的文件",FontSize=19,FontWeight=FontWeights.SemiBold});
   var hint=new TextBlock{Text="使用已有图片不会再占一份空间，也不会覆盖原有备注。",Margin=new Thickness(0,10,0,22),TextWrapping=TextWrapping.Wrap};hint.SetResourceReference(TextBlock.ForegroundProperty,"Muted");root.Children.Add(hint);
   var grid=new Grid();grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition());root.Children.Add(grid);
   var left=Card(library.FullPath(existing),"已有文件"+(existing.Deleted?" · 回收站":""),existing.Name,existing.Detail);grid.Children.Add(left);
   var right=Card(incoming,"本次导入",Path.GetFileNameWithoutExtension(incoming),Asset.Size(new FileInfo(incoming).Length)+" · "+Path.GetExtension(incoming).TrimStart('.').ToUpperInvariant());Grid.SetColumn(right,1);grid.Children.Add(right);
   var check=new CheckBox{Content="本批重复文件都按此处理",Visibility=batch?Visibility.Visible:Visibility.Collapsed,Margin=new Thickness(0,20,0,0)};root.Children.Add(check);
   var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,24,0,0)};root.Children.Add(buttons);
   foreach(var pair in new[]{new[]{"skip","不导入"},new[]{"reuse","使用已有"},new[]{"keep","仍然导入一份"}}){var action=pair[0];var button=new Button{Content=pair[1],IsCancel=action=="skip",Margin=new Thickness(7,0,0,0)};button.Click+=delegate{choice=action;window.Close();};buttons.Children.Add(button);}
   window.ShowDialog();applyAll=check.IsChecked==true;return choice;
  }
  static FrameworkElement Card(string path,string label,string name,string info){var stack=new StackPanel{Margin=new Thickness(8,0,8,0)};var frame=new Border{Height=205,Padding=new Thickness(10),CornerRadius=new CornerRadius(6)};frame.SetResourceReference(Border.BackgroundProperty,"Surface");stack.Children.Add(frame);
   try{int width,height;using(var source=File.OpenRead(path)){var decoder=BitmapDecoder.Create(source,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);width=decoder.Frames[0].PixelWidth;height=decoder.Frames[0].PixelHeight;}double scale=Math.Min(1,260.0/Math.Max(width,height));var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.DecodePixelWidth=Math.Max(1,(int)(width*scale));bitmap.DecodePixelHeight=Math.Max(1,(int)(height*scale));bitmap.UriSource=new Uri(path);bitmap.EndInit();bitmap.Freeze();frame.Child=new Image{Source=bitmap,Stretch=Stretch.Uniform};}catch(Exception){frame.Child=new TextBlock{Text=Path.GetExtension(path).TrimStart('.').ToUpperInvariant(),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};}
   stack.Children.Add(new TextBlock{Text=label,HorizontalAlignment=HorizontalAlignment.Center,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,12,0,7)});stack.Children.Add(new TextBlock{Text=name,TextTrimming=TextTrimming.CharacterEllipsis,TextAlignment=TextAlignment.Center,ToolTip=name});var details=new TextBlock{Text=info,FontSize=11,TextAlignment=TextAlignment.Center,Margin=new Thickness(0,6,0,0)};details.SetResourceReference(TextBlock.ForegroundProperty,"Muted");stack.Children.Add(details);return stack;
  }
 }
}
