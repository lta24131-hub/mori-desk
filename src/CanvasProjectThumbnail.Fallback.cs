using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MoriDesk {
 internal static partial class CanvasProjectThumbnail {
  // This is a read-only overview, not another canvas editor. Exact SDK exports
  // take precedence; saved scenes without one still get recognizable thumbnails
  // without starting WebView, opening windows or rewriting a project.
  static bool PreviewFailure(Exception error){return error is IOException||error is InvalidDataException||error is UnauthorizedAccessException||error is ArgumentException||error is InvalidOperationException||error is NotSupportedException||error is FormatException||error is System.Security.SecurityException;}
  static Dictionary<string,object> MapValue(object value){return value as Dictionary<string,object>??new Dictionary<string,object>();}
  static object Value(Dictionary<string,object> map,string key){object result;return map.TryGetValue(key,out result)?result:null;}
  static string Word(Dictionary<string,object> map,string key,string fallback=""){return Value(map,key) as string??fallback;}
  static double Number(Dictionary<string,object> map,string key,double fallback=0){var value=Value(map,key);return value==null?fallback:Convert.ToDouble(value,CultureInfo.InvariantCulture);}
  static bool Flag(Dictionary<string,object> map,string key){return Value(map,key) is bool&&(bool)map[key];}
  static Point Position(object value){var point=MapValue(value);return new Point(Number(point,"x"),Number(point,"y"));}
  static object[] ArrayValue(object value){return value as object[]??new object[0];}
  sealed class PreviewNode {
   internal Dictionary<string,object> Record,Props;
   internal string Id,Type;
   internal Matrix World;
   internal double Opacity;
   internal Rect Bounds;
   internal readonly List<PreviewNode> Children=new List<PreviewNode>();
  }
  static Brush Palette(string color){
   string[] names={"black","grey","light-violet","violet","blue","light-blue","yellow","orange","green","light-green","light-red","red","white"};
   string[] values={"#1D1D1D","#9FA8B2","#E085F4","#AE3EC9","#4465E9","#4BA1F1","#F1AC4B","#E16919","#099268","#4CB05E","#F87777","#E03131","#FFFFFF"};
   int index=System.Array.IndexOf(names,color);var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(values[index<0?0:index]));brush.Freeze();return brush;
  }
  static double SizeValue(Dictionary<string,object> props,bool font=false){string size=Word(props,"size","m");double value=font?(size=="s"?18:size=="l"?36:size=="xl"?44:24):(size=="s"?2:size=="l"?5:size=="xl"?10:3.5);return value*Number(props,"scale",1);}
  static FormattedText SceneText(Dictionary<string,object> props,string text,double width){
   string family=Word(props,"font")=="serif"?"Georgia":Word(props,"font")=="mono"?"Consolas":"Microsoft YaHei UI";
   var formatted=new FormattedText(text,CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,new Typeface(family),Math.Min(3000,Math.Max(1,SizeValue(props,true)+Number(props,"fontSizeAdjustment"))),Palette(Word(props,"labelColor",Word(props,"color","black"))));
   formatted.MaxTextWidth=Math.Min(1000000,Math.Max(1,width));
   string align=Word(props,"textAlign",Word(props,"align","start"));formatted.TextAlignment=align.StartsWith("middle",StringComparison.Ordinal)?TextAlignment.Center:align.StartsWith("end",StringComparison.Ordinal)?TextAlignment.Right:TextAlignment.Left;
   return formatted;
  }
  static List<Point[]> Segments(Dictionary<string,object> props){return ArrayValue(Value(props,"segments")).Select(value=>ArrayValue(Value(MapValue(value),"points")).Select(Position).ToArray()).ToList();}
  static Rect PointsBounds(IEnumerable<Point> points){Rect bounds=Rect.Empty;foreach(var point in points)bounds.Union(point);return bounds;}
  static Rect NodeBounds(PreviewNode node){
   var props=node.Props;
   if(node.Type=="group")return Rect.Empty;
   if(node.Type=="line")return PointsBounds(MapValue(Value(props,"points")).Values.Select(MapValue).OrderBy(p=>Word(p,"index"),StringComparer.Ordinal).Select(p=>new Point(Number(p,"x"),Number(p,"y"))));
   if(node.Type=="arrow"){var bounds=PointsBounds(new[]{Position(Value(props,"start")),Position(Value(props,"end"))});bounds.Inflate(Math.Abs(Number(props,"bend")),Math.Abs(Number(props,"bend")));return bounds;}
   if(node.Type=="draw"||node.Type=="highlight")return PointsBounds(Segments(props).SelectMany(segment=>segment));
   if(node.Type=="text"){double width=Number(props,"w",300)*Number(props,"scale",1);var text=SceneText(props,Word(props,"text"),width);return new Rect(0,0,Math.Max(1,width),Math.Max(1,text.Height));}
   return new Rect(0,0,Math.Max(1,Number(props,"w",node.Type=="note"?200:100)),Math.Max(1,Number(props,"h",node.Type=="note"?200:100)+Number(props,"growY")));
  }
  static void ResolveNodes(List<PreviewNode> nodes,Matrix parent,double opacity,int depth,List<PreviewNode> visible){
   if(depth>64)return;
   foreach(var node in nodes.OrderBy(n=>Word(n.Record,"index"),StringComparer.Ordinal)){
    var local=Matrix.Identity;local.Rotate(Number(node.Record,"rotation")*180/Math.PI);local.Translate(Number(node.Record,"x"),Number(node.Record,"y"));local.Append(parent);node.World=local;node.Opacity=opacity*Number(node.Record,"opacity",1);
    node.Bounds=NodeBounds(node);if(node.Opacity>0)visible.Add(node);ResolveNodes(node.Children,local,node.Opacity,depth+1,visible);
   }
  }
  internal static BitmapSource RenderMature(MatureCanvasStore store,MatureCanvasDocument document){
   // Validate via the normal reader before calling this method. Resources are
   // resolved only through its registered local identities, never scene URLs.
   var scene=MapValue(new System.Web.Script.Serialization.JavaScriptSerializer{MaxJsonLength=MatureCanvasStore.MaximumSceneBytes*2,RecursionLimit=48}.DeserializeObject(document.SceneJson));
   var records=MapValue(Value(MapValue(Value(scene,"document")),"store"));var session=MapValue(Value(scene,"session"));string page=Word(session,"currentPageId");
   if(string.IsNullOrEmpty(page))page=records.Values.Select(MapValue).Where(r=>Word(r,"typeName")=="page").OrderBy(r=>Word(r,"index"),StringComparer.Ordinal).Select(r=>Word(r,"id")).FirstOrDefault();
   var nodes=records.Values.Select(MapValue).Where(r=>Word(r,"typeName")=="shape").Select(r=>new PreviewNode{Record=r,Props=MapValue(Value(r,"props")),Id=Word(r,"id"),Type=Word(r,"type")}).ToDictionary(n=>n.Id,StringComparer.Ordinal);
   var roots=new List<PreviewNode>();foreach(var node in nodes.Values){string parent=Word(node.Record,"parentId");PreviewNode ancestor;if(parent==page)roots.Add(node);else if(nodes.TryGetValue(parent,out ancestor))ancestor.Children.Add(node);}
   var visible=new List<PreviewNode>();ResolveNodes(roots,Matrix.Identity,1,0,visible);Rect bounds=Rect.Empty;
   foreach(var node in visible)if(!node.Bounds.IsEmpty){var current=new MatrixTransform(node.World).TransformBounds(node.Bounds);current.Inflate(Math.Max(1,SizeValue(node.Props)),Math.Max(1,SizeValue(node.Props)));bounds.Union(current);}
   if(bounds.IsEmpty)return null;
   const int width=320,height=200;double scale=Math.Min((width-24)/Math.Max(1,bounds.Width),(height-24)/Math.Max(1,bounds.Height));
   var fit=new Matrix(scale,0,0,scale,(width-bounds.Width*scale)/2-bounds.X*scale,(height-bounds.Height*scale)/2-bounds.Y*scale);
   var resources=document.Resources.ToDictionary(r=>r.FileId,StringComparer.Ordinal);long decodedBytes=0;var visual=new DrawingVisual();
   using(var drawing=visual.RenderOpen()){
    drawing.DrawRectangle(Brushes.White,null,new Rect(0,0,width,height));drawing.PushClip(new RectangleGeometry(new Rect(0,0,width,height)));drawing.PushTransform(new MatrixTransform(fit));
    foreach(var node in visible){drawing.PushTransform(new MatrixTransform(node.World));drawing.PushOpacity(node.Opacity);DrawNode(drawing,node,store,document,records,resources,scale,ref decodedBytes);drawing.Pop();drawing.Pop();}
    drawing.Pop();drawing.Pop();
   }
   var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);bitmap.Freeze();return bitmap;
  }
  static Geometry ScenePath(IEnumerable<Point> points,bool closed=false){var values=points.ToArray();if(values.Length==0)return Geometry.Empty;var path=new StreamGeometry();using(var context=path.Open()){context.BeginFigure(values[0],closed,closed);for(int i=1;i<values.Length;i++)context.LineTo(values[i],true,false);}path.Freeze();return path;}
  static Pen ScenePen(Dictionary<string,object> props,bool draw=false){var pen=new Pen(Palette(Word(props,"color","black")),Math.Max(.1,SizeValue(props)+(draw?Number(props,"scale",1):0))){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round};string dash=Word(props,"dash");if(dash=="dashed")pen.DashStyle=DashStyles.Dash;else if(dash=="dotted")pen.DashStyle=DashStyles.Dot;return pen;}
  static void ArrowHead(DrawingContext drawing,Pen pen,Point end,Vector toward,string kind){if(kind=="none"||toward.LengthSquared<.00001)return;toward.Normalize();double length=Math.Max(10,pen.Thickness*3.5);var side=new Vector(-toward.Y,toward.X)*length*.5;var back=end+toward*length;if(kind=="dot"){drawing.DrawEllipse(pen.Brush,null,end,length*.4,length*.4);return;}if(kind=="bar"||kind=="pipe"){drawing.DrawLine(pen,end+side,end-side);return;}var shape=ScenePath(new[]{back+side,end,back-side},kind!="arrow");drawing.DrawGeometry(kind=="arrow"?null:pen.Brush,pen,shape);}
  static void DrawNode(DrawingContext drawing,PreviewNode node,MatureCanvasStore store,MatureCanvasDocument document,Dictionary<string,object> records,Dictionary<string,MatureCanvasResource> resources,double scale,ref long decodedBytes){
   var props=node.Props;var bounds=node.Bounds;var pen=ScenePen(props,node.Type=="draw");if(node.Type=="group")return;
   if(node.Type=="image"){
    try{
     var asset=MapValue(Value(records,Word(props,"assetId")));var assetProps=MapValue(Value(asset,"props"));string url=Word(assetProps,"src"),prefix=MatureCanvasStore.TrustedOrigin+"/resource/"+document.Id+"/";MatureCanvasResource resource;
     if(!url.StartsWith(prefix,StringComparison.Ordinal)||!resources.TryGetValue(url.Substring(prefix.Length),out resource))throw new InvalidDataException("未注册的预览资源。");
     const long budget=4L*1024*1024;if(decodedBytes>=budget)throw new InvalidDataException("预览图片预算已用完。");int dimension=Math.Max(1,Math.Min(DecodeDimension,(int)Math.Ceiling(Math.Max(bounds.Width,bounds.Height)*scale)));dimension=Math.Min(dimension,Math.Max(1,(int)Math.Sqrt((budget-decodedBytes)/4)));
     string path=store.ResourcePath(document,resource);int imageWidth,imageHeight;BitmapSource image=FreeCanvasStore.Decode(path,path,dimension,System.Threading.CancellationToken.None,out imageWidth,out imageHeight);decodedBytes+=(long)image.PixelWidth*image.PixelHeight*4;
     var crop=Value(props,"crop") as Dictionary<string,object>;if(crop!=null){var top=Position(Value(crop,"topLeft"));var bottom=Position(Value(crop,"bottomRight"));int x=Math.Max(0,Math.Min(image.PixelWidth-1,(int)Math.Floor(top.X*image.PixelWidth))),y=Math.Max(0,Math.Min(image.PixelHeight-1,(int)Math.Floor(top.Y*image.PixelHeight)));int w=Math.Max(1,Math.Min(image.PixelWidth-x,(int)Math.Ceiling((bottom.X-top.X)*image.PixelWidth))),h=Math.Max(1,Math.Min(image.PixelHeight-y,(int)Math.Ceiling((bottom.Y-top.Y)*image.PixelHeight)));image=new CroppedBitmap(image,new Int32Rect(x,y,w,h));}
     drawing.PushTransform(new ScaleTransform(Flag(props,"flipX")?-1:1,Flag(props,"flipY")?-1:1,bounds.Width/2,bounds.Height/2));drawing.DrawImage(image,bounds);drawing.Pop();
    }catch(Exception error){if(!PreviewFailure(error))throw;drawing.DrawRectangle(Brushes.LightGray,new Pen(Brushes.Gray,Math.Max(.1,1/scale)),bounds);}return;
   }
   if(node.Type=="text"){drawing.DrawText(SceneText(props,Word(props,"text"),bounds.Width),new Point());return;}
   if(node.Type=="line"){var points=MapValue(Value(props,"points")).Values.Select(MapValue).OrderBy(p=>Word(p,"index"),StringComparer.Ordinal).Select(p=>new Point(Number(p,"x"),Number(p,"y")));drawing.DrawGeometry(null,pen,ScenePath(points));return;}
   if(node.Type=="arrow"){
    var start=Position(Value(props,"start"));var end=Position(Value(props,"end"));var direction=end-start;double bend=Number(props,"bend");Point control=(Point)((Vector)start+(Vector)end);control=new Point(control.X/2,control.Y/2);if(direction.LengthSquared>.0001){var normal=new Vector(-direction.Y,direction.X);normal.Normalize();control+=normal*bend*2;}
    if(Math.Abs(bend)<.001)drawing.DrawLine(pen,start,end);else {var curve=new StreamGeometry();using(var context=curve.Open()){context.BeginFigure(start,false,false);context.QuadraticBezierTo(control,end,true,false);}drawing.DrawGeometry(null,pen,curve);}
    ArrowHead(drawing,pen,start,Math.Abs(bend)<.001?direction:control-start,Word(props,"arrowheadStart","none"));ArrowHead(drawing,pen,end,Math.Abs(bend)<.001?-direction:control-end,Word(props,"arrowheadEnd","arrow"));
   }else if(node.Type=="draw"||node.Type=="highlight"){
    if(node.Type=="highlight")drawing.PushOpacity(.35);
    foreach(var segment in Segments(props)){if(segment.Length==1)drawing.DrawEllipse(pen.Brush,null,segment[0],pen.Thickness/2,pen.Thickness/2);else drawing.DrawGeometry(Flag(props,"isClosed")&&Word(props,"fill")!="none"?pen.Brush:null,pen,ScenePath(segment,Flag(props,"isClosed")));}
    if(node.Type=="highlight")drawing.Pop();return;
   }else{
    Brush fill=Word(props,"fill","none")=="none"?null:Palette(Word(props,"color","black"));if(node.Type=="note")fill=Palette(Word(props,"color","yellow"));if(node.Type=="frame")fill=Brushes.White;
    string shape=Word(props,"geo","rectangle");if(shape=="ellipse"||shape=="oval")drawing.DrawEllipse(fill,pen,new Point(bounds.Width/2,bounds.Height/2),bounds.Width/2,bounds.Height/2);
    else if(shape=="diamond")drawing.DrawGeometry(fill,pen,ScenePath(new[]{new Point(bounds.Width/2,0),new Point(bounds.Width,bounds.Height/2),new Point(bounds.Width/2,bounds.Height),new Point(0,bounds.Height/2)},true));
    else if(shape=="triangle")drawing.DrawGeometry(fill,pen,ScenePath(new[]{new Point(bounds.Width/2,0),new Point(bounds.Width,bounds.Height),new Point(0,bounds.Height)},true));
    else drawing.DrawRectangle(fill,pen,bounds);
   }
   string label=Word(props,"text",node.Type=="frame"?Word(props,"name"):"");if(label.Length>0){var text=SceneText(props,label,Math.Max(1,bounds.Width-12));drawing.DrawText(text,new Point(6,Math.Max(2,(bounds.Height-text.Height)/2)));}
  }
 }
}
