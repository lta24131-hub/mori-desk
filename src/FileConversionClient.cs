using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MoriDesk {
 internal sealed class ConversionTarget {
  public string Id { get; set; }
  public string Label { get; set; }
  public string Extension { get; set; }
  public string Warning { get; set; }
  public override string ToString(){return string.IsNullOrWhiteSpace(Label)?Id:Label;}
 }
 internal sealed class ConversionCapabilities {
  public ConversionTarget[] Targets { get; set; }
  public ConversionInputInfo[] Files { get; set; }
  public string[] MissingEngines { get; set; }
  public string[] MissingComponentIds { get; set; }
 }
 internal sealed class ConversionInputInfo {
  public string Source { get; set; }
  public string Format { get; set; }
  public string[] Targets { get; set; }
  public string[] Warnings { get; set; }
 }
 internal sealed class ConvertedFile {
  public string Source { get; set; }
  public string[] Outputs { get; set; }
  public string Error { get; set; }
 }
 internal sealed class ConversionBatchResult {
  public List<ConvertedFile> Files { get; set; }
  public bool Cancelled { get; set; }
  public ConversionBatchResult(){Files=new List<ConvertedFile>();}
 }
 internal sealed class ConversionProgress {
  public int Completed { get; set; }
  public int Total { get; set; }
  public string FileName { get; set; }
 }

 // Filee stays outside the .NET Framework/UI process. Nothing is started until
 // the user adds files. A worker can only publish files into its own fresh session.
 internal sealed partial class FileConversionClient {
  const int MaximumResponseBytes=2*1024*1024,MaximumOutputs=1000;
  internal const long MaximumInputBytes=8L*1024*1024*1024;
  internal const long MaximumOutputBytes=16L*1024*1024*1024;
  readonly string executable,sessionRoot,componentsRoot;
  readonly Func<string,CancellationToken,int,Task<int>> run;
  internal static bool IsAvailable {get{return File.Exists(DefaultExecutable);}}
  static string DefaultExecutable {get{return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"file-conversion","Nook.FileConversion.exe");}}
  // Display stems supplied from the originating library snapshot. No resolver
  // value is ever used as a source path, directory or replacement destination.
  internal Func<string,string> SourceName {get;set;}
  internal FileConversionClient():this(DefaultExecutable,Path.Combine(Path.GetTempPath(),"Nook-FileConversion"),null){}
  // Injection is for synthetic background tests, never an alternate executable
  // selected from a downloaded request or from the user's input files.
  internal FileConversionClient(string worker,string sessions,Func<string,CancellationToken,int,Task<int>> runner,string componentsRoot=null){
   executable=Path.GetFullPath(worker);sessionRoot=Path.GetFullPath(sessions);
   this.componentsRoot=componentsRoot==null?null:ValidateLocalPath(componentsRoot,false);
   run=runner??((request,token,timeout)=>ConversionWorkerProcess.RunAsync(executable,request,token,timeout,timeout>1800000?5000:250));
  }
  internal async Task<ConversionCapabilities> GetCapabilitiesAsync(string[] inputs,CancellationToken token){
   string[] paths=ValidateInputs(inputs);token.ThrowIfCancellationRequested();
   var reply=await RequestAsync(paths,"capabilities",null,90,token).ConfigureAwait(false);
   var missingIds=reply.Response.missingComponentIds??new string[0];
   if(missingIds.Any(id=>!IsComponentId(id))||missingIds.Distinct(StringComparer.Ordinal).Count()!=missingIds.Length)throw new IOException("转换组件返回了未识别的组件需求。");
   return new ConversionCapabilities{MissingEngines=reply.Response.missingEngines??new string[0],MissingComponentIds=missingIds,Targets=reply.Response.targets.Select(t=>new ConversionTarget{
    Id=t.id,Label=t.label,Extension=t.extension,Warning=t.warning??""
   }).ToArray(),Files=reply.Response.files.Select(f=>new ConversionInputInfo{
    Source=f.source,Format=f.format??"",Targets=(string[])f.targets.Clone(),Warnings=(f.warnings??new string[0]).Where(w=>!string.IsNullOrWhiteSpace(w)).Distinct().ToArray()
   }).ToArray()};
  }
  internal async Task<ConversionBatchResult> ConvertAsync(string[] inputs,string target,string outputFolder,int quality,IProgress<ConversionProgress> progress,CancellationToken token){
   var result=new ConversionBatchResult();
   if(token.IsCancellationRequested){result.Cancelled=true;return result;}
   string[] paths=ValidateInputs(inputs);
   if(!ConversionFormats.IsTarget(target))throw new ArgumentException("请选择支持的目标格式。");
   if(quality<1||quality>100)throw new ArgumentOutOfRangeException("quality","图片质量需要在 1–100 之间。");
   string destination=ValidateLocalPath(outputFolder,false);
   if(File.Exists(destination))throw new IOException("结果保存位置不是文件夹。");
   var sourceNames=paths.ToDictionary(path=>path,path=>{
    string display=null;if(SourceName!=null)display=SourceName(path);
    return string.IsNullOrWhiteSpace(display)?ConversionFormats.NameWithoutExtension(path):display;
   },StringComparer.OrdinalIgnoreCase);
   string batchFolder=null;
   for(int index=0;index<paths.Length;index++){
    if(token.IsCancellationRequested){result.Cancelled=true;break;}
    string source=paths[index];Report(progress,index,paths.Length,source);ConversionReply reply=null;
    var file=new ConvertedFile{Source=source,Outputs=new string[0]};
    try{
     reply=await RequestAsync(new[]{source},"convert",target,quality,token).ConfigureAwait(false);
     token.ThrowIfCancellationRequested();
     // Do not create destination directories for rejected/failed conversions.
     if(batchFolder==null){
      ValidateLocalPath(destination,false);Directory.CreateDirectory(destination);ValidateLocalPath(destination,true);
      batchFolder=Path.Combine(destination,"Nook-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,8));
      if(Directory.Exists(batchFolder)||File.Exists(batchFolder))throw new IOException("结果目录已存在，请重试。");
      Directory.CreateDirectory(batchFolder);ValidateLocalPath(batchFolder,true);
     }
     // Cancellation is honored between files; once publication starts it is
     // completed as one file's transaction. Already published results survive.
     var published=new List<string>();
     try{
      for(int outputIndex=0;outputIndex<reply.Response.outputs.Length;outputIndex++){
       string staged=reply.Response.outputs[outputIndex];
       ValidateLocalPath(staged,true);ValidateLocalPath(batchFolder,true);
       string name=OutputNameFromStem(sourceNames[source],target,outputIndex,reply.Response.outputs.Length);
       string output=CopyNew(staged,batchFolder,name);published.Add(output);
      }
     }catch{
      // These are exclusively created outputs, never pre-existing user files.
      foreach(string ownFile in published)TryDeleteOwnedFile(ownFile,batchFolder);
      throw;
     }
     file.Outputs=published.ToArray();
    }catch(OperationCanceledException){result.Cancelled=true;break;}
    catch(Exception error){file.Error=FriendlyMessage(error);}
    finally{if(reply!=null&&reply.Session!=null)DeleteSession(reply.Session);}
    result.Files.Add(file);Report(progress,index+1,paths.Length,source);
   }
   if(token.IsCancellationRequested)result.Cancelled=true;
   return result;
  }
  static void Report(IProgress<ConversionProgress> progress,int completed,int total,string source){
   if(progress!=null)progress.Report(new ConversionProgress{Completed=completed,Total=total,FileName=Path.GetFileName(source)});
  }
  static string FriendlyMessage(Exception error){
   if(error is TimeoutException)return "转换耗时过长，已停止本次转换。可以先减少页数或分批处理。";
   return error.Message;
  }
  internal static string[] ValidateInputs(string[] inputs){
   if(inputs==null||inputs.Length==0||inputs.Length>100)throw new ArgumentException("请添加 1–100 个本地文件。");
   var paths=new List<string>();var unique=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   foreach(string input in inputs){
    string path=ValidateLocalPath(input,true);
    if(!File.Exists(path)||Directory.Exists(path))throw new IOException("文件已移动或无法读取："+Path.GetFileName(path));
    if(new FileInfo(path).Length>MaximumInputBytes)throw new IOException("暂不转换超过 8 GB 的单个文件："+Path.GetFileName(path));
    if(unique.Add(path))paths.Add(path);
   }
   return paths.ToArray();
  }
  internal static string ValidateLocalPath(string path,bool mustExist){
   if(string.IsNullOrWhiteSpace(path)||path.Length<3||path[1]!=':'||(path[2]!='\\'&&path[2]!='/')||!Path.IsPathRooted(path)||path.StartsWith(@"\\",StringComparison.Ordinal)||path.IndexOf('\0')>=0)
    throw new IOException("请选择普通的本地文件路径；暂不支持网络盘或设备路径。");
   string full=Path.GetFullPath(path);
   if(full.Length<3||full[1]!=':'||full.Substring(2).Contains(":"))throw new IOException("不支持此文件路径。");
   string check=full;
   while(!string.IsNullOrEmpty(check)){
    if(File.Exists(check)||Directory.Exists(check)){
     if((File.GetAttributes(check)&FileAttributes.ReparsePoint)!=0)throw new IOException("暂不支持链接或重定向文件夹，请选择原文件位置。");
    }else if(mustExist&&string.Equals(check,full,StringComparison.OrdinalIgnoreCase))throw new FileNotFoundException("文件或目录不存在。",full);
    string parent=Path.GetDirectoryName(check);if(parent==check)break;check=parent;
   }
   return full;
  }
  async Task<ConversionReply> RequestAsync(string[] paths,string operation,string target,int quality,CancellationToken token){
   if(!File.Exists(executable))throw new FileNotFoundException("转换组件未安装，请使用包含转换组件的完整版本。");
   ValidateLocalPath(sessionRoot,false);Directory.CreateDirectory(sessionRoot);ValidateLocalPath(sessionRoot,true);
   string session=Path.Combine(sessionRoot,Guid.NewGuid().ToString("N"));
   if(Directory.Exists(session)||File.Exists(session))throw new IOException("临时任务目录已存在，请重试。");
   Directory.CreateDirectory(session);ValidateLocalPath(session,true);
   bool retain=false;
   try{
    string id=Path.GetFileName(session),requestPath=Path.Combine(session,"request.json"),responsePath=Path.Combine(session,"response.json"),staging=Path.Combine(session,"output");
    var request=new WorkerRequest{schemaVersion=1,id=id,operation=operation,inputs=paths,target=target,quality=quality,
     outputDirectory=operation=="convert"?staging:null,responsePath=responsePath,componentRoot=componentsRoot};
    byte[] bytes=new UTF8Encoding(false).GetBytes(Serializer().Serialize(request));
    if(bytes.Length>65536)throw new IOException("文件路径过多或过长，请减少本次文件数量。");
    using(var file=new FileStream(requestPath,FileMode.CreateNew,FileAccess.Write,FileShare.None))file.Write(bytes,0,bytes.Length);
    token.ThrowIfCancellationRequested();
    int exit=await run(requestPath,token,operation=="capabilities"?45000:1800000).ConfigureAwait(false);
    token.ThrowIfCancellationRequested();
    if(!File.Exists(responsePath))throw new IOException("转换组件未返回可验证的结果（退出码 "+exit+"）。未接收任何文件；请检查文件路径或重新安装转换组件。");
    ValidateLocalPath(responsePath,true);var info=new FileInfo(responsePath);
    if(info.Length<=0||info.Length>MaximumResponseBytes)throw new IOException("转换组件返回了无效结果。");
    WorkerResponse response;
    using(var stream=new FileStream(responsePath,FileMode.Open,FileAccess.Read,FileShare.Read)){
     if(stream.Length>MaximumResponseBytes)throw new IOException("转换结果过大。");
     using(var reader=new StreamReader(stream,new UTF8Encoding(false,true)))response=Serializer().Deserialize<WorkerResponse>(reader.ReadToEnd());
    }
    ValidateResponse(response,request,exit,staging);
    var reply=new ConversionReply{Response=response,Session=operation=="convert"?session:null};
    retain=operation=="convert";return reply;
   }finally{if(!retain)DeleteSession(session);}
  }
  static JavaScriptSerializer Serializer(){return new JavaScriptSerializer{MaxJsonLength=MaximumResponseBytes,RecursionLimit=32};}
  internal static void ValidateResponse(WorkerResponse response,WorkerRequest request,int exit,string staging){
   if(response==null||response.schemaVersion!=1||response.id!=request.id||response.operation!=request.operation)throw new IOException("转换组件返回的任务标识不匹配，未接收结果。");
   if(exit!=0||response.status!="ok"){
    if(response.status=="cancelled"&&exit==3)throw new OperationCanceledException();
    throw new IOException(response.error!=null&&!string.IsNullOrWhiteSpace(response.error.message)?response.error.message:"转换组件未完成任务。");
   }
   if(response.error!=null)throw new IOException("转换组件返回了矛盾的任务状态。");
   if(response.files==null||response.files.Length!=request.inputs.Length||response.targets==null||response.outputs==null)throw new IOException("转换组件返回的文件清单不完整。");
   var targetIds=new HashSet<string>(StringComparer.Ordinal);
   foreach(var t in response.targets){
    if(t==null||!ConversionFormats.IsTarget(t.id)||!targetIds.Add(t.id)||t.extension!=ConversionFormats.Extension(t.id)||string.IsNullOrWhiteSpace(t.label))throw new IOException("转换组件返回了不支持的格式。");
   }
   for(int i=0;i<request.inputs.Length;i++){
    var file=response.files[i];
    if(file==null||!string.Equals(file.source,request.inputs[i],StringComparison.OrdinalIgnoreCase)||file.outputs==null||file.targets==null)
     throw new IOException("转换组件返回的原文件不匹配。");
    if(file.targets.Any(t=>!ConversionFormats.IsTarget(t)))throw new IOException("转换组件返回了不支持的文件格式。");
   }
   if(request.operation=="capabilities"){
    if(response.outputs.Length!=0||response.files.Any(f=>f.outputs.Length!=0))throw new IOException("格式检查不应生成结果文件。");
    if(response.targets.Any(t=>response.files.Any(f=>!string.Equals(f.format,t.id,StringComparison.Ordinal)&&!f.targets.Contains(t.id,StringComparer.Ordinal))))throw new IOException("批量转换格式不匹配。");
    return;
   }
   if(response.outputs.Length==0||response.outputs.Length>MaximumOutputs||response.files.Length!=1||!response.files[0].outputs.SequenceEqual(response.outputs,StringComparer.OrdinalIgnoreCase))
    throw new IOException("转换结果文件清单无效。");
   var unique=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0;
   foreach(string output in response.outputs){
    string full=ValidateLocalPath(output,true);
    if(!IsUnder(full,staging)||!File.Exists(full)||Directory.Exists(full)||!unique.Add(full))throw new IOException("转换结果超出临时目录，未接收文件。");
    if(!ConversionFormats.MatchesExtension(full,request.target))throw new IOException("转换结果与目标格式不一致。");
    long length=new FileInfo(full).Length;total+=length;
    if(length<=0||total>MaximumOutputBytes)throw new IOException("转换结果为空或超过本次 16 GB 上限。");
   }
  }
  internal static bool IsUnder(string path,string directory){return Path.GetFullPath(path).StartsWith(Path.GetFullPath(directory).TrimEnd('\\','/')+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);}
  // Worker filenames are implementation details. Present a new copy of the
  // user's original name, and number one-to-many outputs in response/page order.
  // The preview uses this same rule, without running the worker or touching disk.
  internal static string OutputName(string input,string target,int index,int count){
   return OutputNameFromStem(ConversionFormats.NameWithoutExtension(input),target,index,count);
  }
  internal static string OutputNameFromStem(string name,string target,int index,int count){
   if(!ConversionFormats.IsTarget(target))throw new ArgumentException("请选择支持的目标格式。");
   if(count<1||count>MaximumOutputs||index<0||index>=count)throw new ArgumentOutOfRangeException("index");
   var invalid=Path.GetInvalidFileNameChars();
   name=new string((name??"").Select(c=>invalid.Contains(c)?'_':c).ToArray()).Trim().TrimEnd('.');
   if(name.Length>100){name=name.Substring(0,100);if(char.IsHighSurrogate(name[name.Length-1]))name=name.Substring(0,name.Length-1);name=name.TrimEnd(' ','.');}
   if(string.IsNullOrWhiteSpace(name))name="converted";
   string deviceName=name.Split('.')[0].ToUpperInvariant();
   if(deviceName=="CON"||deviceName=="PRN"||deviceName=="AUX"||deviceName=="NUL"||
    (deviceName.Length==4&&(deviceName.StartsWith("COM",StringComparison.Ordinal)||deviceName.StartsWith("LPT",StringComparison.Ordinal))&&deviceName[3]>='1'&&deviceName[3]<='9'))
    name="_"+name;
   return name+(count>1?"-"+(index+1).ToString("D3"):"")+"."+ConversionFormats.Extension(target);
  }
  static string CopyNew(string source,string folder,string outputName){
   string name=ConversionFormats.NameWithoutExtension(outputName),ext=ConversionFormats.FileExtension(outputName);
   for(int i=0;i<10000;i++){
    string target=Path.Combine(folder,name+(i==0?"":" ("+i+")")+ext);
    if(!IsUnder(target,folder))throw new IOException("结果文件名无效。");
    if(File.Exists(target)||Directory.Exists(target))continue;
    bool created=false;
    try{
     using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read))
     using(var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None)){created=true;input.CopyTo(output);output.Flush(true);}
     return target;
    }catch(IOException){if(created){TryDeleteOwnedFile(target,folder);throw;}if(File.Exists(target)||Directory.Exists(target))continue;throw;}
    catch{if(created)TryDeleteOwnedFile(target,folder);throw;}
   }
   throw new IOException("同名结果文件过多，请选择其他结果目录。");
  }
  static void TryDeleteOwnedFile(string path,string parent){
   try{if(IsUnder(path,parent)){ValidateLocalPath(path,true);File.Delete(path);}}catch{}
  }
  void DeleteSession(string session){
   if(TryDeleteSession(session))return;
   // Antivirus/indexing can briefly lock a result after the worker exits. Retry
   // only this task's exact directory, off the UI thread, never a broad temp sweep.
   Task.Run(async delegate{
    foreach(int delay in new[]{100,300,1000}){
     await Task.Delay(delay).ConfigureAwait(false);if(TryDeleteSession(session))return;
    }
   });
  }
  bool TryDeleteSession(string session){
   try{
    if(!IsUnder(session,sessionRoot)||Path.GetFileName(session).Length!=32||!Directory.Exists(session))return true;
    ValidateLocalPath(session,true);
    DeleteNativeWork(session);
    DeleteTreeWithoutLinks(session);
    return true;
   }catch{return false;}
  }
  internal static void DeleteNativeWork(string session){
   string full=Path.GetFullPath(session),id=Path.GetFileName(full);
   Guid parsed;
   if(id.Length!=32||!Guid.TryParseExact(id,"N",out parsed))return;
   // Derive the only permitted location ourselves; never trust a returned
   // arbitrary cleanup path. A marker must prove this exact session owns it.
   string root=Path.Combine(Path.GetTempPath(),"Nook-ConversionNative");
   string work=Path.Combine(root,id);
   if(!Directory.Exists(work))return;
   ValidateLocalPath(work,true);
   string marker=Path.Combine(work,"owner-session.txt");
   if(!File.Exists(marker))return;
   ValidateLocalPath(marker,true);
   if(new FileInfo(marker).Length>4096)return;
   if(!string.Equals(File.ReadAllText(marker).Trim(),full,StringComparison.OrdinalIgnoreCase))return;
   DeleteTreeWithoutLinks(work);
  }
  static void DeleteTreeWithoutLinks(string directory){
   foreach(string child in Directory.GetFileSystemEntries(directory)){
    // Refuse to traverse any substituted directory. Do not follow or delete its target.
    if((File.GetAttributes(child)&FileAttributes.ReparsePoint)!=0)throw new IOException("临时目录包含链接。");
    if(Directory.Exists(child))DeleteTreeWithoutLinks(child);else File.Delete(child);
   }
   Directory.Delete(directory);
  }
  sealed class ConversionReply {internal WorkerResponse Response;internal string Session;}
  internal sealed class WorkerRequest {
   public int schemaVersion {get;set;} public string id {get;set;} public string operation {get;set;}
   public string[] inputs {get;set;} public string target {get;set;} public int quality {get;set;}
   public string outputDirectory {get;set;} public string responsePath {get;set;}
   public string componentId {get;set;} public string componentRoot {get;set;}
  }
  internal sealed class WorkerResponse {
   public int schemaVersion {get;set;} public string id {get;set;} public string operation {get;set;} public string status {get;set;}
   public WorkerTarget[] targets {get;set;} public WorkerFile[] files {get;set;} public string[] outputs {get;set;} public WorkerError error {get;set;}
   public string[] missingEngines {get;set;}
   public string[] missingComponentIds {get;set;} public WorkerComponent[] components {get;set;}
  }
  internal sealed class WorkerTarget {public string id {get;set;} public string label {get;set;} public string extension {get;set;} public string warning {get;set;}}
  internal sealed class WorkerFile {public string source {get;set;} public string format {get;set;} public string[] targets {get;set;} public string[] outputs {get;set;} public string[] warnings {get;set;}}
  internal sealed class WorkerError {public string code {get;set;} public string message {get;set;}}
 }

 // Created suspended, assigned to a kill-on-close job, then resumed. No worker
 // code runs in the gap before containment. Closing Nook terminates only this job.
 internal static class ConversionWorkerProcess {
  internal static Task<int> RunAsync(string executable,string request,CancellationToken token,int timeoutMs,int cancellationGraceMs=250){
   return Task.Run(()=>Run(executable,request,token,timeoutMs,cancellationGraceMs));
  }
  static int Run(string executable,string request,CancellationToken token,int timeoutMs,int cancellationGraceMs){
   token.ThrowIfCancellationRequested();
   FileConversionClient.ValidateLocalPath(executable,true);FileConversionClient.ValidateLocalPath(request,true);
   IntPtr job=IntPtr.Zero;PROCESS_INFORMATION process=new PROCESS_INFORMATION();
   try{
    job=CreateJobObject(IntPtr.Zero,null);if(job==IntPtr.Zero)throw new Win32Exception();
    var limits=new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
    limits.BasicLimitInformation.LimitFlags=0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
    int size=Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));IntPtr data=Marshal.AllocHGlobal(size);
    try{Marshal.StructureToPtr(limits,data,false);if(!SetInformationJobObject(job,9,data,(uint)size))throw new Win32Exception();}finally{Marshal.FreeHGlobal(data);}
    var startup=new STARTUPINFO{cb=Marshal.SizeOf(typeof(STARTUPINFO)),dwFlags=1,wShowWindow=0};
    var command=new StringBuilder("\""+executable+"\" --request \""+request+"\"");
    if(!CreateProcess(executable,command,IntPtr.Zero,IntPtr.Zero,false,0x08000004,IntPtr.Zero,Path.GetDirectoryName(executable),ref startup,out process))throw new Win32Exception();
    if(!AssignProcessToJobObject(job,process.hProcess))throw new Win32Exception();
    if(ResumeThread(process.hThread)==uint.MaxValue)throw new Win32Exception();
    var elapsed=Stopwatch.StartNew();bool cancelled=false,timedOut=false;
    while(WaitForSingleObject(process.hProcess,50)==0x102){
     cancelled=token.IsCancellationRequested;timedOut=elapsed.ElapsedMilliseconds>=timeoutMs;
     if(!cancelled&&!timedOut)continue;
     try{using(var flag=new FileStream(Path.Combine(Path.GetDirectoryName(request),"cancel.flag"),FileMode.CreateNew,FileAccess.Write,FileShare.Read)){}}catch{}
     if(WaitForSingleObject(process.hProcess,(uint)cancellationGraceMs)==0x102){TerminateJobObject(job,3);WaitForSingleObject(process.hProcess,5000);}
     if(cancelled)throw new OperationCanceledException(token);
     throw new TimeoutException();
    }
    token.ThrowIfCancellationRequested();
    uint code;if(!GetExitCodeProcess(process.hProcess,out code)||code==259)throw new IOException("无法读取转换任务的结束状态。");
    return unchecked((int)code);
   }finally{
    // Also terminate a suspended process if assigning it to the job failed.
    if(process.hProcess!=IntPtr.Zero){uint code;if(GetExitCodeProcess(process.hProcess,out code)&&code==259){TerminateProcess(process.hProcess,3);WaitForSingleObject(process.hProcess,5000);}}
    if(job!=IntPtr.Zero)CloseHandle(job);
    if(process.hThread!=IntPtr.Zero)CloseHandle(process.hThread);
    if(process.hProcess!=IntPtr.Zero)CloseHandle(process.hProcess);
   }
  }
  [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct STARTUPINFO {public int cb;public string lpReserved,lpDesktop,lpTitle;public uint dwX,dwY,dwXSize,dwYSize,dwXCountChars,dwYCountChars,dwFillAttribute,dwFlags;public short wShowWindow,cbReserved2;public IntPtr lpReserved2,hStdInput,hStdOutput,hStdError;}
  [StructLayout(LayoutKind.Sequential)]struct PROCESS_INFORMATION {public IntPtr hProcess,hThread;public uint dwProcessId,dwThreadId;}
  [StructLayout(LayoutKind.Sequential)]struct JOBOBJECT_BASIC_LIMIT_INFORMATION {public long PerProcessUserTimeLimit,PerJobUserTimeLimit;public uint LimitFlags;public UIntPtr MinimumWorkingSetSize,MaximumWorkingSetSize;public uint ActiveProcessLimit;public UIntPtr Affinity;public uint PriorityClass,SchedulingClass;}
  [StructLayout(LayoutKind.Sequential)]struct IO_COUNTERS {public ulong ReadOperationCount,WriteOperationCount,OtherOperationCount,ReadTransferCount,WriteTransferCount,OtherTransferCount;}
  [StructLayout(LayoutKind.Sequential)]struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION {public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;public IO_COUNTERS IoInfo;public UIntPtr ProcessMemoryLimit,JobMemoryLimit,PeakProcessMemoryUsed,PeakJobMemoryUsed;}
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr CreateJobObject(IntPtr attributes,string name);
  [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetInformationJobObject(IntPtr job,int infoClass,IntPtr info,uint length);
  [DllImport("kernel32.dll",SetLastError=true)]static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool CreateProcess(string application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,bool inherit,uint flags,IntPtr environment,string currentDirectory,ref STARTUPINFO startup,out PROCESS_INFORMATION process);
  [DllImport("kernel32.dll",SetLastError=true)]static extern uint ResumeThread(IntPtr thread);
  [DllImport("kernel32.dll",SetLastError=true)]static extern uint WaitForSingleObject(IntPtr handle,uint timeout);
  [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetExitCodeProcess(IntPtr process,out uint code);
  [DllImport("kernel32.dll",SetLastError=true)]static extern bool TerminateProcess(IntPtr process,uint code);
  [DllImport("kernel32.dll",SetLastError=true)]static extern bool TerminateJobObject(IntPtr job,uint code);
  [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
 }
}
