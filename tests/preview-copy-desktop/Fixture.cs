// Explicit manual desktop fixture. Compiling it does not run any UI or access
// the clipboard. Only the visible paste buttons read the live clipboard.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class PreviewCopyDesktopFixture {
 const string Marker=".nook-preview-copy-desktop";
 static string root,build,png;
 static Application app;
 static Window host;
 static TextBlock status;
 static Image pasted;
 static BitmapSource original;
 static readonly HashSet<Window> owned=new HashSet<Window>();
 static bool stopping;
 [STAThread]static int Main(string[] args){
  if(args.Length!=2||args[0]!="--manual-desktop-acceptance")return 2;
  root=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);build=Path.GetFullPath(args[1]);
  if(!File.Exists(Path.Combine(root,Marker))||!File.Exists(Path.Combine(build,"Mori.exe")))return 2;
  Environment.SetEnvironmentVariable("MORI_DESK_DATA",Path.Combine(root,"data"));
  AppDomain.CurrentDomain.AssemblyResolve+=(sender,eventArgs)=>{string name=new AssemblyName(eventArgs.Name).Name;string file=Path.Combine(build,name=="Mori"?"Mori.exe":name+".dll");return File.Exists(file)?Assembly.LoadFrom(file):null;};
  try{return Run();}catch(Exception error){Log("fatal "+error);return 1;}
 }
 [MethodImpl(MethodImplOptions.NoInlining)]static int Run(){
  Directory.CreateDirectory(Path.Combine(root,"data"));original=Sample();png=Path.Combine(root,"synthetic-alpha-640x360.png");Save(original,png);
  app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};Mori.Theme.Set("light");
  Log("start pid="+System.Diagnostics.Process.GetCurrentProcess().Id+" candidate="+Path.Combine(build,"Mori.exe")+" version="+typeof(Mori.PreviewWindow).Assembly.GetName().Version);
  var body=new DockPanel{Margin=new Thickness(18)};var top=new StackPanel();DockPanel.SetDock(top,Dock.Top);body.Children.Add(top);
  top.Children.Add(new TextBlock{Text="Nook · 图片复制验收",FontSize=23,FontWeight=FontWeights.SemiBold});
  top.Children.Add(new TextBlock{Text="只使用生成的透明 PNG，不连接真实图库。打开任一预览 → 放大/拖动 → 右键“复制图片”或 Ctrl+C → 回到这里点击粘贴验证。也可先关闭预览再粘贴，检查剪贴板持久性。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,9,0,10)});
  var bar=new WrapPanel();top.Children.Add(bar);
  Button(bar,"1 素材库预览",OpenLibrary);Button(bar,"2 独立看图",OpenViewer);Button(bar,"3 粘贴 PNG 并验证",()=>Paste(true));Button(bar,"4 粘贴 Bitmap 兼容格式",()=>Paste(false));Button(bar,"关闭自有预览",CloseOwned);Button(bar,"结束测试",Stop);
  status=new TextBlock{Text="等待操作；本测试启动和退出都不会读取或清空剪贴板。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,12),FontSize=13};top.Children.Add(status);
  var grid=new Grid();grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition());
  grid.Children.Add(Panel("生成的原图 · 640 × 360 · 透明 / 半透明 / 不透明",original,0,out pasted));Image result;grid.Children.Add(Panel("实际粘贴结果",null,1,out result));pasted=result;body.Children.Add(grid);
  host=new Window{Title="Nook · 图片复制验收",Width=1120,Height=620,MinWidth=800,MinHeight=450,Background=Brushes.White,Content=body,WindowStartupLocation=WindowStartupLocation.CenterScreen};
  host.Closing+=(s,e)=>{if(stopping)return;stopping=true;CloseOwned();};host.Closed+=(s,e)=>app.Shutdown();app.DispatcherUnhandledException+=(s,e)=>{Log("dispatcher-error "+e.Exception);status.Text="测试窗口发生异常，详见 events.txt："+e.Exception.Message;e.Handled=true;};
  app.Exit+=(s,e)=>Log("exit "+e.ApplicationExitCode);return app.Run(host);
 }
 static void Button(Panel panel,string text,Action action){var button=new Button{Content=text,Padding=new Thickness(12,7,12,7),Margin=new Thickness(0,0,8,7)};button.Click+=(s,e)=>{try{action();}catch(Exception error){status.Text="未完成："+error.Message;Log("action-error "+text+" "+error);}};panel.Children.Add(button);}
 static Border Panel(string caption,BitmapSource source,int column,out Image image){
  var body=new DockPanel{Margin=new Thickness(8)};var title=new TextBlock{Text=caption,Margin=new Thickness(5,0,5,8),TextWrapping=TextWrapping.Wrap};DockPanel.SetDock(title,Dock.Top);body.Children.Add(title);image=new Image{Source=source,Stretch=Stretch.Uniform,Margin=new Thickness(8)};
  var drawing=new DrawingGroup();drawing.Children.Add(new GeometryDrawing(Brushes.White,null,new RectangleGeometry(new Rect(0,0,24,24))));drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(220,223,228)),null,new RectangleGeometry(new Rect(0,0,12,12))));drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(220,223,228)),null,new RectangleGeometry(new Rect(12,12,12,12))));var checker=new DrawingBrush(drawing){TileMode=TileMode.Tile,Viewport=new Rect(0,0,24,24),ViewportUnits=BrushMappingMode.Absolute};checker.Freeze();body.Children.Add(new Border{Background=checker,Child=image});var panel=new Border{BorderBrush=Brushes.LightGray,BorderThickness=new Thickness(1),Margin=new Thickness(4),Child=body};Grid.SetColumn(panel,column);return panel;
 }
 static BitmapSource Sample(){const int width=640,height=360;var pixels=new byte[width*height*4];for(int y=0;y<height;y++)for(int x=0;x<width;x++){int i=(y*width+x)*4;if(x<80||y<30||y>=330)continue;if(x<260){pixels[i]=210;pixels[i+1]=80;pixels[i+2]=35;pixels[i+3]=128;}else if(x<440){pixels[i]=35;pixels[i+1]=135;pixels[i+2]=235;pixels[i+3]=255;}else{pixels[i]=180;pixels[i+1]=55;pixels[i+2]=100;pixels[i+3]=255;}}var image=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);image.Freeze();return image;}
 static void Track(Window window,string kind){owned.Add(window);window.Closed+=(s,e)=>{owned.Remove(window);Log("closed "+kind);};Log("opened "+kind);}
 static void OpenLibrary(){var before=new HashSet<Window>(app.Windows.Cast<Window>());Mori.PreviewWindow.Images(host,new[]{png},new[]{"透明 PNG · 复制验收"});foreach(var window in app.Windows.Cast<Window>().Where(w=>!before.Contains(w)).ToArray())Track(window,"production-library-preview");status.Text="已打开真实素材库图片预览，请右键复制或 Ctrl+C。";}
 static void OpenViewer(){var window=new MoriDesk.ImageBrowser(png){Owner=host};Track(window,"production-image-browser");window.Show();status.Text="已打开真实独立看图，请右键复制或 Ctrl+C。";}
 static void CloseOwned(){foreach(var window in owned.ToArray())try{window.Close();}catch(Exception error){Log("close-owned-error "+error.Message);}}
 static void Stop(){if(stopping)return;stopping=true;CloseOwned();host.Close();}
 static byte[] Bytes(BitmapSource source){var converted=source.Format==PixelFormats.Bgra32?source:new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);var pixels=new byte[checked(source.PixelWidth*source.PixelHeight*4)];converted.CopyPixels(pixels,source.PixelWidth*4,0);return pixels;}
 static string Digest(byte[] bytes){using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","");}
 static void Paste(bool preferPng){
  // Deliberately no Clipboard.Clear/Set: copying is exercised exclusively via
  // the real production preview menus and keyboard handlers.
  var data=Clipboard.GetDataObject();if(data==null)throw new InvalidOperationException("剪贴板没有内容。");Log("manual-paste requested="+(preferPng?"PNG":"Bitmap")+" formats="+string.Join(",",data.GetFormats(false)));
  BitmapSource bitmap;
  if(preferPng){if(!data.GetDataPresent("PNG",false))throw new InvalidOperationException("剪贴板没有 PNG 格式；请先在测试预览内复制图片。");var value=data.GetData("PNG",false);byte[] encoded=value as byte[];var input=value as Stream;if(encoded==null&&input!=null){if(input.CanSeek){if(input.Length>64L*1024*1024)throw new InvalidDataException("测试 PNG 超过 64 MB。");input.Position=0;}using(var copy=new MemoryStream()){var buffer=new byte[8192];int count;while((count=input.Read(buffer,0,buffer.Length))>0){if(copy.Length+count>64L*1024*1024)throw new InvalidDataException("测试 PNG 超过 64 MB。");copy.Write(buffer,0,count);}encoded=copy.ToArray();}}if(encoded==null)throw new InvalidDataException("无法读取 PNG 剪贴板类型。");using(var stream=new MemoryStream(encoded,false)){var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.None);var frame=decoder.Frames[0];if(frame.PixelWidth<1||frame.PixelHeight<1||(long)frame.PixelWidth*frame.PixelHeight>32L*1024*1024)throw new InvalidDataException("测试图片尺寸超限。");bitmap=new WriteableBitmap(frame);bitmap.Freeze();}}
  else{bitmap=data.GetData(DataFormats.Bitmap)as BitmapSource;if(bitmap==null)throw new InvalidDataException("剪贴板没有可读的 Bitmap 图片。");if((long)bitmap.PixelWidth*bitmap.PixelHeight>32L*1024*1024)throw new InvalidDataException("测试图片尺寸超限。");bitmap=new WriteableBitmap(bitmap);bitmap.Freeze();}
  pasted.Source=bitmap;var bytes=Bytes(bitmap);bool dimensions=bitmap.PixelWidth==640&&bitmap.PixelHeight==360;bool exact=dimensions&&bytes.SequenceEqual(Bytes(original));string text=(preferPng?"PNG":"Bitmap")+"："+bitmap.PixelWidth+" × "+bitmap.PixelHeight+"；尺寸 "+(dimensions?"PASS":"FAIL")+"；完整 RGBA "+(exact?"PASS":"不同（Bitmap 兼容接收方可能不保透明度）");status.Text=text;
  string file=Path.Combine(root,"pasted-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+(preferPng?"-PNG":"-Bitmap")+".png");Save(bitmap,file);Log(text+" pixel-sha256="+Digest(bytes)+" file="+Path.GetFileName(file));File.WriteAllText(Path.Combine(root,"last-result.txt"),text+Environment.NewLine+"pixel-sha256="+Digest(bytes),Encoding.UTF8);
 }
 static void Save(BitmapSource image,string file){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var stream=File.Create(file))encoder.Save(stream);}
 static void Log(string value){try{File.AppendAllText(Path.Combine(root,"events.txt"),DateTime.UtcNow.ToString("o")+" "+value+Environment.NewLine,Encoding.UTF8);}catch{}}
}
