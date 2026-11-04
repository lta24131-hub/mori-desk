using System;
using System.IO;
using System.Linq;
using Mori;

namespace MoriDesk {
 internal static class SvgWorkerProtocolTests {
  static bool Reject(Action action){try{action();return false;}catch(InvalidDataException){return true;}catch(EndOfStreamException){return true;}}
  internal static void Run(Action<bool,string> check){
   var startup=new WorkerFailureDiagnostic("svg");
   check(!SvgRaster.WorkerLimitReached(startup,7999,-1,0),"cold SVG startup retains its bounded initialization budget");
   check(SvgRaster.WorkerLimitReached(startup,8001,-1,0)&&startup.Reason=="startup-timeout","worker that never becomes ready cannot wait indefinitely");
   var rendering=new WorkerFailureDiagnostic("svg");
   check(!SvgRaster.WorkerLimitReached(rendering,12000,5000,0),"five-second cold startup does not steal seven-second document render budget");
   check(SvgRaster.WorkerLimitReached(rendering,13001,5000,0)&&rendering.Reason=="timeout"&&rendering.ElapsedMilliseconds==13001,"render timeout remains bounded and diagnostics retain total elapsed time");
   foreach(long readyAt in new long[]{-1,50}){
    var memory=new WorkerFailureDiagnostic("svg");check(SvgRaster.WorkerLimitReached(memory,100,readyAt,256L*1024*1024+1)&&memory.MemoryExceeded,"memory protection covers startup and rendering / "+readyAt);
   }
   int ready=0;byte[] payload={1,2,3,4,5};
   using(var stream=new MemoryStream()){
    var writer=new BinaryWriter(stream);writer.Write(SvgRaster.WorkerReadyMagic);writer.Write(payload);writer.Flush();stream.Position=0;
    var result=SvgRaster.ReadWorkerOutput(stream,()=>{ready++;check(stream.Position==4,"worker readiness is reported before consuming image payload");});
    check(ready==1&&result.SequenceEqual(payload)&&stream.CanRead,"valid handshake is stripped once and caller keeps stream ownership");
   }
   foreach(var bytes in new[]{new byte[0],new byte[3],new byte[4]})using(var input=new MemoryStream(bytes))check(Reject(()=>SvgRaster.ReadWorkerOutput(input,()=>ready++))&&ready==1,"missing or invalid handshake cannot grant a fresh render budget / "+bytes.Length);
   using(var stream=new MemoryStream()){
    var writer=new BinaryWriter(stream);writer.Write(SvgRaster.WorkerReadyMagic);writer.Write(new byte[SvgRaster.MaximumOutput+1]);writer.Flush();stream.Position=0;
    check(Reject(()=>SvgRaster.ReadWorkerOutput(stream,()=>ready++)),"ready worker cannot bypass the image output size cap");
   }
  }
 }
}
