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
 public static class R3SidebarSpacingTests {
  internal static Rect Bounds(FrameworkElement element,Visual root){return element.TransformToAncestor(root).TransformBounds(new Rect(new Point(),element.RenderSize));}
  internal static bool Hit(FrameworkElement root,FrameworkElement control){
   var point=control.TranslatePoint(new Point(control.ActualWidth/2,control.ActualHeight/2),root);DependencyObject target=null;
   VisualTreeHelper.HitTest(root,node=>{var ui=node as UIElement;return ui!=null&&(ui.Visibility!=Visibility.Visible||!ui.IsHitTestVisible)?HitTestFilterBehavior.ContinueSkipSelfAndChildren:HitTestFilterBehavior.Continue;},result=>{target=result.VisualHit;return HitTestResultBehavior.Stop;},new PointHitTestParameters(point));
   bool found=false;for(var node=target;node!=null;node=MainWindow.SelectionParent(node)){var ui=node as UIElement;if(ui!=null&&(ui.Visibility!=Visibility.Visible||!ui.IsHitTestVisible||!ui.IsEnabled))return false;if(ReferenceEquals(node,control))found=true;}return found;
  }

  public static void Run(string output,Action<bool,string> check){
   Directory.CreateDirectory(output);
   var app=Application.Current??new Application();var oldShutdown=app.ShutdownMode;string oldTheme=Theme.Current??"dark";
   app.ShutdownMode=ShutdownMode.OnExplicitShutdown;Window window=null;
   try{
    using(var stream=typeof(MainWindow).Assembly.GetManifestResourceStream("Mori.Main.xaml"))window=(Window)XamlReader.Load(stream);
    var content=(FrameworkElement)window.Content;window.Content=null;content.Resources.MergedDictionaries.Add(window.Resources);
    content.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty,window.FontFamily);
    content.SetValue(System.Windows.Documents.TextElement.FontSizeProperty,window.FontSize);
    content.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty,"Text");
    var workspace=(Grid)window.FindName("Workspace");var tree=(TreeView)window.FindName("Folders");
    var footer=(Border)window.FindName("SidebarFooter");var trash=(Button)window.FindName("Trash");var utilities=(StackPanel)window.FindName("SidebarUtilityActions");var links=(StackPanel)window.FindName("SidebarCollectionLinks");var settings=(Button)window.FindName("Settings");var more=(Button)window.FindName("More");var bookmarks=(Button)window.FindName("Bookmarks");
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
     check(trash.ActualHeight>=34&&footer.Padding.Bottom<=6&&Math.Abs(footer.ActualHeight-(trash.ActualHeight+trash.Margin.Top+trash.Margin.Bottom+footer.Padding.Top+footer.Padding.Bottom))<1,label+"footer reserves only the visible recycle-bin target and explicit spacing");
     foreach(var action in new[]{trash,settings,more,bookmarks}){var bounds=Bounds(action,workspace);check(action.Visibility==Visibility.Visible&&action.ActualWidth>=24&&action.ActualHeight>=24&&action.IsEnabled&&action.Focusable&&Hit(content,action)&&bounds.Left>=0&&bounds.Right<=width+0.5,label+"visible sidebar action has a reachable focusable target: "+action.Name);}
     check(Bounds(settings,workspace).Right<=Bounds(more,workspace).Left+0.5&&Bounds(settings,workspace).Bottom<=Bounds(trash,workspace).Top&&Bounds(more,workspace).Bottom<=Bounds(trash,workspace).Top,label+"settings, more and recycle-bin targets never overlap");
     var treeBounds=Bounds(tree,workspace);var linkBounds=Bounds(links,workspace);var utilityBounds=Bounds(utilities,workspace);double footerBottom=Bounds(footer,workspace).Bottom;
     check(tree.ActualHeight>220&&Math.Abs(treeBounds.Bottom+links.Margin.Top-linkBounds.Top)<1&&Math.Abs(linkBounds.Bottom+links.Margin.Bottom+utilities.Margin.Top-utilityBounds.Top)<1&&Math.Abs(utilityBounds.Bottom+utilities.Margin.Bottom-workspace.ActualHeight)<1,label+"category list retains >220px and exactly fills the area before separate collection and utility groups (height="+tree.ActualHeight+")");
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
     check(scroll.VerticalOffset>0&&Math.Abs(Bounds(footer,workspace).Bottom-footerBottom)<1&&Math.Abs(Bounds(utilities,workspace).Bottom+utilities.Margin.Bottom-workspace.ActualHeight)<1&&Hit(content,trash),label+"tree scrolls independently while reachable recycle bin stays in the fixed bottom utility group");
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
