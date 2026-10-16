using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace QingJie {
    internal enum CaptureStartupStage {Queued,Settle,Screens,Targets,Constructed,Shown,ContentRendered,Failed}

    // Metadata only: never accepts image pixels, titles, URLs, paths or error
    // messages. File I/O is deferred until a single background writer drains.
    internal sealed class CaptureStartupTiming {
        readonly string id,mode,started;
        readonly Func<double> elapsed;
        readonly Action<string,string> publish;
        readonly List<string> stages=new List<string>();
        internal CaptureStartupTiming(string identifier,string source,Func<double> milliseconds,Action<string,string> output){
            id=identifier;mode=source;elapsed=milliseconds;publish=output;started=DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ",CultureInfo.InvariantCulture);
        }
        internal static CaptureStartupTiming Start(long origin,bool hotkey,bool fixture){
            try{return new CaptureStartupTiming(Guid.NewGuid().ToString("N").Substring(0,12),fixture?"fixture":hotkey?"hotkey":"ui",()=>Math.Max(0,(Stopwatch.GetTimestamp()-origin)*1000.0/Stopwatch.Frequency),CaptureStartupLog.Queue);}
            catch{return null;}
        }
        internal static void Mark(CaptureStartupTiming timing,CaptureStartupStage stage,int index=-1,int count=-1){
            if(timing==null)return;
            try{timing.Record(stage,index,count);}catch{ /* Diagnostics never interrupt capture. */ }
        }
        void Record(CaptureStartupStage stage,int index,int count){
            if(!Enum.IsDefined(typeof(CaptureStartupStage),stage)||stages.Count>=64)return;
            double value=elapsed();if(double.IsNaN(value)||double.IsInfinity(value))value=0;value=Math.Max(0,Math.Min(86400000,value));
            string entry=stage.ToString().ToLowerInvariant()+(index>=0?"["+Math.Min(index,999).ToString(CultureInfo.InvariantCulture)+"]":"")+"="+value.ToString("0.000",CultureInfo.InvariantCulture)+"ms";
            if(count>=0)entry+="/count:"+Math.Min(count,999).ToString(CultureInfo.InvariantCulture);
            stages.Add(entry);
            // Do not compete with screen acquisition or native target discovery
            // for file I/O. First publish happens only once an overlay is shown.
            if(stage!=CaptureStartupStage.Shown&&stage!=CaptureStartupStage.ContentRendered&&stage!=CaptureStartupStage.Failed)return;
            string sample="v1 id="+id+" utc="+started+" mode="+mode+" "+string.Join(" ",stages);
            publish(id,sample);
        }
    }

    internal static class CaptureStartupLog {
        // ASCII metadata leaves headroom below the requested 16 KiB limit.
        internal const int MaximumBytes=16000;
        static readonly CaptureStartupTimingWriter writer=new CaptureStartupTimingWriter(
            action=>ThreadPool.QueueUserWorkItem(ignored=>action()),
            sample=>Write(Path.Combine(MoriDesk.Entry.DataRoot,"capture","startup-timings.log"),sample));
        internal static void Queue(string id,string sample){try{writer.Enqueue(id,sample);}catch{}}
        internal static string KeepRecent(string previous,string sample){
            if(string.IsNullOrEmpty(sample)||sample.Length>4096||sample.IndexOf('\n')>=0||sample.IndexOf('\r')>=0||sample.Any(c=>c<32||c>126))return "";
            var separator=sample.IndexOf(' ',6);if(!sample.StartsWith("v1 id=",StringComparison.Ordinal)||separator<7)return "";
            string prefix=sample.Substring(0,separator+1);
            var lines=(previous??"").Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Where(line=>line.StartsWith("v1 id=",StringComparison.Ordinal)&&!line.StartsWith(prefix,StringComparison.Ordinal)&&line.Length<=4096&&line.All(c=>c>=32&&c<=126)).ToList();
            lines.Add(sample);
            int length=lines.Sum(line=>line.Length+1);
            while(length>MaximumBytes&&lines.Count>1){length-=lines[0].Length+1;lines.RemoveAt(0);}
            return string.Join("\n",lines)+"\n";
        }
        internal static void Write(string path,string sample){
            try{
                string previous="";
                if(File.Exists(path))using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)){
                    bool partial=stream.Length>MaximumBytes;if(partial)stream.Seek(-MaximumBytes,SeekOrigin.End);
                    using(var reader=new StreamReader(stream,Encoding.ASCII)){if(partial)reader.ReadLine();previous=reader.ReadToEnd();}
                }
                string content=KeepRecent(previous,sample);if(content.Length==0)return;
                Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,content,new UTF8Encoding(false));
            }catch{ /* Best-effort local timing must never affect screenshots. */ }
        }
    }

    internal sealed class CaptureStartupTimingWriter {
        readonly object gate=new object();
        readonly Dictionary<string,string> latest=new Dictionary<string,string>();
        readonly Queue<string> order=new Queue<string>();
        readonly Action<Action> schedule;
        readonly Action<string> write;
        bool active;
        internal CaptureStartupTimingWriter(Action<Action> dispatch,Action<string> output){schedule=dispatch;write=output;}
        internal void Enqueue(string id,string sample){
            try{
                lock(gate){
                    if(!latest.ContainsKey(id)){if(order.Count>=16)latest.Remove(order.Dequeue());order.Enqueue(id);}
                    latest[id]=sample;if(active)return;active=true;
                }
                try{schedule(Drain);}catch{lock(gate){active=false;latest.Clear();order.Clear();}}
            }catch{}
        }
        void Drain(){
            while(true){
                string sample;
                lock(gate){if(order.Count==0){active=false;return;}string id=order.Dequeue();sample=latest[id];latest.Remove(id);}
                try{write(sample);}catch{}
            }
        }
    }
}
