using System;
using System.IO;
using MoriDesk;

namespace Mori {
 public sealed partial class Library {
  // Only validated converter output may enter this route. Ordinary import keeps
  // its existing allowlist. All converted formats use managed independent copies,
  // including files for which Nook can only offer the default external viewer.
  internal ImportResult ImportConvertedFile(string source,string name,string url,string tags,string notes,string created,string duplicateAction){
   string format=ConversionFormats.Detect(source);
   if(!ConversionFormats.IsTarget(format)||!ConversionFormats.MatchesExtension(source,format))
    throw new IOException("转换结果不是可收藏的文件格式。");
   FileConversionClient.ValidateLocalPath(source,true);
   return ImportCore(source,"",string.IsNullOrWhiteSpace(name)?ConversionFormats.NameWithoutExtension(source):name,url,tags,notes,created,duplicateAction,false,true,"."+ConversionFormats.Extension(format));
  }
 }
}
