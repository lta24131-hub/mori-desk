using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MoriDesk {
 // A thumbnail is a disposable view of a committed scene, never project data.
 // Pair the PNG with rendered-content and image hashes, so view-only saves do
 // not invalidate it. Gallery decoding is detached and bounded to card size.
 internal static partial class CanvasProjectThumbnail {
  internal const string ImageName="canvas-preview.png",MetadataName="canvas-preview.json";
  internal const int MaximumBytes=2*1024*1024,MaximumDimension=640,DecodeDimension=320;
  internal const long CacheBudget=8L*1024*1024;
  internal sealed class Metadata {public Metadata(){}public string Project {get;set;}public string ManifestHash {get;set;}public string ContentHash {get;set;}public string ImageHash {get;set;}public bool Empty {get;set;}}
  sealed class Cached {internal BitmapSource Image;internal long Bytes,Touch;}
  static readonly object cacheGate=new object();
  static readonly Dictionary<string,Cached> cache=new Dictionary<string,Cached>(StringComparer.OrdinalIgnoreCase);
  static long cacheBytes,touch;
  static readonly SemaphoreSlim slots=new SemaphoreSlim(1,1);
  static string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");}
  static object Stable(object value){
   var map=value as Dictionary<string,object>;if(map!=null){var sorted=new SortedDictionary<string,object>(StringComparer.Ordinal);foreach(var pair in map)sorted.Add(pair.Key,Stable(pair.Value));return sorted;}
   var array=value as object[];if(array!=null)return array.Select(Stable).ToArray();
   // The editor uses IEEE-754 numbers. Its first save may shorten a decimal
   // spelling without changing the number (264.16666666666669 -> ...6667).
   // Hash the same double domain instead of serializer-specific decimals.
   // Framework decimal-to-double conversion can double-round (1.504...4809).
   // Parsing its exact decimal text matches the editor's JSON-number parser.
   if(value is decimal)return double.Parse(((decimal)value).ToString(CultureInfo.InvariantCulture),CultureInfo.InvariantCulture);
   if(value is int||value is long||value is double||value is float)return Convert.ToDouble(value,CultureInfo.InvariantCulture);return value;
  }
  static string ContentHash(MatureCanvasDocument document){
   // SVG export fits the current page, independent of camera, selection and
   // window/tool state. Keep the page identity, not those session-only fields.
   var json=new JavaScriptSerializer{MaxJsonLength=MatureCanvasStore.MaximumSceneBytes*2,RecursionLimit=48};var scene=json.DeserializeObject(document.SceneJson) as Dictionary<string,object>;object content,sessionValue,page=null;
   if(scene==null||!scene.TryGetValue("document",out content))throw new InvalidDataException("画布预览缺少文档。");
   if(scene.TryGetValue("session",out sessionValue)){var session=sessionValue as Dictionary<string,object>;if(session!=null)session.TryGetValue("currentPageId",out page);}
   return Hash(Encoding.UTF8.GetBytes(json.Serialize(new object[]{Stable(content),page})));
  }
  static void Safe(string path){for(string part=Path.GetFullPath(path);!string.IsNullOrEmpty(part);part=Path.GetDirectoryName(part))if((File.Exists(part)||Directory.Exists(part))&&(File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new IOException("画布预览路径包含链接，不能安全读写。");}
  static string Folder(string root,string id){if(!FreeCanvasStore.IsId(id))throw new InvalidDataException("画布编号无效。");string folder=Path.Combine(root,id);Safe(folder);return folder;}
  static byte[] ReadBounded(string file,int maximum){Safe(file);using(var input=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){if(input.Length<=0||input.Length>maximum)throw new InvalidDataException("画布预览为空或过大。");byte[] bytes=new byte[(int)input.Length];int offset=0,read;while(offset<bytes.Length&&(read=input.Read(bytes,offset,bytes.Length-offset))>0)offset+=read;if(offset!=bytes.Length||input.ReadByte()!=-1)throw new IOException("画布预览读取时发生变化。");return bytes;}}
  static void Atomic(string file,byte[] bytes){Safe(file);string folder=Path.GetDirectoryName(file);if(!Directory.Exists(folder))throw new DirectoryNotFoundException("画布已移走，不能写入预览。");string pending=Path.Combine(folder,"thumbnail-"+Guid.NewGuid().ToString("N")+".tmp");try{using(var output=new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(bytes,0,bytes.Length);output.Flush(true);}Safe(file);if(File.Exists(file))File.Replace(pending,file,null,true);else File.Move(pending,file);}finally{if(File.Exists(pending))File.Delete(pending);}}
  static BitmapSource DecodePng(byte[] bytes){
   if(bytes.Length<8||bytes[0]!=137||bytes[1]!=80||bytes[2]!=78||bytes[3]!=71||bytes[4]!=13||bytes[5]!=10||bytes[6]!=26||bytes[7]!=10)throw new InvalidDataException("画布预览必须是 PNG。");
   int width,height;using(var stream=new MemoryStream(bytes,false)){var decoder=new PngBitmapDecoder(stream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);var frame=decoder.Frames[0];width=frame.PixelWidth;height=frame.PixelHeight;if(width<1||height<1||width>MaximumDimension||height>MaximumDimension)throw new InvalidDataException("画布预览尺寸超出范围。");}
   using(var stream=new MemoryStream(bytes,false)){var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=stream;if(width>=height)bitmap.DecodePixelWidth=Math.Min(width,DecodeDimension);else bitmap.DecodePixelHeight=Math.Min(height,DecodeDimension);bitmap.EndInit();bitmap.Freeze();return bitmap;}
  }
  internal static void SaveMature(MatureCanvasStore store,MatureCanvasDocument document,string dataUrl){
   if(store==null||document==null||document.LoadedHash==null)throw new InvalidDataException("只能为已经保存的画布生成预览。");
   string folder=Folder(store.Root,document.Id),manifest=Path.Combine(folder,MatureCanvasStore.ManifestName);
   // Use the scene writer's mutex, not a separate thumbnail-only lock: another
   // process cannot commit a newer scene between verification and pair-write.
   string lockName="Local\\NookMatureCanvas-"+Hash(Encoding.UTF8.GetBytes(manifest.ToUpperInvariant()));using(var mutex=new Mutex(false,lockName)){bool owns=false;try{try{owns=mutex.WaitOne(5000);}catch(AbandonedMutexException){owns=true;}if(!owns)throw new IOException("画布正在保存，稍后生成预览。");SaveMatureLocked(store,document,dataUrl,folder,manifest);}finally{if(owns)mutex.ReleaseMutex();}}
  }
  static void SaveMatureLocked(MatureCanvasStore store,MatureCanvasDocument document,string dataUrl,string folder,string manifest){
   string actual=Hash(ReadBounded(manifest,MatureCanvasStore.MaximumSceneBytes*2));if(actual!=document.LoadedHash)throw new IOException("画布已更新，旧预览没有保存。");
   var metadata=new Metadata{Project=document.Id,ManifestHash=actual,ContentHash=ContentHash(document),Empty=string.IsNullOrEmpty(dataUrl)};
   if(!metadata.Empty){const string prefix="data:image/png;base64,";if(!dataUrl.StartsWith(prefix,StringComparison.Ordinal)||dataUrl.Length>MaximumBytes*4/3+prefix.Length+4)throw new InvalidDataException("画布预览格式或大小无效。");byte[] bytes;try{bytes=Convert.FromBase64String(dataUrl.Substring(prefix.Length));}catch(FormatException error){throw new InvalidDataException("画布预览编码无效。",error);}if(bytes.Length==0||bytes.Length>MaximumBytes)throw new InvalidDataException("画布预览过大。");DecodePng(bytes);if(Hash(ReadBounded(manifest,MatureCanvasStore.MaximumSceneBytes*2))!=actual)throw new IOException("画布在生成预览时已更新。");metadata.ImageHash=Hash(bytes);Atomic(Path.Combine(folder,ImageName),bytes);}
   Atomic(Path.Combine(folder,MetadataName),Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(metadata)));Invalidate(store.Root,document.Id);
  }
  internal static BitmapSource ReadMature(MatureCanvasStore store,string id){
   string folder=Folder(store.Root,id);string metadataFile=Path.Combine(folder,MetadataName);if(!File.Exists(metadataFile))return null;var metadata=new JavaScriptSerializer().Deserialize<Metadata>(Encoding.UTF8.GetString(ReadBounded(metadataFile,4096)));
   if(metadata==null||metadata.Project!=id)return null;byte[] manifest=ReadBounded(Path.Combine(folder,MatureCanvasStore.ManifestName),MatureCanvasStore.MaximumSceneBytes*2);
   if(string.IsNullOrEmpty(metadata.ContentHash)){if(metadata.ManifestHash!=Hash(manifest))return null;}
   else{var json=new JavaScriptSerializer{MaxJsonLength=MatureCanvasStore.MaximumSceneBytes*2,RecursionLimit=48};var document=json.Deserialize<MatureCanvasDocument>(Encoding.UTF8.GetString(manifest));if(document==null||document.Id!=id||metadata.ContentHash!=ContentHash(document))return null;}
   if(metadata.Empty)return null;
   byte[] bytes=ReadBounded(Path.Combine(folder,ImageName),MaximumBytes);if(metadata.ImageHash!=Hash(bytes))return null;return DecodePng(bytes);
  }
  static string Stamp(string file){Safe(file);if(!File.Exists(file))return "missing";var info=new FileInfo(file);return info.Length.ToString(CultureInfo.InvariantCulture)+":"+info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);}
  internal static string CacheKey(FreeCanvasStore store,MatureCanvasStore matureStore,CanvasProjectRow row){string folder=Folder(store.Root,row.Id);return folder+"|"+row.Updated+"|"+Stamp(Path.Combine(folder,matureStore!=null&&matureStore.Exists(row.Id)?MatureCanvasStore.ManifestName:"project.json"))+"|"+Stamp(Path.Combine(folder,MetadataName));}
  internal static void Invalidate(string root,string id){string prefix=Folder(root,id)+"|";lock(cacheGate){foreach(string key in cache.Keys.Where(k=>k.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)).ToArray()){cacheBytes-=cache[key].Bytes;cache.Remove(key);}}}
  internal static long CacheBytesForTests {get{lock(cacheGate)return cacheBytes;}}
  internal static BitmapSource Read(FreeCanvasStore store,MatureCanvasStore matureStore,CanvasProjectRow row){
   if(row==null||!string.IsNullOrEmpty(row.Error))return null;string key=CacheKey(store,matureStore,row);lock(cacheGate){Cached found;if(cache.TryGetValue(key,out found)){found.Touch=++touch;return found.Image;}}
   BitmapSource result;
   if(matureStore!=null&&matureStore.Exists(row.Id)){
    // Engine-exported previews remain the high-fidelity first choice. Older
    // projects may never have exported one, and an interrupted pair-write can
    // leave it unavailable. Render only the saved scene as a bounded fallback:
    // opening the editor must not be a prerequisite for recognizing a project.
    try{result=ReadMature(matureStore,row.Id);}catch(Exception error){if(!PreviewFailure(error))throw;result=null;}
    if(result==null)result=RenderMature(matureStore,matureStore.Load(row.Id));
   }else result=RenderLegacy(store,store.Load(row.Id));
   if(result!=null){if(result.CanFreeze&&!result.IsFrozen)result.Freeze();long bytes=(long)result.PixelWidth*result.PixelHeight*4;lock(cacheGate){if(!cache.ContainsKey(key)){while(cacheBytes+bytes>CacheBudget&&cache.Count>0){string remove=cache.OrderBy(p=>p.Value.Touch).First().Key;cacheBytes-=cache[remove].Bytes;cache.Remove(remove);}if(bytes<=CacheBudget){cache[key]=new Cached{Image=result,Bytes=bytes,Touch=++touch};cacheBytes+=bytes;}}}}return result;
  }
  internal static async Task<BitmapSource> ReadAsync(FreeCanvasStore store,MatureCanvasStore matureStore,CanvasProjectRow row,Func<bool> wanted){
   await slots.WaitAsync();try{if(!wanted())return null;return await Sta(()=>Read(store,matureStore,row));}finally{slots.Release();}
  }
  static Task<T> Sta<T>(Func<T> read){var completion=new TaskCompletionSource<T>();var thread=new Thread(()=>{try{completion.SetResult(read());}catch(Exception error){completion.SetException(error);}}){IsBackground=true,Name="Nook canvas thumbnail"};thread.SetApartmentState(ApartmentState.STA);thread.Start();return completion.Task;}
  static Brush Ink(FreeCanvasItem item){return item.Color=="auto"||string.IsNullOrEmpty(item.Color)?Brushes.Black:(Brush)new BrushConverter().ConvertFromString(item.Color);}
  internal static BitmapSource RenderLegacy(FreeCanvasStore store,FreeCanvasDocument document){
   if(document.Items.Count==0)return null;Rect bounds=Rect.Empty;foreach(var item in document.Items){Rect current=item.Bounds();if(item.Kind=="text"){var text=Text(item);current.Height=Math.Max(current.Height,text.Height);}if(item.IsDrawing)current.Inflate(Math.Max(1,item.Stroke),Math.Max(1,item.Stroke));bounds.Union(current);}if(bounds.IsEmpty)return null;
   const int width=320,height=200;double scale=Math.Min((width-24)/Math.Max(1,bounds.Width),(height-24)/Math.Max(1,bounds.Height));double x=(width-bounds.Width*scale)/2-bounds.X*scale,y=(height-bounds.Height*scale)/2-bounds.Y*scale;
   long imageBytes=0;const long imageBudget=4L*1024*1024;var visual=new DrawingVisual();using(var dc=visual.RenderOpen()){dc.DrawRectangle(Brushes.White,null,new Rect(0,0,width,height));dc.PushClip(new RectangleGeometry(new Rect(0,0,width,height)));dc.PushTransform(new MatrixTransform(scale,0,0,scale,x,y));foreach(var item in document.Items){
    if(item.Kind=="image"){try{if(imageBytes>=imageBudget){dc.DrawRectangle(Brushes.LightGray,null,item.Bounds());continue;}int w,h;int size=Math.Max(1,Math.Min(DecodeDimension,(int)Math.Ceiling(Math.Max(item.Width,item.Height)*scale)));if(imageBytes+(long)size*size*4>imageBudget)size=Math.Max(1,(int)Math.Sqrt(Math.Max(4,imageBudget-imageBytes)/4));var image=FreeCanvasStore.Decode(store.ImagePath(document.Id,item.Image),item.Image,size,CancellationToken.None,out w,out h);imageBytes+=(long)image.PixelWidth*image.PixelHeight*4;dc.DrawImage(image,item.Bounds());}catch(Exception error){if(error is OutOfMemoryException)throw;dc.DrawRectangle(Brushes.LightGray,new Pen(Brushes.Gray,1/scale),item.Bounds());}}
    else if(item.Kind=="text")dc.DrawText(Text(item),new Point(item.X,item.Y));
    else{var brush=Ink(item);var pen=new Pen(brush,item.Stroke){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round};Point start=new Point(item.X,item.Y),end=new Point(item.EndX,item.EndY);if(item.Kind=="line"||item.Kind=="arrow"){dc.DrawLine(pen,start,end);if(item.Kind=="arrow"){Vector direction=start-end;double length=direction.Length;if(length>0){direction.Normalize();double arm=Math.Min(length*.45,Math.Max(10,item.Stroke*4));Vector side=new Vector(-direction.Y,direction.X);dc.DrawLine(pen,end,end+direction*arm+side*arm*.45);dc.DrawLine(pen,end,end+direction*arm-side*arm*.45);}}}else if(item.Kind=="rectangle")dc.DrawRectangle(null,pen,item.Bounds());else if(item.Kind=="ellipse"){Rect rect=item.Bounds();dc.DrawEllipse(null,pen,new Point(rect.X+rect.Width/2,rect.Y+rect.Height/2),rect.Width/2,rect.Height/2);}else if(item.Kind=="stroke"&&item.Points!=null&&item.Points.Count>0){var geometry=new StreamGeometry();using(var context=geometry.Open()){context.BeginFigure(new Point(item.Points[0].X,item.Points[0].Y),false,false);foreach(var point in item.Points.Skip(1))context.LineTo(new Point(point.X,point.Y),true,false);}geometry.Freeze();if(item.Points.Count==1)dc.DrawEllipse(brush,null,new Point(item.Points[0].X,item.Points[0].Y),item.Stroke/2,item.Stroke/2);else dc.DrawGeometry(null,pen,geometry);}}
   }dc.Pop();dc.Pop();}var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);bitmap.Freeze();return bitmap;
  }
  static FormattedText Text(FreeCanvasItem item){var text=new FormattedText(item.Text??"",CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Microsoft YaHei UI"),item.FontSize,Ink(item));text.MaxTextWidth=Math.Max(1,item.Width);return text;}
 }
 // Loading begins only when the card is in the viewport. Detached thumbnails
 // survive refresh in a small LRU, while unloaded cards release their Source.
 internal sealed class CanvasProjectThumbnailImage:Image {
  readonly FreeCanvasStore store;readonly MatureCanvasStore matureStore;readonly CanvasProjectRow row;bool loading,attempted;int generation;
  internal event Action<bool> PreviewResolved;
  internal CanvasProjectThumbnailImage(FreeCanvasStore store,MatureCanvasStore matureStore,CanvasProjectRow row){this.store=store;this.matureStore=matureStore;this.row=row;Stretch=Stretch.Uniform;RenderOptions.SetBitmapScalingMode(this,BitmapScalingMode.HighQuality);Loaded+=(s,e)=>UpdateViewport();LayoutUpdated+=(s,e)=>UpdateViewport();IsVisibleChanged+=(s,e)=>UpdateViewport();Unloaded+=(s,e)=>{generation++;attempted=false;Source=null;};}
  bool InViewport(){if(!IsLoaded||!IsVisible)return false;DependencyObject current=this;while(current!=null){var scroll=current as ScrollViewer;if(scroll!=null){try{Rect bounds=TransformToAncestor(scroll).TransformBounds(new Rect(new Point(),RenderSize));return bounds.IntersectsWith(new Rect(0,0,scroll.ActualWidth,scroll.ActualHeight));}catch(InvalidOperationException){return false;}}current=VisualTreeHelper.GetParent(current);}return true;}
  void UpdateViewport(){if(InViewport())TryLoad();else if(Source!=null||attempted){generation++;Source=null;attempted=false;}}
  async void TryLoad(){if(loading||attempted||Source!=null||!InViewport())return;loading=true;int stamp=generation;try{var image=await CanvasProjectThumbnail.ReadAsync(store,matureStore,row,()=>stamp==generation&&InViewport());if(stamp==generation&&InViewport()){Source=image;attempted=true;if(PreviewResolved!=null)PreviewResolved(image!=null);}}catch(Exception error){System.Diagnostics.Debug.WriteLine("Canvas project thumbnail: "+error.Message);if(stamp==generation){attempted=true;if(PreviewResolved!=null)PreviewResolved(false);}}finally{loading=false;}}
 }
}
