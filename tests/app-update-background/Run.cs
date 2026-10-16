using System;
using System.IO;
using System.Reflection;

namespace NookBackgroundUpdateTests {
 internal static class Run {
  [STAThread]
  public static int Main(string[] args){
   int checks=0;string fixtures=null;
   try{
    if(args.Length<1)throw new ArgumentException("Pass the built Mori.exe path; no application entry point will be invoked.");
    string executable=Path.GetFullPath(args[0]),directory=Path.GetDirectoryName(executable);
    AppDomain.CurrentDomain.AssemblyResolve+=(sender,eventArgs)=>{string dependency=Path.Combine(directory,new AssemblyName(eventArgs.Name).Name+".dll");return File.Exists(dependency)?Assembly.LoadFrom(dependency):null;};
    fixtures=args.Length>1?Path.GetFullPath(args[1]):Path.Combine(Path.GetTempPath(),"nook-update-background-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixtures);
    var assembly=Assembly.LoadFrom(executable);
    Action<bool,string> check=(passed,message)=>{checks++;if(!passed)throw new InvalidOperationException(message);};
    foreach(string name in new[]{"MoriDesk.AppUpdateServiceTests","MoriDesk.AppUpdatePanelTests","MoriDesk.AppUpdateCoordinatorTests"}){
     var type=assembly.GetType(name,true);var method=type.GetMethod("Run",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic,null,new[]{typeof(string),typeof(Action<bool,string>)},null);
     if(method==null)throw new MissingMethodException(name,"Run");method.Invoke(null,new object[]{fixtures,check});
     Console.WriteLine("PASS "+name);
    }
    Console.WriteLine("PASS "+checks+" background update checks. Fixtures: "+fixtures);return 0;
   }catch(Exception exception){var invocation=exception as TargetInvocationException;Console.Error.WriteLine("FAIL after "+checks+" checks: "+(invocation!=null?invocation.InnerException:exception));Console.Error.WriteLine("Fixtures: "+fixtures);return 1;}
  }
 }
}
