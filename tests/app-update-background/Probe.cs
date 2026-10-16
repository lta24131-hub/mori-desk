using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace NookBackgroundUpdateTests {
 // Explicit read-only online probe. Not part of ordinary/offline test runs.
 // Reflection loads the updater only: no Nook entry, UI, credentials,
 // installer download, process launch, settings or library access.
 internal static class Probe {
  public static int Main(string[] args){
   try{
    if(args.Length!=2||args[1]!="--anonymous-metadata-only")throw new ArgumentException("Explicit anonymous metadata probe flag required");
    string executable=Path.GetFullPath(args[0]),directory=Path.GetDirectoryName(executable);
    AppDomain.CurrentDomain.AssemblyResolve+=(sender,eventArgs)=>{string dependency=Path.Combine(directory,new AssemblyName(eventArgs.Name).Name+".dll");return File.Exists(dependency)?Assembly.LoadFrom(dependency):null;};
    var assembly=Assembly.LoadFrom(executable);var type=assembly.GetType("MoriDesk.AppUpdateService",true);
    var constructor=type.GetConstructor(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,Type.EmptyTypes,null);
    var method=type.GetMethod("CheckAsync",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
    if(constructor==null||method==null)throw new MissingMethodException("Updater constructor / check missing");
    using(var service=(IDisposable)constructor.Invoke(new object[0])){
     using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(90))){
      object prior=Check(method,service,new Version(0,1,22,0),timeout.Token);
      if(prior==null)throw new InvalidOperationException("Expected published update was not discovered");
      Type release=prior.GetType();string tag=(string)release.GetProperty("Tag").GetValue(prior,null),hash=(string)release.GetProperty("ExpectedSha256").GetValue(prior,null);
      long size=(long)release.GetProperty("Size").GetValue(prior,null);bool hasHash=hash!=null&&hash.Length==64;
      if(tag!="v0.1.23-preview"||size<=0||!hasHash)throw new InvalidOperationException("Public release identity / size / checksum differs from expected");
      Console.WriteLine("PASS current=0.1.22.0 release="+tag+" size="+size+" hasSha256="+hasHash);
      Version current=assembly.GetName().Version;
      if(current!=new Version(0,1,24,0))throw new InvalidOperationException("Probe target is not expected current build");
      object latest=Check(method,service,current,timeout.Token);
      if(latest!=null)throw new InvalidOperationException("Current build unexpectedly offers a downgrade / reinstall");
      Console.WriteLine("PASS current="+current+" release=none; anonymous real .NET TLS/proxy/JSON path verified");
     }
    }
    return 0;
   }catch(Exception error){while(error is TargetInvocationException&&error.InnerException!=null)error=error.InnerException;Console.Error.WriteLine("FAIL anonymous metadata probe: "+error.GetType().Name+": "+error.Message);return 1;}
  }
  static object Check(MethodInfo method,object service,Version current,CancellationToken token){
   var task=(Task)method.Invoke(service,new object[]{current,"",true,token});task.GetAwaiter().GetResult();return task.GetType().GetProperty("Result").GetValue(task,null);
  }
 }
}
