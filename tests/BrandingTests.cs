using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace MoriDesk {
 internal static class BrandingTests {
  const string CurrentName="Nook · 图片与网址收藏",LegacyName="Mori Desk · 图片与网址收藏";
  const string FixedId="ohmoflmbcmhahccacmlbggkpalcmmmae";
  // Freeze the established extension identity independently of the production
  // constant: changing both the shipped manifest and manager must still fail.
  const string PublicKeyHash="e7ce5bc12c7072202cb166af0b2ccc047d6e44732a89db59e1a5758f1a9afb70";
  const string MainIconHash="2350bef57e46e3ef3adeaa871949c6e56df9037f2785c6c2a87f2a0d0a940481";
  static readonly int[] IconSizes={16,32,48,128};
  static readonly string[] IconHashes={"b24e36e249845644be3059b858cda3f60d9c2f2acd2fb587b6982f897a2bc3aa","24fda89e52f3bce009ab782b525b7db2db3dc2f335c08b6da59179136ee0c423","a5262abd311285dd4e548fcb27dceaf2a61a53f7af35554a7578a8ffe9d9c37a","60dcf9e92646f48c145114b3a233915266213f06787f6161f75bf6585240df24"};
  sealed class Fixture {
   internal readonly string Root,Data,Bundle,Legacy;
   internal readonly HashSet<string> Registered=new HashSet<string>(StringComparer.Ordinal);
   internal readonly JavaScriptSerializer Json=new JavaScriptSerializer();
   internal Fixture(string root,string name,string displayName){
    Root=Path.Combine(root,name);Data=Path.Combine(Root,"data");Bundle=Path.Combine(Root,"bundle");Legacy=Path.Combine(Root,"legacy","browser-extension");Directory.CreateDirectory(Bundle);
    string shipped=BrowserConnection.ExtensionDirectory(typeof(Entry).Assembly.Location);
    foreach(string file in BrowserExtensionManager.RuntimeFiles)File.Copy(Path.Combine(shipped,file),Path.Combine(Bundle,file),false);
    Change(displayName,"0.2.18");
   }
   internal Dictionary<string,object> Manifest(string directory){return Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(directory,"manifest.json")));}
   internal void Change(string name,string version,string key=null){var manifest=Manifest(Bundle);manifest["name"]=name;manifest["version"]=version;if(key!=null)manifest["key"]=key;File.WriteAllText(Path.Combine(Bundle,"manifest.json"),Json.Serialize(manifest),new UTF8Encoding(false));}
   internal BrowserExtensionStore Store(){return new BrowserExtensionStore(Data,Bundle,Legacy,BrowserExtensionManager.PublicKey,id=>Registered.Add(id),()=>Registered.ToArray(),null,()=>new DateTime(2026,10,14,12,0,0,DateTimeKind.Utc));}
   internal BrowserExtensionInstall Install(){return Json.Deserialize<BrowserExtensionCatalog>(File.ReadAllText(Path.Combine(Data,"browser","extension-installs.json"))).installs.Single();}
   internal Dictionary<string,object> Send(BrowserExtensionStore store,BrowserExtensionInstall item,string op,string token=null){
    var request=new Dictionary<string,object>{{"action","extensionControl"},{"op",op},{"_extensionId",item.extensionId},{"extensionId",item.extensionId},{"installationId",item.installationId},{"clientId",new string('a',32)},{"version",item.version}};
    if(token!=null)request["token"]=token;
    return Json.Deserialize<Dictionary<string,object>>(Json.Serialize(store.Handle(request)));
   }
  }
  static bool Ok(Dictionary<string,object> response){object ok;return response.TryGetValue("ok",out ok)&&true.Equals(ok);}
  static bool Rejects(Action action){try{action();return false;}catch(InvalidDataException){return true;}}
  static string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
  static bool PngSize(byte[] bytes,int size){
   byte[] signature={137,80,78,71,13,10,26,10};if(bytes.Length<33||!bytes.Take(8).SequenceEqual(signature))return false;
   long width=((long)bytes[16]<<24)|((long)bytes[17]<<16)|((long)bytes[18]<<8)|bytes[19],height=((long)bytes[20]<<24)|((long)bytes[21]<<16)|((long)bytes[22]<<8)|bytes[23];return width==size&&height==size;
  }
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);AssemblyAndIcons(check);WindowBrand(check);ShippedManifest(check);
   Transition(root,"legacy-to-nook",LegacyName,CurrentName,check);
   // Both canonical names must work through staging and post-rename verification,
   // not merely through the string predicate used before reading a manifest.
   Transition(root,"nook-to-supported-legacy",CurrentName,LegacyName,check);
   Rejections(root,check);
  }
  static void AssemblyAndIcons(Action<bool,string> check){
   var assembly=typeof(Entry).Assembly;
   var product=(AssemblyProductAttribute)Attribute.GetCustomAttribute(assembly,typeof(AssemblyProductAttribute));
   var title=(AssemblyTitleAttribute)Attribute.GetCustomAttribute(assembly,typeof(AssemblyTitleAttribute));
   var fileVersion=(AssemblyFileVersionAttribute)Attribute.GetCustomAttribute(assembly,typeof(AssemblyFileVersionAttribute));
   check(product!=null&&product.Product=="Nook"&&title!=null&&title.Title=="Nook","executable metadata exposes the chosen Nook product and title");
   check(assembly.GetName().Version.Equals(new Version(0,1,22,0))&&fileVersion!=null&&fileVersion.Version=="0.1.22.0","Nook assembly and file versions are exactly 0.1.22.0");
   check(assembly.GetName().Name=="Mori"&&Path.GetFileName(assembly.Location)=="Mori.exe"&&File.Exists(assembly.Location+".config"),"branding preserves the Mori executable/assembly/config identity");
   var info=FileVersionInfo.GetVersionInfo(assembly.Location);check(info.ProductName=="Nook"&&info.FileVersion=="0.1.22.0","Windows executable details report Nook and the exact release version");
   check(assembly.GetManifestResourceNames().Count(n=>n=="QingJie.Icon.png")==1&&!assembly.GetManifestResourceNames().Contains("Nook.Icon.png"),"capture keeps its single established QingJie.Icon.png resource identity");
   byte[] embedded;using(var resource=assembly.GetManifestResourceStream("QingJie.Icon.png")){using(var copy=new MemoryStream()){resource.CopyTo(copy);embedded=copy.ToArray();}}
   byte[] shipped=File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(assembly.Location),"qingjie-icon.png"));
   check(embedded.SequenceEqual(shipped)&&PngSize(embedded,256)&&Hash(embedded)==MainIconHash,"embedded and shipped capture PNGs exactly match the selected purple four-tile 256 px icon");
   string extension=BrowserConnection.ExtensionDirectory(assembly.Location);
   for(int i=0;i<IconSizes.Length;i++){byte[] icon=File.ReadAllBytes(Path.Combine(extension,"icon-"+IconSizes[i]+".png"));check(PngSize(icon,IconSizes[i])&&Hash(icon)==IconHashes[i],"bundled extension PNG exactly matches the chosen Nook asset at "+IconSizes[i]+" px");}
  }
  static void WindowBrand(Action<bool,string> check){
   System.Windows.Window window;
   using(var stream=typeof(Entry).Assembly.GetManifestResourceStream("Mori.Main.xaml"))window=(System.Windows.Window)System.Windows.Markup.XamlReader.Load(stream);
   try{
    Mori.Theme.Attach(window,null);
    var label=window.FindName("BrandText") as System.Windows.Controls.TextBlock;
    check(window.Title=="Nook · 本地素材库"&&label!=null&&label.Text=="Nook","main window title and visible header use the selected Nook name");
    var icon=window.Icon as System.Windows.Media.Imaging.BitmapSource;
    check(ReferenceEquals(icon,QingJie.Ui.AppIcon)&&icon!=null&&icon.IsFrozen&&icon.PixelWidth==256&&icon.PixelHeight==256,"attached main window uses the shared frozen selected icon");
    check(!window.IsVisible,"brand window verification never opens a desktop window");
   }finally{window.Close();}
  }
  static void ShippedManifest(Action<bool,string> check){
   var json=new JavaScriptSerializer();var manifest=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(BrowserConnection.ExtensionDirectory(typeof(Entry).Assembly.Location),"manifest.json")));
   check(Convert.ToString(manifest["name"])==CurrentName&&Convert.ToString(manifest["version"])=="0.2.21","actual bundled manifest ships canonical Nook branding and extension version 0.2.21");
   string key=Convert.ToString(manifest["key"]);check(key==BrowserExtensionManager.PublicKey&&Hash(Convert.FromBase64String(key))==PublicKeyHash&&BrowserExtensionStore.KeyId(key)==FixedId,"actual bundle and updater retain the frozen public key and Chrome extension ID");
   var icons=(Dictionary<string,object>)manifest["icons"];check(icons.Count==IconSizes.Length&&IconSizes.All(size=>icons.ContainsKey(size.ToString())&&Convert.ToString(icons[size.ToString()])=="icon-"+size+".png"),"Nook manifest preserves every established extension icon filename");
   check(BrowserConnection.Host=="app.mori.desk","Nook keeps the established native messaging host");
   string legacy=json.Serialize(new Dictionary<string,object>{{"name","app.mori.desk"},{"description","Mori Desk local library"},{"allowed_origins",new[]{"chrome-extension://"+FixedId+"/"}}});
   var native=json.Deserialize<Dictionary<string,object>>(BrowserConnection.Manifest(typeof(Entry).Assembly.Location,FixedId,legacy));
   check(Convert.ToString(native["name"])=="app.mori.desk"&&Convert.ToString(native["description"])=="Nook local library"&&Convert.ToString(native["path"])==typeof(Entry).Assembly.Location&&((IEnumerable)native["allowed_origins"]).Cast<object>().Select(Convert.ToString).SequenceEqual(new[]{"chrome-extension://"+FixedId+"/"}),"native manifest rebranding retains executable path, host and already-paired extension origin");
  }
  static void Transition(string root,string fixtureName,string firstName,string nextName,Action<bool,string> check){
   var fixture=new Fixture(root,fixtureName,firstName);var store=fixture.Store();
   check(store.Status().bundledVersion=="0.2.18","real bundle manifest reader accepts canonical initial name: "+firstName);
   var initialStatus=store.EnsureInstalled();var initial=fixture.Install();var initialManifest=fixture.Manifest(initial.path);
   check(Convert.ToString(initialManifest["name"])==firstName&&Convert.ToString(initialManifest["key"])==BrowserExtensionManager.PublicKey&&initial.extensionId==FixedId&&fixture.Registered.SetEquals(new[]{FixedId})&&initialStatus.path==Path.Combine(fixture.Data,"browser","extension"),"actual managed install preserves branding and exact stable identity for "+firstName);
   check(Ok(fixture.Send(store,initial,"poll")),"installed canonical branded extension authenticates its update heartbeat: "+firstName);
   byte[] before=File.ReadAllBytes(Path.Combine(initial.path,"manifest.json")),pairing=File.ReadAllBytes(Path.Combine(initial.path,"local-connection.js"));
   fixture.Change(nextName,"0.2.19");var stagedStatus=store.RequestUpdate();var pending=fixture.Install();
   check(stagedStatus.pending&&pending.pending!=null&&pending.pending.version=="0.2.19"&&Convert.ToString(fixture.Manifest(pending.pending.path)["name"])==nextName&&File.ReadAllBytes(Path.Combine(initial.path,"manifest.json")).SequenceEqual(before),"real update stages accepted next brand without changing installed files before apply: "+nextName);
   var poll=fixture.Send(store,pending,"poll");var offer=poll["update"] as Dictionary<string,object>;
   check(Ok(poll)&&offer!=null&&Convert.ToString(offer["version"])=="0.2.19"&&Convert.ToString(offer["token"])==pending.pending.token,"authenticated poll offers the exact staged Nook/legacy branded update token");
   var applied=fixture.Send(store,pending,"apply",pending.pending.token);var installed=fixture.Install();var finalManifest=fixture.Manifest(installed.path);
   check(Ok(applied)&&true.Equals(applied["reload"])&&installed.pending==null&&installed.version=="0.2.19"&&Convert.ToString(finalManifest["name"])==nextName,"actual apply accepts the branded manifest through hash and post-rename verification: "+nextName);
   check(installed.installationId==initial.installationId&&installed.extensionId==FixedId&&installed.manifestKey==BrowserExtensionManager.PublicKey&&Convert.ToString(finalManifest["key"])==BrowserExtensionManager.PublicKey&&File.ReadAllBytes(Path.Combine(installed.path,"local-connection.js")).SequenceEqual(pairing),"brand transition retains install ID, exact key, Chrome ID and existing local pairing bytes");
   check(Directory.Exists(installed.backup)&&File.ReadAllBytes(Path.Combine(installed.backup,"manifest.json")).SequenceEqual(before),"brand transition retains the exact original installed manifest in its rollback backup");
   check(Ok(fixture.Send(store,installed,"poll"))&&store.Status().upToDate&&!store.Status().pending,"new branded runtime acknowledgment completes the actual update lifecycle");
  }
  static void Rejections(string root,Action<bool,string> check){
   foreach(string name in new[]{"Nook","Nook unrelated extension","Other · 图片与网址收藏"}){
    var fixture=new Fixture(root,"invalid-name-"+Guid.NewGuid().ToString("N"),name);var store=fixture.Store();
    check(Rejects(()=>store.EnsureInstalled())&&!Directory.Exists(Path.Combine(fixture.Data,"browser","extension"))&&!File.Exists(Path.Combine(fixture.Data,"browser","extension-installs.json")),"actual install rejects unrecognized display name without committing an installation: "+name);
   }
   var wrongKey=new Fixture(root,"invalid-key",CurrentName);wrongKey.Change(CurrentName,"0.2.19","AQID");
   check(Rejects(()=>wrongKey.Store().EnsureInstalled())&&!Directory.Exists(Path.Combine(wrongKey.Data,"browser","extension"))&&!File.Exists(Path.Combine(wrongKey.Data,"browser","extension-installs.json")),"accepted Nook display name cannot authorize a changed public key or extension identity");
  }
 }
}
