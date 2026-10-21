using System;
using System.IO;
using System.Reflection;
using System.Text;

// Optional full-component tests: only explicit isolated worker engines and
// generated Office/EPUB fixtures; never launch Nook or installed Office apps.
internal static class DocumentRunner {
 [STAThread]static int Main(string[] args){
  Console.OutputEncoding=Encoding.UTF8;
  try{
   if(args.Length!=4)throw new ArgumentException("DocumentRunner <Mori.exe> <worker.exe> <new-fixture-directory> <office|ebooks|all>");
   string app=Path.GetFullPath(args[0]),worker=Path.GetFullPath(args[1]),root=Path.GetFullPath(args[2]),directory=Path.GetDirectoryName(app);
   if(!File.Exists(app)||!File.Exists(worker)||Directory.Exists(root))throw new IOException("Actual candidates and a new fixture directory are required.");
   AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=>{string path=Path.Combine(directory,new AssemblyName(e.Name).Name+".dll");return File.Exists(path)?Assembly.LoadFrom(path):null;};
   int passed=0,failed=0;
   Action<bool,string> check=(ok,message)=>{if(ok)passed++;else failed++;Console.WriteLine((ok?"PASS ":"FAIL ")+message);};
   var assembly=Assembly.LoadFrom(app);
   assembly.GetType("MoriDesk.QuickConversionTests",true).GetMethod("RunDocuments",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{worker,root,args[3],check});
   Console.WriteLine("Real Office/ebook host closed-loop: "+passed+" passed; "+failed+" failed. Generated files and isolated engines only.");
   return failed==0?0:1;
  }catch(Exception error){var invocation=error as TargetInvocationException;Console.Error.WriteLine(invocation!=null&&invocation.InnerException!=null?invocation.InnerException:error);return 1;}
 }
}
