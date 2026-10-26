using System;
using System.IO;
using System.Threading;

namespace Mori {
 internal sealed class LibraryRouteChangedException : IOException {
  internal LibraryRouteChangedException():base(LibraryWriteGuard.RetiredMessage){}
 }
 // A session owns its original library, not the route selected when it finishes.
 internal sealed class LibraryWriteGuard {
  internal const string RetiredMessage="图库已切换。此旧窗口已停止保存，请关闭旧窗口并打开当前图库后重试；本次未保存到旧图库。";
  readonly string root;
  readonly Func<string> currentRoot;
  int retired,pending;
  internal LibraryWriteGuard(string root,Func<string> currentRoot,bool pendingActivation=false){
   if(string.IsNullOrWhiteSpace(root))throw new ArgumentException("请指定写入图库。","root");
   this.root=Path.GetFullPath(root);this.currentRoot=currentRoot;pending=pendingActivation?1:0;
  }
  internal bool IsRetired {get{return Thread.VolatileRead(ref retired)!=0;}}
  internal bool IsPending {get{return Thread.VolatileRead(ref pending)!=0;}}
  internal void Retire(){Interlocked.Exchange(ref retired,1);}
  internal void ActivateRoute(){Interlocked.Exchange(ref pending,0);ValidateCurrent();}
  void CheckRoute(){
   if(IsRetired)throw new LibraryRouteChangedException();
   if(IsPending)throw new IOException("新图库尚未完成切换，本次未保存。");
   if(currentRoot!=null&&!LibraryTransfer.SameRoot(root,currentRoot())){
    Interlocked.Exchange(ref retired,1);throw new LibraryRouteChangedException();
   }
  }
  internal void ValidateCurrent(){Run(delegate{});}
  // Monitoring is not a write attempt. A live collector/transfer owns this
  // same lease legitimately, so defer only that contention without reporting
  // that its already completed save failed. All route/read errors still escape.
  internal bool TryValidateCurrent(){
   if(IsRetired)throw new LibraryRouteChangedException();
   using(var lease=LibraryTransfer.TryAcquireWrite(root)){
    if(lease==null)return false;
    CheckRoute();return true;
   }
  }
  // The handoff child must finish legacy schema setup before signalling ready;
  // the parent commits the default route only afterwards. This is NOT a public
  // write path: content collection remains refused while activation is pending.
  internal void RunInitialization(Action initialize){
   if(initialize==null)throw new ArgumentNullException("initialize");
   if(!IsPending){Run(initialize);return;}
   if(IsRetired)throw new LibraryRouteChangedException();
   using(var lease=LibraryTransfer.TryAcquireWrite(root)){
    if(lease==null)throw new IOException("图库正在接收收藏或迁移，初始化尚未完成。");
    if(IsRetired)throw new LibraryRouteChangedException();
    if(!IsPending)CheckRoute();initialize();
   }
  }
  internal void Run(Action write){
   if(write==null)throw new ArgumentNullException("write");
   Run<object>(delegate{write();return null;});
  }
  internal T Run<T>(Func<T> write){
   if(write==null)throw new ArgumentNullException("write");
   if(IsRetired)throw new LibraryRouteChangedException();
   // Mutex ownership is thread-affine. Never hold this lease across await.
   using(var lease=LibraryTransfer.TryAcquireWrite(root)){
    if(lease==null)throw new IOException("图库正在接收收藏或迁移，本次尚未保存，请稍后重试。");
    CheckRoute();return write();
   }
  }
 }
}
