using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace Mori {
 internal sealed class PdfRenderConnection:IDisposable {
  internal const int Magic=0x50444631,MaximumOutput=24*1024*1024,TimeoutMilliseconds=20000;
  internal const long MaximumMemory=384L*1024*1024;
  readonly Process process;readonly BinaryReader input;readonly BinaryWriter output;readonly PdfWorkerJob job;
  internal PdfPageSize[] Sizes;internal int ProcessId{get{return process.Id;}}
  internal int? ExitCode;internal bool ForcedStop;bool disposed;
  PdfRenderConnection(Process process,PdfWorkerJob job){this.process=process;this.job=job;input=new BinaryReader(process.StandardOutput.BaseStream);output=new BinaryWriter(process.StandardInput.BaseStream);}
  internal static PdfRenderConnection Open(string path,int page,CancellationToken cancellation){
   cancellation.ThrowIfCancellationRequested();
   var start=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--pdf-render-worker \""+path+"\" "+page){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardInput=true,RedirectStandardOutput=true};
   var child=Process.Start(start);PdfWorkerJob job=null;PdfRenderConnection worker=null;
   try{
    // The child waits for this handshake before opening any document. Assign
    // it first, so parent crashes cannot leave an orphan holding source files.
    job=new PdfWorkerJob(child);worker=new PdfRenderConnection(child,job);
    worker.Run(delegate{worker.output.Write(Magic);worker.output.Flush();worker.ReadStatus();if(worker.input.ReadInt32()!=Magic)throw new InvalidDataException("PDF 预览连接版本不符。");
     int count=worker.input.ReadInt32(),first=worker.input.ReadInt32(),number=worker.input.ReadInt32();
     if(count<1||count>PdfPreview.MaximumPages||first!=(page<0?0:page)||number!=(page<0?count:1)||first<0||first>=count)throw new InvalidDataException("PDF 页数数据无效。");
     var sizes=new PdfPageSize[count];for(int i=first;i<first+number;i++){double width=worker.input.ReadDouble(),height=worker.input.ReadDouble();if(!ValidSize(width)||!ValidSize(height))throw new InvalidDataException("PDF 页面尺寸无效。");sizes[i]=new PdfPageSize(width,height);}worker.Sizes=sizes;return true;
    },cancellation);return worker;
   }catch{if(worker!=null)worker.Dispose();else{if(job!=null)job.Dispose();try{if(!child.HasExited)child.Kill();child.WaitForExit(2000);}catch(InvalidOperationException){}finally{child.Dispose();}}throw;}
  }
  static bool ValidSize(double value){return !double.IsNaN(value)&&!double.IsInfinity(value)&&value>0&&value<=int.MaxValue;}
  void ReadStatus(){int status=input.ReadInt32();if(status==0)return;if(status!=1)throw new InvalidDataException("PDF 预览响应无效。");int size=input.ReadInt32();if(size<1||size>8192)throw new InvalidDataException("PDF 预览错误数据无效。");throw new NotSupportedException(Encoding.UTF8.GetString(ReadBytes(size)));}
  byte[] ReadBytes(int length){byte[] value=input.ReadBytes(length);if(value.Length!=length)throw new EndOfStreamException("PDF 预览连接中断。");return value;}
  internal PdfPagePreview Render(int page,int dimension,CancellationToken cancellation){
   return Run(delegate{
    output.Write(page);output.Write(dimension);output.Flush();ReadStatus();
    int index=input.ReadInt32(),width=input.ReadInt32(),height=input.ReadInt32(),length=input.ReadInt32();
    if(index!=page||width<1||height<1||length<24||length>MaximumOutput||width!=(int)Math.Max(1,Math.Round(Sizes[page].Width))||height!=(int)Math.Max(1,Math.Round(Sizes[page].Height)))throw new InvalidDataException("PDF 页面响应无效。");
    byte[] bytes=ReadBytes(length);ValidatePng(bytes,dimension);
    using(var stream=new MemoryStream(bytes,false)){
     var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=stream;bitmap.EndInit();
     if(bitmap.PixelWidth<1||bitmap.PixelHeight<1||bitmap.PixelWidth>dimension||bitmap.PixelHeight>dimension)throw new InvalidDataException("PDF 页面超出安全尺寸。");bitmap.Freeze();
     return new PdfPagePreview{Bitmap=bitmap,PageCount=Sizes.Length,PageIndex=page,Width=width,Height=height};
    }
   },cancellation);
  }
  internal static void ValidatePng(byte[] bytes,int dimension){
   byte[] signature={137,80,78,71,13,10,26,10};if(bytes.Length<33||bytes.Length>MaximumOutput)throw new InvalidDataException("PDF 预览数据无效。");for(int i=0;i<8;i++)if(bytes[i]!=signature[i])throw new InvalidDataException("PDF 预览不是 PNG。");
   if(bytes[8]!=0||bytes[9]!=0||bytes[10]!=0||bytes[11]!=13||bytes[12]!=73||bytes[13]!=72||bytes[14]!=68||bytes[15]!=82)throw new InvalidDataException("PDF 预览尺寸头无效。");
   long width=((long)bytes[16]<<24)|((long)bytes[17]<<16)|((long)bytes[18]<<8)|bytes[19],height=((long)bytes[20]<<24)|((long)bytes[21]<<16)|((long)bytes[22]<<8)|bytes[23];
   if(width<1||height<1||width>dimension||height>dimension)throw new InvalidDataException("PDF 预览超出安全尺寸。");
  }
  T Run<T>(Func<T> action,CancellationToken cancellation){
   if(disposed)throw new ObjectDisposedException("PdfRenderConnection");cancellation.ThrowIfCancellationRequested();var pending=Task.Run(action);var watch=Stopwatch.StartNew();
   try{
    while(!pending.Wait(25)){
     cancellation.ThrowIfCancellationRequested();if(process.HasExited){if(!pending.Wait(2000))throw new IOException("PDF 预览进程已退出，请重新打开文档。");break;}
     process.Refresh();if(watch.ElapsedMilliseconds>TimeoutMilliseconds||process.PrivateMemorySize64>MaximumMemory)throw new NotSupportedException("此 PDF 页面处理时间或内存超出限制，已停止预览。请使用默认程序打开。");
    }
    cancellation.ThrowIfCancellationRequested();return pending.GetAwaiter().GetResult();
   }catch(AggregateException error){Abort();throw error.InnerException??error;}
   catch{Abort();throw;}
   finally{if(!pending.IsCompleted)try{pending.Wait(2000);}catch(AggregateException){}if(pending.IsFaulted){var observed=pending.Exception;}}
  }
  void Abort(){try{if(!process.HasExited){ForcedStop=true;job.Dispose();if(!process.WaitForExit(2000)){process.Kill();process.WaitForExit(2000);}}}catch(InvalidOperationException){}catch(Win32Exception){}}
  public void Dispose(){
   if(disposed)return;disposed=true;
   try{output.Dispose();if(!process.WaitForExit(2000))Abort();if(process.HasExited)ExitCode=process.ExitCode;}
   catch(IOException){Abort();}catch(InvalidOperationException){Abort();}
   finally{input.Dispose();job.Dispose();process.Dispose();}
  }
 }
 // No UI, profile access, routing, network access or application registration.
 internal static class PdfRenderWorker {
  internal static int Run(string[] args){
   int requested;if(args.Length!=3||args[0]!="--pdf-render-worker"||!int.TryParse(args[2],out requested)||requested< -1||requested>=PdfPreview.MaximumPages)return 2;
   using(var input=new BinaryReader(Console.OpenStandardInput()))using(var output=new BinaryWriter(Console.OpenStandardOutput())){
    try{
     if(input.ReadInt32()!=PdfRenderConnection.Magic)return 2;
     using(var session=Task.Run(()=>PdfNativePreview.OpenCoreAsync(args[1],CancellationToken.None,requested)).GetAwaiter().GetResult()){
      output.Write(0);output.Write(PdfRenderConnection.Magic);output.Write(session.PageCount);int first=requested<0?0:requested,number=requested<0?session.PageCount:1;output.Write(first);output.Write(number);
      for(int i=first;i<first+number;i++){var size=session.GetPageSize(i);output.Write(size.Width);output.Write(size.Height);}output.Flush();
      while(true){int page;try{page=input.ReadInt32();}catch(EndOfStreamException){break;}int dimension=input.ReadInt32();if(requested>=0&&requested!=page)throw new ArgumentOutOfRangeException("page");
       var raster=session.RenderAsync(page,dimension,CancellationToken.None).GetAwaiter().GetResult();
       output.Write(0);output.Write(raster.PageIndex);output.Write(raster.Width);output.Write(raster.Height);output.Write(raster.Bytes.Length);output.Write(raster.Bytes);output.Flush();
      }
     }
     return 0;
    }catch(Exception error){try{string message=error is NotSupportedException?error.Message:"无法预览此 PDF / AI 文件，可能已加密、损坏或原文件不可用。请用默认程序打开。";byte[] bytes=Encoding.UTF8.GetBytes(message);if(bytes.Length>8192)bytes=Encoding.UTF8.GetBytes("PDF 预览失败，请用默认程序打开。");output.Write(1);output.Write(bytes.Length);output.Write(bytes);output.Flush();}catch(IOException){}return 2;}
   }
  }
 }
 internal sealed class PdfWorkerJob:IDisposable {
  readonly SafeFileHandle handle;
  internal PdfWorkerJob(Process process){
   handle=CreateJobObject(IntPtr.Zero,null);if(handle.IsInvalid)throw new Win32Exception();
   try{var limits=new ExtendedLimits();limits.Basic.LimitFlags=0x2000|0x100;limits.ProcessMemoryLimit=new UIntPtr((ulong)PdfRenderConnection.MaximumMemory);int size=Marshal.SizeOf(typeof(ExtendedLimits));IntPtr data=Marshal.AllocHGlobal(size);
    try{Marshal.StructureToPtr(limits,data,false);if(!SetInformationJobObject(handle,9,data,(uint)size)||!AssignProcessToJobObject(handle,process.Handle))throw new Win32Exception();}finally{Marshal.FreeHGlobal(data);}
   }catch{handle.Dispose();throw;}
  }
  public void Dispose(){handle.Dispose();}
  [StructLayout(LayoutKind.Sequential)] struct BasicLimits {public long PerProcessUserTimeLimit,PerJobUserTimeLimit;public uint LimitFlags;public UIntPtr MinimumWorkingSetSize,MaximumWorkingSetSize;public uint ActiveProcessLimit;public UIntPtr Affinity;public uint PriorityClass,SchedulingClass;}
  [StructLayout(LayoutKind.Sequential)] struct IoCounters {public ulong ReadOperationCount,WriteOperationCount,OtherOperationCount,ReadTransferCount,WriteTransferCount,OtherTransferCount;}
  [StructLayout(LayoutKind.Sequential)] struct ExtendedLimits {public BasicLimits Basic;public IoCounters Io;public UIntPtr ProcessMemoryLimit,JobMemoryLimit,PeakProcessMemoryUsed,PeakJobMemoryUsed;}
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle CreateJobObject(IntPtr attributes,string name);
  [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetInformationJobObject(SafeFileHandle handle,int infoClass,IntPtr info,uint length);
  [DllImport("kernel32.dll",SetLastError=true)] static extern bool AssignProcessToJobObject(SafeFileHandle handle,IntPtr process);
 }
}
