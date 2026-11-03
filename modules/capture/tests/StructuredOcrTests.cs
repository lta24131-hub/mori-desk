using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using QingJie;

internal static class StructuredOcrTests {
    static void Box(BinaryWriter writer,double x=10){writer.Write(x);writer.Write(20d);writer.Write(40d);writer.Write(16d);}
    static byte[] Reply(int magic=0x514A4F33,double x=10,double confidence=.9,int regions=1){
        using(var memory=new MemoryStream())using(var writer=new BinaryWriter(memory,Encoding.UTF8,true)){
            writer.Write(magic);writer.Write("");writer.Write("");writer.Write(2);
            writer.Write("版本");writer.Write(0);Box(writer,x);writer.Write("");writer.Write(confidence);
            writer.Write("0.2.0");writer.Write(0);Box(writer,50);writer.Write(" ");writer.Write(.8);
            writer.Write(regions);
            if(regions==1){writer.Write("版本 0.2.0");Box(writer);writer.Write(1);writer.Write("版本 0.2.0");Box(writer);}
            writer.Flush();return memory.ToArray();
        }
    }
    static OcrPage Read(byte[] bytes){using(var stream=new MemoryStream(bytes))using(var reader=new BinaryReader(stream))return OcrWorker.ReadResult(reader);}
    internal static void Run(Action<bool,string> check){
        Corrections(check);
        var page=Read(Reply());
        check(page.Text=="版本 0.2.0"&&page.Words[1].TextStart==3,"structured OCR preserves Chinese/Latin spacing and selection offsets");
        check(page.TranslationRegions.Single().Text==page.Text&&page.TranslationRegions[0].SourceLines.Single().Box==new Rect(10,20,40,16),"worker paragraph text and line coordinates reach translation without regrouping");
        check(page.Words[0].Confidence==.9&&page.Words[1].Prefix==" ","worker confidence and exact word separators survive transport");
        var cache=new OcrResultCache();cache.Put("model-a",page);page.TranslationRegions[0].SourceLines[0].Text="changed";page.Words[0].Prefix="bad";
        var copy=cache.Get("model-a");copy.TranslationRegions[0].Text="changed";copy.Words[0].Confidence=0;
        var intact=cache.Get("model-a");
        check(intact.TranslationRegions[0].Text=="版本 0.2.0"&&intact.TranslationRegions[0].SourceLines[0].Text=="版本 0.2.0"&&intact.Words[0].Prefix==""&&intact.Words[0].Confidence==.9&&cache.Get("model-b")==null,"cached paragraphs, source lines, prefixes and confidence are isolated from caller mutation and model identity");
        foreach(var bytes in new[]{Reply(123),Reply(x:double.NaN),Reply(x:double.PositiveInfinity),Reply(confidence:double.NaN),Reply(confidence:2),Reply(regions:10001),Reply().Take(20).ToArray()}){
            bool failed=false;try{Read(bytes);}catch(InvalidDataException){failed=true;}catch(EndOfStreamException){failed=true;}
            check(failed,"malformed OCR transport fails instead of drawing invalid/misaligned text");
        }
        using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream,Encoding.UTF8,true)){
            OcrWorker.WriteResult(writer,new OcrPage{Words=page.Words});writer.Flush();
            var legacy=Read(stream.ToArray());check(legacy.Words.Count==2&&legacy.TranslationRegions.Count==0,"optional legacy-language worker remains protocol-compatible");
        }
    }
    static void Corrections(Action<bool,string> check){
        var source=new OcrPage();
        source.Words.Add(new WordBox{Text="No Al for now",Box=new Rect(10,10,120,18),Line=0});
        source.Words.Add(new WordBox{Text="The built-in engines still show you everything they find.",Box=new Rect(10,50,450,18),Line=1});
        OcrService.IndexText(source);source.TranslationRegions=ImageTranslation.Regions(source);
        var corrected=OcrCorrection.Apply(source,0,"No AI for now");
        check(corrected.Text.StartsWith("No AI for now")&&corrected.TranslationRegions[0].Text=="No AI for now"&&source.Text.StartsWith("No Al for now"),"manual OCR correction updates translation input without mutating the source or shared cache");
        check(corrected.Words[0].Box==source.Words[0].Box&&corrected.Words[1].Box==source.Words[1].Box&&corrected.Words[1].Text==source.Words[1].Text,"correcting a paragraph preserves its source geometry and all neighbouring content");
        corrected=OcrCorrection.Apply(corrected,0,"A longer corrected title");
        check(corrected.Text.Substring(corrected.Words[1].TextStart)==source.Words[1].Text&&OcrCorrection.FindRegion(corrected,corrected.Words[0].TextStart,3)==0,"repeated and length-changing corrections retain accurate selection offsets");
        check(OcrCorrection.FindRegion(corrected,0,corrected.Text.Length)==-1,"cross-paragraph selection never silently edits only part of a selection");
        foreach(string invalid in new[]{"", "  ",new string('x',16001)}){bool rejected=false;try{OcrCorrection.Apply(source,0,invalid);}catch(ArgumentException){rejected=true;}check(rejected&&source.Text.StartsWith("No Al"),"invalid correction is rejected without losing original recognition");}
        var pixels=new byte[600*100*4];var image=System.Windows.Media.Imaging.BitmapSource.Create(600,100,96,96,System.Windows.Media.PixelFormats.Bgra32,null,pixels,2400);image.Freeze();
        var window=new TextWindow(image,"zh",source,new ImageTranslationResult{Image=image,Transcript="old translation",UnplacedCount=0},Translation.Provider);
        try{window.CorrectRegion(0,"No AI for now");var current=window.EnsureOcr().GetAwaiter().GetResult();
            var field=typeof(TextWindow).GetField("translatedImage",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
            check(current.Text.StartsWith("No AI for now")&&field.GetValue(window)==null,"correction invalidates the visible old translation and reuses corrected OCR without another engine run");
        }finally{window.Close();}
    }
}
