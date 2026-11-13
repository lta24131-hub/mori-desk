using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace MoriDeskInstaller {
 internal sealed class WizardLine {internal long Sequence;internal string Text;public override string ToString(){return Text;}}
 internal sealed class WizardView {
  internal string Stage="正在检查安装环境…",Version,Result,Title;
  internal InstallerOperation Operation;internal long Elapsed,Sequence;internal int Percent,AttentionSequence;
  internal bool Finished,Waiting,Failed,Warning,CloseRequested;internal WizardLine[] Lines;
  internal WizardView Copy(){return (WizardView)MemberwiseClone();}
 }
 internal sealed class WizardState {
  readonly object gate=new object();readonly Stopwatch clock=new Stopwatch();readonly WizardView value=new WizardView();readonly Queue<WizardLine> lines=new Queue<WizardLine>();
  internal const int MaximumLines=800;
  void Add(string text){if(string.IsNullOrWhiteSpace(text))return;lines.Enqueue(new WizardLine{Sequence=++value.Sequence,Text=text.Length>600?text.Substring(0,600)+"…":text});while(lines.Count>MaximumLines)lines.Dequeue();}
  internal void Log(string text){lock(gate){if(!value.Finished)Add(text);}}
  internal void Report(string text){lock(gate){if(value.Finished)return;if(value.Stage!=text)Add(text);value.Stage=text;}}
  internal void Progress(int percent){lock(gate){if(!value.Finished)value.Percent=Math.Max(value.Percent,Math.Min(99,Math.Max(0,percent)));}}
  internal void Result(string title,string text,bool error=false,bool warning=false){lock(gate){if(value.Finished)return;value.Title=title;value.Result=text;value.Failed=error;value.Warning=warning;Add(text);}}
  internal void SetOperation(InstallerOperation operation,string version){lock(gate){if(!value.Finished){value.Operation=operation;value.Version=version;}}}
  internal void RequestAttention(){lock(gate){value.AttentionSequence++;}}
  internal void Wait(bool waiting){lock(gate){value.Waiting=waiting;value.CloseRequested=false;if(waiting)clock.Stop();else if(!value.Finished)clock.Start();}}
  internal bool Close(){lock(gate){if(value.Finished||value.Waiting)return true;value.CloseRequested=true;return false;}}
  internal void Complete(){lock(gate){if(value.Finished)return;clock.Stop();value.Elapsed=clock.ElapsedMilliseconds;value.Finished=true;value.Waiting=false;if(!value.Failed)value.Percent=100;}}
  internal WizardView Snapshot(){lock(gate){var copy=value.Copy();copy.Elapsed=value.Finished?value.Elapsed:clock.ElapsedMilliseconds;copy.Lines=lines.ToArray();return copy;}}
 }
 internal static class InstallerWizard {
  // The owning STA executes transactions. The UI has no filesystem/install
  // callbacks: decisions are immutable plans; waits are outside every lease.
  internal static int Run(InstallerPlan plan,bool automatic,Func<InstallerPlan,string> validate,Func<InstallerPlan,int> attempt,Action afterSuccess){
   var state=new WizardState();var previous=InstallerProgress.Wizard;InstallerProgress.Wizard=state;
   using(var host=new Host(state,plan,automatic,validate)){
    try{
     host.Start();return Execute(state,host.Decision,()=>attempt(host.Selection),afterSuccess,host.WaitClosed);
    }finally{InstallerProgress.Wizard=previous;state.Complete();}
   }
  }
  internal static int Execute(WizardState state,Func<bool> decision,Func<int> attempt,Action afterSuccess,Action waitClosed){
     if(!decision())return 2;int code;
     while(true){state.Wait(false);try{code=attempt();}catch(Exception error){state.Result("安装未完成",error.Message,true);code=1;}
      state.RequestAttention();if(code!=Program.ManualNeedsExit)break;
      state.Report("请先保存内容并退出 Nook");state.Log("通过菜单或托盘选择“退出 Nook”，然后点“重试”。不会强制结束程序。");state.Wait(true);
      if(!decision())return 2;
     }
     if(code==0&&afterSuccess!=null)try{afterSuccess();}catch(Exception error){state.Result("安装完成，清理暂缓","新版已安装；旧文件暂时保留。"+error.Message,false,true);}
     if(string.IsNullOrEmpty(state.Snapshot().Result))state.Result(code==0?"安装完成":"安装未完成",code==0?"Nook 已就绪，素材和设置已保留。":"未完成本次操作，原资料已保留。",code!=0);
     state.Complete();waitClosed();return code;
  }
  sealed class Host:IDisposable {
   readonly WizardState state;readonly InstallerPlan plan;readonly bool automatic;readonly Func<InstallerPlan,string> validate;
   readonly ManualResetEvent shown=new ManualResetEvent(false),closed=new ManualResetEvent(false);readonly AutoResetEvent decision=new AutoResetEvent(false);
   Thread thread;internal InstallerPlan Selection;
   internal Host(WizardState state,InstallerPlan plan,bool automatic,Func<InstallerPlan,string> validate){this.state=state;this.plan=plan;this.automatic=automatic;this.validate=validate;}
   internal void Start(){thread=new Thread(()=>{try{Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException,true);using(var form=new Window(state,plan,automatic,validate,p=>{Selection=p;decision.Set();},()=>shown.Set()))Application.Run(form);}catch(Exception error){Trace.WriteLine("Installer window: "+error.GetType().Name);}finally{shown.Set();closed.Set();}}){IsBackground=true,Name="Nook installation wizard"};thread.SetApartmentState(ApartmentState.STA);thread.Start();shown.WaitOne(5000);}
   internal bool Decision(){return WaitHandle.WaitAny(new WaitHandle[]{decision,closed})==0;}
   internal void WaitClosed(){closed.WaitOne();}
   public void Dispose(){if(thread!=null&&thread.Join(1000)){shown.Dispose();closed.Dispose();decision.Dispose();}}
  }
  internal sealed class Window:Form {
   readonly WizardState state;readonly InstallerPlan plan;readonly Func<InstallerPlan,string> validate;readonly Action<InstallerPlan> start;
   readonly Label title,subtitle,phase,footnote,summary,error;readonly Panel page;readonly TextBox location;readonly Button browse,back,next,cancel;
   readonly ProgressBar progress;readonly ListBox log;readonly CheckBox follow;readonly CheckBox[] choices;readonly System.Windows.Forms.Timer timer;
   int step,lastAttention;bool running,finished;long lastLog=-1;readonly Font heading;
   static readonly Color Purple=Color.FromArgb(103,75,191),Muted=Color.FromArgb(102,105,112);
   internal Window(WizardState state,InstallerPlan source,bool automatic,Func<InstallerPlan,string> validate,Action<InstallerPlan> start,Action visible){
    this.state=state;plan=source.Copy();this.validate=validate;this.start=start;
    Text="Nook 安装";ClientSize=new Size(660,510);FormBorderStyle=FormBorderStyle.FixedSingle;MaximizeBox=false;StartPosition=FormStartPosition.CenterScreen;
    AutoScaleMode=AutoScaleMode.Dpi;Font=new Font("Microsoft YaHei UI",9F);heading=new Font(Font.FontFamily,17F,FontStyle.Bold);BackColor=Color.White;
    try{Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);}catch{}
    title=new Label{Location=new Point(28,24),Size=new Size(604,35),Font=heading};subtitle=new Label{Location=new Point(30,68),Size=new Size(600,42),ForeColor=Muted};
    page=new Panel{Location=new Point(30,120),Size=new Size(600,306)};
    location=new TextBox{Location=new Point(0,42),Size=new Size(492,28),ReadOnly=plan.LocationLocked,Text=plan.Location,AccessibleName="安装位置"};
    browse=Button("浏览…",504,40,96);browse.Enabled=!plan.LocationLocked;browse.Click+=(s,e)=>{using(var picker=new FolderBrowserDialog{Description="选择程序文件夹的上级目录",SelectedPath=System.IO.Path.GetDirectoryName(location.Text)})if(picker.ShowDialog(this)==DialogResult.OK)location.Text=System.IO.Path.Combine(picker.SelectedPath,"Nook");};
    summary=new Label{Location=new Point(0,0),Size=new Size(600,300)};
    choices=new CheckBox[plan.Components.Length];for(int i=0;i<choices.Length;i++){var c=plan.Components[i];choices[i]=new CheckBox{Location=new Point(0,10+i*72),Size=new Size(600,30),Text=c.Name+"   "+InstallerPlan.Size(c.Bytes),Checked=c.Selected,Enabled=c.Bytes>0};}
    phase=new Label{Location=new Point(0,0),Size=new Size(600,24)};
    progress=new ProgressBar{Location=new Point(0,33),Size=new Size(600,15),Minimum=0,Maximum=100};
    log=new ListBox{Location=new Point(0,63),Size=new Size(600,209),HorizontalScrollbar=true,IntegralHeight=false,BorderStyle=BorderStyle.FixedSingle,AccessibleName="安装与清理记录"};
    follow=new CheckBox{Location=new Point(0,280),Size=new Size(260,24),Text="自动滚动到最新记录",Checked=true};
    error=new Label{Location=new Point(30,428),Size=new Size(600,27),ForeColor=Color.Firebrick};
    var footer=new Panel{Location=new Point(0,464),Size=new Size(660,46),BackColor=Color.FromArgb(246,246,248)};
    back=Button("上一步",338,7,90);next=Button("下一步",438,7,90);cancel=Button("取消",538,7,90);next.BackColor=Purple;next.ForeColor=Color.White;
    footnote=new Label{Location=new Point(28,12),Size=new Size(298,28),ForeColor=Muted,Text="Nook "+plan.Version};footer.Controls.AddRange(new Control[]{footnote,back,next,cancel});
    Controls.AddRange(new Control[]{title,subtitle,page,error,footer});back.Click+=(s,e)=>{if(!running){step--;RenderPage();}};next.Click+=(s,e)=>Next();cancel.Click+=(s,e)=>Close();AcceptButton=next;CancelButton=cancel;
    FormClosing+=(s,e)=>{if(running&&!state.Close()){e.Cancel=true;error.Text="安装尚未完成，可以先最小化。";}};
    timer=new System.Windows.Forms.Timer{Interval=150};timer.Tick+=(s,e)=>RefreshProgress();Shown+=(s,e)=>{visible();if(automatic)Begin();timer.Start();};RenderPage();
   }
   static Button Button(string text,int x,int y,int width){return new Button{Text=text,Location=new Point(x,y),Size=new Size(width,31),FlatStyle=FlatStyle.Flat};}
   void RenderPage(){
    page.Controls.Clear();error.Text="";back.Enabled=step>0;next.Text=step==2?"开始安装":"下一步";
    if(step==0){title.Text="安装位置";subtitle.Text=plan.LocationLocked?"检测到已有 Nook，将在原位置更新。素材保存位置不变。":"选择 Nook 的程序位置。素材保存位置可在软件设置中单独修改。";
     summary.Text="程序文件夹";summary.Size=new Size(600,28);page.Controls.AddRange(new Control[]{summary,location,browse,new Label{Location=new Point(0,89),Size=new Size(600,85),ForeColor=Muted,Text="程序与素材分开保存。\r\n更新会沿用此位置，并在新版检查通过后清理旧程序。\r\n已有安装不在这里搬迁，避免留下两套启动入口。"}});
    }else if(step==1){title.Text="可选组件";subtitle.Text="按需要选择；以后可以再次运行安装器调整。更新会保留上次选择。";
     for(int i=0;i<choices.Length;i++){page.Controls.Add(choices[i]);page.Controls.Add(new Label{Location=new Point(25,41+i*72),Size=new Size(575,35),Text=plan.Components[i].Description,ForeColor=Muted});}
     page.Controls.Add(new Label{Location=new Point(0,234),Size=new Size(600,70),ForeColor=Muted,Text="图库、看图、画布、基础截图与浏览器连接随主程序提供。\r\nChrome 插件首次安装仍需在浏览器确认。"});
    }else{title.Text="准备安装";subtitle.Text="确认后，安装、替换和清理会在这个窗口内完成。";summary.Size=new Size(600,300);
     summary.Text="安装位置\r\n"+plan.Location+"\r\n\r\n安装内容\r\n主程序"+string.Concat(plan.Components.Where(c=>c.Selected).Select(c=>"、"+c.Name))+"\r\n\r\n预计程序占用  "+InstallerPlan.Size(plan.SelectedBytes)+"\r\n\r\n素材、分类、画布、贴图、保存位置和设置均保留。\r\n旧程序将在新版启动和连接检查通过后清理。";page.Controls.Add(summary);
    }
   }
   void Next(){
    if(finished){Close();return;}if(running){if(state.Snapshot().Waiting){state.Wait(false);next.Enabled=false;cancel.Enabled=false;start(plan.Copy());}return;}
    try{if(step==0){var candidate=plan.Copy();candidate.Location=location.Text;plan.Location=location.Text=validate(candidate);}if(step==1)for(int i=0;i<choices.Length;i++)plan.Components[i].Selected=choices[i].Checked;
     if(step<2){step++;RenderPage();}else{validate(plan);Begin();}
    }catch(Exception exception){error.Text=exception.Message;}
   }
   void Begin(){running=true;page.Controls.Clear();page.Controls.AddRange(new Control[]{phase,progress,log,follow});back.Enabled=false;next.Enabled=false;cancel.Enabled=false;next.Text="正在安装";title.Text="正在安装 Nook";subtitle.Text="素材、画布、贴图和设置会保留。安装中可以最小化此窗口。";error.Text="";start(plan.Copy());}
   void RefreshProgress(){
    if(!running)return;var view=state.Snapshot();phase.Text=view.Stage;progress.Style=!view.Finished&&!view.Waiting&&view.Percent==0?ProgressBarStyle.Marquee:ProgressBarStyle.Continuous;progress.Value=view.Percent;long seconds=view.Elapsed/1000;footnote.Text="Nook "+plan.Version+"  ·  "+seconds/60+" 分 "+(seconds%60).ToString("00")+" 秒";
    // Startup verification can activate the new app. Reveal the remaining
    // cleanup once, while respecting a user who minimized this installer.
    if(lastAttention!=view.AttentionSequence){lastAttention=view.AttentionSequence;if(WindowState!=FormWindowState.Minimized){BringToFront();Activate();}}
    if(lastLog!=view.Sequence){int top=log.TopIndex;log.BeginUpdate();try{if(log.Items.Count>0&&view.Lines.Length>0&&((WizardLine)log.Items[0]).Sequence<view.Lines[0].Sequence){log.Items.Clear();lastLog=-1;}foreach(var line in view.Lines)if(line.Sequence>lastLog)log.Items.Add(line);lastLog=view.Sequence;if(log.Items.Count>0)log.TopIndex=follow.Checked?log.Items.Count-1:Math.Min(top,log.Items.Count-1);}finally{log.EndUpdate();}}
    if(view.Finished){finished=true;title.Text=view.Title??"安装完成";subtitle.Text=view.Result;phase.Text=view.Failed?"未完成":view.Warning?"安装已完成，部分清理暂缓":"已完成";next.Text="完成";next.Enabled=true;cancel.Visible=false;error.Text="";return;}
    if(view.Waiting){title.Text="等待退出 Nook";subtitle.Text="请保存编辑内容，并从菜单或托盘完全退出 Nook，然后重试。";next.Text="重试";next.Enabled=true;cancel.Enabled=true;}
    else{title.Text="正在"+InstallerProgress.OperationText(view.Operation)+" Nook";next.Text="处理中";next.Enabled=false;cancel.Enabled=false;}
    if(view.CloseRequested)error.Text="安装尚未完成，可以先最小化。";
   }
   protected override void Dispose(bool disposing){if(disposing){timer.Dispose();heading.Dispose();Font.Dispose();}base.Dispose(disposing);}
  }
 }
}
