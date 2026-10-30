using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
[assembly: AssemblyTitle("Nook")]
[assembly: AssemblyProduct("Nook")]
[assembly: AssemblyVersion("0.1.35.0")]
[assembly: AssemblyFileVersion("0.1.35.0")]
namespace MoriDesk {
 public static class Entry {
  public static bool InteractiveShell;
  public static bool BoardEnabled;
  public static bool ClassicEnabled;
  public static string DataRoot {get{return PhysicalUserContext.DataRoot();}}
  [STAThread] public static int Main(string[] args){
   if(args.Length>0&&args[0]=="--svg-render")return Mori.SvgRaster.Worker(args);
   if(args.Length>0&&args[0]=="--image-codec")return Mori.ImageCodec.Worker(args);
   if(args.Length>0&&args[0]=="--video-thumbnail")return Mori.VideoThumbnail.Worker(args);
   if(args.Length>0&&args[0]=="--pdf-text-worker")return Mori.PdfTextWorker.Run(args);
   if(args.Length>0&&args[0]=="--pdf-render-worker")return Mori.PdfRenderWorker.Run(args);
   var worker=args.FirstOrDefault(a=>a.StartsWith("--ocr-worker=",StringComparison.Ordinal));
   if(worker!=null)return QingJie.OcrWorker.Execute(worker.Substring(13));
   return RunApplication(args);
  }
  // Media workers use only their explicit input/output. Keep shell/configuration
  // probes and WPF application startup out of their short-lived processes.
  [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
  static int RunApplication(string[] args){
   if(args.Length>0&&args[0]=="--suite-test")return SuiteTests.Run();
   if(args.Length==2&&args[0]=="--mature-canvas-test")return MatureCanvasFixture.Run(args[1]);
   if(args.Length==2&&args[0]=="--mature-canvas-preview"){
    try{var app=new Application();app.ShutdownMode=ShutdownMode.OnMainWindowClose;Mori.Theme.Set("light");var window=new MatureCanvasWindow(args[1]);window.Title="Nook · 成熟画布测试";app.Run(window);return 0;}
    catch(Exception error){MessageBox.Show(error.Message,"画布测试未启动");return 1;}
   }
   if(args.Length==2&&args[0]=="--desktop-drop-fixture")return DesktopDropFixture.Run(args[1]);
   if(args.Length>0&&args[0]=="--integration-test")return IntegrationTests.Run();
   if(args.Length==3&&args[0]=="--browser-bridge-test")return BrowserBridgeFixture.Run(args[1],args[2]);
   if(args.Length==2&&args[0]=="--browser-update-host-test")return BrowserUpdateHostFixture.Run(args[1]);
   // Native messaging owns stdin/stdout and reports a failed context through
   // its normal JSON response. Never show a dialog or relaunch that pipe.
   bool native=args.Length>0&&args[0].StartsWith("chrome-extension://",StringComparison.Ordinal);
   try{int routedExit;if(InstalledApplicationRouting.TryRoute(args,out routedExit))return routedExit;}
   catch(Exception ex){if(native){try{InstalledApplicationRouting.WriteNativeFailure(Console.OpenStandardOutput());}catch{}return 1;}MessageBox.Show(ex.Message,"Nook · 安装状态异常",MessageBoxButton.OK,MessageBoxImage.Warning);return 1;}
   if(!native){try{string verifiedRoot=DataRoot;}catch(Exception ex){MessageBox.Show(ex.Message,"Nook · 启动环境异常",MessageBoxButton.OK,MessageBoxImage.Error);return 1;}}
   if(args.Length>0&&args[0]=="--register-extension"){try{if(args.Length!=2)throw new ArgumentException("需要提供扩展 ID");BrowserConnection.Register(args[1]);return 0;}catch(Exception ex){MessageBox.Show(ex.Message,"浏览器连接未完成");return 1;}}
   LaunchRequest request;
   try{request=LaunchRequest.Parse(args);}catch(Exception ex){MessageBox.Show(ex.Message,"Nook · 无法打开",MessageBoxButton.OK,MessageBoxImage.Information);return 1;}
   if(request.Mode=="image")return ImageBrowser.Run(request.Path);
   if(request.Mode=="model")return ModelWindow.Run(request.Path);
   if(request.Mode=="preview")return FilePreviewHost.Run(request.Path);
   InteractiveShell=request.Mode=="library";ClassicEnabled=InteractiveShell||args.Contains("--ui-test");
   if(InteractiveShell&&!args.Contains("--ui-test")&&!InstalledApplicationRouting.IsPendingCandidate)OpenWithRegistration.TryRepairCurrentInstallation();
   return Mori.Program.Main(args);
  }
 }
}
