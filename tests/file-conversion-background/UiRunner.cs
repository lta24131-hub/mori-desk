using System;
using System.IO;
using System.Reflection;
using System.Text;

// This runner loads tests only. It never invokes the application's entry point,
// creates a Window/Application, shows a picker, or starts a conversion worker.
internal static class UiRunner {
 [STAThread]
 private static int Main(string[] args) {
  Console.OutputEncoding=Encoding.UTF8;
  if(args.Length!=2){Console.Error.WriteLine("Usage: UiRunner <Mori.exe> <isolated-fixture-directory>");return 2;}
  try {
   string app=Path.GetFullPath(args[0]),fixtures=Path.GetFullPath(args[1]);
   if(!File.Exists(app))throw new FileNotFoundException("Compiled test assembly was not found.",app);
   if(Directory.Exists(fixtures)&&Directory.GetFileSystemEntries(fixtures).Length>0)throw new IOException("Use a new, empty fixture directory.");
   string directory=Path.GetDirectoryName(app);
   AppDomain.CurrentDomain.AssemblyResolve+=delegate(object sender,ResolveEventArgs eventArgs){
    string dependency=Path.Combine(directory,new AssemblyName(eventArgs.Name).Name+".dll");
    return File.Exists(dependency)?Assembly.LoadFrom(dependency):null;
   };
   var assembly=Assembly.LoadFrom(app);
   var suite=assembly.GetType("MoriDesk.FileConversionUiTests",true);
   var run=suite.GetMethod("Run",BindingFlags.Static|BindingFlags.NonPublic);
   if(run==null)throw new MissingMethodException(suite.FullName,"Run");
   int passed=0,failed=0;
   Action<bool,string> check=(ok,message)=>{if(ok)passed++;else failed++;Console.WriteLine((ok?"PASS ":"FAIL ")+message);};
   run.Invoke(null,new object[]{fixtures,check});
   Console.WriteLine("Legacy UI group: "+passed+" passed; "+failed+" failed.");
   int previousPassed=passed,previousFailed=failed;
   var collectionSuite=assembly.GetType("MoriDesk.FileConversionCollectionTests",true);
   collectionSuite.GetMethod("Run",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{fixtures,check});
   Console.WriteLine("Collection and host group: "+(passed-previousPassed)+" passed; "+(failed-previousFailed)+" failed.");
   previousPassed=passed;previousFailed=failed;
   var quickSuite=assembly.GetType("MoriDesk.QuickConversionTests",true);
   quickSuite.GetMethod("Run",BindingFlags.Static|BindingFlags.NonPublic,null,new[]{typeof(string),typeof(Action<bool,string>)},null).Invoke(null,new object[]{fixtures,check});
   Console.WriteLine("Quick conversion group: "+(passed-previousPassed)+" passed; "+(failed-previousFailed)+" failed.");
   Console.WriteLine("Conversion UI and collection checks: "+passed+" passed; "+failed+" failed. No windows or conversion engine started.");
   return failed==0?0:1;
  }catch(Exception error){
   var invocation=error as TargetInvocationException;
   Console.Error.WriteLine((invocation!=null&&invocation.InnerException!=null?invocation.InnerException:error).ToString());
   return 1;
  }
 }
}
