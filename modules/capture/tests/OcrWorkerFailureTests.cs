#if OCR_WORKER_FAILURE_TESTS
// Dedicated VM-only executable, named QingJie.Tests.exe to use the application's
// existing friend assembly. Runs real child processes and named pipes, no models,
// networking, application data or visible windows. Do not run on the host.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Web.Script.Serialization;
using QingJie;

internal static class OcrWorkerFailureTests {
    static int checks;static string root;
    static readonly List<object> results=new List<object>();
    static string Executable {get{return typeof(OcrWorkerFailureTests).Assembly.Location;}}
    static void Check(bool value,string message){checks++;if(!value)throw new InvalidOperationException(message);Console.WriteLine("PASS "+message);}
    static bool Inside(string path,string parent){return !string.IsNullOrEmpty(path)&&Path.GetFullPath(path).StartsWith(parent+"\\",StringComparison.OrdinalIgnoreCase);}
    static bool Guard(){return Environment.UserName.Equals("NookTester",StringComparison.OrdinalIgnoreCase)&&Inside(AppDomain.CurrentDomain.BaseDirectory,@"C:\NookLab\Audit\apps")&&Inside(Environment.GetEnvironmentVariable("MORI_DESK_DATA"),@"C:\NookLab\Audit\data");}
    static string Quote(string value){return "\""+value+"\"";}
    static ProcessStartInfo Start(string arguments){return new ProcessStartInfo(Executable,arguments){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=AppDomain.CurrentDomain.BaseDirectory};}
    static BitmapSource Image(){var image=BitmapSource.Create(4,4,96,96,PixelFormats.Bgra32,null,new byte[64],16);image.Freeze();return image;}
    static void RecordPid(string directory){File.WriteAllText(Path.Combine(directory,"pid-"+Process.GetCurrentProcess().Id+".txt"),Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString());}
    static bool AllStopped(string directory){
        foreach(string path in Directory.GetFiles(directory,"pid-*.txt")){
            int pid=int.Parse(Path.GetFileNameWithoutExtension(path).Substring(4));long ticks=long.Parse(File.ReadAllText(path));
            try{using(var process=Process.GetProcessById(pid))if(!process.HasExited&&process.StartTime.ToUniversalTime().Ticks==ticks)return false;}catch(ArgumentException){}catch(InvalidOperationException){}
        }
        return true;
    }
    static void VerifyStopped(string directory){var watch=Stopwatch.StartNew();while(!AllStopped(directory)&&watch.ElapsedMilliseconds<2000)Thread.Sleep(25);Check(AllStopped(directory),"all fixture workers and descendants exited: "+Path.GetFileName(directory));}
    sealed class Outcome {internal OcrPage Page;internal Exception Error;internal int Starts;internal long Elapsed;internal string Directory;}
    static Outcome Case(string name,Func<int,string> scenario,int budget=7000,CancellationToken cancel=default(CancellationToken),Action<string> prepared=null){
        var outcome=new Outcome {Directory=Path.Combine(root,name)};Directory.CreateDirectory(outcome.Directory);if(prepared!=null)prepared(outcome.Directory);
        var clock=Stopwatch.StartNew();
        try{outcome.Page=OcrWorker.RunWithWorker(Image(),cancel,"",false,(pipe,attempt)=>{
            Check(AllStopped(outcome.Directory),"previous attempt is fully stopped before next launch: "+name+"/"+attempt);
            outcome.Starts++;return Start("--fixture "+scenario(attempt)+" "+Quote(pipe)+" "+Quote(outcome.Directory));
        },budget);}catch(Exception error){outcome.Error=error;}
        outcome.Elapsed=clock.ElapsedMilliseconds;VerifyStopped(outcome.Directory);
        results.Add(new{name,elapsedMs=outcome.Elapsed,starts=outcome.Starts,success=outcome.Page!=null,error=outcome.Error==null?null:outcome.Error.GetType().FullName,message=outcome.Error==null?null:outcome.Error.Message});return outcome;
    }
    static void Failed(Outcome outcome,int starts,string name){
        Check(outcome.Page==null&&outcome.Error!=null,"failure never returns a partial page: "+name);
        Check(outcome.Starts==starts,"bounded attempts: "+name);
        Check(outcome.Error.Message.Contains("原图已保留"),"failure explains that original image is retained: "+name);
    }
    static void Cases(){
        var good=Case("complete-then-exit",i=>"complete");Check(good.Page!=null&&good.Page.Text=="complete result"&&good.Starts==1,"a fully buffered response survives immediate clean exit");
        good=Case("complete-then-native-exit",i=>"complete-native");Check(good.Page!=null&&good.Page.Text=="complete result"&&good.Starts==1,"a full valid response remains usable even when worker exits immediately afterwards");
        good=Case("native-before-connect-recovers",i=>i==0?"native-before":"complete");Check(good.Page!=null&&good.Starts==2,"native failure before connection gets one serial recovery");
        good=Case("native-after-request-recovers",i=>i==0?"native-after":"complete");Check(good.Page!=null&&good.Starts==2,"native failure before first response byte gets one serial recovery");
        var failed=Case("native-twice",i=>"native-after");Failed(failed,2,"second native failure is final");Check(Convert.ToString(failed.Error.Data["OcrWorkerExitCode"])=="0xC0000005"&&Convert.ToInt32(failed.Error.Data["OcrWorkerAttempt"])==2,"final diagnostics retain actual child exit code and attempt count");
        failed=Case("explicit-before-connect",i=>"exit-before");Failed(failed,1,"explicit pre-connect exit");Check(failed.Elapsed<5000,"pre-connect exit does not wait for the full 7 second request timeout");
        failed=Case("explicit-after-request",i=>"exit-after");Failed(failed,1,"explicit child failure");
        failed=Case("illegal-instruction",i=>"illegal");Failed(failed,1,"unsupported CPU/native instruction is not retried");
        failed=Case("half-response-native-exit",i=>"half");Failed(failed,1,"partial reply is not retried even for an otherwise recoverable exit code");Check(Convert.ToInt64(failed.Error.Data["OcrResponseBytes"])==4,"partial protocol header is counted before failure");
        failed=Case("malformed-response",i=>"malformed");Failed(failed,1,"malformed protocol is not retried");
        failed=Case("explicit-error-frame",i=>"error");Failed(failed,1,"reported recognition failure is not retried");Check(failed.Error.Message.Contains("合成组件错误"),"explicit engine guidance remains visible");
        failed=Case("deadline-no-connect",i=>"hang-before",1200);Check(failed.Error is TimeoutException&&failed.Starts==1,"timeout before connection remains timeout and never retries");
        failed=Case("deadline-after-request",i=>"hang",1800);Check(failed.Error is TimeoutException&&failed.Starts==1,"blocked response read is interrupted by total timeout");
        // First attempt spends part of this budget before the injected native exit;
        // the recovery must receive only the remainder, not a fresh nine seconds.
        failed=Case("recovery-shares-deadline",i=>i==0?"slow-native":"hang",9000);Check(failed.Error is TimeoutException&&failed.Starts==2,"one recovery retains the same deadline");Check(failed.Elapsed<11000,"recovery does not reset the nine-second request budget");
        using(var cancel=new CancellationTokenSource()){
            cancel.Cancel();failed=Case("pre-cancelled",i=>"complete",7000,cancel.Token);Check(failed.Error is OperationCanceledException&&failed.Starts==0,"pre-cancelled call never starts a worker");
        }
        using(var cancel=new CancellationTokenSource()){
            Task trigger=null;failed=Case("cancel-active-tree",i=>"hang-tree",10000,cancel.Token,directory=>{
                trigger=Task.Run(()=>{var watch=Stopwatch.StartNew();while(!File.Exists(Path.Combine(directory,"ready"))&&watch.ElapsedMilliseconds<7000)Thread.Sleep(20);cancel.Cancel();});
            });trigger.GetAwaiter().GetResult();Check(failed.Error is OperationCanceledException&&failed.Starts==1,"active cancellation kills worker and grandchild without retry");Check(Directory.GetFiles(failed.Directory,"pid-*.txt").Length==2,"cancellation fixture actually created a descendant");
        }
        ParentCrash();ParentCrashBeforeResume();
    }
    static void ParentCrash(){
        string directory=Path.Combine(root,"parent-crash");Directory.CreateDirectory(directory);
        using(var owner=Process.Start(Start("--owner "+Quote(directory)))){
            try{
                var watch=Stopwatch.StartNew();while(!File.Exists(Path.Combine(directory,"ready"))&&!owner.HasExited&&watch.ElapsedMilliseconds<10000)Thread.Sleep(20);
                Check(File.Exists(Path.Combine(directory,"ready")),"parent-crash fixture has live worker and grandchild before owner termination");
                owner.Kill();Check(owner.WaitForExit(3000),"test owner exits after forced termination");VerifyStopped(directory);
                Check(Directory.GetFiles(directory,"pid-*.txt").Length==2,"closing parent kills the job's two real descendants");
            }finally{try{if(!owner.HasExited){owner.Kill();owner.WaitForExit(2000);}}catch(InvalidOperationException){}}
        }
    }
    static void ParentCrashBeforeResume(){
        string directory=Path.Combine(root,"parent-crash-before-resume");Directory.CreateDirectory(directory);var elapsed=Stopwatch.StartNew();
        using(var owner=Process.Start(Start("--owner-suspended "+Quote(directory)))){
            try{
                var watch=Stopwatch.StartNew();while(!File.Exists(Path.Combine(directory,"suspended-ready"))&&!owner.HasExited&&watch.ElapsedMilliseconds<10000)Thread.Sleep(20);
                Check(File.Exists(Path.Combine(directory,"suspended-ready")),"owner reached the immediately-created suspended worker boundary");
                Check(Directory.GetFiles(directory,"pid-*.txt").Length==1&&!AllStopped(directory),"a real suspended worker is alive before owner termination");
                Check(!File.Exists(Path.Combine(directory,"fixture-entered")),"suspended child has not entered any managed code or watchdog");
                owner.Kill();Check(owner.WaitForExit(3000),"owner killed before ResumeThread completes");VerifyStopped(directory);
                Check(!File.Exists(Path.Combine(directory,"fixture-entered")),"atomic job cleanup terminates the child without ever resuming it");
                results.Add(new{name="parent-crash-before-resume",elapsedMs=elapsed.ElapsedMilliseconds,success=true});
            }finally{
                try{if(!owner.HasExited){owner.Kill();owner.WaitForExit(2000);}}catch(InvalidOperationException){}
                // If a regression reintroduces the orphan gap, do not leave that
                // suspended fixture behind after the assertion has failed.
                foreach(string path in Directory.GetFiles(directory,"pid-*.txt")){
                    int pid=int.Parse(Path.GetFileNameWithoutExtension(path).Substring(4));long ticks=long.Parse(File.ReadAllText(path));
                    try{using(var child=Process.GetProcessById(pid))if(!child.HasExited&&child.StartTime.ToUniversalTime().Ticks==ticks){child.Kill();child.WaitForExit(2000);}}catch(ArgumentException){}catch(InvalidOperationException){}
                }
            }
        }
    }
    static int Fixture(string[] args){
        string scenario=args[1],pipeName=args[2],directory=args[3];if(!Inside(directory,Environment.GetEnvironmentVariable("MORI_DESK_DATA")))return 77;File.WriteAllText(Path.Combine(directory,"fixture-entered"),"1");RecordPid(directory);
        if(scenario=="native-before"){NativeExit(0xC0000005);return 1;}
        if(scenario=="exit-before")return 2;
        if(scenario=="hang-before"){Thread.Sleep(30000);return 1;}
        using(var pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.InOut)){
            pipe.Connect(5000);
            using(var reader=new BinaryReader(pipe,Encoding.UTF8,true))using(var writer=new BinaryWriter(pipe,Encoding.UTF8,true)){
                reader.ReadInt32();int width=reader.ReadInt32(),height=reader.ReadInt32();reader.ReadString();reader.ReadString();
                int remaining=checked(width*height*4);byte[] buffer=new byte[4096];while(remaining>0){int count=reader.Read(buffer,0,Math.Min(remaining,buffer.Length));if(count==0)return 3;remaining-=count;}
                if(scenario=="native-after"){NativeExit(0xC0000005);return 1;}
                if(scenario=="slow-native"){Thread.Sleep(3000);NativeExit(0xC0000005);return 1;}
                if(scenario=="illegal"){NativeExit(0xC000001D);return 1;}
                if(scenario=="exit-after")return 2;
                if(scenario=="half"){writer.Write(0x514A4F34);writer.Flush();NativeExit(0xC0000005);return 1;}
                if(scenario=="malformed"){writer.Write(42);writer.Flush();return 1;}
                if(scenario=="error"){writer.Write(0x514A4F32);writer.Write("合成组件错误，请修复识别组件。");writer.Flush();return 1;}
                if(scenario=="hang"||scenario=="hang-tree"){
                    if(scenario=="hang-tree"){
                        using(var child=Process.Start(Start("--descendant "+Quote(directory)))){
                            var watch=Stopwatch.StartNew();while(Directory.GetFiles(directory,"pid-*.txt").Length<2&&watch.ElapsedMilliseconds<5000)Thread.Sleep(20);
                        }
                    }
                    File.WriteAllText(Path.Combine(directory,"ready"),"1");Thread.Sleep(30000);return 1;
                }
                var page=new OcrPage();page.Words.Add(new WordBox{Text="complete result",Line=0,Box=new Rect(0,0,3,3)});OcrWorker.WriteResult(writer,page);writer.Flush();
                if(scenario=="complete-native")NativeExit(0xC0000005);else Environment.Exit(0);return 0;
            }
        }
    }
    static void NativeExit(uint code){TerminateProcess(GetCurrentProcess(),code);Thread.Sleep(Timeout.Infinite);}
    [DllImport("kernel32.dll")]static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool TerminateProcess(IntPtr process,uint exitCode);
    [STAThread] public static int Main(string[] args){
        if(!Guard())return 77;
        if(args.Length==4&&args[0]=="--fixture")return Fixture(args);
        if(args.Length==2&&args[0]=="--descendant"){
            if(!Inside(args[1],Environment.GetEnvironmentVariable("MORI_DESK_DATA")))return 77;RecordPid(args[1]);Thread.Sleep(30000);return 1;
        }
        if(args.Length==2&&args[0]=="--owner"){
            if(!Inside(args[1],Environment.GetEnvironmentVariable("MORI_DESK_DATA")))return 77;
            OcrWorker.RunWithWorker(Image(),CancellationToken.None,"",false,(pipe,attempt)=>Start("--fixture hang-tree "+Quote(pipe)+" "+Quote(args[1])),25000);return 1;
        }
        if(args.Length==2&&args[0]=="--owner-suspended"){
            string directory=args[1];if(!Inside(directory,Environment.GetEnvironmentVariable("MORI_DESK_DATA")))return 77;
            OcrWorker.RunWithWorker(Image(),CancellationToken.None,"",false,(pipe,attempt)=>Start("--fixture complete "+Quote(pipe)+" "+Quote(directory)),25000,child=>{
                File.WriteAllText(Path.Combine(directory,"pid-"+child.Id+".txt"),child.StartTime.ToUniversalTime().Ticks.ToString());
                File.WriteAllText(Path.Combine(directory,"suspended-ready"),"1");Thread.Sleep(30000);
            });return 1;
        }
        if(args.Length!=0)return 64;
        root=Path.Combine(Environment.GetEnvironmentVariable("MORI_DESK_DATA"),"worker-faults-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        bool passed=false;string error=null;
        try{Cases();passed=true;Console.WriteLine("PASS "+checks+" worker fault assertions");return 0;}
        catch(Exception failure){error=failure.ToString();Console.Error.WriteLine(error);return 1;}
        finally{File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ocr-worker-failures.json"),new JavaScriptSerializer().Serialize(new{passed,checks,results,error,fixtureDirectory=root}));}
    }
}
#endif
