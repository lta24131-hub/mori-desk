using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Automation;
using System.Windows.Threading;
using Mori;
using Application=System.Windows.Application;
using ShutdownMode=System.Windows.ShutdownMode;
using Forms=System.Windows.Forms;

namespace MoriDesk {
 // Synthetic render/state fixtures only: no Start(), no browser runtime,
 // no media source, no shown window, and no changes to the user's settings.
 internal static class PreviewPreferencesTests {
  const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
  static bool Near(double actual,double expected){return Math.Abs(actual-expected)<.0001;}
  static bool NearColor(Color actual,Color expected){return Math.Abs(actual.R-expected.R)<4&&Math.Abs(actual.G-expected.G)<4&&Math.Abs(actual.B-expected.B)<4;}
  static bool CheckerColor(Color color,bool dark){return NearColor(color,dark?PreviewBackgrounds.DarkA:PreviewBackgrounds.LightA)||NearColor(color,dark?PreviewBackgrounds.DarkB:PreviewBackgrounds.LightB);}
  static void Call(VideoPlayer player,string name,params object[] values){typeof(VideoPlayer).GetMethod(name,Hidden).Invoke(player,values);}
  static bool Animating(PreviewPicture picture){return (bool)typeof(PreviewPicture).GetField("animating",Hidden).GetValue(picture);}
  static void Inert(VideoPlayer player,Action<bool,string> check,string label){
   var opening=(DispatcherTimer)typeof(VideoPlayer).GetField("opening",Hidden).GetValue(player);
   check(!player.UsingBrowser&&player.Browser==null&&player.Media.Source==null&&!player.TimerRunning&&!opening.IsEnabled&&!(bool)typeof(VideoPlayer).GetField("started",Hidden).GetValue(player),"preview preferences never initialize a decoder or browser / "+label);
  }
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);string settingsPath=Path.Combine(root,"preview-preferences.json");
   var defaults=new Settings();check(defaults.PreviewBackdrop=="automatic"&&defaults.AnimateImages&&defaults.MediaVolume==70&&!defaults.MediaMuted&&defaults.MediaAutoPlay&&defaults.MediaLoop,"new preview preferences preserve all previous defaults");
   var settings=new Settings{LibraryPath=Path.Combine(root,"fixture-library"),PreviewBackdrop="dark-checker",AnimateImages=false,MediaVolume=34,MediaMuted=true,MediaAutoPlay=false,MediaLoop=false};settings.SaveTo(settingsPath);var restored=Settings.LoadFrom(settingsPath);
   check(restored.PreviewBackdrop=="dark-checker"&&!restored.AnimateImages&&restored.MediaVolume==34&&restored.MediaMuted&&!restored.MediaAutoPlay&&!restored.MediaLoop,"preview defaults survive the real settings serializer without touching user settings");
   Backgrounds(check);Animation(check);Playback(restored,check);
  }
  static void Backgrounds(Action<bool,string> check){
   string[] values={"automatic","light-checker","dark-checker","white","dark"};PreviewBackground[] modes={PreviewBackground.Automatic,PreviewBackground.LightChecker,PreviewBackground.DarkChecker,PreviewBackground.White,PreviewBackground.Dark};
   using(var image=new Bitmap(80,60,PixelFormat.Format32bppArgb)){
    using(var graphics=Graphics.FromImage(image)){graphics.Clear(Color.Transparent);graphics.FillRectangle(Brushes.Crimson,20,15,40,30);}
    var view=new PreviewViewport(80,60);view.Resize(80,60);
    for(int i=0;i<values.Length;i++){
     var settings=new Settings{PreviewBackdrop=values[i],AnimateImages=false};
     using(var picture=new PreviewPicture(image,view,settings){Size=new Size(80,60),BackColor=Color.SteelBlue})using(var render=new Bitmap(80,60)){
      check(picture.BackgroundMode==modes[i],"image preview maps saved backdrop / "+values[i]);
      int checkedCount=0;bool checkedChoice=false;foreach(Forms.ToolStripMenuItem item in picture.ContextMenuStrip.Items){if(item.Checked)checkedCount++;if((PreviewBackground)item.Tag==modes[i])checkedChoice=item.Checked;}
      check(checkedCount==1&&checkedChoice,"saved backdrop is checked before opening its menu / "+values[i]);
      picture.DrawToBitmap(render,new Rectangle(0,0,80,60));var color=render.GetPixel(4,4);
      check(i<3?CheckerColor(color,i==2):NearColor(color,i==3?Color.White:PreviewBackgrounds.SolidDark),"saved backdrop reaches the actual renderer / "+values[i]);
      check(NearColor(render.GetPixel(40,30),Color.Crimson)&&image.GetPixel(4,4).A==0,"backdrop preserves opaque and transparent source pixels / "+values[i]);
      settings.PreviewBackdrop=values[(i+1)%values.Length];check(picture.BackgroundMode==modes[i],"changing settings cannot recolor an existing preview / "+values[i]);
      picture.BackgroundMode=modes[(i+2)%modes.Length];check(settings.PreviewBackdrop==values[(i+1)%values.Length],"per-preview background choice does not rewrite global defaults / "+values[i]);
     }
    }
    var newest=new Settings{PreviewBackdrop="white"};using(var first=new PreviewPicture(image,view,newest)){
     newest.PreviewBackdrop="dark";using(var next=new PreviewPicture(image,view,newest))check(first.BackgroundMode==PreviewBackground.White&&next.BackgroundMode==PreviewBackground.Dark,"new previews consume updated defaults while existing previews retain their choice");
    }
   }
  }
  static void GifCode(byte[] bytes,ref int bit,int code){for(int i=0;i<3;i++,bit++)if((code&(1<<i))!=0)bytes[bit/8]|=(byte)(1<<(bit%8));}
  static byte[] AnimatedGif(){
   // A realistic uniform 8x8 frame leaves an interior pixel after GDI's
   // bilinear upscale. A 1x1 frame blends its entire area with edge padding.
   using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream)){
    writer.Write(new byte[]{71,73,70,56,57,97,8,0,8,0,128,0,0,255,0,0,0,0,255,33,255,11,78,69,84,83,67,65,80,69,50,46,48,3,1,0,0,0});
    for(int frame=0;frame<2;frame++){
     writer.Write(new byte[]{33,249,4,4,10,0,0,0,44,0,0,0,0,8,0,8,0,0,2});
     var pixels=new byte[49];int bit=0;for(int i=0;i<64;i++){GifCode(pixels,ref bit,4);GifCode(pixels,ref bit,frame);}GifCode(pixels,ref bit,5);
     writer.Write((byte)pixels.Length);writer.Write(pixels);writer.Write((byte)0);
    }
    writer.Write((byte)59);return stream.ToArray();
   }
  }
  static void Animation(Action<bool,string> check){
   byte[] gif=AnimatedGif();
   using(var stream=new MemoryStream(gif))using(var image=Image.FromStream(stream)){
    check(ImageAnimator.CanAnimate(image)&&image.GetFrameCount(FrameDimension.Time)==2,"animation preferences use a real two-frame GIF fixture");
    image.SelectActiveFrame(FrameDimension.Time,1);using(var decoded=new Bitmap(image))check(decoded.GetPixel(4,4).B>240&&decoded.GetPixel(4,4).R<10,"GIF fixture has a visibly different blue second frame");
    image.SelectActiveFrame(FrameDimension.Time,0);using(var decoded=new Bitmap(image))check(decoded.GetPixel(4,4).R>240&&decoded.GetPixel(4,4).B<10,"GIF fixture has an opaque red first frame before the preview starts");
    var view=new PreviewViewport(image.Width,image.Height);view.Resize(40,40);var settings=new Settings{AnimateImages=false};
    using(var picture=new PreviewPicture(image,view,settings){Size=new Size(40,40)})using(var render=new Bitmap(40,40)){
     var handle=picture.Handle;check(!Animating(picture),"disabled GIF animation does not register callbacks on handle creation");
     picture.SetPlaybackVisible(false);picture.SetPlaybackVisible(true);picture.Visible=false;picture.Visible=true;check(!Animating(picture),"restoring a disabled GIF preview never starts its animator");
     picture.DrawToBitmap(render,new Rectangle(0,0,40,40));var first=render.GetPixel(20,20);check(first.R>240&&first.B<10,"disabled GIF remains readable at its actual first frame");
     settings.AnimateImages=true;picture.SetPlaybackVisible(true);check(!Animating(picture),"updating the default cannot start animation in an already-open static preview");
    }
   }
   using(var stream=new MemoryStream(gif))using(var image=Image.FromStream(stream)){
    var view=new PreviewViewport(image.Width,image.Height);view.Resize(40,40);var settings=new Settings{AnimateImages=true};using(var picture=new PreviewPicture(image,view,settings){Size=new Size(40,40)}){
     var handle=picture.Handle;check(Animating(picture),"enabled GIF default retains animation on new preview");settings.AnimateImages=false;picture.SetPlaybackVisible(false);check(!Animating(picture),"minimized animated preview retains existing callback suspension");picture.SetPlaybackVisible(true);check(Animating(picture),"existing animated preview resumes its copied preference even after defaults change");picture.Dispose();check(!Animating(picture),"disposing an animated preview stops callbacks");
    }
   }
  }
  static void Playback(Settings restored,Action<bool,string> check){
   var app=Application.Current??new Application();var shutdown=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
   try{
    foreach(string extension in new[]{".mp4",".mp3",".webm"})using(var player=new VideoPlayer(Path.Combine(Path.GetTempPath(),"nook-inert-preferences"+extension),restored)){
     check(Near(player.Sound.Level,.34)&&player.Sound.Muted&&player.Sound.Effective==0&&player.Media.Volume==0&&Near(player.Volume.Value,.34),"saved muted volume is applied without losing its slider level / "+extension);
     check(!player.State.Playing&&!player.State.Loop&&AutomationProperties.GetName(player.Play)=="播放","saved autoplay and loop defaults initialize the visible playback control / "+extension);Inert(player,check,"constructor "+extension);
     Call(player,"Opened",12.0);check(player.State.Ready&&player.State.Position==0&&!player.State.Playing&&player.Progress.IsEnabled,"autoplay-off loads metadata and seek controls but stays paused / "+extension);Inert(player,check,"metadata "+extension);
     player.ToggleMute();check(!player.Sound.Muted&&Near(player.Media.Volume,.34)&&Near(player.Volume.Value,.34),"unmute restores the configured default level / "+extension);
     player.SetVolume(.25);player.ToggleMute();check(player.Media.Volume==0&&Near(player.Sound.Level,.25),"manual mute preserves a later per-preview adjustment / "+extension);
     player.Toggle();check(player.State.Playing&&AutomationProperties.GetName(player.Play)=="暂停","autoplay-off preview can be started explicitly / "+extension);player.State.Seek(11.9);Call(player,"Ended");
     check(!player.State.Playing&&player.State.Position==12&&player.Progress.Value==12&&player.Elapsed.Text=="00:12 / 00:12"&&AutomationProperties.GetName(player.Play)=="播放","loop-off completion pauses at the end and never jumps to zero / "+extension);
     player.SetVisible(false);player.SetVisible(true);check(!player.State.Playing&&player.State.Position==12,"restoring an ended non-loop preview cannot restart it / "+extension);
     player.Toggle();check(player.State.Playing&&player.State.Position==0&&player.Progress.Value==0,"explicit replay starts at zero only after the user presses play / "+extension);
     player.State.Seeking=true;player.State.Seek(8);Call(player,"Ended");check(player.State.Seeking&&player.State.Position==8&&player.State.Playing,"late completion event cannot interrupt an active seek / "+extension);player.State.Seeking=false;
     Inert(player,check,"all interactions "+extension);
    }
    var preferences=new Settings{MediaVolume=29,MediaAutoPlay=true,MediaLoop=true};using(var player=new VideoPlayer(Path.Combine(Path.GetTempPath(),"nook-inert-loop.mp4"),preferences)){
     check(Near(player.Sound.Level,.29)&&Near(player.Media.Volume,.29)&&player.State.Playing&&player.State.Loop,"enabled defaults preserve automatic playback and looping");
     preferences.MediaVolume=90;preferences.MediaMuted=true;preferences.MediaAutoPlay=false;preferences.MediaLoop=false;
     Call(player,"Opened",12.0);player.State.Seek(11.9);Call(player,"Ended");check(player.State.Position==0&&player.State.Playing&&player.State.Loop&&Near(player.Sound.Level,.29)&&!player.Sound.Muted,"open player keeps copied loop and sound preferences when global defaults change");
     player.Toggle();player.State.Seek(12);Call(player,"Ended");check(!player.State.Playing&&player.State.Position==0,"loop reset continues to preserve a manually paused state");Inert(player,check,"loop completion");
     using(var next=new VideoPlayer(Path.Combine(Path.GetTempPath(),"nook-inert-new.mp4"),preferences))check(Near(next.Sound.Level,.9)&&next.Sound.Muted&&!next.State.Playing&&!next.State.Loop,"next player consumes the changed preferences without altering the existing one");
    }
    foreach(int value in new[]{0,100})using(var player=new VideoPlayer(Path.Combine(Path.GetTempPath(),"nook-inert-volume.mp4"),new Settings{MediaVolume=value,MediaMuted=false}))check(Near(player.Media.Volume,value/100.0)&&Near(player.Volume.Value,value/100.0)&&!player.Sound.Muted,"zero and maximum default volume remain bounded / "+value);
   }finally{app.ShutdownMode=shutdown;}
  }
 }
}
