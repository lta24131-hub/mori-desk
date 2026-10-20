using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rectangle = System.Drawing.Rectangle;

namespace QingJie {
    internal sealed class ExternalScrollPoint { public double x,y; }
    internal sealed class ExternalScrollAnnotation {
        public string kind,color,text,mosaicPath;
        public double x1,y1,x2,y2,strokeWidth,fontSize,textWrapWidth;
        public List<ExternalScrollPoint> points=new List<ExternalScrollPoint>();
        [ScriptIgnore] public BitmapSource Mosaic {get;set;}
    }
    internal sealed class ExternalScrollResult {
        public int schemaVersion;
        public int exportId;
        public string sessionId,status,action,imagePath,message;
    }
    internal sealed class ExternalScrollExportReply {
        public string status,message;
        internal static ExternalScrollExportReply Success(){return new ExternalScrollExportReply{status="ok"};}
        internal static ExternalScrollExportReply Cancelled(){return new ExternalScrollExportReply{status="cancelled"};}
        internal static ExternalScrollExportReply Failure(string message){return new ExternalScrollExportReply{status="error",message=message};}
    }
    internal sealed class ExternalScrollReady { public int schemaVersion;public string sessionId;public bool ready; }
    internal sealed class ExternalScrollRecoverySnapshot {
        internal readonly int CompletedExportId;
        internal readonly string CompletedExportAction;
        internal readonly ExternalScrollResult VerifiedImage;
        internal ExternalScrollRecoverySnapshot(int id,string action,ExternalScrollResult image){
            CompletedExportId=id;CompletedExportAction=action;
            if(image!=null)VerifiedImage=new ExternalScrollResult{schemaVersion=image.schemaVersion,exportId=image.exportId,sessionId=image.sessionId,status=image.status,action=image.action,imagePath=image.imagePath,message=image.message};
        }
    }

    // The helper is a packaged, fixed executable, never a command from the
    // browser or a returned result. Every launch owns a fresh GUID directory.
    // Construction/serialization are inert and can be tested without a desktop.
    internal sealed class ExternalScrollCaptureSession : IDisposable {
        const int MaximumResultCharacters=32768;
        internal const int StartupTimeoutMilliseconds=20000;
        internal const long MaximumImagePixels=64000000;
        readonly object gate=new object();
        readonly TaskCompletionSource<bool> lifetimeEnded=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly Rectangle region,monitor;
        readonly double scaleFactor;
        readonly IList<ExternalScrollAnnotation> annotations;
        readonly CaptureFeatures captureFeatures;
        IScrollCaptureChild process;
        readonly ScrollCaptureWarmLease warmLease;
        readonly ScrollCaptureStartupTiming startupTiming;
        bool usedWarmLease;
        bool started,disposed,startHandoffPending,startReleased,startHandoffAbandoned;
        int processedExports,completedExportId;
        string completedExportAction;
        ExternalScrollResult acceptedResult,verifiedImageResult;
        internal string SessionId {get;private set;}
        internal string OutputDirectory {get;private set;}
        internal readonly string HelperPath;
        internal bool IsDisposed {get{lock(gate)return disposed;}}
        internal bool IsStarted {get{lock(gate)return started;}}
        internal bool CanReleaseStart {get{lock(gate)return !disposed&&!startHandoffAbandoned;}}
        // Only local destination acknowledgement proves an output succeeded.
        // Copy metadata, not pixels, so failed transport can recover without
        // keeping another full-size bitmap alive or trusting result.json.
        internal ExternalScrollRecoverySnapshot RecoverySnapshot(){lock(gate)return new ExternalScrollRecoverySnapshot(completedExportId,completedExportAction,verifiedImageResult);}
        internal string RequestPath {get{return Path.Combine(OutputDirectory,"request.json");}}
        internal ExternalScrollCaptureSession(string appBase,Rectangle region,Rectangle monitor,double scaleFactor,IList<ExternalScrollAnnotation> annotations=null,CaptureFeatures features=null,ScrollCaptureWarmLease warm=null,ScrollCaptureStartupTiming timing=null){
            if(string.IsNullOrWhiteSpace(appBase)||!Path.IsPathRooted(appBase))throw new ArgumentException("截图组件目录无效。");
            if(monitor.Width<=0||monitor.Height<=0||region.Width<32||region.Height<80||!monitor.Contains(region))throw new ArgumentException("滚动截屏区域至少需要 32 × 80 像素，并位于同一块屏幕内。");
            if(!Finite(scaleFactor)||scaleFactor<=0)throw new ArgumentException("截图缩放比例无效。");
            this.region=region;this.monitor=monitor;this.scaleFactor=scaleFactor;this.annotations=annotations??new List<ExternalScrollAnnotation>();
            captureFeatures=(features??new CaptureFeatures()).Copy();
            ResetColdIdentity();
            HelperPath=Path.GetFullPath(Path.Combine(appBase,"scroll-capture","Nook.ScrollCapture.exe"));
            warmLease=warm;startupTiming=timing;
        }
        void ResetColdIdentity(){SessionId=Guid.NewGuid().ToString("N");OutputDirectory=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"Nook","scroll-capture",SessionId));usedWarmLease=false;}
        internal async Task<bool> AdoptWarmProcessAsync(int timeoutMilliseconds=StartupTimeoutMilliseconds){
            if(warmLease==null)return false;
            if(timeoutMilliseconds<1)throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            var watch=Stopwatch.StartNew();
            if(!warmLease.IsClaimed||!captureFeatures.Scrolling||!captureFeatures.ScrollPrewarm||!string.Equals(warmLease.HelperPath,HelperPath,StringComparison.OrdinalIgnoreCase))warmLease.Dispose();
            var child=await warmLease.TakeProcessAsync(timeoutMilliseconds).ConfigureAwait(false);
            if(child==null){
                warmLease.Dispose();
                using(var timeout=new CancellationTokenSource()){
                    int remaining=(int)Math.Max(1,timeoutMilliseconds-watch.ElapsedMilliseconds);
                    var winner=await Task.WhenAny(warmLease.Retirement,lifetimeEnded.Task,Task.Delay(remaining,timeout.Token)).ConfigureAwait(false);timeout.Cancel();
                    if(IsDisposed)throw new OperationCanceledException();
                    if(winner!=warmLease.Retirement)throw new TimeoutException("截图预备进程未及时退出，原截图和标注已保留。请稍后重试。");
                }
                if(!await warmLease.Retirement.ConfigureAwait(false))throw new IOException("截图预备进程尚未退出，请稍后重试。");
                if(IsDisposed)throw new OperationCanceledException();return false;
            }
            lock(gate){SessionId=warmLease.SessionId;OutputDirectory=warmLease.OutputDirectory;usedWarmLease=true;process=child;if(disposed)throw new OperationCanceledException();}
            ScrollCaptureStartupTiming.Mark(startupTiming,ScrollCaptureStartupStage.WarmClaim);return true;
        }
        static bool Finite(double value){return !double.IsNaN(value)&&!double.IsInfinity(value);}
        internal static List<ExternalScrollAnnotation> ConvertAnnotations(IEnumerable<Annotation> marks,Rect selection,double scaleX,double scaleY,int cropX,int cropY){
            if(marks==null||selection.IsEmpty||!Finite(scaleX)||!Finite(scaleY)||scaleX<=0||scaleY<=0)throw new ArgumentException("标注坐标无效。");
            var result=new List<ExternalScrollAnnotation>();
            foreach(var mark in marks){
                if(mark==null)throw new InvalidDataException("截图包含空标注，原截图已保留。");
                if(mark.Kind!="rect"&&mark.Kind!="ellipse"&&mark.Kind!="line"&&mark.Kind!="arrow"&&mark.Kind!="pen"&&mark.Kind!="text"&&mark.Kind!="mosaic")throw new InvalidDataException("暂不支持迁移这种标注："+mark.Kind);
                if(!Finite(mark.Width)||mark.Width<=0||!Finite(mark.Start.X)||!Finite(mark.Start.Y)||!Finite(mark.End.X)||!Finite(mark.End.Y))throw new InvalidDataException("截图标注尺寸无效，原截图已保留。");
                var bounds=mark.Bounds;double margin=mark.Kind=="arrow"?Math.Max(10,mark.Width*3.5):mark.Width/2;bounds.Inflate(margin,margin);
                if(!bounds.IntersectsWith(selection))continue;
                var color=(Color)ColorConverter.ConvertFromString(mark.Color);
                var converted=new ExternalScrollAnnotation {
                    kind=mark.Kind,x1=mark.Start.X*scaleX-cropX,y1=mark.Start.Y*scaleY-cropY,x2=mark.End.X*scaleX-cropX,y2=mark.End.Y*scaleY-cropY,
                    color=color.A==255?"#"+color.R.ToString("X2")+color.G.ToString("X2")+color.B.ToString("X2"):color.ToString(CultureInfo.InvariantCulture),
                    strokeWidth=mark.Width*(scaleX+scaleY)/2,fontSize=mark.TextFontSize*scaleY,text=mark.Text??"",textWrapWidth=mark.TextWrapWidth*scaleX
                };
                if(mark.Kind=="text"||mark.Kind=="mosaic"){
                    var content=mark.Bounds;converted.x1=content.Left*scaleX-cropX;converted.y1=content.Top*scaleY-cropY;converted.x2=content.Right*scaleX-cropX;converted.y2=content.Bottom*scaleY-cropY;
                }
                if(mark.Points!=null)foreach(var point in mark.Points){if(!Finite(point.X)||!Finite(point.Y))throw new InvalidDataException("截图画笔坐标无效。");converted.points.Add(new ExternalScrollPoint{x=point.X*scaleX-cropX,y=point.Y*scaleY-cropY});}
                if(mark.Kind=="mosaic"){
                    if(mark.Mosaic==null)throw new InvalidDataException("马赛克图像暂不可用，原截图已保留。");
                    converted.Mosaic=mark.Mosaic.IsFrozen?mark.Mosaic:mark.Mosaic.Clone();if(!converted.Mosaic.IsFrozen)converted.Mosaic.Freeze();converted.mosaicPath="mark-"+result.Count.ToString(CultureInfo.InvariantCulture)+".png";
                }
                result.Add(converted);
            }
            return result;
        }
        internal string SerializeRequest(){
            return new JavaScriptSerializer {MaxJsonLength=4*1024*1024}.Serialize(new {
                schemaVersion=1,sessionId=SessionId,outputDirectory=OutputDirectory,
                transactionalOutputs=true,deferredStart=true,
                features=new {rectangle=captureFeatures.Rectangle,ellipse=captureFeatures.Ellipse,lines=captureFeatures.Lines,pen=captureFeatures.Pen,text=captureFeatures.Text,mosaic=captureFeatures.Mosaic,pin=captureFeatures.Pin,collect=captureFeatures.Collect,scrolling=captureFeatures.Scrolling},
                parentProcessId=Process.GetCurrentProcess().Id,
                region=new{x=region.X,y=region.Y,width=region.Width,height=region.Height},
                monitor=new{x=monitor.X,y=monitor.Y,width=monitor.Width,height=monitor.Height,scaleFactor=scaleFactor},
                initialAnnotations=annotations,annotationsClip=new{x=0,y=0,width=region.Width,height=region.Height}
            });
        }
        internal static string LaunchArguments(string requestPath){
            if(string.IsNullOrWhiteSpace(requestPath)||!Path.IsPathRooted(requestPath)||requestPath.IndexOf('"')>=0||requestPath.IndexOf('\r')>=0||requestPath.IndexOf('\n')>=0)throw new ArgumentException("截图请求路径无效。");
            return "--nook-request \""+Path.GetFullPath(requestPath)+"\"";
        }
        void BeginSession(){
            if(!captureFeatures.Scrolling)throw new InvalidOperationException("滚动截屏已在设置中关闭。");
            lock(gate){if(disposed)throw new ObjectDisposedException("ExternalScrollCaptureSession");if(started)throw new InvalidOperationException("滚动截屏已启动。");started=true;}
        }
        internal bool AllowsOutput(string action){return action=="copy"||action=="save"||(action=="pin"&&captureFeatures.Pin)||(action=="collect"&&captureFeatures.Collect);}
        ExternalScrollResult AcceptResult(string json){
            lock(gate){
                if(disposed)throw new OperationCanceledException();acceptedResult=ParseResult(json,SessionId,OutputDirectory);
                if(acceptedResult.status=="ok"&&!AllowsOutput(acceptedResult.action))throw new InvalidDataException("此截图输出功能已关闭。");
                if(acceptedResult.status=="ok"&&(acceptedResult.exportId>0||processedExports>0)){
                    if(completedExportId<1||acceptedResult.exportId!=completedExportId||acceptedResult.action!=completedExportAction)throw new InvalidDataException("截图输出未得到成功确认。");
                    verifiedImageResult=acceptedResult;
                }
                return acceptedResult;
            }
        }
        // Pure transport seam: fake-helper tests exercise the same start,
        // disposed/late-result gate and result parser without starting a process
        // or creating files, native handles, clipboard or desktop resources.
        internal async Task<ExternalScrollResult> RunWithTransport(Func<string,Task<string>> transport){
            if(transport==null)throw new ArgumentNullException("transport");BeginSession();
            return AcceptResult(await transport(SerializeRequest()));
        }
        internal Task<ExternalScrollResult> RunAsync(Func<ExternalScrollResult,Task<ExternalScrollExportReply>> export=null,Func<Task<bool>> handoff=null){
            if(handoff==null)throw new ArgumentNullException("handoff");
            BeginSession();
            return Task.Run(async ()=>{
                bool ready=false;
                var startup=Stopwatch.StartNew();
                try{
                    if(!File.Exists(HelperPath))throw new FileNotFoundException("滚动截图组件尚未安装，请更新 Nook 后重试。",HelperPath);
                    // Older helpers would ignore deferredStart and capture the
                    // still-visible frozen WPF overlay. Fail before launch.
                    var component=Path.Combine(Path.GetDirectoryName(HelperPath),"component.json");
                    RequireRegularDirectory(Path.GetDirectoryName(HelperPath));RequireRegularFile(component,65536);
                    ValidateDeferredStartComponent(File.ReadAllText(component,Encoding.UTF8),RequiresFeaturePolicy);
                    bool warmStarted=await AdoptWarmProcessAsync((int)Math.Max(1,StartupTimeoutMilliseconds-startup.ElapsedMilliseconds));
                    IScrollCaptureChild child;lock(gate)child=process;
                    if(warmStarted){
                        try{
                            RequireRegularDirectory(OutputDirectory);RequireRegularFile(Path.Combine(OutputDirectory,"warm.json"),4096);
                            var warmReady=Path.Combine(OutputDirectory,"warm.ready.json");
                            if(File.Exists(warmReady)){RequireRegularFile(warmReady,4096);ScrollCaptureWarmLease.ValidateReady(File.ReadAllText(warmReady,Encoding.UTF8),SessionId);}
                        }catch{RetireWarmForFallback(child);child=null;warmStarted=false;}
                    }
                PrepareRequest:
                    lock(gate){if(disposed)throw new OperationCanceledException();}
                    // A stale GUID directory is never reused, including a reparse
                    // point created by another process before launch.
                    if(!warmStarted){
                        if(Directory.Exists(OutputDirectory)||File.Exists(OutputDirectory))throw new IOException("滚动截图会话目录已存在，请重试。");
                        Directory.CreateDirectory(OutputDirectory);
                    }
                    RequireRegularDirectory(OutputDirectory);
                    for(int i=0;i<annotations.Count;i++)if(annotations[i].Mosaic!=null){
                        string name="mark-"+i.ToString(CultureInfo.InvariantCulture)+".png";
                        if(annotations[i].mosaicPath!=name)throw new InvalidDataException("马赛克文件名无效。");
                        ImageFiles.Write(annotations[i].Mosaic,Path.Combine(OutputDirectory,name));
                    }
                    PublishRequest();
                    ScrollCaptureStartupTiming.Mark(startupTiming,ScrollCaptureStartupStage.RequestPublished);
                    if(!warmStarted){
                        ScrollCaptureStartupTiming.Mark(startupTiming,ScrollCaptureStartupStage.ColdLaunch);
                        child=ScrollCaptureChild.Launch(new ProcessStartInfo {FileName=HelperPath,Arguments=LaunchArguments(RequestPath),WorkingDirectory=Path.GetDirectoryName(HelperPath),UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden});
                        lock(gate){process=child;if(disposed)throw new OperationCanceledException();}
                    }
                    var resultFile=Path.Combine(OutputDirectory,"result.json");var readyFile=Path.Combine(OutputDirectory,"ready.json");var preparedFile=Path.Combine(OutputDirectory,"prepared.json");
                    bool resultCommitted=false;
                    while(!child.WaitForExit(ready?100:30)){
                        lock(gate){if(disposed)throw new OperationCanceledException();}
                        if(File.Exists(resultFile)){
                            // A committed result is terminal. A stuck Chromium
                            // teardown must not keep a finished screenshot lost
                            // behind the hidden original overlay indefinitely.
                            resultCommitted=true;if(!child.WaitForExit(3000))StopChild(child);break;
                        }
                        if(StartupExpired(ready,false,startup.ElapsedMilliseconds))throw new TimeoutException("滚动截图组件启动超时，原截图和标注已保留。请重试。");
                        if(!startReleased&&File.Exists(preparedFile)){
                            RequireRegularDirectory(OutputDirectory);RequireRegularFile(preparedFile,4096);
                            ScrollCaptureStartupTiming.Mark(startupTiming,ScrollCaptureStartupStage.Prepared);
                            try{await ReleasePreparedStartAsync(File.ReadAllText(preparedFile,Encoding.UTF8),handoff,(int)(StartupTimeoutMilliseconds-startup.ElapsedMilliseconds));}
                            catch{
                                // Esc in the helper can commit cancellation
                                // while WPF is settling its hidden overlay.
                                // Read that terminal reply instead of treating
                                // a deliberate cancellation as a startup error.
                                if(IsDisposed||!File.Exists(resultFile))throw;
                                resultCommitted=true;if(!child.WaitForExit(3000))StopChild(child);break;
                            }
                        }
                        if(StartupExpired(ready,false,startup.ElapsedMilliseconds))throw new TimeoutException("滚动截图组件启动超时，原截图和标注已保留。请重试。");
                        if(!ready&&File.Exists(readyFile)){
                            RequireRegularDirectory(OutputDirectory);RequireRegularFile(readyFile,4096);
                            AcceptReady(File.ReadAllText(readyFile,Encoding.UTF8));ready=true;
                            ScrollCaptureStartupTiming.Mark(startupTiming,ScrollCaptureStartupStage.Ready);
                        }
                        var exportFile=Path.Combine(OutputDirectory,"export-"+(processedExports+1).ToString(CultureInfo.InvariantCulture)+".json");
                        if(ready&&File.Exists(exportFile)){
                            RequireRegularDirectory(OutputDirectory);RequireRegularFile(exportFile,MaximumResultCharacters*4L);
                            var output=ParseExport(File.ReadAllText(exportFile,Encoding.UTF8),SessionId,OutputDirectory,processedExports+1);
                            var reply=await ProcessExport(output,export);
                            if(!File.Exists(resultFile))WriteExportReply(output,reply);
                        }
                        if(StartupExpired(ready,false,startup.ElapsedMilliseconds))throw new TimeoutException("滚动截图组件启动超时，原截图和标注已保留。请重试。");
                    }
                    lock(gate){if(disposed)throw new OperationCanceledException();}
                    // A TTL/failed warm renderer may exit between claiming and
                    // reading request.json. Before ANY prepared/UI handoff,
                    // retire it completely, then use one fresh cold session.
                    if(warmStarted&&!startHandoffPending&&!startReleased&&!File.Exists(resultFile)){
                        RetireWarmForFallback(child);child=null;warmStarted=false;goto PrepareRequest;
                    }
                    RequireRegularDirectory(OutputDirectory);
                    RequireRegularFile(resultFile,MaximumResultCharacters*4L);
                    var result=AcceptResult(File.ReadAllText(resultFile,Encoding.UTF8));
                    if(result.status=="ok"&&!ready){
                        if(StartupExpired(false,false,startup.ElapsedMilliseconds))throw new TimeoutException("滚动截图组件未及时就绪，原截图已保留。");
                        RequireRegularFile(readyFile,4096);AcceptReady(File.ReadAllText(readyFile,Encoding.UTF8));
                    }
                    if(!resultCommitted&&child.ExitCode!=0&&result.status=="ok")throw new IOException("滚动截图组件异常退出，原截图已保留。");
                    return result;
                }catch{
                    ScrollCaptureStartupTiming.Mark(startupTiming,ScrollCaptureStartupStage.Failed);
                    // Recheck a user cancellation racing the deadline BEFORE
                    // StopChild creates its own cancellation marker. Otherwise
                    // one Esc at that boundary could restore the old overlay.
                    bool hasChild;lock(gate)hasChild=process!=null;
                    var cancelled=hasChild&&!ready?ReadStartupCancellation():null;
                    if(cancelled!=null)return cancelled;
                    throw;
                }finally{
                    IScrollCaptureChild finished;lock(gate){finished=process;process=null;}if(finished!=null){StopChild(finished);finished.Dispose();}
                    if(warmLease!=null)warmLease.Dispose();
                }
            });
        }
        internal void PublishRequest(){
            RequireRegularDirectory(OutputDirectory);
            string temp=RequestPath+".tmp";
            if(File.Exists(RequestPath)||Directory.Exists(RequestPath)||File.Exists(temp)||Directory.Exists(temp))throw new IOException("滚动截图请求已存在。");
            using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new StreamWriter(stream,new UTF8Encoding(false))){writer.Write(SerializeRequest());writer.Flush();stream.Flush(true);}
            File.Move(temp,RequestPath);
        }
        internal void RetireWarmForFallback(IScrollCaptureChild child){
            lock(gate){if(disposed)throw new OperationCanceledException();if(!usedWarmLease||startHandoffPending||startReleased)throw new InvalidOperationException("截图已交接，不能重新采集。");}
            StopChild(child);
            if(!child.HasExited)throw new IOException("截图预备进程尚未退出，不能同时启动另一会话。");
            lock(gate){if(ReferenceEquals(process,child))process=null;}
            child.Dispose();CleanupOwnedDirectory(OutputDirectory,OwnedFileNames());
            lock(gate){if(disposed)throw new OperationCanceledException();ResetColdIdentity();}
        }
        internal ExternalScrollResult ReadStartupCancellation(){
            lock(gate){if(!started||disposed)return null;}
            try{
                var path=Path.Combine(OutputDirectory,"result.json");
                RequireRegularDirectory(OutputDirectory);RequireRegularFile(path,MaximumResultCharacters*4L);
                var json=File.ReadAllText(path,Encoding.UTF8);var result=ParseResult(json,SessionId,OutputDirectory);
                return result.status=="cancelled"?AcceptResult(json):null;
            }catch(InvalidDataException){return null;}catch(IOException){return null;}catch(UnauthorizedAccessException){return null;}catch(ArgumentException){return null;}catch(OperationCanceledException){return null;}
        }
        internal bool RequiresFeaturePolicy {get{return !captureFeatures.Rectangle||!captureFeatures.Ellipse||!captureFeatures.Lines||!captureFeatures.Pen||!captureFeatures.Text||!captureFeatures.Mosaic||!captureFeatures.Pin||!captureFeatures.Collect;}}
        internal static void ValidateDeferredStartComponent(string json,bool requireFeaturePolicy=false){
            Dictionary<string,object> value;
            try{if(string.IsNullOrWhiteSpace(json)||json.Length>65536)throw new InvalidDataException();value=new JavaScriptSerializer {MaxJsonLength=65536,RecursionLimit=16}.Deserialize<Dictionary<string,object>>(json);}
            catch(Exception error){throw new InvalidDataException("滚动截图组件信息无效，请完整更新 Nook。",error);}
            object schema,deferred;
            if(value==null||!value.TryGetValue("schemaVersion",out schema)||!(schema is int)||(int)schema!=1||!value.TryGetValue("deferredStart",out deferred)||!(deferred is bool)||!(bool)deferred)
                throw new InvalidDataException("滚动截图组件版本不匹配，请完整更新 Nook 后重试。");
            object policy;
            if(requireFeaturePolicy&&(!value.TryGetValue("captureFeaturePolicy",out policy)||!(policy is bool)||!(bool)policy))throw new InvalidDataException("滚动截图组件不支持当前功能选配，请完整更新 Nook 后重试。");
        }
        internal static void ValidatePrepared(string json,string expectedSessionId){
            Dictionary<string,object> value;
            try{if(string.IsNullOrWhiteSpace(json)||json.Length>4096)throw new InvalidDataException();value=new JavaScriptSerializer {MaxJsonLength=4096,RecursionLimit=4}.Deserialize<Dictionary<string,object>>(json);}
            catch(Exception error){throw new InvalidDataException("滚动截图准备信号无效。",error);}
            object schema,id,prepared;
            if(value==null||value.Count!=3||!value.TryGetValue("schemaVersion",out schema)||!(schema is int)||(int)schema!=1||!value.TryGetValue("sessionId",out id)||!(id is string)||(string)id!=expectedSessionId||!value.TryGetValue("prepared",out prepared)||!(prepared is bool)||!(bool)prepared)
                throw new InvalidDataException("滚动截图准备信号不属于当前会话。");
        }
        // This is the real file-publication boundary, also exercised with
        // synthetic session directories. Never hold the session lock while
        // waiting for WPF; Esc must be able to cancel a queued UI handoff.
        internal async Task ReleasePreparedStartAsync(string json,Func<Task<bool>> handoff,int timeoutMilliseconds=StartupTimeoutMilliseconds){
            var handoffWatch=Stopwatch.StartNew();
            ValidatePrepared(json,SessionId);
            if(handoff==null)throw new ArgumentNullException("handoff");
            if(timeoutMilliseconds<=0)throw new TimeoutException("滚动截图准备超时，原截图已保留。");
            string target=Path.Combine(OutputDirectory,"start.request"),temp=target+".tmp";
            lock(gate){
                if(disposed)throw new OperationCanceledException();
                if(startHandoffPending||startReleased)throw new InvalidDataException("滚动截图准备信号已处理。");
                RequireRegularDirectory(OutputDirectory);
                RequirePendingStart();
                if(File.Exists(target)||Directory.Exists(target)||File.Exists(temp)||Directory.Exists(temp))throw new InvalidDataException("滚动截图启动信号已存在。");
                startHandoffPending=true;
            }
            using(var timeout=new CancellationTokenSource())try{
                ScrollCaptureStartupTiming.Mark(startupTiming,ScrollCaptureStartupStage.Handoff);
                var pending=handoff();if(pending==null)throw new InvalidDataException("截图窗口未确认交接。");
                var winner=await Task.WhenAny(pending,lifetimeEnded.Task,Task.Delay(Math.Min(timeoutMilliseconds,StartupTimeoutMilliseconds),timeout.Token)).ConfigureAwait(false);
                if(winner!=pending){
                    // A late UI callback still guards session identity itself;
                    // observe any late fault without ever publishing start.
                    ObserveLateHandoffFault(pending);
                    if(IsDisposed)throw new OperationCanceledException();
                    throw new TimeoutException("滚动截图窗口交接超时，原截图已保留。");
                }
                if(!await pending.ConfigureAwait(false))throw new OperationCanceledException();
                lock(gate){
                    if(disposed)throw new OperationCanceledException();
                    if(handoffWatch.ElapsedMilliseconds>=Math.Min(timeoutMilliseconds,StartupTimeoutMilliseconds))throw new TimeoutException("滚动截图窗口交接超时，原截图已保留。");
                    RequireRegularDirectory(OutputDirectory);
                    RequirePendingStart();
                    var start=new JavaScriptSerializer().Serialize(new{schemaVersion=1,sessionId=SessionId,start=true});
                    using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new StreamWriter(stream,new UTF8Encoding(false))){writer.Write(start);writer.Flush();stream.Flush(true);}
                    File.Move(temp,target);startReleased=true;
                    ScrollCaptureStartupTiming.Mark(startupTiming,ScrollCaptureStartupStage.StartReleased);
                }
            }catch{lock(gate)startHandoffAbandoned=true;throw;}
            finally{timeout.Cancel();}
        }
        static void ObserveLateHandoffFault(Task<bool> pending){
            // Deliberately detached: waiting here would defeat the timeout or
            // cancellation. This continuation only observes a late exception.
            pending.ContinueWith(task=>{var ignored=task.Exception;},TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously);
        }
        void RequirePendingStart(){
            if(File.Exists(Path.Combine(OutputDirectory,"cancel.request")))throw new OperationCanceledException();
            // Check both before and after the UI callback. A mismatched helper
            // cannot claim ready or finish while native sampling is forbidden.
            if(File.Exists(Path.Combine(OutputDirectory,"ready.json"))||File.Exists(Path.Combine(OutputDirectory,"result.json")))throw new InvalidDataException("滚动截图组件提前结束交接，请完整更新 Nook。");
        }
        internal void AcceptReady(string json){
            ValidateReady(json,SessionId);
            lock(gate){if(disposed)throw new OperationCanceledException();if(!startReleased)throw new InvalidDataException("滚动截图组件未完成交接，请完整更新 Nook。");}
        }
        internal static bool StartupExpired(bool ready,bool processExited,long elapsedMilliseconds){return !ready&&!processExited&&elapsedMilliseconds>=StartupTimeoutMilliseconds;}
        internal static void ValidateReady(string json,string expectedSessionId){
            ExternalScrollReady state;
            try{if(string.IsNullOrWhiteSpace(json)||json.Length>4096)throw new InvalidDataException();state=new JavaScriptSerializer {MaxJsonLength=4096,RecursionLimit=4}.Deserialize<ExternalScrollReady>(json);}catch(Exception error){throw new InvalidDataException("滚动截图就绪信号无效。",error);}
            if(state==null||state.schemaVersion!=1||state.sessionId!=expectedSessionId||!state.ready)throw new InvalidDataException("滚动截图就绪信号不属于当前会话。");
        }
        internal static ExternalScrollResult ParseResult(string json,string expectedSessionId,string outputDirectory){
            return ParseMessage(json,expectedSessionId,outputDirectory,0);
        }
        internal static ExternalScrollResult ParseExport(string json,string expectedSessionId,string outputDirectory,int expectedExportId){
            if(expectedExportId<1||expectedExportId>1000)throw new InvalidDataException("截图输出编号无效。");
            return ParseMessage(json,expectedSessionId,outputDirectory,expectedExportId);
        }
        static ExternalScrollResult ParseMessage(string json,string expectedSessionId,string outputDirectory,int expectedExportId){
            Guid id;if(!Guid.TryParseExact(expectedSessionId,"N",out id)||string.IsNullOrWhiteSpace(outputDirectory)||!Path.IsPathRooted(outputDirectory))throw new ArgumentException("截图会话无效。");
            if(string.IsNullOrWhiteSpace(json)||json.Length>MaximumResultCharacters)throw new InvalidDataException("滚动截图结果为空或过大。");
            ExternalScrollResult result;
            try{result=new JavaScriptSerializer {MaxJsonLength=MaximumResultCharacters,RecursionLimit=8}.Deserialize<ExternalScrollResult>(json);}catch(Exception error){throw new InvalidDataException("滚动截图结果格式无效。",error);}
            if(result==null||result.schemaVersion!=1||result.sessionId!=expectedSessionId)throw new InvalidDataException("滚动截图结果不属于当前会话，原截图已保留。");
            if(expectedExportId>0){if(result.status!="export"||result.exportId!=expectedExportId)throw new InvalidDataException("截图输出请求已失效。");}
            else if(result.status!="ok"&&result.status!="cancelled"&&result.status!="error")throw new InvalidDataException("滚动截图返回了未知状态。");
            if(result.message!=null&&result.message.Length>2048)result.message=result.message.Substring(0,2048);
            if(result.status=="ok"||result.status=="export"){
                if(result.action!="copy"&&result.action!="save"&&result.action!="pin"&&result.action!="collect")throw new InvalidDataException("滚动截图返回了未知操作。");
                // The protocol has one exact local image name. No absolute,
                // encoded, sibling or nested path from the helper is accepted.
                if(result.exportId<0||result.exportId>1000)throw new InvalidDataException("截图输出编号无效。");
                string expectedName=result.exportId>0?"export-"+result.exportId.ToString(CultureInfo.InvariantCulture)+".png":"result.png";
                if(result.imagePath!=expectedName)throw new InvalidDataException("滚动截图图像路径无效。");
                result.imagePath=Path.Combine(Path.GetFullPath(outputDirectory),expectedName);
            }else result.imagePath=null;
            return result;
        }
        internal async Task<ExternalScrollExportReply> ProcessExport(ExternalScrollResult output,Func<ExternalScrollResult,Task<ExternalScrollExportReply>> export){
            lock(gate){
                if(disposed)throw new OperationCanceledException();
                if(output==null||output.schemaVersion!=1||output.sessionId!=SessionId||output.status!="export"||output.exportId<1||output.exportId>1000||output.exportId!=processedExports+1||completedExportId>0)throw new InvalidDataException("截图输出请求已失效。");
                if((output.action!="copy"&&output.action!="save"&&output.action!="pin"&&output.action!="collect")||output.imagePath!=Path.Combine(OutputDirectory,"export-"+output.exportId.ToString(CultureInfo.InvariantCulture)+".png"))throw new InvalidDataException("截图输出内容无效。");
                processedExports=output.exportId;
            }
            ExternalScrollExportReply reply;
            if(!AllowsOutput(output.action))return ExternalScrollExportReply.Failure("此截图输出功能已关闭。");
            try{reply=export==null?ExternalScrollExportReply.Failure("此版本不支持保留编辑，请更新 Nook。"):await export(output);}
            catch(Exception error){reply=ExternalScrollExportReply.Failure(error.Message);}
            if(reply==null|| (reply.status!="ok"&&reply.status!="cancelled"&&reply.status!="error"))reply=ExternalScrollExportReply.Failure("输出没有返回有效结果，图片仍可编辑。");
            lock(gate){if(disposed)throw new OperationCanceledException();if(reply.status=="ok"){completedExportId=output.exportId;completedExportAction=output.action;}}
            return reply;
        }
        void WriteExportReply(ExternalScrollResult output,ExternalScrollExportReply reply){
            RequireRegularDirectory(OutputDirectory);
            string target=Path.Combine(OutputDirectory,"export-"+output.exportId.ToString(CultureInfo.InvariantCulture)+".reply.json");
            string temp=target+".tmp";
            var json=new JavaScriptSerializer().Serialize(new{schemaVersion=1,sessionId=SessionId,exportId=output.exportId,status=reply.status,message=(reply.message??"").Substring(0,Math.Min(1600,(reply.message??"").Length))});
            using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new StreamWriter(stream,new UTF8Encoding(false))){writer.Write(json);writer.Flush();stream.Flush(true);}
            File.Move(temp,target);
        }
        internal static void RequireRegularDirectory(string path){
            for(string current=Path.GetFullPath(path);!string.IsNullOrEmpty(current);current=Path.GetDirectoryName(current)){
                if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("截图会话不能使用重定向目录。");
                if(Path.GetPathRoot(current)==current)break;
            }
        }
        internal static void RequireRegularFile(string path,long maximum){
            if(!File.Exists(path))throw new FileNotFoundException("滚动截图未返回完整结果，原截图已保留。",path);
            if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("截图结果不能使用重定向文件。");
            long size=new FileInfo(path).Length;if(size<1||size>maximum)throw new InvalidDataException("滚动截图结果大小无效。");
        }
        internal static void ValidatePngHeader(byte[] header){
            byte[] signature={137,80,78,71,13,10,26,10};
            if(header==null||header.Length<24)throw new InvalidDataException("滚动截图图像不完整。");
            for(int i=0;i<signature.Length;i++)if(header[i]!=signature[i])throw new InvalidDataException("滚动截图不是 PNG 图像。");
            if(header[8]!=0||header[9]!=0||header[10]!=0||header[11]!=13||header[12]!=73||header[13]!=72||header[14]!=68||header[15]!=82)throw new InvalidDataException("滚动截图 PNG 头无效。");
            uint width=((uint)header[16]<<24)|((uint)header[17]<<16)|((uint)header[18]<<8)|header[19],height=((uint)header[20]<<24)|((uint)header[21]<<16)|((uint)header[22]<<8)|header[23];
            if(width<1||height<1||width>32768||height>32768||(long)width*height>MaximumImagePixels)throw new InvalidDataException("滚动截图超过安全图像尺寸，原截图已保留。");
        }
        internal BitmapSource LoadImage(ExternalScrollResult result){
            if(result!=null&&!AllowsOutput(result.action))throw new InvalidDataException("此截图输出功能已关闭。");
            string expectedName=result!=null&&result.exportId>0?"export-"+result.exportId.ToString(CultureInfo.InvariantCulture)+".png":"result.png";
            if(result==null||(result.status!="ok"&&result.status!="export")||result.sessionId!=SessionId||result.exportId<0||result.exportId>1000||result.imagePath!=Path.Combine(OutputDirectory,expectedName))throw new InvalidDataException("滚动截图图像不属于当前会话。");
            RequireRegularDirectory(OutputDirectory);RequireRegularFile(result.imagePath,256L*1024*1024);
            var header=new byte[24];using(var stream=File.OpenRead(result.imagePath)){int read=0;while(read<header.Length){int next=stream.Read(header,read,header.Length-read);if(next==0)throw new InvalidDataException("滚动截图 PNG 不完整。");read+=next;}}ValidatePngHeader(header);
            var image=Snapshot.Load(result.imagePath);
            lock(gate){if(disposed)throw new OperationCanceledException();verifiedImageResult=result;}
            return image;
        }
        internal string[] OwnedFileNames(){
            var files=new List<string>{"request.json","prepared.json","start.request","start.request.tmp","ready.json","cancel.request","result.json","result.png"};
            if(usedWarmLease){files.Add("warm.json");files.Add("warm.ready.json");}
            files.Add("request.json.tmp");files.Add("startup-timing.json");
            for(int id=1;id<=processedExports;id++){string stem="export-"+id.ToString(CultureInfo.InvariantCulture);files.Add(stem+".json");files.Add(stem+".png");files.Add(stem+".reply.json");}
            for(int i=0;i<annotations.Count;i++)if(annotations[i].Mosaic!=null)files.Add("mark-"+i.ToString(CultureInfo.InvariantCulture)+".png");
            return files.ToArray();
        }
        internal void CleanupCompletedFiles(ExternalScrollResult result=null,bool successfulExport=false){
            // Failure/recovery sessions stay on disk. The caller must confirm
            // the verified image was exported, or use an accepted cancellation.
            lock(gate){
                if(result==null||result!=acceptedResult||process!=null)return;
                if(result.status!="cancelled"&&(result.status!="ok"||!successfulExport||verifiedImageResult!=result))return;
            }
            // Unknown siblings are left alone. Only the exact generated
            // host-profile tree is eligible, after checking its entire tree.
            CleanupOwnedDirectory(OutputDirectory,OwnedFileNames());
        }
        internal static void CleanupOwnedDirectory(string outputDirectory,IEnumerable<string> names){
            try{
                string root=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"Nook","scroll-capture"));
                string full=Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);Guid id;
                if(!string.Equals(Path.GetDirectoryName(full),root,StringComparison.OrdinalIgnoreCase)||!Guid.TryParseExact(Path.GetFileName(full),"N",out id))throw new IOException("截图清理目录无效。");
                if(!Directory.Exists(full))return;RequireRegularDirectory(full);
                DeleteOwnedHostProfile(full);
                foreach(string name in names){
                    if(string.IsNullOrEmpty(name)||Path.GetFileName(name)!=name||name.IndexOf(':')>=0||name.IndexOf('*')>=0||name.IndexOf('?')>=0||name=="."||name=="..")throw new IOException("截图清理文件名无效。");
                    string path=Path.Combine(full,name);if(!File.Exists(path))continue;
                    if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)continue;
                    File.Delete(path);
                }
                if(Directory.GetFileSystemEntries(full).Length==0)Directory.Delete(full,false);
            }catch(IOException){}catch(UnauthorizedAccessException){}
        }
        static void DeleteOwnedHostProfile(string outputDirectory){
            string output=Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
            string expected=Path.Combine(output,"host-profile");
            string profile=Path.GetFullPath(Path.Combine(outputDirectory,"host-profile"));
            if(!string.Equals(profile,expected,StringComparison.OrdinalIgnoreCase)||!string.Equals(Path.GetDirectoryName(profile),output,StringComparison.OrdinalIgnoreCase))throw new IOException("截图临时配置目录无效。");
            if(!Directory.Exists(profile)){if(File.Exists(profile))throw new IOException("截图临时配置目录不是文件夹。");return;}
            var pending=new Stack<string>();var directories=new List<string>();var files=new List<string>();pending.Push(profile);
            string prefix=profile+Path.DirectorySeparatorChar;
            // Validate every descendant before deleting anything. Do not follow
            // junctions, links or mount points, including the profile root.
            while(pending.Count>0){
                string directory=pending.Pop();RequireRegularDirectory(directory);directories.Add(directory);
                foreach(string entry in Directory.GetFileSystemEntries(directory)){
                    string path=Path.GetFullPath(entry);
                    FileAttributes attributes=File.GetAttributes(path);
                    ValidateProfileEntry(path,directory,prefix,attributes);
                    if((attributes&FileAttributes.Directory)!=0)pending.Push(path);else files.Add(path);
                }
            }
            foreach(string file in files){
                RequireRegularDirectory(Path.GetDirectoryName(file));
                if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)throw new IOException("截图临时配置文件已改变。");
                File.Delete(file);
            }
            for(int i=directories.Count-1;i>=0;i--){RequireRegularDirectory(directories[i]);Directory.Delete(directories[i],false);}
        }
        internal static void ValidateProfileEntry(string path,string parent,string profilePrefix,FileAttributes attributes){
            if(!path.StartsWith(profilePrefix,StringComparison.OrdinalIgnoreCase)||!string.Equals(Path.GetDirectoryName(path),parent,StringComparison.OrdinalIgnoreCase))throw new IOException("截图临时配置路径越界。");
            if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("截图临时配置不能包含重定向文件。");
        }
        void RequestCancellation(){
            try{if(!Directory.Exists(OutputDirectory))return;RequireRegularDirectory(OutputDirectory);string marker=Path.Combine(OutputDirectory,"cancel.request");if(!File.Exists(marker))File.WriteAllText(marker,SessionId,new UTF8Encoding(false));}catch(IOException){}catch(UnauthorizedAccessException){}
        }
        void StopChild(IScrollCaptureChild child){
            try{
                if(child.HasExited)return;RequestCancellation();
                // The helper watches its parent PID and this owned marker. Let
                // it close Electron renderer children before the last-resort
                // kill of the exact main process started by this session.
                if(!child.WaitForExit(2000)){child.Kill();child.WaitForExit(1000);}
            }catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
        }
        public void Dispose(){
            IScrollCaptureChild child;lock(gate){if(disposed)return;disposed=true;child=process;}
            lifetimeEnded.TrySetResult(true);
            if(warmLease!=null)warmLease.Dispose();
            // Only the exact process started by this session is touched. Normal
            // Escape/cancel belongs to the helper; this is emergency teardown
            // when Nook itself ends the capture or shuts down.
            if(child!=null){RequestCancellation();Task.Run(()=>StopChild(child));}
            // Keep the controlled session files for recovery if exporting or
            // collecting fails. Never recursively remove a returned path.
        }
    }
}
