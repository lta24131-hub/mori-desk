using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

// Loads isolated settings/update tests, never Nook's entry point or a window.
internal static class StorageRunner {
 [STAThread]
 static int Main(string[] args){
  Console.OutputEncoding=Encoding.UTF8;
  if(args.Length!=2){Console.Error.WriteLine("Usage: StorageRunner <Mori.exe> <fresh-isolated-fixture-directory>");return 2;}
  try{
   string app=Path.GetFullPath(args[0]),fixtures=Path.GetFullPath(args[1]);
   if(!File.Exists(app))throw new FileNotFoundException("Compiled test assembly was not found.",app);
   if(Directory.Exists(fixtures)&&Directory.GetFileSystemEntries(fixtures).Length>0)throw new IOException("Use a new, empty fixture directory.");
   string directory=Path.GetDirectoryName(app);
   Environment.SetEnvironmentVariable("MORI_DESK_DATA",Path.Combine(fixtures,"unused-app-data"));
   AppDomain.CurrentDomain.AssemblyResolve+=delegate(object sender,ResolveEventArgs eventArgs){
    string dependency=Path.Combine(directory,new AssemblyName(eventArgs.Name).Name+".dll");
    return File.Exists(dependency)?Assembly.LoadFrom(dependency):null;
   };
   var assembly=Assembly.LoadFrom(app);
   int passed=0,failed=0;
   Action<bool,string> check=(ok,message)=>{if(ok)passed++;else failed++;Console.WriteLine((ok?"PASS ":"FAIL ")+message);};
   var storage=assembly.GetType("Mori.StorageSettingsTests",true);
   var checks=(List<string>)storage.GetMethod("Run",BindingFlags.Static|BindingFlags.Public).Invoke(null,new object[]{Path.Combine(fixtures,"storage")});
   foreach(string line in checks){passed++;Console.WriteLine(line);}
   Console.WriteLine("Storage settings group: "+checks.Count+" passed.");
   int previousPassed=passed,previousFailed=failed;
   var update=assembly.GetType("MoriDesk.AppUpdateCoordinatorTests",true);
   update.GetMethod("Run",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{Path.Combine(fixtures,"update"),check});
   Console.WriteLine("Update metadata and coordinator group: "+(passed-previousPassed)+" passed; "+(failed-previousFailed)+" failed.");
   Console.WriteLine("Storage preference checks: "+passed+" passed; "+failed+" failed. No windows, personal settings or real library used.");
   return failed==0?0:1;
  }catch(Exception error){
   var invocation=error as TargetInvocationException;
   Console.Error.WriteLine((invocation!=null&&invocation.InnerException!=null?invocation.InnerException:error).ToString());
   return 1;
  }
 }
}
