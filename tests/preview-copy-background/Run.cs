using System;
using System.IO;
using System.Reflection;

namespace NookPreviewCopyBackgroundTests {
 internal static class Run {
  [STAThread]
  public static int Main(string[] args){
   int checks=0;
   try{
    if(args.Length!=2)throw new ArgumentException("Pass the built Mori.exe and an isolated fixture directory.");
    string executable=Path.GetFullPath(args[0]),directory=Path.GetDirectoryName(executable),fixtures=Path.GetFullPath(args[1]);
    AppDomain.CurrentDomain.AssemblyResolve+=(sender,eventArgs)=>{string dependency=Path.Combine(directory,new AssemblyName(eventArgs.Name).Name+".dll");return File.Exists(dependency)?Assembly.LoadFrom(dependency):null;};
    Directory.CreateDirectory(fixtures);var assembly=Assembly.LoadFrom(executable);
    var type=assembly.GetType("MoriDesk.PreviewClipboardTests",true);
    var method=type.GetMethod("Run",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);
    Action<bool,string> check=(passed,message)=>{checks++;if(!passed)throw new InvalidOperationException(message);};
    method.Invoke(null,new object[]{fixtures,check});
    Console.WriteLine("PASS "+checks+" preview-copy checks. No application entry point, shown windows, clipboard reads/writes, or user files.");return 0;
   }catch(Exception error){var invocation=error as TargetInvocationException;Console.Error.WriteLine("FAIL after "+checks+" checks: "+(invocation!=null?invocation.InnerException:error));return 1;}
  }
 }
}
