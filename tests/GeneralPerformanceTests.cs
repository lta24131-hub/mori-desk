using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mori;
using Forms=System.Windows.Forms;

namespace MoriDesk {
 internal static class GeneralPerformanceTests {
  const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
  static readonly List<object> metrics=new List<object>();
  static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
  static void Await(Task task){DateTime end=DateTime.UtcNow.AddSeconds(20);while(!task.IsCompleted){if(DateTime.UtcNow>end)throw new TimeoutException("Performance fixture timed out");Pump();Thread.Sleep(1);}task.GetAwaiter().GetResult();}
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);metrics.Clear();var context=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
   try{Import(root,check);Projects(root,check);Animation(check);}
   finally{SynchronizationContext.SetSynchronizationContext(context);File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"general-performance.json"),new JavaScriptSerializer().Serialize(new{runtime=Environment.Version.ToString(),metrics=metrics}));}
  }
  static void WriteImage(string path,int width,int height){
   var pixels=new byte[width*height*4];for(int y=0;y<height;y++)for(int x=0;x<width;x++){int at=(y*width+x)*4;pixels[at]=(byte)(x*13+y*17);pixels[at+1]=(byte)(x+y);pixels[at+2]=(byte)(x*7);pixels[at+3]=(byte)(x%2==0?255:120);}
   var bitmap=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var output=File.Create(path))encoder.Save(output);
  }
  static MatureCanvasResource ImportOne(MatureCanvasStore store,MatureCanvasDocument doc,string path,out int width,out int height){
   var fast=typeof(MatureCanvasStore).GetMethod("RegisterImage",Hidden,null,new[]{typeof(MatureCanvasDocument),typeof(string),typeof(int).MakeByRefType(),typeof(int).MakeByRefType(),typeof(string)},null);
   if(fast!=null){object[] args={doc,path,0,0,null};var resource=(MatureCanvasResource)fast.Invoke(store,args);width=(int)args[2];height=(int)args[3];return resource;}
   var old=store.RegisterImage(doc,path);var image=store.Preview(doc,old.FileId);width=image.PixelWidth;height=image.PixelHeight;return old;
  }
  static void Import(string root,Action<bool,string> check){
   var store=new MatureCanvasStore(Path.Combine(root,"import-profile"));var doc=store.Create("尺寸和透明度");
   foreach(var size in new[]{new[]{41,29},new[]{2500,500},new[]{500,2500}}){
    string path=Path.Combine(root,"source-"+size[0]+".png");WriteImage(path,size[0],size[1]);byte[] before=File.ReadAllBytes(path);DateTime modified=File.GetLastWriteTimeUtc(path);int width,height;var resource=ImportOne(store,doc,path,out width,out height);var preview=store.Preview(doc,resource.FileId);
    check(width==preview.PixelWidth&&height==preview.PixelHeight&&Math.Max(width,height)<=MatureCanvasStore.PreviewDimension,"import dimensions match the durable bounded PNG / "+size[0]);
    check(before.SequenceEqual(File.ReadAllBytes(path))&&modified==File.GetLastWriteTimeUtc(path),"canvas import preserves original bytes and timestamps / "+size[0]);
    using(var open=File.Open(store.ResourcePath(doc,resource),FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(open.Length>0,"completed import releases its saved image handle / "+size[0]);
    if(size[0]==41){var pixels=new byte[41*29*4];new FormatConvertedBitmap(preview,PixelFormats.Bgra32,null,0).CopyPixels(pixels,41*4,0);check(pixels[7]==120&&pixels[3]==255,"import preserves mixed alpha instead of flattening transparent pixels");}
   }
   string svg=Path.Combine(root,"vector.svg");File.WriteAllText(svg,"<svg xmlns='http://www.w3.org/2000/svg' width='100' height='50'><rect width='100' height='50' fill='red'/></svg>");
   try{int sw,sh;var vector=ImportOne(store,doc,svg,out sw,out sh);var rendered=store.Preview(doc,vector.FileId);check(sw==rendered.PixelWidth&&sh==rendered.PixelHeight&&vector.MimeType=="image/png","vector import reports rasterized dimensions and still registers only a safe PNG");}
   catch(Exception error){if(error is TargetInvocationException)error=error.InnerException;var details=new Dictionary<string,object>();foreach(System.Collections.DictionaryEntry pair in error.Data)details[Convert.ToString(pair.Key)]=pair.Value;metrics.Add(new{name="svg-import-failure",details=details});check(false,"vector import failed / "+new JavaScriptSerializer().Serialize(details));}
   string large=Path.Combine(root,"large.png");WriteImage(large,2048,1536);var results=new List<double>();var allocations=new List<long>();AppDomain.MonitoringIsEnabled=true;
   for(int round=0;round<4;round++){
    var target=store.Create("导入计时 "+round);GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();long before=AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;var clock=Stopwatch.StartNew();for(int i=0;i<3;i++){int w,h;ImportOne(store,target,large,out w,out h);}clock.Stop();if(round>0){results.Add(clock.Elapsed.TotalMilliseconds);allocations.Add(AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize-before);}
   }
   metrics.Add(new{name="import-three-2048x1536-png",milliseconds=results,managedAllocatedBytes=allocations});
  }
  static CanvasProjectsPage Page(FreeCanvasStore store){return new CanvasProjectsPage(store,(id,paths)=>true,data=>new string[0],data=>new string[0],()=>true,()=>true,()=>false,message=>{});}
  static void Projects(string root,Action<bool,string> check){
   var store=new FreeCanvasStore(Path.Combine(root,"projects"));for(int i=0;i<40;i++)store.Create("项目 "+i);
   var page=Page(store);check(page.QueryCountForTests==0,"constructing a hidden project page does not synchronously scan project files");CanvasPageTestSupport.Wait(page);
   check(page.ProjectsForTests.Count==40,"asynchronous initial load produces every saved project");var cards=page.ProjectsForTests.Select(row=>page.ProjectButtonForTests(row.Id)).ToArray();
   check(cards.All(card=>ReferenceEquals(card.Template,cards[0].Template)),"all cards in one page reuse a template instead of parsing identical XAML per card");
   string oldTheme=Theme.Current;var host=new Window{Content=page,Width=650,Height=430,Left=-20000,Top=-20000,ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual};
   try{host.Show();foreach(string theme in new[]{"light","dark"}){Theme.Set(theme);Pump();host.UpdateLayout();check(cards.All(card=>card.ActualWidth>0&&card.Background==Theme.Brush("Panel")),"reused card template remains theme-aware and lays out normally / "+theme);}}
   finally{host.Content=null;host.Close();Theme.Set(oldTheme);}
   using(var entered=new ManualResetEventSlim(false))using(var release=new ManualResetEventSlim(false)){
    int dispatcherThread=Thread.CurrentThread.ManagedThreadId,queryThread=0;page.RefreshVisibleForTests=()=>true;
    page.ProjectQueryForTests=()=>{queryThread=Thread.CurrentThread.ManagedThreadId;entered.Set();if(!release.Wait(5000))throw new TimeoutException("Read gate timed out");return CanvasProjectsPage.QueryProjects(store);};
    // The timed release prevents the pre-fix synchronous implementation from
    // deadlocking the comparison run, while still exposing UI blocking.
    var unblock=Task.Run(()=>{if(entered.Wait(5000)){Thread.Sleep(250);release.Set();}});var clock=Stopwatch.StartNew();page.Refresh();clock.Stop();Task refresh=page.RefreshTaskForTests;
    check(entered.Wait(3000)&&queryThread!=dispatcherThread,"explicit refresh performs manifest I/O away from the dispatcher");
    check(!release.IsSet,"explicit refresh returns before a deliberately delayed project read completes");
    bool responsive=false;Dispatcher.CurrentDispatcher.BeginInvoke(new Action(()=>responsive=true));Pump();check(responsive&&!release.IsSet,"dispatcher handles another action while project files are still being read");
    metrics.Add(new{name="refresh-return-with-250ms-read",milliseconds=clock.Elapsed.TotalMilliseconds});Await(unblock);Await(refresh);check(page.ProjectsForTests.Count==40,"delayed refresh eventually publishes the complete list");
   }
   page.RefreshVisibleForTests=()=>false;page.ProjectQueryForTests=null;page.Visibility=Visibility.Collapsed;
   var times=new List<double>();var allocations=new List<long>();
   for(int round=0;round<4;round++){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();long before=AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;var clock=Stopwatch.StartNew();var next=Page(store);CanvasPageTestSupport.Wait(next);clock.Stop();next.Visibility=Visibility.Collapsed;if(round>0){times.Add(clock.Elapsed.TotalMilliseconds);allocations.Add(AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize-before);}}
   metrics.Add(new{name="build-and-load-40-project-cards",milliseconds=times,managedAllocatedBytes=allocations});
  }
  static void Animation(Action<bool,string> check){
   var callback=typeof(PreviewPicture).GetMethod("FrameChanged",Hidden);
   using(var image=new System.Drawing.Bitmap(32,24))using(var picture=new PreviewPicture(image,new PreviewViewport(32,24),new Settings{AnimateImages=false})){
    var handle=picture.Handle;int invalidations=0;picture.Invalidated+=(s,e)=>invalidations++;Forms.Application.DoEvents();invalidations=0;
    var worker=Task.Run(()=>{for(int i=0;i<1000;i++)callback.Invoke(picture,new object[]{null,EventArgs.Empty});});worker.GetAwaiter().GetResult();check(invalidations==0,"animation callback does not repaint from the decoder thread");Forms.Application.DoEvents();
    check(invalidations==1,"1000 queued animation frame notifications collapse to one repaint");metrics.Add(new{name="1000-animation-notifications",invalidations=invalidations});
    callback.Invoke(picture,new object[]{null,EventArgs.Empty});Forms.Application.DoEvents();check(invalidations==2,"later animation frames repaint after the pending notification is consumed");
    picture.SetPlaybackVisible(false);int before=invalidations;callback.Invoke(picture,new object[]{null,EventArgs.Empty});Forms.Application.DoEvents();check(invalidations==before,"late animation callback cannot repaint a suspended preview");picture.SetPlaybackVisible(true);
    typeof(Forms.Control).GetMethod("RecreateHandle",Hidden).Invoke(picture,null);invalidations=0;callback.Invoke(picture,new object[]{null,EventArgs.Empty});Forms.Application.DoEvents();check(invalidations==1,"preview still repaints after native handle recreation");
    callback.Invoke(picture,new object[]{null,EventArgs.Empty});picture.Dispose();Forms.Application.DoEvents();check(picture.IsDisposed,"closing a preview safely discards an outstanding repaint callback");
   }
  }
 }
}
