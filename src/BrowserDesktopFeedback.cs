using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Runtime.InteropServices;

namespace MoriDesk {
 // Ephemeral current-user notification process. It never loads a library,
 // registers a shortcut, downloads media, or activates the foreground window.
 internal static class BrowserDesktopFeedback {
  const int Maximum=100000;
  internal static string PipeName(){using(var hash=System.Security.Cryptography.SHA256.Create())return "Nook.Feedback."+BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(WindowsIdentity.GetCurrent().User.Value+"|"+typeof(Entry).Assembly.Location+"|"+Entry.DataRoot))).Replace("-","").Substring(0,24)+"."+Process.GetCurrentProcess().SessionId;}
  internal static Dictionary<string,object> Clean(Dictionary<string,object> request){
   string id=Text(request,"id",36),state=Text(request,"state",16);Guid parsed;
   if(!Guid.TryParseExact(id,"D",out parsed)||!new[]{"saving","success","error","unknown","choice","dismiss"}.Contains(state))throw new InvalidDataException("无效的收藏通知");
   string preview=Text(request,"preview",90000);
   if(preview!=""&&!preview.StartsWith("data:image/png;base64,",StringComparison.Ordinal))throw new InvalidDataException("通知预览必须为小型 PNG");
   return new Dictionary<string,object>{{"id",id},{"state",state},{"name",Text(request,"name",250)},{"folderName",Text(request,"folderName",250)},{"detail",Text(request,"detail",600)},{"preview",preview},{"theme",Text(request,"theme",5)=="dark"?"dark":"light"},{"duplicate",Flag(request,"duplicate")},{"keptCopy",Flag(request,"keptCopy")}};
  }
  internal static string Text(Dictionary<string,object> value,string key,int max){object raw;if(!value.TryGetValue(key,out raw)||!(raw is string))return "";string text=(string)raw;if(text.Length>max)throw new InvalidDataException("通知内容过长");return new string(text.Where(c=>!char.IsControl(c)||c=='\n').ToArray());}
  internal static bool Flag(Dictionary<string,object> value,string key){object raw;return value.TryGetValue(key,out raw)&&raw is bool&&(bool)raw;}
  internal static object Handle(Dictionary<string,object> request){BrowserConnection.ValidateId(Text(request,"_extensionId",32));return Send(Clean(request));}
  internal static object Send(Dictionary<string,object> value){
   string pipe=PipeName();
   using(var client=new NamedPipeClientStream(".",pipe,PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification)){
    try{client.Connect(80);}catch(TimeoutException){
     var start=new ProcessStartInfo(typeof(Entry).Assembly.Location,"--collection-notification-host"){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Path.GetDirectoryName(typeof(Entry).Assembly.Location)};
     start.EnvironmentVariables.Remove("NOOK_UPDATE_REQUEST");start.EnvironmentVariables.Remove("NOOK_UPDATE_EXPECTED_VERSION");
     Mutex running;if(Mutex.TryOpenExisting("Local\\"+pipe,out running))running.Dispose();else using(var child=Process.Start(start)){if(child==null)throw new IOException("桌面通知未启动");}
     client.Connect(2400);
    }
    Write(client,new JavaScriptSerializer().Serialize(value));
    if(Read(client)!="ok")throw new IOException("桌面通知未显示");
   }
   return new{ok=true};
  }
  static void Wait(Task task){if(!task.Wait(5000))throw new TimeoutException("桌面通知超时");task.GetAwaiter().GetResult();}
  static void Exact(Stream stream,byte[] bytes){int at=0;while(at<bytes.Length){var read=stream.ReadAsync(bytes,at,bytes.Length-at);Wait(read);int count=read.Result;if(count==0)throw new EndOfStreamException();at+=count;}}
  static string Read(Stream stream){var size=new byte[4];Exact(stream,size);int length=BitConverter.ToInt32(size,0);if(length<1||length>Maximum)throw new InvalidDataException();var bytes=new byte[length];Exact(stream,bytes);return new UTF8Encoding(false,true).GetString(bytes);}
  static void Write(Stream stream,string text){byte[] bytes=Encoding.UTF8.GetBytes(text);if(bytes.Length>Maximum)throw new InvalidDataException();var size=BitConverter.GetBytes(bytes.Length);Wait(stream.WriteAsync(size,0,size.Length));Wait(stream.WriteAsync(bytes,0,bytes.Length));Wait(stream.FlushAsync());}
  internal static int Run(){
   try{string name=PipeName();bool owned;using(var mutex=new Mutex(true,"Local\\"+name,out owned)){
    if(!owned)return 0;
    var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};var window=new CollectionNotificationDisplay();
    using(var receiver=new Receiver(name,window)){
     var idle=new DispatcherTimer{Interval=TimeSpan.FromSeconds(2)};DateTime emptySince=DateTime.UtcNow;
     idle.Tick+=(s,e)=>{if(window.NoticeCount>0)emptySince=DateTime.UtcNow;else if(DateTime.UtcNow-emptySince>TimeSpan.FromSeconds(10)){idle.Stop();app.Shutdown();}};idle.Start();app.Run();
    }window.Close();mutex.ReleaseMutex();return 0;
   }}catch{return 1;}
  }
  sealed class Receiver:IDisposable {
   readonly CollectionNotificationDisplay window;readonly NamedPipeServerStream server;volatile bool stopped;
   internal Receiver(string name,CollectionNotificationDisplay window){this.window=window;server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,4096,4096,ImagePinBridge.Security());new Thread(Listen){IsBackground=true,Name="Nook collection notification"}.Start();}
   void Listen(){while(!stopped){try{server.WaitForConnection();var value=Clean(new JavaScriptSerializer{MaxJsonLength=Maximum}.Deserialize<Dictionary<string,object>>(Read(server)));var action=window.Dispatcher.InvokeAsync(()=>window.Update(value));if(!action.Task.Wait(4500)){action.Abort();throw new TimeoutException();}action.Task.GetAwaiter().GetResult();Write(server,"ok");}catch{}finally{try{if(server.IsConnected)server.Disconnect();}catch{}}}}
   public void Dispose(){stopped=true;server.Dispose();}
  }
 }

 internal sealed class CollectionNotificationWindow:Window {
  readonly StackPanel stack=new StackPanel();readonly Dictionary<string,Card> cards=new Dictionary<string,Card>();readonly HashSet<string> closed=new HashSet<string>();readonly Queue<string> history=new Queue<string>();
  readonly DispatcherTimer timer;readonly Stopwatch clock=Stopwatch.StartNew();
  readonly Func<Rect> workArea;bool placing;
  internal int NoticeCount {get{return cards.Count;}}
  [DllImport("user32.dll",EntryPoint="GetWindowLongW")]static extern int GetStyle(IntPtr window,int index);
  [DllImport("user32.dll",EntryPoint="SetWindowLongW")]static extern int SetStyle(IntPtr window,int index,int value);
  [StructLayout(LayoutKind.Sequential)]struct NativeRect{public int Left,Top,Right,Bottom;}
  [DllImport("user32.dll")]static extern bool GetWindowRect(IntPtr window,out NativeRect bounds);
  [DllImport("user32.dll")]static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
  internal CollectionNotificationWindow(Func<Rect> workArea=null){
   this.workArea=workArea??(()=>{var area=System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;return new Rect(area.Left,area.Top,area.Width,area.Height);});
   Title="Nook · 收藏通知";Width=408;MaxHeight=Math.Max(80,SystemParameters.WorkArea.Height-24);SizeToContent=SizeToContent.Height;WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;AllowsTransparency=true;Background=Brushes.Transparent;ShowInTaskbar=false;ShowActivated=false;Topmost=true;Content=new ScrollViewer{Content=stack,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
   SourceInitialized+=(s,e)=>{var handle=new WindowInteropHelper(this).Handle;SetStyle(handle,-20,GetStyle(handle,-20)|0x08000000|0x00000080);};
   SizeChanged+=(s,e)=>Place();
   timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(250)};
   timer.Tick+=(s,e)=>Advance(clock.Elapsed.TotalSeconds);timer.Start();Closed+=(s,e)=>timer.Stop();
  }
  internal void Advance(double now){
    foreach(var card in cards.Values.ToArray()){
     double delta=now-card.Last;card.Last=now;
     if(card.State=="saving"){if(now-card.Created>90){card.State="unknown";card.Title.Text="收藏结果未确认";card.Detail.Text="请先检查图库，避免重复收藏。";card.Remaining=15;card.Mark.Text="!";Appearance(card,card.Dark);}else if(now-card.Created>8){card.Detail.Text="仍在保存，请稍候；完成后会在这里通知你。";}}
     else if(!card.View.IsMouseOver&&!card.View.IsKeyboardFocusWithin){card.Remaining-=delta;if(card.Remaining<=0)Remove(card.Id,card.State!="choice"&&card.State!="unknown");}
    }
  }
  internal static Rect Placement(Rect area,double width,double height){width=Math.Min(width,Math.Max(1,area.Width-24));height=Math.Min(height,Math.Max(1,area.Height-24));return new Rect(Math.Max(area.Left+12,area.Right-width-12),Math.Max(area.Top+12,area.Bottom-height-12),width,height);}
  void Place(){
   if(placing)return;var handle=new WindowInteropHelper(this).Handle;if(handle==IntPtr.Zero)return;
   placing=true;try{
    var area=workArea();var source=HwndSource.FromHwnd(handle);var scale=source!=null&&source.CompositionTarget!=null?source.CompositionTarget.TransformToDevice:Matrix.Identity;
    Width=Math.Min(408,Math.Max(1,(area.Width-24)/scale.M11));MaxHeight=Math.Max(1,(area.Height-24)/scale.M22);
    NativeRect pixels;if(!GetWindowRect(handle,out pixels))return;
    var bounds=Placement(area,pixels.Right-pixels.Left,pixels.Bottom-pixels.Top);
    // Use physical screen coordinates; WPF Left/Top mix logical origins on
    // monitors with different scale factors. Never activate or resize here.
    SetWindowPos(handle,new IntPtr(-1),(int)Math.Round(bounds.Left),(int)Math.Round(bounds.Top),0,0,0x0011);
   }finally{placing=false;}
  }
  void Remember(string id){if(closed.Add(id)){history.Enqueue(id);while(history.Count>128)closed.Remove(history.Dequeue());}}
  void Remove(string id,bool remember=true){Card card;if(cards.TryGetValue(id,out card)){stack.Children.Remove(card.View);cards.Remove(id);if(remember)Remember(id);}if(cards.Count==0)Hide();else Place();}
  internal void Update(Dictionary<string,object> raw){
   var value=BrowserDesktopFeedback.Clean(raw);string id=(string)value["id"],state=(string)value["state"];
   if(state=="dismiss"){Remove(id);Remember(id);return;}if(closed.Contains(id))return;
   Card card;if(cards.TryGetValue(id,out card)){if(card.State!="saving"&&card.State!="choice"&&state=="saving")return;if(card.State=="choice"&&state=="saving")card.Created=clock.Elapsed.TotalSeconds;}
   else{if(cards.Count>=3){var oldest=cards.Values.OrderBy(c=>c.Created).First();Remove(oldest.Id,oldest.State=="success"||oldest.State=="error");}card=new Card{Id=id,Created=clock.Elapsed.TotalSeconds};cards.Add(id,card);Build(card);stack.Children.Add(card.View);}
   card.State=state;card.Last=clock.Elapsed.TotalSeconds;card.Remaining=state=="success"?4:15;
   bool dark=(string)value["theme"]=="dark";Brush background=new SolidColorBrush(dark?Color.FromRgb(36,36,36):Colors.White),foreground=new SolidColorBrush(dark?Color.FromRgb(238,238,238):Color.FromRgb(37,37,37));
   card.View.Background=background;card.Name.Foreground=foreground;card.Detail.Foreground=foreground;card.Close.Foreground=foreground;card.View.BorderBrush=new SolidColorBrush(dark?Color.FromRgb(72,72,72):Color.FromRgb(215,215,215));card.Open.Foreground=new SolidColorBrush(dark?Color.FromRgb(183,168,237):Color.FromRgb(109,91,199));
   bool success=state=="success",duplicate=(bool)value["duplicate"],kept=(bool)value["keptCopy"];
   card.Dark=dark;Appearance(card,dark);
   card.Mark.Text=success?"✓":state=="saving"?"↓":"!";
   card.Title.Text=state=="saving"?"正在收藏…":success?(kept?"收藏成功 · 已另存一份":duplicate?"已使用已有图片":"收藏成功"):state=="choice"?"发现重复图片":state=="unknown"?"收藏结果未确认":"收藏失败";
   card.Name.Text=(string)value["name"];
   string folder=(string)value["folderName"];
   card.Detail.Text=state=="choice"?"请返回原网页选择处理方式。":success?(duplicate&&!kept?"已使用图库原图，未重复保存":folder!=""?"已保存到「"+folder+"」":"已保存到本机图库 · 未分类"):state=="saving"?(folder!=""?"正在保存到「"+folder+"」":"正在保存到本机图库"):(string)value["detail"];
   card.Open.Visibility=success?Visibility.Visible:Visibility.Collapsed;
   string preview=(string)value["preview"];if(card.PreviewSource!=preview){card.PreviewSource=preview;card.Image.Source=Preview(preview);}card.Visual.Visibility=card.Image.Source==null?Visibility.Collapsed:Visibility.Visible;
   if(!IsVisible)Show();UpdateLayout();Place();
  }
  internal static BitmapSource Preview(string source){
   if(string.IsNullOrEmpty(source))return null;
   try{byte[] bytes=Convert.FromBase64String(source.Substring("data:image/png;base64,".Length));if(bytes.Length>67500)return null;
    using(var input=new MemoryStream(bytes,false)){var decoder=BitmapDecoder.Create(input,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);var frame=decoder.Frames[0];if(frame.PixelWidth>256||frame.PixelHeight>256||decoder.Frames.Count!=1||decoder.CodecInfo.ContainerFormat!=new Guid("1b7cfaf4-713f-473c-bbcd-6137425faeaf"))return null;var bitmap=new WriteableBitmap(frame);bitmap.Freeze();return bitmap;}
   }catch{return null;}
  }
  static TextBlock Text(double size,FontWeight weight){return new TextBlock{FontSize=size,FontWeight=weight,Foreground=new SolidColorBrush(Color.FromRgb(37,37,37)),TextTrimming=TextTrimming.CharacterEllipsis};}
  static void Appearance(Card card,bool dark){
   bool success=card.State=="success",saving=card.State=="saving",error=card.State=="error";
   var accent=new SolidColorBrush(success?(dark?Color.FromRgb(117,217,164):Color.FromRgb(26,116,64)):saving?(dark?Color.FromRgb(183,168,237):Color.FromRgb(109,91,199)):error?(dark?Color.FromRgb(255,161,153):Color.FromRgb(175,55,43)):(dark?Color.FromRgb(241,190,112):Color.FromRgb(150,89,17)));
   card.Title.Foreground=accent;card.Mark.Foreground=accent;card.Stripe.Background=accent;card.Progress.Foreground=accent;
   card.Badge.Background=new SolidColorBrush(success?(dark?Color.FromRgb(34,64,47):Color.FromRgb(228,245,234)):saving?(dark?Color.FromRgb(52,45,70):Color.FromRgb(239,235,250)):error?(dark?Color.FromRgb(72,40,38):Color.FromRgb(253,234,231)):(dark?Color.FromRgb(67,54,34):Color.FromRgb(252,242,222)));
   card.Progress.IsIndeterminate=saving;card.Progress.Visibility=saving?Visibility.Visible:Visibility.Collapsed;
  }
  void Build(Card card){
   var grid=new Grid();grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(48)});grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});grid.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(24)});
   var surface=new Grid();grid.Margin=new Thickness(18,16,12,16);surface.Children.Add(grid);
   card.Stripe=new Border{Width=4,HorizontalAlignment=HorizontalAlignment.Left,CornerRadius=new CornerRadius(2),Margin=new Thickness(0,16,0,16)};surface.Children.Add(card.Stripe);
   card.View=new Border{Background=Brushes.White,BorderBrush=new SolidColorBrush(Color.FromRgb(215,215,215)),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(14),Margin=new Thickness(0,4,0,4),Child=surface};
   card.Mark=Text(27,FontWeights.SemiBold);card.Mark.HorizontalAlignment=HorizontalAlignment.Center;card.Mark.VerticalAlignment=VerticalAlignment.Center;
   card.Badge=new Border{Width=46,Height=46,CornerRadius=new CornerRadius(23),VerticalAlignment=VerticalAlignment.Top,Child=card.Mark};grid.Children.Add(card.Badge);
   card.Image=new Image{Stretch=Stretch.Uniform};card.Visual=new Border{Width=56,Height=56,CornerRadius=new CornerRadius(8),Margin=new Thickness(10,0,6,0),VerticalAlignment=VerticalAlignment.Top,Child=card.Image};Grid.SetColumn(card.Visual,2);grid.Children.Add(card.Visual);
   var body=new StackPanel{Margin=new Thickness(12,0,4,0)};Grid.SetColumn(body,1);grid.Children.Add(body);
   card.Title=Text(17,FontWeights.SemiBold);body.Children.Add(card.Title);card.Name=Text(13,FontWeights.Normal);card.Name.Margin=new Thickness(0,6,0,0);body.Children.Add(card.Name);
   card.Detail=Text(12,FontWeights.Normal);card.Detail.Opacity=.75;card.Detail.TextWrapping=TextWrapping.Wrap;card.Detail.MaxHeight=66;card.Detail.Margin=new Thickness(0,2,0,0);body.Children.Add(card.Detail);
   card.Progress=new ProgressBar{IsIndeterminate=true,Height=4,BorderThickness=new Thickness(0),Background=new SolidColorBrush(Color.FromArgb(28,128,128,128)),Margin=new Thickness(0,10,0,0)};System.Windows.Automation.AutomationProperties.SetName(card.Progress,"正在保存收藏");body.Children.Add(card.Progress);
   card.Open=new Button{Content="查看图库",HorizontalAlignment=HorizontalAlignment.Left,Background=Brushes.Transparent,BorderThickness=new Thickness(0),Foreground=new SolidColorBrush(Color.FromRgb(109,91,199)),Margin=new Thickness(0,10,0,0),Padding=new Thickness(0),Cursor=System.Windows.Input.Cursors.Hand};
   card.Open.Click+=(s,e)=>{try{if(!BrowserCollectionFeedback.CanOpen)throw new InvalidOperationException();string executable=InstalledApplicationRouting.RegistrationExecutable();AppUpdateCoordinator.PlainPath(executable);using(var process=Process.Start(BrowserCollectionFeedback.StartInfo(executable))){if(process==null)throw new IOException();}}catch{card.Detail.Text="收藏已完成，请从任务栏打开 Nook。";card.Remaining=15;}};body.Children.Add(card.Open);
   var close=new Button{Content="×",FontSize=18,Height=24,Width=24,Padding=new Thickness(0),Background=Brushes.Transparent,BorderThickness=new Thickness(0),VerticalAlignment=VerticalAlignment.Top,Cursor=System.Windows.Input.Cursors.Hand,ToolTip="关闭提示"};card.Close=close;System.Windows.Automation.AutomationProperties.SetName(close,"关闭收藏提示");close.Click+=(s,e)=>Remove(card.Id,card.State!="saving");Grid.SetColumn(close,3);grid.Children.Add(close);
  }
  sealed class Card {internal string Id,State,PreviewSource;internal bool Dark;internal double Created,Last,Remaining;internal Border View,Badge,Stripe,Visual;internal TextBlock Title,Name,Detail,Mark;internal Image Image;internal Button Open,Close;internal ProgressBar Progress;}
 }
}
