using System;
using System.Collections;
using System.Collections.Generic;
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
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

[assembly: AssemblyTitle("Nook actual library handoff - VM TEST ONLY")]
// Uses the unchanged product binary for real transfer, child ContentRendered,
// ready/commit events and route activation. No installed user profile is used.
internal static class LibraryHandoffGuest {
 const string AppHash="97B122310B1033BE7EBDA63730F929C256D8D0972CBC3497C7A7D6539CCA50D6";
 static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=1048576,RecursionLimit=20};
 static readonly UTF8Encoding Utf8=new UTF8Encoding(false);static readonly List<string> Checks=new List<string>();
 static string Package,Root,Target;static bool GuestVerified;static Process Child;static DateTime ChildStarted;static bool ForcedChildCleanup;static IntPtr OwnedJob,ChildHandle;static uint JobActiveAtCompletion,JobTotalProcesses,ChildExitCode;
 [StructLayout(LayoutKind.Sequential)]struct JobBasicLimits{public long ProcessTime,JobTime;public uint Flags;public UIntPtr MinimumWorkingSet,MaximumWorkingSet;public uint ActiveLimit;public UIntPtr Affinity;public uint Priority,Scheduling;}
 [StructLayout(LayoutKind.Sequential)]struct JobIoCounters{public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes;}
 [StructLayout(LayoutKind.Sequential)]struct JobExtendedLimits{public JobBasicLimits Basic;public JobIoCounters Io;public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;}
 [StructLayout(LayoutKind.Sequential)]struct JobAccounting{public long UserTime,KernelTime,PeriodUserTime,PeriodKernelTime;public uint PageFaults,TotalProcesses,ActiveProcesses,TerminatedProcesses;}
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr CreateJobObject(IntPtr security,string name);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetInformationJobObject(IntPtr job,int kind,ref JobExtendedLimits limits,uint size);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool QueryInformationJobObject(IntPtr job,int kind,out JobAccounting value,uint size,IntPtr returned);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
 [DllImport("kernel32.dll")]static extern IntPtr GetCurrentProcess();
 [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetExitCodeProcess(IntPtr process,out uint exitCode);
 [DllImport("user32.dll")]static extern bool IsWindowEnabled(IntPtr window);
 [DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr window);
 static void Need(bool value,string message){if(!value)throw new InvalidDataException(message);Checks.Add(message);}
 static string Text(object value){return Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture);}
 static void NoLinks(string path){for(string part=Path.GetFullPath(path);!string.IsNullOrEmpty(part);part=Path.GetDirectoryName(part))if((File.Exists(part)||Directory.Exists(part))&&(File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked fixture path refused.");}
 static string Hash(string path){NoLinks(path);using(var file=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");}
 static void Save(string path,object value){NoLinks(path);using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new StreamWriter(file,Utf8)){writer.Write(Json.Serialize(value));writer.Flush();file.Flush(true);}}
 static void OwnProcessTree(){
  IntPtr job=CreateJobObject(IntPtr.Zero,null);if(job==IntPtr.Zero)throw new IOException("Cannot create owned process job: "+Marshal.GetLastWin32Error());
  bool assigned=false;try{var limits=new JobExtendedLimits{Basic=new JobBasicLimits{Flags=0x2000}};if(!SetInformationJobObject(job,9,ref limits,(uint)Marshal.SizeOf(typeof(JobExtendedLimits)))||!AssignProcessToJobObject(job,GetCurrentProcess()))throw new IOException("Cannot contain VM process tree: "+Marshal.GetLastWin32Error());assigned=true;OwnedJob=job;Need(true,"Kill-on-close Job contains this helper and all non-breakaway descendants before any application window is created.");}
  finally{if(!assigned)CloseHandle(job);}
  // Intentionally retain the non-inheritable handle until process teardown.
  // The OS closes it even after watchdog Environment.Exit, killing descendants.
 }
 static bool OnlyHelper(){JobAccounting value;if(OwnedJob==IntPtr.Zero||!QueryInformationJobObject(OwnedJob,1,out value,(uint)Marshal.SizeOf(typeof(JobAccounting)),IntPtr.Zero))throw new IOException("Cannot query owned process job.");JobActiveAtCompletion=value.ActiveProcesses;JobTotalProcesses=value.TotalProcesses;return value.ActiveProcesses==1;}
 static void CheckProcessTree(){Until(OnlyHelper,8000,"Owned application processes did not finish draining after native close.");Need(OnlyHelper(),"Owned Job contains only this helper after normal child cleanup; no application descendants remain.");}
 static void Guest(){
  bool vm=false;using(var query=new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem"))foreach(ManagementObject row in query.Get())using(row)vm|=Regex.IsMatch(Text(row["Manufacturer"])+" "+Text(row["Model"]),"QEMU|KVM|Bochs",RegexOptions.IgnoreCase);
  Need(vm&&Environment.UserName.Equals("NookTester",StringComparison.OrdinalIgnoreCase),"Dedicated NookTester QEMU guest required before any write.");GuestVerified=true;
  Package=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');Need(Regex.IsMatch(Package,@"\AC:\\NookLab\\Audit\\apps\\[a-fA-F0-9]{12}\z"),"Hash-addressed VM package required.");NoLinks(Package);
  string manifestFile=Path.Combine(Package,"library-handoff-manifest.json");Need(new FileInfo(manifestFile).Length<1048576,"Bounded manifest required.");var manifest=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(manifestFile));
  var files=((IEnumerable)manifest["files"]).Cast<Dictionary<string,object>>().ToArray();Need(files.Length>0&&files.Length<80,"Bounded sealed payload required.");
  foreach(var entry in files){string relative=Text(entry["path"]);Need(!Path.IsPathRooted(relative)&&!relative.Split('/','\\').Contains(".."),"Relative payload required.");string path=Path.GetFullPath(Path.Combine(Package,relative));Need(path.StartsWith(Package+"\\",StringComparison.OrdinalIgnoreCase)&&Hash(path)==Text(entry["sha256"]),"Sealed payload verified: "+relative);}
  Need(Hash(Path.Combine(Package,"Mori.exe"))==AppHash,"Exact final 0.1.45 candidate required.");
  foreach(string name in new[]{"Mori","Nook","NookLauncher"}){var processes=Process.GetProcessesByName(name);try{Need(processes.Length==0,"No existing "+name+" process may share this VM handoff run.");}finally{foreach(var process in processes)process.Dispose();}}
  Root=@"C:\NookLab\Audit\library-handoff-"+Guid.NewGuid().ToString("N");NoLinks(Root);Need(!Directory.Exists(Root),"Fresh isolated fixture required.");Directory.CreateDirectory(Root);Target=Path.Combine(Root,"target");Environment.SetEnvironmentVariable("MORI_DESK_DATA",Path.Combine(Root,"profile"));
 }
 static void Until(Func<bool> complete,int milliseconds,string stage){
  var clock=Stopwatch.StartNew();var frame=new DispatcherFrame();Exception failure=null;var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(30)};
  timer.Tick+=(s,e)=>{try{if(complete())frame.Continue=false;else if(clock.ElapsedMilliseconds>milliseconds){failure=new TimeoutException(stage);frame.Continue=false;}}catch(Exception error){failure=error;frame.Continue=false;}};
  timer.Start();try{if(!complete())Dispatcher.PushFrame(frame);if(failure!=null)throw failure;}finally{timer.Stop();}
 }
 static void FindChild(){
  if(Child!=null)return;int own=Process.GetCurrentProcess().Id;using(var query=new ManagementObjectSearcher("SELECT ProcessId,ExecutablePath,CommandLine FROM Win32_Process WHERE ParentProcessId="+own))foreach(ManagementObject row in query.Get())using(row){
   string path=Text(row["ExecutablePath"]),command=Text(row["CommandLine"]);if(!string.Equals(path,Path.Combine(Package,"Mori.exe"),StringComparison.OrdinalIgnoreCase)||!command.Contains("--library \""+Target+"\" --handoff \"Local\\Mori-Handoff-"))continue;
   var process=Process.GetProcessById(Convert.ToInt32(row["ProcessId"]));if(process.HasExited){process.Dispose();continue;}if(Child!=null){process.Dispose();throw new IOException("More than one handoff child was created.");}ChildHandle=process.Handle;Child=process;ChildStarted=process.StartTime.ToUniversalTime();
  }
 }
 static bool ChildOwned(){try{return Child!=null&&!Child.HasExited&&Child.StartTime.ToUniversalTime()==ChildStarted&&string.Equals(Child.MainModule.FileName,Path.Combine(Package,"Mori.exe"),StringComparison.OrdinalIgnoreCase);}catch(InvalidOperationException){return false;}}
 static void Picture(string file){byte[] pixels=new byte[96*64*4];for(int i=0;i<pixels.Length;i+=4){pixels[i]=33;pixels[i+1]=109;pixels[i+2]=207;pixels[i+3]=255;}var image=BitmapSource.Create(96,64,96,96,PixelFormats.Bgra32,null,pixels,96*4);image.Freeze();var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var stream=new FileStream(file,FileMode.CreateNew))encoder.Save(stream);}
 static int Run(){
  var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};Mori.MainWindow main=null;Mori.Library library=null;Exception failure=null;bool passed=false,oldClosed=false,childEnabled=false,childExited=false;int childId=0;string sourceHash=null,assetId=null;
  try{
   MoriDesk.Entry.InteractiveShell=true;MoriDesk.Entry.ClassicEnabled=true;MoriDesk.Entry.BoardEnabled=false;
   string source=Path.Combine(Root,"source"),input=Path.Combine(Root,"original.png");new Mori.Settings{LibraryPath=source,CloseToTray=false}.Save();Picture(input);library=new Mori.Library(source);var asset=library.Import(input,"","handoff original","https://example.com/fixture","handoff tag","handoff note").Item;assetId=asset.Id;string original=library.FullPath(asset);sourceHash=Hash(original);
   main=new Mori.MainWindow(library){PreviewMode=false};main.DeferDeskServices();main.Window.Title="Nook · 图库真实交接验收";bool rendered=false;main.Window.ContentRendered+=(s,e)=>rendered=true;main.Window.Closed+=(s,e)=>oldClosed=true;main.Window.Show();SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));Until(()=>rendered&&main.Window.IsLoaded,12000,"Source main window did not render.");app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
   Need(!main.PreviewMode&&main.Window.IsVisible&&new WindowInteropHelper(main.Window).Handle!=IntPtr.Zero,"Actual non-preview source MainWindow is native and rendered; auxiliary parent services are deferred.");
   var route=typeof(Mori.MainWindow).GetMethod("RunLibraryRouteChange",BindingFlags.Instance|BindingFlags.NonPublic);
   var transfer=Task.Run(()=>route.Invoke(main,new object[]{(Action)(()=>Mori.LibraryTransfer.Transfer(library,Target,null,()=>false,path=>Mori.LibraryActivation.Open(path,false)))}));
   Until(()=>transfer.IsCompleted,25000,"Real library transfer and ready/commit handoff did not finish.");transfer.GetAwaiter().GetResult();
   Need(Mori.LibraryTransfer.SameRoot(Mori.Settings.Load().LibraryPath,Target),"Real non-preview Open returned after child ContentRendered readiness and committing the new persistent route.");
   FindChild();Need(Child!=null&&ChildOwned(),"Replacement is the exact owned candidate child with the generated target and handoff command.");childId=Child.Id;
   main.CloseForReplacement();Until(()=>oldClosed&&!main.Window.IsVisible,6000,"Source window did not close after successful handoff.");Need(oldClosed,"Source MainWindow closes normally after the committed handoff.");
   Until(()=>{if(Child.HasExited)throw new IOException("Replacement child exited before route activation.");Child.Refresh();return Child.MainWindowHandle!=IntPtr.Zero&&IsWindowVisible(Child.MainWindowHandle)&&IsWindowEnabled(Child.MainWindowHandle);},12000,"Replacement child did not enable its native window after CompleteLibraryRoute.");childEnabled=true;
   Need(childEnabled,"Replacement native child is visible and enabled after the actual commit event and route activation.");
   Need(Hash(original)==sourceHash&&Hash(input)==sourceHash,"Successful handoff leaves both the original imported bytes and input unchanged.");
   Need(Child.CloseMainWindow(),"Owned replacement accepts a normal native close request.");Until(()=>Child.HasExited,12000,"Replacement child did not exit normally with CloseToTray disabled.");childExited=true;Need(GetExitCodeProcess(ChildHandle,out ChildExitCode)&&ChildExitCode==0,"Replacement child exits normally with code zero from its retained native process handle.");
   using(var copied=new Mori.Library(Target)){var saved=copied.All().Single();Need(saved.Id==asset.Id&&saved.Name==asset.Name&&saved.Tags==asset.Tags&&saved.Notes==asset.Notes&&Hash(copied.FullPath(saved))==sourceHash,"Reopening the real target retains image identity, user metadata and all original bytes.");}
   Need(Mori.LibraryTransfer.SameRoot(Mori.Settings.Load().LibraryPath,Target)&&!Mori.Settings.Load().CloseToTray,"Child shutdown retains the committed target route and the explicit close behavior.");Need(Hash(original)==sourceHash&&library.All().Count==1,"Source library remains complete and recoverable after replacement exits.");passed=true;
  }catch(Exception error){failure=error is TargetInvocationException&&error.InnerException!=null?error.InnerException:error;}
  finally{
   try{FindChild();if(ChildOwned()){Child.CloseMainWindow();Until(()=>Child.HasExited,4000,"Owned child cleanup did not finish normally.");}}catch(Exception error){if(failure==null)failure=error;passed=false;}
   if(ChildOwned()){ForcedChildCleanup=true;passed=false;Child.Kill();Child.WaitForExit(4000);}if(Child!=null)Child.Dispose();
   try{if(main!=null&&main.Window.IsVisible)main.CloseForReplacement();MoriDesk.DeskRuntime.Stop();if(app.Windows.Cast<Window>().Any(window=>window.IsVisible)){passed=false;if(failure==null)failure=new IOException("Fixture window remains visible.");}}catch(Exception error){passed=false;if(failure==null)failure=error;}
   if(library!=null)library.Dispose();app.Shutdown();try{CheckProcessTree();}catch(Exception error){passed=false;if(failure==null)failure=error;}
  }
  Save(Path.Combine(Package,"library-handoff-result.json"),new{passed,appSha256=Hash(Path.Combine(Package,"Mori.exe")),scope="VM-only real LibraryTransfer plus guarded non-preview LibraryActivation.Open child readiness/commit, old MainWindow close and child native enable/normal exit. Source auxiliary services deferred; no production profile or installed shell lease.",checks=Checks,fixture=Root,target=Target,sourceHash,assetId,childId,oldClosed,childEnabled,childExited,childExitCode=ChildExitCode,forcedChildCleanup=ForcedChildCleanup,killOnJobClose=true,jobActiveAtCompletion=JobActiveAtCompletion,jobActiveIncludesHelper=true,jobTotalProcesses=JobTotalProcesses,hostExecuted=false,installedApplicationTouched=false,error=failure==null?null:failure.ToString()});return passed?0:1;
 }
 [STAThread]static int Main(string[] args){try{Guest();Need(args.Length==1&&args[0]=="--run","Explicit VM command required.");OwnProcessTree();using(var watchdog=new Timer(_=>Environment.Exit(124),null,90000,Timeout.Infinite))return Run();}catch(Exception error){Console.WriteLine(error);if(GuestVerified&&Package!=null&&Regex.IsMatch(Package,@"\AC:\\NookLab\\Audit\\apps\\[a-fA-F0-9]{12}\z"))try{string result=Path.Combine(Package,"library-handoff-result.json");if(!File.Exists(result))Save(result,new{passed=false,error=error.ToString(),checks=Checks});}catch{}return 1;}}
}
