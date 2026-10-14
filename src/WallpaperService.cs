using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Mori;

namespace MoriDesk {
 // Keep an independent, Windows-compatible copy: moving a shortcut's source or
 // switching libraries must not remove the image used by the desktop.
 internal sealed class WallpaperService {
  internal const uint SetDesktopWallpaper=0x0014,UpdateProfileAndNotify=0x0003;
  const long MaximumInput=256L*1024*1024;
  const string Prefix="nook-wallpaper-";
  readonly string storage;
  readonly Action<string> apply;
  [DllImport("user32.dll",EntryPoint="SystemParametersInfoW",CharSet=CharSet.Unicode,SetLastError=true)]
  [return:MarshalAs(UnmanagedType.Bool)]
  static extern bool SystemParametersInfo(uint action,uint parameter,string value,uint flags);

  internal WallpaperService(string storage,Action<string> apply){
   if(!AbsolutePath(storage)||storage.IndexOf("://",StringComparison.Ordinal)>=0)throw new ArgumentException("壁纸缓存需要完整的本地路径。","storage");
   this.storage=Path.GetFullPath(storage).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
   if(this.storage.Length<=Path.GetPathRoot(this.storage).TrimEnd('\\','/').Length||this.storage.StartsWith("\\\\",StringComparison.Ordinal))throw new ArgumentException("壁纸缓存不能放在磁盘根目录或网络位置。","storage");
   if(apply==null)throw new ArgumentNullException("apply");this.apply=apply;
  }
  internal static bool Supports(Asset asset){return asset!=null&&!asset.IsDirectory&&!asset.IsBookmark&&!asset.Deleted&&MediaTypes.Pictures.Contains(asset.Ext??"");}
  static bool AbsolutePath(string path){if(string.IsNullOrWhiteSpace(path)||!Path.IsPathRooted(path))return false;string root=Path.GetPathRoot(path);return root.EndsWith("\\",StringComparison.Ordinal)||root.EndsWith("/",StringComparison.Ordinal);}
  internal static bool SourceAvailable(string path){try{return AbsolutePath(path)&&File.Exists(path)&&!Directory.Exists(path);}catch{return false;}}
  internal static WallpaperService CreateDefault(){return new WallpaperService(Path.Combine(Settings.ConfigDir,"wallpaper"),ApplyWindows);}
  internal static void ApplyWindows(string path){ApplyWindows(path,SystemParametersInfo,Marshal.GetLastWin32Error);}
  internal static void ApplyWindows(string path,Func<uint,uint,string,uint,bool> api,Func<int> error){
   if(!api(SetDesktopWallpaper,0,path,UpdateProfileAndNotify))throw new Win32Exception(error(),"Windows 未能设置桌面壁纸。请检查系统设置或管理员限制。");
  }
  internal string Set(Asset asset,string source){
   if(!Supports(asset))throw new NotSupportedException("请只选择一张可用的图片设置桌面壁纸。");
   if(!SourceAvailable(source))throw new FileNotFoundException("原图片已移动、删除或磁盘未连接，请先重新定位原文件。",source);
   source=Path.GetFullPath(source);
   if(!MediaTypes.Pictures.Contains(Path.GetExtension(source)))throw new NotSupportedException("原文件不是可支持的图片格式。");
   var info=new FileInfo(source);if(info.Length<1||info.Length>MaximumInput)throw new NotSupportedException("图片为空或超过 256 MB，暂不能设为桌面壁纸。");
   GuardStorage();Directory.CreateDirectory(storage);GuardStorage();
   string target=Path.Combine(storage,Prefix+Guid.NewGuid().ToString("N")+".bmp");
   try{
    WriteBitmap(source,target);
    apply(target);
   }catch{TryRemove(target);throw;}
   // Retain the active copy and one previous copy only. Never delete source
   // images or unfamiliar files in this application-owned cache.
   try{foreach(var old in Directory.GetFiles(storage,Prefix+"*.bmp").Where(p=>!string.Equals(p,target,StringComparison.OrdinalIgnoreCase)&&!string.Equals(p,source,StringComparison.OrdinalIgnoreCase)&&Owned(p)).OrderByDescending(File.GetLastWriteTimeUtc).Skip(1))TryRemove(old);}catch(IOException){}catch(UnauthorizedAccessException){}
   return target;
  }
  void GuardStorage(){
   for(string parent=storage;!string.IsNullOrEmpty(parent);parent=Path.GetDirectoryName(parent)){
    if(File.Exists(parent))throw new IOException("壁纸缓存位置被同名文件占用。");
    if(Directory.Exists(parent)&&(File.GetAttributes(parent)&FileAttributes.ReparsePoint)!=0)throw new IOException("壁纸缓存位置不能使用重定向目录。");
   }
  }
  bool Owned(string path){
   if(!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)),storage,StringComparison.OrdinalIgnoreCase))return false;
   string name=Path.GetFileName(path);if(name.Length!=Prefix.Length+32+4||!name.StartsWith(Prefix,StringComparison.Ordinal)||!name.EndsWith(".bmp",StringComparison.Ordinal))return false;
   return name.Substring(Prefix.Length,32).All(c=>(c>='0'&&c<='9')||(c>='a'&&c<='f'));
  }
  void TryRemove(string path){try{if(Owned(path)&&File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)==0)File.Delete(path);}catch(IOException){}catch(UnauthorizedAccessException){} }
  static void WriteBitmap(string source,string target){
   // Inspect ordinary rasters before GDI allocates the complete image. Special
   // formats reuse the existing bounded SVG / PSD / AI / image-codec preview.
   string ext=Path.GetExtension(source).ToLowerInvariant();
   if(new[]{".jpg",".jpeg",".png",".gif",".bmp",".tif",".tiff",".ico"}.Contains(ext))using(var stream=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read)){
    var decoder=PreviewThumbnail.DecodeWic(stream,source,System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation);
    var frame=decoder.Frames[0];ImageCodec.ValidateDimensions(frame.PixelWidth,frame.PixelHeight);
   }
   using(var preview=PreviewImage.Open(source)){
    var image=preview.Image;
    if(image.Width<1||image.Height<1||(long)image.Width*image.Height>ImageCodec.MaximumSourcePixels)throw new NotSupportedException("图片像素过大，暂不能设为桌面壁纸。");
    if(image.FrameDimensionsList.Contains(FrameDimension.Time.Guid))image.SelectActiveFrame(FrameDimension.Time,0);
    double scale=Math.Min(1,4096.0/Math.Max(image.Width,image.Height));int width=Math.Max(1,(int)Math.Round(image.Width*scale)),height=Math.Max(1,(int)Math.Round(image.Height*scale));
    using(var bitmap=new Bitmap(width,height,PixelFormat.Format24bppRgb)){
     using(var graphics=Graphics.FromImage(bitmap)){graphics.Clear(Color.White);graphics.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;graphics.DrawImage(image,new Rectangle(0,0,width,height));}
     using(var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None))bitmap.Save(output,ImageFormat.Bmp);
    }
   }
  }
 }
}
