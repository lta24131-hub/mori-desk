using System;
using System.Net.Sockets;

namespace MoriDesk {
 // A replacement window can become ready while the previous one still owns
 // the loopback port. Keep that handoff retryable without hiding other errors.
 internal sealed class BrowserBridgeHost : IDisposable {
  readonly Func<IDisposable> open;
  IDisposable connection;
  bool disposed;
  internal bool Ready {get{return connection!=null;}}
  internal BrowserBridgeHost(Func<IDisposable> factory){open=factory;}
  internal bool TryStart(){
   if(disposed)return false;
   if(connection!=null)return true;
   try{connection=open();if(connection==null)throw new InvalidOperationException("浏览器连接未能启动。");return true;}
   catch(SocketException ex){if(ex.SocketErrorCode!=SocketError.AddressAlreadyInUse)throw;return false;}
  }
  public void Dispose(){if(disposed)return;disposed=true;if(connection!=null){connection.Dispose();connection=null;}}
 }
}
