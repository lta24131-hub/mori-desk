using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mori;

namespace MoriDesk {
 internal static class GallerySelectionStabilityTests {
  const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
  static T Field<T>(MainWindow main,string name){return (T)typeof(MainWindow).GetField(name,Flags).GetValue(main);}
  static void Set(MainWindow main,string name,object value){typeof(MainWindow).GetField(name,Flags).SetValue(main,value);}
  static object Call(MainWindow main,string name,params object[] args){return typeof(MainWindow).GetMethod(name,Flags,null,args.Select(a=>a.GetType()).ToArray(),null).Invoke(main,args);}
  static T Find<T>(MainWindow main,string name)where T:class{return main.Window.FindName(name)as T;}
  static void Click(MainWindow main,string name){Find<Button>(main,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
  static void StopTimers(MainWindow main){foreach(string name in new[]{"externalTimer","searchTimer","layoutTimer","sizeSaveTimer","sidebarSaveTimer"})Field<DispatcherTimer>(main,name).Stop();}
  static Rect Bounds(FrameworkElement item,FrameworkElement root){return new Rect(item.TranslatePoint(new Point(),root),new Size(item.ActualWidth,item.ActualHeight));}
  static bool Same(Rect a,Rect b){return Math.Abs(a.X-b.X)<.5&&Math.Abs(a.Y-b.Y)<.5&&Math.Abs(a.Width-b.Width)<.5&&Math.Abs(a.Height-b.Height)<.5;}
  static IEnumerable<T> Children<T>(DependencyObject root)where T:DependencyObject{
   for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T)yield return (T)child;foreach(var nested in Children<T>(child))yield return nested;}
  }
  static void Measure(MainWindow main,FrameworkElement content,int width){
   content.Width=width;content.Height=680;content.Measure(new Size(width,680));content.Arrange(new Rect(0,0,width,680));content.UpdateLayout();
   // Exercise the delayed row-reflow path as well, so a changed gallery width
   // cannot pass merely because its debounce timer has not run yet.
   Call(main,"BuildRows",true);content.UpdateLayout();StopTimers(main);
  }
  static Dictionary<string,Rect> Tiles(ListBox gallery,FrameworkElement root){
   // Waterfall rows may contain two visible fragments of the same image.
   Rect viewport=Bounds(gallery,root);return Children<Border>(gallery).Where(b=>b.Tag is Asset&&Bounds(b,root).IntersectsWith(viewport)).GroupBy(b=>((Asset)b.Tag).Id).SelectMany(group=>group.OrderBy(b=>Bounds(b,root).Y).Select((b,index)=>new{Key=group.Key+"#"+index,Box=Bounds(b,root)})).ToDictionary(item=>item.Key,item=>item.Box);
  }
  static bool Hit(FrameworkElement root,FrameworkElement control){
   DependencyObject target=null;VisualTreeHelper.HitTest(root,node=>{var ui=node as UIElement;return ui!=null&&(ui.Visibility!=Visibility.Visible||!ui.IsHitTestVisible)?HitTestFilterBehavior.ContinueSkipSelfAndChildren:HitTestFilterBehavior.Continue;},result=>{target=result.VisualHit;return HitTestResultBehavior.Stop;},new PointHitTestParameters(control.TranslatePoint(new Point(control.ActualWidth/2,control.ActualHeight/2),root)));
   for(var node=target;node!=null;node=MainWindow.SelectionParent(node))if(ReferenceEquals(node,control))return true;return false;
  }
  static void Commands(MainWindow main,FrameworkElement content,Action<bool,string> check,string label){
   var bar=Find<Border>(main,"BatchBar");bool shown=bar.Visibility==Visibility.Visible;
   check(Find<StackPanel>(main,"GalleryToolbar").Visibility==(shown?Visibility.Collapsed:Visibility.Visible),label+" browsing and selection commands share one existing row");if(!shown)return;
   Rect area=Bounds(bar,content);var controls=new List<FrameworkElement>();
   foreach(string name in new[]{"BatchMove","SelectionConvert","SelectionCanvas","BatchRestore","SelectionMore","BatchAll","SelectionMulti","BatchClear"}){var item=Find<FrameworkElement>(main,name);if(item.Visibility==Visibility.Visible)controls.Add(item);}
   check(controls.All(item=>{Rect r=Bounds(item,content);return r.Width>0&&r.Height>=24&&r.Left>=area.Left-.5&&r.Right<=area.Right+.5&&r.Top>=area.Top-.5&&r.Bottom<=area.Bottom+.5&&Hit(content,item);})&&controls.Zip(controls.Skip(1),(a,b)=>Bounds(a,content).Right<=Bounds(b,content).Left+.5).All(ok=>ok),label+" every visible action fits one row and receives input without overlap");
   check(Find<TextBlock>(main,"BatchCount").ActualWidth>=48,label+" selection count retains readable space beside actions");
  }
  static void Render(FrameworkElement content,string path,int width){var bitmap=new RenderTargetBitmap(width,680,96,96,PixelFormats.Pbgra32);bitmap.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var output=File.Create(path))encoder.Save(output);}
  static void StartupPreference(Library library,Action<bool,string> check){
   // Exercise actual saved settings and fresh windows, not an injected field.
   // The caller has already isolated Settings.ConfigDir inside this fixture.
   foreach(int step in new[]{0,1,2}){
    var main=new MainWindow(library);StopTimers(main);
    try{
     bool shown=step!=1;var sidebars=Field<SidebarLayout>(main,"sidebars");
     check(sidebars.RightDisplayed==shown,step==0?"first startup shows details before any image is selected":step==1?"fresh window restores manually hidden details":"fresh window restores manually opened details");
     var asset=library.All().First();main.SelectTile(asset,ModifierKeys.None);main.FinishTileSelection(asset,false);Call(main,"ClearGallerySelection");
     check(sidebars.RightDisplayed==shown,"startup preference survives selection and clearing, step "+step);
     if(step<2){main.ToggleSidebar(false);check(Settings.Load().RightSidebarVisible==!shown,"manual details toggle persists for next window, step "+step);}
     check(!main.Window.IsVisible,"startup persistence check never shows a desktop window, step "+step);
    }finally{StopTimers(main);main.Window.Close();}
   }
  }
  internal static void Run(string root,Action<bool,string> check){
   root=Path.Combine(root,"selection-stability");Directory.CreateDirectory(root);string priorData=Environment.GetEnvironmentVariable("MORI_DESK_DATA"),priorTheme=Theme.Current;bool interactive=Entry.InteractiveShell;var priorLibrary=ThumbnailImage.CurrentLibrary;
   var app=Application.Current??new Application();var shutdown=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
   var sync=System.Threading.SynchronizationContext.Current;System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
   Environment.SetEnvironmentVariable("MORI_DESK_DATA",Path.Combine(root,"config"));Entry.InteractiveShell=false;
   try{
    string image=Path.Combine(root,"sample.png");using(var picture=new System.Drawing.Bitmap(120,80)){using(var paint=System.Drawing.Graphics.FromImage(picture))paint.Clear(System.Drawing.Color.MediumPurple);picture.Save(image,System.Drawing.Imaging.ImageFormat.Png);}
    using(var library=new Library(Path.Combine(root,"library"))){
     for(int i=0;i<48;i++)library.ImportConvertedFile(image,"参考图片 "+i,"","","",null,"keep");
     library.Db.Exec("UPDATE assets SET width=120,height=80");
     StartupPreference(library,check);
     var main=new MainWindow(library){PreviewMode=true};main.PendingInformationChoice=()=>{throw new Exception("selection without drafts must not prompt");};StopTimers(main);
     var content=(FrameworkElement)main.Window.Content;main.Window.Content=null;content.Resources.MergedDictionaries.Add(main.Window.Resources);content.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty,main.Window.FontFamily);content.SetValue(System.Windows.Documents.TextElement.FontSizeProperty,main.Window.FontSize);content.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty,"Text");
     var sidebars=Field<SidebarLayout>(main,"sidebars");var gallery=Find<ListBox>(main,"Gallery");
     try{
      foreach(string theme in new[]{"light","dark"})foreach(int width in new[]{1040,1366})foreach(bool waterfall in new[]{false,true})foreach(bool details in new[]{false,true})foreach(bool scrolled in new[]{false,true}){
       Theme.Set(theme);foreach(object key in app.Resources.Keys)content.Resources[key]=app.Resources[key];content.SetResourceReference(Panel.BackgroundProperty,"Canvas");
       main.SetBatchSelecting(false);main.SetSection("assets");sidebars.SetPreferred(true,296);sidebars.SetPreferred(false,252);sidebars.SetVisible(false,true);Set(main,"homeInspectorExplicit",details);Call(main,"UpdateHomeInspector");main.SetGalleryLayout(waterfall);Measure(main,content,width);
       var scroll=MainWindow.VisualChild<ScrollViewer>(gallery);scroll.ScrollToVerticalOffset(scrolled?400:0);Measure(main,content,width);
       Rect before=Bounds(gallery,content);double offset=scroll.VerticalOffset;var tiles=Tiles(gallery,content);string label=theme+" / "+width+" / waterfall="+waterfall+" / details="+details+" / scrolled="+scrolled;
       check(tiles.Count>=2&&scroll.ScrollableHeight>0&&(!scrolled||offset>0),label+": real gallery tiles and a scrollable viewport are measured");
       var items=Field<List<Asset>>(main,"visible").Where(a=>tiles.ContainsKey(a.Id+"#0")).Take(2).ToArray();
       Action<string> stable=stage=>{
        Measure(main,content,width);var current=Tiles(gallery,content);
        check(sidebars.RightDisplayed==details&&Same(before,Bounds(gallery,content))&&Math.Abs(scroll.VerticalOffset-offset)<.5&&tiles.Count==current.Count&&tiles.All(pair=>current.ContainsKey(pair.Key)&&Same(pair.Value,current[pair.Key])),label+" / "+stage+": selection retains viewport, scroll offset and every visible tile rectangle");Commands(main,content,check,label+" / "+stage);
       };
       main.SelectTile(items[0],ModifierKeys.None);main.FinishTileSelection(items[0],false);stable("first click");
       if(width==1040&&!waterfall&&!scrolled)Render(content,Path.Combine(root,theme+(details?"-details":"-compact")+"-selected.png"),width);
       main.SelectTile(items[1],ModifierKeys.None);main.FinishTileSelection(items[1],false);stable("switch image");
       main.SelectTile(items[0],ModifierKeys.Control);main.FinishTileSelection(items[0],false);stable("Ctrl multi-select");
       Click(main,"SelectionMulti");stable("continue multi-select");check(Field<bool>(main,"batchSelecting")&&Field<HashSet<string>>(main,"selection").Count==2,label+": entering click-to-select mode keeps both selected images");
       Click(main,"BatchClear");stable("clear selection");main.SetBatchSelecting(true);stable("enter empty batch mode");
       check(Find<Button>(main,"BatchClear").IsEnabled,label+": empty multi-select can be cancelled");Click(main,"BatchAll");stable("select all");Click(main,"BatchClear");stable("leave batch mode");
       check(!Field<bool>(main,"batchSelecting")&&Field<HashSet<string>>(main,"selection").Count==0,label+": Cancel exits batch mode and restores browse controls");
      }
      Click(main,"ToggleInspector");main.SelectTile(Field<List<Asset>>(main,"visible")[0],ModifierKeys.None);main.SetSection("pictures");Measure(main,content,1040);
      check(!sidebars.RightDisplayed&&!sidebars.RightVisible,"explicitly closed details stay closed across selection and page navigation");
      check(library.All().Count==48&&!main.Window.IsVisible,"selection checks preserve every synthetic asset and never open a desktop window");
     }finally{StopTimers(main);main.Window.Close();}
    }
   }finally{Environment.SetEnvironmentVariable("MORI_DESK_DATA",priorData);Entry.InteractiveShell=interactive;Theme.Set(priorTheme);ThumbnailImage.CurrentLibrary=priorLibrary;app.ShutdownMode=shutdown;System.Threading.SynchronizationContext.SetSynchronizationContext(sync);}
  }
 }
}
