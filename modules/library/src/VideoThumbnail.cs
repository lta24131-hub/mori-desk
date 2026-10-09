using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Mori {
 internal static class VideoThumbnail {
  static readonly SemaphoreSlim Workers=new SemaphoreSlim(1);
  internal static string LastFailure;
  static readonly object failureLock=new object();static readonly Dictionary<string,DateTime> failures=new Dictionary<string,DateTime>();
  internal static BitmapSource Render(string path,out int width,out int height){
   width=height=0;string key=Path.GetFullPath(path)+"|"+File.GetLastWriteTimeUtc(path).Ticks;DateTime failed;
   lock(failureLock)if(failures.TryGetValue(key,out failed)&&failed>DateTime.UtcNow.AddMinutes(-10))return null;
   Workers.Wait();try{
    var start=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--video-thumbnail \""+Path.GetFullPath(path)+"\""){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
    using(var process=Process.Start(start)){
     var diagnostic=process.StandardError.ReadToEndAsync();
     var bytes=Task.Run(delegate{using(var buffer=new MemoryStream()){byte[] part=new byte[16384];int n;while((n=process.StandardOutput.BaseStream.Read(part,0,part.Length))>0){if(buffer.Length+n>3*1024*1024)throw new InvalidDataException("Video thumbnail output too large");buffer.Write(part,0,n);}return buffer.ToArray();}});
     var clock=Stopwatch.StartNew();bool stopped=false;
     try{
      while(!process.WaitForExit(50)){process.Refresh();if(clock.ElapsedMilliseconds>15000||process.PrivateMemorySize64>256L*1024*1024){stopped=true;break;}}
      if(stopped||process.ExitCode!=0)throw new NotSupportedException("系统未能生成视频缩略图 "+(stopped?"timeout":diagnostic.GetAwaiter().GetResult()));var data=bytes.GetAwaiter().GetResult();
      using(var input=new MemoryStream(data))using(var reader=new BinaryReader(input)){width=reader.ReadInt32();height=reader.ReadInt32();if(width<0||height<0||width>100000||height>100000)throw new InvalidDataException();using(var png=new MemoryStream(data,8,data.Length-8)){var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=png;bitmap.EndInit();bitmap.Freeze();if(bitmap.PixelWidth>512||bitmap.PixelHeight>512)throw new InvalidDataException();return bitmap;}}
     }finally{if(!process.HasExited){process.Kill();process.WaitForExit(2000);}try{bytes.Wait(2000);}catch(AggregateException){}}
    }
   }catch(Exception ex){LastFailure=ex.Message;lock(failureLock){if(failures.Count>=128)failures.Clear();failures[key]=DateTime.UtcNow;}return null;}finally{Workers.Release();}
  }
  [StructLayout(LayoutKind.Sequential)]struct SIZE {public int X,Y;}
  [ComImport,Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]interface IShellItemImageFactory {[PreserveSig]int GetImage(SIZE size,int flags,out IntPtr bitmap);}
  [StructLayout(LayoutKind.Sequential)]struct PROPERTYKEY {public Guid fmtid;public uint pid;}
  [StructLayout(LayoutKind.Explicit,Size=24)]struct PROPVARIANT {[FieldOffset(0)]public ushort type;[FieldOffset(8)]public uint value;}
  [ComImport,Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]interface IPropertyStore {
   [PreserveSig]int GetCount(out uint count);[PreserveSig]int GetAt(uint index,out PROPERTYKEY key);[PreserveSig]int GetValue(ref PROPERTYKEY key,out PROPVARIANT value);[PreserveSig]int SetValue(ref PROPERTYKEY key,ref PROPVARIANT value);[PreserveSig]int Commit();
  }
  [DllImport("shell32.dll",CharSet=CharSet.Unicode,PreserveSig=false)]static extern void SHCreateItemFromParsingName(string path,IntPtr context,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out IShellItemImageFactory factory);
  [DllImport("shell32.dll",CharSet=CharSet.Unicode,PreserveSig=false)]static extern void SHGetPropertyStoreFromParsingName(string path,IntPtr context,int flags,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out IPropertyStore store);
  [DllImport("gdi32.dll")]static extern bool DeleteObject(IntPtr bitmap);
  [DllImport("ole32.dll")]static extern int PropVariantClear(ref PROPVARIANT value);
  static int Dimension(IPropertyStore store,uint id){var key=new PROPERTYKEY{fmtid=new Guid("64440491-4C8B-11D1-8B70-080036B11A03"),pid=id};PROPVARIANT value;int hr=store.GetValue(ref key,out value);try{return hr>=0&&(value.type==19||value.type==3)&&value.value<=100000?(int)value.value:0;}finally{PropVariantClear(ref value);}}
  internal static int Worker(string[] args){if(args.Length!=2||!File.Exists(args[1]))return 2;if(!string.Equals(Path.GetExtension(args[1]),".webm",StringComparison.OrdinalIgnoreCase)&&ShellWorker(args)==0)return 0;return BrowserVideoPoster.Worker(args[1]);}
  static int ShellWorker(string[] args){IShellItemImageFactory factory=null;IPropertyStore store=null;IntPtr native=IntPtr.Zero;
   try{if(args.Length!=2||!File.Exists(args[1]))return 2;Guid iid=typeof(IShellItemImageFactory).GUID;SHCreateItemFromParsingName(args[1],IntPtr.Zero,ref iid,out factory);
    // THUMBNAILONLY: never cache a generic file-type icon as a video poster.
    Marshal.ThrowExceptionForHR(factory.GetImage(new SIZE{X=512,Y=512},8,out native));if(native==IntPtr.Zero)return 2;
    var bitmap=Imaging.CreateBitmapSourceFromHBitmap(native,IntPtr.Zero,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());bitmap.Freeze();int width=0,height=0;
    try{iid=typeof(IPropertyStore).GUID;SHGetPropertyStoreFromParsingName(args[1],IntPtr.Zero,0,ref iid,out store);width=Dimension(store,3);height=Dimension(store,4);}catch(COMException){}
    using(var png=new MemoryStream()){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));encoder.Save(png);using(var output=Console.OpenStandardOutput())using(var writer=new BinaryWriter(output)){writer.Write(width);writer.Write(height);writer.Write(png.ToArray());}}return 0;
   }catch{return 2;}finally{if(native!=IntPtr.Zero)DeleteObject(native);if(store!=null)Marshal.ReleaseComObject(store);if(factory!=null)Marshal.ReleaseComObject(factory);}
  }
 }
}
