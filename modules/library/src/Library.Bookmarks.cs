using System;
using System.IO;
using System.Text;

namespace Mori {
 public sealed partial class Library {
  public static string BookmarkAddress(string value){
   if(value==null||value.Length>8000)throw new ArgumentException("网址过长或为空。");
   return SourceLink.Parse(value).AbsoluteUri;
  }
  public static string ReadBookmark(string path){
   if(new FileInfo(path).Length>65536)throw new InvalidDataException("网址文件过大。");
   bool section=false;string address=null;
   foreach(string raw in File.ReadAllLines(path)){
    string line=raw.Trim();if(line.StartsWith("[")){section=string.Equals(line,"[InternetShortcut]",StringComparison.OrdinalIgnoreCase);continue;}
    if(section&&line.StartsWith("URL=",StringComparison.OrdinalIgnoreCase)){
     if(address!=null)throw new InvalidDataException("网址文件包含多个地址。");address=line.Substring(4);
    }
   }
   return BookmarkAddress(address);
  }
  public ImportResult ImportBookmark(string address,string name,string folder,string tags="",string notes="",string duplicateAction="ask"){
   string url=BookmarkAddress(address);string title=string.IsNullOrWhiteSpace(name)?new Uri(url).Host:name.Trim();if(title.Length>250)title=title.Substring(0,250);
   string temp=Path.Combine(Path.GetTempPath(),"Mori-bookmark-"+Guid.NewGuid().ToString("N")+".url");
   try{
    // Only URL is stored: never copy executable shortcuts, remote icons or INI extras.
    File.WriteAllText(temp,"[InternetShortcut]\r\nURL="+url+"\r\n",new UTF8Encoding(false));
    return ImportCore(temp,BookmarkFolder(folder),title,url,tags,notes,"",duplicateAction,false);
   }finally{if(File.Exists(temp))File.Delete(temp);}
  }
 }
}
