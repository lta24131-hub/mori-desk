using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mori;

namespace MoriDesk {
 // VM-only synthetic regressions. No clipboard, desktop input, real profile or
 // user image is accessed, and every constructed Window remains hidden.
 internal static class ImageWorkRegressionTests {
  const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
  static WriteableBitmap Bitmap(){var image=new WriteableBitmap(7,5,96,96,PixelFormats.Bgra32,null);Paint(image,48,96,192);return image;}
  static void Paint(WriteableBitmap image,byte blue,byte green,byte red){var pixels=new byte[image.PixelWidth*image.PixelHeight*4];for(int i=0;i<pixels.Length;i+=4){pixels[i]=blue;pixels[i+1]=green;pixels[i+2]=red;pixels[i+3]=255;}image.WritePixels(new Int32Rect(0,0,image.PixelWidth,image.PixelHeight),pixels,image.PixelWidth*4,0);}
  static byte[] Pixel(string file){using(var stream=File.OpenRead(file)){var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.None,BitmapCacheOption.OnLoad);var image=new FormatConvertedBitmap(decoder.Frames[0],PixelFormats.Bgra32,null,0);var bytes=new byte[4];image.CopyPixels(new Int32Rect(0,0,1,1),bytes,4,0);return bytes;}}
  static void Encode(BitmapSource bitmap,Stream output){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));encoder.Save(output);}
  static bool Cancelled(Task task){try{task.GetAwaiter().GetResult();return false;}catch(OperationCanceledException){return true;}}
  static object Field(MainWindow main,string name){return typeof(MainWindow).GetField(name,Hidden).GetValue(main);}
  static void Set(MainWindow main,string name,object value){typeof(MainWindow).GetField(name,Hidden).SetValue(main,value);}
  static object Call(MainWindow main,string name,params object[] args){return typeof(MainWindow).GetMethod(name,Hidden).Invoke(main,args);}
  static Button Button(MainWindow main,string name){return (Button)main.Window.FindName(name);}
  static void Click(MainWindow main,string name){Button(main,name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));}
  static void StopTimers(MainWindow main){foreach(string name in new[]{"externalTimer","searchTimer","layoutTimer","sizeSaveTimer","sidebarSaveTimer"})((DispatcherTimer)Field(main,name)).Stop();}
  static void PumpUntil(Func<bool> finished){var end=DateTime.UtcNow.AddSeconds(15);while(!finished()&&DateTime.UtcNow<end){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);Thread.Sleep(2);}if(!finished())throw new Exception("synthetic image worker did not finish");}
  static void Select(MainWindow main,string id){var selection=(HashSet<string>)Field(main,"selection");selection.Clear();selection.Add(id);Call(main,"UpdateInspector");}
  internal static void Run(string testRoot,Action<bool,string> check){
   string root=Path.Combine(testRoot,"image-work");Directory.CreateDirectory(root);
   ClipboardFiles(Path.Combine(root,"clipboard"),check);ImageIndex(check);HiddenWindow(Path.Combine(root,"hidden-window"),check);
  }
  static void ClipboardFiles(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);int dispatcherThread=Thread.CurrentThread.ManagedThreadId,encoderThread=dispatcherThread;
   var original=Bitmap();var snapshot=ClipboardImageFile.Snapshot(original);Paint(original,0,0,0);
   check(snapshot.IsFrozen&&!object.ReferenceEquals(snapshot,original),"clipboard snapshot is independent and frozen before background encoding");
   using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false)){
    var task=ClipboardImageFile.CreateAsync(snapshot,root,CancellationToken.None,delegate(BitmapSource image,Stream output){encoderThread=Thread.CurrentThread.ManagedThreadId;entered.Set();if(!release.WaitOne(5000))throw new TimeoutException("encoder regression gate");Encode(image,output);});
    bool running=false;try{running=entered.WaitOne(5000)&&!task.IsCompleted;}finally{release.Set();}
    using(var temporary=task.GetAwaiter().GetResult()){
     check(running&&encoderThread!=dispatcherThread,"PNG encoding and temporary writes do not run on the dispatcher thread");
     check(Pixel(temporary.Path).SequenceEqual(new byte[]{48,96,192,255}),"encoded PNG retains the frozen snapshot despite later clipboard-image edits");
    }
   }
   check(Directory.GetFiles(root).Length==0,"successful temporary PNG lease removes its file when disposed");
   string failedPath=null;bool failed=false;
   var failure=ClipboardImageFile.CreateAsync(snapshot,root,CancellationToken.None,delegate(BitmapSource image,Stream output){failedPath=((FileStream)output).Name;output.WriteByte(137);throw new IOException("synthetic partial disk write");});
   try{failure.GetAwaiter().GetResult();}catch(IOException){failed=true;}
   check(failed&&failedPath!=null&&!File.Exists(failedPath)&&Directory.GetFiles(root).Length==0,"partial PNG write failure always removes its owned temporary file");
   using(var source=new CancellationTokenSource()){
    int writes=0;
    var cancelled=ClipboardImageFile.CreateAsync(snapshot,root,source.Token,delegate(BitmapSource image,Stream output){writes++;Encode(image,output);source.Cancel();});
    check(Cancelled(cancelled)&&writes==1&&Directory.GetFiles(root).Length==0,"stop during uninterruptible PNG encoding discards the output before import can start");
   }
   using(var source=new CancellationTokenSource()){
    source.Cancel();int writes=0;string absent=Path.Combine(root,"pre-cancelled");
    var cancelled=ClipboardImageFile.CreateAsync(snapshot,absent,source.Token,delegate(BitmapSource image,Stream output){writes++;});
    check(Cancelled(cancelled)&&writes==0&&!Directory.Exists(absent),"pre-cancelled PNG work starts no encoder and creates no directory or file");
   }
   string owned=null;bool importFailed=false;
   try{using(var temporary=ClipboardImageFile.CreateAsync(snapshot,root,CancellationToken.None).GetAwaiter().GetResult()){owned=temporary.Path;throw new IOException("synthetic importer failure");}}
   catch(IOException){importFailed=true;}
   check(importFailed&&owned!=null&&!File.Exists(owned),"import failure still disposes the successfully encoded temporary PNG");
   bool unfrozen=false;try{ClipboardImageFile.CreateAsync(original,root,CancellationToken.None);}catch(ArgumentException){unfrozen=true;}
   check(unfrozen&&Directory.GetFiles(root).Length==0,"dispatcher-owned mutable bitmaps are rejected before worker scheduling");
  }
  static void ImageIndex(Action<bool,string> check){
   var eligible=new[]{".png",".JPEG",".gif",".webp",".svg",".psd",".psb",".heic",".dng"}.Select((ext,index)=>new Asset{Id="image-"+index,Ext=ext,Color="#123456",Perceptual="0000000000000000"}).ToList();
   eligible.Add(new Asset{Id="shortcut",Ext=".png",ReferencePath=@"X:\synthetic\source.png",ReferenceSection="pictures",Color="#123456",Perceptual="0000000000000000"});
   var rejected=new[]{".mp4",".mov",".mkv",".webm",".mp3",".wav",".pdf",".AI",".txt",".docx",".step",".qoi",null}.Select((ext,index)=>new Asset{Id="other-"+index,Ext=ext,Color="#123456",Perceptual="0000000000000000"}).ToList();
   rejected.Add(new Asset{Id="directory",Ext=".png",IsDirectory=true,ReferencePath=@"X:\synthetic\folder.png",Color="#123456",Perceptual="0000000000000000"});
   rejected.Add(new Asset{Id="deleted",Ext=".png",Deleted=true,Color="#123456",Perceptual="0000000000000000"});
   rejected.Add(new Asset{Id="bookmark",Ext=".url",Source="https://example.invalid/",Color="#123456",Perceptual="0000000000000000"});
   var mixed=rejected.Concat(eligible).ToArray();var built=new List<string>();
   int count=ImageSimilarityIndex.Build(mixed,a=>built.Add(a.Id),CancellationToken.None);
   check(count==eligible.Count&&built.SequenceEqual(eligible.Select(a=>a.Id)),"mixed-library similarity indexing touches only comparable static/animated images and file shortcuts");
   check(!ImageSimilarityIndex.CanCompare(null)&&rejected.All(a=>!ImageSimilarityIndex.HasIndex(a,false)&&!ImageSimilarityIndex.HasIndex(a,true)),"non-images, documents and reference directories cannot leak into either ranked result even with old fingerprint metadata");
   check(eligible.All(a=>ImageSimilarityIndex.HasIndex(a,false)&&ImageSimilarityIndex.HasIndex(a,true)),"source selection, indexing and result eligibility share one image predicate");
   using(var source=new CancellationTokenSource()){
    source.Cancel();int visited=0;bool stopped=false;
    try{ImageSimilarityIndex.Build(mixed,a=>visited++,source.Token);}catch(OperationCanceledException){stopped=true;}
    check(stopped&&visited==0,"pre-cancelled image index never starts a decoder");
   }
   using(var source=new CancellationTokenSource()){
    var visited=new List<string>();bool stopped=false;
    try{ImageSimilarityIndex.Build(mixed,delegate(Asset asset){visited.Add(asset.Id);source.Cancel();},source.Token);}catch(OperationCanceledException){stopped=true;}
    check(stopped&&visited.SequenceEqual(new[]{eligible[0].Id}),"cancellation completes the current image safely and starts no subsequent image");
   }
   int calls=0;bool failed=false;
   try{ImageSimilarityIndex.Build(eligible,delegate{calls++;throw new IOException("synthetic index storage failure");},CancellationToken.None);}catch(IOException){failed=true;}
   check(failed&&calls==1,"index storage errors surface immediately without continuing an uncommitted search");
  }
  static void HiddenWindow(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);string data=Environment.GetEnvironmentVariable("MORI_DESK_DATA"),theme=Theme.Current;bool classic=Entry.ClassicEnabled,board=Entry.BoardEnabled,interactive=Entry.InteractiveShell;
   var previous=ThumbnailImage.CurrentLibrary;var context=SynchronizationContext.Current;var app=Application.Current??new Application();var shutdown=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
   Environment.SetEnvironmentVariable("MORI_DESK_DATA",Path.Combine(root,"isolated-profile"));Entry.ClassicEnabled=true;Entry.BoardEnabled=false;Entry.InteractiveShell=false;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
   try{using(var library=new Library(Path.Combine(root,"library"))){
    var main=new MainWindow(library){PreviewMode=true};main.DeferDeskServices();main.PendingInformationChoice=()=>MessageBoxResult.No;StopTimers(main);
    try{
     var bitmap=Bitmap();var task=(Task)Call(main,"PasteBitmap",bitmap);
     check((bool)Field(main,"busy")&&Button(main,"Cancel").Visibility==Visibility.Visible&&Button(main,"Cancel").IsEnabled&&!Button(main,"Import").IsEnabled&&!Button(main,"Paste").IsEnabled,"paste publishes busy and a visible stop affordance before its first suspension");
     Click(main,"Cancel");PumpUntil(()=>task.IsCompleted);check(Cancelled(task)&&library.All().Count==0,"immediate paste cancellation reaches the real orchestration before any library import");
     check(!(bool)Field(main,"busy")&&Field(main,"libraryWorkCancellation")==null&&Button(main,"Cancel").Visibility==Visibility.Collapsed&&Button(main,"Import").IsEnabled&&Button(main,"Paste").IsEnabled,"cancelled paste restores controls and disposes its cancellation source");
     task=(Task)Call(main,"PasteBitmap",bitmap);Paint(bitmap,0,0,0);PumpUntil(()=>task.IsCompleted);task.GetAwaiter().GetResult();StopTimers(main);
     var stored=library.All().Single();byte[] bytes=File.ReadAllBytes(library.FullPath(stored));
     check(Pixel(library.FullPath(stored)).SequenceEqual(new byte[]{48,96,192,255})&&((HashSet<string>)Field(main,"selection")).Contains(stored.Id),"successful synthetic paste imports and reveals the immutable snapshot");
     Select(main,stored.Id);Set(main,"similarId","previous-search");Call(main,"SearchByImage",false);
     check((bool)Field(main,"busy")&&Button(main,"Cancel").Visibility==Visibility.Visible&&Convert.ToString(Button(main,"Cancel").Content)=="停止索引","image indexing exposes its own visible stop action before background work");
     ((TextBox)main.Window.FindName("Tags")).Text="draft during indexing";
     Click(main,"Cancel");PumpUntil(()=>!(bool)Field(main,"busy"));StopTimers(main);
     check((string)Field(main,"similarId")=="previous-search"&&Field(main,"libraryWorkCancellation")==null&&Button(main,"Cancel").Visibility==Visibility.Collapsed,"cancelled image indexing preserves the previous search and releases all busy/cancel state");
     check(((TextBox)main.Window.FindName("Tags")).Text=="draft during indexing","image-work cleanup never repopulates or erases an inspector draft");
     ((TextBox)main.Window.FindName("Tags")).Text=stored.Tags;Set(main,"similarId","");Select(main,stored.Id);Call(main,"SearchByImage",true);PumpUntil(()=>!(bool)Field(main,"busy"));StopTimers(main);
     check((string)Field(main,"colorId")==stored.Id&&(string)Field(main,"mediaSection")=="pictures"&&((List<Asset>)Field(main,"visible")).Single().Id==stored.Id,"successful image search opens the comparable-image scope and is reusable after cancellation");
     var another=Bitmap();Paint(another,120,64,32);task=(Task)Call(main,"PasteBitmap",another);
     ((TextBox)main.Window.FindName("Tags")).Text="draft while another image is pasted";PumpUntil(()=>task.IsCompleted);task.GetAwaiter().GetResult();StopTimers(main);
     check(library.All().Count==2&&((TextBox)main.Window.FindName("Tags")).Text=="draft while another image is pasted"&&((HashSet<string>)Field(main,"selection")).SetEquals(new[]{stored.Id}),"completed paste preserves newly typed metadata and the original selection instead of forcing a new image into the inspector");
     check((bool)Field(main,"refreshDeferred")&&(bool)Field(main,"metadataReloadRequired")&&(string)Field(main,"colorId")==stored.Id&&((TextBlock)main.Window.FindName("Status")).Text.Contains("已收藏到"),"deferred paste still commits the image and explains where it was saved without changing the active search");
     check(File.ReadAllBytes(library.FullPath(stored)).SequenceEqual(bytes)&&!main.Window.IsVisible,"synthetic image-work regressions preserve source bytes and never present a desktop window");
    }finally{StopTimers(main);main.CloseForReplacement();}
   }}finally{Environment.SetEnvironmentVariable("MORI_DESK_DATA",data);Entry.ClassicEnabled=classic;Entry.BoardEnabled=board;Entry.InteractiveShell=interactive;ThumbnailImage.CurrentLibrary=previous;SynchronizationContext.SetSynchronizationContext(context);Theme.Set(theme);app.ShutdownMode=shutdown;}
  }
 }
}
