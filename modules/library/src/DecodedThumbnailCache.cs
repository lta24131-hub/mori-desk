using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Mori {
 // This cache owns only detached, frozen, generated <=512 px thumbnails. A
 // full-size preview and its source stream must never become a cache entry.
 internal sealed class DecodedThumbnailCache {
  internal const long DefaultBudget=32L*1024*1024;
  internal const int DefaultMaximum=128,MaximumDimension=512;
  static readonly TimeSpan FailureLifetime=TimeSpan.FromSeconds(30);
  struct Stamp : IEquatable<Stamp> {
   internal long Length,Ticks;
   public bool Equals(Stamp other){return Length==other.Length&&Ticks==other.Ticks;}
  }
  struct ReadKey : IEquatable<ReadKey> {
   internal readonly string Path;internal readonly bool Owned;
   internal ReadKey(string path,bool owned){Path=path;Owned=owned;}
   public bool Equals(ReadKey other){return Owned==other.Owned&&StringComparer.OrdinalIgnoreCase.Equals(Path,other.Path);}
   public override bool Equals(object other){return other is ReadKey&&Equals((ReadKey)other);}
   public override int GetHashCode(){return StringComparer.OrdinalIgnoreCase.GetHashCode(Path)^(Owned?1:0);}
  }
  sealed class Cached {internal Stamp Stamp;internal BitmapImage Image;internal long Bytes;internal LinkedListNode<string> Node;}
  sealed class Pending {
   internal Stamp Stamp;internal long Epoch;
   internal readonly TaskCompletionSource<BitmapImage> Completion=new TaskCompletionSource<BitmapImage>(TaskCreationOptions.RunContinuationsAsynchronously);
  }
  sealed class Failure {internal Stamp Stamp;internal DateTime Expires;}
  readonly object sync=new object();
  readonly Dictionary<string,Cached> images=new Dictionary<string,Cached>(StringComparer.OrdinalIgnoreCase);
  readonly Dictionary<ReadKey,Pending> pending=new Dictionary<ReadKey,Pending>();
  readonly Dictionary<ReadKey,Failure> failures=new Dictionary<ReadKey,Failure>();
  readonly LinkedList<string> lru=new LinkedList<string>();
  readonly long budget;readonly int maximum;readonly Func<string,BitmapImage> decode;
  long bytes,epoch,decodes,hits,joins;
  internal DecodedThumbnailCache():this(DefaultBudget,DefaultMaximum,null){}
  internal DecodedThumbnailCache(long budget,int maximum,Func<string,BitmapImage> decode=null){
   if(budget<1)throw new ArgumentOutOfRangeException("budget");if(maximum<1)throw new ArgumentOutOfRangeException("maximum");this.budget=budget;this.maximum=maximum;this.decode=decode??DecodeFile;
  }
  internal int Count {get{lock(sync)return images.Count;}}
  internal long Bytes {get{lock(sync)return bytes;}}
  internal int PendingCount {get{lock(sync)return pending.Count;}}
  internal int FailureCount {get{lock(sync)return failures.Count;}}
  internal long DecodeCount {get{return Interlocked.Read(ref decodes);}}
  internal long HitCount {get{return Interlocked.Read(ref hits);}}
  internal long JoinCount {get{return Interlocked.Read(ref joins);}}
  // A detached old caller may finish after Clear, but may neither refill this
  // generation nor remove the new generation's in-flight request for its path.
  internal void Clear(){lock(sync){epoch++;images.Clear();lru.Clear();failures.Clear();pending.Clear();bytes=0;}}
  // Detach just this path's in-flight work. Owns also compares the Pending
  // object, so an old decode cannot refill or remove its replacement request.
  internal void Invalidate(string file){string path=Path.GetFullPath(file);lock(sync){Remove(path);for(int mode=0;mode<2;mode++){var key=new ReadKey(path,mode==1);failures.Remove(key);pending.Remove(key);}}}
  static bool ReadStamp(string path,out Stamp stamp){
   stamp=new Stamp();try{var file=new FileInfo(path);if(!file.Exists)return false;stamp.Length=file.Length;stamp.Ticks=file.LastWriteTimeUtc.Ticks;return true;}
   catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}
  }
  void Remove(string path){Cached cached;if(!images.TryGetValue(path,out cached))return;images.Remove(path);lru.Remove(cached.Node);bytes-=cached.Bytes;}
  bool Owns(ReadKey key,Pending work){Pending current;return work.Epoch==epoch&&pending.TryGetValue(key,out current)&&object.ReferenceEquals(current,work);}
  void RememberFailure(ReadKey key,Stamp stamp){
   var now=DateTime.UtcNow;var expired=new List<ReadKey>();foreach(var entry in failures)if(entry.Value.Expires<=now)expired.Add(entry.Key);foreach(var expiredKey in expired)failures.Remove(expiredKey);
   if(!failures.ContainsKey(key)&&failures.Count>=DefaultMaximum){ReadKey oldest=new ReadKey();bool found=false;DateTime expires=DateTime.MaxValue;foreach(var entry in failures)if(!found||entry.Value.Expires<expires){oldest=entry.Key;expires=entry.Value.Expires;found=true;}if(found)failures.Remove(oldest);}
   failures[key]=new Failure{Stamp=stamp,Expires=now+FailureLifetime};
  }
  static bool PredictableDecodeFailure(Exception error){return error is FormatException||error is NotSupportedException||error is InvalidDataException;}
  BitmapImage Decode(string path){Interlocked.Increment(ref decodes);return decode(path);}
  internal BitmapImage Read(string file){return ReadCore(file,false);}
  internal BitmapImage ReadOwned(string file){return ReadCore(file,true);}
  // Admission hint only: a known cold path must not queue behind slow warm
  // disk/source validation. No IO, waits, LRU/counter changes or cache refill.
  // A racing eviction may make this stale; TryReadWarm remains authoritative.
  internal bool HasWarmCandidate(string file){
   try{
    string path=Path.GetFullPath(file);
    if(!path.EndsWith(".512.png",StringComparison.OrdinalIgnoreCase)||!Monitor.TryEnter(sync))return false;
    try{
     Cached cached;if(!images.TryGetValue(path,out cached))return false;
     var image=cached.Image;
     return image!=null&&image.IsFrozen&&image.PixelWidth>0&&image.PixelHeight>0&&image.PixelWidth<=MaximumDimension&&image.PixelHeight<=MaximumDimension;
    }finally{Monitor.Exit(sync);}
   }catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}catch(ArgumentException){return false;}catch(NotSupportedException){return false;}catch(System.Security.SecurityException){return false;}
  }
  // Positive-only advisory read for the bounded background warm lane. Neither
  // a miss nor a hit changes LRU/counters, pending work or failure backoff.
  // Disk IO is outside sync, and both acquisitions are zero-wait: slow decodes
  // or a racing Clear/Invalidate must turn this into a normal-path miss.
  internal BitmapImage TryReadWarm(string file){
   try{
    string path=Path.GetFullPath(file);
    if(!path.EndsWith(".512.png",StringComparison.OrdinalIgnoreCase))return null;
    Cached snapshot;long generation;
    if(!Monitor.TryEnter(sync))return null;
    try{if(!images.TryGetValue(path,out snapshot))return null;generation=epoch;}
    finally{Monitor.Exit(sync);}
    Stamp current;if(!ReadStamp(path,out current)||!snapshot.Stamp.Equals(current))return null;
    if(!Monitor.TryEnter(sync))return null;
    try{
     Cached live;
     if(generation!=epoch||!images.TryGetValue(path,out live)||!object.ReferenceEquals(live,snapshot)||!live.Stamp.Equals(current))return null;
     var image=live.Image;
     return image!=null&&image.IsFrozen&&image.PixelWidth>0&&image.PixelHeight>0&&image.PixelWidth<=MaximumDimension&&image.PixelHeight<=MaximumDimension?image:null;
    }finally{Monitor.Exit(sync);}
   }catch(IOException){return null;}catch(UnauthorizedAccessException){return null;}catch(ArgumentException){return null;}catch(NotSupportedException){return null;}catch(System.Security.SecurityException){return null;}
  }
  BitmapImage ReadCore(string file,bool owned){
   string path=Path.GetFullPath(file);
   // ReadThumbnail is also used by a few one-off callers. Preserve their old
   // decoding behavior without retaining arbitrary files or original images.
   if(!owned&&!path.EndsWith(".512.png",StringComparison.OrdinalIgnoreCase))return Decode(path);
   var key=new ReadKey(path,owned);Stamp before;bool stamped=ReadStamp(path,out before);Pending work;bool owner=false;
   lock(sync){
    Cached cached;
    if(images.TryGetValue(path,out cached)){
     if(stamped&&cached.Stamp.Equals(before)){lru.Remove(cached.Node);lru.AddLast(cached.Node);Interlocked.Increment(ref hits);return cached.Image;}
     Remove(path);
    }
    Failure failure;
    if(failures.TryGetValue(key,out failure)){
     if(stamped&&failure.Stamp.Equals(before)&&DateTime.UtcNow<failure.Expires)throw new InvalidDataException("缩略图暂不可用，请按 F5 重试。");
     failures.Remove(key);
    }
    // An owned reader must never join a generic decode that has not performed
    // the PNG header check. Both modes may reuse already bounded frozen pixels.
    if(stamped&&pending.TryGetValue(key,out work)&&work.Epoch==epoch&&work.Stamp.Equals(before))Interlocked.Increment(ref joins);
    else{work=new Pending{Stamp=before,Epoch=epoch};pending[key]=work;owner=true;}
   }
   if(!owner)return work.Completion.Task.GetAwaiter().GetResult();
   try{
    var image=owned?DecodeOwned(path):Decode(path);Stamp after;bool unchanged=stamped&&ReadStamp(path,out after)&&before.Equals(after);
    lock(sync){
     if(Owns(key,work)){
      if(unchanged&&image!=null&&image.IsFrozen&&image.PixelWidth>0&&image.PixelHeight>0&&image.PixelWidth<=MaximumDimension&&image.PixelHeight<=MaximumDimension){
       Cached shared;
       if(images.TryGetValue(path,out shared)&&shared.Stamp.Equals(before))image=shared.Image;
       else{
        long cost=(long)image.PixelWidth*image.PixelHeight*Math.Max(4,(image.Format.BitsPerPixel+7)/8);
        if(cost<=budget){Remove(path);while(images.Count>0&&(images.Count>=maximum||bytes+cost>budget))Remove(lru.First.Value);var node=lru.AddLast(path);images[path]=new Cached{Stamp=before,Image=image,Bytes=cost,Node=node};bytes+=cost;}
       }
       failures.Remove(new ReadKey(path,false));failures.Remove(new ReadKey(path,true));
      }
      pending.Remove(key);
     }
    }
    work.Completion.TrySetResult(image);return image;
   }catch(Exception error){
    Stamp after;bool unchanged=stamped&&ReadStamp(path,out after)&&before.Equals(after);
    lock(sync){if(Owns(key,work)){Cached shared;bool healthy=images.TryGetValue(path,out shared)&&shared.Stamp.Equals(before);if(unchanged&&!healthy&&PredictableDecodeFailure(error))RememberFailure(key,before);pending.Remove(key);}}
    // The owner rethrows directly, while joined callers observe this same
    // failure. Observe the task too when there were no joined callers.
    work.Completion.TrySetException(error);var observed=work.Completion.Task.Exception;throw;
   }finally{lock(sync){if(Owns(key,work))pending.Remove(key);}}
  }
  BitmapImage DecodeOwned(string file){
   // The same handle covers both preflight and WIC's OnLoad decode. In
   // particular do not permit a writer or replacement between these reads.
   using(var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read)){
    var header=new byte[24];int count=0;while(count<header.Length){int read=stream.Read(header,count,header.Length-count);if(read==0)throw new InvalidDataException("派生缩略图的 PNG 文件头不完整");count+=read;}
    if(header[0]!=137||header[1]!=80||header[2]!=78||header[3]!=71||header[4]!=13||header[5]!=10||header[6]!=26||header[7]!=10||header[8]!=0||header[9]!=0||header[10]!=0||header[11]!=13||header[12]!=73||header[13]!=72||header[14]!=68||header[15]!=82)throw new InvalidDataException("派生缩略图不是标准 PNG");
    uint width=BigEndian(header,16),height=BigEndian(header,20);
    if(width==0||height==0||width>MaximumDimension||height>MaximumDimension)throw new InvalidDataException("派生缩略图尺寸必须在 1 到 512 像素之间");
    stream.Position=0;Interlocked.Increment(ref decodes);return DecodeStream(stream);
   }
  }
  static uint BigEndian(byte[] data,int offset){return ((uint)data[offset]<<24)|((uint)data[offset+1]<<16)|((uint)data[offset+2]<<8)|data[offset+3];}
  static BitmapImage DecodeStream(Stream stream){var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();return bitmap;}
  internal static BitmapImage DecodeFile(string file){using(var stream=File.OpenRead(file))return DecodeStream(stream);}
 }
}
