using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Threading;

namespace MoriDesk {
 internal static class MatureCanvasCloseFailureTests {
  static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=128*1024*1024};}
  static Dictionary<string,object> Map(params object[] pairs){var map=new Dictionary<string,object>();for(int i=0;i<pairs.Length;i+=2)map.Add((string)pairs[i],pairs[i+1]);return map;}
  static Dictionary<string,object> Obj(object value){return (Dictionary<string,object>)value;}
  static Task Send(MatureCanvasWindow host,string type,string request,object payload){return host.HandleMessageAsync(MatureCanvasWindow.Origin,Json().Serialize(Map("protocol",MatureCanvasWindow.Protocol,"session",host.SessionForTests,"type",type,"requestId",request,"payload",payload)));}
  static void Until(Func<bool> complete){var end=DateTime.UtcNow.AddSeconds(25);while(!complete()&&DateTime.UtcNow<end){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);Thread.Sleep(1);}if(!complete())throw new Exception("close fixture timed out");}
  static void Wait(Task task){Until(()=>task.IsCompleted);task.GetAwaiter().GetResult();}
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);var previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
   try{
    foreach(string outcome in new[]{"keep","discard","export-cancel","export-fail","export-ok","save-ok"}){
     string data=Path.Combine(root,outcome);var host=new MatureCanvasWindow(data);var replies=new List<Task>();int prompts=0,exports=0;bool available=false,closed=false;host.Closed+=(s,e)=>closed=true;
     try{
      Wait(Send(host,"ready","ready",Map()));Wait(Send(host,"loaded","loaded",Map()));
      string file=Path.Combine(new MatureCanvasStore(data).Root,host.ProjectId,MatureCanvasStore.ManifestName);byte[] before=File.ReadAllBytes(file);
      host.PostForTests=json=>{var message=Obj(Json().DeserializeObject(json));if((string)message["type"]!="flush")return;var scene=Obj(Json().DeserializeObject(host.DocumentForTests.SceneJson));if(outcome!="save-ok")Obj(Obj(Obj(scene["document"])["store"])["document:document"])["unsupportedField"]="fixture edit";replies.Add(Send(host,"save",(string)message["requestId"],Map("scene",scene,"revision",host.RevisionForTests)));};
      host.FailedCloseChoiceForTests=canExport=>{prompts++;available=canExport;return outcome=="discard"?MatureCanvasWindow.FailedCloseChoice.Discard:outcome.StartsWith("export-")?MatureCanvasWindow.FailedCloseChoice.ExportRecovery:MatureCanvasWindow.FailedCloseChoice.KeepOpen;};
      host.ExportRecoveryForTests=()=>{exports++;if(outcome=="export-fail")throw new IOException("fixture full disk");return Task.FromResult(outcome=="export-ok");};
      var close=host.PrepareCloseAsync();Wait(close);foreach(var reply in replies)Wait(reply);
      bool shouldClose=outcome=="discard"||outcome=="export-ok"||outcome=="save-ok";
      check(close.Result==shouldClose&&host.PreparedForExit==shouldClose,"close result requires durable save or an explicit successful recovery/discard choice: "+outcome);
      check(prompts==(outcome=="save-ok"?0:1)&&(outcome=="save-ok"||available),"only failed saves show choice, with a current rejected snapshot for export: "+outcome);
      check(exports==(outcome.StartsWith("export-")?1:0),"export is never triggered by cancel/discard/success: "+outcome);
      if(outcome!="save-ok")check(File.ReadAllBytes(file).SequenceEqual(before),"failed save/close never replaces the committed project: "+outcome);
      if(shouldClose){host.Close();check(closed,"prepared close actually releases the WPF window: "+outcome);}else check(host.CanImportImages&&!closed,"cancelled/failed export returns control to the editable canvas: "+outcome);
     }finally{host.PostForTests=null;typeof(MatureCanvasWindow).GetField("ready",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(host,false);if(!closed)host.Close();}
    }
    var exitHost=new MatureCanvasWindow(Path.Combine(root,"app-exit"));int actions=0;bool prepared=false;
    try{
     Wait(Send(exitHost,"ready","ready",Map()));Wait(Send(exitHost,"loaded","loaded",Map()));
     exitHost.PostForTests=message=>{var output=Obj(Json().DeserializeObject(message));if((string)output["type"]=="flush")Wait(Send(exitHost,"error",(string)output["requestId"],Map("message","fixture flush failed")));};
     exitHost.FailedCloseChoiceForTests=available=>MatureCanvasWindow.FailedCloseChoice.Discard;
     exitHost.CloseAndRun(()=>{actions++;prepared=exitHost.PreparedForExit;});Until(()=>exitHost.CanImportImages);
     check(actions==1&&prepared&&!exitHost.PreparedForExit&&exitHost.CanImportImages,"application exit can discard explicitly and resets when another window cancels exit");
     exitHost.FailedCloseChoiceForTests=available=>MatureCanvasWindow.FailedCloseChoice.KeepOpen;exitHost.CloseAndRun(()=>actions++);Until(()=>exitHost.CanImportImages);
     check(actions==1&&!exitHost.PreparedForExit&&exitHost.CanImportImages,"next application exit cannot reuse a prior discard decision");
    }finally{exitHost.PostForTests=null;typeof(MatureCanvasWindow).GetField("ready",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(exitHost,false);exitHost.Close();}
    var store=new MatureCanvasStore(Path.Combine(root,"recovery-data"));var doc=store.Create("recovery");string original=Path.Combine(root,"original.png");using(var image=new System.Drawing.Bitmap(8,6))image.Save(original,System.Drawing.Imaging.ImageFormat.Png);var resource=store.RegisterImage(doc,original);store.Save(doc);
    string manifest=Path.Combine(store.Root,doc.Id,MatureCanvasStore.ManifestName);byte[] durable=File.ReadAllBytes(manifest);string rejectedScene=doc.SceneJson.Replace("\"gridSize\":10","\"gridSize\":0"),zip=Path.Combine(root,"recovery.zip");store.ExportRecovery(doc,rejectedScene,zip);
    using(var archive=ZipFile.OpenRead(zip)){using(var reader=new StreamReader(archive.GetEntry(MatureCanvasStore.ManifestName).Open()))check(Json().Deserialize<MatureCanvasDocument>(reader.ReadToEnd()).SceneJson==rejectedScene,"recovery exports the failed snapshot rather than the old committed scene");using(var input=archive.GetEntry("editor-images/"+resource.Image).Open())using(var bytes=new MemoryStream()){input.CopyTo(bytes);check(bytes.ToArray().SequenceEqual(File.ReadAllBytes(store.ResourcePath(doc,resource))),"recovery contains exact registered image bytes");}}
    check(File.ReadAllBytes(manifest).SequenceEqual(durable),"recovery never rewrites the active manifest");bool blocked=false;try{store.ExportRecovery(doc,rejectedScene,manifest);}catch(IOException){blocked=true;}check(blocked&&File.ReadAllBytes(manifest).SequenceEqual(durable),"recovery refuses export over any active project path");
   }finally{SynchronizationContext.SetSynchronizationContext(previous);}
  }
 }
}
