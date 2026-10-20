using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace QingJie {
    internal enum ScrollCaptureStartupStage {Click,WarmClaim,ColdLaunch,RequestPublished,Prepared,Handoff,StartReleased,Ready,Failed}
    // One clock per process: helper timings remain separate. Never subtract
    // Electron's performance clock from this Stopwatch, or log pixels/paths.
    internal sealed class ScrollCaptureStartupTiming {
        readonly object gate=new object();
        readonly string id,started;
        readonly Func<double> elapsed;
        readonly Action<string,string> publish;
        readonly List<string> stages=new List<string>();
        static readonly CaptureStartupTimingWriter writer=new CaptureStartupTimingWriter(action=>ThreadPool.QueueUserWorkItem(ignored=>action()),sample=>CaptureStartupLog.Write(Path.Combine(MoriDesk.Entry.DataRoot,"capture","scroll-startup-timings.log"),sample));
        internal ScrollCaptureStartupTiming(string identifier,Func<double> milliseconds,Action<string,string> output){id=identifier;elapsed=milliseconds;publish=output;started=DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ",CultureInfo.InvariantCulture);}
        internal static ScrollCaptureStartupTiming Start(){var watch=Stopwatch.StartNew();return new ScrollCaptureStartupTiming(Guid.NewGuid().ToString("N").Substring(0,12),()=>watch.Elapsed.TotalMilliseconds,(id,sample)=>writer.Enqueue(id,sample));}
        internal static void Mark(ScrollCaptureStartupTiming timing,ScrollCaptureStartupStage stage){if(timing==null)return;try{timing.Record(stage);}catch{}}
        void Record(ScrollCaptureStartupStage stage){
            lock(gate){
                if(!Enum.IsDefined(typeof(ScrollCaptureStartupStage),stage)||stages.Count>=16)return;
                double value=elapsed();if(double.IsNaN(value)||double.IsInfinity(value))value=0;value=Math.Max(0,Math.Min(86400000,value));
                stages.Add(stage.ToString().ToLowerInvariant()+"="+value.ToString("0.000",CultureInfo.InvariantCulture)+"ms");
                if(stage==ScrollCaptureStartupStage.Ready||stage==ScrollCaptureStartupStage.Failed)publish(id,"v1 id="+id+" utc="+started+" mode=scroll "+string.Join(" ",stages));
            }
        }
    }
}
