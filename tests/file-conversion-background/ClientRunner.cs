using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using MoriDesk;

// Compile with FileConversionClient.cs, without the Nook entry point or any UI.
// Every file and child process belongs to the explicitly supplied fresh fixture.
internal static class ClientRunner {
 static int checks;
 static string root,worker,source;
 static readonly JavaScriptSerializer Json=new JavaScriptSerializer { MaxJsonLength=4*1024*1024 };
 sealed class ImmediateProgress:IProgress<ConversionProgress>{
  readonly Action<ConversionProgress> action;
  internal ImmediateProgress(Action<ConversionProgress> action){this.action=action;}
  public void Report(ConversionProgress value){action(value);}
 }
 sealed class NonPumpingContext:SynchronizationContext {
  internal int Posts;
  public override void Post(SendOrPostCallback action,object state){Interlocked.Increment(ref Posts);}
 }
 static void Check(bool condition,string message){
  if(!condition)throw new Exception("FAIL "+message);
  checks++;Console.WriteLine("PASS "+message);
 }
 static void Throws<T>(Action action,string message) where T:Exception{
  try{action();}catch(T){Check(true,message);return;}
  throw new Exception("FAIL "+message+" (no "+typeof(T).Name+")");
 }
 static string Folder(string name){string path=Path.Combine(root,name);Directory.CreateDirectory(path);return path;}
 static string Write(string path,string content){Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,content,new UTF8Encoding(false));return path;}
 static bool Empty(string path){return !Directory.Exists(path)||!Directory.EnumerateFileSystemEntries(path).Any();}
 static FileConversionClient.WorkerRequest ReadRequest(string path){return Json.Deserialize<FileConversionClient.WorkerRequest>(File.ReadAllText(path));}
 static FileConversionClient.WorkerResponse Reply(FileConversionClient.WorkerRequest request){
  return new FileConversionClient.WorkerResponse {
   schemaVersion=1,id=request.id,operation=request.operation,status="ok",
   targets=new[]{new FileConversionClient.WorkerTarget{id="jpg",extension="jpg",label="JPEG"}},
   outputs=new string[0],files=request.inputs.Select(p=>new FileConversionClient.WorkerFile{
    source=p,format="png",targets=new[]{"jpg"},outputs=new string[0],warnings=new string[0]
   }).ToArray()
  };
 }
 static void Respond(FileConversionClient.WorkerRequest request,FileConversionClient.WorkerResponse response){File.WriteAllText(request.responsePath,Json.Serialize(response),new UTF8Encoding(false));}
 static void Outputs(FileConversionClient.WorkerResponse response,params string[] paths){response.outputs=paths;response.files[0].outputs=paths;}
 static FileConversionClient Client(string name,Func<string,CancellationToken,int,Task<int>> run){
  return new FileConversionClient(worker,Path.Combine(root,name,"sessions"),run);
 }
 static FileConversionClient Fake(string name,Action<FileConversionClient.WorkerRequest,FileConversionClient.WorkerResponse> mutate,int exit){
  return Client(name,(path,token,timeout)=>{
   var request=ReadRequest(path);var response=Reply(request);
   if(mutate!=null)mutate(request,response);Respond(request,response);return Task.FromResult(exit);
  });
 }
 static ConversionBatchResult Convert(FileConversionClient client,string destination,CancellationToken token){
  return client.ConvertAsync(new[]{source},"jpg",destination,90,null,token).GetAwaiter().GetResult();
 }
 static void InputTests(){
  Throws<ArgumentException>(()=>FileConversionClient.ValidateInputs(null),"null input list rejected");
  Throws<ArgumentException>(()=>FileConversionClient.ValidateInputs(new string[0]),"empty input list rejected");
  Throws<ArgumentException>(()=>FileConversionClient.ValidateInputs(Enumerable.Repeat(source,101).ToArray()),"over 100 inputs rejected");
  Check(FileConversionClient.ValidateInputs(new[]{source,source.ToUpperInvariant()}).Length==1,"input paths deduplicate case-insensitively");
  Throws<IOException>(()=>FileConversionClient.ValidateInputs(new[]{Folder("directory-input")}),"directory is not accepted as input");
  Throws<FileNotFoundException>(()=>FileConversionClient.ValidateInputs(new[]{Path.Combine(root,"missing.png")}),"missing source rejected before worker");
  Throws<IOException>(()=>FileConversionClient.ValidateLocalPath("relative.png",false),"relative path rejected");
  Throws<IOException>(()=>FileConversionClient.ValidateLocalPath(@"\\server\share\input.png",false),"UNC path rejected");
  Throws<Exception>(()=>FileConversionClient.ValidateLocalPath(source+":secret",false),"alternate stream rejected");
  Throws<Exception>(()=>FileConversionClient.ValidateLocalPath(source+"\0",false),"embedded NUL rejected");
  Throws<IOException>(()=>FileConversionClient.ValidateLocalPath(@"\\?\C:\input.png",false),"device-prefixed path rejected");
  int calls=0;var client=Client("invalid-input",(p,t,m)=>{calls++;return Task.FromResult(0);});
  Throws<ArgumentException>(()=>client.ConvertAsync(new[]{source},"exe",Path.Combine(root,"bad-format"),90,null,CancellationToken.None).GetAwaiter().GetResult(),"nonallowlisted output rejected");
  Throws<ArgumentOutOfRangeException>(()=>client.ConvertAsync(new[]{source},"jpg",Path.Combine(root,"bad-quality"),0,null,CancellationToken.None).GetAwaiter().GetResult(),"zero quality rejected");
  Throws<ArgumentOutOfRangeException>(()=>client.ConvertAsync(new[]{source},"jpg",Path.Combine(root,"bad-quality"),101,null,CancellationToken.None).GetAwaiter().GetResult(),"quality above 100 rejected");
  Throws<IOException>(()=>Convert(client,source,CancellationToken.None),"output destination cannot be existing file");
  Check(calls==0,"invalid inputs do not invoke worker");
  using(var cancel=new CancellationTokenSource()){
   cancel.Cancel();var result=client.ConvertAsync(null,"exe",null,0,null,cancel.Token).GetAwaiter().GetResult();
   Check(result.Cancelled&&result.Files.Count==0&&calls==0,"precancelled conversion does no validation, disk or worker work");
  }
 }
 static void CapabilityTests(){
  string name="caps-valid";var client=Fake(name,null,0);
  var caps=client.GetCapabilitiesAsync(new[]{source},CancellationToken.None).GetAwaiter().GetResult();
  Check(caps.Targets.Length==1&&caps.Targets[0].Id=="jpg"&&caps.Targets[0].Warning=="","valid capability protocol reaches host");
  Check(caps.Files.Length==1&&caps.Files[0].Source==source&&caps.Files[0].Format=="png"&&caps.Files[0].Warnings.Length==0,"source capabilities reach host without invented page count");
  var warningCaps=Fake("caps-warnings",(q,r)=>{
   r.files[0].format="pdf";
   r.files[0].warnings=new[]{"PDF 每页生成一个图片文件。",null," ","PDF 每页生成一个图片文件。"};
   r.targets[0].warning="JPG 使用有损压缩。";
  },0).GetCapabilitiesAsync(new[]{source},CancellationToken.None).GetAwaiter().GetResult();
  Check(warningCaps.Files[0].Format=="pdf"&&warningCaps.Files[0].Warnings.SequenceEqual(new[]{"PDF 每页生成一个图片文件。"})&&
   warningCaps.Targets[0].Warning=="JPG 使用有损压缩。","per-source multi-page warnings and target fidelity warnings are separately preserved");
  var noWarnings=Fake("caps-null-warnings",(q,r)=>{r.files[0].format=null;r.files[0].warnings=null;},0).GetCapabilitiesAsync(new[]{source},CancellationToken.None).GetAwaiter().GetResult();
  Check(noWarnings.Files[0].Format==""&&noWarnings.Files[0].Warnings.Length==0,"optional source metadata is normalized safely");
  Check(Empty(Path.Combine(root,name,"sessions")),"capability session is deleted");
  var mutations=new Dictionary<string,Action<FileConversionClient.WorkerRequest,FileConversionClient.WorkerResponse>>{
   {"schema",(q,r)=>r.schemaVersion=2},
   {"id",(q,r)=>r.id="other"},
   {"operation",(q,r)=>r.operation="convert"},
   {"status",(q,r)=>r.status="error"},
   {"contradictory-error",(q,r)=>r.error=new FileConversionClient.WorkerError{message="error with success"}},
   {"missing-files",(q,r)=>r.files=null},
   {"wrong-file-count",(q,r)=>r.files=new FileConversionClient.WorkerFile[0]},
   {"missing-targets",(q,r)=>r.targets=null},
   {"missing-outputs",(q,r)=>r.outputs=null},
   {"wrong-source",(q,r)=>r.files[0].source=worker},
   {"missing-file-targets",(q,r)=>r.files[0].targets=null},
   {"unsupported-file-target",(q,r)=>r.files[0].targets=new[]{"exe"}},
   {"unsupported-target",(q,r)=>r.targets[0].id="exe"},
   {"wrong-target-extension",(q,r)=>r.targets[0].extension="png"},
   {"missing-target-label",(q,r)=>r.targets[0].label=" "},
   {"duplicate-targets",(q,r)=>r.targets=new[]{r.targets[0],r.targets[0]}},
   {"not-common-target",(q,r)=>r.files[0].targets=new[]{"png"}},
   {"unexpected-output",(q,r)=>Outputs(r,source)}
  };
  foreach(var pair in mutations){
   string test="caps-"+pair.Key;
   Throws<IOException>(()=>Fake(test,pair.Value,0).GetCapabilitiesAsync(new[]{source},CancellationToken.None).GetAwaiter().GetResult(),"reject capability "+pair.Key);
   Check(Empty(Path.Combine(root,test,"sessions")),"rejected "+pair.Key+" session cleaned");
  }
  Throws<OperationCanceledException>(()=>Fake("caps-cancel",(q,r)=>r.status="cancelled",3).GetCapabilitiesAsync(new[]{source},CancellationToken.None).GetAwaiter().GetResult(),"worker cancellation is propagated");
  Throws<IOException>(()=>Fake("caps-bad-exit",null,1).GetCapabilitiesAsync(new[]{source},CancellationToken.None).GetAwaiter().GetResult(),"nonzero exit rejects otherwise successful response");
  var noReply=Client("missing-reply",(p,t,m)=>Task.FromResult(2));
  try{noReply.GetCapabilitiesAsync(new[]{source},CancellationToken.None).GetAwaiter().GetResult();throw new Exception("missing response accepted");}
  catch(IOException error){Check(error.Message.Contains("退出码 2")&&error.Message.Contains("转换组件"),"missing response reports component and exit code, not missing source");}
  var large=Client("large-reply",(p,t,m)=>{
   Write(ReadRequest(p).responsePath,new string(' ',2*1024*1024+1));return Task.FromResult(0);
  });
  Throws<IOException>(()=>large.GetCapabilitiesAsync(new[]{source},CancellationToken.None).GetAwaiter().GetResult(),"oversized response rejected before deserialization");
  var badUtf8=Client("invalid-utf8",(p,t,m)=>{
   File.WriteAllBytes(ReadRequest(p).responsePath,new byte[]{0xff,0xfe,0xff});return Task.FromResult(0);
  });
  Throws<Exception>(()=>badUtf8.GetCapabilitiesAsync(new[]{source},CancellationToken.None).GetAwaiter().GetResult(),"malformed response bytes rejected");
  var malformed=Client("malformed-json",(p,t,m)=>{
   Write(ReadRequest(p).responsePath,"{\"schemaVersion\":");return Task.FromResult(0);
  });
  Throws<Exception>(()=>malformed.GetCapabilitiesAsync(new[]{source},CancellationToken.None).GetAwaiter().GetResult(),"malformed JSON response rejected");
 }
 static void ConversionTests(){
  string original=File.ReadAllText(source),destination=Folder("valid-output");
  string existing=Write(Path.Combine(destination,"same.jpg"),"PREEXISTING KEEP");
  var success=Fake("publish",(q,r)=>{
   string a=Write(Path.Combine(q.outputDirectory,"a","same.jpg"),"FIRST");
   string b=Write(Path.Combine(q.outputDirectory,"b","same.jpg"),"SECOND");Outputs(r,a,b);
  },0);
  var result=Convert(success,destination,CancellationToken.None);
  Check(result.Files.Count==1&&result.Files[0].Error==null&&result.Files[0].Outputs.Length==2,"multiple outputs published as one successful file");
  var paths=result.Files[0].Outputs;
  Check(Path.GetFileName(paths[0])=="original-001.jpg"&&Path.GetFileName(paths[1])=="original-002.jpg","multiple outputs use source name and ordered suffixes instead of worker names");
  Check(paths.All(p=>FileConversionClient.IsUnder(p,destination))&&Path.GetDirectoryName(paths[0])!=destination,"publication uses a fresh child folder");
  Check(File.ReadAllText(existing)=="PREEXISTING KEEP"&&File.ReadAllText(source)==original,"preexisting outputs and source bytes are unchanged");
  Check(File.ReadAllText(paths[0])=="FIRST"&&File.ReadAllText(paths[1])=="SECOND","published bytes equal staged outputs");
  Check(Empty(Path.Combine(root,"publish","sessions")),"successful conversion staging removed");
  foreach(string test in new[]{"outside","wrong-extension","empty","missing","duplicate","mismatched-file-list","no-outputs"}){
   string target=Path.Combine(root,"reject-"+test);
   var bad=Fake("convert-"+test,(q,r)=>{
    string output=Write(Path.Combine(q.outputDirectory,"result.jpg"),"STAGED");
    Outputs(r,output);
    if(test=="outside")Outputs(r,source);
    if(test=="wrong-extension")Outputs(r,Write(Path.Combine(q.outputDirectory,"result.exe"),"STAGED"));
    if(test=="empty")File.WriteAllText(output,"");
    if(test=="missing")File.Delete(output);
    if(test=="duplicate")Outputs(r,output,output);
    if(test=="mismatched-file-list")r.files[0].outputs=new string[0];
    if(test=="no-outputs")Outputs(r);
   },0);
   var failed=Convert(bad,target,CancellationToken.None);
   Check(failed.Files.Count==1&&!string.IsNullOrWhiteSpace(failed.Files[0].Error)&&failed.Files[0].Outputs.Length==0,"reject invalid converted output "+test);
   Check(!Directory.Exists(target)&&Empty(Path.Combine(root,"convert-"+test,"sessions")),"rejected "+test+" creates no destination and leaves no staging");
  }
  var permitted=Fake("alias-extension",(q,r)=>Outputs(r,Write(Path.Combine(q.outputDirectory,"converted.jpeg"),"JPEG")),0);
  var aliasResult=Convert(permitted,Path.Combine(root,"alias-output"),CancellationToken.None);
  Check(aliasResult.Files[0].Outputs.Length==1&&Path.GetFileName(aliasResult.Files[0].Outputs[0])=="original.jpg","JPEG extension alias is accepted and published with the chosen canonical extension");
  var errorClient=Fake("worker-error",(q,r)=>{r.status="error";r.error=new FileConversionClient.WorkerError{code="decode",message="合成图片已损坏"};},1);
  var errorResult=Convert(errorClient,Path.Combine(root,"error-output"),CancellationToken.None);
  Check(errorResult.Files.Count==1&&errorResult.Files[0].Error=="合成图片已损坏","worker-specific failure message reaches result row");
  var timeout=Client("timeout",(p,t,m)=>{throw new TimeoutException("internal details");});
  var timed=Convert(timeout,Path.Combine(root,"timeout-output"),CancellationToken.None);
  Check(timed.Files[0].Error.Contains("耗时过长")&&!Directory.Exists(Path.Combine(root,"timeout-output"))&&Empty(Path.Combine(root,"timeout","sessions")),"timeout is friendly, creates no output, and cleans request");
  string second=Write(Path.Combine(root,"input-2.png"),"SECOND ORIGINAL");
  using(var cancel=new CancellationTokenSource()){
   int calls=0;var sequential=Fake("batch-cancel",(q,r)=>{calls++;Outputs(r,Write(Path.Combine(q.outputDirectory,"result.jpg"),"DONE"));},0);
   var progress=new ImmediateProgress(p=>{if(p.Completed==1)cancel.Cancel();});
   var cancelled=sequential.ConvertAsync(new[]{source,second},"jpg",Path.Combine(root,"cancel-output"),90,progress,cancel.Token).GetAwaiter().GetResult();
   Check(cancelled.Cancelled&&cancelled.Files.Count==1&&calls==1&&cancelled.Files[0].Outputs.All(File.Exists),"between-file cancellation retains completed output and never starts next worker");
   Check(Empty(Path.Combine(root,"batch-cancel","sessions")),"between-file cancellation removes staging");
  }
  using(var cancel=new CancellationTokenSource()){
   var cancelledWorker=Fake("cancel-before-publish",(q,r)=>{Outputs(r,Write(Path.Combine(q.outputDirectory,"result.jpg"),"DO NOT PUBLISH"));cancel.Cancel();},0);
   var cancelled=Convert(cancelledWorker,Path.Combine(root,"cancel-before-publish-output"),cancel.Token);
   Check(cancelled.Cancelled&&cancelled.Files.Count==0&&!Directory.Exists(Path.Combine(root,"cancel-before-publish-output")),"cancellation on response arrival prevents publication");
   Check(Empty(Path.Combine(root,"cancel-before-publish","sessions")),"cancellation on response arrival removes staging");
  }
  // The second source below is exclusively locked after response creation.
  // Publication of the first output must be rolled back on the second failure.
  FileStream exclusive=null;string lockedSession=null;
  string unrelatedSession=Path.Combine(root,"rollback","sessions",Guid.NewGuid().ToString("N"));
  string unrelatedSentinel=Write(Path.Combine(unrelatedSession,"preserve.txt"),"UNRELATED SESSION");
  try{
   var rollback=Fake("rollback",(q,r)=>{
    string first=Write(Path.Combine(q.outputDirectory,"first.jpg"),"FIRST");
    string last=Write(Path.Combine(q.outputDirectory,"locked.jpg"),"LOCKED");
    Outputs(r,first,last);lockedSession=Path.GetDirectoryName(q.responsePath);exclusive=new FileStream(last,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
   },0);
   string rollbackDestination=Path.Combine(root,"rollback-output");
   var failure=Convert(rollback,rollbackDestination,CancellationToken.None);
   Check(failure.Files.Count==1&&failure.Files[0].Outputs.Length==0&&!string.IsNullOrEmpty(failure.Files[0].Error),"partial publication failure is reported without partial output list");
   Check(Directory.Exists(rollbackDestination)&&!Directory.EnumerateFiles(rollbackDestination,"*",SearchOption.AllDirectories).Any(),"partial publication rolls back only newly created files");
   Check(Directory.Exists(lockedSession),"locked staging is retained until the exclusive handle is released");
  }finally{if(exclusive!=null)exclusive.Dispose();}
  var cleanup=Stopwatch.StartNew();
  while(Directory.Exists(lockedSession)&&cleanup.ElapsedMilliseconds<3000)Thread.Sleep(10);
  Check(!Directory.Exists(lockedSession),"bounded cleanup retry removes the session after its file lock is released");
  Check(File.ReadAllText(unrelatedSentinel)=="UNRELATED SESSION","cleanup retry leaves unrelated sessions in the same root untouched");
  Check(File.ReadAllText(source)==original,"source survives rejected outputs, timeout, cancellation, and publication rollback");
 }
 static void NamingTests(){
  Check(FileConversionClient.OutputName(@"C:\example\陶瓷花瓶 (最终) 2.1.png","jpg",0,1)=="陶瓷花瓶 (最终) 2.1.jpg","single output preserves human source spelling and punctuation");
  Check(FileConversionClient.OutputName(@"C:\example\notes.pdf","png",9,12)=="notes-010.png","multiple outputs have stable sortable numbering");
  Check(FileConversionClient.OutputName(@"C:\example\notes.pdf","png",999,1000)=="notes-1000.png","large supported page count does not repeat an output name");
  Check(FileConversionClient.OutputName(@"C:\example\project.tar.gz","zip",0,1)=="project.zip"&&FileConversionClient.OutputName(@"C:\example\book.kepub.epub","txt",0,1)=="book.txt","compound source suffix is removed as a whole when naming a converted file");
  Check(FileConversionClient.OutputName(@"C:\example\.png","jpg",0,1)=="converted.jpg","empty filename stem receives a safe fallback");
  Check(FileConversionClient.OutputName(@"C:\example\CON.png","jpg",0,1)=="_CON.jpg"&&FileConversionClient.OutputName(@"C:\example\LPT1.notes.png","jpg",0,1)=="_LPT1.notes.jpg","reserved Windows device stems cannot become published paths");
  string longName=new string('a',99)+char.ConvertFromUtf32(0x1f4f7)+"x.png";
  string trimmed=FileConversionClient.OutputName(@"C:\example\"+longName,"jpg",0,1);
  Check(trimmed==new string('a',99)+".jpg","long names trim without splitting a Unicode surrogate pair");
  Throws<ArgumentException>(()=>FileConversionClient.OutputName(source,"../jpg",0,1),"preview naming rejects unlisted target");
  Throws<ArgumentOutOfRangeException>(()=>FileConversionClient.OutputName(source,"jpg",1,1),"preview naming rejects index outside output count");
  Throws<ArgumentOutOfRangeException>(()=>FileConversionClient.OutputName(source,"jpg",0,1001),"preview naming enforces output-count limit");
  string first=Write(Path.Combine(root,"same-name-first","陶瓷 花瓶 (新).png"),"FIRST ORIGINAL");
  string second=Write(Path.Combine(root,"same-name-second","陶瓷 花瓶 (新).png"),"SECOND ORIGINAL");
  var client=Fake("same-names",(q,r)=>Outputs(r,Write(Path.Combine(q.outputDirectory,"converted-internal-name.jpg"),q.inputs[0]==first?"FIRST":"SECOND")),0);
  string destination=Folder("same-name-results");
  var batch=client.ConvertAsync(new[]{first,second},"jpg",destination,90,null,CancellationToken.None).GetAwaiter().GetResult();
  string[] outputs=batch.Files.SelectMany(f=>f.Outputs).ToArray();
  Check(outputs.Length==2&&Path.GetFileName(outputs[0])=="陶瓷 花瓶 (新).jpg"&&Path.GetFileName(outputs[1])=="陶瓷 花瓶 (新) (1).jpg","same-named sources are disambiguated without worker prefixes or overwrite");
  Check(File.ReadAllText(outputs[0])=="FIRST"&&File.ReadAllText(outputs[1])=="SECOND"&&File.ReadAllText(first)=="FIRST ORIGINAL"&&File.ReadAllText(second)=="SECOND ORIGINAL","same-name publication keeps each result and both source files");
  var repeated=client.ConvertAsync(new[]{first},"jpg",destination,90,null,CancellationToken.None).GetAwaiter().GetResult();
  Check(Path.GetFileName(repeated.Files[0].Outputs[0])=="陶瓷 花瓶 (新).jpg"&&Path.GetDirectoryName(repeated.Files[0].Outputs[0])!=Path.GetDirectoryName(outputs[0])&&outputs.All(File.Exists),"repeat conversion starts a new batch without renaming or removing prior results");
  int resolved=0;string display="设计.v2";client.SourceName=path=>{resolved++;return display;};
  var renameProgress=new ImmediateProgress(p=>{if(p.Completed==0)display="new selection";});
  var named=client.ConvertAsync(new[]{first,second},"jpg",destination,90,renameProgress,CancellationToken.None).GetAwaiter().GetResult();
  Check(resolved==2&&Path.GetFileName(named.Files[0].Outputs[0])=="设计.v2.jpg"&&Path.GetFileName(named.Files[1].Outputs[0])=="设计.v2 (1).jpg","library display stems preserve dots and are frozen before worker progress or selection changes");
  client.SourceName=path=>@"..\..\outside\CON";
  var unsafeName=client.ConvertAsync(new[]{first},"jpg",destination,90,null,CancellationToken.None).GetAwaiter().GetResult();
  Check(unsafeName.Files[0].Outputs.Length==1&&FileConversionClient.IsUnder(unsafeName.Files[0].Outputs[0],destination)&&
   Path.GetFileName(unsafeName.Files[0].Outputs[0])==".._.._outside_CON.jpg","display name resolver cannot redirect outputs outside the results folder");
 }
 static void SynchronizationContextTests(){
  int callerThread=Thread.CurrentThread.ManagedThreadId,completionThread=0;
  var context=new NonPumpingContext();SynchronizationContext previous=SynchronizationContext.Current;
  using(var gate=new ManualResetEventSlim(false)){
   var client=Client("context-convert",(path,token,timeout)=>Task.Run(()=>{
    if(!gate.Wait(5000))throw new TimeoutException("fixture gate");
    var request=ReadRequest(path);var response=Reply(request);
    Outputs(response,Write(Path.Combine(request.outputDirectory,"output.jpg"),"BACKGROUND OUTPUT"));
    Respond(request,response);return 0;
   }));
   Task<ConversionBatchResult> pending;
   try{
    SynchronizationContext.SetSynchronizationContext(context);
    pending=client.ConvertAsync(new[]{source},"jpg",Path.Combine(root,"context-output"),90,
     new ImmediateProgress(p=>{if(p.Completed>0)completionThread=Thread.CurrentThread.ManagedThreadId;}),CancellationToken.None);
   }finally{SynchronizationContext.SetSynchronizationContext(previous);}
   gate.Set();
   Check(pending.Wait(5000),"conversion completes without pumping the initiating synchronization context");
   var result=pending.GetAwaiter().GetResult();
   Check(context.Posts==0,"conversion continuations do not post disk publication back to the UI context");
   Check(completionThread!=0&&completionThread!=callerThread&&result.Files.Count==1&&result.Files[0].Outputs.All(File.Exists),
    "published conversion completes on a background thread");
   Check(Empty(Path.Combine(root,"context-convert","sessions")),"background conversion cleans staging before returning");
  }
  context=new NonPumpingContext();
  using(var gate=new ManualResetEventSlim(false)){
   var client=Client("context-caps",(path,token,timeout)=>Task.Run(()=>{
    if(!gate.Wait(5000))throw new TimeoutException("fixture gate");
    var request=ReadRequest(path);Respond(request,Reply(request));return 0;
   }));
   Task<ConversionCapabilities> pending;
   try{
    SynchronizationContext.SetSynchronizationContext(context);
    pending=client.GetCapabilitiesAsync(new[]{source},CancellationToken.None);
   }finally{SynchronizationContext.SetSynchronizationContext(previous);}
   gate.Set();
   Check(pending.Wait(5000),"capability request completes without pumping the initiating UI context");
   Check(context.Posts==0&&pending.Result.Targets.Length==1,"capability continuations remain outside the UI context");
   Check(Empty(Path.Combine(root,"context-caps","sessions")),"background capability request cleans its session");
  }
 }
 static int WorkerMode(string requestPath){
  string mode=File.ReadAllText(requestPath).Trim(),basePath=Path.GetDirectoryName(requestPath);
  string childMarker=Path.Combine(basePath,"child.pid");
  var start=new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,"--job-child \""+childMarker+"\""){
   UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden
  };
  using(var child=Process.Start(start)){
   Write(Path.Combine(basePath,"parent.pid"),Process.GetCurrentProcess().Id.ToString());
   var ready=Stopwatch.StartNew();while(!File.Exists(childMarker)&&ready.ElapsedMilliseconds<5000)Thread.Sleep(10);
   if(!File.Exists(childMarker))return 9;
   Write(Path.Combine(basePath,"ready"),"ready");
   if(mode=="exit")return 7;
   while(true)Thread.Sleep(100);
  }
 }
 static bool Exited(int pid){
  try{using(var process=Process.GetProcessById(pid))return process.HasExited;}
  catch(ArgumentException){return true;}
 }
 static void WaitReady(string path,Task task){
  var timer=Stopwatch.StartNew();
  while(!File.Exists(path)&&!task.IsCompleted&&timer.ElapsedMilliseconds<8000)Thread.Sleep(10);
  if(!File.Exists(path)){task.GetAwaiter().GetResult();throw new Exception("Job fixture did not become ready");}
 }
 static void CheckExited(string fixture,string message){
  int parent=int.Parse(File.ReadAllText(Path.Combine(fixture,"parent.pid")));
  int child=int.Parse(File.ReadAllText(Path.Combine(fixture,"child.pid")));
  var timer=Stopwatch.StartNew();
  while((!Exited(parent)||!Exited(child))&&timer.ElapsedMilliseconds<5000)Thread.Sleep(10);
  Check(Exited(parent)&&Exited(child),message);
 }
 static void JobTests(){
  string executable=Process.GetCurrentProcess().MainModule.FileName;
  string fixture=Folder("job-cancel"),request=Write(Path.Combine(fixture,"request.json"),"hang");
  using(var cancel=new CancellationTokenSource()){
   var task=ConversionWorkerProcess.RunAsync(executable,request,cancel.Token,15000);
   WaitReady(Path.Combine(fixture,"ready"),task);cancel.Cancel();
   Throws<OperationCanceledException>(()=>task.GetAwaiter().GetResult(),"native runner cancellation is propagated");
   Check(File.Exists(Path.Combine(fixture,"cancel.flag")),"native cancellation creates cooperative stop flag");
   CheckExited(fixture,"native cancellation terminates exactly the fixture parent and child");
  }
  fixture=Folder("job-timeout");request=Write(Path.Combine(fixture,"request.json"),"hang");
  var timeout=ConversionWorkerProcess.RunAsync(executable,request,CancellationToken.None,1500);
  WaitReady(Path.Combine(fixture,"ready"),timeout);
  Throws<TimeoutException>(()=>timeout.GetAwaiter().GetResult(),"native runner deadline is enforced");
  CheckExited(fixture,"native timeout terminates fixture parent and descendant");
  fixture=Folder("job-success");request=Write(Path.Combine(fixture,"request.json"),"exit");
  int code=ConversionWorkerProcess.RunAsync(executable,request,CancellationToken.None,10000).GetAwaiter().GetResult();
  Check(code==7,"native runner returns original process exit code");
  CheckExited(fixture,"job close kills surviving descendant when parent exits normally");
 }
 static void NativeCleanupTests(){
  string id=Guid.NewGuid().ToString("N"),session=Path.Combine(root,"native-owner",id);
  string work=Path.Combine(Path.GetTempPath(),"Nook-ConversionNative",id),marker=Path.Combine(work,"owner-session.txt");
  if(Directory.Exists(work)||File.Exists(work))throw new IOException("Fresh owned native fixture path already exists");
  Directory.CreateDirectory(work);string payload=Write(Path.Combine(work,"owned.tmp"),"SYNTHETIC NATIVE TEMP");
  try{
   FileConversionClient.DeleteNativeWork(session);
   Check(File.Exists(payload),"native cleanup refuses a directory without its owner marker");
   Write(marker,Path.Combine(root,"different-owner",id));
   FileConversionClient.DeleteNativeWork(session);
   Check(File.Exists(payload),"native cleanup refuses a matching directory id owned by another session");
   Write(marker,new string('x',4097));
   FileConversionClient.DeleteNativeWork(session);
   Check(File.Exists(payload),"native cleanup refuses an oversized owner marker");
   Write(marker,Path.GetFullPath(session));
   FileConversionClient.DeleteNativeWork(session);
   Check(!Directory.Exists(work),"matching explicit owner marker permits cleanup of only its derived native work directory");
  }finally{
   if(Directory.Exists(work)){
    if(File.Exists(payload))File.Delete(payload);
    if(File.Exists(marker))File.Delete(marker);
    Directory.Delete(work);
   }
  }
 }
 public static int Main(string[] args){
  try{
   if(args.Length==2&&args[0]=="--job-child"){Write(args[1],Process.GetCurrentProcess().Id.ToString());while(true)Thread.Sleep(100);}
   if(args.Length==2&&args[0]=="--request")return WorkerMode(args[1]);
   if(args.Length!=1)throw new ArgumentException("ClientRunner.exe <fresh-output-directory>");
   root=Path.GetFullPath(args[0]);
   if(Directory.Exists(root)||File.Exists(root))throw new IOException("Use a fresh fixture directory");
   Directory.CreateDirectory(root);worker=Write(Path.Combine(root,"fake-worker.exe"),"NOT EXECUTED");source=Write(Path.Combine(root,"original.png"),"ORIGINAL DO NOT CHANGE");
   InputTests();CapabilityTests();ConversionTests();NamingTests();SynchronizationContextTests();JobTests();NativeCleanupTests();
   Console.WriteLine("PASS total="+checks+"; no desktop, clipboard, Nook, or real user materials");return 0;
  }catch(Exception error){Console.Error.WriteLine(error);return 1;}
 }
}
