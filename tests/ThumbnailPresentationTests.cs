using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mori;

namespace MoriDesk {
 // VM-only regression entry point. Real Loaded/Unloaded events and the real
 // Library image decoder are exercised; no private Load method is invoked and
 // no source, decoder, dispatcher or build gate is replaced with a fake.
 internal static class ThumbnailPresentationTests {
  const BindingFlags PrivateInstance=BindingFlags.Instance|BindingFlags.NonPublic;
  static string diagnosticRoot;static ThumbnailImage observedImage;
  static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
  static void Until(Func<bool> ready,string description){var watch=Stopwatch.StartNew();while(!ready()&&watch.ElapsedMilliseconds<6000){Pump();Thread.Sleep(1);}if(!ready()){
   var image=observedImage;var asset=image==null?null:image.DataContext as Asset;
   if(diagnosticRoot!=null)File.AppendAllText(Path.Combine(diagnosticRoot,"thumbnail-timeout.txt"),DateTime.UtcNow.ToString("o")+" step="+description+" state="+(image==null?"none":image.PreviewState)+" loaded="+(image!=null&&image.IsLoaded)+" visible="+(image!=null&&image.IsVisible)+" asset="+(asset==null?"none":asset.Id)+" source="+(image!=null&&image.Source!=null)+" slots="+AvailableSlots()+" buildPaths="+ThumbnailBuildGate.ActivePathCount+" decodeFailures="+ThumbnailBuildGate.FailureCount+" worker="+ImageCodec.ActiveWorkerId+Environment.NewLine);
   throw new TimeoutException("Thumbnail presentation did not finish: "+description);
  }Pump();}
  static void Layout(FrameworkElement root,double width,double height){root.Measure(new Size(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();}
  static T Named<T>(DependencyObject root,string name) where T:FrameworkElement {var current=root as T;if(current!=null&&current.Name==name)return current;for(int n=0;n<VisualTreeHelper.GetChildrenCount(root);n++){var child=Named<T>(VisualTreeHelper.GetChild(root,n),name);if(child!=null)return child;}return null;}
  static Rect Bounds(FrameworkElement item,Visual relative){return new Rect(item.TranslatePoint(new Point(),(UIElement)relative),item.RenderSize);}
  static bool Inside(Rect item,FrameworkElement parent){return item.Left>=-.01&&item.Top>=-.01&&item.Right<=parent.ActualWidth+.01&&item.Bottom<=parent.ActualHeight+.01;}
  static void Picture(string path,int width,int height,byte red){byte[] pixels=new byte[width*height*4];for(int n=0;n<pixels.Length;n+=4){pixels[n]=63;pixels[n+1]=125;pixels[n+2]=red;pixels[n+3]=255;}var image=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var file=File.Create(path))encoder.Save(file);}
  static Asset Reference(Library library,string root,string name,int width,int height){string path=Path.Combine(root,name+".png");Picture(path,width,height,173);string id=library.AddReferenceAssets(new[]{path},"").Single();return library.All().Single(a=>a.Id==id);}
  static Asset ExistingDocument(Library library,string extension){
   // A persisted, genuine bounded PNG represents a previously rendered page.
   // No native PDF/AI renderer is launched by this queue/cache regression.
   var asset=new Asset{Id=Guid.NewGuid().ToString("N"),Name="cached-document"+extension,Ext=extension,Relative=Path.Combine("原图","cached-document"+extension),Width=0,Height=0,Perceptual="0000000000000000"};
   File.WriteAllText(library.FullPath(asset),"Existing document cache fixture");Picture(library.ThumbnailPath(asset),81,113,211);return asset;
  }
  // Synchronization only: the permit is returned in Load's finally, after its
  // UI continuation. This lets stale-result assertions wait for completion
  // without pausing the scheduler (which could otherwise mask a version bug).
  static ThumbnailLoadScheduler Scheduler(){return (ThumbnailLoadScheduler)typeof(ThumbnailImage).GetField("Gate",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);}
  static ThumbnailLoadGate SharedGate(){return (ThumbnailLoadGate)typeof(ThumbnailLoadScheduler).GetField("shared",PrivateInstance).GetValue(Scheduler());}
  static ThumbnailLoadGate WarmGate(){return (ThumbnailLoadGate)typeof(ThumbnailLoadScheduler).GetField("warm",PrivateInstance).GetValue(Scheduler());}
  static int Slots(ThumbnailLoadGate gate){return ((SemaphoreSlim)typeof(ThumbnailLoadGate).GetField("slots",PrivateInstance).GetValue(gate)).CurrentCount;}
  static int AvailableSlots(){return Slots(SharedGate());}
  static int WaitingSlots(){return SharedGate().WaitingCount;}
  // The UI owns each real Monitor lease; Users >= 2 proves its decoder has
  // reached that same output path rather than merely taking a shared slot.
  static int BuildUsers(string path){var type=typeof(ThumbnailBuildGate);var sync=type.GetField("sync",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);lock(sync){var entries=(IDictionary)type.GetField("entries",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);var entry=entries[Path.GetFullPath(path)];return entry==null?0:(int)entry.GetType().GetField("Users",PrivateInstance).GetValue(entry);}}
  static void Mount(Grid host,ThumbnailImage image){host.Children.Clear();host.Children.Add(image);Layout(host,340,240);Until(()=>image.IsLoaded,"actual Loaded event");}
  static void Unmount(Grid host,ThumbnailImage image){host.Children.Clear();Layout(host,340,240);Until(()=>!image.IsLoaded,"actual Unloaded event");}
  static void Append(Grid host,ThumbnailImage image){host.Children.Add(image);Layout(host,340,240);Until(()=>image.IsLoaded,"additional actual Loaded event");}
  static void Remove(Grid host,ThumbnailImage image){host.Children.Remove(image);Layout(host,340,240);Until(()=>!image.IsLoaded,"individual actual Unloaded event");}
  static IDisposable Drained(Task<IDisposable> task){Until(()=>task.IsCompleted,"pause/drain completion");return task.GetAwaiter().GetResult();}
  internal static void Run(string root,Action<bool,string> check){
   // ScenarioRunner additionally checks the QEMU/KVM/Bochs machine identity.
   if(!Path.GetFullPath(root).StartsWith(@"C:\NookLab\Audit\",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Thumbnail UI/decoder regressions require the isolated VM fixture root.");
   Directory.CreateDirectory(root);diagnosticRoot=root;var app=Application.Current??new Application();var shutdown=app.ShutdownMode;var previousLibrary=ThumbnailImage.CurrentLibrary;string previousTheme=Theme.Current??"dark";app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
   try{Lifecycle(root,check);StorageResume(root,check);Saturation(root,check);Presentation(root,check);}finally{observedImage=null;diagnosticRoot=null;ThumbnailImage.CurrentLibrary=previousLibrary;Theme.Set(previousTheme);app.ShutdownMode=shutdown;}
  }
  static void Lifecycle(string root,Action<bool,string> check){
   using(var library=new Library(Path.Combine(root,"library"))){
    library.EnableReferenceAssets();ThumbnailImage.CurrentLibrary=library;ThumbnailImage.RetryFailedThumbnails();
    var host=new Grid{Width=340,Height=240};var image=new ThumbnailImage{Width=220,Height=160};observedImage=image;int loadedEvents=0,unloadedEvents=0;image.Loaded+=delegate{loadedEvents++;};image.Unloaded+=delegate{unloadedEvents++;};
    using(var source=new HwndSource(new HwndSourceParameters("Nook VM thumbnail lifecycle"){Width=340,Height=240,WindowStyle=0})){
     source.RootVisual=host;int baseline=ThumbnailBuildGate.ActivePathCount;IDisposable held=null,pause=null;
     try{
      var good=Reference(library,root,"normal",67,43);image.DataContext=good;Mount(host,image);Until(()=>image.PreviewState=="ready","normal decoder ready");
      check(image.Source is BitmapSource&&good.Width==67&&good.Height==43&&File.Exists(library.ThumbnailPath(good)),"real normal image reaches ready with a persisted thumbnail and decoded dimensions");
      string badPath=Path.Combine(root,"damaged.png");File.WriteAllBytes(badPath,new byte[]{1,2,3,4,5});string badId=library.AddReferenceAssets(new[]{badPath},"").Single();var damaged=library.All().Single(a=>a.Id==badId);image.DataContext=damaged;
      Until(()=>image.PreviewState=="unavailable","real damaged image unavailable");
      check(image.Source==null&&!File.Exists(library.ThumbnailPath(damaged)),"real damaged image reaches unavailable, without showing the previously selected image");
      image.DataContext=good;Until(()=>image.PreviewState=="ready","recycled good image ready");
      check(image.Source!=null,"changing back from a damaged image restores the good cached thumbnail");

      var old=Reference(library,root,"old-binding",37,19);var current=Reference(library,root,"new-binding",61,47);held=ThumbnailBuildGate.Enter(library.ThumbnailPath(old));image.DataContext=old;Until(()=>image.PreviewState=="loading"&&AvailableSlots()==2,"old request owns its real scheduler slot");
      image.DataContext=current;check(image.Source==null,"DataContext replacement clears the old Source immediately");Until(()=>image.PreviewState=="ready","new binding ready while old decoder is blocked");var latest=image.Source;
      held.Dispose();held=null;Until(()=>AvailableSlots()==3&&ThumbnailBuildGate.ActivePathCount==baseline,"old request and UI continuation completed");
      check(ReferenceEquals(image.Source,latest)&&ReferenceEquals(image.DataContext,current)&&image.PreviewState=="ready"&&((BitmapSource)image.Source).PixelWidth==61,"completed old request cannot overwrite a recycled tile's newer binding or ready state");

      var unloading=Reference(library,root,"unloaded",53,31);held=ThumbnailBuildGate.Enter(library.ThumbnailPath(unloading));image.DataContext=unloading;Until(()=>image.PreviewState=="loading"&&AvailableSlots()==2,"unload request entered decoder");Unmount(host,image);
      check(image.Source==null&&image.PreviewState=="idle","actual Unloaded clears Source and loading state immediately");held.Dispose();held=null;Until(()=>AvailableSlots()==3&&ThumbnailBuildGate.ActivePathCount==baseline,"unloaded decoder and UI continuation completed");
      check(image.Source==null&&image.PreviewState=="idle"&&!image.IsLoaded,"late decoder completion does not repopulate an unloaded tile");Mount(host,image);Until(()=>image.PreviewState=="ready","remounted cached image ready");
      check(image.Source!=null,"remounting after cancellation can reuse the valid completed disk thumbnail");

      var hiding=Reference(library,root,"hidden-active",57,35);held=ThumbnailBuildGate.Enter(library.ThumbnailPath(hiding));image.DataContext=hiding;Until(()=>image.PreviewState=="loading"&&AvailableSlots()==2&&BuildUsers(library.ThumbnailPath(hiding))>=2,"hidden request has entered its real decoder");host.Visibility=Visibility.Collapsed;Layout(host,340,240);Until(()=>!image.IsVisible,"parent collapse changes effective thumbnail visibility");
      check(image.IsLoaded&&image.Source==null&&image.PreviewState=="idle"&&AvailableSlots()==2,"collapsing a loaded parent clears tile state while allowing its already active decoder to finish safely");image.DataContext=good;
      check(image.Source==null&&image.PreviewState=="idle"&&WaitingSlots()==0,"rebinding inside a collapsed parent does not start a new thumbnail request");host.Visibility=Visibility.Visible;Layout(host,340,240);Until(()=>image.PreviewState=="ready","revealed parent displays its newer warm binding");var revealed=image.Source;
      held.Dispose();held=null;Until(()=>AvailableSlots()==3&&ThumbnailBuildGate.ActivePathCount==baseline,"hidden old decoder completes outside a pause");
      check(ReferenceEquals(image.DataContext,good)&&ReferenceEquals(image.Source,revealed)&&image.PreviewState=="ready"&&((BitmapSource)image.Source).PixelWidth==67,"an old active decoder cannot overwrite a newer binding after its parent is shown again");
      var hiddenCompletion=Reference(library,root,"hidden-completion",59,37);held=ThumbnailBuildGate.Enter(library.ThumbnailPath(hiddenCompletion));image.DataContext=hiddenCompletion;Until(()=>AvailableSlots()==2&&BuildUsers(library.ThumbnailPath(hiddenCompletion))>=2,"second hidden request owns its real decode slot");host.Visibility=Visibility.Collapsed;Layout(host,340,240);held.Dispose();held=null;Until(()=>AvailableSlots()==3&&ThumbnailBuildGate.ActivePathCount==baseline,"active decoder completes while its loaded parent stays collapsed");
      check(image.IsLoaded&&!image.IsVisible&&image.Source==null&&image.PreviewState=="idle"&&File.Exists(library.ThumbnailPath(hiddenCompletion)),"hidden completion may persist a safe derived PNG but cannot refill the invisible tile");host.Visibility=Visibility.Visible;Layout(host,340,240);Until(()=>image.PreviewState=="ready","showing the same binding reuses the completed hidden decode");
      check(image.Source is BitmapSource&&((BitmapSource)image.Source).PixelWidth==59,"revealing the same binding restores the correct cached image without manual refresh");

      var pausing=Reference(library,root,"paused-success",71,39);held=ThumbnailBuildGate.Enter(library.ThumbnailPath(pausing));image.DataContext=pausing;Until(()=>image.PreviewState=="loading"&&AvailableSlots()==2,"pause success request entered decoder");int loadedBeforePause=loadedEvents,unloadedBeforePause=unloadedEvents;var drain=ThumbnailImage.PauseAndDrainAsync();check(!drain.IsCompleted,"pause/drain waits for the actual active thumbnail request");held.Dispose();held=null;pause=Drained(drain);
      check(image.Source==null&&image.PreviewState=="idle","completion during a drained pause cannot publish a success or leave loading text behind");
      var hiddenResume=Reference(library,root,"hidden-resume",101,63);var hiddenImage=new ThumbnailImage{DataContext=hiddenResume,Width=100,Height=65,Visibility=Visibility.Collapsed};host.Children.Add(hiddenImage);Layout(host,340,240);Until(()=>hiddenImage.IsLoaded,"hidden recovery fixture is loaded");ThumbnailImage.ResumeVisible(host);
      check(image.Source==null&&image.PreviewState=="idle"&&AvailableSlots()==0,"explicit view recovery cannot bypass a still active pause");pause.Dispose();pause=null;long resumeDecodes=ThumbnailImage.DecodedCache.DecodeCount;ThumbnailImage.ResumeVisible(host);ThumbnailImage.ResumeVisible(host);Until(()=>image.PreviewState=="ready","same-binding visible thumbnail resumes after pause disposal without unloading");
      check(image.Source!=null&&ReferenceEquals(image.DataContext,pausing)&&loadedEvents==loadedBeforePause&&unloadedEvents==unloadedBeforePause&&ThumbnailImage.DecodedCache.DecodeCount==resumeDecodes+1,"retained visible preview resumes the same binding exactly once without Loaded/DataContext replacement");var resumed=image.Source;ThumbnailImage.ResumeVisible(host);
      check(ReferenceEquals(image.Source,resumed)&&ThumbnailImage.DecodedCache.DecodeCount==resumeDecodes+1&&hiddenImage.Source==null&&hiddenImage.PreviewState=="idle"&&!File.Exists(library.ThumbnailPath(hiddenResume)),"recovery preserves already-ready pixels and leaves hidden originals unprocessed");Remove(host,hiddenImage);

      var failing=Reference(library,root,"paused-error",49,29);library.BuildThumbnail(failing);string thumbnail=library.ThumbnailPath(failing);File.WriteAllBytes(thumbnail,new byte[]{0,1,2,3,4});held=ThumbnailBuildGate.Enter(thumbnail);image.DataContext=failing;Until(()=>image.PreviewState=="loading"&&AvailableSlots()==2,"pause error request entered cached-file read");drain=ThumbnailImage.PauseAndDrainAsync();held.Dispose();held=null;pause=Drained(drain);
      check(image.Source==null&&image.PreviewState=="idle","a corrupt cached-PNG repair during pause cannot publish stale UI state after cancellation");pause.Dispose();pause=null;Unmount(host,image);Mount(host,image);Until(()=>image.PreviewState=="ready","automatic recovery from corrupt derived cache without deleting it in the fixture");
      check(image.Source!=null&&File.Exists(library.FullPath(failing)),"cache failure recovery preserves the original source and can rebuild the real image");
      image.DataContext=new Asset{Ext=".mp3",Name="audio has no image thumbnail"};check(image.Source==null&&image.PreviewState=="idle","recycling a ready image into a non-thumbnail asset leaves no stale Source or status");
      check(ThumbnailBuildGate.ActivePathCount==baseline,"lifecycle paths release every per-output build gate");
     }finally{if(held!=null)held.Dispose();if(pause!=null)pause.Dispose();host.Children.Clear();Pump();using(Drained(ThumbnailImage.PauseAndDrainAsync())){}source.RootVisual=null;}
    }
   }
   ThumbnailImage.CurrentLibrary=null;
  }
  static void StorageResume(string root,Action<bool,string> check){
   string previousData=Environment.GetEnvironmentVariable("MORI_DESK_DATA");bool classic=Entry.ClassicEnabled,board=Entry.BoardEnabled,interactive=Entry.InteractiveShell;var context=SynchronizationContext.Current;
   Environment.SetEnvironmentVariable("MORI_DESK_DATA",Path.Combine(root,"storage-resume-settings"));Entry.ClassicEnabled=true;Entry.BoardEnabled=false;Entry.InteractiveShell=false;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
   try{
    new Settings{CloseToTray=false}.SaveTo(Path.Combine(Settings.ConfigDir,"settings.json"));
    using(var library=new Library(Path.Combine(root,"storage-resume-library"))){
     library.EnableReferenceAssets();var selected=Reference(library,root,"storage-selected",107,69);var grouped=Reference(library,root,"storage-grouped",109,71);string collection=library.CreateLocalCollection("Retained cover");library.MoveToLocalCollection(new[]{grouped.Id},collection);
     byte[] selectedBytes=File.ReadAllBytes(selected.ReferencePath),groupedBytes=File.ReadAllBytes(grouped.ReferencePath);DateTime selectedTime=File.GetLastWriteTimeUtc(selected.ReferencePath),groupedTime=File.GetLastWriteTimeUtc(grouped.ReferencePath);
     MainWindow main=null;IDisposable selectedHeld=null,groupedHeld=null,pause=null;
     try{
      main=new MainWindow(library){PreviewMode=true};main.DeferDeskServices();main.SetSection("shortcuts");((HashSet<string>)typeof(MainWindow).GetField("selection",PrivateInstance).GetValue(main)).Add(selected.Id);typeof(MainWindow).GetMethod("UpdateInspector",PrivateInstance).Invoke(main,null);
      selectedHeld=ThumbnailBuildGate.Enter(library.ThumbnailPath(selected));groupedHeld=ThumbnailBuildGate.Enter(library.ThumbnailPath(grouped));main.Window.Width=1280;main.Window.Height=820;main.Window.Loaded+=delegate{Application.Current.ShutdownMode=ShutdownMode.OnExplicitShutdown;};main.Window.Show();main.Window.UpdateLayout();
      var detail=(ThumbnailImage)main.Window.FindName("DetailPreview");var collections=(WrapPanel)main.Window.FindName("LocalCollectionList");var cover=MainWindow.VisualChild<ThumbnailImage>(collections);observedImage=detail;
      Until(()=>cover!=null&&detail.IsLoaded&&detail.IsVisible&&cover.IsLoaded&&cover.IsVisible&&detail.PreviewState=="loading"&&cover.PreviewState=="loading"&&BuildUsers(library.ThumbnailPath(selected))>=2&&BuildUsers(library.ThumbnailPath(grouped))>=2,"real inspector and local-collection cover have started their blocked decoders");
      object detailBinding=detail.DataContext,coverBinding=cover.DataContext;int detailUnloaded=0,coverUnloaded=0;detail.Unloaded+=delegate{detailUnloaded++;};cover.Unloaded+=delegate{coverUnloaded++;};
      foreach(string field in new[]{"Name","Tags","Notes","Source"})((TextBox)main.Window.FindName(field)).Text="unsaved "+field;
      var drain=ThumbnailImage.PauseAndDrainAsync();selectedHeld.Dispose();selectedHeld=null;groupedHeld.Dispose();groupedHeld=null;pause=Drained(drain);
      check(detail.Source==null&&cover.Source==null&&detail.PreviewState=="idle"&&cover.PreviewState=="idle"&&detail.IsLoaded&&cover.IsLoaded,"paused storage work leaves retained inspector and folder-cover controls loaded with blank idle previews");pause.Dispose();pause=null;
      main.ResumeStorageView();Until(()=>detail.PreviewState=="ready"&&cover.PreviewState=="ready","the production storage-cancel recovery restores retained inspector and collection previews");
      check(detail.Source is BitmapSource&&cover.Source is BitmapSource&&((BitmapSource)detail.Source).PixelWidth==107&&((BitmapSource)cover.Source).PixelWidth==109&&ReferenceEquals(detail.DataContext,detailBinding)&&ReferenceEquals(cover.DataContext,coverBinding)&&ReferenceEquals(cover,MainWindow.VisualChild<ThumbnailImage>(collections))&&detailUnloaded==0&&coverUnloaded==0,"storage recovery restores the original retained preview controls and bindings without rebuilding the inspector or collection cards");
      check(new[]{"Name","Tags","Notes","Source"}.All(field=>((TextBox)main.Window.FindName(field)).Text=="unsaved "+field)&&library.All().Single(asset=>asset.Id==selected.Id).Notes=="","storage recovery leaves all four inspector drafts intact without silently saving or refreshing metadata");
      check(File.ReadAllBytes(selected.ReferencePath).SequenceEqual(selectedBytes)&&File.ReadAllBytes(grouped.ReferencePath).SequenceEqual(groupedBytes)&&File.GetLastWriteTimeUtc(selected.ReferencePath)==selectedTime&&File.GetLastWriteTimeUtc(grouped.ReferencePath)==groupedTime,"storage recovery only reads derived previews and preserves both referenced originals byte-for-byte and timestamp-for-timestamp");
     }finally{
      if(selectedHeld!=null)selectedHeld.Dispose();if(groupedHeld!=null)groupedHeld.Dispose();if(pause!=null)pause.Dispose();if(main!=null){main.PendingInformationChoice=()=>MessageBoxResult.No;main.CloseForReplacement();}using(Drained(ThumbnailImage.PauseAndDrainAsync())){}
     }
    }
   }finally{observedImage=null;ThumbnailImage.CurrentLibrary=null;Environment.SetEnvironmentVariable("MORI_DESK_DATA",previousData);Entry.ClassicEnabled=classic;Entry.BoardEnabled=board;Entry.InteractiveShell=interactive;SynchronizationContext.SetSynchronizationContext(context);}
  }
  static void Saturation(string root,Action<bool,string> check){
   root=Path.Combine(root,"saturated-slots");Directory.CreateDirectory(root);
   using(var library=new Library(Path.Combine(root,"library"))){
    library.EnableReferenceAssets();ThumbnailImage.CurrentLibrary=library;ThumbnailImage.RetryFailedThumbnails();
    var good=Reference(library,root,"warm-image",73,45);var changed=Reference(library,root,"warm-source-changes",43,27);
    // This controlled video asset has an existing, genuine derived PNG. It
    // exercises the production video scheduling branch without playing video
    // or asking a codec to manufacture this unrelated concurrency fixture.
    var video=new Asset{Id=Guid.NewGuid().ToString("N"),Name="cached-video.mp4",Ext=".mp4",Relative=Path.Combine("原图","cached-video.mp4"),Width=320,Height=180,Perceptual="0000000000000000"};
    File.WriteAllBytes(library.FullPath(video),new byte[]{0,0,0,24,102,116,121,112});Picture(library.ThumbnailPath(video),80,45,207);
    var goodBitmap=library.ReadThumbnail(good);var videoBitmap=library.ReadThumbnail(video);var oldChangedBitmap=library.ReadThumbnail(changed);
    var documents=new[]{".pdf",".AI",".txt",".docx",".xlsx",".pptx",".odt",".ods",".odp"}.Select(extension=>ExistingDocument(library,extension)).ToArray();
    var documentBitmaps=documents.Select(library.ReadThumbnail).ToArray();
    string textPath=Path.Combine(root,"live-document.txt");File.WriteAllText(textPath,"A real text document whose thumbnail has no source pixel dimensions.");string textId=library.AddReferenceAssets(new[]{textPath},"").Single();var textDocument=library.All().Single(a=>a.Id==textId);var textBitmap=library.ReadThumbnail(textDocument);
    check(documents.All(asset=>asset.Width==0&&asset.Height==0)&&documentBitmaps.All(bitmap=>bitmap!=null&&bitmap.IsFrozen)&&textBitmap!=null&&textDocument.Width==0&&textDocument.Height==0&&!string.IsNullOrEmpty(textDocument.Perceptual),"existing PDF/AI/Office page caches and a real text-document render preserve unknown source pixel dimensions");
    var guard=documents[0];string guardPerceptual=guard.Perceptual,guardExtension=guard.Ext;guard.Perceptual="";
    check(!library.MayHaveWarmThumbnail(guard)&&library.TryReadWarmThumbnail(guard)==null,"a bounded document cache with no completed perceptual index cannot bypass ordinary loading");guard.Perceptual=guardPerceptual;
    foreach(string extension in new[]{".png",".psd",".svg",".mp4"}){guard.Ext=extension;check(!library.MayHaveWarmThumbnail(guard)&&library.TryReadWarmThumbnail(guard)==null,"unknown original dimensions do not admit incomplete "+extension+" metadata to the warm lane");}guard.Ext=guardExtension;
    guard.Width=-1;check(!library.MayHaveWarmThumbnail(guard)&&library.TryReadWarmThumbnail(guard)==null,"negative document dimensions remain invalid even with a healthy cached PNG");guard.Width=1;check(!library.MayHaveWarmThumbnail(guard)&&library.TryReadWarmThumbnail(guard)==null,"partially populated document dimensions remain on the normal route");guard.Width=0;
    string textStamp=textDocument.ThumbnailSourceStamp;textDocument.ThumbnailSourceStamp="";check(!library.MayHaveWarmThumbnail(textDocument)&&library.TryReadWarmThumbnail(textDocument)==null,"a document shortcut with a missing source stamp cannot bypass source validation");textDocument.ThumbnailSourceStamp=textStamp+"-stale";check(library.TryReadWarmThumbnail(textDocument)==null,"a document shortcut with mismatching source metadata cannot publish its cached old page");textDocument.ThumbnailSourceStamp=textStamp;
    var oversized=ExistingDocument(library,".pdf");Picture(library.ThumbnailPath(oversized),513,27,217);ThumbnailImage.DecodedCache.Read(library.ThumbnailPath(oversized));
    check(!library.MayHaveWarmThumbnail(oversized)&&library.TryReadWarmThumbnail(oversized)==null,"unknown document dimensions never admit an oversized generated PNG to the positive warm cache");
    check(goodBitmap!=null&&videoBitmap!=null&&oldChangedBitmap!=null&&goodBitmap.IsFrozen&&videoBitmap.IsFrozen&&oldChangedBitmap.IsFrozen,"saturation fixture prewarms real frozen image/reference and existing-video PNGs through Library.ReadThumbnail");
    var cold=Enumerable.Range(0,3).Select(n=>Reference(library,root,"blocked-cold-"+n,47+n*6,31+n*4)).ToArray();
    var abandoned=cold.Select(asset=>new ThumbnailImage{DataContext=asset,Width=100,Height=65}).ToArray();
    var held=new IDisposable[3];IDisposable pause=null,warmHeld=null;Task<IDisposable> drain=null;
    var host=new Grid{Width=340,Height=240};int baseline=ThumbnailBuildGate.ActivePathCount;
    using(var source=new HwndSource(new HwndSourceParameters("Nook VM saturated thumbnail slots"){Width=340,Height=240,WindowStyle=0})){
     source.RootVisual=host;
     try{
      var warmAdmission=WarmGate().EnterAsync();Until(()=>warmAdmission.IsCompleted,"test owns the real warm-probe admission");warmHeld=warmAdmission.GetAwaiter().GetResult();
      var independent=Reference(library,root,"cold-with-blocked-warm-probe",83,59);var independentImage=new ThumbnailImage{DataContext=independent,Width=100,Height=65};observedImage=independentImage;Append(host,independentImage);Until(()=>independentImage.PreviewState=="ready","cold reference progresses while the actual warm-probe gate is held");
      check(warmHeld!=null&&Slots(WarmGate())==0&&independentImage.Source is BitmapSource&&((BitmapSource)independentImage.Source).IsFrozen&&independent.Width==83&&independent.Height==59&&File.Exists(library.ThumbnailPath(independent)),"a genuinely cold mounted tile decodes and becomes ready without joining the occupied warm-probe lane");
      Remove(host,independentImage);Until(()=>AvailableSlots()==3,"independent cold decoder returns its shared slot");warmHeld.Dispose();warmHeld=null;
      for(int n=0;n<3;n++){held[n]=ThumbnailBuildGate.Enter(library.ThumbnailPath(cold[n]));host.Children.Add(abandoned[n]);}
      Layout(host,340,240);observedImage=abandoned[0];
      Until(()=>abandoned.All(image=>image.IsLoaded&&image.PreviewState=="loading")&&AvailableSlots()==0&&cold.All(asset=>BuildUsers(library.ThumbnailPath(asset))>=2),"all three actual decoders wait on the three UI-held output paths");
      check(held.All(lease=>lease!=null)&&AvailableSlots()==0&&cold.All(asset=>!File.Exists(library.ThumbnailPath(asset))),"three real cold-reference decoders saturate every shared slot before any derived PNG exists");
      host.Children.Clear();Layout(host,340,240);Until(()=>abandoned.All(image=>!image.IsLoaded),"three actual Unloaded events while all output locks remain held");
      check(abandoned.All(image=>image.Source==null&&image.PreviewState=="idle")&&AvailableSlots()==0&&cold.All(asset=>BuildUsers(library.ThumbnailPath(asset))>=2),"unloading clears each tile but its blocked real decoder still owns a shared slot");

      var hiddenOld=Reference(library,root,"hidden-queued-old",91,55);var hiddenNew=Reference(library,root,"hidden-queued-new",93,57);var visibilityHost=new Grid();var visibilityImage=new ThumbnailImage{DataContext=hiddenOld,Width=100,Height=65};visibilityHost.Children.Add(visibilityImage);host.Children.Add(visibilityHost);Layout(host,340,240);observedImage=visibilityImage;
      Until(()=>visibilityImage.IsLoaded&&visibilityImage.IsVisible&&WaitingSlots()==1,"a parented visible thumbnail queues behind all three active decoders");visibilityHost.Visibility=Visibility.Collapsed;Layout(host,340,240);Until(()=>WaitingSlots()==0&&!visibilityImage.IsVisible,"parent collapse cancels its actual queued admission");
      check(visibilityImage.IsLoaded&&visibilityImage.Source==null&&visibilityImage.PreviewState=="idle"&&!File.Exists(library.ThumbnailPath(hiddenOld))&&AvailableSlots()==0,"collapsed gallery rows stay loaded but no longer occupy the decoder queue or build hidden previews");visibilityImage.DataContext=hiddenNew;Pump();
      check(WaitingSlots()==0&&visibilityImage.Source==null&&visibilityImage.PreviewState=="idle"&&!File.Exists(library.ThumbnailPath(hiddenNew)),"changing a hidden row binding leaves both old and new cold originals unprocessed");visibilityHost.Visibility=Visibility.Visible;Layout(host,340,240);Until(()=>WaitingSlots()==1&&visibilityImage.PreviewState=="loading","showing the parent automatically queues its latest cold binding");
      check(visibilityImage.IsVisible&&ReferenceEquals(visibilityImage.DataContext,hiddenNew),"restoring parent visibility resumes the latest binding without another Loaded event or manual refresh");visibilityHost.Visibility=Visibility.Collapsed;Layout(host,340,240);Until(()=>WaitingSlots()==0,"second collapse cancels the resumed cold admission");visibilityImage.DataContext=good;visibilityHost.Visibility=Visibility.Visible;Layout(host,340,240);Until(()=>visibilityImage.PreviewState=="ready","showing a rebound warm row bypasses the still saturated decoder queue");
      check(ReferenceEquals(visibilityImage.Source,goodBitmap)&&!File.Exists(library.ThumbnailPath(hiddenOld))&&!File.Exists(library.ThumbnailPath(hiddenNew))&&AvailableSlots()==0&&WaitingSlots()==0,"repeated hide/rebind/show displays only the newest warm result and never starts either abandoned cold decode");host.Children.Remove(visibilityHost);Layout(host,340,240);Until(()=>!visibilityImage.IsLoaded,"visibility fixture unloads cleanly");

      long warmDecodes=ThumbnailImage.DecodedCache.DecodeCount;
      var hotImage=new ThumbnailImage{DataContext=good,Width=100,Height=65};observedImage=hotImage;Append(host,hotImage);Until(()=>hotImage.PreviewState=="ready","warm image bypasses three occupied decode slots");
      check(ReferenceEquals(hotImage.Source,goodBitmap)&&((BitmapSource)hotImage.Source).IsFrozen&&ThumbnailImage.DecodedCache.DecodeCount==warmDecodes&&AvailableSlots()==0&&held.All(lease=>lease!=null),"visible warm image reuses the identical frozen instance with zero decodes while every shared slot and output lock is occupied");
      var hotVideo=new ThumbnailImage{DataContext=video,Width=100,Height=65};observedImage=hotVideo;Append(host,hotVideo);Until(()=>hotVideo.PreviewState=="ready","warm video poster bypasses three occupied decode slots");
      check(ReferenceEquals(hotVideo.Source,videoBitmap)&&((BitmapSource)hotVideo.Source).IsFrozen&&ThumbnailImage.DecodedCache.DecodeCount==warmDecodes&&AvailableSlots()==0&&held.All(lease=>lease!=null),"visible warm video poster reuses the identical frozen instance without another decode or a free shared slot");
      for(int n=0;n<documents.Length;n++){
       var document=documents[n];var hotDocument=new ThumbnailImage{DataContext=document,Width=100,Height=65};observedImage=hotDocument;Append(host,hotDocument);Until(()=>hotDocument.PreviewState=="ready","warm "+document.Ext+" document bypasses three occupied decode slots");
       check(ReferenceEquals(hotDocument.Source,documentBitmaps[n])&&ThumbnailImage.DecodedCache.DecodeCount==warmDecodes&&AvailableSlots()==0&&held.All(lease=>lease!=null)&&document.Width==0&&document.Height==0,"visible "+document.Ext+" cached page reuses its verified frozen PNG immediately without decoding or inventing original pixel dimensions");Remove(host,hotDocument);
      }
      var hotText=new ThumbnailImage{DataContext=textDocument,Width=100,Height=65};observedImage=hotText;Append(host,hotText);Until(()=>hotText.PreviewState=="ready","real text-document shortcut bypasses three occupied decode slots");
      check(ReferenceEquals(hotText.Source,textBitmap)&&ThumbnailImage.DecodedCache.DecodeCount==warmDecodes&&AvailableSlots()==0&&textDocument.Width==0&&textDocument.Height==0,"a real text-document shortcut reuses its verified page cache while preserving source-stamp checks and unknown original dimensions");Remove(host,hotText);

      var miss=Reference(library,root,"visible-cold-miss",89,53);var coldImage=new ThumbnailImage{DataContext=miss,Width=100,Height=65};observedImage=coldImage;Append(host,coldImage);
      Until(()=>WaitingSlots()>=1,"cold warm-probe miss has actually queued for a shared decode slot");
      check(coldImage.Source==null&&coldImage.PreviewState=="loading"&&!File.Exists(library.ThumbnailPath(miss))&&AvailableSlots()==0&&ThumbnailImage.DecodedCache.DecodeCount==warmDecodes,"a real cold miss stays loading without stale pixels or decoding while all shared slots are occupied");
      string originalStamp=ThumbnailBuildGate.SourceStamp(library.FullPath(changed));DateTime changedTime=File.GetLastWriteTimeUtc(library.FullPath(changed)).AddSeconds(2);Picture(library.FullPath(changed),79,41,229);File.SetLastWriteTimeUtc(library.FullPath(changed),changedTime);
      var changedImage=new ThumbnailImage{DataContext=changed,Width=100,Height=65};observedImage=changedImage;Append(host,changedImage);Until(()=>WaitingSlots()>=2,"changed reference rejects warm pixels and actually queues for rebuilding");
      check(ThumbnailBuildGate.SourceStamp(library.FullPath(changed))!=originalStamp&&changedImage.Source==null&&changedImage.PreviewState=="loading"&&AvailableSlots()==0&&ThumbnailImage.DecodedCache.DecodeCount==warmDecodes,"a changed reference source cannot publish its still-cached old frozen thumbnail through the warm bypass");

      var recycled=abandoned[0];recycled.DataContext=good;observedImage=recycled;Append(host,recycled);Until(()=>recycled.PreviewState=="ready","recycled blocked tile accepts a newer warm binding");
      check(ReferenceEquals(recycled.Source,goodBitmap)&&ReferenceEquals(recycled.DataContext,good)&&ThumbnailImage.DecodedCache.DecodeCount==warmDecodes&&AvailableSlots()==0&&WaitingSlots()>=2,"a recycled tile can show its newer warm binding before its old blocked decoder finishes");
      // Release this tile's OLD decoder before pausing. Otherwise a missing
      // generation check could be hidden by the pause's publication barrier.
      held[0].Dispose();held[0]=null;
      Until(()=>BuildUsers(library.ThumbnailPath(cold[0]))==0&&File.Exists(library.ThumbnailPath(cold[0]))&&coldImage.PreviewState=="ready"&&changedImage.PreviewState=="ready"&&AvailableSlots()==1&&WaitingSlots()==0,"old decoder and queued visible misses complete with no active pause");
      check(ReferenceEquals(recycled.Source,goodBitmap)&&ReferenceEquals(recycled.DataContext,good)&&recycled.PreviewState=="ready"&&held.Skip(1).All(lease=>lease!=null),"an old saturated decoder completes outside any pause without overwriting its tile's newer warm binding");
      check(coldImage.Source!=null&&changedImage.Source!=null&&!ReferenceEquals(changedImage.Source,oldChangedBitmap),"releasing one real slot lets both queued visible misses become ready while the other two decoders remain blocked");
      drain=ThumbnailImage.PauseAndDrainAsync();check(!drain.IsCompleted&&held.Skip(1).All(lease=>lease!=null),"pause/drain cannot complete while two real decoders are still blocked on held output locks");
      long pausedDecodes=ThumbnailImage.DecodedCache.DecodeCount;
      var pausedHot=new ThumbnailImage{DataContext=good,Width=100,Height=65};observedImage=pausedHot;Append(host,pausedHot);
      check(pausedHot.Source==null&&pausedHot.PreviewState=="idle"&&ThumbnailImage.DecodedCache.DecodeCount==pausedDecodes,"a newly loaded warm tile cannot bypass an active pause or start another decode");
      for(int n=0;n<held.Length;n++)if(held[n]!=null){held[n].Dispose();held[n]=null;}
      pause=Drained(drain);drain=null;
      check(ReferenceEquals(recycled.Source,goodBitmap)&&ReferenceEquals(recycled.DataContext,good)&&recycled.PreviewState=="ready","after the real old decode completes and drains, it cannot overwrite the tile's newer warm binding");
      check(abandoned.Skip(1).All(image=>!image.IsLoaded&&image.Source==null&&image.PreviewState=="idle"),"completed saturated decoders cannot repopulate either genuinely unloaded tile");
      check(coldImage.Source!=null&&changedImage.Source!=null&&coldImage.PreviewState=="ready"&&changedImage.PreviewState=="ready"&&WaitingSlots()==0&&ThumbnailBuildGate.ActivePathCount==baseline,"draining the remaining decoders preserves already-ready visible results and releases every path gate");
      pause.Dispose();pause=null;
      Remove(host,coldImage);Remove(host,changedImage);Append(host,coldImage);observedImage=coldImage;Until(()=>coldImage.PreviewState=="ready","cold miss becomes ready after released locks and resume");Append(host,changedImage);observedImage=changedImage;Until(()=>changedImage.PreviewState=="ready","changed reference rebuilds after released locks and resume");
      Until(()=>AvailableSlots()==3&&WaitingSlots()==0&&ThumbnailBuildGate.ActivePathCount==baseline,"saturated lifecycle returns every decode slot and path gate");
      check(coldImage.Source is BitmapSource&&((BitmapSource)coldImage.Source).PixelWidth==89&&miss.Width==89&&miss.Height==53,"the visible cold miss eventually becomes ready through the real image decoder after resume");
      check(changedImage.Source is BitmapSource&&!ReferenceEquals(changedImage.Source,oldChangedBitmap)&&((BitmapSource)changedImage.Source).PixelWidth==79&&changed.Width==79&&changed.Height==41,"the changed reference eventually displays the new real pixels and dimensions, never its pre-edit warm instance");
      check(ReferenceEquals(recycled.Source,goodBitmap)&&recycled.PreviewState=="ready"&&ThumbnailImage.DecodedCache.PendingCount==0,"finishing every queued cold request leaves the recycled warm result intact and no pending decode entry");
     }finally{
      // Monitor leases must be released by their owning UI thread before
      // draining; a failure at any assertion must not strand the VM runner.
      for(int n=0;n<held.Length;n++)if(held[n]!=null){held[n].Dispose();held[n]=null;}
      if(warmHeld!=null)warmHeld.Dispose();if(pause!=null)pause.Dispose();host.Children.Clear();Pump();
      if(drain!=null)using(Drained(drain)){}
      using(Drained(ThumbnailImage.PauseAndDrainAsync())){}source.RootVisual=null;
     }
    }
   }
   observedImage=null;ThumbnailImage.CurrentLibrary=null;
  }
  static void Presentation(string root,Action<bool,string> check){
   ThumbnailImage.CurrentLibrary=null;Window owner=null;
   try{
    using(var stream=typeof(ThumbnailImage).Assembly.GetManifestResourceStream("Mori.Main.xaml"))owner=(Window)XamlReader.Load(stream);var template=(DataTemplate)owner.Resources["Tile"];
    foreach(string appearance in new[]{"dark","light"}){
     Theme.Set(appearance);var sheet=new StackPanel{Width=940,Background=Theme.Brush("Canvas")};sheet.Resources.MergedDictionaries.Add(owner.Resources);
     foreach(var size in new[]{new Size(100,66),new Size(139,110),new Size(140,109),new Size(140,110),new Size(160,112),new Size(210,160)}){
      var asset=new Asset{Name="预览状态测试.png",Ext=".png",ReferencePath=@"C:\NookLab\Audit\synthetic\image.png",Selected=true,Selecting=true};var tile=new GalleryTile{Asset=asset,Width=size.Width,ImageHeight=size.Height,ShowName=true,ShowDetails=false};var presenter=new ContentPresenter{ContentTemplate=template,Content=tile};presenter.Resources.MergedDictionaries.Add(owner.Resources);Layout(presenter,size.Width+12,size.Height+48);
      var image=Named<ThumbnailImage>(presenter,"TileImage");var status=Named<TextBlock>(presenter,"ThumbnailStatus");var overlay=(Grid)status.Parent;var imageArea=(Grid)image.Parent;var badge=Named<Border>(presenter,"MediaBadge");var source=Named<Border>(presenter,"SourceBadge");var select=Named<Border>(presenter,"SelectionMark");
      foreach(string state in new[]{"loading","unavailable","ready","idle"}){
       image.SetValue(ThumbnailImage.PreviewStateProperty,state);Layout(presenter,size.Width+12,size.Height+48);string label=appearance+" / "+size+" / "+state+": ";bool message=state=="loading"||state=="unavailable";
       check(overlay.Visibility==(tile.ShowPreviewStatus?Visibility.Visible:Visibility.Collapsed)&&status.Visibility==(message?Visibility.Visible:Visibility.Collapsed),label+"real template shows only relevant preview status, with compact cards suppressed");
       check(!overlay.IsHitTestVisible&&!status.IsHitTestVisible,label+"status overlay cannot capture drag, selection or double-click");
       if(message){check(status.Text==(state=="loading"?"正在生成预览…":"暂无预览 · F5 重试"),label+"status explains progress or explicit retry rather than a false successful preview");check(((SolidColorBrush)status.Foreground).Color==((SolidColorBrush)Theme.Brush("Muted")).Color,label+"status foreground follows the active theme");}
       if(message&&tile.ShowPreviewStatus){var bounds=Bounds(status,imageArea);check(Inside(bounds,imageArea),label+"status text remains inside the image rectangle");check(!bounds.IntersectsWith(Bounds(badge,imageArea))&&!bounds.IntersectsWith(Bounds(source,imageArea))&&!bounds.IntersectsWith(Bounds(select,imageArea)),label+"status has no overlap with format, source or selection markers");}
      }
      image.SetValue(ThumbnailImage.PreviewStateProperty,"unavailable");sheet.Children.Add(presenter);
     }
     Layout(sheet,940,1050);var bitmap=new RenderTargetBitmap(940,1050,96,96,PixelFormats.Pbgra32);bitmap.Render(sheet);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(root,"thumbnail-status-"+appearance+".png")))encoder.Save(file);
    }
    check(!owner.IsVisible,"template status checks render only synthetic VM content without showing a product window");
   }finally{if(owner!=null)owner.Close();}
  }
 }
}
