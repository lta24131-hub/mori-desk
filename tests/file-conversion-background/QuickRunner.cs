using System;
using System.IO;
using System.Reflection;
using System.Text;

// Actual hidden conversion worker + synthetic database only. The application
// assembly is loaded for its classes; its entry point and UI are never run.
internal static class QuickRunner {
 [STAThread]static int Main(string[] args){
  Console.OutputEncoding=Encoding.UTF8;
  try{
   if(args.Length!=3)throw new ArgumentException("QuickRunner <Mori.exe> <worker.exe> <new-fixture-directory>");
   string app=Path.GetFullPath(args[0]),worker=Path.GetFullPath(args[1]),root=Path.GetFullPath(args[2]),directory=Path.GetDirectoryName(app);
   if(!File.Exists(app)||!File.Exists(worker)||Directory.Exists(root))throw new IOException("Actual candidates and a fresh fixture directory are required.");
   AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=>{string path=Path.Combine(directory,new AssemblyName(e.Name).Name+".dll");return File.Exists(path)?Assembly.LoadFrom(path):null;};
   int passed=0,failed=0;
   Action<bool,string> check=(ok,message)=>{if(ok)passed++;else failed++;Console.WriteLine((ok?"PASS ":"FAIL ")+message);};
   var assembly=Assembly.LoadFrom(app);var tests=assembly.GetType("MoriDesk.QuickConversionTests",true);
   tests.GetMethod("RunReal",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{worker,root,check});
   Console.WriteLine("Quick real-worker closed-loop: "+passed+" passed; "+failed+" failed. Synthetic files and database only.");
   return failed==0?0:1;
  }catch(Exception error){var invocation=error as TargetInvocationException;Console.Error.WriteLine(invocation!=null&&invocation.InnerException!=null?invocation.InnerException:error);return 1;}
 }
}
