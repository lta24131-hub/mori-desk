#if OCR_CONFIDENCE_TESTS
// Compile as QingJie.Tests.exe against the current Mori.exe. This runner opens
// no windows, touches no application data and loads no recognition models.
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using QingJie;

public static class OcrConfidenceTests {
    static int checks;
    static void Check(bool condition,string message){checks++;if(!condition)throw new InvalidOperationException(message);Console.WriteLine("PASS "+message);}
    static void Reject<T>(Action action,string message) where T:Exception {
        bool failed=false;try{action();}catch(T){failed=true;}Check(failed,message);
    }
    static WordBox Word(string text,int index,double confidence,OcrConfidenceSource source){return new WordBox{Text=text,Line=0,Box=new Rect(index*60,10,50,20),Confidence=confidence,ConfidenceSource=source};}
    static OcrPage Read(Action<BinaryWriter> write){
        using(var bytes=new MemoryStream()){
            using(var writer=new BinaryWriter(bytes,Encoding.UTF8,true)){write(writer);writer.Flush();}
            bytes.Position=0;using(var reader=new BinaryReader(bytes,Encoding.UTF8,true))return OcrWorker.ReadResult(reader);
        }
    }
    static void Header(BinaryWriter writer,int protocol){writer.Write(protocol);writer.Write("");writer.Write("");writer.Write(1);}
    static void RawWord(BinaryWriter writer){writer.Write("word");writer.Write(0);writer.Write(1d);writer.Write(2d);writer.Write(30d);writer.Write(12d);}
    static OcrPage RawLocal(double score,int source){return Read(writer=>{Header(writer,0x514A4F34);RawWord(writer);writer.Write(score);writer.Write(source);});}
    static OcrPage RawModern(double score,string prefix=""){return Read(writer=>{Header(writer,0x514A4F33);RawWord(writer);writer.Write(prefix);writer.Write(score);writer.Write(0);});}
    static void ProtocolTests(){
        Check(new WordBox().Confidence==1&&new WordBox().ConfidenceSource==OcrConfidenceSource.Unknown,"an omitted score remains explicitly unknown even when its legacy numeric default is one");
        var page=new OcrPage{Notice="test notice"};
        page.Words.Add(Word("Windows",0,1,OcrConfidenceSource.Unknown));
        page.Words.Add(Word("model",1,.804599940776825,OcrConfidenceSource.Model));
        page.Words.Add(Word("edited",2,1,OcrConfidenceSource.User));
        OcrService.IndexText(page);
        var received=Read(writer=>OcrWorker.WriteResult(writer,page));
        Check(received.Words.Select(w=>w.ConfidenceSource).SequenceEqual(new[]{OcrConfidenceSource.Unknown,OcrConfidenceSource.Model,OcrConfidenceSource.User}),"fallback worker round trip retains unknown, model and user provenance independently");
        Check(received.Words[1].Confidence==page.Words[1].Confidence,"fallback worker transmits the actual model score without rounding");
        Check(received.Text=="Windows model edited"&&received.Words[1].TextStart==8&&received.Words.All(w=>w.Prefix==null),"fallback metadata preserves legacy inferred spaces and source text offsets");
        Check(received.Notice==page.Notice&&received.Words[2].Box==page.Words[2].Box,"fallback metadata does not change notices or geometry");
        var legacy=Read(writer=>{Header(writer,0x514A4F32);RawWord(writer);});
        Check(legacy.Words[0].ConfidenceSource==OcrConfidenceSource.Unknown&&legacy.Words[0].Confidence==1,"old worker protocol is readable without inventing model confidence");
        var modern=RawModern(.804599940776825," ");
        Check(modern.Words[0].ConfidenceSource==OcrConfidenceSource.Model&&modern.Words[0].Confidence==.804599940776825&&modern.Words[0].Prefix==" ","structured RapidOCR result retains its score and prefix and is marked as model evidence");
        foreach(double score in new[]{0d,1d}){
            Check(RawLocal(score,(int)OcrConfidenceSource.Model).Words[0].Confidence==score,"local model boundary score is valid: "+score);
            Check(RawModern(score).Words[0].ConfidenceSource==OcrConfidenceSource.Model,"structured model boundary score remains explicit model evidence: "+score);
        }
        foreach(double bad in new[]{double.NaN,double.NegativeInfinity,double.PositiveInfinity,-.001,1.001}){
            Reject<InvalidDataException>(()=>RawLocal(bad,(int)OcrConfidenceSource.Model),"local protocol rejects invalid confidence: "+bad);
            Reject<InvalidDataException>(()=>RawModern(bad),"structured protocol rejects invalid confidence: "+bad);
        }
        foreach(int bad in new[]{-1,3,int.MaxValue})Reject<InvalidDataException>(()=>RawLocal(.8,bad),"local protocol rejects unknown provenance enum: "+bad);
        Reject<InvalidDataException>(()=>RawModern(.8,new string(' ',1001)),"structured protocol retains its prefix length bound");
        Reject<InvalidDataException>(()=>Read(writer=>writer.Write(0x514A4F35)),"unrecognized protocol fails before reading a result");
        Reject<EndOfStreamException>(()=>Read(writer=>{Header(writer,0x514A4F34);RawWord(writer);writer.Write(.8);}),"a truncated source flag cannot silently become Unknown");
        Reject<InvalidOperationException>(()=>Read(writer=>{writer.Write(0x514A4F32);writer.Write("worker failed");}),"legacy worker errors still propagate before reading success metadata");
    }
    static void CopyAndCorrectionTests(){
        var page=new OcrPage();
        page.Words.Add(Word("Al",0,.8,OcrConfidenceSource.Model));
        page.Words.Add(Word("remains",1,1,OcrConfidenceSource.Unknown));
        foreach(var word in page.Words){
            var region=new TranslationRegion{Text=word.Text,Box=word.Box};
            region.SourceLines.Add(new TranslationSourceLine{Text=word.Text,Box=word.Box});page.TranslationRegions.Add(region);
        }
        OcrService.IndexText(page);
        var copied=OcrResultCache.Copy(page);
        Check(copied.Words[0].ConfidenceSource==OcrConfidenceSource.Model&&copied.Words[0].Confidence==.8&&copied.Words[1].ConfidenceSource==OcrConfidenceSource.Unknown,"copy retains both model evidence and unknown provenance");
        copied.Words[0].ConfidenceSource=OcrConfidenceSource.User;copied.Words[0].Text="changed";
        Check(page.Words[0].ConfidenceSource==OcrConfidenceSource.Model&&page.Words[0].Text=="Al","mutating a copy cannot overwrite shared OCR evidence");
        var corrected=OcrCorrection.Apply(page,0,"AI");
        Check(corrected.Words[0].Text=="AI"&&corrected.Words[0].ConfidenceSource==OcrConfidenceSource.User,"manual correction is explicit user-confirmed text");
        Check(corrected.Words[0].Box==page.Words[0].Box&&corrected.TranslationRegions[0].SourceLines[0].Box==page.TranslationRegions[0].SourceLines[0].Box,"manual correction keeps source geometry for image layout");
        Check(corrected.Words[1].ConfidenceSource==OcrConfidenceSource.Unknown&&corrected.Words[1].Text=="remains","editing one region does not confirm unrelated OCR text");
        Check(page.Text=="Al remains"&&page.Words[0].ConfidenceSource==OcrConfidenceSource.Model&&page.TranslationRegions[0].Text=="Al","manual correction never mutates the original OCR page or regions");
        var cache=new OcrResultCache(()=>0);cache.Put("original",page);cache.Put("edited",corrected);
        var originalHit=cache.Get("original");var editedHit=cache.Get("edited");
        Check(originalHit.Words[0].ConfidenceSource==OcrConfidenceSource.Model&&editedHit.Words[0].ConfidenceSource==OcrConfidenceSource.User,"cache independently preserves original model evidence and edited user evidence");
        editedHit.Words[0].ConfidenceSource=OcrConfidenceSource.Unknown;editedHit.Words[0].Text="changed";
        Check(cache.Get("edited").Words[0].ConfidenceSource==OcrConfidenceSource.User&&cache.Get("edited").Words[0].Text=="AI","returned cache copies cannot erase user confirmation in stored results");
    }
    public static int Main(){
        try{ProtocolTests();CopyAndCorrectionTests();Console.WriteLine("PASS "+checks+" OCR confidence checks; no network, windows or models");return 0;}
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
#endif
