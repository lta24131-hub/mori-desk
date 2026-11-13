using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Mori;

namespace MoriDesk {
 // VM-only execution: real video/PDF children and controlled sampling races.
 // The seam replaces only the process metric read, never decoding or output.
 internal static class WorkerExitSamplingTests {
  static readonly BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
  static void ProbeContracts(Action<bool,string> check){
   using(var current=Process.GetCurrentProcess()){
    foreach(Exception expected in new Exception[]{new InvalidOperationException("live invalid-operation"),new Win32Exception(5,"live access-denied"),new IOException("unrelated I/O failure")}){
     WorkerIo.PrivateMemoryForTests=p=>{throw expected;};Exception caught=null;long memory;
     try{WorkerIo.TryReadPrivateMemory(current,out memory);}catch(Exception error){caught=error;}
     check(ReferenceEquals(caught,expected),"a live process metric failure is preserved, not treated as exit: "+expected.GetType().Name);
    }
    WorkerIo.PrivateMemoryForTests=p=>123456;long observed;
    check(WorkerIo.TryReadPrivateMemory(current,out observed)&&observed==123456,"a live process sample retains its exact byte count");
   }
  }
  static void VideoExit(string root,string extension,byte[] bytes,Exception exitRace,Action<bool,string> check){
   string file=Path.Combine(root,"exit-race"+extension);File.WriteAllBytes(file,bytes);DateTime stamp=File.GetLastWriteTimeUtc(file);int calls=0,exitCode=-1;bool unrelatedPropagated=false;
   VideoThumbnail.ClearFailures();WorkerIo.PrivateMemoryForTests=child=>{
    calls++;if(!child.WaitForExit(VideoThumbnail.MaximumMilliseconds))throw new TimeoutException("Video fixture did not finish before the controlled metric race.");
    exitCode=child.ExitCode;
    var installed=WorkerIo.PrivateMemoryForTests;var unrelated=new IOException("unrelated error after exit");Exception caught=null;long ignored;
    try{WorkerIo.PrivateMemoryForTests=p=>{throw unrelated;};try{WorkerIo.TryReadPrivateMemory(child,out ignored);}catch(Exception error){caught=error;}unrelatedPropagated=ReferenceEquals(caught,unrelated);}
    finally{WorkerIo.PrivateMemoryForTests=installed;}
    throw exitRace;
   };
   int width,height;var bitmap=VideoThumbnail.Render(file,out width,out height);
   File.WriteAllText(Path.Combine(root,"exit-race"+extension+".txt"),"failure="+VideoThumbnail.LastFailure+Environment.NewLine+VideoThumbnail.LastWorkerDiagnostic);
   check(calls==1&&exitCode==0,"real video child exits normally during exactly one controlled sample: "+extension);
   check(unrelatedPropagated,"even an exited process cannot hide an unrelated sampling exception: "+extension);
   check(bitmap!=null&&bitmap.IsFrozen&&bitmap.PixelWidth>0&&bitmap.PixelHeight>0&&width>0&&height>0,"completed real video output survives the metric exit race: "+extension+" / "+VideoThumbnail.LastFailure);
   check(VideoThumbnail.LastFailure==null&&!VideoThumbnail.FailureCache.Has(ThumbnailBuildGate.SourceStamp(file),DateTime.UtcNow),"a successful raced video never acquires a ten-minute failure-cache entry: "+extension);
   check(File.ReadAllBytes(file).SequenceEqual(bytes)&&File.GetLastWriteTimeUtc(file)==stamp,"video exit sampling preserves original bytes and timestamp: "+extension);
  }
  static void VideoRejects(string root,string name,Exception failure,bool overLimit,Action<bool,string> check){
   string file=Path.Combine(root,name+".webm");File.WriteAllBytes(file,WebMediaFixtures.WebM());int calls=0;
   VideoThumbnail.ClearFailures();WorkerIo.PrivateMemoryForTests=child=>{
    calls++;if(child.HasExited)throw new InvalidDataException("Rejection fixture must sample a live child.");
    if(overLimit)return VideoThumbnail.MaximumPrivateMemory+1;throw failure;
   };
   int width,height;var image=VideoThumbnail.Render(file,out width,out height);
   string expected=overLimit?"private-memory-limit":failure.Message;
   check(calls==1&&image==null&&width==0&&height==0&&(VideoThumbnail.LastFailure??"").Contains(expected),"actual video path rejects a live metric error or over-budget worker: "+name);
   check(VideoThumbnail.FailureCache.Has(ThumbnailBuildGate.SourceStamp(file),DateTime.UtcNow),"actual worker rejection remains recorded rather than becoming a false success: "+name);
  }
  static void PdfExit(string root,bool failedResponse,Action<bool,string> check){
   string file=Path.Combine(root,failedResponse?"pdf-fault.pdf":"pdf-complete.pdf");byte[] original=PdfTextTests.Fixture();File.WriteAllBytes(file,original);
   WorkerIo.PrivateMemoryForTests=null;var connection=PdfRenderConnection.Open(file,-1,CancellationToken.None);int calls=0,exitCode=-1;
   using(var sampling=new ManualResetEventSlim()){
    try{
     var output=(BinaryWriter)typeof(PdfRenderConnection).GetField("output",Hidden).GetValue(connection);
     var run=typeof(PdfRenderConnection).GetMethod("Run",Hidden).MakeGenericMethod(typeof(string));
     // Keep pending incomplete until Run has checked the child is alive and
     // actually entered its metric read. Then EOF lets the real PDF child exit.
     Func<string> action=()=>{
      if(!sampling.Wait(10000))throw new TimeoutException("PDF sampling fixture was never reached.");
      output.Dispose();if(failedResponse)throw new EndOfStreamException("controlled incomplete PDF response");return "already-complete-response";
     };
     WorkerIo.PrivateMemoryForTests=child=>{
      calls++;sampling.Set();if(!child.WaitForExit(PdfRenderConnection.TimeoutMilliseconds))throw new TimeoutException("PDF fixture did not exit after input EOF.");
      exitCode=child.ExitCode;throw new InvalidOperationException("controlled PDF metric exit race");
     };
     object value=null;Exception caught=null;
     try{value=run.Invoke(connection,new object[]{action,CancellationToken.None});}catch(TargetInvocationException error){caught=error.InnerException;}
     check(calls==1&&exitCode==0,"PDF child exits normally after entering the metric read / failedResponse="+failedResponse);
     check(failedResponse?caught is EndOfStreamException:caught==null&&Equals(value,"already-complete-response"),failedResponse?"PDF pipe failure still propagates after metric sampling observes exit":"PDF completed response survives metric sampling exit and the existing drain path");
    }finally{sampling.Set();WorkerIo.PrivateMemoryForTests=null;connection.Dispose();}
   }
   check(connection.ExitCode==0&&!connection.ForcedStop,"PDF metric race does not force-kill an already-exited child / failedResponse="+failedResponse);
   check(File.ReadAllBytes(file).SequenceEqual(original),"PDF sampling fixture preserves document bytes / failedResponse="+failedResponse);
  }
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);var previous=WorkerIo.PrivateMemoryForTests;
   try{
    ProbeContracts(check);
    VideoExit(root,".webm",WebMediaFixtures.WebM(),new InvalidOperationException("controlled video exit race"),check);
    VideoExit(root,".mp4",WebMediaFixtures.Mp4(),new Win32Exception(6,"controlled exited handle"),check);
    VideoRejects(root,"live-invalid",new InvalidOperationException("controlled live metric error"),false,check);
    VideoRejects(root,"live-win32",new Win32Exception(5,"controlled live access error"),false,check);
    VideoRejects(root,"memory-limit",null,true,check);
    PdfExit(root,false,check);PdfExit(root,true,check);
   }finally{WorkerIo.PrivateMemoryForTests=previous;VideoThumbnail.ClearFailures();}
  }
 }
}
