using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Threading;

namespace MoriDesk {
 // Exercises the actual native message handler without showing a window or
 // starting WebView. Every manifest/image belongs to this isolated fixture.
 internal static class MatureCanvasHostTests {
  static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=128*1024*1024,RecursionLimit=64};}
  static Dictionary<string,object> Obj(object value){return (Dictionary<string,object>)value;}
  static Dictionary<string,object> Map(params object[] values){var map=new Dictionary<string,object>();for(int i=0;i<values.Length;i+=2)map.Add((string)values[i],values[i+1]);return map;}
  static Dictionary<string,object> Scene(MatureCanvasWindow host){return Obj(Json().DeserializeObject(host.DocumentForTests.SceneJson));}
  static Dictionary<string,object> Records(Dictionary<string,object> scene){return Obj(Obj(scene["document"])["store"]);}
  static string Message(MatureCanvasWindow host,string type,string requestId,object payload=null,string session=null){return Json().Serialize(Map("protocol",MatureCanvasWindow.Protocol,"type",type,"requestId",requestId,"session",session??host.SessionForTests,"payload",payload??Map()));}
  static Task Send(MatureCanvasWindow host,string type,string requestId,object payload=null,string session=null){return host.HandleMessageAsync(MatureCanvasWindow.Origin+"/index.html",Message(host,type,requestId,payload,session));}
  static void Wait(Func<bool> complete){DateTime end=DateTime.UtcNow.AddSeconds(20);while(!complete()&&DateTime.UtcNow<end){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);Thread.Sleep(1);}if(!complete())throw new Exception("Mature canvas host fixture timed out");}
  static void Wait(Task task){Wait(()=>task.IsCompleted);task.GetAwaiter().GetResult();}
  static FieldInfo Field(string name){return typeof(MatureCanvasWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic);}
  static void Dispose(MatureCanvasWindow host){if((bool)Field("disposed").GetValue(host))return;host.PostForTests=null;Field("ready").SetValue(host,false);host.Close();}
  static void Load(MatureCanvasWindow host){Wait(Send(host,"ready","ready"));Wait(Send(host,"loaded","loaded",Map("revision",host.RevisionForTests)));}
  static string Manifest(string root,string id){return Path.Combine(new MatureCanvasStore(root).Root,id,MatureCanvasStore.ManifestName);}
  static byte[] Raster(){using(var bitmap=new Bitmap(4,3)){using(var graphics=Graphics.FromImage(bitmap))graphics.Clear(Color.MediumPurple);using(var output=new MemoryStream()){bitmap.Save(output,ImageFormat.Png);return output.ToArray();}}}
  static void AddInline(Dictionary<string,object> scene,string source){Records(scene)["asset:inlineFixture"]=Map("id","asset:inlineFixture","typeName","asset","type","image","meta",Map(),"props",Map("w",4,"h",3,"name","inline.png","mimeType","image/png","src",source,"isAnimated",false));}
  static void AddImported(Dictionary<string,object> scene,Dictionary<string,object> item){
   string asset="asset:"+item["id"],shape="shape:import-"+item["id"];var records=Records(scene);
   records[asset]=Map("id",asset,"typeName","asset","type","image","meta",Map(),"props",Map("w",item["width"],"h",item["height"],"name",item["name"],"mimeType",item["mimeType"],"src",item["src"],"isAnimated",false));
   records[shape]=Map("id",shape,"typeName","shape","type","image","x",20,"y",40,"rotation",0,"index","a1","parentId","page:page","isLocked",false,"opacity",1,"meta",Map(),"props",Map("w",item["width"],"h",item["height"],"assetId",asset,"playing",true,"url","","crop",null,"flipX",false,"flipY",false));
  }
  // A valid legacy annotation lets the real serializer announce its thread
  // and pause deterministically. No test hook is added to the production host.
  sealed class SerializationProbe : IDisposable {
   readonly int owner=Thread.CurrentThread.ManagedThreadId;
   readonly ManualResetEventSlim entered=new ManualResetEventSlim(),resume=new ManualResetEventSlim();
   int worker;
   public Dictionary<string,object> nookLegacy {get{Interlocked.Exchange(ref worker,Thread.CurrentThread.ManagedThreadId);entered.Set();if(worker!=owner&&!resume.Wait(TimeSpan.FromSeconds(10)))throw new TimeoutException("Serialization probe was not released");return Map("kind","text","color","auto","stroke",1,"fontSize",12,"width",40,"height",20);}}
   internal bool Entered {get{return entered.IsSet;}}
   internal bool Background {get{return worker!=0&&worker!=owner;}}
   internal bool Released {get{return resume.IsSet;}}
   internal void Release(){resume.Set();}
   public void Dispose(){resume.Set();entered.Dispose();resume.Dispose();}
  }
  static Task SaveStage(MatureCanvasWindow host,Dictionary<string,object> scene,string request){return (Task)typeof(MatureCanvasWindow).GetMethod("SaveMessage",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(host,new object[]{Map("scene",scene,"revision",host.RevisionForTests),request,host.DocumentForTests,host.SessionForTests});}
  sealed class ImportEcho {
   readonly MatureCanvasWindow host;
   internal readonly List<Task> Replies=new List<Task>();
   internal bool HoldLoad,HoldAdded;
   internal int Loads,Adds;
   Dictionary<string,object> browserScene;
   string addedRequest;int addedCount;
   internal ImportEcho(MatureCanvasWindow host){this.host=host;host.PostForTests=Post;}
   void Post(string json){
    var output=Obj(Json().DeserializeObject(json));string type=output["type"].ToString();var payload=Obj(output["payload"]);
    if(type=="load"){Loads++;browserScene=Obj(payload["scene"]);if(!HoldLoad)Replies.Add(Loaded());}
    else if(type=="flush")Replies.Add(Send(host,"save",output["requestId"].ToString(),Map("scene",browserScene,"revision",host.RevisionForTests)));
    else if(type=="addImages"){Adds++;var images=(object[])payload["images"];foreach(var item in images)AddImported(browserScene,Obj(item));addedRequest=output["requestId"].ToString();addedCount=images.Length;if(!HoldAdded)Replies.Add(Added());}
   }
   internal Task Loaded(){return Send(host,"loaded","load_"+Guid.NewGuid().ToString("N"));}
   internal Task Added(){return Send(host,"imagesAdded",addedRequest,Map("count",addedCount));}
  }
  static async Task<bool> WaitAndImport(MatureCanvasWindow host,string target,string[] paths){await host.WaitUntilReadyAsync();return await host.OpenProjectAsync(target,paths);}
  static bool Rejected(Task task){try{Wait(task);return false;}catch(IOException){return true;}}
  static bool LoadWarning(MatureCanvasWindow host){var text=(System.Windows.Controls.TextBlock)Field("feedback").GetValue(host);return text.Visibility==System.Windows.Visibility.Visible&&text.Text.Contains("可继续等待")&&text.Text.Contains("取消");}
  static void ReadinessLifecycle(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);string source=Path.Combine(root,"source.png");byte[] original=Raster();File.WriteAllBytes(source,original);
   string slowData=Path.Combine(root,"slow-start");var slow=new MatureCanvasWindow(slowData);var slowEcho=new ImportEcho(slow){HoldLoad=true};var notice=new TaskCompletionSource<bool>();slow.LoadNoticeDelayForTests=()=>notice.Task;
   try{
    var pending=WaitAndImport(slow,slow.ProjectId,new[]{source});notice.SetResult(true);Wait(()=>LoadWarning(slow));
    check(!pending.IsCompleted&&!slow.CanImportImages,"slow first startup keeps the original image request pending and shows wait/cancel guidance instead of abandoning it");
    Wait(Send(slow,"ready","ready"));Wait(slowEcho.Loaded());Wait(pending);foreach(var reply in slowEcho.Replies.ToArray())Wait(reply);
    check(pending.Result&&slowEcho.Adds==1&&new MatureCanvasStore(slowData).Load(slow.ProjectId).ImageCount==1,"a late first loaded ACK continues the same request through one image insertion and a durable save without user retry");
   }finally{Dispose(slow);}
   string switchData=Path.Combine(root,"slow-switch");var switching=new MatureCanvasWindow(switchData);var switchEcho=new ImportEcho(switching);
   try{
    Wait(Send(switching,"ready","ready"));string oldId=switching.ProjectId;var store=new MatureCanvasStore(switchData);string next=store.Create("slow destination").Id;
    switchEcho.HoldLoad=true;var switchNotice=new TaskCompletionSource<bool>();switching.LoadNoticeDelayForTests=()=>switchNotice.Task;
    var pending=switching.OpenProjectAsync(next,new[]{source});Wait(()=>switchEcho.Loads==2);switchNotice.SetResult(true);Wait(()=>LoadWarning(switching));
    check(!pending.IsCompleted&&!switching.CanImportImages,"a slow project switch retains its image request and visible cancellation guidance without releasing the transition early");
    Wait(switchEcho.Loaded());Wait(pending);foreach(var reply in switchEcho.Replies.ToArray())Wait(reply);
    check(pending.Result&&switchEcho.Adds==1&&switching.ProjectId==next&&store.Load(next).ImageCount==1&&store.Load(oldId).ImageCount==0,"late destination load imports exactly once into the selected project and never into the previous project");
   }finally{Dispose(switching);}
   var closing=new MatureCanvasWindow(Path.Combine(root,"cancel-start"));var closeEcho=new ImportEcho(closing){HoldLoad=true};var closeNotice=new TaskCompletionSource<bool>();closing.LoadNoticeDelayForTests=()=>closeNotice.Task;
   try{
    var pending=WaitAndImport(closing,closing.ProjectId,new[]{source});closeNotice.SetResult(true);Wait(()=>LoadWarning(closing));
    var args=new CancelEventArgs();typeof(MatureCanvasWindow).GetMethod("ClosingCanvas",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(closing,new object[]{closing,args});check(!args.Cancel,"the actual titlebar close can cancel a still-starting canvas without an impossible save prompt");
    Dispose(closing);check(Rejected(pending),"closing a slow-start window ends the pending readiness/import task rather than leaving it hung");Wait(Send(closing,"ready","lateReady"));Wait(closeEcho.Loaded());check(closeEcho.Adds==0,"late messages after cancelled startup never resurrect its image request");
   }finally{Dispose(closing);}
   var failed=new MatureCanvasWindow(Path.Combine(root,"engine-fails"));var failedEcho=new ImportEcho(failed){HoldLoad=true};
   try{
    var pending=WaitAndImport(failed,failed.ProjectId,new[]{source});Wait(Send(failed,"ready","ready"));Wait(Send(failed,"error","loadFailed",Map("message","fixture engine startup failed")));
    check(Rejected(pending)&&!failed.CanImportImages&&failedEcho.Adds==0,"an explicit engine load failure promptly ends the pending request and never sends its images");
   }finally{Dispose(failed);}
   foreach(string brokenType in new[]{"load","interaction"}){
    var broken=new MatureCanvasWindow(Path.Combine(root,"broken-"+brokenType));
    try{
     broken.PostForTests=json=>{if(Obj(Json().DeserializeObject(json))["type"].ToString()==brokenType)throw new IOException("fixture disconnected load channel");};var pending=broken.WaitUntilReadyAsync();Wait(Send(broken,"ready","ready"));if(brokenType=="interaction")Wait(Send(broken,"loaded","loaded"));
     check(Rejected(pending)&&!broken.CanImportImages,"a synchronous "+brokenType+" bridge failure ends readiness instead of leaving a visible but permanently waiting canvas");
    }finally{Dispose(broken);}
   }
   string initialData=Path.Combine(root,"initial-import");var inputs=new[]{source};var initial=new MatureCanvasWindow(initialData,null,inputs);inputs[0]=Path.Combine(root,"mutated-after-construction.png");var initialEcho=new ImportEcho(initial){HoldAdded=true};
   try{
    var pending=initial.WaitUntilReadyAsync();Wait(Send(initial,"ready","ready"));Wait(()=>initialEcho.Adds==1);
    check(!pending.IsCompleted,"readiness does not finish while constructor images are still awaiting the browser import ACK");Wait(initialEcho.Loaded());check(initialEcho.Adds==1&&!pending.IsCompleted,"a repeated loaded ACK cannot duplicate constructor images or prematurely complete readiness");
    Wait(initialEcho.Added());Wait(pending);foreach(var reply in initialEcho.Replies.ToArray())Wait(reply);
    check(initialEcho.Adds==1&&new MatureCanvasStore(initialData).Load(initial.ProjectId).ImageCount==1,"constructor image paths are frozen and their single import is durably saved before readiness completes");
   }finally{Dispose(initial);}
   string cancelImportData=Path.Combine(root,"cancel-initial-import");var cancelImport=new MatureCanvasWindow(cancelImportData,null,new[]{source});var cancelEcho=new ImportEcho(cancelImport){HoldAdded=true};
   try{
    var pending=cancelImport.WaitUntilReadyAsync();Wait(Send(cancelImport,"ready","ready"));Wait(()=>cancelEcho.Adds==1);var args=new CancelEventArgs();typeof(MatureCanvasWindow).GetMethod("ClosingCanvas",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(cancelImport,new object[]{cancelImport,args});
    check(!args.Cancel,"titlebar close can also cancel the pending constructor-image import before readiness completes");string target=cancelImport.ProjectId;Dispose(cancelImport);check(Rejected(pending),"closing during the initial browser image ACK ends the whole startup operation");Wait(cancelEcho.Added());foreach(var reply in cancelEcho.Replies.ToArray())Wait(reply);
    check(cancelEcho.Adds==1&&new MatureCanvasStore(cancelImportData).Load(target).ImageCount==0,"a late image ACK after startup cancellation is ignored without re-importing or falsely saving an image shape");
   }finally{Dispose(cancelImport);}
   string staleData=Path.Combine(root,"stale-import");var stale=new MatureCanvasWindow(staleData);var staleEcho=new ImportEcho(stale);
   try{
    Wait(Send(stale,"ready","ready"));var store=new MatureCanvasStore(staleData);string oldId=stale.ProjectId;byte[] before=File.ReadAllBytes(Manifest(staleData,oldId));var replacement=store.Create("replacement");byte[] replacementBytes=File.ReadAllBytes(Manifest(staleData,replacement.Id));var gate=(SemaphoreSlim)Field("operations").GetValue(stale);Task pending;
    check(gate.Wait(0),"stale image-import fixture holds the actual production serialization gate");
    try{pending=(Task)typeof(MatureCanvasWindow).GetMethod("ImportCore",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(stale,new object[]{new[]{source}});check(!pending.IsCompleted,"image import waits on the real resource serialization gate");Field("document").SetValue(stale,replacement);Field("session").SetValue(stale,Guid.NewGuid().ToString("N"));}finally{gate.Release();}
    check(Rejected(pending)&&staleEcho.Adds==0&&File.ReadAllBytes(Manifest(staleData,oldId)).SequenceEqual(before)&&File.ReadAllBytes(Manifest(staleData,replacement.Id)).SequenceEqual(replacementBytes),"a queued import rechecks its captured project/session after the gate and cannot write or post into a replacement canvas");
   }finally{Dispose(stale);}
   check(File.ReadAllBytes(source).SequenceEqual(original),"slow startup, delayed project load, cancelled readiness and stale import leave the original image unchanged");
  }
  internal static void Run(string root,Action<bool,string> check){
   var previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
   try{RunCore(root,check);}finally{SynchronizationContext.SetSynchronizationContext(previous);}
  }
  static void RunCore(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);check(MatureCanvasWindow.IsAvailable,"mature host fixture requires the actual bundled canvas entry point");
   ReadinessLifecycle(Path.Combine(root,"readiness-lifecycle"),check);
   var messages=new List<Dictionary<string,object>>();string data=Path.Combine(root,"protocol");var host=new MatureCanvasWindow(data);host.PostForTests=json=>messages.Add(Obj(Json().DeserializeObject(json)));
   try{
    string project=host.ProjectId,file=Manifest(data,project);byte[] initial=File.ReadAllBytes(file);
    Wait(host.HandleMessageAsync("https://example.com/index.html",Message(host,"ready","foreign")));
    Wait(host.HandleMessageAsync("https://nook-canvas.local.evil/index.html",Message(host,"ready","suffix")));
    Wait(host.HandleMessageAsync("http://nook-canvas.local/index.html",Message(host,"ready","plain")));
    Wait(Send(host,"ready","bad request"));
    Wait(host.HandleMessageAsync(MatureCanvasWindow.Origin+"/index.html",Json().Serialize(Map("protocol","other-protocol","type","ready","requestId","wrong"))));
    check(!host.ReadyForTests&&messages.Count==0&&File.ReadAllBytes(file).SequenceEqual(initial),"untrusted sources, invalid requests and wrong protocol never load or write a project");
    Wait(Send(host,"ready","ready"));
    var load=messages.Single(m=>m["type"].ToString()=="load");
    check(host.ReadyForTests&&!host.CanImportImages&&load["session"].ToString()==host.SessionForTests&&Obj(load["payload"])["scene"]!=null,"ready sends native session and scene but does not enable import before loaded ACK");
    Wait(Send(host,"loaded","oldLoaded",Map(),"stale-session"));check(!host.CanImportImages,"old-session loaded ACK cannot enable the current project");
    Wait(Send(host,"loaded","loaded"));check(host.CanImportImages,"matching loaded ACK enables native image import");
    int loads=messages.Count(m=>m["type"].ToString()=="load");Wait(Send(host,"ready","again"));check(messages.Count(m=>m["type"].ToString()=="load")==loads,"duplicate ready never reloads over browser edits");
    var scene=Scene(host);Obj(Records(scene)["document:document"])["name"]="edited fixture";
    Wait(Send(host,"save","save1",Map("scene",scene,"revision",0)));
    var saved=messages.Single(m=>m["type"].ToString()=="saved"&&m["requestId"].ToString()=="save1");
    check(host.RevisionForTests==1&&Convert.ToInt32(Obj(saved["payload"])["revision"])==1,"real native handler acknowledges the expected revision exactly once");
    check(new MatureCanvasStore(data).Load(project).SceneJson.Contains("edited fixture"),"saved ACK follows a durable manifest readable by a fresh store");
    byte[] committed=File.ReadAllBytes(file);Wait(Send(host,"save","stale",Map("scene",scene,"revision",0)));
    check(host.RevisionForTests==1&&File.ReadAllBytes(file).SequenceEqual(committed)&&messages.Any(m=>m["type"].ToString()=="error"&&m["requestId"].ToString()=="stale"),"stale expected revision returns an error without replacing committed bytes");
    Wait(Send(host,"save","stringRevision",Map("scene",scene,"revision","1")));check(host.RevisionForTests==1&&File.ReadAllBytes(file).SequenceEqual(committed),"string revision cannot masquerade as an integer protocol revision");
    Wait(Send(host,"save","oldSession",Map("scene",scene,"revision",1),"stale-session"));check(host.RevisionForTests==1&&File.ReadAllBytes(file).SequenceEqual(committed),"save from an old session cannot update the current project");
   }finally{Dispose(host);}

   string backgroundData=Path.Combine(root,"background-normalization");var backgroundHost=new MatureCanvasWindow(backgroundData);var backgroundMessages=new List<Dictionary<string,object>>();int dispatcherThread=Thread.CurrentThread.ManagedThreadId,offThreadPosts=0;
   backgroundHost.PostForTests=json=>{if(Thread.CurrentThread.ManagedThreadId!=dispatcherThread)Interlocked.Increment(ref offThreadPosts);backgroundMessages.Add(Obj(Json().DeserializeObject(json)));};
   try{
    Load(backgroundHost);var operationGate=(SemaphoreSlim)Field("operations").GetValue(backgroundHost);
    using(var probe=new SerializationProbe()){
     var scene=Scene(backgroundHost);Obj(Records(scene)["document:document"])["meta"]=probe;Task saving=null;bool heartbeat=false,normalizingDuringHeartbeat=false;
     check(operationGate.Wait(0),"background normalization fixture owns the real save serialization gate");
     try{
      saving=SaveStage(backgroundHost,scene,"backgroundSave");Wait(()=>probe.Entered);
      Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Normal,new Action(()=>{heartbeat=true;normalizingDuringHeartbeat=!saving.IsCompleted&&!probe.Released;probe.Release();}));
      Wait(()=>heartbeat);Wait(saving);
      check(probe.Background&&normalizingDuringHeartbeat,"actual scene serialization runs off the Dispatcher while UI input remains processable, without timing-based sleeps");
     }finally{probe.Release();if(saving!=null&&!saving.IsCompleted)Wait(saving);operationGate.Release();}
    }
    check(backgroundHost.RevisionForTests==1&&new MatureCanvasStore(backgroundData).Load(backgroundHost.ProjectId).SceneJson.Contains("nookLegacy")&&offThreadPosts==0,"background normalization commits valid scene bytes once and returns to the UI thread before bridge acknowledgements");
    using(var probe=new SerializationProbe()){
     string oldId=backgroundHost.ProjectId,oldFile=Manifest(backgroundData,oldId);byte[] oldBytes=File.ReadAllBytes(oldFile);var scene=Scene(backgroundHost);Obj(Records(scene)["document:document"])["meta"]=probe;Task saving=null;bool rejected=false;
     check(operationGate.Wait(0),"post-normalization session fixture owns the real save serialization gate");
     try{
      saving=SaveStage(backgroundHost,scene,"supersededDuringNormalize");Wait(()=>probe.Entered);
      var replacement=new MatureCanvasStore(backgroundData).Create("replacement while normalizing");Field("document").SetValue(backgroundHost,replacement);Field("session").SetValue(backgroundHost,Guid.NewGuid().ToString("N"));Field("revision").SetValue(backgroundHost,0);byte[] replacementBytes=File.ReadAllBytes(Manifest(backgroundData,replacement.Id));
      probe.Release();try{Wait(saving);}catch(IOException){rejected=true;}
      check(rejected&&backgroundHost.RevisionForTests==0&&File.ReadAllBytes(oldFile).SequenceEqual(oldBytes)&&File.ReadAllBytes(Manifest(backgroundData,replacement.Id)).SequenceEqual(replacementBytes),"session and document are rechecked after background normalization, preserving both projects when the result becomes stale");
      check(!backgroundMessages.Any(m=>m["type"].ToString()=="saved"&&m["requestId"].ToString()=="supersededDuringNormalize"),"a stale normalization result never acknowledges a save or adopts the replacement session");
     }finally{probe.Release();if(saving!=null&&!saving.IsCompleted)try{Wait(saving);}catch(IOException){}operationGate.Release();}
    }
   }finally{Dispose(backgroundHost);}

   string inlineData=Path.Combine(root,"inline-retry");var inlineHost=new MatureCanvasWindow(inlineData);var inlineMessages=new List<Dictionary<string,object>>();inlineHost.PostForTests=json=>inlineMessages.Add(Obj(Json().DeserializeObject(json)));
   try{
    Load(inlineHost);byte[] raster=Raster();string source="data:image/png;base64,"+Convert.ToBase64String(raster),assetId="asset:inlineFixture";
    check(MatureCanvasWindow.InlineResourceId(assetId,source)==MatureCanvasWindow.InlineResourceId(assetId,source)&&MatureCanvasWindow.InlineResourceId(assetId,source)!=MatureCanvasWindow.InlineResourceId("asset:another",source),"inline retry identity is deterministic and scoped to asset identity and content");
    var badScene=Scene(inlineHost);AddInline(badScene,source);Obj(Records(badScene)["document:document"])["gridSize"]=0;
    string inlineFile=Manifest(inlineData,inlineHost.ProjectId);byte[] before=File.ReadAllBytes(inlineFile);
    Wait(Send(inlineHost,"save","invalidInline",Map("scene",badScene,"revision",0)));
    check(inlineHost.RevisionForTests==0&&File.ReadAllBytes(inlineFile).SequenceEqual(before)&&inlineHost.DocumentForTests.Resources.Count==1,"validation failure after inline staging retains committed scene and one recoverable resource");
    var retry=Scene(inlineHost);AddInline(retry,source);Wait(Send(inlineHost,"save","retryInline",Map("scene",retry,"revision",0)));
    var store=new MatureCanvasStore(inlineData);var persisted=store.Load(inlineHost.ProjectId);var resource=persisted.Resources.Single();
    check(inlineHost.RevisionForTests==1&&persisted.Resources.Count==1&&File.ReadAllBytes(store.ResourcePath(persisted,resource)).SequenceEqual(raster),"valid retry reuses the staged identity and persists exact raster bytes without duplicate files");
    check(!persisted.SceneJson.Contains("base64")&&persisted.SceneJson.Contains(MatureCanvasStore.ResourceUrl(persisted.Id,resource.FileId)),"successful native save normalizes inline source to the current project's durable local resource URL");
    var ack=inlineMessages.Single(m=>m["type"].ToString()=="saved"&&m["requestId"].ToString()=="retryInline");var updates=(object[])Obj(ack["payload"])["assets"];
    check(updates.Length==1&&Obj(updates[0])["id"].ToString()==assetId&&Obj(updates[0])["src"].ToString()==MatureCanvasStore.ResourceUrl(persisted.Id,resource.FileId),"saved ACK identifies exactly which browser asset to normalize");
    var replay=Scene(inlineHost);AddInline(replay,source);Wait(Send(inlineHost,"save","replayInline",Map("scene",replay,"revision",1)));
    check(inlineHost.DocumentForTests.Resources.Count==1&&Directory.GetFiles(Path.GetDirectoryName(store.ResourcePath(persisted,resource))).Length==1,"late duplicate inline capture cannot accumulate duplicate resource files");
   }finally{Dispose(inlineHost);}

   string importData=Path.Combine(root,"native-import"),sourceFile=Path.Combine(root,"native-original.png");byte[] sourceBytes=Raster();File.WriteAllBytes(sourceFile,sourceBytes);DateTime sourceTime=File.GetLastWriteTimeUtc(sourceFile);
   var importHost=new MatureCanvasWindow(importData);string importedProject=importHost.ProjectId;Dictionary<string,object> browserScene=null,importMetadata=null;var importReplies=new List<Task>();var transitions=new List<bool>();int importBridgeThread=0;
   try{
    importHost.PostForTests=json=>{
     var output=Obj(Json().DeserializeObject(json));string type=output["type"].ToString();var payload=Obj(output["payload"]);
     if(type=="load"){browserScene=Obj(payload["scene"]);importReplies.Add(Send(importHost,"loaded","loaded_"+Guid.NewGuid().ToString("N")));}
     else if(type=="flush")importReplies.Add(Send(importHost,"save",output["requestId"].ToString(),Map("scene",browserScene,"revision",importHost.RevisionForTests)));
     else if(type=="interaction")transitions.Add((bool)payload["enabled"]);
     else if(type=="addImages"){
      importMetadata=Obj(((object[])payload["images"])[0]);importBridgeThread=Thread.CurrentThread.ManagedThreadId;
      foreach(var item in (object[])payload["images"])AddImported(browserScene,Obj(item));
      importReplies.Add(Send(importHost,"imagesAdded",output["requestId"].ToString(),Map("count",((object[])payload["images"]).Length)));
     }
    };
    Wait(Send(importHost,"ready","ready"));check(importHost.CanImportImages,"native import fixture loads through the actual host load and loaded handshake");
    Wait(importHost.ImportImagesAsync(new[]{sourceFile}));foreach(var reply in importReplies.ToArray())Wait(reply);
    var importStore=new MatureCanvasStore(importData);var imported=importStore.Load(importedProject);
    check(imported.ElementCount==1&&imported.ImageCount==1&&imported.Resources.Count==1&&importHost.RevisionForTests==2,"native import stages a resource then the browser save durably commits its image shape");
    check(importMetadata!=null&&Convert.ToInt32(importMetadata["width"])==4&&Convert.ToInt32(importMetadata["height"])==3&&importBridgeThread==Thread.CurrentThread.ManagedThreadId,"background image staging returns exact bounded preview dimensions and posts only metadata on the UI thread");
    check(!imported.SceneJson.Contains("base64")&&File.Exists(importStore.ResourcePath(imported,imported.Resources.Single())),"native addImages uses a saved project-local resource without exposing source paths or inline expansion");
    check(transitions.Contains(false)&&transitions.Last()&&importHost.CanImportImages,"native import freezes interaction during flush and staging and re-enables it after import");
    var next=importStore.Create("separate project");var switched=importHost.OpenProjectAsync(next.Id);Wait(switched);foreach(var reply in importReplies.ToArray())Wait(reply);
    check(switched.Result&&importHost.ProjectId==next.Id&&importHost.DocumentForTests.ElementCount==0&&importHost.RevisionForTests==0,"actual project switch flushes old images before loading an independent empty project and resetting revision");
    var returned=importHost.OpenProjectAsync(importedProject);Wait(returned);foreach(var reply in importReplies.ToArray())Wait(reply);
    check(returned.Result&&importHost.ProjectId==importedProject&&importHost.DocumentForTests.ElementCount==1&&importHost.DocumentForTests.Resources.Count==1,"switching back reloads the committed native image rather than an empty or foreign project scene");
    check(File.ReadAllBytes(sourceFile).SequenceEqual(sourceBytes)&&File.GetLastWriteTimeUtc(sourceFile)==sourceTime,"native import, project switch and reload never alter the source file bytes or timestamp");
   }finally{Dispose(importHost);}
   var reopened=new MatureCanvasWindow(importData,importedProject);
   try{Load(reopened);check(reopened.DocumentForTests.ImageCount==1&&reopened.DocumentForTests.Resources.Count==1&&reopened.CanImportImages,"fresh host reopening retains the imported image and enables editing only after loaded ACK");}finally{Dispose(reopened);}

   string queuedData=Path.Combine(root,"queued-session");var queuedHost=new MatureCanvasWindow(queuedData);var queuedMessages=new List<Dictionary<string,object>>();queuedHost.PostForTests=json=>queuedMessages.Add(Obj(Json().DeserializeObject(json)));
   try{
    Load(queuedHost);var oldScene=Scene(queuedHost);Obj(Records(oldScene)["document:document"])["name"]="must not reach next project";string oldId=queuedHost.ProjectId,oldSession=queuedHost.SessionForTests;byte[] oldBytes=File.ReadAllBytes(Manifest(queuedData,oldId));
    var gate=(SemaphoreSlim)Field("operations").GetValue(queuedHost);check(gate.Wait(0),"queued-session fixture holds the actual production serialization gate");
    Task queued;
    try{
     queued=Send(queuedHost,"save","queuedOld",Map("scene",oldScene,"revision",0),oldSession);check(!queued.IsCompleted,"old-session save reaches and waits on the production operation gate");
     var replacement=new MatureCanvasStore(queuedData).Create("next project");Field("document").SetValue(queuedHost,replacement);Field("session").SetValue(queuedHost,Guid.NewGuid().ToString("N"));Field("revision").SetValue(queuedHost,0);
    }finally{gate.Release();}
    string nextId=queuedHost.ProjectId;byte[] nextBytes=File.ReadAllBytes(Manifest(queuedData,nextId));Wait(queued);
    check(queuedHost.RevisionForTests==0&&File.ReadAllBytes(Manifest(queuedData,nextId)).SequenceEqual(nextBytes)&&File.ReadAllBytes(Manifest(queuedData,oldId)).SequenceEqual(oldBytes),"session and document are rechecked after gate acquisition so a queued old save cannot write either project");
    check(!queuedMessages.Any(m=>m["requestId"]!=null&&m["requestId"].ToString()=="queuedOld"),"discarded stale queued save never acknowledges the replacement session");
   }finally{Dispose(queuedHost);}

   string exitData=Path.Combine(root,"flush-exit");var exitHost=new MatureCanvasWindow(exitData);var replies=new List<Task>();int exitCalls=0,flushCount=0;bool preparedInside=false,failFlush=true;
   try{
    exitHost.PostForTests=json=>{var output=Obj(Json().DeserializeObject(json));if(output["type"].ToString()!="flush")return;flushCount++;string request=output["requestId"].ToString();replies.Add(failFlush?Send(exitHost,"error",request,Map("message","fixture capture failure")):Send(exitHost,"save",request,Map("scene",Scene(exitHost),"revision",exitHost.RevisionForTests)));};
    Load(exitHost);exitHost.SaveAndRun(()=>{exitCalls++;preparedInside=exitHost.PreparedForExit;});Wait(()=>exitHost.CanImportImages);foreach(var task in replies)Wait(task);
    check(flushCount==1&&exitCalls==0&&!exitHost.PreparedForExit,"browser flush error releases the pending exit without calling the destructive follow-up action");
    check(((Dictionary<string,TaskCompletionSource<bool>>)Field("flushes").GetValue(exitHost)).Count==0,"failed flush removes its waiter instead of leaking or timing out the next action");
    failFlush=false;exitHost.SaveAndRun(()=>{exitCalls++;preparedInside=exitHost.PreparedForExit;});Wait(()=>exitCalls==1&&exitHost.CanImportImages);foreach(var task in replies)Wait(task);
    check(preparedInside&&!exitHost.PreparedForExit&&exitHost.RevisionForTests==1,"successful follow-up is exit-prepared only during the action and resets when the app stays open");
    exitHost.SaveAndRun(()=>exitCalls++);Wait(()=>exitCalls==2&&exitHost.CanImportImages);foreach(var task in replies)Wait(task);
    check(flushCount==3&&exitHost.RevisionForTests==2&&!exitHost.PreparedForExit,"cancelled or no-op prior exit cannot bypass saving on the next exit attempt");
   }finally{Dispose(exitHost);}

   string failedData=Path.Combine(root,"failed-load");var failedHost=new MatureCanvasWindow(failedData);
   try{
    Wait(Send(failedHost,"ready","ready"));byte[] before=File.ReadAllBytes(Manifest(failedData,failedHost.ProjectId));Wait(Send(failedHost,"error","loadFailure",Map("message","fixture invalid editor snapshot")));
    check(!failedHost.ReadyForTests&&!failedHost.CanImportImages&&File.ReadAllBytes(Manifest(failedData,failedHost.ProjectId)).SequenceEqual(before),"browser load failure leaves saved project intact and disables the unavailable engine");
    bool exited=false;failedHost.SaveAndRun(()=>exited=true);check(exited&&!failedHost.PreparedForExit,"unavailable engine cannot trap closing in an impossible flush loop");
   }finally{Dispose(failedHost);}

   string untouchedData=Path.Combine(root,"close-before-dirty-notification");var untouchedHost=new MatureCanvasWindow(untouchedData);int untouchedFlushes=0;var untouchedReplies=new List<Task>();
   try{
    untouchedHost.PostForTests=json=>{var output=Obj(Json().DeserializeObject(json));if(output["type"].ToString()!="flush")return;untouchedFlushes++;untouchedReplies.Add(Send(untouchedHost,"error",output["requestId"].ToString(),Map("message","fixture unavailable capture before deferred dirty notification")));};Load(untouchedHost);
    string file=Manifest(untouchedData,untouchedHost.ProjectId);byte[] before=File.ReadAllBytes(file);
    var close=new CancelEventArgs();typeof(MatureCanvasWindow).GetMethod("ClosingCanvas",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(untouchedHost,new object[]{untouchedHost,close});Wait(()=>untouchedHost.CanImportImages);foreach(var reply in untouchedReplies.ToArray())Wait(reply);
    check(close.Cancel&&untouchedFlushes==1&&File.ReadAllBytes(file).SequenceEqual(before),"actual X-close must flush even without a prior dirty message: deferred browser edits cannot be silently discarded when capture fails");
   }finally{Dispose(untouchedHost);}

   string closeDirtyData=Path.Combine(root,"dirty-close-retry");var closeDirtyHost=new MatureCanvasWindow(closeDirtyData);var dirtyReplies=new List<Task>();bool dirtyFail=true;int dirtyFlushes=0;
   try{
    closeDirtyHost.PostForTests=json=>{var output=Obj(Json().DeserializeObject(json));if(output["type"].ToString()!="flush")return;dirtyFlushes++;dirtyReplies.Add(dirtyFail?Send(closeDirtyHost,"error",output["requestId"].ToString(),Map("message","fixture capture failed")):Send(closeDirtyHost,"save",output["requestId"].ToString(),Map("scene",Scene(closeDirtyHost),"revision",closeDirtyHost.RevisionForTests)));};Load(closeDirtyHost);
    string file=Manifest(closeDirtyData,closeDirtyHost.ProjectId);byte[] before=File.ReadAllBytes(file);Wait(Send(closeDirtyHost,"dirty","userEdit"));
    var close=new CancelEventArgs();typeof(MatureCanvasWindow).GetMethod("ClosingCanvas",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(closeDirtyHost,new object[]{closeDirtyHost,close});Wait(()=>closeDirtyHost.CanImportImages);foreach(var reply in dirtyReplies.ToArray())Wait(reply);
    check(close.Cancel&&dirtyFlushes==1&&!closeDirtyHost.PreparedForExit&&File.ReadAllBytes(file).SequenceEqual(before),"actual X-close protects observed user edits when browser capture fails and leaves the committed project untouched");
    dirtyFail=false;bool followup=false;closeDirtyHost.SaveAndRun(()=>followup=true);Wait(()=>followup&&closeDirtyHost.CanImportImages);foreach(var reply in dirtyReplies.ToArray())Wait(reply);
    check(dirtyFlushes==2&&closeDirtyHost.RevisionForTests==1,"a failed close remains retryable and a later successful flush safely releases its follow-up action");
    dirtyFail=true;followup=false;closeDirtyHost.SaveAndRun(()=>followup=true);Wait(()=>closeDirtyHost.CanImportImages);foreach(var reply in dirtyReplies.ToArray())Wait(reply);
    check(!followup&&dirtyFlushes==3,"a previous durable ACK never incorrectly marks a potentially edited browser session as safe to discard");
   }finally{Dispose(closeDirtyHost);}

   string transportData=Path.Combine(root,"flush-send-failure");var transportHost=new MatureCanvasWindow(transportData);var transportReplies=new List<Task>();bool rejectTransport=false;int transportActions=0;
   try{
    transportHost.PostForTests=json=>{var output=Obj(Json().DeserializeObject(json));string type=output["type"].ToString();if(type=="interaction"&&rejectTransport)throw new IOException("fixture disconnected interaction channel");if(type!="flush")return;if(rejectTransport)throw new IOException("fixture disconnected flush channel");transportReplies.Add(Send(transportHost,"save",output["requestId"].ToString(),Map("scene",Scene(transportHost),"revision",transportHost.RevisionForTests)));};Load(transportHost);Wait(Send(transportHost,"dirty","editBeforeDisconnect"));
    rejectTransport=true;transportHost.SaveAndRun(()=>transportActions++);Wait(()=>transportHost.CanImportImages);
    check(transportActions==0&&((Dictionary<string,TaskCompletionSource<bool>>)Field("flushes").GetValue(transportHost)).Count==0&&!transportHost.PreparedForExit,"a synchronous bridge-send failure releases its exit waiter and transition without invoking the follow-up or crashing the UI");
    typeof(MatureCanvasWindow).GetMethod("SaveNow",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(transportHost,null);Wait(()=>transportHost.CanImportImages);
    check(((Dictionary<string,TaskCompletionSource<bool>>)Field("flushes").GetValue(transportHost)).Count==0&&!transportHost.PreparedForExit,"the actual manual-save action handles a disconnected bridge without an async-void exception or leaked waiter");
    rejectTransport=false;transportHost.SaveAndRun(()=>transportActions++);Wait(()=>transportActions==1&&transportHost.CanImportImages);foreach(var reply in transportReplies.ToArray())Wait(reply);
    check(transportHost.RevisionForTests==1&&((Dictionary<string,TaskCompletionSource<bool>>)Field("flushes").GetValue(transportHost)).Count==0,"restored bridge transport can save and complete an exit after a failed send, without stale waiters");
    transportHost.PostForTests=json=>{if(Obj(Json().DeserializeObject(json))["type"].ToString()=="error")throw new IOException("fixture disconnected error channel");};Wait(Send(transportHost,"save","badAfterDisconnect",Map("scene",Map(),"revision",transportHost.RevisionForTests)));
    check(transportHost.RevisionForTests==1,"reporting a malformed save over a disconnected channel remains handled and cannot change the committed revision");
   }finally{Dispose(transportHost);}

   string callbackData=Path.Combine(root,"save-list-refresh-failure");var callbackHost=new MatureCanvasWindow(callbackData,null,null,()=>{throw new IOException("fixture list refresh unavailable");});var callbackMessages=new List<Dictionary<string,object>>();var callbackReplies=new List<Task>();bool callbackAction=false;
   try{
    callbackHost.PostForTests=json=>{var output=Obj(Json().DeserializeObject(json));callbackMessages.Add(output);if(output["type"].ToString()=="flush")callbackReplies.Add(Send(callbackHost,"save",output["requestId"].ToString(),Map("scene",Scene(callbackHost),"revision",callbackHost.RevisionForTests)));};Load(callbackHost);Wait(Send(callbackHost,"dirty","userEdit"));callbackHost.SaveAndRun(()=>callbackAction=true);Wait(()=>callbackAction&&callbackHost.CanImportImages);foreach(var reply in callbackReplies.ToArray())Wait(reply);
    check(callbackHost.RevisionForTests==1&&new MatureCanvasStore(callbackData).Load(callbackHost.ProjectId).SceneJson==callbackHost.DocumentForTests.SceneJson&&callbackMessages.Any(m=>m["type"].ToString()=="saved")&&!callbackMessages.Any(m=>m["type"].ToString()=="error"),"project-list refresh failures cannot turn a durable canvas save into a failed ACK or trap a safe close");
   }finally{Dispose(callbackHost);}

   string resourceData=Path.Combine(root,"local-resource-routing");var resourceHost=new MatureCanvasWindow(resourceData);
   try{
    string bundle=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"web","canvas");
    var expectedTypes=new Dictionary<string,string>{{".html","text/html; charset=utf-8"},{".js","application/javascript; charset=utf-8"},{".css","text/css; charset=utf-8"},{".json","application/json; charset=utf-8"},{".svg","image/svg+xml"},{".png","image/png"},{".woff2","font/woff2"}};
    foreach(var pair in expectedTypes){string path=pair.Key==".json"?Path.Combine(bundle,"tl-assets","translations","zh-cn.json"):Directory.GetFiles(bundle,"*"+pair.Key,SearchOption.AllDirectories).First();string relative="/"+path.Substring(bundle.Length+1).Replace('\\','/');var response=resourceHost.ReadCanvasResource(MatureCanvasWindow.Origin+relative);check(response.StatusCode==200&&response.ContentType==pair.Value&&response.Bytes.SequenceEqual(File.ReadAllBytes(path))&&!response.Dynamic&&response.CacheControl=="no-store","unified local route serves exact bundled "+pair.Key+" bytes with the correct MIME type and never pins an old application bundle in the WebView cache");}
    foreach(string directory in new[]{"assets","tl-assets"})foreach(string path in Directory.GetFiles(Path.Combine(bundle,directory),"*",SearchOption.AllDirectories)){string relative="/"+path.Substring(bundle.Length+1).Replace('\\','/');var response=resourceHost.ReadCanvasResource(MatureCanvasWindow.Origin+relative);string expected;check(expectedTypes.TryGetValue(Path.GetExtension(path),out expected)&&response.StatusCode==200&&response.ContentType==expected&&response.Bytes.SequenceEqual(File.ReadAllBytes(path))&&!response.Dynamic&&response.CacheControl=="no-store","every bundled runtime dependency is served exactly through the unified route: "+relative);}
    check(resourceHost.ReadCanvasResource(MatureCanvasWindow.Origin+"/BUILD-INFO.json").StatusCode==404,"build provenance metadata is not accidentally exposed as a runtime dependency");
    string source=Path.Combine(resourceData,"resource-original.png");byte[] original=Raster();File.WriteAllBytes(source,original);var resourceStore=new MatureCanvasStore(resourceData);var image=resourceStore.RegisterImage(resourceHost.DocumentForTests,source);resourceStore.Save(resourceHost.DocumentForTests);string url=MatureCanvasStore.ResourceUrl(resourceHost.ProjectId,image.FileId);
    var local=resourceHost.ReadCanvasResource(url);using(var stream=new MemoryStream(local.Bytes??new byte[0]))using(var bitmap=new Bitmap(stream)){check(local.StatusCode==200&&local.ContentType=="image/png"&&local.Dynamic&&local.CacheControl=="private, max-age=86400"&&bitmap.Width==4&&bitmap.Height==3,"the exact URL used by canvas image assets returns a decodable bounded PNG through the production resource route");}
    check(resourceHost.ReadCanvasResource(url).StatusCode==200&&File.ReadAllBytes(source).SequenceEqual(original),"repeated canvas-resource reads are non-destructive and never modify the original image");
    string otherProject=resourceStore.Create("not current").Id;check(resourceHost.ReadCanvasResource(MatureCanvasStore.ResourceUrl(otherProject,image.FileId)).StatusCode==404,"dynamic image routes cannot read another project's resources");
    check(resourceHost.ReadCanvasResource(MatureCanvasWindow.Origin+"/resource/"+resourceHost.ProjectId+"/not_registered").StatusCode==404,"unregistered dynamic images return a controlled 404 instead of exposing arbitrary files");
    foreach(string invalid in new[]{"https://example.com/index.html","https://nook-canvas.local.evil/index.html","http://nook-canvas.local/index.html","file:///C:/private.txt"})check(resourceHost.ReadCanvasResource(invalid).StatusCode==403,"the unified resource route refuses foreign or native-file origin: "+invalid);
    foreach(string invalid in new[]{"/index.html?cache=1","/index.html#fragment","/assets/../index.html","/assets/%2e%2e/index.html","/assets/%2findex.html","/assets/%5cindex.html","/assets/../../private.txt","/assets/index.js:secret","//index.html","/resource/"+resourceHost.ProjectId+"/../index.html"})check(resourceHost.ReadCanvasResource(MatureCanvasWindow.Origin+invalid).StatusCode==404,"the unified resource route rejects query, fragment, traversal or alternate-stream paths: "+invalid);
    check(resourceHost.ReadCanvasResource(MatureCanvasWindow.Origin+"/index.html","POST").StatusCode==405&&resourceHost.ReadCanvasResource(MatureCanvasWindow.Origin+"/unknown.exe").StatusCode==404,"local resources are read-only GET routes and unknown bundle paths cannot be served");
    string unusedType;check(MatureCanvasWindow.BundledCanvasFile(bundle,"/assets/../../../private.txt",out unusedType)==null&&MatureCanvasWindow.BundledCanvasFile(bundle,"/assets//index.js",out unusedType)==null&&MatureCanvasWindow.BundledCanvasFile(bundle,"/assets/private.exe",out unusedType)==null,"static path resolution independently rejects directory escape, empty segments and unsupported executable formats");
    var worker=Task.Run(()=>resourceHost.ReadCanvasResource(url));Wait(worker);check(worker.Result.StatusCode==200&&worker.Result.Bytes.Length>8,"the production resource decoder can safely run off the UI thread before the WebView response is completed");
   }finally{Dispose(resourceHost);}
   Uri safe;
   check(MatureCanvasWindow.SafeExternalUrl("https://example.com/reference",out safe)&&MatureCanvasWindow.SafeExternalUrl("http://example.com/reference",out safe),"explicit user link navigation allows ordinary web references");
   check(!MatureCanvasWindow.SafeExternalUrl("file:///C:/private.txt",out safe)&&!MatureCanvasWindow.SafeExternalUrl("javascript:alert(1)",out safe)&&!MatureCanvasWindow.SafeExternalUrl("https://user:secret@example.com/",out safe),"link navigation rejects native files, script protocols and embedded credentials");
  }
 }
}
