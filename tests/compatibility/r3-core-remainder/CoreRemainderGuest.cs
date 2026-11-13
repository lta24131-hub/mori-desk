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

[assembly: AssemblyTitle("Nook feature regression wrapper - VM ONLY")]
internal static class CoreRemainderGuest {
 const string AppHash="97B122310B1033BE7EBDA63730F929C256D8D0972CBC3497C7A7D6539CCA50D6";
 const string Target=@"C:\NookLab\Audit\apps\72F61B078B2C";
 static string TestHash,TestName,Label;static int Deadline;static Dictionary<string,object> Plan;
 static void LoadPlan(string label){
  Need(label=="core-remainder","Only reviewed core remainder plan");Label=label;Plan=Read(Path.Combine(Package,"plans",label+".json"));
  TestName=Convert.ToString(Plan["exe"]);TestHash=Convert.ToString(Plan["sha256"]);Deadline=Convert.ToInt32(Plan["timeoutSeconds"]);
  Need(Regex.IsMatch(TestName,@"\A[A-Za-z][A-Za-z0-9.]*\.exe\z")&&Regex.IsMatch(TestHash,@"\A[A-F0-9]{64}\z")&&Deadline>=30&&Deadline<=600,"Bounded pinned helper plan");
  Need(Hash(Path.Combine(Target,"Mori.exe"))==AppHash,"Final product identity verified");
 }
 static void CopySame(string source,string dest){Plain(source);Plain(dest);if(File.Exists(dest)){Need(Hash(source)==Hash(dest),"Existing test helper matches exact bytes");return;}File.Copy(source,dest,false);Need(Hash(source)==Hash(dest),"Copied test-only helper verified");}
 static string Arguments(){var values=(System.Collections.IEnumerable)Plan["args"];var args=new List<string>();foreach(object raw in values){string value=Convert.ToString(raw);if(value=="$root")value=Safe("fixture");else if(value=="$app")value=Path.Combine(Target,"Mori.exe");else if(value=="$worker")value=Path.Combine(Target,@"file-conversion\Nook.FileConversion.exe");Need(value.IndexOf('"')<0&&!value.EndsWith("\\",StringComparison.Ordinal),"Safe explicit argument");args.Add("\""+value+"\"");}return string.Join(" ",args);}

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
  SetRoot(id);Owner();var gate=Stopwatch.StartNew();while(!File.Exists(Safe("start.gate"))){if(gate.Elapsed.TotalSeconds>45)throw new TimeoutException("Parent gate timeout");Thread.Sleep(50);}Need(File.ReadAllText(Safe("start.gate"),Utf8)==TestHash,"Atomic gate pins exact safety test");bool member;Need(IsProcessInJob(GetCurrentProcess(),IntPtr.Zero,out member)&&member,"Worker in Job before invoking tests");
  string executable=Path.Combine(Target,TestName);Need(Hash(executable)==TestHash,"Helper SHA256 matches reviewed build");var start=Start(executable,Arguments());start.RedirectStandardOutput=start.RedirectStandardError=true;var output=new StringBuilder();var errors=new StringBuilder();object sync=new object();bool truncated=false;
  var clock=Stopwatch.StartNew();using(var p=new Process{StartInfo=start}){
   p.OutputDataReceived+=(s,e)=>{if(e.Data==null)return;lock(sync){if(output.Length<600000)output.AppendLine(e.Data);else truncated=true;}};p.ErrorDataReceived+=(s,e)=>{if(e.Data==null)return;lock(sync){if(errors.Length<600000)errors.AppendLine(e.Data);else truncated=true;}};
   Need(p.Start(),"Test child started without shell using fixed reviewed arguments");p.BeginOutputReadLine();p.BeginErrorReadLine();p.WaitForExit();NewText(Safe("stdout.txt"),output.ToString());NewText(Safe("stderr.txt"),errors.ToString());NewJson(Safe("worker-result.json"),new{exitCode=p.ExitCode,elapsedSeconds=clock.Elapsed.TotalSeconds,outputTruncated=truncated});return p.ExitCode;
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
 static long RemoveOwned(string path){path=Path.GetFullPath(path);Need(path.StartsWith(Root+"\\",StringComparison.OrdinalIgnoreCase),"Cleanup exact owned descendant");Plain(path);long bytes=0;foreach(string file in Directory.GetFiles(path)){Plain(file);bytes+=new FileInfo(file).Length;File.SetAttributes(file,FileAttributes.Normal);File.Delete(file);}foreach(string dir in Directory.GetDirectories(path))bytes+=RemoveOwned(dir);Directory.Delete(path,false);return bytes;}
 static int Run(){
  Need(Hash(Path.Combine(Package,TestName))==TestHash,"Sealed helper verified before copy");CopySame(Path.Combine(Package,TestName),Path.Combine(Target,TestName));CopySame(Path.Combine(Target,"Mori.exe.config"),Path.Combine(Target,TestName+".config"));
  string id=Guid.NewGuid().ToString("N");SetRoot(id);Need(!Directory.Exists(Root)&&!File.Exists(Root),"Fresh nonexisting short GUID root");Directory.CreateDirectory(Root);NewJson(Safe("owner.json"),new{runId=id,package=Package,testSha256=TestHash});Directory.CreateDirectory(Safe("temp"));Directory.CreateDirectory(Safe("data"));Need(Directory.GetFileSystemEntries(Safe("temp")).Length==0,"Initially empty private TEMP");
  if(Plan.ContainsKey("marker")){Directory.CreateDirectory(Safe("fixture"));NewText(Safe("fixture\\"+Convert.ToString(Plan["marker"])),"generated VM test fixture");}
  var report=new Dictionary<string,object>{{"scope","VM-only reviewed capture, translation and conversion helper; independent fixture"},{"runRoot",Root},{"testSha256",TestHash},{"startedUtc",DateTime.UtcNow.ToString("O")},{"physicalApplicationRun",false},{"actualInstallation",false}};
  IntPtr job=IntPtr.Zero;Process worker=null;Timer timer=null;int timedOut=0;bool childrenStopped=false,passed=false;var clock=Stopwatch.StartNew();
  try{
   Need(Hash(Path.Combine(Target,TestName))==TestHash,"Parent pins reviewed helper build");job=CreateJobObject(IntPtr.Zero,null);if(job==IntPtr.Zero)throw new Win32Exception();var limits=new Limits();limits.Basic.Flags=0x2000;if(!SetInformationJobObject(job,9,ref limits,Marshal.SizeOf(typeof(Limits))))throw new Win32Exception();report["killOnJobClose"]=true;
   worker=Process.Start(Start(Path.Combine(Package,"CoreRemainderGuest.exe"),"--worker "+Label+" "+id));if(!AssignProcessToJobObject(job,worker.Handle))throw new Win32Exception();bool member;Need(IsProcessInJob(worker.Handle,job,out member)&&member,"Worker assigned to kill-on-close Job before gate");
   IntPtr watched=job;timer=new Timer(delegate{Interlocked.Exchange(ref timedOut,1);TerminateJobObject(watched,124);},null,(Deadline+15)*1000,Timeout.Infinite);string pending=Safe("start.gate.pending");NewText(pending,TestHash);File.Move(pending,Safe("start.gate"));
   if(!worker.WaitForExit(Deadline*1000))throw new TimeoutException("Helper exceeded "+Deadline+" seconds");report["workerExitCode"]=worker.ExitCode;var wait=Stopwatch.StartNew();while(Info(job).ActiveProcesses!=0&&wait.Elapsed.TotalSeconds<10)Thread.Sleep(100);Need(Info(job).ActiveProcesses==0,"All test descendants exited");childrenStopped=true;var result=Read(Safe("worker-result.json"));report["workerResult"]=result;Need(worker.ExitCode==0&&Convert.ToInt32(result["exitCode"])==0,"Safety runner and suite exited zero");Need(!Convert.ToBoolean(result["outputTruncated"]),"Safety output is complete");string stdout=File.ReadAllText(Safe("stdout.txt"),Utf8);var match=Regex.Match(stdout,Convert.ToString(Plan["successRegex"]),RegexOptions.Multiline);Need(match.Success,"Actual helper completion summary present");report["checksPassed"]=int.Parse(match.Groups[1].Value);Need((int)report["checksPassed"]>0,"At least one actual assertion");report["completionSummary"]=match.Value;
   if(Plan.ContainsKey("marker")&&Convert.ToString(Plan["marker"])==".capture-background-sandbox"){string summary=File.ReadAllText(Safe("fixture\\background-summary.txt"),Utf8);Need(summary.Contains("Completed=True")&&summary.Contains("failedGroupsOrSafety=0"),"Capture all requested groups and final cleanup completed");report["captureSummary"]=summary;}
   Need(Volatile.Read(ref timedOut)==0,"Watchdog did not fire");passed=true;
  }catch(Exception error){report["error"]=error.ToString();}
  finally{
   if(timer!=null)using(var done=new ManualResetEvent(false)){if(timer.Dispose(done))done.WaitOne(5000);}
   if(job!=IntPtr.Zero){try{if(Info(job).ActiveProcesses!=0){TerminateJobObject(job,125);var stop=Stopwatch.StartNew();while(Info(job).ActiveProcesses!=0&&stop.Elapsed.TotalSeconds<10)Thread.Sleep(100);}childrenStopped=Info(job).ActiveProcesses==0;report["jobTotalProcesses"]=Info(job).TotalProcesses;report["jobActiveProcessesAfterStop"]=Info(job).ActiveProcesses;}catch(Exception error){report["jobStopError"]=error.ToString();}CloseHandle(job);}
   if(worker!=null){try{if(!worker.HasExited){worker.Kill();worker.WaitForExit(5000);}}catch{}worker.Dispose();}
  }
  string evidence=Safe("fixture\\test-report.txt");if(File.Exists(evidence)){string text=File.ReadAllText(evidence,Utf8);var parts=new List<object>();int start=0,index=0;while(start<text.Length){int take=Math.Min(64000,text.Length-start);if(start+take<text.Length&&char.IsHighSurrogate(text[start+take-1]))take--;string name=Label+"-checks-"+(index++).ToString("D3")+".txt";string file=Path.Combine(Package,name);NewText(file,text.Substring(start,take));parts.Add(new{name,sha256=Hash(file),bytes=new FileInfo(file).Length});start+=take;}report["coreEvidence"]=new{path=evidence,sha256=Hash(evidence),bytes=new FileInfo(evidence).Length,parts};}
  foreach(string name in new[]{"stdout","stderr"})if(File.Exists(Safe(name+".txt")))NewText(Path.Combine(Package,Label+"-"+name+".txt"),File.ReadAllText(Safe(name+".txt"),Utf8));
  var cleanup=new List<object>();report["fixtureCleanup"]="retained for independent owned cleanup";report["cleanup"]=cleanup;report["childrenStopped"]=childrenStopped;report["watchdogFired"]=Volatile.Read(ref timedOut)!=0;report["elapsedSeconds"]=Math.Round(clock.Elapsed.TotalSeconds,2);report["passed"]=passed&&childrenStopped;report["checks"]=Checks;report["completedUtc"]=DateTime.UtcNow.ToString("O");NewJson(Path.Combine(Package,Label+".json"),report);return passed&&childrenStopped?0:1;
 }
 [STAThread]static int Main(string[] args){try{Guard();if(args.Length==2&&args[0]=="--run"){LoadPlan(args[1]);return Run();}if(args.Length==3&&args[0]=="--worker"){LoadPlan(args[1]);return Worker(args[2]);}throw new InvalidDataException("Explicit VM-only command required");}catch(Exception error){if(Verified&&args.Length==2&&args[0]=="--run"&&Label!=null)try{string report=Path.Combine(Package,Label+".json");if(!File.Exists(report))NewJson(report,new{passed=false,error=error.ToString(),checks=Checks});}catch{}Console.WriteLine(error);return 1;}}
}
