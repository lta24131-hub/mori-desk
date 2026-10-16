using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MoriDesk {
 // A local, current-user-only handoff for the independent image viewer.
 // Two fixed receivers bound decoding and dispatcher work; no browser/API key.
 internal sealed class ImagePinBridge:IDisposable {
  internal const int MaximumBytes=24*1024*1024,MaximumPixels=32*1024*1024;
  readonly string name;readonly Dispatcher dispatcher;readonly Action<BitmapSource> pin;
  readonly HashSet<NamedPipeServerStream> streams=new HashSet<NamedPipeServerStream>();
  volatile bool stopped;
  internal static void ValidateDimensions(int width,int height){if(width<1||height<1||(long)width*height>MaximumPixels)throw new NotSupportedException("图片超过贴图大小限制（3200 万像素），请先缩小后重试。");}
  internal static string PipeName(string root){var sid=WindowsIdentity.GetCurrent().User.Value;using(var hash=System.Security.Cryptography.SHA256.Create())return "Nook.ImagePins."+BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(sid+"|"+Path.GetFullPath(root).TrimEnd('\\','/').ToUpperInvariant()))).Replace("-","").Substring(0,24);}
  internal static PipeSecurity Security(){var security=new PipeSecurity();var user=WindowsIdentity.GetCurrent().User;security.SetAccessRuleProtection(true,false);security.SetOwner(user);security.AddAccessRule(new PipeAccessRule(user,PipeAccessRights.FullControl,AccessControlType.Allow));return security;}
  internal ImagePinBridge(string root,Dispatcher dispatcher,Action<BitmapSource> pin){
   if(dispatcher==null||pin==null)throw new ArgumentNullException();this.dispatcher=dispatcher;this.pin=pin;name=PipeName(root);
   // Bind before returning so startup errors are explicit, not lost on a thread.
   var first=Create();NamedPipeServerStream second;try{second=Create();}catch{Dispose();throw;}foreach(var stream in new[]{first,second})new Thread(()=>Listen(stream)){IsBackground=true,Name="Nook image pin receiver"}.Start();
  }
  NamedPipeServerStream Create(){var stream=new NamedPipeServerStream(name,PipeDirection.InOut,2,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,4096,4096,Security());lock(streams){if(stopped){stream.Dispose();throw new ObjectDisposedException("ImagePinBridge");}streams.Add(stream);}return stream;}
  void Listen(NamedPipeServerStream initial){var stream=initial;while(!stopped){try{stream.WaitForConnection();Serve(stream);}catch(IOException){}catch(ObjectDisposedException){}catch(Exception){}finally{lock(streams)streams.Remove(stream);stream.Dispose();}if(stopped)return;try{stream=Create();}catch{return;}}}
  void Serve(NamedPipeServerStream stream){
   string result="ok";try{var bytes=ReadPacket(stream,MaximumBytes,5000).GetAwaiter().GetResult();var ticket=new DispatchTicket{Image=Decode(bytes)};
    var queued=dispatcher.BeginInvoke(DispatcherPriority.Normal,new Action(()=>{BitmapSource image;lock(ticket){if(ticket.Canceled)return;ticket.Started=true;image=ticket.Image;ticket.Image=null;}try{if(stopped)throw new InvalidOperationException("Nook 正在退出，请稍后重试。");pin(image);}catch(Exception error){ticket.Error=error.Message;}finally{ticket.Done.TrySetResult(true);}}));
    if(!ticket.Done.Task.Wait(5000)){lock(ticket){if(!ticket.Started){ticket.Canceled=true;ticket.Image=null;queued.Abort();throw new InvalidOperationException("Nook 正忙，请稍后重试。");}}throw new InvalidOperationException("贴图保存尚未确认，请检查桌面后再重试，避免重复。");}
    if(ticket.Error!=null)throw new InvalidOperationException(ticket.Error);
   }catch(Exception error){result=error.Message;if(result.Length>1000)result=result.Substring(0,1000);}
   try{WritePacket(stream,Encoding.UTF8.GetBytes(result),2000).GetAwaiter().GetResult();}catch{}
  }
  sealed class DispatchTicket {internal bool Canceled,Started;internal string Error;internal BitmapSource Image;internal readonly TaskCompletionSource<bool> Done=new TaskCompletionSource<bool>();}
  internal static BitmapSource Decode(byte[] bytes){if(bytes==null||bytes.Length==0||bytes.Length>MaximumBytes)throw new InvalidDataException("图片数据过大或无效。");using(var input=new MemoryStream(bytes,false)){var decoder=BitmapDecoder.Create(input,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.None);if(decoder.CodecInfo.ContainerFormat!=new Guid("1b7cfaf4-713f-473c-bbcd-6137425faeaf")||decoder.Frames.Count!=1)throw new InvalidDataException("贴图需要一张 PNG 静态图片。");var frame=decoder.Frames[0];ValidateDimensions(frame.PixelWidth,frame.PixelHeight);var result=new WriteableBitmap(frame);result.Freeze();return result;}}
  internal static byte[] Encode(BitmapSource image){ValidateDimensions(image.PixelWidth,image.PixelHeight);using(var output=new LimitedMemoryStream()){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));encoder.Save(output);return output.ToArray();}}
  sealed class LimitedMemoryStream:MemoryStream {public override void Write(byte[] buffer,int offset,int count){if(Position+count>MaximumBytes)throw new NotSupportedException("图片贴图数据超过 24 MB，请先缩小后重试。");base.Write(buffer,offset,count);}public override void WriteByte(byte value){if(Position>=MaximumBytes)throw new NotSupportedException("图片贴图数据超过 24 MB，请先缩小后重试。");base.WriteByte(value);}public override void SetLength(long value){if(value>MaximumBytes)throw new NotSupportedException("图片贴图数据超过 24 MB，请先缩小后重试。");base.SetLength(value);}}
  static async Task Deadline(Task task,int milliseconds){if(await Task.WhenAny(task,Task.Delay(milliseconds)).ConfigureAwait(false)!=task)throw new TimeoutException("Nook 贴图服务未及时响应，请检查主窗口是否已打开。");await task.ConfigureAwait(false);}
  static async Task<byte[]> ReadPacket(Stream stream,int maximum,int timeout){var header=new byte[4];await Deadline(ReadExact(stream,header),timeout).ConfigureAwait(false);int length=BitConverter.ToInt32(header,0);if(length<1||length>maximum)throw new InvalidDataException("图片数据过大或无效。");var bytes=new byte[length];await Deadline(ReadExact(stream,bytes),timeout).ConfigureAwait(false);return bytes;}
  static async Task ReadExact(Stream stream,byte[] bytes){int offset=0;while(offset<bytes.Length){int count=await stream.ReadAsync(bytes,offset,bytes.Length-offset).ConfigureAwait(false);if(count==0)throw new EndOfStreamException("贴图连接已关闭。");offset+=count;}}
  static async Task WritePacket(Stream stream,byte[] bytes,int timeout){var header=BitConverter.GetBytes(bytes.Length);await Deadline(stream.WriteAsync(header,0,header.Length),timeout).ConfigureAwait(false);await Deadline(stream.WriteAsync(bytes,0,bytes.Length),timeout).ConfigureAwait(false);await Deadline(stream.FlushAsync(),timeout).ConfigureAwait(false);}
  internal static async Task SendAsync(string root,BitmapSource image){
   // Encoding runs on immutable pixels. The preview may close while the request
   // commits; its lifetime must not own or close the resulting desktop pin.
   if(!image.IsFrozen){image=image.Clone();image.Freeze();}var frozen=image;var bytes=await Task.Run(()=>Encode(frozen)).ConfigureAwait(false);
   using(var stream=new NamedPipeClientStream(".",PipeName(root),PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification)){
    try{await Deadline(stream.ConnectAsync(1500),2000).ConfigureAwait(false);}catch(Exception error){throw new InvalidOperationException("请先打开 Nook 主窗口，再使用“钉在桌面”。",error);}
    string result;try{await WritePacket(stream,bytes,5000).ConfigureAwait(false);result=Encoding.UTF8.GetString(await ReadPacket(stream,4096,12000).ConfigureAwait(false));}catch(Exception error){throw new InvalidOperationException("贴图请求已发送，但结果尚未确认。请先检查桌面，避免重复钉图。",error);}if(result!="ok")throw new InvalidOperationException(result);
   }
  }
  public void Dispose(){stopped=true;lock(streams){foreach(var stream in streams)stream.Dispose();streams.Clear();}}
 }
}
