using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;
using Rectangle = System.Drawing.Rectangle;

// Pure fake transport, generated pixels, serialization, geometry and generated
// GUID-owned filesystem sandboxes only. Existing real sessions are never read.
// Never call RunAsync: no helper process, native windows, screen reads, input,
// hooks, hotkeys, clipboard, real collection or real settings are involved.
static class ExternalScrollCaptureTests {
#if EXTERNAL_SCROLL_TEST_RUNNER
    [STAThread]
    static int Main(){
        int count=0;
        try{Run((ok,name)=>{if(!ok)throw new Exception(name);count++;Console.WriteLine("PASS "+name);});Console.WriteLine("PASS "+count+" external scroll background checks");return 0;}
        catch(Exception error){Console.Error.WriteLine("FAIL after "+count+" checks: "+error);return 1;}
    }
#endif
    static readonly Rectangle Monitor=new Rectangle(-2560,-400,2560,1440),Region=new Rectangle(-2410,-250,800,600);
    static ExternalScrollCaptureSession Session(){return new ExternalScrollCaptureSession(Path.GetFullPath(@"C:\Nook test"),Region,Monitor,1.5);}
    static bool Reject(Action action){try{action();return false;}catch(ArgumentException){return true;}catch(InvalidDataException){return true;}}
    static Dictionary<string,object> Json(string text){return new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(text);}
    static string Result(string id,string status="ok",string action="copy",string path="result.png",int version=1){return new JavaScriptSerializer().Serialize(new {schemaVersion=version,sessionId=id,status=status,action=action,imagePath=path});}
    static byte[] Header(uint width,uint height){
        var bytes=new byte[24];new byte[]{137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82}.CopyTo(bytes,0);
        for(int i=0;i<4;i++){bytes[16+i]=(byte)(width>>(24-i*8));bytes[20+i]=(byte)(height>>(24-i*8));}return bytes;
    }
    internal static void Run(Action<bool,string> check){Request(check);Results(check);Annotations(check);Headers(check);Readiness(check);Lifetime(check);Cleanup(check);}
    static void Request(Action<bool,string> check){
        using(var first=Session())using(var next=Session()){
            var request=Json(first.SerializeRequest());var region=(Dictionary<string,object>)request["region"];var monitor=(Dictionary<string,object>)request["monitor"];var clip=(Dictionary<string,object>)request["annotationsClip"];
            Guid id;
            check(Guid.TryParseExact(first.SessionId,"N",out id)&&first.SessionId.Length==32&&first.SessionId!=next.SessionId,"every external request has a fresh 32-hex session identity");
            check(Convert.ToInt32(request["schemaVersion"])==1&&(string)request["sessionId"]==first.SessionId&&(string)request["outputDirectory"]==first.OutputDirectory&&Path.IsPathRooted(first.OutputDirectory),"request schema and absolute output directory belong to the current GUID");
            check(Convert.ToInt32(request["parentProcessId"])==System.Diagnostics.Process.GetCurrentProcess().Id,"helper receives exact parent identity to detect Nook exit without touching unrelated processes");
            check(Convert.ToInt32(region["x"])==-2410&&Convert.ToInt32(region["y"])==-250&&Convert.ToInt32(region["width"])==800&&Convert.ToInt32(region["height"])==600,"request preserves negative-origin physical region pixels without DIP conversion");
            check(Convert.ToInt32(monitor["x"])==-2560&&Convert.ToInt32(monitor["y"])==-400&&Convert.ToInt32(monitor["width"])==2560&&Convert.ToDouble(monitor["scaleFactor"])==1.5,"request preserves mixed-DPI monitor bounds and scale explicitly");
            check(Convert.ToInt32(clip["x"])==0&&Convert.ToInt32(clip["y"])==0&&Convert.ToInt32(clip["width"])==800&&Convert.ToInt32(clip["height"])==600,"annotations clip is relative to the first captured region");
            check(first.HelperPath==Path.GetFullPath(@"C:\Nook test\scroll-capture\Nook.ScrollCapture.exe")&&!first.IsStarted&&!first.IsDisposed&&!Directory.Exists(first.OutputDirectory),"unstarted helper locator is a fixed packaged path and acquires no process or session directory");
            check(ExternalScrollCaptureSession.LaunchArguments(@"C:\test folder\request.json")=="--nook-request \"C:\\test folder\\request.json\"","request argument is quoted as data and does not involve a shell");
            foreach(var bad in new[]{"request.json",@"C:\test\bad\"+"\".json","C:\\test\r\nrequest.json".Replace("\\r\\n","\r\n")})check(Reject(()=>ExternalScrollCaptureSession.LaunchArguments(bad)),"helper command rejects a relative or injectable request path");
        }
        foreach(double scale in new[]{0,-1,double.NaN,double.PositiveInfinity})check(Reject(()=>new ExternalScrollCaptureSession(@"C:\Nook",Region,Monitor,scale)),"helper constructor rejects invalid monitor scale "+scale);
        foreach(var crop in new[]{new Rectangle(-2410,-250,31,600),new Rectangle(-2410,-250,800,79),new Rectangle(-2700,-250,800,600)})check(Reject(()=>new ExternalScrollCaptureSession(@"C:\Nook",crop,Monitor,1)),"helper constructor rejects undersized or cross-monitor selection");
        for(int mask=0;mask<16;mask++)check(CaptureWindow.AcceptCaptureWheel((mask&1)!=0,(mask&2)!=0,(mask&4)!=0,(mask&8)!=0)==(mask==0),"wheel entry blocks closed, collecting, suspended and external session states: "+mask);
    }
    static void Results(Action<bool,string> check){
        using(var session=Session()){
            foreach(var action in new[]{"copy","save","pin","collect"}){
                var parsed=ExternalScrollCaptureSession.ParseResult(Result(session.SessionId,"ok",action),session.SessionId,session.OutputDirectory);
                check(parsed.status=="ok"&&parsed.action==action&&parsed.imagePath==Path.Combine(session.OutputDirectory,"result.png"),"only the current session's fixed local PNG is resolved for "+action);
            }
            foreach(var path in new[]{"../result.png","..\\result.png","folder/result.png","folder\\result.png",@"C:\result.png","/result.png","file:///C:/result.png","result.png:stream","RESULT.PNG","%2e%2e/result.png",""})check(Reject(()=>ExternalScrollCaptureSession.ParseResult(Result(session.SessionId,"ok","copy",path),session.SessionId,session.OutputDirectory)),"returned PNG path cannot escape or reinterpret the session: "+path);
            foreach(var status in new[]{"cancelled","error"}){
                var parsed=ExternalScrollCaptureSession.ParseResult(Result(session.SessionId,status,"unknown",@"C:\untrusted.png"),session.SessionId,session.OutputDirectory);
                check(parsed.status==status&&parsed.imagePath==null,"cancel/error never resolves or reads a returned image path: "+status);
            }
            check(Reject(()=>ExternalScrollCaptureSession.ParseResult(Result(Guid.NewGuid().ToString("N")),session.SessionId,session.OutputDirectory)),"stale completion from a previous GUID is rejected");
            check(Reject(()=>ExternalScrollCaptureSession.ParseResult(Result(session.SessionId,version:2),session.SessionId,session.OutputDirectory)),"unknown result schema is rejected");
            check(Reject(()=>ExternalScrollCaptureSession.ParseResult(Result(session.SessionId,"success"),session.SessionId,session.OutputDirectory)),"unknown result status is rejected");
            check(Reject(()=>ExternalScrollCaptureSession.ParseResult(Result(session.SessionId,"ok","open-program"),session.SessionId,session.OutputDirectory)),"result cannot request arbitrary execution");
            foreach(var text in new[]{"", "null", "{",new string('x',32769)})check(Reject(()=>ExternalScrollCaptureSession.ParseResult(text,session.SessionId,session.OutputDirectory)),"empty, malformed and oversized completion JSON are rejected");
            var json=Result(session.SessionId).TrimEnd('}')+",\"message\":\""+new string('x',3000)+"\"}";
            check(ExternalScrollCaptureSession.ParseResult(json,session.SessionId,session.OutputDirectory).message.Length==2048,"helper error text has a bounded display size");
        }
    }
    static void Annotations(Action<bool,string> check){
        var line=new Annotation{Kind="line",Start=new Point(20,30),End=new Point(100,70),Width=4,Color="#123456"};
        var edge=new Annotation{Kind="line",Start=new Point(8,20),End=new Point(8,50),Width=6};
        var hidden=new Annotation{Kind="rect",Start=new Point(300,300),End=new Point(400,400)};
        var text=new Annotation{Kind="text",Start=new Point(30,40),Width=5,Text="small\nlarge",Color="#80123456"};
        var pen=new Annotation{Kind="pen",Start=new Point(20,20),End=new Point(60,60),Points=new List<Point>{new Point(20,20),new Point(35,30),new Point(60,60)}};
        var pixels=BitmapSource.Create(2,2,96,96,PixelFormats.Bgra32,null,new byte[16],8);pixels.Freeze();
        var mosaic=new Annotation{Kind="mosaic",Start=new Point(30,50),End=new Point(50,70),Mosaic=pixels};
        var marks=new[]{line,edge,hidden,text,pen,mosaic};var selection=new Rect(10,10,130,100);
        var result=ExternalScrollCaptureSession.ConvertAnnotations(marks,selection,1.5,2,15,20);
        check(result.Count==5&&result[0].kind=="line"&&result[1].kind=="line"&&result[2].kind=="text","conversion retains order and edge-touching strokes but excludes fully outside marks");
        check(result[0].x1==15&&result[0].y1==40&&result[0].x2==135&&result[0].y2==120&&result[0].strokeWidth==7&&result[0].color=="#123456","annotation coordinates subtract the physical crop and scale independent X/Y axes");
        check(result[2].fontSize==50&&result[2].text=="small\nlarge"&&result[2].color=="#80123456"&&result[2].x2>result[2].x1,"multiline text retains actual font size, measured bounds and WPF alpha color");
        check(result[3].points.Count==3&&result[3].points[1].x==37.5&&result[3].points[1].y==40,"freehand points remain editable physical coordinates");
        check(result[4].mosaicPath=="mark-4.png"&&ReferenceEquals(result[4].Mosaic,pixels)&&result[4].x2-result[4].x1==30,"mosaic retains controlled relative sidecar and physical image geometry");
        check(line.Start==new Point(20,30)&&line.Width==4&&pen.Points[1]==new Point(35,30)&&ReferenceEquals(mosaic.Mosaic,pixels),"conversion does not mutate existing annotations, points or source mosaic");
        using(var session=new ExternalScrollCaptureSession(@"C:\Nook",Region,Monitor,1.5,result)){
            string json=session.SerializeRequest();check(json.Contains("\"initialAnnotations\"")&&json.Contains("mark-4.png")&&!json.Contains("PixelWidth")&&!json.Contains("\"Mosaic\""),"request includes editable annotations without serializing bitmap objects");
            check(session.OwnedFileNames().SequenceEqual(new[]{"request.json","ready.json","cancel.request","result.json","result.png","mark-4.png"}),"cleanup names include only protocol files and this session's generated mosaic sidecar, never returned paths or wildcards");
        }
        foreach(string kind in new[]{"rect","ellipse","arrow","line","pen","text","mosaic"}){
            var mark=new Annotation{Kind=kind,Start=new Point(20,20),End=new Point(50,50),Text="A",Mosaic=pixels,Points=new List<Point>{new Point(20,20),new Point(50,50)}};
            check(ExternalScrollCaptureSession.ConvertAnnotations(new[]{mark},selection,1,1,0,0).Single().kind==kind,"all supported annotation tools transfer explicitly: "+kind);
        }
        check(Reject(()=>ExternalScrollCaptureSession.ConvertAnnotations(new[]{new Annotation{Kind="unknown",Start=new Point(20,20),End=new Point(40,40)}},selection,1,1,0,0)),"unknown annotation type reports an error rather than silently dropping edits");
        check(Reject(()=>ExternalScrollCaptureSession.ConvertAnnotations(new[]{new Annotation{Kind="mosaic",Start=new Point(20,20),End=new Point(40,40)}},selection,1,1,0,0)),"missing mosaic pixels report an error rather than replacing private content");
    }
    static void Headers(Action<bool,string> check){
        foreach(var dimensions in new[]{new[]{1U,1U},new[]{8000U,8000U},new[]{32768U,1U},new[]{1U,32768U}}){ExternalScrollCaptureSession.ValidatePngHeader(Header(dimensions[0],dimensions[1]));check(true,"valid PNG dimensions within packaged helper canvas limits: "+dimensions[0]+"x"+dimensions[1]);}
        foreach(var dimensions in new[]{new[]{0U,10U},new[]{10U,0U},new[]{8000U,8001U},new[]{32769U,1U},new[]{1U,32769U},new[]{uint.MaxValue,uint.MaxValue}})check(Reject(()=>ExternalScrollCaptureSession.ValidatePngHeader(Header(dimensions[0],dimensions[1]))),"unsafe PNG dimensions rejected before decoding: "+dimensions[0]+"x"+dimensions[1]);
        var bad=Header(50,50);bad[0]=0;check(Reject(()=>ExternalScrollCaptureSession.ValidatePngHeader(bad)),"non-PNG bytes rejected before decoding");bad=Header(50,50);bad[12]=0;check(Reject(()=>ExternalScrollCaptureSession.ValidatePngHeader(bad)),"missing IHDR rejected before decoding");
        check(Reject(()=>ExternalScrollCaptureSession.ValidatePngHeader(new byte[23]))&&Reject(()=>ExternalScrollCaptureSession.ValidatePngHeader(null)),"truncated PNG header never enters image decoder");
    }
    static void Readiness(Action<bool,string> check){
        using(var session=Session()){
            var ready=new JavaScriptSerializer().Serialize(new{schemaVersion=1,sessionId=session.SessionId,ready=true});ExternalScrollCaptureSession.ValidateReady(ready,session.SessionId);
            check(true,"only matching atomic ready handshake releases the startup watchdog");
            foreach(var json in new[]{"null","{",new string('x',4097),new JavaScriptSerializer().Serialize(new{schemaVersion=2,sessionId=session.SessionId,ready=true}),new JavaScriptSerializer().Serialize(new{schemaVersion=1,sessionId=Guid.NewGuid().ToString("N"),ready=true}),new JavaScriptSerializer().Serialize(new{schemaVersion=1,sessionId=session.SessionId,ready=false})})check(Reject(()=>ExternalScrollCaptureSession.ValidateReady(json,session.SessionId)),"missing, malformed, stale and non-ready startup handshakes are rejected");
            check(!ExternalScrollCaptureSession.StartupExpired(false,false,19999)&&ExternalScrollCaptureSession.StartupExpired(false,false,20000),"startup watchdog expires at 20 seconds instead of leaving a hidden overlay indefinitely");
            check(!ExternalScrollCaptureSession.StartupExpired(true,false,long.MaxValue),"ready interactive editing has no arbitrary total timeout");
            check(!ExternalScrollCaptureSession.StartupExpired(false,true,20000),"early helper exit is handled as result/error instead of a spurious startup timeout");
            session.CleanupCompletedFiles();check(!Directory.Exists(session.OutputDirectory),"cleanup of an unstarted session cannot create a directory or touch unrelated temporary files");
        }
    }
    static void Lifetime(Action<bool,string> check){
        using(var current=Session()){
            var pending=new TaskCompletionSource<string>();string requested=null;
            var task=current.RunWithTransport(request=>{requested=request;return pending.Task;});
            check(current.IsStarted&&!task.IsCompleted&&(string)Json(requested)["sessionId"]==current.SessionId,"fake helper receives the real request and completion remains pending without desktop resources");
            current.Dispose();current.Dispose();pending.SetResult(Result(current.SessionId));
            bool cancelled=false;try{task.GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}
            check(cancelled&&current.IsDisposed&&!Directory.Exists(current.OutputDirectory),"disposed capture ignores late successful fake-helper completion and does not recreate files or overlays");
        }
        using(var previous=Session())using(var current=Session()){
            previous.Dispose();bool rejected=false;
            try{current.RunWithTransport(request=>Task.FromResult(Result(previous.SessionId))).GetAwaiter().GetResult();}catch(InvalidDataException){rejected=true;}
            check(rejected&&!current.IsDisposed,"old helper completion cannot complete or cancel a newer live session");
        }
        foreach(var status in new[]{"ok","cancelled","error"})using(var session=Session()){
            var result=session.RunWithTransport(request=>Task.FromResult(Result(session.SessionId,status))).GetAwaiter().GetResult();
            check(result.status==status&&!Directory.Exists(session.OutputDirectory),"fake transport completes explicit "+status+" without processes or files");
            bool repeated=false;try{session.RunWithTransport(request=>Task.FromResult(Result(session.SessionId))).GetAwaiter().GetResult();}catch(InvalidOperationException){repeated=true;}
            check(repeated,"same capture session cannot launch a second helper after "+status);
        }
        using(var disposed=Session()){
            disposed.Dispose();bool rejected=false;try{disposed.RunWithTransport(request=>Task.FromResult(Result(disposed.SessionId))).GetAwaiter().GetResult();}catch(ObjectDisposedException){rejected=true;}
            check(rejected,"disposed unstarted session cannot launch a fake or real helper");
        }
    }
    static void CreateSandbox(ExternalScrollCaptureSession session){
        string expected=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"Nook","scroll-capture",session.SessionId));
        if(session.OutputDirectory!=expected||Directory.Exists(expected)||File.Exists(expected))throw new IOException("Test refuses to reuse any existing session directory.");
        Directory.CreateDirectory(expected);
        Directory.CreateDirectory(Path.Combine(expected,"host-profile","Default","Cache"));
        File.WriteAllText(Path.Combine(expected,"host-profile","Local State"),"generated test state");
        File.WriteAllText(Path.Combine(expected,"host-profile","Default","Cache","data_0"),"generated test cache");
        File.WriteAllText(Path.Combine(expected,"request.json"),"generated request");
    }
    static void RemoveSandbox(ExternalScrollCaptureSession session){
        // Test-only exact generated GUID root, validated again before removing
        // its remaining regular files. Never enumerate other real sessions.
        string expected=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"Nook","scroll-capture",session.SessionId));
        if(session.OutputDirectory!=expected)throw new IOException("Unexpected sandbox target.");
        if(!Directory.Exists(expected))return;
        var pending=new Stack<string>();var dirs=new List<string>();pending.Push(expected);
        while(pending.Count>0){string dir=pending.Pop();if((File.GetAttributes(dir)&FileAttributes.ReparsePoint)!=0)throw new IOException("Test sandbox link refused.");dirs.Add(dir);foreach(string path in Directory.GetFileSystemEntries(dir)){var attrs=File.GetAttributes(path);if((attrs&FileAttributes.ReparsePoint)!=0)throw new IOException("Test sandbox link refused.");if((attrs&FileAttributes.Directory)!=0)pending.Push(path);else File.Delete(path);}}
        for(int i=dirs.Count-1;i>=0;i--)Directory.Delete(dirs[i],false);
    }
    static void Cleanup(Action<bool,string> check){
        using(var session=Session())try{
            CreateSandbox(session);
            session.CleanupCompletedFiles();
            check(File.Exists(Path.Combine(session.OutputDirectory,"request.json"))&&Directory.Exists(Path.Combine(session.OutputDirectory,"host-profile")),"unstarted or unverified cleanup preserves generated request and profile");
            var error=session.RunWithTransport(request=>Task.FromResult(Result(session.SessionId,"error"))).GetAwaiter().GetResult();
            session.Dispose();session.CleanupCompletedFiles(error,true);
            check(File.Exists(Path.Combine(session.OutputDirectory,"request.json"))&&Directory.Exists(Path.Combine(session.OutputDirectory,"host-profile")),"error completion preserves all recovery protocol and profile files");
        }finally{RemoveSandbox(session);}
        using(var session=Session())try{
            CreateSandbox(session);
            var result=session.RunWithTransport(request=>Task.FromResult(Result(session.SessionId))).GetAwaiter().GetResult();
            session.CleanupCompletedFiles(result,true);
            check(File.Exists(Path.Combine(session.OutputDirectory,"request.json"))&&Directory.Exists(Path.Combine(session.OutputDirectory,"host-profile")),"reported success without verified decoded PNG cannot clean recovery files");
            var image=BitmapSource.Create(2,2,96,96,PixelFormats.Bgra32,null,new byte[16],8);image.Freeze();
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
            using(var stream=File.Create(Path.Combine(session.OutputDirectory,"result.png")))encoder.Save(stream);
            var loaded=session.LoadImage(result);check(loaded.PixelWidth==2&&loaded.IsFrozen,"generated successful image is decoded and frozen before cleanup becomes eligible");
            session.CleanupCompletedFiles(result,false);
            check(File.Exists(Path.Combine(session.OutputDirectory,"result.png")),"cancelled save or failed export keeps verified long screenshot for recovery");
            File.WriteAllText(Path.Combine(session.OutputDirectory,"unrelated.txt"),"keep me");Directory.CreateDirectory(Path.Combine(session.OutputDirectory,"unknown-folder"));File.WriteAllText(Path.Combine(session.OutputDirectory,"unknown-folder","keep.txt"),"keep me too");
            session.Dispose();session.CleanupCompletedFiles(result,true);
            check(!Directory.Exists(Path.Combine(session.OutputDirectory,"host-profile"))&&!File.Exists(Path.Combine(session.OutputDirectory,"request.json"))&&!File.Exists(Path.Combine(session.OutputDirectory,"result.png")),"successful verified export removes only owned protocol files and the exact generated profile tree");
            check(File.ReadAllText(Path.Combine(session.OutputDirectory,"unrelated.txt"))=="keep me"&&File.ReadAllText(Path.Combine(session.OutputDirectory,"unknown-folder","keep.txt"))=="keep me too","unknown sibling files and folders survive profile cleanup unchanged");
        }finally{RemoveSandbox(session);}
        using(var session=Session())try{
            CreateSandbox(session);var result=session.RunWithTransport(request=>Task.FromResult(Result(session.SessionId,"cancelled"))).GetAwaiter().GetResult();
            var other=ExternalScrollCaptureSession.ParseResult(Result(session.SessionId,"cancelled"),session.SessionId,session.OutputDirectory);session.CleanupCompletedFiles(other);
            check(Directory.Exists(Path.Combine(session.OutputDirectory,"host-profile")),"unaccepted result objects cannot authorize deleting a session profile");
            session.Dispose();session.CleanupCompletedFiles(result);
            check(!Directory.Exists(session.OutputDirectory),"explicit accepted cancellation removes its generated profile and empty owned GUID directory");
        }finally{RemoveSandbox(session);}
        string root=Path.GetFullPath(@"C:\generated-scroll-test\host-profile"),prefix=root+Path.DirectorySeparatorChar;
        foreach(var attrs in new[]{FileAttributes.ReparsePoint,FileAttributes.ReparsePoint|FileAttributes.Directory}){
            bool refused=false;try{ExternalScrollCaptureSession.ValidateProfileEntry(Path.Combine(root,"link"),root,prefix,attrs);}catch(IOException){refused=true;}
            check(refused,"both file and directory reparse descendants are rejected before any profile deletion: "+attrs);
        }
        foreach(var path in new[]{Path.GetFullPath(@"C:\generated-scroll-test\host-profile-other\data"),Path.Combine(root,"nested","data")}){
            bool refused=false;try{ExternalScrollCaptureSession.ValidateProfileEntry(path,root,prefix,FileAttributes.Normal);}catch(IOException){refused=true;}
            check(refused,"profile validation rejects sibling-prefix or non-immediate entries: "+path);
        }
    }
}
