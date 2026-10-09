using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Mori {
 // System monochrome glyphs: no bitmap cache, bundled font or runtime dependency.
 public static class UiIcons {
  public static readonly DependencyProperty GlyphProperty=DependencyProperty.RegisterAttached("Glyph",typeof(string),typeof(UiIcons),new FrameworkPropertyMetadata(""));
  public static string GetGlyph(DependencyObject target){return (string)target.GetValue(GlyphProperty);}
  public static void SetGlyph(DependencyObject target,string value){target.SetValue(GlyphProperty,value);}
  public static string ForMenu(string label){
   label=label??"";
   if(label.StartsWith("撤销"))return "\uE7A7";
   if(label.Contains("删除")||label.Contains("回收站"))return "\uE74D";
   if(label.Contains("重命名"))return "\uE70F";
   if(label.Contains("新建"))return "\uE8F4";
   if(label.Contains("恢复"))return "\uE777";
   if(label.Contains("备份"))return "\uE74E";
   if(label.Contains("复制"))return "\uE8C8";
   if(label.Contains("导出"))return "\uE898";
   if(label.Contains("星标"))return "\uE734";
   if(label.Contains("刷新"))return "\uE72C";
   if(label.Contains("排列"))return "\uE8CB";
   if(label.Contains("书签")||label.Contains("网址")||label.Contains("网页"))return "\uE774";
   if(label.Contains("插件"))return "\uEA86";
   if(label.Contains("说明"))return "\uE946";
   if(label.Contains("退出"))return "\uE8BB";
   if(label=="导入"||label.Contains("素材文件"))return "\uE8B5";
   if(label.Contains("文件夹")||label.Contains("分类")||label.Contains("图库")||label.Contains("资源管理器"))return "\uE8B7";
   if(label.Contains("预览")||label=="显示")return "\uE890";
   if(label.Contains("打开"))return "\uE8A7";
   if(label.Contains("信息"))return "\uE8A5";
   return "";
  }
 }
 public sealed class MenuIconConverter:IValueConverter {
  public object Convert(object value,Type targetType,object parameter,CultureInfo culture){return UiIcons.ForMenu(value as string);}
  public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture){throw new NotSupportedException();}
 }
}
