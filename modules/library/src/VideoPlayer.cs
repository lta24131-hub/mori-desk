using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Mori {
 // Shared state for mouse seeking, keyboard seeking and end-of-video looping.
 internal sealed class VideoPlayback {
  internal double Duration {get;private set;}
  internal double Position {get;private set;}
  internal bool Playing=true,Seeking;
  internal bool Ready {get{return Duration>0;}}
  internal static double Clamp(double value,double duration){return double.IsNaN(value)||double.IsInfinity(value)||duration<=0?0:Math.Max(0,Math.Min(duration,value));}
  internal void Open(double seconds){Duration=double.IsNaN(seconds)||double.IsInfinity(seconds)?0:Math.Max(0,seconds);Position=0;}
  internal void Seek(double seconds){Position=Clamp(seconds,Duration);}
  internal void Tick(double seconds){if(!Seeking)Seek(seconds);}
  internal void End(){Position=0;}
  internal static string Time(double seconds){var value=TimeSpan.FromSeconds(Math.Max(0,double.IsNaN(seconds)||double.IsInfinity(seconds)?0:seconds));return value.TotalHours>=1?((int)value.TotalHours)+value.ToString(@"\:mm\:ss"):((int)value.TotalMinutes).ToString("00")+value.ToString(@"\:ss");}
 }
 internal sealed class VideoPlayer : IDisposable {
  internal readonly DockPanel Root=new DockPanel();
  internal readonly MediaElement Media=new MediaElement{LoadedBehavior=MediaState.Manual,UnloadedBehavior=MediaState.Close,Stretch=Stretch.Uniform,ScrubbingEnabled=true};
  internal readonly Slider Progress=new Slider{Minimum=0,Maximum=1,IsEnabled=false,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,12,0),SmallChange=5,LargeChange=15};
  internal readonly VideoPlayback State=new VideoPlayback();
  internal readonly TextBlock Elapsed=new TextBlock{Text="00:00 / --:--",VerticalAlignment=VerticalAlignment.Center,MinWidth=103};
  internal readonly Button Play=new Button{Content="暂停",MinWidth=70,Padding=new Thickness(12,8,12,8),ToolTip="播放 / 暂停 · 空格"};
  readonly DispatcherTimer timer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(200)};
  readonly TextBlock error=new TextBlock{TextWrapping=TextWrapping.Wrap,TextAlignment=TextAlignment.Center,Margin=new Thickness(20)};
  readonly StackPanel fallback=new StackPanel{VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center,Visibility=Visibility.Collapsed};
  readonly Grid surface=new Grid{Background=new SolidColorBrush(Color.FromRgb(23,25,28))};readonly string file;
  readonly DispatcherTimer opening=new DispatcherTimer{Interval=TimeSpan.FromSeconds(18)};
  BrowserVideo browser;bool syncing,closed,visible=true,failed,started;
  internal bool TimerRunning {get{return timer.IsEnabled;}}
  internal bool Failed {get{return failed;}}
  internal bool UsingBrowser {get{return browser!=null;}}
  internal BrowserVideo Browser {get{return browser;}}
  internal event Action CloseRequested;
  internal VideoPlayer(string file){
   this.file=file;
   var bar=new Grid{Margin=new Thickness(12,8,12,8)};bar.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});bar.ColumnDefinitions.Add(new ColumnDefinition());bar.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});DockPanel.SetDock(bar,Dock.Bottom);bar.Children.Add(Play);Grid.SetColumn(Progress,1);bar.Children.Add(Progress);Grid.SetColumn(Elapsed,2);bar.Children.Add(Elapsed);Root.Children.Add(bar);
   surface.Children.Add(Media);fallback.Children.Add(error);var open=new Button{Content="用默认播放器打开",Padding=new Thickness(16,8,16,8),HorizontalAlignment=HorizontalAlignment.Center};open.Click+=delegate{try{Process.Start(new ProcessStartInfo(file){UseShellExecute=true});}catch(Exception ex){error.Text=ex.Message;}};fallback.Children.Add(open);surface.Children.Add(fallback);Root.Children.Add(surface);
   AutomationProperties.SetName(Progress,"视频进度");Progress.ToolTip="拖动或点击跳转 · 左右方向键前后 5 秒";AutomationProperties.SetName(Elapsed,"播放时间");UiIcons.SetGlyph(Play,"\uE769");
   Media.MediaOpened+=delegate{if(closed||browser!=null)return;Opened(Media.NaturalDuration.HasTimeSpan?Media.NaturalDuration.TimeSpan.TotalSeconds:0);};
   Media.MediaEnded+=delegate{if(closed||failed||browser!=null||State.Seeking)return;State.End();Media.Position=TimeSpan.Zero;Sync();ApplyPlayback();};
   Media.MediaFailed+=delegate{if(!closed&&browser==null)StartBrowser();};
   opening.Tick+=delegate{opening.Stop();if(!State.Ready)Fail();};
   Play.Click+=delegate{Toggle();};timer.Tick+=delegate{if(!closed&&!State.Seeking){State.Tick(Media.Position.TotalSeconds);Sync();}};
   Progress.ValueChanged+=delegate{if(!syncing&&State.Ready&&!State.Seeking){Seek(Progress.Value);}};
   Progress.PreviewMouseLeftButtonDown+=delegate(object s,MouseButtonEventArgs e){if(!State.Ready||failed)return;Progress.Focus();BeginSeek();if(Mouse.Capture(Progress)){Pointer(e.GetPosition(Progress));}else EndSeek();e.Handled=true;};
   Progress.PreviewMouseMove+=delegate(object s,MouseEventArgs e){if(State.Seeking&&Mouse.Captured==Progress){Pointer(e.GetPosition(Progress));e.Handled=true;}};
   Progress.PreviewMouseLeftButtonUp+=delegate(object s,MouseButtonEventArgs e){if(State.Seeking){Pointer(e.GetPosition(Progress));EndSeek();e.Handled=true;}};
   Progress.LostMouseCapture+=delegate{if(State.Seeking)EndSeek();};
  }
  internal void Start(){if(closed||failed||started)return;started=true;if(string.Equals(System.IO.Path.GetExtension(file),".webm",StringComparison.OrdinalIgnoreCase)){StartBrowser();return;}Media.Source=new Uri(System.IO.Path.GetFullPath(file));ApplyPlayback();}
  void Opened(double seconds){opening.Stop();State.Open(seconds);Progress.Maximum=Math.Max(1,State.Duration);Progress.IsEnabled=State.Ready;Play.IsEnabled=true;Sync();ApplyPlayback();}
  async void StartBrowser(){
   if(closed||browser!=null)return;timer.Stop();Media.Stop();Media.Close();Media.Source=null;Media.Visibility=Visibility.Collapsed;Elapsed.Text="正在加载…";Play.IsEnabled=false;
   browser=new BrowserVideo(file);surface.Children.Insert(0,browser.View);
   browser.Opened+=delegate(double seconds,int width,int height){if(!closed)Opened(seconds);};
   browser.PositionChanged+=delegate(double position){if(!closed&&!State.Seeking){State.Tick(position);Sync();}};
   browser.Ended+=delegate{if(closed||State.Seeking)return;State.End();Commit();Sync();ApplyPlayback();};browser.Failed+=Fail;
   browser.CloseRequested+=delegate{if(CloseRequested!=null)CloseRequested();};browser.Key+=BrowserKey;
   opening.Start();await browser.Start();
  }
  void Fail(){if(closed||failed)return;failed=true;opening.Stop();timer.Stop();State.Seeking=false;Release();Progress.IsEnabled=false;Play.IsEnabled=false;if(browser!=null){surface.Children.Remove(browser.View);browser.Dispose();}error.Text=BrowserVideo.Available?"无法解码此视频，文件可能损坏或编码暂不支持。可用默认播放器打开。":"此视频需要 Microsoft Edge WebView2 Runtime，当前电脑未安装。可先用默认播放器打开。";Elapsed.Text="无法播放";fallback.Visibility=Visibility.Visible;}
  internal void BrowserKey(string key){if(key=="Escape"){if(CloseRequested!=null)CloseRequested();}else if(key==" ")Toggle();else if(State.Ready&&(key=="ArrowLeft"||key=="ArrowRight"||key=="Home"||key=="End"))Seek(key=="Home"?0:key=="End"?Math.Max(0,State.Duration-.05):State.Position+(key=="ArrowLeft"?-5:5));}
  internal void Toggle(){if(closed||failed)return;State.Playing=!State.Playing;ApplyPlayback();}
  void ApplyPlayback(){if(closed||failed)return;Play.Content=State.Playing?"暂停":"播放";UiIcons.SetGlyph(Play,State.Playing?"\uE769":"\uE768");bool playing=State.Playing&&visible&&!State.Seeking;if(browser!=null){browser.Command(playing?"play":"pause");return;}if(playing){Media.Play();if(State.Ready)timer.Start();}else{Media.Pause();timer.Stop();}}
  internal void SetVisible(bool value){visible=value;if(!value&&State.Seeking)EndSeek();ApplyPlayback();}
  internal void BeginSeek(){if(!State.Ready||closed||failed)return;State.Seeking=true;timer.Stop();if(browser!=null)browser.Command("pause");else Media.Pause();}
  void Pointer(Point point){var track=Progress.Template.FindName("PART_Track",Progress) as Track;if(track==null)return;State.Seek(track.ValueFromPoint(Progress.TranslatePoint(point,track)));Sync();}
  internal void EndSeek(){if(!State.Seeking)return;State.Seeking=false;Release();Commit();ApplyPlayback();}
  void Release(){if(Mouse.Captured==Progress)Mouse.Capture(null);}
  internal void Seek(double value){if(!State.Ready||closed||failed)return;State.Seek(value);Commit();Sync();}
  void Commit(){if(!closed&&!failed){if(browser!=null)browser.Command("seek",State.Position);else Media.Position=TimeSpan.FromSeconds(State.Position);}}
  void Sync(){syncing=true;Progress.Value=State.Position;syncing=false;Elapsed.Text=VideoPlayback.Time(State.Position)+" / "+(State.Ready?VideoPlayback.Time(State.Duration):"--:--");}
  internal void KeyDown(object sender,KeyEventArgs e){if(e.Handled)return;if(e.Key==Key.Space){Toggle();e.Handled=true;}else if(State.Ready&&(e.Key==Key.Left||e.Key==Key.Right||e.Key==Key.Home||e.Key==Key.End)){Seek(e.Key==Key.Home?0:e.Key==Key.End?Math.Max(0,State.Duration-.05):State.Position+(e.Key==Key.Left?-5:5));e.Handled=true;}}
  public void Dispose(){if(closed)return;closed=true;State.Seeking=false;Release();timer.Stop();opening.Stop();if(browser!=null){surface.Children.Remove(browser.View);browser.Dispose();}Media.Stop();Media.Close();Media.Source=null;}
 }
}
