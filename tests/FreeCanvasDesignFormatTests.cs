using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mori;

namespace MoriDesk {
 // All artwork and project data are generated within the suite's isolated
 // directory. No actual gallery, desktop artwork or user settings are read.
 internal static class FreeCanvasDesignFormatTests {
  static bool Reject(Action action){try{action();return false;}catch{return true;}}
  static bool Canceled(Action action){try{action();return false;}catch(OperationCanceledException){return true;}}
  static byte[] Center(BitmapSource bitmap){var converted=new FormatConvertedBitmap(bitmap,PixelFormats.Bgra32,null,0);var pixel=new byte[4];converted.CopyPixels(new Int32Rect(bitmap.PixelWidth/2,bitmap.PixelHeight/2,1,1),pixel,4,0);return pixel;}
  static void NoPending(FreeCanvasStore store,Action<bool,string> check,string context){check(!Directory.GetFiles(store.Root,"*",SearchOption.AllDirectories).Any(p=>Path.GetFileName(p).Contains(".part")),context+" leaves no staged source or PNG attachment behind");}
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);var store=new FreeCanvasStore(Path.Combine(root,"data"));var project=store.Create("设计文件画布");
   string svg=Path.Combine(root,"safe artwork.SVG");string content="<svg xmlns='http://www.w3.org/2000/svg' width='400' height='200' viewBox='0 0 400 200'><rect x='20' y='20' width='360' height='160' fill='#b04080'/></svg>";File.WriteAllText(svg,content,Encoding.UTF8);byte[] svgOriginal=File.ReadAllBytes(svg);DateTime svgTime=File.GetLastWriteTimeUtc(svg);
   check(FreeCanvasStore.SupportedImage(svg)&&FreeCanvasStore.SupportedImage(Path.Combine(root,"test.AI")),"canvas accepts SVG and Illustrator source extensions case-insensitively");
   var vector=store.Import(project.Id,svg,CancellationToken.None);string ownSvg=store.ImagePath(project.Id,vector.Image);int width,height;var vectorBitmap=FreeCanvasStore.Decode(ownSvg,ownSvg,2048,CancellationToken.None,out width,out height);byte[] center=Center(vectorBitmap);
   check(vector.Kind=="image"&&Path.GetExtension(vector.Image)==".png"&&vector.Source==svg&&File.Exists(ownSvg)&&vector.Width==360&&vector.Height==180,"SVG becomes an independent PNG image item at its original artwork aspect ratio");
   check(width==2048&&height==1024&&vectorBitmap.IsFrozen&&center[0]==128&&center[1]==64&&center[2]==176&&center[3]==255,"SVG attachment keeps rendered vector content within the 2048-pixel bound");
   byte[] svgCopy=File.ReadAllBytes(ownSvg);File.WriteAllText(svg,"<svg>changed externally</svg>");check(svgCopy.SequenceEqual(File.ReadAllBytes(ownSvg)),"subsequent SVG source edits do not mutate committed canvas artwork");File.WriteAllBytes(svg,svgOriginal);File.SetLastWriteTimeUtc(svg,svgTime);
   check(svgOriginal.SequenceEqual(File.ReadAllBytes(svg))&&File.GetLastWriteTimeUtc(svg)==svgTime,"SVG conversion never changes source bytes or modification time");
   string ai=Path.Combine(root,"two artboards.AI");byte[] aiOriginal=PdfPreviewTests.Fixture(new[]{320,180},new[]{180,320});File.WriteAllBytes(ai,aiOriginal);DateTime aiTime=File.GetLastWriteTimeUtc(ai);var illustrator=store.Import(project.Id,ai,CancellationToken.None);string ownAi=store.ImagePath(project.Id,illustrator.Image);var aiBitmap=FreeCanvasStore.Decode(ownAi,ownAi,2048,CancellationToken.None,out width,out height);var first=PdfPreview.RenderAsync(ai,0,2048,CancellationToken.None).GetAwaiter().GetResult();var second=PdfPreview.RenderAsync(ai,1,64,CancellationToken.None).GetAwaiter().GetResult();
   check(illustrator.Kind=="image"&&Path.GetExtension(illustrator.Image)==".png"&&illustrator.Source==ai&&Math.Abs(illustrator.Width/illustrator.Height-320.0/180)<.000001,"PDF-compatible Illustrator becomes only the first artboard PNG, with the original source path retained");
   check(Math.Abs(illustrator.Width/illustrator.Height-(double)first.Bitmap.PixelWidth/first.Bitmap.PixelHeight)<.000001,"Illustrator canvas layout preserves its rendered PNG aspect ratio despite rounded native page dimensions");
   check(width<=2048&&height<=2048&&Center(aiBitmap).SequenceEqual(Center(first.Bitmap))&&!Center(aiBitmap).SequenceEqual(Center(second.Bitmap)),"Illustrator attachment contains the first page only, not another artboard or a misleading document placeholder");
   check(aiOriginal.SequenceEqual(File.ReadAllBytes(ai))&&File.GetLastWriteTimeUtc(ai)==aiTime,"Illustrator conversion leaves original multi-artboard bytes and timestamp untouched");
   project.Items.Add(vector);project.Items.Add(illustrator);store.Save(project);var loaded=store.Load(project.Id);check(loaded.Items.Count==2&&loaded.Items.All(item=>Path.GetExtension(item.Image)==".png")&&loaded.Items[1].Source==ai,"converted design attachments and source references survive atomic save and reload");
   var again=store.Import(project.Id,ai,CancellationToken.None);check(again.Image!=illustrator.Image&&File.ReadAllBytes(ownAi).SequenceEqual(File.ReadAllBytes(store.ImagePath(project.Id,again.Image))),"repeated AI insertion owns a separate attachment without mutating previously committed artwork");
   // Raster sources still keep their original encoded bytes, rather than being
   // silently recompressed by the new design-format conversion path.
   var raster=store.Import(project.Id,ownSvg,CancellationToken.None);check(File.ReadAllBytes(ownSvg).SequenceEqual(File.ReadAllBytes(store.ImagePath(project.Id,raster.Image))),"ordinary raster imports preserve the original encoded bytes exactly");
   NoPending(store,check,"successful design conversion");
   int attachments=Directory.GetFiles(Path.Combine(store.Root,project.Id,"images")).Length;using(var canceled=new CancellationTokenSource()){canceled.Cancel();check(Canceled(()=>store.Import(project.Id,ai,canceled.Token))&&Canceled(()=>store.Import(project.Id,svg,canceled.Token)),"pre-canceled SVG and AI imports commit no attachment or decoder work");}
   check(attachments==Directory.GetFiles(Path.Combine(store.Root,project.Id,"images")).Length,"pre-canceled design conversion does not create partial project images");
   // Hold the existing cancellable PDF worker gate: cancellation must settle
   // even while the native renderer is queued, and must clean its source stage.
   PdfPreview.Workers.Wait();try{using(var canceled=new CancellationTokenSource()){var queued=Task.Run(()=>store.Import(project.Id,ai,canceled.Token));bool staged=SpinWait.SpinUntil(()=>Directory.GetFiles(Path.Combine(store.Root,project.Id,"images"),"*.part.ai").Any(),5000);canceled.Cancel();bool settled=Canceled(()=>queued.GetAwaiter().GetResult());check(staged&&settled,"queued AI conversion cancellation returns without needing a native render slot");}}finally{PdfPreview.Workers.Release();}
   NoPending(store,check,"canceled design conversion");
   string legacy=Path.Combine(root,"legacy.ai");File.WriteAllText(legacy,"%!PS-Adobe-3.0\n%%Creator: generated fixture\n",Encoding.ASCII);bool explained=false;try{store.Import(project.Id,legacy,CancellationToken.None);}catch(NotSupportedException error){explained=error.Message.Contains("创建 PDF 兼容文件");}check(explained,"legacy Illustrator content explains the PDF-compatible export requirement instead of falsely importing a blank image");
   string malformed=Path.Combine(root,"broken.svg");File.WriteAllText(malformed,"<svg>broken",Encoding.ASCII);check(Reject(()=>store.Import(project.Id,malformed,CancellationToken.None)),"malformed SVG fails inside the existing isolated renderer without committing an attachment");
   string oversize=Path.Combine(root,"oversized.svg");using(var file=File.Create(oversize))file.SetLength(SvgRaster.MaximumInput+1);check(Reject(()=>store.Import(project.Id,oversize,CancellationToken.None)),"SVG import applies its tighter 8 MB cap before staging or starting a renderer");using(var file=new FileStream(oversize,FileMode.Open,FileAccess.Write))file.SetLength(16);
   check(Reject(()=>store.Import(project.Id,"https://example.com/art.svg",CancellationToken.None))&&Reject(()=>store.Import(project.Id,root,CancellationToken.None)),"canvas design import does not download URLs or expand dropped folders");
   check(Reject(()=>store.ImagePath(project.Id,new string('a',32)+".svg"))&&Reject(()=>store.ImagePath(project.Id,new string('a',32)+".ai")),"project image records accept only safe raster attachments, never untrusted design documents");
   check(attachments==Directory.GetFiles(Path.Combine(store.Root,project.Id,"images")).Length,"failed design conversion adds no final image file");NoPending(store,check,"failed design conversion");
   check(aiOriginal.SequenceEqual(File.ReadAllBytes(ai))&&svgOriginal.SequenceEqual(File.ReadAllBytes(svg))&&File.GetLastWriteTimeUtc(ai)==aiTime&&File.GetLastWriteTimeUtc(svg)==svgTime,"all successful, canceled and failed imports preserve the original generated design sources");
  }
 }
}
