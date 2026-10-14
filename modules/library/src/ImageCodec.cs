using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Mori {
 // Optional raster fallback only. Native ImageMagick is never loaded by the
 // gallery process: each decode has a cancellable, bounded worker lifetime.
 internal static class ImageCodec {
  internal const long MaximumInput=256L*1024*1024;
  internal const int MaximumOutput=20*1024*1024;
  internal const int MaximumSourceDimension=100000;
  internal const long MaximumSourcePixels=32L*1024*1024;
  internal const int MaximumMilliseconds=12000;
  internal const long MaximumPrivateMemory=384L*1024*1024;
  static readonly SemaphoreSlim Workers=new SemaphoreSlim(1,1);
  static int activeWorkerId;
  internal static int ActiveWorkerId {get{return Volatile.Read(ref activeWorkerId);}}
  static readonly object FailureLock=new object();
  static readonly Dictionary<string,DateTime> Failures=new Dictionary<string,DateTime>();
  static readonly HashSet<string> Extensions=new HashSet<string>(new[]{".jpg",".jpeg",".png",".gif",".bmp",".tif",".tiff",".ico",".webp",".avif",".heic",".heif",".psd",".psb",".dng",".cr2",".cr3",".nef",".arw",".orf",".rw2",".raf"},StringComparer.OrdinalIgnoreCase);
  internal static string ConfigurationPath {get{return Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"image-codecs","config");}}
  internal static bool Supports(string path){return !string.IsNullOrWhiteSpace(path)&&Extensions.Contains(Path.GetExtension(path));}
  internal static bool Available {get{string root=Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);return File.Exists(Path.Combine(root,"Magick.NET.Core.dll"))&&File.Exists(Path.Combine(root,"Magick.NET-Q8-x64.dll"))&&File.Exists(Path.Combine(root,"Magick.Native-Q8-x64.dll"))&&File.Exists(Path.Combine(ConfigurationPath,"policy.xml"));}}
  internal static bool IsDecodeFailure(Exception ex){return ex is NotSupportedException||ex is FileFormatException||ex is ArgumentException||ex is System.Runtime.InteropServices.ExternalException||ex is OutOfMemoryException||ex is InvalidDataException;}
  internal static BitmapSource Render(string path,int longest,out int width,out int height){return Render(path,longest,CancellationToken.None,out width,out height);}
  internal static BitmapSource Render(string path,int longest,CancellationToken cancellation,out int width,out int height){
   width=height=0;cancellation.ThrowIfCancellationRequested();
   if(longest<1||longest>2048)throw new ArgumentOutOfRangeException("longest");
   if(!Supports(path)||path.IndexOf("://",StringComparison.Ordinal)>=0)throw new NotSupportedException("此格式不使用图像增强预览。");
   if(!Available)throw new NotSupportedException("图像增强组件尚未安装，请用默认程序打开。");
   var info=new FileInfo(path);if(!info.Exists)throw new FileNotFoundException("图片不存在，可能已移动。",path);
   if(info.Length<4||info.Length>MaximumInput)throw new NotSupportedException("此图片无法使用增强预览，文件上限为 256 MB。");
   string key=info.FullName+"|"+info.LastWriteTimeUtc.Ticks+"|"+info.Length;DateTime failed;
   lock(FailureLock)if(longest<=512&&Failures.TryGetValue(key,out failed)&&failed>DateTime.UtcNow.AddMinutes(-10))throw new NotSupportedException("此图片暂时无法生成增强缩略图。");
   Workers.Wait(cancellation);
   try{
    cancellation.ThrowIfCancellationRequested();
    byte[] result=RunWorker(info.FullName,longest,cancellation);
    cancellation.ThrowIfCancellationRequested();
    if(result.Length<16)throw new InvalidDataException("图像增强预览输出不完整。");
    using(var header=new BinaryReader(new MemoryStream(result,0,8,false))){width=header.ReadInt32();height=header.ReadInt32();}
    ValidateDimensions(width,height);
    using(var png=new MemoryStream(result,8,result.Length-8,false)){
     var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=png;bitmap.EndInit();bitmap.Freeze();
     if(bitmap.PixelWidth<1||bitmap.PixelHeight<1||bitmap.PixelWidth>longest||bitmap.PixelHeight>longest)throw new InvalidDataException("图像增强输出尺寸无效。");
     cancellation.ThrowIfCancellationRequested();return bitmap;
    }
   }catch(OperationCanceledException){throw;}
   catch{if(longest<=512)lock(FailureLock){if(Failures.Count>=128)Failures.Clear();Failures[key]=DateTime.UtcNow;}throw;}
   finally{Workers.Release();}
  }
  internal static void ValidateDimensions(int width,int height){if(width<1||height<1||width>MaximumSourceDimension||height>MaximumSourceDimension||(long)width*height>MaximumSourcePixels)throw new NotSupportedException("图片尺寸超过增强预览的安全范围，请用默认程序打开。");}
  static byte[] RunWorker(string path,int longest,CancellationToken cancellation){
   var start=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--image-codec \""+path+"\" "+longest){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
   using(var process=Process.Start(start)){
    if(process==null)throw new NotSupportedException("无法启动图像增强预览。");
    var output=Task.Run(()=>ReadOutput(process.StandardOutput.BaseStream));
    var errors=Task.Run(delegate{byte[] buffer=new byte[4096];int read,total=0;while((read=process.StandardError.BaseStream.Read(buffer,0,buffer.Length))>0){total+=read;if(total>65536)throw new InvalidDataException("图像增强错误输出过大。");} });
    Interlocked.Exchange(ref activeWorkerId,process.Id);
    // The UI may cancel while closing its window. All process killing and
    // waits stay on this background decode thread; Cancel() never waits on it.
     try{
      var clock=Stopwatch.StartNew();
      while(!process.WaitForExit(50)){
       cancellation.ThrowIfCancellationRequested();
       if(output.IsFaulted||errors.IsFaulted||clock.ElapsedMilliseconds>MaximumMilliseconds)throw new NotSupportedException("图片过于复杂或增强预览超时，请用默认程序打开。");
       try{process.Refresh();if(process.PrivateMemorySize64>MaximumPrivateMemory)throw new NotSupportedException("图片预览占用过大，已停止增强解码。");}catch(InvalidOperationException){if(!process.HasExited)throw;}
      }
      cancellation.ThrowIfCancellationRequested();
      if(process.ExitCode!=0||errors.IsFaulted)throw new NotSupportedException("图片已损坏或增强解码不支持此变体，请用默认程序打开。");
      try{if(!output.Wait(2000))throw new NotSupportedException("图像增强输出未能结束。");}catch(AggregateException){return output.GetAwaiter().GetResult();}
      return output.GetAwaiter().GetResult();
     }finally{Stop(process);Observe(output);Observe(errors);Interlocked.Exchange(ref activeWorkerId,0);}
   }
  }
  static byte[] ReadOutput(Stream input){using(var data=new MemoryStream()){byte[] buffer=new byte[16384];int count;while((count=input.Read(buffer,0,buffer.Length))>0){if(data.Length+count>MaximumOutput)throw new InvalidDataException("图像增强输出过大。");data.Write(buffer,0,count);}return data.ToArray();}}
  static void Stop(Process process){try{if(!process.HasExited){process.Kill();process.WaitForExit(2000);}}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){} }
  static void Observe(Task task){try{task.Wait(2000);}catch(AggregateException){} }
  // Keeping this call out of the normal preview JIT path prevents the native
  // engine and its managed wrappers becoming resident in the main application.
  [MethodImpl(MethodImplOptions.NoInlining)]
  internal static int Worker(string[] args){try{int longest;if(args.Length!=3||!int.TryParse(args[2],out longest)||longest<1||longest>2048)return 2;return ImageCodecWorker.Run(args[1],longest);}catch{return 2;}}
 }
}
