using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using Tesseract;
using Rect=System.Windows.Rect;

namespace QingJie {
    // A translation-only second opinion for a single upright, fully visible line.
    // The native engine and this worker see the same sampling and crop. Keep the
    // original RGB pixels for coverage checks: grayscale can hide a coloured mark.
    internal static class ConsensusLineOcr {

        internal static bool Eligible(DesktopLineEvidence.Ink ink,IList<WordBox> native,double? angle){
            if(ink==null||!ink.HasColor||native==null||native.Count<2||native.Count>12)return false;
            if(angle.HasValue&&(double.IsNaN(angle.Value)||Math.Abs(angle.Value)>3))return false;
            int length=native.Count-1;
            foreach(var word in native){
                if(word==null||word.Line!=0||!VerifiedLineOcr.SimpleWord(word.Text)||!Valid(word.Box))return false;
                length+=word.Text.Length;
            }
            return length<=60;
        }

        internal static bool CanCompare(DesktopLineEvidence.Ink ink,IList<WordBox> words,double? angle){
            if(Eligible(ink,words,angle))return true;
            if(ink==null||!ink.HasColor||words==null||words.Count<3||words.Count>12||
                (angle.HasValue&&!(Math.Abs(angle.Value)<=3)))return false;
            int regular=0,artifacts=0,total=0;
            for(int i=0;i<words.Count;i++){
                var w=words[i];if(w==null||w.Line!=0||!Valid(w.Box)||string.IsNullOrEmpty(w.Text))return false;
                total+=w.Text.Length+1;
                if(VerifiedLineOcr.SimpleWord(w.Text)){regular++;continue;}
                if(w.Text.Length!=1||!(w.Text[0]>='a'&&w.Text[0]<='z'||w.Text=="|"&&(i==0||i==words.Count-1)))return false;
                artifacts++;
            }
            return regular>=2&&artifacts<=2&&total<=62;
        }
        internal static bool NeedsAnotherScale(IList<WordBox> native,OcrPage tess){
            if(tess==null||tess.Words.Count==0)return false;
            var expected=new StringBuilder();int n=0;bool different=false;
            foreach(var word in tess.Words){
                if(!(word.Confidence>=.94&&word.Confidence<=1))return false;
                if((word.Line&1)!=0)continue;
                if(word.Line!=2*n||!VerifiedLineOcr.SimpleWord(word.Text))return false;
                if(n>=native.Count||native[n].Text!=word.Text)different=true;
                expected.Append(word.Text);n++;
            }
            if(n<2||n>12||!different&&n==native.Count)return false;
            // Retry a segmentation disagreement only. Never use an extra reading
            // to choose between genuinely different letters or punctuation.
            var actual=new StringBuilder();
            for(int i=0;i<native.Count;i++){
                if(native[i].Text=="|"&&(i==0||i==native.Count-1))continue;
                actual.Append(native[i].Text);
            }
            return string.Equals(actual.ToString(),expected.ToString(),StringComparison.Ordinal);
        }

        internal static bool Installed(){
            string root=AppDomain.CurrentDomain.BaseDirectory;
            return Environment.Is64BitProcess&&File.Exists(Path.Combine(root,"Nook.LineOcr.exe"))&&
                File.Exists(Path.Combine(root,"tessdata","eng.traineddata"))&&
                File.Exists(Path.Combine(root,"Tesseract.dll"))&&File.Exists(Path.Combine(root,"x64","tesseract50.dll"))&&
                File.Exists(Path.Combine(root,"x64","leptonica-1.82.0.dll"));
        }

        internal static OcrPage ReadIsolated(BitmapSource view,CancellationToken cancel){
            return ReadIsolated(view,cancel,null);
        }
        internal static OcrPage ReadIsolated(BitmapSource view,CancellationToken cancel,Action<int> started){
            return OcrWorker.ReadShortLineSymbols(OcrService.Prepare(view,2),cancel,started);
        }

        // This runs only in the disposable worker. A word (even group) is
        // followed by its individual symbols (next odd group). Preserve their
        // original spelling and coordinates across the existing private pipe.
        internal static OcrPage ReadSymbols(BitmapSource view){
            var pixels=new OcrPixels(OcrService.Prepare(view,2));
            using(var engine=OpenSymbolsEngine())
            using(var pix=pixels.TesseractBitmap())return ReadSymbols(engine,pix);
        }
        internal static TesseractEngine OpenSymbolsEngine(){
            return LineSymbolReader.Open();
        }
        internal static OcrPage ReadSymbols(TesseractEngine engine,Pix bitmap){
            var page=new OcrPage();
            foreach(var row in LineSymbolReader.Read(engine,bitmap)){
                page.Words.Add(new WordBox{Text=row.Text,Line=row.Group,Box=new Rect(row.X,row.Y,row.Width,row.Height),
                    Confidence=row.Confidence,ConfidenceSource=OcrConfidenceSource.Model});
            }
            return page;
        }
        static bool Valid(Rect b){
            return !b.IsEmpty&&b.Width>0&&b.Height>0&&b.Left>=0&&b.Top>=0&&b.Right<=2048&&b.Bottom<=320;
        }
        static bool Overlaps(Rect a,Rect b){
            var common=Rect.Intersect(a,b);
            return !common.IsEmpty&&common.Width>=.7*Math.Max(a.Width,b.Width)&&common.Height>=.64*Math.Max(a.Height,b.Height);
        }

        internal static OcrPage Verify(DesktopLineEvidence.Ink ink,IList<WordBox> native,OcrPage tess,int originX,int originY,CancellationToken cancel){
            bool missingNativeEdge;
            return Verify(ink,native,tess,originX,originY,cancel,out missingNativeEdge);
        }
        internal static OcrPage Verify(DesktopLineEvidence.Ink ink,IList<WordBox> native,OcrPage tess,int originX,int originY,CancellationToken cancel,out bool missingNativeEdge){
            missingNativeEdge=false;
            if(!Eligible(ink,native,null)||tess==null)return null;
            var coverage=new byte[ink.W*ink.H];var symbols=new List<WordBox>();var result=new OcrPage();
            int index=0;double previousRight=-1;
            for(int i=0;i<native.Count;i++){
                cancel.ThrowIfCancellationRequested();
                if(index>=tess.Words.Count)return null;
                var word=tess.Words[index++];var original=native[i];
                if(word.Line!=2*i||word.Text!=original.Text||!(word.Confidence>=.94&&word.Confidence<=1)||!Valid(word.Box))return null;
                var box=word.Box;box.Offset(originX,originY);
                if(!Valid(box)||box.Right>ink.W||box.Bottom>ink.H||!Overlaps(original.Box,box)||box.Left<previousRight-1)return null;
                previousRight=box.Right;
                Mark(coverage,ink,original.Box,1);Mark(coverage,ink,box,2);
                double previousCenter=-1;
                foreach(char ch in word.Text){
                    if(index>=tess.Words.Count)return null;
                    var symbol=tess.Words[index++];
                    if(symbol.Line!=2*i+1||symbol.Text==null||symbol.Text.Length!=1||symbol.Text[0]!=ch||
                        !(symbol.Confidence>=.94&&symbol.Confidence<=1)||!Valid(symbol.Box))return null;
                    var cell=symbol.Box;cell.Offset(originX,originY);
                    var wordArea=box;wordArea.Inflate(1.25,1.25);
                    double center=cell.Left+cell.Width/2;
                    if(!wordArea.Contains(cell)||center<=previousCenter||!ContainsInk(ink,cell))return null;
                    previousCenter=center;Mark(coverage,ink,cell,4);
                    symbols.Add(new WordBox{Text=symbol.Text,Box=cell,Line=i});
                }
                result.Words.Add(new WordBox{Line=0,Text=original.Text,Box=box,Confidence=word.Confidence,ConfidenceSource=OcrConfidenceSource.Model});
            }
            if(index!=tess.Words.Count||!AccountsForInk(ink,coverage,symbols,result.Words,cancel,out missingNativeEdge))return null;
            OcrService.IndexText(result);return result;
        }
        static void Mark(byte[] coverage,DesktopLineEvidence.Ink ink,Rect box,byte flag){
            box.Inflate(1.25,1.25);
            int left=Math.Max(0,(int)Math.Ceiling(box.Left-.5)),right=Math.Min(ink.W-1,(int)Math.Floor(box.Right-.5));
            int top=Math.Max(0,(int)Math.Ceiling(box.Top-.5)),bottom=Math.Min(ink.H-1,(int)Math.Floor(box.Bottom-.5));
            for(int y=top;y<=bottom;y++)for(int x=left;x<=right;x++)coverage[y*ink.W+x]|=flag;
        }
        static int Strength(DesktopLineEvidence.Ink ink,int pixel){
            var p=ink.Pixels;int offset=pixel*4,bg=p[0];
            return Math.Max(Math.Abs(p[offset]-bg),Math.Max(Math.Abs(p[offset+1]-bg),Math.Abs(p[offset+2]-bg)));
        }
        static bool ContainsInk(DesktopLineEvidence.Ink ink,Rect box){
            int left=Math.Max(0,(int)Math.Ceiling(box.Left-.5)),top=Math.Max(0,(int)Math.Ceiling(box.Top-.5));
            for(int y=top;y<ink.H&&y+.5<=box.Bottom;y++)
                for(int x=left;x<ink.W&&x+.5<=box.Right;x++)if(Strength(ink,y*ink.W+x)*4>=ink.Contrast)return true;
            return false;
        }
        static bool AccountsForInk(DesktopLineEvidence.Ink ink,byte[] coverage,List<WordBox> symbols,List<WordBox> words,CancellationToken cancel,out bool missingNativeEdge){
            missingNativeEdge=false;
            bool nativeBoxIncomplete=false;
            var b=ink.Bounds;
            for(int y=b.Y;y<b.Y+b.Height;y++){
                cancel.ThrowIfCancellationRequested();
                for(int x=b.X;x<b.X+b.Width;x++){
                    int i=y*ink.W+x;
                    if(Strength(ink,i)==0)continue;
                    // A second scale may put the native word edge in the right
                    // place. The other engine's word AND symbol must cover this
                    // pixel, and every component must still pass the checks below.
                    if(coverage[i]==6)nativeBoxIncomplete=true;
                    else if(coverage[i]!=7)return false;
                    coverage[i]=8;
                }
            }
            // A word or symbol rectangle can enclose a distant dot. Inspect the
            // connected original ink as well, including one-level RGB changes.
            // A disconnected small mark must belong to a character which has a
            // dot, comma or apostrophe; one character cannot explain two marks.
            var queue=new int[b.Width*b.Height];var usedMarks=new bool[symbols.Count];
            for(int y=b.Y;y<b.Y+b.Height;y++){
                cancel.ThrowIfCancellationRequested();
                for(int x=b.X;x<b.X+b.Width;x++){
                    int first=y*ink.W+x;if(coverage[first]!=8)continue;
                    int front=0,back=1,left=x,right=x,top=y,bottom=y,strong=0;
                    queue[0]=first;coverage[first]=0;
                    while(front<back){
                        int at=queue[front++],cy=at/ink.W,cx=at%ink.W;
                        if(Strength(ink,at)*4>=ink.Contrast)strong++;
                        left=Math.Min(left,cx);right=Math.Max(right,cx);top=Math.Min(top,cy);bottom=Math.Max(bottom,cy);
                        // Analyze rejects all ink within two pixels of an edge.
                        for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++){
                            int near=at+dy*ink.W+dx;if(coverage[near]!=8)continue;
                            coverage[near]=0;queue[back++]=near;
                        }
                    }
                    if(strong<2)return false;
                    if((bottom-top+1)*3>b.Height||(right-left+1)*2>b.Height)continue;
                    bool claimed=false;var bounds=new Rect(left,top,right-left+1,bottom-top+1);
                    for(int j=0;j<symbols.Count;j++){
                        if(usedMarks[j]||"ij.!?,’'".IndexOf(symbols[j].Text[0])<0)continue;
                        if(!MatchesMark(ink,bounds,symbols[j],words[symbols[j].Line].Box))continue;
                        if(claimed)return false;
                        claimed=true;usedMarks[j]=true;
                    }
                    if(!claimed)return false;
                }
            }
            for(int i=0;i<symbols.Count;i++)if(".!,?".IndexOf(symbols[i].Text[0])>=0&&!usedMarks[i])return false;
            missingNativeEdge=nativeBoxIncomplete;
            return !nativeBoxIncomplete;
        }
        static bool MatchesMark(DesktopLineEvidence.Ink ink,Rect mark,WordBox symbol,Rect word){
            char c=symbol.Text[0];var cell=symbol.Box;
            if(c=='i'||c=='j'){
                // The recognizer can place its character boundary between the
                // stem and dot of the adjacent lowercase i. Confirm the actual
                // separate upright stem below that dot, in the same agreed word.
                cell.Inflate(Math.Ceiling(word.Height*.35),1.25);
                return cell.Contains(mark)&&mark.Top<word.Top+word.Height*.45&&HasAlignedStem(ink,mark,word);
            }
            cell.Inflate(1.25,1.25);
            if(!cell.Contains(mark))return false;
            if(c=='.'||c==','){
                // Upright square/round screen periods have balanced rows. An
                // asymmetric or angled mark cannot prove a period on this route.
                bool symmetric=true;
                for(int y=0;y<(int)mark.Height/2&&symmetric;y++)for(int x=(int)mark.Left;x<(int)mark.Right;x++){
                    int a=((int)mark.Top+y)*ink.W+x,b=((int)mark.Bottom-1-y)*ink.W+x;
                    for(int channel=0;channel<3;channel++)if(ink.Pixels[a*4+channel]!=ink.Pixels[b*4+channel])symmetric=false;
                }
                if(mark.Top<word.Top+word.Height*.55||mark.Width>word.Height*.5)return false;
                return c=='.'?symmetric:!symmetric;
            }
            return true;
        }
        static bool HasAlignedStem(DesktopLineEvidence.Ink ink,Rect dot,Rect word){
            int center=(int)(dot.Left+dot.Width/2),start=(int)dot.Bottom;
            int stop=Math.Min(ink.H-1,(int)Math.Ceiling(word.Bottom));
            int matched=0,required=Math.Max(3,(int)Math.Floor(word.Height*.30));
            bool blank=false;
            for(int y=start;y<stop;y++){
                int row=y*ink.W,left=center,right=center;
                if(Strength(ink,row+center)==0){
                    if(matched!=0||y-start>word.Height*.35)return false;
                    blank=true;continue;
                }
                if(!blank)return false;
                while(left>0&&Strength(ink,row+left-1)>0)left--;
                while(right+1<ink.W&&Strength(ink,row+right+1)>0)right++;
                if(left<word.Left-1.25||right>word.Right+1.25||right-left+1>dot.Width*1.7||
                    Math.Abs((left+right+1)*.5-(dot.Left+dot.Width*.5))>Math.Max(1,dot.Width*.4))return false;
                if(++matched>=required)return true;
            }
            return false;
        }
    }
}
