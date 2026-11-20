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

// Lossless guest-screen crops of an offline Edge page. The fixture checked
// unique corner marks before cropping. These tests never open a browser or
// require a network service. The English recognition component is required.
internal static class BrowserLineEvidenceTests {
    internal static void Run(string folder,Action<bool,string> check){
        Task.Run(()=>RunOnWorker(folder,check)).GetAwaiter().GetResult();
    }

    static BitmapSource Load(string folder,string name){
        using(var file=File.OpenRead(Path.Combine(folder,name+".png"))){
            var decoder=BitmapDecoder.Create(file,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
            var image=new WriteableBitmap(decoder.Frames[0]);image.Freeze();return image;
        }
    }

    static BitmapSource Change(BitmapSource source,Action<byte[],int> modify){
        var bgra=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
        int stride=4*bgra.PixelWidth;var pixels=new byte[stride*bgra.PixelHeight];
        bgra.CopyPixels(pixels,stride,0);modify(pixels,stride);
        var marked=BitmapSource.Create(bgra.PixelWidth,bgra.PixelHeight,96,96,PixelFormats.Bgra32,null,pixels,stride);
        marked.Freeze();return marked;
    }

    static void FullReading(string folder,string name,string expected,Action<bool,string> check){
        var stages=new List<string>();
        var page=VerifiedLineOcr.TryReadObserved(Load(folder,name),CancellationToken.None,
            stage=>{lock(stages)stages.Add(stage);},12000).GetAwaiter().GetResult();
        bool passed=page!=null&&page.Text==expected;
        var message=name+" retains the visible words and punctuation on the production short-line route";
        if(!passed){
            lock(stages)message+="; expected="+expected+"; OCR="+(page==null?"<no result>":page.Text)+"; stages="+string.Join(" > ",stages);
        }
        check(passed,message);
    }

    static List<WordBox> MatchingVote(OcrPage symbols,int left,int top){
        var native=new List<WordBox>();
        foreach(var word in symbols.Words.Where(w=>w.Line%2==0)){
            var box=word.Box;box.Offset(left,top);
            native.Add(new WordBox{Text=word.Text,Line=0,Box=box});
        }
        return native;
    }

    static OcrPage ReadSymbols(BitmapSource source,DesktopLineEvidence.Ink ink,out int left,out int top){
        var bounds=ink.Bounds;
        left=Math.Max(0,bounds.X-6);top=Math.Max(0,bounds.Y-6);
        int right=Math.Min(ink.W,bounds.X+bounds.Width+6),bottom=Math.Min(ink.H,bounds.Y+bounds.Height+6);
        var view=new WriteableBitmap(new CroppedBitmap(source,new Int32Rect(left,top,right-left,bottom-top)));view.Freeze();
        return ConsensusLineOcr.ReadSymbols(view);
    }

    static void DarkNativeBounds(string folder,Action<bool,string> check){
        var source=Load(folder,"browser-save-edge-real");var ink=DesktopLineEvidence.Analyze(source);
        int left,top;var tess=ReadSymbols(source,ink,out left,out top);
        // Both native readings came from this exact Edge crop. At 2x the first
        // native box clips the top of S; the 3x word boxes cover the entire S.
        var atTwo=new[]{new WordBox{Text="Save",Box=new Rect(10,24.5,44,16)},new WordBox{Text="changes",Box=new Rect(60.5,20.5,78.5,19.5)}};
        var atThree=new[]{new WordBox{Text="Save",Box=new Rect(31.0/3,23,130.0/3,47.0/3)},new WordBox{Text="changes",Box=new Rect(181.0/3,61.0/3,235.0/3,62.0/3)}};
        bool retry;
        check(ConsensusLineOcr.Verify(ink,atTwo,tess,left,top,CancellationToken.None,out retry)==null&&retry,
            "the clipped native S alone permits a geometry check, never an early result");
        var corrected=ConsensusLineOcr.Verify(ink,atThree,tess,left,top,CancellationToken.None,out retry);
        check(corrected!=null&&corrected.Text=="Save changes"&&!retry,"a second reading with complete bounds proves the original dark menu");
        atThree[0].Text="Safe";
        check(ConsensusLineOcr.Verify(ink,atThree,tess,left,top,CancellationToken.None,out retry)==null&&!retry&&
            !ConsensusLineOcr.NeedsAnotherScale(atThree,tess),"changing the spelling on the second scale disqualifies the result");
        atThree[0].Text="Save";
        var marked=Change(source,(pixels,stride)=>{
            int i=26*stride+17*4;
            if(pixels[i]!=36||pixels[i+1]!=36||pixels[i+2]!=36)throw new InvalidDataException("Expected dark background inside the S.");
            pixels[i+1]++;
        });
        var extra=DesktopLineEvidence.Analyze(marked);
        check(ConsensusLineOcr.Verify(extra,atTwo,tess,left,top,CancellationToken.None,out retry)==null&&!retry&&
            ConsensusLineOcr.Verify(extra,atThree,tess,left,top,CancellationToken.None,out retry)==null&&!retry,
            "a single new color level inside the dark S blocks both scales even when the native word edge was clipped");
    }

    static void RunOnWorker(string folder,Action<bool,string> check){
        FullReading(folder,"browser-file-edge-real","Open this file.",check);
        FullReading(folder,"browser-door-edge-real","Close the door.",check);
        FullReading(folder,"browser-copy-edge-real","Copy link",check);
        FullReading(folder,"browser-save-edge-real","Save changes",check);
        DarkNativeBounds(folder,check);

        var source=Load(folder,"browser-file-edge-real");
        var ink=DesktopLineEvidence.Analyze(source);
        int left,top;var tess=ReadSymbols(source,ink,out left,out top);
        // Keep both hypothetical sets of votes fixed for every perturbation.
        // Agreement and confidence alone cannot establish what was printed.
        var native=MatchingVote(tess,left,top);
        Func<BitmapSource,OcrPage> verify=image=>ConsensusLineOcr.Verify(DesktopLineEvidence.Analyze(image),native,tess,left,top,CancellationToken.None);
        var unchanged=verify(source);
        check(unchanged!=null&&unchanged.Text=="Open this file.","control image passes with the frozen word and symbol boxes");
        var period=tess.Words.Single(w=>w.Line%2==1&&w.Text==".");
        var confidence=period.Confidence;
        try{
            period.Confidence=.5;
            bool retry;
            check(ConsensusLineOcr.Verify(ink,native,tess,left,top,CancellationToken.None,out retry)==null&&!retry,
                "a weakly recognized final period cannot borrow its containing word's high confidence or request a geometry retry");
        }finally{period.Confidence=confidence;}
        foreach(byte green in new byte[]{249,85}){
            var changed=Change(source,(pixels,stride)=>{
                int i=29*stride+18*4;
                if(pixels[i]!=250||pixels[i+1]!=250||pixels[i+2]!=250)throw new InvalidDataException("Expected a blank pixel within O.");
                pixels[i+1]=green;
            });
            check(verify(changed)==null,"a disconnected new mark inside O is rejected even with identical OCR votes (G="+green+")");
        }
        var dottedStem=Change(source,(pixels,stride)=>{
            // Split the printed l inside "file." into a dot and a narrow
            // vertical stem. The same word contains a real i in its fi ligature.
            // The added dot therefore cannot be assigned using the word alone.
            for(int y=23;y<=25;y++)for(int x=122;x<=125;x++){
                int p=y*stride+x*4;pixels[p]=pixels[p+1]=pixels[p+2]=250;
            }
        });
        check(verify(dottedStem)==null,"a dotted l cannot borrow the i from the ligature in the same word");
        var annotation=Change(source,(pixels,stride)=>{
            // A second distant, faint label with two strokes, on the same row.
            for(int y=28;y<34;y++)foreach(int x in new[]{265,268}){
                int p=y*stride+x*4;pixels[p]=pixels[p+1]=pixels[p+2]=236;
            }
        });
        check(verify(annotation)==null,"the large text cannot conceal a small low-contrast annotation at the right");
        var comma=Change(source,(pixels,stride)=>{
            int a=37*stride+139*4,b=38*stride+138*4;
            pixels[a]=pixels[a+1]=pixels[a+2]=101;
            pixels[b]=pixels[b+1]=pixels[b+2]=130;
        });
        check(verify(comma)==null,"a two-pixel tail below the final period is not accepted as the unchanged source");

        // Exercise the lexical guard with a complete, internally consistent
        // misreading, rather than merely introducing a different symbol count.
        foreach(string ambiguous in new[]{"Al","AI"}){
            var wrong=new OcrPage();
            var word=tess.Words[0];var wordBox=word.Box;
            wrong.Words.Add(new WordBox{Text=ambiguous,Line=0,Box=wordBox,Confidence=1});
            for(int i=0;i<2;i++)wrong.Words.Add(new WordBox{Text=ambiguous.Substring(i,1),Line=1,Confidence=1,
                Box=new Rect(wordBox.Left+i*wordBox.Width/2,wordBox.Top,wordBox.Width/2,wordBox.Height)});
            wrong.Words.AddRange(tess.Words.Where(w=>w.Line>=2));
            var agrees=MatchingVote(wrong,left,top);
            check(!ConsensusLineOcr.Eligible(ink,agrees,0)&&ConsensusLineOcr.Verify(ink,agrees,wrong,left,top,CancellationToken.None)==null,
                "matching high-confidence votes for the ambiguous token "+ambiguous+" must defer to the main recognizer");
        }
    }
}
