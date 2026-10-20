using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using QingJie;

// Every process boundary, queue and clock below is a fake. Only newly generated
// GUID filesystem fixtures are used; no screens, helpers, clipboard or UI run.
static class ScrollCaptureWarmLeaseTests {
    const string AppBase=@"C:\Nook warm fixture";
    sealed class FakeChild : IScrollCaptureChild {
        internal bool Exited;
        internal int Waits,Kills,Disposals;
        public bool HasExited {get{return Exited;}}
        public int ExitCode {get{return 0;}}
        public bool WaitForExit(int milliseconds){Waits++;Exited=true;return true;}
        public void Kill(){Kills++;Exited=true;}
        public void Dispose(){Disposals++;}
    }
    sealed class Harness {
        internal long Elapsed;
        internal int Created,Launched,Cleaned;
        internal bool Fail,ExitEarly,FailRetirement;
        internal readonly Queue<Action> Work=new Queue<Action>();
        internal readonly List<ScrollCaptureWarmLease> Leases=new List<ScrollCaptureWarmLease>();
        internal readonly List<FakeChild> Children=new List<FakeChild>();
        internal Action DuringPrepare;
        internal ScrollCaptureWarmLease Create(long owner){
            Created++;
            var lease=new ScrollCaptureWarmLease(AppBase,owner,action=>{Work.Enqueue(action);return Task.FromResult(true);},
                value=>{Launched++;if(DuringPrepare!=null)DuringPrepare();if(Fail)throw new IOException("synthetic launch failure");var child=new FakeChild{Exited=ExitEarly};Children.Add(child);return child;},
                (value,child)=>{Cleaned++;if(FailRetirement)throw new IOException("synthetic retirement failure");if(child!=null){((FakeChild)child).Exited=true;child.Dispose();}},()=>Elapsed);
            Leases.Add(lease);return lease;
        }
        internal void Drain(){while(Work.Count>0)Work.Dequeue()();}
    }
    static ExternalScrollCaptureSession Session(ScrollCaptureWarmLease lease=null,CaptureFeatures features=null){return new ExternalScrollCaptureSession(AppBase,new System.Drawing.Rectangle(30,40,400,600),new System.Drawing.Rectangle(0,0,1920,1080),1,features:features,warm:lease);}
    static bool Reject(Action action){try{action();return false;}catch(InvalidDataException){return true;}catch(ArgumentException){return true;}}
    internal static void Run(Action<bool,string> check){Gating(check);Claiming(check);TimeoutAndCancellation(check);Fallback(check);Protocol(check);Files(check);Timing(check);}
    static void Gating(Action<bool,string> check){
        foreach(string mode in new[]{"scroll-off","prewarm-off","synthetic","null"}){
            var h=new Harness();var c=new ScrollCaptureWarmCoordinator(h.Create);var features=new CaptureFeatures();
            if(mode=="scroll-off")features.Scrolling=false;if(mode=="prewarm-off")features.ScrollPrewarm=false;
            c.Begin(1,mode=="null"?null:features,mode=="synthetic");c.Rendered(1);c.Rendered(1);
            check(h.Created==0&&h.Work.Count==0&&h.Launched==0,"disabled or synthetic screenshot acquires no warm resource: "+mode);c.Cancel();
        }
        {
            var h=new Harness();var c=new ScrollCaptureWarmCoordinator(h.Create);c.Begin(1,new CaptureFeatures(),false);
            c.Rendered(1);c.Rendered(1);c.Rendered(1);
            check(h.Created==1&&h.Work.Count==1&&h.Launched==0,"two monitor first-paint callbacks schedule exactly one background job and return before any launch or IO");
            c.Cancel();c.Rendered(1);h.Drain();
            check(h.Launched==0&&h.Cleaned==1&&h.Leases[0].Retirement.Result,"close before queued preparation never launches a late helper");
            c.Begin(2,new CaptureFeatures(),false);c.Rendered(1);check(h.Created==1,"old first-paint callback cannot start the new capture warm lease");
            c.Rendered(2);h.Drain();check(h.Launched==1,"current screenshot starts a fresh one-shot warm lease");c.Cancel();
            check(h.Children[0].Disposals==0,"UI cancellation schedules retirement without waiting on a process");h.Drain();check(h.Children[0].Disposals==1,"retirement disposes only the owned fake child");
        }
    }
    static void Claiming(Action<bool,string> check){
        {
            var h=new Harness();var c=new ScrollCaptureWarmCoordinator(h.Create);c.Begin(11,new CaptureFeatures(),false);c.Rendered(11);
            check(c.Claim(10)==null&&!h.Leases[0].IsClaimed,"another generation cannot steal a warm lease");
            var lease=c.Claim(11);check(lease!=null&&lease.IsClaimed&&!lease.IsPrepared,"click may claim the helper before its startup task finishes");
            lease.ExpireIfUnclaimed();check(!lease.IsDisposed,"already queued TTL callback cannot cancel a claimed warm session");
            c.Cancel();check(!lease.IsDisposed&&c.Claim(11)==null,"coordinator no longer owns the one-shot claim after transfer to a capture session");
            var pending=lease.TakeProcessAsync();check(!pending.IsCompleted,"claim waits asynchronously while preparation is pending");
            h.Drain();var child=pending.GetAwaiter().GetResult();check(child==h.Children[0]&&lease.IsTransferred,"prepared child transfers exactly once");
            check(lease.TakeProcessAsync().GetAwaiter().GetResult()==null,"duplicate transfer cannot acquire the same child");
            lease.Dispose();h.Drain();check(h.Cleaned==0&&h.Children[0].Disposals==0,"expired/disposed old owner cannot kill transferred helper");child.Dispose();
        }
        {
            var h=new Harness();var lease=h.Create(12);lease.Start();lease.TryClaim(12);var pending=lease.TakeProcessAsync();lease.Dispose();h.Drain();
            check(pending.GetAwaiter().GetResult()==null&&h.Launched==0&&lease.Retirement.Result,"cancelled claimed session ignores late queued preparation");
        }
        {
            var h=new Harness();var lease=h.Create(13);lease.Start();lease.TryClaim(13);h.DuringPrepare=()=>lease.Dispose();h.Drain();
            check(lease.IsDisposed&&lease.TakeProcessAsync().GetAwaiter().GetResult()==null&&h.Children[0].Disposals==1,"cancellation during launch retires the late child without publishing it");
        }
        {
            var h=new Harness();var lease=h.Create(14);lease.Start();h.Drain();lease.TryClaim(14);lease.Dispose();
            check(lease.TakeProcessAsync().GetAwaiter().GetResult()==null,"prepared child cannot override cancellation priority");h.Drain();check(h.Children[0].Disposals==1,"prepared-before-cancel child is retired once");
        }
    }
    static void Fallback(Action<bool,string> check){
        foreach(string mode in new[]{"failed","expired","early-exit","disabled"}){
            var h=new Harness{Fail=mode=="failed",ExitEarly=mode=="early-exit"};var c=new ScrollCaptureWarmCoordinator(h.Create);c.Begin(20,new CaptureFeatures(),false);c.Rendered(20);h.Drain();
            if(mode=="expired")h.Elapsed=ScrollCaptureWarmLease.LifetimeMilliseconds;
            var lease=c.Claim(20);var features=new CaptureFeatures{ScrollPrewarm=mode!="disabled"};
            using(var session=Session(lease,features)){
                string coldId=session.SessionId;var task=session.AdoptWarmProcessAsync();
                h.Drain();check(!task.GetAwaiter().GetResult()&&session.SessionId==coldId&&session.SessionId!=lease.SessionId,"unavailable warm helper falls back only after retirement, keeping a fresh cold GUID: "+mode);
                check(h.Leases[0].Retirement.IsCompleted&&h.Cleaned==1,"warm fallback has no still-owned old process: "+mode);
            }
        }
        {
            var h=new Harness{FailRetirement=true};var lease=h.Create(21);lease.Start();h.Drain();lease.TryClaim(21);lease.Dispose();h.Drain();
            using(var session=Session(lease)){bool refused=false;try{session.AdoptWarmProcessAsync().GetAwaiter().GetResult();}catch(IOException){refused=true;}check(refused,"uncertain old-process retirement refuses a concurrent cold launch");}
        }
        {
            var h=new Harness();var lease=h.Create(22);lease.Start();h.Drain();lease.TryClaim(22);
            using(var session=Session(lease)){
                check(session.AdoptWarmProcessAsync().GetAwaiter().GetResult()&&session.SessionId==lease.SessionId&&session.OutputDirectory==lease.OutputDirectory,"successful session adopts warm identity and exact directory rather than spawning again");
                h.Children[0].Exited=true;string warmId=session.SessionId;session.RetireWarmForFallback(h.Children[0]);
                check(session.SessionId!=warmId&&!Directory.Exists(session.OutputDirectory)&&h.Children[0].Disposals==1,"helper expiry at attach boundary retires before cold fallback and never reuses warm directory");
            }
        }
    }
    static void TimeoutAndCancellation(Action<bool,string> check){
        {
            var h=new Harness();var lease=h.Create(40);lease.Start();lease.TryClaim(40);
            using(var session=Session(lease)){
                bool timedOut=false;try{session.AdoptWarmProcessAsync(5).GetAwaiter().GetResult();}catch(TimeoutException){timedOut=true;}
                check(timedOut&&lease.IsDisposed&&!lease.Retirement.IsCompleted&&h.Launched==0,"frozen preparation times out without waiting indefinitely or starting a concurrent cold helper");
                h.Drain();check(lease.Retirement.Result&&h.Launched==0,"preparation arriving after timeout observes cancellation and retires without launching");
            }
        }
        {
            var h=new Harness();var lease=h.Create(41);lease.Start();lease.TryClaim(41);
            using(var session=Session(lease)){
                var pending=session.AdoptWarmProcessAsync();session.Dispose();bool cancelled=false;
                try{pending.GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}
                check(cancelled&&!lease.Retirement.IsCompleted&&h.Launched==0,"Esc cancels even when warm preparation has not returned; it does not wait for the worker");
                h.Drain();check(lease.Retirement.Result&&h.Launched==0,"late worker after session cancellation cannot revive old startup");
            }
        }
    }
    static void Protocol(Action<bool,string> check){
        var h=new Harness();var lease=h.Create(30);var value=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(lease.SerializeWarmRequest());
        check(value.Count==4&&(int)value["schemaVersion"]==1&&(string)value["sessionId"]==lease.SessionId&&(string)value["outputDirectory"]==lease.OutputDirectory&&(int)value["parentProcessId"]==System.Diagnostics.Process.GetCurrentProcess().Id,"warm request contains only bounded ownership metadata, no monitor, region, pixels or annotations");
        check(ScrollCaptureWarmLease.LaunchArguments(Path.Combine(lease.OutputDirectory,"warm.json")).StartsWith("--nook-warm \"",StringComparison.Ordinal),"warm launch uses fixed mode and quoted absolute generated path");
        foreach(string path in new[]{"relative.json","C:\\bad\"path","C:\\bad\rpath"})check(Reject(()=>ScrollCaptureWarmLease.LaunchArguments(path)),"warm arguments reject unsafe paths");
        string manifest="{\"schemaVersion\":1,\"deferredStart\":true,\"captureFeaturePolicy\":true,\"capturePrewarm\":true}";
        ScrollCaptureWarmLease.ValidateComponent(manifest);check(true,"warm launch requires explicit helper capability");
        foreach(string bad in new[]{manifest.Replace(",\"capturePrewarm\":true",""),manifest.Replace("\"capturePrewarm\":true","\"capturePrewarm\":false"),manifest.Replace("\"capturePrewarm\":true","\"capturePrewarm\":\"true\""),"null","{"})check(Reject(()=>ScrollCaptureWarmLease.ValidateComponent(bad)),"old or malformed helper is refused before warm resources");
        string ready=new JavaScriptSerializer().Serialize(new{schemaVersion=1,sessionId=lease.SessionId,warm=true});ScrollCaptureWarmLease.ValidateReady(ready,lease.SessionId);check(true,"warm readiness matches exact session identity");
        foreach(string bad in new[]{ready.Replace(lease.SessionId,Guid.NewGuid().ToString("N")),ready.Replace("true","false"),ready.Replace("true","\"true\""),ready.TrimEnd('}')+",\"extra\":1}","null",new string('x',4097)})check(Reject(()=>ScrollCaptureWarmLease.ValidateReady(bad,lease.SessionId)),"stale, malformed and untyped warm readiness is rejected");
        lease.Dispose();h.Drain();
    }
    static void Files(Action<bool,string> check){
        using(var session=Session()){
            string dir=session.OutputDirectory;
            if(Directory.Exists(dir)||File.Exists(dir))throw new IOException("Refuse existing fixture.");Directory.CreateDirectory(dir);
            try{
                session.PublishRequest();var request=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(session.RequestPath));
                check((string)request["sessionId"]==session.SessionId&&!File.Exists(session.RequestPath+".tmp"),"activation request publishes atomically without exposing partial JSON");
                bool duplicate=false;try{session.PublishRequest();}catch(IOException){duplicate=true;}check(duplicate,"duplicate activation never overwrites an existing final request");
                File.WriteAllText(Path.Combine(dir,"warm.json"),"generated");File.WriteAllText(Path.Combine(dir,"warm.ready.json"),"generated");File.WriteAllText(Path.Combine(dir,"startup-timing.json"),"generated");File.WriteAllText(Path.Combine(dir,"keep.txt"),"unrelated fixture");
                Directory.CreateDirectory(Path.Combine(dir,"host-profile"));File.WriteAllText(Path.Combine(dir,"host-profile","generated.txt"),"generated");
                ExternalScrollCaptureSession.CleanupOwnedDirectory(dir,new[]{"warm.json","warm.ready.json","startup-timing.json","request.json"});
                check(!Directory.Exists(Path.Combine(dir,"host-profile"))&&!File.Exists(session.RequestPath)&&File.ReadAllText(Path.Combine(dir,"keep.txt"))=="unrelated fixture","warm cleanup removes owned metadata/profile and preserves unknown siblings");
                File.Delete(Path.Combine(dir,"keep.txt"));Directory.Delete(dir,false);
            }finally{if(Directory.Exists(dir))ExternalScrollCaptureSession.CleanupOwnedDirectory(dir,new[]{"request.json","request.json.tmp","warm.json","warm.ready.json","startup-timing.json","keep.txt"});}
        }
    }
    static void Timing(Action<bool,string> check){
        double ms=0;var samples=new List<string>();var timing=new ScrollCaptureStartupTiming("fixture123",()=>ms,(id,sample)=>samples.Add(sample));
        ScrollCaptureStartupTiming.Mark(timing,ScrollCaptureStartupStage.Click);ms=30;ScrollCaptureStartupTiming.Mark(timing,ScrollCaptureStartupStage.WarmClaim);ms=60;ScrollCaptureStartupTiming.Mark(timing,ScrollCaptureStartupStage.Prepared);
        check(samples.Count==0,"startup timing performs no log IO on first click, claim or preparation");ms=100;ScrollCaptureStartupTiming.Mark(timing,ScrollCaptureStartupStage.Ready);
        check(samples.Count==1&&samples[0].Contains("click=0.000ms")&&samples[0].Contains("warmclaim=30.000ms")&&samples[0].Contains("prepared=60.000ms")&&samples[0].Contains("ready=100.000ms"),"monotonic click-to-ready stages distinguish actual warm handoff costs");
        check(samples[0].All(c=>c>=32&&c<=126)&&samples[0].Length<4096&&!samples[0].Contains(AppBase),"timing record is bounded metadata with no paths, screenshots or text content");
        var bad=new ScrollCaptureStartupTiming("badclock",()=>double.NaN,(id,sample)=>samples.Add(sample));ScrollCaptureStartupTiming.Mark(bad,ScrollCaptureStartupStage.Failed);check(samples[1].Contains("failed=0.000ms"),"diagnostic invalid clock is harmless and bounded");
    }
}
