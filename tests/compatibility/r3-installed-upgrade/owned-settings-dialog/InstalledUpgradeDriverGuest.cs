using System;
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
using System.Web.Script.Serialization;
using System.Windows.Automation;
using Microsoft.Win32;

[assembly: AssemblyTitle("Nook actual public upgrade driver - VM TEST ONLY")]
// Reuses the November VmDriver actual no-argument install entry. All UIA
// actions are scoped to the exact started installer or verified installed PID.
// This is not --verify, reflection installation, or a simulated transaction.
internal static class InstalledUpgradeDriverGuest {
 const string OldSetup="DABCF2BA17300A7CE5B64AD342188207C81CBACE35E14AA28E67753A1F4F666B",NewSetup="15BD55114642B697723731D53D3D1B98ABCBF00F9A399F500FBE26AC16650994";
 const string OldApp="0023F5CF9C7B6021ED8071E9936EB20282C1FBBE467716B33146AA52110662EB",NewApp="97B122310B1033BE7EBDA63730F929C256D8D0972CBC3497C7A7D6539CCA50D6";
 static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=4*1024*1024,RecursionLimit=40};static readonly UTF8Encoding Utf8=new UTF8Encoding(false);static readonly List<string> Checks=new List<string>();static readonly List<object> Actions=new List<object>();
 static string Package,Data,Program,Operation;static IntPtr Job;static bool Forced;static uint JobActive,JobTotal;
 [StructLayout(LayoutKind.Sequential)]struct Basic{public long ProcessTime,JobTime;public uint Flags;public UIntPtr MinimumWorkingSet,MaximumWorkingSet;public uint ActiveLimit;public UIntPtr Affinity;public uint Priority,Scheduling;}
 [StructLayout(LayoutKind.Sequential)]struct Io{public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes;}
 [StructLayout(LayoutKind.Sequential)]struct Extended{public Basic Basic;public Io Io;public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;}
 [StructLayout(LayoutKind.Sequential)]struct Accounting{public long UserTime,KernelTime,PeriodUserTime,PeriodKernelTime;public uint PageFaults,TotalProcesses,ActiveProcesses,TerminatedProcesses;}
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr CreateJobObject(IntPtr security,string name);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetInformationJobObject(IntPtr job,int kind,ref Extended limits,uint size);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool QueryInformationJobObject(IntPtr job,int kind,out Accounting value,uint size,IntPtr returned);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
 [DllImport("kernel32.dll")]static extern IntPtr GetCurrentProcess();
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetExitCodeProcess(IntPtr process,out uint code);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool IsProcessInJob(IntPtr process,IntPtr job,out bool contained);
 [DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr window);
 [DllImport("user32.dll")]static extern bool IsWindowEnabled(IntPtr window);
 static void Need(bool value,string reason){if(!value)throw new InvalidDataException(reason);Checks.Add(reason);}
 static string Text(object value){return Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture);}
 static void NoLinks(string path){for(string part=Path.GetFullPath(path);!string.IsNullOrEmpty(part);part=Path.GetDirectoryName(part))if((Directory.Exists(part)||File.Exists(part))&&(File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked upgrade path refused.");}
 static string Hash(string file){NoLinks(file);using(var input=File.OpenRead(file))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(input)).Replace("-","");}
 static Dictionary<string,object> Read(string file){NoLinks(file);if(new FileInfo(file).Length>4*1024*1024)throw new InvalidDataException("Bounded upgrade evidence required.");return Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(file,Utf8));}
 static void Save(string file,object value){NoLinks(file);using(var output=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new StreamWriter(output,Utf8)){writer.Write(Json.Serialize(value));writer.Flush();output.Flush(true);}}
 static void Guest(){
  bool found=false;using(var query=new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem"))foreach(ManagementObject row in query.Get())using(row)found|=Regex.IsMatch(Text(row["Manufacturer"])+" "+Text(row["Model"]),"QEMU|KVM|Bochs",RegexOptions.IgnoreCase);
  Need(found&&Environment.UserName.Equals("NookTester",StringComparison.OrdinalIgnoreCase),"Dedicated NookTester QEMU VM required before any action.");
  Package=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');Need(Regex.IsMatch(Package,@"\AC:\\NookLab\\Audit\\apps\\[a-fA-F0-9]{12}\z"),"Hash-addressed driver package required.");NoLinks(Package);
  Data=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MoriDesk");Program=Path.Combine(Path.GetDirectoryName(Data),"Programs","MoriDesk");Need(Data==@"C:\Users\NookTester\AppData\Local\MoriDesk","Exact dedicated-user physical profile.");NoLinks(Data);NoLinks(Program);
  using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Nook\Installation")){object value=key==null?null:key.GetValue("ProgramRoot");Need(value==null||Text(value).Equals(Program,StringComparison.OrdinalIgnoreCase),"Existing root must be this user's actual default root.");}
  Job=CreateJobObject(IntPtr.Zero,null);if(Job==IntPtr.Zero)throw new IOException("Job creation failed.");var limits=new Extended{Basic=new Basic{Flags=0x2000}};Need(SetInformationJobObject(Job,9,ref limits,(uint)Marshal.SizeOf(typeof(Extended)))&&AssignProcessToJobObject(Job,GetCurrentProcess()),"Own helper and descendants in a kill-on-close Job before launch; inherited handle is disabled.");
 }
 static bool OnlyHelper(){Accounting value;if(!QueryInformationJobObject(Job,1,out value,(uint)Marshal.SizeOf(typeof(Accounting)),IntPtr.Zero))throw new IOException("Cannot query owned process tree.");JobActive=value.ActiveProcesses;JobTotal=value.TotalProcesses;return JobActive==1;}
 static void Wait(Func<bool> complete,int milliseconds,string reason){var clock=Stopwatch.StartNew();while(!complete()){if(clock.ElapsedMilliseconds>milliseconds)throw new TimeoutException(reason);Thread.Sleep(100);}}
 static void NoProduct(){foreach(string name in new[]{"Mori","Nook","Nook-Setup"}){var processes=Process.GetProcessesByName(name);try{Need(processes.Length==0,"No existing "+name+" process before a new installed stage.");}finally{foreach(var process in processes)process.Dispose();}}}
 static Dictionary<string,object> Active(string version,string hash){var record=Read(Path.Combine(Program,"active-install.json"));string relative=Text(record["executable"]);Need(Regex.IsMatch(relative,@"\A[0-9][A-Za-z0-9.-]{1,120}\\Mori\.exe\z"),"Strict committed executable location.");string file=Path.Combine(Program,relative);Need(Text(record["version"])==version&&Text(record["sha256"]).Equals(hash,StringComparison.OrdinalIgnoreCase)&&Hash(file)==hash,"Committed installed record matches the exact expected public/candidate binary.");return record;}
 static AutomationElement[] Windows(int pid){return AutomationElement.RootElement.FindAll(TreeScope.Children,new PropertyCondition(AutomationElement.ProcessIdProperty,pid)).Cast<AutomationElement>().ToArray();}
 static AutomationElement Find(AutomationElement root,string name,ControlType type){return root.FindFirst(TreeScope.Descendants,new AndCondition(new PropertyCondition(AutomationElement.NameProperty,name),new PropertyCondition(AutomationElement.ControlTypeProperty,type)));}
 static void Invoke(AutomationElement element,int pid,string label){Need(element!=null&&element.Current.ProcessId==pid&&element.Current.IsEnabled,"Exact enabled owned UI action: "+label);object value;Need(element.TryGetCurrentPattern(InvokePattern.Pattern,out value),"Owned control supports invoke: "+label);((InvokePattern)value).Invoke();Actions.Add(new{pid,action=label,utc=DateTime.UtcNow.ToString("O")});}
 static void DriveInstaller(Process process,bool newer,ref int step,bool committed){foreach(var window in Windows(process.Id)){
   string title=window.Current.Name;
   if(!newer&&step==0&&title=="Nook · 安装与维护") {var text=window.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Text)).Cast<AutomationElement>().Select(e=>e.Current.Name);Need(text.Any(t=>t.Contains("准备安装 Nook 0.1.42")),"Actual public installer confirmation names 0.1.42.");var ok=window.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"1"));Invoke(ok,process.Id,"Confirm actual public 0.1.42 installation");step=1;return;}
   if(newer&&title=="Nook 安装"){
    if(step==0&&Find(window,"安装位置",ControlType.Text)!=null){Invoke(Find(window,"下一步",ControlType.Button),process.Id,"Keep existing default install location");step=1;return;}
    if(step==1&&Find(window,"可选组件",ControlType.Text)!=null){Invoke(Find(window,"下一步",ControlType.Button),process.Id,"Keep inherited installed components");step=2;return;}
    if(step==2&&Find(window,"准备安装",ControlType.Text)!=null){Invoke(Find(window,"开始安装",ControlType.Button),process.Id,"Start actual 0.1.45 replacement installation");step=3;return;}
    var finish=Find(window,"完成",ControlType.Button);if(finish!=null&&finish.Current.IsEnabled){Need(committed,"Never dismiss a failed wizard as a successful installation.");Invoke(finish,process.Id,"Finish completed install and cleanup window");step=4;return;}
   }
   if(!newer&&step==1&&committed&&(title=="Nook · 安装完成"||title=="Nook · 更新完成")){Invoke(window.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"1")),process.Id,"Close confirmed successful public installer result");step=2;return;}
  }}
 static bool TrySession(string expected,out string session){session=null;string updates=Path.Combine(Data,"updates");if(!Directory.Exists(updates))return false;var found=new List<string>();foreach(string folder in Directory.GetDirectories(updates)){if(!Regex.IsMatch(Path.GetFileName(folder),@"\A[a-f0-9]{32}\z"))continue;string result=Path.Combine(folder,"result.json");if(!File.Exists(result))continue;var value=Read(result);object version,status;if(value.TryGetValue("expectedVersion",out version)&&Text(version)==expected&&value.TryGetValue("status",out status)&&Text(status)=="installed")found.Add(folder);}if(found.Count>1)throw new InvalidDataException("Only one successful session for this installation version is allowed.");if(found.Count==0)return false;session=found[0];return true;}
 static object Session(string session,string version,string hash,out Process child){
  var active=Active(version+".0",hash);string app=Path.Combine(Program,Text(active["executable"]));var receipt=Read(Path.Combine(session,"startup-ready.json"));Need(Text(receipt["expectedVersion"])==version&&Text(receipt["executable"]).Equals(app,StringComparison.OrdinalIgnoreCase),"Real startup receipt identifies the committed candidate.");
  child=Process.GetProcessById(Convert.ToInt32(receipt["processId"]));Need(!child.HasExited&&child.StartTime.ToUniversalTime().Ticks==Convert.ToInt64(receipt["startedUtcTicks"])&&child.MainModule.FileName.Equals(app,StringComparison.OrdinalIgnoreCase),"Receipt PID/start time refer to the actual live installed child.");bool contained;Need(IsProcessInJob(child.Handle,Job,out contained)&&contained,"Actual installed child belongs to this run's owned process Job.");var running=child;Wait(()=>{running.Refresh();return running.MainWindowHandle!=IntPtr.Zero&&IsWindowVisible(running.MainWindowHandle)&&IsWindowEnabled(running.MainWindowHandle);},15000,"Installed native window did not become visible and enabled.");
  var evidence=new Dictionary<string,object>();foreach(string name in new[]{"request.json","ready.json","candidate.json","startup-ready.json","result.json","installer-cleanup.json"}){string file=Path.Combine(session,name);if(File.Exists(file))evidence[name]=Read(file);}return new{session,active,receipt,evidence,childId=child.Id,childNativeWindow=child.MainWindowHandle.ToInt64()};
 }
 static void CloseApp(Process app,string expectedHash){Need(!app.HasExited&&Hash(app.MainModule.FileName)==expectedHash,"Only the verified installed child may be closed.");IntPtr handle=app.Handle;bool closeToTray=true;string settings=Path.Combine(Data,"settings.json");if(File.Exists(settings)){var value=Read(settings);object setting;if(value.TryGetValue("CloseToTray",out setting)&&setting is bool)closeToTray=(bool)setting;}
  if(!closeToTray){Need(app.CloseMainWindow(),"Verified installed app accepts normal native close.");}
  else{var root=AutomationElement.FromHandle(app.MainWindowHandle);var more=root.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"More"));Invoke(more,app.Id,"Open owned app More menu");AutomationElement exit=null;Wait(()=>{foreach(var window in Windows(app.Id)){exit=Find(window,"退出 Nook",ControlType.MenuItem);if(exit!=null)return true;}return false;},6000,"Owned exit menu item did not appear.");Invoke(exit,app.Id,"Exit Nook normally through its actual menu");}
  Wait(()=>app.HasExited,15000,"Installed application refused normal close; preserve evidence.");uint exitCode;Need(GetExitCodeProcess(handle,out exitCode)&&exitCode==0,"Installed app exits normally with code zero from its retained native handle.");
 }
 static ProcessStartInfo Start(string path){var start=new ProcessStartInfo(path){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(path)};start.EnvironmentVariables.Remove("MORI_DESK_DATA");start.EnvironmentVariables["TEMP"]=start.EnvironmentVariables["TMP"]=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Temp");return start;}
 static object Install(bool newer,string setup){
  Need(Regex.IsMatch(setup,@"\AC:\\NookLab\\Audit\\apps\\[a-fA-F0-9]{12}\\Nook-Setup\.exe\z"),"Exact hash-addressed genuine setup file required.");Need(Hash(setup)==(newer?NewSetup:OldSetup),"Exact published/final installer SHA256.");NoProduct();
  string oldFolder=null,launcher=Path.Combine(Program,"Launcher","Mori.exe"),launcherBefore=null;
  if(newer){var old=Active("0.1.42.0",OldApp);oldFolder=Path.GetDirectoryName(Path.Combine(Program,Text(old["executable"])));launcherBefore=Hash(launcher);Need(File.Exists(@"C:\NookLab\Upgrade42To45-20261113-r3-healthy\baseline42.json"),"Completed old-version fixture baseline before upgrade.");}
  else Need(!Directory.Exists(Program),"Public baseline installation starts in the cleared dedicated default root.");
  int step=0;string session=null;object evidence=null;Process child=null;var clock=Stopwatch.StartNew();using(var process=Process.Start(Start(setup))){
   while(!process.HasExited){bool committed=TrySession(newer?"0.1.45":"0.1.42",out session);DriveInstaller(process,newer,ref step,committed);if(clock.Elapsed.TotalSeconds>720)throw new TimeoutException("Real installer exceeded its bounded VM budget.");Thread.Sleep(200);}
   Need(process.ExitCode==0,"Actual no-argument installer exits with zero.");Need(TrySession(newer?"0.1.45":"0.1.42",out session),"Installer produced an actual installed transaction result.");evidence=Session(session,newer?"0.1.45":"0.1.42",newer?NewApp:OldApp,out child);
   Need(File.Exists(launcher)&&File.ReadAllText(Path.Combine(Program,"Launcher",".nook-launcher-v1"),Utf8)=="Nook stable launcher protocol 1\n"+Hash(launcher)+"\n","Actual fixed launcher and ownership marker agree.");
   if(newer){Need(Hash(launcher)==launcherBefore,"Public 0.1.42 fixed launcher bytes remain stable across update.");Need(!Directory.Exists(oldFolder),"Exact previous 0.1.42 payload directory is removed by product cleanup.");var cleanup=Read(Path.Combine(session,"installer-cleanup.json"));Need(Convert.ToBoolean(cleanup["finished"])&&Text(cleanup["state"])=="complete","Product reports completed cleanup for this real update.");}
   CloseApp(child,newer?NewApp:OldApp);child.Dispose();
  }
  return new{actualInstallerEntry="no arguments",setupSha256=newer?NewSetup:OldSetup,elapsedSeconds=clock.Elapsed.TotalSeconds,uiSteps=step,transaction=evidence,oldFolder,oldFolderRemoved=oldFolder!=null&&!Directory.Exists(oldFolder),launcherPath=launcher,launcherSha256=Hash(launcher)};
 }
 // The fixed launcher and payload share the Mori process name. The known
 // bootstrap is never a payload candidate. Other exited processes may vanish
 // between HasExited and MainModule; live unreadable identities still fail.
 static string CandidateImage(Process candidate,int bootstrapPid){
  if(candidate.Id==bootstrapPid||candidate.HasExited)return null;
  try{
   var module=candidate.MainModule;
   if(module==null){if(candidate.HasExited)return null;throw new InvalidDataException("Live launcher candidate has no main module; identity cannot be verified.");}
   string path=module.FileName;
   if(string.IsNullOrEmpty(path))throw new InvalidDataException("Live launcher candidate has no executable path; identity cannot be verified.");
   return path;
  }catch(Exception error){
   if((error is InvalidOperationException||error is System.ComponentModel.Win32Exception||error is NullReferenceException)&&candidate.HasExited)return null;
   throw;
  }
 }
 static object Launch(bool newer){NoProduct();var active=Active(newer?"0.1.45.0":"0.1.42.0",newer?NewApp:OldApp);string executable=Path.Combine(Program,Text(active["executable"])),launcher=Path.Combine(Program,"Launcher","Mori.exe");Process child=null;using(var bootstrap=Process.Start(Start(launcher))){Wait(()=>{var candidates=Process.GetProcessesByName("Mori");try{foreach(var candidate in candidates){string image=CandidateImage(candidate,bootstrap.Id);if(image!=null&&image.Equals(executable,StringComparison.OrdinalIgnoreCase)&&candidate.MainWindowHandle!=IntPtr.Zero&&IsWindowVisible(candidate.MainWindowHandle)&&IsWindowEnabled(candidate.MainWindowHandle)){child=candidate;return true;}}return false;}finally{foreach(var candidate in candidates)if(candidate!=child)candidate.Dispose();}},30000,"Fixed launcher did not open the expected native installed version.");Need(child!=null&&Hash(child.MainModule.FileName)==(newer?NewApp:OldApp),"Same fixed launcher opens exact expected installed payload.");bool contained;Need(IsProcessInJob(child.Handle,Job,out contained)&&contained,"Fixed-launcher application is contained in this run's Job.");int pid=child.Id;string title=child.MainWindowTitle;CloseApp(child,newer?NewApp:OldApp);child.Dispose();Wait(()=>bootstrap.HasExited,5000,"Fixed launcher process did not exit.");return new{launcher,launcherSha256=Hash(launcher),actualExecutable=executable,appSha256=newer?NewApp:OldApp,pid,title,normalClose=true};}}
 // Only PrepareBrowser42 uses native top-level enumeration. No UIA desktop
 // traversal, title read or action is performed for another process.
 delegate bool PreparationEnumProc(IntPtr window,IntPtr context);
 [DllImport("user32.dll",SetLastError=true)]static extern bool EnumWindows(PreparationEnumProc callback,IntPtr context);
 [DllImport("user32.dll",SetLastError=true)]static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowText(IntPtr window,StringBuilder text,int capacity);
 [DllImport("user32.dll")]static extern IntPtr GetWindow(IntPtr window,uint command);
 [DllImport("user32.dll")]static extern bool IsWindow(IntPtr window);
 static readonly Dictionary<long,object> PreparationWindowsObserved=new Dictionary<long,object>();
 static AutomationElement PreparationSettings(int pid,IntPtr main,bool requireVisible){
  var handles=new List<IntPtr>();bool enumerated=EnumWindows(delegate(IntPtr window,IntPtr context){uint ownerPid;GetWindowThreadProcessId(window,out ownerPid);if(ownerPid==(uint)pid)handles.Add(window);return handles.Count<=128;},IntPtr.Zero);
  if(!enumerated||handles.Count>128)throw new IOException("Cannot enumerate bounded exact-public42 native windows.");
  AutomationElement found=null;
  foreach(IntPtr window in handles){
   uint windowPid;GetWindowThreadProcessId(window,out windowPid);if(windowPid!=(uint)pid)continue;
   var title=new StringBuilder(512);GetWindowText(window,title,title.Capacity);bool visible=IsWindowVisible(window),enabled=IsWindowEnabled(window);IntPtr owner=GetWindow(window,4);
   PreparationWindowsObserved[window.ToInt64()]=new{pid,handle=window.ToInt64(),title=title.ToString(),visible,enabled,ownerHandle=owner.ToInt64(),expectedMainHandle=main.ToInt64(),observedUtc=DateTime.UtcNow.ToString("O")};
   if(PreparationWindowsObserved.Count>128)throw new IOException("Too many exact-public42 native windows observed.");
   if(title.ToString()!="Nook · 设置"||owner!=main||(requireVisible&&!visible))continue;
   try{var element=AutomationElement.FromHandle(window);var current=element.Current;
    if(current.ProcessId!=pid||current.NativeWindowHandle!=window.ToInt32()||current.Name!="Nook · 设置"||current.ControlType!=ControlType.Window)throw new InvalidDataException("Exact native Settings window does not match its public42 UIA identity.");
    if(found!=null)throw new InvalidDataException("More than one owned public42 Settings window.");found=element;
   }catch(ElementNotAvailableException){uint livePid;GetWindowThreadProcessId(window,out livePid);if(IsWindow(window)&&livePid==(uint)pid)throw;}
  }
  return found;
 }
 static object PrepareBrowser42(){
  NoProduct();var active=Active("0.1.42.0",OldApp);string executable=Path.Combine(Program,Text(active["executable"])),launcher=Path.Combine(Program,"Launcher","Mori.exe"),browser=Path.Combine(Data,"browser");
  Need(!File.Exists(Path.Combine(browser,"app.mori.desk.json"))&&!File.Exists(Path.Combine(browser,"extension-installs.json"))&&!Directory.Exists(Path.Combine(browser,"extension")),"New healthy baseline has no browser manifest/catalog/runtime to replace.");
  foreach(string name in new[]{"chrome","msedge"}){var all=Process.GetProcessesByName(name);try{Need(all.Length==0,"No browser runs while the public42 UI prepares its own extension: "+name);}finally{foreach(var item in all)item.Dispose();}}
  Process child=null;DateTime beforeClick=DateTime.MinValue;using(var bootstrap=Process.Start(Start(launcher))){
   Wait(()=>{var candidates=Process.GetProcessesByName("Mori");try{foreach(var candidate in candidates){string image=CandidateImage(candidate,bootstrap.Id);if(image!=null&&image.Equals(executable,StringComparison.OrdinalIgnoreCase)&&candidate.MainWindowHandle!=IntPtr.Zero&&IsWindowVisible(candidate.MainWindowHandle)&&IsWindowEnabled(candidate.MainWindowHandle)){child=candidate;return true;}}return false;}finally{foreach(var candidate in candidates)if(candidate!=child)candidate.Dispose();}},30000,"Fixed launcher did not open public42 for browser preparation.");
   Need(child!=null&&Hash(child.MainModule.FileName)==OldApp,"Browser preparation belongs to exact genuine public42.");bool contained;Need(IsProcessInJob(child.Handle,Job,out contained)&&contained,"Public42 preparation child is owned by this helper Job.");
   IntPtr mainHandle=child.MainWindowHandle;var main=AutomationElement.FromHandle(mainHandle);var settingsButton=main.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"Settings"));Invoke(settingsButton,child.Id,"Open public42 Settings");
   AutomationElement settings=null;Wait(()=>{settings=PreparationSettings(child.Id,mainHandle,true);return settings!=null;},6000,"Public42 Settings dialog did not appear.");
   Invoke(Find(settings,"浏览器收藏",ControlType.Button),child.Id,"Select public42 browser collection settings");
   AutomationElement install=null;Wait(()=>{install=Find(settings,"安装 / 修复",ControlType.Button);return install!=null&&install.Current.IsEnabled;},6000,"Public42 supported Install / Repair button did not appear.");
   beforeClick=DateTime.UtcNow;Invoke(install,child.Id,"Prepare extension once through public42 Install / Repair");
   string manifest=Path.Combine(browser,"app.mori.desk.json"),catalog=Path.Combine(browser,"extension-installs.json"),runtime=Path.Combine(browser,"extension","manifest.json"),connection=Path.Combine(browser,"connection-status.log");
   Wait(()=>File.Exists(manifest)&&File.Exists(catalog)&&File.Exists(runtime)&&File.Exists(connection)&&File.GetLastWriteTimeUtc(connection)>=beforeClick&&File.ReadAllText(connection,Utf8).Contains(" listening"),30000,"Public42 UI did not prepare its manifest/catalog/runtime and local listening state.");
   var native=Read(manifest);Need(Text(native["name"])=="app.mori.desk"&&Text(native["type"])=="stdio"&&Text(native["path"]).Equals(launcher,StringComparison.OrdinalIgnoreCase)&&File.Exists(launcher),"Public42 generated manifest targets existing stable launcher.");
   object pattern=null;Need(settings.Current.ProcessId==child.Id&&settings.TryGetCurrentPattern(WindowPattern.Pattern,out pattern),"Only owned public42 Settings dialog closes normally.");((WindowPattern)pattern).Close();
   Wait(()=>PreparationSettings(child.Id,mainHandle,false)==null,6000,"Settings did not close normally.");
   CloseApp(child,OldApp);child.Dispose();Wait(()=>bootstrap.HasExited,5000,"Fixed launcher did not exit after browser preparation.");
   return new{publicUiOnly=true,clickedInstallRepairOnce=true,realChromeLoaded=false,manifestSha256=Hash(manifest),catalogSha256=Hash(catalog),runtimeManifestSha256=Hash(runtime),localListeningObserved=true,normalClose=true};
  }
 }
 [STAThread]static int Main(string[] args){object result=null;Exception error=null;bool passed=false;try{Operation=args.Length>0?args[0]:"";Need(args.Length==1&&Operation=="--prepare-browser42","This supplement only retries public42 browser preparation; never install, launch-only, reseed or preserve.");Need((args.Length==2&&(Operation=="--install42"||Operation=="--install45"))||(args.Length==1&&(Operation=="--launch42"||Operation=="--launch45"||Operation=="--prepare-browser42")),"Explicit installed test stage required.");Guest();using(var watchdog=new Timer(_=>Environment.Exit(124),null,780000,Timeout.Infinite)){result=Operation=="--prepare-browser42"?PrepareBrowser42():args.Length==2?Install(Operation=="--install45",args[1]):Launch(Operation=="--launch45");Wait(OnlyHelper,12000,"Owned application descendants did not drain.");Need(OnlyHelper(),"All owned runtime descendants exited; only this helper remains.");passed=true;}}catch(Exception failure){error=failure;try{Forced=Job!=IntPtr.Zero&&!OnlyHelper();}catch{Forced=true;}}finally{if(Package!=null)try{Save(Path.Combine(Package,Operation.TrimStart('-')+"-result.json"),new{passed,operation=Operation,result,checks=Checks,uiActions=Actions,preparationNativeWindows=PreparationWindowsObserved.Values.ToArray(),forcedCleanupAtProcessExit=Forced,jobActive=JobActive,jobTotal=JobTotal,jobActiveIncludesHelper=true,hostExecuted=false,installationStage=Operation=="--install42"||Operation=="--install45",error=error==null?null:error.ToString()});}catch{}}
  return passed?0:1;}
}
