using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

[assembly: AssemblyTitle("Nook synthetic component store contract wrapper - VM ONLY")]
internal static class ComponentStoreGuest {
 const string TestHash="9ABA9538C4FB64FB92FC7D183322E720A4834B4A0869F4946F18DA8137B7A3C5";
 const string HostHash="358C0619CA242AA765F1480F22243D8393B72D1CFBBEB016675117AB0BE95C85", InventoryHash="4A31CF85C1FBA50F520B58989D66292235BD1137641FD380966EB35ACFD696D6";
 static readonly UTF8Encoding Utf8=new UTF8Encoding(false);
 static readonly JavaScriptSerializer Json=new JavaScriptSerializer {MaxJsonLength=2*1024*1024};
 static readonly List<string> Checks=new List<string>();
 static string Package,Root,RunId;static bool Verified;
 static void Need(bool ok,string text){if(!ok)throw new InvalidDataException(text);Checks.Add(text);}
 static void Plain(string path){for(string p=Path.GetFullPath(path);!string.IsNullOrEmpty(p);p=Path.GetDirectoryName(p))if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("Reparse point refused: "+p);}
 static string Hash(string path){Plain(path);using(var f=File.OpenRead(path))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(f)).Replace("-","");}
 static void NewText(string path,string value){Plain(path);using(var f=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read))using(var w=new StreamWriter(f,Utf8)){w.Write(value);w.Flush();f.Flush(true);}}
 static void NewJson(string path,object value){NewText(path,Json.Serialize(value));}
 static Dictionary<string,object> Read(string path){Plain(path);Need(new FileInfo(path).Length<2*1024*1024,"Bounded owner/report");return Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path,Utf8));}
 static string Safe(string relative){string path=Path.GetFullPath(Path.Combine(Root,relative));Need(path.StartsWith(Root+"\\",StringComparison.OrdinalIgnoreCase),"Path confined to this run");Plain(path);return path;}
 static void Guard(){
  bool guest=false;using(var q=new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem"))foreach(ManagementObject row in q.Get())using(row)guest|=Regex.IsMatch(Convert.ToString(row["Manufacturer"])+" "+Convert.ToString(row["Model"]),"QEMU|KVM|Bochs",RegexOptions.IgnoreCase);
  Need(guest&&Environment.UserName.Equals("NookTester",StringComparison.OrdinalIgnoreCase),"Dedicated QEMU NookTester required before writes");Package=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');Need(Regex.IsMatch(Package,@"\AC:\\NookLab\\Audit\\apps\\[a-fA-F0-9]{12}\z"),"Hash-addressed audit package required");Plain(Package);Verified=true;
 }
 static void SetRoot(string id){Need(Regex.IsMatch(id,@"\A[a-f0-9]{32}\z"),"Full GUID required");RunId=id;Root=@"C:\NookLab\Audit\s-"+id.Substring(0,12);Plain(Root);}
 static void Owner(){var o=Read(Safe("owner.json"));Need(Convert.ToString(o["runId"])==RunId&&Convert.ToString(o["package"])==Package&&Convert.ToString(o["testSha256"])==TestHash,"Exact full GUID/package/test owner required");}
 static ProcessStartInfo Start(string exe,string arguments){var start=new ProcessStartInfo(exe,arguments){WorkingDirectory=Root,UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};start.EnvironmentVariables["TEMP"]=Safe("temp");start.EnvironmentVariables["TMP"]=Safe("temp");start.EnvironmentVariables["MORI_DESK_DATA"]=Safe("data");return start;}
 static int Worker(string id){
  SetRoot(id);Owner();var gate=Stopwatch.StartNew();while(!File.Exists(Safe("start.gate"))){if(gate.Elapsed.TotalSeconds>45)throw new TimeoutException("Parent gate timeout");Thread.Sleep(50);}Need(File.ReadAllText(Safe("start.gate"),Utf8)==TestHash,"Atomic gate pins exact component contract assembly");bool member;Need(IsProcessInJob(GetCurrentProcess(),IntPtr.Zero,out member)&&member,"Worker in Job before invoking tests");
  string executable=Path.Combine(Package,"runtime","ComponentStoreTests.exe");Need(Hash(executable)==HostHash&&Hash(Path.Combine(Package,"runtime","ComponentStoreTests.dll"))==TestHash,"Exact apphost and contract assembly match reviewed build");var start=Start(executable,"\""+Safe("fixture")+"\"");start.RedirectStandardOutput=start.RedirectStandardError=true;var output=new StringBuilder();var errors=new StringBuilder();object sync=new object();bool truncated=false;
  var clock=Stopwatch.StartNew();using(var p=new Process{StartInfo=start}){
   p.OutputDataReceived+=(s,e)=>{if(e.Data==null)return;lock(sync){if(output.Length<180000)output.AppendLine(e.Data);else truncated=true;}};p.ErrorDataReceived+=(s,e)=>{if(e.Data==null)return;lock(sync){if(errors.Length<180000)errors.AppendLine(e.Data);else truncated=true;}};
   Need(p.Start(),"Contract child starts without shell, with one new generated fixture path");bool actualChildInJob;Need(IsProcessInJob(p.Handle,IntPtr.Zero,out actualChildInJob)&&actualChildInJob,"Actual contract process inherited the kill-on-close Job");p.BeginOutputReadLine();p.BeginErrorReadLine();p.WaitForExit();NewText(Safe("stdout.txt"),output.ToString());NewText(Safe("stderr.txt"),errors.ToString());NewJson(Safe("worker-result.json"),new{exitCode=p.ExitCode,elapsedSeconds=clock.Elapsed.TotalSeconds,outputTruncated=truncated,actualChildInJob=actualChildInJob});return p.ExitCode;
  }
 }
 [StructLayout(LayoutKind.Sequential)]struct Basic {public long ProcessTime,JobTime;public uint Flags;public UIntPtr MinimumWorkingSet,MaximumWorkingSet;public uint ActiveProcessLimit;public UIntPtr Affinity;public uint PriorityClass,SchedulingClass;}
 [StructLayout(LayoutKind.Sequential)]struct Io {public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes;}
 [StructLayout(LayoutKind.Sequential)]struct Limits {public Basic Basic;public Io Io;public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;}
 [StructLayout(LayoutKind.Sequential)]struct Accounting {public long UserTime,KernelTime,PeriodUserTime,PeriodKernelTime;public uint PageFaults,TotalProcesses,ActiveProcesses,TerminatedProcesses;}
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr CreateJobObject(IntPtr attrs,string name);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetInformationJobObject(IntPtr job,int type,ref Limits value,int size);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool QueryInformationJobObject(IntPtr job,int type,out Accounting value,int size,IntPtr returned);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool IsProcessInJob(IntPtr process,IntPtr job,out bool value);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool TerminateJobObject(IntPtr job,uint exitCode);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool CloseHandle(IntPtr handle);
 [DllImport("kernel32.dll")]static extern IntPtr GetCurrentProcess();
 static Accounting Info(IntPtr job){Accounting a;if(!QueryInformationJobObject(job,1,out a,Marshal.SizeOf(typeof(Accounting)),IntPtr.Zero))throw new Win32Exception();return a;}
 static void ValidatePayload(){
  string manifest=Path.Combine(Package,"component-contract-files.json");Need(Hash(manifest)==InventoryHash,"Exact reviewed payload inventory");var inventory=Read(manifest);var items=(System.Collections.IEnumerable)inventory["files"];int count=0;long bytes=0;var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  string runtime=Path.Combine(Package,"runtime");Plain(runtime);
  foreach(object raw in items){var item=(Dictionary<string,object>)raw;string relative=Convert.ToString(item["path"]);Need(relative.IndexOf(':')<0&&!Path.IsPathRooted(relative)&&relative.IndexOf('\\')<0,"Plain relative runtime member");string file=Path.GetFullPath(Path.Combine(runtime,relative.Replace('/','\\')));Need(file.StartsWith(runtime+"\\",StringComparison.OrdinalIgnoreCase)&&names.Add(file),"Unique contained runtime file");Plain(file);long length=Convert.ToInt64(item["bytes"]);Need(File.Exists(file)&&new FileInfo(file).Length==length&&Hash(file)==Convert.ToString(item["sha256"]),"Exact payload file: "+relative);count++;bytes+=length;}
  Need(count>100&&count<1000&&bytes<300L*1024*1024,"Bounded self-contained contract payload");Need(Hash(Path.Combine(runtime,"ComponentStoreTests.exe"))==HostHash&&Hash(Path.Combine(runtime,"ComponentStoreTests.dll"))==TestHash,"Exact reviewed apphost and contract assembly");
  foreach(string entry in Directory.GetFiles(runtime,"*",SearchOption.AllDirectories)){Plain(entry);Need(names.Remove(entry),"No unexpected runtime file: "+entry);}Need(names.Count==0,"Exact runtime inventory, no missing or extra files");
 }
 static int Run(){
  string id=Guid.NewGuid().ToString("N");SetRoot(id);Need(!Directory.Exists(Root)&&!File.Exists(Root),"Fresh nonexisting short GUID root");Directory.CreateDirectory(Root);NewJson(Safe("owner.json"),new{runId=id,package=Package,testSha256=TestHash});Directory.CreateDirectory(Safe("temp"));Directory.CreateDirectory(Safe("data"));Need(Directory.GetFileSystemEntries(Safe("temp")).Length==0,"Initially empty private TEMP");
  var report=new Dictionary<string,object>{{"scope","ComponentStore contract, synthetic HTTP and generated component trees; no real external engines"},{"runRoot",Root},{"testSha256",TestHash},{"startedUtc",DateTime.UtcNow.ToString("O")},{"physicalApplicationRun",false},{"actualInstallation",false}};
  IntPtr job=IntPtr.Zero;Process worker=null;Timer timer=null;int timedOut=0;bool childrenStopped=false,passed=false;var clock=Stopwatch.StartNew();
  try{
   ValidatePayload();job=CreateJobObject(IntPtr.Zero,null);if(job==IntPtr.Zero)throw new Win32Exception();var limits=new Limits();limits.Basic.Flags=0x2000;if(!SetInformationJobObject(job,9,ref limits,Marshal.SizeOf(typeof(Limits))))throw new Win32Exception();report["killOnJobClose"]=true;
   worker=Process.Start(Start(Path.Combine(Package,"ComponentStoreGuest.exe"),"--worker "+id));if(!AssignProcessToJobObject(job,worker.Handle))throw new Win32Exception();bool member;Need(IsProcessInJob(worker.Handle,job,out member)&&member,"Worker assigned to kill-on-close Job before gate");
   IntPtr watched=job;timer=new Timer(delegate{Interlocked.Exchange(ref timedOut,1);TerminateJobObject(watched,124);},null,315000,Timeout.Infinite);string pending=Safe("start.gate.pending");NewText(pending,TestHash);File.Move(pending,Safe("start.gate"));
   if(!worker.WaitForExit(300000))throw new TimeoutException("Component contract suite exceeded 300 seconds");report["workerExitCode"]=worker.ExitCode;var wait=Stopwatch.StartNew();while(Info(job).ActiveProcesses!=0&&wait.Elapsed.TotalSeconds<10)Thread.Sleep(100);Need(Info(job).ActiveProcesses==0,"All test descendants exited");childrenStopped=true;var result=Read(Safe("worker-result.json"));report["workerResult"]=result;Need(worker.ExitCode==0&&Convert.ToInt32(result["exitCode"])==0,"Contract wrapper and actual suite exited zero");Need(!Convert.ToBoolean(result["outputTruncated"]),"Contract output is complete");string stdout=File.ReadAllText(Safe("stdout.txt"),Utf8);var match=Regex.Match(stdout,@"(?m)^Component store and synthetic-download checks: (\d+) passed; 0 failed\. No real component root, network, process or desktop\.\r?$");Need(match.Success,"Actual unchanged component contract completion summary present");report["checksPassed"]=int.Parse(match.Groups[1].Value);Need((int)report["checksPassed"]>0,"Actual component assertions executed");report["completionSummary"]=match.Value;Need(Volatile.Read(ref timedOut)==0,"Watchdog did not fire");passed=true;
  }catch(Exception error){report["error"]=error.ToString();}
  finally{
   if(timer!=null)using(var done=new ManualResetEvent(false)){if(timer.Dispose(done))done.WaitOne(5000);}
   if(job!=IntPtr.Zero){try{if(Info(job).ActiveProcesses!=0){TerminateJobObject(job,125);var stop=Stopwatch.StartNew();while(Info(job).ActiveProcesses!=0&&stop.Elapsed.TotalSeconds<10)Thread.Sleep(100);}childrenStopped=Info(job).ActiveProcesses==0;report["jobTotalProcesses"]=Info(job).TotalProcesses;report["jobActiveProcessesAfterStop"]=Info(job).ActiveProcesses;}catch(Exception error){report["jobStopError"]=error.ToString();}CloseHandle(job);}
   if(worker!=null){try{if(!worker.HasExited){worker.Kill();worker.WaitForExit(5000);}}catch{}worker.Dispose();}
  }
  foreach(string name in new[]{"stdout","stderr"})if(File.Exists(Safe(name+".txt")))NewText(Path.Combine(Package,"component-store-"+name+".txt"),File.ReadAllText(Safe(name+".txt"),Utf8));
  var cleanup=new List<object>();report["fixtureCleanup"]="retained for independent review; no deletion by helper";Owner();
  report["cleanup"]=cleanup;report["childrenStopped"]=childrenStopped;report["payloadInventorySha256"]=InventoryHash;report["realExternalEnginesTested"]=false;report["watchdogFired"]=Volatile.Read(ref timedOut)!=0;report["elapsedSeconds"]=Math.Round(clock.Elapsed.TotalSeconds,2);report["passed"]=passed&&childrenStopped;report["checks"]=Checks;report["completedUtc"]=DateTime.UtcNow.ToString("O");NewJson(Path.Combine(Package,"component-store-result.json"),report);return passed&&childrenStopped?0:1;
 }
 [STAThread]static int Main(string[] args){try{Guard();if(args.Length==1&&args[0]=="--run")return Run();if(args.Length==2&&args[0]=="--worker")return Worker(args[1]);throw new InvalidDataException("Explicit VM-only command required");}catch(Exception error){if(Verified&&args.Length==1&&args[0]=="--run")try{string report=Path.Combine(Package,"component-store-result.json");if(!File.Exists(report))NewJson(report,new{passed=false,error=error.ToString(),checks=Checks});}catch{}Console.WriteLine(error);return 1;}}
}
