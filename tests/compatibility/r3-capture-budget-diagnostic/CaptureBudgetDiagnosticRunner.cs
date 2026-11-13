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
using System.Windows.Media.Imaging;

// Independent diagnostic; no friend assembly identity and no test/product source changes.
internal static class CaptureBudgetDiagnosticRunner {
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
 static object PixelEvidence(BitmapSource image){
  Need(image.Format.BitsPerPixel==32,"Expected original 32-bit fixture");var pixels=new byte[image.PixelWidth*image.PixelHeight*4];image.CopyPixels(pixels,image.PixelWidth*4,0);var colors=new Dictionary<uint,int>();for(int i=0;i<pixels.Length;i+=4){uint color=BitConverter.ToUInt32(pixels,i);int count;colors.TryGetValue(color,out count);colors[color]=count+1;}
  var samples=new List<object>();foreach(int[] p in new[]{new[]{0,0},new[]{20,20},new[]{50,50},new[]{150,120},new[]{100,100},new[]{599,399}}){int at=(p[1]*image.PixelWidth+p[0])*4;samples.Add(new{x=p[0],y=p[1],blue=pixels[at],green=pixels[at+1],red=pixels[at+2],alpha=pixels[at+3]});}
  string path=Path.Combine(Root,"original-synthetic.png");using(var f=new FileStream(path,FileMode.CreateNew,FileAccess.Write)){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));encoder.Save(f);f.Flush(true);}
  return new{width=image.PixelWidth,height=image.PixelHeight,format=image.Format.ToString(),isFrozen=image.IsFrozen,rawPixelBytes=pixels.Length,rawPixelSha256=BytesHash(pixels),uniqueColors=colors.Count,colors=colors.OrderByDescending(k=>k.Value).Select(k=>new{argb=k.Key.ToString("X8"),pixels=k.Value}).ToArray(),samples,png="original-synthetic.png",pngSha256=Hash(path),pngBytes=new FileInfo(path).Length};
 }
 static int Run(){
  var report=new Dictionary<string,object>{{"scope","Independent same-image default55 diagnostic followed by explicit2000 geometry function; no original performance pass"},{"original943RetainedFailed",true},{"productPassed",null},{"appSha256",AppHash},{"originalTestSha256",TestHash}};bool completed=false;Exception failure=null;
  try{
   var originalCheck=(Action<bool,string>)Check;Call(Tests.GetType("CaptureStartupConstructorBenchmark",true),"Run",new[]{typeof(Action<bool,string>)},originalCheck);
   Type fixture=Tests.GetType("CaptureStartupFixture",true);var image=(BitmapSource)Call(fixture,"Image",new[]{typeof(int),typeof(int)},600,400);Type windowType=Product.GetType("QingJie.CaptureWindow",true);var window=(Window)Call(fixture,"Window",new[]{typeof(BitmapSource),typeof(bool)},image,true);
   try{Check(Call(fixture,"Field",new[]{windowType,typeof(string)},window,"visualTargets")==null,"Original constructor starts with no visual targets");Check(Call(fixture,"Field",new[]{windowType,typeof(string)},window,"visualTargetCancel")==null,"Original unshown constructor starts with no eager worker");}finally{window.Close();}
   Type latency=Tests.GetType("CaptureStartupLatencyTests",true);Func<bool,bool,int> settle=(hotkey,hidden)=>(int)Call(latency,"Settle",new[]{typeof(bool),typeof(bool)},hotkey,hidden);Check(settle(true,false)==40,"Original direct hotkey settle policy is 40ms");Check(settle(false,false)==130&&settle(false,true)==130&&settle(true,true)==130,"Original menu/hidden settle policy is 130ms");
   Dictionary<string,object> defaultResult;Read(image,55,out defaultResult);defaultResult["originalFifthConditionObserved"]=image.IsFrozen&&Convert.ToBoolean(defaultResult["offUiThread"])&&Convert.ToInt32(defaultResult["Count"])>0;report["default55"]=defaultResult;Save("default55-before-pixel-copy.json",defaultResult);
   report["pixelsAfterDefault55"]=PixelEvidence(image);Dictionary<string,object> geometry;object result=Read(image,2000,out geometry);var rectangles=new List<object>();foreach(Rect rect in (IEnumerable)Invoke(result.GetType().GetMethod("At",Instance),result,new Point(150,120),new Rect(0,0,600,400)))rectangles.Add(new{x=rect.X,y=rect.Y,width=rect.Width,height=rect.Height});geometry["rectanglesAt150x120"]=rectangles;report["explicit2000Geometry"]=geometry;
   Check(image.IsFrozen&&Convert.ToBoolean(geometry["offUiThread"]),"Same frozen original fixture analysed on worker with explicit geometry budget");Check(Convert.ToBoolean(geometry["PreparationCompleted"])&&!Convert.ToBoolean(geometry["ReadFailed"]),"Explicit geometry preparation succeeds without read exception");Check(Convert.ToInt32(geometry["Count"])>0&&rectangles.Count>0,"Explicit 2000ms geometry budget locates original synthetic card at original point");
   Check(Hash(ProductFile)==AppHash&&Hash(TestFile)==TestHash,"Original product and tests remain byte-identical");Unshown();completed=true;
  }catch(Exception error){failure=error;Console.WriteLine(error);}
  finally{if(Application.Current!=null)foreach(Window w in Application.Current.Windows.Cast<Window>().ToArray()){if(w.IsVisible||new WindowInteropHelper(w).Handle!=IntPtr.Zero){completed=false;if(failure==null)failure=new InvalidOperationException("Unexpected native/visible window");continue;}w.Close();}report["diagnosticCompleted"]=completed;report["checksPassed"]=Checks;report["error"]=failure==null?null:failure.ToString();Save("capture-budget-diagnostic.json",report);}
  if(completed){Console.WriteLine("OBSERVED "+Checks+" capture diagnostic and explicit-budget geometry checks; original 943 failure retained");return 0;}return 1;
 }
 [STAThread]static int Main(string[] args){try{Need(args.Length==2&&args[1]=="capture-budget-diagnostic","Explicit diagnostic only");Guard(args[0]);return Run();}catch(Exception error){Console.WriteLine(error);return 1;}}
}
