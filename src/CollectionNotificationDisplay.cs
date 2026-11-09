using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace MoriDesk {
 // Keep each operation on its original monitor, including after the user
 // switches apps. Different monitors own separate non-activating stacks.
 internal sealed class CollectionNotificationDisplay {
  readonly Dictionary<string,CollectionNotificationWindow> windows=new Dictionary<string,CollectionNotificationWindow>();
  readonly Dictionary<string,string> routes=new Dictionary<string,string>();
  readonly Queue<string> history=new Queue<string>();
  readonly Func<string> select;
  readonly Func<string,CollectionNotificationWindow> create;
  internal readonly Dispatcher Dispatcher=Dispatcher.CurrentDispatcher;
  [DllImport("user32.dll")]static extern IntPtr GetForegroundWindow();
  internal CollectionNotificationDisplay(Func<string> select=null,Func<string,CollectionNotificationWindow> create=null){
   this.select=select??(()=>{var foreground=GetForegroundWindow();return (foreground!=IntPtr.Zero?Forms.Screen.FromHandle(foreground):Forms.Screen.FromPoint(Forms.Cursor.Position)).DeviceName;});
   this.create=create??(device=>new CollectionNotificationWindow(()=>{
    var screen=Forms.Screen.AllScreens.FirstOrDefault(s=>s.DeviceName==device)??Forms.Screen.PrimaryScreen;
    var area=screen.WorkingArea;return new Rect(area.Left,area.Top,area.Width,area.Height);
   }));
  }
  internal int NoticeCount {get{return windows.Values.Sum(window=>window.NoticeCount);}}
  internal void Update(Dictionary<string,object> raw){
   var value=BrowserDesktopFeedback.Clean(raw);string id=(string)value["id"],device;
   if(!routes.TryGetValue(id,out device)){
    if((string)value["state"]=="dismiss")return;
    device=select();routes.Add(id,device);history.Enqueue(id);
    while(history.Count>512)routes.Remove(history.Dequeue());
   }
   CollectionNotificationWindow window;
   if(!windows.TryGetValue(device,out window)){window=create(device);windows.Add(device,window);}
   window.Update(value);
  }
  internal void Close(){foreach(var window in windows.Values)window.Close();windows.Clear();routes.Clear();history.Clear();}
 }
}
