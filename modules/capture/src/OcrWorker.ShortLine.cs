using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QingJie {
    public static partial class OcrWorker {
        internal const int ColdSymbolMilliseconds=6500;
        const int WarmSymbolMilliseconds=2300,SymbolIdleMilliseconds=12000;
        static readonly object idleGate=new object();
        static readonly SemaphoreSlim symbolRequests=new SemaphoreSlim(1,1);
        static SymbolSession idleSession;
        static Timer idleTimer;
        static long idleGeneration;
        internal static bool HasIdleSymbolSession {get{lock(idleGate)return idleSession!=null;}}

        sealed class SymbolSession:IDisposable {
            internal readonly NamedPipeServerStream Pipe;
            readonly WorkerProcess worker;
            internal SymbolSession(){
                string name="QingJie-OCR-"+Guid.NewGuid().ToString("N");
                Pipe=CreateUserPipe(name);
                try{
                    var start=new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Nook.LineOcr.exe"),"--pipe="+name){
                        UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
                        WorkingDirectory=AppDomain.CurrentDomain.BaseDirectory
                    };
                    worker=WorkerProcess.Start(start,null);
                }catch{Pipe.Dispose();throw;}
            }
            internal int ProcessId{get{return worker.Process.Id;}}
            internal bool Alive{get{return !worker.Process.HasExited;}}
            internal void Connect(CancellationToken token){WaitForShortLineConnection(Pipe,worker.Process,token).GetAwaiter().GetResult();}
            internal void Abort(){worker.Terminate();Pipe.Dispose();}
            public void Dispose(){worker.Dispose();Pipe.Dispose();}
        }

        internal static async Task WaitForShortLineConnection(NamedPipeServerStream pipe,Process child,CancellationToken token){
            token.ThrowIfCancellationRequested();
            var exited=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler onExit=(sender,args)=>exited.TrySetResult(true);
            child.Exited+=onExit;
            try{
                child.EnableRaisingEvents=true;
                if(child.HasExited)throw new EndOfStreamException("Short-line OCR exited before connecting.");
                using(var pending=CancellationTokenSource.CreateLinkedTokenSource(token)){
                    var connection=pipe.WaitForConnectionAsync(pending.Token);
                    if(await Task.WhenAny(connection,exited.Task).ConfigureAwait(false)!=connection){
                        // No request bytes were sent. Stop and observe this pending
                        // accept before the caller closes the pipe or starts again.
                        pending.Cancel();
                        try{await connection.ConfigureAwait(false);}catch(OperationCanceledException){}
                        token.ThrowIfCancellationRequested();
                        throw new EndOfStreamException("Short-line OCR exited before connecting.");
                    }
                    await connection.ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }
            }finally{child.Exited-=onExit;child.EnableRaisingEvents=false;}
        }

        static SymbolSession TakeIdleSymbols(){
            lock(idleGate){
                idleGeneration++;
                if(idleTimer!=null){idleTimer.Dispose();idleTimer=null;}
                var session=idleSession;idleSession=null;
                return session;
            }
        }
        static void ParkSymbols(SymbolSession session){
            lock(idleGate){
                long generation=++idleGeneration;
                idleSession=session;
                idleTimer=new Timer(_=>{
                    lock(idleGate){
                        if(generation!=idleGeneration)return;
                        idleSession=null;idleTimer.Dispose();idleTimer=null;
                    }
                    session.Dispose();
                },null,SymbolIdleMilliseconds,Timeout.Infinite);
            }
        }

        // One private, job-contained process can serve a short burst of captures.
        // The image, recognizer and reading belong to one request. The worker
        // consumes the identical Gray8 samples already prepared for Windows OCR.
        internal static OcrPage ReadShortLineSymbols(BitmapSource image,CancellationToken cancel,Action<int> started,Action<string> trace=null){
            if(image.Format!=PixelFormats.Gray8||!SymbolSize(image.PixelWidth,image.PixelHeight))
                throw new InvalidDataException("Invalid prepared short-line image.");
            symbolRequests.Wait(cancel);
            SymbolSession session=null;
            try{
                cancel.ThrowIfCancellationRequested();
                session=TakeIdleSymbols();
                if(session!=null&&!session.Alive){session.Dispose();session=null;}
                bool cold=session==null;int budget=cold?ColdSymbolMilliseconds:WarmSymbolMilliseconds;
                if(started!=null)started(budget);
                using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancel)){
                    timeout.CancelAfter(budget);
                    timeout.Token.ThrowIfCancellationRequested();
                    if(cold)session=new SymbolSession();
                    if(trace!=null)trace(cold?"symbol-launched":"symbol-reused");
                    var token=timeout.Token;var pipe=session.Pipe;
                    OcrPage result;
                    try{using(token.Register(session.Abort)){
                        token.ThrowIfCancellationRequested();
                        if(cold)session.Connect(token);
                        if(trace!=null)trace("symbol-connected");
                        using(var reader=new BinaryReader(pipe,Encoding.UTF8,true)){
                            int w=image.PixelWidth,h=image.PixelHeight;
                            // The native bitmap rows are padded to whole 32-bit words.
                            int stride=(w+3)&~3;
                            const int headerSize=5*sizeof(int);
                            byte[] packet=new byte[headerSize+stride*h];
                            image.CopyPixels(packet,stride,headerSize);
                            using(var header=new BinaryWriter(new MemoryStream(packet))){
                                header.Write(Magic);header.Write(w);header.Write(h);
                                header.Write((int)Math.Round(image.DpiX));header.Write((int)Math.Round(image.DpiY));
                            }
                            pipe.Write(packet,0,packet.Length);
                            if(trace!=null)trace("symbol-sent");
                            int size=reader.ReadInt32();
                            if(size<=0||size>32768)throw new InvalidDataException("Invalid short-line result length.");
                            var data=reader.ReadBytes(size);
                            if(data.Length!=size)throw new EndOfStreamException();
                            if(trace!=null)trace("symbol-replied");
                            using(var response=new BinaryReader(new MemoryStream(data)))result=ReadResult(response);
                        }
                    }}catch{token.ThrowIfCancellationRequested();throw;}
                    token.ThrowIfCancellationRequested();
                    ParkSymbols(session);session=null;return result;
                }
            }finally{
                if(session!=null)session.Dispose();
                symbolRequests.Release();
            }
        }

        static bool SymbolSize(int w,int h){
            return w>0&&h>0&&w<=1536&&h<=224&&(long)w*h<=280000;
        }
    }
}
