using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using QingJie;

internal static class ShortLineSessionTests {
    static BitmapSource LoadLine(string path,bool cropToInk=true){
        using(var stream=File.OpenRead(path)){
            var image=new WriteableBitmap(BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0]);
            if(!cropToInk){image.Freeze();return image;}
            var box=DesktopLineEvidence.Analyze(image).Bounds;
            var crop=new CroppedBitmap(image,new Int32Rect(box.X-6,box.Y-6,box.Width+12,box.Height+12));
            var snapshot=new WriteableBitmap(crop);snapshot.Freeze();return snapshot;
        }
    }
    static Process IdleWorker(){
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
        var session=typeof(OcrWorker).GetField("idleSession",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
        return Process.GetProcessById((int)session.GetType().GetProperty("ProcessId",flags).GetValue(session,null));
    }
    static string Words(OcrPage result){return string.Join(" ",result.Words.Where(w=>(w.Line&1)==0).Select(w=>w.Text));}
    static bool SameRecords(OcrPage a,OcrPage b){
        if(a.Words.Count!=b.Words.Count)return false;
        for(int i=0;i<a.Words.Count;i++){
            var x=a.Words[i];var y=b.Words[i];
            if(x.Text!=y.Text||x.Line!=y.Line||x.Box!=y.Box||x.Confidence!=y.Confidence||x.ConfidenceSource!=y.ConfidenceSource)return false;
        }
        return true;
    }
    static void ExitBeforeConnection(Action<bool,string> check){
        // The real helper waits on a separate control pipe. Its first invalid
        // request ends it while the production connection wait is pending.
        string controlName="QingJie-OCR-"+Guid.NewGuid().ToString("N");
        var start=new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Nook.LineOcr.exe"),"--pipe="+controlName){
            UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
        using(var control=new NamedPipeServerStream(controlName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous))
        using(var waiting=new NamedPipeServerStream("QingJie-OCR-"+Guid.NewGuid().ToString("N"),PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous))
        using(var guard=new CancellationTokenSource(10000))using(var child=Process.Start(start)){
            try{
                control.WaitForConnectionAsync(guard.Token).GetAwaiter().GetResult();
                var pending=OcrWorker.WaitForShortLineConnection(waiting,child,guard.Token);
                check(!pending.IsCompleted,"the OCR connection is waiting for the launched process");
                control.Write(new byte[4],0,4);control.Flush();
                bool exited=false;
                try{pending.GetAwaiter().GetResult();}catch(EndOfStreamException){exited=true;}
                check(exited&&!guard.IsCancellationRequested&&child.HasExited&&child.ExitCode==2,
                    "a worker exiting before it connects ends the wait without using the timeout");
            }finally{if(!child.HasExited){child.Kill();child.WaitForExit(3000);}}
        }
    }
    internal static void Run(string folder,Action<bool,string> check){
        ExitBeforeConnection(check);
        var file=LoadLine(Path.Combine(folder,"browser-file-edge-real.png"));
        var copy=LoadLine(Path.Combine(folder,"browser-copy-edge-real.png"));
        var ambiguous=LoadLine(Path.Combine(folder,"ai11.png"),false);
        // Each reference opens its own fresh model. The isolated helper must
        // agree on every character, position and score regardless of history.
        var referenceFile=ConsensusLineOcr.ReadSymbols(file);
        var referenceCopy=ConsensusLineOcr.ReadSymbols(copy);
        var referenceAmbiguous=ConsensusLineOcr.ReadSymbols(ambiguous);
        var first=ConsensusLineOcr.ReadIsolated(file,CancellationToken.None);
        using(var worker=IdleWorker()){
            int id=worker.Id;
            var second=ConsensusLineOcr.ReadIsolated(copy,CancellationToken.None);
            using(var reused=IdleWorker()){
                check(reused.Id==id&&Words(first)=="Open this file."&&Words(second)=="Copy link",
                    "two different screenshots reuse one isolated runtime and each returns its own words");
            }
            var adverse=ConsensusLineOcr.ReadIsolated(ambiguous,CancellationToken.None);
            var copyAgain=ConsensusLineOcr.ReadIsolated(copy,CancellationToken.None);
            var fileAgain=ConsensusLineOcr.ReadIsolated(file,CancellationToken.None);
            using(var reused=IdleWorker()){
                check(reused.Id==id&&SameRecords(first,referenceFile)&&SameRecords(second,referenceCopy)&&
                    SameRecords(adverse,referenceAmbiguous)&&SameRecords(copyAgain,referenceCopy)&&SameRecords(fileAgain,referenceFile),
                    "an ambiguous label and reverse-order repeats preserve fresh-model symbol bounds, scores, and punctuation");
            }
            check(worker.WaitForExit(18000),"the isolated runtime exits after the brief idle period");
        }
        ConsensusLineOcr.ReadIsolated(copy,CancellationToken.None);
        using(var worker=IdleWorker())using(var stop=new CancellationTokenSource()){
            bool cancelled=false;
            try{ConsensusLineOcr.ReadIsolated(file,stop.Token,milliseconds=>stop.Cancel());}
            catch(OperationCanceledException){cancelled=stop.IsCancellationRequested;}
            check(cancelled&&worker.WaitForExit(3000),"cancelling a checked-out session promptly releases the worker");
        }
        var recovered=ConsensusLineOcr.ReadIsolated(file,CancellationToken.None);
        check(Words(recovered)=="Open this file.","a new session can recognize another screenshot after cancellation");
    }
}
