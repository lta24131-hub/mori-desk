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

[assembly: AssemblyTitle("Nook cancelled-exit native pin journey - VM TEST ONLY")]
// Actual Paste and native Closing handlers in an isolated main window. Does
// not send global keys, register shell handlers or start a normal application.
internal static class PinCancelledExitGuest {
 static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=1048576,RecursionLimit=20};
 static readonly UTF8Encoding Utf8=new UTF8Encoding(false);static readonly List<string> Checks=new List<string>();
 static string Package,Root;static bool GuestVerified;
 static void Need(bool value,string message){if(!value)throw new InvalidDataException(message);Checks.Add(message);}
 static void NoLinks(string path){for(string part=Path.GetFullPath(path);!string.IsNullOrEmpty(part);part=Path.GetDirectoryName(part))if((File.Exists(part)||Directory.Exists(part))&&(File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked fixture path refused.");}
 static string Hash(string path){NoLinks(path);using(var file=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");}
 static string Text(object value){return Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture);}
 static void Save(string path,object value){NoLinks(path);using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new StreamWriter(file,Utf8)){writer.Write(Json.Serialize(value));writer.Flush();file.Flush(true);}}
 static void Guest(){
  bool vm=false;using(var query=new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem"))foreach(ManagementObject row in query.Get())using(row)vm|=Regex.IsMatch(Text(row["Manufacturer"])+" "+Text(row["Model"]),"QEMU|KVM|Bochs",RegexOptions.IgnoreCase);
  Need(vm&&Environment.UserName.Equals("NookTester",StringComparison.OrdinalIgnoreCase),"Dedicated NookTester QEMU guest required before any write.");GuestVerified=true;
  Package=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');Need(Regex.IsMatch(Package,@"\AC:\\NookLab\\Audit\\apps\\[a-fA-F0-9]{12}\z"),"Hash-addressed VM package required.");NoLinks(Package);
  string file=Path.Combine(Package,"pin-cancelled-exit-manifest.json");Need(new FileInfo(file).Length<1048576,"Bounded manifest required.");var manifest=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(file));
  var files=((IEnumerable)manifest["files"]).Cast<Dictionary<string,object>>().ToArray();Need(files.Length>0&&files.Length<80,"Bounded sealed payload required.");
  foreach(var entry in files){string relative=Text(entry["path"]);Need(!Path.IsPathRooted(relative)&&!relative.Split('/','\\').Contains(".."),"Relative payload required.");string path=Path.GetFullPath(Path.Combine(Package,relative));Need(path.StartsWith(Package+"\\",StringComparison.OrdinalIgnoreCase)&&Hash(path)==Text(entry["sha256"]),"Sealed payload verified: "+relative);}
  Need(Hash(Path.Combine(Package,"Mori.exe"))=="95122AAA3FB4F69AB2A1D259B74500A71424B2439F8375784E86BCC563237716","Exact final 0.1.45 candidate required.");
  Root=@"C:\NookLab\Audit\pin-cancelled-exit-"+Guid.NewGuid().ToString("N");NoLinks(Root);Need(!Directory.Exists(Root),"Fresh isolated fixture required.");Directory.CreateDirectory(Root);
  Environment.SetEnvironmentVariable("MORI_DESK_DATA",Path.Combine(Root,"profile"));
 }
 static void Until(Func<bool> complete,string stage){
  var clock=Stopwatch.StartNew();var frame=new DispatcherFrame();Exception failure=null;
  var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(10)};timer.Tick+=(s,e)=>{try{if(complete())frame.Continue=false;else if(clock.ElapsedMilliseconds>12000){failure=new TimeoutException(stage);frame.Continue=false;}}catch(Exception error){failure=error;frame.Continue=false;}};
  timer.Start();try{if(!complete())Dispatcher.PushFrame(frame);if(failure!=null)throw failure;}finally{timer.Stop();}
 }
 static BitmapSource Picture(byte value){byte[] pixels=new byte[320*180*4];for(int i=0;i<pixels.Length;i+=4){pixels[i]=value;pixels[i+1]=107;pixels[i+2]=211;pixels[i+3]=255;}var image=BitmapSource.Create(320,180,96,96,PixelFormats.Bgra32,null,pixels,1280);image.Freeze();return image;}
 static CancellationToken Token(){return (CancellationToken)typeof(MoriDesk.DeskRuntime).GetProperty("PinCancellation",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null,null);}
 static bool Pasting(){return (bool)typeof(QingJie.AppState).GetField("pastingPin",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);}
 static int NativePins(Application app){return app.Windows.Cast<Window>().Count(window=>window is QingJie.PinWindow&&window.IsVisible&&new WindowInteropHelper(window).Handle!=IntPtr.Zero);}
 static void Paste(Application app,int count,byte value){
  Clipboard.SetImage(Picture(value));QingJie.AppState.Paste(); // same actual entry called by the F3 dispatcher, without global key injection
  Until(()=>!Pasting()&&QingJie.AppState.Pins.Records.Count==count&&NativePins(app)==count,"Paste did not present a committed native pin.");
  Need(QingJie.AppState.Pins.Records.Count==count&&NativePins(app)==count,"Actual Paste commits and shows native pin count "+count);
 }
 static int Run(){
  var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};Mori.MainWindow main=null;Mori.Library library=null;IDataObject oldClipboard=null;bool clipboardCaptured=false,clipboardRestored=false,cleaned=false,passed=false;Exception failure=null;var notices=new List<string>();
  try{
   try{oldClipboard=Clipboard.GetDataObject();clipboardCaptured=true;}catch{}
   typeof(QingJie.Preferences).GetField("TestFilePath",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,Path.Combine(Root,"capture-settings.json"));
   MoriDesk.Entry.InteractiveShell=false;MoriDesk.Entry.ClassicEnabled=true;MoriDesk.Entry.BoardEnabled=false;
   string libraryRoot=Path.Combine(Root,"library");new Mori.Settings{LibraryPath=libraryRoot,CloseToTray=false}.Save();library=new Mori.Library(libraryRoot);
   main=new Mori.MainWindow(library){PreviewMode=true};main.DeferDeskServices();main.Window.Title="Nook · 贴图取消退出验收";bool rendered=false;main.Window.ContentRendered+=(s,e)=>rendered=true;main.Window.Show();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
   SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));Until(()=>rendered&&main.Window.IsLoaded&&main.Window.IsVisible,"Main window did not render.");
   MoriDesk.DeskRuntime.Notify=message=>notices.Add(message);MoriDesk.DeskRuntime.Start(true,libraryRoot);
   Need(MoriDesk.DeskRuntime.Started&&!MoriDesk.Entry.InteractiveShell&&QingJie.AppState.Pins.Folder.StartsWith(Root+"\\",StringComparison.OrdinalIgnoreCase),"Preview runtime owns only the generated fixture; normal startup and shell registration remain disabled.");
   Need(QingJie.AppState.Hotkeys==null,"No global hotkeys are installed by this journey.");
   Paste(app,1,37);string first=QingJie.AppState.Pins.ImagePath(QingJie.AppState.Pins.Records[0]);string firstHash=Hash(first);var abandoned=Token();
   main.PreviewMode=false;Task<bool> exit;
   try{exit=(Task<bool>)typeof(Mori.MainWindow).GetMethod("ExitForUpdateAsync",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(main,new object[]{Path.Combine(Root,"unused-backup"),(Func<bool>)(()=>false),CancellationToken.None});Until(()=>exit.IsCompleted,"Cancelled update exit did not finish.");}
   finally{main.PreviewMode=true;}
   Need(!exit.GetAwaiter().GetResult()&&main.Window.IsVisible&&new WindowInteropHelper(main.Window).Handle!=IntPtr.Zero,"Real native Closing is cancelled by the later installer-readiness handler and the main window remains usable.");
   Need(abandoned.IsCancellationRequested&&!Token().IsCancellationRequested&&!QingJie.AppState.Quitting,"Actual Closing cancellation keeps abandoned work cancelled and restores a usable pin lifetime.");
   Need(!Directory.Exists(Path.Combine(Root,"unused-backup")),"Refused installer readiness creates no update backup or installation work.");
   Paste(app,2,79);Need(Hash(first)==firstHash&&new QingJie.PinStore(QingJie.AppState.Pins.Folder).Records.Count==2,"Second Paste after cancelled exit preserves the first original and both committed records.");
   Need(notices.Count==0,"Both actual Paste operations finish without a failure or busy notification.");passed=true;
  }catch(Exception error){failure=error is TargetInvocationException&&error.InnerException!=null?error.InnerException:error;}
  finally{
   try{
    QingJie.AppState.Quitting=true;foreach(var pin in app.Windows.Cast<Window>().OfType<QingJie.PinWindow>().ToArray())pin.Close();
    if(main!=null){main.PreviewMode=true;main.CloseForReplacement();}MoriDesk.DeskRuntime.Stop();
    cleaned=!app.Windows.Cast<Window>().Any(window=>window.IsVisible);if(!cleaned)throw new InvalidOperationException("Fixture native windows did not close normally.");
   }catch(Exception error){passed=false;if(failure==null)failure=error;}
   try{if(clipboardCaptured){if(oldClipboard==null)Clipboard.Clear();else Clipboard.SetDataObject(oldClipboard,true);clipboardRestored=true;}else Clipboard.Clear();}catch{}
   if(library!=null)library.Dispose();app.Shutdown();
  }
  Save(Path.Combine(Package,"pin-cancelled-exit-result.json"),new{passed,appSha256=Hash(Path.Combine(Package,"Mori.exe")),scope="VM-only native main and pin windows; real Paste entry plus real update Closing cancellation; no OS F3 injection, installer commit, shell registration or production profile",checks=Checks,notices,fixture=Root,nativeWindowsClosed=cleaned,clipboardCaptured,clipboardRestored,hostExecuted=false,installedApplicationTouched=false,error=failure==null?null:failure.ToString()});return passed?0:1;
 }
 [STAThread]static int Main(string[] args){try{Guest();Need(args.Length==1&&args[0]=="--run","Explicit VM command required.");using(var watchdog=new Timer(_=>Environment.Exit(124),null,60000,Timeout.Infinite))return Run();}catch(Exception error){Console.WriteLine(error);if(GuestVerified&&Package!=null&&Regex.IsMatch(Package,@"\AC:\\NookLab\\Audit\\apps\\[a-fA-F0-9]{12}\z"))try{string result=Path.Combine(Package,"pin-cancelled-exit-result.json");if(!File.Exists(result))Save(result,new{passed=false,error=error.ToString(),checks=Checks});}catch{}return 1;}}
}
