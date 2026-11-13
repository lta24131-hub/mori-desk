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
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MoriDesk;

[assembly: AssemblyTitle("Nook real WebView canvas journey - VM TEST ONLY")]
// Reuses 20261102 CanvasRunner.WebScenario against the unmodified final binary.
// Test mode exposes inspection only; real WebView messages, resources and saves
// are used. No protocol reply, image decode or save is mocked.
internal static class CanvasWebJourneyGuest {
 const string AppHash="97B122310B1033BE7EBDA63730F929C256D8D0972CBC3497C7A7D6539CCA50D6";
 const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
 static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=1048576,RecursionLimit=32};
 static readonly UTF8Encoding Utf8=new UTF8Encoding(false);static readonly List<string> Checks=new List<string>();static readonly List<object> Screenshots=new List<object>();
 static string Package,Root;static IntPtr OwnedJob;static uint JobActive,JobTotal;static bool GuestVerified,ForcedClose;static MatureCanvasWindow Active;static string BrowserVersion;
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
 static string Text(object value){return Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture);}
 static void Need(bool value,string message){if(!value)throw new InvalidDataException(message);Checks.Add(message);}
 static void NoLinks(string path){for(string part=Path.GetFullPath(path);!string.IsNullOrEmpty(part);part=Path.GetDirectoryName(part))if((File.Exists(part)||Directory.Exists(part))&&(File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked fixture path refused.");}
 static string Hash(string path){NoLinks(path);using(var file=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");}
 static void Save(string path,object value){NoLinks(path);using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new StreamWriter(file,Utf8)){writer.Write(Json.Serialize(value));writer.Flush();file.Flush(true);}}
 static void Guest(){
  bool vm=false;using(var query=new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem"))foreach(ManagementObject row in query.Get())using(row)vm|=Regex.IsMatch(Text(row["Manufacturer"])+" "+Text(row["Model"]),"QEMU|KVM|Bochs",RegexOptions.IgnoreCase);
  Need(vm&&Environment.UserName.Equals("NookTester",StringComparison.OrdinalIgnoreCase),"Dedicated NookTester QEMU guest required before any write.");GuestVerified=true;
  Package=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');Need(Regex.IsMatch(Package,@"\AC:\\NookLab\\Audit\\apps\\[a-fA-F0-9]{12}\z"),"Hash-addressed VM package required.");NoLinks(Package);
  string file=Path.Combine(Package,"canvas-web-journey-manifest.json");Need(new FileInfo(file).Length<1048576,"Bounded manifest required.");var manifest=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(file));
  var files=((IEnumerable)manifest["files"]).Cast<Dictionary<string,object>>().ToArray();Need(files.Length>0&&files.Length<512,"Bounded sealed payload required.");
  foreach(var entry in files){string relative=Text(entry["path"]);if(Path.IsPathRooted(relative)||relative.Split('/','\\').Contains(".."))throw new IOException("Relative payload required.");string path=Path.GetFullPath(Path.Combine(Package,relative));if(!path.StartsWith(Package+"\\",StringComparison.OrdinalIgnoreCase)||Hash(path)!=Text(entry["sha256"]))throw new IOException("Sealed payload mismatch: "+relative);}
  Need(Hash(Path.Combine(Package,"Mori.exe"))==AppHash,"Exact final binary and all bundled web/dependency files match the sealed manifest.");
  Root=@"C:\NookLab\Audit\canvas-web-"+Guid.NewGuid().ToString("N");NoLinks(Root);Need(!Directory.Exists(Root),"Fresh isolated fixture required.");Directory.CreateDirectory(Root);Environment.SetEnvironmentVariable("MORI_DESK_DATA",Path.Combine(Root,"profile"));
 }
 static void OwnTree(){IntPtr job=CreateJobObject(IntPtr.Zero,null);if(job==IntPtr.Zero)throw new IOException("Cannot create owned job.");bool assigned=false;try{var limits=new JobExtendedLimits{Basic=new JobBasicLimits{Flags=0x2000}};if(!SetInformationJobObject(job,9,ref limits,(uint)Marshal.SizeOf(typeof(JobExtendedLimits)))||!AssignProcessToJobObject(job,GetCurrentProcess()))throw new IOException("Cannot contain WebView process tree: "+Marshal.GetLastWin32Error());assigned=true;OwnedJob=job;}finally{if(!assigned)CloseHandle(job);}Need(true,"Kill-on-close Job owns the helper and WebView descendants before window creation; handle stays open until OS process teardown.");}
 static bool OnlyHelper(){JobAccounting value;if(!QueryInformationJobObject(OwnedJob,1,out value,(uint)Marshal.SizeOf(typeof(JobAccounting)),IntPtr.Zero))throw new IOException("Cannot query WebView process job.");JobActive=value.ActiveProcesses;JobTotal=value.TotalProcesses;return JobActive==1;}
 static void Pump(Task task,int milliseconds){var frame=new DispatcherFrame();var clock=Stopwatch.StartNew();bool timeout=false;var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};timer.Tick+=(s,e)=>{if(task.IsCompleted)frame.Continue=false;else if(clock.ElapsedMilliseconds>milliseconds){timeout=true;frame.Continue=false;}};timer.Start();try{if(!task.IsCompleted)Dispatcher.PushFrame(frame);if(timeout)throw new TimeoutException("Real canvas journey exceeded its VM budget.");task.GetAwaiter().GetResult();}finally{timer.Stop();}}
 static WebView2 Web(MatureCanvasWindow host){return (WebView2)typeof(MatureCanvasWindow).GetField("web",Hidden).GetValue(host);}
 static async Task<MatureCanvasWindow> Open(string data,string id=null){
  var host=new MatureCanvasWindow(data,id){Width=1000,Height=660};Active=host;
  Func<CoreWebView2,Task> before=async core=>{BrowserVersion=core.Environment.BrowserVersionString;await core.AddScriptToExecuteOnDocumentCreatedAsync("history.replaceState(null,'','/index.html?test=1');window.fixtureErrors=[];window.addEventListener('error',e=>window.fixtureErrors.push(e.error&&e.error.stack||e.message));window.addEventListener('unhandledrejection',e=>window.fixtureErrors.push(e.reason&&e.reason.stack||String(e.reason)));");};
  typeof(MatureCanvasWindow).GetProperty("BeforeNavigationForTests",Hidden).SetValue(host,before,null);host.Show();var ready=host.WaitUntilReadyAsync();if(await Task.WhenAny(ready,Task.Delay(45000))!=ready)throw new TimeoutException("Actual WebView editor did not load.");await ready;
  Need(host.IsVisible&&new WindowInteropHelper(host).Handle!=IntPtr.Zero&&await Web(host).CoreWebView2.ExecuteScriptAsync("Boolean(window.nookCanvas&&window.nookCanvas.state().loaded)")=="true","Real native canvas and bundled WebView editor complete the production load handshake.");return host;
 }
 static async Task Poll(MatureCanvasWindow host,string expression,string label){var clock=Stopwatch.StartNew();while(clock.ElapsedMilliseconds<20000){if(await Web(host).CoreWebView2.ExecuteScriptAsync(expression)=="true"){Need(true,label);return;}await Task.Delay(80);}throw new IOException(label+"; "+await Web(host).CoreWebView2.ExecuteScriptAsync("JSON.stringify({state:window.nookCanvas&&window.nookCanvas.state(),error:window.fixtureError,errors:window.fixtureErrors,body:document.body.innerText})"));}
 static async Task Close(MatureCanvasWindow host){bool closed=false;host.Closed+=(s,e)=>closed=true;host.Close();var clock=Stopwatch.StartNew();while(!closed&&clock.ElapsedMilliseconds<20000)await Task.Delay(50);Need(closed&&!host.IsVisible,"Normal native Close flushes the actual browser scene and releases its canvas window.");Active=null;}
 static async Task Capture(MatureCanvasWindow host,string name){
  string file=Path.Combine(Package,name);using(var output=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None))await Web(host).CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,output);long size=new FileInfo(file).Length;Need(size>1000&&size<16*1024*1024,"Actual bounded WebView composited preview captured: "+name);
  // The VM evidence transport reads text. Preserve the original PNG and expose
  // bounded base64 parts so the host can reconstruct and verify its exact hash.
  string encoded=Convert.ToBase64String(File.ReadAllBytes(file));var parts=new List<string>();
  for(int offset=0,index=0;offset<encoded.Length;offset+=80000,index++){string part=name+".b64."+index.ToString("D3")+".txt";using(var output=new FileStream(Path.Combine(Package,part),FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new StreamWriter(output,Utf8))writer.Write(encoded.Substring(offset,Math.Min(80000,encoded.Length-offset)));parts.Add(part);}
  Screenshots.Add(new{file=name,sha256=Hash(file),bytes=size,base64Parts=parts});
 }
 static void NativeImage(string file){using(var bitmap=new System.Drawing.Bitmap(280,180)){using(var graphics=System.Drawing.Graphics.FromImage(bitmap)){graphics.Clear(System.Drawing.Color.FromArgb(33,143,111));using(var white=new System.Drawing.SolidBrush(System.Drawing.Color.White)){graphics.FillEllipse(white,22,30,80,80);using(var font=new System.Drawing.Font("Arial",18))graphics.DrawString("Native image",font,white,22,130);}}bitmap.Save(file,System.Drawing.Imaging.ImageFormat.Png);}}
 static async Task Journey(){
  string data=Path.Combine(Root,"canvas"),native=Path.Combine(Root,"native-original.png");NativeImage(native);string originalHash=Hash(native);var host=await Open(data);string id=host.ProjectId;var core=Web(host).CoreWebView2;
  await core.ExecuteScriptAsync(@"window.fixtureDone=false;window.fixtureError=null;(async()=>{try{const c=document.createElement('canvas');c.width=300;c.height=200;const g=c.getContext('2d');g.fillStyle='#7755b9';g.fillRect(0,0,300,200);g.fillStyle='white';g.fillRect(24,28,100,70);g.font='24px sans-serif';g.fillText('SDK image',24,160);const b=await new Promise(r=>c.toBlob(r,'image/png'));const f=new File([b],'sdk-generated.png',{type:'image/png'});await window.nookCanvas.editor.putExternalContent({type:'files',files:[f],point:{x:0,y:0}});window.fixtureDone=true;}catch(e){window.fixtureError=String(e)}})();");
  await Poll(host,"window.fixtureDone===true","Official SDK external-file handler imports the first generated image.");
  await Poll(host,"window.nookCanvas.state().change===window.nookCanvas.state().savedChange&&window.nookCanvas.state().revision>0","SDK image receives the real durable native save acknowledgement.");
  await host.ImportImagesAsync(new[]{native});var store=new MatureCanvasStore(data);var saved=store.Load(id);Need(saved.ImageCount==2&&saved.Resources.Count==2&&!saved.SceneJson.Contains("base64"),"SDK and native imports persist exactly two local resources and two image shapes.");
  await core.ExecuteScriptAsync("window.nookCanvas.editor.selectNone();window.nookCanvas.editor.zoomToFit();");
  await Poll(host,"Array.from(document.querySelectorAll('img')).filter(i=>i.src.includes('/resource/')&&i.complete&&i.naturalWidth>0).length>=1","At least the native resource route is decoded and displayed before closing.");
  string shapes=await core.ExecuteScriptAsync("JSON.stringify(window.nookCanvas.editor.getCurrentPageShapes().filter(s=>s.type==='image').sort((a,b)=>a.id.localeCompare(b.id)).map(s=>({id:s.id,x:s.x,y:s.y,props:s.props})))");
  var resources=saved.Resources.ToDictionary(resource=>resource.FileId,resource=>Hash(store.ResourcePath(saved,resource)));
  await Capture(host,"canvas-before-close.png");Need(await core.ExecuteScriptAsync("window.fixtureErrors.length===0")=="true","Actual first editor session has no uncaught script errors or rejected promises.");await Close(host);
  host=await Open(data,id);core=Web(host).CoreWebView2;
  Need(await core.ExecuteScriptAsync("window.nookCanvas.editor.getCurrentPageShapes().filter(s=>s.type==='image').length===2")=="true","Reopened real editor restores exactly two image shapes.");
  Need(await core.ExecuteScriptAsync("JSON.stringify(window.nookCanvas.editor.getCurrentPageShapes().filter(s=>s.type==='image').sort((a,b)=>a.id.localeCompare(b.id)).map(s=>({id:s.id,x:s.x,y:s.y,props:s.props})))")==shapes,"Reopened image identities, positions, sizes and properties match the saved scene.");
  await core.ExecuteScriptAsync("window.nookCanvas.editor.selectNone();window.nookCanvas.editor.zoomToFit();");
  await Poll(host,"new Set(Array.from(document.querySelectorAll('img')).filter(i=>i.src.includes('/resource/')&&i.complete&&i.naturalWidth>0).map(i=>i.src)).size===2","Both distinct persisted local image resources decode and display after reopening.");
  var reopened=store.Load(id);Need(reopened.Resources.Count==2&&reopened.Resources.All(resource=>resources.ContainsKey(resource.FileId)&&Hash(store.ResourcePath(reopened,resource))==resources[resource.FileId]),"Reopen preserves both exact registered resource byte streams.");
  await Capture(host,"canvas-reopened.png");Need(await core.ExecuteScriptAsync("window.fixtureErrors.length===0")=="true","Reopened editor has no uncaught script errors or rejected promises.");await Close(host);Need(Hash(native)==originalHash,"Import, save, close and reopen leave the original native input byte-for-byte unchanged.");
 }
 static int Run(){var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));Mori.Theme.Set("light");Exception failure=null;bool passed=false;try{Pump(Journey(),145000);passed=true;}catch(Exception error){failure=error;}finally{if(Active!=null){try{ForcedClose=true;typeof(MatureCanvasWindow).GetField("ready",Hidden).SetValue(Active,false);Active.Close();Active=null;}catch{}passed=false;}try{Pump(Task.Run(async()=>{for(int i=0;i<100&&!OnlyHelper();i++)await Task.Delay(100);}),12000);Need(OnlyHelper(),"After normal canvas disposal the Job contains only this helper; all WebView children have exited.");}catch(Exception error){passed=false;if(failure==null)failure=error;}app.Shutdown();}
  Save(Path.Combine(Package,"canvas-web-journey-result.json"),new{passed,appSha256=Hash(Path.Combine(Package,"Mori.exe")),browserVersion=BrowserVersion,scope="VM-only actual WebView load, SDK and native two-image import, save, normal close and reopen. No gesture or full drawing-tool coverage.",checks=Checks,screenshots=Screenshots,fixture=Root,forcedClose=ForcedClose,killOnJobClose=true,jobActiveAtCompletion=JobActive,jobActiveIncludesHelper=true,jobTotalProcesses=JobTotal,hostExecuted=false,installedApplicationTouched=false,error=failure==null?null:failure.ToString()});return passed?0:1;}
 [STAThread]static int Main(string[] args){try{Guest();Need(args.Length==1&&args[0]=="--run","Explicit VM command required.");OwnTree();using(var watchdog=new Timer(_=>Environment.Exit(124),null,170000,Timeout.Infinite))return Run();}catch(Exception error){Console.WriteLine(error);if(GuestVerified&&Package!=null&&Regex.IsMatch(Package,@"\AC:\\NookLab\\Audit\\apps\\[a-fA-F0-9]{12}\z"))try{string result=Path.Combine(Package,"canvas-web-journey-result.json");if(!File.Exists(result))Save(result,new{passed=false,error=error.ToString(),checks=Checks});}catch{}return 1;}}
}
