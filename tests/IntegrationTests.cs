using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QingJie;
namespace MoriDesk {
 public static class IntegrationTests {
  static T Await<T>(Task<T> task){var frame=new DispatcherFrame();task.ContinueWith(t=>frame.Continue=false);Dispatcher.PushFrame(frame);return task.GetAwaiter().GetResult();}
  public static int Run(){string folder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"integration-test");Directory.CreateDirectory(folder);string log=Path.Combine(folder,"report.txt");try{
   Preferences.TestFilePath=Path.Combine(folder,"preferences.json");AppState.Settings=new Preferences();OcrLanguagePacks.TestFolder=Path.Combine(folder,"languages");
   var visual=new DrawingVisual();using(var draw=visual.RenderOpen()){draw.DrawRectangle(Brushes.White,null,new Rect(0,0,560,160));draw.DrawText(new FormattedText("Open file\nSave image",System.Globalization.CultureInfo.GetCultureInfo("en-US"),FlowDirection.LeftToRight,new Typeface("Arial"),32,Brushes.Black),new Point(24,20));}
   var bitmap=new RenderTargetBitmap(560,160,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);bitmap.Freeze();var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var output=File.Create(Path.Combine(folder,"ocr-source.png")))encoder.Save(output);
   var timer=System.Diagnostics.Stopwatch.StartNew();var page=Await(OcrService.Read(bitmap));long ocrMs=timer.ElapsedMilliseconds;if(!page.Text.ToLowerInvariant().Contains("open")||!page.Text.ToLowerInvariant().Contains("save"))throw new Exception("OCR mismatch: "+page.Text);
   using(var timeout=new CancellationTokenSource(25000)){string chinese=Await(Translation.Translate("Open file",timeout.Token,"zh","tencent"));string english=Await(Translation.Translate("打开文件",timeout.Token,"en","tencent"));if(!chinese.Any(c=>c>=0x4e00&&c<=0x9fff)||!english.Any(c=>char.IsLetter(c)&&c<128))throw new Exception("Translation returned unexpected output");var regions=ImageTranslation.Regions(page);var translations=Await(Translation.TranslateImage(regions.Select(r=>r.Text).ToArray(),timeout.Token,"zh","tencent"));var translated=ImageTranslation.Render(bitmap,regions,translations);if(translated.PixelWidth!=bitmap.PixelWidth||translated.PixelHeight!=bitmap.PixelHeight)throw new Exception("Overlay dimensions changed");encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(translated));using(var output=File.Create(Path.Combine(folder,"ocr-translated.png")))encoder.Save(output);File.WriteAllText(log,"PASS OCR subprocess + EN→ZH + ZH→EN + original-position overlay\r\nOCR: "+page.Text+"\r\nChinese: "+chinese+"\r\nEnglish: "+english+"\r\nOCR ms: "+ocrMs+"\r\nTotal ms: "+timer.ElapsedMilliseconds);}
   return 0;
  }catch(Exception ex){File.WriteAllText(log,"FAIL "+ex);return 1;}}
 }
}
