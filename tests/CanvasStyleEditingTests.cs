using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mori;

namespace MoriDesk {
 internal static class CanvasStyleEditingTests {
  const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
  static IEnumerable<DependencyObject> Children(DependencyObject root){yield return root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Children(VisualTreeHelper.GetChild(root,i)))yield return child;}
  static T Named<T>(FrameworkElement root,string name)where T:FrameworkElement{return Children(root).OfType<T>().Single(c=>c.Name==name);}
  static void Click(Button button){button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
  static void Layout(FrameworkElement content){content.Width=1000;content.Height=700;content.Measure(new Size(1000,700));content.Arrange(new Rect(0,0,1000,700));content.UpdateLayout();}
  static object Field(FreeCanvasWindow window,string name){return typeof(FreeCanvasWindow).GetField(name,Hidden).GetValue(window);}
  static FreeCanvasViewport View(FreeCanvasWindow window){return (FreeCanvasViewport)Field(window,"view");}
  static int UndoCount(FreeCanvasWindow window){return ((List<Action>)Field(window,"undo")).Count;}
  static FreeCanvasItem Item(FreeCanvasWindow window,string id){return window.DocumentForTests.Items.Single(i=>i.Id==id);}
  static Dictionary<string,FreeCanvasItem> Snapshot(FreeCanvasWindow window){return window.DocumentForTests.Items.ToDictionary(i=>i.Id,i=>i.Copy());}
  static bool Same(FreeCanvasItem current,FreeCanvasItem original){
   if(current.Id!=original.Id||current.Kind!=original.Kind||current.Color!=original.Color||current.Stroke!=original.Stroke||current.X!=original.X||current.Y!=original.Y||current.EndX!=original.EndX||current.EndY!=original.EndY||current.Width!=original.Width||current.Height!=original.Height||current.Text!=original.Text||current.FontSize!=original.FontSize||current.Image!=original.Image||current.Source!=original.Source)return false;
   if(current.Points==null||original.Points==null)return current.Points==original.Points;
   return current.Points.Count==original.Points.Count&&current.Points.Zip(original.Points,(a,b)=>a.X==b.X&&a.Y==b.Y).All(v=>v);
  }
  static bool SameAll(FreeCanvasWindow window,Dictionary<string,FreeCanvasItem> original){return window.ItemCount==original.Count&&window.DocumentForTests.Items.All(i=>original.ContainsKey(i.Id)&&Same(i,original[i.Id]));}
  static bool SameGeometry(FreeCanvasItem current,FreeCanvasItem original){var copy=current.Copy();copy.Kind=original.Kind;copy.Color=original.Color;copy.Stroke=original.Stroke;return Same(copy,original);}
  static ComboBoxItem Choice(ComboBox control,object value){return control.Items.Cast<ComboBoxItem>().Single(i=>object.Equals(i.Tag,value));}
  static object Value(ComboBox control){var selected=control.SelectedItem as ComboBoxItem;return selected==null?null:selected.Tag;}
  // Setting a real control's selected item raises its production
  // SelectionChanged handler; no test helper implements the style edit.
  static void Choose(ComboBox control,object value){control.SelectedItem=Choice(control,value);}
  static void ReplaySelection(ComboBox control){var item=control.SelectedItem;control.RaiseEvent(new SelectionChangedEventArgs(Selector.SelectionChangedEvent,new object[0],new object[]{item}));}
  static FreeCanvasItem Draw(FreeCanvasWindow window,string kind,Point from,Point to){if(!window.BeginDrawingForTests(kind,from))throw new Exception("Canvas style fixture gesture did not begin.");window.EndDrawingForTests(to);return window.DocumentForTests.Items.Last();}
  static FreeCanvasItem Add(FreeCanvasWindow window,FreeCanvasItem item){window.DrawItemForTests(item);return item;}
  static string Id(){return Guid.NewGuid().ToString("N");}
  static void PictureFile(FreeCanvasStore store,string project,string file){string path=store.ImagePath(project,file);Directory.CreateDirectory(Path.GetDirectoryName(path));byte[] pixels=Enumerable.Repeat((byte)170,40*30*4).ToArray();for(int i=3;i<pixels.Length;i+=4)pixels[i]=255;var bitmap=BitmapSource.Create(40,30,96,96,PixelFormats.Pbgra32,null,pixels,160);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var output=File.Create(path))encoder.Save(output);}
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);var app=Application.Current??new Application();var shutdown=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;string theme=Theme.Current,accent=Theme.AccentKey;FreeCanvasWindow window=null,reopened=null;
   try{
    Theme.Set("light");string data=Path.Combine(root,"profile");var store=new FreeCanvasStore(data);window=new FreeCanvasWindow(data,new Settings());var content=(FrameworkElement)window.Content;Layout(content);
    var colors=Named<ComboBox>(content,"CanvasInkColor");var widths=Named<ComboBox>(content,"CanvasStrokeWidth");var types=Named<ComboBox>(content,"CanvasLineType");var context=Named<TextBlock>(content,"CanvasStyleContext");var undo=Named<Button>(content,"CanvasUndo");
    Click(Named<Button>(content,"CanvasTool_line"));Choose(colors,"#547FC3");Choose(widths,8.0);
    var line=Draw(window,"line",new Point(100,160),new Point(250,210));string lineId=line.Id;
    var arrow=Add(window,new FreeCanvasItem{Id=Id(),Kind="arrow",Color="#4C9277",Stroke=2,X=340,Y=170,EndX=460,EndY=240});string arrowId=arrow.Id;
    var rectangle=Add(window,new FreeCanvasItem{Id=Id(),Kind="rectangle",Color="auto",Stroke=3,X=520,Y=130,EndX=650,EndY=240});string rectangleId=rectangle.Id;
    var stroke=Add(window,new FreeCanvasItem{Id=Id(),Kind="stroke",Color="#333333",Stroke=5,Points=new List<FreeCanvasPoint>{new FreeCanvasPoint{X=140,Y=300},new FreeCanvasPoint{X=190,Y=330},new FreeCanvasPoint{X=220,Y=290}}});string strokeId=stroke.Id;
    var text=Add(window,new FreeCanvasItem{Id=Id(),Kind="text",Color="auto",Text="样式仅修改选择内容",FontSize=22,X=350,Y=320,Width=290,Height=50});string textId=text.Id;
    var legacy=Add(window,new FreeCanvasItem{Id=Id(),Kind="line",Color="#2764B0",Stroke=6,X=420,Y=440,EndX=590,EndY=460});string legacyId=legacy.Id;
    string imageId=Id(),imageName=imageId+".png";PictureFile(store,window.DocumentForTests.Id,imageName);var image=Add(window,new FreeCanvasItem{Id=imageId,Kind="image",Image=imageName,Source=Path.Combine(root,"synthetic-image.png"),X=700,Y=150,Width=80,Height=60});
    check(Item(window,lineId).Color=="#547FC3"&&Item(window,lineId).Stroke==8,"real drawing color and width controls establish the default style before any selection edit");
    check(window.SaveForTests(),"style fixtures save using only an independent synthetic project profile");
    var original=Snapshot(window);int before=UndoCount(window);

    Click(Named<Button>(content,"CanvasTool_select"));window.SelectForTests(legacyId);
    check(object.Equals(Value(colors),"#2764B0")&&object.Equals(Value(widths),6.0),"non-preset styles from existing projects display their actual custom color and thickness instead of an unrelated preset");
    check(SameAll(window,original)&&UndoCount(window)==before&&!window.DirtyForTests,"displaying old custom style values is a read-only operation with no implicit normalization");
    window.SelectForTests(arrowId);Choose(types,"line");
    check(Item(window,arrowId).Kind=="line"&&Item(window,arrowId).Color==original[arrowId].Color&&Item(window,arrowId).Stroke==original[arrowId].Stroke&&SameGeometry(Item(window,arrowId),original[arrowId])&&UndoCount(window)==before+1,"an existing arrow converts back to a straight line through the actual type control without changing style or either endpoint");
    Click(undo);check(SameAll(window,original)&&UndoCount(window)==before&&window.SaveForTests(),"visible undo restores an existing arrow after arrow-to-line conversion");

    Click(Named<Button>(content,"CanvasTool_select"));window.SelectForTests(lineId);
    check(colors.IsEnabled&&widths.IsEnabled&&types.IsEnabled&&types.Visibility==Visibility.Visible,"selecting a line enables color, width and line-or-arrow editing without changing to a drawing tool");
    check(object.Equals(Value(colors),"#547FC3")&&object.Equals(Value(widths),8.0)&&object.Equals(Value(types),"line"),"selected-line properties show the selected object's actual style rather than the next-drawing defaults");
    check(!string.IsNullOrWhiteSpace(context.Text)&&SameAll(window,original)&&UndoCount(window)==before&&!window.DirtyForTests,"synchronizing selected properties is read-only and does not create undo or autosave work");
    Choose(colors,"#C45B61");check(Item(window,lineId).Color=="#C45B61"&&Item(window,lineId).Stroke==8&&Item(window,lineId).Kind=="line"&&SameGeometry(Item(window,lineId),original[lineId]),"actual color-control change updates the selected line and preserves its complete geometry");
    Choose(widths,5.0);check(Item(window,lineId).Color=="#C45B61"&&Item(window,lineId).Stroke==5&&SameGeometry(Item(window,lineId),original[lineId]),"actual thickness-control change updates only selected-line stroke width");
    Choose(types,"arrow");check(Item(window,lineId).Kind=="arrow"&&Item(window,lineId).Color=="#C45B61"&&Item(window,lineId).Stroke==5&&SameGeometry(Item(window,lineId),original[lineId]),"actual line-type control converts the selected line to an arrow without moving either endpoint");
    check(original.Where(p=>p.Key!=lineId).All(p=>Same(Item(window,p.Key),p.Value))&&UndoCount(window)==before+3,"three property edits each add one undo and leave every unselected image, text and path intact");
    Click(undo);check(Item(window,lineId).Kind=="line"&&Item(window,lineId).Color=="#C45B61"&&Item(window,lineId).Stroke==5,"visible undo restores the kind changed by the last line-to-arrow edit");
    Click(undo);check(Item(window,lineId).Kind=="line"&&Item(window,lineId).Color=="#C45B61"&&Item(window,lineId).Stroke==8,"second visible undo restores the previous width without undoing the earlier color edit");
    Click(undo);check(SameAll(window,original)&&UndoCount(window)==before,"third visible undo restores the exact original color, width, kind and coordinates");
    check(window.SaveForTests(),"undone style edits can be committed without changing the original synthetic content");
    window.SelectForTests(lineId);before=UndoCount(window);ReplaySelection(colors);ReplaySelection(widths);ReplaySelection(types);
    check(SameAll(window,original)&&UndoCount(window)==before&&!window.DirtyForTests,"replaying a same-value production selection event is a no-op with no redundant undo or dirty state");

    window.SelectForTests(lineId,arrowId,rectangleId,strokeId,textId,imageId);before=UndoCount(window);
    check(colors.IsEnabled&&widths.IsEnabled&&types.IsEnabled&&Value(colors)==null&&Value(widths)==null&&Value(types)==null,"mixed-selection property controls expose truthful mixed values without silently choosing a style");
    check(SameAll(window,original)&&UndoCount(window)==before&&!window.DirtyForTests,"entering a mixed style selection never applies a placeholder value to the document");
    Choose(colors,"#8266BD");check(new[]{lineId,arrowId,rectangleId,strokeId,textId}.All(id=>Item(window,id).Color=="#8266BD")&&Same(Item(window,imageId),original[imageId]),"actual multi-selection color event changes every editable selected annotation and never writes image properties");
    Choose(widths,12.0);check(new[]{lineId,arrowId,rectangleId,strokeId}.All(id=>Item(window,id).Stroke==12)&&Item(window,textId).Stroke==original[textId].Stroke&&Same(Item(window,imageId),original[imageId]),"actual multi-selection thickness event changes drawing objects but not text or images");
    Choose(types,"arrow");check(Item(window,lineId).Kind=="arrow"&&Item(window,arrowId).Kind=="arrow"&&Item(window,rectangleId).Kind=="rectangle"&&Item(window,strokeId).Kind=="stroke"&&Item(window,textId).Kind=="text"&&Same(Item(window,imageId),original[imageId]),"actual multi-selection type event converts line-like objects without converting rectangles, freehand, text or pictures");
    check(window.DocumentForTests.Items.All(i=>SameGeometry(i,original[i.Id]))&&UndoCount(window)==before+3,"multi-selection style edits preserve geometry and each control change has exactly one group undo");
    Click(undo);check(Item(window,lineId).Kind=="line"&&Item(window,arrowId).Kind=="arrow"&&Item(window,lineId).Stroke==12,"group type undo restores mixed line and arrow kinds without reversing width");
    Click(undo);check(new[]{lineId,arrowId,rectangleId,strokeId}.All(id=>Item(window,id).Stroke==original[id].Stroke)&&Item(window,textId).Color=="#8266BD","group width undo restores each object's individual original thickness and retains the color edit");
    Click(undo);check(SameAll(window,original)&&UndoCount(window)==before,"group color undo restores all distinct original values, including independent freehand points");
    check(window.SaveForTests(),"multi-selection undo restores an intact savable project");

    window.SelectForTests(textId);check(colors.IsEnabled&&!widths.IsEnabled&&types.Visibility!=Visibility.Visible,"text selection offers color but does not offer ineffective line width or arrow controls");
    before=UndoCount(window);Choose(colors,"#B38A3D");check(Item(window,textId).Color=="#B38A3D"&&SameGeometry(Item(window,textId),original[textId])&&UndoCount(window)==before+1,"actual text color edit changes only color and remains a single undoable action");Click(undo);check(SameAll(window,original),"text color undo restores the original measured text and every other item");
    check(window.SaveForTests(),"text color undo is persisted before checking disabled contexts");
    window.SelectForTests(imageId);before=UndoCount(window);
    check(!colors.IsEnabled&&!widths.IsEnabled&&types.Visibility!=Visibility.Visible,"image-only selection disables annotation properties rather than pretending they can edit a picture");
    Choose(colors,"#4C9277");Choose(widths,1.0);Choose(types,"arrow");
    check(SameAll(window,original)&&UndoCount(window)==before&&!window.DirtyForTests,"even programmatic disabled-control selection events cannot mutate a picture or unselected annotations");
    Click(Named<Button>(content,"CanvasTool_pan"));before=UndoCount(window);Choose(colors,"#333333");Choose(widths,2.0);Choose(types,"line");
    check(SameAll(window,original)&&UndoCount(window)==before&&!window.DirtyForTests,"panning-context property events never edit document contents");

    Click(Named<Button>(content,"CanvasTool_line"));check(object.Equals(Value(colors),"#547FC3")&&object.Equals(Value(widths),8.0),"returning to a drawing tool restores the original next-drawing defaults after selected edits and disabled contexts");
    var unchanged=Snapshot(window);Choose(colors,"#547FC3");Choose(widths,3.0);
    check(SameAll(window,unchanged),"editing next-drawing defaults does not retroactively restyle the prior selection");
    var secondLine=Draw(window,"line",new Point(100,440),new Point(270,460));string secondId=secondLine.Id;
    check(secondLine.Color=="#547FC3"&&secondLine.Stroke==3&&secondLine.Kind=="line","the next real drawing gesture uses only the explicitly chosen drawing defaults");
    Click(Named<Button>(content,"CanvasTool_select"));window.SelectForTests(secondId);var secondOriginal=Item(window,secondId).Copy();View(window).Zoom=2;window.SelectForTests(secondId);before=UndoCount(window);
    Choose(widths,8.0);check(Item(window,secondId).Stroke==4&&SameGeometry(Item(window,secondId),secondOriginal)&&UndoCount(window)==before+1,"at two-times zoom an 8 px property choice stores a 4-world-unit stroke and preserves both endpoints");
    check(window.SaveForTests(),"zoom-relative thickness edit saves through the production project path");before=UndoCount(window);window.SelectForTests(secondId);
    check(object.Equals(Value(widths),8.0)&&Item(window,secondId).Stroke==4&&UndoCount(window)==before&&!window.DirtyForTests,"zoomed selected-property synchronization displays screen pixels without rescaling the stored stroke");
    Click(undo);check(Same(Item(window,secondId),secondOriginal),"zoomed width undo restores exact world thickness, color, kind and endpoints");
    View(window).Zoom=1;window.SelectForTests(secondId);check(object.Equals(Value(widths),3.0),"returning to one-times zoom displays the original physical screen thickness truthfully");
    check(window.SaveForTests(),"pre-clamp original stroke is committed before extreme-zoom checks");
    View(window).Zoom=.02;window.SelectForTests(secondId);before=UndoCount(window);Choose(widths,12.0);
    check(Item(window,secondId).Stroke==100&&object.Equals(Value(widths),2.0)&&SameGeometry(Item(window,secondId),secondOriginal)&&UndoCount(window)==before+1,"minimum zoom clamps to the largest safe world stroke and displays its actual 2 px result rather than the requested 12 px");
    Click(undo);check(Same(Item(window,secondId),secondOriginal),"undo after minimum-zoom clipping restores the exact original safe stroke");
    View(window).Zoom=16;window.SelectForTests(secondId);before=UndoCount(window);Choose(widths,1.0);
    check(Item(window,secondId).Stroke==.25&&object.Equals(Value(widths),4.0)&&SameGeometry(Item(window,secondId),secondOriginal)&&UndoCount(window)==before+1,"maximum zoom clamps to the smallest safe world stroke and displays its actual custom 4 px result rather than 1 px");
    check(window.SaveForTests(),"clamped minimum stroke remains valid for the existing project-store range");before=UndoCount(window);window.SelectForTests(secondId);
    check(Item(window,secondId).Stroke==.25&&object.Equals(Value(widths),4.0)&&UndoCount(window)==before&&!window.DirtyForTests,"reselecting a clamped stroke neither normalizes stored data nor adds undo work");
    Click(undo);View(window).Zoom=1;window.SelectForTests(secondId);check(Same(Item(window,secondId),secondOriginal)&&object.Equals(Value(widths),3.0),"extreme-zoom editing and undo return to the original item and one-times thickness");
    Choose(types,"arrow");Choose(colors,"#C45B61");Choose(widths,5.0);var final=Snapshot(window);
    check(window.SaveForTests(),"selected color, thickness and arrow edits save together with all unchanged objects");string project=window.DocumentForTests.Id;var saved=store.Load(project);
    check(saved.Items.Count==final.Count&&saved.Items.All(i=>Same(i,final[i.Id])),"on-disk project round-trips the complete post-edit geometry, kind, style, text and image records");
    window.Close();window=null;reopened=new FreeCanvasWindow(data,new Settings());Layout((FrameworkElement)reopened.Content);reopened.SelectForTests(secondId);
    check(reopened.DocumentForTests.Id==project&&SameAll(reopened,final)&&!reopened.DirtyForTests,"closing and reopening restores every saved style edit without a spurious dirty draft");
    var reopenedContent=(FrameworkElement)reopened.Content;
    check(object.Equals(Value(Named<ComboBox>(reopenedContent,"CanvasInkColor")),"#C45B61")&&object.Equals(Value(Named<ComboBox>(reopenedContent,"CanvasStrokeWidth")),5.0)&&object.Equals(Value(Named<ComboBox>(reopenedContent,"CanvasLineType")),"arrow"),"reopened selected-arrow controls show persisted color, thickness and kind rather than fresh defaults");
    check(!reopened.IsVisible&&reopened.Owner==null,"all style-editing checks remain offscreen and independent of actual desktop projects");
   }finally{if(window!=null)window.Close();if(reopened!=null)reopened.Close();Theme.Set(theme);Theme.SetAccent(accent);app.ShutdownMode=shutdown;}
  }
 }
}
