using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QingJie {
    // Same EXE/model, short-lived process: no OCR engines retained by the tray app.
    // Local user-only pipe, lossless pixels, no screenshot files or network traffic.
    public static class OcrWorker {
        const int Magic=0x514A4F31,MaxPixels=64000000;
        static readonly SemaphoreSlim queue=new SemaphoreSlim(1,1);
        static readonly OcrResultCache cache=new OcrResultCache();
        static void Kill(Process process){try{if(process!=null&&!process.HasExited)process.Kill();}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}}
        public static async Task<OcrPage> Read(BitmapSource source,CancellationToken cancel){
            cancel.ThrowIfCancellationRequested();
            if(source==null)throw new ArgumentNullException("source");
            if((long)source.PixelWidth*source.PixelHeight>MaxPixels)throw new InvalidOperationException("图片过大，请缩小截图区域后重试。");
            if(!source.IsFrozen){source=source.Clone();source.Freeze();}
            string language=OcrLanguagePacks.EffectiveSelectionKey;
            await queue.WaitAsync(cancel);
            try{return await Task.Run(()=>{
                string key=language+":"+OcrResultCache.Fingerprint(source,cancel);var saved=cache.Get(key);cancel.ThrowIfCancellationRequested();if(saved!=null)return saved;
                var result=Run(source,cancel,language);cancel.ThrowIfCancellationRequested();cache.Put(key,result);return result;
            },cancel);}finally{queue.Release();}
        }
        static OcrPage Run(BitmapSource source,CancellationToken cancel,string language){
            string name="QingJie-OCR-"+Guid.NewGuid().ToString("N");
            var security=new PipeSecurity();security.SetAccessRuleProtection(true,false);using(var identity=WindowsIdentity.GetCurrent())security.AddAccessRule(new PipeAccessRule(identity.User,PipeAccessRights.FullControl,AccessControlType.Allow));
            using(var pipe=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,65536,65536,security))
            using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancel)){
                timeout.CancelAfter(TimeSpan.FromSeconds(90));
                var start=new ProcessStartInfo(typeof(Program).Assembly.Location,"--ocr-worker="+name){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=AppDomain.CurrentDomain.BaseDirectory};
                using(var process=Process.Start(start)){
                    try{using(timeout.Token.Register(()=>{Kill(process);try{pipe.Dispose();}catch{}})){
                        pipe.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();
                        using(var writer=new BinaryWriter(pipe,Encoding.UTF8,true))using(var reader=new BinaryReader(pipe,Encoding.UTF8,true)){
                            writer.Write(Magic);writer.Write(source.PixelWidth);writer.Write(source.PixelHeight);writer.Write(language);writer.Write(OcrLanguagePacks.TestFolder??"");
                            var bgra=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);bgra.Freeze();int stride=checked(source.PixelWidth*4);
                            // Stream small strips rather than duplicate the entire screenshot.
                            var strip=new byte[checked(stride*Math.Min(32,source.PixelHeight))];
                            for(int y=0;y<source.PixelHeight;y+=32){timeout.Token.ThrowIfCancellationRequested();int rows=Math.Min(32,source.PixelHeight-y);bgra.CopyPixels(new Int32Rect(0,y,source.PixelWidth,rows),strip,stride,0);writer.Write(strip,0,stride*rows);}writer.Flush();
                            if(reader.ReadInt32()!=Magic)throw new InvalidDataException("识别进程返回数据异常。");
                            string error=reader.ReadString();if(error.Length>0)throw new InvalidOperationException(error);
                            int count=reader.ReadInt32();if(count<0||count>1000000)throw new InvalidDataException("识别文字数量异常。");
                            var page=new OcrPage();for(int i=0;i<count;i++){var word=new WordBox {Text=reader.ReadString(),Line=reader.ReadInt32()};word.Box=new Rect(reader.ReadDouble(),reader.ReadDouble(),reader.ReadDouble(),reader.ReadDouble());page.Words.Add(word);}OcrService.IndexText(page);
                            if(!process.WaitForExit(3000))Kill(process);cancel.ThrowIfCancellationRequested();return page;
                        }
                    }}catch(Exception ex){cancel.ThrowIfCancellationRequested();if(timeout.IsCancellationRequested)throw new TimeoutException("文字识别超时，原图已保留，请缩小区域重试。",ex);throw;}
                    finally{Kill(process);try{process.WaitForExit(3000);}catch(InvalidOperationException){}}
                }
            }
        }
        public static int Execute(string name){
            Guid id;if(!name.StartsWith("QingJie-OCR-",StringComparison.Ordinal)||!Guid.TryParseExact(name.Substring(12),"N",out id))return 2;
            // A crashed parent must not leave a forgotten OCR process behind.
            using(var watchdog=new Timer(s=>Environment.Exit(3),null,95000,Timeout.Infinite))
            using(var pipe=new NamedPipeClientStream(".",name,PipeDirection.InOut)){
                try{pipe.Connect(10000);using(var reader=new BinaryReader(pipe,Encoding.UTF8,true))using(var writer=new BinaryWriter(pipe,Encoding.UTF8,true)){
                    try{
                        if(reader.ReadInt32()!=Magic)throw new InvalidDataException("Invalid OCR request");int width=reader.ReadInt32(),height=reader.ReadInt32();
                        if(width<=0||height<=0||(long)width*height>MaxPixels)throw new InvalidDataException("Invalid image dimensions");
                        string language=reader.ReadString(),testFolder=reader.ReadString();if(testFolder.Length>0)OcrLanguagePacks.TestFolder=testFolder;
                        var bitmap=ReadBitmap(reader,width,height);
                        var task=OcrService.ReadLocal(bitmap,language);var frame=new System.Windows.Threading.DispatcherFrame();task.ContinueWith(t=>frame.Continue=false);System.Windows.Threading.Dispatcher.PushFrame(frame);
                        var page=task.GetAwaiter().GetResult();writer.Write(Magic);writer.Write("");writer.Write(page.Words.Count);
                        foreach(var word in page.Words){writer.Write(word.Text);writer.Write(word.Line);writer.Write(word.Box.X);writer.Write(word.Box.Y);writer.Write(word.Box.Width);writer.Write(word.Box.Height);}writer.Flush();return 0;
                    }catch(Exception ex){writer.Write(Magic);writer.Write(ex.Message);writer.Flush();return 1;}
                }}catch{return 1;}
            }
        }
        internal static BitmapSource ReadBitmap(BinaryReader reader,int width,int height){
            if(width<=0||height<=0||(long)width*height>MaxPixels)throw new InvalidDataException("Invalid image dimensions");
            int stride=checked(width*4);var strip=new byte[checked(stride*Math.Min(32,height))];
            int bytes=checked(stride*height);var address=System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes);
            try{
                for(int y=0;y<height;y+=32){
                    int rows=Math.Min(32,height-y),length=stride*rows,offset=0;
                    while(offset<length){int read=reader.Read(strip,offset,length-offset);if(read==0)throw new EndOfStreamException();offset+=read;}
                    System.Runtime.InteropServices.Marshal.Copy(strip,0,IntPtr.Add(address,y*stride),length);
                }
                var bitmap=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,address,bytes,stride);bitmap.Freeze();return bitmap;
            }finally{System.Runtime.InteropServices.Marshal.FreeHGlobal(address);}
        }
    }
}
