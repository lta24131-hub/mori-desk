using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace MoriDesk {
 internal static class BrowserDesktopFeedbackTests {
  [System.Runtime.InteropServices.DllImport("user32.dll")]static extern IntPtr GetForegroundWindow();
  static bool HostRunning(){Mutex mutex;if(!Mutex.TryOpenExisting("Local\\"+BrowserDesktopFeedback.PipeName(),out mutex))return false;mutex.Dispose();return true;}
  static bool Protocol(Dictionary<string,object> message){
   var json=new System.Web.Script.Serialization.JavaScriptSerializer();message["action"]="collectionFeedback";
   var start=new ProcessStartInfo(typeof(Entry).Assembly.Location,"chrome-extension://"+new string('a',32)+"/"){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardInput=true,RedirectStandardOutput=true};
   using(var process=Process.Start(start))try{
    byte[] body=System.Text.Encoding.UTF8.GetBytes(json.Serialize(message));var input=process.StandardInput.BaseStream;input.Write(BitConverter.GetBytes(body.Length),0,4);input.Write(body,0,body.Length);input.Flush();process.StandardInput.Close();
    var read=System.Threading.Tasks.Task.Run(()=>{var output=process.StandardOutput.BaseStream;var header=ReadExact(output,4);int length=BitConverter.ToInt32(header,0);if(length<1||length>100000)throw new InvalidDataException();return json.Deserialize<Dictionary<string,object>>(System.Text.Encoding.UTF8.GetString(ReadExact(output,length)));});
    if(!read.Wait(15000))throw new TimeoutException("Native feedback exceeded the extension deadline");object ok;return read.Result.TryGetValue("ok",out ok)&&ok is bool&&(bool)ok;
   }finally{if(!process.WaitForExit(1000))process.Kill();}
  }
  static byte[] ReadExact(Stream input,int length){var bytes=new byte[length];int at=0;while(at<length){int count=input.Read(bytes,at,length-at);if(count==0)throw new EndOfStreamException();at+=count;}return bytes;}
  static Dictionary<string,object> Message(string id,string state){return new Dictionary<string,object>{{"id",id},{"state",state},{"name","工作区灵感.png"},{"folderName","设计参考"}};}
  static bool HasText(DependencyObject node,string text){var label=node as System.Windows.Controls.TextBlock;if(label!=null&&label.Text==text)return true;for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)if(HasText(VisualTreeHelper.GetChild(node,i),text))return true;return false;}
  static IEnumerable<T> Children<T>(DependencyObject node) where T:DependencyObject {var match=node as T;if(match!=null)yield return match;for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)foreach(var child in Children<T>(VisualTreeHelper.GetChild(node,i)))yield return child;}
  static string Render(CollectionNotificationWindow window){window.UpdateLayout();var image=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);image.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var stream=new MemoryStream()){encoder.Save(stream);return Convert.ToBase64String(stream.ToArray());}}
  public static void Run(string root,Action<bool,string> check){
   string id=Guid.NewGuid().ToString();var valid=Message(id,"saving");valid["path"]="C:\\untrusted.exe";var clean=BrowserDesktopFeedback.Clean(valid);check(!clean.ContainsKey("path"),"notifications never accept executable or saved file paths");
   foreach(var pair in new[]{new[]{"id","not-a-guid"},new[]{"state","run"},new[]{"preview","https://example.com/image.png"},new[]{"name",new string('x',251)}}){var bad=Message(id,"saving");bad[pair[0]]=pair[1];bool refused=false;try{BrowserDesktopFeedback.Clean(bad);}catch(InvalidDataException){refused=true;}check(refused,"invalid notification field refused: "+pair[0]);}
   check(CollectionNotificationWindow.Preview("data:image/png;base64,invalid")==null,"bad preview cannot turn a successful save into failure");
   foreach(var area in new[]{new Rect(0,0,1920,1040),new Rect(-1280,0,1280,984),new Rect(0,0,800,560),new Rect(1920,-720,3840,2120),new Rect(-320,0,320,480)}){var place=CollectionNotificationWindow.Placement(area,612,270);check(Math.Abs(place.Right-(area.Right-12))<.1&&Math.Abs(place.Bottom-(area.Bottom-12))<.1&&area.Contains(place),"notification fits work-area bottom right, including scaled and negative screen origins");}
  }
  public static void Visual(string root,Action<bool,string> check){
   if(Environment.UserName!="NookTester"||string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MORI_DESK_DATA")))throw new InvalidOperationException("Dedicated isolated VM required");
   var window=new CollectionNotificationWindow();string id=Guid.NewGuid().ToString();
   var previews=new Dictionary<string,string>();
   try{
    window.Update(Message(id,"saving"));check(window.IsVisible&&!window.ShowActivated&&window.Topmost&&!window.ShowInTaskbar,"desktop notification is visible, topmost, nonactivating, absent from taskbar");
    check(window.NoticeCount==1,"one pending notification");
    check(Children<System.Windows.Controls.ProgressBar>(window).Any(p=>p.IsVisible&&p.IsIndeterminate),"saving has visible indeterminate progress without a fabricated percentage");previews["saving"]=Render(window);
    window.Update(Message(id,"success"));check(window.NoticeCount==1,"success updates pending card in place");
    check(HasText(window,"收藏成功")&&HasText(window,"✓")&&!Children<System.Windows.Controls.ProgressBar>(window).Any(p=>p.IsVisible),"confirmed success replaces the progress animation with a prominent explicit result");previews["success"]=Render(window);
    window.Update(Message(id,"saving"));check(window.NoticeCount==1,"late pending does not duplicate terminal card");
    window.UpdateLayout();var area=SystemParameters.WorkArea;check(Math.Abs(window.Left+window.ActualWidth-(area.Right-12))<2&&Math.Abs(window.Top+window.ActualHeight-(area.Bottom-12))<2,"actual window occupies desktop lower-right above taskbar");
    var image=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);image.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var file=File.Create(Path.Combine(root,"desktop-card.png")))encoder.Save(file);
    window.Update(Message(id,"dismiss"));window.Update(Message(id,"success"));check(window.NoticeCount==0,"dismissed notification cannot reappear on completion");
    for(int n=0;n<5;n++)window.Update(Message(Guid.NewGuid().ToString(),"success"));check(window.NoticeCount==3,"notification burst stays bounded to three cards");
   }finally{window.Close();}
   var readable=new CollectionNotificationWindow();try{
    string done=Guid.NewGuid().ToString();var success=Message(done,"success");success["theme"]="dark";readable.Update(success);previews["dark-success"]=Render(readable);
    readable.Advance(3);check(readable.NoticeCount==1,"success remains briefly readable");readable.Advance(5);check(readable.NoticeCount==0,"success automatically disappears after four seconds instead of lingering");
    string slow=Guid.NewGuid().ToString();readable.Update(Message(slow,"saving"));
    Children<System.Windows.Controls.Button>(readable).Single(button=>Convert.ToString(button.Content)=="×").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
    check(readable.NoticeCount==0,"closing pending progress hides it immediately");readable.Update(Message(slow,"success"));check(HasText(readable,"收藏成功"),"dismissing progress does not silently suppress the later save result");
   }finally{readable.Close();}
   var displays=new Dictionary<string,CollectionNotificationWindow>();string activeScreen="left";
   var router=new CollectionNotificationDisplay(()=>activeScreen,screen=>{var display=new CollectionNotificationWindow();displays.Add(screen,display);return display;});
   try{
    string first=Guid.NewGuid().ToString(),second=Guid.NewGuid().ToString();router.Update(Message(first,"saving"));activeScreen="right";router.Update(Message(second,"saving"));router.Update(Message(first,"success"));
    check(displays.Count==2&&HasText(displays["left"],"收藏成功")&&HasText(displays["right"],"正在收藏…"),"switching screens during a save keeps its result on the originating monitor");
    check(router.NoticeCount==2,"simultaneous collections on different monitors retain separate progress cards");router.Update(Message(first,"dismiss"));router.Update(Message(first,"success"));check(router.NoticeCount==1,"dismissed final results stay dismissed on the original monitor");
   }finally{router.Close();}
   var burst=new CollectionNotificationWindow();try{
    string slow=Guid.NewGuid().ToString();burst.Update(Message(slow,"saving"));
    for(int n=0;n<3;n++)burst.Update(Message(Guid.NewGuid().ToString(),"success"));
    var result=Message(slow,"success");result["name"]="burst-completed.png";burst.Update(result);burst.UpdateLayout();
    check(burst.NoticeCount==3&&HasText(burst,"burst-completed.png"),"evicted pending collection can still show its later completion within the card limit");
   }finally{burst.Close();}
   var delayed=new CollectionNotificationWindow();try{
    string slow=Guid.NewGuid().ToString();delayed.Update(Message(slow,"saving"));delayed.Advance(91);delayed.UpdateLayout();
    check(HasText(delayed,"收藏结果未确认"),"a stalled collection never invents success");
    delayed.Advance(107);check(delayed.NoticeCount==0,"automatic uncertainty notice expires");
    var result=Message(slow,"success");result["name"]="late-completed.png";delayed.Update(result);delayed.UpdateLayout();
    check(delayed.NoticeCount==1&&HasText(delayed,"late-completed.png"),"a real late completion remains visible after automatic uncertainty expiry");
    delayed.Update(Message(slow,"dismiss"));delayed.Update(result);check(delayed.NoticeCount==0,"manual dismiss still blocks late completion after recovery");
   }finally{delayed.Close();}
   var start=Stopwatch.StartNew();var foreground=GetForegroundWindow();var pending=Message(Guid.NewGuid().ToString(),"saving");
   check(Protocol(pending),"real native messaging process acknowledges actual desktop display");
   double cold=start.Elapsed.TotalMilliseconds;
   pending["state"]="success";check(Protocol(pending),"same desktop host accepts final result from a separate native messaging process");
   check(GetForegroundWindow()==foreground,"native progress and completion do not steal foreground focus");
   check(HostRunning(),"notification helper exists while result is visible");
   var exit=Stopwatch.StartNew();while(HostRunning()&&exit.Elapsed.TotalSeconds<30)Thread.Sleep(150);
   check(!HostRunning(),"notification helper automatically exits after last card expires");
   File.WriteAllText(Path.Combine(root,"notification-timing.txt"),"coldMs="+cold.ToString("F0")+"; autoExitMs="+exit.Elapsed.TotalMilliseconds.ToString("F0"));
   File.WriteAllText("collection-previews.json",new System.Web.Script.Serialization.JavaScriptSerializer{MaxJsonLength=4*1024*1024}.Serialize(previews));
  }
 }
}
