using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Mori;

namespace MoriDesk {
 internal static class PdfIsolationTests {
  static bool Gone(int id){try{using(var p=Process.GetProcessById(id))return p.HasExited;}catch(ArgumentException){return true;}}
  static bool NativeLoaded(){using(var p=Process.GetCurrentProcess())return p.Modules.Cast<ProcessModule>().Any(m=>string.Equals(m.ModuleName,"Windows.Data.Pdf.dll",StringComparison.OrdinalIgnoreCase));}
  static bool Unlocked(string file){try{using(var input=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None))return true;}catch(IOException){return false;}}
  static bool ReadWorker(string receipt,out int worker){worker=0;try{return int.TryParse(File.ReadAllText(receipt),out worker)&&worker>0;}catch(IOException){return false;}}
  internal static void Orphan(string root,string runner,Action<bool,string> check){
   Directory.CreateDirectory(root);string file=Path.Combine(root,"parent-exit.pdf"),receipt=Path.Combine(root,"child.txt");File.WriteAllBytes(file,PdfPreviewTests.Fixture(1));int worker=0;
   using(var parent=Process.Start(new ProcessStartInfo(runner,"orphan \""+file+"\" \""+receipt+"\""){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden})){
    try{
     bool ready=SpinWait.SpinUntil(()=>ReadWorker(receipt,out worker)||parent.HasExited,25000);if(!ready||worker<=0)throw new TimeoutException("Disposable parent did not publish its PDF worker receipt.");
     check(!Gone(worker),"crash-recovery fixture has a live worker before its parent exits");parent.Kill();parent.WaitForExit(3000);
     check(SpinWait.SpinUntil(()=>Gone(worker)&&Unlocked(file),5000),"abrupt parent exit terminates its renderer and releases the source through the Windows job");
    }finally{
     if(!parent.HasExited){parent.Kill();parent.WaitForExit(2000);}
     if(worker>0&&!Gone(worker))using(var orphan=Process.GetProcessById(worker)){orphan.Kill();orphan.WaitForExit(2000);}
    }
   }
  }
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);string file=Path.Combine(root,"连续 中文 ' (1).pdf");byte[] original=PdfPreviewTests.Fixture(8);File.WriteAllBytes(file,original);DateTime stamp=File.GetLastWriteTimeUtc(file);
   check(!NativeLoaded(),"PDF tests begin without Windows PDF renderer loaded in the UI process");
   var session=PdfPreview.OpenAsync(file,CancellationToken.None).GetAwaiter().GetResult();int worker=session.WorkerId;
   try{
    check(worker>0&&worker!=Process.GetCurrentProcess().Id,"native PDF metadata loads in an independent hidden worker");
    var first=session.RenderAsync(0,512,CancellationToken.None).GetAwaiter().GetResult();var last=session.RenderAsync(7,1024,CancellationToken.None).GetAwaiter().GetResult();
    check(session.WorkerId==worker&&first.PageIndex==0&&last.PageIndex==7,"nonsequential pages and zoom reuse one renderer process");
    check(first.Bitmap.PixelWidth==512&&last.Bitmap.PixelWidth==1024&&first.Bitmap.IsFrozen&&last.Bitmap.IsFrozen,"bounded page pixels remain frozen and reflect requested zoom resolution");
    using(var canceled=new CancellationTokenSource()){canceled.Cancel();bool stopped=false;try{session.RenderAsync(1,512,canceled.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){stopped=true;}check(stopped&&session.WorkerId==worker&&!Gone(worker),"pre-cancelled page request preserves the reusable renderer");}
    check(!NativeLoaded(),"rendering PDF pages never loads native PDF component into the UI process");
    using(var child=Process.GetProcessById(worker)){child.Kill();child.WaitForExit(2000);}bool failure=false;try{session.RenderAsync(2,64,CancellationToken.None).GetAwaiter().GetResult();}catch(IOException){failure=true;}catch(ObjectDisposedException){failure=true;}
    check(failure,"unexpected worker exit returns an error instead of hanging the UI");
    var retry=session.RenderAsync(3,128,CancellationToken.None).GetAwaiter().GetResult();worker=session.WorkerId;check(retry.PageIndex==3&&worker>0,"next page request can reopen a failed worker without retaining stale pipe state");
   }finally{session.CloseAsync().GetAwaiter().GetResult();}
   check(Gone(worker)&&Unlocked(file),"closing the session exits its worker and releases the original file");
   // Observe OS exit as well as rendered pixels. A managed return code alone
   // missed the original shutdown failure in the in-process renderer.
   var connection=PdfRenderConnection.Open(file,-1,CancellationToken.None);int process=connection.ProcessId;connection.Render(4,64,CancellationToken.None);connection.Dispose();
   check(connection.ExitCode==0&&!connection.ForcedStop&&Gone(process),"ordinary worker disposal exits normally without a forced kill");
   var active=PdfPreview.OpenAsync(file,CancellationToken.None).GetAwaiter().GetResult();int activeWorker=active.WorkerId;
   using(var cancel=new CancellationTokenSource()){
    var rendering=active.RenderAsync(0,2048,cancel.Token);cancel.CancelAfter(60);bool settled=false;
    try{settled=rendering.GetAwaiter().GetResult().Bitmap.IsFrozen;}catch(OperationCanceledException){settled=true;}
    check(settled,"an active render either safely completes or observes cancellation");
   }
   active.CloseAsync().GetAwaiter().GetResult();check(Gone(activeWorker)&&Unlocked(file),"active cancellation and close leave no PDF source or process behind");
   string blocker=Path.Combine(root,"not-a-profile-directory");File.WriteAllText(blocker,"untouched");string prior=Environment.GetEnvironmentVariable("MORI_DESK_DATA");
   try{Environment.SetEnvironmentVariable("MORI_DESK_DATA",Path.Combine(blocker,"MoriDesk"));var independent=PdfPreview.RenderAsync(file,0,32,CancellationToken.None).GetAwaiter().GetResult();check(independent.Bitmap.PixelWidth==32&&File.ReadAllText(blocker)=="untouched","PDF worker renders without initializing or repairing application configuration");}finally{Environment.SetEnvironmentVariable("MORI_DESK_DATA",prior);}
   check(original.SequenceEqual(File.ReadAllBytes(file))&&stamp==File.GetLastWriteTimeUtc(file)&&Unlocked(file),"all PDF render/recovery paths preserve source bytes, time and exclusive access");
   var queued=Enumerable.Range(0,4).Select(index=>PdfPreview.RenderAsync(file,index,64,CancellationToken.None)).ToArray();Task.WaitAll(queued);
   check(queued.Select((task,index)=>task.Result.PageIndex==index&&task.Result.Bitmap.PixelWidth==64).All(value=>value)&&PdfPreview.Workers.CurrentCount==1&&Unlocked(file),"concurrent thumbnail requests retain individual pages and release the shared worker slot and source");
   check(!NativeLoaded(),"PDF recovery and shutdown still leave native renderer outside UI process");
   byte[] hostile=new byte[33];byte[] header={137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82};Buffer.BlockCopy(header,0,hostile,0,header.Length);hostile[16]=127;hostile[23]=1;bool rejected=false;try{PdfRenderConnection.ValidatePng(hostile,2048);}catch(InvalidDataException){rejected=true;}check(rejected,"oversized encoded PNG dimensions are rejected before any bitmap decoder allocation");
  }
 }
}
