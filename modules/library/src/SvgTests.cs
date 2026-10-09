using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media.Imaging;

namespace Mori {
 public static class SvgTests {
  static bool Reject(Action action){try{action();return false;}catch{return true;}}
  static void Save(BitmapSource b,string file){var e=new PngBitmapEncoder();e.Frames.Add(BitmapFrame.Create(b));using(var s=File.Create(file))e.Save(s);}
  public static void Run(string output,Action<bool,string> check){
   Directory.CreateDirectory(output);string file=Path.Combine(output,"vector.svg");
   string svg="<svg xmlns='http://www.w3.org/2000/svg' width='320' height='180' viewBox='0 0 320 180'><defs><linearGradient id='g'><stop stop-color='#477bff'/><stop offset='1' stop-color='#32cda6'/></linearGradient></defs><rect x='8' y='8' width='304' height='164' rx='22' fill='url(#g)'/><path d='M38 100 L78 50 L118 100 Z' fill='white' stroke='#172c58' stroke-width='4'/><circle cx='166' cy='70' r='26' fill='none' stroke='white' stroke-width='8'/><text x='32' y='146' font-size='28' fill='white' font-family='Arial'>Mori SVG</text></svg>";
   File.WriteAllText(file,svg,Encoding.UTF8);byte[] original=File.ReadAllBytes(file);int w,h;
   var watch=System.Diagnostics.Stopwatch.StartNew();var bitmap=SvgRaster.Render(file,512,out w,out h);watch.Stop();
   check(w==320&&h==180&&bitmap.PixelWidth==512&&bitmap.PixelHeight==288&&bitmap.IsFrozen,"isolated SVG renderer handles dimensions, paths, strokes, gradient, text and transparent output");Save(bitmap,Path.Combine(output,"vector-thumbnail.png"));File.WriteAllText(Path.Combine(output,"timing.txt"),"Cold SVG render: "+watch.ElapsedMilliseconds+" ms");
   using(var preview=PreviewImage.Open(file))check(preview.Image.Width==2048&&preview.Image.Height==1152,"SVG opens in internal zoom/pan preview with bounded high-resolution raster");
   using(var lib=new Library(Path.Combine(output,"library"))){var a=lib.Import(file,"","SVG 示例","","","").Item;lib.BuildThumbnail(a);check(File.Exists(lib.ThumbnailPath(a))&&a.Width==320&&a.Height==180&&!string.IsNullOrEmpty(a.Color),"SVG gallery builds cached thumbnail and color index while keeping natural dimensions");var timestamp=File.GetLastWriteTimeUtc(lib.ThumbnailPath(a));lib.BuildThumbnail(a);check(timestamp==File.GetLastWriteTimeUtc(lib.ThumbnailPath(a)),"cached SVG thumbnail avoids repeated rendering");}
   check(original.SequenceEqual(File.ReadAllBytes(file)),"SVG source bytes stay untouched");
   string viewbox=Path.Combine(output,"viewbox.svg");File.WriteAllText(viewbox,"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 58 55'><path fill='#dd9933' d='M0 0H58V55H0Z'/></svg>");SvgRaster.Render(viewbox,512,out w,out h);check(w==58&&h==55,"SVG without explicit dimensions uses its viewBox");
   string active=Path.Combine(output,"untrusted.svg");File.WriteAllText(active,"<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink' width='40' height='40' onload='bad()'><script>bad()</script><foreignObject><body>bad</body></foreignObject><image href='file:///C:/secret.png'/><use xlink:href='https://example.com/a.svg#x'/><style>@import url(https://example.com/style.css);</style><rect width='40' height='40' fill='red'/></svg>");
   string cleaned=SvgRaster.SafeDocument(active).OuterXml;check(!cleaned.Contains("bad()")&&!cleaned.Contains("secret")&&!cleaned.Contains("https://")&&!cleaned.Contains("foreignObject"),"SVG sanitizer strips scripts, foreign objects, handlers, external href and CSS fetches");SvgRaster.Render(active,64,out w,out h);check(w==40&&h==40,"untrusted SVG retains safe vector content without loading external resources");
   string dtd=Path.Combine(output,"dtd.svg");File.WriteAllText(dtd,"<!DOCTYPE svg PUBLIC '-//W3C//DTD SVG 1.1//EN' 'http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd'>"+svg);check(SvgRaster.SafeDocument(dtd).DocumentType==null,"ordinary export DOCTYPE is ignored without fetching its DTD");File.WriteAllText(dtd,"<!DOCTYPE svg [<!ENTITY leak SYSTEM 'file:///C:/secret'>]><svg xmlns='http://www.w3.org/2000/svg'>&leak;</svg>");check(Reject(()=>SvgRaster.SafeDocument(dtd)),"SVG entity expansion and external entity resolution are rejected");
   File.WriteAllText(dtd,"<svg>broken");check(Reject(()=>SvgRaster.Render(dtd,512,out w,out h)),"invalid SVG fails inside worker without crashing parent");
   File.WriteAllText(dtd,"<svg>"+string.Concat(Enumerable.Repeat("<g>",100))+string.Concat(Enumerable.Repeat("</g>",100))+"</svg>");check(Reject(()=>SvgRaster.SafeDocument(dtd)),"deeply nested SVG is rejected before renderer recursion");
   using(var s=File.Create(dtd))s.SetLength(SvgRaster.MaximumInput+1);check(Reject(()=>SvgRaster.Render(dtd,512,out w,out h)),"oversized SVG is rejected before allocating document or launching worker");
  }
 }
}
