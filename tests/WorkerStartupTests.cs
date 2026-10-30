using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Mori;

namespace MoriDesk {
 internal static class WorkerStartupTests {
  sealed class Result {internal bool Exited;internal int ExitCode;internal byte[] Output;}
  static Result RunWorker(string arguments,string data){
   var start=new ProcessStartInfo(typeof(Entry).Assembly.Location,arguments){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true};
   start.EnvironmentVariables["MORI_DESK_DATA"]=data;
   using(var child=Process.Start(start)){
    var read=Task.Run(delegate{using(var output=new MemoryStream()){byte[] buffer=new byte[8192];int n;while((n=child.StandardOutput.BaseStream.Read(buffer,0,buffer.Length))>0){if(output.Length+n>1024*1024)throw new InvalidDataException("Small worker fixture exceeded output budget.");output.Write(buffer,0,n);}return output.ToArray();}});
    bool exited=false;
    try{exited=child.WaitForExit(15000);if(!exited)return new Result{Exited=false};return new Result{Exited=true,ExitCode=child.ExitCode,Output=read.GetAwaiter().GetResult()};}
    finally{if(!exited&&!child.HasExited){child.Kill();child.WaitForExit(2000);}try{read.Wait(2000);}catch(AggregateException){}}
   }
  }
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);string blocker=Path.Combine(root,"configuration-is-a-file");File.WriteAllText(blocker,"must stay unchanged");
   string inaccessible=Path.Combine(blocker,"MoriDesk"),source=Path.Combine(root,"透明 形状.svg");
   string svg="<svg xmlns='http://www.w3.org/2000/svg' width='100' height='50'><rect x='10' y='10' width='30' height='20' fill='#ff0000'/></svg>";File.WriteAllText(source,svg);DateTime stamp=File.GetLastWriteTimeUtc(source);
   var valid=RunWorker("--svg-render \""+source+"\" 100",inaccessible);
   check(valid.Exited,"SVG worker exits without trying to display a configuration error dialog");
   if(valid.Exited){
    check(valid.ExitCode==0&&valid.Output.Length>16,"SVG worker renders with an inaccessible application configuration path");
    if(valid.ExitCode==0&&valid.Output.Length>16){
     check(BitConverter.ToInt32(valid.Output,0)==100&&BitConverter.ToInt32(valid.Output,4)==50,"worker keeps the existing dimension header and source size contract");
     using(var stream=new MemoryStream(valid.Output,8,valid.Output.Length-8))using(var image=new System.Drawing.Bitmap(stream)){
      check(image.Width==100&&image.Height==50,"SVG worker produces the requested bounded preview");
      check(image.GetPixel(0,0).A==0&&image.GetPixel(20,20).R==255&&image.GetPixel(20,20).A==255,"worker preserves transparency and real shape pixels");
     }
    }
   }
   foreach(string args in new[]{"--svg-render","--svg-render \""+source+"\" 0","--svg-render \""+source+"\" 2049"}){
    var invalid=RunWorker(args,inaccessible);check(invalid.Exited&&invalid.ExitCode==2&&invalid.Output.Length==0,"invalid worker input returns failure without opening the main application / "+args.Split(' ').Last());
   }
   check(File.ReadAllText(blocker)=="must stay unchanged"&&!Directory.Exists(inaccessible),"media worker neither initializes nor rewrites application configuration");
   check(File.ReadAllText(source)==svg&&File.GetLastWriteTimeUtc(source)==stamp,"worker startup leaves the original file bytes and timestamp intact");
   string fresh=Path.Combine(root,"absent-profile");var absent=RunWorker("--svg-render \""+source+"\" 100",fresh);
   check(absent.Exited&&absent.ExitCode==0&&!Directory.Exists(fresh),"SVG rendering does not create an otherwise unused profile directory");
  }
 }
}
