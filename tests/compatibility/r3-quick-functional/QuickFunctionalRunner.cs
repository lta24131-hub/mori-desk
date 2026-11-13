using System;
using System.IO;
using System.Reflection;
using System.Management;
using System.Text;

// Actual hidden conversion worker + synthetic database only. The application
// assembly is loaded for its classes; its entry point and UI are never run.
internal static class QuickRunner {
 [STAThread]static int Main(string[] args){
  Console.OutputEncoding=Encoding.UTF8;
  try{
   bool guest=false;using(var q=new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem"))foreach(ManagementObject row in q.Get())using(row){string machine=Convert.ToString(row["Manufacturer"])+" "+Convert.ToString(row["Model"]);guest|=System.Text.RegularExpressions.Regex.IsMatch(machine,"QEMU|KVM|Bochs",System.Text.RegularExpressions.RegexOptions.IgnoreCase);}
   if(!guest||!Environment.UserName.Equals("NookTester",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Dedicated VM only; no host test execution allowed.");
   if(args.Length!=3)throw new ArgumentException("QuickRunner <Mori.exe> <worker.exe> <new-fixture-directory>");
   string app=Path.GetFullPath(args[0]),worker=Path.GetFullPath(args[1]),root=Path.GetFullPath(args[2]),directory=Path.GetDirectoryName(app);
   if(!File.Exists(app)||!File.Exists(worker)||Directory.Exists(root))throw new IOException("Actual candidates and a fresh fixture directory are required.");
   AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=>{string path=Path.Combine(directory,new AssemblyName(e.Name).Name+".dll");return File.Exists(path)?Assembly.LoadFrom(path):null;};
   int passed=0,failed=0;
   Action<bool,string> check=(ok,message)=>{if(ok)passed++;else failed++;Console.WriteLine((ok?"PASS ":"FAIL ")+message);};
   var assembly=Assembly.LoadFrom(app);var tests=assembly.GetType("MoriDesk.QuickConversionTests",true);
   string oldWait=Environment.GetEnvironmentVariable("NOOK_VM_REAL_CONVERSION_WAIT_SECONDS");
   try{
    Environment.SetEnvironmentVariable("NOOK_VM_REAL_CONVERSION_WAIT_SECONDS","120");
    Action<string> trace=message=>{Console.WriteLine("TRACE "+message);Console.Out.Flush();};
    tests.GetMethod("RunReal",BindingFlags.NonPublic|BindingFlags.Static,null,new[]{typeof(string),typeof(string),typeof(Action<bool,string>),typeof(Action<string>)},null).Invoke(null,new object[]{worker,root,check,trace});
   }finally{Environment.SetEnvironmentVariable("NOOK_VM_REAL_CONVERSION_WAIT_SECONDS",oldWait);}
   Console.WriteLine("Quick real-worker closed-loop: "+passed+" passed; "+failed+" failed. Synthetic files and database only.");
   return failed==0?0:1;
  }catch(Exception error){var invocation=error as TargetInvocationException;Console.Error.WriteLine(invocation!=null&&invocation.InnerException!=null?invocation.InnerException:error);return 1;}
 }
}
