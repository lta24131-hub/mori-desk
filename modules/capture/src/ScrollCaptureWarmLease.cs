using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace QingJie {
    // Shared process boundary lets lifecycle tests exercise the production
    // ownership logic without launching Electron, windows or native capture.
    internal interface IScrollCaptureChild : IDisposable {
        bool HasExited {get;}
        int ExitCode {get;}
        bool WaitForExit(int milliseconds);
        void Kill();
    }
    internal sealed class ScrollCaptureChild : IScrollCaptureChild {
        readonly Process process;
        ScrollCaptureChild(Process value){process=value;}
        internal static IScrollCaptureChild Launch(ProcessStartInfo info){
            var value=new Process{StartInfo=info};
            try{if(!value.Start())throw new IOException("滚动截图组件未能启动。");return new ScrollCaptureChild(value);}catch{value.Dispose();throw;}
        }
        public bool HasExited {get{return process.HasExited;}}
        public int ExitCode {get{return process.ExitCode;}}
        public bool WaitForExit(int milliseconds){return process.WaitForExit(milliseconds);}
        public void Kill(){process.Kill();}
        public void Dispose(){process.Dispose();}
    }

    // One lease per ordinary screenshot (not per monitor). Until claimed it
    // owns only code/hidden UI: no source pixels, annotations, global shortcuts
    // or sampling request exist. A failed warm start is always optional.
    internal sealed class ScrollCaptureWarmLease : IDisposable {
        internal const int LifetimeMilliseconds=120000;
        readonly object gate=new object();
        readonly Func<Action,Task> schedule;
        readonly Func<ScrollCaptureWarmLease,IScrollCaptureChild> prepare;
        readonly Func<long> elapsed;
        readonly Action<ScrollCaptureWarmLease,IScrollCaptureChild> retire;
        readonly TaskCompletionSource<IScrollCaptureChild> prepared=new TaskCompletionSource<IScrollCaptureChild>(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource<bool> retired=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Timer expiry;
        IScrollCaptureChild child;
        bool started,preparing,claimed,disposed,transferred,ownsDirectory;
        internal readonly long Owner;
        internal readonly string SessionId,OutputDirectory,HelperPath;
        internal Task<bool> Retirement {get{return retired.Task;}}
        internal bool IsDisposed {get{lock(gate)return disposed;}}
        internal bool IsClaimed {get{lock(gate)return claimed;}}
        internal bool IsTransferred {get{lock(gate)return transferred;}}
        internal bool IsPrepared {get{return prepared.Task.IsCompleted;}}
        internal ScrollCaptureWarmLease(string appBase,long owner)
            :this(appBase,owner,null,null,null,null,true){}
        // Test dependencies never fall back to a real launch. Synthetic tests
        // supply BOTH prepare and retire plus a deterministic scheduler/clock.
        internal ScrollCaptureWarmLease(string appBase,long owner,Func<Action,Task> dispatch,Func<ScrollCaptureWarmLease,IScrollCaptureChild> preparation,Action<ScrollCaptureWarmLease,IScrollCaptureChild> cleanup,Func<long> clock,bool automaticExpiry=false){
            if(string.IsNullOrWhiteSpace(appBase)||!Path.IsPathRooted(appBase))throw new ArgumentException("截图组件目录无效。");
            if(owner<=0)throw new ArgumentException("截图预备会话无效。");
            if((preparation==null)!=(cleanup==null))throw new ArgumentException("预备会话必须同时提供准备和清理接口。");
            Owner=owner;SessionId=Guid.NewGuid().ToString("N");OutputDirectory=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"Nook","scroll-capture",SessionId));
            HelperPath=Path.GetFullPath(Path.Combine(appBase,"scroll-capture","Nook.ScrollCapture.exe"));
            schedule=dispatch??(action=>Task.Run(action));prepare=preparation??Prepare;retire=cleanup??Retire;
            var watch=Stopwatch.StartNew();elapsed=clock??(()=>watch.ElapsedMilliseconds);
            if(automaticExpiry)expiry=new Timer(ignored=>ExpireIfUnclaimed(),null,LifetimeMilliseconds,Timeout.Infinite);
        }
        internal void Start(){
            lock(gate){if(started||disposed)return;started=true;preparing=true;}
            try{schedule(PrepareCore);}catch{lock(gate){preparing=false;disposed=true;prepared.TrySetResult(null);}StopExpiry();retired.TrySetResult(true);}
        }
        void PrepareCore(){
            IScrollCaptureChild value=null;
            try{if(!IsDisposed)value=prepare(this);}catch{ /* Warm failure must not interrupt ordinary screenshots. */ }
            bool keep;
            lock(gate){
                preparing=false;keep=!disposed&&value!=null;
                if(keep){child=value;prepared.TrySetResult(value);}
                else{disposed=true;prepared.TrySetResult(null);}
            }
            if(!keep){StopExpiry();RetireCore(value);}
        }
        internal bool TryClaim(long owner){
            bool expired=false;
            lock(gate){
                if(owner!=Owner||!started||disposed||claimed||transferred)return false;
                expired=elapsed()>=LifetimeMilliseconds;
                if(!expired)claimed=true;
            }
            if(expired){Dispose();return false;}
            StopExpiry();return true;
        }
        // The owner can claim while renderer loading is still in progress.
        // Transfer is single-shot; cancellation always wins over late launch.
        internal async Task<IScrollCaptureChild> TakeProcessAsync(int timeoutMilliseconds=ExternalScrollCaptureSession.StartupTimeoutMilliseconds){
            if(timeoutMilliseconds<1)throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            using(var timeout=new CancellationTokenSource()){
                var winner=await Task.WhenAny(prepared.Task,Task.Delay(timeoutMilliseconds,timeout.Token)).ConfigureAwait(false);
                timeout.Cancel();
                if(winner!=prepared.Task){Dispose();return null;}
            }
            var value=await prepared.Task.ConfigureAwait(false);
            bool usable=false;
            lock(gate){
                if(!disposed&&claimed&&!transferred&&value!=null&&ReferenceEquals(value,child)){
                    try{usable=!value.HasExited;}catch{usable=false;}
                    if(usable){transferred=true;child=null;}
                }
            }
            if(usable){StopExpiry();retired.TrySetResult(true);return value;}
            Dispose();return null;
        }
        internal string SerializeWarmRequest(){return new JavaScriptSerializer().Serialize(new{schemaVersion=1,sessionId=SessionId,outputDirectory=OutputDirectory,parentProcessId=Process.GetCurrentProcess().Id});}
        internal static string LaunchArguments(string path){
            // Use the same exact quoting rules as the final request path.
            return ExternalScrollCaptureSession.LaunchArguments(path).Replace("--nook-request ","--nook-warm ");
        }
        internal static void ValidateComponent(string json){
            ExternalScrollCaptureSession.ValidateDeferredStartComponent(json,true);
            var manifest=new JavaScriptSerializer{MaxJsonLength=65536,RecursionLimit=16}.Deserialize<Dictionary<string,object>>(json);object capability;
            if(!manifest.TryGetValue("capturePrewarm",out capability)||!(capability is bool)||!(bool)capability)throw new InvalidDataException("此截图组件不支持后台预备。");
        }
        static IScrollCaptureChild Prepare(ScrollCaptureWarmLease lease){
            string folder=Path.GetDirectoryName(lease.HelperPath),manifest=Path.Combine(folder,"component.json");
            if(!File.Exists(lease.HelperPath))throw new FileNotFoundException();
            ExternalScrollCaptureSession.RequireRegularDirectory(folder);ExternalScrollCaptureSession.RequireRegularFile(manifest,65536);
            ValidateComponent(File.ReadAllText(manifest,Encoding.UTF8));
            if(lease.IsDisposed)throw new OperationCanceledException();
            if(Directory.Exists(lease.OutputDirectory)||File.Exists(lease.OutputDirectory))throw new IOException("截图预备目录已存在。");
            Directory.CreateDirectory(lease.OutputDirectory);lease.ownsDirectory=true;
            ExternalScrollCaptureSession.RequireRegularDirectory(lease.OutputDirectory);
            string path=Path.Combine(lease.OutputDirectory,"warm.json");
            using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new StreamWriter(stream,new UTF8Encoding(false)))writer.Write(lease.SerializeWarmRequest());
            // Do not hold the UI-facing gate during OS process startup. If
            // cancellation races this call, PrepareCore retires the late child
            // instead of publishing it, and never revives the closed capture.
            if(lease.IsDisposed)throw new OperationCanceledException();
            return ScrollCaptureChild.Launch(new ProcessStartInfo{FileName=lease.HelperPath,Arguments=LaunchArguments(path),WorkingDirectory=folder,UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden});
        }
        internal static void ValidateReady(string json,string expectedSession){
            Dictionary<string,object> value;
            try{if(string.IsNullOrWhiteSpace(json)||json.Length>4096)throw new InvalidDataException();value=new JavaScriptSerializer{MaxJsonLength=4096,RecursionLimit=4}.Deserialize<Dictionary<string,object>>(json);}catch(Exception error){throw new InvalidDataException("截图预备信号无效。",error);}
            object schema,id,warm;
            if(value==null||value.Count!=3||!value.TryGetValue("schemaVersion",out schema)||!(schema is int)||(int)schema!=1||!value.TryGetValue("sessionId",out id)||!(id is string)||(string)id!=expectedSession||!value.TryGetValue("warm",out warm)||!(warm is bool)||!(bool)warm)throw new InvalidDataException("截图预备信号不属于当前会话。");
        }
        static void Retire(ScrollCaptureWarmLease lease,IScrollCaptureChild value){
            if(value!=null){
                try{
                    if(!value.HasExited){
                        lease.RequestCancellation();
                        if(!value.WaitForExit(2000)){value.Kill();value.WaitForExit(1000);}
                    }
                    if(!value.HasExited)throw new IOException("截图预备进程尚未退出，不能同时启动另一会话。");
                }finally{value.Dispose();}
            }
            if(lease.ownsDirectory)ExternalScrollCaptureSession.CleanupOwnedDirectory(lease.OutputDirectory,new[]{"warm.json","warm.ready.json","startup-timing.json","cancel.request"});
        }
        void RequestCancellation(){
            try{
                if(!ownsDirectory||!Directory.Exists(OutputDirectory))return;
                ExternalScrollCaptureSession.RequireRegularDirectory(OutputDirectory);
                string path=Path.Combine(OutputDirectory,"cancel.request");
                if(!File.Exists(path))using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read))using(var writer=new StreamWriter(stream,new UTF8Encoding(false)))writer.Write(SessionId);
            }catch(IOException){}catch(UnauthorizedAccessException){}
        }
        void RetireCore(IScrollCaptureChild value){bool success=true;try{retire(this,value);}catch{success=false;}finally{retired.TrySetResult(success);}}
        void StopExpiry(){Timer timer;lock(gate){timer=expiry;expiry=null;}if(timer!=null)timer.Dispose();}
        internal void ExpireIfUnclaimed(){Cancel(true);}
        public void Dispose(){Cancel(false);}
        void Cancel(bool onlyUnclaimed){
            IScrollCaptureChild value=null;bool cleanup=false;
            lock(gate){
                if(disposed||transferred||(onlyUnclaimed&&claimed))return;
                disposed=true;prepared.TrySetResult(null);
                if(!preparing){value=child;child=null;cleanup=true;}
            }
            StopExpiry();
            // Scheduling is constant-time on the UI thread. WaitForExit and
            // profile cleanup happen only in the background retirement job.
            if(cleanup)try{schedule(()=>RetireCore(value));}catch{ThreadPool.QueueUserWorkItem(ignored=>RetireCore(value));}
        }
    }

    // Dispatcher-owned generation gate; also testable without WPF windows.
    internal sealed class ScrollCaptureWarmCoordinator {
        readonly Func<long,ScrollCaptureWarmLease> create;
        long owner;bool enabled,requested;
        ScrollCaptureWarmLease lease;
        internal ScrollCaptureWarmCoordinator(Func<long,ScrollCaptureWarmLease> factory){create=factory;}
        internal void Begin(long ticket,CaptureFeatures features,bool synthetic){
            Cancel();owner=ticket;enabled=ticket>0&&!synthetic&&features!=null&&features.Scrolling&&features.ScrollPrewarm;
        }
        internal void Rendered(long ticket){
            if(ticket!=owner||!enabled||requested)return;requested=true;
            try{lease=create(ticket);if(lease!=null)lease.Start();}catch{lease=null;}
        }
        internal ScrollCaptureWarmLease Claim(long ticket){
            if(ticket!=owner||lease==null)return null;
            var value=lease;lease=null;
            // Even an expired/failed lease is returned as a retirement barrier:
            // the final session must not cold-launch while it is still exiting.
            if(!value.TryClaim(ticket))value.Dispose();return value;
        }
        internal void Cancel(){owner=0;enabled=false;requested=false;var value=lease;lease=null;if(value!=null)value.Dispose();}
    }
}
