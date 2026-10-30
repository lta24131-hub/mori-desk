using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mori {
 // A pause first blocks admission, then owns all slots after existing jobs drain.
 // Nested pause leases share that barrier; only the final dispose resumes work.
 internal sealed class ThumbnailLoadGate {
  readonly SemaphoreSlim slots;readonly int capacity;readonly object state=new object();int pauses,waiting;Task drained;
  public ThumbnailLoadGate(int capacity){if(capacity<1)throw new ArgumentOutOfRangeException("capacity");this.capacity=capacity;slots=new SemaphoreSlim(capacity,capacity);}
  public bool IsPaused {get{return Volatile.Read(ref pauses)>0;}}
  internal int WaitingCount {get{return Volatile.Read(ref waiting);}}
  public async Task<IDisposable> EnterAsync(CancellationToken cancellation=default(CancellationToken)){if(IsPaused)return null;Interlocked.Increment(ref waiting);try{await slots.WaitAsync(cancellation).ConfigureAwait(false);}finally{Interlocked.Decrement(ref waiting);}if(IsPaused){slots.Release();return null;}return new Lease(delegate{slots.Release();});}
  async Task DrainAsync(){int held=0;try{for(int i=0;i<capacity;i++){await slots.WaitAsync().ConfigureAwait(false);held++;}}catch{if(held>0)slots.Release(held);throw;}}
  public async Task<IDisposable> PauseAndDrainAsync(){
   Task wait;lock(state){pauses++;if(pauses==1)drained=DrainAsync();wait=drained;}
   try{await wait.ConfigureAwait(false);}catch{lock(state){pauses--;if(pauses==0)drained=null;}throw;}
   return new Lease(delegate{lock(state){pauses--;if(pauses==0){drained=null;slots.Release(capacity);}}});
  }
  sealed class Lease:IDisposable {Action release;public Lease(Action release){this.release=release;}public void Dispose(){var action=Interlocked.Exchange(ref release,null);if(action!=null)action();}}
 }
 // Videos have a serial decoder. Waiting for it must happen before taking a
 // shared slot, otherwise three videos can prevent every image from loading.
 internal sealed class ThumbnailLoadScheduler {
  readonly ThumbnailLoadGate shared,videos=new ThumbnailLoadGate(1),warm=new ThumbnailLoadGate(1);
  internal ThumbnailLoadScheduler(int capacity){shared=new ThumbnailLoadGate(capacity);}
  internal bool IsPaused {get{return shared.IsPaused||videos.IsPaused||warm.IsPaused;}}
  // Positive-cache probes do no decoding or building. Keep their background
  // file-stat work bounded, but independent of occupied image/video decoders.
  internal Task<IDisposable> EnterWarmAsync(CancellationToken cancellation=default(CancellationToken)){return warm.EnterAsync(cancellation);}
  internal async Task<IDisposable> EnterAsync(bool video,CancellationToken cancellation=default(CancellationToken)){
   IDisposable serial=null,common=null;
   try{
    if(video){serial=await videos.EnterAsync(cancellation).ConfigureAwait(false);if(serial==null)return null;}
    common=await shared.EnterAsync(cancellation).ConfigureAwait(false);if(common==null){if(serial!=null)serial.Dispose();return null;}
    return new Lease(common,serial);
   }catch{if(common!=null)common.Dispose();if(serial!=null)serial.Dispose();throw;}
  }
  internal async Task<IDisposable> PauseAndDrainAsync(){
   // Close every admission before awaiting any barrier: a video may be waiting
   // on shared capacity, and a probe must finish before cache/library changes.
   var warmPause=warm.PauseAndDrainAsync();var videoPause=videos.PauseAndDrainAsync();var sharedPause=shared.PauseAndDrainAsync();
   try{await Task.WhenAll(warmPause,videoPause,sharedPause).ConfigureAwait(false);return new Lease(sharedPause.Result,videoPause.Result,warmPause.Result);}
   catch{if(sharedPause.Status==TaskStatus.RanToCompletion)sharedPause.Result.Dispose();if(videoPause.Status==TaskStatus.RanToCompletion)videoPause.Result.Dispose();if(warmPause.Status==TaskStatus.RanToCompletion)warmPause.Result.Dispose();throw;}
  }
  sealed class Lease:IDisposable {
   IDisposable shared,video,warm;internal Lease(IDisposable shared,IDisposable video,IDisposable warm=null){this.shared=shared;this.video=video;this.warm=warm;}
   public void Dispose(){var common=Interlocked.Exchange(ref shared,null);if(common!=null)common.Dispose();var serial=Interlocked.Exchange(ref video,null);if(serial!=null)serial.Dispose();var probe=Interlocked.Exchange(ref warm,null);if(probe!=null)probe.Dispose();}
  }
 }
 public class ThumbnailImage : Image {
  public static readonly DependencyProperty PreviewStateProperty=DependencyProperty.Register("PreviewState",typeof(string),typeof(ThumbnailImage),new PropertyMetadata("idle"));
  public string PreviewState {get{return (string)GetValue(PreviewStateProperty);}private set{SetValue(PreviewStateProperty,value);}}
  public static readonly RoutedEvent DimensionsReadyEvent=EventManager.RegisterRoutedEvent("DimensionsReady",RoutingStrategy.Bubble,typeof(RoutedEventHandler),typeof(ThumbnailImage));
  static Library currentLibrary;
  internal static readonly DecodedThumbnailCache DecodedCache=new DecodedThumbnailCache();
  public static Library CurrentLibrary {get{return Volatile.Read(ref currentLibrary);}set{if(!object.ReferenceEquals(Interlocked.Exchange(ref currentLibrary,value),value))DecodedCache.Clear();}}
  static readonly ThumbnailLoadScheduler Gate=new ThumbnailLoadScheduler(3);
  // Call/await/dispose from the UI thread. Rebuilt rows receive Loaded again;
  // retained inspector/collection previews need ResumeVisible after disposal.
  public static async Task<IDisposable> PauseAndDrainAsync(){var pause=await Gate.PauseAndDrainAsync().ConfigureAwait(false);DecodedCache.Clear();return pause;}
  internal static void ResumeVisible(DependencyObject root){
   if(root==null||Gate.IsPaused)return;root.Dispatcher.VerifyAccess();
   var element=root as UIElement;if(element!=null&&!element.IsVisible)return;
   var image=root as ThumbnailImage;
   if(image!=null&&image.IsLoaded&&image.Source==null&&image.PreviewState=="idle"&&image.admission==null)image.Load();
   for(int n=0;n<VisualTreeHelper.GetChildrenCount(root);n++)ResumeVisible(VisualTreeHelper.GetChild(root,n));
  }
  internal static void RetryFailedThumbnails(){DecodedCache.Clear();ThumbnailBuildGate.ClearDecodeFailures();VideoThumbnail.ClearFailures();SvgRaster.ClearFailures();}
  int generation;CancellationTokenSource admission;
  void CancelLoad(){generation++;var previous=admission;admission=null;if(previous!=null)previous.Cancel();}
  void ResetLoad(){CancelLoad();Source=null;PreviewState="idle";}
  public ThumbnailImage(){
   Loaded+=delegate{if(admission==null)Load();};Unloaded+=delegate{ResetLoad();};
   DataContextChanged+=delegate{ResetLoad();Load();};
   // Switching to Projects/Browser collapses the gallery without unloading its
   // rows. Cancel queued admissions then; an active decoder finishes safely,
   // but its old generation cannot repopulate hidden or rebound controls.
   IsVisibleChanged+=delegate{if(!IsVisible)ResetLoad();else if(admission==null)Load();};
  }
  bool IsCurrent(int version,Library library){return !Gate.IsPaused&&version==generation&&IsLoaded&&IsVisible&&object.ReferenceEquals(library,CurrentLibrary);}
  async void Load(){if(!IsLoaded||!IsVisible)return;CancelLoad();var a=DataContext as Asset;var library=CurrentLibrary;if(a==null||library==null||Gate.IsPaused||!MediaTypes.CanThumbnail(a))return;int version=generation;IDisposable lease=null;var cancellation=new CancellationTokenSource();admission=cancellation;
   PreviewState="loading";
   try{
    int previousWidth=a.Width,previousHeight=a.Height;
    BitmapImage bitmap=null;
    // This first check is memory-only. A slow drive's cached image may hold
    // the stat lane, but must not delay unrelated cold images or their builds.
    if(library.MayHaveWarmThumbnail(a)){
     lease=await Gate.EnterWarmAsync(cancellation.Token);if(lease==null||!IsCurrent(version,library))return;
     bitmap=await Task.Run(delegate{return library.TryReadWarmThumbnail(a);});
     if(!IsCurrent(version,library))return;
    }
    if(bitmap==null){
     // A miss, path contention or changed reference returns to the original
     // guarded route. Never build, decode, join or repair in the probe lane.
     if(lease!=null){lease.Dispose();lease=null;}
     lease=await Gate.EnterAsync(MainWindow.IsVideo(a),cancellation.Token);if(lease==null||!IsCurrent(version,library))return;
     bitmap=await Task.Run(delegate{return library.ReadThumbnail(a);});
     if(!IsCurrent(version,library))return;
    }
    Source=bitmap;PreviewState=bitmap==null?"unavailable":"ready";if((previousWidth!=a.Width||previousHeight!=a.Height)&&a.Width>0&&a.Height>0){a.NotifyDimensions();RaiseEvent(new RoutedEventArgs(DimensionsReadyEvent));}}
   catch(OperationCanceledException){}
   catch(Exception ex){if(IsCurrent(version,library))PreviewState="unavailable";System.Diagnostics.Debug.WriteLine("Mori thumbnail: "+ex.Message);}finally{if(lease!=null)lease.Dispose();if(object.ReferenceEquals(admission,cancellation))admission=null;cancellation.Dispose();if(version==generation&&PreviewState=="loading")PreviewState="idle";}
  }
  internal static BitmapImage ReadThumbnail(string file){return DecodedCache.Read(file);}
 }
}
