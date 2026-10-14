using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mori;

namespace MoriDesk {
 internal static class PdfContinuousTests {
  sealed class OffscreenInputSource:PresentationSource {
   public override Visual RootVisual {get;set;}
   public override bool IsDisposed {get{return false;}}
   protected override CompositionTarget GetCompositionTargetCore(){return null;}
  }
  static void Until(Func<bool> condition,string description,int milliseconds=20000){
   if(condition())return;var frame=new DispatcherFrame();var watch=System.Diagnostics.Stopwatch.StartNew();
   var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};timer.Tick+=delegate{if(condition()||watch.ElapsedMilliseconds>milliseconds)frame.Continue=false;};
   timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}if(!condition())throw new TimeoutException(description);
  }
  static void Await(Task pending){Until(()=>pending.IsCompleted,"continuous document opening timed out");pending.GetAwaiter().GetResult();}
  static bool Unlocked(string path){try{using(var exclusive=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))return true;}catch(IOException){return false;}}
  static void Arrange(PdfContinuousView view,double width=760,double height=900){view.Measure(new Size(width,height));view.Arrange(new Rect(0,0,width,height));view.UpdateLayout();}
  static void Save(PdfContinuousView view,string path,int width=760,int height=900){var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(view);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var output=File.Create(path))encoder.Save(output);}
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);string prior=Theme.Current;Theme.Set("dark");
   try{Geometry(check);Cache(check);Toolbar(check);View(root,check);}finally{Theme.Set(prior);}
  }
  static void Geometry(Action<bool,string> check){
   var sizes=new[]{new PdfPageSize(600,800),new PdfPageSize(1200,600),new PdfPageSize(300,300)};
   var layout=new PdfContinuousLayout(sizes,800,1);
   check(layout.Count==3&&layout.Width==800&&layout.Widths.All(x=>x==764),"continuous pages fit viewport width with one consistent inset");
   check(layout.Heights[0]>layout.Heights[2]&&layout.Heights[1]<layout.Heights[2],"portrait/landscape/square page aspect ratios remain distinct");
   check(layout.Tops[1]==layout.Tops[0]+layout.Heights[0]+PdfContinuousLayout.Gap&&layout.Tops[2]>layout.Tops[1]&&PdfContinuousLayout.Gap==8,"document pages use a narrow 8-pixel gap without inter-page numbering");
   foreach(int page in Enumerable.Range(0,3))check(layout.PageAt(layout.Tops[page]+1)==page,"binary offset lookup selects page "+page);
   check(layout.PageAt(-400)==0&&layout.PageAt(layout.Height+400)==2,"offset lookup is bounded at either document end");
   double original=layout.Tops[1]+layout.Heights[1]*.4;double fraction=layout.FractionAt(1,original);var zoomed=new PdfContinuousLayout(sizes,800,2);
   check(Math.Abs(zoomed.FractionAt(1,zoomed.OffsetAt(1,fraction))-fraction)<.000001&&zoomed.Width>800,"zoom anchor retains current page and within-page position");
   var longDoc=new PdfContinuousLayout(Enumerable.Range(0,20000).Select(x=>new PdfPageSize(600,800)).ToArray(),800,1);
   int[] visible=longDoc.Near(longDoc.Tops[18000],900);check(visible.Contains(18000)&&visible.Length<6&&!visible.Contains(0),"20,000-page document selects only viewport-near pages without scanning/rendering prior pages");
   bool invalid=false;try{new PdfContinuousLayout(new[]{new PdfPageSize(0,300)},800,1);}catch(NotSupportedException){invalid=true;}check(invalid,"invalid page geometry is rejected instead of creating non-finite WPF layout");
  }
  static void Cache(Action<bool,string> check){
   var cache=new PdfRasterCache();var bitmap=BitmapSource.Create(2048,2048,96,96,PixelFormats.Bgra32,null,new byte[2048*2048*4],2048*4);bitmap.Freeze();
   for(int i=0;i<12;i++)cache.Put(i,bitmap,2048);
   check(cache.Bytes<=PdfRasterCache.Budget&&cache.Count==3&&cache.Get(0)==null&&cache.Get(11)!=null,"page raster cache evicts least-recently-used entries within 48 MiB accounting budget");
   cache.Retain(new[]{11});check(cache.Count==1&&cache.Bytes==16L*1024*1024,"offscreen page entries are released immediately after scrolling away");
   cache.TrimPerPage(4L*1024*1024);check(cache.Count==0&&cache.Bytes==0,"cached high-resolution pages shrink admission when viewport needs more pages");
   cache.Put(0,bitmap,2048);cache.Clear();check(cache.Count==0&&cache.Bytes==0,"closing preview releases all cached page rasters");
  }
  static void Toolbar(Action<bool,string> check){
   foreach(string theme in new[]{"dark","light"}){
    Theme.Set(theme);var window=new DocumentPreviewWindow(null,Path.Combine(Path.GetTempPath(),"not-opened.pdf"),"Theme check",false);
    try{
     var panel=(System.Windows.Controls.DockPanel)window.Content;panel.Measure(new Size(520,360));panel.Arrange(new Rect(0,0,520,360));panel.UpdateLayout();
     var bar=panel.Children.OfType<System.Windows.Controls.WrapPanel>().Single();var buttons=bar.Children.OfType<System.Windows.Controls.Button>().ToArray();
     check(buttons.Length==8&&buttons.All(b=>((SolidColorBrush)b.Foreground).Color==((SolidColorBrush)Theme.Brush("Text")).Color&&((SolidColorBrush)b.Background).Color==((SolidColorBrush)Theme.Brush("Control")).Color),theme+" direct PDF/AI toolbar remains theme-aware without a main-window owner");
     check(((SolidColorBrush)panel.Background).Color==((SolidColorBrush)Theme.Brush("Canvas")).Color,theme+" document surface has an explicit opaque theme background");
     check(bar.DesiredSize.Width<=500&&bar.ActualHeight>=30,theme+" document toolbar stays within the minimum window content width");
     bar.Measure(new Size(300,1000));check(bar.DesiredSize.Width<=300&&bar.DesiredSize.Height>30,theme+" compact document toolbar wraps when its available width is constrained");
     var view=(PdfContinuousView)typeof(DocumentPreviewWindow).GetField("document",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(window);
     view.Scroller.Content=new System.Windows.Controls.Border{Width=1600,Height=2200};Arrange(view,320,240);
     var pan=(MiddleDragPan)typeof(PdfContinuousView).GetField("middlePan",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(view);
     // This host is never shown and its capture is disabled for the fixture;
     // execute the real window's Deactivated hook, not a direct End call.
     check(pan.Begin(new Point(100,100)),theme+" document window fixture begins a middle drag without desktop input");
     typeof(Window).GetMethod("OnDeactivated",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(window,new object[]{EventArgs.Empty});
     check(!pan.Dragging&&!view.Scroller.ForceCursor&&view.Scroller.Cursor==null,theme+" switching away from the document window restores cursor and ends middle drag");
    }finally{window.Close();}
   }
   Theme.Set("dark");
  }
  static void View(string root,Action<bool,string> check){
   string pdf=Path.Combine(root,"continuous.pdf"),ai=Path.Combine(root,"all-artboards.ai");byte[] original=PdfPreviewTests.Fixture(120);File.WriteAllBytes(pdf,original);File.WriteAllBytes(ai,PdfPreviewTests.Fixture(4));
   foreach(string path in new[]{pdf,ai}){
    using(var view=new PdfContinuousView(false)){
     Arrange(view);Await(view.OpenAsync(path,CancellationToken.None));Arrange(view);
     Until(()=>view.CachedPages>0,"first continuous page never rendered");
     check(view.PageCount==(path==pdf?120:4)&&view.VisibleTiles>1&&view.VisibleTiles<8,"PDF/AI has all-page metadata but only nearby page visuals: "+Path.GetExtension(path));
     var canvas=(System.Windows.Controls.Canvas)view.Scroller.Content;check(canvas.Children.OfType<System.Windows.Controls.Grid>().All(tile=>!tile.Children.OfType<System.Windows.Controls.TextBlock>().Any()),"PDF/AI page gaps contain no visible page-number labels");
     Until(()=>view.CachedPages==view.VisibleTiles,"nearby continuous pages never completed");
     check(view.CachedBytes<=PdfRasterCache.Budget,"actual PDF/AI raster cache fits budget: "+Path.GetExtension(path));
     Save(view,Path.Combine(root,Path.GetExtension(path).Substring(1)+"-waterfall-dark.png"));
     int last=view.PageCount-1;view.GoTo(last);view.UpdateLayout();Until(()=>view.LastVisiblePage==last&&view.AtBottom&&view.IsPageRendered(last),"continuous last-page navigation timed out");
     check(view.VisibleTiles<8&&view.CachedBytes<=PdfRasterCache.Budget,"jump to final PDF/AI page evicts old pages without rendering intervening pages");
     double bottom=view.Scroller.VerticalOffset;view.Move(-1);view.UpdateLayout();Until(()=>view.Scroller.VerticalOffset<bottom,"previous-page control stuck at document bottom");check(true,"previous page remains reachable when multiple last pages fit inside viewport");
     view.GoTo(1);view.UpdateLayout();Until(()=>view.CurrentPage==1,"second-page anchor failed");view.SetZoom(2);view.UpdateLayout();Until(()=>view.CurrentPage==1,"zoom changed anchored page");
     check(view.ZoomFactor==2&&view.Scroller.ScrollableWidth>0,"continuous zoom preserves page and permits horizontal detail scrolling");
     var pan=(MiddleDragPan)typeof(PdfContinuousView).GetField("middlePan",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(view);
     check(pan.Begin(new Point(100,100)),"PDF/AI middle drag can begin at the real zoomed document extent without desktop capture");view.SetZoom(3);view.UpdateLayout();double zoomOffset=view.Scroller.VerticalOffset;pan.MoveTo(new Point(101,100));view.UpdateLayout();
     check(!pan.Dragging&&view.Scroller.VerticalOffset==zoomOffset,"zoom cancels old middle-drag origin so later movement cannot jump back");
     check(pan.Begin(new Point(100,100)),"PDF/AI can start a fresh middle drag after zoom");view.GoTo(2);view.UpdateLayout();double pageOffset=view.Scroller.VerticalOffset;pan.MoveTo(new Point(101,100));view.UpdateLayout();
     check(!pan.Dragging&&view.Scroller.VerticalOffset==pageOffset,"page navigation ends middle drag instead of restoring the previous page offset");
     check(pan.Begin(new Point(100,100)),"PDF/AI can start a fresh middle drag after navigation");Arrange(view,700,860);Until(()=>!pan.Dragging,"resize left old middle-drag origin active");check(!pan.Dragging,"viewport reflow releases middle drag before rebuilding page geometry");
     check(pan.Begin(new Point(100,100)),"PDF/AI can start a fresh middle drag before wheel navigation");var wheel=new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,120){RoutedEvent=System.Windows.UIElement.PreviewMouseWheelEvent};view.RaiseEvent(wheel);check(!pan.Dragging,"wheel navigation cancels old middle-drag origin without sending desktop input");
     foreach(var key in new[]{System.Windows.Input.Key.Home,System.Windows.Input.Key.End,System.Windows.Input.Key.Up,System.Windows.Input.Key.Down,System.Windows.Input.Key.Left,System.Windows.Input.Key.Right,System.Windows.Input.Key.PageUp,System.Windows.Input.Key.PageDown,System.Windows.Input.Key.Space}){
      check(pan.Begin(new Point(100,100)),"PDF/AI begins a held drag before keyboard navigation / "+key);
      var navigation=new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,new OffscreenInputSource(),Environment.TickCount,key){RoutedEvent=System.Windows.UIElement.PreviewKeyDownEvent};
      // A memory-only source satisfies the routed event contract without
      // creating a native window or sending input. Then apply native scroll.
      view.RaiseEvent(navigation);view.Scroller.ScrollToVerticalOffset(0);view.UpdateLayout();pan.MoveTo(new Point(101,99));view.UpdateLayout();check(!pan.Dragging&&!navigation.Handled&&view.Scroller.VerticalOffset==0,"keyboard navigation releases the drag without swallowing keys or restoring old offsets / "+key);
     }
     view.SetZoom(1);view.GoTo(0);view.UpdateLayout();Arrange(view,400,520);Until(()=>view.CachedPages>0,"narrow continuous layout did not render");
     check(view.VisibleTiles>0&&view.CachedBytes<=PdfRasterCache.Budget&&view.Scroller.ScrollableWidth<1,"narrow fit-width viewport stays usable without unnecessary horizontal scrolling");
     view.SetZoom(99);check(view.ZoomFactor==4,"continuous zoom maximum bounded");view.SetZoom(.001);check(view.ZoomFactor==.5,"continuous zoom minimum bounded");
    }
    Until(()=>Unlocked(path),"closed continuous source stayed locked");check(true,"closing continuous viewer releases source PDF/AI lock");
   }
   string mixed=Path.Combine(root,"mixed-pages.pdf");int pageNumber=0;
   string content=Regex.Replace(Encoding.ASCII.GetString(PdfPreviewTests.Fixture(6)),@"/MediaBox \[0 0 320 180\]",delegate(Match match){int page=pageNumber++;return page%2==0?"/MediaBox [0 0 600 800]":"/MediaBox [0 0 800 400]";});File.WriteAllText(mixed,content,Encoding.ASCII);
   Theme.Set("light");
   using(var view=new PdfContinuousView()){Arrange(view);Await(view.OpenAsync(mixed,CancellationToken.None));Arrange(view);Until(()=>view.CachedPages>0,"mixed-page continuous document failed");view.GoTo(1);view.UpdateLayout();Until(()=>view.CurrentPage==1&&view.CachedPages==view.VisibleTiles,"mixed-page navigation failed");Save(view,Path.Combine(root,"mixed-waterfall-light.png"));check(view.PageCount==6&&view.CachedBytes<=PdfRasterCache.Budget&&((SolidColorBrush)view.Background).Color==((SolidColorBrush)Theme.Brush("Canvas")).Color,"mixed aspect-ratio PDF remains readable in light theme");view.GoTo(5);view.SetZoom(3);}
   Until(()=>Unlocked(mixed),"closed mixed-page source stayed locked");check(true,"close during queued scrolling/zoom releases original file");
   check(File.ReadAllBytes(pdf).SequenceEqual(original),"continuous preview does not modify or copy source document bytes");
   string boundary=Path.Combine(root,"height-boundary.pdf");File.WriteAllBytes(boundary,PdfPreviewTests.Fixture(1,500,590));
   using(var view=new PdfContinuousView()){Arrange(view);Await(view.OpenAsync(boundary,CancellationToken.None));Arrange(view);Until(()=>view.IsPageRendered(0),"height-boundary page never stabilised");double width=view.Scroller.ViewportWidth;Await(Task.Delay(220));view.UpdateLayout();check(Math.Abs(view.Scroller.ViewportWidth-width)<.01&&view.CachedPages==1,"fit-width one-page boundary reserves scrollbar width without layout oscillation");}
   using(var canceled=new PdfContinuousView()){Arrange(canceled);var task=canceled.OpenAsync(pdf,CancellationToken.None);canceled.Dispose();bool cancellation=false;try{Await(task);}catch(OperationCanceledException){cancellation=true;}check(cancellation,"closing while native document opens cancels without adopting leaked source session");}
   Until(()=>Unlocked(pdf),"cancelled continuous opening kept the original file locked");check(true,"closing during native opening releases the source lock after bounded asynchronous cleanup");
  }
 }
}
