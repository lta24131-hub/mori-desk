using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Windows.Media.Ocr;
using Windows.Graphics.Imaging;

namespace QingJie {
    public sealed class WordBox { public string Text; public Rect Box; public int Line; public int TextStart; }
    public sealed class OcrPage { public string Text; public List<WordBox> Words = new List<WordBox>(); }
    public static class OcrService {
        public static void IndexText(OcrPage page){var text=new System.Text.StringBuilder();int line=-1;foreach(var word in page.Words){if(text.Length>0){if(line!=word.Line)text.AppendLine();else if(word.Text.Length>0&&Geometry.NeedsWordSpace(text[text.Length-1],word.Text[0]))text.Append(' ');}word.TextStart=text.Length;text.Append(word.Text);line=word.Line;}page.Text=text.ToString();}
        private static Task<T> Wait<T>(Windows.Foundation.IAsyncOperation<T> operation) {
            var completion = new TaskCompletionSource<T>();
            operation.Completed = (op, status) => {
                try { if (status == Windows.Foundation.AsyncStatus.Canceled) completion.TrySetCanceled(); else completion.TrySetResult(op.GetResults()); }
                catch (Exception error) { completion.TrySetException(error); }
            };
            return completion.Task;
        }
        public static string Languages() {
            var result = new List<string>();
            foreach (var lang in OcrEngine.AvailableRecognizerLanguages) result.Add(lang.LanguageTag);
            return string.Join(", ", result);
        }
        public static Task<OcrPage> Read(BitmapSource source) {return OcrWorker.Read(source,System.Threading.CancellationToken.None);}
        public static Task<OcrPage> Read(BitmapSource source,System.Threading.CancellationToken cancel) {return OcrWorker.Read(source,cancel);}
        public static async Task<OcrPage> ReadLocal(BitmapSource source,string extraLanguage=null) {
            OcrEngine engine = null;
            // Prefer a Chinese recognizer when installed; it also recognizes Latin text.
            foreach (var lang in OcrEngine.AvailableRecognizerLanguages)
                if (lang.LanguageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) { engine = OcrEngine.TryCreateFromLanguage(lang); break; }
            if (engine == null) engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null) throw new InvalidOperationException("Windows 尚未安装文字识别语言。请在系统的语言选项中添加中文或英文 OCR 组件。");
            // Small, anti-aliased website text needs more pixels than the original
            // screenshot gives Windows OCR. Keep the inverse coordinate mapping.
            double ratio = Math.Min(3.0, (double)OcrEngine.MaxImageDimension / Math.Max(source.PixelWidth, source.PixelHeight));
            BitmapSource input = Prepare(source, ratio);
            var pixels=new OcrPixels(input);
            OcrPage page;
            using (var bitmap = pixels.WindowsBitmap()) {
                var result = await Wait(engine.RecognizeAsync(bitmap));
                page = new OcrPage { Text = result.Text };
                int line = 0;
                foreach (var l in result.Lines) {
                    foreach (var word in l.Words) { var b = word.BoundingRect; page.Words.Add(new WordBox { Text=word.Text, Line=line, Box=new Rect(b.X/ratio,b.Y/ratio,b.Width/ratio,b.Height/ratio) }); }
                    line++;
                }
                IndexText(page);
            }
            return await EnglishOcr.Improve(pixels,ratio,page,extraLanguage??OcrLanguagePacks.EffectiveSelectionKey);
        }
        public static BitmapSource Prepare(BitmapSource source,double ratio){
            var bgra=new FormatConvertedBitmap(source,System.Windows.Media.PixelFormats.Bgra32,null,0);
            int width=source.PixelWidth,height=source.PixelHeight;var pixels=new byte[checked(width*height)];var row=new byte[checked(width*4)];int dark=0,samples=0;
            for(int y=0;y<height;y++){
                bgra.CopyPixels(new Int32Rect(0,y,width,1),row,width*4,0);
                for(int x=0;x<width;x++){int i=x*4,at=y*width+x;double luminance=row[i]*.0722+row[i+1]*.7152+row[i+2]*.2126;
                    if(at%16==0){if(luminance<100)dark++;samples++;}
                    pixels[at]=(byte)(((int)luminance*row[i+3]+255*(255-row[i+3]))/255);
                }
            }
            if(dark>samples*.6)for(int i=0;i<pixels.Length;i++)pixels[i]=(byte)(255-pixels[i]);
            // Same grayscale samples, 1 byte/pixel instead of four identical channels.
            var normalized=BitmapSource.Create(width,height,96,96,System.Windows.Media.PixelFormats.Gray8,null,pixels,width);normalized.Freeze();
            var scaled=new TransformedBitmap(normalized,new System.Windows.Media.ScaleTransform(ratio,ratio));scaled.Freeze();return scaled;
        }
    }
}
