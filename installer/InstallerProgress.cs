using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace MoriDeskInstaller {
 // Presentation only. The transaction, its lease and Shell COM objects remain
 // on the original STA thread. No UI event can cancel or re-enter installation.
 internal sealed class InstallerProgressSnapshot {
  internal readonly string Stage;
  internal readonly long ElapsedMilliseconds;
  internal readonly bool Finished;
  internal InstallerProgressSnapshot(string stage,long elapsed,bool finished){Stage=stage;ElapsedMilliseconds=elapsed;Finished=finished;}
 }
 internal sealed class InstallerProgressState {
  readonly object gate=new object();readonly Func<long> elapsed;
  string stage="正在检查安装环境…";bool finished;long lastElapsed;
  internal InstallerProgressState():this(Stopwatch.StartNew()){}
  InstallerProgressState(Stopwatch clock):this(()=>clock.ElapsedMilliseconds){}
  internal InstallerProgressState(Func<long> elapsed){if(elapsed==null)throw new ArgumentNullException("elapsed");this.elapsed=elapsed;}
  long ReadElapsed(){try{long value=elapsed();if(value>lastElapsed)lastElapsed=value;}catch(Exception){}return lastElapsed;}
  internal void Report(string value){if(string.IsNullOrWhiteSpace(value))return;lock(gate){if(!finished)stage=value;}}
  internal InstallerProgressSnapshot Snapshot(){lock(gate){return new InstallerProgressSnapshot(stage,finished?lastElapsed:ReadElapsed(),finished);}}
  internal void Complete(){lock(gate){if(finished)return;ReadElapsed();finished=true;}}
 }
 internal static class InstallerProgress {
  [ThreadStatic] static InstallerProgressState current;
  internal static void Report(string stage){try{var state=current;if(state!=null)state.Report(stage);}catch(Exception){}}
  internal static int Run(Func<int> transaction){return Run(transaction,StartWindow);}
  // The callback includes acquisition AND disposal of the install lease.
  // The seam exercises ordering without creating windows or touching an install.
  internal static int Run(Func<int> transaction,Func<InstallerProgressState,Action> show){
   if(transaction==null||show==null)throw new ArgumentNullException();
   var state=new InstallerProgressState();var previous=current;Action close=null;current=state;
   try{
    // A presentation failure is never an installation failure or permission to
    // skip the transaction's rollback/finally path.
    try{close=show(state);}catch(Exception){}
    return transaction();
   }finally{
    current=previous;state.Complete();
    try{if(close!=null)close();}catch(Exception){}
   }
  }
  static Action StartWindow(InstallerProgressState state){var window=new ProgressWindowHost(state);window.Start();return window.Stop;}
  sealed class ProgressWindowHost {
   readonly InstallerProgressState state;readonly ManualResetEvent shown=new ManualResetEvent(false);Thread thread;
   internal ProgressWindowHost(InstallerProgressState state){this.state=state;}
   internal void Start(){
    thread=new Thread(delegate(){
     try{
      Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException,true);
      using(var form=new ProgressForm(state,()=>shown.Set()))Application.Run(form);
     }catch(Exception error){try{Trace.WriteLine("Nook installer progress unavailable: "+error.GetType().Name);}catch(Exception){}}
     finally{shown.Set();}
    }){IsBackground=true,Name="Nook installer progress"};
    thread.SetApartmentState(ApartmentState.STA);thread.Start();
    // This bounded initial wait happens BEFORE the installation lease. Never
    // wait for user input or synchronously invoke the UI from the transaction.
    shown.WaitOne(5000);
   }
   internal void Stop(){
    // Complete is published only after transaction unwinding/lease disposal.
    // The UI timer closes its own form; no cross-thread control access occurs.
    if(thread!=null&&!thread.IsAlive){shown.Dispose();return;}
    if(thread!=null&&thread.Join(3000))shown.Dispose();
    // A broken presentation thread must not hold up results or keep the
    // installer alive. Never Abort/kill it or dispose an event it still uses.
   }
  }
  sealed class ProgressForm : Form {
   readonly InstallerProgressState state;readonly Label phase,elapsed;
   readonly System.Windows.Forms.Timer timer;readonly Action visible;
   bool signalled;
   internal ProgressForm(InstallerProgressState state,Action visible){
    this.state=state;this.visible=visible;
    Text="Nook · 正在安装更新";ClientSize=new Size(460,180);FormBorderStyle=FormBorderStyle.FixedSingle;
    MaximizeBox=false;MinimizeBox=true;ShowInTaskbar=true;StartPosition=FormStartPosition.CenterScreen;
    AutoScaleMode=AutoScaleMode.Dpi;Font=new Font("Microsoft YaHei UI",9F);BackColor=SystemColors.Window;
    phase=new Label{AutoSize=false,Location=new Point(24,22),Size=new Size(412,30),Text=state.Snapshot().Stage};
    var bar=new ProgressBar{Location=new Point(24,62),Size=new Size(412,12),Style=ProgressBarStyle.Marquee,MarqueeAnimationSpeed=35};
    elapsed=new Label{AutoSize=false,Location=new Point(24,87),Size=new Size(412,24),ForeColor=SystemColors.GrayText};
    var hint=new Label{AutoSize=false,Location=new Point(24,119),Size=new Size(412,42),Text="素材与设置会保留。安装期间可最小化，请勿重复运行安装包。",ForeColor=SystemColors.GrayText};
    Controls.AddRange(new Control[]{phase,bar,elapsed,hint});
    timer=new System.Windows.Forms.Timer{Interval=200};timer.Tick+=(sender,args)=>RefreshProgress();
    Shown+=(sender,args)=>{timer.Start();RefreshProgress();};
    FormClosing+=(sender,args)=>{if(args.CloseReason==CloseReason.UserClosing&&!state.Snapshot().Finished)args.Cancel=true;};
   }
   protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);if(!signalled){signalled=true;visible();}}
   void RefreshProgress(){
    var snapshot=state.Snapshot();if(snapshot.Finished){timer.Stop();Close();return;}
    phase.Text=snapshot.Stage;long seconds=snapshot.ElapsedMilliseconds/1000;
    elapsed.Text="已用时 "+(seconds/60).ToString()+" 分 "+(seconds%60).ToString("00")+" 秒";
   }
   protected override void Dispose(bool disposing){if(disposing){timer.Dispose();Font.Dispose();}base.Dispose(disposing);}
  }
 }
}
