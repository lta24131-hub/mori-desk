using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

// Test-fixture generator only. No Nook application, user media or settings.
internal static class MediaFixtureGuest {
 const string ManifestSha="ACA85133E64204B4E03AD85CEC03B784A376127D495CA102456936BFF790D2F4";
 static readonly UTF8Encoding Utf8=new UTF8Encoding(false);
 static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=32*1024*1024};
 static string Package,Root,RunId;static bool Verified;
 static void Need(bool ok,string why){if(!ok)throw new InvalidDataException(why);}
 static void Plain(string path){for(string p=Path.GetFullPath(path);!string.IsNullOrEmpty(p);p=Path.GetDirectoryName(p))if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked fixture path refused: "+p);}
 static string Hash(string path){Plain(path);using(var f=File.OpenRead(path))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(f)).Replace("-","");}
 static Dictionary<string,object> Read(string path){Plain(path);Need(new FileInfo(path).Length<32*1024*1024,"Bounded JSON");return Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path,Utf8));}
 static void Write(string path,object value){Plain(path);using(var f=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read))using(var w=new StreamWriter(f,Utf8)){w.Write(Json.Serialize(value));w.Flush();f.Flush(true);}}
 static string Safe(string relative){string p=Path.GetFullPath(Path.Combine(Root,relative));Need(p.StartsWith(Root+"\\",StringComparison.OrdinalIgnoreCase),"Fresh owned fixture descendant");Plain(p);return p;}
 static void Guard(){bool vm=false;using(var q=new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem"))foreach(ManagementObject row in q.Get())using(row)vm|=Regex.IsMatch(Convert.ToString(row["Manufacturer"])+" "+Convert.ToString(row["Model"]),"QEMU|KVM|Bochs",RegexOptions.IgnoreCase);Need(vm&&Environment.UserName.Equals("NookTester",StringComparison.OrdinalIgnoreCase),"Dedicated NookTester VM required before writes");Package=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');Need(Regex.IsMatch(Package,@"\AC:\\NookLab\\Audit\\apps\\[a-fA-F0-9]{12}\z"),"Hash-addressed package");Plain(Package);Verified=true;}
 static void SetRoot(string id){Need(Regex.IsMatch(id,@"\A[a-f0-9]{32}\z"),"Exact GUID");RunId=id;Root=@"C:\NookLab\Audit\m-"+id.Substring(0,12);Plain(Root);}
 static void VerifyTools(){string p=Path.Combine(Package,"ffmpeg-inputs.json");Need(Hash(p)==ManifestSha,"Pinned tool manifest");var manifest=Read(p);foreach(Dictionary<string,object> row in (IEnumerable)manifest["files"]){string rel=Convert.ToString(row["path"]);Need(Regex.IsMatch(rel,@"\Affmpeg/bin/[A-Za-z0-9.-]+\.(exe|dll)\z"),"Fixed tool filename");string file=Path.Combine(Package,rel.Replace('/','\\'));Need(new FileInfo(file).Length==Convert.ToInt64(row["bytes"])&&Hash(file)==Convert.ToString(row["sha256"]),"Tool dependency exact identity: "+rel);}}
 static ProcessStartInfo Start(string exe,string args){var start=new ProcessStartInfo(exe,args){WorkingDirectory=Root,UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};start.EnvironmentVariables["TEMP"]=Safe("temp");start.EnvironmentVariables["TMP"]=Safe("temp");return start;}
 static string RunTool(string name,string args,int seconds){
  Need(name=="ffmpeg.exe"||name=="ffprobe.exe","Two exact tools only");var start=Start(Path.Combine(Package,"ffmpeg","bin",name),args);start.RedirectStandardOutput=start.RedirectStandardError=true;
  var stdout=new StringBuilder();var stderr=new StringBuilder();object sync=new object();bool truncated=false;int code;
  using(var p=new Process{StartInfo=start}){p.OutputDataReceived+=(s,e)=>{if(e.Data!=null)lock(sync){if(stdout.Length<1024*1024)stdout.AppendLine(e.Data);else truncated=true;}};p.ErrorDataReceived+=(s,e)=>{if(e.Data!=null)lock(sync){if(stderr.Length<1024*1024)stderr.AppendLine(e.Data);else truncated=true;}};Need(p.Start(),"Fixed tool started");IntPtr handle=p.Handle;Need(handle!=IntPtr.Zero,"Native handle retained");p.BeginOutputReadLine();p.BeginErrorReadLine();if(!p.WaitForExit(seconds*1000)){p.Kill();p.WaitForExit(5000);throw new TimeoutException(name+" timeout");}p.WaitForExit();code=p.ExitCode;}
  Write(Safe(name+".json"),new{exitCode=code,stdout=stdout.ToString(),stderr=stderr.ToString(),truncated});Need(code==0&&!truncated,name+" completed with full bounded output");return stdout.ToString();
 }
 static int Worker(string id){
  SetRoot(id);var owner=Read(Safe("owner.json"));Need(Convert.ToString(owner["runId"])==RunId&&Convert.ToString(owner["package"])==Package&&Convert.ToString(owner["manifestSha256"])==ManifestSha,"Exact fixture owner");var wait=Stopwatch.StartNew();while(!File.Exists(Safe("start.gate"))){if(wait.Elapsed.TotalSeconds>45)throw new TimeoutException("Gate timeout");Thread.Sleep(25);}Need(File.ReadAllText(Safe("start.gate"),Utf8)==ManifestSha,"Pinned parent gate");bool member;Need(IsProcessInJob(GetCurrentProcess(),IntPtr.Zero,out member)&&member,"Fixture worker in Job before starting tools");VerifyTools();
  string file=Safe("library-video.mp4");Need(!File.Exists(file),"No fixture overwrite");
  RunTool("ffmpeg.exe","-hide_banner -loglevel error -nostdin -n -f lavfi -i testsrc2=size=640x360:rate=30 -f lavfi -i sine=frequency=440:sample_rate=48000 -t 12 -c:v libx264 -preset ultrafast -crf 30 -pix_fmt yuv420p -r 30 -c:a aac -b:a 64k -threads 2 -movflags +faststart \""+file+"\"",90);
  string probe=RunTool("ffprobe.exe","-v error -show_entries stream=codec_type,codec_name,width,height,r_frame_rate,pix_fmt,duration -show_entries format=duration -of json \""+file+"\"",20);
  var decoded=Json.Deserialize<Dictionary<string,object>>(probe);var streams=((IEnumerable)decoded["streams"]).Cast<Dictionary<string,object>>().ToArray();var video=streams.Single(x=>Convert.ToString(x["codec_type"])=="video");Need(Convert.ToInt32(video["width"])==640&&Convert.ToInt32(video["height"])==360&&Convert.ToString(video["codec_name"])=="h264"&&Convert.ToString(video["pix_fmt"])=="yuv420p"&&Convert.ToString(video["r_frame_rate"])=="30/1","Exact 640x360 H264 yuv420p 30fps");double duration=double.Parse(Convert.ToString(((Dictionary<string,object>)decoded["format"])["duration"]),CultureInfo.InvariantCulture);Need(Math.Abs(duration-12)<.01,"Exact twelve-second duration");Need(streams.Any(x=>Convert.ToString(x["codec_type"])=="audio"&&Convert.ToString(x["codec_name"])=="aac"),"Synthetic AAC audio present");
  Need(new FileInfo(file).Length>0&&new FileInfo(file).Length<=8*1024*1024,"Bounded generated fixture");string b64=Convert.ToBase64String(File.ReadAllBytes(file));var parts=new List<object>();int count=(b64.Length+128*1024-1)/(128*1024);for(int i=0,index=0;i<b64.Length;i+=128*1024,index++){string piece=b64.Substring(i,Math.Min(128*1024,b64.Length-i)),name="media-video-part-"+index.ToString("D3")+".json";byte[] bytes=Convert.FromBase64String(piece);string digest;using(var h=SHA256.Create())digest=BitConverter.ToString(h.ComputeHash(bytes)).Replace("-","");Write(Path.Combine(Package,name),new{index,count,bytes=bytes.Length,sha256=digest,data=piece});parts.Add(new{index,count,name,bytes=bytes.Length,sha256=digest,encodedLength=piece.Length});}Write(Safe("worker-result.json"),new{passed=true,source="VM-generated lavfi testsrc2 and sine only",probe=decoded,file=new{path=file,bytes=new FileInfo(file).Length,sha256=Hash(file),parts}});return 0;
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
 static int Run(){
  VerifyTools();SetRoot(Guid.NewGuid().ToString("N"));Need(!Directory.Exists(Root)&&!File.Exists(Root),"Fresh fixture root");Directory.CreateDirectory(Root);Write(Safe("owner.json"),new{runId=RunId,package=Package,manifestSha256=ManifestSha});Directory.CreateDirectory(Safe("temp"));var report=new Dictionary<string,object>{{"runRoot",Root},{"scope","VM synthetic media generation only; no application or actual user content"},{"manifestSha256",ManifestSha}};
  IntPtr job=IntPtr.Zero;Process worker=null;Timer watchdog=null;int fired=0;bool passed=false,stopped=false;
  try{job=CreateJobObject(IntPtr.Zero,null);if(job==IntPtr.Zero)throw new Win32Exception();var limits=new Limits();limits.Basic.Flags=0x2000;if(!SetInformationJobObject(job,9,ref limits,Marshal.SizeOf(typeof(Limits))))throw new Win32Exception();worker=Process.Start(Start(Path.Combine(Package,"MediaFixtureGuest.exe"),"--worker "+RunId));IntPtr handle=worker.Handle;if(!AssignProcessToJobObject(job,handle))throw new Win32Exception();bool member;Need(IsProcessInJob(handle,job,out member)&&member,"Generator assigned to kill-on-close Job before gate");IntPtr watched=job;watchdog=new Timer(delegate{Interlocked.Exchange(ref fired,1);TerminateJobObject(watched,124);},null,165000,Timeout.Infinite);string pending=Safe("start.pending");using(var f=new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var w=new StreamWriter(f,Utf8)){w.Write(ManifestSha);w.Flush();f.Flush(true);}File.Move(pending,Safe("start.gate"));if(!worker.WaitForExit(150000))throw new TimeoutException("Generator exceeded 150 seconds");report["workerExitCode"]=worker.ExitCode;Need(worker.ExitCode==0,"Generator completed");var result=Read(Safe("worker-result.json"));Need(Convert.ToBoolean(result["passed"]),"Real output/probe validated");report["result"]=result;passed=true;}
  catch(Exception error){report["error"]=error.ToString();}
  finally{if(watchdog!=null)using(var done=new ManualResetEvent(false)){if(watchdog.Dispose(done))done.WaitOne(5000);}if(job!=IntPtr.Zero){try{if(Info(job).Active!=0)TerminateJobObject(job,125);var wait=Stopwatch.StartNew();while(Info(job).Active!=0&&wait.Elapsed.TotalSeconds<10)Thread.Sleep(100);stopped=Info(job).Active==0;report["jobActiveAfterStop"]=Info(job).Active;report["jobTotalProcesses"]=Info(job).Total;}catch(Exception e){report["stopError"]=e.ToString();}CloseHandle(job);}if(worker!=null){try{if(!worker.HasExited){worker.Kill();worker.WaitForExit(5000);}}catch{}worker.Dispose();}}
  foreach(string name in new[]{"ffmpeg.exe.json","ffprobe.exe.json","worker-error.json"})if(File.Exists(Safe(name)))report[name]=Read(Safe(name));report["passed"]=passed&&stopped&&fired==0;report["childrenStopped"]=stopped;report["watchdogFired"]=fired!=0;Write(Path.Combine(Package,"media-fixture-result.json"),report);return passed&&stopped&&fired==0?0:1;
 }
 [STAThread]static int Main(string[] args){try{Guard();if(args.Length==1&&args[0]=="--run")return Run();if(args.Length==2&&args[0]=="--worker")return Worker(args[1]);throw new InvalidDataException("Explicit VM-only mode required");}catch(Exception e){if(Verified&&Root!=null&&args.Length==2)try{Write(Safe("worker-error.json"),new{error=e.ToString()});}catch{}Console.WriteLine(e);return 1;}}
}
