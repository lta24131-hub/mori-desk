using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Mori;

// Independent visible native-MP4 bounded functional supplement.
// Retains original 700ms/1000ms observations; never claims those timing gates passed.
internal static class R3MediaFunctionalRunner {
 const string AppHash="97B122310B1033BE7EBDA63730F929C256D8D0972CBC3497C7A7D6539CCA50D6";
 const string Mp4=@"C:\NookLab\Audit\apps\76D90129C8BD\fixtures\library-video.mp4";
 const string Mp4Hash="0430D51A7C8EF567F00130EB303DBC0236DAD1972CF54467C9FE3554C2666B1B";
 const string ToolsRoot=@"C:\NookLab\Audit\apps\A4548CA73B4C";
 const string ManifestHash="ACA85133E64204B4E03AD85CEC03B784A376127D495CA102456936BFF790D2F4";
 static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=2*1024*1024};
 static readonly UTF8Encoding Utf8=new UTF8Encoding(false);
 static string Root;
 static void Need(bool ok,string why){if(!ok)throw new InvalidDataException(why);}
 static void Plain(string path){for(string part=Path.GetFullPath(path);!string.IsNullOrEmpty(part);part=Path.GetDirectoryName(part))if((File.Exists(part)||Directory.Exists(part))&&(File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked path refused");}
 static string Hash(string path){Plain(path);using(var f=File.OpenRead(path))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(f)).Replace("-","");}
 static void Save(string name,object value){string path=Path.Combine(Root,name);Plain(path);using(var f=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read))using(var w=new StreamWriter(f,Utf8)){w.Write(Json.Serialize(value));w.Flush();f.Flush(true);}}
 [DllImport("kernel32.dll")]static extern IntPtr GetCurrentProcess();
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool IsProcessInJob(IntPtr process,IntPtr job,out bool value);
 [DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr window);
 static void Set(Assembly a,string type,string field,object value){a.GetType(type,true).GetField(field,BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).SetValue(null,value);}
 static void Guard(string root){
  bool vm=false;using(var q=new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem"))foreach(ManagementObject row in q.Get())using(row)vm|=Regex.IsMatch(Convert.ToString(row["Manufacturer"])+" "+Convert.ToString(row["Model"]),"QEMU|KVM|Bochs",RegexOptions.IgnoreCase);
  Need(vm&&Environment.UserName.Equals("NookTester",StringComparison.OrdinalIgnoreCase),"Dedicated VM required before writes");bool member;Need(IsProcessInJob(GetCurrentProcess(),IntPtr.Zero,out member)&&member,"Must inherit wrapper Job before fixtures/tools");Root=Path.GetFullPath(root);Plain(Root);string data=Environment.GetEnvironmentVariable("MORI_DESK_DATA");Need(Regex.IsMatch(Root,@"\AC:\\NookLab\\Audit\\s-[a-f0-9]{12}\\fixture\z")&&data==Path.Combine(Path.GetDirectoryName(Root),"data"),"Wrapper-owned fixture/data only");Need(Hash(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Mori.exe"))==AppHash,"Exact frozen R3 product");Need(!Directory.Exists(Root),"Fresh fixture required");Directory.CreateDirectory(Root);
  Environment.SetEnvironmentVariable("MORI_TEST_VIDEO",null);Environment.SetEnvironmentVariable("MORI_TEST_WEBM",null);
  var product=typeof(MainWindow).Assembly;Set(product,"QingJie.Preferences","TestFilePath",Path.Combine(data,"capture","settings.json"));Set(product,"QingJie.OcrLanguagePacks","TestFolder",Path.Combine(data,"languages"));Set(product,"QingJie.OfflinePacks","TestFolder",Path.Combine(data,"offline"));Set(product,"QingJie.AppState","Settings",Activator.CreateInstance(product.GetType("QingJie.Preferences",true)));BookmarkIcons.NetworkEnabled=false;
  var app=Application.Current??new Application();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
 }
 static void Pump(int milliseconds){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(Math.Max(1,milliseconds))};timer.Tick+=delegate{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}

 [DllImport("user32.dll")]static extern IntPtr GetForegroundWindow();
 static object CaptureFrame(VideoPlayer player,Window window){
  window.Activate();Pump(200);IntPtr handle=new WindowInteropHelper(window).Handle;Need(IsWindowVisible(handle)&&GetForegroundWindow()==handle,"VM playback window is visible and foreground for real screen frame");
  var top=player.Media.PointToScreen(new Point(0,0));var bottom=player.Media.PointToScreen(new Point(player.Media.ActualWidth,player.Media.ActualHeight));int width=(int)Math.Round(bottom.X-top.X),height=(int)Math.Round(bottom.Y-top.Y);Need(width>=100&&height>=100&&width<=1600&&height<=1200,"Bounded actual media screen rectangle");string file=Path.Combine(Root,"mp4-visible-frame.png");Need(!File.Exists(file),"Fresh frame evidence only");int colors;
  using(var bitmap=new System.Drawing.Bitmap(width,height)){using(var graphics=System.Drawing.Graphics.FromImage(bitmap))graphics.CopyFromScreen((int)Math.Round(top.X),(int)Math.Round(top.Y),0,0,new System.Drawing.Size(width,height),System.Drawing.CopyPixelOperation.SourceCopy);var values=new HashSet<int>();for(int y=0;y<height;y+=5)for(int x=0;x<width;x+=5)values.Add(bitmap.GetPixel(x,y).ToArgb());colors=values.Count;bitmap.Save(file,System.Drawing.Imaging.ImageFormat.Png);}
  Need(colors>32,"Actual displayed patterned video frame is not a uniform blank");return new{file="mp4-visible-frame.png",sha256=Hash(file),bytes=new FileInfo(file).Length,width,height,uniqueSampledColors=colors,source="VM-only CopyFromScreen of visible foreground MediaElement rectangle"};
 }
 static int Functional(){
  Need(Hash(Mp4)==Mp4Hash,"Same pinned fixture as original 888 and 896");var checks=new List<string>();var samples=new List<object>();var events=new List<object>();var timing=new List<object>();var clock=Stopwatch.StartNew();var player=new VideoPlayer(Mp4);var host=new Grid{Width=640,Height=430};host.Children.Add(player.Root);var window=new Window{Title="Nook VM native MP4 functional check",Content=host,Width=680,Height=490,Left=40,Top=40,WindowStartupLocation=WindowStartupLocation.Manual,ShowActivated=true,ShowInTaskbar=false};int ended=0,heartbeats=0;Exception failure=null;bool completed=false,disposed=false,closed=false;object frame=null;window.Closed+=(s,e)=>closed=true;
  var heartbeat=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(100)};heartbeat.Tick+=delegate{heartbeats++;};
  Action<string> sample=label=>samples.Add(new{label,elapsedMs=clock.Elapsed.TotalMilliseconds,heartbeats,playing=player.State.Playing,seeking=player.State.Seeking,ready=player.State.Ready,duration=player.State.Duration,statePosition=player.State.Position,nativePosition=player.Media.Position.TotalSeconds,timerRunning=player.TimerRunning,failed=player.Failed,usingBrowser=player.UsingBrowser,mediaSource=player.Media.Source==null?null:player.Media.Source.AbsoluteUri,endedEvents=ended,mediaVisible=player.Media.IsVisible,windowVisible=window.IsVisible});
  Action<bool,string> check=(condition,label)=>{sample(label);if(!condition)throw new InvalidOperationException(label);checks.Add(label);};
  Action<Func<bool>,Stopwatch,int,string> until=(predicate,phase,bound,label)=>{while(!predicate()&&!player.Failed&&phase.ElapsedMilliseconds<bound)Pump(50);sample(label);check(predicate()&&!player.Failed,label);};
  player.Media.MediaOpened+=delegate{events.Add(new{kind="MediaOpened",elapsedMs=clock.Elapsed.TotalMilliseconds});};player.Media.MediaEnded+=delegate{ended++;events.Add(new{kind="MediaEnded",elapsedMs=clock.Elapsed.TotalMilliseconds,nativePosition=player.Media.Position.TotalSeconds});};player.Media.MediaFailed+=delegate(object sender,ExceptionRoutedEventArgs e){events.Add(new{kind="MediaFailed",elapsedMs=clock.Elapsed.TotalMilliseconds,error=e.ErrorException==null?null:e.ErrorException.ToString()});};
  try{
   window.Show();window.Activate();host.UpdateLayout();heartbeat.Start();player.Start();var opening=Stopwatch.StartNew();until(()=>player.State.Ready,opening,8000,"Visible native decoder opens within original 8-second bound");check(!player.UsingBrowser&&Math.Abs(player.State.Duration-12)<.1,"Native MediaElement opens original 12-second MP4");
   player.SetVolume(.28);check(Math.Abs(player.Media.Volume-.28)<.0001,"Native volume accepts chosen level");player.ToggleMute();check(player.Media.Volume==0&&Math.Abs(player.Sound.Level-.28)<.0001,"Native mute preserves level");player.ToggleMute();check(Math.Abs(player.Media.Volume-.28)<.0001&&!player.Sound.Muted,"Native unmute restores level");
   player.Toggle();player.Seek(8);Pump(500);check(!player.State.Playing&&!player.TimerRunning&&Math.Abs(player.Media.Position.TotalSeconds-8)<.3,"Original paused seek condition at 500ms");player.BeginSeek();player.State.Seek(3);player.EndSeek();Pump(300);check(!player.State.Playing&&Math.Abs(player.Media.Position.TotalSeconds-3)<.3,"Original mouse-seek pause condition at 300ms");
   player.Toggle();var resume=Stopwatch.StartNew();Pump(700);bool originalResume=player.State.Playing&&player.TimerRunning&&player.Media.Position.TotalSeconds>3.3;timing.Add(new{phase="resume",nominalMs=700,actualMs=resume.Elapsed.TotalMilliseconds,condition=originalResume,nativePosition=player.Media.Position.TotalSeconds});sample("original resume at 700ms observed, not converted to pass");until(()=>player.State.Playing&&player.TimerRunning&&!player.UsingBrowser&&player.Media.Position.TotalSeconds>3.3,resume,5000,"Native clock genuinely advances from seek within bounded 5 seconds");double resumed=player.Media.Position.TotalSeconds;var advance=Stopwatch.StartNew();until(()=>player.State.Playing&&player.TimerRunning&&player.Media.Position.TotalSeconds>resumed+.15,advance,3000,"Native clock keeps advancing after resume");
   frame=CaptureFrame(player,window);check(frame!=null,"One actual displayed video frame retained for visual review");
   int endedBefore=ended;player.Seek(11.8);var loop=Stopwatch.StartNew();Pump(1000);bool originalLoop=player.State.Playing&&player.Media.Position.TotalSeconds<3;timing.Add(new{phase="loop",nominalMs=1000,actualMs=loop.Elapsed.TotalMilliseconds,condition=originalLoop,nativePosition=player.Media.Position.TotalSeconds,endedEvents=ended-endedBefore});sample("original loop at 1000ms observed, not converted to pass");until(()=>ended>endedBefore&&player.State.Playing&&player.TimerRunning&&player.Media.Position.TotalSeconds<3,loop,5000,"Actual MediaEnded event loops native clock back to beginning within 5 seconds");double loopPosition=player.Media.Position.TotalSeconds;var loopAdvance=Stopwatch.StartNew();until(()=>player.State.Playing&&player.TimerRunning&&player.Media.Position.TotalSeconds>loopPosition+.15&&player.Media.Position.TotalSeconds<6,loopAdvance,3000,"Looped video genuinely advances again after returning to beginning");
   player.SetVisible(false);check(!player.TimerRunning&&player.State.Playing,"Hiding suspends native timer while preserving play intent");Pump(300);double paused=player.Media.Position.TotalSeconds;Pump(500);check(!player.TimerRunning&&Math.Abs(player.Media.Position.TotalSeconds-paused)<.08,"Hidden native decoder remains paused across observed half second");player.SetVisible(true);check(player.TimerRunning&&player.State.Playing,"Reshow restores playing timer");var shown=Stopwatch.StartNew();until(()=>player.State.Playing&&player.TimerRunning&&player.Media.Position.TotalSeconds>paused+.15,shown,5000,"Reshown native decoder genuinely resumes clock within 5 seconds");check(heartbeats>5&&!player.Failed&&!player.UsingBrowser,"Dispatcher remains responsive with no native decode failure/fallback");completed=true;
  }catch(Exception error){failure=error;}
  finally{heartbeat.Stop();try{player.Dispose();disposed=!player.TimerRunning&&player.Media.Source==null;sample("disposed");if(!disposed)throw new InvalidOperationException("Dispose did not release decoder/source/timer");checks.Add("Disposal releases native decoder source and progress timer");}catch(Exception error){completed=false;if(failure==null)failure=error;}try{window.Close();Pump(100);if(!closed||window.IsVisible)throw new InvalidOperationException("Visible test window did not close normally");checks.Add("Visible native test window closes normally");}catch(Exception error){completed=false;if(failure==null)failure=error;}bool unchanged=Hash(Mp4)==Mp4Hash;if(!unchanged){completed=false;if(failure==null)failure=new InvalidDataException("MP4 input changed");}else checks.Add("Original MP4 bytes unchanged");Save("mp4-functional.json",new{passed=completed&&disposed&&closed,scope="Independent actual-visible-window bounded native MP4 function only; not fixed 700ms/1000ms performance pass",original888RetainedFailed=true,original896RetainedFailed=true,fixedTimingRequirementPassed=false,appSha256=AppHash,videoSha256=Mp4Hash,checksPassed=checks.Count,checks,originalTimingObservations=timing,samples,events,frame,disposed,closed,error=failure==null?null:failure.ToString()});}
  if(!completed||!disposed||!closed){Console.WriteLine(failure);return 1;}Console.WriteLine("PASS "+checks.Count+" bounded native-MP4 functional checks; original timing failures retained");return 0;
 }
 [STAThread]static int Main(string[] args){try{Need(args.Length==2&&args[1]=="native-mp4-functional","Only explicit reviewed functional suite");Guard(args[0]);return Functional();}catch(Exception error){if(Root!=null&&Directory.Exists(Root))try{Save("media-probe-error.json",new{error=error.ToString()});}catch{}Console.WriteLine(error);return 1;}}
}
