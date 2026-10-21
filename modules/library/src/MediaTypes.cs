using System;
using System.Collections.Generic;

namespace Mori {
 internal static class MediaTypes {
  internal static readonly HashSet<string> Pictures=Set(MoriDesk.FileFormats.Pictures);
  // Classification is broader than built-in preview support. A converted QOI,
  // JPEG XL or vector file remains a picture, with a file icon/default viewer
  // when this build cannot safely render it. Ordinary import is unchanged.
  static readonly HashSet<string> OtherPictures=Set(MoriDesk.FileFormats.OtherPictures);
  internal static bool IsPictureFormat(string extension){return Pictures.Contains(extension)||OtherPictures.Contains(extension);}
  internal static readonly HashSet<string> Videos=Set(MoriDesk.FileFormats.Videos);
  internal static readonly HashSet<string> Audio=Set(MoriDesk.FileFormats.Audio);
  internal static readonly HashSet<string> TextDocuments=Set(MoriDesk.FileFormats.ContentDocuments);
  internal static readonly HashSet<string> Documents=Set(MoriDesk.FileFormats.Documents);
  internal static bool CanThumbnail(Asset a){return a!=null&&!a.IsDirectory&&!a.IsBookmark&&(Pictures.Contains(a.Ext)||Videos.Contains(a.Ext)||Documents.Contains(a.Ext));}
  static HashSet<string> Set(string extensions){return new HashSet<string>(extensions.Split(' '),StringComparer.OrdinalIgnoreCase);}
  internal static HashSet<string> Supported(){var result=new HashSet<string>(Pictures,StringComparer.OrdinalIgnoreCase);result.UnionWith(Videos);result.UnionWith(Audio);result.UnionWith(Documents);result.Add(".url");return result;}
  internal static bool Contains(Asset a,string section){
   if(a==null||a.IsBookmark)return false;
   if(a.IsReference){if(section=="shortcuts")return true;if(a.IsDirectory)return false;if(section=="media")return true;if(section=="assets")return a.ReferenceSection!="documents"&&a.ReferenceSection!="websites";return a.ReferenceSection==section;}
   switch(section){
    case "pictures":return IsPictureFormat(a.Ext);
    case "videos":return Videos.Contains(a.Ext);
    case "audio":return Audio.Contains(a.Ext);
    // Managed conversion results outside the media families remain findable
    // under Documents; this does not pretend we can render every file type.
    case "documents":return Documents.Contains(a.Ext)||(!IsPictureFormat(a.Ext)&&!Videos.Contains(a.Ext)&&!Audio.Contains(a.Ext));
    case "assets":return IsPictureFormat(a.Ext)||Videos.Contains(a.Ext)||Audio.Contains(a.Ext);
    // Project folders and the local recycle bin retain documents as well.
    case "media":return Contains(a,"assets")||Contains(a,"documents");
    default:return false;
   }
  }
 }
}
