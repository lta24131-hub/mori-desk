using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mori;
namespace MoriDesk {
 internal static class FormatPreviewTests {
  static byte[] Psd(){var bytes=new byte[43];Buffer.BlockCopy(Encoding.ASCII.GetBytes("8BPS"),0,bytes,0,4);bytes[5]=1;bytes[13]=3;bytes[17]=1;bytes[21]=1;bytes[23]=8;bytes[25]=3;bytes[40]=200;bytes[41]=100;bytes[42]=50;return bytes;}
  static void Pump(int milliseconds){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(milliseconds)};timer.Tick+=delegate{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
  static void SingleAiRoute(string ai,Action<bool,string> check){
   // Normal application startup initializes theme resources before opening a
   // native window. This group must establish the same prerequisite when run
   // on its own instead of inheriting it from a preceding theme test.
   if(Theme.Current==null)Theme.Set("dark");
   var app=Application.Current;var shutdown=app.ShutdownMode;var previousMain=app.MainWindow;var context=SynchronizationContext.Current;Window opened=null;Exception unhandled=null;
   app.ShutdownMode=ShutdownMode.OnExplicitShutdown;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
   DispatcherUnhandledExceptionEventHandler onError=delegate(object sender,DispatcherUnhandledExceptionEventArgs e){unhandled=e.Exception;e.Handled=true;};app.DispatcherUnhandledException+=onError;
   try{
    PreviewWindow.Images(null,new[]{ai},new[]{"Synthetic all-page AI"},delegate(Window window){opened=window;window.WindowStartupLocation=WindowStartupLocation.Manual;window.Left=-20000;window.Top=-20000;window.ShowInTaskbar=false;window.ShowActivated=false;});
    check(opened is DocumentPreviewWindow,"single AI full preview routes to the multipage document window, not the raster first page");
    opened.Close();Pump(100);
    check(!opened.IsVisible&&!app.Windows.Cast<Window>().Contains(opened),"closing routed AI preview removes the window immediately");
    check(unhandled==null,"closing routed AI preview has no asynchronous dispatcher error"+(unhandled==null?"":": "+unhandled));
   }finally{if(opened!=null&&opened.IsVisible)opened.Close();Pump(20);app.DispatcherUnhandledException-=onError;app.MainWindow=previousMain;app.ShutdownMode=shutdown;SynchronizationContext.SetSynchronizationContext(context);}
  }
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);string originals=Path.Combine(root,"originals");Directory.CreateDirectory(originals);
   foreach(string ext in MediaTypes.Pictures)check(FileRouter.Kind("file"+ext.ToUpperInvariant())=="image","shared design-image route "+ext);
   foreach(string ext in MediaTypes.Videos.Concat(MediaTypes.Audio).Concat(MediaTypes.Documents)){string path=Path.Combine(originals,"route"+ext.ToUpperInvariant());File.WriteAllText(path,"inert routing fixture");var route=LaunchRequest.Parse(new[]{"--preview",path});check(route.Mode=="preview"&&route.Path==path,"standalone read-only preview route "+ext);}
   check(MediaTypes.TextDocuments.All(DocumentContent.Supports),"every advertised text/document format has a content decoder");
   check(!MediaTypes.Pictures.Overlaps(MediaTypes.Documents)&&!MediaTypes.Videos.Overlaps(MediaTypes.Audio),"document/media sections have unambiguous type registry");
   check(FileRouter.PictureFilter.Contains("*.psd")&&FileRouter.PictureFilter.Contains("*.ai"),"standalone picker includes Photoshop and Illustrator formats");
   check(BrowserVideo.MimeType("f.mkv")=="video/x-matroska"&&BrowserVideo.MimeType("f.MP3")=="audio/mpeg"&&BrowserVideo.MimeType("f.OGG")=="audio/ogg","media stream content types distinguish audio and video containers");
   var mediaTypes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){
    {".mp4","video/mp4"},{".mov","video/quicktime"},{".m4v","video/mp4"},{".webm","video/webm"},{".avi","video/x-msvideo"},{".mkv","video/x-matroska"},{".wmv","video/x-ms-wmv"},{".mpg","video/mpeg"},{".mpeg","video/mpeg"},{".m2ts","video/mp2t"},{".mts","video/mp2t"},{".ts","video/mp2t"},{".3gp","video/3gpp"},{".ogv","video/ogg"},
    {".flv","video/x-flv"},{".f4v","video/x-f4v"},{".3g2","video/3gpp2"},{".vob","video/x-ms-vob"},{".dv","video/dv"},{".mxf","application/mxf"},
    {".mp3","audio/mpeg"},{".wav","audio/wav"},{".m4a","audio/mp4"},{".aac","audio/aac"},{".flac","audio/flac"},{".ogg","audio/ogg"},{".opus","audio/ogg"},{".wma","audio/x-ms-wma"},{".aiff","audio/x-aiff"},{".aif","audio/x-aiff"},
    {".m4b","audio/mp4"},{".ac3","audio/ac3"},{".amr","audio/amr"},{".au","audio/basic"},{".caf","audio/x-caf"},{".weba","audio/webm"},{".mka","audio/x-matroska"},{".mp2","audio/mpeg"},{".voc","audio/x-voc"},{".wv","audio/x-wavpack"},{".tta","audio/x-tta"},{".w64","audio/x-w64"},{".eac3","audio/eac3"}
   };
   check(new HashSet<string>(MediaTypes.Videos.Concat(MediaTypes.Audio),StringComparer.OrdinalIgnoreCase).SetEquals(mediaTypes.Keys),"strict MIME regression table covers every classified audio/video extension, including newly added formats");
   foreach(var mediaType in mediaTypes){
    string filename=Path.Combine(originals,"media name.final"+mediaType.Key.ToUpperInvariant());
    check(BrowserVideo.MimeType(filename)==mediaType.Value&&FileFormats.MediaMimeType(mediaType.Key)==mediaType.Value,"browser response uses the correct shared case-insensitive container MIME: "+mediaType.Key);
   }
   check(MediaTypes.Audio.All(ext=>BrowserVideo.MimeType("file"+ext).StartsWith("audio/",StringComparison.Ordinal)),"no audio format is mislabeled as video/mp4 or generic data");
   foreach(string unknown in new[]{null,"","no-extension","file.bin","file.png","file.mp4.backup","file.exe"})check(BrowserVideo.MimeType(unknown)=="application/octet-stream","unknown/non-media data is never falsely identified as MP4: "+unknown);
   check(FormatPreviewInfo.Notice("x.psd").Contains("近似")&&FormatPreviewInfo.Notice("x.ai").Contains("并排比较仅显示首页")&&FormatPreviewInfo.Notice("x.nef").Contains("编码组件"),"design/image raster limitations distinguish comparison first page from the full AI preview");
   check(FormatPreviewInfo.UsesPagedPreview("x.AI")&&FormatPreviewInfo.UsesPagedPreview("x.PDF")&&!FormatPreviewInfo.UsesPagedPreview("x.psd")&&!FormatPreviewInfo.UsesPagedPreview("x.png"),"PDF and AI share case-insensitive all-page viewer routing without changing other picture formats");
   string psd=Path.Combine(originals,"画面.psd"),pdf=Path.Combine(originals,"页面.pdf"),ai=Path.Combine(originals,"画板.ai"),word=Path.Combine(originals,"说明.docx");
   // Different contents intentionally: importing identical bytes must still
   // reuse one asset even if the source suffix changes from PDF to AI.
   byte[] psdBytes=Psd(),pdfBytes=PdfPreviewTests.Fixture(2),aiBytes=PdfPreviewTests.Fixture(3);File.WriteAllBytes(psd,psdBytes);File.WriteAllBytes(pdf,pdfBytes);File.WriteAllBytes(ai,aiBytes);
   using(var archive=ZipFile.Open(word,ZipArchiveMode.Create))using(var writer=new StreamWriter(archive.CreateEntry("word/document.xml").Open(),Encoding.UTF8))writer.Write("<document><p><t>Read only content 中文</t></p></document>");byte[] wordBytes=File.ReadAllBytes(word);
   PreviewThumbnail.ValidateEmbeddedSize(2048,2048);check(true,"bounded system-codec embedded preview is accepted");foreach(var dimensions in new[]{new[]{0,2},new[]{2,-1},new[]{65536,65536},new[]{4096,2048},new[]{100001,1}}){bool oversized=false;try{PreviewThumbnail.ValidateEmbeddedSize(dimensions[0],dimensions[1]);}catch(NotSupportedException){oversized=true;}check(oversized,"invalid or oversized embedded system preview is rejected before pixel transform");}
   int width,height;var psdThumb=PreviewThumbnail.Read(psd,64,out width,out height);check(psdThumb.IsFrozen&&width==1&&height==1,"PSD tile uses original dimensions and frozen bounded raster");
   string embeddedFile=Path.Combine(originals,"saved-16bit-ZIP.psd");File.WriteAllBytes(embeddedFile,PsdEmbeddedThumbnailTests.Fixture());var embeddedThumb=PreviewThumbnail.Read(embeddedFile,64,out width,out height);check(width==12000&&height==8000&&embeddedThumb.PixelWidth==64&&embeddedThumb.PixelHeight==32&&embeddedThumb.IsFrozen,"PSD thumbnail prefers saved preview even when full composite is unsupported 16-bit ZIP");
   foreach(string ext in new[]{".png",".jpg"}){
    string photo=Path.Combine(originals,"raster"+ext);using(var bitmap=new System.Drawing.Bitmap(640,360))using(var draw=System.Drawing.Graphics.FromImage(bitmap)){draw.Clear(System.Drawing.Color.CadetBlue);bitmap.Save(photo,ext==".png"?System.Drawing.Imaging.ImageFormat.Png:System.Drawing.Imaging.ImageFormat.Jpeg);}
    var raster=PreviewThumbnail.Read(photo,128,out width,out height);check(width==640&&height==360&&raster.IsFrozen&&raster.PixelWidth<=128&&raster.PixelHeight<=128,"stream-source thumbnails preserve ordinary raster compatibility without URI cache: "+ext);
    using(var exclusive=new FileStream(photo,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(exclusive.Length>0,"ordinary raster thumbnail releases file: "+ext);
    BitmapSource detached;using(var source=new FileStream(photo,FileMode.Open,FileAccess.Read,FileShare.Read)){var decoded=BitmapDecoder.Create(source,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);detached=PreviewThumbnail.ScaleEmbedded(decoded.Frames[0],64);}var detachedPixels=new byte[detached.PixelWidth*detached.PixelHeight*4];detached.CopyPixels(detachedPixels,detached.PixelWidth*4,0);check(detached.IsFrozen&&detached.PixelWidth<=64&&detachedPixels.Any(x=>x!=0),"embedded preview materializes bounded pixels before decoder stream closes: "+ext);
   }
   using(var image=PreviewImage.Open(psd))check(image.Image.Width==1&&image.Image.Height==1,"PSD preview opens same merged composite as its tile");
   using(var image=PreviewImage.Open(ai))check(image.Image.Width>0&&image.Image.Width<=2048,"AI image preview uses PDF-compatible first page");
   check(PdfPreview.RenderAsync(ai,2,256,CancellationToken.None).GetAwaiter().GetResult().PageCount==3,"AI thumbnail first page does not prevent rendering the final exported page");
   var aiLaunch=LaunchRequest.Parse(new[]{"--image",ai});check(aiLaunch.Mode=="image"&&aiLaunch.Path==ai&&FormatPreviewInfo.UsesPagedPreview(aiLaunch.Path),"standalone image launch preserves AI category while dispatching to the full-page viewer");
   SingleAiRoute(ai,check);
   var aiThumb=PreviewThumbnail.Read(ai,64,out width,out height);check(aiThumb.IsFrozen&&aiThumb.PixelWidth<=64&&width==0&&height==0,"AI page units are not mislabeled as source raster resolution");
   var wordThumb=PreviewThumbnail.Read(word,128,out width,out height);check(wordThumb.IsFrozen&&wordThumb.PixelHeight<=128,"Office has bounded labelled content thumbnail, without Office installation");
   foreach(string path in new[]{psd,pdf,ai,word})check(ReferenceThumbnail.CanPreview(new FileReference{Path=path}),"local shortcuts use same thumbnail capability: "+Path.GetExtension(path));
   check(!ReferenceThumbnail.CanPreview(new FileReference{Path=originals,Directory=true}),"folders never become recursively decoded material thumbnails");
   using(var lib=new Library(Path.Combine(root,"library"))){
    lib.EnableReferenceAssets();
    foreach(string path in new[]{psd,pdf,ai,word}){var item=lib.Import(path,"",null,"","","").Item;lib.BuildThumbnail(item);check(File.Exists(lib.ThumbnailPath(item)),"library copied material thumbnail: "+item.Ext);var raster=ThumbnailImage.ReadThumbnail(lib.ThumbnailPath(item));check(raster.PixelWidth<=512&&raster.PixelHeight<=512,"cached thumbnail stays bounded: "+item.Ext);}
    string[] keys=lib.AddReferenceAssets(new[]{psd,pdf,ai,word},"");var refs=lib.All().Where(a=>a.IsReference).ToArray();check(refs.Length==4&&keys.Length==4,"same original files may remain local shortcuts without copying");
    foreach(var item in refs){lib.BuildThumbnail(item);check(File.Exists(lib.ThumbnailPath(item)),"local shortcut receives same thumbnail: "+item.Ext);check(MediaTypes.Contains(item,"pictures")==new[]{".psd",".ai"}.Contains(item.Ext),"local design previews preserve section semantics: "+item.Ext);}
    check(lib.All().Count(a=>MediaTypes.Contains(a,"documents"))==4&&lib.All().Count(a=>MediaTypes.Contains(a,"assets"))==4,"PDF/Office stay separate while PSD/AI remain visible with pictures");
    var cache=lib.ThumbnailPath(refs.First(a=>a.Ext==".psd"));DateTime stamp=File.GetLastWriteTimeUtc(cache);lib.BuildThumbnail(refs.First(a=>a.Ext==".psd"));check(File.GetLastWriteTimeUtc(cache)==stamp,"unchanged local source reuses thumbnail cache without redecoding");
   }
   check(File.ReadAllBytes(psd).SequenceEqual(psdBytes)&&File.ReadAllBytes(ai).SequenceEqual(aiBytes)&&File.ReadAllBytes(pdf).SequenceEqual(pdfBytes)&&File.ReadAllBytes(word).SequenceEqual(wordBytes),"all original design and document files remain byte-for-byte unchanged");
   bool cancelled=false;try{PreviewImage.OpenAsync(psd,new CancellationToken(true)).GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}check(cancelled,"new image formats honor pre-cancelled preview admission");
   var app=System.Windows.Application.Current;var previousTheme=Theme.Current;foreach(string theme in new[]{"dark","light"}){Theme.Set(theme);var window=new DocumentPreviewWindow(null,word,"内容预览");check(!window.IsVisible&&window.Owner==null,"document preview creates without touching the live desktop: "+theme);window.Close();}Theme.Set(previousTheme);
  }
 }
}
