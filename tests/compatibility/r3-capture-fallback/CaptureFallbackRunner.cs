using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media.Imaging;

// Independent diagnostic; no friend assembly identity and no test/product source changes.
internal static class CaptureFallbackRunner {
 const string AppHash="97B122310B1033BE7EBDA63730F929C256D8D0972CBC3497C7A7D6539CCA50D6";
 const string TestHash="63915E957D6D4F857648088181B594B8CEBC13598E1E0D34C1452F2340F052DF";
 static string Root,ProductFile,TestFile;static Assembly Product,Tests;static int Checks;
 static readonly BindingFlags Static=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic,Instance=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
 static readonly UTF8Encoding Utf8=new UTF8Encoding(false);
 static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=2*1024*1024};
 static void Need(bool ok,string why){if(!ok)throw new InvalidDataException(why);}
 static void Check(bool ok,string why){Unshown();Need(ok,why);Checks++;Console.WriteLine("PASS "+why);}
 static void Plain(string path){for(string p=Path.GetFullPath(path);!string.IsNullOrEmpty(p);p=Path.GetDirectoryName(p))if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("Reparse path refused");}
 static string Hash(string path){Plain(path);using(var f=File.OpenRead(path))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(f)).Replace("-","");}
 static string BytesHash(byte[] data){using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(data)).Replace("-","");}
 static void Save(string name,object value){string path=Path.Combine(Root,name);Plain(path);using(var f=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read))using(var w=new StreamWriter(f,Utf8)){w.Write(Json.Serialize(value));w.Flush();f.Flush(true);}}
 static object Invoke(MethodInfo method,object target,params object[] args){Need(method!=null,"Exact reflected method exists");try{return method.Invoke(target,args);}catch(TargetInvocationException e){if(e.InnerException!=null)ExceptionDispatchInfo.Capture(e.InnerException).Throw();throw;}}
 static object Call(Type type,string name,Type[] signature,params object[] args){return Invoke(type.GetMethod(name,Static,null,signature,null),null,args);}
 static object Get(object target,string name){return target.GetType().GetProperty(name,Instance).GetValue(target,null);}
 static void Set(string type,string field,object value){Product.GetType(type,true).GetField(field,Static).SetValue(null,value);}
 static void Unshown(){if(Application.Current==null)return;foreach(Window w in Application.Current.Windows)Need(!w.IsVisible&&new WindowInteropHelper(w).Handle==IntPtr.Zero,"No native or visible capture window permitted");}
 [DllImport("kernel32.dll")]static extern IntPtr GetCurrentProcess();
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool IsProcessInJob(IntPtr process,IntPtr job,out bool value);
 static void Guard(string root){
  bool vm=false;using(var q=new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem"))foreach(ManagementObject row in q.Get())using(row)vm|=Regex.IsMatch(Convert.ToString(row["Manufacturer"])+" "+Convert.ToString(row["Model"]),"QEMU|KVM|Bochs",RegexOptions.IgnoreCase);
  Need(vm&&Environment.UserName.Equals("NookTester",StringComparison.OrdinalIgnoreCase),"Dedicated VM before writes");bool member;Need(IsProcessInJob(GetCurrentProcess(),IntPtr.Zero,out member)&&member,"Owned Job required");Root=Path.GetFullPath(root);Plain(Root);string data=Environment.GetEnvironmentVariable("MORI_DESK_DATA");Need(Regex.IsMatch(Root,@"\AC:\\NookLab\\Audit\\s-[a-f0-9]{12}\\fixture\z"),"Owned fixture path");Need(data==Path.Combine(Path.GetDirectoryName(Root),"data"),"Owned data path");
  ProductFile=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Mori.exe");TestFile=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"QingJie.Tests.exe");Need(Hash(ProductFile)==AppHash&&Hash(TestFile)==TestHash,"Exact frozen R3 product and original test assembly");Need(!Directory.Exists(Root),"Fresh fixture only");Directory.CreateDirectory(Root);Product=Assembly.LoadFrom(ProductFile);Tests=Assembly.LoadFrom(TestFile);Need(Tests!=Assembly.GetExecutingAssembly(),"Diagnostic identity cannot shadow original tests");
  Set("QingJie.Preferences","TestFilePath",Path.Combine(data,"capture","settings.json"));Set("QingJie.OcrLanguagePacks","TestFolder",Path.Combine(data,"languages"));Set("QingJie.OfflinePacks","TestFolder",Path.Combine(data,"offline"));Set("QingJie.AppState","Settings",Activator.CreateInstance(Product.GetType("QingJie.Preferences",true)));
  var app=Application.Current??new Application();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
 }
 static Dictionary<string,object> Result(object result,int ui,int worker,double elapsed,int budget){
  var row=new Dictionary<string,object>{{"uiThread",ui},{"workerThread",worker},{"offUiThread",ui!=worker},{"elapsedMilliseconds",elapsed},{"budgetMilliseconds",budget}};
  foreach(string name in new[]{"Count","PrepareMilliseconds","ScanMilliseconds","ProcessedRows","TimedOut","PixelFormatConverted","PreparationCompleted","ReadFailed"})row[name]=Get(result,name);return row;
 }
 static object Read(BitmapSource image,int budget,out Dictionary<string,object> diagnostic){
  int ui=Thread.CurrentThread.ManagedThreadId,worker=0;var bounds=new System.Drawing.Rectangle(0,0,600,400);Type regions=Product.GetType("QingJie.CaptureVisualRegions",true);var watch=Stopwatch.StartNew();
  var task=Task.Run(()=>{worker=Thread.CurrentThread.ManagedThreadId;return budget==55?Call(regions,"Read",new[]{typeof(BitmapSource),typeof(System.Drawing.Rectangle),typeof(CancellationToken)},image,bounds,CancellationToken.None):Call(regions,"ReadWithBudget",new[]{typeof(BitmapSource),typeof(System.Drawing.Rectangle),typeof(CancellationToken),typeof(int)},image,bounds,CancellationToken.None,budget);});
  object result=task.GetAwaiter().GetResult();diagnostic=Result(result,ui,worker,watch.Elapsed.TotalMilliseconds,budget);diagnostic["imageFrozen"]=image.IsFrozen;diagnostic["imagePixelFormat"]=image.Format.ToString();return result;
 }

 static Type Fixture{get{return Tests.GetType("CaptureStartupFixture",true);}}
 static Window Window(BitmapSource image){return (Window)Call(Fixture,"Window",new[]{typeof(BitmapSource),typeof(bool)},image,true);}
 static object Surface(Window window){return Call(Fixture,"Surface",new[]{Product.GetType("QingJie.CaptureWindow",true)},window);}
 static object Field(object target,string name){return target.GetType().GetField(name,Instance).GetValue(target);}
 static void Field(object target,string name,object value){target.GetType().GetField(name,Instance).SetValue(target,value);}
 static object On(Window window,string name,Type[] signature,params object[] args){return Invoke(window.GetType().GetMethod(name,Instance,null,signature,null),window,args);}
 static void PumpUntil(Func<bool> ready){Call(Fixture,"PumpUntil",new[]{typeof(Func<bool>)},ready);}
 static Rect RectField(object surface,string name){return (Rect)Field(surface,name);}
 static void Pointer(Window window,Point start,Point end){
  Check((bool)On(window,"BeginPointer",new[]{typeof(Point),typeof(int),typeof(ModifierKeys)},start,1,ModifierKeys.None),"Original pointer-down is accepted in unshown fixture");
  if(start!=end)On(window,"ContinuePointer",new[]{typeof(Point)},end);On(window,"EndPointer",new[]{typeof(Point)},end);
 }
 static Dictionary<string,object> Scan(object result){var row=new Dictionary<string,object>();foreach(string name in new[]{"Count","PrepareMilliseconds","ScanMilliseconds","ProcessedRows","TimedOut","PixelFormatConverted","PreparationCompleted","ReadFailed"})row[name]=Get(result,name);return row;}
 static object Fallback(BitmapSource image){
  Dictionary<string,object> observed;object expired=Read(image,0,out observed);
  Check(Convert.ToBoolean(observed["TimedOut"])&&Convert.ToBoolean(observed["PreparationCompleted"])&&!Convert.ToBoolean(observed["ReadFailed"])&&Convert.ToInt32(observed["Count"])==0&&Convert.ToInt32(observed["ProcessedRows"])==0,"Explicit zero budget yields real prepared timed-out empty geometry");
  var expectedHost=new Rect(30,20,540,360);var point=new Point(150,120);Window window=Window(image);
  try{
   Field(window,"visualTargets",expired);object surface=Surface(window);On(window,"UpdateSnap",new[]{typeof(Point)},point);
   Check(RectField(surface,"HoverSelection")==expectedHost&&RectField(surface,"Selection").IsEmpty,"Real empty timed-out geometry retains the fixture native window target");
   Pointer(window,point,point);Check(RectField(surface,"Selection")==expectedHost,"Native window target remains selectable after visual budget exhaustion");
  }finally{window.Close();}
  window=Window(image);var end=new Point(260,190);
  try{
   Field(window,"visualTargets",expired);object surface=Surface(window);Pointer(window,point,end);
   Check(RectField(surface,"Selection")==new Rect(point,end),"Manual drag commits its exact rectangle after visual budget exhaustion");
  }finally{window.Close();}
  observed["scope"]="Explicit test-only budget0 result is injected into fixture visualTargets to exercise native/manual fallback; production budget remains55";return observed;
 }
 static object AsyncHover(BitmapSource image){
  var window=Window(image);var point=new Point(150,120);
  try{
   object surface=Surface(window);On(window,"UpdateSnap",new[]{typeof(Point)},point);On(window,"CycleSnap",new[]{typeof(Point),typeof(int)},point,1);var previous=RectField(surface,"HoverSelection");
   Check(!previous.IsEmpty&&RectField(surface,"Selection").IsEmpty,"Wheel selects a visible parent hover before asynchronous production scan");
   On(window,"PrepareVisualTargets",Type.EmptyTypes);object first=Field(window,"visualTargetCancel");Check(first!=null&&Field(window,"visualTargets")==null,"Actual55 worker first yields without publishing or blocking selection");
   On(window,"PrepareVisualTargets",Type.EmptyTypes);Check(ReferenceEquals(first,Field(window,"visualTargetCancel")),"Repeated prepare shares the same pending worker");
   PumpUntil(()=>Field(window,"visualTargetCancel")==null);object result=Field(window,"visualTargets");Check(result!=null,"Actual55 completion stores its result even if budget produces no rectangles");
   Check(RectField(surface,"HoverSelection")==previous&&RectField(surface,"Selection").IsEmpty,"Actual55 completion neither replaces wheel-chosen hover nor confirms a crop");
   On(window,"PrepareVisualTargets",Type.EmptyTypes);Check(ReferenceEquals(result,Field(window,"visualTargets"))&&Field(window,"visualTargetCancel")==null,"Completed actual55 geometry is reused without starting another worker");
   var row=Scan(result);row["scope"]="Production PrepareVisualTargets path with unchanged55ms budget; no positive region count or timing performance claim";return row;
  }finally{window.Close();}
 }
 static object AsyncConfirmed(BitmapSource image){
  var window=Window(image);var start=new Point(150,120);var end=new Point(260,190);
  try{
   object surface=Surface(window);On(window,"PrepareVisualTargets",Type.EmptyTypes);Check(Field(window,"visualTargetCancel")!=null,"Production visual worker is pending before a manual selection");Pointer(window,start,end);Rect confirmed=RectField(surface,"Selection");Check(confirmed==new Rect(start,end),"User manually confirms a crop while actual55 work is pending");
   PumpUntil(()=>Field(window,"visualTargetCancel")==null);object result=Field(window,"visualTargets");Check(result!=null&&RectField(surface,"Selection")==confirmed&&RectField(surface,"HoverSelection").IsEmpty,"Late actual55 completion cannot replace a confirmed manual crop or restore hover");return Scan(result);
  }finally{window.Close();}
 }
 static void Cancellation(BitmapSource image){
  using(var cancel=new CancellationTokenSource()){
   cancel.Cancel();bool cancelled=false;try{Call(Product.GetType("QingJie.CaptureVisualRegions",true),"Read",new[]{typeof(BitmapSource),typeof(System.Drawing.Rectangle),typeof(CancellationToken)},image,new System.Drawing.Rectangle(0,0,600,400),cancel.Token);}catch(OperationCanceledException){cancelled=true;}
   Check(cancelled,"Original default55 Read observes pre-cancellation before allocating/scanning");
  }
  Call(Tests.GetType("CaptureStartupLatencyTests",true),"ClosePending",new[]{typeof(BitmapSource),typeof(Action<bool,string>)},image,(Action<bool,string>)Check);
 }
 static int Run(){
  var report=new Dictionary<string,object>{{"scope","Independent fallback and asynchronous lifecycle function only; no original55 performance pass"},{"original943RetainedFailed",true},{"diagnostic1091Retained",true},{"appSha256",AppHash},{"originalTestSha256",TestHash}};bool completed=false;Exception failure=null;var previous=SynchronizationContext.Current;
  try{
   SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));var image=(BitmapSource)Call(Fixture,"Image",new[]{typeof(int),typeof(int)},600,400);Check(image.IsFrozen,"Original synthetic fixture remains frozen");
   report["explicitZeroBudgetFallback"]=Fallback(image);report["actual55Hover"]=AsyncHover(image);report["actual55ConfirmedSelection"]=AsyncConfirmed(image);Cancellation(image);
   Check(Mouse.Captured==null,"Completed fallback and lifecycle paths retain no logical mouse capture");
   var state=Product.GetType("QingJie.AppState",true);Check(((ICollection)state.GetField("Captures",Static).GetValue(null)).Count==0&&state.GetField("Hotkeys",Static).GetValue(null)==null&&state.GetField("Tray",Static).GetValue(null)==null,"No desktop capture session, registered hotkeys or tray service started");
   Check(Hash(ProductFile)==AppHash&&Hash(TestFile)==TestHash,"Original R3 product and capture test assembly remain unchanged");completed=true;
  }catch(Exception error){failure=error;Console.WriteLine(error);}
  finally{SynchronizationContext.SetSynchronizationContext(previous);if(Application.Current!=null)foreach(Window w in Application.Current.Windows.Cast<Window>().ToArray()){if(w.IsVisible||new WindowInteropHelper(w).Handle!=IntPtr.Zero){completed=false;if(failure==null)failure=new InvalidOperationException("Unexpected native/visible fixture");continue;}w.Close();}report["functionalPassed"]=completed;report["checksPassed"]=Checks;report["fixed55PerformancePassed"]=false;report["error"]=failure==null?null:failure.ToString();Save("capture-fallback-lifecycle.json",report);}
  if(!completed)return 1;Console.WriteLine("PASS "+Checks+" independent capture fallback and lifecycle checks; original 943 failure retained");return 0;
 }
 [STAThread]static int Main(string[] args){try{Need(args.Length==2&&args[1]=="capture-fallback-lifecycle","Explicit functional supplement only");Guard(args[0]);return Run();}catch(Exception error){Console.WriteLine(error);return 1;}}
}
