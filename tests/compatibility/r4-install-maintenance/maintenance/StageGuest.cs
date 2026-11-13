using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;

// Test-only VM controller. No normal application UI startup or installation.
// Reviewed tests may launch dedicated media workers through their fixed flags.
// Tests, evidence and cleanup have separate lifetimes; fixtures are retained.
internal static class StageGuest {
 static readonly UTF8Encoding Utf8=new UTF8Encoding(false);
 static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=4*1024*1024};
 static string Package,Root,Label,RunId;static bool Verified;static Dictionary<string,object> Plan;
 static string Text(object o){return Convert.ToString(o,System.Globalization.CultureInfo.InvariantCulture);}
 static void Need(bool ok,string reason){if(!ok)throw new InvalidDataException(reason);}
 static void Plain(string path){for(string p=Path.GetFullPath(path);!string.IsNullOrEmpty(p);p=Path.GetDirectoryName(p))if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked controller path refused: "+p);}
 static string Hash(string path){Plain(path);using(var f=File.OpenRead(path))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(f)).Replace("-","");}
 static Dictionary<string,object> Read(string path){Plain(path);Need(new FileInfo(path).Length<4*1024*1024,"Bounded JSON required");return Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path,Utf8));}
 static void Write(string path,object o){Plain(path);using(var f=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read))using(var w=new StreamWriter(f,Utf8)){w.Write(Json.Serialize(o));w.Flush();f.Flush(true);}}
 static string Owned(string relative){string p=Path.GetFullPath(Path.Combine(Root,relative));Need(p.StartsWith(Root+"\\",StringComparison.OrdinalIgnoreCase),"Owned run path required");Plain(p);return p;}
 static void Guard(){bool guest=false;using(var q=new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem"))foreach(ManagementObject row in q.Get())using(row)guest|=Regex.IsMatch(Text(row["Manufacturer"])+" "+Text(row["Model"]),"QEMU|KVM|Bochs",RegexOptions.IgnoreCase);Need(guest&&Environment.UserName.Equals("NookTester",StringComparison.OrdinalIgnoreCase),"Dedicated NookTester VM required before writes");Package=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');Need(Regex.IsMatch(Package,@"\AC:\\NookLab\\Audit\\apps\\[a-fA-F0-9]{12}\z"),"Hash-addressed VM package required");Plain(Package);Verified=true;}
 static void LoadPlan(string label){Need(Regex.IsMatch(label,@"\A[a-z0-9-]{1,60}\z"),"Allowlisted plan filename required");Label=label;Plan=Read(Path.Combine(Package,"plans",label+".json"));Need(Regex.IsMatch(Text(Plan["appSha256"]),@"\A[A-Fa-f0-9]{64}\z"),"Pinned product hash required");int seconds=Convert.ToInt32(Plan["timeoutSeconds"]);Need(seconds>=15&&seconds<=600,"Bounded stage deadline");}
 static void SetRoot(string id){Need(Regex.IsMatch(id,@"\A[a-f0-9]{32}\z"),"Full GUID run id required");RunId=id;Root=@"C:\NookLab\Audit\b-"+id.Substring(0,12);Plain(Root);}
 static void CheckOwner(){var o=Read(Owned("owner.json"));Need(Text(o["runId"])==RunId&&Text(o["label"])==Label&&Text(o["package"])==Package&&Text(o["appSha256"])==Text(Plan["appSha256"]),"Exact full GUID, plan, package and product owner required");}
 static object[] Items(object o){return ((IEnumerable)o).Cast<object>().ToArray();}
 static void SetStatic(Assembly a,string type,string field,object value){var f=a.GetType(type,true).GetField(field,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static);Need(f!=null,"Known isolated test setting: "+type+"."+field);f.SetValue(null,value);}
 static void CaptureSettings(Assembly a){SetStatic(a,"QingJie.Preferences","TestFilePath",Owned("data\\capture\\settings.json"));SetStatic(a,"QingJie.OcrLanguagePacks","TestFolder",Owned("data\\languages"));SetStatic(a,"QingJie.OfflinePacks","TestFolder",Owned("data\\offline"));SetStatic(a,"QingJie.AppState","Settings",Activator.CreateInstance(a.GetType("QingJie.Preferences",true)));}

 static void ConfigureVideoFixture(){
  Environment.SetEnvironmentVariable("MORI_TEST_VIDEO",null);
  Need(Label!="library-media"||Plan.ContainsKey("videoFixture"),"Library media requires its explicit real-video fixture");
  if(!Plan.ContainsKey("videoFixture"))return;
  Need(Label=="library-media","Real-video fixture is limited to library-media");
  var fixture=(Dictionary<string,object>)Plan["videoFixture"];
  Need(Text(fixture["path"])=="fixtures/library-video.mp4"&&Regex.IsMatch(Text(fixture["sha256"]),@"\A[A-F0-9]{64}\z"),"Fixed package-local video fixture required");
  string file=Path.Combine(Package,@"fixtures\library-video.mp4");Plain(file);
  Need(File.Exists(file)&&new FileInfo(file).Length>0&&new FileInfo(file).Length<=32*1024*1024&&Hash(file)==Text(fixture["sha256"]),"Real-video fixture hash and bounded size verified");
  Environment.SetEnvironmentVariable("MORI_TEST_VIDEO",file);
 }
 static int Worker(string label,string id){
  LoadPlan(label);SetRoot(id);CheckOwner();var gateWatch=Stopwatch.StartNew();while(!File.Exists(Owned("start.gate"))){if(gateWatch.Elapsed.TotalSeconds>45)throw new TimeoutException("Parent gate timeout");Thread.Sleep(25);}Need(File.ReadAllText(Owned("start.gate"),Utf8)==Text(Plan["appSha256"]),"Atomic gate matches product hash");bool member;Need(IsProcessInJob(GetCurrentProcess(),IntPtr.Zero,out member)&&member,"Worker must belong to Job before product reflection");Need(Hash(Path.Combine(Package,"Mori.exe")).Equals(Text(Plan["appSha256"]),StringComparison.OrdinalIgnoreCase),"Exact product hash verified in worker");
  AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string name=new AssemblyName(e.Name).Name;Need(Regex.IsMatch(name,@"\A[\w.-]+\z"),"Plain dependency name");string p=Path.Combine(Package,name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
  ConfigureVideoFixture();
  var app=Application.Current??new Application();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;var product=Assembly.LoadFrom(Path.Combine(Package,"Mori.exe"));if(Plan.ContainsKey("captureSettings")&&Convert.ToBoolean(Plan["captureSettings"]))CaptureSettings(product);
  var results=new List<object>();bool productPassed=true,expectationMatched=false;string failure=null;var total=Stopwatch.StartNew();int totalChecks=0;var stages=Items(Plan["stages"]);Need(stages.Length>0&&stages.Length<=32,"Bounded explicit stage list");
  using(var log=new StreamWriter(Owned("progress.txt"),false,Utf8)){
   int index=0;foreach(var raw in stages){var stage=(Dictionary<string,object>)raw;string type=Text(stage["type"]),method=Text(stage["method"]);Need(Regex.IsMatch(type,@"\A[A-Za-z_][A-Za-z0-9_.]*Tests\z")&&new[]{"Run","RunSlice","RunScenario","Svg","Raster","Scheduling","Media","Benchmark","VideoMp4"}.Contains(method),"Only explicit test methods may be reflected");Assembly assembly=product;
    if(stage.ContainsKey("assembly")){string name=Text(stage["assembly"]);Need(name=="QingJie.Tests.exe"&&stage.ContainsKey("assemblySha256")&&Hash(Path.Combine(Package,name)).Equals(Text(stage["assemblySha256"]),StringComparison.OrdinalIgnoreCase),"Pinned optional capture test assembly");assembly=Assembly.LoadFrom(Path.Combine(Package,name));}
    string fixture=Owned("f"+(index++).ToString("D2"));Directory.CreateDirectory(fixture);int checks=0;string lastFailure=null;var stageWatch=Stopwatch.StartNew();log.WriteLine(DateTime.UtcNow.ToString("O")+" START "+type+"."+method);log.Flush();
    Action<bool,string> check=(ok,message)=>{checks++;totalChecks++;log.WriteLine((ok?"PASS ":"FAIL ")+checks+" "+message);if(!ok||checks%25==0)log.Flush();if(!ok){lastFailure=message;throw new InvalidOperationException(message);}};
    try{object[] supplied=Items(stage["args"]).Select(value=>{if(value is int)return value;string token=Text(value);if(token=="check")return (object)check;if(token=="root")return fixture;if(token.StartsWith("sub:",StringComparison.Ordinal)){string rel=token.Substring(4);Need(Regex.IsMatch(rel,@"\A[A-Za-z0-9_-]{1,80}\z"),"Fixed fixture subdirectory");return Path.Combine(fixture,rel);}throw new InvalidDataException("Unknown test argument token");}).ToArray();
     var types=supplied.Select(value=>value is Action<bool,string>?typeof(Action<bool,string>):value.GetType()).ToArray();var target=assembly.GetType(type,true).GetMethod(method,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static,null,types,null);Need(target!=null,"Exact method signature exists: "+type+"."+method);Need(target.ReturnType==typeof(void)||target.ReturnType==typeof(int),"Only synchronous void/int tests are accepted");object returned=target.Invoke(null,supplied);if(target.ReturnType==typeof(int))Need(Convert.ToInt32(returned)==0,"Self-reporting stage returned zero: "+type+"."+method);
     string ownReport=Path.Combine(fixture,"test-report.txt"),detail=null;if(File.Exists(ownReport)){detail=File.ReadAllText(ownReport,Utf8);if(detail.Length>60000)detail=detail.Substring(detail.Length-60000);}
     results.Add(new{stage=type+"."+method,passed=true,checks,elapsedSeconds=stageWatch.Elapsed.TotalSeconds,fixture,returnValue=returned,externalReport=detail});log.WriteLine("STAGE PASS "+type+"."+method);log.Flush();
    }catch(Exception error){while(error is TargetInvocationException&&error.InnerException!=null)error=error.InnerException;productPassed=false;failure=error.ToString();string expected=Plan.ContainsKey("expectedFailure")?Text(Plan["expectedFailure"]):null;expectationMatched=expected!=null&&lastFailure==expected;results.Add(new{stage=type+"."+method,passed=false,checks,elapsedSeconds=stageWatch.Elapsed.TotalSeconds,fixture,failedCheck=lastFailure,error=failure});log.WriteLine("STAGE FAIL "+failure);log.Flush();break;}
   }
  }
  bool witness=Plan.ContainsKey("expectedFailure"),passed=witness?expectationMatched:productPassed;Write(Owned("worker-result.json"),new{passed,productPassed,expectedFailureConfirmed=expectationMatched,mutationWitness=witness,checks=totalChecks,stages=results,videoFixture=Plan.ContainsKey("videoFixture")?Plan["videoFixture"]:null,error=failure,elapsedSeconds=total.Elapsed.TotalSeconds,fixtureRoot=Root});return passed?0:1;
 }
 [StructLayout(LayoutKind.Sequential)]struct Basic{public long ProcessTime,JobTime;public uint Flags;public UIntPtr Min,Max;public uint ActiveLimit;public UIntPtr Affinity;public uint Priority,Scheduling;}
 [StructLayout(LayoutKind.Sequential)]struct Io{public ulong R,W,O,RB,WB,OB;}
 [StructLayout(LayoutKind.Sequential)]struct Limits{public Basic Basic;public Io Io;public UIntPtr PM,JM,PPM,PJM;}
 [StructLayout(LayoutKind.Sequential)]struct Accounting{public long U,K,PU,PK;public uint Faults,Total,Active,Terminated;}
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr CreateJobObject(IntPtr attrs,string name);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetInformationJobObject(IntPtr job,int kind,ref Limits data,int size);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool QueryInformationJobObject(IntPtr job,int kind,out Accounting data,int size,IntPtr returned);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool IsProcessInJob(IntPtr process,IntPtr job,out bool value);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool TerminateJobObject(IntPtr job,uint code);
 [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
 [DllImport("kernel32.dll")]static extern IntPtr GetCurrentProcess();
 static Accounting Info(IntPtr job){Accounting a;if(!QueryInformationJobObject(job,1,out a,Marshal.SizeOf(typeof(Accounting)),IntPtr.Zero))throw new Win32Exception();return a;}
 static int Run(string label){
  LoadPlan(label);SetRoot(Guid.NewGuid().ToString("N"));Need(!Directory.Exists(Root)&&!File.Exists(Root),"Fresh short root required");Directory.CreateDirectory(Root);Write(Owned("owner.json"),new{runId=RunId,label=Label,package=Package,appSha256=Text(Plan["appSha256"])});Directory.CreateDirectory(Owned("temp"));Directory.CreateDirectory(Owned("data"));Need(Hash(Path.Combine(Package,"Mori.exe")).Equals(Text(Plan["appSha256"]),StringComparison.OrdinalIgnoreCase),"Parent verifies exact product hash");
  var report=new Dictionary<string,object>{{"label",label},{"runRoot",Root},{"package",Package},{"appSha256",Text(Plan["appSha256"])},{"scope","VM-only explicit test stages; no normal application UI startup or installation; reviewed media workers allowed"},{"fixtureCleanup","retained for separate owned cleanup after evidence collection"},{"startedUtc",DateTime.UtcNow.ToString("O")}};IntPtr job=IntPtr.Zero;Process worker=null;Timer watchdog=null;bool stopped=false,passed=false;int fired=0;var total=Stopwatch.StartNew();
  try{job=CreateJobObject(IntPtr.Zero,null);if(job==IntPtr.Zero)throw new Win32Exception();var limits=new Limits();limits.Basic.Flags=0x2000;if(!SetInformationJobObject(job,9,ref limits,Marshal.SizeOf(typeof(Limits))))throw new Win32Exception();
   var start=new ProcessStartInfo(Path.Combine(Package,"StageGuest.exe"),"--worker "+label+" "+RunId){WorkingDirectory=Root,UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};start.EnvironmentVariables["TEMP"]=Owned("temp");start.EnvironmentVariables["TMP"]=Owned("temp");start.EnvironmentVariables["MORI_DESK_DATA"]=Owned("data");worker=Process.Start(start);if(!AssignProcessToJobObject(job,worker.Handle))throw new Win32Exception();bool member;Need(IsProcessInJob(worker.Handle,job,out member)&&member,"Worker in kill-on-close Job before gate");int timeout=Convert.ToInt32(Plan["timeoutSeconds"]);IntPtr watched=job;watchdog=new Timer(delegate{Interlocked.Exchange(ref fired,1);TerminateJobObject(watched,124);},null,(timeout+15)*1000,Timeout.Infinite);
   string gate=Owned("start.gate.pending");using(var f=new FileStream(gate,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var w=new StreamWriter(f,Utf8)){w.Write(Text(Plan["appSha256"]));w.Flush();f.Flush(true);}File.Move(gate,Owned("start.gate"));
   if(!worker.WaitForExit(timeout*1000))throw new TimeoutException("Stage group exceeded "+timeout+" seconds");report["workerExitCode"]=worker.ExitCode;var wait=Stopwatch.StartNew();while(Info(job).Active!=0&&wait.Elapsed.TotalSeconds<10)Thread.Sleep(100);Need(Info(job).Active==0,"All stage workers exited");stopped=true;var result=Read(Owned("worker-result.json"));report["result"]=result;Need(worker.ExitCode==0&&Convert.ToBoolean(result["passed"]),"Stage result satisfies explicit plan");passed=true;
  }catch(Exception error){report["error"]=error.ToString();}
  finally{if(watchdog!=null)using(var done=new ManualResetEvent(false)){if(watchdog.Dispose(done))done.WaitOne(5000);}if(job!=IntPtr.Zero){try{if(Info(job).Active!=0){TerminateJobObject(job,125);var wait=Stopwatch.StartNew();while(Info(job).Active!=0&&wait.Elapsed.TotalSeconds<10)Thread.Sleep(100);}stopped=Info(job).Active==0;report["jobActiveAfterStop"]=Info(job).Active;report["jobTotalProcesses"]=Info(job).Total;}catch(Exception error){report["stopError"]=error.ToString();}CloseHandle(job);}if(worker!=null){try{if(!worker.HasExited){worker.Kill();worker.WaitForExit(5000);}}catch{}worker.Dispose();}}
  string progress=Owned("progress.txt");if(File.Exists(progress)){string text=File.ReadAllText(progress,Utf8);report["progressTail"]=text.Length>120000?text.Substring(text.Length-120000):text;}if(!report.ContainsKey("result")&&File.Exists(Owned("worker-result.json")))report["result"]=Read(Owned("worker-result.json"));report["passed"]=passed&&stopped;report["childrenStopped"]=stopped;report["watchdogFired"]=fired!=0;report["elapsedSeconds"]=Math.Round(total.Elapsed.TotalSeconds,2);report["completedUtc"]=DateTime.UtcNow.ToString("O");Write(Path.Combine(Package,label+".json"),report);return passed&&stopped?0:1;
 }
 [STAThread]static int Main(string[] args){try{Guard();if(args.Length==2&&args[0]=="--run")return Run(args[1]);if(args.Length==3&&args[0]=="--worker")return Worker(args[1],args[2]);throw new InvalidDataException("Explicit test-only command required");}catch(Exception error){if(Verified&&args.Length==2&&args[0]=="--run"&&Regex.IsMatch(args[1],@"\A[a-z0-9-]{1,60}\z"))try{string p=Path.Combine(Package,args[1]+".json");if(!File.Exists(p))Write(p,new{passed=false,error=error.ToString(),runRoot=Root});}catch{}Console.WriteLine(error);return 1;}}
}
