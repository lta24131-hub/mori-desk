using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;

internal static class VerifiedLineOcrTests {
    static BitmapSource Image(string text,bool footnote=false,bool faintInline=false,double size=24){
        var visual=new DrawingVisual();
        using(var dc=visual.RenderOpen()){
            dc.DrawRectangle(Brushes.White,null,new Rect(0,0,340,80));
            dc.DrawText(new FormattedText(text,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),size,Brushes.Black,1),new Point(12,10));
            if(footnote)dc.DrawRectangle(Brushes.Gainsboro,null,new Rect(20,68,60,1));
            if(faintInline)dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(254,254,254)),null,new Rect(246,27,2,2));
        }
        var image=new RenderTargetBitmap(340,80,96,96,PixelFormats.Pbgra32);image.Render(visual);image.Freeze();return VerifiedLineOcr.OpaqueReference(image,false);
    }
    static WordBox[] Reading(string text){
        return text.Split(' ').Select(word=>new WordBox{Text=word,Line=0}).ToArray();
    }
    internal static void CheckRealCaseProof(string directory,Action<bool,string> check){
        var source=Snapshot.Load(Path.Combine(directory,"held-out-retry-real.png"));var events=new List<string>();
        var page=VerifiedLineOcr.TryReadObserved(source,CancellationToken.None,message=>{lock(events)events.Add(message);},10000).GetAwaiter().GetResult();
        check(page!=null&&page.Text=="Please try again."&&page.Words.Count==3&&page.Words[2].Text=="again.",
            "a held-out real screen sentence preserves all letters and its period on the short-line route");
        // Other Windows OCR builds can read the line on their first attempt.
        // Also feed the two recorded transcriptions to the same pixel check.
        var ink=DesktopLineEvidence.Analyze(source);var first=Reading("PIease try again.");var second=Reading("Please try again.");
        var proof=VerifiedLineOcr.ProveCaseRereading(null,ink,first,second,CancellationToken.None);
        check(proof!=null&&proof.Text==page.Text&&proof.Words.Select(w=>w.Box).SequenceEqual(page.Words.Select(w=>w.Box)),
            "recorded native disagreement is resolved only by reproducing the whole real line and its word bounds");
        lock(events)check(!events.Contains("case-recheck")||events.Contains("case-proof"),
            "a second-scale case check on the held-out crop finishes with a pixel proof");
    }
    internal static void Run(Action<bool,string> check){
        var source=Image("Please close the door.");var ink=VerifiedLineOcr.Analyze(source);
        var raw=new byte[source.PixelWidth*source.PixelHeight*4];source.CopyPixels(raw,source.PixelWidth*4,0);raw[3]=254;
        var transparent=BitmapSource.Create(source.PixelWidth,source.PixelHeight,96,96,PixelFormats.Bgra32,null,raw,source.PixelWidth*4);transparent.Freeze();
        check(VerifiedLineOcr.Analyze(transparent)==null,"real source transparency is never flattened or ignored");
        check(ink!=null&&VerifiedLineOcr.Reproduces(ink,"Please close the door.",CancellationToken.None),"reference glyph outlines reproduce all the ink in an unfamiliar sentence");
        var mapped=VerifiedLineOcr.Prove(ink,new[]{"Please","close","the","door•"},CancellationToken.None);
        check(mapped!=null&&mapped.Text=="Please close the door."&&mapped.Words.Count==4&&VerifiedLineOcr.Covered(ink,mapped),"a glyph-verified punctuation hypothesis returns actual all-ink word bounds");
        check(VerifiedLineOcr.Prove(VerifiedLineOcr.Analyze(Image("Please close the door•")),new[]{"Please","close","the","door•"},CancellationToken.None)==null,"an actual bullet must not become a full stop");
        check(!VerifiedLineOcr.Reproduces(ink,"Please close the door",CancellationToken.None),"missing punctuation cannot be accepted even when boxes cover the entire line");
        check(!VerifiedLineOcr.Reproduces(ink,"Please close the doors.",CancellationToken.None),"plausible but wrong text must fail full-line pixel verification");
        var misread=Reading("PIease close the door.");var reread=Reading("Please close the door.");
        var recovered=VerifiedLineOcr.ProveCaseRereading(ink,null,misread,reread,CancellationToken.None);
        check(recovered!=null&&recovered.Text=="Please close the door."&&VerifiedLineOcr.Covered(ink,recovered),
            "a fresh transcription and distinct grayscale glyphs can recover a single internal lowercase L");
        check(VerifiedLineOcr.ProveCaseRereading(VerifiedLineOcr.Analyze(Image("PIease close the door.")),null,misread,reread,CancellationToken.None)==null,
            "a real mixed-case identifier is never respelled to fit the surrounding sentence");
        check(VerifiedLineOcr.ProveCaseRereading(VerifiedLineOcr.Analyze(Image("Please close the door.",false,true)),null,misread,reread,CancellationToken.None)==null,
            "a lowercase L recheck still accounts for a faint unexpected mark in the screenshot");
        foreach(string other in new[]{"PIease close the door.","Please close the door","Please close the doors.","Please cose the door.","Please closethedoor."})
            check(VerifiedLineOcr.ProveCaseRereading(ink,null,misread,Reading(other),CancellationToken.None)==null,
                "a repeated miss or any additional disagreement disqualifies a scale recheck: "+other);
        check(VerifiedLineOcr.ProveCaseRereading(ink,null,Reading("PIease cIose the door."),reread,CancellationToken.None)==null&&
            VerifiedLineOcr.ProveCaseRereading(ink,null,Reading("AI model"),Reading("Al model"),CancellationToken.None)==null,
            "a second mixed-case word and an ambiguous acronym stay on the complete OCR path");
        // Windows sometimes supplies an extra word boundary within a real word.
        // Its rectangles only nominate a candidate; they cannot determine the text.
        var split=new[]{new WordBox{Text="Please",Box=new Rect(12,14,64,21)},new WordBox{Text="close",Box=new Rect(87,14,50,20)},
            new WordBox{Text="the",Box=new Rect(145,14,31,20)},new WordBox{Text="do",Box=new Rect(184,14,24,20)},new WordBox{Text="or.",Box=new Rect(210,14,25,20)}};
        var joined=VerifiedLineOcr.ProveReading(ink,null,split,CancellationToken.None);
        check(joined!=null&&joined.Text=="Please close the door."&&joined.Words.Count==4&&VerifiedLineOcr.Covered(ink,joined),"an unlisted split word needs whole-line proof and genuine word boxes");
        split[4].Text="ur.";
        check(VerifiedLineOcr.ProveReading(ink,null,split,CancellationToken.None)==null,"a geometrically plausible join cannot correct a wrong character");
        split[4].Text="or.";split[4].Box=new Rect(224,14,25,20);
        check(VerifiedLineOcr.ProveReading(ink,null,split,CancellationToken.None)==null,"a recognizer split with a wide gap is not joined");
        var spaced=new[]{new WordBox{Text="use",Box=new Rect(10,10,32,20)},new WordBox{Text="less",Box=new Rect(44,10,34,20)}};
        var distinct=VerifiedLineOcr.ProveReading(VerifiedLineOcr.Analyze(Image("use less")),null,spaced,CancellationToken.None);
        check(distinct!=null&&distinct.Text=="use less"&&distinct.Words.Count==2&&distinct.Words[0].Box.Right<distinct.Words[1].Box.Left,"actual whitespace is preserved even when the reported boxes touch");
        check(!VerifiedLineOcr.Reproduces(VerifiedLineOcr.Analyze(Image("Save as")),"Save is",CancellationToken.None),"a valid but visually different small word cannot be accepted");
        check(VerifiedLineOcr.Analyze(Image("Save settings",true))==null,"faint second line is never passed to single-line OCR");
        check(!VerifiedLineOcr.Reproduces(VerifiedLineOcr.Analyze(Image("Save settings",false,true)),"Save settings",CancellationToken.None),"a nearly white same-baseline mark is not background and cannot be dropped");
        var page=new OcrPage();page.Words.Add(new WordBox{Text="Please",Box=new Rect(8,9,70,30),Line=0});
        check(!VerifiedLineOcr.Covered(ink,page),"OCR reading only a prefix cannot hide the remaining ink");
        foreach(string token in new[]{"Al","Al.","Il?","AI","A1","12","foo/bar","Name.XML","SAVE","1st"})check(!VerifiedLineOcr.SimpleWord(token),"unverified identifier or code must fall back: "+token);
        foreach(string token in new[]{"Don't","We're","it'll","Here’s","Wait,","I’m"})check(VerifiedLineOcr.SimpleWord(token),"ordinary punctuation remains available for whole-line verification: "+token);
        foreach(string token in new[]{"Al's","AI’s","A1's","r&d","O'Neil","can''t",",","''","won't,."})check(!VerifiedLineOcr.SimpleWord(token),"ambiguous tokens and invalid punctuation are declined: "+token);
        foreach(string label in Translation.CanonicalEnglishScreenshotCommands()){
            var menu=Image(label,size:18);var direct=ExactMenuGlyphOcr.TryRead(menu,VerifiedLineOcr.Analyze(menu),CancellationToken.None);
            // A font difference may decline; it must not yield another label.
            check((direct!=null&&direct.Text==label&&direct.Words.Count==label.Split(' ').Length)||(direct==null&&label!="New folder"),"menu label and its word boxes are verified or declined: "+label);
        }
        foreach(string text in new[]{"New folder?","New foIder","Select all.","Copy l1nk","New folder"}){
            var mixed=Image(text,false,text=="New folder",18);
            check(ExactMenuGlyphOcr.TryRead(mixed,VerifiedLineOcr.Analyze(mixed),CancellationToken.None)==null,"menu dictionary cannot erase or replace visible ink: "+text);
        }
    }
}
