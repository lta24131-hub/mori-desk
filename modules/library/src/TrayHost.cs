using System;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Threading;
using Forms=System.Windows.Forms;

namespace Mori {
 internal interface ITrayHost:IDisposable {void NotifyHidden();}
 internal sealed class TrayHost:ITrayHost {
  readonly Forms.NotifyIcon icon;readonly Forms.ContextMenuStrip menu;readonly Icon image;
  readonly EventWaitHandle activation;readonly RegisteredWaitHandle wait;bool disposed,notified;
  public TrayHost(string eventName,Dispatcher dispatcher,Action show,Action settings,Action exit){
   Action<Action> post=action=>{if(!disposed&&!dispatcher.HasShutdownStarted)dispatcher.BeginInvoke(new Action(delegate{if(!disposed)action();}));};
   try {
    image=Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
    menu=new Forms.ContextMenuStrip();menu.Items.Add("打开 Mori Desk",null,delegate{post(show);});menu.Items.Add("截图 / 原位翻译",null,delegate{post(()=>QingJie.AppState.BeginCapture());});menu.Items.Add("输入翻译",null,delegate{post(QingJie.AppState.ShowTranslator);});menu.Items.Add("粘贴 / 找回贴图",null,delegate{post(()=>QingJie.AppState.Paste());});menu.Items.Add("打开图片…",null,delegate{post(()=>MoriDesk.FileRouter.Choose(null,false));});menu.Items.Add("打开 3D 模型…",null,delegate{post(()=>MoriDesk.FileRouter.Choose(null,true));});menu.Items.Add(new Forms.ToolStripSeparator());menu.Items.Add("设置…",null,delegate{post(settings);});menu.Items.Add("退出 Mori Desk",null,delegate{post(exit);});
    icon=new Forms.NotifyIcon{Icon=image,Text="Mori Desk · 素材与工具",ContextMenuStrip=menu};icon.MouseClick+=delegate(object sender,Forms.MouseEventArgs e){if(e.Button==Forms.MouseButtons.Left)post(show);};icon.BalloonTipClicked+=delegate{post(show);};icon.Visible=true;
    activation=new EventWaitHandle(false,EventResetMode.AutoReset,eventName);
    wait=ThreadPool.RegisterWaitForSingleObject(activation,delegate{post(show);},null,Timeout.Infinite,false);
   } catch {Dispose();throw;}
  }
  public void NotifyHidden(){if(disposed||notified)return;notified=true;icon.ShowBalloonTip(2500,"Mori Desk 仍在后台运行","点击托盘图标重新打开，右键选择“退出 Mori Desk”可彻底退出。",Forms.ToolTipIcon.Info);}
  public static bool ActivateExisting(string eventName){try{using(var handle=EventWaitHandle.OpenExisting(eventName))handle.Set();return true;}catch(WaitHandleCannotBeOpenedException){return false;}catch(UnauthorizedAccessException){return false;}}
  public void Dispose(){if(disposed)return;disposed=true;if(wait!=null)wait.Unregister(null);if(activation!=null)activation.Dispose();if(icon!=null){icon.Visible=false;icon.Dispose();}if(menu!=null)menu.Dispose();if(image!=null)image.Dispose();}
 }
}
