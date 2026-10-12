using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
namespace MoriDesk {
 public static class ImageDirectory {
  public const int Limit=10000;
  public static string[] Read(string current,CancellationToken cancel){return Select(current,Directory.EnumerateFiles(Path.GetDirectoryName(current)),cancel);}
  internal static string[] Select(string current,IEnumerable<string> paths,CancellationToken cancel){
   var found=new HashSet<string>(StringComparer.OrdinalIgnoreCase){current};int inspected=0;
   foreach(var path in paths){cancel.ThrowIfCancellationRequested();if(FileRouter.Pictures.Contains(Path.GetExtension(path).ToLowerInvariant()))found.Add(path);if(++inspected>=Limit)break;}
   cancel.ThrowIfCancellationRequested();return found.OrderBy(p=>p,StringComparer.CurrentCultureIgnoreCase).ToArray();
  }
 }
}
