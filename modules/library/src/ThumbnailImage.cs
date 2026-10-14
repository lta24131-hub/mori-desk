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
 public class ThumbnailImage : Image {
  public static readonly RoutedEvent DimensionsReadyEvent=EventManager.RegisterRoutedEvent("DimensionsReady",RoutingStrategy.Bubble,typeof(RoutedEventHandler),typeof(ThumbnailImage));
  public static Library CurrentLibrary;
  static readonly ThumbnailLoadGate Gate=new ThumbnailLoadGate(3);
  // Call/await/dispose from the UI thread. Rebuild gallery rows after disposal
  // so images skipped during the pause receive Loaded/DataContextChanged again.
  public static Task<IDisposable> PauseAndDrainAsync(){return Gate.PauseAndDrainAsync();}
  int generation;
  public ThumbnailImage(){Loaded+=delegate{Load();};Unloaded+=delegate{generation++;Source=null;};DataContextChanged+=delegate{generation++;Source=null;if(IsLoaded)Load();};}
  async void Load(){var a=DataContext as Asset;if(a==null||CurrentLibrary==null||Gate.IsPaused||!MediaTypes.CanThumbnail(a))return;int version=++generation;var library=CurrentLibrary;var lease=await Gate.EnterAsync();if(lease==null)return;
   using(lease){try{if(Gate.IsPaused||version!=generation||!IsLoaded)return;int previousWidth=a.Width,previousHeight=a.Height;
    var bitmap=await Task.Run(delegate{library.BuildThumbnail(a);string file=library.ThumbnailPath(a);if(!File.Exists(file))return null;return ReadThumbnail(file);});
    if(Gate.IsPaused||version!=generation||!IsLoaded)return;Source=bitmap;if((previousWidth!=a.Width||previousHeight!=a.Height)&&a.Width>0&&a.Height>0){a.NotifyDimensions();RaiseEvent(new RoutedEventArgs(DimensionsReadyEvent));}}
   catch(Exception ex){System.Diagnostics.Debug.WriteLine("Mori thumbnail: "+ex.Message);}}
  }
  internal static BitmapImage ReadThumbnail(string file){using(var stream=File.OpenRead(file)){var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();return bitmap;}}
 }
}
