using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace Mori {
 public sealed partial class Library {
  static bool HasWarmThumbnailIndex(Asset asset){
   if(!MediaTypes.CanThumbnail(asset)||string.IsNullOrEmpty(asset.Perceptual))return false;
   if(asset.Width>0&&asset.Height>0)return true;
   // These renderers deliberately leave original pixel dimensions unknown:
   // PDF page units and generated document pages are not source-image pixels.
   // Their frozen derived PNG is still dimension-checked by the decoded cache.
   // Keep incomplete raster/video metadata on the normal build/read route.
   return asset.Width==0&&asset.Height==0&&(string.Equals(asset.Ext,".pdf",StringComparison.OrdinalIgnoreCase)||string.Equals(asset.Ext,".ai",StringComparison.OrdinalIgnoreCase)||DocumentContent.Supports(asset.Ext));
  }
  // Pure admission hint on the UI path. Syntax and indexed metadata only: no
  // source/disk stat, database lookup or full path validation may happen here.
  internal bool MayHaveWarmThumbnail(Asset asset){
   if(!HasWarmThumbnailIndex(asset)||(asset.IsReference&&string.IsNullOrEmpty(asset.ThumbnailSourceStamp)))return false;
   try{return ThumbnailImage.DecodedCache.HasWarmCandidate(ThumbnailPath(asset));}
   catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}catch(ArgumentException){return false;}catch(NotSupportedException){return false;}catch(System.Security.SecurityException){return false;}
  }
  // Read-only best effort: never build, repair, refresh metadata or wait for a
  // decoder. Any uncertainty is a miss; ReadThumbnail remains authoritative.
  internal BitmapImage TryReadWarmThumbnail(Asset asset){
   if(!HasWarmThumbnailIndex(asset))return null;
   try{
    string path=ThumbnailPath(asset);
    using(var lease=ThumbnailBuildGate.TryEnter(path)){
     if(lease==null)return null;
     ValidateThumbnailPath(asset,path);
     bool reference=asset.IsReference;string sourceStamp=null,indexedStamp=asset.ThumbnailSourceStamp;
     if(reference){
      long sourceBytes;sourceStamp=ReferenceThumbnailStamp(asset,out sourceBytes);
      if(string.IsNullOrEmpty(sourceStamp)||string.IsNullOrEmpty(indexedStamp)||!string.Equals(sourceStamp,indexedStamp,StringComparison.Ordinal))return null;
     }
     var image=ThumbnailImage.DecodedCache.TryReadWarm(path);
     if(image==null)return null;
     ValidateThumbnailPath(asset,path);
     if(asset.IsReference!=reference||!HasWarmThumbnailIndex(asset))return null;
     if(reference){
      long sourceBytes;string after=ReferenceThumbnailStamp(asset,out sourceBytes);
      if(!string.Equals(sourceStamp,after,StringComparison.Ordinal)||!string.Equals(indexedStamp,asset.ThumbnailSourceStamp,StringComparison.Ordinal))return null;
     }
     return image;
    }
   }catch(IOException){return null;}catch(UnauthorizedAccessException){return null;}catch(ArgumentException){return null;}catch(NotSupportedException){return null;}catch(System.Security.SecurityException){return null;}
  }
  // Only this asset-aware entry point may repair a derived file. The general
  // ReadThumbnail(string) helper must never overwrite an arbitrary user file.
  internal BitmapImage ReadThumbnail(Asset asset){
   if(!MediaTypes.CanThumbnail(asset))return null;
   string path=ThumbnailPath(asset);
   using(ThumbnailBuildGate.Enter(path)){
    ValidateThumbnailPath(asset,path);
    BuildThumbnailLocked(asset,path);
    if(!File.Exists(path))return null;
    string failedStamp=ThumbnailBuildGate.SourceStamp(path);
    try{return ThumbnailImage.DecodedCache.ReadOwned(path);}
    catch(Exception error){
     // Access/sharing errors are not evidence of corruption. Nor are output,
     // database, memory or source failures: only a predictable decode failure
     // of an owned derived PNG permits one atomic repair attempt.
     if(!(error is FormatException)&&!(error is NotSupportedException)&&!(error is InvalidDataException))throw;
     ValidateThumbnailWritePath(asset,path);
     if(failedStamp!=null&&ThumbnailBuildGate.SourceStamp(path)==failedStamp)BuildThumbnailLocked(asset,path,true,failedStamp);
     return ThumbnailImage.DecodedCache.ReadOwned(path);
    }
   }
  }
  void ValidateThumbnailPath(Asset asset,string path){
   string expected=Path.GetFullPath(ThumbnailPath(asset)),full=Path.GetFullPath(path);
   string folder=Path.GetFullPath(Path.Combine(Root,".mori","thumbs"));
   if(!string.Equals(expected,full,StringComparison.OrdinalIgnoreCase)||!string.Equals(Path.GetDirectoryName(full),folder,StringComparison.OrdinalIgnoreCase)||string.Equals(FullPath(asset),full,StringComparison.OrdinalIgnoreCase)||Directory.Exists(full))throw new IOException("不能安全读取或重建此缩略图");
  }
  void ValidateThumbnailWritePath(Asset asset,string path){
   ValidateThumbnailPath(asset,path);
   string full=Path.GetFullPath(path);
   // A healthy cache may be read through a legitimate linked library location.
   // Every persistent build/refresh/repair still refuses redirected ancestors;
   // read compatibility is not permission to create or replace files there.
   for(string current=full;!string.IsNullOrEmpty(current);current=Path.GetDirectoryName(current)){
    FileAttributes attributes;
    try{attributes=File.GetAttributes(current);}catch(FileNotFoundException){continue;}catch(DirectoryNotFoundException){continue;}
    if((attributes&FileAttributes.ReparsePoint)!=0||(current==full&&(attributes&FileAttributes.Directory)!=0))throw new IOException("缩略图位置包含链接或不是普通文件，未进行替换");
   }
  }
 }
}
