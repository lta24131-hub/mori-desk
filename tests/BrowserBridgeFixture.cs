using System;
using System.IO;
using System.Linq;
namespace MoriDesk {
 internal static class BrowserBridgeFixture {
  internal static int Run(string root,string id){
   // Explicit, disposable test fixture only: no registrations or real settings.
   root=Path.GetFullPath(root);if(!root.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(root).StartsWith("MoriDesk-browser-test-",StringComparison.Ordinal))return 2;
   string library=Path.Combine(root,"Library"),data=Path.Combine(root,"MoriDesk"),extension=Path.Combine(root,"extension");Environment.SetEnvironmentVariable("MORI_DESK_DATA",data);
   try{
    string manifest=BrowserConnection.WriteManifest(data,typeof(Entry).Assembly.Location,BrowserConnection.ValidateId(id));string secret=BrowserLocalBridge.Pair(data,extension);
    using(var lib=new Mori.Library(library))lib.CreateFolder("Browser test","");
    using(var bridge=new BrowserLocalBridge(BrowserLocalBridge.Port,secret,origin=>BrowserLocalBridge.OriginAllowed(manifest,origin),request=>Mori.NativeBridge.HandleAtCurrentLibrary(request,library))){
     Console.WriteLine("READY");Console.Out.Flush();var done=new System.Threading.ManualResetEvent(false);System.Threading.ThreadPool.QueueUserWorkItem(delegate{Console.ReadLine();done.Set();});if(!done.WaitOne(60000))return 3;
    }
    using(var lib=new Mori.Library(library)){bool valid=lib.All().Count==1&&lib.All()[0].Name=="Browser fallback fixture"&&lib.AssetFolders(lib.All()[0].Id).Single().Name=="Browser test";Console.WriteLine(valid?"PASS saved original, source and category; duplicate not added":"FAIL fixture library");return valid?0:1;}
   }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
  }
 }
}
