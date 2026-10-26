using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace Mori {
 public static class GalleryLayoutTests {
  static List<T> Descendants<T>(DependencyObject root)where T:DependencyObject{var results=new List<T>();for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);var typed=child as T;if(typed!=null)results.Add(typed);results.AddRange(Descendants<T>(child));}return results;}
  static Asset A(int n,int w,int h){return new Asset{Id=n.ToString(),Name="Gallery test "+n,Ext=".png",Width=w,Height=h};}
  static MouseWheelEventArgs Wheel(int delta){return new MouseWheelEventArgs(Mouse.PrimaryDevice,0,delta){RoutedEvent=Mouse.PreviewMouseWheelEvent};}
  static Rect Bounds(FrameworkElement element,Visual root){return element.TransformToAncestor(root).TransformBounds(new Rect(new Point(),element.RenderSize));}
  static void HeaderLayout(Window window,FrameworkElement content,Action<bool,string> check){
   var header=(Grid)window.FindName("GalleryHeader");var title=(TextBlock)window.FindName("ViewTitle");var count=(TextBlock)window.FindName("ViewCount");
   var clear=(Button)window.FindName("ClearFilter");var filter=(ComboBox)window.FindName("MediaFilter");var sort=(ComboBox)window.FindName("Sort");var multi=(Button)window.FindName("MultiSelect");var toggle=(Button)window.FindName("ToggleInspector");
   var workspace=(Grid)window.FindName("Workspace");var center=(Grid)window.FindName("Center");
   string oldTitle=title.Text,oldCount=count.Text;Visibility oldClear=clear.Visibility,oldFilter=filter.Visibility;
   double oldWidth=content.Width,oldHeight=content.Height;var oldLeft=workspace.ColumnDefinitions[0].Width;
   try{
    // Measure the real full page from its detached root. Arranging the header
    // alone while it still has a parent lets UpdateLayout restore its old
    // 1380px window allocation, making every "narrow" case falsely 860px wide.
    workspace.ColumnDefinitions[0].Width=new GridLength(296);
    count.Text="9999 个素材 · 128.50 GB";
    foreach(string theme in new[]{"dark","light"})foreach(double width in new[]{480.0,720,1000})foreach(bool searching in new[]{false,true})foreach(bool filtering in new[]{false,true})foreach(bool longTitle in new[]{false,true}){
     Theme.Set(theme);title.Text=longTitle?"一个名称很长的项目素材分类用于窄窗口检查":"全部素材";clear.Visibility=searching?Visibility.Visible:Visibility.Collapsed;filter.Visibility=filtering?Visibility.Visible:Visibility.Collapsed;
     double fullWidth=width+296+6+252+6;
     content.Width=fullWidth;content.Height=900;content.Measure(new Size(fullWidth,900));content.Arrange(new Rect(0,0,fullWidth,900));content.UpdateLayout();
     string context=theme+" header / "+width+" / search="+searching+" / filter="+filtering+" / long="+longTitle;
     check(VisualTreeHelper.GetParent(content)==null&&Math.Abs(content.ActualWidth-fullWidth)<.5&&Math.Abs(center.ActualWidth-width)<.5&&Math.Abs(header.ActualWidth-(width-header.Margin.Left-header.Margin.Right))<.5,context+" measures the requested center width after header margins (root="+content.ActualWidth+", center="+center.ActualWidth+", header="+header.ActualWidth+")");
     var probe=new TextBlock{Text=count.Text,FontFamily=count.FontFamily,FontSize=count.FontSize,FontWeight=count.FontWeight};probe.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
     check(count.ActualWidth>=probe.DesiredSize.Width-.5,context+" keeps full count and size instead of toolbar-induced ellipsis");
     var heading=Bounds((FrameworkElement)window.FindName("GalleryHeading"),header);var toggleBounds=Bounds(toggle,header);var multiBounds=Bounds(multi,header);var sortBounds=Bounds(sort,header);
     check(heading.Right<=toggleBounds.Left+.5&&heading.Bottom<=multiBounds.Top+.5,context+" separates heading, details toggle and actions");
     check(sortBounds.Right<=header.ActualWidth+.5&&multiBounds.Left>=-.5&&sortBounds.Height>=30,context+" fits actions within minimum center width");
     var previous=multiBounds;bool separated=true;foreach(FrameworkElement control in new FrameworkElement[]{clear,filter,sort}){if(control.Visibility!=Visibility.Visible)continue;var next=Bounds(control,header);separated&=previous.Right<=next.Left+.5;previous=next;}
     check(separated,context+" keeps visible controls separate in their reading order");
     check(title.ActualWidth<=200.5&&Bounds(title,(Grid)window.FindName("GalleryHeading")).Right<=Bounds(count,(Grid)window.FindName("GalleryHeading")).Left+.5,context+" constrains the actual title element, not only its Auto column, before the count");
    }
    check(System.Windows.Automation.AutomationProperties.GetName(filter)=="素材类型筛选"&&System.Windows.Automation.AutomationProperties.GetName(sort)=="素材排序"&&System.Windows.Automation.AutomationProperties.GetName(toggle)=="收起详情栏","gallery controls keep distinct assistive labels after rearrangement");
    check(multi.Focusable&&clear.Focusable&&filter.Focusable&&sort.Focusable&&toggle.Focusable,"gallery actions remain keyboard focusable");
   }finally{title.Text=oldTitle;count.Text=oldCount;clear.Visibility=oldClear;filter.Visibility=oldFilter;workspace.ColumnDefinitions[0].Width=oldLeft;content.Width=oldWidth;content.Height=oldHeight;}
  }
  public static void Run(string output,Action<bool,string> check){
   Directory.CreateDirectory(output);
   var assets=new List<Asset>{A(0,492,900),A(1,1027,787),A(2,658,435),A(3,1440,1801),A(4,748,512),A(5,564,564),A(6,470,470),A(7,483,391),A(8,564,650),A(9,730,592),A(10,1080,807),A(11,750,1000)};
   assets[2].Selected=true;
   check(GalleryLayout.Build(new List<Asset>(),900,210).Count==0,"gallery empty layout has no rows");
   foreach(double width in new[]{40.0,180,540,900,1920})foreach(int size in new[]{120,210,360}){
    var rows=GalleryLayout.Build(assets,width,size);var tiles=rows.SelectMany(r=>r.Items).ToList();
    check(tiles.Select(t=>t.Asset).SequenceEqual(assets),"gallery preserves identity/order at "+width+" / "+size);
    check(rows.All(r=>r.Items.Sum(t=>t.Width+12)<=width+.01&&r.Items.All(t=>t.Width>0&&t.ImageHeight>0)),"gallery rows fit width at "+width+" / "+size);
    check(tiles.All(t=>Math.Abs((t.Width-4)/t.ImageHeight-GalleryLayout.Ratio(t.Asset))<.00001),"gallery aspect ratios retained at "+width+" / "+size);
   }
   check(assets[2].Selected&&assets.Count==12,"relayout leaves selected asset and original data unchanged");
   var last=GalleryLayout.Build(new[]{A(0,640,480)},1200,210)[0];check(last.Items[0].ImageHeight==210&&last.Items[0].Width<300,"sparse last row stays at requested size");
   check(GalleryLayout.Ratio(A(0,0,0))==1&&GalleryLayout.Ratio(A(1,100000,1))==3&&GalleryLayout.Ratio(A(2,1,100000))==.35,"unknown/extreme dimensions use bounded uncropped tile frames");
   var large=Enumerable.Range(0,10000).Select(i=>A(i,640+i%100,480)).ToList();var timer=System.Diagnostics.Stopwatch.StartNew();var largeRows=GalleryLayout.Build(large,1000,210);timer.Stop();
   check(largeRows.Sum(r=>r.Items.Count)==10000&&timer.ElapsedMilliseconds<1500,"10000-item metadata layout completes without image decoding");
   File.WriteAllText(Path.Combine(output,"layout-timing.txt"),"10000 assets layout: "+timer.ElapsedMilliseconds+" ms");
   var slider=new Slider{Minimum=120,Maximum=360,Value=210};var e=Wheel(120);
   check(!GalleryLayout.HandleWheel(slider,e,ModifierKeys.None)&&!e.Handled&&slider.Value==210,"normal wheel is left to list scrolling");
   e=Wheel(120);check(GalleryLayout.HandleWheel(slider,e,ModifierKeys.Control)&&e.Handled&&slider.Value==230,"Ctrl wheel up enlarges thumbnail size");
   e=Wheel(-120);GalleryLayout.HandleWheel(slider,e,ModifierKeys.Control);check(slider.Value==210&&e.Handled,"Ctrl wheel down shrinks thumbnail size");
   e=Wheel(30);GalleryLayout.HandleWheel(slider,e,ModifierKeys.Control);check(slider.Value==215,"high-resolution wheel uses proportional increments");
   e=Wheel(120000);GalleryLayout.HandleWheel(slider,e,ModifierKeys.Control);check(slider.Value==360&&e.Handled,"zoom upper limit consumes wheel without list scrolling");
   e=Wheel(-120000);GalleryLayout.HandleWheel(slider,e,ModifierKeys.Control);check(slider.Value==120&&e.Handled,"zoom lower limit consumes wheel without list scrolling");
   e=Wheel(0);check(!GalleryLayout.HandleWheel(slider,e,ModifierKeys.Control)&&!e.Handled,"zero wheel delta does not force a layout");
   string path=Path.Combine(output,"settings.json");new Settings{LibraryPath=output,Theme="light",ThumbnailSize=290}.SaveTo(path);var settings=Settings.LoadFrom(path);
   check(settings.ThumbnailSize==290&&settings.Theme=="light"&&settings.LibraryPath==output,"size preference survives restart with library/theme unchanged");
   settings.ThumbnailSize=9999;check(settings.ThumbnailSize==360,"oversize preference bounded");settings.ThumbnailSize=-10;check(settings.ThumbnailSize==120,"undersize preference bounded");
   var json=new System.Web.Script.Serialization.JavaScriptSerializer();
   File.WriteAllText(path,json.Serialize(new{LibraryPath=output,ThumbnailSize="broken",Theme="light"}));check(Settings.LoadFrom(path).ThumbnailSize==210&&Settings.LoadFrom(path).Theme=="light","malformed size falls back without losing theme");
   File.WriteAllText(path,json.Serialize(new{LibraryPath=output,Theme="light"}));check(Settings.LoadFrom(path).ThumbnailSize==210,"legacy preferences get larger default thumbnails");
   // Exercise real templates, never send input to the desktop.
   var app=Application.Current??new Application();var old=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;Window window=null;
   try{
    using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Mori.Main.xaml"))window=(Window)XamlReader.Load(stream);
    var content=(FrameworkElement)window.Content;window.Content=null;content.Resources.MergedDictionaries.Add(window.Resources);
    content.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty,window.FontFamily);content.SetValue(System.Windows.Documents.TextElement.FontSizeProperty,window.FontSize);content.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty,"Text");
    var gallery=(ListBox)window.FindName("Gallery");((StackPanel)window.FindName("Empty")).Visibility=Visibility.Collapsed;
    foreach(string theme in new[]{"dark","light"}){
     Theme.Set(theme);gallery.ItemsSource=GalleryLayout.Build(assets,885,210);content.Width=1380;content.Height=900;content.Measure(new Size(1380,900));content.Arrange(new Rect(0,0,1380,900));content.UpdateLayout();
     var tiles=Descendants<Border>(gallery).Where(b=>b.Tag is Asset).ToList();check(tiles.Count>0&&tiles.All(b=>b.Padding==new Thickness(0)),theme+" gallery uses image-first unpadded tiles");
     var selected=tiles.Single(b=>((Asset)b.Tag).Selected);check(selected.BorderBrush==Theme.Brush("Accent"),theme+" selection outline binds to underlying asset");
     check(tiles.All(b=>!MainWindow.IsGalleryBlank(b,gallery)),theme+" variable-width tiles remain selectable");
     check(Descendants<ThumbnailImage>(gallery).All(i=>i.DataContext is Asset&&i.Stretch==Stretch.Uniform),theme+" images receive assets without cropping");
     var sizeSlider=(Slider)window.FindName("ThumbnailSize");sizeSlider.ApplyTemplate();check(sizeSlider.Template.FindName("PART_Track",sizeSlider)!=null,theme+" size slider has working native track");
    }
    HeaderLayout(window,content,check);content.InvalidateMeasure();content.Measure(new Size(1380,900));content.Arrange(new Rect(0,0,1380,900));
    gallery.ItemsSource=GalleryLayout.Build(large,885,210);content.UpdateLayout();check(Descendants<Border>(gallery).Count(b=>b.Tag is Asset)<100,"10000-asset gallery materializes only visible/adjacent rows");
    check(VirtualizingPanel.GetIsVirtualizing(gallery)&&VirtualizingPanel.GetScrollUnit(gallery)==ScrollUnit.Pixel,"gallery retains virtualization and pixel scrolling");
    check(!window.IsVisible,"layout tests do not interrupt the desktop");
   }finally{if(window!=null)window.Close();Theme.Set("dark");app.ShutdownMode=old;}
  }
 }
}
