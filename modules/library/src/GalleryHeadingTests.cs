using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Mori {
 public static class GalleryHeadingTests {
  [StructLayout(LayoutKind.Sequential)]struct NativeRect {internal int Left,Top,Right,Bottom;}
  [DllImport("user32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]static extern bool GetClientRect(IntPtr window,out NativeRect rectangle);
  [DllImport("user32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]static extern bool GetWindowRect(IntPtr window,out NativeRect rectangle);
  [DllImport("user32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
  static Rect Bounds(FrameworkElement element,Visual root){return element.TransformToAncestor(root).TransformBounds(new Rect(new Point(),element.RenderSize));}
  static Window Load(){using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Mori.Main.xaml"))return (Window)XamlReader.Load(stream);}
  static FrameworkElement Detach(Window window){
   var content=(FrameworkElement)window.Content;window.Content=null;content.Resources.MergedDictionaries.Add(window.Resources);
   content.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty,window.FontFamily);content.SetValue(System.Windows.Documents.TextElement.FontSizeProperty,window.FontSize);
   content.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty,"Text");return content;
  }
  static void MeasureDetached(FrameworkElement content,int width){
   // An offscreen root has no PresentationSource to schedule another arrange
   // after a child changes. Measure the actual full page, never a header clone.
   content.Width=width;content.Height=680;content.InvalidateMeasure();content.InvalidateArrange();content.Measure(new Size(width,680));content.Arrange(new Rect(0,0,width,680));content.UpdateLayout();
  }
  static bool Hit(FrameworkElement root,FrameworkElement control){
   // Geometric hit testing also works for the detached wide-screen tree. Keep
   // the real visibility, enabled and hit-test policy along the entire path.
   var point=control.TranslatePoint(new Point(control.ActualWidth/2,control.ActualHeight/2),root);DependencyObject target=null;
   VisualTreeHelper.HitTest(root,delegate(DependencyObject node){var ui=node as UIElement;return ui!=null&&(ui.Visibility!=Visibility.Visible||!ui.IsHitTestVisible)?HitTestFilterBehavior.ContinueSkipSelfAndChildren:HitTestFilterBehavior.Continue;},delegate(HitTestResult result){target=result.VisualHit;return HitTestResultBehavior.Stop;},new PointHitTestParameters(point));
   bool found=false;for(var current=target;current!=null;current=MainWindow.SelectionParent(current)){var ui=current as UIElement;if(ui!=null&&(ui.Visibility!=Visibility.Visible||!ui.IsHitTestVisible||!ui.IsEnabled))return false;if(object.ReferenceEquals(current,control))found=true;}return found;
  }
  static void Configure(Window window,int width,bool filtering){
   ((Grid)window.FindName("Workspace")).ColumnDefinitions[0].Width=new GridLength(width==1040?296:208);
   ((TextBlock)window.FindName("ViewTitle")).Text=filtering?"一个很长的分类名称：图片与视频参考":"全部素材";
   ((TextBlock)window.FindName("ViewCount")).Text="657 个素材 · 483.5 MB";
   ((Button)window.FindName("ClearFilter")).Visibility=filtering?Visibility.Visible:Visibility.Collapsed;
  }
  static void Drain(){
   // This is a real WPF layout/render queue, not a timed sleep or manually
   // invalidated detached Grid. It runs only in the VM regression process.
   var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);
  }
  static void SizeHostedClient(Window window,int width,int height){
   // WPF's initial Show clamps top-level tracking width to the VM monitor
   // (1044px on its 1024px display). Resize the actual HWND client after Show,
   // never the child root: WM_SIZE must drive the normal WPF measure/arrange.
   var source=PresentationSource.FromVisual(window);var handle=new WindowInteropHelper(window).Handle;NativeRect client,outer;
   if(source==null||source.CompositionTarget==null||handle==IntPtr.Zero||!GetClientRect(handle,out client)||!GetWindowRect(handle,out outer))throw new InvalidOperationException("Cannot measure the VM heading test's native window.");
   var requested=source.CompositionTarget.TransformToDevice.Transform(new Vector(width,height));
   int outerWidth=checked((int)Math.Round(requested.X)+outer.Right-outer.Left-(client.Right-client.Left));
   int outerHeight=checked((int)Math.Round(requested.Y)+outer.Bottom-outer.Top-(client.Bottom-client.Top));
   if(!SetWindowPos(handle,IntPtr.Zero,0,0,outerWidth,outerHeight,0x0016))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Cannot size the VM heading test's native client.");
  }
  static void Verify(Window window,FrameworkElement content,int width,bool hosted,string label,Action<bool,string> check){
   var center=(Grid)window.FindName("Center");var header=(Grid)window.FindName("GalleryHeader");var heading=(Grid)window.FindName("GalleryHeading");var toolbar=(StackPanel)window.FindName("GalleryToolbar");var toggle=(Button)window.FindName("ToggleInspector");var title=(TextBlock)window.FindName("ViewTitle");var count=(TextBlock)window.FindName("ViewCount");var media=(ComboBox)window.FindName("MediaFilter");var sort=(ComboBox)window.FindName("Sort");
   double expectedCenter=width-(width==1040?296:208)-6-252-6;
   bool rootState=hosted?window.IsVisible&&object.ReferenceEquals(window.Content,content)&&PresentationSource.FromVisual(content)!=null:!window.IsVisible&&VisualTreeHelper.GetParent(content)==null&&PresentationSource.FromVisual(content)==null;
   check(rootState&&Math.Abs(content.ActualWidth-width)<.5&&Math.Abs(center.ActualWidth-expectedCenter)<.5&&Math.Abs(header.ActualWidth-(expectedCenter-header.Margin.Left-header.Margin.Right))<.5,label+"whole-page root and header honor the requested width before geometry assertions (root="+content.ActualWidth+", center="+center.ActualWidth+", header="+header.ActualWidth+")");
   check(content.IsMeasureValid&&content.IsArrangeValid&&header.IsMeasureValid&&header.IsArrangeValid&&title.IsMeasureValid&&title.IsArrangeValid&&count.IsMeasureValid&&count.IsArrangeValid,label+"geometry is observed after the real root, header, title and count have completed layout");
   check(Math.Abs(header.ActualHeight-64)<.5&&Math.Abs(center.RowDefinitions[0].ActualHeight-78)<.5&&Math.Abs(((ListBox)window.FindName("Gallery")).TranslatePoint(new Point(),center).Y-78)<.5,label+"two compact header rows reserve their measured height before the gallery");
   double titleY=title.TranslatePoint(new Point(),heading).Y+title.ActualHeight/2,countY=count.TranslatePoint(new Point(),heading).Y+count.ActualHeight/2;
   check(Math.Abs(titleY-countY)<1&&title.FontSize==14&&title.ActualHeight<=24&&count.ActualHeight<=24,label+"title and count remain one compact vertically aligned line");
   var headingBounds=Bounds(heading,header);var toolbarBounds=Bounds(toolbar,header);var toggleBounds=Bounds(toggle,header);
   bool separated=headingBounds.Right<=toggleBounds.Left+.5&&headingBounds.Bottom<=toolbarBounds.Top+.5&&Bounds(title,heading).Right<=Bounds(count,heading).Left+.5&&Bounds(count,heading).Right<=heading.ActualWidth+.5&&Bounds(media,toolbar).Right<=Bounds(sort,toolbar).Left+.5&&Bounds(sort,header).Right<=header.ActualWidth+.5&&media.ActualWidth>=94&&sort.ActualWidth>=109&&title.ActualWidth>20&&title.ActualWidth<=200.5;
   check(separated,label+"long titles, details toggle and second-row filters remain separated in minimum window"+(separated?"":" (heading="+headingBounds+", title="+Bounds(title,heading)+", count="+Bounds(count,heading)+", toggle="+toggleBounds+", toolbar="+toolbarBounds+", media="+Bounds(media,toolbar)+", sort="+Bounds(sort,header)+")"));
   var clear=(Button)window.FindName("ClearFilter");check(Hit(content,toggle)&&Hit(content,media)&&Hit(content,sort)&&(clear.Visibility!=Visibility.Visible||Hit(content,clear)),label+"details, type and sort controls, and the visible filter-clear action are hit-testable at their displayed positions");
   check(title.ToolTip.ToString()==title.Text&&count.ToolTip.ToString()==count.Text,label+"full category name and count retained in tooltips");
  }
  static void Render(Window window,FrameworkElement content,int width,string path){
   var center=(Grid)window.FindName("Center");var bitmap=new RenderTargetBitmap(width,680,96,96,PixelFormats.Pbgra32);bitmap.Render(content);
   var origin=center.TransformToAncestor(content).Transform(new Point());var crop=new CroppedBitmap(bitmap,new Int32Rect((int)origin.X,(int)origin.Y,(int)center.ActualWidth,(int)Math.Ceiling(center.RowDefinitions[0].ActualHeight)));
   var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(crop));using(var file=File.Create(path))encoder.Save(file);
  }
  public static void Run(string output,Action<bool,string> check){
   Directory.CreateDirectory(output);var app=Application.Current??new Application();var shutdown=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
   try{
    // Independent static states cover both widths. The second pass retains
    // real hosted invalidation at minimum width and full-width offscreen
    // short/long/restored transitions regardless of the VM monitor resolution.
    foreach(string theme in new[]{"dark","light"})foreach(int width in new[]{1040,1380})foreach(bool filtering in new[]{false,true}){
     Theme.Set(theme);var window=Load();
     try{
      var content=Detach(window);foreach(object key in app.Resources.Keys)content.Resources[key]=app.Resources[key];content.SetResourceReference(Panel.BackgroundProperty,"Canvas");Configure(window,width,filtering);
      MeasureDetached(content,width);
      Verify(window,content,width,false,theme+" / "+width+" / filter "+filtering+": ",check);
      Render(window,content,width,Path.Combine(output,theme+"-"+width+(filtering?"-long-filtered":"")+".png"));
     }finally{window.Close();}
    }
    foreach(string theme in new[]{"dark","light"})foreach(int width in new[]{1040,1380}){
     Theme.Set(theme);var window=Load();
     try{
      bool hosted=width==1040;FrameworkElement content;
      if(hosted){
       // Preserve the actual HWND-driven short/long/restored layout path at
       // minimum width; its width fits the VM's 1044px tracking limit.
       window.Title="Nook · 标题重排测试";window.WindowStyle=WindowStyle.None;window.ResizeMode=ResizeMode.NoResize;window.WindowStartupLocation=WindowStartupLocation.Manual;window.Left=24;window.Top=24;window.Width=width;window.Height=680;window.ShowInTaskbar=false;window.ShowActivated=false;
       content=(FrameworkElement)window.Content;Configure(window,width,false);window.Show();SizeHostedClient(window,width,680);Drain();
      }else{
       // Windows clamps a 1380px top-level HWND to 1044px on the VM's 1024px
       // display even after SetWindowPos. Use the real content tree at the
       // requested client width instead of weakening the width assertion.
       content=Detach(window);foreach(object key in app.Resources.Keys)content.Resources[key]=app.Resources[key];content.SetResourceReference(Panel.BackgroundProperty,"Canvas");Configure(window,width,false);MeasureDetached(content,width);
      }
      string[] phases={"short-before","long-filtered","short-restored"};
      for(int i=0;i<phases.Length;i++){
       if(i>0){Configure(window,width,i==1);if(hosted)Drain();else MeasureDetached(content,width);}
       string mode=hosted?"hosted":"offscreen-transition";
       Verify(window,content,width,hosted,theme+" / "+width+" / "+mode+" "+phases[i]+": ",check);
       Render(window,content,width,Path.Combine(output,theme+"-"+width+"-"+mode+"-"+phases[i]+".png"));
      }
     }finally{window.Close();Drain();}
     }
   }finally{Theme.Set("dark");app.ShutdownMode=shutdown;}
  }
 }
}
