using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mori;
using QingJie;
using Drawing=System.Drawing;
using Imaging=System.Drawing.Imaging;
using Forms=System.Windows.Forms;

namespace MoriDesk {
 // Synthetic images, isolated pin folders and same-user pipes only. No real
 // screenshot runtime, hotkeys, shown pin windows or live library are started.
 internal static class DesktopImagePinTests {
  static void Reject(Action action,Action<bool,string> check,string message){bool rejected=false;try{action();}catch{rejected=true;}check(rejected,message);}
  static void Await(Task task,bool pump=true){var end=DateTime.UtcNow.AddSeconds(18);while(!task.IsCompleted&&DateTime.UtcNow<end){if(pump){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}Thread.Sleep(2);}if(!task.IsCompleted)throw new TimeoutException("Image pin fixture timed out");task.GetAwaiter().GetResult();}
  static byte[] Pixel(BitmapSource image,int x,int y){var bytes=new byte[4];var converted=new FormatConvertedBitmap(image,System.Windows.Media.PixelFormats.Bgra32,null,0);converted.CopyPixels(new Int32Rect(x,y,1,1),bytes,4,0);return bytes;}
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);BitmapSource snapshot;
   using(var image=new Drawing.Bitmap(200,100,Imaging.PixelFormat.Format32bppArgb)){
    image.SetPixel(40,30,Drawing.Color.FromArgb(128,30,80,170));image.SetPixel(100,60,Drawing.Color.FromArgb(255,80,110,150));snapshot=DesktopImagePins.Snapshot(image);
    check(snapshot.IsFrozen&&snapshot.PixelWidth==200&&snapshot.PixelHeight==100,"desktop pin snapshots full decoded image dimensions, not a zoomed viewport");var pixel=Pixel(snapshot,40,30);check(pixel[0]==170&&pixel[1]==80&&pixel[2]==30&&pixel[3]==128&&Pixel(snapshot,0,0)[3]==0,"desktop pin copies original alpha and RGB without transparency checker or letterbox / "+string.Join(",",pixel));
    var view=new PreviewViewport(200,100);view.Resize(60,60);view.ZoomAt(5,20,20);view.PanTo(-140,-60);
    using(var picture=new PreviewPicture(image,view,new Settings{AnimateImages=false})){
     picture.BackgroundMode=PreviewBackground.DarkChecker;int requests=0;picture.AttachDesktopPin(()=>{requests++;return Task.FromResult(true);});picture.AttachDesktopPin(()=>{requests+=100;return Task.FromResult(true);});
     var menu=picture.ContextMenuStrip;check(menu.Items.Count==7&&menu.Items[0].Name=="PinToDesktop"&&menu.Items[1] is Forms.ToolStripSeparator,"image context menu exposes one desktop pin action above all original backdrop choices");((Forms.ToolStripMenuItem)menu.Items[0]).PerformClick();check(requests==1,"actual desktop pin menu click invokes its one explicit action");
     picture.BackgroundMode=PreviewBackground.White;var choices=menu.Items.OfType<Forms.ToolStripMenuItem>().Where(x=>x.Tag is PreviewBackground).ToArray();check(choices.Length==5&&choices.Count(x=>x.Checked)==1&&choices.Single(x=>x.Checked).Tag.Equals(PreviewBackground.White),"adding pin action preserves all five mutually exclusive image background choices");
     using(var saved=new MemoryStream(ImagePinBridge.Encode(DesktopImagePins.Snapshot(image)))){var decoded=ImagePinBridge.Decode(saved.ToArray());check(decoded.PixelWidth==200&&decoded.PixelHeight==100&&Pixel(decoded,0,0)[3]==0,"zoom, pan and chosen preview backdrop never enter the pinned image");}
     picture.Dispose();check(menu.IsDisposed,"closing a preview releases its extra pin menu along with backdrop controls");
    }
   }
   check(Pixel(snapshot,40,30)[3]==128,"frozen pin snapshot survives disposal of its preview image");
   var encoded=ImagePinBridge.Encode(snapshot);var detached=ImagePinBridge.Decode(encoded);check(detached.IsFrozen&&Pixel(detached,100,60)[2]==80,"bounded PNG bridge decode is detached and retains original pixels");
   Reject(()=>DesktopImagePins.Snapshot(null),check,"missing decoded image cannot produce a pin");Reject(()=>ImagePinBridge.ValidateDimensions(0,50),check,"zero-size pin rejected");Reject(()=>ImagePinBridge.ValidateDimensions(10000,10000),check,"oversized pin rejected before allocating a converted image");Reject(()=>ImagePinBridge.Decode(new byte[]{1,2,3}),check,"corrupt bridge image is rejected");Reject(()=>ImagePinBridge.Decode(new byte[ImagePinBridge.MaximumBytes+1]),check,"oversized bridge byte payload is rejected before decoding");
   using(var jpeg=new Drawing.Bitmap(2,2))using(var stream=new MemoryStream()){jpeg.Save(stream,Imaging.ImageFormat.Jpeg);Reject(()=>ImagePinBridge.Decode(stream.ToArray()),check,"bridge protocol accepts only its single static PNG representation");}
   var gif=(byte[])typeof(PreviewPreferencesTests).GetMethod("AnimatedGif",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,null);using(var stream=new MemoryStream(gif))using(var animated=Drawing.Image.FromStream(stream)){
    animated.SelectActiveFrame(Imaging.FrameDimension.Time,1);var still=DesktopImagePins.Snapshot(animated);check(still.PixelWidth==8&&still.PixelHeight==8&&Pixel(still,4,4)[0]>240&&Pixel(still,4,4)[2]<10,"animated GIF pin preserves explicitly visible current frame as a static PNG");animated.SelectActiveFrame(Imaging.FrameDimension.Time,0);check(Pixel(still,4,4)[0]>240,"later animation frames cannot alter the frozen desktop pin");
   }
   foreach(var work in new[]{new Drawing.Rectangle(0,0,1920,1080),new Drawing.Rectangle(-1280,0,1280,720)})foreach(var size in new[]{new Drawing.Size(4000,6000),new Drawing.Size(5000,2000),new Drawing.Size(200,100)}){
    var first=DesktopImagePins.InitialBounds(size.Width,size.Height,work,0);var next=DesktopImagePins.InitialBounds(size.Width,size.Height,work,1);check(work.Contains(new Drawing.Rectangle(first.X,first.Y,first.Width,first.Height))&&first.Width<=720&&first.Height<=work.Height-48,"new desktop pin fits the selected monitor working area / "+work.Width+" / "+size);check(first.X!=next.X||first.Y!=next.Y,"consecutive desktop pins are offset rather than fully covering one another / "+work.Width+" / "+size);
   }
   var pins=new PinStore(Path.Combine(root,"pins"));var one=pins.Add(snapshot);var two=pins.Add(detached);check(one.Id!=two.Id&&pins.Records.Count==2&&File.Exists(pins.ImagePath(one))&&File.Exists(pins.ImagePath(two)),"two pin requests persist independent snapshots without a picture-count cap");
   one.Width=20;pins.Save();check(new PinStore(pins.Folder).Records.Single(record=>record.Id==one.Id).Width==20,"restoring a narrow aspect-fitted long pin does not silently expand it to an eighty-pixel minimum");
   var oldPins=AppState.Pins;var oldSettings=AppState.Settings;PinWindow a=null,b=null;
   try{AppState.Pins=pins;AppState.Settings=new Preferences();a=new PinWindow(pins,one,snapshot);b=new PinWindow(pins,two,detached);check(a.WindowStyle==WindowStyle.None&&a.AllowsTransparency&&a.Topmost&&!a.ShowInTaskbar&&a.Owner==null,"library pin reuses screenshot borderless topmost window without preview ownership");var picture=(System.Windows.Controls.Image)typeof(PinWindow).GetField("picture",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(a);check(a.Width-2*PinFrame.Inset==20&&picture.Stretch==System.Windows.Media.Stretch.Uniform,"narrow restored pin keeps its saved scale and never stretches image axes independently");check(b!=a&&b.Topmost&&b.Owner==null,"multiple independent pin windows do not share ownership or closing state");a.Close();check(File.Exists(pins.ImagePath(two))&&b.Content!=null,"closing one pin retains another independent pin and its saved pixels");}finally{if(a!=null)a.Close();if(b!=null)b.Close();AppState.Pins=oldPins;AppState.Settings=oldSettings;}
   var security=ImagePinBridge.Security();var rules=security.GetAccessRules(true,false,typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();var sid=WindowsIdentity.GetCurrent().User;check(security.AreAccessRulesProtected&&rules.Length==1&&rules[0].IdentityReference.Equals(sid)&&rules[0].AccessControlType==AccessControlType.Allow,"image pin pipe disables inherited access and grants only current Windows user");
   check(ImagePinBridge.PipeName(root)==ImagePinBridge.PipeName(root.ToUpperInvariant()+"\\")&&ImagePinBridge.PipeName(root)!=ImagePinBridge.PipeName(Path.Combine(root,"other")),"image pin service name follows normalized data root and never crosses fixture roots");
   Bridge(root,snapshot,detached,check);
   AsyncBridge(root,snapshot,check);
   ViewerPending(root,snapshot,check);
  }
  static void AsyncBridge(string root,BitmapSource image,Action<bool,string> check){
   var commit=new TaskCompletionSource<bool>();var started=new TaskCompletionSource<bool>();int completed=0;bool responsive=false;
   using(var service=new ImagePinBridge(Path.Combine(root,"async-bridge"),Dispatcher.CurrentDispatcher,async value=>{started.TrySetResult(true);await commit.Task;completed++;})){
    var pending=ImagePinBridge.SendAsync(Path.Combine(root,"async-bridge"),image);Await(started.Task);
    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>responsive=true));
    var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);
    check(responsive&&!pending.IsCompleted&&completed==0,"resident pin bridge releases the dispatcher and does not acknowledge an uncommitted asynchronous save");
    commit.SetResult(true);Await(pending);check(completed==1,"resident pin bridge acknowledges exactly one completed asynchronous save");
   }
   var failed=new TaskCompletionSource<bool>();started=new TaskCompletionSource<bool>();
   using(var service=new ImagePinBridge(Path.Combine(root,"async-failure"),Dispatcher.CurrentDispatcher,value=>{started.TrySetResult(true);return failed.Task;})){
    var pending=ImagePinBridge.SendAsync(Path.Combine(root,"async-failure"),image);Await(started.Task);failed.SetException(new IOException("fixture asynchronous save failed"));
    bool rejected=false;try{Await(pending);}catch(InvalidOperationException error){rejected=error.Message=="fixture asynchronous save failed";}check(rejected,"a later asynchronous persistence failure reaches the independent viewer instead of a false success");
   }
  }
  static void Bridge(string root,BitmapSource first,BitmapSource second,Action<bool,string> check){
   string endpoint=Path.Combine(root,"bridge"),unavailable=Path.Combine(root,"not-running");int calls=0;var store=new PinStore(Path.Combine(root,"bridge-pins"));
   using(var service=new ImagePinBridge(endpoint,Dispatcher.CurrentDispatcher,image=>{calls++;store.Add(image);return Task.FromResult(true);})){
    var one=ImagePinBridge.SendAsync(endpoint,first);var two=ImagePinBridge.SendAsync(endpoint,second);Await(Task.WhenAll(one,two));check(calls==2&&store.Records.Count==2&&store.Records.Select(x=>x.Id).Distinct().Count()==2,"two independent viewer requests are acknowledged only after one resident sink persists each snapshot");
    // Block this fixture's dispatcher only. Expired queued work must not create
    // a surprise pin after the caller received a busy response.
    var busy=ImagePinBridge.SendAsync(endpoint,first);Thread.Sleep(6200);bool rejected=false;try{Await(busy,false);}catch(InvalidOperationException error){rejected=error.Message.Contains("正忙");}check(rejected&&calls==2,"busy resident UI returns an explicit bounded error without pretending the pin was saved");
    var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);check(calls==2,"a timed-out queued pin cannot execute later when the UI becomes available");
   }
   bool missing=false;try{Await(ImagePinBridge.SendAsync(unavailable,first),false);}catch(InvalidOperationException error){missing=error.Message.Contains("主窗口");}check(missing,"independent viewer without resident Nook explains how to enable pinning instead of creating a second store");
   using(var failing=new ImagePinBridge(Path.Combine(root,"failing"),Dispatcher.CurrentDispatcher,image=>{throw new IOException("fixture pin save failed");})){
    bool failed=false;try{Await(ImagePinBridge.SendAsync(Path.Combine(root,"failing"),first));}catch(InvalidOperationException error){failed=error.Message=="fixture pin save failed";}check(failed,"resident pin persistence failure reaches viewer and can never be acknowledged as success");
   }
   check(store.Records.Count==2&&new PinStore(store.Folder).Records.Count==2,"closing independent preview/bridge lifetimes preserves already-saved desktop pins");
  }
  static void ViewerPending(string root,BitmapSource snapshot,Action<bool,string> check){
   const BindingFlags hidden=BindingFlags.NonPublic|BindingFlags.Instance;string path=Path.Combine(root,"viewer.png");ImageFiles.Write(snapshot,path);var context=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));var before=DesktopImagePins.SinkForTests;ImageBrowser viewer=null;PreviewImage opened=null;var completed=new TaskCompletionSource<bool>();BitmapSource submitted=null;
   try{
    viewer=new ImageBrowser(null);opened=PreviewImage.Open(path);typeof(ImageBrowser).GetField("image",hidden).SetValue(viewer,opened);typeof(ImageBrowser).GetField("currentPath",hidden).SetValue(viewer,path);typeof(ImageBrowser).GetField("siblings",hidden).SetValue(viewer,new[]{path});var update=typeof(ImageBrowser).GetMethod("UpdateInfo",hidden);update.Invoke(viewer,null);var button=(System.Windows.Controls.Button)typeof(ImageBrowser).GetField("pinButton",hidden).GetValue(viewer);check(button.IsEnabled,"independent image viewer enables its desktop pin button after image load");
    DesktopImagePins.SinkForTests=image=>{submitted=image;return completed.Task;};var pending=(Task)typeof(ImageBrowser).GetMethod("PinCurrentAsync",hidden).Invoke(viewer,null);check(!pending.IsCompleted&&!button.IsEnabled&&submitted!=null&&submitted.IsFrozen,"viewer freezes clicked image before asynchronous handoff and disables duplicate submission");
    update.Invoke(viewer,null);check(!button.IsEnabled,"directory scanning or information refresh cannot reenable a pending pin request");
    var again=(Task)typeof(ImageBrowser).GetMethod("PinCurrentAsync",hidden).Invoke(viewer,null);check(again.IsCompleted&&!pending.IsCompleted,"repeated click cannot submit the pending image a second time");
    viewer.Close();check((bool)typeof(ImageBrowser).GetField("closeAfterPin",hidden).GetValue(viewer)&&!(bool)typeof(ImageBrowser).GetField("closed",hidden).GetValue(viewer),"closing viewer while handing off defers process window closure until result is known");completed.SetResult(true);Await(pending);check((bool)typeof(ImageBrowser).GetField("closed",hidden).GetValue(viewer)&&Pixel(submitted,40,30)[3]==128,"viewer closes after handoff while immutable desktop pin pixels remain independent");opened=null;
   }finally{completed.TrySetResult(true);DesktopImagePins.SinkForTests=before;if(viewer!=null)viewer.Close();if(opened!=null)opened.Dispose();SynchronizationContext.SetSynchronizationContext(context);}
  }
 }
}
