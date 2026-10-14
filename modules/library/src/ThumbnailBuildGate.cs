using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Mori {
 // Thumbnail requests arrive from the gallery, inspector and local shortcut
 // views. Share a gate by output path, not by Library object or asset instance.
 internal static class ThumbnailBuildGate {
  sealed class Entry {internal readonly object Sync=new object();internal int Users;}
  sealed class Lease : IDisposable {
   readonly string path;readonly Entry entry;bool disposed;
   internal Lease(string path,Entry entry){this.path=path;this.entry=entry;}
   public void Dispose(){if(disposed)return;disposed=true;Monitor.Exit(entry.Sync);Release(path,entry);}
  }
  static readonly object sync=new object();
  static readonly Dictionary<string,Entry> entries=new Dictionary<string,Entry>(StringComparer.OrdinalIgnoreCase);
  static readonly Dictionary<string,DateTime> failures=new Dictionary<string,DateTime>(StringComparer.OrdinalIgnoreCase);
  internal const int MaximumFailures=128;
  internal static readonly TimeSpan FailureLifetime=TimeSpan.FromSeconds(30);
  internal static IDisposable Enter(string thumbnailPath){
   string path=Path.GetFullPath(thumbnailPath);Entry entry;
   lock(sync){if(!entries.TryGetValue(path,out entry)){entry=new Entry();entries.Add(path,entry);}entry.Users++;}
   try{Monitor.Enter(entry.Sync);return new Lease(path,entry);}catch{Release(path,entry);throw;}
  }
  static void Release(string path,Entry entry){lock(sync){entry.Users--;if(entry.Users==0)entries.Remove(path);}}
  internal static int ActivePathCount {get{lock(sync)return entries.Count;}}
  internal static int FailureCount {get{lock(sync)return failures.Count;}}
  // A changed length or timestamp immediately permits another attempt. Missing
  // or inaccessible files have no version and must not poison a future read.
  internal static string SourceStamp(string sourcePath){
   try{var file=new FileInfo(sourcePath);if(!file.Exists)return null;return file.FullName.ToUpperInvariant()+"|"+file.Length.ToString(CultureInfo.InvariantCulture)+"|"+file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);}
   catch(IOException){return null;}catch(UnauthorizedAccessException){return null;}catch(ArgumentException){return null;}catch(NotSupportedException){return null;}
  }
  internal static bool HasDecodeFailure(string stamp){return HasDecodeFailure(stamp,DateTime.UtcNow);}
  internal static bool HasDecodeFailure(string stamp,DateTime now){
   if(stamp==null)return false;
   lock(sync){DateTime expires;if(!failures.TryGetValue(stamp,out expires))return false;if(now<expires)return true;failures.Remove(stamp);return false;}
  }
  internal static void RememberDecodeFailure(string stamp){RememberDecodeFailure(stamp,DateTime.UtcNow);}
  internal static void RememberDecodeFailure(string stamp,DateTime now){
   if(stamp==null)return;
   lock(sync){
    var expired=new List<string>();foreach(var item in failures)if(item.Value<=now)expired.Add(item.Key);foreach(string key in expired)failures.Remove(key);
    if(!failures.ContainsKey(stamp)&&failures.Count>=MaximumFailures){string oldest=null;DateTime earliest=DateTime.MaxValue;foreach(var item in failures)if(oldest==null||item.Value<earliest){oldest=item.Key;earliest=item.Value;}if(oldest!=null)failures.Remove(oldest);}
    failures[stamp]=now+FailureLifetime;
   }
  }
 }
}
