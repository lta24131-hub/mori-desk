using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace Mori {
 // A pause first blocks admission, then owns all slots after existing jobs drain.
 // Nested pause leases share that barrier; only the final dispose resumes work.
 internal sealed class ThumbnailLoadGate {
  readonly SemaphoreSlim slots;readonly int capacity;readonly object state=new object();int pauses;Task drained;
  public ThumbnailLoadGate(int capacity){if(capacity<1)throw new ArgumentOutOfRangeException("capacity");this.capacity=capacity;slots=new SemaphoreSlim(capacity,capacity);}
  public bool IsPaused {get{return Volatile.Read(ref pauses)>0;}}
  public async Task<IDisposable> EnterAsync(){if(IsPaused)return null;await slots.WaitAsync().ConfigureAwait(false);if(IsPaused){slots.Release();return null;}return new Lease(delegate{slots.Release();});}
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
  readonly ThumbnailLoadGate shared,videos=new ThumbnailLoadGate(1);
  internal ThumbnailLoadScheduler(int capacity){shared=new ThumbnailLoadGate(capacity);}
  internal bool IsPaused {get{return shared.IsPaused||videos.IsPaused;}}
  internal async Task<IDisposable> EnterAsync(bool video){
   IDisposable serial=null,common=null;
   try{
    if(video){serial=await videos.EnterAsync().ConfigureAwait(false);if(serial==null)return null;}
    common=await shared.EnterAsync().ConfigureAwait(false);if(common==null){if(serial!=null)serial.Dispose();return null;}
    return new Lease(common,serial);
   }catch{if(common!=null)common.Dispose();if(serial!=null)serial.Dispose();throw;}
  }
  internal async Task<IDisposable> PauseAndDrainAsync(){
   // Start both barriers before awaiting either: a video may be waiting on a
   // shared slot and must be released by that gate's paused-admission check.
   var videoPause=videos.PauseAndDrainAsync();var sharedPause=shared.PauseAndDrainAsync();
   try{await Task.WhenAll(videoPause,sharedPause).ConfigureAwait(false);return new Lease(sharedPause.Result,videoPause.Result);}
   catch{if(sharedPause.Status==TaskStatus.RanToCompletion)sharedPause.Result.Dispose();if(videoPause.Status==TaskStatus.RanToCompletion)videoPause.Result.Dispose();throw;}
  }
  sealed class Lease:IDisposable {
   IDisposable shared,video;internal Lease(IDisposable shared,IDisposable video){this.shared=shared;this.video=video;}
   public void Dispose(){var common=Interlocked.Exchange(ref shared,null);if(common!=null)common.Dispose();var serial=Interlocked.Exchange(ref video,null);if(serial!=null)serial.Dispose();}
  }
 }
 public class ThumbnailImage : Image {
  public static readonly RoutedEvent DimensionsReadyEvent=EventManager.RegisterRoutedEvent("DimensionsReady",RoutingStrategy.Bubble,typeof(RoutedEventHandler),typeof(ThumbnailImage));
  public static Library CurrentLibrary;
  static readonly ThumbnailLoadScheduler Gate=new ThumbnailLoadScheduler(3);
  // Call/await/dispose from the UI thread. Rebuild gallery rows after disposal
  // so images skipped during the pause receive Loaded/DataContextChanged again.
  public static Task<IDisposable> PauseAndDrainAsync(){return Gate.PauseAndDrainAsync();}
  internal static void RetryFailedThumbnails(){ThumbnailBuildGate.ClearDecodeFailures();VideoThumbnail.ClearFailures();}
  int generation;
  public ThumbnailImage(){Loaded+=delegate{Load();};Unloaded+=delegate{generation++;Source=null;};DataContextChanged+=delegate{generation++;Source=null;if(IsLoaded)Load();};}
  async void Load(){var a=DataContext as Asset;if(a==null||CurrentLibrary==null||Gate.IsPaused||!MediaTypes.CanThumbnail(a))return;int version=++generation;var library=CurrentLibrary;IDisposable lease=null;
   try{lease=await Gate.EnterAsync(MainWindow.IsVideo(a));if(lease==null||Gate.IsPaused||version!=generation||!IsLoaded)return;int previousWidth=a.Width,previousHeight=a.Height;
    var bitmap=await Task.Run(delegate{library.BuildThumbnail(a);string file=library.ThumbnailPath(a);if(!File.Exists(file))return null;return ReadThumbnail(file);});
    if(Gate.IsPaused||version!=generation||!IsLoaded)return;Source=bitmap;if((previousWidth!=a.Width||previousHeight!=a.Height)&&a.Width>0&&a.Height>0){a.NotifyDimensions();RaiseEvent(new RoutedEventArgs(DimensionsReadyEvent));}}
   catch(Exception ex){System.Diagnostics.Debug.WriteLine("Mori thumbnail: "+ex.Message);}finally{if(lease!=null)lease.Dispose();}
  }
  internal static BitmapImage ReadThumbnail(string file){using(var stream=File.OpenRead(file)){var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();return bitmap;}}
 }
}
