using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Controls;
using QingJie;
namespace MoriDesk {
 public static class ScreenshotCollectionTests {
  static readonly BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
  static T Await<T>(Task<T> task){
   var end=DateTime.UtcNow.AddSeconds(12);
   while(!task.IsCompleted&&DateTime.UtcNow<end){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);System.Threading.Thread.Sleep(1);}
   if(!task.IsCompleted)throw new Exception("Screenshot collection test timed out");return task.GetAwaiter().GetResult();
  }
  static BitmapSource Sample(){var b=new WriteableBitmap(160,80,96,96,PixelFormats.Bgra32,null);byte[] pixels=new byte[160*80*4];for(int i=0;i<pixels.Length;i+=4){pixels[i]=100;pixels[i+1]=180;pixels[i+2]=220;pixels[i+3]=255;}b.WritePixels(new Int32Rect(0,0,160,80),pixels,640,0);b.Freeze();return b;}
  static CaptureWindow Capture(){var w=new CaptureWindow(new Snapshot{Image=Sample(),Bounds=new System.Drawing.Rectangle(0,0,160,80)});var s=Surface(w);s.Measure(new Size(160,80));s.Arrange(new Rect(0,0,160,80));s.Selection=new Rect(0,0,160,80);return w;}
  static ScreenshotSurface Surface(CaptureWindow window){return (ScreenshotSurface)typeof(CaptureWindow).GetField("surface",Hidden).GetValue(window);}
  static string Status(CaptureWindow window){return ((TextBlock)typeof(CaptureWindow).GetField("translationStatus",Hidden).GetValue(window)).Text;}
  static bool Closed(CaptureWindow window){return (bool)typeof(CaptureWindow).GetField("closed",Hidden).GetValue(window);}
  static void CaptureFlow(Action<bool,string> check){
   var previous=DeskRuntime.CollectImage;CaptureWindow w=null;int calls=0;
   try{
    var pending=new TaskCompletionSource<bool>();BitmapSource exported=null;
    DeskRuntime.CollectImage=image=>{calls++;exported=image;return pending.Task;};w=Capture();
    var toolbar=(StackPanel)typeof(CaptureWindow).GetField("toolbar",Hidden).GetValue(w);
    var button=(Button)LogicalTreeHelper.FindLogicalNode(w,"CaptureCollect");
    check(System.Windows.Automation.AutomationProperties.GetName(button).Contains("Ctrl+D")&&button.ToolTip.ToString().Contains("收藏到素材库"),"icon-only collection retains accessible name and shortcut tooltip");
    check(button.Content is ToolIcon&&button.Width==36&&button.Height==36,"collection is an ordinary-sized icon with no visible text");
    var icon=(ToolIcon)button.Content;icon.Measure(new Size(20,20));icon.Arrange(new Rect(0,0,20,20));var iconImage=new RenderTargetBitmap(20,20,96,96,PixelFormats.Pbgra32);iconImage.Render(icon);byte[] iconPixels=new byte[20*20*4];iconImage.CopyPixels(iconPixels,80,0);
    bool bluePixel=false;for(int i=0;i<iconPixels.Length;i+=4)if(iconPixels[i+3]>200&&iconPixels[i]>iconPixels[i+2]+50&&iconPixels[i]>iconPixels[i+1]+40){bluePixel=true;break;}
    check(bluePixel,"collection glyph is blue rather than ordinary toolbar ink");
    typeof(CaptureWindow).GetMethod("StartText",Hidden,null,new[]{typeof(Point)},null).Invoke(w,new object[]{new Point(5,5)});
    ((TextBox)typeof(CaptureWindow).GetField("textEditor",Hidden).GetValue(w)).Text="Note";
    var task=w.CollectToLibraryAsync();
    check(Surface(w).Marks.Any(m=>m.Kind=="text"&&m.Text=="Note"),"collection commits current text annotation before export");
    check(exported!=null&&exported.IsFrozen&&exported.PixelWidth==160&&exported.PixelHeight==80,"collection exports frozen physical pixels for background work");
    check(!Surface(w).IsEnabled&&!toolbar.IsEnabled&&!Closed(w),"capture remains visible and locked until import commits");
    check(!Await(w.CollectToLibraryAsync())&&calls==1,"double click cannot save a second copy");
    pending.SetResult(false);check(!Await(task)&&!Closed(w)&&Surface(w).IsEnabled&&toolbar.IsEnabled,"unsuccessful import keeps capture editable");
    check(Status(w).Contains("截图已保留"),"failure guidance says capture is retained");
    DeskRuntime.CollectImage=image=>{throw new IOException("Synthetic unavailable library");};
    check(!Await(w.CollectToLibraryAsync())&&!Closed(w)&&Status(w).Contains("Synthetic unavailable library"),"write error is inline and screenshot is retained for retry");
    DeskRuntime.CollectImage=image=>Task.FromResult(true);
    check(Await(w.CollectToLibraryAsync())&&Closed(w),"successful retry closes only the completed capture");w=null;
    DeskRuntime.CollectImage=image=>{calls++;return Task.FromResult(true);};w=Capture();Surface(w).Selection=Rect.Empty;int before=calls;
    check(!Await(w.CollectToLibraryAsync())&&calls==before&&!Closed(w),"empty selection never imports or closes capture");w.Close();w=null;
    // If some external lifecycle action closes an in-flight capture, its late
    // result must never close a subsequently created screenshot.
    pending=new TaskCompletionSource<bool>();DeskRuntime.CollectImage=image=>pending.Task;w=Capture();task=w.CollectToLibraryAsync();w.Close();
    var next=Capture();try{pending.SetResult(true);check(Await(task)&&!Closed(next),"late save result cannot close a newer capture");}finally{next.Close();}w=null;
    DeskRuntime.CollectImage=image=>{exported=image;return Task.FromResult(false);};w=Capture();Surface(w).TranslationImage=BitmapSource.Create(1,1,96,96,PixelFormats.Bgra32,null,new byte[]{0,200,0,255},4);Surface(w).TranslationBounds=Surface(w).Selection;Surface(w).ShowTranslation=true;
    Await(w.CollectToLibraryAsync());byte[] pixel=new byte[4];exported.CopyPixels(new Int32Rect(70,40,1,1),pixel,4,0);check(pixel[1]==200&&pixel[2]==0,"collection preserves the currently shown translated image");
    Surface(w).ShowTranslation=false;Await(w.CollectToLibraryAsync());exported.CopyPixels(new Int32Rect(70,40,1,1),pixel,4,0);check(pixel[2]==220,"toggling to original collects original pixels instead");
   }finally{if(w!=null&&!Closed(w))w.Close();DeskRuntime.CollectImage=previous;}
  }
  #if CAPTURE_BACKGROUND_TESTS
  public static void Run(Action<bool,string> check){
   string data=Environment.GetEnvironmentVariable("MORI_DESK_DATA");
   if(string.IsNullOrEmpty(data)||!Path.IsPathRooted(data)||!File.Exists(Path.Combine(Path.GetDirectoryName(data),".capture-background-sandbox")))throw new InvalidOperationException("Screenshot collection requires the isolated background runner.");
   Run(Path.Combine(data,"screenshot-collection"),check);
  }
  #endif
  public static void Run(string root,Action<bool,string> check){
   var app=Application.Current??new Application();var mode=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
   var context=System.Threading.SynchronizationContext.Current;System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
   try{using(var lib=new Mori.Library(Path.Combine(root,"screenshot-library"))){
    var main=new Mori.MainWindow(lib){PreviewMode=true};
    try{
     var collect=typeof(Mori.MainWindow).GetMethod("CollectScreenshot",Hidden);
     var result=collect.Invoke(main,new object[]{Sample()});
     check(Await((Task<bool>)result),"main screenshot import reports committed success");
     check(lib.All().Count==1,"screenshot collection actually writes one asset, not a successful skip");
     var item=lib.All().Single();check(item.Ext==".png"&&File.Exists(lib.FullPath(item)),"PNG file persisted in current library");
     check(lib.Db.Query("SELECT * FROM memberships").Count==0,"new screenshot goes to unfiled without a category dialog");
     var decoded=Snapshot.Load(lib.FullPath(item));check(decoded.PixelWidth==160&&decoded.PixelHeight==80,"saved PNG preserves original dimensions");
     string folder=lib.CreateFolder("Existing category","");lib.AddToFolder(new[]{item.Id},folder);item.Name="User title";item.Notes="Keep notes";item.Tags="Keep tags";lib.Update(item);
     check(Await((Task<bool>)collect.Invoke(main,new object[]{Sample()}))&&lib.All().Count==1,"repeated capture reuses existing file");
     item=lib.All().Single();check(item.Name=="User title"&&item.Notes=="Keep notes"&&item.Tags=="Keep tags"&&lib.Db.Query("SELECT * FROM memberships").Count==1,"duplicate preserves user metadata and category");
     lib.Trash(new[]{item.Id},true);Await((Task<bool>)collect.Invoke(main,new object[]{Sample()}));
     check(lib.All().Count==1&&!lib.All().Single().Deleted,"collecting a recycled duplicate restores it instead of silently skipping");
     typeof(Mori.MainWindow).GetField("busy",Hidden).SetValue(main,true);bool blocked=false;
     try{Await((Task<bool>)collect.Invoke(main,new object[]{Sample()}));}catch(InvalidOperationException){blocked=true;}finally{typeof(Mori.MainWindow).GetField("busy",Hidden).SetValue(main,false);}
     check(blocked&&lib.All().Count==1,"busy library refuses safely without a new file");
     check(lib.Db.Query("PRAGMA integrity_check").Single().Values.Single()=="ok","capture imports leave SQLite intact");
    }finally{main.CloseForReplacement();}
   }
   using(var reopened=new Mori.Library(Path.Combine(root,"screenshot-library")))check(reopened.All().Count==1,"screenshot remains after reopening library");
   CaptureFlow(check);
   }finally{app.ShutdownMode=mode;System.Threading.SynchronizationContext.SetSynchronizationContext(context);}
  }
 }
}
