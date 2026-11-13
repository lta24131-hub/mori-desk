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
using System.Collections.Generic;
using System.Web.Script.Serialization;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace QingJie {
    // Same EXE/model, short-lived process: no OCR engines retained by the tray app.
    // Local user-only pipe, lossless pixels, no screenshot files or network traffic.
    public static class OcrWorker {
        const int Magic=0x514A4F32,StructuredMagic=0x514A4F33,LocalConfidenceMagic=0x514A4F34,MaxPixels=64000000;
        internal static int RequestTimeoutMilliseconds=90000;
        static string ModernWorker {get{return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ocr-engine","Nook.Ocr.exe");}}
        internal static bool UsesModernEngine(string language){
            if(!string.IsNullOrEmpty(language))return false;
            string folder=Path.GetDirectoryName(ModernWorker);if(!Directory.Exists(folder))return false;
            string manifest=Path.Combine(folder,"component.json");
            try{
                if(!File.Exists(ModernWorker)||!File.Exists(manifest)||new FileInfo(manifest).Length>1024*1024)throw new InvalidDataException();
                var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(manifest));
                if(Convert.ToInt32(data["schemaVersion"])!=1||Convert.ToInt32(data["protocol"])!=3||Convert.ToString(data["component"])!="ocr-engine"||Convert.ToString(data["engineIdentity"])!="rapidocrnet-4.2.0-ppocrv5-ch-screen2x-trim1-layout5")throw new InvalidDataException();
                return true;
            }catch(Exception e){throw new InvalidDataException("文字识别组件不完整，请修复或更新 Nook。原图已保留。",e);}
        }
        static readonly SemaphoreSlim queue=new SemaphoreSlim(1,1);
        static readonly OcrResultCache cache=new OcrResultCache();
        public static async Task<OcrPage> Read(BitmapSource source,CancellationToken cancel){
            cancel.ThrowIfCancellationRequested();
            if(source==null)throw new ArgumentNullException("source");
            if((long)source.PixelWidth*source.PixelHeight>MaxPixels)throw new InvalidOperationException("图片过大，请缩小截图区域后重试。");
            if(!source.IsFrozen){source=source.Clone();source.Freeze();}
            string language=OcrLanguagePacks.EffectiveSelectionKey;
            await queue.WaitAsync(cancel);
            try{return await Task.Run(()=>{
                string key=(UsesModernEngine(language)?"rapidocrnet-4.2.0-ppocrv5-ch-screen2x-trim1-layout5:":"windows-tesseract-v1:")+language+":"+OcrResultCache.Fingerprint(source,cancel);var saved=cache.Get(key);cancel.ThrowIfCancellationRequested();if(saved!=null)return saved;
                var result=Run(source,cancel,language);cancel.ThrowIfCancellationRequested();cache.Put(key,result);return result;
            },cancel);}finally{queue.Release();}
        }
        static OcrPage Run(BitmapSource source,CancellationToken cancel,string language){
            bool modern=UsesModernEngine(language);
            return RunWithWorker(source,cancel,language,modern,(name,attempt)=>new ProcessStartInfo(modern?ModernWorker:typeof(Program).Assembly.Location,"--ocr-worker="+name){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=modern?Path.GetDirectoryName(ModernWorker):AppDomain.CurrentDomain.BaseDirectory},RequestTimeoutMilliseconds);
        }
        // The same deadline covers startup, transfer, response and the one permitted
        // recovery. The factory also lets the VM runner use real faulting processes
        // without loading a model or changing installed application components.
        internal static OcrPage RunWithWorker(BitmapSource source,CancellationToken cancel,string language,bool modern,Func<string,int,ProcessStartInfo> startWorker,int timeoutMilliseconds){
            return RunWithWorker(source,cancel,language,modern,startWorker,timeoutMilliseconds,null);
        }
        internal static OcrPage RunWithWorker(BitmapSource source,CancellationToken cancel,string language,bool modern,Func<string,int,ProcessStartInfo> startWorker,int timeoutMilliseconds,Action<Process> beforeResume){
            if(source==null)throw new ArgumentNullException("source");
            if(startWorker==null)throw new ArgumentNullException("startWorker");
            if(timeoutMilliseconds<1)throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            var clock=Stopwatch.StartNew();
            using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancel)){
                timeout.CancelAfter(timeoutMilliseconds);
                for(int attempt=0;attempt<2;attempt++){
                    cancel.ThrowIfCancellationRequested();
                    try{
                        timeout.Token.ThrowIfCancellationRequested();
                        var page=RunAttempt(source,language,modern,startWorker,attempt,timeout.Token,clock,timeoutMilliseconds,beforeResume);
                        cancel.ThrowIfCancellationRequested();timeout.Token.ThrowIfCancellationRequested();return page;
                    }catch(Exception error){
                        cancel.ThrowIfCancellationRequested();
                        if(timeout.IsCancellationRequested||clock.ElapsedMilliseconds>=timeoutMilliseconds)throw new TimeoutException("文字识别超时，原图已保留，请缩小区域重试。",error);
                        var failure=error as WorkerFailure;
                        // Only a native access violation before any response bytes may
                        // recover. Partial/malformed responses, explicit engine errors,
                        // missing components and every other exit code are final.
                        if(attempt==0&&failure!=null&&failure.Recoverable&&failure.WorkerStopped)continue;
                        if(failure!=null)throw;
                        throw new InvalidOperationException("文字识别未能启动，原图已保留。请重试；若持续失败，请修复或更新 Nook。",error);
                    }
                }
                throw new InvalidOperationException("文字识别未完成，原图已保留。");
            }
        }
        static int Remaining(Stopwatch clock,int timeoutMilliseconds,int maximum){return (int)Math.Max(0,Math.Min(maximum,timeoutMilliseconds-clock.ElapsedMilliseconds));}
        static OcrPage RunAttempt(BitmapSource source,string language,bool modern,Func<string,int,ProcessStartInfo> startWorker,int attempt,CancellationToken token,Stopwatch clock,int timeoutMilliseconds,Action<Process> beforeResume){
            string name="QingJie-OCR-"+Guid.NewGuid().ToString("N");
            var security=new PipeSecurity();security.SetAccessRuleProtection(true,false);using(var identity=WindowsIdentity.GetCurrent())security.AddAccessRule(new PipeAccessRule(identity.User,PipeAccessRights.FullControl,AccessControlType.Allow));
            using(var pipe=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,65536,65536,security)){
                token.ThrowIfCancellationRequested();
                using(var worker=WorkerProcess.Start(startWorker(name,attempt),beforeResume)){
                    var process=worker.Process;var exited=new TaskCompletionSource<bool>();
                    EventHandler onExit=(sender,args)=>exited.TrySetResult(true);process.Exited+=onExit;process.EnableRaisingEvents=true;if(process.HasExited)exited.TrySetResult(true);
                    var response=new ResponseStream(pipe);WorkerFailure failure=null;
                    try{using(token.Register(()=>{worker.Terminate();try{pipe.Dispose();}catch(ObjectDisposedException){}catch(IOException){}})){
                        token.ThrowIfCancellationRequested();
                        var connection=pipe.WaitForConnectionAsync(token);
                        Task.WhenAny(connection,exited.Task).GetAwaiter().GetResult();
                        // The connection completion can race a very early exit. Let its
                        // already-signalled I/O settle; never close an established pipe
                        // merely because Exited fired, since it may hold a full reply.
                        if(!connection.IsCompleted&&!connection.Wait(Remaining(clock,timeoutMilliseconds,100)))throw new IOException("OCR worker exited before connecting.");
                        connection.GetAwaiter().GetResult();
                        using(var writer=new BinaryWriter(pipe,Encoding.UTF8,true))using(var reader=new BinaryReader(response,Encoding.UTF8,true)){
                            writer.Write(modern?StructuredMagic:Magic);writer.Write(source.PixelWidth);writer.Write(source.PixelHeight);writer.Write(language);writer.Write(OcrLanguagePacks.TestFolder??"");
                            var bgra=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);bgra.Freeze();int stride=checked(source.PixelWidth*4);
                            // Stream small strips rather than duplicate the entire screenshot.
                            var strip=new byte[checked(stride*Math.Min(32,source.PixelHeight))];
                            for(int y=0;y<source.PixelHeight;y+=32){token.ThrowIfCancellationRequested();int rows=Math.Min(32,source.PixelHeight-y);bgra.CopyPixels(new Int32Rect(0,y,source.PixelWidth,rows),strip,stride,0);writer.Write(strip,0,stride*rows);}writer.Flush();
                            var page=ReadResult(reader);
                            token.ThrowIfCancellationRequested();return page;
                        }
                    }}catch(Exception error){
                        token.ThrowIfCancellationRequested();
                        int? exitCode=null;
                        try{if(process.HasExited||process.WaitForExit(Remaining(clock,timeoutMilliseconds,100)))exitCode=process.ExitCode;}catch(InvalidOperationException){}
                        bool transport=error is IOException&&!(error is InvalidDataException);
                        bool recoverable=transport&&response.BytesRead==0&&exitCode==unchecked((int)0xC0000005);
                        string message=response.BytesRead>0&&error is IOException?"文字识别结果不完整，原图已保留。请重试；若持续失败，请修复或更新 Nook。":"文字识别进程意外停止，原图已保留。请重试；若持续失败，请修复或更新 Nook。";
                        if(error is InvalidOperationException)message="文字识别未完成，原图已保留。"+Environment.NewLine+error.Message;
                        failure=new WorkerFailure(message,error,recoverable,exitCode,response.BytesRead,attempt+1);throw failure;
                    }finally{
                        // Close/terminate the worker-only job before a retry can start.
                        // Its children cannot outlive this request or a parent crash.
                        process.Exited-=onExit;worker.Terminate();
                        bool stopped=worker.WaitForStopped(Remaining(clock,timeoutMilliseconds,250));
                        if(failure!=null)failure.WorkerStopped=stopped;
                    }
                }
            }
        }
        sealed class WorkerFailure:IOException {
            internal readonly bool Recoverable;internal bool WorkerStopped;
            internal WorkerFailure(string message,Exception inner,bool recoverable,int? exitCode,long bytes,int attempt):base(message,inner){
                Recoverable=recoverable;Data["OcrWorkerAttempt"]=attempt;Data["OcrResponseBytes"]=bytes;
                if(exitCode.HasValue)Data["OcrWorkerExitCode"]="0x"+unchecked((uint)exitCode.Value).ToString("X8");
            }
        }
        sealed class ResponseStream:Stream {
            readonly Stream inner;internal long BytesRead;
            internal ResponseStream(Stream value){inner=value;}
            public override int Read(byte[] buffer,int offset,int count){int read=inner.Read(buffer,offset,count);BytesRead+=read;return read;}
            public override int ReadByte(){int value=inner.ReadByte();if(value>=0)BytesRead++;return value;}
            public override bool CanRead{get{return true;}}public override bool CanSeek{get{return false;}}public override bool CanWrite{get{return false;}}
            public override long Length{get{throw new NotSupportedException();}}public override long Position{get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override void Flush(){}public override long Seek(long offset,SeekOrigin origin){throw new NotSupportedException();}public override void SetLength(long value){throw new NotSupportedException();}public override void Write(byte[] buffer,int offset,int count){throw new NotSupportedException();}
        }
        // Windows 10+ assigns the job atomically while creating the process. A
        // separate CreateProcess/AssignProcessToJobObject pair leaves an orphan
        // suspended process if the owner crashes between those two calls.
        sealed class WorkerProcess:IDisposable {
            readonly object gate=new object();IntPtr job;internal Process Process;
            internal static WorkerProcess Start(ProcessStartInfo start,Action<Process> beforeResume){
                var result=new WorkerProcess();PROCESS_INFORMATION info=new PROCESS_INFORMATION();bool ready=false;
                IntPtr attributes=IntPtr.Zero,jobList=IntPtr.Zero;bool attributesInitialized=false;
                try{
                    if(start==null||start.UseShellExecute||!Path.IsPathRooted(start.FileName))throw new InvalidOperationException("Invalid OCR worker launch configuration.");
                    result.job=CreateJobObject(IntPtr.Zero,null);if(result.job==IntPtr.Zero)throw new Win32Exception();
                    var limits=new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();limits.BasicLimitInformation.LimitFlags=0x2000;
                    int size=Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));IntPtr data=Marshal.AllocHGlobal(size);
                    try{Marshal.StructureToPtr(limits,data,false);if(!SetInformationJobObject(result.job,9,data,(uint)size))throw new Win32Exception();}finally{Marshal.FreeHGlobal(data);}
                    IntPtr attributeBytes=IntPtr.Zero;
                    InitializeProcThreadAttributeList(IntPtr.Zero,1,0,ref attributeBytes);
                    if(attributeBytes==IntPtr.Zero||Marshal.GetLastWin32Error()!=122)throw new Win32Exception();
                    attributes=Marshal.AllocHGlobal(attributeBytes);
                    if(!InitializeProcThreadAttributeList(attributes,1,0,ref attributeBytes))throw new Win32Exception();attributesInitialized=true;
                    jobList=Marshal.AllocHGlobal(IntPtr.Size);Marshal.WriteIntPtr(jobList,result.job);
                    // PROC_THREAD_ATTRIBUTE_JOB_LIST (13 | INPUT). No uncontained
                    // fallback is allowed if the OS or current job rejects it.
                    if(!UpdateProcThreadAttribute(attributes,0,new IntPtr(0x0002000D),jobList,new IntPtr(IntPtr.Size),IntPtr.Zero,IntPtr.Zero))throw new Win32Exception();
                    var startup=new STARTUPINFOEX{StartupInfo=new STARTUPINFO{cb=Marshal.SizeOf(typeof(STARTUPINFOEX)),dwFlags=1,wShowWindow=0},AttributeList=attributes};
                    var command=new StringBuilder("\""+start.FileName+"\" "+start.Arguments);
                    // EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW | CREATE_SUSPENDED.
                    if(!CreateProcess(start.FileName,command,IntPtr.Zero,IntPtr.Zero,false,0x08080004,IntPtr.Zero,start.WorkingDirectory,ref startup,out info))throw new Win32Exception();
                    result.Process=System.Diagnostics.Process.GetProcessById((int)info.dwProcessId);IntPtr handle=result.Process.Handle;
                    // VM fault injection pauses the owner at the former orphan gap.
                    // Production supplies null; the child is already in the job here.
                    if(beforeResume!=null)beforeResume(result.Process);
                    if(ResumeThread(info.hThread)==uint.MaxValue)throw new Win32Exception();ready=true;return result;
                }finally{
                    if(attributesInitialized)DeleteProcThreadAttributeList(attributes);
                    if(attributes!=IntPtr.Zero)Marshal.FreeHGlobal(attributes);if(jobList!=IntPtr.Zero)Marshal.FreeHGlobal(jobList);
                    if(!ready){if(info.hProcess!=IntPtr.Zero)TerminateProcess(info.hProcess,3);result.Dispose();}
                    if(info.hThread!=IntPtr.Zero)CloseHandle(info.hThread);if(info.hProcess!=IntPtr.Zero)CloseHandle(info.hProcess);
                }
            }
            internal void Terminate(){lock(gate){if(job!=IntPtr.Zero)TerminateJobObject(job,3);}}
            internal bool WaitForStopped(int milliseconds){
                var clock=Stopwatch.StartNew();
                do{
                    lock(gate){
                        if(job==IntPtr.Zero)return true;
                        JOBOBJECT_BASIC_ACCOUNTING_INFORMATION accounting;
                        if(!QueryInformationJobObject(job,1,out accounting,(uint)Marshal.SizeOf(typeof(JOBOBJECT_BASIC_ACCOUNTING_INFORMATION)),IntPtr.Zero))return false;
                        if(accounting.ActiveProcesses==0)return true;
                    }
                    int remaining=milliseconds-(int)clock.ElapsedMilliseconds;if(remaining<=0)return false;Thread.Sleep(Math.Min(10,remaining));
                }while(true);
            }
            public void Dispose(){lock(gate){if(job!=IntPtr.Zero){CloseHandle(job);job=IntPtr.Zero;}}if(Process!=null)Process.Dispose();}
            [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct STARTUPINFO {public int cb;public string lpReserved,lpDesktop,lpTitle;public uint dwX,dwY,dwXSize,dwYSize,dwXCountChars,dwYCountChars,dwFillAttribute,dwFlags;public short wShowWindow,cbReserved2;public IntPtr lpReserved2,hStdInput,hStdOutput,hStdError;}
            [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct STARTUPINFOEX {public STARTUPINFO StartupInfo;public IntPtr AttributeList;}
            [StructLayout(LayoutKind.Sequential)]struct PROCESS_INFORMATION {public IntPtr hProcess,hThread;public uint dwProcessId,dwThreadId;}
            [StructLayout(LayoutKind.Sequential)]struct JOBOBJECT_BASIC_LIMIT_INFORMATION {public long PerProcessUserTimeLimit,PerJobUserTimeLimit;public uint LimitFlags;public UIntPtr MinimumWorkingSetSize,MaximumWorkingSetSize;public uint ActiveProcessLimit;public UIntPtr Affinity;public uint PriorityClass,SchedulingClass;}
            [StructLayout(LayoutKind.Sequential)]struct IO_COUNTERS {public ulong ReadOperationCount,WriteOperationCount,OtherOperationCount,ReadTransferCount,WriteTransferCount,OtherTransferCount;}
            [StructLayout(LayoutKind.Sequential)]struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION {public long TotalUserTime,TotalKernelTime,ThisPeriodTotalUserTime,ThisPeriodTotalKernelTime;public uint TotalPageFaultCount,TotalProcesses,ActiveProcesses,TotalTerminatedProcesses;}
            [StructLayout(LayoutKind.Sequential)]struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION {public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;public IO_COUNTERS IoInfo;public UIntPtr ProcessMemoryLimit,JobMemoryLimit,PeakProcessMemoryUsed,PeakJobMemoryUsed;}
            [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr CreateJobObject(IntPtr attributes,string name);
            [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetInformationJobObject(IntPtr job,int infoClass,IntPtr info,uint length);
            [DllImport("kernel32.dll",SetLastError=true)]static extern bool InitializeProcThreadAttributeList(IntPtr attributes,int count,uint flags,ref IntPtr size);
            [DllImport("kernel32.dll",SetLastError=true)]static extern bool UpdateProcThreadAttribute(IntPtr attributes,uint flags,IntPtr attribute,IntPtr value,IntPtr size,IntPtr previousValue,IntPtr returnSize);
            [DllImport("kernel32.dll")]static extern void DeleteProcThreadAttributeList(IntPtr attributes);
            [DllImport("kernel32.dll",SetLastError=true)]static extern bool QueryInformationJobObject(IntPtr job,int infoClass,out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info,uint length,IntPtr returnLength);
            [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool CreateProcess(string application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,bool inherit,uint flags,IntPtr environment,string currentDirectory,ref STARTUPINFOEX startup,out PROCESS_INFORMATION process);
            [DllImport("kernel32.dll",SetLastError=true)]static extern uint ResumeThread(IntPtr thread);
            [DllImport("kernel32.dll",SetLastError=true)]static extern bool TerminateProcess(IntPtr process,uint code);
            [DllImport("kernel32.dll",SetLastError=true)]static extern bool TerminateJobObject(IntPtr job,uint code);
            [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
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
                        var page=task.GetAwaiter().GetResult();WriteResult(writer,page);writer.Flush();return 0;
                    }catch(Exception ex){writer.Write(Magic);writer.Write(ex.Message);writer.Flush();return 1;}
                }}catch{return 1;}
            }
        }
        internal static void WriteResult(BinaryWriter writer,OcrPage page){
            // The in-process fallback worker uses the same application version.
            // Keep its source flag so a Windows unknown score is never mistaken
            // for a confident model result after the pipe round trip.
            writer.Write(LocalConfidenceMagic);writer.Write("");writer.Write(page.Notice??"");writer.Write(page.Words.Count);
            foreach(var word in page.Words){writer.Write(word.Text);writer.Write(word.Line);writer.Write(word.Box.X);writer.Write(word.Box.Y);writer.Write(word.Box.Width);writer.Write(word.Box.Height);writer.Write(word.Confidence);writer.Write((int)word.ConfidenceSource);}
        }
        internal static OcrPage ReadResult(BinaryReader reader){
            int protocol=reader.ReadInt32();if(protocol!=Magic&&protocol!=StructuredMagic&&protocol!=LocalConfidenceMagic)throw new InvalidDataException("识别进程返回数据异常。");
            string error=reader.ReadString();if(error.Length>0)throw new InvalidOperationException(error);
            string notice=reader.ReadString();int count=reader.ReadInt32();if(count<0||count>1000000)throw new InvalidDataException("识别文字数量异常。");
            var page=new OcrPage{Notice=notice.Length==0?null:notice};
            for(int i=0;i<count;i++){
                var word=new WordBox {Text=reader.ReadString(),Line=reader.ReadInt32()};word.Box=ReadBox(reader);
                if(protocol==StructuredMagic){word.Prefix=reader.ReadString();word.Confidence=reader.ReadDouble();word.ConfidenceSource=OcrConfidenceSource.Model;if(word.Prefix.Length>1000)throw new InvalidDataException("识别文字数据异常。");}
                else if(protocol==LocalConfidenceMagic){word.Confidence=reader.ReadDouble();int source=reader.ReadInt32();if(source<0||source>(int)OcrConfidenceSource.User)throw new InvalidDataException("识别文字来源异常。");word.ConfidenceSource=(OcrConfidenceSource)source;}
                if(double.IsNaN(word.Confidence)||word.Confidence<0||word.Confidence>1)throw new InvalidDataException("识别文字数据异常。");
                page.Words.Add(word);
            }
            if(protocol==StructuredMagic){
                int regions=reader.ReadInt32();if(regions<0||regions>10000)throw new InvalidDataException("识别段落数量异常。");
                for(int n=0;n<regions;n++){
                    var region=new TranslationRegion{Text=reader.ReadString(),Box=ReadBox(reader),Lines=reader.ReadInt32()};
                    if(region.Lines<1||region.Lines>10000||string.IsNullOrWhiteSpace(region.Text))throw new InvalidDataException("识别段落数据异常。");
                    for(int line=0;line<region.Lines;line++)region.SourceLines.Add(new TranslationSourceLine{Text=reader.ReadString(),Box=ReadBox(reader)});
                    page.TranslationRegions.Add(region);
                }
            }
            OcrService.IndexText(page);return page;
        }
        static Rect ReadBox(BinaryReader reader){
            double x=reader.ReadDouble(),y=reader.ReadDouble(),w=reader.ReadDouble(),h=reader.ReadDouble();
            foreach(double value in new[]{x,y,w,h})if(double.IsNaN(value)||double.IsInfinity(value)||Math.Abs(value)>1000000)throw new InvalidDataException("识别位置数据异常。");
            if(w<=0||h<=0)throw new InvalidDataException("识别位置数据异常。");return new Rect(x,y,w,h);
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
