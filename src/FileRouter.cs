using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
namespace MoriDesk {
 public static class FileRouter {
  public static readonly string[] Models=FileFormats.Models.Split(' ');
  public static readonly string[] Pictures=Mori.MediaTypes.Pictures.OrderBy(x=>x).ToArray();
  public static string PictureFilter {get{return "图片与设计文件|"+string.Join(";",Pictures.Select(x=>"*"+x));}}
  public static string Quote(string path){return "\""+path.Replace("\"","")+"\"";}
  public static string Kind(string path){var ext=Path.GetExtension(path).ToLowerInvariant();return Models.Contains(ext)?"model":Pictures.Contains(ext)?"image":Mori.MediaTypes.Documents.Contains(ext)||Mori.MediaTypes.Audio.Contains(ext)||Mori.MediaTypes.Videos.Contains(ext)?"preview":"external";}
  public static void Choose(Window owner,bool model){var d=new Microsoft.Win32.OpenFileDialog{Title=model?"打开 3D 模型（不会收藏）":"打开图片（不会收藏）",Filter=model?"3D 模型|"+string.Join(";",Models.Select(x=>"*"+x)):PictureFilter};if(d.ShowDialog(owner)==true)Open(owner,d.FileName,Path.GetFileName(d.FileName));}
  public static void Open(Window owner,string path,string name){if(!File.Exists(path)&&!Directory.Exists(path))throw new FileNotFoundException("原文件不存在，可能已移动或磁盘未连接。",path);string kind=Directory.Exists(path)?"external":Kind(path);if(kind!="external"){Process.Start(new ProcessStartInfo(typeof(Entry).Assembly.Location,"--"+kind+" "+Quote(path)){UseShellExecute=false});return;}OpenDefault(owner,path);}
  public static void OpenDefault(Window owner,string path){RequireOriginal(path);string ext=Path.GetExtension(path).ToLowerInvariant();if(new[]{".exe",".com",".bat",".cmd",".ps1",".vbs",".js",".lnk",".msi",".url"}.Contains(ext)&&MessageBox.Show(owner,"此文件可能运行程序或打开外部链接：\n"+path+"\n\n确认打开？","打开外部文件",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;OpenDefaultCore(path,AssociatedExecutable,p=>Process.Start(new ProcessStartInfo(p){UseShellExecute=true}),p=>ChooseOtherApplication(owner,p));}
  static void RequireOriginal(string path){if(!File.Exists(path)&&!Directory.Exists(path))throw new FileNotFoundException("原文件不存在，可能已移动或磁盘未连接。",path);}
  // Resolve the effective Windows association before asking the shell to open a
  // document. Nook can itself be the default: opening it again is not an escape
  // from an unsupported preview, and old/launcher associations have the same loop.
  internal static void OpenDefaultCore(string path,Func<string,string> association,Action<string> shellOpen,Action<string> chooseApplication){
   RequireOriginal(path);string ext=Path.GetExtension(path).ToLowerInvariant();
   bool direct=Directory.Exists(path)||new[]{".exe",".com",".bat",".cmd",".lnk",".url"}.Contains(ext);
   string executable=direct?null:association(ext);
   // Some valid UWP/DelegateExecute associations have no executable string.
   // Only a known self-association bypasses the shell; an unknown result still
   // gets normal shell handling and falls back only when that launch fails.
   if(!direct&&IsNookExecutable(executable)){chooseApplication(path);return;}
   try{shellOpen(path);}
   catch(Win32Exception ex){
    // An uninstalled default application can still have a registration. Offer
    // another app, but do not mask access-denied or an original that disappeared.
    if(direct||(ex.NativeErrorCode!=2&&ex.NativeErrorCode!=3&&ex.NativeErrorCode!=1155))throw;
    RequireOriginal(path);chooseApplication(path);
   }
  }
  internal static bool IsNookExecutable(string executable){
   if(string.IsNullOrWhiteSpace(executable))return false;
   try{
    string path=Path.GetFullPath(executable.Trim().Trim('"'));
    if(string.Equals(path,typeof(Entry).Assembly.Location,StringComparison.OrdinalIgnoreCase))return true;
    string name=Path.GetFileName(path);
    // These are the shipped application identities, including obsolete copies.
    // Do not require the target to still exist just to recognize a stale default.
    return string.Equals(name,"Mori.exe",StringComparison.OrdinalIgnoreCase)||string.Equals(name,"Nook.exe",StringComparison.OrdinalIgnoreCase);
   }catch(ArgumentException){return false;}catch(NotSupportedException){return false;}catch(PathTooLongException){return false;}
  }
  [DllImport("shlwapi.dll",CharSet=CharSet.Unicode)]static extern int AssocQueryString(uint flags,uint value,string association,string extra,StringBuilder result,ref uint length);
  static string AssociatedExecutable(string extension){
   if(string.IsNullOrEmpty(extension))return null;
   uint length=32768;var value=new StringBuilder((int)length);
   // ASSOCF_INIT_IGNOREUNKNOWN avoids the generic unknown-file fallback;
   // ASSOCSTR_EXECUTABLE honors the user's effective file association.
   return AssocQueryString(0x400,2,extension,null,value,ref length)==0?value.ToString():null;
  }
  [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct OpenAsInfo{[MarshalAs(UnmanagedType.LPWStr)]public string File;[MarshalAs(UnmanagedType.LPWStr)]public string Class;public uint Flags;}
  [DllImport("shell32.dll",CharSet=CharSet.Unicode)]static extern int SHOpenWithDialog(IntPtr owner,ref OpenAsInfo info);
  static void ChooseOtherApplication(Window owner,string path){
   // OAIF_EXEC only: launch the chosen app; never request default registration.
   var info=new OpenAsInfo{File=path,Class=null,Flags=4};
   int result=SHOpenWithDialog(owner==null?IntPtr.Zero:new WindowInteropHelper(owner).Handle,ref info);
   if(result<0&&result!=unchecked((int)0x800704C7)&&result!=unchecked((int)0x80004004))throw new InvalidOperationException("无法打开应用选择窗口。请在资源管理器中右键此文件，选择“打开方式”。",Marshal.GetExceptionForHR(result));
  }
  public static void Reveal(string path){if(Directory.Exists(path))Process.Start("explorer.exe",Quote(path));else if(File.Exists(path))Process.Start("explorer.exe","/select,"+Quote(path));else throw new FileNotFoundException("原文件已移动、重命名、删除，或磁盘暂时不可用。请重新定位收藏后再打开位置。",path);}
 }
}
