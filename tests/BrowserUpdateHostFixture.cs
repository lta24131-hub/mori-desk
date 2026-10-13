using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace MoriDesk {
 // Actual manager/HTTP/native-save end-to-end fixture. Every mutable path is
 // below a new, explicitly named direct child of TEMP. No registry/UI/user data.
 internal static class BrowserUpdateHostFixture {
  static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
  static void Emit(object value){Console.WriteLine(Json.Serialize(value));Console.Out.Flush();}
  internal static int Run(string requestedRoot){
   try{
    string root=Path.GetFullPath(requestedRoot).TrimEnd(Path.DirectorySeparatorChar);
    string temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
    if(!string.Equals(Path.GetDirectoryName(root),temp,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(root).StartsWith("MoriDesk-update-host-",StringComparison.Ordinal)||!Directory.Exists(root)||Directory.EnumerateFileSystemEntries(root).Any())throw new InvalidOperationException("A fresh, empty MoriDesk-update-host-* TEMP directory is required.");
    for(var dir=new DirectoryInfo(root);dir!=null;dir=dir.Parent)if(dir.Exists&&(dir.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Fixture paths must not contain links.");
    string data=Path.Combine(root,"data"),bundle=Path.Combine(root,"bundle"),library=Path.Combine(root,"Library");
    Environment.SetEnvironmentVariable("MORI_DESK_DATA",data);
    Directory.CreateDirectory(bundle);
    foreach(string file in BrowserExtensionManager.RuntimeFiles)File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"browser-extension",file),Path.Combine(bundle,file));
    string manifestPath=Path.Combine(bundle,"manifest.json");
    var manifest=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(manifestPath));
    string oldVersion=(string)manifest["version"];var parts=oldVersion.Split('.').Select(int.Parse).ToArray();parts[parts.Length-1]++;string nextVersion=string.Join(".",parts);
    var ids=new HashSet<string>();
    var store=new BrowserExtensionStore(data,bundle,Path.Combine(root,"legacy"),BrowserExtensionManager.PublicKey,id=>ids.Add(id),()=>ids.ToArray());
    string secret=BrowserLocalBridge.Pair(Path.Combine(data,"browser"),Path.Combine(root,"bootstrap"));int saves=0,applies=0;
    using(var bridge=new BrowserLocalBridge(0,secret,origin=>ids.Contains(BrowserConnection.IdFromOrigin(origin)),request=>{
     string action=Convert.ToString(request["action"]);
     if(action=="extensionControl"||action=="bridgeStatus"){
      if(action=="extensionControl"&&Convert.ToString(request["op"])=="apply")Interlocked.Increment(ref applies);
      return store.Handle(request);
     }
     if(action=="save")Interlocked.Increment(ref saves);
     return Mori.NativeBridge.HandleAtCurrentLibrary(request,library);
    })){
     // Redirect only this trusted temporary test bundle. Keep native() intact
     // so updater pre-write journaling is exercised; never contact port 47891.
     string endpoint="http://127.0.0.1:"+bridge.ListeningPort+"/mori/v1";
     string hook="\n// Isolated test fixture transport, not shipped runtime code.\n"+
      "const hostFixtureRequest = async message => {const response = await fetch("+Json.Serialize(endpoint)+", {method:'POST', credentials:'omit', cache:'no-store', headers:{'Content-Type':'application/json','X-Mori-Token':self.MoriLocalConnection.token}, body:JSON.stringify(message)});return response.json();};\n"+
      "MoriLocalBridge.request = hostFixtureRequest;\n"+
      "chrome.runtime.sendNativeMessage = (host,message,callback) => {hostFixtureRequest(message).then(callback,()=>callback({ok:false,code:'LOCAL_CONNECTION_UNAVAILABLE',error:'Isolated fixture transport unavailable'}));};\n";
     File.AppendAllText(Path.Combine(bundle,"background.js"),hook,new UTF8Encoding(false));
     var installed=store.EnsureInstalled();
     Emit(new{kind="ready",path=installed.path,id=BrowserExtensionStore.KeyId(BrowserExtensionManager.PublicKey),port=bridge.ListeningPort,version=oldVersion,nextVersion=nextVersion});
     var deadline=DateTime.UtcNow.AddMinutes(2);
     while(DateTime.UtcNow<deadline){
      var read=System.Threading.Tasks.Task.Factory.StartNew(()=>Console.ReadLine());
      if(!read.Wait(TimeSpan.FromSeconds(90)))throw new TimeoutException("Fixture command timed out.");
      string command=read.Result;if(command==null)return 3;
      if(command=="stage"){
       manifest["version"]=nextVersion;File.WriteAllText(manifestPath,Json.Serialize(manifest),new UTF8Encoding(false));
       store.RequestUpdate();Emit(new{kind="staged",version=nextVersion});continue;
      }
      if(command!="inspect"&&command!="finish")throw new InvalidOperationException("Unknown fixture command.");
      store.Status();
      var catalog=Json.Deserialize<BrowserExtensionCatalog>(File.ReadAllText(Path.Combine(data,"browser","extension-installs.json")));
      var entry=catalog.installs.Single();bool exists=File.Exists(Path.Combine(library,".mori","library.db"));int count=0;bool source=false;
      if(exists)using(var lib=new Mori.Library(library)){var all=lib.All();count=all.Count;source=all.Count==1&&all[0].Source=="https://mori-update-host-fixture.invalid/page";}
      bool settings=File.Exists(Path.Combine(data,"settings.json")),backup=Directory.Exists(entry.backup);
      Emit(new{kind=command,libraryExists=exists,settingsExists=settings,count=count,source=source,version=entry.version,activeVersion=entry.activeVersion,pending=entry.pending!=null,awaitingReload=entry.awaitingReload,backup=backup,saves=saves,applies=applies});
      if(command=="finish")return count==1&&source&&!settings&&entry.version==nextVersion&&entry.activeVersion==nextVersion&&!entry.awaitingReload&&entry.pending==null&&backup&&saves==1&&applies==1?0:1;
     }
     throw new TimeoutException("Fixture lifetime exceeded.");
    }
   }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
  }
 }
}
