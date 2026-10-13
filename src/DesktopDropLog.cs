using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;

namespace MoriDesk {
 internal static class DesktopDropLog {
  // Local, bounded diagnostics only: no image bytes, URLs, filenames, source
  // app names, window titles, exception messages or account credentials.
  static readonly string[] Formats={"PNG","image/png","image/jpeg","JFIF","image/gif","image/webp",DataFormats.Bitmap,DataFormats.Html,DataFormats.FileDrop,"FileGroupDescriptorW","FileGroupDescriptor","FileContents","UniformResourceLocatorW","UniformResourceLocator"};
  internal static void Write(string root,string phase,IDataObject data=null,Exception error=null){
   try{
    string folder=Path.Combine(root,".mori"),path=Path.Combine(folder,"desktop-drop.log");Directory.CreateDirectory(folder);
    string offered=data==null?"":string.Join(",",Formats.Where(f=>{try{return data.GetDataPresent(f,false);}catch{return false;}}));
    string line=DateTime.UtcNow.ToString("o")+" "+phase+(offered.Length==0?"":" formats="+offered)+(error==null?"":" error="+error.GetType().Name+" hr="+error.HResult.ToString("X8"))+Environment.NewLine;
    if(File.Exists(path)&&new FileInfo(path).Length>65536)File.WriteAllText(path,"",new UTF8Encoding(false));
    File.AppendAllText(path,line,new UTF8Encoding(false));
   }catch(IOException){}catch(UnauthorizedAccessException){}
  }
 }
}
