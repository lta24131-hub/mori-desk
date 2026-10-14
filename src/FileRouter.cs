using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
namespace MoriDesk {
 public static class FileRouter {
  public static readonly string[] Models={".stp",".step",".stl",".obj",".glb",".3mf"};
  public static readonly string[] Pictures=Mori.MediaTypes.Pictures.OrderBy(x=>x).ToArray();
  public static string PictureFilter {get{return "图片与设计文件|"+string.Join(";",Pictures.Select(x=>"*"+x));}}
  public static string Quote(string path){return "\""+path.Replace("\"","")+"\"";}
  public static string Kind(string path){var ext=Path.GetExtension(path).ToLowerInvariant();return Models.Contains(ext)?"model":Pictures.Contains(ext)?"image":Mori.MediaTypes.Documents.Contains(ext)||Mori.MediaTypes.Audio.Contains(ext)||Mori.MediaTypes.Videos.Contains(ext)?"preview":"external";}
  public static void Choose(Window owner,bool model){var d=new Microsoft.Win32.OpenFileDialog{Title=model?"打开 3D 模型（不会收藏）":"打开图片（不会收藏）",Filter=model?"3D 模型|*.stp;*.step;*.stl;*.obj;*.glb;*.3mf":PictureFilter};if(d.ShowDialog(owner)==true)Open(owner,d.FileName,Path.GetFileName(d.FileName));}
  public static void Open(Window owner,string path,string name){if(!File.Exists(path)&&!Directory.Exists(path))throw new FileNotFoundException("原文件不存在，可能已移动或磁盘未连接。",path);string kind=Directory.Exists(path)?"external":Kind(path);if(kind!="external"){Process.Start(new ProcessStartInfo(typeof(Entry).Assembly.Location,"--"+kind+" "+Quote(path)){UseShellExecute=false});return;}OpenDefault(owner,path);}
  public static void OpenDefault(Window owner,string path){if(!File.Exists(path)&&!Directory.Exists(path))throw new FileNotFoundException("原文件不存在，可能已移动或磁盘未连接。",path);string ext=Path.GetExtension(path).ToLowerInvariant();if(new[]{".exe",".bat",".cmd",".ps1",".vbs",".js",".lnk",".msi",".url"}.Contains(ext)&&MessageBox.Show(owner,"此文件可能运行程序或打开外部链接：\n"+path+"\n\n确认打开？","打开外部文件",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;Process.Start(new ProcessStartInfo(path){UseShellExecute=true});}
  public static void Reveal(string path){if(Directory.Exists(path))Process.Start("explorer.exe",Quote(path));else if(File.Exists(path))Process.Start("explorer.exe","/select,"+Quote(path));else throw new FileNotFoundException("原文件已移动、重命名、删除，或磁盘暂时不可用。请重新定位收藏后再打开位置。",path);}
 }
}
