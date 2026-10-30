using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QingJie;

namespace MoriDesk {
 // Only unshown logical WPF windows and injected output tasks are used.
 // No native window, monitor query, OCR worker or real pin presenter is run.
 internal static class TextWindowPinOutputTests {
  const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
  static T Field<T>(TextWindow window,string name){return (T)typeof(TextWindow).GetField(name,Hidden).GetValue(window);}
  static BitmapSource Pixels(byte channel){var image=BitmapSource.Create(2,2,96,96,PixelFormats.Bgra32,null,new byte[]{channel,90,120,255,channel,90,120,255,channel,90,120,255,channel,90,120,255},8);image.Freeze();return image;}
  static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
  static void Wait(Task task){var deadline=DateTime.UtcNow.AddSeconds(5);while(!task.IsCompleted&&DateTime.UtcNow<deadline){Pump();Thread.Sleep(1);}if(!task.IsCompleted)throw new Exception("Synthetic pin output did not settle.");task.GetAwaiter().GetResult();}
  static TextWindow Reader(BitmapSource image){return new TextWindow(image,"zh",(source,token)=>Task.FromResult(new OcrPage()));}
  static void Inert(TextWindow window,Action<bool,string> check){check(!window.IsVisible&&new WindowInteropHelper(window).Handle==IntPtr.Zero,"image-reader pin regression never creates a visible or native window");}
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);var oldPins=AppState.Pins;var oldContext=SynchronizationContext.Current;var oldSettings=AppState.Settings;
   SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));AppState.Settings=new Preferences();AppState.Pins=new PinStore(root);
   try{
    var image=Pixels(55);var window=Reader(image);
    try{
     int calls=0;BitmapSource received=null;var pending=new TaskCompletionSource<bool>();
     window.PinOutput=(source,token)=>{calls++;received=source;return pending.Task;};
     var request=window.PinImageAsync();
     check(!request.IsCompleted&&calls==1&&ReferenceEquals(received,image),"pin action yields while preserving the exact displayed source image");
     bool dispatcherRan=false;window.Dispatcher.BeginInvoke(new Action(()=>dispatcherRan=true));Pump();
     check(dispatcherRan&&!request.IsCompleted,"image reader dispatcher remains responsive while pin output is pending");
     check(!Field<Button>(window,"pinImage").IsEnabled&&!Field<Button>(window,"copyImage").IsEnabled&&!Field<Button>(window,"saveImage").IsEnabled,"pending pin disables competing pin copy and save actions");
     Wait(window.PinImageAsync());check(calls==1,"repeated pin command cannot start a duplicate output while the first is pending");
     pending.SetResult(true);Wait(request);
     check(Field<Button>(window,"pinImage").IsEnabled&&Field<Button>(window,"copyImage").IsEnabled&&Field<Button>(window,"saveImage").IsEnabled,"successful pin restores all output actions");
     check(Field<TextBlock>(window,"status").Text=="已贴到屏幕。"&&!Field<bool>(window,"closed"),"successful pin confirms completion and leaves the image reader open");
     var translated=Pixels(155);typeof(TextWindow).GetField("translatedImage",Hidden).SetValue(window,translated);typeof(TextWindow).GetField("showTranslated",Hidden).SetValue(window,true);
     Wait(window.PinImageAsync());check(calls==2&&ReferenceEquals(received,translated),"pinning translated view uses translated pixels rather than the hidden original");Inert(window,check);
    }finally{window.Close();}
    var failed=Reader(image);
    try{
     var failure=new TaskCompletionSource<bool>();failure.SetException(new IOException("synthetic full disk"));failed.PinOutput=(source,token)=>failure.Task;Wait(failed.PinImageAsync());
     check(!Field<bool>(failed,"closed")&&ReferenceEquals(Field<Image>(failed,"picture").Source,image)&&Field<TextBlock>(failed,"status").Text.Contains("synthetic full disk"),"failed pin keeps source image open and exposes a retryable save error");
     int retried=0;failed.PinOutput=(source,token)=>{retried++;return Task.FromResult(true);};Wait(failed.PinImageAsync());
     check(retried==1&&Field<Button>(failed,"pinImage").IsEnabled,"pin output can retry normally after a storage failure");Inert(failed,check);
    }finally{failed.Close();}
    var closing=Reader(image);CancellationToken lifetime=CancellationToken.None;
    try{
     int presented=0;closing.PinOutput=async(source,token)=>{lifetime=token;await Task.Delay(Timeout.Infinite,token);presented++;};var request=closing.PinImageAsync();closing.Close();Wait(request);
     check(lifetime.IsCancellationRequested&&presented==0&&Field<bool>(closing,"closed"),"closing the reader cancels pending pin output before a late desktop presentation");
     Wait(closing.PinImageAsync());check(presented==0,"closed reader cannot start another pin output");Inert(closing,check);
    }finally{if(!Field<bool>(closing,"closed"))closing.Close();}
    var disabled=new TextWindow(image,"zh",(source,token)=>Task.FromResult(new OcrPage()),allowPin:false);
    try{int calls=0;disabled.PinOutput=(source,token)=>{calls++;return Task.FromResult(true);};Wait(disabled.PinImageAsync());check(calls==0,"disabled screenshot pin feature rejects direct reader pin commands");Inert(disabled,check);}finally{disabled.Close();}
    check(AppState.Pins.Records.Count==0&&Directory.GetFiles(root,"*.png").Length==0,"injected reader pin tests never create actual desktop pins or image files");
   }finally{AppState.Pins=oldPins;AppState.Settings=oldSettings;SynchronizationContext.SetSynchronizationContext(oldContext);}
  }
 }
}
