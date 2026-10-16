using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mori;

namespace MoriDesk {
 internal static class CanvasLineSelectionTests {
  const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
  static object Field(FreeCanvasWindow window,string name){return typeof(FreeCanvasWindow).GetField(name,Hidden).GetValue(window);}
  static void Field(FreeCanvasWindow window,string name,object value){typeof(FreeCanvasWindow).GetField(name,Hidden).SetValue(window,value);}
  static void Invoke(FreeCanvasWindow window,string name,params object[] arguments){typeof(FreeCanvasWindow).GetMethod(name,Hidden,null,arguments.Select(a=>a.GetType()).ToArray(),null).Invoke(window,arguments);}
  static FreeCanvasItem Item(FreeCanvasWindow window,string id){return window.DocumentForTests.Items.Single(i=>i.Id==id);}
  static FreeCanvasViewport View(FreeCanvasWindow window){return (FreeCanvasViewport)Field(window,"view");}
  static void Select(FreeCanvasWindow window,params string[] ids){Invoke(window,"Mode","select");var selected=(HashSet<string>)Field(window,"selected");selected.Clear();foreach(string id in ids)selected.Add(id);Invoke(window,"Render");}
  static FreeCanvasItem Draw(FreeCanvasWindow window,string kind,Point start,Point end){if(!window.BeginDrawingForTests(kind,start))throw new Exception("Line selection fixture gesture did not start.");window.EndDrawingForTests(end);return window.DocumentForTests.Items.Last();}
  static bool Same(FreeCanvasItem current,FreeCanvasItem original){return current.Id==original.Id&&current.Kind==original.Kind&&current.X==original.X&&current.Y==original.Y&&current.EndX==original.EndX&&current.EndY==original.EndY&&current.Width==original.Width&&current.Height==original.Height&&current.Stroke==original.Stroke&&current.Color==original.Color;}
  // Set only the state normally established by MouseDown. Actual pointer
  // movement and commit still run the production UpdateGesture/FinishGesture.
  static void Drag(FreeCanvasWindow window,string id,string endpoint,Point downWorld,Point toWorld,bool commit){
   var item=Item(window,id);Select(window,id);var view=View(window);
   Field(window,"before",new List<FreeCanvasItem>{item.Copy()});Field(window,"downWorld",downWorld);Field(window,"downScreen",view.Screen(downWorld));Field(window,"lastScreen",view.Screen(downWorld));
   Field(window,"resizeCorner",endpoint);Field(window,"resizing",endpoint!=null);Field(window,"gesture",true);
   Invoke(window,"UpdateGesture",view.Screen(toWorld));Invoke(window,"FinishGesture",commit);
  }
  static string Corner(FreeCanvasWindow window,Point world,FreeCanvasItem item){return (string)typeof(FreeCanvasWindow).GetMethod("Corner",Hidden).Invoke(window,new object[]{View(window).Screen(world),item});}
  sealed class Picture {
   internal readonly RenderTargetBitmap Bitmap;
   readonly byte[] pixels;
   readonly int surfaceX,surfaceY,width;
   internal Picture(FreeCanvasWindow window){
    const int height=700;width=1000;var content=(FrameworkElement)window.Content;content.Width=width;content.Height=height;content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();
    Bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);Bitmap.Render(content);pixels=new byte[width*height*4];Bitmap.CopyPixels(pixels,width*4,0);Point offset=window.SurfaceForTests.TranslatePoint(new Point(),content);surfaceX=(int)Math.Round(offset.X);surfaceY=(int)Math.Round(offset.Y);
   }
   Color Pixel(int x,int y){int index=((y+surfaceY)*width+x+surfaceX)*4;return Color.FromArgb(pixels[index+3],pixels[index+2],pixels[index+1],pixels[index]);}
   internal bool BackgroundNear(Point point,Color color){int cx=(int)Math.Round(point.X),cy=(int)Math.Round(point.Y);for(int y=cy-2;y<=cy+2;y++)for(int x=cx-2;x<=cx+2;x++)if(Pixel(x,y)!=color)return false;return true;}
   internal bool AccentNear(Point point,Color color,int radius){int cx=(int)Math.Round(point.X),cy=(int)Math.Round(point.Y);for(int y=cy-radius;y<=cy+radius;y++)for(int x=cx-radius;x<=cx+radius;x++){Color pixel=Pixel(x,y);if(Math.Abs(pixel.R-color.R)<8&&Math.Abs(pixel.G-color.G)<8&&Math.Abs(pixel.B-color.B)<8&&pixel.A>240)return true;}return false;}
   internal void Save(string path){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(Bitmap));using(var output=File.Create(path))encoder.Save(output);}
  }
  static Point[] FormerFrame(FreeCanvasItem item){var bounds=item.Bounds();return new[]{new Point((bounds.Left+bounds.Right)/2,bounds.Top),new Point((bounds.Left+bounds.Right)/2,bounds.Bottom),new Point(bounds.Left,(bounds.Top+bounds.Bottom)/2),new Point(bounds.Right,(bounds.Top+bounds.Bottom)/2)};}
  static void CheckSelection(Picture image,FreeCanvasItem item,Action<bool,string> check,string label,bool endpointHandles=true){
   Color background=((SolidColorBrush)Theme.Brush("Canvas")).Color,accent=((SolidColorBrush)Theme.Brush("Accent")).Color;
   check(FormerFrame(item).All(point=>image.BackgroundNear(point,background)),label+" selects only the visible path, without an unrelated rectangular perimeter");
   check(image.AccentNear(new Point((item.X+item.EndX)/2,(item.Y+item.EndY)/2),accent,3),label+" shows an accent highlight on the actual path");
   if(endpointHandles)check(image.AccentNear(new Point(item.X+4,item.Y-4),accent,1)&&image.AccentNear(new Point(item.EndX+4,item.EndY-4),accent,1),label+" shows independently visible handles at both real endpoints");
  }
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);var app=Application.Current??new Application();var shutdown=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;string theme=Theme.Current,accent=Theme.AccentKey;FreeCanvasWindow window=null,reopened=null;
   try{
    Theme.Set("light");string data=Path.Combine(root,"profile");window=new FreeCanvasWindow(data,new Settings());new Picture(window);window.SetDrawingStyleForTests("#333333",3);
    var line=Draw(window,"line",new Point(80,100),new Point(260,230));var arrow=Draw(window,"arrow",new Point(420,100),new Point(600,230));string lineId=line.Id,arrowId=arrow.Id;var originalLine=line.Copy();var originalArrow=arrow.Copy();
    Select(window);var plain=new Picture(window);Color background=((SolidColorBrush)Theme.Brush("Canvas")).Color;
    check(FormerFrame(line).Concat(FormerFrame(arrow)).All(point=>plain.BackgroundNear(point,background)),"unselected line and arrow do not have rectangular borders");
    check(!plain.BackgroundNear(new Point(170,165),background)&&!plain.BackgroundNear(new Point(510,165),background),"unselected paths remain visible with their original drawing color");
    check(!plain.BackgroundNear(new Point(589,230),background),"unselected arrow retains its non-collinear arrowhead");
    foreach(string appearance in new[]{"light","dark"}){
     Theme.Set(appearance);Select(window,lineId);var lineImage=new Picture(window);CheckSelection(lineImage,line,check,appearance+" single line");lineImage.Save(Path.Combine(root,"line-selection-"+appearance+".png"));
     Select(window,arrowId);var arrowImage=new Picture(window);CheckSelection(arrowImage,arrow,check,appearance+" single arrow");arrowImage.Save(Path.Combine(root,"arrow-selection-"+appearance+".png"));
     Select(window,lineId,arrowId);var multiple=new Picture(window);CheckSelection(multiple,line,check,appearance+" multiple-selection line",false);CheckSelection(multiple,arrow,check,appearance+" multiple-selection arrow",false);
     check(Same(Item(window,lineId),originalLine)&&Same(Item(window,arrowId),originalArrow),"selection painting in "+appearance+" theme never mutates stored geometry or drawing style");
    }
    Theme.Set("light");Select(window,lineId);
    check(window.HitForTests(new Point(170,165))==line&&window.HitForTests(new Point(170,100))==null,"line picking follows its path rather than its former frame");
    check(Corner(window,new Point(line.X,line.Y),line)=="start"&&Corner(window,new Point(line.EndX,line.EndY),line)=="end"&&Corner(window,new Point(line.X,line.EndY),line)==null,"line editing exposes only actual endpoint handles, not empty rectangle corners");
    Drag(window,lineId,null,new Point(170,165),new Point(210,195),true);line=Item(window,lineId);
    check(line.X==originalLine.X+40&&line.Y==originalLine.Y+30&&line.EndX==originalLine.EndX+40&&line.EndY==originalLine.EndY+30&&line.Stroke==3,"actual whole-path move translates both endpoints without changing thickness");
    window.UndoForTests();line=Item(window,lineId);check(Same(line,originalLine),"whole-path move undo restores the original complete line");
    Drag(window,lineId,"start",new Point(line.X,line.Y),new Point(100,130),true);line=Item(window,lineId);
    check(line.X==100&&line.Y==130&&line.EndX==originalLine.EndX&&line.EndY==originalLine.EndY,"actual start-handle drag changes only the start endpoint");
    check(window.SaveForTests(),"endpoint edit can be committed before undo");window.UndoForTests();check(Same(Item(window,lineId),originalLine)&&window.SaveForTests(),"undo after committed endpoint edit restores the line on disk");
    arrow=Item(window,arrowId);Drag(window,arrowId,"end",new Point(arrow.EndX,arrow.EndY),new Point(650,260),true);arrow=Item(window,arrowId);
    check(arrow.X==originalArrow.X&&arrow.Y==originalArrow.Y&&arrow.EndX==650&&arrow.EndY==260&&arrow.Kind=="arrow","actual arrow end-handle drag retains the start and arrow kind");
    check(window.HitForTests(new Point((arrow.X+arrow.EndX)/2,(arrow.Y+arrow.EndY)/2))==arrow,"edited arrow remains selectable on its new shaft");
    var editedArrow=arrow.Copy();Drag(window,arrowId,"start",new Point(arrow.X,arrow.Y),new Point(370,70),false);check(Same(Item(window,arrowId),editedArrow),"cancelling an endpoint drag restores its prior geometry without committing a draft");
    check(window.SaveForTests(),"line and arrow edits save successfully with path-only selection");string project=window.DocumentForTests.Id;var store=new FreeCanvasStore(data);var saved=store.Load(project);check(Same(saved.Items.Single(i=>i.Id==lineId),originalLine)&&Same(saved.Items.Single(i=>i.Id==arrowId),editedArrow),"project records persist the line and edited arrow geometry without selection overlays");
    window.Close();window=null;reopened=new FreeCanvasWindow(data,new Settings());check(reopened.DocumentForTests.Id==project&&Same(Item(reopened,lineId),originalLine)&&Same(Item(reopened,arrowId),editedArrow)&&!reopened.DirtyForTests,"close and reopen retain the exact edited paths without creating a dirty draft");
    Select(reopened,arrowId);CheckSelection(new Picture(reopened),Item(reopened,arrowId),check,"reopened arrow");check(!reopened.IsVisible,"all path-selection fixtures remain offscreen without modifying a real desktop window");
   }finally{if(window!=null)window.Close();if(reopened!=null)reopened.Close();Theme.Set(theme);Theme.SetAccent(accent);app.ShutdownMode=shutdown;}
  }
 }
}
