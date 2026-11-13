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
    static Rect DarkBounds(BitmapSource image,Rect box){
        var pixels=Pixels(image);var bounds=Rect.Empty;
        for(int y=(int)box.Top;y<box.Bottom;y++)for(int x=(int)box.Left;x<box.Right;x++){
            int i=(y*image.PixelWidth+x)*4;
            if(pixels[i]<100&&pixels[i+1]<100&&pixels[i+2]<100)bounds.Union(new Rect(x,y,1,1));
        }
        return bounds;
    }
    static void Throws<T>(Action action,string name) where T:Exception{bool caught=false;try{action();}catch(T){caught=true;}Check(caught,name);}
    static void Tests(){
        Check(!typeof(ImageTranslationLayoutTests).Assembly.GetReferencedAssemblies().Any(a=>a.Name=="Tesseract"||a.Name=="Mori"||a.Name=="System.Net.Http"||a.Name=="PresentationFramework"),
            "standalone host references no OCR, application, network client or window UI assembly");
        Run("Use verified blank space for readable single-line translation",()=>{
            var box=new Rect(20,20,75,26);var source=Source(240,100,Tuple.Create(box,Colors.Black));
            var blocked=Source(240,100,Tuple.Create(box,Colors.Black),Tuple.Create(new Rect(103,18,30,30),Colors.Blue));
            var open=ImageTranslation.RenderWithStatus(source,new[]{Region(box)},new[]{"打开所有设置项"});
            var limited=ImageTranslation.RenderWithStatus(blocked,new[]{Region(box)},new[]{"打开所有设置项"});
            var a=DarkBounds(open.Image,new Rect(18,18,120,30));var b=DarkBounds(limited.Image,new Rect(18,18,83,30));
            Check(open.UnplacedCount==0&&limited.UnplacedCount==0,"both layouts retain the complete translation");
            Check(a.Height>b.Height&&a.Right>103,"safe blank space produces observably larger text instead of a tiny label");
            Check(Math.Abs(a.Left-b.Left)<=1,"readable label keeps its original left alignment");
            OutsideUnchanged(blocked,limited.Image,new[]{new Rect(14,18,87,30)},"unrecognized coloured content blocks expansion");
            var neighbour=Region(new Rect(104,20,40,26),"AI");
            var protectedResult=ImageTranslation.RenderWithStatus(source,new[]{Region(box),neighbour},new[]{"打开所有设置项","AI"});
            OutsideUnchanged(source,protectedResult.Image,new[]{new Rect(14,18,85.5,30)},"adjacent OCR label blocks expansion");
            var diagonal=Region(new Rect(103,6,60,16),"AI");
            var diagonalResult=ImageTranslation.RenderWithStatus(source,new[]{Region(box),diagonal},new[]{"打开所有设置项","AI"});
            OutsideUnchanged(source,diagonalResult.Image,new[]{new Rect(14,18,87,30)},"diagonal neighbour also blocks same-height expansion");
            Png(open.Image,"readable-blank-space.png");Png(limited.Image,"readable-protected-art.png");
        });
        Run("Unfittable translation cannot erase unrecognized artwork",()=>{
            var box=new Rect(40,40,50,18);var art=new Rect(98,35,10,24);
            var source=Source(240,100,Tuple.Create(box,Colors.Black),Tuple.Create(art,Colors.Blue));
            var result=ImageTranslation.RenderWithStatus(source,new[]{Region(box)},new[]{"打开设置并保存更改"});
            Check(result.Transcript=="打开设置并保存更改","unfittable source box retains full transcript");
            Check(ColorCount(result.Image,art,false)==ColorCount(source,art,false),"expanded fallback never erases unrecognized artwork");
            Check(result.UnplacedCount==0,"verified empty lower space still allows the complete translation");
            Png(result.Image,"safe-expanded-fallback.png");
        });
        Run("Lower unrecognized content blocks fallback growth",()=>{
            var box=new Rect(40,40,50,18);var rightArt=new Rect(98,35,10,24);var lowerArt=new Rect(36,62,58,6);
            var source=Source(240,100,Tuple.Create(box,Colors.Black),Tuple.Create(rightArt,Colors.Blue),Tuple.Create(lowerArt,Colors.Blue));
            const string text="打开设置并保存更改";
            var result=ImageTranslation.RenderWithStatus(source,new[]{Region(box)},new[]{text});
            Check(result.UnplacedCount==1&&result.Transcript==text,"blocked right and lower space report a complete transcript fallback");
            Check(ReferenceEquals(source,result.Image),"blocked expansion returns the original bitmap");
            Unchanged(source,result.Image,"unrecognized lower artwork");
        });
        Run("Transparent lower pixels block otherwise white fallback space",()=>{
            const int width=240,height=100;var box=new Rect(40,40,50,18);var rightArt=new Rect(98,35,10,24);
            var pixels=Pixels(Source(width,height,Tuple.Create(box,Colors.Black),Tuple.Create(rightArt,Colors.Blue)));
            // Preserve white RGB so alpha alone must stop the blank-space scan.
            for(int y=62;y<68;y++)for(int x=36;x<94;x++)pixels[(y*width+x)*4+3]=128;
            var source=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);source.Freeze();
            const string text="打开设置并保存更改";
            var result=ImageTranslation.RenderWithStatus(source,new[]{Region(box)},new[]{text});
            Check(result.UnplacedCount==1&&result.Transcript==text,"semi-transparent lower space is not treated as opaque blank background");
            Check(ReferenceEquals(source,result.Image),"transparent blocked fallback avoids bitmap recomposition");
            Unchanged(source,result.Image,"semi-transparent lower pixels");
        });
        Run("Lower unchanged OCR label blocks fallback growth",()=>{
            var box=new Rect(40,40,50,18);var rightArt=new Rect(98,35,10,24);
            var source=Source(240,100,Tuple.Create(box,Colors.Black),Tuple.Create(rightArt,Colors.Blue));
            var neighbour=Region(new Rect(36,62,58,12),"AI");
            // The neighbouring pixels are white: geometry must protect the OCR
            // label even when its ink is too faint or absent in this fixture.
            const string text="打开设置并保存更改";
            var result=ImageTranslation.RenderWithStatus(source,new[]{Region(box),neighbour},new[]{text,"AI"});
            Check(result.UnplacedCount==1&&result.Transcript==text+"\n\nAI","only the blocked changed region is unplaced while the neighbouring label stays valid");
            Check(ReferenceEquals(source,result.Image),"lower OCR protection prevents a speculative repaint");
            Unchanged(source,result.Image,"unchanged lower OCR label");
        });
        Run("Lower-right obstacle still permits original-width fallback",()=>{
            var box=new Rect(40,40,50,18);var art=new Rect(98,61,10,6);
            var source=Source(240,100,Tuple.Create(box,Colors.Black),Tuple.Create(art,Colors.Blue));
            const string text="打开设置并保存更改";
            var result=ImageTranslation.RenderWithStatus(source,new[]{Region(box)},new[]{text});
            Check(result.Transcript==text,"lower-right obstacle retains the full transcript");
            Check(ColorCount(result.Image,art,false)==ColorCount(source,art,false),"lower-right artwork is never erased to make the translation fit");
            Check(result.UnplacedCount==0,"safe original-width lower space is used when a wider lower rectangle is blocked");
            OutsideUnchanged(source,result.Image,new[]{new Rect(36,38,58,28)},"original-width lower fallback");
            Png(result.Image,"safe-narrow-lower-fallback.png");
        });
        Run("Unchanged regions preserve status without a repaint",()=>{
            var source=Source();var same=Region(new Rect(20,20,30,20),"AI");
            var invalid=Region(new Rect(-60,-60,20,20),"PDF");
            var result=ImageTranslation.RenderWithStatus(source,new[]{same,invalid,null},new[]{"AI","PDF",""});
            Check(ReferenceEquals(source,result.Image)&&result.UnplacedCount==2,"no-paint result keeps original image and invalid-region counts");
            Check(result.Transcript=="AI\n\nPDF\n\n","no-paint result retains all transcript entries");
        });
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
            var allowed=box;allowed.Inflate(8,2);OutsideUnchanged(source,result.Image,new[]{allowed},"multiline fit");
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
            OutsideUnchanged(source,result.Image,new[]{new Rect(0,18,103,28)},"edge box");
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
        Run("Unequal paragraphs with slightly overlapping OCR edges",()=>{
            var stripe=new Rect(200,99,30,4);
            var source=Source(320,200,Tuple.Create(stripe,Colors.Black));
            var regions=new[]{Region(new Rect(20,20,250,100)),Region(new Rect(20,118,250,42))};
            var result=ImageTranslation.RenderWithStatus(source,regions,new[]{"one","two"});
            Check(result.UnplacedCount==0,"both unequal-height paragraphs remain readable");
            var pixels=Pixels(result.Image);bool cleared=true;
            for(int y=99;y<103;y++)for(int x=200;x<230;x++)for(int c=0;c<3;c++)if(pixels[(y*320+x)*4+c]!=255)cleared=false;
            Check(cleared,"near-overlap does not leave the preceding paragraph's last source line behind");
        });
        Run("Padded paint conflict across three-pixel source gap",()=>{
            var source=Source(320,180,Tuple.Create(new Rect(20,20,100,24),Colors.DarkRed),Tuple.Create(new Rect(123,20,100,24),Colors.DarkBlue));
            var regions=new[]{Region(new Rect(20,20,100,24)),Region(new Rect(123,20,100,24))};var text=new[]{"one","two"};
            Check(ImageTranslation.RenderWithStatus(source,new[]{regions[0]},new[]{text[0]}).UnplacedCount==0&&ImageTranslation.RenderWithStatus(source,new[]{regions[1]},new[]{text[1]}).UnplacedCount==0,"both three-pixel-separated labels fit when isolated");
            var result=ImageTranslation.RenderWithStatus(source,regions,text);Common(source,result,"padded paint conflict");
            Check(result.UnplacedCount==0&&result.Transcript=="one\n\ntwo","three-pixel gap is partitioned so both labels remain visible");
            Check(!Pixels(source).SequenceEqual(Pixels(result.Image)),"both adjacent labels are actually rendered");
        });
        Run("Merely touching padded paint footprints are not overlap",()=>{
            var source=Source();var regions=new[]{Region(new Rect(20,20,100,24)),Region(new Rect(124,20,100,24))};
            var result=ImageTranslation.RenderWithStatus(source,regions,new[]{"one","two"});Common(source,result,"touching padded footprints");
            Check(result.UnplacedCount==0,"zero-area contact between padded footprints does not reject otherwise fitting labels");
            var allowed=regions.Select(r=>{var box=r.Box;box.Inflate(8,2);return box;}).ToArray();OutsideUnchanged(source,result.Image,allowed,"touching padded footprints");
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
            Check(result.UnplacedCount==0&&result.Transcript==text,"mixed checkerboard background receives a complete readable caption");
            Check(!Pixels(source).SequenceEqual(Pixels(result.Image)),"mixed-background text is visible rather than silently skipped");
        });
        Run("High-contrast detail crossing source boundary stays untouched",()=>{
            var source=Source(320,180,Tuple.Create(new Rect(85,0,60,180),Colors.Black));string text="请保留穿过文字区域的物体。";
            var result=ImageTranslation.RenderWithStatus(source,new[]{Region(new Rect(20,30,160,32))},new[]{text});Common(source,result,"cross-boundary object");
            Check(result.UnplacedCount==0&&result.Transcript==text,"high-contrast background uses an explicit readable overlay");
            OutsideUnchanged(source,result.Image,new[]{new Rect(18,28,164,36)},"cross-boundary object");
        });
        Run("Ordinary wrapped paragraphs use uniform layout and clear their whole area",()=>{
            var first=new Rect(20,20,180,20);var second=new Rect(20,44,80,20);
            var region=Region(new Rect(20,20,180,44));region.Lines=2;
            region.SourceLines.Add(new TranslationSourceLine{Text="long source line",Box=first});
            region.SourceLines.Add(new TranslationSourceLine{Text="short source",Box=second});
            // Simulates an original glyph outside an undersized line box but
            // inside the full detected paragraph. It must not survive repaint.
            var remnant=new Rect(160,50,10,5);var source=Source(320,180,Tuple.Create(remnant,Colors.Black));
            string text="这是第一句很长的译文，需要自然换行。\n短句。";
            var result=ImageTranslation.RenderWithStatus(source,new[]{region},new[]{text});
            var uniform=ImageTranslation.RenderWithStatus(source,new[]{region},new[]{text.Replace('\n',' ')});
            Check(result.UnplacedCount==0&&result.Transcript==text,"physical OCR wraps preserve the complete transcript");
            Check(Pixels(result.Image).SequenceEqual(Pixels(uniform.Image)),"source wraps do not give the short line a different font size or independent paint box");
            var pixels=Pixels(result.Image);bool clear=true;
            for(int y=50;y<55;y++)for(int x=160;x<170;x++)for(int c=0;c<3;c++)if(pixels[(y*320+x)*4+c]!=255)clear=false;
            Check(clear,"ordinary paragraph repaint removes stray source glyphs beyond short line bounds");
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
            var allowed=regions[0].Box;allowed.Inflate(8,2);OutsideUnchanged(source,result.Image,new[]{allowed},"line colors");
            Png(source,"source-line-colors.png");Png(result.Image,"translated-line-colors.png");
        });
        Run("OCR underestimates final glyph edge",()=>{
            var box=new Rect(19,16.5,228.5,37);var remnant=new Rect(251,24,2,20);var neighbour=new Rect(259,20,20,30);
            var source=Source(320,80,Tuple.Create(remnant,Colors.Black),Tuple.Create(neighbour,Colors.Blue));
            var result=ImageTranslation.RenderWithStatus(source,new[]{Region(box),Region(neighbour,"preserved")},new[]{"中文","preserved"});
            Check(result.UnplacedCount==0,"underestimated OCR edge translates while adjacent unchanged region stays valid");
            var pixels=Pixels(result.Image);bool clear=true;
            for(int y=24;y<44;y++)for(int x=251;x<253;x++)for(int c=0;c<3;c++)if(pixels[(y*320+x)*4+c]!=255)clear=false;
            Check(clear,"final source glyph stem outside OCR box is fully erased instead of leaving a vertical bar");
            Check(ColorCount(result.Image,neighbour,false)==ColorCount(source,neighbour,false),"nearby preserved blue content is not erased by glyph-edge padding");
            var allowed=box;allowed.Inflate(8,2);OutsideUnchanged(source,result.Image,new[]{allowed},"bounded edge cleanup");
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
    public sealed class OcrPage{public string Text;public List<WordBox> Words=new List<WordBox>();public List<TranslationRegion> TranslationRegions=new List<TranslationRegion>();}
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
