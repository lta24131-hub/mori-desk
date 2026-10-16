#if IMAGE_TRANSLATION_LAYOUT_TESTS
// Standalone off-screen layout tests. Compile with real ImageTranslation.cs
// only; adapters below expose no OCR, network, settings, AppState or UI entry.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;

internal static class ImageTranslationLayoutTests {
    static int checks,cases;
    static string sandbox;
    static void Check(bool condition,string name){if(!condition)throw new InvalidOperationException(name);checks++;Console.WriteLine("PASS "+name);}
    static void Run(string name,Action test){Console.WriteLine("CASE "+name);test();cases++;}
    static WordBox Word(string text,int line,double x,double y,double width=100,double height=18){return new WordBox{Text=text,Line=line,Box=new Rect(x,y,width,height)};}
    static TranslationRegion Region(Rect box,string text="source words"){return new TranslationRegion{Text=text,Box=box};}
    static BitmapSource Source(int width=320,int height=180,params Tuple<Rect,Color>[] marks){
        var pixels=new byte[checked(width*height*4)];
        for(int index=0;index<pixels.Length;index+=4){pixels[index]=pixels[index+1]=pixels[index+2]=pixels[index+3]=255;}
        foreach(var mark in marks){
            for(int y=Math.Max(0,(int)Math.Floor(mark.Item1.Top));y<Math.Min(height,(int)Math.Ceiling(mark.Item1.Bottom));y++)
                for(int x=Math.Max(0,(int)Math.Floor(mark.Item1.Left));x<Math.Min(width,(int)Math.Ceiling(mark.Item1.Right));x++){
                    int index=(y*width+x)*4;pixels[index]=mark.Item2.B;pixels[index+1]=mark.Item2.G;pixels[index+2]=mark.Item2.R;pixels[index+3]=255;
                }
        }
        var bitmap=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);bitmap.Freeze();return bitmap;
    }
    static byte[] Pixels(BitmapSource source){var converted=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);var pixels=new byte[checked(source.PixelWidth*source.PixelHeight*4)];converted.CopyPixels(pixels,source.PixelWidth*4,0);return pixels;}
    static void Common(BitmapSource original,ImageTranslationResult result,string name){
        Check(result!=null&&result.Image!=null&&result.Image.IsFrozen,name+": frozen image returned");
        Check(result.Image.PixelWidth==original.PixelWidth&&result.Image.PixelHeight==original.PixelHeight,name+": original pixel dimensions preserved");
        Check(result.Transcript!=null&&result.UnplacedCount>=0,name+": explicit transcript and valid unplaced status");
    }
    static void Unchanged(BitmapSource original,BitmapSource rendered,string name){Check(Pixels(original).SequenceEqual(Pixels(rendered)),name+": all original pixels retained");}
    static void OutsideUnchanged(BitmapSource original,BitmapSource rendered,IEnumerable<Rect> allowed,string name){
        var oldPixels=Pixels(original);var newPixels=Pixels(rendered);var boxes=allowed.ToArray();int width=original.PixelWidth;
        bool same=true;int compared=0;
        for(int y=0;y<original.PixelHeight;y++)for(int x=0;x<width;x++){
            if(boxes.Any(box=>box.Contains(new Point(x+.5,y+.5))))continue;
            int index=(y*width+x)*4;for(int channel=0;channel<4;channel++)if(oldPixels[index+channel]!=newPixels[index+channel])same=false;compared++;
        }
        Check(same&&compared>0,name+": every pixel outside affected region is identical");
    }
    static int ColorCount(BitmapSource image,Rect box,bool red){
        var pixels=Pixels(image);int count=0;
        for(int y=(int)Math.Max(0,box.Top);y<Math.Min(image.PixelHeight,box.Bottom);y++)for(int x=(int)Math.Max(0,box.Left);x<Math.Min(image.PixelWidth,box.Right);x++){
            int index=(y*image.PixelWidth+x)*4;int blue=pixels[index],green=pixels[index+1],r=pixels[index+2];
            if(red?r>100&&r>blue*1.5&&green<r*.65:blue>100&&blue>r*1.5&&green<blue*.65)count++;
        }
        return count;
    }
    static void Png(BitmapSource bitmap,string filename){using(var stream=File.Create(Path.Combine(sandbox,filename))){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));encoder.Save(stream);}}
    static void Throws<T>(Action action,string name) where T:Exception{bool caught=false;try{action();}catch(T){caught=true;}Check(caught,name);}
    static void Tests(){
        Check(!typeof(ImageTranslationLayoutTests).Assembly.GetReferencedAssemblies().Any(a=>a.Name=="Tesseract"||a.Name=="Mori"||a.Name=="System.Net.Http"||a.Name=="PresentationFramework"),
            "standalone host references no OCR, application, network client or window UI assembly");
        Run("Interleaved multi-column paragraph geometry",()=>{
            var page=new OcrPage();
            page.Words.AddRange(new[]{Word("left one",0,20,20),Word("right one",1,220,20.5),Word("left two",2,20,38),Word("right two",3,220,38.5),Word("left three",4,20,56),Word("right three",5,220,56.5)});
            var regions=ImageTranslation.Regions(page);
            Check(regions.Count==2,"interleaved column tops do not split either paragraph");
            var left=regions.Single(r=>r.Box.Left==20);var right=regions.Single(r=>r.Box.Left==220);
            Check(left.Text=="left one\nleft two\nleft three"&&right.Text=="right one\nright two\nright three","columns retain their own source line order and complete text");
            Check(left.Lines==3&&right.Lines==3&&left.SourceLines.Count==3&&right.SourceLines.Count==3,"every original source line and geometry is retained");
            Check(left.SourceLines[1].Box==new Rect(20,38,100,18)&&right.SourceLines[2].Box==new Rect(220,56.5,100,18),"source-line boxes survive paragraph grouping exactly");
        });
        Run("One-third pixel source line overlap",()=>{
            var page=new OcrPage();page.Words.AddRange(new[]{Word("first line",0,20,20),Word("second line",1,20,38-1.0/3)});
            var regions=ImageTranslation.Regions(page);
            Check(regions.Count==1&&regions[0].Lines==2,"minus one-third pixel row gap merges same-column neighbours");
            Check(Math.Abs(regions[0].SourceLines[1].Box.Top-(38-1.0/3))<1e-9,"subpixel original row geometry is not rounded away");
        });
        Run("Saved sample unequal ink heights and negative row gap",()=>{
            var page=new OcrPage();var first=Word("Independent",0,755,452,106,27+2.0/3);var second=Word("wheel travel,",1,753+2.0/3,475+1.0/3,107,15+1.0/3);page.Words.AddRange(new[]{first,second});
            var regions=ImageTranslation.Regions(page);
            Check(regions.Count==1&&regions[0].Text=="Independent\nwheel travel,","saved-sample unequal ink heights and minus 4.333 px gap form one coherent phrase");
            Check(regions[0].SourceLines.Count==2&&regions[0].SourceLines[0].Box==first.Box&&regions[0].SourceLines[1].Box==second.Box,"saved-sample negative-gap source boxes are retained exactly");
        });
        Run("Arabic recognizer logical word order",()=>{
            var page=new OcrPage();page.Words.AddRange(new[]{Word("مرحبا",0,180,20,60),Word("بكم",0,110,20,60)});
            var regions=ImageTranslation.Regions(page);
            Check(regions.Count==1&&regions[0].Text=="مرحبا بكم","right-to-left source words retain recognizer logical order rather than sorting by X");
            Check(regions[0].SourceLines.Count==1&&regions[0].SourceLines[0].Text=="مرحبا بكم","RTL source-line transcript remains in the same logical order");
        });
        Run("Same-baseline labels remain separate",()=>{
            var page=new OcrPage();page.Words.AddRange(new[]{Word("label one",0,20,20,60),Word("label two",1,120,20.2,60)});
            var regions=ImageTranslation.Regions(page);
            Check(regions.Count==2&&regions.All(r=>r.Lines==1),"neighbour labels on one baseline are not joined into a paragraph");
        });
        Run("Adjacent geometric paragraph barrier",()=>{
            var page=new OcrPage();page.Words.AddRange(new[]{Word("same top",0,20,20),Word("다른 언어",1,20,39),Word("same bottom",2,20,58)});
            var regions=ImageTranslation.Regions(page);
            Check(regions.Count==3,"matching language cannot jump over an intervening geometric segment");
        });
        Run("Long unbreakable token remains accessible",()=>{
            var box=new Rect(20,20,65,16);var source=Source(320,180,Tuple.Create(box,Colors.DarkRed));
            string token="https://example.invalid/"+new string('W',256)+"?id=20261016";
            var result=ImageTranslation.RenderWithStatus(source,new[]{Region(box)},new[]{token});Common(source,result,"long token");
            Check(result.UnplacedCount==1&&result.Transcript==token,"long token falls back explicitly without truncation or ellipsis");
            Unchanged(source,result.Image,"long token");
        });
        Run("Explicit newline transcript and full fit",()=>{
            var source=Source();var box=new Rect(20,20,210,100);string text="First translated line\nSecond translated line";
            var result=ImageTranslation.RenderWithStatus(source,new[]{Region(box)},new[]{text});Common(source,result,"newlines");
            Check(result.UnplacedCount==0&&result.Transcript==text,"short explicit newline content fits and transcript preserves line breaks");
            Check(!Pixels(source).SequenceEqual(Pixels(result.Image)),"placed multiline text actually changes image pixels");
            var allowed=box;allowed.Inflate(2,2);OutsideUnchanged(source,result.Image,new[]{allowed},"multiline fit");
            Png(result.Image,"multiline-fit.png");
        });
        Run("Null and empty translation entries",()=>{
            var source=Source(320,180,Tuple.Create(new Rect(20,20,100,20),Colors.DarkRed));
            var regions=new[]{Region(new Rect(20,20,100,20)),Region(new Rect(20,60,100,20))};
            var result=ImageTranslation.RenderWithStatus(source,regions,new string[]{null,""});Common(source,result,"empty entries");
            Check(string.IsNullOrWhiteSpace(result.Transcript),"empty translation entries do not manufacture transcript text");
            Check(result.UnplacedCount==2,"both missing translation entries are explicitly reported unplaced");
            Unchanged(source,result.Image,"empty entries");
        });
        Run("Too-small source box safe fallback",()=>{
            var source=Source();string text="完整译文必须保留";
            var result=ImageTranslation.RenderWithStatus(source,new[]{Region(new Rect(20,20,2,2))},new[]{text});Common(source,result,"small box");
            Check(result.UnplacedCount==1&&result.Transcript==text,"tiny box does not shrink readable text to an illegible clipped remnant");
            Unchanged(source,result.Image,"small box");
        });
        Run("Entirely out-of-image region",()=>{
            var source=Source();string text="outside transcript";
            var result=ImageTranslation.RenderWithStatus(source,new[]{Region(new Rect(-100,-100,20,20))},new[]{text});Common(source,result,"outside box");
            Check(result.UnplacedCount==1&&result.Transcript==text,"off-image content remains in transcript with explicit fallback");
            Unchanged(source,result.Image,"outside box");
        });
        Run("Partially out-of-image region",()=>{
            var source=Source();string text="edge text";
            var result=ImageTranslation.RenderWithStatus(source,new[]{Region(new Rect(-5,20,100,24))},new[]{text});Common(source,result,"edge box");
            Check(result.Transcript==text,"partly clipped source geometry retains full transcript");
            OutsideUnchanged(source,result.Image,new[]{new Rect(0,18,97,28)},"edge box");
        });
        Run("Empty source geometry",()=>{
            var source=Source();string text="empty geometry transcript";
            var result=ImageTranslation.RenderWithStatus(source,new[]{Region(Rect.Empty)},new[]{text});Common(source,result,"empty geometry");
            Check(result.UnplacedCount==1&&result.Transcript==text,"empty geometry is reported rather than silently dropping translation");
            Unchanged(source,result.Image,"empty geometry");
        });
        Run("Conflicting regions retain all text",()=>{
            var source=Source(320,180,Tuple.Create(new Rect(20,20,130,28),Colors.DarkRed));var box=new Rect(20,20,130,28);
            string first="First conflicting text",second="Second conflicting text";
            var result=ImageTranslation.RenderWithStatus(source,new[]{Region(box),Region(box)},new[]{first,second});Common(source,result,"conflicting boxes");
            Check(result.UnplacedCount==2&&result.Transcript.Contains(first)&&result.Transcript.Contains(second),"both conflicts are explicit and both full strings remain recoverable");
            Unchanged(source,result.Image,"conflicting boxes");
            var allowed=box;allowed.Inflate(2,2);OutsideUnchanged(source,result.Image,new[]{allowed},"conflicting boxes");
        });
        Run("Padded paint conflict across three-pixel source gap",()=>{
            var source=Source(320,180,Tuple.Create(new Rect(20,20,100,24),Colors.DarkRed),Tuple.Create(new Rect(123,20,100,24),Colors.DarkBlue));
            var regions=new[]{Region(new Rect(20,20,100,24)),Region(new Rect(123,20,100,24))};var text=new[]{"one","two"};
            Check(ImageTranslation.RenderWithStatus(source,new[]{regions[0]},new[]{text[0]}).UnplacedCount==0&&ImageTranslation.RenderWithStatus(source,new[]{regions[1]},new[]{text[1]}).UnplacedCount==0,"both three-pixel-separated labels fit when isolated");
            var result=ImageTranslation.RenderWithStatus(source,regions,text);Common(source,result,"padded paint conflict");
            Check(result.UnplacedCount==2&&result.Transcript=="one\n\ntwo","overlapping padded paint masks reject both even when original source boxes do not overlap");
            Unchanged(source,result.Image,"padded paint conflict");
        });
        Run("Merely touching padded paint footprints are not overlap",()=>{
            var source=Source();var regions=new[]{Region(new Rect(20,20,100,24)),Region(new Rect(124,20,100,24))};
            var result=ImageTranslation.RenderWithStatus(source,regions,new[]{"one","two"});Common(source,result,"touching padded footprints");
            Check(result.UnplacedCount==0,"zero-area contact between padded footprints does not reject otherwise fitting labels");
            var allowed=regions.Select(r=>{var box=r.Box;box.Inflate(2,2);return box;}).ToArray();OutsideUnchanged(source,result.Image,allowed,"touching padded footprints");
        });
        Run("Missing region retains translation",()=>{
            var source=Source();string text="translation without valid geometry";
            var result=ImageTranslation.RenderWithStatus(source,new TranslationRegion[]{null},new[]{text});Common(source,result,"null region entry");
            Check(result.UnplacedCount==1&&result.Transcript==text,"missing region is explicit and full translation remains accessible");
            Unchanged(source,result.Image,"null region entry");
        });
        Run("Transparent fallback preserves hidden RGB",()=>{
            var pixels=new byte[40*30*4];for(int n=0;n<pixels.Length;n+=4){pixels[n]=121;pixels[n+1]=57;pixels[n+2]=241;pixels[n+3]=(byte)((n/4)%255);}
            var source=BitmapSource.Create(40,30,96,96,PixelFormats.Bgra32,null,pixels,40*4);source.Freeze();
            var result=ImageTranslation.RenderWithStatus(source,new[]{Region(new Rect(10,10,2,2))},new[]{"unplaceable text"});Common(source,result,"transparent fallback");
            Check(result.UnplacedCount==1&&object.ReferenceEquals(source,result.Image),"all-fallback returns original transparent bitmap without recomposition");
            Unchanged(source,result.Image,"transparent fallback");
        });
        Run("Checkerboard pictorial background stays untouched",()=>{
            const int width=320,height=180;var pixels=new byte[width*height*4];
            for(int y=0;y<height;y++)for(int x=0;x<width;x++){int n=(y*width+x)*4;byte c=(byte)(((x/8+y/8)%2==0)?255:0);pixels[n]=pixels[n+1]=pixels[n+2]=c;pixels[n+3]=255;}
            var source=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);source.Freeze();string text="完整译文仍在，图片不应被纯色覆盖。";
            var result=ImageTranslation.RenderWithStatus(source,new[]{Region(new Rect(20,20,160,36))},new[]{text});Common(source,result,"checkerboard background");
            Check(result.UnplacedCount==1&&result.Transcript==text,"visibly mixed checkerboard boundary falls back with complete translated text");
            Unchanged(source,result.Image,"checkerboard background");
        });
        Run("High-contrast detail crossing source boundary stays untouched",()=>{
            var source=Source(320,180,Tuple.Create(new Rect(85,0,60,180),Colors.Black));string text="请保留穿过文字区域的物体。";
            var result=ImageTranslation.RenderWithStatus(source,new[]{Region(new Rect(20,30,160,32))},new[]{text});Common(source,result,"cross-boundary object");
            Check(result.UnplacedCount==1&&result.Transcript==text,"high-contrast object across text-box boundary is not flattened into a dominant color");
            Unchanged(source,result.Image,"cross-boundary object");
        });
        Run("Per-source-line foreground colors",()=>{
            var page=new OcrPage();page.Words.AddRange(new[]{Word("red source",0,20,20,170,22),Word("blue source",1,20,45,170,22)});
            var regions=ImageTranslation.Regions(page);Check(regions.Count==1&&regions[0].SourceLines.Count==2,"colored source lines group with two preserved geometries");
            var source=Source(320,180,Tuple.Create(page.Words[0].Box,Colors.Red),Tuple.Create(page.Words[1].Box,Colors.Blue));
            string translated="第一行红字\n第二行蓝字";var result=ImageTranslation.RenderWithStatus(source,regions,new[]{translated});Common(source,result,"line colors");
            Check(result.UnplacedCount==0&&result.Transcript==translated,"matching two-line translation is placed completely");
            Check(!Pixels(source).SequenceEqual(Pixels(result.Image)),"per-line color test performs actual replacement, not fallback");
            Check(ColorCount(result.Image,page.Words[0].Box,true)>15&&ColorCount(result.Image,page.Words[0].Box,false)==0,"first line retains red rather than paragraph-wide color");
            Check(ColorCount(result.Image,page.Words[1].Box,false)>15&&ColorCount(result.Image,page.Words[1].Box,true)==0,"second line retains blue rather than paragraph-wide color");
            var allowed=regions[0].Box;allowed.Inflate(2,2);OutsideUnchanged(source,result.Image,new[]{allowed},"line colors");
            Png(source,"source-line-colors.png");Png(result.Image,"translated-line-colors.png");
        });
        Run("Legacy Render wrapper compatibility",()=>{
            var source=Source();var regions=new[]{Region(new Rect(20,20,180,28))};var text=new[]{"short translated text"};
            var status=ImageTranslation.RenderWithStatus(source,regions,text);var legacy=ImageTranslation.Render(source,regions,text);
            Check(Pixels(status.Image).SequenceEqual(Pixels(legacy)),"old Render API matches status renderer pixels exactly");
        });
        Run("Empty region list",()=>{
            var source=Source();var result=ImageTranslation.RenderWithStatus(source,new TranslationRegion[0],new string[0]);Common(source,result,"empty list");
            Check(result.UnplacedCount==0&&result.Transcript=="","empty list has no missing content or invented transcript");Unchanged(source,result.Image,"empty list");
        });
        Run("Invalid argument contract",()=>{
            var source=Source();var regions=new[]{Region(new Rect(20,20,80,20))};
            Throws<ArgumentNullException>(()=>ImageTranslation.RenderWithStatus(null,regions,new[]{"text"}),"null original is a clear argument error");
            Throws<ArgumentNullException>(()=>ImageTranslation.RenderWithStatus(source,null,new[]{"text"}),"null region list is a clear argument error");
            Throws<ArgumentNullException>(()=>ImageTranslation.RenderWithStatus(source,regions,null),"null translation list is a clear argument error");
            Throws<InvalidOperationException>(()=>ImageTranslation.RenderWithStatus(source,regions,new string[0]),"mismatched regions and text cannot silently discard content");
        });
    }
    [STAThread]
    static int Main(string[] args){
        try{
            Console.OutputEncoding=new UTF8Encoding(false);
            if(args.Length!=1||!Path.IsPathRooted(args[0]))throw new ArgumentException("Prepared absolute sandbox required.");
            sandbox=Path.GetFullPath(args[0]);if(!File.Exists(Path.Combine(sandbox,".image-translation-layout-sandbox")))throw new InvalidOperationException("Missing isolated sandbox marker.");
            Tests();Console.WriteLine("PASS "+checks+" assertions in "+cases+" offscreen deterministic cases");
            Console.WriteLine("SAFETY: synthetic pixels and text only; no OCR, network, window/Application, AppState, settings, clipboard, desktop, real image/data or application entry point.");return 0;
        }catch(Exception error){Console.Error.WriteLine("FAIL after "+checks+" assertions: "+error);return 1;}
    }
}

namespace QingJie {
    public sealed class WordBox{public string Text;public Rect Box;public int Line,TextStart;}
    public sealed class OcrPage{public string Text;public List<WordBox> Words=new List<WordBox>();}
    public static class Ui{public static Brush Ink{get{return Brushes.Black;}}}
    public static class Geometry {
        public static bool IsHangul(char c){return (c>='\uac00'&&c<='\ud7a3')||(c>='\u1100'&&c<='\u11ff')||(c>='\u3130'&&c<='\u318f');}
        public static bool NeedsWordSpace(char previous,char next){return (previous<0x2e80&&next<0x2e80)||IsHangul(previous)||IsHangul(next);}
        public static string JoinWords(IEnumerable<WordBox> words){
            var result=new StringBuilder();int line=-1;foreach(var word in words){if(result.Length>0){if(line!=word.Line)result.AppendLine();else if(word.Text.Length>0&&NeedsWordSpace(result[result.Length-1],word.Text[0]))result.Append(' ');}result.Append(word.Text);line=word.Line;}return result.ToString();
        }
    }
    // Exact script-only SourceLanguage behavior; intentionally no provider API.
    public static class Translation {
        public static string SourceLanguage(string text){
            if(text.Count(Geometry.IsHangul)>=2)return "ko";
            if(text.Count(c=>OcrLanguagePacks.IsScript(c,"jpn"))>=2)return "ja";
            if(text.Count(c=>OcrLanguagePacks.IsScript(c,"rus"))>=2)return "ru";
            if(text.Count(c=>OcrLanguagePacks.IsScript(c,"ara"))>=2)return "ar";
            if(text.Count(c=>OcrLanguagePacks.IsScript(c,"tha"))>=2)return "th";return "auto";
        }
    }
    public static class OcrLanguagePacks {
        public static bool IsScript(char c,string id){
            if(id=="jpn")return c>='\u3040'&&c<='\u30ff';if(id=="rus")return c>='\u0400'&&c<='\u052f';
            if(id=="ara")return (c>='\u0600'&&c<='\u06ff')||(c>='\ufb50'&&c<='\ufeff');if(id=="tha")return c>='\u0e00'&&c<='\u0e7f';return false;
        }
    }
}
#endif
