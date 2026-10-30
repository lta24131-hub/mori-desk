using System;
using System.Threading;
using System.Windows.Threading;

namespace MoriDesk {
 internal static class CanvasPageTestSupport {
  // Offscreen fixtures explicitly opt into visibility and pump the real async
  // read. Production never uses a synchronous disk-read shortcut for tests.
  internal static void Wait(CanvasProjectsPage page){
   var visible=page.RefreshVisibleForTests;
   var context=SynchronizationContext.Current;
   SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
   try{
    page.RefreshVisibleForTests=()=>true;page.Refresh();
    DateTime deadline=DateTime.UtcNow.AddSeconds(30);
    do{
     page.FlushRefreshForTests();
     if(DateTime.UtcNow>deadline)throw new TimeoutException("Project list did not settle.");
     var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);Thread.Sleep(1);
    }while(page.RefreshPendingForTests||!page.RefreshTaskForTests.IsCompleted);
    page.RefreshTaskForTests.GetAwaiter().GetResult();
   }finally{page.RefreshVisibleForTests=visible;SynchronizationContext.SetSynchronizationContext(context);}
  }
 }
}
