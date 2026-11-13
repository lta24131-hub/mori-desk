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

// Independent VM-only observations and unmodified real WebM regression.
// Does not replace or weaken the original failed MP4 assertion.
internal static class R3MediaProbeRunner {
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
 static List<object> Observe(bool visible){
  var samples=new List<object>();var events=new List<object>();var observations=new List<object>();var clock=Stopwatch.StartNew();var player=new VideoPlayer(Mp4);var host=new Grid{Width=640,Height=430};host.Children.Add(player.Root);HwndSource source=null;Window window=null;IntPtr handle=IntPtr.Zero;int heartbeats=0;
  var heartbeat=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(100)};heartbeat.Tick+=delegate{heartbeats++;};
  Action<string> sample=label=>samples.Add(new{label,elapsedMs=clock.Elapsed.TotalMilliseconds,heartbeats,playing=player.State.Playing,seeking=player.State.Seeking,ready=player.State.Ready,duration=player.State.Duration,statePosition=player.State.Position,nativePosition=player.Media.Position.TotalSeconds,timerRunning=player.TimerRunning,failed=player.Failed,usingBrowser=player.UsingBrowser,mediaSource=player.Media.Source==null?null:player.Media.Source.AbsoluteUri,mediaVisible=player.Media.IsVisible,rootVisible=player.Root.IsVisible,hostVisible=host.IsVisible,hwnd=handle.ToInt64(),hwndVisible=handle!=IntPtr.Zero&&IsWindowVisible(handle),hasPresentationSource=PresentationSource.FromVisual(player.Root)!=null,isBuffering=player.Media.IsBuffering,bufferingProgress=player.Media.BufferingProgress,downloadProgress=player.Media.DownloadProgress,canPause=player.Media.CanPause,hasAudio=player.Media.HasAudio,hasVideo=player.Media.HasVideo,speedRatio=player.Media.SpeedRatio,clockState=player.Media.Clock==null?null:player.Media.Clock.CurrentState.ToString()});
  player.Media.MediaOpened+=delegate{events.Add(new{kind="MediaOpened",elapsedMs=clock.Elapsed.TotalMilliseconds});sample("event-opened");};
  player.Media.MediaEnded+=delegate{events.Add(new{kind="MediaEnded",elapsedMs=clock.Elapsed.TotalMilliseconds});sample("event-ended");};
  player.Media.MediaFailed+=delegate(object sender,ExceptionRoutedEventArgs e){events.Add(new{kind="MediaFailed",elapsedMs=clock.Elapsed.TotalMilliseconds,error=e.ErrorException==null?null:e.ErrorException.ToString()});sample("event-failed");};
  Action<bool,string> observe=(condition,label)=>observations.Add(new{label,condition,elapsedMs=clock.Elapsed.TotalMilliseconds});
  try{
   if(visible){window=new Window{Title="Nook VM native playback diagnostic",Content=host,Width=680,Height=490,Left=40,Top=40,WindowStartupLocation=WindowStartupLocation.Manual,ShowActivated=false,ShowInTaskbar=false};window.Show();handle=new WindowInteropHelper(window).Handle;}
   else{source=new HwndSource(new HwndSourceParameters("Mori isolated media regression"){Width=640,Height=430,WindowStyle=0});source.RootVisual=host;handle=source.Handle;host.Measure(new Size(640,430));host.Arrange(new Rect(0,0,640,430));host.UpdateLayout();}
   heartbeat.Start();sample("before-start");player.Start();sample("after-start");var opening=Stopwatch.StartNew();while(!player.State.Ready&&!player.Failed&&opening.ElapsedMilliseconds<8000)Pump(100);sample("after-original-open-wait");
   observe(player.State.Ready&&!player.Failed&&Math.Abs(player.State.Duration-12)<.1,"original real MediaElement opens 12-second MP4");
   if(player.State.Ready&&!player.Failed){
    player.SetVolume(.28);player.ToggleMute();player.ToggleMute();sample("after-original-volume-mute-restore");
    player.Toggle();sample("after-pause-before-seek");player.Seek(8);Pump(500);sample("after-original-pause-seek-500ms");observe(!player.State.Playing&&!player.TimerRunning&&Math.Abs(player.Media.Position.TotalSeconds-8)<.3,"original paused seek lands at requested time without resuming");
    player.BeginSeek();sample("mouse-seek-begin");player.State.Seek(3);player.EndSeek();Pump(300);sample("after-original-mouse-seek-300ms");observe(!player.State.Playing&&Math.Abs(player.Media.Position.TotalSeconds-3)<.3,"original mouse-seek preserves pause and commits selected time");
    player.Toggle();var resume=Stopwatch.StartNew();sample("resume-immediate");Pump(700);sample("resume-original-700ms");observe(player.State.Playing&&player.TimerRunning&&player.Media.Position.TotalSeconds>3.3,"original 700ms play resumes from new seek point");
    foreach(int target in new[]{1500,3000,5000}){Pump((int)Math.Max(1,target-resume.ElapsedMilliseconds));sample("resume-diagnostic-"+target+"ms");}
   }
  }finally{heartbeat.Stop();player.Dispose();sample("after-dispose");if(source!=null)source.Dispose();if(window!=null)window.Close();}
  Save(visible?"mp4-visible.json":"mp4-hidden.json",new{scope="diagnostic observation only; original 888 failure remains",mode=visible?"actual-visible-window":"original-style0-HwndSource",appSha256=AppHash,videoSha256=Hash(Mp4),samples,events,originalConditions=observations});
  return samples;
 }
 static int Mp4Diagnostic(){Need(Hash(Mp4)==Mp4Hash,"Same generated MP4 as failed 888");var hidden=Observe(false);var visible=Observe(true);Need(Hash(Mp4)==Mp4Hash,"Diagnostic leaves input unchanged");Save("mp4-diagnostic.json",new{diagnosticCompleted=true,productPassed=(object)null,original888RetainedFailed=true,samples=hidden.Count+visible.Count,appSha256=AppHash,videoSha256=Mp4Hash});Console.WriteLine("OBSERVED "+(hidden.Count+visible.Count)+" MP4 diagnostic samples; original 888 failure retained");return 0;}
 static void VerifyTools(){string path=Path.Combine(ToolsRoot,"ffmpeg-inputs.json");Need(Hash(path)==ManifestHash,"Reviewed FFmpeg source manifest");var manifest=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path,Utf8));foreach(Dictionary<string,object> row in (IEnumerable)manifest["files"]){string relative=Convert.ToString(row["path"]);Need(Regex.IsMatch(relative,@"\Affmpeg/bin/[A-Za-z0-9.-]+\.(exe|dll)\z"),"Fixed tool dependency filename");string file=Path.Combine(ToolsRoot,relative.Replace('/','\\'));Need(new FileInfo(file).Length==Convert.ToInt64(row["bytes"])&&Hash(file)==Convert.ToString(row["sha256"]),"Exact reviewed tool dependency");}}
 static string RunTool(string name,string arguments,int seconds){
  Need(name=="ffmpeg.exe"||name=="ffprobe.exe","Only fixed reviewed media tools");var start=new ProcessStartInfo(Path.Combine(ToolsRoot,"ffmpeg","bin",name),arguments){WorkingDirectory=Root,UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};var stdout=new StringBuilder();var stderr=new StringBuilder();object sync=new object();bool truncated=false;int code;
  using(var process=new Process{StartInfo=start}){process.OutputDataReceived+=(s,e)=>{if(e.Data!=null)lock(sync){if(stdout.Length<300000)stdout.AppendLine(e.Data);else truncated=true;}};process.ErrorDataReceived+=(s,e)=>{if(e.Data!=null)lock(sync){if(stderr.Length<300000)stderr.AppendLine(e.Data);else truncated=true;}};Need(process.Start(),"Fixed tool starts inside inherited Job");IntPtr handle=process.Handle;bool member;Need(IsProcessInJob(handle,IntPtr.Zero,out member)&&member,"Tool inherits Job");process.BeginOutputReadLine();process.BeginErrorReadLine();if(!process.WaitForExit(seconds*1000)){process.Kill();process.WaitForExit(5000);throw new TimeoutException(name+" bounded timeout");}process.WaitForExit();code=process.ExitCode;}
  Save(name+".json",new{exitCode=code,truncated,stdout=stdout.ToString(),stderr=stderr.ToString()});Need(code==0&&!truncated,"Full bounded tool result");return stdout.ToString();
 }
 static int Webm(){
  VerifyTools();string file=Path.Combine(Root,"real-pattern.webm");Need(!File.Exists(file),"New generated WebM only");
  RunTool("ffmpeg.exe","-hide_banner -loglevel error -nostdin -n -f lavfi -i testsrc2=size=640x360:rate=30 -f lavfi -i sine=frequency=440:sample_rate=48000 -t 12 -c:v libvpx -deadline realtime -cpu-used 8 -b:v 600k -pix_fmt yuv420p -r 30 -c:a libvorbis -b:a 64k -threads 2 \""+file+"\"",90);
  string raw=RunTool("ffprobe.exe","-v error -show_entries stream=codec_type,codec_name,width,height,r_frame_rate,pix_fmt,duration -show_entries format=duration -of json \""+file+"\"",20);var probe=Json.Deserialize<Dictionary<string,object>>(raw);var streams=((IEnumerable)probe["streams"]).Cast<Dictionary<string,object>>().ToArray();var video=streams.Single(x=>Convert.ToString(x["codec_type"])=="video");double duration=double.Parse(Convert.ToString(((Dictionary<string,object>)probe["format"])["duration"]),CultureInfo.InvariantCulture);Need(Convert.ToInt32(video["width"])==640&&Convert.ToInt32(video["height"])==360&&Convert.ToString(video["codec_name"])=="vp8"&&Convert.ToString(video["pix_fmt"])=="yuv420p"&&Convert.ToString(video["r_frame_rate"])=="30/1"&&Math.Abs(duration-12)<.05,"640x360 twelve-second VP8 picture fixture");Need(streams.Any(x=>Convert.ToString(x["codec_type"])=="audio"&&Convert.ToString(x["codec_name"])=="vorbis"),"Synthetic Vorbis audio");Need(new FileInfo(file).Length>3000&&new FileInfo(file).Length<=8*1024*1024,"Bounded substantial fixture");string hash=Hash(file);Save("webm-fixture.json",new{source="VM-generated testsrc2 and sine only",manifestSha256=ManifestHash,appSha256=AppHash,file,sha256=hash,bytes=new FileInfo(file).Length,probe});
  Environment.SetEnvironmentVariable("MORI_TEST_WEBM",file);int count=0;var passed=new List<string>();using(var log=new StreamWriter(Path.Combine(Root,"webm-checks.txt"),false,Utf8)){log.AutoFlush=true;Action<bool,string> check=(ok,message)=>{log.WriteLine((ok?"PASS ":"FAIL ")+message);if(!ok)throw new InvalidOperationException(message);passed.Add(message);count++;};BrowserVideoTests.Run(Path.Combine(Root,"original-webm"),check);}
  foreach(string proof in new[]{"WebM clock advances with real decoded playback","real WebM frame contains decoded picture, not a black placeholder","last WebM preview closes its browser runtime process","WebM paused seek lands at chosen frame without resuming"})Need(passed.Contains(proof),"Full real WebM branch completed: "+proof);Need(Hash(file)==hash,"Generated input hash unchanged across whole playback suite");Save("webm-result.json",new{passed=true,checksPassed=count,appSha256=AppHash,videoSha256=hash,originalBrowserVideoTestsUnmodified=true});Console.WriteLine("PASS "+count+" actual WebM checks; product="+AppHash+"; video="+hash);return 0;
 }
 [STAThread]static int Main(string[] args){try{Need(args.Length==2&&(args[1]=="mp4-diagnostic"||args[1]=="actual-webm"),"Explicit fixed diagnostic or WebM suite");Guard(args[0]);return args[1]=="mp4-diagnostic"?Mp4Diagnostic():Webm();}catch(Exception error){if(Root!=null&&Directory.Exists(Root))try{Save("media-probe-error.json",new{error=error.ToString()});}catch{}Console.WriteLine(error);return 1;}}
}
