using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mori {
 public static class SidebarSpacingTests {
  public static void Run(string output,Action<bool,string> check){
   Directory.CreateDirectory(output);
   var app=Application.Current??new Application();var oldShutdown=app.ShutdownMode;string oldTheme=Theme.Current??"dark";
   app.ShutdownMode=ShutdownMode.OnExplicitShutdown;Window window=null;
   try{
    using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Mori.Main.xaml"))window=(Window)XamlReader.Load(stream);
    var content=(FrameworkElement)window.Content;window.Content=null;content.Resources.MergedDictionaries.Add(window.Resources);
    content.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty,window.FontFamily);
    content.SetValue(System.Windows.Documents.TextElement.FontSizeProperty,window.FontSize);
    content.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty,"Text");
    var workspace=(Grid)window.FindName("Workspace");var tree=(TreeView)window.FindName("Folders");
    var footer=(Border)window.FindName("SidebarFooter");var trash=(Button)window.FindName("Trash");
    var root=Node("Eagle 导入 · 分类","root");var parent=Node("00 产品参考","parent");var leaf=Node("01 双轮、结构与细节参考","leaf");
    root.Items.Add(parent);parent.Items.Add(leaf);tree.Items.Add(root);
    for(int i=2;i<=24;i++)parent.Items.Add(Node(i.ToString("00")+" 图片与视频参考分类","item"+i));
    var other=Node("01 其他参考","other");root.Items.Add(other);other.Items.Add(Node("更深层分类名称仍可查看完整提示","deep"));
    int selectedEvents=0;tree.SelectedItemChanged+=delegate{selectedEvents++;};
    foreach(string theme in new[]{"dark","light"})foreach(int width in new[]{160,208,320}){
     Theme.Set(theme);foreach(object key in app.Resources.Keys)content.Resources[key]=app.Resources[key];
     content.SetResourceReference(Panel.BackgroundProperty,"Canvas");workspace.ColumnDefinitions[0].Width=new GridLength(width);
     content.Width=1040;content.Height=680;Layout(content);
     var header=(ContentPresenter)leaf.Template.FindName("PART_Header",leaf);
     var rootHeader=(ContentPresenter)root.Template.FindName("PART_Header",root);
     var parentHeader=(ContentPresenter)parent.Template.FindName("PART_Header",parent);
     var row=(Border)leaf.Template.FindName("FolderRow",leaf);
     var expander=(ToggleButton)parent.Template.FindName("Expander",parent);
     var items=(ItemsPresenter)parent.Template.FindName("ItemsHost",parent);
     var scroll=Descendant<ScrollViewer>(tree);string label=theme+" / "+width+" sidebar: ";
     double first=rootHeader.TransformToAncestor(workspace).Transform(new Point()).X;
     double second=parentHeader.TransformToAncestor(workspace).Transform(new Point()).X;
     double third=header.TransformToAncestor(workspace).Transform(new Point()).X;
     check(first<=26&&Math.Abs(second-first-12)<0.1&&Math.Abs(third-second-12)<0.1,label+"compact 12px nesting and <=50px third-level inset");
     check(header.ActualWidth>=75&&row.ActualHeight>=28&&row.ActualHeight<=32,label+"narrow sidebar preserves useful text width and comfortable row height");
     var text=Descendant<TextBlock>(header);
     check(text!=null&&text.TextTrimming==TextTrimming.CharacterEllipsis&&leaf.ToolTip.ToString()==leaf.Header.ToString(),label+"long labels ellipsize and retain full-name tooltip");
     check(scroll!=null&&scroll.ComputedHorizontalScrollBarVisibility!=Visibility.Visible&&scroll.ScrollableWidth<0.1&&scroll.ComputedVerticalScrollBarVisibility==Visibility.Visible,label+"no horizontal overflow; vertical category scrolling remains available");
     check(footer.ActualHeight<=46&&trash.ActualHeight>=34&&footer.Padding.Bottom<=6,label+"footer has usable recycle-bin button without redundant notice or blank padding");
     check(tree.ActualHeight>220&&Math.Abs(tree.ActualHeight+footer.ActualHeight+tree.TranslatePoint(new Point(),workspace).Y-workspace.ActualHeight)<1,label+"category list keeps >220px below six media sections at minimum window size (height="+tree.ActualHeight+")");
     expander.IsChecked=false;Layout(content);
     check(!parent.IsExpanded&&items.Visibility==Visibility.Collapsed,label+"expander still collapses children");
     parent.IsExpanded=true;Layout(content);
     check(expander.IsChecked==true&&items.Visibility==Visibility.Visible,label+"expanded state updates expander both ways");
     leaf.IsSelected=true;Layout(content);
     check(tree.SelectedItem==leaf&&selectedEvents>0&&Same(row.Background,Theme.Brush("AccentSoft"))&&leaf.AllowDrop,label+"selection highlight, selected item and drop target remain intact");
     check(Same(leaf.Foreground,Theme.Brush("Text"))&&Same(expander.Foreground,Theme.Brush("Muted")),label+"labels and expanders match current theme");
     scroll.ScrollToTop();Layout(content);
     SaveSidebar(content,workspace,width,Path.Combine(output,theme+"-"+width+".png"));
     scroll.ScrollToEnd();Layout(content);
     check(scroll.VerticalOffset>0&&Math.Abs(footer.TranslatePoint(new Point(),workspace).Y+footer.ActualHeight-workspace.ActualHeight)<1,label+"tree scrolls independently while recycle bin stays at bottom");
     scroll.ScrollToTop();leaf.IsSelected=false;Layout(content);
    }
    check(!window.IsVisible,"spacing verification uses isolated synthetic categories; no desktop input or user settings writes");
   }finally{if(window!=null)window.Close();Theme.Set(oldTheme);app.ShutdownMode=oldShutdown;}
  }
  static TreeViewItem Node(string name,string id){return new TreeViewItem{Header=name,Tag=id,ToolTip=name,AllowDrop=true};}
  static void Layout(FrameworkElement content){content.Measure(new Size(content.Width,content.Height));content.Arrange(new Rect(0,0,content.Width,content.Height));content.UpdateLayout();}
  static T Descendant<T>(DependencyObject root)where T:DependencyObject{for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T)return (T)child;var result=Descendant<T>(child);if(result!=null)return result;}return null;}
  static bool Same(Brush first,Brush second){return first is SolidColorBrush&&second is SolidColorBrush&&((SolidColorBrush)first).Color==((SolidColorBrush)second).Color;}
  static void SaveSidebar(FrameworkElement content,Grid workspace,int width,string path){
   var bitmap=new RenderTargetBitmap((int)content.Width,(int)content.Height,96,96,PixelFormats.Pbgra32);bitmap.Render(content);
   var origin=workspace.TransformToAncestor(content).Transform(new Point());var crop=new CroppedBitmap(bitmap,new Int32Rect((int)origin.X,(int)origin.Y,width,(int)workspace.ActualHeight));
   var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(crop));using(var stream=File.Create(path))encoder.Save(stream);
  }
 }
}
