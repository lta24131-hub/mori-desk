using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace Mori {
 // Keep the old setting until the replacement window is actually ready.
 public static class LibraryActivation {
  public static void Open(string root,bool preview) {
   root=Path.GetFullPath(root);if(LibraryTransfer.SameRoot(root,Path.GetPathRoot(root)))throw new IOException("请选择磁盘中的图库文件夹，不要使用磁盘根目录。");root=root.TrimEnd(Path.DirectorySeparatorChar);
   string eventName="Local\\Mori-Handoff-"+Guid.NewGuid().ToString("N");
   using(var ready=new EventWaitHandle(false,EventResetMode.ManualReset,eventName))using(var activate=new EventWaitHandle(false,EventResetMode.ManualReset,eventName+"-commit")) {
    Process child=null;bool committed=false;
    try {
     child=Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,(preview?"--ui-test":"--library")+" \""+root+"\" --handoff \""+eventName+"\""){UseShellExecute=false});
     if(child==null)throw new IOException("无法打开新图库，仍使用原来的保存位置。");
     var clock=Stopwatch.StartNew();bool opened=false;
     while(clock.ElapsedMilliseconds<15000){if(ready.WaitOne(100)){opened=true;break;}if(child.HasExited)break;}
     if(!opened||child.HasExited)throw new IOException("新图库未能完成启动，默认保存位置没有改变。请稍后重试。");
     if(!preview){var settings=Settings.Load();settings.LibraryPath=root;settings.Save();}
     activate.Set();committed=true;
    } finally {
     if(child!=null){if(!committed)try{if(!child.HasExited)child.CloseMainWindow();}catch(InvalidOperationException){}child.Dispose();}
    }
   }
  }
 }
}
