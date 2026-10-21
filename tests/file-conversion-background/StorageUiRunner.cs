using System;
using System.IO;
using System.Reflection;
using System.Text;

// No entry point, Application, native window, picker or real settings path.
internal static class StorageUiRunner {
 [STAThread]static int Main(string[] args){
  Console.OutputEncoding=Encoding.UTF8;
  try{
   if(args.Length!=2)throw new ArgumentException("StorageUiRunner <Mori.exe> <new-fixture-directory>");
   string app=Path.GetFullPath(args[0]),root=Path.GetFullPath(args[1]),directory=Path.GetDirectoryName(app);
   if(!File.Exists(app)||Directory.Exists(root))throw new IOException("A compiled candidate and a new fixture directory are required.");
   Environment.SetEnvironmentVariable("MORI_DESK_DATA",Path.Combine(root,"isolated-user-data"));
   Environment.SetEnvironmentVariable("NOOK_UPDATE_REQUEST",null);
   Environment.SetEnvironmentVariable("NOOK_UPDATE_EXPECTED_VERSION",null);
   AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=>{string path=Path.Combine(directory,new AssemblyName(e.Name).Name+".dll");return File.Exists(path)?Assembly.LoadFrom(path):null;};
   int passed=0,failed=0;
   Action<bool,string> check=(ok,message)=>{if(ok)passed++;else failed++;Console.WriteLine((ok?"PASS ":"FAIL ")+message);};
   Assembly.LoadFrom(app).GetType("MoriDesk.ConversionStorageUiTests",true).GetMethod("Run",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{root,check});
   Console.WriteLine("Conversion storage UI checks: "+passed+" passed; "+failed+" failed. Detached controls, isolated preferences, no desktop surfaces.");
   return failed==0?0:1;
  }catch(Exception error){var invocation=error as TargetInvocationException;Console.Error.WriteLine(invocation!=null&&invocation.InnerException!=null?invocation.InnerException:error);return 1;}
 }
}
