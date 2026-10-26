using System;

namespace Mori {
 public partial class MainWindow {
  readonly LibraryWriteGuard libraryWrites;
  void WriteToLibrary(Action write){libraryWrites.Run(write);}
  T WriteToLibrary<T>(Func<T> write){return libraryWrites.Run(write);}
  internal void CompleteLibraryRoute(){libraryWrites.ActivateRoute();}
  bool PrepareLibraryRouteChange(){
   // A stale window must not copy its old library somewhere else and make that
   // copy current again. This is an admission check only: the existing transfer
   // coordinator still owns its synchronous leases and activation handshake.
   try{libraryWrites.ValidateCurrent();return true;}
   catch(LibraryRouteChangedException){Status(LibraryWriteGuard.RetiredMessage);return false;}
   catch(Exception error){Status("保存位置暂无法核对："+error.Message);return false;}
  }
  void CheckLibraryRoute(){
   if(libraryWrites.IsPending)return;
   try{libraryWrites.TryValidateCurrent();}
   catch(LibraryRouteChangedException){
    Status(LibraryWriteGuard.RetiredMessage);
    if(Window.Title.IndexOf("旧图库",StringComparison.Ordinal)<0)Window.Title+=" · 旧图库（已停止保存）";
   }
   // Only busy monitoring is deferred; real route/read failures stay visible.
   // A successful probe does not overwrite a newer user-operation message.
   catch(Exception error){Status("保存位置暂无法核对："+error.Message);}
  }
 }
}
