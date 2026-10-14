using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
[assembly: AssemblyTitle("Nook")]
[assembly: AssemblyProduct("Nook")]
[assembly: AssemblyVersion("0.1.22.0")]
[assembly: AssemblyFileVersion("0.1.22.0")]
namespace MoriDesk {
 public static class Entry {
  public static bool InteractiveShell;
  public static bool BoardEnabled;
  public static bool ClassicEnabled;
  public static string DataRoot {get{return Environment.GetEnvironmentVariable("MORI_DESK_DATA")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MoriDesk");}}
  [STAThread] public static int Main(string[] args){
   if(args.Length>0&&args[0]=="--image-codec")return Mori.ImageCodec.Worker(args);
   if(args.Length>0&&args[0]=="--pdf-text-worker")return Mori.PdfTextWorker.Run(args);
   var worker=args.FirstOrDefault(a=>a.StartsWith("--ocr-worker=",StringComparison.Ordinal));
   if(worker!=null)return QingJie.OcrWorker.Execute(worker.Substring(13));
   if(args.Length>0&&args[0]=="--suite-test")return SuiteTests.Run();
   if(args.Length==2&&args[0]=="--desktop-drop-fixture")return DesktopDropFixture.Run(args[1]);
   if(args.Length>0&&args[0]=="--integration-test")return IntegrationTests.Run();
   if(args.Length==3&&args[0]=="--browser-bridge-test")return BrowserBridgeFixture.Run(args[1],args[2]);
   if(args.Length==2&&args[0]=="--browser-update-host-test")return BrowserUpdateHostFixture.Run(args[1]);
   if(args.Length>0&&args[0]=="--register-extension"){try{if(args.Length!=2)throw new ArgumentException("需要提供扩展 ID");BrowserConnection.Register(args[1]);return 0;}catch(Exception ex){MessageBox.Show(ex.Message,"浏览器连接未完成");return 1;}}
   LaunchRequest request;
   try{request=LaunchRequest.Parse(args);}catch(Exception ex){MessageBox.Show(ex.Message,"Nook · 无法打开",MessageBoxButton.OK,MessageBoxImage.Information);return 1;}
   if(request.Mode=="image")return ImageBrowser.Run(request.Path);
   if(request.Mode=="model")return ModelWindow.Run(request.Path);
   if(request.Mode=="preview")return FilePreviewHost.Run(request.Path);
   InteractiveShell=request.Mode=="library";ClassicEnabled=InteractiveShell||args.Contains("--ui-test");return Mori.Program.Main(args);
  }
 }
}
