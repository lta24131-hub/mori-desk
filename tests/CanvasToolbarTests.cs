using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mori;

namespace MoriDesk {
 internal static class CanvasToolbarTests {
  static IEnumerable<DependencyObject> Children(DependencyObject root){yield return root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Children(VisualTreeHelper.GetChild(root,i)))yield return child;}
  static T Named<T>(FrameworkElement root,string name)where T:FrameworkElement{return Children(root).OfType<T>().Single(c=>c.Name==name);}
  static void Click(Button button){button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
  static FreeCanvasItem Text(string value){return new FreeCanvasItem{Id=Guid.NewGuid().ToString("N"),Kind="text",Text=value,Color="auto",FontSize=22,X=70,Y=90,Width=320,Height=60};}
  static void Layout(FrameworkElement content,int width,int height){content.Width=width;content.Height=height;content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();}
  static Rect Bounds(FrameworkElement element,FrameworkElement root){return new Rect(element.TranslatePoint(new Point(),root),new Size(element.ActualWidth,element.ActualHeight));}
  static void Render(FrameworkElement content,string path,int width,int height){Layout(content,width,height);var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var output=File.Create(path))encoder.Save(output);}
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);var app=Application.Current??new Application();var shutdown=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;string theme=Theme.Current,accent=Theme.AccentKey;FreeCanvasWindow window=null;
   string data=Path.Combine(root,"profile");var store=new FreeCanvasStore(data);
   try{
    Theme.Set("light");window=new FreeCanvasWindow(data,new Settings());var content=(FrameworkElement)window.Content;Layout(content,860,600);
    var tools=new[]{"select","pan","stroke","line","arrow","rectangle","ellipse","text"}.Select(k=>Named<Button>(content,"CanvasTool_"+k)).ToArray();
    check(tools.Length==8&&tools.All(b=>b.ActualWidth>=40&&b.ActualHeight>=40&&b.Content is Viewbox),"canvas drawing targets are eight 40px vector buttons, including a visible hand tool");
    check(tools.All(b=>!string.IsNullOrWhiteSpace(AutomationProperties.GetName(b))&&b.ToolTip!=null),"canvas vector tools retain readable accessibility names and explanatory hover labels");
    var color=Named<ComboBox>(content,"CanvasInkColor");var width=Named<ComboBox>(content,"CanvasStrokeWidth");var undo=Named<Button>(content,"CanvasUndo");var state=Named<TextBlock>(content,"CanvasSaveState");
    check(window.ModeForTests=="select"&&!color.IsEnabled&&!width.IsEnabled&&!undo.IsEnabled&&state.Text=="已保存","empty canvas starts in selection mode with truthful style and undo availability");
    Click(Named<Button>(content,"CanvasTool_pan"));check(window.ModeForTests=="pan"&&window.SurfaceForTests.Cursor==Cursors.Hand&&!color.IsEnabled&&!width.IsEnabled&&AutomationProperties.GetItemStatus(Named<Button>(content,"CanvasTool_pan"))=="已选中","hand button selects panning and never offers ineffective drawing-style controls");
    foreach(string kind in new[]{"stroke","line","arrow","rectangle","ellipse"}){Click(Named<Button>(content,"CanvasTool_"+kind));check(color.IsEnabled&&width.IsEnabled&&window.ModeForTests==kind,"color and width become available for "+kind);}
    Click(Named<Button>(content,"CanvasTool_text"));check(color.IsEnabled&&!width.IsEnabled,"text exposes ink color but disables the irrelevant pen width");
    check(width.Items.Cast<ComboBoxItem>().All(item=>((StackPanel)item.Content).Children.OfType<Border>().Single().Height==(double)item.Tag),"every width choice contains an actual corresponding line thickness preview");
    check(color.Items.Cast<ComboBoxItem>().All(item=>((StackPanel)item.Content).Children.OfType<Border>().Single().Width==11),"every ink choice includes a visible compact color swatch");
    Click(Named<Button>(content,"CanvasTool_line"));color.SelectedIndex=6;width.SelectedIndex=4;window.BeginDrawingForTests("line",new Point(100,150));window.EndDrawingForTests(new Point(230,175));var stroke=window.DocumentForTests.Items.Single();
    check(stroke.Color=="#547FC3"&&stroke.Stroke==8&&undo.IsEnabled&&state.Text=="待保存","actual toolbar style selection controls the next annotation and enables visible undo");
    Click(undo);check(window.ItemCount==0&&!undo.IsEnabled,"visible undo removes the committed drawing and becomes disabled when empty");
    window.DrawItemForTests(Text("旧项目不覆盖"));string original=window.DocumentForTests.Id;var create=Named<Button>(content,"CanvasNew");
    Click(create);string second=window.DocumentForTests.Id;check(second!=original&&window.ItemCount==0&&!window.DirtyForTests&&store.List().Count==2&&store.Load(original).Items.Single().Text=="旧项目不覆盖","one new-canvas click saves old content before creating an independent blank board without a prompt");
    Click(create);string third=window.DocumentForTests.Id;check(third!=second&&store.List().Count==3&&store.List().Select(p=>p.Name).Distinct().Count()==3,"successive one-click boards have distinct IDs and understandable unique automatic names");
    check(window.OpenProject(original)&&window.DocumentForTests.Items.Single().Text=="旧项目不覆盖"&&store.LastOpenedId==original,"project-page opening API restores the requested board and remembers the view");
    int count=store.List().Count;check(!window.OpenProject("../outside")&&window.DocumentForTests.Id==original&&store.List().Count==count,"invalid requested project never changes the current board or fabricates another one");
    string manifest=Path.Combine(store.Root,original,"project.json");byte[] good=File.ReadAllBytes(manifest);File.WriteAllText(manifest,"external conflict");var draft=Text("未保存的编辑");draft.Y=170;window.DrawItemForTests(draft);
    Click(create);check(window.DocumentForTests.Id==original&&window.ItemCount==2&&window.DirtyForTests&&store.List().Count==count&&File.ReadAllText(manifest)=="external conflict"&&state.Text=="未能保存","new-canvas action refuses a save conflict while keeping the current unsaved work and damaged file");
    Click(Named<Button>(content,"CanvasTool_select"));Theme.Set("dark");Layout(content,860,600);check(state.Text=="未能保存"&&!window.OpenProject(second)&&window.DocumentForTests.Id==original,"failed-save indicator survives theme repaint and project switching cannot bypass save protection");
    File.WriteAllBytes(manifest,good);check(window.SaveForTests()&&state.Text=="已保存"&&Named<TextBlock>(content,"CanvasStatus").Text=="画布已保存"&&store.Load(original).Items.Count==2,"repairing only the fixture manifest permits successful save and clears both stale error indicators");
    byte[] savedOriginal=File.ReadAllBytes(manifest);File.WriteAllText(manifest,"another external conflict");var rescue=Text("复制救援内容");rescue.Y=250;window.DrawItemForTests(rescue);
    check(window.CopyProject("救援副本")&&window.DocumentForTests.Id!=original&&window.ItemCount==3&&store.Load(window.DocumentForTests.Id).Items.Last().Text==rescue.Text&&File.ReadAllText(manifest)=="another external conflict"&&!window.DirtyForTests,"copy-as-new remains a rescue path for unsaved editing when the original manifest cannot be overwritten");
    File.WriteAllBytes(manifest,savedOriginal);check(window.OpenProject(original)&&window.ItemCount==2,"independent rescue copy does not replace the original project's previously saved content");
    window.BeginDrawingForTests("arrow",new Point(100,300));window.UpdateDrawingForTests(new Point(220,340));int beforeCopy=window.ItemCount;
    check(window.PrepareCopyProject()&&window.ItemCount==beforeCopy+1&&window.DocumentForTests.Items.Last().Kind=="arrow"&&window.DirtyForTests,"copy prompt preparation commits an active drawing before a modal can deactivate and cancel it");
    check(window.CopyProject("手势副本")&&store.Load(window.DocumentForTests.Id).Items.Last().Kind=="arrow","copy after modal preparation includes the committed gesture in the independent saved board");
    foreach(string palette in new[]{"light","dark"}){
     Theme.Set(palette);Layout(content,860,600);
     var controls=new FrameworkElement[]{create,Named<Button>(content,"CanvasManage"),Named<ComboBox>(content,"CanvasProjects"),color,width,undo,Named<Button>(content,"CanvasFit")};
     check(controls.All(c=>{var b=Bounds(c,content);return b.X>=0&&b.Y>=0&&b.Right<=860&&b.Bottom<200&&b.Width>0&&b.Height>0;}),palette+" project actions and properties stay visible above the canvas at minimum width");
     check(tools.All(c=>{var b=Bounds(c,content);return b.X>=0&&b.Right<=56&&b.Y>=Bounds(window.SurfaceForTests,content).Top&&b.Width>=40&&b.Height>=40;}),palette+" drawing tools form a distinct left rail rather than occupying the top properties row");
     check(controls.SelectMany((a,i)=>controls.Skip(i+1).Select(b=>!Bounds(a,content).IntersectsWith(Bounds(b,content)))).All(v=>v)&&tools.SelectMany((a,i)=>tools.Skip(i+1).Select(b=>!Bounds(a,content).IntersectsWith(Bounds(b,content)))).All(v=>v),palette+" top properties and left drawing targets never overlap within their regions");
     Layout(content,860,460);var rail=Named<ScrollViewer>(content,"CanvasToolRail");check(rail.ScrollableHeight>0&&rail.VerticalScrollBarVisibility==ScrollBarVisibility.Auto&&rail.HorizontalScrollBarVisibility==ScrollBarVisibility.Disabled,"minimum-height "+palette+" canvas keeps all eight tools reachable through vertical scrolling");rail.ScrollToEnd();Layout(content,860,460);Rect textTool=Bounds(Named<Button>(content,"CanvasTool_text"),content),railBounds=Bounds(rail,content);check(textTool.Top>=railBounds.Top&&textTool.Bottom<=railBounds.Bottom&&Bounds(color,content).Bottom<railBounds.Top,"minimum-height "+palette+" scroll exposes the text tool without hiding top properties");rail.ScrollToTop();
     foreach(var button in tools){var paths=Children((DependencyObject)button.Content).OfType<System.Windows.Shapes.Path>().ToArray();check(paths.Length==1&&paths[0].Data!=null&&!paths[0].Data.Bounds.IsEmpty&&paths[0].Data.IsFrozen&&paths[0].Stroke==button.Foreground,palette+" "+button.Name+" has frozen nonempty vector geometry and follows its button color");}
     Render(content,Path.Combine(root,"canvas-toolbar-"+palette+"-860.png"),860,600);Render(content,Path.Combine(root,"canvas-toolbar-"+palette+"-1180.png"),1180,760);
    }
    check(!window.IsVisible&&window.Owner==null,"toolbar checks render offscreen without opening or changing any real desktop window");
   }finally{if(window!=null)window.Close();Theme.Set(theme);Theme.SetAccent(accent);app.ShutdownMode=shutdown;}
  }
 }
}
