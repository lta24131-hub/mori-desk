using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
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

[assembly: AssemblyTitle("Nook PinStore pixel reuse diagnostic - VM TEST ONLY")]
// Resource samples supplement direct reference/pixel evidence; they are not a
// product benchmark or a claim about host speed. Never shows a native window.
internal static class PinMemoryGuest {
 static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=1048576,RecursionLimit=20};
 static readonly UTF8Encoding Utf8=new UTF8Encoding(false);
 static readonly BindingFlags Hidden=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
 static string Package,Fixture;static Dictionary<string,object> Manifest;static Assembly Product;
 static BitmapSource Source;static string ExpectedPixels;static bool Measuring,ReusedSource;static object Observation;static long BeforePrivate,BeforeManaged;static Stopwatch Clock;
 sealed class Inspected : Exception {}
 static void Need(bool value,string message){if(!value)throw new InvalidDataException(message);}
 static void NoLinks(string path){for(string part=Path.GetFullPath(path);!string.IsNullOrEmpty(part);part=Path.GetDirectoryName(part))if((File.Exists(part)||Directory.Exists(part))&&(File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked fixture path refused.");}
 static string Hash(string path){NoLinks(path);using(var file=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");}
 static string PixelHash(byte[] data){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(data)).Replace("-","");}
 static void Save(string path,object value){NoLinks(path);using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new StreamWriter(file,Utf8)){writer.Write(Json.Serialize(value));writer.Flush();file.Flush(true);}}
 static Dictionary<string,object> Read(string path){NoLinks(path);Need(new FileInfo(path).Length<1048576,"Bounded fixture JSON required.");return Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path));}
 static string Text(object value){return Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture);}
 static object Get(object target,string name){return target.GetType().GetField(name,Hidden).GetValue(target);}
 static void Guest(){
  bool vm=false;using(var query=new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem"))foreach(ManagementObject row in query.Get())using(row)vm|=Regex.IsMatch(Text(row["Manufacturer"])+" "+Text(row["Model"]),"QEMU|KVM|Bochs",RegexOptions.IgnoreCase);
  Need(vm&&Environment.UserName.Equals("NookTester",StringComparison.OrdinalIgnoreCase),"Dedicated NookTester QEMU guest required before any write.");
  Package=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');Need(Regex.IsMatch(Package,@"\AC:\\NookLab\\Audit\\apps\\[a-fA-F0-9]{12}\z"),"Hash-addressed VM package required.");NoLinks(Package);
  Manifest=Read(Path.Combine(Package,"pin-memory-manifest.json"));var files=((IEnumerable)Manifest["files"]).Cast<Dictionary<string,object>>().ToArray();Need(files.Length>0&&files.Length<30,"Bounded sealed payload required.");
  foreach(var entry in files){string relative=Text(entry["path"]);Need(!Path.IsPathRooted(relative)&&!relative.Split('/','\\').Contains(".."),"Relative payload required.");string path=Path.GetFullPath(Path.Combine(Package,relative));Need(path.StartsWith(Package+"\\",StringComparison.OrdinalIgnoreCase)&&Hash(path)==Text(entry["sha256"]),"Sealed payload hash mismatch.");}
 }
 static BitmapSource Picture(int width,int height){
  byte[] pixels=new byte[checked(width*height*4)];for(int y=0;y<height;y++)for(int x=0;x<width;x++){int n=(y*width+x)*4;pixels[n]=(byte)(x%251);pixels[n+1]=(byte)(y%251);pixels[n+2]=(byte)((x+y)%251);pixels[n+3]=255;}
  ExpectedPixels=PixelHash(pixels);var source=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);source.Freeze();return source;
 }
 static string ReadPixels(BitmapSource image){
  var converted=new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);byte[] row=new byte[image.PixelWidth*4];
  using(var sha=SHA256.Create()){for(int y=0;y<image.PixelHeight;y++){converted.CopyPixels(new Int32Rect(0,y,image.PixelWidth,1),row,row.Length,0);sha.TransformBlock(row,0,row.Length,row,0);}sha.TransformFinalBlock(new byte[0],0,0);return BitConverter.ToString(sha.Hash).Replace("-","");}
 }
 static long PrivateBytes(){using(var process=Process.GetCurrentProcess()){process.Refresh();return process.PrivateMemorySize64;}}
 static void Collect(){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();}
 static void Inspect(object raw){
  var window=(Window)raw;Need(!window.IsVisible&&new WindowInteropHelper(window).Handle==IntPtr.Zero,"Inspection must happen before any native window is shown.");var image=(BitmapSource)Get(raw,"image");
  Need(image.PixelWidth==Source.PixelWidth&&image.PixelHeight==Source.PixelHeight&&ReadPixels(image)==ExpectedPixels,"Materialized pixels must match the complete generated source.");
  if(Measuring){Collect();long afterManaged=GC.GetTotalMemory(false),afterPrivate=PrivateBytes();ReusedSource=ReferenceEquals(Source,image)&&image.IsFrozen;Observation=new{sourceWidth=Source.PixelWidth,sourceHeight=Source.PixelHeight,sameFrozenSource=ReusedSource,allPixelsEqual=true,nativeWindowShown=false,beforePrivateBytes=BeforePrivate,atPresentationPrivateBytes=afterPrivate,privateDeltaBytes=afterPrivate-BeforePrivate,beforeManagedBytes=BeforeManaged,atPresentationManagedBytes=afterManaged,managedDeltaBytes=afterManaged-BeforeManaged,diagnosticElapsedIncludingPixelCheckAndGcMilliseconds=Clock.ElapsedMilliseconds};}
  throw new Inspected();
 }
 static void Wait(Task task){
  var frame=new DispatcherFrame();var timeout=new DispatcherTimer{Interval=TimeSpan.FromSeconds(30)};bool expired=false;timeout.Tick+=(s,e)=>{expired=true;frame.Continue=false;};
  var poll=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(5)};poll.Tick+=(s,e)=>{if(task.IsCompleted)frame.Continue=false;};
  timeout.Start();poll.Start();try{if(!task.IsCompleted)Dispatcher.PushFrame(frame);Need(!expired,"Pin materialization exceeded the VM diagnostic budget.");task.GetAwaiter().GetResult();}finally{timeout.Stop();poll.Stop();}
 }
 static void Exercise(object store){
  bool inspected=false;try{Wait((Task)store.GetType().GetMethod("NewAtAsync",Hidden).Invoke(store,new object[]{Source,new Int32Rect(0,0,320,180),CancellationToken.None}));}catch(Inspected){inspected=true;}
  Need(inspected,"Actual NewAtAsync must reach the pre-show materializer.");Need(((IEnumerable)Get(store,"Records")).Cast<object>().Count()==0&&Directory.GetFiles(Text(Get(store,"Folder")),"*.png").Length==0,"Diagnostic presentation veto must roll back only its own temporary pin.");GC.KeepAlive(Source);
 }
 static int Measure(string stage,string run){
  Need((stage=="old"||stage=="candidate")&&Regex.IsMatch(run,"^[a-f0-9]{32}$"),"Known stage and fixture ID required.");Fixture=@"C:\NookLab\Audit\pin-memory-"+run;NoLinks(Fixture);Need(Directory.Exists(Fixture)&&Text(Read(Path.Combine(Fixture,"owner.json"))["package"])==Package,"Owned fixture required.");
  string directory=Path.Combine(Package,stage),executable=Path.Combine(directory,"Mori.exe");Need(Hash(executable)==Text(Manifest[stage+"AppSha256"]),"Exact selected app required.");
  Environment.SetEnvironmentVariable("MORI_DESK_DATA",Path.Combine(Fixture,"profile"));Product=Assembly.LoadFrom(executable);AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string path=Path.Combine(directory,new AssemblyName(e.Name).Name+".dll");return File.Exists(path)?Assembly.LoadFrom(path):null;};
  Product.GetType("QingJie.Preferences",true).GetField("TestFilePath",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,Path.Combine(Fixture,"preferences.json"));var state=Product.GetType("QingJie.AppState",true);state.GetField("Settings").SetValue(null,Activator.CreateInstance(Product.GetType("QingJie.Preferences",true)));state.GetField("Quitting").SetValue(null,true);
  SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));var store=Activator.CreateInstance(Product.GetType("QingJie.PinStore",true),new object[]{Path.Combine(Fixture,"pins")});
  var hook=store.GetType().GetField("ConfigureWindowForTests",Hidden);hook.SetValue(store,Delegate.CreateDelegate(hook.FieldType,typeof(PinMemoryGuest).GetMethod("Inspect",BindingFlags.Static|BindingFlags.NonPublic)));
  Source=Picture(64,64);Exercise(store);Source=null;Collect(); // warm the real encoding/materializer paths before resource sampling
  Source=Picture(3840,2160);Collect();BeforeManaged=GC.GetTotalMemory(false);BeforePrivate=PrivateBytes();Measuring=true;Clock=Stopwatch.StartNew();Exercise(store);Clock.Stop();
  Need(Observation!=null&&ReusedSource==(stage=="candidate"),"Only the candidate must reuse the original frozen pixel object.");Save(Path.Combine(Fixture,"result.json"),new{passed=true,stage,productSha256=Hash(executable),productVersion=FileVersionInfo.GetVersionInfo(executable).FileVersion,observation=Observation,sourcePixelBytes=3840L*2160*4,hostExecuted=false,installedApplicationTouched=false});return 0;
 }
 static int Run(){
  var samples=new List<object>();bool passed=true;
  for(int trial=1;trial<=3&&passed;trial++)foreach(string stage in new[]{"old","candidate"}){
   string run=Guid.NewGuid().ToString("N");Fixture=@"C:\NookLab\Audit\pin-memory-"+run;NoLinks(Fixture);Need(!Directory.Exists(Fixture),"Fresh fixture required.");Directory.CreateDirectory(Fixture);Save(Path.Combine(Fixture,"owner.json"),new{package=Package});
   var start=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--measure "+stage+" "+run){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Package,RedirectStandardOutput=true,RedirectStandardError=true};
   using(var child=Process.Start(start)){var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();if(!child.WaitForExit(45000)){child.Kill();child.WaitForExit();passed=false;}passed&=child.ExitCode==0;File.WriteAllText(Path.Combine(Fixture,"stdout.txt"),stdout.GetAwaiter().GetResult(),Utf8);File.WriteAllText(Path.Combine(Fixture,"stderr.txt"),stderr.GetAwaiter().GetResult(),Utf8);}
   string result=Path.Combine(Fixture,"result.json");if(File.Exists(result))samples.Add(new{trial,stage,result=Read(result)});else{passed=false;samples.Add(new{trial,stage,error="Missing resource sample",fixture=Fixture});}if(!passed)break;
  }
  Save(Path.Combine(Package,"pin-memory-result.json"),new{passed,scope="VM-only adjacent draft/candidate 4K pin materialization diagnostic",baselineRole="earlier 0.1.44 draft candidate; not a published-release upgrade",samples,limits="Private and managed memory snapshots after full collection at the pre-show materializer; not process peak memory. Separate cold processes with a small warmup. Diagnostic elapsed time includes encoding/materialization, full pixel hashing and forced GC; it is not pure production latency. Inspect veto removes each fixture pin; no native windows shown.",hostExecuted=false,installedApplicationTouched=false});return passed?0:1;
 }
 [STAThread]static int Main(string[] args){try{Guest();if(args.Length==1&&args[0]=="--run")return Run();if(args.Length==3&&args[0]=="--measure")return Measure(args[1],args[2]);throw new InvalidDataException("Explicit VM diagnostic command required.");}catch(Exception error){Console.WriteLine(error);return 1;}}
}
