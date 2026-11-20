using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Ocr;

namespace QingJie {
    // Windows' transcription is a hypothesis, not a confidence score. Reproduce
    // the WHOLE observed ink mask with a known typeface; decline if it differs.
    // A different renderer, unknown font or unaccounted mark is a normal miss.
    internal static class VerifiedLineOcr {
        internal static bool Small(BitmapSource b){return Small(b.PixelWidth,b.PixelHeight);}
        internal static bool Small(double width,double height){return width>=32&&width<=768&&height>=14&&height<=112&&width*height<=70000;}
        internal static bool MayContainShortLine(BitmapSource source){
            int w=source.PixelWidth,h=source.PixelHeight;
            return w>=32&&w<=2048&&h>=14&&h<=320&&(long)w*h<=192000;
        }
        // Users often select the width of a dialog around one label. Ignore only
        // perfectly uniform, opaque padding. A one-level mark remains content.
        internal static BitmapSource ShortSelection(BitmapSource source,CancellationToken token,out Int32Rect crop){
            crop=Int32Rect.Empty;
            if(Small(source))return source;
            if(!MayContainShortLine(source))return null;
            int width=source.PixelWidth,height=source.PixelHeight,stride=checked(width*4);
            var image=source.Format==PixelFormats.Bgra32?source:new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
            var rows=new byte[stride*Math.Min(8,height)];
            int left=width,top=height,right=-1,bottom=-1,bg=0;
            for(int y=0;y<height;y+=8){
                token.ThrowIfCancellationRequested();
                int count=Math.Min(8,height-y);
                image.CopyPixels(new Int32Rect(0,y,width,count),rows,stride,0);
                if(y==0){
                    bg=rows[0];
                    if(rows[1]!=bg||rows[2]!=bg||(bg>50&&bg<205))return null;
                }
                for(int row=0;row<count;row++){
                    int py=y+row;
                    for(int x=0,i=row*stride;x<width;x++,i+=4){
                        if(rows[i+3]!=255)return null;
                        if(rows[i]==bg&&rows[i+1]==bg&&rows[i+2]==bg)continue;
                        if(x<2||py<2||x>=width-2||py>=height-2)return null;
                        if(bottom>=0&&py>bottom+1&&
                            !DesktopLineEvidence.MayBeDetachedDot(right-left+1,bottom-top+1,py-bottom-1))return null;
                        left=Math.Min(left,x);right=Math.Max(right,x);
                        top=Math.Min(top,py);bottom=py;
                    }
                    if(right-left+1>768||bottom-top+1>112)return null;
                }
            }
            if(right<left)return null;
            left=Math.Max(0,left-10);top=Math.Max(0,top-10);
            int w=Math.Min(width,right+11)-left,h=Math.Min(height,bottom+11)-top;
            if(!Small(w,h))return null;
            crop=new Int32Rect(left,top,w,h);
            var bytes=new byte[w*h*4];
            image.CopyPixels(crop,bytes,w*4,0);
            var view=BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,bytes,w*4);
            view.Freeze();return view;
        }
        internal sealed class Ink {
            public int Width,Height,Count;
            public bool Dark;
            public byte[] Samples;
            public Int32Rect Bounds;
        }
        internal static Ink Analyze(BitmapSource source){
            if(!Small(source))return null;
            return Analyze(CopyBgra(source),source.PixelWidth,source.PixelHeight);
        }
        internal static byte[] CopyBgra(BitmapSource source){
            var b=source.Format==PixelFormats.Bgra32?source:new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
            int stride=checked(b.PixelWidth*4);var pixels=new byte[checked(stride*b.PixelHeight)];b.CopyPixels(pixels,stride,0);
            return pixels;
        }
        static Ink Analyze(byte[] p,int w,int h){
            // Single neutral opaque field only, strictly reject any content at
            // an edge (clipping) and multiple bands including faint footnotes.
            int bg=p[0];if(p[1]!=bg||p[2]!=bg||(bg>25&&bg<230))return null;
            bool light=bg>=230;int max=0,previous=-1,x0=w,x1=-1,y0=h,y1=-1;
            for(int y=0;y<h;y++){
                bool ink=false;
                for(int x=0;x<w;x++){
                    int n=(y*w+x)*4,c=p[n],delta=light?bg-c:c-bg;
                    if(p[n+3]!=255||p[n+1]!=c||p[n+2]!=c||delta<0)return null;
                    if(delta==0)continue;
                    if(x<2||y<2||x>=w-2||y>=h-2)return null;
                    ink=true;max=Math.Max(max,delta);
                }
                if(ink){if(previous>=0&&y-previous>1)return null;previous=y;}
            }
            if(max<96)return null;
            var mask=new byte[w*h];int count=0;
            for(int y=0;y<h;y++)for(int x=0;x<w;x++){
                int i=y*w+x,delta=Math.Abs(bg-p[i*4]);if(delta==0)continue;
                // Every non-background pixel participates, including a faint
                // period or a low-contrast word on the same baseline.
                mask[i]=delta*4<max?(byte)1:(byte)2;count++;x0=Math.Min(x0,x);x1=Math.Max(x1,x);y0=Math.Min(y0,y);y1=Math.Max(y1,y);
            }
            int ih=y1-y0+1,iw=x1-x0+1;
            if(ih<10||ih>46||iw<10||count<24||count>iw*ih*.74)return null;
            return new Ink{Width=w,Height=h,Dark=!light,Count=count,Samples=mask,Bounds=new Int32Rect(x0,y0,iw,ih)};
        }
        // Only for an internally drawn calibration template with known black or
        // white background. Software WPF can leave coverage alpha in its
        // premultiplied antialias pixels. Composite into the known background
        // exactly as the desktop compositor does. Never call on external pixels.
        internal static byte[] OpaqueReferencePixels(RenderTargetBitmap rendered,bool dark){
            if(rendered.Format!=PixelFormats.Pbgra32)throw new ArgumentException("Expected internally drawn premultiplied pixels.");
            int w=rendered.PixelWidth,h=rendered.PixelHeight;var p=new byte[checked(w*h*4)];
            rendered.CopyPixels(p,w*4,0);
            for(int i=0;i<p.Length;i+=4){int add=dark?0:255-p[i+3];
                if(add!=0)for(int j=0;j<3;j++)p[i+j]=(byte)Math.Min(255,p[i+j]+add);
                p[i+3]=255;
            }
            return p;
        }
        internal static Ink AnalyzeReference(RenderTargetBitmap rendered,bool dark){
            return Analyze(OpaqueReferencePixels(rendered,dark),rendered.PixelWidth,rendered.PixelHeight);
        }
        internal static BitmapSource OpaqueReference(RenderTargetBitmap rendered,bool dark){
            int w=rendered.PixelWidth,h=rendered.PixelHeight;
            var image=BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,OpaqueReferencePixels(rendered,dark),w*4);
            image.Freeze();return image;
        }
        internal static bool SimpleWord(string text){
            return LineWordRules.SimpleWord(text);
        }
        internal static bool EqualInk(Ink a,Ink b){
            if(a==null||b==null||a.Bounds.Width!=b.Bounds.Width||a.Bounds.Height!=b.Bounds.Height||a.Count!=b.Count)return false;
            for(int y=0;y<a.Bounds.Height;y++){
                int l=(a.Bounds.Y+y)*a.Width+a.Bounds.X,r=(b.Bounds.Y+y)*b.Width+b.Bounds.X;
                for(int x=0;x<a.Bounds.Width;x++)if(a.Samples[l+x]!=b.Samples[r+x])return false;
            }
            return true;
        }
        internal static bool Covered(Ink ink,OcrPage page){
            if(ink==null||page==null||page.Words.Count==0||page.Words.Count>12)return false;
            var boxes=new List<Rect>();double previous=-1;
            foreach(var word in page.Words){
                if(word.Line!=0||!SimpleWord(word.Text)||word.Box.IsEmpty||word.Box.Width<2||word.Box.Height<7||word.Box.Left<previous-1)return false;
                previous=word.Box.Right;var box=word.Box;box.Inflate(1.75,1.75);boxes.Add(box);
            }
            int left=ink.Bounds.X,top=ink.Bounds.Y;
            for(int y=top;y<top+ink.Bounds.Height;y++)for(int x=left;x<left+ink.Bounds.Width;x++){
                if(ink.Samples[y*ink.Width+x]==0)continue;
                bool found=false;foreach(var box in boxes)if(box.Contains(x+.5,y+.5)){found=true;break;}if(!found)return false;
            }
            return true;
        }
        static readonly string[] fonts={"Segoe UI","Arial","Calibri"};
        internal static bool Reproduces(Ink ink,string text,CancellationToken cancel){
            OcrPage ignore;return Reproduces(ink,text,cancel,false,out ignore);
        }
        static OcrPage InkWords(Ink ink,Ink rendered,FormattedText line,string text) {
            var words=text.Split(' ');int left=ink.Bounds.X,right=left+ink.Bounds.Width,pos=0;
            var result=new OcrPage();
            for(int i=0;i<words.Length;i++){
                if(!SimpleWord(words[i]))return null;pos+=words[i].Length;int end=right;
                if(i+1<words.Length){
                    var g=line.BuildHighlightGeometry(new Point(5,5),pos,1);if(g==null||g.Bounds.IsEmpty)return null;
                    double x=g.Bounds.X+g.Bounds.Width*.5-rendered.Bounds.X+ink.Bounds.X;
                    int rounded=(int)Math.Round(x);end=-1;
                    foreach(int d in new[]{0,-1,1,-2,2}){
                        int cut=rounded+d;if(cut<=left||cut>=right)continue;
                        bool clear=true;for(int y=ink.Bounds.Y;y<ink.Bounds.Y+ink.Bounds.Height;y++)if(ink.Samples[y*ink.Width+cut]!=0){clear=false;break;}
                        if(clear){end=cut;break;}
                    }
                    if(end<0)return null;pos++;
                }
                int x0=end,x1=-1,y0=ink.Bounds.Y+ink.Bounds.Height,y1=-1;
                for(int y=ink.Bounds.Y;y<ink.Bounds.Y+ink.Bounds.Height;y++)for(int x=left;x<end;x++)if(ink.Samples[y*ink.Width+x]!=0){x0=Math.Min(x0,x);x1=Math.Max(x1,x);y0=Math.Min(y0,y);y1=Math.Max(y1,y);}
                if(x1<x0||y1<y0)return null;
                result.Words.Add(new WordBox{Text=words[i],Line=0,Box=new Rect(x0,y0,x1-x0+1,y1-y0+1)});left=end;
            }
            OcrService.IndexText(result);return Covered(ink,result)?result:null;
        }
        static bool Reproduces(Ink ink,string text,CancellationToken cancel,bool map,out OcrPage page){
            page=null;
            // Scale comes from line width. Check integer and half-pixel sizes
            // around that estimate. No per-image raster or text is retained.
            if(ink==null||string.IsNullOrEmpty(text)||text.Length>64)return false;
            foreach(string family in fonts){
                var face=new Typeface(family);var culture=CultureInfo.InvariantCulture;
                var measure=new FormattedText(text,culture,FlowDirection.LeftToRight,face,24,Brushes.Black,1);
                double advance=measure.WidthIncludingTrailingWhitespace-measure.OverhangLeading-measure.OverhangTrailing;
                if(advance<=0)continue;double mid=Math.Round(24*ink.Bounds.Width/advance*2)/2;
                foreach(double shift in new[]{0,.5,-.5,1,-1,1.5,-1.5,2,-2}){
                    cancel.ThrowIfCancellationRequested();double sz=mid+shift;if(sz<10||sz>68)continue;
                    var line=new FormattedText(text,culture,FlowDirection.LeftToRight,face,sz,Brushes.Black,1);
                    int w=(int)Math.Ceiling(line.WidthIncludingTrailingWhitespace)+12,h=(int)Math.Ceiling(line.Height)+12;
                    if(w>768||h>112||w*h>70000||Math.Abs(line.Width-ink.Bounds.Width)>12)continue;
                    w=Math.Max(32,w);var visual=new DrawingVisual();
                    line.SetForegroundBrush(ink.Dark?Brushes.White:Brushes.Black);
                    using(var dc=visual.RenderOpen()){dc.DrawRectangle(ink.Dark?Brushes.Black:Brushes.White,null,new Rect(0,0,w,h));dc.DrawText(line,new Point(5,5));}
                    var rb=new RenderTargetBitmap(w,h,96,96,PixelFormats.Pbgra32);rb.Render(visual);rb.Freeze();
                    var candidate=AnalyzeReference(rb,ink.Dark);
                    if(EqualInk(ink,candidate)){if(!map)return true;page=InkWords(ink,candidate,line,text);return page!=null;}
                }
            }
            return false;
        }
        internal static OcrPage Prove(Ink ink,IList<string> words,CancellationToken token){
            if(ink==null||words.Count<1||words.Count>12)return null;
            // Never modify the native transcription or accept a punctuation guess.
            // A period hypothesis must reproduce every single captured mark.
            var values=new string[words.Count];
            for(int i=0;i<values.Length;i++){string s=words[i];
                if(i==values.Length-1&&s!=null&&s.EndsWith("•",StringComparison.Ordinal)&&s.Length>1)s=s.Substring(0,s.Length-1)+".";
                if(!SimpleWord(s))return null;values[i]=s;
            }
            OcrPage mapped;return Reproduces(ink,string.Join(" ",values),token,true,out mapped)?mapped:null;
        }
        static void Stage(Action<string> trace,string name){if(trace!=null)trace(name);}
        static Task<OcrPage> StartSymbolRead(BitmapSource pixels,CancellationToken token,Action<int> started,Action<string> trace){
            return Task.Run(()=>{
                try{
                    Stage(trace,"symbol-read");
                    return OcrWorker.ReadShortLineSymbols(pixels,token,started,trace);
                }catch(Exception e){Stage(trace,e.GetType().Name);return null;}
            });
        }
        internal static string[] Hypothesis(IList<WordBox> raw){
            if(raw.Count<1||raw.Count>12)return null;
            var words=new List<string>(raw.Count);
            bool merged=false;
            for(int i=0;i<raw.Count;i++){
                string s=raw[i].Text;
                if(i==raw.Count-1&&s!=null&&s.Length>1&&s.EndsWith("•",StringComparison.Ordinal))s=s.Substring(0,s.Length-1)+".";
                if(!SimpleWord(s)&&s!=null&&s.Length==1&&!merged&&s[0]!='I'&&s[0]!='l'){
                    int left=s[0]>='A'&&s[0]<='Z'?i:i-1;
                    if(left>=0&&left+1<raw.Count&&(left==i||s[0]>='a'&&s[0]<='z')){
                        var a=raw[left].Box;
                        var b=raw[left+1].Box;
                        double gap=b.Left-a.Right, height=Math.Min(a.Height,b.Height);
                        double verticalOverlap=Math.Min(a.Bottom,b.Bottom)-Math.Max(a.Top,b.Top);
                        if(!a.IsEmpty&&!b.IsEmpty&&gap>=-1&&gap<=height*.25&&verticalOverlap>=height*.65){
                            if(left==i)s+=raw[++i].Text;
                            else {
                                if(words.Count==0)return null;
                                s=words[words.Count-1]+s;
                                words.RemoveAt(words.Count-1);
                            }
                            merged=true;
                        }
                    }
                }
                if(!SimpleWord(s))return null;words.Add(s);
            }
            return words.ToArray();
        }
        static string[] JoinedHypothesis(IList<WordBox> raw,string[] words){
            // Some native recognizers split one English word into two plausible
            // fragments. Only try one close split; the full line must still
            // reproduce the captured ink, including every space and punctuation.
            if(words.Length!=raw.Count||words.Length<2)return null;
            int join=-1;
            for(int i=1;i<words.Length;i++){
                if(words[i][0]<'a'||words[i][0]>'z'||!SimpleWord(words[i-1]+words[i]))continue;
                var left=raw[i-1].Box;var right=raw[i].Box;
                if(left.IsEmpty||right.IsEmpty||right.Left<=left.Left)continue;
                double height=Math.Min(left.Height,right.Height),gap=right.Left-left.Right;
                double overlap=Math.Min(left.Bottom,right.Bottom)-Math.Max(left.Top,right.Top);
                if(height<7||gap< -1||gap>height*.25||overlap<height*.65)continue;
                if(join>=0)return null;
                join=i;
            }
            if(join<0)return null;
            var joined=new string[words.Length-1];
            Array.Copy(words,0,joined,0,join-1);
            joined[join-1]=words[join-1]+words[join];
            Array.Copy(words,join+1,joined,join,words.Length-join-1);
            return joined;
        }
        static OcrPage ProveCandidate(Ink ink,DesktopLineEvidence.Ink desktop,string[] words,CancellationToken token){
            token.ThrowIfCancellationRequested();
            return desktop==null?Prove(ink,words,token):DesktopLineEvidence.Prove(desktop,string.Join(" ",words),token);
        }
        static OcrPage ProveContraction(Ink ink,DesktopLineEvidence.Ink desktop,IList<WordBox> raw,CancellationToken token){
            if(raw.Count<1||raw.Count>12)return null;
            // A recognizer can turn the short vertical mark in a contraction
            // into an I or swap the two apostrophe glyphs. Do not insert missing
            // punctuation or change arbitrary identifiers. Test only one mark.
            var words=new string[raw.Count];int wordIndex=-1,charIndex=-1;
            for(int i=0;i<words.Length;i++){
                string s=raw[i].Text;
                if(string.IsNullOrEmpty(s)||s.Length>22)return null;
                if(i==words.Length-1&&s.Length>1&&s[s.Length-1]=='•')s=s.Substring(0,s.Length-1)+".";
                words[i]=s;
                int mark=s.IndexOf('\'');if(mark<0)mark=s.IndexOf('\u2019');if(mark<0)mark=s.IndexOf('I',1);
                if(mark<0){if(!SimpleWord(s))return null;continue;}
                if(wordIndex>=0||mark==0||mark==s.Length-1)return null;
                wordIndex=i;charIndex=mark;
            }
            if(wordIndex<0)return null;
            string original=words[wordIndex];
            OcrPage match=null;
            foreach(char mark in new[]{'\'','\u2019'}){
                if(mark==original[charIndex])continue; // the reported reading already failed
                string alternative=original.Substring(0,charIndex)+mark+original.Substring(charIndex+1);
                if(!SimpleWord(alternative))return null;
                words[wordIndex]=alternative;
                var page=ProveCandidate(ink,desktop,words,token);
                if(page==null)continue;
                if(match!=null)return null; // the pixels cannot distinguish the two forms
                match=page;
            }
            return match;
        }
        internal static OcrPage ProveReading(Ink ink,DesktopLineEvidence.Ink desktop,IList<WordBox> raw,CancellationToken token){
            var words=Hypothesis(raw);
            if(words==null)return ProveContraction(ink,desktop,raw,token);
            var result=ProveCandidate(ink,desktop,words,token);if(result!=null)return result;
            result=ProveContraction(ink,desktop,raw,token);if(result!=null)return result;
            var joined=JoinedHypothesis(raw,words);
            return joined==null?null:ProveCandidate(ink,desktop,joined,token);
        }
        static int InternalCapitalI(IList<WordBox> words){
            if(words.Count<2||words.Count>12)return -1;
            int suspect=-1,length=words.Count-1;
            for(int i=0;i<words.Count;i++){
                var word=words[i];if(word==null||word.Line!=0||string.IsNullOrEmpty(word.Text))return -1;
                string text=word.Text;length+=text.Length;if(length>60)return -1;
                if(SimpleWord(text))continue;
                int mark=text.IndexOf('I',1);
                if(suspect>=0||text.Length<4||mark<1||mark+1>=text.Length||
                    text[mark+1]<'a'||text[mark+1]>'z'||!SimpleWord(text.Substring(0,mark)+"l"+text.Substring(mark+1)))return -1;
                suspect=i;
            }
            return suspect;
        }
        internal static OcrPage ProveCaseRereading(Ink ink,DesktopLineEvidence.Ink desktop,IList<WordBox> first,IList<WordBox> second,CancellationToken token){
            token.ThrowIfCancellationRequested();
            int index=InternalCapitalI(first);if(index<0||first.Count!=second.Count)return null;
            var words=new string[first.Count];string previous=first[index].Text;int mark=previous.IndexOf('I',1);
            for(int i=0;i<words.Length;i++){
                if(second[i]==null||second[i].Line!=0)return null;
                words[i]=second[i].Text;
                string expected=i==index?previous.Substring(0,mark)+"l"+previous.Substring(mark+1):first[i].Text;
                if(words[i]!=expected)return null;
            }
            // Both independently sampled readings must preserve every other
            // letter, space and punctuation mark. Resolve just this disagreement
            // by reproducing the full line on the original, unscaled pixels.
            string corrected=string.Join(" ",words);words[index]=previous;
            string competing=string.Join(" ",words);
            if(desktop!=null)return DisplayGlyphEvidence.Prove(desktop,corrected,token,0,false,competing);
            OcrPage mapped;
            if(ink==null||!Reproduces(ink,corrected,token,true,out mapped)||Reproduces(ink,competing,token))return null;
            return mapped;
        }
        static Ink AnalyzeSource(BitmapSource source,out DesktopLineEvidence.Ink desktop){
            // Both verifiers inspect the same original samples. A ClearType
            // crop often needs a large array even for a single visible line.
            int width=source.PixelWidth,height=source.PixelHeight;var original=CopyBgra(source);
            var ink=Analyze(original,width,height);desktop=null;
            if(ink==null){desktop=DesktopLineEvidence.Analyze(original,width,height,true);if(desktop!=null&&!desktop.HasColor)desktop=null;}
            return ink;
        }
        static async Task<OcrPage> TryCore(BitmapSource source,CancellationToken token,Action<string> trace,Action<int> beginSymbolRead){
            Stage(trace,"worker-enter");DesktopLineEvidence.Ink desktop;var ink=AnalyzeSource(source,out desktop);
            Stage(trace,ink!=null?"eligible":desktop!=null?"rgb-eligible":"not-eligible");if(ink==null&&desktop==null)return null;
            var command=ink==null?ExactMenuGlyphOcr.TryReadDesktop(desktop,token):ExactMenuGlyphOcr.TryRead(source,ink,token);Stage(trace,command!=null?"command-hit":"command-miss");if(command!=null)return command;
            // A detached dot is admissible only in a complete exact caption.
            if(desktop!=null&&desktop.HasDetachedDot)return null;
            var available=OcrEngine.AvailableRecognizerLanguages;
            var language=available.FirstOrDefault(l=>l.LanguageTag.StartsWith("en",StringComparison.OrdinalIgnoreCase))??
                         available.FirstOrDefault(l=>l.LanguageTag.StartsWith("zh",StringComparison.OrdinalIgnoreCase));
            if(language==null)return null;var engine=OcrEngine.TryCreateFromLanguage(language);Stage(trace,"native-created");if(engine==null)return null;
            const double ratio=2;
            if(Math.Max(source.PixelWidth,source.PixelHeight)*ratio>OcrEngine.MaxImageDimension)return null;
            var view=source;
            int originX=0,originY=0;
            if(desktop!=null){
                // Excess blank space biases the native line-angle estimator on
                // short labels. This box already contains every non-background
                // RGB sample. Compare the result against the original image.
                var bounds=desktop.Bounds;
                originX=Math.Max(0,bounds.X-6);originY=Math.Max(0,bounds.Y-6);
                int right=Math.Min(source.PixelWidth,bounds.X+bounds.Width+6);
                int bottom=Math.Min(source.PixelHeight,bounds.Y+bounds.Height+6);
                // Even a frozen BitmapFrame may still consult its decoder on
                // the thread that opened the file while freezing a CroppedBitmap.
                // Copy the small crop from pixels already read for the verifier.
                int width=right-originX,height=bottom-originY,stride=desktop.W*4;
                var crop=new WriteableBitmap(width,height,96,96,PixelFormats.Bgra32,null);
                crop.WritePixels(new Int32Rect(0,0,width,height),desktop.Pixels,stride,originY*stride+originX*4);
                crop.Freeze();view=crop;
            }
            var prepared=OcrService.Prepare(view,ratio);
            var pixels=new OcrPixels(prepared);Stage(trace,"pixels-ready");token.ThrowIfCancellationRequested();
            OcrResult native;
            List<WordBox> raw;
            OcrPage symbols;
            using(var secondary=CancellationTokenSource.CreateLinkedTokenSource(token)){
                try{
                    // Both read the same immutable samples. For RGB text a cold
                    // helper can start while Windows is recognizing the first line.
                    // A cached menu or a grayscale crop never starts the helper.
                    var nativeReading=ReadNative(engine,pixels,token);
                    bool canReadSymbols=desktop!=null&&ConsensusLineOcr.Installed();
                    Task<OcrPage> reading=null;
                    if(canReadSymbols&&!OcrWorker.HasIdleSymbolSession)
                        reading=StartSymbolRead(prepared,secondary.Token,null,trace);
                    native=await nativeReading.ConfigureAwait(false);
                    Interlocked.Exchange(ref nativeHasReplied,1);
                    Stage(trace,"native-reply");token.ThrowIfCancellationRequested();
                    if(native.Lines.Count!=1)return null;
                    raw=NativeWords(native,ratio,originX,originY);
                    bool compare=canReadSymbols&&ConsensusLineOcr.CanCompare(desktop,raw,native.TextAngle);
                    TaskCompletionSource<int> warmReadBudget=null;
                    if(compare){
                        if(reading!=null)beginSymbolRead(OcrWorker.ColdSymbolMilliseconds);
                        else{
                            // A likely line can spend longer proving its font than
                            // reading its symbols. Overlap the two only after Windows
                            // has found comparable words. Exact proof stays first.
                            warmReadBudget=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                            reading=StartSymbolRead(prepared,secondary.Token,ms=>warmReadBudget.TrySetResult(ms),trace);
                        }
                    }
                    var exact=ProveReading(ink,desktop,raw,token);
                    Stage(trace,exact==null?"no-proof":"proof-complete");
                    if(exact!=null)return exact;
                    if(!compare){
                        if(InternalCapitalI(raw)<0||(native.TextAngle.HasValue&&!(Math.Abs(native.TextAngle.Value)<=3))||
                            Math.Max(view.PixelWidth,view.PixelHeight)*3>OcrEngine.MaxImageDimension)return null;
                        Stage(trace,"case-recheck");
                        var again=await ReadNative(engine,new OcrPixels(OcrService.Prepare(view,3)),token).ConfigureAwait(false);
                        if(again.Lines.Count!=1||(again.TextAngle.HasValue&&!(Math.Abs(again.TextAngle.Value)<=3)))return null;
                        var proof=ProveCaseRereading(ink,desktop,raw,NativeWords(again,3,originX,originY),token);
                        Stage(trace,proof!=null?"case-proof":"case-miss");return proof;
                    }
                    if(warmReadBudget!=null){
                        // Keep the normal proof deadline until symbols are actually
                        // needed. The idle process can expire before acquisition,
                        // so use the budget for the session the worker really got.
                        await Task.WhenAny(warmReadBudget.Task,reading).ConfigureAwait(false);
                        if(warmReadBudget.Task.IsCompleted)beginSymbolRead(warmReadBudget.Task.Result);
                    }
                    symbols=await reading.ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if(symbols==null)return null;
                }finally{secondary.Cancel();}
            }
            bool missingNativeEdge=false;
            var agreed=ConsensusLineOcr.Eligible(desktop,raw,native.TextAngle)?
                ConsensusLineOcr.Verify(desktop,raw,symbols,originX,originY,token,out missingNativeEdge):null;
            if(agreed==null&&(missingNativeEdge||ConsensusLineOcr.NeedsAnotherScale(raw,symbols))&&Math.Max(view.PixelWidth,view.PixelHeight)*3<=OcrEngine.MaxImageDimension){
                token.ThrowIfCancellationRequested();Stage(trace,"symbol-recheck");
                native=await ReadNative(engine,new OcrPixels(OcrService.Prepare(view,3)),token).ConfigureAwait(false);
                if(native.Lines.Count!=1)return null;
                raw=NativeWords(native,3,originX,originY);
                // Keep the independent symbols from the first reading. Changed
                // letters, punctuation or incomplete boxes still fail verification.
                if(ConsensusLineOcr.Eligible(desktop,raw,native.TextAngle))
                    agreed=ConsensusLineOcr.Verify(desktop,raw,symbols,originX,originY,token);
            }
            Stage(trace,agreed==null?"symbol-miss":"symbol-complete");return agreed;
        }
        static List<WordBox> NativeWords(OcrResult reading,double ratio,int x,int y){
            var words=new List<WordBox>();
            foreach(var word in reading.Lines[0].Words){
                var b=word.BoundingRect;words.Add(new WordBox{Text=word.Text,Line=0,Box=new Rect(x+b.X/ratio,y+b.Y/ratio,b.Width/ratio,b.Height/ratio)});
            }
            return words;
        }
        static async Task<OcrResult> ReadNative(OcrEngine engine,OcrPixels pixels,CancellationToken token){
            token.ThrowIfCancellationRequested();
            using(var bitmap=pixels.WindowsBitmap()){
                var op=engine.RecognizeAsync(bitmap);var done=new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                op.Completed=(a,s)=>{try{if(s==Windows.Foundation.AsyncStatus.Canceled)done.TrySetCanceled();else done.TrySetResult(a.GetResults());}catch(Exception ex){done.TrySetException(ex);}};
                using(token.Register(()=>{try{op.Cancel();}catch(Exception){}}))return await done.Task.ConfigureAwait(false);
            }
        }
        static readonly SemaphoreSlim slot=new SemaphoreSlim(1,1);
        static int nativeHasReplied;
        internal static Task<OcrPage> TryRead(BitmapSource source,CancellationToken cancel){
            return TryReadObserved(source,cancel,null,0);
        }
        internal static async Task<OcrPage> TryReadObserved(BitmapSource source,CancellationToken cancel,Action<string> trace,int budget){
            cancel.ThrowIfCancellationRequested();if(!Small(source))return null;
            if(!slot.Wait(0)){
                // The previous caller can already have cancelled while its native
                // operation is still acknowledging that cancellation. Give a retry
                // a brief handoff without ever waiting for a stuck native reader.
                Stage(trace,"waiting-for-verifier");
                if(!await slot.WaitAsync(500,cancel).ConfigureAwait(false))return null;
            }
            // On first use, Windows can take most of the normal deadline just
            // to activate OCR. Keep a little headroom for verifying its result.
            // Exact menu hits do not count as having initialized native OCR.
            bool defaultBudget=budget<=0;
            if(defaultBudget)budget=Volatile.Read(ref nativeHasReplied)==0?3500:1800;
            var life=CancellationTokenSource.CreateLinkedTokenSource(cancel);var token=life.Token;
            var expired=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using(token.Register(()=>expired.TrySetResult(true))){
                life.CancelAfter(budget);Stage(trace,"submitted");
                var work=Task.Run(async()=>{try{
                    return await TryCore(source,token,trace,milliseconds=>{if(defaultBudget)life.CancelAfter(milliseconds);}).ConfigureAwait(false);
                }catch(Exception e){if(trace!=null)trace(e.GetType().Name);return null;}finally{life.Dispose();slot.Release();}});
                var finished=await Task.WhenAny(work,expired.Task).ConfigureAwait(false);cancel.ThrowIfCancellationRequested();
                Stage(trace,finished==work?"completed":"deadline");
                return finished==work?await work.ConfigureAwait(false):null;
            }
        }
    }
}
