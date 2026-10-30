using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Mori {
 // Page geometry is inexpensive; page images and WPF visuals exist only near
 // the viewport. Long documents never allocate a bitmap for every page.
 internal sealed class PdfContinuousLayout {
  internal const double Gap=8,Inset=18;
  internal readonly double[] Tops,Widths,Heights;
  internal readonly double Width,Height;
  internal int Count {get{return Tops.Length;}}
  internal PdfContinuousLayout(PdfPageSize[] sizes,double viewportWidth,double zoom){
   Tops=new double[sizes.Length];Widths=new double[sizes.Length];Heights=new double[sizes.Length];
   double pageWidth=Math.Max(120,viewportWidth-2*Inset)*zoom,top=Inset;
   Width=Math.Max(viewportWidth,pageWidth+2*Inset);
   for(int i=0;i<sizes.Length;i++){
    if(sizes[i].Width<=0||sizes[i].Height<=0||double.IsNaN(sizes[i].Width)||double.IsNaN(sizes[i].Height)||double.IsInfinity(sizes[i].Width)||double.IsInfinity(sizes[i].Height))throw new NotSupportedException("页面尺寸无效，无法连续预览。");
    Tops[i]=top;Widths[i]=pageWidth;Heights[i]=pageWidth*sizes[i].Height/sizes[i].Width;
    top+=Heights[i]+Gap;
   }
   Height=Math.Max(1,top);
  }
  internal int PageAt(double offset){
   if(Count==0)return -1;int lo=0,hi=Count-1;
   while(lo<hi){int mid=lo+(hi-lo+1)/2;if(Tops[mid]<=offset)lo=mid;else hi=mid-1;}
   return lo;
  }
  internal int[] Near(double offset,double viewportHeight){
   if(Count==0)return new int[0];double padding=Math.Min(400,Math.Max(80,viewportHeight*.35));
   int first=PageAt(Math.Max(0,offset-padding));int last=PageAt(offset+viewportHeight+padding);
   return Enumerable.Range(first,last-first+1).ToArray();
  }
  internal double FractionAt(int page,double offset){return Math.Max(0,Math.Min(1,(offset-Tops[page])/Math.Max(1,Heights[page]+Gap)));}
  internal double OffsetAt(int page,double fraction){return Tops[page]+fraction*(Heights[page]+Gap);}
 }
 internal sealed class PdfRasterCache {
  internal const long Budget=48L*1024*1024;
  internal const int MaximumPages=1024;
  internal sealed class Entry {internal BitmapSource Bitmap;internal int Dimension;internal long Bytes,Touch;}
  readonly Dictionary<int,Entry> entries=new Dictionary<int,Entry>();long clock;
  internal long Bytes {get;private set;}
  internal int Count {get{return entries.Count;}}
  internal Entry Get(int page){Entry value;if(!entries.TryGetValue(page,out value))return null;value.Touch=++clock;return value;}
  internal void Put(int page,BitmapSource bitmap,int dimension){
   Remove(page);long bytes=(long)bitmap.PixelWidth*bitmap.PixelHeight*((bitmap.Format.BitsPerPixel+7)/8);
   entries[page]=new Entry{Bitmap=bitmap,Dimension=dimension,Bytes=bytes,Touch=++clock};Bytes+=bytes;
   while(Bytes>Budget||Count>MaximumPages)Remove(entries.OrderBy(x=>x.Value.Touch).First().Key);
  }
  internal void Retain(IEnumerable<int> pages){var keep=new HashSet<int>(pages);foreach(int key in entries.Keys.ToArray())if(!keep.Contains(key))Remove(key);}
  internal void TrimPerPage(long allowance){foreach(var pair in entries.ToArray())if(pair.Value.Bytes>allowance+4096)Remove(pair.Key);}
  void Remove(int page){Entry entry;if(entries.TryGetValue(page,out entry)){Bytes-=entry.Bytes;entries.Remove(page);}}
  internal void Clear(){entries.Clear();Bytes=0;}
 }
 internal sealed class PdfContinuousView:Grid,IDisposable {
  sealed class Tile {internal Grid Root;internal Image Image;internal TextBlock Message;internal PdfTextSelection Text;}
  // Reserve the vertical scrollbar width even for one page: fit-width must
  // not oscillate as an Auto scrollbar appears and disappears at a boundary.
  readonly ScrollViewer scroll=new ScrollViewer{HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Visible,CanContentScroll=false,Focusable=true};
  readonly MiddleDragPan middlePan;
  readonly Canvas canvas=new Canvas();readonly Dictionary<int,Tile> tiles=new Dictionary<int,Tile>();
  readonly PdfRasterCache cache=new PdfRasterCache();readonly Dictionary<int,string> failures=new Dictionary<int,string>();
  readonly PdfTextCache textCache=new PdfTextCache();readonly Dictionary<int,string> textFailures=new Dictionary<int,string>();
  readonly HashSet<int> memoryLimitedText=new HashSet<int>();
  readonly DispatcherTimer settle=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(90)};
  readonly CancellationTokenSource lifetime=new CancellationTokenSource();
  PdfPreviewSession session;PdfPageSize[] sizes;PdfContinuousLayout layout;CancellationTokenSource rendering;
  PdfTextSession textSession;CancellationTokenSource openingText,extracting;string sourcePath,selectedText="";bool selectText,textPumping,textRefreshPending;int extractingPage=-1,selectedPage=-1;
  bool closed,pumping,arranging,refreshPending;int[] wanted=new int[0];int renderingPage=-1,renderingDimension,currentPage;
  double zoom=1,lastViewportWidth;string idleNotice;
  bool fitPage;
  int[] thumbnailPages=new int[0];
  readonly Dictionary<int,BitmapSource> thumbnails=new Dictionary<int,BitmapSource>();
  readonly HashSet<int> thumbnailFailures=new HashSet<int>();
  internal event Action ThumbnailChanged;
  internal event Action ZoomChanged;
  internal int ThumbnailCount {get{return thumbnails.Count;}}
  internal BitmapSource Thumbnail(int page){BitmapSource value;return thumbnails.TryGetValue(page,out value)?value:null;}
  internal void SetThumbnailPages(int[] pages){
   if(closed)return;var next=pages.Where(p=>p>=0&&p<PageCount).Distinct().Take(12).ToArray();if(thumbnailPages.SequenceEqual(next))return;
   thumbnailPages=next;foreach(int p in thumbnails.Keys.ToArray())if(!next.Contains(p))thumbnails.Remove(p);thumbnailFailures.IntersectWith(next);
   refreshPending=true;settle.Stop();settle.Start();
  }
  // Windows.Data.Pdf already reports page dimensions in 96-DPI DIPs.
  internal double DisplayZoom {get{return sizes==null||layout==null?1:layout.Widths[currentPage]/sizes[currentPage].Width;}}
  internal void SetDisplayZoom(double value){if(sizes==null||layout==null)return;SetZoom(zoom*value/DisplayZoom);}
  internal void FitWidth(){fitPage=false;SetZoom(1);}
  internal void FitPage(){fitPage=true;ArrangePages(true);RefreshViewport();}
  internal event Action<int,int> PageChanged;
  internal event Action<string> StatusChanged;
  internal event Action<string> SelectionChanged;
  internal bool TextSelectionEnabled {get{return selectText;}}
  internal string SelectedText {get{return selectedText;}}
  internal int TextCachedPages {get{return textCache.Count;}}
  internal long TextCachedBytes {get{return textCache.Bytes;}}
  internal bool IsTextReady(int page){return textCache.Get(page)!=null;}
  internal bool IsTextMemoryLimited(int page){return memoryLimitedText.Contains(page);}
  internal bool TextWorkActive {get{return textPumping;}}
  internal int PageCount {get{return sizes==null?0:sizes.Length;}}
  internal int CurrentPage {get{return currentPage;}}
  internal int LastVisiblePage {get{return layout==null?0:layout.PageAt(scroll.VerticalOffset+Math.Max(1,scroll.ViewportHeight)-1);}}
  internal bool AtBottom {get{return scroll.VerticalOffset>=scroll.ScrollableHeight-.5;}}
  internal bool IsPageRendered(int page){return cache.Get(page)!=null;}
  internal double ZoomFactor {get{return zoom;}}
  internal long CachedBytes {get{return cache.Bytes;}}
  internal int CachedPages {get{return cache.Count;}}
  internal int VisibleTiles {get{return tiles.Count;}}
  internal ScrollViewer Scroller {get{return scroll;}}
  internal void EndMiddleDrag(){middlePan.End();}
  internal PdfContinuousView(bool captureMiddleMouse=true){
   SetResourceReference(BackgroundProperty,"Canvas");scroll.Content=canvas;Children.Add(scroll);middlePan=new MiddleDragPan(scroll,captureMiddleMouse);
   scroll.ScrollChanged+=delegate(object sender,ScrollChangedEventArgs e){if(closed||arranging)return;if((e.ViewportWidthChange!=0&&Math.Abs(scroll.ViewportWidth-lastViewportWidth)>1)||(fitPage&&e.ViewportHeightChange!=0))ArrangePages(true);RefreshViewport();};
   settle.Tick+=delegate{settle.Stop();Pump();PumpText();};
   PreviewMouseWheel+=delegate(object sender,MouseWheelEventArgs e){middlePan.End();if((Keyboard.Modifiers&ModifierKeys.Control)==0)return;SetZoom(zoom*(e.Delta>0?1.15:1/1.15));e.Handled=true;};
   // Native ScrollViewer keyboard navigation also changes its offset. End a
   // held drag first so the next pointer move cannot restore the old origin.
   PreviewKeyDown+=delegate(object sender,KeyEventArgs e){if(e.Key==Key.Home||e.Key==Key.End||e.Key==Key.Up||e.Key==Key.Down||e.Key==Key.Left||e.Key==Key.Right||e.Key==Key.PageUp||e.Key==Key.PageDown||e.Key==Key.Space)middlePan.End();};
  }
  internal async Task OpenAsync(string path,CancellationToken cancellation){
   using(var pending=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,cancellation)){
    var opened=await PdfPreview.OpenAsync(path,pending.Token).ConfigureAwait(false);
    // Explicit dispatcher adoption also works for standalone/offscreen hosts
    // that have not installed a SynchronizationContext before opening a file.
    bool adopted=false;
    try{await Dispatcher.InvokeAsync(delegate{
      if(closed||pending.IsCancellationRequested){pending.Token.ThrowIfCancellationRequested();return;}
      session=opened;adopted=true;sourcePath=path;sizes=new PdfPageSize[opened.PageCount];for(int i=0;i<sizes.Length;i++)sizes[i]=opened.GetPageSize(i);
      idleNotice=string.Equals(System.IO.Path.GetExtension(path),".ai",StringComparison.OrdinalIgnoreCase)?"AI 已导出页面 · 中键拖动 · Ctrl＋滚轮缩放 · 图层请用 Illustrator":"连续滚动 · 中键拖动 · Ctrl＋滚轮缩放";
      ArrangePages(false);RefreshViewport();
     });
    }finally{if(!adopted)opened.Dispose();}
   }
  }
  void Status(string value){if(StatusChanged!=null)StatusChanged(value);}
  internal void SetTextSelection(bool value){
   if(closed||selectText==value)return;selectText=value;
   if(!value){if(openingText!=null)openingText.Cancel();if(extracting!=null)extracting.Cancel();ClearSelection();}
   foreach(var tile in tiles.Values)tile.Text.SetActive(value&&tile.Image.Source!=null);
   if(value){RetryMemoryLimitedText();textRefreshPending=true;settle.Stop();settle.Start();}Status(TextNotice());
  }
  void Selection(int page,string value){selectedPage=string.IsNullOrEmpty(value)?-1:page;selectedText=value??"";if(SelectionChanged!=null)SelectionChanged(selectedText);if(selectText)Status(TextNotice());}
  internal void ClearSelection(){foreach(var tile in tiles.Values)tile.Text.Clear();Selection(-1,"");}
  internal void SelectPageText(){Tile tile;if(!selectText||!tiles.TryGetValue(currentPage,out tile)||tile.Text.Page==null)return;ClearSelection();tile.Text.Select(0,tile.Text.Page.Words.Length-1);}
  internal void SelectTextForTest(int page,int first,int last){Tile tile;if(!tiles.TryGetValue(page,out tile))return;ClearSelection();tile.Text.Select(first,last);}
  string TextNotice(){
   if(!selectText)return idleNotice;
   if(!string.IsNullOrEmpty(selectedText))return "已选 "+selectedText.Length+" 字 · Ctrl+C 复制 · 拖选仅限同一页";
   string failure;if(textFailures.TryGetValue(currentPage,out failure))return failure;
   var page=textCache.Get(currentPage);if(page==null)return "正在读取当前页文字层… · 原页面仍可正常浏览";
   return page.Words.Length==0?"此页没有可选文字层（可能是扫描件或文字轮廓）· 请用截图翻译":"拖动选择本页文字 · Ctrl+C 复制 · 选中后可翻译";
  }
  internal void Move(int delta){if(layout==null)return;GoTo(Math.Max(0,Math.Min(PageCount-1,currentPage+delta)));}
  internal void GoTo(int page){if(layout==null||page<0||page>=PageCount)return;middlePan.End();if(fitPage)ArrangePages(false,page);scroll.ScrollToVerticalOffset(Math.Max(0,layout.Tops[page]-PdfContinuousLayout.Inset));RefreshViewport();}
  internal void SetZoom(double value){if(closed||double.IsNaN(value)||double.IsInfinity(value))return;fitPage=false;double next=Math.Max(.05,Math.Min(4,value));if(Math.Abs(next-zoom)<.001)return;zoom=next;RetryMemoryLimitedText();ArrangePages(true);RefreshViewport();}
  void RetryMemoryLimitedText(){foreach(int page in memoryLimitedText)textFailures.Remove(page);memoryLimitedText.Clear();}
  void ArrangePages(bool preserve,int fitTarget=-1){
   if(sizes==null||closed)return;middlePan.End();int page=layout==null?0:layout.PageAt(scroll.VerticalOffset);double fraction=layout==null?0:layout.FractionAt(page,scroll.VerticalOffset);
   double horizontal=layout==null?0:(scroll.HorizontalOffset+scroll.ViewportWidth/2)/Math.Max(1,layout.Width);
   double width=scroll.ViewportWidth>1?scroll.ViewportWidth:Math.Max(300,ActualWidth-18);lastViewportWidth=width;
   int fitted=fitTarget>=0?fitTarget:page;
   if(fitPage&&sizes.Length>0)zoom=Math.Max(.05,Math.Min(1,(Math.Max(80,scroll.ViewportHeight)-2*PdfContinuousLayout.Inset)/(Math.Max(120,width-2*PdfContinuousLayout.Inset)*sizes[fitted].Height/sizes[fitted].Width)));
   layout=new PdfContinuousLayout(sizes,width,zoom);arranging=true;
   canvas.Width=layout.Width;canvas.Height=layout.Height;
   foreach(var pair in tiles)Place(pair.Key,pair.Value);
   if(preserve){scroll.ScrollToVerticalOffset(layout.OffsetAt(page,fraction));scroll.ScrollToHorizontalOffset(Math.Max(0,horizontal*layout.Width-scroll.ViewportWidth/2));}
   arranging=false;if(ZoomChanged!=null)ZoomChanged();
  }
  Tile CreateTile(int page){
   var image=new Image{Stretch=Stretch.Fill};RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality);
   var message=new TextBlock{Text="正在读取第 "+(page+1)+" 页…",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(18),VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center,Foreground=new SolidColorBrush(Color.FromRgb(95,103,105)),MaxWidth=480};
   var text=new PdfTextSelection();text.Changed+=delegate(PdfTextSelection sender,string value){if(!string.IsNullOrEmpty(value)||selectedPage==page)Selection(page,value);};
   text.Activated+=delegate(PdfTextSelection sender){foreach(var tile in tiles.Values)if(!ReferenceEquals(tile.Text,sender))tile.Text.Clear();};
   var sheet=new Grid{Background=Brushes.White};sheet.Children.Add(image);sheet.Children.Add(message);sheet.Children.Add(text);
   var root=new Grid();root.RowDefinitions.Add(new RowDefinition());root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(PdfContinuousLayout.Gap)});root.Children.Add(sheet);
   System.Windows.Automation.AutomationProperties.SetName(root,"第 "+(page+1)+" 页");canvas.Children.Add(root);return new Tile{Root=root,Image=image,Message=message,Text=text};
  }
  void Place(int page,Tile tile){tile.Root.Width=layout.Widths[page];tile.Root.Height=layout.Heights[page]+PdfContinuousLayout.Gap;Canvas.SetLeft(tile.Root,(layout.Width-layout.Widths[page])/2);Canvas.SetTop(tile.Root,layout.Tops[page]);}
  int Dimension(int page){
   double width=sizes[page].Width,height=sizes[page].Height,aspect=Math.Min(width,height)/Math.Max(width,height);
   // The budget includes every nearby page, including very short/wide pages.
   // Leave one quarter free for the in-flight raster and native render buffers.
   double allowance=PdfRasterCache.Budget*.75/Math.Max(3,wanted.Length);
   int budget=(int)Math.Min(PdfPreview.MaximumDimension,Math.Sqrt(allowance/(4*Math.Max(1e-8,aspect))));
   var transform=PresentationSource.FromVisual(this);double dpi=transform==null||transform.CompositionTarget==null?1:transform.CompositionTarget.TransformToDevice.M11;
   int display=(int)Math.Min(PdfPreview.MaximumDimension,Math.Ceiling(Math.Max(layout.Widths[page],layout.Heights[page])*dpi));
   return Math.Max(64,Math.Min(budget,Math.Max(256,display)));
  }
  void ApplyCached(int page,Tile tile){
   var entry=cache.Get(page);string failure;if(entry!=null){tile.Image.Source=entry.Bitmap;tile.Message.Visibility=Visibility.Collapsed;}
   else{tile.Image.Source=null;tile.Message.Visibility=Visibility.Visible;tile.Message.Text=failures.TryGetValue(page,out failure)?"第 "+(page+1)+" 页无法预览："+failure:"正在读取第 "+(page+1)+" 页…";}
   var text=textCache.Get(page);if(!ReferenceEquals(tile.Text.Page,text))tile.Text.SetPage(text,selectText&&entry!=null);else tile.Text.SetActive(selectText&&entry!=null);
  }
  void RefreshViewport(){
   if(closed||layout==null||scroll.ViewportHeight<=0)return;
   wanted=layout.Near(scroll.VerticalOffset,scroll.ViewportHeight);
   var keep=new HashSet<int>(wanted);foreach(int page in tiles.Keys.ToArray())if(!keep.Contains(page)){if(page==selectedPage)Selection(-1,"");tiles[page].Text.SetPage(null,false);tiles[page].Image.Source=null;canvas.Children.Remove(tiles[page].Root);tiles.Remove(page);}
   cache.Retain(wanted);
   cache.TrimPerPage((long)(PdfRasterCache.Budget*.75/Math.Max(3,wanted.Length)));
   foreach(int page in wanted){Tile tile;if(!tiles.TryGetValue(page,out tile)){tile=CreateTile(page);tiles.Add(page,tile);}Place(page,tile);ApplyCached(page,tile);}
   int before=currentPage;currentPage=layout.PageAt(scroll.VerticalOffset+Math.Min(48,scroll.ViewportHeight/4));if(currentPage!=before)RetryMemoryLimitedText();if(PageChanged!=null)PageChanged(currentPage,PageCount);
   if(currentPage!=before&&ZoomChanged!=null)ZoomChanged();
   int[] textWanted=TextWanted();textCache.Retain(textWanted);foreach(int key in textFailures.Keys.ToArray())if(!textWanted.Contains(key))textFailures.Remove(key);memoryLimitedText.IntersectWith(textWanted);
   foreach(var pair in tiles)if(!textWanted.Contains(pair.Key))pair.Value.Text.SetPage(null,false);
   if(extracting!=null&&(!selectText||!textWanted.Contains(extractingPage)))extracting.Cancel();textRefreshPending=true;
   if(rendering!=null&&(!keep.Contains(renderingPage)||renderingDimension!=Dimension(renderingPage)))rendering.Cancel();
   refreshPending=true;settle.Stop();settle.Start();
  }
  async void Pump(){
   if(closed||session==null||layout==null||pumping)return;pumping=true;refreshPending=false;
   try{
    while(!closed){
     int page=-1,dimension=0;
     foreach(int candidate in wanted.OrderBy(x=>Math.Abs(x-currentPage))){if(failures.ContainsKey(candidate))continue;var stored=cache.Get(candidate);int target=Dimension(candidate);if(stored==null||stored.Dimension<target){page=candidate;dimension=target;break;}}
     if(page<0){
      Status(failures.Count==0?TextNotice():"部分页面无法预览，可用默认程序检查原文件。 · "+TextNotice());
      // Thumbnails share the already-open worker, after visible full pages.
      // At most twelve small rasters exist; hidden sidebars request none.
      int small=-1;foreach(int candidate in thumbnailPages)if(!thumbnails.ContainsKey(candidate)&&!thumbnailFailures.Contains(candidate)){small=candidate;break;}
      if(small<0)break;
      try{var result=await session.RenderAsync(small,160,lifetime.Token);if(closed)break;if(thumbnailPages.Contains(small)){thumbnails[small]=result.Bitmap;if(ThumbnailChanged!=null)ThumbnailChanged();}}
      catch(OperationCanceledException){}catch{if(!closed)thumbnailFailures.Add(small);}
      if(refreshPending)break;continue;
     }
     renderingPage=page;renderingDimension=dimension;var pending=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);rendering=pending;
     try{
      var result=await session.RenderAsync(page,dimension,pending.Token);
      if(closed)break;
      if(wanted.Contains(page)&&!pending.IsCancellationRequested){cache.Put(page,result.Bitmap,dimension);foreach(var pair in tiles)ApplyCached(pair.Key,pair.Value);}
     }catch(OperationCanceledException){}catch(Exception error){if(!closed&&!pending.IsCancellationRequested){failures[page]=PreviewWindow.FailureText(error);Tile tile;if(tiles.TryGetValue(page,out tile))ApplyCached(page,tile);Status("部分页面无法预览，可用默认程序检查原文件。");}}
     finally{if(ReferenceEquals(rendering,pending))rendering=null;pending.Dispose();}
     // Rapid scrolling/zooming gets one settle interval, not a render/cancel
     // storm after each native cancellation completes.
     if(refreshPending)break;
    }
   }finally{pumping=false;if(!closed&&refreshPending){settle.Stop();settle.Start();}}
  }
  int[] TextWanted(){if(layout==null||scroll.ViewportHeight<=0)return new int[0];int first=layout.PageAt(scroll.VerticalOffset+1),last=layout.PageAt(scroll.VerticalOffset+scroll.ViewportHeight-1);return Enumerable.Range(first,last-first+1).Take(PdfTextCache.MaximumPages).ToArray();}
  async void PumpText(){
   if(closed||!selectText||layout==null||sourcePath==null||textPumping)return;textPumping=true;textRefreshPending=false;
   try{
    if(textSession==null){
     var opening=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);openingText=opening;PdfTextSession opened=null;
     try{
      // Even the small filesystem/header probe can stall on an unavailable
      // removable/network drive. It must not run on the viewer's UI thread.
      opened=await Task.Run(delegate{opening.Token.ThrowIfCancellationRequested();return new PdfTextSession(sourcePath);},opening.Token);
      if(closed||!selectText||opening.IsCancellationRequested)return;
      textSession=opened;opened=null;
     }finally{if(opened!=null)opened.Dispose();if(ReferenceEquals(openingText,opening))openingText=null;opening.Dispose();}
    }
    var attempted=new HashSet<int>();
    while(!closed&&selectText){
     int page=-1;foreach(int candidate in TextWanted().OrderBy(x=>Math.Abs(x-currentPage)))if(!attempted.Contains(candidate)&&textCache.Get(candidate)==null&&!textFailures.ContainsKey(candidate)){page=candidate;break;}
     if(page<0){foreach(int missing in TextWanted())if(attempted.Contains(missing)&&textCache.Get(missing)==null&&!textFailures.ContainsKey(missing)){memoryLimitedText.Add(missing);textFailures[missing]="当前页面文字较密，已释放选字缓存 · 放大页面或重新开启选字后可重试";}Status(TextNotice());break;}
     attempted.Add(page);
     extractingPage=page;var pending=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);extracting=pending;
     try{var result=await textSession.ReadAsync(page,pending.Token);if(closed)break;if(selectText&&!pending.IsCancellationRequested&&TextWanted().Contains(page)){if(!textCache.TryPut(result,currentPage)){memoryLimitedText.Add(page);textFailures[page]=page==currentPage?"此页文字超过选字缓存上限 · 仍可浏览或使用截图翻译":"此页文字较密，已优先保留正在阅读的页 · 滚到此页或放大页面后可重试";}foreach(var pair in tiles)ApplyCached(pair.Key,pair.Value);Status(TextNotice());}}
     catch(OperationCanceledException){}catch(Exception error){if(!closed&&!pending.IsCancellationRequested){textFailures[page]=PreviewWindow.FailureText(error);Status(TextNotice());}}
     finally{if(ReferenceEquals(extracting,pending))extracting=null;pending.Dispose();}
     if(textRefreshPending)break;
    }
   }catch(OperationCanceledException){}catch(Exception error){if(!closed&&selectText){foreach(int page in TextWanted())textFailures[page]="文字层无法读取："+PreviewWindow.FailureText(error)+" · 仍可使用截图翻译";Status(TextNotice());}}
   finally{textPumping=false;if(!closed&&selectText&&textRefreshPending){settle.Stop();settle.Start();}}
  }
  public void Dispose(){if(closed)return;closed=true;middlePan.Dispose();settle.Stop();lifetime.Cancel();if(rendering!=null)rendering.Cancel();if(openingText!=null)openingText.Cancel();if(extracting!=null)extracting.Cancel();foreach(var tile in tiles.Values){tile.Text.SetPage(null,false);tile.Image.Source=null;}tiles.Clear();canvas.Children.Clear();cache.Clear();thumbnails.Clear();thumbnailFailures.Clear();thumbnailPages=new int[0];textCache.Clear();failures.Clear();textFailures.Clear();memoryLimitedText.Clear();selectedText="";if(textSession!=null){textSession.Dispose();textSession=null;}if(session!=null){session.Dispose();session=null;}sizes=null;layout=null;}
 }
}
