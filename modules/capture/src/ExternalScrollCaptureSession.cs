using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
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
        public double x1,y1,x2,y2,strokeWidth,fontSize;
        public List<ExternalScrollPoint> points=new List<ExternalScrollPoint>();
        [ScriptIgnore] public BitmapSource Mosaic {get;set;}
    }
    internal sealed class ExternalScrollResult {
        public int schemaVersion;
        public string sessionId,status,action,imagePath,message;
    }
    internal sealed class ExternalScrollReady { public int schemaVersion;public string sessionId;public bool ready; }

    // The helper is a packaged, fixed executable, never a command from the
    // browser or a returned result. Every launch owns a fresh GUID directory.
    // Construction/serialization are inert and can be tested without a desktop.
    internal sealed class ExternalScrollCaptureSession : IDisposable {
        const int MaximumResultCharacters=32768;
        internal const int StartupTimeoutMilliseconds=20000;
        internal const long MaximumImagePixels=64000000;
        readonly object gate=new object();
        readonly Rectangle region,monitor;
        readonly double scaleFactor;
        readonly IList<ExternalScrollAnnotation> annotations;
        Process process;
        bool started,disposed;
        ExternalScrollResult acceptedResult,verifiedImageResult;
        internal readonly string SessionId,OutputDirectory,HelperPath;
        internal bool IsDisposed {get{lock(gate)return disposed;}}
        internal bool IsStarted {get{lock(gate)return started;}}
        internal string RequestPath {get{return Path.Combine(OutputDirectory,"request.json");}}
        internal ExternalScrollCaptureSession(string appBase,Rectangle region,Rectangle monitor,double scaleFactor,IList<ExternalScrollAnnotation> annotations=null){
            if(string.IsNullOrWhiteSpace(appBase)||!Path.IsPathRooted(appBase))throw new ArgumentException("截图组件目录无效。");
            if(monitor.Width<=0||monitor.Height<=0||region.Width<32||region.Height<80||!monitor.Contains(region))throw new ArgumentException("滚动截屏区域至少需要 32 × 80 像素，并位于同一块屏幕内。");
            if(!Finite(scaleFactor)||scaleFactor<=0)throw new ArgumentException("截图缩放比例无效。");
            this.region=region;this.monitor=monitor;this.scaleFactor=scaleFactor;this.annotations=annotations??new List<ExternalScrollAnnotation>();
            SessionId=Guid.NewGuid().ToString("N");
            OutputDirectory=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"Nook","scroll-capture",SessionId));
            HelperPath=Path.GetFullPath(Path.Combine(appBase,"scroll-capture","Nook.ScrollCapture.exe"));
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
                    strokeWidth=mark.Width*(scaleX+scaleY)/2,fontSize=mark.TextFontSize*scaleY,text=mark.Text??""
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
            lock(gate){if(disposed)throw new ObjectDisposedException("ExternalScrollCaptureSession");if(started)throw new InvalidOperationException("滚动截屏已启动。");started=true;}
        }
        ExternalScrollResult AcceptResult(string json){
            lock(gate){if(disposed)throw new OperationCanceledException();acceptedResult=ParseResult(json,SessionId,OutputDirectory);return acceptedResult;}
        }
        // Pure transport seam: fake-helper tests exercise the same start,
        // disposed/late-result gate and result parser without starting a process
        // or creating files, native handles, clipboard or desktop resources.
        internal async Task<ExternalScrollResult> RunWithTransport(Func<string,Task<string>> transport){
            if(transport==null)throw new ArgumentNullException("transport");BeginSession();
            return AcceptResult(await transport(SerializeRequest()));
        }
        internal Task<ExternalScrollResult> RunAsync(){
            BeginSession();
            return Task.Run(()=>{
                try{
                    if(!File.Exists(HelperPath))throw new FileNotFoundException("滚动截图组件尚未安装，请更新 Nook 后重试。",HelperPath);
                    // A stale GUID directory is never reused, including a reparse
                    // point created by another process before launch.
                    if(Directory.Exists(OutputDirectory)||File.Exists(OutputDirectory))throw new IOException("滚动截图会话目录已存在，请重试。");
                    Directory.CreateDirectory(OutputDirectory);RequireRegularDirectory(OutputDirectory);
                    for(int i=0;i<annotations.Count;i++)if(annotations[i].Mosaic!=null){
                        string name="mark-"+i.ToString(CultureInfo.InvariantCulture)+".png";
                        if(annotations[i].mosaicPath!=name)throw new InvalidDataException("马赛克文件名无效。");
                        ImageFiles.Write(annotations[i].Mosaic,Path.Combine(OutputDirectory,name));
                    }
                    File.WriteAllText(RequestPath,SerializeRequest(),new UTF8Encoding(false));
                    var child=new Process {StartInfo=new ProcessStartInfo {FileName=HelperPath,Arguments=LaunchArguments(RequestPath),WorkingDirectory=Path.GetDirectoryName(HelperPath),UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden}};
                    lock(gate){if(disposed){child.Dispose();throw new OperationCanceledException();}process=child;try{if(!child.Start())throw new IOException("滚动截图组件未能启动。");}catch{process=null;child.Dispose();throw;}}
                    var resultFile=Path.Combine(OutputDirectory,"result.json");var readyFile=Path.Combine(OutputDirectory,"ready.json");
                    var startup=Stopwatch.StartNew();bool ready=false,resultCommitted=false;
                    while(!child.WaitForExit(100)){
                        lock(gate){if(disposed)throw new OperationCanceledException();}
                        if(File.Exists(resultFile)){
                            // A committed result is terminal. A stuck Chromium
                            // teardown must not keep a finished screenshot lost
                            // behind the hidden original overlay indefinitely.
                            resultCommitted=true;if(!child.WaitForExit(3000))StopChild(child);break;
                        }
                        if(!ready&&File.Exists(readyFile)){
                            RequireRegularDirectory(OutputDirectory);RequireRegularFile(readyFile,4096);
                            ValidateReady(File.ReadAllText(readyFile,Encoding.UTF8),SessionId);ready=true;
                        }
                        if(StartupExpired(ready,false,startup.ElapsedMilliseconds))throw new TimeoutException("滚动截图组件启动超时，原截图和标注已保留。请重试。");
                    }
                    lock(gate){if(disposed)throw new OperationCanceledException();}
                    RequireRegularDirectory(OutputDirectory);
                    RequireRegularFile(resultFile,MaximumResultCharacters*4L);
                    var result=AcceptResult(File.ReadAllText(resultFile,Encoding.UTF8));
                    if(!resultCommitted&&child.ExitCode!=0&&result.status=="ok")throw new IOException("滚动截图组件异常退出，原截图已保留。");
                    return result;
                }finally{
                    Process finished;lock(gate){finished=process;process=null;}if(finished!=null){StopChild(finished);finished.Dispose();}
                }
            });
        }
        internal static bool StartupExpired(bool ready,bool processExited,long elapsedMilliseconds){return !ready&&!processExited&&elapsedMilliseconds>=StartupTimeoutMilliseconds;}
        internal static void ValidateReady(string json,string expectedSessionId){
            ExternalScrollReady state;
            try{if(string.IsNullOrWhiteSpace(json)||json.Length>4096)throw new InvalidDataException();state=new JavaScriptSerializer {MaxJsonLength=4096,RecursionLimit=4}.Deserialize<ExternalScrollReady>(json);}catch(Exception error){throw new InvalidDataException("滚动截图就绪信号无效。",error);}
            if(state==null||state.schemaVersion!=1||state.sessionId!=expectedSessionId||!state.ready)throw new InvalidDataException("滚动截图就绪信号不属于当前会话。");
        }
        internal static ExternalScrollResult ParseResult(string json,string expectedSessionId,string outputDirectory){
            Guid id;if(!Guid.TryParseExact(expectedSessionId,"N",out id)||string.IsNullOrWhiteSpace(outputDirectory)||!Path.IsPathRooted(outputDirectory))throw new ArgumentException("截图会话无效。");
            if(string.IsNullOrWhiteSpace(json)||json.Length>MaximumResultCharacters)throw new InvalidDataException("滚动截图结果为空或过大。");
            ExternalScrollResult result;
            try{result=new JavaScriptSerializer {MaxJsonLength=MaximumResultCharacters,RecursionLimit=8}.Deserialize<ExternalScrollResult>(json);}catch(Exception error){throw new InvalidDataException("滚动截图结果格式无效。",error);}
            if(result==null||result.schemaVersion!=1||result.sessionId!=expectedSessionId)throw new InvalidDataException("滚动截图结果不属于当前会话，原截图已保留。");
            if(result.status!="ok"&&result.status!="cancelled"&&result.status!="error")throw new InvalidDataException("滚动截图返回了未知状态。");
            if(result.message!=null&&result.message.Length>2048)result.message=result.message.Substring(0,2048);
            if(result.status=="ok"){
                if(result.action!="copy"&&result.action!="save"&&result.action!="pin"&&result.action!="collect")throw new InvalidDataException("滚动截图返回了未知操作。");
                // The protocol has one exact local image name. No absolute,
                // encoded, sibling or nested path from the helper is accepted.
                if(result.imagePath!="result.png")throw new InvalidDataException("滚动截图图像路径无效。");
                result.imagePath=Path.Combine(Path.GetFullPath(outputDirectory),"result.png");
            }else result.imagePath=null;
            return result;
        }
        static void RequireRegularDirectory(string path){
            for(string current=Path.GetFullPath(path);!string.IsNullOrEmpty(current);current=Path.GetDirectoryName(current)){
                if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("截图会话不能使用重定向目录。");
                if(Path.GetPathRoot(current)==current)break;
            }
        }
        static void RequireRegularFile(string path,long maximum){
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
            if(result==null||result.status!="ok"||result.sessionId!=SessionId||result.imagePath!=Path.Combine(OutputDirectory,"result.png"))throw new InvalidDataException("滚动截图图像不属于当前会话。");
            RequireRegularDirectory(OutputDirectory);RequireRegularFile(result.imagePath,256L*1024*1024);
            var header=new byte[24];using(var stream=File.OpenRead(result.imagePath)){int read=0;while(read<header.Length){int next=stream.Read(header,read,header.Length-read);if(next==0)throw new InvalidDataException("滚动截图 PNG 不完整。");read+=next;}}ValidatePngHeader(header);
            var image=Snapshot.Load(result.imagePath);
            lock(gate){if(disposed)throw new OperationCanceledException();verifiedImageResult=result;}
            return image;
        }
        internal string[] OwnedFileNames(){
            var files=new List<string>{"request.json","ready.json","cancel.request","result.json","result.png"};
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
            try{
                if(!Directory.Exists(OutputDirectory))return;RequireRegularDirectory(OutputDirectory);
                DeleteOwnedHostProfile();
                foreach(string name in OwnedFileNames()){
                    string path=Path.Combine(OutputDirectory,name);if(!File.Exists(path))continue;
                    if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)continue;
                    File.Delete(path);
                }
                if(Directory.GetFileSystemEntries(OutputDirectory).Length==0)Directory.Delete(OutputDirectory,false);
            }catch(IOException){}catch(UnauthorizedAccessException){}
        }
        void DeleteOwnedHostProfile(){
            string output=Path.GetFullPath(OutputDirectory).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
            string expected=Path.Combine(output,"host-profile");
            string profile=Path.GetFullPath(Path.Combine(OutputDirectory,"host-profile"));
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
        void StopChild(Process child){
            try{
                if(child.HasExited)return;RequestCancellation();
                // The helper watches its parent PID and this owned marker. Let
                // it close Electron renderer children before the last-resort
                // kill of the exact main process started by this session.
                if(!child.WaitForExit(2000)){child.Kill();child.WaitForExit(1000);}
            }catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
        }
        public void Dispose(){
            Process child;lock(gate){if(disposed)return;disposed=true;child=process;}
            // Only the exact process started by this session is touched. Normal
            // Escape/cancel belongs to the helper; this is emergency teardown
            // when Nook itself ends the capture or shuts down.
            if(child!=null){RequestCancellation();Task.Run(()=>StopChild(child));}
            // Keep the controlled session files for recovery if exporting or
            // collecting fails. Never recursively remove a returned path.
        }
    }
}
