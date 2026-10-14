using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Mori {
 // One volume model drives either decoder. Muting preserves the user's level.
 internal sealed class VideoVolume {
  internal double Level {get;private set;}internal bool Muted {get;private set;}
  internal double Effective {get{return Muted?0:Level;}}
  internal VideoVolume():this(.7,false){}
  internal VideoVolume(double level,bool muted){Level=Clamp(level);Muted=muted;}
  internal static double Clamp(double value){return double.IsNaN(value)||double.IsInfinity(value)?0:Math.Max(0,Math.Min(1,value));}
  internal void Set(double value){Level=Clamp(value);if(Level>0)Muted=false;}
  internal void Toggle(){if(Level==0){Level=.7;Muted=false;}else Muted=!Muted;}
 }
 // Resize the existing playback window; never recreate the decoder for full screen.
 internal sealed class VideoFullscreen {
  readonly Window window;WindowStyle style;ResizeMode resize;WindowState state;Rect restore;
  internal bool Active {get;private set;}
  [DllImport("user32.dll")]static extern bool SetWindowPos(IntPtr handle,IntPtr after,int x,int y,int width,int height,uint flags);
  internal VideoFullscreen(Window window){this.window=window;}
  internal void Enter(){
   var handle=new WindowInteropHelper(window).Handle;var screen=handle==IntPtr.Zero?System.Windows.Forms.Screen.PrimaryScreen:System.Windows.Forms.Screen.FromHandle(handle);var bounds=screen.Bounds;var source=PresentationSource.FromVisual(window);var transform=source!=null&&source.CompositionTarget!=null?source.CompositionTarget.TransformFromDevice:Matrix.Identity;var origin=transform.Transform(new Point(bounds.Left,bounds.Top));var size=transform.Transform(new Vector(bounds.Width,bounds.Height));
   Enter(new Rect(origin.X,origin.Y,size.X,size.Y));
   // Screen bounds are physical pixels. This also handles a current monitor with
   // a different DPI from the primary monitor without leaving a taskbar gap.
   if(Active&&handle!=IntPtr.Zero)SetWindowPos(handle,IntPtr.Zero,bounds.Left,bounds.Top,bounds.Width,bounds.Height,0x0034);
  }
  internal void Enter(Rect bounds){
   if(Active||bounds.IsEmpty||bounds.Width<=0||bounds.Height<=0||!Finite(bounds.X)||!Finite(bounds.Y)||!Finite(bounds.Width)||!Finite(bounds.Height))return;
   style=window.WindowStyle;resize=window.ResizeMode;state=window.WindowState;restore=state==WindowState.Normal||window.RestoreBounds.IsEmpty?new Rect(Finite(window.Left)?window.Left:0,Finite(window.Top)?window.Top:0,Finite(window.Width)?window.Width:Math.Max(window.MinWidth,window.ActualWidth),Finite(window.Height)?window.Height:Math.Max(window.MinHeight,window.ActualHeight)):window.RestoreBounds;
   Active=true;window.WindowState=WindowState.Normal;window.WindowStyle=WindowStyle.None;window.ResizeMode=ResizeMode.NoResize;window.Left=bounds.X;window.Top=bounds.Y;window.Width=bounds.Width;window.Height=bounds.Height;
  }
  static bool Finite(double value){return !double.IsNaN(value)&&!double.IsInfinity(value);}
  internal void Exit(){if(!Active)return;Active=false;window.WindowState=WindowState.Normal;window.WindowStyle=style;window.ResizeMode=resize;window.Left=restore.X;window.Top=restore.Y;window.Width=restore.Width;window.Height=restore.Height;window.WindowState=state;}
 }
 // Shared state for mouse seeking, keyboard seeking and end-of-video looping.
 internal sealed class VideoPlayback {
  internal double Duration {get;private set;}
  internal double Position {get;private set;}
  internal bool Playing=true,Seeking,Loop=true;
  internal bool Ready {get{return Duration>0;}}
  internal static double Clamp(double value,double duration){return double.IsNaN(value)||double.IsInfinity(value)||duration<=0?0:Math.Max(0,Math.Min(duration,value));}
  internal void Open(double seconds){Duration=double.IsNaN(seconds)||double.IsInfinity(seconds)?0:Math.Max(0,seconds);Position=0;}
  internal void Seek(double seconds){Position=Clamp(seconds,Duration);}
  internal void Tick(double seconds){if(!Seeking)Seek(seconds);}
  internal void End(){Position=Loop?0:Duration;if(!Loop)Playing=false;}
  internal static string Time(double seconds){var value=TimeSpan.FromSeconds(Math.Max(0,double.IsNaN(seconds)||double.IsInfinity(seconds)?0:seconds));return value.TotalHours>=1?((int)value.TotalHours)+value.ToString(@"\:mm\:ss"):((int)value.TotalMinutes).ToString("00")+value.ToString(@"\:ss");}
 }
 internal sealed class VideoPlayer : IDisposable {
  internal readonly DockPanel Root=new DockPanel();
  internal readonly MediaElement Media=new MediaElement{LoadedBehavior=MediaState.Manual,UnloadedBehavior=MediaState.Close,Stretch=Stretch.Uniform,ScrubbingEnabled=true};
  internal readonly Slider Progress=new Slider{Minimum=0,Maximum=1,IsEnabled=false,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(10,0,10,0),SmallChange=5,LargeChange=15};
  internal readonly VideoPlayback State=new VideoPlayback();
  internal readonly TextBlock Elapsed=new TextBlock{Text="00:00 / --:--",VerticalAlignment=VerticalAlignment.Center,MinWidth=95,FontSize=11};
  internal readonly Button Play=IconButton("\uE769","播放 / 暂停 · 空格");
  internal readonly Button Mute=IconButton("\uE767","静音 / 恢复声音 · M");
  internal readonly Button Fullscreen=IconButton("\uE740","全屏 · F11");
  internal readonly Slider Volume=new Slider{Minimum=0,Maximum=1,Value=.7,Width=60,IsMoveToPointEnabled=true,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(2,0,8,0),SmallChange=.05,LargeChange=.1,ToolTip="音量"};
  internal readonly VideoVolume Sound;
  internal VideoFullscreen Screen {get;private set;}
  readonly DispatcherTimer timer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(200)};
  readonly TextBlock error=new TextBlock{TextWrapping=TextWrapping.Wrap,TextAlignment=TextAlignment.Center,Margin=new Thickness(20)};
  readonly StackPanel fallback=new StackPanel{VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center,Visibility=Visibility.Collapsed};
  readonly Grid surface=new Grid{Background=new SolidColorBrush(Color.FromRgb(23,25,28))};readonly string file;readonly bool audio;
  readonly DispatcherTimer opening=new DispatcherTimer{Interval=TimeSpan.FromSeconds(18)};
  BrowserVideo browser;bool syncing,syncingVolume,closed,visible=true,failed,started;
  internal bool TimerRunning {get{return timer.IsEnabled;}}
  internal bool Failed {get{return failed;}}
  internal bool UsingBrowser {get{return browser!=null;}}
  internal BrowserVideo Browser {get{return browser;}}
  internal event Action CloseRequested;
  static Button IconButton(string glyph,string tip){return new Button{Content=glyph,FontFamily=new FontFamily("Segoe MDL2 Assets"),FontSize=15,Width=32,Height=32,Padding=new Thickness(0),Margin=new Thickness(0),ToolTip=tip};}
  internal VideoPlayer(string file):this(file,Settings.Load()){}
  // Defaults are copied once. Changing settings does not interrupt an open clip.
  internal VideoPlayer(string file,Settings preferences){
   if(preferences==null)throw new ArgumentNullException("preferences");
   this.file=file;audio=MediaTypes.Audio.Contains(System.IO.Path.GetExtension(file));Sound=new VideoVolume(preferences.MediaVolume/100.0,preferences.MediaMuted);State.Playing=preferences.MediaAutoPlay;State.Loop=preferences.MediaLoop;
   var bar=new Grid{Margin=new Thickness(10,6,10,6)};for(int i=0;i<6;i++)bar.ColumnDefinitions.Add(new ColumnDefinition{Width=i==1?new GridLength(1,GridUnitType.Star):GridLength.Auto});DockPanel.SetDock(bar,Dock.Bottom);var controls=new FrameworkElement[]{Play,Progress,Elapsed,Mute,Volume,Fullscreen};for(int i=0;i<controls.Length;i++){Grid.SetColumn(controls[i],i);bar.Children.Add(controls[i]);}Root.Children.Add(bar);Progress.SetResourceReference(Control.StyleProperty,"ThumbnailSlider");Volume.SetResourceReference(Control.StyleProperty,"ThumbnailSlider");Fullscreen.Visibility=audio?Visibility.Collapsed:Visibility.Visible;Fullscreen.IsEnabled=false;
   surface.Children.Add(Media);fallback.Children.Add(error);var open=new Button{Content="用默认播放器打开",Padding=new Thickness(16,8,16,8),HorizontalAlignment=HorizontalAlignment.Center};open.Click+=delegate{try{Process.Start(new ProcessStartInfo(file){UseShellExecute=true});}catch(Exception ex){error.Text=ex.Message;}};fallback.Children.Add(open);surface.Children.Add(fallback);Root.Children.Add(surface);
   AutomationProperties.SetName(Progress,"视频进度");Progress.ToolTip="拖动或点击跳转 · 左右方向键前后 5 秒";AutomationProperties.SetName(Elapsed,"播放时间");Play.Content=State.Playing?"\uE769":"\uE768";AutomationProperties.SetName(Play,State.Playing?"暂停":"播放");AutomationProperties.SetName(Mute,"静音");AutomationProperties.SetName(Volume,"音量");AutomationProperties.SetName(Fullscreen,"全屏");ApplyVolume();
   if(audio){AutomationProperties.SetName(Progress,"音频进度");var info=new StackPanel{VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center,IsHitTestVisible=false};info.Children.Add(new TextBlock{Text="\uE8D6",FontFamily=new FontFamily("Segoe MDL2 Assets"),FontSize=68,Foreground=Brushes.Gainsboro,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,0,0,20)});info.Children.Add(new TextBlock{Text=System.IO.Path.GetFileName(file),Foreground=Brushes.Gainsboro,FontSize=16,MaxWidth=600,TextTrimming=TextTrimming.CharacterEllipsis});System.Windows.Controls.Panel.SetZIndex(info,1);surface.Children.Add(info);}
   Media.MediaOpened+=delegate{if(closed||failed||browser!=null)return;Opened(Media.NaturalDuration.HasTimeSpan?Media.NaturalDuration.TimeSpan.TotalSeconds:0);};
   Media.MediaEnded+=delegate{if(browser==null)Ended();};
   Media.MediaFailed+=delegate{if(!closed&&!failed&&browser==null)StartBrowser();};
   opening.Tick+=delegate{opening.Stop();if(!State.Ready)Fail();};
   Play.Click+=delegate{Toggle();};Mute.Click+=delegate{ToggleMute();};Fullscreen.Click+=delegate{ToggleFullscreen();};Volume.ValueChanged+=delegate{if(!syncingVolume)SetVolume(Volume.Value);};timer.Tick+=delegate{if(!closed&&!failed&&!State.Seeking){State.Tick(Media.Position.TotalSeconds);Sync();}};
   Media.MouseLeftButtonDown+=delegate(object sender,MouseButtonEventArgs e){if(e.ClickCount==2){if(CloseRequested!=null)CloseRequested();e.Handled=true;}};
   Progress.ValueChanged+=delegate{if(!syncing&&State.Ready&&!State.Seeking){Seek(Progress.Value);}};
   Progress.PreviewMouseLeftButtonDown+=delegate(object s,MouseButtonEventArgs e){if(!State.Ready||failed)return;Progress.Focus();BeginSeek();if(Mouse.Capture(Progress)){Pointer(e.GetPosition(Progress));}else EndSeek();e.Handled=true;};
   Progress.PreviewMouseMove+=delegate(object s,MouseEventArgs e){if(State.Seeking&&Mouse.Captured==Progress){Pointer(e.GetPosition(Progress));e.Handled=true;}};
   Progress.PreviewMouseLeftButtonUp+=delegate(object s,MouseButtonEventArgs e){if(State.Seeking){Pointer(e.GetPosition(Progress));EndSeek();e.Handled=true;}};
   Progress.LostMouseCapture+=delegate{if(State.Seeking)EndSeek();};
  }
  internal void Start(){if(closed||failed||started)return;started=true;string ext=System.IO.Path.GetExtension(file).ToLowerInvariant();if(ext==".webm"||ext==".ogg"||ext==".opus"||ext==".flac"){StartBrowser();return;}opening.Start();Media.Source=new Uri(System.IO.Path.GetFullPath(file));ApplyPlayback();}
  void Opened(double seconds){if(closed||failed)return;opening.Stop();State.Open(seconds);Progress.Maximum=Math.Max(1,State.Duration);Progress.IsEnabled=State.Ready;Play.IsEnabled=true;Sync();ApplyPlayback();}
  async void StartBrowser(){
   if(closed||failed||browser!=null)return;opening.Stop();timer.Stop();Media.Stop();Media.Close();Media.Source=null;Media.Visibility=Visibility.Collapsed;Elapsed.Text="正在加载…";Play.IsEnabled=false;
   browser=new BrowserVideo(file);surface.Children.Insert(0,browser.View);
   browser.Opened+=delegate(double seconds,int width,int height){if(!closed&&!failed)Opened(seconds);};
   browser.PositionChanged+=delegate(double position){if(!closed&&!failed&&!State.Seeking){State.Tick(position);Sync();}};
   browser.Ended+=Ended;browser.Failed+=Fail;
   browser.CloseRequested+=delegate{if(!closed&&!failed&&CloseRequested!=null)CloseRequested();};browser.Key+=BrowserKey;
   opening.Start();await browser.Start();
  }
  void Fail(){if(closed||failed)return;failed=true;opening.Stop();timer.Stop();State.Seeking=false;Release();Progress.IsEnabled=false;Play.IsEnabled=false;if(browser!=null){surface.Children.Remove(browser.View);browser.Dispose();}Media.Stop();Media.Close();Media.Source=null;error.Text=BrowserVideo.Available?"无法解码此"+(audio?"音频":"视频")+"，文件可能损坏或编码暂不支持。可用默认播放器打开。":"此文件需要 Microsoft Edge WebView2 Runtime，当前电脑未安装。可先用默认播放器打开。";Elapsed.Text="无法播放";System.Windows.Controls.Panel.SetZIndex(fallback,2);fallback.Visibility=Visibility.Visible;}
  internal void AttachWindow(Window window){if(Screen!=null)throw new InvalidOperationException("Playback window already attached");Screen=new VideoFullscreen(window);Fullscreen.IsEnabled=!audio;}
  internal void ToggleFullscreen(){if(closed||audio||Screen==null)return;if(Screen.Active)Screen.Exit();else Screen.Enter();SyncFullscreen();}
  void SyncFullscreen(){bool active=Screen!=null&&Screen.Active;Fullscreen.Content=active?"\uE73F":"\uE740";Fullscreen.ToolTip=active?"退出全屏 · Esc / F11":"全屏 · F11";AutomationProperties.SetName(Fullscreen,active?"退出全屏":"全屏");}
  internal void SetVolume(double value){if(closed)return;Sound.Set(value);ApplyVolume();}
  internal void ToggleMute(){if(closed)return;Sound.Toggle();ApplyVolume();}
  void ApplyVolume(){Media.Volume=Sound.Effective;if(browser!=null)browser.SetVolume(Sound.Effective);syncingVolume=true;Volume.Value=Sound.Level;syncingVolume=false;bool muted=Sound.Effective==0;Mute.Content=muted?"\uE74F":"\uE767";Mute.ToolTip=muted?"恢复声音 · M":"静音 · M";Volume.ToolTip="音量 "+Math.Round(Sound.Level*100)+"%"+(Sound.Muted?"（已静音）":"");AutomationProperties.SetName(Mute,muted?"恢复声音":"静音");}
  internal void BrowserKey(string key){HandleKey(key);}
  bool HandleKey(string key){if(closed)return false;if(key=="Escape"){if(Screen!=null&&Screen.Active){Screen.Exit();SyncFullscreen();}else if(CloseRequested!=null)CloseRequested();return true;}if(key=="F11"&&!audio){ToggleFullscreen();return true;}if(key=="m"||key=="M"){ToggleMute();return true;}if(key==" "){Toggle();return true;}if(State.Ready&&(key=="ArrowLeft"||key=="ArrowRight"||key=="Home"||key=="End")){Seek(key=="Home"?0:key=="End"?Math.Max(0,State.Duration-.05):State.Position+(key=="ArrowLeft"?-5:5));return true;}return false;}
  internal void Toggle(){if(closed||failed)return;if(!State.Playing&&State.Ready&&State.Position>=State.Duration){State.Seek(0);Commit();Sync();}State.Playing=!State.Playing;ApplyPlayback();}
  void Ended(){if(closed||failed||State.Seeking)return;State.End();if(State.Loop)Commit();Sync();ApplyPlayback();}
  void ApplyPlayback(){if(closed||failed)return;Play.Content=State.Playing?"\uE769":"\uE768";AutomationProperties.SetName(Play,State.Playing?"暂停":"播放");ApplyVolume();if(!started)return;bool playing=State.Playing&&visible&&!State.Seeking;if(browser!=null){browser.Command(playing?"play":"pause");return;}if(playing){Media.Play();if(State.Ready)timer.Start();}else{Media.Pause();timer.Stop();}}
  internal void SetVisible(bool value){visible=value;if(!value&&State.Seeking)EndSeek();ApplyPlayback();}
  internal void BeginSeek(){if(!State.Ready||closed||failed)return;State.Seeking=true;timer.Stop();if(browser!=null)browser.Command("pause");else Media.Pause();}
  void Pointer(Point point){var track=Progress.Template.FindName("PART_Track",Progress) as Track;if(track==null)return;State.Seek(track.ValueFromPoint(Progress.TranslatePoint(point,track)));Sync();}
  internal void EndSeek(){if(!State.Seeking)return;State.Seeking=false;Release();Commit();ApplyPlayback();}
  void Release(){if(Mouse.Captured==Progress)Mouse.Capture(null);}
  internal void Seek(double value){if(!State.Ready||closed||failed)return;State.Seek(value);Commit();Sync();}
  void Commit(){if(started&&!closed&&!failed){if(browser!=null)browser.Command("seek",State.Position);else Media.Position=TimeSpan.FromSeconds(State.Position);}}
  void Sync(){syncing=true;Progress.Value=State.Position;syncing=false;Elapsed.Text=VideoPlayback.Time(State.Position)+" / "+(State.Ready?VideoPlayback.Time(State.Duration):"--:--");}
  internal bool ControlOwnsKey(Key key,IInputElement focus){if(key==Key.Space&&(ReferenceEquals(focus,Play)||ReferenceEquals(focus,Mute)||ReferenceEquals(focus,Fullscreen)))return true;return ReferenceEquals(focus,Volume)&&(key==Key.Left||key==Key.Right||key==Key.Home||key==Key.End||key==Key.Up||key==Key.Down);}
  internal void KeyDown(object sender,KeyEventArgs e){if(e.Handled||ControlOwnsKey(e.Key,Keyboard.FocusedElement))return;if(e.IsRepeat&&(e.Key==Key.Escape||e.Key==Key.F11||e.Key==Key.Space||e.Key==Key.M)){e.Handled=true;return;}string key=e.Key==Key.Escape?"Escape":e.Key==Key.F11?"F11":e.Key==Key.Space?" ":e.Key==Key.M?"m":e.Key==Key.Left?"ArrowLeft":e.Key==Key.Right?"ArrowRight":e.Key==Key.Home?"Home":e.Key==Key.End?"End":null;if(HandleKey(key))e.Handled=true;}
  public void Dispose(){if(closed)return;closed=true;State.Seeking=false;Release();timer.Stop();opening.Stop();if(browser!=null){surface.Children.Remove(browser.View);browser.Dispose();}Media.Stop();Media.Close();Media.Source=null;}
 }
}
