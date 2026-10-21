using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Mori {
 // A clipboard bitmap belongs to its dispatcher until it is frozen. Only this
 // immutable snapshot may cross to the encoder worker; the clipboard itself
 // is never accessed by a background thread.
 internal sealed class ClipboardImageFile : IDisposable {
  internal readonly string Path;
  ClipboardImageFile(string path){Path=path;}
  internal static BitmapSource Snapshot(BitmapSource bitmap){
   if(bitmap==null)throw new InvalidOperationException("剪贴板图片已不可用，请重新复制后重试。");
   var snapshot=bitmap.CloneCurrentValue();snapshot.Freeze();return snapshot;
  }
  internal static Task<ClipboardImageFile> CreateAsync(BitmapSource snapshot,string directory,CancellationToken token){return CreateAsync(snapshot,directory,token,Encode);}
  // The encoder boundary also permits deterministic disk-failure/cancellation
  // regressions without using the real clipboard or presenting a window.
  internal static Task<ClipboardImageFile> CreateAsync(BitmapSource snapshot,string directory,CancellationToken token,Action<BitmapSource,Stream> encode){
   if(snapshot==null||!snapshot.IsFrozen)throw new ArgumentException("后台编码只接受已冻结的图片快照。","snapshot");
   if(encode==null)throw new ArgumentNullException("encode");
   return Task.Run(delegate{
    token.ThrowIfCancellationRequested();Directory.CreateDirectory(directory);
    string path=System.IO.Path.Combine(directory,Guid.NewGuid().ToString("N")+".png");bool created=false;
    try{
     token.ThrowIfCancellationRequested();
     using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
      created=true;encode(snapshot,stream);
     }
     // PNG encoding is not interruptible. Cancellation discards its completed
     // temporary file before an import/commit can start.
     token.ThrowIfCancellationRequested();return new ClipboardImageFile(path);
    }catch{if(created)DeleteTemporary(path);throw;}
   },token);
  }
  static void Encode(BitmapSource snapshot,Stream stream){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(snapshot));encoder.Save(stream);}
  static void DeleteTemporary(string path){try{File.Delete(path);}catch(IOException){}catch(UnauthorizedAccessException){}}
  public void Dispose(){DeleteTemporary(Path);}
 }
}
