using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MoriDesk {
 internal sealed class ConversionComponentInfo {
  public string Id {get;set;}
  public string Name {get;set;}
  public string Description {get;set;}
  public string InstalledVersion {get;set;}
  public string AvailableVersion {get;set;}
  public long DownloadBytes {get;set;}
  public long InstalledBytes {get;set;}
  public string State {get;set;}
  public bool CanUninstall {get;set;}
  public string[] Dependencies {get;set;}
  public string Message {get;set;}
 }
 internal sealed class ConversionComponentProgress {
  public string Stage {get;set;}
  public string ComponentId {get;set;}
  public long BytesDone {get;set;}
  public long BytesTotal {get;set;}
  public double Fraction {get;set;}
 }
 internal sealed partial class FileConversionClient {
  // A response can describe only reviewed component IDs. A document or remote
  // manifest cannot redirect the host to an arbitrary installer or executable.
  internal static bool IsComponentId(string id){
   return new[]{"ffmpeg","libreoffice","calibre","pandoc","ghostscript","7zip","rhwp","jre","h2orestart","vcruntime"}.Contains(id,StringComparer.Ordinal);
  }
  internal Task<ConversionComponentInfo[]> ListComponentsAsync(CancellationToken token){
   return ComponentRequestAsync(null,"list",null,token);
  }
  internal Task<ConversionComponentInfo[]> ManageComponentAsync(string id,string action,IProgress<ConversionComponentProgress> progress,CancellationToken token){
   if(!IsComponentId(id)||!new[]{"install","update","uninstall"}.Contains(action,StringComparer.Ordinal))
    throw new ArgumentException("请选择有效的组件操作。");
   return ComponentRequestAsync(id,action,progress,token);
  }
  async Task<ConversionComponentInfo[]> ComponentRequestAsync(string componentId,string action,IProgress<ConversionComponentProgress> progress,CancellationToken token){
   token.ThrowIfCancellationRequested();
   if(!File.Exists(executable))throw new FileNotFoundException("此版本缺少转换核心，请先更新 Nook；个人文件不会改动。");
   ValidateLocalPath(sessionRoot,false);Directory.CreateDirectory(sessionRoot);ValidateLocalPath(sessionRoot,true);
   string session=Path.Combine(sessionRoot,Guid.NewGuid().ToString("N"));
   if(Directory.Exists(session)||File.Exists(session))throw new IOException("组件任务目录已存在，请重试。");
   Directory.CreateDirectory(session);
   CancellationTokenSource polling=null;Task watcher=null;
   try{
    var request=new WorkerRequest{schemaVersion=1,id=Path.GetFileName(session),operation="components-"+action,
     inputs=new string[0],quality=90,responsePath=Path.Combine(session,"response.json"),componentId=componentId,componentRoot=componentsRoot};
    string requestPath=Path.Combine(session,"request.json");
    byte[] data=new UTF8Encoding(false).GetBytes(Serializer().Serialize(request));
    using(var stream=new FileStream(requestPath,FileMode.CreateNew,FileAccess.Write,FileShare.None))stream.Write(data,0,data.Length);
    polling=new CancellationTokenSource();
    if(progress!=null)watcher=WatchComponentProgress(session,progress,polling.Token);
    int exit;
    try{exit=await run(requestPath,token,action=="list"?45000:7200000).ConfigureAwait(false);}
    catch(OperationCanceledException){
     if(!File.Exists(request.responsePath))throw;
     // The worker may already have atomically switched the active version just
     // before the host noticed Cancel. Validate its durable result below.
     exit=0;
    }
    // A completed commit wins a late cancellation. Read the durable response
    // before reporting an operation as cancelled.
    if(!File.Exists(request.responsePath)){token.ThrowIfCancellationRequested();throw new IOException("组件未返回结果，请刷新状态后重试。");}
    ValidateLocalPath(request.responsePath,true);
    if(new FileInfo(request.responsePath).Length>MaximumResponseBytes)throw new IOException("组件状态过大。");
    var response=Serializer().Deserialize<WorkerResponse>(File.ReadAllText(request.responsePath,new UTF8Encoding(false,true)));
    if(response==null||response.schemaVersion!=1||response.id!=request.id||response.operation!=request.operation)
     throw new IOException("组件任务标识不匹配，未接受结果。");
    if(response.status=="cancelled"&&(exit==3||token.IsCancellationRequested))throw new OperationCanceledException(token);
    if(exit!=0||response.status!="ok"||response.error!=null)
     throw new IOException(response.error!=null&&!string.IsNullOrWhiteSpace(response.error.message)?response.error.message:"组件操作未完成。");
    var result=ValidateComponents(response.components);
    if(action!="list"){
     var changed=result.FirstOrDefault(item=>item.Id==componentId);
     if(changed==null||(action=="uninstall"?changed.State!="missing":changed.State!="installed"&&changed.State!="bundled"))
      throw new IOException("组件状态尚未确认，请刷新后重试。");
    }
    return result;
   }finally{
    if(polling!=null){
     polling.Cancel();
     if(watcher!=null){var completedPolling=polling;watcher.ContinueWith(task=>{var ignored=task.Exception;completedPolling.Dispose();},TaskScheduler.Default);}
     else polling.Dispose();
    }
    DeleteSession(session);
   }
  }
  static async Task WatchComponentProgress(string session,IProgress<ConversionComponentProgress> progress,CancellationToken token){
   string path=Path.Combine(session,"progress.json");
   while(!token.IsCancellationRequested){
    try{
     if(File.Exists(path)){
      ValidateLocalPath(path,true);
      if(new FileInfo(path).Length<=8192){
       var value=Serializer().Deserialize<WorkerComponentProgress>(File.ReadAllText(path));
       if(value!=null&&value.bytesDone>=0&&value.bytesTotal>=0&&value.fraction>=0&&value.fraction<=1)
        progress.Report(new ConversionComponentProgress{Stage=value.stage,ComponentId=value.componentId,BytesDone=value.bytesDone,BytesTotal=value.bytesTotal,Fraction=value.fraction});
      }
     }
    }catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}catch(InvalidOperationException){}
    await Task.Delay(300,token).ConfigureAwait(false);
   }
  }
  internal static ConversionComponentInfo[] ValidateComponents(WorkerComponent[] values){
   if(values==null||values.Length>10)throw new IOException("组件清单无效。");
   var ids=new HashSet<string>(StringComparer.Ordinal);
   return values.Select(value=>{
    if(value==null||!IsComponentId(value.id)||!ids.Add(value.id)||string.IsNullOrWhiteSpace(value.name)||
     value.downloadBytes<0||value.installedBytes<0||value.downloadBytes>16L*1024*1024*1024||value.installedBytes>32L*1024*1024*1024||
     value.dependencies==null||value.dependencies.Any(id=>!IsComponentId(id)||id==value.id)||value.dependencies.Distinct(StringComparer.Ordinal).Count()!=value.dependencies.Length||
     !new[]{"missing","installed","update-available","broken","bundled","in-use","newer_version"}.Contains(value.state,StringComparer.Ordinal))
     throw new IOException("组件清单包含未识别的信息。");
    return new ConversionComponentInfo{Id=value.id,Name=value.name,Description=value.description??"",InstalledVersion=value.installedVersion??"",
     AvailableVersion=value.availableVersion??"",DownloadBytes=value.downloadBytes,InstalledBytes=value.installedBytes,State=value.state,
     CanUninstall=value.canUninstall,Dependencies=(string[])value.dependencies.Clone(),Message=value.message??""};
   }).ToArray();
  }
  internal sealed class WorkerComponent {
   public string id {get;set;} public string name {get;set;} public string description {get;set;}
   public string installedVersion {get;set;} public string availableVersion {get;set;} public long downloadBytes {get;set;}
   public long installedBytes {get;set;} public string state {get;set;} public bool canUninstall {get;set;}
   public string[] dependencies {get;set;} public string message {get;set;}
  }
  sealed class WorkerComponentProgress {
   public string stage {get;set;} public string componentId {get;set;} public long bytesDone {get;set;} public long bytesTotal {get;set;} public double fraction {get;set;}
  }
 }
}
