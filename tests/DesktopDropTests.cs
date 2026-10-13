using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mori;
using STATSTG=System.Runtime.InteropServices.ComTypes.STATSTG;

namespace MoriDesk {
 internal static class DesktopDropTests {
  const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
  static bool Reject(Action action){try{action();return false;}catch{return true;}}
  static void PumpUntil(Func<bool> done){
   var frame=new DispatcherFrame();var watch=System.Diagnostics.Stopwatch.StartNew();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(10)};
   timer.Tick+=(s,e)=>{if(done()||watch.Elapsed.TotalSeconds>10)frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);timer.Stop();if(!done())throw new Exception("desktop drop timed out");
  }
  static DragEventArgs Drop(MainWindow main,DataObject data,DragDropEffects effects=DragDropEffects.Copy){
   var args=(DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),Flags,null,new object[]{data,DragDropKeyStates.None,effects,main.Window,new Point(10,10)},System.Globalization.CultureInfo.InvariantCulture);
   args.RoutedEvent=UIElement.PreviewDropEvent;main.Window.RaiseEvent(args);PumpUntil(()=>!(bool)typeof(MainWindow).GetField("busy",Flags).GetValue(main));return args;
  }
  public static void Run(string root,Action<bool,string> check){
   byte[] png=File.ReadAllBytes(Path.Combine(root,"reference-photo.png"));
   var data=new DataObject();data.SetData("PNG",new MemoryStream(png));check(DesktopImageDrop.CanRead(data),"raw desktop PNG offered as copy");
   var capture=DesktopImageDrop.Capture(data);check(capture.Bytes.SequenceEqual(png),"PNG copied during drop callback");
   var flaky=new FlakyData();flaky.Inner.SetData("PNG",new MemoryStream(png));flaky.Broken=DataFormats.Html;
   check(DesktopImageDrop.Capture(flaky).Bytes.SequenceEqual(png),"optional HTML failure never discards valid PNG");
   data=new DataObject("PNG",new byte[]{1,2,3});data.SetData("image/png",new MemoryStream(png));
   check(DesktopImageDrop.Capture(data).Bytes.SequenceEqual(png),"invalid first encoded format falls back to valid second format");
   string staged=capture.Stage().GetAwaiter().GetResult();check(File.Exists(staged)&&File.ReadAllBytes(staged).SequenceEqual(png),"staged image preserves bytes");DesktopImageDrop.Cleanup(staged);check(!File.Exists(staged),"staged files cleaned after import");
   var stream=new MemoryStream(png);stream.Position=5;check(DesktopImageDrop.ReadBounded(stream,png.Length).SequenceEqual(png)&&stream.Position==5,"source stream position restored");check(Reject(()=>DesktopImageDrop.ReadBounded(stream,1)),"over-limit streams rejected");
   check(VirtualImageFile.ReadStream(new ImageStream(png)).SequenceEqual(png),"indexed OLE stream can be read and bounded");
   using(var source=new MemoryStream(png)){var bitmap=BitmapFrame.Create(source,BitmapCreateOptions.None,BitmapCacheOption.OnLoad);data=new DataObject(DataFormats.Bitmap,bitmap);capture=DesktopImageDrop.Capture(data);check(capture.Bitmap.IsFrozen&&DesktopImageDrop.ImageExtension(capture.Materialize().Result)==".png","desktop bitmap captured as safe frozen PNG");}
   var descriptor=new byte[596];BitConverter.GetBytes(1).CopyTo(descriptor,0);Encoding.Unicode.GetBytes("参考图片.png\0").CopyTo(descriptor,76);data=new DataObject();data.SetData("FileGroupDescriptorW",new MemoryStream(descriptor));data.SetData("FileContents",new MemoryStream(png));capture=DesktopImageDrop.Capture(data);check(capture.Name=="参考图片"&&capture.Bytes.SequenceEqual(png),"virtual file with Unicode name captured");
   flaky=new FlakyData{Broken="PNG"};flaky.Inner.SetData("FileGroupDescriptorW",new MemoryStream(descriptor));flaky.Inner.SetData("FileContents",new MemoryStream(png));
   check(DesktopImageDrop.Capture(flaky).Bytes.SequenceEqual(png),"failed advertised PNG falls back to virtual file contents");
   using(var source=new MemoryStream(png)){var bitmap=BitmapFrame.Create(source,BitmapCreateOptions.None,BitmapCacheOption.OnLoad);flaky=new FlakyData{Broken="FileGroupDescriptorW"};flaky.Inner.SetData(DataFormats.Bitmap,bitmap);check(DesktopImageDrop.Capture(flaky).Bitmap!=null,"failed virtual descriptor falls back to bitmap");}
   BitConverter.GetBytes(2).CopyTo(descriptor,0);data.SetData("FileGroupDescriptorW",new MemoryStream(descriptor));check(Reject(()=>DesktopImageDrop.Capture(data)),"multiple virtual files explicitly rejected");
   string html="Version:1.0\r\nSourceURL:https://example.com/page\r\n<img data-src='wrong.png' src=\"https://example.com/image.png?a=1&amp;b=2\">";
   data=new DataObject(DataFormats.Html,html);capture=DesktopImageDrop.Capture(data);check(capture.Address=="https://example.com/image.png?a=1&b=2"&&capture.Source=="https://example.com/page","HTML real src and source retained without data-src confusion");
   check(!DesktopImageDrop.CanRead(new DataObject(DataFormats.Html,"<p>text only</p>")),"text drag is not hijacked");
   data=new DataObject(DataFormats.Html,"<img src='data:image/png;base64,"+Convert.ToBase64String(png)+"'>");check(DesktopImageDrop.Capture(data).Materialize().Result.SequenceEqual(png),"inline desktop image supported");
   foreach(string address in new[]{"file:///C:/secret.png","blob:https://example.com/abc","https://user:pass@example.com/x","http://127.0.0.1/image.png","http://192.168.1.1/image.png","http://[::1]/image.png"})check(Reject(()=>DesktopImageDrop.DownloadAddress(address)),"unsafe image URL rejected "+address.Split(':')[0]);
   check(DesktopImageDrop.DownloadAddress("https://example.com/image.png").Host=="example.com","public image URL accepted");check(Reject(()=>DesktopImageDrop.ImageExtension(Encoding.UTF8.GetBytes("<html>login</html>"))),"HTML login response never imported as image");
   data=new DataObject(DataFormats.Html,"<img src='blob:https://example.com/private'>");data.SetData("UniformResourceLocatorW","https://example.com/image.png");check(DesktopImageDrop.Capture(data).Address=="https://example.com/image.png","unreadable blob does not mask usable source URL");
   data=new DataObject("PNG",new MemoryStream(png));data.SetData("Mori.AssetIds",new[]{"fixture"});check(!DesktopImageDrop.CanRead(data),"internal Mori asset drag not intercepted");
   var local=new DataObject(DataFormats.FileDrop,new[]{Path.Combine(root,"reference-photo.png")});check(MainWindow.IsLocalFileDrop(local)&&!DesktopImageDrop.CanRead(local),"normal disk images remain shortcuts");
   string temporary=Path.Combine(Path.GetTempPath(),"Mori-desktop-drop-test-"+Guid.NewGuid().ToString("N")+".png");File.WriteAllBytes(temporary,png);
   try{
    var tempData=new DataObject(DataFormats.FileDrop,new[]{temporary});capture=DesktopImageDrop.Capture(tempData);File.Delete(temporary);check(capture.Bytes.SequenceEqual(png)&&!MainWindow.IsLocalFileDrop(tempData),"temporary image survives source deletion as copied bytes");
    using(var lib=new Library(Path.Combine(root,"desktop-drop-library"))){
     Entry.ClassicEnabled=true;var main=new MainWindow(lib){PreviewMode=true};var previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
     try{
      var args=Drop(main,new DataObject("PNG",new MemoryStream(png)));check(args.Handled&&args.Effects==DragDropEffects.Copy&&lib.All().Count==1&&!lib.All()[0].IsReference,"actual window drop imports image as managed copy");
      var item=lib.All()[0];check(File.ReadAllBytes(lib.FullPath(item)).SequenceEqual(png),"actual imported bytes verified");lib.BuildThumbnail(item);check(File.Exists(lib.ThumbnailPath(item))&&ThumbnailImage.ReadThumbnail(lib.ThumbnailPath(item)).PixelWidth>0,"desktop image produces usable gallery thumbnail");
      main.SetSection("bookmarks");args=Drop(main,new DataObject(DataFormats.Html,"SourceURL:https://example.org/fixture\r\n<img src='data:image/png;base64,"+Convert.ToBase64String(png)+"'>"),DragDropEffects.Move);check(args.Handled&&lib.All().Count==1,"move-only drag never removes source or imports");
      args=Drop(main,new DataObject("PNG",new MemoryStream(new byte[]{1,2,3})));check(args.Handled&&lib.All().Count==1&&!((bool)typeof(MainWindow).GetField("busy",Flags).GetValue(main)),"invalid picture gives failure without orphan asset or busy lock");
      var text=(System.Windows.Controls.TextBlock)main.Window.FindName("Status");if(text!=null)check(text.Text.Contains("未收藏"),"failed image status visible");
      var notice=(System.Windows.Controls.Border)typeof(MainWindow).GetField("desktopDropFeedback",Flags).GetValue(main);check(notice.Visibility==Visibility.Visible&&((System.Windows.Controls.TextBlock)notice.Child).Text.Contains("未收藏"),"failure shown beside gallery instead of only tiny status bar");
      string log=File.ReadAllText(Path.Combine(lib.Root,".mori","desktop-drop.log"));check(log.Contains("received")&&log.Contains("saved")&&log.Contains("capture-failed"),"drop phases diagnose actual received versus saved outcome");check(!log.Contains("https:")&&!log.Contains("参考图片")&&!log.Contains("file:///"),"drop diagnostics contain no image paths or source URLs");
     }finally{SynchronizationContext.SetSynchronizationContext(previous);main.Window.Close();Entry.ClassicEnabled=false;ThumbnailImage.CurrentLibrary=null;}
    }
   }finally{DesktopImageDrop.Cleanup(temporary);}
  }
  sealed class FlakyData:System.Windows.IDataObject {
   internal readonly DataObject Inner=new DataObject();internal string Broken;
   public object GetData(string format,bool convert){if(format==Broken)throw new COMException("source declined rendering");return Inner.GetData(format,convert);}
   public object GetData(string format){return GetData(format,true);}public object GetData(Type type){return Inner.GetData(type);}
   public bool GetDataPresent(string format,bool convert){return format==Broken||Inner.GetDataPresent(format,convert);}public bool GetDataPresent(string format){return GetDataPresent(format,true);}public bool GetDataPresent(Type type){return Inner.GetDataPresent(type);}
   public string[] GetFormats(bool convert){return Inner.GetFormats(convert).Concat(new[]{Broken}).ToArray();}public string[] GetFormats(){return GetFormats(true);}
   public void SetData(string format,object value,bool convert){Inner.SetData(format,value,convert);}public void SetData(string format,object value){Inner.SetData(format,value);}public void SetData(Type type,object value){Inner.SetData(type,value);}public void SetData(object value){Inner.SetData(value);}
  }
  sealed class ImageStream:IStream {
   readonly MemoryStream inner;internal ImageStream(byte[] bytes){inner=new MemoryStream(bytes);}
   public void Read(byte[] buffer,int count,IntPtr read){Marshal.WriteInt32(read,inner.Read(buffer,0,count));}
   public void Write(byte[] b,int c,IntPtr p){throw new NotSupportedException();}public void Seek(long p,int o,IntPtr r){throw new NotSupportedException();}public void SetSize(long n){throw new NotSupportedException();}public void CopyTo(IStream s,long c,IntPtr r,IntPtr w){throw new NotSupportedException();}public void Commit(int f){}public void Revert(){throw new NotSupportedException();}public void LockRegion(long a,long b,int c){throw new NotSupportedException();}public void UnlockRegion(long a,long b,int c){throw new NotSupportedException();}public void Stat(out STATSTG s,int f){s=new STATSTG{cbSize=inner.Length};}public void Clone(out IStream s){throw new NotSupportedException();}
  }
 }
}
