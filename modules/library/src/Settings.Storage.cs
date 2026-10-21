using System;
using System.IO;
using System.Linq;

namespace Mori {
 public partial class Settings {
  string fileConversionOutputPath;
  internal static string DefaultConversionOutputPath {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"Nook 转换");}}
  public string FileConversionOutputPath {
   get{return string.IsNullOrWhiteSpace(fileConversionOutputPath)?DefaultConversionOutputPath:fileConversionOutputPath;}
   set{fileConversionOutputPath=value;}
  }
  // Validation only: accepting a preference must not create a directory, move
  // existing output, touch the library, or change component/application storage.
  internal static string ValidateConversionOutputPath(string value){
   string path=(value??"").Trim();
   if(path.Length<4||path.Length>4096||!char.IsLetter(path[0])||path[1]!=':'||(path[2]!='\\'&&path[2]!='/')
      ||path.IndexOf(':',2)>=0||path.IndexOfAny(new[]{'"','\0','*','?','<','>','|'})>=0||path.Any(char.IsControl))
    throw new IOException("请选择普通本地磁盘上的文件夹，不接受网址、网络共享或设备路径。");
   string full=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
   if(string.Equals(full,Path.GetPathRoot(full).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar),StringComparison.OrdinalIgnoreCase))
    throw new IOException("请使用磁盘中的子文件夹，不能直接使用磁盘根目录。");
   foreach(string part in full.Substring(3).Split(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)){
    string name=part.Split('.')[0].ToUpperInvariant();
    if(part.EndsWith(" ",StringComparison.Ordinal)||part.EndsWith(".",StringComparison.Ordinal)
       ||new[]{"CON","PRN","AUX","NUL","CLOCK$","CONIN$","CONOUT$"}.Contains(name)
       ||name.Length==4&&(name.StartsWith("COM",StringComparison.Ordinal)||name.StartsWith("LPT",StringComparison.Ordinal))&&name[3]>='1'&&name[3]<='9')
     throw new IOException("保存位置包含 Windows 不支持的文件夹名称。");
   }
   for(string current=full;!string.IsNullOrEmpty(current);current=Path.GetDirectoryName(current)){
    try{
     var attributes=File.GetAttributes(current);
     if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("保存位置不能包含符号链接或重解析目录。");
     if((attributes&FileAttributes.Directory)==0)throw new IOException("保存位置或它的上级路径不是文件夹。");
    }catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
   }
   return full;
  }
 }
}
