using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mori;

namespace MoriDesk {
 public sealed class FreeCanvasPoint {
  public double X {get;set;} public double Y {get;set;}
  public FreeCanvasPoint Copy(){return (FreeCanvasPoint)MemberwiseClone();}
 }
 public sealed class FreeCanvasItem {
  public string Id {get;set;}
  public string Kind {get;set;}
  public string Image {get;set;}
  public string Source {get;set;}
  public string Text {get;set;}
  public string Color {get;set;}
  public double X {get;set;} public double Y {get;set;}
  public double Width {get;set;} public double Height {get;set;}
  public double EndX {get;set;} public double EndY {get;set;}
  public double Stroke {get;set;} public double FontSize {get;set;}
  // Freehand points are absolute world coordinates, just like line endpoints.
  // Keep each undo/project copy independent from subsequent drawing edits.
  public List<FreeCanvasPoint> Points {get;set;}
  public FreeCanvasItem Copy(){var copy=(FreeCanvasItem)MemberwiseClone();if(Points!=null)copy.Points=Points.Select(p=>p==null?null:p.Copy()).ToList();return copy;}
  internal bool HasEndpoints {get{return Kind=="line"||Kind=="arrow"||Kind=="rectangle"||Kind=="ellipse";}}
  internal bool IsDrawing {get{return HasEndpoints||Kind=="stroke";}}
  public Rect Bounds(){
   if(Kind=="stroke"&&Points!=null&&Points.Count>0){double left=Points[0].X,top=Points[0].Y,right=left,bottom=top;foreach(var point in Points){left=Math.Min(left,point.X);top=Math.Min(top,point.Y);right=Math.Max(right,point.X);bottom=Math.Max(bottom,point.Y);}double pad=Math.Max(.5,Stroke/2);return new Rect(left-pad,top-pad,right-left+pad*2,bottom-top+pad*2);}
   return HasEndpoints?new Rect(new Point(Math.Min(X,EndX),Math.Min(Y,EndY)),new Size(Math.Max(1,Math.Abs(EndX-X)),Math.Max(1,Math.Abs(EndY-Y)))):new Rect(X,Y,Width,Height);
  }
 }
 public sealed class FreeCanvasDocument {
  public int Version {get;set;}
  public string Id {get;set;} public string Name {get;set;}
  public double Zoom {get;set;} public double PanX {get;set;} public double PanY {get;set;}
  public bool Pinned {get;set;}
  public string Updated {get;set;}
  public List<FreeCanvasItem> Items {get;set;}
  internal string LoadedHash;
 }
 public sealed class FreeCanvasProjectInfo {
  public string Id {get;set;} public string Name {get;set;} public string Updated {get;set;} public string Error {get;set;}
  public override string ToString(){return Name+(string.IsNullOrEmpty(Error)?"":"（无法读取）");}
 }
 // A project owns copies, never the gallery or desktop originals. Each atomic
 // manifest write keeps the previous version; stale and damaged manifests are
 // rejected instead of quietly replacing the user's existing work.
 public sealed class FreeCanvasStore {
  static readonly Regex IdPattern=new Regex("^[a-f0-9]{32}$",RegexOptions.CultureInvariant);
  static readonly Regex ImagePattern=new Regex("^[a-f0-9]{32}\\.(png|jpg|jpeg|gif|bmp|tif|tiff|ico|webp|avif|heic|heif|psd|psb|dng|cr2|cr3|nef|arw|orf|rw2|raf)$",RegexOptions.CultureInvariant|RegexOptions.IgnoreCase);
  readonly string root;
  internal const int MaximumStrokePoints=100000,MaximumDocumentPoints=1000000,DesignPreviewDimension=2048;
  public string Root {get{return root;}}
  static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=128*1024*1024,RecursionLimit=32};}
  public FreeCanvasStore(string dataRoot){
   if(string.IsNullOrWhiteSpace(dataRoot)||!Path.IsPathRooted(dataRoot)||dataRoot.StartsWith("\\\\",StringComparison.Ordinal)||dataRoot.Length<3||dataRoot[1]!=':'||dataRoot[2]!='\\')throw new ArgumentException("画布必须保存到本机绝对路径。");
   string full=Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar);if(full.Length<=3||string.Equals(full.TrimEnd('\\'),Path.GetPathRoot(full).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))throw new ArgumentException("画布数据不能保存到磁盘根目录。");
   root=Path.Combine(full,"free-canvas");EnsureSafe(full);Directory.CreateDirectory(root);EnsureSafe(root);
  }
  internal static bool IsId(string value){return value!=null&&IdPattern.IsMatch(value);}
  internal static bool Finite(double value){return !double.IsNaN(value)&&!double.IsInfinity(value)&&Math.Abs(value)<=100000000;}
  internal static bool SupportedImage(string path){if(string.IsNullOrWhiteSpace(path))return false;string ext=Path.GetExtension(path);return ImagePattern.IsMatch(new string('a',32)+ext)||ext.Equals(".svg",StringComparison.OrdinalIgnoreCase)||ext.Equals(".ai",StringComparison.OrdinalIgnoreCase);}
  static void EnsureSafe(string path){for(string p=Path.GetFullPath(path);!string.IsNullOrEmpty(p);p=Path.GetDirectoryName(p))if((Directory.Exists(p)||File.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("画布路径包含链接目录，不能安全写入。");}
  string ProjectPath(string id){if(!IsId(id))throw new InvalidDataException("画布项目编号无效。");string path=Path.Combine(root,id);EnsureSafe(path);return path;}
  public string ImagePath(string projectId,string image){if(image==null||!ImagePattern.IsMatch(image))throw new InvalidDataException("画布图片记录无效。");string path=Path.Combine(ProjectPath(projectId),"images",image);EnsureSafe(path);return path;}
  static string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");}
  static string Name(string value){value=(value??"").Trim();if(value.Length==0||value.Length>120)throw new ArgumentException("项目名称需为 1 至 120 个字。");return value;}
  internal static void Validate(FreeCanvasDocument document){
   if(document==null||document.Version!=1||!IsId(document.Id)||document.Items==null)throw new InvalidDataException("画布记录损坏，或由较新版本创建；现有文件不会被覆盖。");
   Name(document.Name);if(!Finite(document.Zoom)||document.Zoom<.02||document.Zoom>16||!Finite(document.PanX)||!Finite(document.PanY))throw new InvalidDataException("画布视图数据无效。");
   var ids=new HashSet<string>();int points=0;foreach(var item in document.Items){
    if(item==null||!IsId(item.Id)||!ids.Add(item.Id)||!new[]{"image","line","text","stroke","arrow","rectangle","ellipse"}.Contains(item.Kind)||!Finite(item.X)||!Finite(item.Y)||!Finite(item.Width)||!Finite(item.Height)||!Finite(item.EndX)||!Finite(item.EndY))throw new InvalidDataException("画布项目内容损坏。");
    if((item.Kind=="image"||item.Kind=="text")&&(item.Width<=0||item.Height<=0))throw new InvalidDataException("画布项目尺寸无效。");
    if(item.Kind=="image"&&(item.Image==null||!ImagePattern.IsMatch(item.Image)))throw new InvalidDataException("画布图片路径无效。");
    if(item.Kind=="text"&&(item.Text==null||item.Text.Length>20000||!Finite(item.FontSize)||item.FontSize<6||item.FontSize>300))throw new InvalidDataException("画布文字无效。");
    if(item.IsDrawing&&(!Finite(item.Stroke)||item.Stroke<.25||item.Stroke>100))throw new InvalidDataException("画布线条数据无效。");
    if(item.Kind=="stroke"){
     if(item.Points==null||item.Points.Count<1||item.Points.Count>MaximumStrokePoints)throw new InvalidDataException("画布笔画点数无效或过多。");
     foreach(var point in item.Points)if(point==null||!Finite(point.X)||!Finite(point.Y))throw new InvalidDataException("画布笔画坐标无效。");
    }else if(item.Points!=null&&item.Points.Count!=0)throw new InvalidDataException("只有自由笔画可以包含点列。");
    if(item.Points!=null){points+=item.Points.Count;if(points>MaximumDocumentPoints)throw new InvalidDataException("画布笔画总点数过多，请分开保存项目。");}
    if(item.Kind!="image"&&!ValidColor(item.Color))throw new InvalidDataException("画布标注颜色无效。");
   }
  }
  internal static bool ValidColor(string color){return color=="auto"||(color!=null&&Regex.IsMatch(color,"^#[a-fA-F0-9]{6}$",RegexOptions.CultureInvariant));}
  public List<FreeCanvasProjectInfo> List(){
   EnsureSafe(root);var result=new List<FreeCanvasProjectInfo>();foreach(string dir in Directory.GetDirectories(root)){
    string id=Path.GetFileName(dir);if(!IsId(id))continue;try{var d=Load(id);result.Add(new FreeCanvasProjectInfo{Id=d.Id,Name=d.Name,Updated=d.Updated});}catch(Exception ex){if(!(ex is IOException)&&!(ex is InvalidDataException)&&!(ex is UnauthorizedAccessException)&&!(ex is ArgumentException)&&!(ex is InvalidOperationException))throw;result.Add(new FreeCanvasProjectInfo{Id=id,Name="无法读取的项目 "+id.Substring(0,6),Updated="",Error=ex.Message});}
   }return result.OrderByDescending(p=>p.Updated,StringComparer.Ordinal).ThenBy(p=>p.Name,StringComparer.CurrentCulture).ToList();
  }
  public FreeCanvasDocument Create(string name){var document=new FreeCanvasDocument{Version=1,Id=Guid.NewGuid().ToString("N"),Name=Name(name),Zoom=1,Items=new List<FreeCanvasItem>()};Save(document);return document;}
  public FreeCanvasDocument Load(string id){
   string file=Path.Combine(ProjectPath(id),"project.json");if(!File.Exists(file))throw new InvalidDataException("这个项目的记录文件不存在；不会覆盖现有图片。");
   var info=new FileInfo(file);if(info.Length==0||info.Length>128L*1024*1024)throw new InvalidDataException("画布记录为空或超出可读取范围。");byte[] bytes=File.ReadAllBytes(file);FreeCanvasDocument document;
   try{document=Json().Deserialize<FreeCanvasDocument>(new UTF8Encoding(false,true).GetString(bytes));}catch(Exception ex){throw new InvalidDataException("画布记录损坏，原文件已保留；可使用 project.json.bak 恢复。",ex);}
   Validate(document);if(document.Id!=id)throw new InvalidDataException("画布目录与记录不匹配。");document.LoadedHash=Hash(bytes);return document;
  }
  public void Save(FreeCanvasDocument document){
   Validate(document);string folder=ProjectPath(document.Id);Directory.CreateDirectory(folder);EnsureSafe(folder);string file=Path.Combine(folder,"project.json");EnsureSafe(file);
   if(File.Exists(file)){if(document.LoadedHash==null||Hash(File.ReadAllBytes(file))!=document.LoadedHash)throw new IOException("画布记录已被其他窗口修改，或发生损坏；请保留当前窗口并另存新项目。");}else if(document.LoadedHash!=null)throw new IOException("画布记录已被移走，不能直接覆盖；请另存新项目。");
   document.Name=Name(document.Name);document.Updated=DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture);byte[] data=new UTF8Encoding(false).GetBytes(Json().Serialize(document));string pending=Path.Combine(folder,"write-"+Guid.NewGuid().ToString("N")+".tmp");
   try{using(var stream=new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.None)){stream.Write(data,0,data.Length);stream.Flush(true);}if(File.Exists(file))File.Replace(pending,file,file+".bak",true);else File.Move(pending,file);document.LoadedHash=Hash(data);}finally{if(File.Exists(pending))File.Delete(pending);}
  }
  public FreeCanvasDocument SaveAs(FreeCanvasDocument source,string name){
   Validate(source);var target=Create(name);try{foreach(var item in source.Items){var copy=item.Copy();if(copy.Kind=="image"){string original=ImagePath(source.Id,copy.Image);string path=ImagePath(target.Id,copy.Image);Directory.CreateDirectory(Path.GetDirectoryName(path));if(!File.Exists(path))File.Copy(original,path,false);}target.Items.Add(copy);}target.Zoom=source.Zoom;target.PanX=source.PanX;target.PanY=source.PanY;target.Pinned=source.Pinned;Save(target);return target;}catch{Trash(target.Id);throw;}
  }
  public string Trash(string id){string folder=ProjectPath(id);if(!Directory.Exists(folder))throw new DirectoryNotFoundException("这个画布已经不存在。");string trash=Path.Combine(root,"_trash");Directory.CreateDirectory(trash);EnsureSafe(trash);string destination=Path.Combine(trash,id+"-"+DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));EnsureSafe(destination);Directory.Move(folder,destination);return destination;}
  public string RestoreLastDeleted(){
   string trash=Path.Combine(root,"_trash");if(!Directory.Exists(trash))return null;EnsureSafe(trash);string candidate=Directory.GetDirectories(trash).Where(p=>Regex.IsMatch(Path.GetFileName(p),"^[a-f0-9]{32}-[0-9]+$",RegexOptions.CultureInvariant)).OrderByDescending(p=>long.Parse(Path.GetFileName(p).Substring(33),CultureInfo.InvariantCulture)).FirstOrDefault();if(candidate==null)return null;
   EnsureSafe(candidate);string id=Path.GetFileName(candidate).Substring(0,32),destination=ProjectPath(id);if(Directory.Exists(destination))throw new IOException("同名项目已存在，不能覆盖恢复。");Directory.Move(candidate,destination);return id;
  }
  public FreeCanvasItem Import(string id,string source,CancellationToken cancellation){
   cancellation.ThrowIfCancellationRequested();
   string folder=ProjectPath(id);if(!Directory.Exists(folder)||!File.Exists(Path.Combine(folder,"project.json")))throw new DirectoryNotFoundException("请先创建画布项目。");
   if(string.IsNullOrWhiteSpace(source)||!Path.IsPathRooted(source)||!(source.StartsWith("\\\\",StringComparison.Ordinal)||(source.Length>=3&&source[1]==':'&&source[2]=='\\'))||source.IndexOf("://",StringComparison.Ordinal)>=0||!SupportedImage(source))throw new NotSupportedException("画布支持拖入本机图片文件；文件夹、网址不会展开导入。");
   source=Path.GetFullPath(source);if(!File.Exists(source))throw new FileNotFoundException("图片已移动或不存在。",source);string extension=Path.GetExtension(source).ToLowerInvariant();bool design=extension==".svg"||extension==".ai";long limit=extension==".svg"?SvgRaster.MaximumInput:ImageCodec.MaximumInput;var info=new FileInfo(source);if(info.Length==0||info.Length>limit)throw new NotSupportedException(extension==".svg"?"SVG 文件需小于 8 MB，画布会保存独立的 PNG 预览。":"单张图片需小于 256 MB；图片数量不设上限。");
   string itemId=Guid.NewGuid().ToString("N"),image=itemId+(design?".png":extension),path=ImagePath(id,image),pending=path+".part"+extension,rendered=design?path+".part.png":null;Directory.CreateDirectory(Path.GetDirectoryName(path));EnsureSafe(Path.GetDirectoryName(path));
   try{
    using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read))using(var output=new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.None)){if(input.Length==0||input.Length>limit)throw new NotSupportedException("图片文件在读取前超出可导入范围。");byte[] buffer=new byte[65536];int read;long count=0;while((read=input.Read(buffer,0,buffer.Length))>0){cancellation.ThrowIfCancellationRequested();count+=read;if(count>limit)throw new NotSupportedException("图片文件在读取时超出可导入范围。");output.Write(buffer,0,read);}output.Flush(true);}
    cancellation.ThrowIfCancellationRequested();int width,height;
    if(design){
     // Design artwork stays untouched. The project owns a bounded, independent
     // PNG: SVG's sanitized artwork or only the first PDF-compatible AI page.
     BitmapSource preview;
     if(extension==".svg")preview=SvgRaster.Render(pending,DesignPreviewDimension,out width,out height);
     else{var page=PdfPreview.RenderAsync(pending,0,DesignPreviewDimension,cancellation).GetAwaiter().GetResult();preview=page.Bitmap;/* Rounded native page dimensions must not distort the committed PNG. */width=preview.PixelWidth;height=preview.PixelHeight;}
     cancellation.ThrowIfCancellationRequested();var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(preview));using(var output=new FileStream(rendered,FileMode.CreateNew,FileAccess.Write,FileShare.None)){encoder.Save(output);output.Flush(true);}cancellation.ThrowIfCancellationRequested();File.Move(rendered,path);
    }else{Decode(pending,source,64,cancellation,out width,out height);cancellation.ThrowIfCancellationRequested();File.Move(pending,path);}
    double longest=Math.Max(width,height),scale=Math.Min(1,360.0/longest);return new FreeCanvasItem{Id=itemId,Kind="image",Image=image,Source=source,X=0,Y=0,Width=Math.Max(1,width*scale),Height=Math.Max(1,height*scale)};
   }finally{if(File.Exists(pending))File.Delete(pending);if(rendered!=null&&File.Exists(rendered))File.Delete(rendered);}
  }
  internal static BitmapSource Decode(string path,string typePath,int longest,CancellationToken cancellation,out int width,out int height){
   width=height=0;cancellation.ThrowIfCancellationRequested();
   try{using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);var frame=decoder.Frames[0];width=frame.PixelWidth;height=frame.PixelHeight;if(width<1||height<1||width>100000||height>100000||(long)width*height>1000000000)throw new InvalidDataException("图片尺寸无效或过大。");}
    using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=stream;if(width>=height)bitmap.DecodePixelWidth=Math.Min(width,longest);else bitmap.DecodePixelHeight=Math.Min(height,longest);bitmap.EndInit();bitmap.Freeze();cancellation.ThrowIfCancellationRequested();return Preview32(bitmap);}
   }catch(Exception ex){if(ex is OperationCanceledException)throw;if(!ImageCodec.IsDecodeFailure(ex)||!ImageCodec.Supports(typePath))throw;}
   return Preview32(ImageCodec.Render(path,longest,cancellation,out width,out height));
  }
  static BitmapSource Preview32(BitmapSource image){if(image.Format==PixelFormats.Pbgra32)return image;var converted=new FormatConvertedBitmap(image,PixelFormats.Pbgra32,null,0);int stride=checked(image.PixelWidth*4);byte[] pixels=new byte[checked(stride*image.PixelHeight)];converted.CopyPixels(pixels,stride,0);var preview=BitmapSource.Create(image.PixelWidth,image.PixelHeight,96,96,PixelFormats.Pbgra32,null,pixels,stride);preview.Freeze();return preview;}
 }
 internal sealed class FreeCanvasViewport {
  internal double Zoom=1,PanX,PanY;
  internal Point World(Point point){return new Point((point.X-PanX)/Zoom,(point.Y-PanY)/Zoom);}
  internal Point Screen(Point point){return new Point(point.X*Zoom+PanX,point.Y*Zoom+PanY);}
  internal void ZoomAt(double factor,Point cursor){Point anchor=World(cursor);Zoom=Math.Max(.02,Math.Min(16,Zoom*factor));PanX=cursor.X-anchor.X*Zoom;PanY=cursor.Y-anchor.Y*Zoom;}
  internal void Fit(IEnumerable<FreeCanvasItem> items,double width,double height){Rect bounds=Rect.Empty;foreach(var item in items)bounds.Union(item.Bounds());if(bounds.IsEmpty){Zoom=1;PanX=PanY=0;return;}Zoom=Math.Max(.02,Math.Min(2,Math.Min(Math.Max(1,width-96)/Math.Max(1,bounds.Width),Math.Max(1,height-96)/Math.Max(1,bounds.Height))));PanX=(width-bounds.Width*Zoom)/2-bounds.X*Zoom;PanY=(height-bounds.Height*Zoom)/2-bounds.Y*Zoom;}
 }
 internal static class FreeCanvasGeometry {
  internal static void Move(FreeCanvasItem target,FreeCanvasItem original,Vector delta){target.X=original.X+delta.X;target.Y=original.Y+delta.Y;if(target.HasEndpoints||target.Kind=="stroke"){target.EndX=original.EndX+delta.X;target.EndY=original.EndY+delta.Y;}if(target.Kind=="stroke"&&original.Points!=null)target.Points=original.Points.Select(p=>new FreeCanvasPoint{X=p.X+delta.X,Y=p.Y+delta.Y}).ToList();}
  internal static void Resize(FreeCanvasItem target,FreeCanvasItem original,string corner,Point world,double zoom){
   Rect bounds=original.Bounds();double anchorX=corner.Contains("w")?bounds.Right:bounds.Left,anchorY=corner.Contains("n")?bounds.Bottom:bounds.Top;double ratio=bounds.Width/bounds.Height;double width=Math.Max(4/zoom,Math.Abs(world.X-anchorX)),height=Math.Max(4/zoom,Math.Abs(world.Y-anchorY));if(target.Kind=="image"){if(width/height>ratio)height=width/ratio;else width=height*ratio;}double left=corner.Contains("w")?anchorX-width:anchorX,top=corner.Contains("n")?anchorY-height:anchorY;
   target.Width=width;target.Height=height;
   if(target.HasEndpoints){target.X=left+(original.X-bounds.X)*width/bounds.Width;target.Y=top+(original.Y-bounds.Y)*height/bounds.Height;target.EndX=left+(original.EndX-bounds.X)*width/bounds.Width;target.EndY=top+(original.EndY-bounds.Y)*height/bounds.Height;}
   else if(target.Kind=="stroke"&&original.Points!=null){
    // Preserve pen width while fitting the centerline inside the resized
    // envelope. A one-point stroke remains a dot rather than a filled shape.
    double pad=Math.Max(.5,original.Stroke/2),innerWidth=Math.Max(0,bounds.Width-pad*2),innerHeight=Math.Max(0,bounds.Height-pad*2),targetWidth=Math.Max(0,width-pad*2),targetHeight=Math.Max(0,height-pad*2);
    target.Points=original.Points.Select(p=>new FreeCanvasPoint{X=innerWidth>0?left+pad+(p.X-(bounds.X+pad))*targetWidth/innerWidth:left+width/2,Y=innerHeight>0?top+pad+(p.Y-(bounds.Y+pad))*targetHeight/innerHeight:top+height/2}).ToList();target.X=target.Points[0].X;target.Y=target.Points[0].Y;target.EndX=target.Points[target.Points.Count-1].X;target.EndY=target.Points[target.Points.Count-1].Y;
   }else{target.X=left;target.Y=top;}
  }
 }
}
