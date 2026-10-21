using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;

// Selected pre-existing regressions only. These groups construct offscreen WPF
// controls and synthetic data; no entry point, Show, clipboard or system shell.
internal static class RegressionRunner {
 [STAThread]static int Main(string[] args){
  Console.OutputEncoding=Encoding.UTF8;
  try{
   if(args.Length!=2)throw new ArgumentException("RegressionRunner <Mori.exe> <new-fixture-directory>");
   string app=Path.GetFullPath(args[0]),root=Path.GetFullPath(args[1]),directory=Path.GetDirectoryName(app);
   if(!File.Exists(app)||Directory.Exists(root))throw new IOException("A compiled candidate and a new fixture directory are required.");
   Directory.CreateDirectory(root);
   Environment.SetEnvironmentVariable("MORI_DESK_DATA",Path.Combine(root,"isolated-settings"));
   AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=>{string path=Path.Combine(directory,new AssemblyName(e.Name).Name+".dll");return File.Exists(path)?Assembly.LoadFrom(path):null;};
   var assembly=Assembly.LoadFrom(app);
   var entry=assembly.GetType("MoriDesk.Entry",true);
   entry.GetField("InteractiveShell").SetValue(null,false);
   entry.GetField("BoardEnabled").SetValue(null,false);
   assembly.GetType("Mori.BookmarkIcons",true).GetField("NetworkEnabled",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,false);
   var presentation=Assembly.Load("PresentationFramework, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
   var applicationType=presentation.GetType("System.Windows.Application",true);
   object application=Activator.CreateInstance(applicationType);
   var shutdown=applicationType.GetProperty("ShutdownMode");
   shutdown.SetValue(application,Enum.Parse(shutdown.PropertyType,"OnExplicitShutdown"),null);
   using(var image=new Bitmap(48,32))using(var graphics=Graphics.FromImage(image)){
    graphics.Clear(Color.DarkViolet);graphics.FillRectangle(Brushes.PeachPuff,8,6,20,16);
    image.Save(Path.Combine(root,"reference-photo.png"),System.Drawing.Imaging.ImageFormat.Png);
   }
   int passed=0,failed=0;
   Action<bool,string> check=(ok,message)=>{if(ok)passed++;else failed++;Console.WriteLine((ok?"PASS ":"FAIL ")+message);};
   foreach(string name in new[]{"MoriDesk.LaunchTests","MoriDesk.ClassicTests","MoriDesk.OverviewVisibilityTests","MoriDesk.DesktopDropTests","Mori.MediaBadgeTests"}){
    int beforePass=passed,beforeFail=failed;
    var suite=assembly.GetType(name,true);
    string path=name=="Mori.MediaBadgeTests"?Path.Combine(root,"media-badges"):root;
    suite.GetMethod("Run",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).Invoke(null,new object[]{path,check});
    Console.WriteLine(name+": "+(passed-beforePass)+" passed; "+(failed-beforeFail)+" failed.");
   }
   bool started=(bool)assembly.GetType("MoriDesk.DeskRuntime",true).GetField("Started").GetValue(null);
   check(!started,"offscreen regressions never start capture services, hotkeys, pin restoration or browser bridge");
   Console.WriteLine("Selected classification, routing, menus and drop regressions: "+passed+" passed; "+failed+" failed. Isolated settings, no shown windows or personal materials.");
   return failed==0?0:1;
  }catch(Exception error){var invocation=error as TargetInvocationException;Console.Error.WriteLine(invocation!=null&&invocation.InnerException!=null?invocation.InnerException:error);return 1;}
 }
}
