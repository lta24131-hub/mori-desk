using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
namespace MoriDesk {
 internal static class ScreenshotCollector {
  internal static Task<Mori.ImportResult> ImportAsync(Mori.Library library,BitmapSource bitmap){
   if(library==null)throw new ArgumentNullException("library");
   if(bitmap==null)throw new ArgumentNullException("bitmap");
   // Encoding and disk work must not block the screenshot toolbar. Exported
   // captures are frozen; clone other callers' images without changing them.
   var image=bitmap.IsFrozen?bitmap:(BitmapSource)bitmap.GetCurrentValueAsFrozen();
   return Task.Run(()=>{
    string file=Path.Combine(Path.GetTempPath(),"MoriDesk-capture-"+Guid.NewGuid().ToString("N")+".png");
    try{
     var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
     using(var stream=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None))encoder.Save(stream);
     // Reuse preserves existing metadata/categories and restores a recycled
     // duplicate. "skip" is a no-write decision, including for NEW screenshots.
     var result=library.Import(file,"","截图 "+DateTime.Now.ToString("yyyy-MM-dd HHmmss"),"","","",DateTime.UtcNow.ToString("o"),"reuse");
     if(result.Item==null||result.Skipped||result.ChoiceRequired)throw new IOException("截图尚未写入素材库，请重试。");
     return result;
    }finally{
     // A temporary-file cleanup failure must not turn a committed import into
     // a false failure and cause the user to save again.
     try{if(File.Exists(file))File.Delete(file);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
   });
  }
 }
}
