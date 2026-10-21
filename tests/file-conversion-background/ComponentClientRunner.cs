using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using MoriDesk;

// Injected request/reply protocol only. The fake executable is never launched;
// component roots, response files and sentinels stay in the new fixture folder.
internal static class ComponentClientRunner {
 static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=4*1024*1024};
 static string root,worker,components;static int checks;
 sealed class ProgressLog:IProgress<ConversionComponentProgress>{
  internal readonly List<ConversionComponentProgress> Values=new List<ConversionComponentProgress>();
  public void Report(ConversionComponentProgress value){lock(Values)Values.Add(value);}
 }
 static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;Console.WriteLine("PASS "+message);}
 static void Reject(Action action,string message){try{action();}catch{Check(true,message);return;}throw new Exception(message+" was not rejected");}
 static string Fresh(string name){return Path.Combine(root,name);}
 static bool Empty(string path){return !Directory.Exists(path)||!Directory.EnumerateFileSystemEntries(path).Any();}
 static FileConversionClient.WorkerComponent Item(string id="ffmpeg",string state="missing"){
  return new FileConversionClient.WorkerComponent{id=id,name="视频与音频",description="fixture",state=state,installedVersion=state=="missing"?"":"1.0",
   availableVersion="2.0",downloadBytes=12345,installedBytes=state=="missing"?0:67890,canUninstall=state!="missing",dependencies=new string[0]};
 }
 static FileConversionClient.WorkerRequest Read(string request){return Json.Deserialize<FileConversionClient.WorkerRequest>(File.ReadAllText(request));}
 static FileConversionClient.WorkerResponse Reply(FileConversionClient.WorkerRequest request){
  return new FileConversionClient.WorkerResponse{schemaVersion=1,id=request.id,operation=request.operation,status="ok",components=new[]{Item()}};
 }
 static void Write(FileConversionClient.WorkerRequest request,FileConversionClient.WorkerResponse response){File.WriteAllText(request.responsePath,Json.Serialize(response),new UTF8Encoding(false));}
 static FileConversionClient Client(string name,Func<string,CancellationToken,int,Task<int>> runner){return new FileConversionClient(worker,Fresh(name),runner,components);}
 static void Validation(){
  foreach(string id in new[]{"ffmpeg","libreoffice","calibre","pandoc","ghostscript","7zip","rhwp","jre","h2orestart","vcruntime"})
   Check(FileConversionClient.IsComponentId(id),"approved component ID retained: "+id);
  foreach(string id in new[]{"FFMPEG","../ffmpeg","unknown","https://example.invalid/download.exe","",null})
   Check(!FileConversionClient.IsComponentId(id),"untrusted component identifier rejected");
  var source=Item("libreoffice","update-available");source.dependencies=new[]{"jre"};source.message=null;
  var parsed=FileConversionClient.ValidateComponents(new[]{source}).Single();
  source.dependencies[0]="pandoc";
  Check(parsed.Dependencies.SequenceEqual(new[]{"jre"})&&parsed.Message==""&&parsed.InstalledVersion=="1.0"&&parsed.AvailableVersion=="2.0","component DTO copies dependency lists and retains separate installed and approved versions");
  Reject(()=>FileConversionClient.ValidateComponents(null),"missing component array rejected");
  Reject(()=>FileConversionClient.ValidateComponents(new FileConversionClient.WorkerComponent[]{null}),"null component record rejected");
  Reject(()=>FileConversionClient.ValidateComponents(new[]{Item(),Item()}),"duplicate component IDs rejected");
  Reject(()=>FileConversionClient.ValidateComponents(Enumerable.Range(0,11).Select(i=>Item()).ToArray()),"oversized component list rejected");
  var cases=new Action<FileConversionClient.WorkerComponent>[]{
   item=>item.id="arbitrary-installer",item=>item.name=" ",item=>item.state="success-but-unknown",
   item=>item.downloadBytes=-1,item=>item.installedBytes=-1,item=>item.downloadBytes=16L*1024*1024*1024+1,
   item=>item.installedBytes=32L*1024*1024*1024+1,item=>item.dependencies=null,item=>item.dependencies=new[]{"unknown"},
   item=>item.dependencies=new[]{"ffmpeg"},item=>item.dependencies=new[]{"jre","jre"}
  };
  foreach(var change in cases){var item=Item();change(item);Reject(()=>FileConversionClient.ValidateComponents(new[]{item}),"invalid component metadata rejected before UI actions");}
 }
 static void Requests(){
  int calls=0;string expectedAction="components-list";string expectedId=null;
  var client=Client("requests",(path,token,timeout)=>{
   calls++;var request=Read(path);
   Check(request.operation==expectedAction&&request.componentId==expectedId,"only the requested component operation reaches the worker");
   Check(request.inputs.Length==0&&request.componentRoot==components,"management request never includes material files and uses the explicit isolated component root");
   Check(timeout==(expectedAction=="components-list"?45000:7200000),"checking and explicit installation use their separate bounded worker timeouts");
   var response=Reply(request);
   if(expectedAction=="components-install"||expectedAction=="components-update")response.components=new[]{Item("ffmpeg","installed")};
   Write(request,response);return Task.FromResult(0);
  });
  Check(client.ListComponentsAsync(CancellationToken.None).Result.Single().Id=="ffmpeg","valid list reply reaches host");
  Check(Empty(Fresh("requests")),"listing removes its owned transient request directory");
  foreach(string action in new[]{"install","update","uninstall"}){
   expectedAction="components-"+action;expectedId="ffmpeg";
   Check(client.ManageComponentAsync("ffmpeg",action,null,CancellationToken.None).Result.Length==1,"valid "+action+" preserves worker's durable final component state");
   Check(Empty(Fresh("requests")),"completed "+action+" cleans its exact session");
  }
  int before=calls;
  foreach(var pair in new[]{new[]{"unknown","install"},new[]{"ffmpeg","download"},new[]{"FFMPEG","install"},new[]{"../ffmpeg","uninstall"}})
   Reject(()=>client.ManageComponentAsync(pair[0],pair[1],null,CancellationToken.None).GetAwaiter().GetResult(),"unsupported component or action cannot start a worker");
  Check(calls==before,"rejected component operations have no subprocess or download side effects");
  using(var cancelled=new CancellationTokenSource()){
   cancelled.Cancel();var fresh=Client("precancelled",(p,t,m)=>{calls++;throw new Exception("must not execute");});
   Reject(()=>fresh.ListComponentsAsync(cancelled.Token).GetAwaiter().GetResult(),"precancelled component check is cancelled");
   Check(calls==before&&!Directory.Exists(Fresh("precancelled")),"precancelled check creates neither session nor component files");
  }
  var missing=new FileConversionClient(Fresh("missing-worker.exe"),Fresh("missing-sessions"),(p,t,m)=>{calls++;throw new Exception("must not execute");},components);
  Reject(()=>missing.ListComponentsAsync(CancellationToken.None).GetAwaiter().GetResult(),"missing core clearly rejects management before launching");
  Check(calls==before&&!Directory.Exists(Fresh("missing-sessions")),"missing core does not create fake installed state or sessions");
 }
 static void BadReplies(){
  var mutations=new Action<FileConversionClient.WorkerResponse>[]{
   reply=>reply.id=Guid.NewGuid().ToString("N"),reply=>reply.schemaVersion=2,reply=>reply.operation="components-install",
   reply=>reply.status="error",reply=>reply.components=null,reply=>reply.components=new[]{Item("unknown")},
   reply=>reply.components=new[]{Item(),Item()}
  };
  int index=0;foreach(var mutate in mutations){
   string name="bad-reply-"+index++;
   var client=Client(name,(path,token,timeout)=>{var request=Read(path);var response=Reply(request);mutate(response);Write(request,response);return Task.FromResult(0);});
   Reject(()=>client.ListComponentsAsync(CancellationToken.None).GetAwaiter().GetResult(),"mismatched or invalid component response is never presented as success");
   Check(Empty(Fresh(name)),"rejected component response cleans only its owned session");
  }
  var absent=Client("absent-reply",(path,token,timeout)=>Task.FromResult(0));
  Reject(()=>absent.ListComponentsAsync(CancellationToken.None).GetAwaiter().GetResult(),"zero exit without durable response cannot claim successful installation");
  var oversized=Client("oversized-reply",(path,token,timeout)=>{var request=Read(path);File.WriteAllText(request.responsePath,new string('x',2*1024*1024+1));return Task.FromResult(0);});
  Reject(()=>oversized.ListComponentsAsync(CancellationToken.None).GetAwaiter().GetResult(),"oversized component response is rejected without deserializing unbounded content");
  var failedExit=Client("exit-error",(path,token,timeout)=>{var request=Read(path);Write(request,Reply(request));return Task.FromResult(2);});
  Reject(()=>failedExit.ListComponentsAsync(CancellationToken.None).GetAwaiter().GetResult(),"error worker exit cannot be disguised by an ok response");
  Check(Empty(Fresh("absent-reply"))&&Empty(Fresh("oversized-reply"))&&Empty(Fresh("exit-error")),"all failed-response sessions are reclaimed");
 }
 static void CancellationAndProgress(){
  using(var cancellation=new CancellationTokenSource()){
   var committed=Client("committed-throw-cancel",(path,token,timeout)=>{
    var request=Read(path);var response=Reply(request);response.components=new[]{Item("ffmpeg","installed")};Write(request,response);
    cancellation.Cancel();throw new OperationCanceledException(token);
   });
   var result=committed.ManageComponentAsync("ffmpeg","install",null,cancellation.Token).GetAwaiter().GetResult();
   Check(result.Single().State=="installed","durable commit survives a runner cancellation exception after publication");
  }
  using(var cancellation=new CancellationTokenSource()){
   var committed=Client("committed-cancel",(path,token,timeout)=>{
    var request=Read(path);var response=Reply(request);response.components=new[]{Item("ffmpeg","installed")};Write(request,response);
    cancellation.Cancel();return Task.FromResult(0);
   });
   var result=committed.ManageComponentAsync("ffmpeg","install",null,cancellation.Token).GetAwaiter().GetResult();
   Check(result.Single().State=="installed","durably committed success wins cancellation after the worker publishes its result");
  }
  using(var cancellation=new CancellationTokenSource()){
   var cancelled=Client("cancelled-reply",(path,token,timeout)=>{var request=Read(path);var response=Reply(request);response.status="cancelled";Write(request,response);cancellation.Cancel();return Task.FromResult(3);});
   bool exact=false;try{cancelled.ManageComponentAsync("ffmpeg","install",null,cancellation.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){exact=true;}
   Check(exact&&Empty(Fresh("cancelled-reply")),"cancelled worker reply reaches caller as cancellation, not success, and cleans session");
  }
  var progress=new ProgressLog();
  var reporting=Client("progress",async(path,token,timeout)=>{
   var request=Read(path);string progressPath=Path.Combine(Path.GetDirectoryName(path),"progress.json");
   File.WriteAllText(progressPath,Json.Serialize(new{stage="download",componentId="ffmpeg",bytesDone=50,bytesTotal=100,fraction=.5}));
   await Task.Delay(650);
   var response=Reply(request);response.components=new[]{Item("ffmpeg","installed")};Write(request,response);return 0;
  });
  reporting.ManageComponentAsync("ffmpeg","install",progress,CancellationToken.None).GetAwaiter().GetResult();
  lock(progress.Values)Check(progress.Values.Any(value=>value.ComponentId=="ffmpeg"&&value.BytesDone==50&&value.BytesTotal==100&&value.Fraction==.5),"bounded worker progress reaches the requested operation without desktop UI");
  Check(Empty(Fresh("progress")),"progress polling stops and its session is reclaimed after completion");
 }
 static int Main(string[] args){
  try{
   if(args.Length!=1)throw new ArgumentException("ComponentClientRunner <new-fixture-directory>");
   root=Path.GetFullPath(args[0]);if(Directory.Exists(root)||File.Exists(root))throw new IOException("Use a fresh fixture directory.");
   Directory.CreateDirectory(root);worker=Fresh("fake-worker.exe");File.WriteAllText(worker,"NEVER EXECUTE");
   components=Fresh("isolated-components");Directory.CreateDirectory(components);string sentinel=Path.Combine(components,"keep-original.bin");File.WriteAllText(sentinel,"ORIGINAL COMPONENT SENTINEL");
   Validation();Requests();BadReplies();CancellationAndProgress();
   Check(File.ReadAllText(sentinel)=="ORIGINAL COMPONENT SENTINEL"&&Directory.GetFileSystemEntries(components).Length==1,"host protocol never installs, deletes or mutates components by itself");
   Console.WriteLine("Component client contract: "+checks+" passed; 0 failed. No actual worker, download or user component store.");return 0;
  }catch(Exception error){Console.Error.WriteLine(error);return 1;}
 }
}
