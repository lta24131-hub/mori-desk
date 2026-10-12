using System;
using System.IO;
namespace MoriDesk {
 // Different libraries share one set of pins/settings. Only one process may
 // own the capture tools at a time, including during a library handoff.
 public sealed class CaptureLease : IDisposable {
  readonly FileStream stream;
  CaptureLease(FileStream value){stream=value;}
  public static CaptureLease TryAcquire(string directory){Directory.CreateDirectory(directory);try{return new CaptureLease(new FileStream(Path.Combine(directory,"runtime.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None));}catch(IOException ex){int code=ex.HResult&0xffff;if(code==32||code==33)return null;throw;}}
  public void Dispose(){stream.Dispose();}
 }
}
