using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Mori;

namespace MoriDesk {
 internal static class PdfReaderExperienceTests {
  static T Field<T>(object owner,string name){return (T)owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(owner);}
  static void Call(object owner,string name){owner.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(owner,null);}
  static void Until(Func<bool> ready,string reason){var watch=System.Diagnostics.Stopwatch.StartNew();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(30)};var frame=new DispatcherFrame();timer.Tick+=delegate{if(ready()||watch.ElapsedMilliseconds>30000)frame.Continue=false;};timer.Start();try{if(!ready())Dispatcher.PushFrame(frame);}finally{timer.Stop();}if(!ready())throw new TimeoutException(reason);}
  static void Pump(int ms){var watch=System.Diagnostics.Stopwatch.StartNew();Until(()=>watch.ElapsedMilliseconds>=ms,"dispatcher delay");}
  static void Click(Button button){button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
  static void Key(Window window,System.Windows.Input.Key key,UIElement target=null){(target??window).RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),Environment.TickCount,key){RoutedEvent=Keyboard.PreviewKeyDownEvent});}
  static IEnumerable<DependencyObject> Children(DependencyObject parent){for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);yield return child;foreach(var nested in Children(child))yield return nested;}}
  static void Shot(Window w,string name){w.UpdateLayout();Pump(300);using(var b=new System.Drawing.Bitmap((int)w.ActualWidth,(int)w.ActualHeight))using(var g=System.Drawing.Graphics.FromImage(b))using(var s=new MemoryStream()){g.CopyFromScreen((int)w.Left,(int)w.Top,0,0,b.Size);b.Save(s,System.Drawing.Imaging.ImageFormat.Png);File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,name+".json"),new System.Web.Script.Serialization.JavaScriptSerializer{MaxJsonLength=8*1024*1024}.Serialize(new{imageBase64=Convert.ToBase64String(s.ToArray())}));}}
  internal static byte[] Fixture(int count,bool mixed=false){
   var objects=new List<string>{"<< /Type /Catalog /Pages 2 0 R >>","<< /Type /Pages /Kids [ "+string.Join(" ",Enumerable.Range(0,count).Select(i=>(4+2*i)+" 0 R"))+" ] /Count "+count+" >>","<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"};
   for(int i=0;i<count;i++){
    objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 "+(mixed&&i%2==1?"800 400":"600 800")+"] /Resources << /Font << /F1 3 0 R >> >> /Contents "+(5+i*2)+" 0 R >>");
    string content="0.45 0.34 0.76 rg 48 716 28 4 re f 0.12 0.12 0.13 rg BT /F1 28 Tf 48 660 Td (Designing a quieter workspace) Tj /F1 12 Tf 0 -35 Td (Research notes / October 2026) Tj /F1 18 Tf 0 -70 Td ("+(i+1)+". Make room for the work) Tj /F1 12 Tf 0 -36 Td (A good reading experience puts the document first.) Tj 0 -22 Td (Keep navigation close and let secondary tools step back.) Tj 0 -22 Td (Small details should help readers keep their place.) Tj ET 0.94 0.94 0.96 rg 48 250 504 180 re f 0.70 0.66 0.83 rg 80 278 100 95 re f 0.59 0.53 0.76 rg 220 278 100 125 re f 0.45 0.34 0.66 rg 360 278 100 150 re f 0.3 0.3 0.34 rg BT /F1 11 Tf 48 213 Td (Figure 1. Less interface, more space for useful content.) Tj /F1 12 Tf 0 -45 Td (Browse continuously. Jump to a page. Focus when needed.) Tj ET\n";
    objects.Add("<< /Length "+Encoding.ASCII.GetByteCount(content)+" >>\nstream\n"+content+"endstream");
   }
   using(var output=new MemoryStream()){Action<string> put=s=>{byte[] b=Encoding.ASCII.GetBytes(s);output.Write(b,0,b.Length);};put("%PDF-1.4\n");var offsets=new List<long>{0};for(int i=0;i<objects.Count;i++){offsets.Add(output.Position);put((i+1)+" 0 obj\n"+objects[i]+"\nendobj\n");}long xref=output.Position;put("xref\n0 "+(objects.Count+1)+"\n0000000000 65535 f \n");foreach(long offset in offsets.Skip(1))put(offset.ToString("D10")+" 00000 n \n");put("trailer\n<< /Size "+(objects.Count+1)+" /Root 1 0 R >>\nstartxref\n"+xref+"\n%%EOF\n");return output.ToArray();}
  }
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);string file=Path.Combine(root,"阅读体验.pdf");byte[] original=Fixture(120);File.WriteAllBytes(file,original);
   foreach(string theme in new[]{"light","dark"}){
    Theme.Set(theme);var w=new DocumentPreviewWindow(null,file,"阅读体验.pdf",false){Width=980,Height=700,Left=0,Top=0,WindowStartupLocation=WindowStartupLocation.Manual};
    try{
     w.Show();var view=Field<PdfContinuousView>(w,"document");Until(()=>view.IsPageRendered(0),"reader opening");var input=Field<TextBox>(w,"pageInput");var pane=Field<PdfThumbnailPane>(w,"thumbnailPane");
     check(view.PageCount==120&&pane.Visibility==Visibility.Collapsed&&view.ThumbnailCount==0,"closed sidebar does no thumbnail work / "+theme);
     Shot(w,"pdf-reader-"+theme);
     input.Focus();input.Text="100";Call(w,"CommitPage");Until(()=>view.CurrentPage==99,"direct page jump");check(input.Text=="100","page input follows committed destination / "+theme);
     foreach(string invalid in new[]{"0","121","-2","abc","999999",""}){input.Focus();input.Text=invalid;Call(w,"CommitPage");check(view.CurrentPage==99&&input.IsKeyboardFocused,"invalid page does not navigate or close: "+invalid+" / "+theme);}
     Key(w,System.Windows.Input.Key.Escape,input);check(w.IsVisible&&input.Text=="100"&&!input.IsKeyboardFocused,"Esc cancels page editing without closing document / "+theme);
     view.SetDisplayZoom(1);w.UpdateLayout();Pump(50);double sheetWidth=((Canvas)view.Scroller.Content).Children.OfType<Grid>().First().Width;check(Math.Abs(view.DisplayZoom-1)<.01&&Math.Abs(sheetWidth-800)<1,"100 percent maps 600 PDF points to 800 WPF DIPs exactly once / "+theme);
     view.FitPage();w.UpdateLayout();Pump(70);check(view.Scroller.ScrollableWidth<1&&((Canvas)view.Scroller.Content).Children.OfType<Grid>().First().Height<view.Scroller.ViewportHeight+10,"fit-page shows whole portrait page / "+theme);
     view.GoTo(0);Until(()=>view.CurrentPage==0,"first page");Click(Field<Button>(w,"sidebarButton"));w.UpdateLayout();Until(()=>view.ThumbnailCount>=3,"visible thumbnails");check(pane.Visibility==Visibility.Visible&&pane.VisibleCount<=12&&view.ThumbnailCount<=12,"120 pages allocate only visible thumbnail controls and small rasters / "+theme);
     var thumb=Children(pane).OfType<Button>().First(b=>Convert.ToString(b.ToolTip)=="第 3 页");Click(thumb);Until(()=>view.CurrentPage==2,"thumbnail navigation");check(input.Text=="3","thumbnail click updates page jump field / "+theme);Shot(w,"pdf-sidebar-"+theme);
     w.Height=460;w.UpdateLayout();Pump(120);check(pane.Scroller.VerticalOffset<=2*176&&pane.Scroller.VerticalOffset+pane.Scroller.ViewportHeight>=3*176-1,"selected thumbnail stays fully visible after shortening window / "+theme);Shot(w,"pdf-overview-"+theme);w.Height=700;w.UpdateLayout();
     pane.Scroller.ScrollToBottom();pane.UpdateLayout();Pump(100);Until(()=>view.Thumbnail(119)!=null,"last thumbnail");check(view.ThumbnailCount<=12&&view.Thumbnail(0)==null,"scrolling thumbnails evicts old pages without growing memory / "+theme);
     var bar=Field<Border>(w,"pdfToolbar");double width=w.Width,height=w.Height;var state=w.WindowState;Key(w,System.Windows.Input.Key.F11);check(bar.Visibility==Visibility.Collapsed&&pane.Visibility==Visibility.Collapsed&&view.ThumbnailCount==0&&w.Width==width&&w.Height==height&&w.WindowState==state,"focus hides chrome and releases thumbnails in the same OS window / "+theme);
     Key(w,System.Windows.Input.Key.Escape);check(w.IsVisible&&bar.Visibility==Visibility.Visible&&pane.Visibility==Visibility.Visible,"Esc leaves focus and restores sidebar without closing / "+theme);
     Click(Field<Button>(w,"sidebarButton"));check(view.ThumbnailCount==0&&pane.VisibleCount==0,"hiding thumbnails releases raster and control references / "+theme);
     view.FitWidth();w.Width=540;w.Height=470;w.UpdateLayout();Pump(100);var toolbar=Children(bar).OfType<Button>().ToArray();var rects=toolbar.Select(b=>new Rect(b.TranslatePoint(new Point(0,0),bar),new Size(b.ActualWidth,b.ActualHeight))).ToArray();
     check(rects.All(r=>r.Left>=0&&r.Right<=bar.ActualWidth)&&rects.SelectMany((r,i)=>rects.Skip(i+1).Select(o=>Rect.Intersect(r,o))).All(r=>r.IsEmpty||r.Width<1||r.Height<1),"toolbar buttons neither overlap nor clip at minimum window width / "+theme);Shot(w,"pdf-compact-"+theme);
     Click(Field<Button>(w,"chooseText"));Until(()=>view.IsTextReady(view.CurrentPage),"text layer");view.SelectPageText();check(view.SelectedText.Contains("Designing a quieter workspace")&&Field<Border>(w,"selectionActions").Visibility==Visibility.Visible,"selection still exposes copy and translation / "+theme);
     Key(w,System.Windows.Input.Key.Escape);check(!w.IsVisible,"normal Esc closes the reader / "+theme);
    }finally{w.Close();}
    Until(()=>{try{using(var s=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None))return true;}catch(IOException){return false;}},"reader source lock");check(true,"closing reader releases original source / "+theme);
   }
   check(File.ReadAllBytes(file).SequenceEqual(original),"reader navigation never modifies source bytes");
   string ai=Path.Combine(root,"mixed.ai");File.WriteAllBytes(ai,Fixture(6,true));var mixedWindow=new DocumentPreviewWindow(null,ai,"不同大小画板",false){Width=800,Height=600};
   try{mixedWindow.Show();var view=Field<PdfContinuousView>(mixedWindow,"document");Until(()=>view.IsPageRendered(0),"mixed AI open");view.FitPage();mixedWindow.UpdateLayout();double portrait=view.ZoomFactor;view.GoTo(1);mixedWindow.UpdateLayout();Until(()=>view.CurrentPage==1,"mixed AI navigation");check(view.ZoomFactor>portrait,"whole-page mode adapts to landscape artboard on explicit page navigation");view.GoTo(2);mixedWindow.UpdateLayout();Until(()=>view.CurrentPage==2,"mixed AI portrait return");check(Math.Abs(view.ZoomFactor-portrait)<.01,"whole-page mode fits the next portrait artboard again");Click(Field<Button>(mixedWindow,"sidebarButton"));mixedWindow.Close();}
   finally{mixedWindow.Close();}
   Until(()=>{try{using(var s=new FileStream(ai,FileMode.Open,FileAccess.ReadWrite,FileShare.None))return true;}catch(IOException){return false;}},"AI closed during thumbnail work");check(true,"close while thumbnail rendering is queued releases AI source");
  }
 }
}
