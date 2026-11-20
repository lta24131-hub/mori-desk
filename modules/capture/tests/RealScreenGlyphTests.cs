using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;

// Fixed lossless crops from the guest desktop, not text drawn by the verifier.
// Every crop came from Snapshot.Screen after the fixture saw its two unique
// border marks. Those marks, desktop surroundings and private data are absent.
internal static class RealScreenGlyphTests {
    static BitmapSource Load(string folder,string name){
        using(var stream=File.OpenRead(Path.Combine(folder,name+".png"))){
            var image=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];
            image.Freeze();return image;
        }
    }
    static void Reading(string folder,string file,string text,Action<bool,string> check,params Rect[] bounds){
        var ink=DesktopLineEvidence.Analyze(Load(folder,file));
        var page=DesktopLineEvidence.Prove(ink,text,CancellationToken.None);
        check(page!=null&&page.Text==text&&page.Words.Select(w=>w.Box).SequenceEqual(bounds),
            file+" preserves its source text and the exact visible extent of each word");
    }
    static void Reject(string folder,string file,string wrong,Action<bool,string> check){
        var ink=DesktopLineEvidence.Analyze(Load(folder,file));
        check(DesktopLineEvidence.Prove(ink,wrong,CancellationToken.None)==null,file+" rejects '"+wrong+"'");
    }
    static BitmapSource CropWord(string folder,string file,Int32Rect bounds){
        var copy=new WriteableBitmap(new CroppedBitmap(Load(folder,file),bounds));copy.Freeze();return copy;
    }
    static OcrPage ExactSingle(BitmapSource source){
        return ExactMenuGlyphOcr.TryReadDesktop(DesktopLineEvidence.Analyze(source,true),CancellationToken.None);
    }
    static void CheckStandaloneCaptions(string folder,Action<bool,string> check){
        var captions="Open Copy Save Close Paste Settings Folder Search Delete Cancel Download Upload Install Uninstall Rename Undo Redo Retry".Split(' ');
        var forms=new HashSet<string>(captions.SelectMany(word=>new[]{word,word.ToLowerInvariant()}),StringComparer.Ordinal);
        check(forms.SetEquals(Translation.StandaloneScreenshotWords())&&forms.All(word=>!string.IsNullOrEmpty(Translation.StandaloneWordMeaning(word,"zh","tencent"))),
            "all eighteen whole-image labels share the exact same case-sensitive OCR and meaning list");
        foreach(string text in new[]{"Download","Rename"}){
            var image=Load(folder,"button-"+text.ToLowerInvariant()+"-real");
            var ink=DesktopLineEvidence.Analyze(image,true);var page=ExactSingle(image);
            check(ink!=null&&page!=null&&page.Text==text&&page.Words.Count==1&&
                page.Words[0].Box==new Rect(ink.Bounds.X,ink.Bounds.Y,ink.Bounds.Width,ink.Bounds.Height),
                "new desktop button retains every letter and its painted bounds: "+text);
        }
    }
    static void SingleWords(string folder,Action<bool,string> check){
        // Original, unscaled desktop pixels. Several words begin mid-sentence
        // and retain the subpixel positioning of the surrounding text.
        var open=CropWord(folder,"sentence",new Int32Rect(4,18,68,34));
        var samples=VerifiedLineOcr.CopyBgra(open);int w=open.PixelWidth,h=open.PixelHeight,removed=-1;
        for(int y=2;y<h-2&&removed<0;y++)for(int x=2;x<w-2;x++){
            int i=4*(y*w+x);
            if(samples[i]<64&&(samples[i-4]<64||samples[i+4]<64)){removed=i;break;}
        }
        if(removed<0)throw new InvalidDataException("No connected dark stroke in Open fixture.");
        samples[removed]=samples[removed+1]=samples[removed+2]=samples[0];
        var damaged=BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,samples,w*4);damaged.Freeze();
        check(ExactSingle(damaged)==null,"one missing pixel in a connected stroke invalidates a one-word reading");
        var extra=VerifiedLineOcr.CopyBgra(open);int knownBlank=4*(3*w+w/2);
        if(extra[knownBlank]!=255||extra[knownBlank+1]!=255||extra[knownBlank+2]!=255)throw new InvalidDataException("Expected a blank pixel above the word.");
        extra[knownBlank+1]=254;
        var annotated=BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,extra,w*4);annotated.Freeze();
        check(ExactSingle(annotated)==null,"a one-level green mark above the word remains part of its original image");
        foreach(string name in new[]{"dot","ai11","name","mixed"})check(ExactSingle(Load(folder,name))==null,
            "isolated word reading does not hide punctuation, names, identifiers, or a second line: "+name);
        var inputs=new[]{open,CropWord(folder,"curly",new Int32Rect(70,21,62,31)),
            CropWord(folder,"dark-copy",new Int32Rect(4,23,55,31)),Load(folder,"no-dot")};
        var expected=new[]{"Open","close","Copy","Save"};
        for(int i=0;i<inputs.Length;i++){
            var ink=DesktopLineEvidence.Analyze(inputs[i]);var page=ExactSingle(inputs[i]);
            check(page!=null&&page.Text==expected[i]&&page.Words.Count==1&&
                page.Words[0].Box==new Rect(ink.Bounds.X,ink.Bounds.Y,ink.Bounds.Width,ink.Bounds.Height),
                "single desktop word retains its exact casing and bounds: "+expected[i]);
        }
        // The standalone rendering cannot reproduce this particular word.
        // Its glyphs, rendered inside an ordinary menu caption, must explain
        // every visible channel without adding the invisible preceding word.
        var folderWord=CropWord(folder,"menu",new Int32Rect(60,18,73,30));
        var folderInk=DesktopLineEvidence.Analyze(folderWord);
        check(DisplayGlyphEvidence.Prove(folderInk,"folder",CancellationToken.None,24,true)==null,
            "a mid-sentence folder crop cannot be explained by standalone word rendering");
        var changed=VerifiedLineOcr.CopyBgra(folderWord);int blank=4*(10*folderWord.PixelWidth+26);
        if(changed[blank]!=255||changed[blank+1]!=255||changed[blank+2]!=255)throw new InvalidDataException("Expected a gap between folder glyphs.");
        changed[blank+1]=254;
        var markedFolder=BitmapSource.Create(folderWord.PixelWidth,folderWord.PixelHeight,96,96,PixelFormats.Bgra32,null,changed,4*folderWord.PixelWidth);markedFolder.Freeze();
        check(ExactSingle(markedFolder)==null,"a one-level change between letters invalidates the entire contextual word proof");
        string[] contextualWords={"changes","image"};
        var contextualCrops=new[]{CropWord(folder,"gray-save",new Int32Rect(48,23,75,29)),CropWord(folder,"dark-copy",new Int32Rect(59,23,62,30))};
        for(int i=0;i<contextualCrops.Length;i++){
            var expectedInk=DesktopLineEvidence.Analyze(contextualCrops[i],true);var reading=ExactSingle(contextualCrops[i]);
            check(reading!=null&&reading.Text==contextualWords[i]&&reading.Words.Count==1&&reading.Words[0].Box==
                new Rect(expectedInk.Bounds.X,expectedInk.Bounds.Y,expectedInk.Bounds.Width,expectedInk.Bounds.Height),
                "word cropped from another native caption preserves the actual word and its bounds: "+contextualWords[i]);
        }
        var dotted=contextualCrops[1];var dottedInk=DesktopLineEvidence.Analyze(dotted,true);
        check(dottedInk!=null&&dottedInk.HasDetachedDot&&DesktopLineEvidence.Analyze(dotted)==null,
            "a detached i dot only qualifies for complete pixel verification");
        check(DesktopLineEvidence.Analyze(Load(folder,"mixed"),true)==null,
            "a second full line cannot enter the detached-dot exception");
        var noDot=VerifiedLineOcr.CopyBgra(dotted);
        for(int y=0;y<8;y++)for(int x=0;x<dotted.PixelWidth;x++){
            int i=(y*dotted.PixelWidth+x)*4;
            noDot[i]=noDot[i+1]=noDot[i+2]=noDot[0];
        }
        var damagedImage=BitmapSource.Create(dotted.PixelWidth,dotted.PixelHeight,96,96,PixelFormats.Bgra32,null,noDot,4*dotted.PixelWidth);damagedImage.Freeze();
        check(ExactSingle(damagedImage)==null,"a missing detached i dot invalidates the actual word image");
        var stages=new List<string>();OcrPage full;
        using(var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            full=OcrWorker.ReadForTranslationObserved(folderWord,deadline.Token,stage=>{lock(stages)stages.Add(stage);},0).GetAwaiter().GetResult();
        check(full!=null&&full.Text=="folder"&&!stages.Contains("queue-acquired")&&stages.Contains("quick-complete"),
            "screenshot translation reads the exact cropped folder word and avoids full-page OCR: "+
            (full==null?"<none>":full.Text)+" ("+string.Join(", ",stages)+")");
    }
    internal static void Run(string folder,Action<bool,string> check){
        // The production screenshot reader runs on the background pool. An
        // STA-only verifier can pass on the UI thread yet throw on real clicks.
        Task.Run(()=>RunOnWorker(folder,check)).GetAwaiter().GetResult();
    }
    static void RunOnWorker(string folder,Action<bool,string> check){
        check(Thread.CurrentThread.GetApartmentState()==ApartmentState.MTA,"real crops use the production worker apartment");
        SingleWords(folder,check);
        CheckStandaloneCaptions(folder,check);
        VerifiedLineOcrTests.CheckRealCaseProof(folder,check);
        Reading(folder,"menu","New folder",check,new Rect(11,24,47,17),new Rect(64,23,64,18));
        Reading(folder,"sentence","Open the window.",check,new Rect(10,24,58,23),new Rect(75,23,34,18),new Rect(115,23,88,18));
        Reading(folder,"dark","Turn on the light.",check,new Rect(10,24,47,17),new Rect(65,29,26,12),new Rect(99,23,34,18),new Rect(141,23,51,24));
        Reading(folder,"tern","Turn the wheel.",check,new Rect(10,28,47,17),new Rect(64,27,35,18),new Rect(105,27,67,18));
        Reading(folder,"day","day",check,new Rect(10,27,39,24));
        Reading(folder,"pay","pay",check,new Rect(11,33,38,18));
        Reading(folder,"dot","Save.",check,new Rect(11,28,53,17));
        Reading(folder,"no-dot","Save",check,new Rect(11,28,48,17));
        Reading(folder,"apostrophe","Don't close it.",check,new Rect(11,28,57,17),new Rect(75,27,53,18),new Rect(136,28,18,17));
        Reading(folder,"curly","Don’t close it.",check,new Rect(11,28,57,17),new Rect(75,27,53,18),new Rect(136,28,18,17));
        Reading(folder,"comma","Wait, please.",check,new Rect(10,28,52,20),new Rect(71,27,71,24));
        Reading(folder,"no-apostrophe","Dont close it.",check,new Rect(11,28,52,17),new Rect(70,27,53,18),new Rect(130,28,18,17));
        Reading(folder,"no-comma","Wait please.",check,new Rect(10,28,48,17),new Rect(65,27,71,24));
        Reading(folder,"gray-save","Save changes",check,new Rect(10,29,37,13),new Rect(52,28,65,18));
        Reading(folder,"gray-dot","Save changes.",check,new Rect(10,29,37,13),new Rect(52,28,69,18));
        Reading(folder,"dark-copy","Copy image",check,new Rect(10,29,46,19),new Rect(62,28,53,20));
        Reject(folder,"day","pay",check);Reject(folder,"pay","day",check);
        Reject(folder,"dot","Save",check);Reject(folder,"no-dot","Save.",check);
        Reject(folder,"sentence","Open the wi ndow.",check);
        Reject(folder,"ai11","Enable sharing",check);Reject(folder,"mixed","Open document",check);
        Reject(folder,"apostrophe","Dont close it.",check);Reject(folder,"no-apostrophe","Don't close it.",check);
        Reject(folder,"apostrophe","Don’t close it.",check);Reject(folder,"curly","Don't close it.",check);
        Reject(folder,"comma","Wait please.",check);Reject(folder,"no-comma","Wait, please.",check);
        Reject(folder,"name","AI's model 12",check);
        Reject(folder,"gray-dot","Save changes",check);Reject(folder,"gray-save","Save changes.",check);
        var splitQuote=new[]{new WordBox{Text="DonIt",Box=new Rect(11,28,57,17)},
            new WordBox{Text="close",Box=new Rect(75,27,53,18)},new WordBox{Text="it.",Box=new Rect(136,28,18,17)}};
        var actual=VerifiedLineOcr.ProveReading(null,DesktopLineEvidence.Analyze(Load(folder,"apostrophe")),splitQuote,CancellationToken.None);
        check(actual!=null&&actual.Text=="Don't close it.","the actual native DonIt hypothesis is corrected only with the matching straight apostrophe");
        check(VerifiedLineOcr.ProveReading(null,DesktopLineEvidence.Analyze(Load(folder,"no-apostrophe")),splitQuote,CancellationToken.None)==null,
            "an apparent native I is not evidence of a printed apostrophe");
        splitQuote[0].Text="Don't";
        // The source has a normal space before the final word, but the
        // recognizer's boxes also suggest an unrelated possible word join.
        splitQuote[2].Box=new Rect(130,28,18,17);
        actual=VerifiedLineOcr.ProveReading(null,DesktopLineEvidence.Analyze(Load(folder,"curly")),splitQuote,CancellationToken.None);
        check(actual!=null&&actual.Text=="Don’t close it.","a curly apostrophe read as a straight one keeps the actual glyph");

        var source=Load(folder,"sentence");var image=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
        int stride=image.PixelWidth*4;var samples=new byte[stride*image.PixelHeight];image.CopyPixels(samples,stride,0);
        // A one-level green dot inside the original bounding rectangle, in
        // the space between the first two words. Dimensions and word outlines
        // alone cannot distinguish this image from the original.
        int green=33*stride+70*4+1;check(samples[green]==255,"single-channel counterexample starts with a blank sample");samples[green]=254;
        var marked=BitmapSource.Create(image.PixelWidth,image.PixelHeight,96,96,PixelFormats.Bgra32,null,samples,stride);marked.Freeze();
        check(DesktopLineEvidence.Prove(DesktopLineEvidence.Analyze(marked),"Open the window.",CancellationToken.None)==null,
            "an additional one-level green dot between words prevents acceptance of the unmarked sentence");
        // The decoder and its frozen frame belong to this thread, as they do
        // in the image viewer. The production read happens on another worker.
        var savedFile=Load(folder,"tern");var stages=new List<string>();
        var fromFile=VerifiedLineOcr.TryReadObserved(savedFile,CancellationToken.None,
            stage=>{lock(stages)stages.Add(stage);},6000).GetAwaiter().GetResult();
        check(stages.Contains("native-reply")&&!stages.Contains("InvalidOperationException")&&fromFile!=null&&fromFile.Text=="Turn the wheel.",
            "frozen PNG opened on another thread preserves its short-sentence fast path");
    }
}
