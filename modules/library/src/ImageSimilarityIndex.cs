using System;
using System.Collections.Generic;
using System.Threading;

namespace Mori {
 internal static class ImageSimilarityIndex {
  internal static bool CanCompare(Asset asset){
   // Classification also contains Illustrator documents and conversion-only
   // image formats. Compare only the image previews this build can decode;
   // never treat a video poster or a document page as a comparable image.
   return asset!=null&&!asset.Deleted&&!asset.IsDirectory&&!asset.IsBookmark&&
    MediaTypes.Pictures.Contains(asset.Ext)&&!string.Equals(asset.Ext,".ai",StringComparison.OrdinalIgnoreCase);
  }
  internal static bool HasIndex(Asset asset,bool color){return CanCompare(asset)&&!string.IsNullOrEmpty(color?asset.Color:asset.Perceptual);}
  internal static int Build(IEnumerable<Asset> snapshot,Action<Asset> build,CancellationToken token){
   int count=0;
   foreach(var asset in snapshot){
    token.ThrowIfCancellationRequested();if(!CanCompare(asset))continue;
    build(asset);count++;
    // A decoder/database update finishes as one unit. Never begin another
    // image, or change the active search, after cancellation is requested.
    token.ThrowIfCancellationRequested();
   }
   token.ThrowIfCancellationRequested();return count;
  }
 }
}
