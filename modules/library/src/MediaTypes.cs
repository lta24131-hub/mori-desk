using System;
using System.Collections.Generic;

namespace Mori {
 internal static class MediaTypes {
  internal static readonly HashSet<string> Pictures=Set(".jpg .jpeg .png .gif .bmp .tif .tiff .webp .avif .ico .svg");
  internal static readonly HashSet<string> Videos=Set(".mp4 .mov .m4v .webm .avi");
  internal static readonly HashSet<string> Audio=Set(".mp3 .wav .m4a .aac .flac .ogg .opus .wma .aiff .aif");
  internal static readonly HashSet<string> Documents=Set(".pdf");
  static HashSet<string> Set(string extensions){return new HashSet<string>(extensions.Split(' '),StringComparer.OrdinalIgnoreCase);}
  internal static HashSet<string> Supported(){var result=new HashSet<string>(Pictures,StringComparer.OrdinalIgnoreCase);result.UnionWith(Videos);result.UnionWith(Audio);result.UnionWith(Documents);result.Add(".url");return result;}
  internal static bool Contains(Asset a,string section){
   if(a==null||a.IsBookmark)return false;
   if(a.IsReference){if(section=="shortcuts")return true;if(a.IsDirectory)return false;if(section=="media")return true;if(section=="assets")return a.ReferenceSection!="documents"&&a.ReferenceSection!="websites";return a.ReferenceSection==section;}
   switch(section){
    case "pictures":return Pictures.Contains(a.Ext);
    case "videos":return Videos.Contains(a.Ext);
    case "audio":return Audio.Contains(a.Ext);
    case "documents":return Documents.Contains(a.Ext);
    case "assets":return Pictures.Contains(a.Ext)||Videos.Contains(a.Ext)||Audio.Contains(a.Ext);
    // Project folders and the local recycle bin retain documents as well.
    case "media":return Contains(a,"assets")||Documents.Contains(a.Ext);
    default:return false;
   }
  }
 }
}
