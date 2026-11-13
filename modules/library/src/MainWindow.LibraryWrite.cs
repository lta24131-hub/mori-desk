using System;

namespace Mori {
 public partial class MainWindow {
  readonly LibraryWriteGuard libraryWrites;
  void WriteToLibrary(Action write){libraryWrites.Run(write);}
  T WriteToLibrary<T>(Func<T> write){return libraryWrites.Run(write);}
  internal void CompleteLibraryRoute(){libraryWrites.ActivateRoute();}
  internal void RunLibraryRouteChange(Action change){
   // Keep this whole synchronous operation on one worker thread. The source
   // lease prevents another settings writer switching away after validation;
   // Transfer and RememberLibrary may recursively acquire it on this thread.
   // Never hold it across await or while showing a UI picker.
   libraryWrites.Run(change);
  }
  bool PrepareLibraryRouteChange(){
   // A stale window must not copy its old library somewhere else and make that
   // copy current again. This is an admission check only: the worker repeats
   // it under the lease kept through copying and the activation handshake.
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
