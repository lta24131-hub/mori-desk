#if CAPTURE_STARTUP_TIMING_TESTS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using QingJie;

// Standalone pure diagnostic tests: no application, screenshot, native API,
// settings, real data, desktop interaction or network assembly is compiled.
internal static class CaptureStartupTimingTests {
    static int checks;
    static void Check(bool passed,string message){if(!passed)throw new InvalidOperationException(message);checks++;Console.WriteLine("PASS "+message);}
    static string Sample(int id,int length){return "v1 id="+id.ToString("000",CultureInfo.InvariantCulture)+" utc=2026-01-01T00:00:00.000Z mode=hotkey shown[0]=40.000ms "+new string('x',length);}
    static int Main(string[] args){
        try{
            if(args.Length!=1||!Path.IsPathRooted(args[0])||!File.Exists(Path.Combine(args[0],".startup-timing-test")))throw new InvalidOperationException("Prepared isolated test directory required.");
            string sandbox=args[0];var samples=new List<string>();double clock=1;
            var timing=new CaptureStartupTiming("synthetic","hotkey",()=>clock,(id,line)=>samples.Add(line));
            CaptureStartupTiming.Mark(timing,CaptureStartupStage.Queued);clock=42.25;CaptureStartupTiming.Mark(timing,CaptureStartupStage.Settle);
            clock=55.5;CaptureStartupTiming.Mark(timing,CaptureStartupStage.Screens,-1,2);clock=61;CaptureStartupTiming.Mark(timing,CaptureStartupStage.Targets);
            clock=70;CaptureStartupTiming.Mark(timing,CaptureStartupStage.Constructed,0);
            Check(samples.Count==0,"screen-acquisition stages never start file publication before the overlay is shown");
            clock=72;CaptureStartupTiming.Mark(timing,CaptureStartupStage.Shown,0);clock=90.125;CaptureStartupTiming.Mark(timing,CaptureStartupStage.ContentRendered,0);
            Check(samples.Count==2&&samples[1].Contains("settle=42.250ms screens=55.500ms/count:2 targets=61.000ms constructed[0]=70.000ms shown[0]=72.000ms contentrendered[0]=90.125ms"),"sample retains each exact shortcut-relative phase and first ContentRendered timestamp");
            Check(samples.All(line=>line.StartsWith("v1 id=synthetic utc=",StringComparison.Ordinal)&&line.Contains(" mode=hotkey "))&&samples[1].Contains("/count:2"),"timing output identifies its version, capture source and anonymous screen count");
            Check(!samples[1].Contains('\\')&&!samples[1].Contains(":\\")&&!samples[1].Contains("http")&&samples[1].All(c=>c>=32&&c<=126),"production-shaped sample contains ASCII metadata without titles, source paths or URLs");
            var broken=new CaptureStartupTiming("broken","ui",()=>{throw new IOException("must not escape");},(id,line)=>{throw new Exception("must not escape");});
            CaptureStartupTiming.Mark(broken,CaptureStartupStage.ContentRendered);CaptureStartupTiming.Mark(null,CaptureStartupStage.Failed);
            var brokenOutput=new CaptureStartupTiming("broken-output","ui",()=>1,(id,line)=>{throw new IOException("must not escape");});CaptureStartupTiming.Mark(brokenOutput,CaptureStartupStage.Shown);
            Check(true,"timing clock, callback and absent-session failures never escape into capture");
            var dispatched=new Queue<Action>();var written=new List<string>();int dispatches=0;
            var writer=new CaptureStartupTimingWriter(action=>{dispatches++;dispatched.Enqueue(action);},line=>written.Add(line));
            for(int i=0;i<100;i++)writer.Enqueue("same","revision="+i);
            Check(dispatches==1&&written.Count==0,"rapid phase updates schedule one asynchronous writer without synchronous I/O");dispatched.Dequeue()();
            Check(written.SequenceEqual(new[]{"revision=99"}),"queued updates coalesce to the latest sample for the same capture");
            for(int i=0;i<50;i++)writer.Enqueue(i.ToString(CultureInfo.InvariantCulture),"sample="+i);dispatched.Dequeue()();
            Check(written.Count==17&&written[1]=="sample=34"&&written.Last()=="sample=49","pending writer memory is bounded to the latest sixteen capture sessions");
            var throwWriter=new CaptureStartupTimingWriter(action=>action(),line=>{throw new IOException("synthetic inaccessible disk");});throwWriter.Enqueue("a","one");throwWriter.Enqueue("b","two");
            int schedules=0;var throwSchedule=new CaptureStartupTimingWriter(action=>{schedules++;throw new IOException("synthetic scheduler failure");},line=>{});throwSchedule.Enqueue("a","one");throwSchedule.Enqueue("b","two");
            Check(schedules==2,"I/O and scheduling failures are swallowed and do not wedge subsequent diagnostics");
            string log="";for(int i=0;i<100;i++){log=CaptureStartupLog.KeepRecent(log,Sample(i,350));Check(Encoding.UTF8.GetByteCount(log)<=CaptureStartupLog.MaximumBytes,"recent-sample output remains below sixteen KiB at append "+i);}
            Check(log.Contains("v1 id=099 ")&&!log.Contains("v1 id=000 ")&&log.EndsWith("\n",StringComparison.Ordinal),"bounded output retains newest complete records rather than truncated lines");
            log=CaptureStartupLog.KeepRecent(log,Sample(99,20));Check(log.Split('\n').Count(line=>line.StartsWith("v1 id=099 ",StringComparison.Ordinal))==1&&log.EndsWith(Sample(99,20)+"\n",StringComparison.Ordinal),"later first-paint timing replaces its earlier show sample without duplicate records");
            Check(CaptureStartupLog.KeepRecent(log,"not a timing record")==""&&CaptureStartupLog.KeepRecent(log,Sample(1,4100))==""&&CaptureStartupLog.KeepRecent(log,Sample(1,2)+"\nextra")=="","oversized, multiline and malformed records are rejected");
            string path=Path.Combine(sandbox,"capture","startup-timings.log");
            for(int i=0;i<80;i++)CaptureStartupLog.Write(path,Sample(i,350));
            Check(new FileInfo(path).Length<=CaptureStartupLog.MaximumBytes&&File.ReadAllText(path).Contains("v1 id=079 "),"real local writer creates a bounded file containing its latest metadata sample");
            string blocked=Path.Combine(sandbox,"not-a-directory");File.WriteAllText(blocked,"fixture");CaptureStartupLog.Write(Path.Combine(blocked,"startup-timings.log"),Sample(1,0));
            Check(File.ReadAllText(blocked)=="fixture","inaccessible log destination fails safely without changing another file");
            Console.WriteLine("PASS "+checks+" startup timing checks");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
namespace MoriDesk {internal static class Entry {internal static string DataRoot {get{throw new InvalidOperationException("Production data is forbidden in pure timing tests.");}}}}
#endif
