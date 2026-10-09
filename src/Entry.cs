using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
[assembly: AssemblyTitle("Mori Desk")]
[assembly: AssemblyProduct("Mori Desk")]
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]
namespace MoriDesk {
 public static class Entry {
  public static bool InteractiveShell;
  public static string DataRoot {get{return Environment.GetEnvironmentVariable("MORI_DESK_DATA")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MoriDesk");}}
  [STAThread] public static int Main(string[] args){
   var worker=args.FirstOrDefault(a=>a.StartsWith("--ocr-worker=",StringComparison.Ordinal));
   if(worker!=null)return QingJie.OcrWorker.Execute(worker.Substring(13));
   if(args.Length>0&&args[0]=="--suite-test")return SuiteTests.Run();
   if(args.Length>0&&args[0]=="--integration-test")return IntegrationTests.Run();
   if(args.Length>0&&args[0]=="--model")return ModelWindow.Run(args.Length>1?args[1]:null);
   if(args.Length>0&&args[0]=="--image")return ImageBrowser.Run(args.Length>1?args[1]:null);
   InteractiveShell=args.Length==0||new[]{"--ui-test","--library","--startup"}.Contains(args[0]);return Mori.Program.Main(args);
  }
 }
}
