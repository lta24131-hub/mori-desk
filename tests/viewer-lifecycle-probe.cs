// Standalone synthetic-only probe. Compile with System.Drawing.dll and run with
// <candidate Mori.exe> <isolated output folder>. No production files/settings.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MoriDesk.Diagnostics {
 public static class ViewerLifecycleProbe {
  sealed class Sample {
   internal string Name;internal long Managed,Private,WorkingSet;internal int Handles;
   internal static Sample Read(string name){using(var process=Process.GetCurrentProcess()){process.Refresh();return new Sample{Name=name,Managed=GC.GetTotalMemory(false),Private=process.PrivateMemorySize64,WorkingSet=process.WorkingSet64,Handles=process.HandleCount};}}
   public override string ToString(){return Name+": managed="+Managed+", private="+Private+", working-set="+WorkingSet+", handles="+Handles;}
  }
  static object Open(Type preview,string file,bool background){if(!background)return preview.GetMethod("Open",BindingFlags.Public|BindingFlags.Static).Invoke(null,new object[]{file});var task=(Task)preview.GetMethod("OpenAsync",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{file,CancellationToken.None});task.GetAwaiter().GetResult();return task.GetType().GetProperty("Result").GetValue(task,null);}
  static void Exercise(Type preview,Type picture,Type viewport,string file,bool background){
   var loaded=Open(preview,file,background);
   try{var image=(Image)preview.GetProperty("Image").GetValue(loaded,null);object view=Activator.CreateInstance(viewport,new object[]{(double)image.Width,(double)image.Height});viewport.GetMethod("Resize").Invoke(view,new object[]{320.0,200.0});
    using(var surface=(System.Windows.Forms.Control)Activator.CreateInstance(picture,BindingFlags.Instance|BindingFlags.Public,null,new object[]{image,view},null))using(var output=new Bitmap(320,200)){
     surface.Size=new Size(320,200);surface.DrawToBitmap(output,new Rectangle(0,0,320,200));
    }
   }finally{((IDisposable)loaded).Dispose();}
   using(var exclusive=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None))if(exclusive.Length<1)throw new Exception("Empty fixture");
  }
  static void Collect(){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();}
  [STAThread] public static int Main(string[] args){
   if(args.Length<2||args.Length>3){Console.Error.WriteLine("Usage: viewer-lifecycle-probe.exe <candidate Mori.exe> <isolated output folder> [cycles=3, 12 previews each]");return 2;}
   try{
    int cycles=args.Length==3?int.Parse(args[2]):3;if(cycles<1||cycles>40)throw new ArgumentOutOfRangeException("cycles");string binary=Path.GetFullPath(args[0]),output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
    var assembly=Assembly.LoadFrom(binary);var preview=assembly.GetType("Mori.PreviewImage",true);var picture=assembly.GetType("Mori.PreviewPicture",true);var viewport=assembly.GetType("Mori.PreviewViewport",true);bool background=preview.GetMethod("OpenAsync",BindingFlags.NonPublic|BindingFlags.Static)!=null;
    string imageFile=Path.Combine(output,"synthetic-16mp.png");using(var bitmap=new Bitmap(4096,4096)){
     using(var graphics=Graphics.FromImage(bitmap))using(var gradient=new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0,0,4096,4096),Color.FromArgb(31,43,53),Color.FromArgb(169,192,206),43)){
      graphics.FillRectangle(gradient,0,0,4096,4096);for(int i=0;i<200;i++)graphics.DrawLine(Pens.Gainsboro,0,i*20,4095,4095-i*20);
     }bitmap.Save(imageFile,ImageFormat.Png);
    }
    string gifFile=Path.Combine(output,"synthetic-animated.gif");File.WriteAllBytes(gifFile,new byte[]{71,73,70,56,57,97,1,0,1,0,128,0,0,255,0,0,0,0,255,33,255,11,78,69,84,83,67,65,80,69,50,46,48,3,1,0,0,0,33,249,4,4,10,0,0,0,44,0,0,0,0,1,0,1,0,0,2,2,68,1,0,33,249,4,4,10,0,0,0,44,0,0,0,0,1,0,1,0,0,2,2,76,1,0,59});
    var report=new List<string>{"Candidate: "+binary,"Synthetic only; lifecycle regressions may show their own non-activating offscreen windows. No original media or production UI used.","Counters are process samples, not an inference of leaks. Private/working-set can retain native decoder caches.","Background decoder: "+background,"Fixture: 4096x4096 PNG, "+new FileInfo(imageFile).Length+" bytes"};
    int checks=0;Action<bool,string> check=delegate(bool ok,string name){if(!ok)throw new Exception("FAIL: "+name);checks++;};
    assembly.GetType("Mori.PreviewImageTests",true).GetMethod("Run").Invoke(null,new object[]{Path.Combine(output,"decoder-regressions"),check});
    assembly.GetType("Mori.PreviewRenderingTests",true).GetMethod("Run").Invoke(null,new object[]{check});report.Add("PASS: "+checks+" decoder/rendering lifecycle regressions");
    Exercise(preview,picture,viewport,imageFile,background);Exercise(preview,picture,viewport,gifFile,background);Collect();report.Add(Sample.Read("warm baseline").ToString());
    var calls=new List<double>();var elapsed=new List<double>();
    for(int cycle=0;cycle<cycles;cycle++){
     for(int i=0;i<12;i++){
      var clock=Stopwatch.StartNew();object loaded;
      if(background){var task=(Task)preview.GetMethod("OpenAsync",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{imageFile,CancellationToken.None});calls.Add(clock.Elapsed.TotalMilliseconds);task.GetAwaiter().GetResult();loaded=task.GetType().GetProperty("Result").GetValue(task,null);}
      else{loaded=Open(preview,imageFile,false);calls.Add(clock.Elapsed.TotalMilliseconds);}
      elapsed.Add(clock.Elapsed.TotalMilliseconds);((IDisposable)loaded).Dispose();Exercise(preview,picture,viewport,gifFile,background);
     }
     Collect();report.Add(Sample.Read("after "+((cycle+1)*12)+" large image + GIF decode/dispose pairs").ToString());
    }
    using(var exclusive=new FileStream(imageFile,FileMode.Open,FileAccess.ReadWrite,FileShare.None))report.Add("PASS: repeated PNG previews release original handle");
    using(var exclusive=new FileStream(gifFile,FileMode.Open,FileAccess.ReadWrite,FileShare.None))report.Add("PASS: repeated GIF previews release original handle");
    calls.Sort();elapsed.Sort();report.Add("Median caller-block time (not full render): "+calls[calls.Count/2].ToString("F3")+" ms");report.Add("Median awaited open time: "+elapsed[elapsed.Count/2].ToString("F3")+" ms");
    File.WriteAllLines(Path.Combine(output,"viewer-lifecycle-report.txt"),report,Encoding.UTF8);foreach(var line in report)Console.WriteLine(line);return 0;
   }catch(Exception error){Console.Error.WriteLine(error);return 1;}
  }
 }
}
