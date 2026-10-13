// Independent state-machine review. The real manager/presentation/local pairing
// sources are compiled with these fail-closed entrypoint stubs. No user browser,
// registry, installed extension or media library is ever opened.
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace MoriDesk {
 static class Entry {internal static string DataRoot {get {throw new InvalidOperationException("Production data entrypoint forbidden in review fixture");}}}
 static class BrowserConnection {
  internal const string Host="app.mori.desk";
  internal static void Register(string id){throw new InvalidOperationException("Production registration forbidden in review fixture");}
  internal static string ExtensionDirectory(string path){throw new InvalidOperationException("Production extension forbidden in review fixture");}
  internal static string IdFromOrigin(string origin){return Regex.IsMatch(origin??"","\\Achrome-extension://[a-p]{32}/?\\z")?origin.Substring(19,32):"";}
 }
 static class Review {
  sealed class Fixture {
   internal string root,data,bundle,legacy;
   internal readonly HashSet<string> allowed=new HashSet<string>();
   internal readonly JavaScriptSerializer json=new JavaScriptSerializer();
   internal DateTime time=new DateTime(2026,10,13,12,0,0,DateTimeKind.Utc);
   internal Fixture(string parent,string name){root=Path.Combine(parent,name);data=Path.Combine(root,"data");bundle=Path.Combine(root,"bundle");legacy=Path.Combine(root,"legacy","browser-extension");Directory.CreateDirectory(bundle);Version("1.0.0");}
   internal void Manifest(string folder,string version,string key){Directory.CreateDirectory(folder);var m=new Dictionary<string,object>{{"manifest_version",3},{"name","Mori review fixture"},{"version",version},{"background",new Dictionary<string,object>{{"service_worker","background.js"}}}};if(key!=null)m.Add("key",key);File.WriteAllText(Path.Combine(folder,"manifest.json"),json.Serialize(m),new UTF8Encoding(false));}
   internal void Version(string value){foreach(string file in BrowserExtensionManager.RuntimeFiles)File.WriteAllText(Path.Combine(bundle,file),file+" fixture "+value,new UTF8Encoding(false));Manifest(bundle,value,BrowserExtensionManager.PublicKey);}
   internal BrowserExtensionStore Store(){return new BrowserExtensionStore(data,bundle,legacy,BrowserExtensionManager.PublicKey,id=>allowed.Add(id),()=>allowed.ToArray(),null,()=>time);}
   internal BrowserExtensionCatalog Catalog(){return json.Deserialize<BrowserExtensionCatalog>(File.ReadAllText(Path.Combine(data,"browser","extension-installs.json")));}
   internal Dictionary<string,object> Send(BrowserExtensionStore store,BrowserExtensionInstall item,string op,string version=null,string client=null,string token=null){var request=new Dictionary<string,object>{{"action","extensionControl"},{"op",op},{"_extensionId",item.extensionId},{"extensionId",item.extensionId},{"installationId",item.installationId},{"clientId",client??new string('a',32)},{"version",version??item.version}};if(token!=null)request.Add("token",token);return json.Deserialize<Dictionary<string,object>>(json.Serialize(store.Handle(request)));}
   internal BrowserExtensionInstall Install(BrowserExtensionStore store){store.EnsureInstalled();var item=Catalog().installs.Single();Send(store,item,"poll");return item;}
   internal BrowserExtensionInstall Legacy(BrowserExtensionStore store,string version){Manifest(legacy,version,null);string id=BrowserExtensionStore.PathId(legacy);allowed.Add(id);store.BootstrapLegacy(id);var item=Catalog().installs.Single(i=>i.legacy);Send(store,item,"poll");return item;}
  }
  static object Snapshot(BrowserExtensionStatus s){return new{summary=s.summary,installed=s.installedVersion,running=s.version,bundled=s.bundledVersion,available=s.availableVersion,pending=s.pending,connected=s.connected,profiles=s.profiles,current=BrowserSettingsPresentation.Current(s),canUpdate=BrowserSettingsPresentation.CanUpdate(s),hint=BrowserSettingsPresentation.UpdateHint(s)};}
  static void Expect(bool condition,string message){if(!condition)throw new InvalidOperationException("Browser status regression: "+message);}
  public static int Main(string[] args){
   if(args.Length!=1||!Path.IsPathRooted(args[0]))throw new ArgumentException("An explicit isolated output root is required");string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);var rows=new List<object>();
   var f=new Fixture(root,"mixed-profiles");var store=f.Store();var item=f.Install(store);f.Send(store,item,"poll","0.9.0",new string('b',32));f.Send(store,item,"poll","1.0.0");var status=store.Status();Expect(status.profiles==2&&!BrowserSettingsPresentation.Current(status)&&!BrowserSettingsPresentation.CanUpdate(status),"mixed profiles cannot be declared current");rows.Add(new{scenario="mixed profile versions",state=Snapshot(status),expectation="current must be false while a recent profile still runs old code"});
   f=new Fixture(root,"current-profiles");store=f.Store();item=f.Install(store);f.Send(store,item,"poll",null,new string('b',32));status=store.Status();Expect(BrowserSettingsPresentation.Current(status)&&BrowserSettingsPresentation.UpdateHint(status).Contains("最新"),"multiple already-current profiles must not be asked to close");rows.Add(new{scenario="multiple profiles already current",state=Snapshot(status),expectation="no need to close healthy current browser profiles"});
   f=new Fixture(root,"expired-stage");store=f.Store();item=f.Install(store);f.Version("2.0.0");store.RequestUpdate();f.time=f.time.AddMinutes(31);var response=f.Send(store,item,"poll","1.0.0");status=store.Status();Expect(response["update"]==null&&BrowserSettingsPresentation.CanUpdate(status)&&!BrowserSettingsPresentation.Current(status),"expired stage must allow explicit retry");rows.Add(new{scenario="expired staged update",offered=response["update"]!=null,state=Snapshot(status),expectation="no live offer means the user must be able to request a fresh update"});
   f=new Fixture(root,"expired-ack");store=f.Store();item=f.Install(store);f.Version("2.0.0");store.RequestUpdate();item=f.Catalog().installs.Single();f.Send(store,item,"apply","1.0.0",null,item.pending.token);f.time=f.time.AddMinutes(31);item=f.Catalog().installs.Single();response=f.Send(store,item,"poll","1.0.0");status=store.Status();Expect(response["update"]==null&&BrowserSettingsPresentation.CanUpdate(status)&&!BrowserSettingsPresentation.Current(status),"expired ACK must allow explicit retry");rows.Add(new{scenario="reload acknowledgement timed out",offered=response["update"]!=null,state=Snapshot(status),expectation="a missing reload ACK must not disable explicit recovery forever"});
   f=new Fixture(root,"multiple-install-pending");store=f.Store();f.Install(store);item=f.Legacy(store,"1.0.0");f.Version("2.0.0");store.RequestUpdate();item=f.Catalog().installs.Single(i=>i.legacy);f.Send(store,item,"apply","1.0.0",null,item.pending.token);item=f.Catalog().installs.Single(i=>i.legacy);f.Send(store,item,"poll");status=store.Status();Expect(status.pending&&!BrowserSettingsPresentation.Current(status),"unselected installation's pending update must remain visible");rows.Add(new{scenario="other installation still pending",actualPendingInstalls=f.Catalog().installs.Count(i=>i.pending!=null||i.awaitingReload),state=Snapshot(status),expectation="must not say globally current or complete while another tracked install awaits update"});
   f=new Fixture(root,"multiple-install-newer");f.Version("3.0.0");store=f.Store();f.Install(store);f.Version("2.0.0");f.Legacy(store,"2.0.0");status=store.Status();Expect(status.availableVersion=="3.0.0"&&!BrowserSettingsPresentation.Current(status)&&!BrowserSettingsPresentation.CanUpdate(status),"unselected newer installation must prevent misleading downgrade");rows.Add(new{scenario="other installation newer than software",state=Snapshot(status),expectation="must expose version 3.0.0 and avoid misleading current/update state"});
   f=new Fixture(root,"same-version-damage");store=f.Store();item=f.Install(store);File.Move(Path.Combine(item.path,"content.js"),Path.Combine(f.root,"retained-content.js"));var before=store.Status();
   // Mirrors the explicit UI repair path: validated RequestUpdate first, then
   // existing registration/pairing setup. Never force-commit over a live browser.
   store.RequestUpdate();store.EnsureInstalled();Expect(store.Status().pending&&!File.Exists(Path.Combine(item.path,"content.js")),"explicit repair stages changed files without force-writing a live browser");rows.Add(new{scenario="same-version missing content script, explicit UI repair path",missingUntilBrowserAck=!File.Exists(Path.Combine(item.path,"content.js")),safeRepairStaged=store.Status().pending,stateBeforeRepair=Snapshot(before),expectation="repair must stage the missing file safely even when reported versions are equal"});
   Console.WriteLine(new JavaScriptSerializer().Serialize(rows));return 0;
  }
 }
}
